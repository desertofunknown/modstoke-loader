// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        private PlayerLifecycle.SourceInputHold _sourceHold;
        private PlayerLifecycle.SourceHoldAdmission _sourceAcceptedWork;
        private bool _pumpingSourceHold;

        internal string SourceInputHoldFailure => _sourceHold?.FailureReason;
        internal bool HasSourceInputHold => _sourceHold != null;
        internal bool SourcePhysicsHeld => _sourceHold?.IsPhysicsHeld == true;
        private bool SourceMovementHeld => _sourceHold?.OwnsPhysics == true;

        /// <summary>
        /// Arm while the exact rebound lifecycle is still paused and bridge/movement
        /// are prepared. acceptedWork must read all accepted inventory/held/life custody
        /// without mutating it. New local inventory input must use IsSourceInputFenced;
        /// incoming authoritative work must yield the hold before touching the rig.
        /// </summary>
        internal bool TryArmSourceInputHold(PlayerBridge bridge,
            PlayerWaterPresentation water, PlayerLifecycle lifecycle,
            PlayerLifecycle.SourceHoldAdmission acceptedWork, out string detail)
        {
            detail = "Arm the exact prepared movement/water/lifecycle hold before releasing initial migration writers.";
            if (Environment.CurrentManagedThreadId != _thread || !IsPrepared || !ReferenceEquals(_prepared, this)
                || _sourceHold != null || acceptedWork == null || lifecycle == null
                || !ReferenceEquals(bridge, _bridge) || !ReferenceEquals(water, _water) || water == null
                || water.IsDisposed || water.FailureReason != null || !water.IsSourceSchedulePrepared
                || Time.inFixedTimeStep || _insideFixed || _awaitingPhysics || !RetainedTargetCurrent(true)
                || !Same(lifecycle.Root, _rig.Root) || !Same(lifecycle.OriginalEntity, _rig.Original)) return false;
            _sourceAcceptedWork = acceptedWork;
            if (!lifecycle.TryArmSourceInputHold(bridge, ReadSourceHoldAcceptedWork, out var hold, out detail))
            { _sourceAcceptedWork = null; return false; }
            _sourceHold = hold;
            return true;
        }

        /// <summary>Same stack as successful TryResumeRebound, before movement activation or any native consumer.</summary>
        internal bool TryAcknowledgeSourceLifecycleRelease(PlayerLifecycle lifecycle,
            BoltMigrationMutationResult releaseResult, out string detail)
        {
            detail = "The exact source hold must have been armed before lifecycle release.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _activated || _sourceHold == null
                || _water == null || !_water.IsActive || _insideFixed || _awaitingPhysics || Time.inFixedTimeStep) return false;
            if (!_sourceHold.TryAcknowledgeRelease(lifecycle, releaseResult, out detail)) return false;
            // Freshness may have elapsed during release. Acquire/retain the new hold,
            // never reinterpret the initial kinematic snapshot as the live resume state.
            return PumpSourceInputHold(out detail) != BoltMigrationAdmission.Rejected;
        }

        internal BoltMigrationAdmission PumpSourceInputHold(out string detail)
        {
            detail = "The source water input/body owner is not armed.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _sourceHold == null || _water == null)
                return BoltMigrationAdmission.Rejected;
            if (_pumpingSourceHold)
            { detail = "Retain the current source hold through its nested native consumer."; return BoltMigrationAdmission.Pending; }
            _pumpingSourceHold = true;
            try
            {
                if (_water.IsDisposed || _water.FailureReason != null)
                {
                    _sourceHold.Retire(_water.FailureReason ?? "The source water endpoint retired.");
                    detail = _sourceHold.FailureReason;
                    return BoltMigrationAdmission.Rejected;
                }
                // The parent applies the first water sample under the initial lifecycle
                // pause. Prepared movement must not drive the live owner before ack.
                if (!_sourceHold.IsAcknowledged) return _sourceHold.Pump(false, out detail);
                bool fresh = _water.IsActive && _water.TryPump(out detail) && !_water.RequiresSourceInputHold;
                if (_water.IsDisposed || _water.FailureReason != null)
                {
                    _sourceHold.Retire(_water.FailureReason ?? "The source water endpoint retired.");
                    detail = _sourceHold.FailureReason;
                    return BoltMigrationAdmission.Rejected;
                }
                var result = _sourceHold.Pump(fresh, out detail);
                if (result == BoltMigrationAdmission.Ready && _water.RequiresSourceInputHold)
                    return _sourceHold.Pump(false, out detail);
                return result;
            }
            catch (Exception error)
            {
                _sourceHold.Fail("Source hold coordination failed: " + error.Message);
                detail = _sourceHold.FailureReason;
                return BoltMigrationAdmission.Rejected;
            }
            finally { _pumpingSourceHold = false; }
        }

        internal void ValidateSourceWaterProjection(PlayerWaterPresentation water)
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed || !ReferenceEquals(water, _water))
                throw new InvalidOperationException("Water projection lost its exact prepared movement owner.");
            _sourceHold?.ValidateWaterProjection();
        }

        private BoltMigrationAdmission ReadSourceHoldAcceptedWork(out string detail)
        {
            detail = "The source hold no longer owns its retained movement lifetime.";
            if (_disposed || !_bridge.IsGameplayActive || !_bridge.TryReadCurrentTarget(out var target)
                || !_rig.Matches(target, true, true) || _sourceAcceptedWork == null)
                return BoltMigrationAdmission.Rejected;
            // Pending state is retained, never cleared to manufacture a hold boundary.
            if (_insideFixed || _awaitingPhysics || _insideCoroutine || _jumpPending || _sendingNative || _transition != null)
            { detail = "Accepted movement sample, jump or mode transition is still draining."; return BoltMigrationAdmission.Pending; }
            if (_nativeMode != null || _zipline != null || _deferredNativeExit != null || CartMigrationHeld)
            {
                // Existing special-mode custody is not a fault. Fresh water needs no
                // acquisition; stale water must wait for that owner to settle or exit.
                detail = "Accepted special-mode custody remains active; stale-water physics acquisition awaits a qualified settled motor.";
                return BoltMigrationAdmission.Pending;
            }
            if (LifeMovementBlocked)
            { detail = "The retained life owner still owns accepted native work."; return BoltMigrationAdmission.Pending; }
            if (_unsent.Sequence != 0)
            {
                FlushInput();
                if (_disposed) { detail = StopReason; return BoltMigrationAdmission.Rejected; }
                if (_unsent.Sequence != 0)
                { detail = "Accepted movement publication is waiting for transport capacity."; return BoltMigrationAdmission.Pending; }
            }
            return _sourceAcceptedWork(out detail);
        }

        private bool SourceHoldCurrent(bool topology) => _sourceHold != null && _sourceHold.IdentityCurrent()
            && _bridge.IsCommittedMovementMode(NativeModeKey.Ground(_modeSequence))
            && _bridge.TryReadCurrentTarget(out var target) && _rig.Matches(target, topology, true);
    }
}
