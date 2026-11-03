// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        public sealed partial class CartDriverAuthority
        {
            private CartPlayerMigrationContext _cartMigrationYield;
            private float _cartMigrationYieldAt;

            internal bool CanYieldCartMigration(PlayerBridge bridge, NativeModeKey key, out string detail)
            {
                try
                {
                    Require(_busy && OnSelectedThread() && !_reentered && !Time.inFixedTimeStep
                        && _cartMigrationYield == null && IsBoundTo(bridge, key)
                        && ReferenceEquals(CartDriverAuthorityHooks.ForMigration(_binding.Cart), this)
                        && !_dismounting && !_exitRequested && !_exitObserved && !_exitDispatch
                        && !_fixed && !_late && !_power && !_visual && !_audio && !_policyWrite && !_motorWrite
                        && _preparedBridge == null && _selectedInput.Input.Sequence == 0 && !_stepCompleted
                        && _pendingState.Motion.InputSequence == 0 && bridge.CartMigrationBoundaryDrained,
                        "The exact active driver lease must drain native simulation, publication and dismount callbacks before migration.");
                    RequireIdentity();
                    RequireSettledServerCart(_binding);
                    CartPlayerCallbackSettlement.RequireSettled(_binding.Cart);
                    detail = "Exact existing driver motor can yield at its completed native boundary.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Driver migration yield pending", error); return false; }
            }

            internal bool TryYieldCartMigration(CartPlayerMigrationContext context,
                PlayerBridge bridge, NativeModeKey key, out string detail)
            {
                if (!CanYieldCartMigration(bridge, key, out detail)) return false;
                try
                {
                    Require(context != null && context.Plan.OldNetworkId == CartNetworkId,
                        "Driver yield must retain the exact enclosing cart plan.");
                    bool participant = false;
                    foreach (var player in context.Players)
                        if (player.Slot == 0 && player.PlayerNetworkId == _driverId
                            && player.SeatNetworkId == _ghostId && SameEntity(player.Entity, _driver)
                            && SameEntity(player.DriverGhost, _ghost)) participant = true;
                    Require(participant, "Driver yield is not a participant of this exact occupied cart context.");
                    _cartMigrationYield = context; _cartMigrationYieldAt = Time.realtimeSinceStartup;
                    // The enclosing adapter acquires its stock cart writer gate synchronously
                    // next. Retain this lease, but do not leave a competing motor policy installed.
                    CartDriverAuthorityHooks.Remove(this);
                    detail = "Existing driver authority yielded to the exact cart preservation transaction.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Driver migration yield unconfirmed", error); return false; }
            }

            internal bool IsCartMigrationYielded(CartPlayerMigrationContext context)
                => ReferenceEquals(_cartMigrationYield, context) && !_released && FailureDetail == null
                    && OnSelectedThread() && CartDriverAuthorityHooks.ForMigration(_binding.Cart) == null;

            internal bool CanRetireCartMigration(CartPlayerMigrationContext context)
                => IsCartMigrationYielded(context) && (!Live(_binding.Entity) || !_binding.Entity.isAttached);

            internal bool TryRetireCartMigration(CartPlayerMigrationContext context, out string detail)
            {
                detail = "The old driver lease remains retained until its exact physical cart has retired.";
                if (!_busy || _reentered || !CanRetireCartMigration(context)) return false;
                CartPlayerCallbackSettlement.RequireSettled(_binding.Cart);
                _released = true; _cartMigrationYield = null;
                detail = "Old motor lease retired without dismounting the retained player or ghost.";
                return true;
            }

            internal bool TryCancelCartMigration(CartPlayerMigrationContext context, out string detail)
            {
                try
                {
                    Require(_busy && !_reentered && IsCartMigrationYielded(context)
                        && _bridge.IsCommittedMovementMode(_mode) && _bridge.CartMigrationBoundaryDrained
                        && !CartWriterHooks.IsHeld(_binding.Cart) && OccupantsFor(_binding.Cart) == null,
                        "Restore the exact stock original before returning its yielded driver lease.");
                    RequireIdentity(); RequireSettledServerCart(_binding);
                    CartPlayerCallbackSettlement.RequireSettled(_binding.Cart);
                    float elapsed = Time.realtimeSinceStartup - _cartMigrationYieldAt;
                    Require(float.IsFinite(elapsed) && elapsed >= 0,
                        "The retained driver input clock cannot be restored.");
                    Require(!_binding.Body.isKinematic && !_binding.Cart._setToKinematic
                        && _binding.CartState.Transform.Simulate?.Pointer == _binding.Entity.transform.Pointer,
                        "The original native driver body and transform policy did not restore.");
                    CartDriverAuthorityHooks.Add(this);
                    if (_hasInput) _receivedAt += elapsed;
                    _fixedTime = float.NaN; _lateFrame = -1;
                    _cartMigrationYield = null;
                    detail = "Same driver lease restored with original input and simulation high-water marks.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Driver migration cancellation unconfirmed", error); return false; }
            }
        }
    }
}
