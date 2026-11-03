// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        private PlayerLifePresentation _life;
        private ulong _lifeCompletedSequence;
        private bool _lifeHeld;

        internal bool TryBindLife(PlayerBridge bridge, PlayerLifePresentation owner,
            out string detail)
        {
            detail = "Bind the exact source life owner while movement and its bridge are prepared and inactive.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _activated || _starting
                || !ReferenceEquals(_prepared, this) || !ReferenceEquals(bridge, _bridge)
                || _bridge.IsDisposed || _bridge.IsServer || _bridge.IsGameplayActive || _life != null
                || owner == null || owner.IsDisposed || owner.FailureReason != null || owner.BlocksMovement
                || owner.CompletedSequence != 0 || Time.inFixedTimeStep || _insideFixed || _awaitingPhysics) return false;
            try
            {
                if (!owner.TryValidateRig(bridge, _rig.Character, out detail)) return false;
                if (!RetainedTargetCurrent(true))
                { detail = "The prepared movement rig no longer matches its retained objects and topology."; return false; }
                _life = owner;
                _lifeCompletedSequence = owner.CompletedSequence;
                detail = "Exact native life owner bound without starting callbacks, movement or presentation.";
                return true;
            }
            catch (Exception error)
            { detail = "Movement life binding refused (" + error.Message + ")."; return false; }
        }

        private bool LifeMovementBlocked => _life != null && (_life.IsDisposed || _life.FailureReason != null
            || _life.BlocksMovement || _lifeHeld || _life.CompletedSequence != _lifeCompletedSequence);

        private bool LifeCurrent() => !_disposed && _activated && Environment.CurrentManagedThreadId == _thread
            && _bridge.IsGameplayActive && _life != null && !_life.IsDisposed && _life.FailureReason == null
            && _nativeMode == null && _transition == null && _deferredNativeExit == null
            && _zipline == null && !CartMigrationHeld && !_sendingNative
            && _bridge.IsCommittedMovementMode(NativeModeKey.Ground(_modeSequence))
            && _bridge.TryReadCurrentTarget(out var entity) && _rig.MatchesLife(entity, _bridge, _life);

        // The native owner, not FPC.enabled or health, owns this interval. Do not invoke
        // life callbacks, restore controls, or acknowledge a source command from movement.
        private bool HoldLifeMovement()
        {
            var owner = _life;
            if (owner == null) return false;
            try
            {
                if (_disposed) return true;
                if (owner.IsDisposed || owner.FailureReason != null)
                { Stop(owner.FailureReason ?? "The retained native life owner retired."); return true; }
                ulong completed = owner.CompletedSequence;
                if (completed < _lifeCompletedSequence)
                { Stop("The retained native life completion sequence regressed."); return true; }
                if (owner.BlocksMovement)
                {
                    if (!LifeCurrent())
                    { Stop("Native life lost its exact retained ground rig or movement custody."); return true; }
                    if (!_lifeHeld)
                    {
                        _lifeHeld = true;
                        ClearLifePrediction();
                    }
                    return true;
                }
                if (!_lifeHeld && completed == _lifeCompletedSequence) return false;

                // A whole action may finish between observations. Its completion still
                // retires every old sample, even if no fixed hook observed its hold.
                _lifeHeld = true;
                DiscardCapturedStep();
                if (Time.inFixedTimeStep) return true;
                if (completed == _lifeCompletedSequence)
                { Stop("Native life released movement without a completed get-up sequence."); return true; }
                if (!owner.TryValidateRig(_bridge, _rig.Character, out var detail))
                { Stop(detail ?? "The native life owner no longer validates its retained rig."); return true; }
                if (!_bridge.IsGameplayActive || _nativeMode != null || _transition != null
                    || _deferredNativeExit != null || _zipline != null || CartMigrationHeld || _sendingNative
                    || !_bridge.IsCommittedMovementMode(NativeModeKey.Ground(_modeSequence))
                    || !_bridge.TryReadCurrentTarget(out var entity) || !_rig.Matches(entity, true)
                    || !(_rig.GroundMode() || (_swimmingAllowed && _rig.SwimMode())))
                { Stop("Native get-up did not restore the exact retained movement rig."); return true; }
                bool allowed = _groundModeAllowed();
                if (owner.BlocksMovement) return true;
                if (!allowed || owner.CompletedSequence != completed || !Current(true)
                    || !(_rig.GroundMode() || (_swimmingAllowed && _rig.SwimMode())))
                { Stop("Native get-up did not restore the admitted ground movement boundary."); return true; }

                ClearLifePrediction();
                if (_disposed) return true;
                _lifeCompletedSequence = completed;
                _lifeHeld = false;
                return false;
            }
            catch (Exception error)
            { Stop("Retained native life coordination failed (" + error.Message + ")."); return true; }
        }

        private void ClearLifePrediction()
        {
            DiscardCapturedStep();
            // Keep the monotonic movement/jump counters. This existing epoch reset drops
            // unread state and excludes old ACKs through _groundInputFloor; corrections
            // require a fresh server pose acknowledging a newly captured input sequence.
            ClearPredictionEpoch();
            _runPressSequence = 0;
            _runPressFrame = -1;
            _runButtonObserved = false;
        }
    }
}
