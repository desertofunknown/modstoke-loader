// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using global::Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        /// <summary>Resolve the admitted held-object identity, never a Unity instance ID or nearby object.</summary>
        public delegate bool ResolveHangGliderAnchor(PlayerHangGliderAction action, out ulong anchorNetworkId);

        private ResolveHangGliderAnchor _resolveGliderAnchor;
        private NativeActionTransition _transition, _deferredNativeExit;
        private bool _applyingNativeTransition;

        public bool HasPendingNativeActionTransition => !_disposed && _transition != null;
        public string NativeTransitionDetail { get; private set; }

        /// <summary>
        /// Bind before activation, or on admitted ground outside a physics step. Native
        /// actions wait for bridge approval. The lifecycle owner must supply server
        /// world/seat policy and cart motor/ghost authority before enabling this binding.
        /// </summary>
        public bool TryEnableNativeActionTransitions(ResolveHangGliderAnchor resolveGliderAnchor, out string detail)
        {
            detail = "Native action hooks and an exact admitted glider identity resolver are required.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || CartMigrationHeld || !_nativeModesInstalled
                || resolveGliderAnchor == null || _resolveGliderAnchor != null
                || _insideFixed || _awaitingPhysics || Time.inFixedTimeStep || _nativeMode != null || _transition != null
                || !RetainedTargetCurrent(true) || (_activated && !CheckMode())) return false;
            _resolveGliderAnchor = resolveGliderAnchor;
            detail = "Original glider/cart actions bound to bridge proposal, approval and commit.";
            return true;
        }

        private sealed class NativeActionTransition
        {
            internal readonly NativeModeKey Expected, Requested;
            internal readonly Behaviour Action;
            internal readonly GolfCartController Cart;
            internal readonly Transform GliderTransform;
            internal readonly bool SendDisconnectEvent, SkipReequip;
            internal PlayerModeTransitionProposal Proposal;
            internal bool Attempted, NativeCompleted, Committed;
            internal bool Entering => Requested.Mode != NativeMode.Ground;
            internal NativeModeKey NativeKey => Entering ? Requested : Expected;

            internal NativeActionTransition(NativeModeKey expected, NativeModeKey requested,
                Behaviour action, GolfCartController cart, bool sendDisconnectEvent, bool skipReequip)
            {
                Expected = expected; Requested = requested; Action = action; Cart = cart;
                SendDisconnectEvent = sendDisconnectEvent; SkipReequip = skipReequip;
                GliderTransform = action.TryCast<PlayerHangGliderAction>()?._hangGliderTransform;
            }
        }

        private static void InstallNativeActionTransitions(HarmonyLib.Harmony harmony)
        {
            Patch(typeof(PlayerHangGliderAction), nameof(PlayerHangGliderAction.BeginGliding), Type.EmptyTypes, nameof(BeforeGliderEntry));
            Patch(typeof(PlayerHangGliderAction), nameof(PlayerHangGliderAction.StopGliding), Type.EmptyTypes, nameof(BeforeGliderExit));
            Patch(typeof(GolfCartController), nameof(GolfCartController.OnDriverInteraction), Type.EmptyTypes, nameof(BeforeCartDriverEntry));
            Patch(typeof(GolfCartController), nameof(GolfCartController.OnPlayerPassengerInteraction), new[] { typeof(int) }, nameof(BeforeCartPassengerEntry));
            Patch(typeof(GolfCartController), nameof(GolfCartController.DisconnectLocalDriver), new[] { typeof(bool), typeof(bool) }, nameof(BeforeCartDriverExit));
            Patch(typeof(GolfCartController), nameof(GolfCartController.DisconnectPassenger), new[] { typeof(int), typeof(bool), typeof(bool) }, nameof(BeforeCartPassengerExit));

            void Patch(Type type, string name, Type[] parameters, string prefix)
            {
                var method = AccessTools.DeclaredMethod(type, name, parameters);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Recognized native action boundary is unavailable: " + name);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerClientMovement), prefix) { priority = Priority.Last });
            }
        }

        private static PlayerClientMovement NativeActionOwner()
        {
            var active = _active;
            return active != null && !active._disposed && active._activated
                && (active._resolveGliderAnchor != null || active.CartMigrationHeld)
                && Environment.CurrentManagedThreadId == _hookThread ? active : null;
        }

        private bool OwnsAction(Behaviour action) => Alive(action) && action.transform.IsChildOf(_rig.Root.transform);

        private static bool SameTransitionCart(GolfCartController left, GolfCartController right)
            => left is null && right is null || Same(left, right);

        private bool Applying(Behaviour action, GolfCartController cart, bool entering, int seat = -1)
            => _applyingNativeTransition && _transition != null && _transition.Entering == entering
                && Same(_transition.Action, action) && SameTransitionCart(_transition.Cart, cart)
                && (seat < 0 || _transition.NativeKey.SeatIndex == seat);

        private void QueueNativeAction(Behaviour action, GolfCartController cart, NativeMode mode,
            ulong anchor, int seat, bool sendDisconnectEvent = true, bool skipReequip = false)
        {
            if (_transition != null || _applyingNativeTransition || _modeSequence == uint.MaxValue || !OwnsAction(action)
                || !RetainedTargetCurrent() || !_bridge.TryGetCommittedMovementMode(out var expected)) return;
            if (mode != NativeMode.Ground && mode != NativeMode.HangGlider && !IsCartMode(mode))
            { NativeTransitionDetail = "This native mode has no supported automatic entry choreography."; return; }
            if (mode != NativeMode.Ground
                ? _nativeMode != null || expected != NativeModeKey.Ground(_modeSequence) || !CheckMode()
                : _nativeMode == null || expected != _nativeMode.Key || !Same(_nativeMode.Action, action)) return;
            var requested = new NativeModeKey(mode, _modeSequence + 1, anchor, seat);
            if (!PlayerBridgeProtocol.IsValidMovementModeKey(requested)) return;
            _transition = new NativeActionTransition(expected, requested, action, cart, sendDisconnectEvent, skipReequip);
            DiscardCapturedStep();
        }

        private void QueueNativeExit(Behaviour action, GolfCartController cart, bool sendDisconnectEvent = true, bool skipReequip = false)
        {
            if (_transition == null)
            {
                QueueNativeAction(action, cart, NativeMode.Ground, 0, -1, sendDisconnectEvent, skipReequip);
                return;
            }
            // Native cleanup can be requested inside entry, or before the entered seat
            // has replicated. Retain that one exit; it cannot overtake its entry commit.
            if (!_transition.Entering || !_transition.Attempted || _deferredNativeExit != null
                || _transition.Requested.TransitionSequence == uint.MaxValue
                || !Same(_transition.Action, action) || !SameTransitionCart(_transition.Cart, cart)) return;
            _deferredNativeExit = new NativeActionTransition(_transition.Requested,
                NativeModeKey.Ground(_transition.Requested.TransitionSequence + 1), action, cart, sendDisconnectEvent, skipReequip);
        }

        private static bool BeforeGliderEntry(PlayerHangGliderAction __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = NativeActionOwner();
            if (active == null || !active.OwnsAction(__instance)) return true;
            if (active.CartMigrationHeld) return false;
            if (active.Applying(__instance, null, true) || __instance._isGliding) return true;
            try
            {
                if (active._resolveGliderAnchor(__instance, out var anchor))
                    active.QueueNativeAction(__instance, null, NativeMode.HangGlider, anchor, -1);
            }
            catch (Exception error) { active.Stop("Glider entry capture failed (" + error.GetType().Name + ")."); }
            return false;
        }

        private static bool BeforeGliderExit(PlayerHangGliderAction __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = NativeActionOwner();
            if (active == null || !active.OwnsAction(__instance) || !__instance._isGliding) return true;
            if (active.CartMigrationHeld) return false;
            if (active.Applying(__instance, null, false)) return true;
            try { active.QueueNativeExit(__instance, null); }
            catch (Exception error) { active.Stop("Glider exit capture failed (" + error.GetType().Name + ")."); }
            return false;
        }

        private static bool BeforeCartDriverEntry(GolfCartController __instance, bool __runOriginal)
            => BeforeCartEntry(__instance, -1, true, __runOriginal);
        private static bool BeforeCartPassengerEntry(GolfCartController __instance, int __0, bool __runOriginal)
            => BeforeCartEntry(__instance, __0, false, __runOriginal);

        private static bool BeforeCartEntry(GolfCartController cart, int seat, bool driver, bool ranOriginal)
        {
            if (!ranOriginal) return false;
            var active = NativeActionOwner();
            if (active == null) return true;
            if (active.CartMigrationHeld) return false;
            try
            {
                if (!Alive(cart) || !Alive(cart.entity) || !cart.entity.isAttached) return false;
                if (driver) seat = cart.GetDriverSlotIndex();
                if (seat < 0 || seat >= cart.GetMaxPlayerSlotCount() || driver != (seat == cart.GetDriverSlotIndex())) return false;
                Behaviour action = driver ? LocalPlayer.PlayerBase.GetComponentInChildren<PlayerGolfCartDriverAction>()
                    : LocalPlayer.PlayerBase.GetComponentInChildren<PlayerGolfCartPassengerAction>();
                if (active.Applying(action, cart, true, seat)) return true;
                active.QueueNativeAction(action, cart, driver ? NativeMode.GolfCartDriver : NativeMode.GolfCartPassenger,
                    cart.entity.networkId.PackedValue, seat);
            }
            catch (Exception error) { active.Stop("Cart entry capture failed (" + error.GetType().Name + ")."); }
            return false;
        }

        private static bool BeforeCartDriverExit(GolfCartController __instance, bool __0, bool __1, bool __runOriginal)
            => BeforeCartExit(__instance, -1, true, __0, __1, __runOriginal);
        private static bool BeforeCartPassengerExit(GolfCartController __instance, int __0, bool __1, bool __2, bool __runOriginal)
            => BeforeCartExit(__instance, __0, false, __1, __2, __runOriginal);

        private static bool BeforeCartExit(GolfCartController cart, int seat, bool driver,
            bool sendDisconnectEvent, bool skipReequip, bool ranOriginal)
        {
            if (!ranOriginal) return false;
            var active = NativeActionOwner();
            if (active == null) return true;
            if (active.CartMigrationHeld) return false;
            try
            {
                var binding = active._nativeMode;
                var pending = active._transition;
                var action = binding?.Action ?? pending?.Action;
                var key = binding?.Key ?? pending?.NativeKey ?? NativeModeKey.Ground();
                if (action == null || !Same(binding?.Cart ?? pending?.Cart, cart)
                    || (driver ? key.Mode != NativeMode.GolfCartDriver : key.Mode != NativeMode.GolfCartPassenger || key.SeatIndex != seat)) return true;
                if (active.Applying(action, cart, false, key.SeatIndex)) return true;
                active.QueueNativeExit(action, cart, sendDisconnectEvent, skipReequip);
            }
            catch (Exception error) { active.Stop("Cart exit capture failed (" + error.GetType().Name + ")."); }
            return false;
        }

        private void DiscardCapturedStep()
        {
            _insideFixed = _awaitingPhysics = _sampled = false;
            _sample = _unsent = default;
            _nativeSample = _nativeUnsent = default;
            _nativeState = default;
            _jumpPending = _jumpLatched = false;
        }

        private bool TransitionCurrent(bool topology) => RetainedTargetCurrent(topology)
            && (_bridge.IsCommittedMovementMode(_transition.Expected) || _bridge.IsCommittedMovementMode(_transition.Requested));

        private void PumpNativeTransition()
        {
            var transition = _transition;
            if (transition == null || Time.inFixedTimeStep || _insideFixed || _awaitingPhysics) return;
            if (IsZiplineMode(transition.NativeKey.Mode)) { PumpZiplineTransition(); return; }
            if (!OwnsAction(transition.Action)) { Stop("Pending native action lifetime ended."); return; }
            string detail;
            if (transition.Proposal.RequestSequence == 0)
            {
                if (!_bridge.TryProposeMovementTransition(transition.Expected, transition.Requested, out var proposal, out detail))
                { NativeTransitionDetail = detail; return; }
                transition.Proposal = proposal;
            }
            if (!transition.Committed)
            {
                if (!_bridge.TryPeekMovementTransitionApproval(out var approval)) return;
                if (approval.Proposal != transition.Proposal)
                { Stop("Movement approval does not match the retained native action."); return; }
                if (approval.Decision == PlayerModeTransitionDecision.Rejected)
                {
                    if (!_bridge.TryCommitMovementTransition(approval, out detail)) { NativeTransitionDetail = detail; return; }
                    if (!transition.Entering && _nativeMode == null)
                    { Stop("Server rejected the exit before its native entry binding settled; lifecycle recovery is required."); return; }
                    ClearPredictionEpoch();
                    _transition = null;
                    _deferredNativeExit = null;
                    NativeTransitionDetail = "Server rejected the native action; no native enter/exit was invoked.";
                    return;
                }
                if (approval.Decision != PlayerModeTransitionDecision.Approved) return;
                if (!transition.NativeCompleted)
                {
                    if (transition.Attempted) { Stop("Approved native action has an unconfirmed application."); return; }
                    if (!CanApplyNativeAction(transition))
                    { Stop("Approved native action lost its exact retained object or seat boundary."); return; }
                    transition.Attempted = true;
                    _applyingNativeTransition = true;
                    try { ApplyNativeAction(transition); }
                    finally { _applyingNativeTransition = false; }
                    if (_disposed) return;
                    if (!NativeActionCompleted(transition))
                    { Stop("Approved native action did not complete; it will not be invoked twice."); return; }
                    transition.NativeCompleted = true;
                }
                if (!_bridge.TryCommitMovementTransition(approval, out detail)) { NativeTransitionDetail = detail; return; }
                transition.Committed = true;
            }
            if (!NativeActionCompleted(transition))
            { Stop("Committed native action changed before its movement binding settled."); return; }
            if (_deferredNativeExit != null)
            {
                ResetModeHistory(transition.Requested.TransitionSequence);
                if (_disposed) return;
                _modeSequence = transition.Requested.TransitionSequence;
                _nativeMode = null;
                _transition = _deferredNativeExit;
                _deferredNativeExit = null;
                return;
            }
            bool bound;
            if (transition.Requested.Mode == NativeMode.Ground)
                bound = TryResumeGround(transition.Requested.TransitionSequence, out detail);
            else if (transition.Requested.Mode == NativeMode.HangGlider)
                bound = TryBeginHangGlider(transition.Requested, transition.Action.TryCast<PlayerHangGliderAction>(), out detail);
            else if (IsCartMode(transition.Requested.Mode))
                bound = TryBeginGolfCartSeat(transition.Requested, transition.Action.TryCast<PlayerGolfCartAction>(), transition.Cart, out detail);
            else
            { Stop("Committed native mode has no supported automatic entry choreography."); return; }
            NativeTransitionDetail = detail;
            if (bound && !_disposed) _transition = null;
        }

        private bool HasCommittedNativeExit(uint sequence) => _transition != null && !_transition.Entering
            && _transition.NativeCompleted && _transition.Committed && _transition.Requested == NativeModeKey.Ground(sequence)
            && _transition.Expected.TransitionSequence == _modeSequence;

        private bool CanApplyNativeAction(NativeActionTransition transition)
        {
            if (!RetainedTargetCurrent(true) || !OwnsAction(transition.Action)) return false;
            var glider = transition.Action.TryCast<PlayerHangGliderAction>();
            if (glider != null)
            {
                if (!transition.Entering) return glider._isGliding;
                return glider.enabled && !glider._isGliding && SupportedMode()
                    && Alive(transition.GliderTransform) && Same(glider._hangGliderTransform, transition.GliderTransform)
                    && _resolveGliderAnchor(glider, out var anchor) && anchor == transition.Requested.AnchorNetworkId;
            }
            var cart = transition.Cart;
            var key = transition.NativeKey;
            if (!Alive(cart) || !Alive(cart.entity) || !cart.entity.isAttached || cart.entity.networkId.PackedValue != key.AnchorNetworkId)
                return false;
            var action = transition.Action.TryCast<PlayerGolfCartAction>();
            var state = action?._state;
            if (!Live(state)) return false;
            if (!transition.Entering)
                return state.IsConnected && Same(state.ConnectedGolfCartController, cart) && state.ConnectedSlotIndex == key.SeatIndex;
            Behaviour nativeAction = key.Mode == NativeMode.GolfCartDriver
                ? LocalPlayer.PlayerBase.GetComponentInChildren<PlayerGolfCartDriverAction>()
                : LocalPlayer.PlayerBase.GetComponentInChildren<PlayerGolfCartPassengerAction>();
            return !state.IsConnected && Same(nativeAction, action) && SupportedMode()
                && !cart.HasSlotConnection(key.SeatIndex);
        }

        private void ApplyNativeAction(NativeActionTransition transition)
        {
            var glider = transition.Action.TryCast<PlayerHangGliderAction>();
            if (glider != null)
            {
                if (transition.Entering) glider.BeginGliding();
                else glider.StopGliding();
            }
            else if (transition.Entering)
            {
                if (transition.NativeKey.Mode == NativeMode.GolfCartDriver) transition.Cart.OnDriverInteraction();
                else transition.Cart.OnPlayerPassengerInteraction(transition.NativeKey.SeatIndex);
            }
            else if (transition.NativeKey.Mode == NativeMode.GolfCartDriver)
            {
                if (!VehiclePreservation.TryApplyApprovedClientCartDriverExit(_bridge,
                        transition.Proposal, transition.Action.TryCast<PlayerGolfCartDriverAction>(),
                        transition.Cart, transition.SkipReequip, out var detail))
                    Stop(detail);
            }
            else
            {
                if (!VehiclePreservation.TryApplyApprovedClientCartPassengerExit(_bridge,
                        transition.Proposal, transition.Action.TryCast<PlayerGolfCartPassengerAction>(),
                        transition.Cart, transition.SkipReequip, out var detail))
                    Stop(detail);
            }
        }

        private bool NativeActionCompleted(NativeActionTransition transition)
        {
            if (!RetainedTargetCurrent() || !OwnsAction(transition.Action)) return false;
            var glider = transition.Action.TryCast<PlayerHangGliderAction>();
            if (glider != null) return glider._isGliding == transition.Entering;
            var state = transition.Action.TryCast<PlayerGolfCartAction>()?._state;
            return Live(state) && (!transition.Entering ? !state.IsConnected
                : state.IsConnected && Same(state.ConnectedGolfCartController, transition.Cart)
                    && state.ConnectedSlotIndex == transition.Requested.SeatIndex);
        }
    }
}
