// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerAuthorityDynamics
    {
        internal bool TryReadSurvivalEnvironmentSwimming(PlayerBridge bridge,
            PlayerCarrier carrier, out bool swimming)
        {
            swimming = false;
            if (!OwnsCarrier(carrier, bridge) || FailureReason != null || _movement == null) return false;
            RequireCurrent();
            if (_movement.IsYieldedToNativeMode && !OwnsYieldedMotor(carrier, bridge)) return false;
            return _movement.TryReadSurvivalEnvironmentSwimming(bridge, carrier, out swimming);
        }
    }

    public sealed partial class PlayerGroundMovement
    {
        internal bool TryReadSurvivalEnvironmentSwimming(PlayerBridge bridge,
            PlayerCarrier carrier, out bool swimming)
        {
            swimming = false;
            if (Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || !ReferenceEquals(bridge, _bridge) || !ReferenceEquals(carrier, _nativeCarrier)
                || (!IsPrepared && !IsActive) || StopReason != null || _insideMovementStep
                || _swimAwaitingPhysics || _movementModes == null || _movementModes.IsDisposed)
                return false;
            if (_nativeModeSuspended)
            {
                // Native entry is only admitted from non-swimming ground. Never interpret
                // a missing/stale yielded mode as a dry actor.
                if (!NativeModeIdentityCurrent() || _swimActive) return false;
            }
            else if (!Current() || !BorrowedCarrierCurrent(_activated)) return false;
            swimming = _swimActive;
            return true;
        }
    }
}
