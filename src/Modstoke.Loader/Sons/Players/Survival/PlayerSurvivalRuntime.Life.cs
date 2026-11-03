// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Settings;
using UnityEngine;
using Environment = System.Environment;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalRuntime
    {
        private PlayerLifeAuthority _lifeAuthority;
        internal bool BlocksPlayerLifeActions => _lifeAuthority?.BlocksActorActions == true;
        internal ulong LifeIntakeTransaction => _consumption?.LastCommittedReceipt.Transaction ?? 0;
        private ulong _completedSourceLifeSequence;
        private int _lifeBurnFrame = -1;
        private float _lifeBurnFrameTime;

        // The numeric frame owner supplies the ORIGINAL admitted Time.time, alongside
        // its retained delta. False leaves the existing non-life burning step in charge.
        internal bool TryAdvanceLifeBurning(float scaledSeconds, float gameTime, int sourceFrame)
        {
            if (!_busy || !IsActive || IsDisposed || FailureReason != null || !float.IsFinite(gameTime)
                || gameTime < 0 || !float.IsFinite(scaledSeconds) || scaledSeconds < 0)
                throw new InvalidOperationException("Life burning requires the exact executing survival frame.");
            _lifeBurnFrame = sourceFrame; _lifeBurnFrameTime = gameTime;
            return _lifeAuthority?.AdvanceBurningDeath(scaledSeconds, gameTime, sourceFrame,
                checked(Progress.Revision + 1)) == true;
        }

        internal void BeginLifeBurning(PlayerLifeAuthority owner, PlayerLifeTransition transition)
        {
            if (!LifeMutationReady(owner) || _lifeBurnFrame != Progress.SourceFrame || _lifeBurnFrame < 0)
                throw new InvalidOperationException("Burning death has no retained native survival frame time.");
            owner.BeginBurningDeath(transition, _lifeBurnFrameTime, _lifeBurnFrame);
        }

        internal PlayerLifeBoundaryResult ReadLifePublicationBoundary(PlayerLifeAuthority owner, out string detail)
        {
            detail = FailureReason ?? "Survival has not reached this render frame's settled life boundary.";
            if (IsDisposed || FailureReason != null || !ReferenceEquals(_lifeAuthority, owner)
                || owner == null || owner.IsDisposed || owner.FailureReason != null
                || Environment.CurrentManagedThreadId != _thread)
                return PlayerLifeBoundaryResult.Faulted;
            RequireCurrent();
            if (_consumption == null || _consumption.IsDisposed || _consumption.IsFaulted
                || !_authority.ConsumptionCurrent(_consumption))
            {
                detail = "Life publication lost its exact consumption owner.";
                return PlayerLifeBoundaryResult.Faulted;
            }
            if (!LifeMutationReady(owner) || _externalMutationPending() || _observedFrame != Time.frameCount
                || _frames.Count != 0 || Progress.SourceFrame != Time.frameCount || LifeNumbersBlocked)
                return PlayerLifeBoundaryResult.Pending;
            detail = "Current-frame numeric life storage and all earlier accepted survival frames are settled.";
            return PlayerLifeBoundaryResult.Settled;
        }

        internal bool TryCompleteSourceLife(PlayerLifeAuthority owner, ulong sequence, out string detail)
        {
            detail = "Source completion requires the exact acknowledged life and drained numeric boundary.";
            if (!LifeMutationReady(owner) || sequence == 0 || owner.SourceCompletedSequence != sequence
                || owner.Phase != PlayerLifePhase.Alive || !MatchesNumericLifeState(owner)
                || _observedFrame != Time.frameCount || _frames.Count != 0 || Progress.StatDamageDue != 0) return false;
            if (_completedSourceLifeSequence == sequence) return true;
            if (_completedSourceLifeSequence > sequence) return false;
            Progress = ReadProgress(checked(Progress.Revision + 1), Progress.SourceFrame, Progress.ExpiredCookingRecipeId, 0);
            _completedSourceLifeSequence = sequence;
            detail = "Actual get-up completion committed once at the drained survival boundary.";
            return true;
        }
        public PlayerLifeTransition AcknowledgedLifeTransition { get; private set; }

        internal bool TryBindLifeAuthority(PlayerLifeAuthority owner,
            PlayerBridge bridge, PlayerCarrier carrier,
            out PlayerStats stats, out PlayerInventoryAuthority inventory)
        {
            stats = null; inventory = null;
            if (owner == null || _lifeAuthority != null || _activationAttempted || IsDisposed
                || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || !ReferenceEquals(bridge, _bridge) || !ReferenceEquals(carrier, _carrier)) return false;
            RequireCurrent();
            if (!_snapshot.MatchesRestored(_stats)) return false;
            _lifeAuthority = owner; stats = _stats; inventory = _authority;
            return true;
        }

        internal bool LifeOwnerCurrent(PlayerLifeAuthority owner)
        {
            if (!ReferenceEquals(_lifeAuthority, owner) || IsDisposed || FailureReason != null
                || Environment.CurrentManagedThreadId != _thread) return false;
            RequireCurrent();
            return true;
        }

        internal bool LifeMutationReady(PlayerLifeAuthority owner)
            => LifeOwnerCurrent(owner) && IsActive && _bridge.IsGameplayActive && !_busy
                && !CustodyPending && !_authority.HasNativeCraftReservation && !Time.inFixedTimeStep
                && _consumption != null && !_consumption.IsDisposed && !_consumption.IsFaulted
                && _authority.ConsumptionCurrent(_consumption);

        internal bool TryCommitOwnedStatDamage(PlayerLifeAuthority owner,
            out PlayerStatDamageReceipt receipt, out string detail)
        {
            receipt = default;
            detail = "The bound life owner must settle the exact pending survival tick.";
            if (!LifeMutationReady(owner) || Progress.StatDamageDue <= 0) return false;
            var pending = Progress;
            if (_vitals._health._currentValue != _damageBefore || _vitals._targetHealth != _damageTargetBefore)
                return Fault("Health changed before its retained stat-damage tick settled.", out detail);
            bool apply = owner.HitDetection;
            float health = apply ? Clamp(_vitals._health, _damageBefore - pending.StatDamageDue) : _damageBefore;
            float target = apply ? MathF.Min(health, _vitals._health._max - _vitals._sickness._currentValue)
                : _damageTargetBefore;
            if (!float.IsFinite(health) || !float.IsFinite(target))
                return Fault("Stat damage produced nonfinite native health storage.", out detail);
            // Keep the exact damage tick retained and both actors alive until native
            // drop, world, equipment and final source receipts have all settled.
            if (apply && health <= Mathf.Epsilon && (!Cheats.GodModeEnabled || owner.HasPendingDowningDrop)
                && owner.Phase == PlayerLifePhase.Alive
                && !owner.TrySettleDowningDrop(out detail)) return false;
            // UpdateStatsDamage 183111D20..D77 bypasses Hit/armour. GodMode is NOT
            // immunity here; CheckDeath evaluates it after the numeric subtraction.
            if (apply)
            {
                _vitals._health._currentValue = health;
                _vitals._targetHealth = target;
            }
            if (!TryCompleteStatDamage(pending.Revision, apply, out detail))
                return Fault("The committed stat-damage readback was not acknowledged; do not replay it.", out detail);
            receipt = new(owner.LifeGeneration, pending.Revision, pending.StatDamageDue, _damageBefore,
                health, apply ? PlayerStatDamageResult.Applied
                    : PlayerStatDamageResult.HitDetectionImmune);
            return true;
        }

        /// <summary>
        /// Acknowledge this owner's actual server commit by retained identity and native
        /// readback, not a caller-supplied success bit. A downed/dead boundary stays held.
        /// Revive numeric acknowledgement does not release source controls or retained frames;
        /// the aggregate must continue holding on SourcePresentationPending.
        /// </summary>
        public bool TryAcknowledgeLifeTransition(PlayerLifeAuthority owner,
            PlayerLifeTransition transition, out string detail)
        {
            detail = "An exact committed life transition and unchanged actor readback are required.";
            try
            {
                if (owner == null || transition == null || !LifeMutationReady(owner)
                    || Progress.StatDamageDue > 0) return false;
                if (ReferenceEquals(AcknowledgedLifeTransition, transition))
                {
                    if (!MatchesNumericLifeState(owner)) return false;
                    detail = "This exact server commit was acknowledged; later numeric ticks retain the same life owner.";
                    return true;
                }
                if (!owner.MatchesCommittedTransition(transition)) return false;
                if (transition.SurvivalRevision != Progress.Revision) return false;
                Progress = ReadProgress(checked(Progress.Revision + 1), Progress.SourceFrame,
                    Progress.ExpiredCookingRecipeId, 0);
                AcknowledgedLifeTransition = transition;
                detail = Progress.RequiresLifeTransition
                    ? "Server life commit acknowledged; downed/dead simulation and source presentation remain held."
                    : "Server revive numbers acknowledged; source get-up/control completion remains pending.";
                return true;
            }
            catch (Exception error) { return Fault("Life acknowledgement lost its exact owner: " + error.Message, out detail); }
        }
    }
}
