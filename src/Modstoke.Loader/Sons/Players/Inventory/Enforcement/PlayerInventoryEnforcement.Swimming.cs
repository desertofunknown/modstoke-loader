// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private sealed class SwimmingStow(SourceItem item, int slot, string modules)
        {
            internal readonly SourceItem Item = item;
            internal readonly int Slot = slot;
            internal readonly string Modules = modules;
            internal bool Captured;
            internal EquipmentIntent Intent;
        }

        private PlayerWaterTransitionKey _swimmingEntryKey;
        private PlayerLifeRoute _swimmingEntryRoute;
        private PlayerAnimatorControl _swimmingEntryAnimation;
        private readonly SwimmingStow[] _swimmingHands = new SwimmingStow[2];
        private bool _swimmingEntryOpen, _swimmingEntryReturned, _swimmingEntryComplete;
        private bool _swimmingStashActive, _swimmingStashSeen, _swimmingStashReturned;

        // This is an outer accepted-work hold, not a CanQueue veto. Its individual
        // transactions already participate in canonical inventory custody.
        internal bool HasPendingSwimmingStow => _swimmingEntryKey.IsValid && !_swimmingEntryComplete;

        internal BoltMigrationAdmission TryBeginSwimmingEntry(PlayerWaterTransitionKey key,
            PlayerLifeRoute route, out string detail)
        {
            detail = "Swimming entry requires its exact settled source life receipt and original inventory.";
            if (!OnThread || IsDisposed || IsFaulted || !IsArmed || _authority != null
                || !key.IsValid || !key.Matches(_bridge) || !ReferenceEquals(_guard, this)
                || route?.Source == null || !ReferenceEquals(route.Source.ForcedInventory, this)
                || !route.MatchesWaterOwner(_bridge, null) || route.IsDisposed || route.FailureReason != null
                || _sourceWaterTransitions?.MatchesSourceActionOwner(_bridge) != true
                || _sourceWaterTransitions.HasPendingSwimmingTransition != true
                || _swimmingEntryKey.IsValid && (HasPendingSwimmingStow || key.Ordinal <= _swimmingEntryKey.Ordinal))
                return BoltMigrationAdmission.Rejected;
            try
            {
                if (!Current()
                    || !route.Source.CanPrepareForcedDrop(key.LifeGeneration, Held.MutationKind.SwimmingDrop, key.Ordinal))
                    return BoltMigrationAdmission.Rejected;
                RefreshEquipmentIntent();
                if (_equipmentIntent?.StateValue == EquipmentIntentState.Unconfirmed)
                    return BoltMigrationAdmission.Rejected;
                // StashRightHandItem's Equipping branch belongs to an already accepted
                // EquipSequence. Drain that owner before dispatch, never adopt a later
                // callback merely because its hand slot matches this swimming edge.
                if (_busy || _writerDepth != 0 || Time.inFixedTimeStep || HasPendingCustody
                    || HasPendingNativeCraftPresentation || InventoryFrozen || _nativeEquip != null
                    || _equipmentIntent is { Terminal: false } || !SwimmingHandsIdle())
                {
                    detail = "Swimming entry waits for earlier accepted equipment and the exact held-drop settlement.";
                    return BoltMigrationAdmission.Pending;
                }
                if (!Unchanged()) return BoltMigrationAdmission.Rejected;
                if (!route.HasSettledForcedDrop(Held.MutationKind.SwimmingDrop, key.Ordinal)
                    || HeldOnlyRoute?.HoldsForcedTransition != true)
                    return BoltMigrationAdmission.Pending;
                var animation = LocalPlayer.AnimControl;
                if (animation == null || animation.WasCollected || !animation)
                    return BoltMigrationAdmission.Rejected;
                _swimmingEntryKey = key;
                _swimmingEntryRoute = route;
                _swimmingEntryAnimation = animation;
                _swimmingHands[0] = _swimmingHands[1] = null;
                _swimmingEntryReturned = _swimmingEntryComplete = false;
                _swimmingStashActive = _swimmingStashSeen = _swimmingStashReturned = false;
                _swimmingEntryOpen = true;
                detail = "Exact swimming entry capture opened; invoke the native entry once, then close this scope.";
                return BoltMigrationAdmission.Ready;
            }
            catch (Exception error)
            { return RejectSwimmingEntry("Swimming entry admission failed: " + error.Message, out detail); }
        }

        internal bool EndSwimmingEntry(PlayerWaterTransitionKey key, bool nativeSucceeded, out string detail)
        {
            detail = "Close only the exact active native swimming invocation.";
            if (!OnThread || key != _swimmingEntryKey || !_swimmingEntryOpen) return false;
            _swimmingEntryOpen = false;
            try
            {
                if (!nativeSucceeded || _swimmingStashActive || !_swimmingStashReturned
                    || !SwimmingEntryCurrent(key) || IsNativeInventoryLifecycleHeld(_inventory) || !Unchanged())
                {
                    RejectSwimmingEntry("Native swimming entry did not return with its exact stow captures intact; it cannot be replayed.", out detail);
                    return false;
                }
                _swimmingEntryReturned = true;
                detail = "Native entry returned; captured hand requests and their terminal receipts remain retained.";
                return true;
            }
            catch (Exception error)
            { RejectSwimmingEntry("Swimming entry return is unconfirmed: " + error.Message, out detail); return false; }
        }

        internal BoltMigrationAdmission TryPumpSwimmingEntry(PlayerWaterTransitionKey key, out string detail)
        {
            detail = "Swimming stows await their exact canonical equipment receipts.";
            if (!OnThread || key != _swimmingEntryKey || !_swimmingEntryKey.IsValid || IsDisposed || IsFaulted)
                return BoltMigrationAdmission.Rejected;
            if (_swimmingEntryComplete) return BoltMigrationAdmission.Ready;
            try
            {
                if (!SwimmingEntryCurrent(key))
                    return RejectSwimmingEntry("Retained swimming entry lost its exact source, life route or forced hold.", out detail);
                if (_swimmingEntryOpen || !_swimmingEntryReturned || _swimmingStashActive
                    || _busy || _writerDepth != 0 || Time.inFixedTimeStep || InventoryFrozen)
                    return BoltMigrationAdmission.Pending;

                // The native callback selects left then right. Keep both captures even
                // though the existing wire/ledger intentionally has one pending stow.
                for (int slot = (int)EquipmentSlot.LeftHand; slot >= (int)EquipmentSlot.RightHand; slot--)
                {
                    var stow = _swimmingHands[slot];
                    if (stow?.Captured != true) continue;
                    if (stow.Intent != null)
                    {
                        var state = stow.Intent.ReadState();
                        if (state is EquipmentIntentState.Refused or EquipmentIntentState.Unconfirmed)
                            return RejectSwimmingEntry("Accepted swimming stow did not settle: " + stow.Intent.Detail, out detail);
                        if (state != EquipmentIntentState.Settled)
                            return BoltMigrationAdmission.Pending;
                        continue;
                    }
                    if (HasPendingCustody || HasPendingNativeCraftPresentation || _nativeEquip != null
                        || _equipmentIntent is { Terminal: false } || !SwimmingHandsIdle())
                        return BoltMigrationAdmission.Pending;
                    if (_nextStowRequest == ulong.MaxValue || !Unchanged() || !SwimmingHandMatches(stow))
                        return RejectSwimmingEntry("Captured swimming hand identity changed before server submission.", out detail);
                    ulong floor = _lastReceived;
                    StartNativeStowRequest(stow.Item, stow.Slot);
                    stow.Intent = new EquipmentIntent(this, stow.Item.Instance, in _nativeStowRequest, floor);
                    // Shared receipt boundaries retain this exact receipt before another
                    // native transaction can replace LastReceipt.
                    _equipmentIntent = stow.Intent;
                    detail = "Captured swimming stow submitted once through the canonical server inventory ledger.";
                    return BoltMigrationAdmission.Pending;
                }
                if (HasPendingCustody || HasPendingNativeCraftPresentation || _nativeEquip != null || !SwimmingHandsIdle())
                    return BoltMigrationAdmission.Pending;
                if (!Unchanged() || !_swimmingEntryRoute.HasSettledForcedDrop(Held.MutationKind.SwimmingDrop, key.Ordinal))
                    return RejectSwimmingEntry("Swimming stow completion lost its inventory or forced-drop settlement.", out detail);
                _swimmingEntryComplete = true;
                detail = "All captured swimming stows, native equipment tails and authoritative publication receipts settled.";
                return BoltMigrationAdmission.Ready;
            }
            catch (Exception error)
            { return RejectSwimmingEntry("Swimming stow progression failed: " + error.Message, out detail); }
        }

        private bool SwimmingEntryCurrent(PlayerWaterTransitionKey key)
            => OnThread && !IsDisposed && !IsFaulted && IsArmed && _authority == null
                && ReferenceEquals(_guard, this) && key == _swimmingEntryKey && key.Matches(_bridge)
                && _bridge.IsGameplayActive && Current()
                && _swimmingEntryAnimation != null && !_swimmingEntryAnimation.WasCollected && _swimmingEntryAnimation
                && LocalPlayer.AnimControl?.Pointer == _swimmingEntryAnimation.Pointer
                && _swimmingEntryRoute?.Source != null && !_swimmingEntryRoute.IsDisposed
                && _swimmingEntryRoute.FailureReason == null && _swimmingEntryRoute.MatchesWaterOwner(_bridge, null)
                && ReferenceEquals(_swimmingEntryRoute.Source.ForcedInventory, this)
                && _swimmingEntryRoute.Source.CanPrepareForcedDrop(key.LifeGeneration, Held.MutationKind.SwimmingDrop, key.Ordinal)
                && HeldOnlyRoute?.HoldsForcedTransition == true;

        private bool SwimmingHandsIdle()
        {
            var props = _inventory._InventoryProps_k__BackingField;
            var next = _inventory._equipmentSlotsNext;
            if (_inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                || _inventory._equipSequenceRight != null || props == null || props.WasCollected || !props
                || props._leftHandWideBlendCoroutine != null || next == null || next.WasCollected || next.Length != 6)
                return false;
            for (int i = 0; i < next.Length; i++) if (next[i] != null) return false;
            return true;
        }

        private SwimmingStow CaptureSwimmingHand(int slot)
        {
            var native = _inventory._equipmentSlots[slot];
            if (native == null) return null;
            foreach (var item in _sourceItems)
                if (item.Quantity == 1 && item.Entry.EquipmentSlot == slot && item.Instance != null
                    && !item.Instance.WasCollected && item.Instance.Pointer == native.Pointer)
                    return new SwimmingStow(item, slot,
                        PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(native)));
            throw new InvalidOperationException("Native swimming hand has no exact retained inventory record.");
        }

        private bool SwimmingHandMatches(SwimmingStow stow)
            => stow != null && stow.Item.Instance != null && !stow.Item.Instance.WasCollected
                && _sourceItems.Contains(stow.Item) && stow.Item.Quantity == 1
                && stow.Item.Entry.EquipmentSlot == stow.Slot
                && _inventory._equipmentSlots[stow.Slot]?.Pointer == stow.Item.Instance.Pointer
                && SupportsHeldStow(stow.Item.Instance.Data)
                && PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(stow.Item.Instance)) == stow.Modules;

        private bool CaptureSwimmingStow(PlayerInventory inventory, EquipmentSlot slot, bool drop, bool stash,
            bool equipPrevious, bool instantDestroy, bool skipDestroy, bool ignoreMultiplayerCallback, bool ranOriginal)
        {
            if (!_swimmingStashActive) return false;
            try
            {
                if (!ranOriginal || !_swimmingEntryOpen || _writerDepth != 1 || !SwimmingEntryCurrent(_swimmingEntryKey)
                    || inventory?.Pointer != _inventory.Pointer || !NativeHandSlot((int)slot)
                    || drop || !stash || equipPrevious || !instantDestroy || skipDestroy || ignoreMultiplayerCallback)
                    throw new InvalidOperationException("Swimming unequip callback did not match its qualified native entry.");
                var retained = _swimmingHands[(int)slot];
                if (!SwimmingHandMatches(retained) || retained.Captured || !Unchanged())
                    throw new InvalidOperationException("Swimming callback changed or repeated its exact captured hand.");
                retained.Captured = true;
            }
            catch (Exception error) { RejectSwimmingEntry(error.Message, out _); }
            return true; // Capture intent only; never authorize the intercepted native mutation.
        }

        private static bool BeforeSwimmingStash(PlayerAnimatorControl __instance, bool __runOriginal,
            out PlayerInventoryEnforcement __state)
        {
            __state = null;
            var owner = _guard;
            if (owner?._swimmingEntryOpen != true) return true;
            try
            {
                if (!__runOriginal || !owner.SwimmingEntryCurrent(owner._swimmingEntryKey)
                    || __instance?.Pointer != owner._swimmingEntryAnimation.Pointer || _writerDepth != 0
                    || owner._swimmingStashSeen || !owner.SwimmingHandsIdle() || !owner.Unchanged())
                    throw new InvalidOperationException("Swimming stash did not enter once on the exact settled source rig.");
                // Native snapshots right before processing left. No request is dispatched
                // until the entire entry returns, so the second hand cannot be dropped.
                owner._swimmingHands[(int)EquipmentSlot.RightHand] = owner.CaptureSwimmingHand((int)EquipmentSlot.RightHand);
                owner._swimmingHands[(int)EquipmentSlot.LeftHand] = owner.CaptureSwimmingHand((int)EquipmentSlot.LeftHand);
                owner._swimmingStashSeen = owner._swimmingStashActive = true;
                __state = owner;
                return true;
            }
            catch (Exception error) { owner.RejectSwimmingEntry(error.Message, out _); return false; }
        }

        private static Exception AfterSwimmingStash(PlayerInventoryEnforcement __state,
            bool __runOriginal, Exception __exception)
        {
            if (__state == null) return __exception;
            __state._swimmingStashActive = false;
            try
            {
                if (!__runOriginal || __exception != null || !__state.SwimmingEntryCurrent(__state._swimmingEntryKey)
                    || !__state.SwimmingHandsIdle() || !__state.Unchanged())
                    __state.RejectSwimmingEntry("Native swimming stash returned with unconfirmed callback or equipment custody.", out _);
                else __state._swimmingStashReturned = true;
            }
            catch (Exception error) { __state.RejectSwimmingEntry(error.Message, out _); }
            return __exception;
        }

        private BoltMigrationAdmission RejectSwimmingEntry(string reason, out string detail)
        {
            Fail(reason);
            detail = Detail;
            return BoltMigrationAdmission.Rejected;
        }
    }
}
