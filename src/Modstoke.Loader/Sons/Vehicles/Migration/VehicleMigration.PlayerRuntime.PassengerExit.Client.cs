// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using Proposal = Modstoke.Sons.PlayerModeTransitionProposal;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        /// <summary>
        /// Run once inside the source sampler's approved transition scope. Approval is read
        /// from the authenticated bridge; the caller still commits its exact receipt afterward.
        /// The native action, controls, player root and camera are never replaced.
        /// </summary>
        public static bool TryApplyApprovedClientCartPassengerExit(PlayerBridge bridge,
            Proposal proposal, PlayerGolfCartPassengerAction action, GolfCartController cart,
            bool skipReequip, out string detail)
            => TryApplyApprovedClientCartExit(bridge, proposal, action, cart, false, skipReequip, out detail);

        public static bool TryApplyApprovedClientCartDriverExit(PlayerBridge bridge,
            Proposal proposal, PlayerGolfCartDriverAction action, GolfCartController cart,
            bool skipReequip, out string detail)
            => TryApplyApprovedClientCartExit(bridge, proposal, action, cart, true, skipReequip, out detail);

        private static bool TryApplyApprovedClientCartExit(PlayerBridge bridge,
            Proposal proposal, PlayerGolfCartAction action, GolfCartController cart, bool driver,
            bool skipReequip, out string detail)
        {
            detail = "The exact received server approval and retained local cart action are required.";
            if (!Enter()) return false;
            CartPlayerProxyAuthority proxy = null;
            bool attempted = false;
            try
            {
                RequireContext();
                CartPassengerExitHooks.Arm();
                PlayerModeTransitionApproval approval = default;
                BoltEntity entity = null;
                Require(CartPassengerExitHooks.Client == null && !Time.inFixedTimeStep
                    && bridge != null && !bridge.IsServer && bridge.IsGameplayActive && bridge.Peer.IsCurrent
                    && bridge.TryPeekMovementTransitionApproval(out approval)
                    && approval.Proposal == proposal && approval.Decision == PlayerModeTransitionDecision.Approved
                    && proposal.MigrationId == bridge.Plan.Id && proposal.Generation == bridge.Plan.Generation
                    && proposal.NetworkId == bridge.NetworkId
                    && proposal.Expected.Mode == (driver ? NativeMode.GolfCartDriver : NativeMode.GolfCartPassenger)
                    && (driver ? proposal.Expected.SeatIndex == 0 : proposal.Expected.SeatIndex is >= 1 and <= 3)
                    && proposal.Expected.TransitionSequence < uint.MaxValue
                    && proposal.Requested == NativeModeKey.Ground(proposal.Expected.TransitionSequence + 1)
                    && bridge.IsCommittedMovementMode(proposal.Expected)
                    && bridge.TryReadCurrentTarget(out entity) && SameEntity(entity, LocalPlayer.Entity), detail);
                Require(Live(cart) && Live(cart.entity) && cart.entity.isAttached && !cart.entity.isOwner
                    && cart.entity.networkId.PackedValue == proposal.Expected.AnchorNetworkId
                    && !CartWriterHooks.IsHeld(cart) && OccupantsFor(cart) == null
                    && !IsOccupiedPlayerWriterHeld(action) && Live(action) && Live(LocalPlayer.PlayerBase)
                    && action.transform.IsChildOf(LocalPlayer.PlayerBase.transform),
                    "The retained client action or replacement cart is unavailable or held.");
                if (driver) RequireExactSaveType<PlayerGolfCartDriverAction>(action);
                else RequireExactSaveType<PlayerGolfCartPassengerAction>(action);
                var state = action._state;
                int slot = proposal.Expected.SeatIndex;
                Require(state != null && !state.WasCollected && state.IsConnected
                    && state.ConnectedGolfCartController?.Pointer == cart.Pointer && state.ConnectedSlotIndex == slot
                    && cart._isLocalConnectedSlots[slot]
                    && (driver ? cart._localPlayerDriverAction?.Pointer == action.Pointer
                        : cart._localConnectedActions[slot]?.Pointer == action.Pointer)
                    && action._hasSetupInputs && (driver ? action.TryCast<PlayerGolfCartDriverAction>()._zoomMapAction != null
                        : action.TryCast<PlayerGolfCartPassengerAction>()._zoomMapAction != null),
                    "The original connected cart state and native controls must still be installed.");
                proxy = CartPlayerProxyAuthority.Find(cart);
                if (driver)
                {
                    Require(proxy != null, "The exact migrated driver replica authority is required for source exit.");
                    proxy.RequireLocalDriverExit(action, bridge.NetworkId);
                }
                var root = LocalPlayer.Transform;
                var camera = LocalPlayer.MainCamTr;
                var character = LocalPlayer.FpCharacter;
                var body = LocalPlayer.Rigidbody;
                var animator = LocalPlayer.Animator;
                var control = LocalPlayer.AnimControl;
                var mainRotator = LocalPlayer.MainRotator;
                var camRotator = LocalPlayer.CamRotator;
                Require(Live(root) && Live(camera) && Live(character) && Live(body) && Live(animator)
                    && Live(control) && Live(mainRotator) && Live(camRotator), "The original local cart rig is incomplete.");
                var receipt = new LocalPassengerExit(action, cart, slot, skipReequip);
                CartPassengerExitHooks.Client = receipt;
                try
                {
                    // The server already executed the received slot mutation. Sending another
                    // legacy disconnect event would create a second, unordered mutation.
                    attempted = true;
                    if (driver) cart.DisconnectLocalDriver(false, skipReequip);
                    else cart.DisconnectPassenger(slot, false, skipReequip);
                }
                finally { CartPassengerExitHooks.Client = null; }
                Require(receipt.Failure == null && receipt.Completed == (driver ? 31 : 15) && !_reentered
                    && Live(action) && action._state != null && !action._state.WasCollected
                    && action._state.Pointer != state.Pointer && !action._state.IsConnected
                    && !action._hasSetupInputs && (driver ? action.TryCast<PlayerGolfCartDriverAction>()._zoomMapAction == null
                        : action.TryCast<PlayerGolfCartPassengerAction>()._zoomMapAction == null)
                    && !cart._isLocalConnectedSlots[slot]
                    && (driver ? cart._localPlayerDriverAction == null && cart._driverConnectIkCoroutine == null
                        && cart._pendingDriverIkTargetsController == null
                        : cart._localConnectedActions[slot] == null && cart._localConnectedActors[slot] == null),
                    receipt.Failure ?? "Original cart disconnect, controls, player hook or driver IK did not complete.");
                Require(SameEntity(LocalPlayer.Entity, entity) && LocalPlayer.Transform?.Pointer == root.Pointer
                    && LocalPlayer.MainCamTr?.Pointer == camera.Pointer && LocalPlayer.FpCharacter?.Pointer == character.Pointer
                    && LocalPlayer.Rigidbody?.Pointer == body.Pointer && LocalPlayer.Animator?.Pointer == animator.Pointer
                    && LocalPlayer.AnimControl?.Pointer == control.Pointer && LocalPlayer.MainRotator?.Pointer == mainRotator.Pointer
                    && LocalPlayer.CamRotator?.Pointer == camRotator.Pointer,
                    "Native cart exit changed the retained player rig or camera identity.");
                Require(bridge.TryPeekMovementTransitionApproval(out var completedApproval) && completedApproval == approval
                    && bridge.IsCommittedMovementMode(proposal.Expected), "The authenticated exit approval changed during native application.");
                if (driver) proxy.CompleteLocalDriverExit(action);
                detail = "Original cart exit completed on the same rig and camera; commit this bridge approval before resuming ground.";
                return true;
            }
            catch (Exception error)
            {
                if (attempted) proxy?.Quarantine();
                detail = Failure("Approved local cart exit unconfirmed; do not replay", error); return false;
            }
            finally { _busy = false; }
        }

        private sealed record LocalPassengerExit(PlayerGolfCartAction Action, GolfCartController Cart, int Slot, bool SkipReequip)
        {
            internal int Entered, Completed;
            internal string Failure;
        }
    }
}
