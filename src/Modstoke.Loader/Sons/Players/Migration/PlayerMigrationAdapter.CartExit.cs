// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMigrationAdapter
    {
        private VehiclePreservation.CartPlayerGroundExit _serverPassengerGroundExit;

        /// <summary>
        /// Pump from the aggregate's selected-thread render update after native callbacks have
        /// returned. Does not activate gameplay or acquire/yield a motor. Pending returns false;
        /// an uncertain dispatched exit sets IsUnconfirmed and cannot be retried as a new exit.
        /// </summary>
        internal bool TryPumpServerCartPassengerExit(out string detail)
        {
            detail = "No active yielded server cart exit is ready.";
            if (!Enter(out detail)) return false;
            try
            {
                if (Time.inFixedTimeStep) return false;
                if (_serverPassengerGroundExit?.IsComplete == true) _serverPassengerGroundExit = null;
                if (!ServerMovementLifetimeCurrent() || _bridge?.IsGameplayActive != true
                    || _serverMovement?.IsActive != true)
                {
                    if (_serverPassengerGroundExit?.HasDispatched == true)
                        throw new InvalidOperationException("The active passenger exit lost its aggregate movement lifetime.");
                    return false;
                }
                if (_serverPassengerGroundExit == null)
                {
                    if (!_bridge.TryPeekMovementTransitionProposal(out var proposal)
                        || proposal.Expected.Mode is not (NativeMode.GolfCartPassenger or NativeMode.GolfCartDriver)
                        || proposal.Requested.Mode != NativeMode.Ground) return false;
                    if (!VehiclePreservation.CartPlayerGroundExit.TryPrepare(_bridge, _carrier,
                        _serverMovement, out _serverPassengerGroundExit, out detail)) return false;
                }
                bool complete = _serverPassengerGroundExit.TryPump(out detail);
                RequireContext();
                if (_serverPassengerGroundExit.IsFaulted)
                { _unconfirmed = true; detail = _serverPassengerGroundExit.FailureReason; }
                return complete;
            }
            catch (Exception error)
            {
                detail = Failure("Server passenger ground handoff unconfirmed", error);
                _serverPassengerGroundExit?.Fail(detail);
                detail = _serverPassengerGroundExit?.FailureReason ?? detail;
                _unconfirmed = true;
                return false;
            }
            finally { Exit(detail); }
        }
    }
}
