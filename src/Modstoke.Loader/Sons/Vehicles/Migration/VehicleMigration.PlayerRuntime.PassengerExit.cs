// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using global::Sons.Gameplay;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using Proposal = Modstoke.Sons.PlayerModeTransitionProposal;
using ExitRoutine = global::Sons.Gameplay.GolfCartController._DelayedFixPlayerProxyRotation_d__270;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        /// <summary>Apply the bridge's received passenger exit once. Retain the policy until completion.</summary>
        public static bool TryApplyReceivedServerCartPassengerExit(PlayerBridge bridge, out string detail)
        {
            detail = "An active migrated passenger and its exact received exit are required.";
            if (!Enter()) return false;
            CartPlayerPassengerAuthority passenger = null;
            try
            {
                RequireContext();
                passenger = CartPlayerPassengerAuthority.Find(bridge);
                Require(passenger != null, detail);
                passenger.ApplyReceivedExit();
                detail = "Native passenger slot disconnect applied once; waiting for its retained proxy continuation.";
                return true;
            }
            catch (Exception error)
            {
                if (passenger?._exitRequested == true) passenger.Quarantine();
                detail = Failure("Passenger exit rejected or unconfirmed", error); return false;
            }
            finally { _busy = false; }
        }

        /// <summary>Approve ground and retire only this passenger after the native exit actually settles.</summary>
        public static bool TryCompleteReceivedServerCartPassengerExit(PlayerBridge bridge,
            out NativeModeKey ground, out string detail)
        {
            ground = default;
            detail = "Wait for the exact native passenger continuation to finish.";
            if (!Enter()) return false;
            CartPlayerPassengerAuthority passenger = null;
            try
            {
                RequireContext();
                if (Time.inFixedTimeStep) return false;
                passenger = CartPlayerPassengerAuthority.Find(bridge);
                Require(passenger != null, "No retained server passenger exit exists for this bridge.");
                passenger.RequireExiting();
                if (!passenger._exitDispatchCompleted || !passenger._exitComplete) return false;
                passenger.RequireEmptySeat();
                passenger.ApproveAndRetire();
                ground = passenger._exitProposal.Requested;
                detail = "Native passenger exit settled; exact ground proposal approved and passenger policy retired.";
                return true;
            }
            catch (Exception error)
            {
                if (passenger?._exitRequested == true) passenger.Quarantine();
                detail = Failure("Passenger exit completion unconfirmed", error); return false;
            }
            finally { _busy = false; }
        }

        // Consult only at the existing driver's DisconnectSlot gate. The typed hook below
        // additionally checks the exact event pointer, player and slot before native mutation.
        internal static bool IsApplyingReceivedPassengerExit(GolfCartController cart)
            => OnSelectedThread() && CartPlayerPassengerAuthority.DispatchFor(cart) != null;

        private sealed partial class CartPlayerPassengerAuthority
        {
            internal bool _exitRequested, _exitDispatchCompleted, _exitComplete;
            internal Proposal _exitProposal;
            private GolfCartConnectSlotEvent _exitEvent;
            private ExitRoutine _exitRoutine;
            private bool _exitDispatchEntered;

            internal static CartPlayerPassengerAuthority Find(PlayerBridge bridge)
            {
                foreach (var item in Volatile.Read(ref _active))
                    if (ReferenceEquals(item._bridge, bridge)) return item;
                return null;
            }

            internal static CartPlayerPassengerAuthority ForSeat(GolfCartController cart, int slot)
            {
                foreach (var item in Volatile.Read(ref _active))
                    if (item._binding.Cart.Pointer == cart?.Pointer && item._player.Slot == slot) return item;
                return null;
            }

            internal static CartPlayerPassengerAuthority DispatchFor(GolfCartController cart)
            {
                foreach (var item in Volatile.Read(ref _active))
                    if (item._binding.Cart.Pointer == cart?.Pointer && item._exitEvent != null && !item._quarantined) return item;
                return null;
            }

            internal void ApplyReceivedExit()
            {
                if (_exitRequested) { RequireExiting(); return; }
                Require(_started && !_inside && !Time.inFixedTimeStep && _selected.Input.Sequence == 0,
                    "Finish the exact selected passenger projection before requesting exit.");
                RequireIdentity(true);
                Publish();
                Require(_pending.Motion.Sequence == 0, "Publish the completed passenger input acknowledgement before exit.");
                Require(_bridge.TryPeekMovementTransitionProposal(out var proposal)
                    && proposal.Expected == _key && proposal.Expected.Mode == NativeMode.GolfCartPassenger
                    && _key.TransitionSequence < uint.MaxValue
                    && proposal.Requested == NativeModeKey.Ground(_key.TransitionSequence + 1)
                    && proposal.MigrationId == _bridge.Plan.Id && proposal.Generation == _bridge.Plan.Generation
                    && proposal.NetworkId == _player.PlayerNetworkId && _bridge.Peer.ServerSession != null,
                    "Exit requires the exact authenticated passenger-to-ground bridge proposal.");
                Require(!CartWriterHooks.IsHeld(_binding.Cart) && OccupantsFor(_binding.Cart) == null,
                    "Passenger exit cannot cross a cart migration hold.");
                CartPlayerCallbackSettlement.RequireSettled(_binding.Cart);
                _exitProposal = proposal;
                var request = GolfCartConnectSlotEvent.Create(_binding.Entity);
                Require(request != null && !request.WasCollected, "Native passenger disconnect event allocation failed.");
                try
                {
                    request.SlotIndex = _player.Slot; request.OccupiedEntity = _player.Entity; request.Disconnect = true;
                    _exitEvent = request; _exitRequested = true;
                    _binding.Cart.DisconnectSlot(request);
                    Require(!_quarantined && !_reentered && _exitDispatchCompleted
                        && _binding.CartState.ConnectedSlots.GetValueNetworkId(_player.Slot).PackedValue == 0,
                        "The original native passenger disconnect did not complete its exact slot mutation.");
                }
                finally
                {
                    _exitEvent = null;
                    request.FreeStorage();
                }
            }

            internal void RequireExiting()
            {
                Require(OnSelectedThread() && _started && !_quarantined && _exitRequested && _binding.Matches()
                    && _binding.Entity.isOwner && _bridge.IsServer && _bridge.IsGameplayActive
                    && _bridge.Peer.IsCurrent && _bridge.Peer.ServerSession != null
                    && _bridge.NetworkId == _player.PlayerNetworkId && Live(_player.Projection)
                    && _bridge.TryReadCurrentTarget(out var entity) && SameEntity(entity, _player.Entity)
                    && _bridge.IsCommittedMovementMode(_key)
                    && _bridge.TryPeekMovementTransitionProposal(out var proposal) && proposal == _exitProposal
                    && !CartWriterHooks.IsHeld(_binding.Cart) && OccupantsFor(_binding.Cart) == null,
                    "The pending passenger exit lost its exact cart, player, input epoch or authenticated proposal.");
                ulong occupant = _binding.CartState.ConnectedSlots.GetValueNetworkId(_player.Slot).PackedValue;
                Require(occupant == 0 || (_exitEvent != null && occupant == _player.PlayerNetworkId),
                    "A different occupant arrived before the retained passenger exit settled.");
            }

            internal bool AcceptDispatch(GolfCartConnectSlotEvent request)
            {
                if (!OnSelectedThread() || _quarantined || !_exitRequested || _exitEvent == null || _exitDispatchEntered
                    || request?.Pointer != _exitEvent.Pointer || request.WasCollected
                    || !request.Disconnect || request.SlotIndex != _player.Slot
                    || request.GetOccupiedEntityNetworkIdValue().PackedValue != _player.PlayerNetworkId
                    || !SameEntity(request.OccupiedEntity, _player.Entity)) return false;
                _exitDispatchEntered = true;
                return true;
            }

            internal static CartPlayerPassengerAuthority ObserveExit(GolfCartController cart, Transform target, ExitRoutine routine)
            {
                foreach (var item in Volatile.Read(ref _active))
                {
                    if (item._binding.Cart.Pointer != cart.Pointer || item._player.Projection?.Pointer != target?.Pointer) continue;
                    try
                    {
                        item.RequireExiting();
                        Require(item._exitRoutine == null && routine.__4__this?.Pointer == cart.Pointer
                            && routine.targetTransform?.Pointer == target.Pointer,
                            "Unexpected or duplicate native passenger exit continuation.");
                        item._exitRoutine = routine;
                    }
                    catch { item.Quarantine(); throw; }
                    return item;
                }
                return null;
            }

            internal void RequireContinuation(ExitRoutine routine)
            {
                RequireExiting();
                Require(!_exitComplete && _exitRoutine?.Pointer == routine.Pointer
                    && routine.__4__this?.Pointer == _binding.Cart.Pointer
                    && routine.targetTransform?.Pointer == _player.Projection.Pointer,
                    "Passenger continuation identity changed.");
            }

            internal void CompleteContinuation(bool ran, bool more, Exception error)
            {
                if (error != null) Quarantine();
                else if (ran && !more) _exitComplete = true;
            }

            internal void RequireEmptySeat()
            {
                int slot = _player.Slot;
                Require(!_inside && _selected.Input.Sequence == 0 && _pending.Motion.Sequence == 0
                    && _binding.CartState.ConnectedSlots.GetValueNetworkId(slot).PackedValue == 0
                    && _binding.Cart._externalEntityTransforms[slot] == null
                    && _binding.Cart._externalPlayerAnimators[slot] == null
                    && _binding.Cart._externalVailActors[slot] == null
                    && !_binding.Cart._isLocalConnectedSlots[slot]
                    && _binding.Cart._localConnectedActions[slot] == null
                    && _binding.Cart._localConnectedActors[slot] == null,
                    "The native passenger slot, projection or input receipt has not settled.");
            }

            internal void ApproveAndRetire()
            {
                var current = Volatile.Read(ref _active);
                int index = Array.IndexOf(current, this);
                Require(index >= 0, "The passenger authority registry changed before retirement.");
                var next = new CartPlayerPassengerAuthority[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, next.Length - index);
                Require(_bridge.TryApproveMovementTransition(_exitProposal, out _, out var detail), detail);
                Volatile.Write(ref _active, next);
            }
        }
    }
}
