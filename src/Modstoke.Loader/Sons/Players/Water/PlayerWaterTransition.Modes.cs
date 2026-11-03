// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMovementModes
    {
        private PlayerGroundMovement _swimmingTransitionOwner;
        private PlayerWaterTransition _acceptedSwimmingEntry;
        private double _swimmingEntryReadyTime = double.NaN;

        internal bool TryBindSwimmingTransitionOwner(PlayerGroundMovement owner,
            PlayerBridge bridge,
            PlayerWaterContactAuthority.ServerContinuation contacts, out string detail)
        {
            detail = "Bind one prepared motor to its exact native water mode resolver.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed
                || _swimmingTransitionOwner != null || owner == null || (!owner.IsPrepared && !owner.IsActive)
                || _mode != PlayerMovementMode.Ground || _acceptedSwimmingEntry != null
                || !ReferenceEquals(bridge, _bridge) || !ReferenceEquals(contacts, _waterContacts)) return false;
            _swimmingTransitionOwner = owner;
            return true;
        }

        // RouteSwimming must use this resolver for the restored-native-water path.
        // Pending is not failure and must return before force/input acknowledgement.
        internal BoltMigrationAdmission ResolveOrderedSwimming(bool serverGrounded, bool serverJumping,
            out PlayerMovementModeState state, out string detail)
        {
            state = default;
            detail = "Swimming requires the retained transition producer on this native water motor.";
            if (_waterContacts == null)
                return TryResolveMode(serverGrounded, serverJumping, out state, out detail)
                    ? BoltMigrationAdmission.Ready : BoltMigrationAdmission.Rejected;
            if (_swimmingTransitionOwner == null || !Current() || !_nativeWaterReady || !Time.inFixedTimeStep)
                return BoltMigrationAdmission.Rejected;
            try
            {
                if (!_swimmingTransitionOwner.PumpSwimmingEntryRelease(out detail))
                    return BoltMigrationAdmission.Rejected;
                if (_acceptedSwimmingEntry == null && _swimmingTransitionOwner.HasPendingSwimmingEntry)
                {
                    detail = "The committed swimming edge awaits source application and paired release.";
                    return BoltMigrationAdmission.Pending;
                }
                double now = Time.fixedTimeAsDouble;
                if (!double.IsFinite(now) || !double.IsNaN(_lastResolvedTime) && now < _lastResolvedTime
                    || now == _lastAppliedTime) return BoltMigrationAdmission.Rejected;
                if (!TryObserveWater(out var water, out detail)) return BoltMigrationAdmission.Pending;
                bool inWater = water.InWater;
                bool expired = !double.IsNaN(_exitStarted) && now - _exitStarted > DisableSwimmingAfter;
                bool entry = inWater && water.Position.y + SwimEnterOffset < water.Height.Value && (!_waterBlocked || expired);
                var previous = _mode;

                // Allocate at the original authoritative threshold branch, before
                // changing mode/history. Subsequent observations cannot replace it.
                if (_acceptedSwimmingEntry != null || entry)
                {
                    var admission = _swimmingTransitionOwner.SettleSwimmingEntry(this,
                        _acceptedSwimmingEntry?.Sample ?? water.NativeSample.Value,
                        _acceptedSwimmingEntry?.Position ?? water.Position,
                        _acceptedSwimmingEntry?.Grounded ?? serverGrounded,
                        _acceptedSwimmingEntry?.Jumping ?? serverJumping,
                        out var accepted, out detail);
                    if (accepted != null) _acceptedSwimmingEntry = accepted;
                    if (admission != BoltMigrationAdmission.Ready) return admission;
                    var retained = _acceptedSwimmingEntry;
                    _water = water; _lastResolvedTime = _swimmingEntryReadyTime = now;
                    // Return the proposed state for ordinary profile/jump validation.
                    // CommitResolvedSwimmingEntry owns the later actual mode/body write.
                    state = _state = new(PlayerMovementMode.Swim,
                        retained.Position.y + WaterlineOffset - retained.Sample.Height < 0,
                        false, true, serverGrounded, serverJumping, water.Position, Vector3.zero,
                        water.Height, water.SurfaceVelocity, 0, Time.frameCount, now);
                    return BoltMigrationAdmission.Ready;
                }
                else
                {
                    if (expired) LeaveSwimming();
                    if (inWater && water.Position.y + SwimEnterOffset < water.Height.Value)
                    {
                        _wasInWater = true;
                        _underwater = water.Position.y + WaterlineOffset - water.Height.Value < 0;
                    }
                    else if (!inWater || water.Position.y + SwimExitOffset > water.Height.Value)
                    {
                        if (_waterBlocked)
                        {
                            if ((serverGrounded || serverJumping) && (inWater || _wasInWater)) LeaveSwimming();
                            else _exitStarted = now;
                            _waterBlocked = false;
                        }
                    }
                }
                if (_mode != PlayerMovementMode.Swim) _swimmingUnderwater = false;
                // Current geometry serves only this force step, never edge identity.
                _water = water; _lastResolvedTime = now;
                state = _state = new(_mode, _underwater, _swimmingUnderwater, previous != _mode,
                    serverGrounded, serverJumping, water.Position, Vector3.zero, water.Height,
                    water.SurfaceVelocity, 0, Time.frameCount, now);
                return BoltMigrationAdmission.Ready;
            }
            catch (Exception error)
            {
                Stop("Ordered swimming failed: " + error.Message, out detail);
                return BoltMigrationAdmission.Rejected;
            }
        }

        internal bool CommitResolvedSwimmingEntry(out string detail)
        {
            detail = "No new swimming entry is selected.";
            if (_waterContacts == null || _acceptedSwimmingEntry == null) return true;
            if (!Current() || !Time.inFixedTimeStep || _swimmingEntryReadyTime != Time.fixedTimeAsDouble)
            { detail = "Commit swimming only in the same validated force-step selection."; return false; }
            var retained = _acceptedSwimmingEntry;
            if (!_swimmingTransitionOwner.CommitSwimmingEntry(this, retained, () =>
            {
                // Re-entering before the delayed exit still runs native cleaning
                // and stowing, but does not reset an already-swimming body/latch.
                if (_mode == PlayerMovementMode.Swim
                    && !double.IsNaN(_exitStarted)
                    && Time.fixedTimeAsDouble - _exitStarted > DisableSwimmingAfter) LeaveSwimming();
                _mode = PlayerMovementMode.Swim;
                _waterBlocked = _wasInWater = true;
                _exitStarted = double.NaN;
                _underwater = retained.Position.y + WaterlineOffset - retained.Sample.Height < 0;
            }, out detail)) return false;
            _acceptedSwimmingEntry = null; _swimmingEntryReadyTime = double.NaN;
            return true;
        }
    }
}
