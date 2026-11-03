// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Physics;
using UnityEngine;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerGroundMovement
    {
        private PlayerWaterContactAuthority.ServerContinuation _waterContacts;
        private PlayerLifeAuthority _waterLife;
        private PlayerLifeRoute _waterLifeRoute;
        private PlayerWaterPresentation _waterPublisher;
        private PlayerWaterTransition _swimmingEntry;
        private ulong _swimmingOrdinal;
        private bool _swimmingCommitAttempted, _swimmingCommitted, _swimmingReleased;

        internal bool HasPendingSwimmingEntry => _swimmingEntry != null;

        internal bool TryBindSwimmingTransitions(PlayerBridge bridge,
            PlayerLifeAuthority life, PlayerLifeRoute route,
            PlayerWaterPresentation publisher, out string detail)
        {
            detail = "Bind swimming to the same prepared motor, life route and native water publisher.";
            if (Environment.CurrentManagedThreadId != _thread || !IsPrepared || Time.inFixedTimeStep
                || !ReferenceEquals(bridge, _bridge) || _bridge.IsGameplayActive || _waterLife != null
                || _waterContacts == null || _movementModes == null || life == null || life.IsDisposed
                || life.FailureReason != null || life.Migration != _bridge.Plan.Id
                || life.ActorNetworkId != _bridge.NetworkId || route == null || route.IsDisposed
                || route.Source != null || route.FailureReason != null || publisher == null
                || !route.MatchesWaterOwner(bridge, life)) return false;
            if (!publisher.TryBindServerSwimming(bridge, this, _waterContacts, life, route, out detail)) return false;
            _waterLife = life; _waterLifeRoute = route; _waterPublisher = publisher;
            return _movementModes.TryBindSwimmingTransitionOwner(this, _bridge, _waterContacts, out detail);
        }

        // Called by the actual mode resolver before its first swimming latch. A
        // pending edge survives later dry observations, life custody and backpressure.
        internal BoltMigrationAdmission SettleSwimmingEntry(
            PlayerMovementModes modes, PlayerWaterContactSample sample,
            Vector3 position, bool grounded, bool jumping, out PlayerWaterTransition edge, out string detail)
        {
            edge = _swimmingEntry;
            detail = "The prepared life/water transition owners are required before accepting swimming.";
            if (!ReferenceEquals(modes, _movementModes) || _waterLife == null || _waterPublisher == null
                || !Current() || !Time.inFixedTimeStep || !_insideMovementStep
                || _swimAwaitingPhysics || _swimmingCommitAttempted)
                return BoltMigrationAdmission.Rejected;
            if (_waterLife.IsDisposed || _waterLife.FailureReason != null || _waterLifeRoute.IsDisposed
                || _waterLifeRoute.FailureReason != null || _waterPublisher.IsDisposed || _waterPublisher.FailureReason != null)
            {
                detail = _waterLife.FailureReason ?? _waterLifeRoute.FailureReason
                    ?? _waterPublisher.FailureReason ?? "A swimming transition owner retired.";
                return BoltMigrationAdmission.Rejected;
            }
            if (_swimmingEntry == null)
            {
                if (!_waterPublisher.CanRetainSwimmingCommit)
                    return BoltMigrationAdmission.Pending;
                if (_swimmingOrdinal == ulong.MaxValue || !sample.InWater
                    || !_waterContacts.IsCurrentSample(sample) || sample.ActorNetworkId != _bridge.NetworkId
                    || !_transform.position.Equals(position)) return BoltMigrationAdmission.Rejected;
                _swimmingEntry = new(new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    _waterLife.LifeGeneration, ++_swimmingOrdinal), sample, position, grounded, jumping, Time.fixedTimeAsDouble);
                edge = _swimmingEntry;
            }
            if (edge.Key.LifeGeneration != _waterLife.LifeGeneration)
            {
                detail = "The accepted swimming edge belongs to an earlier life generation; retain it without a new drop.";
                return BoltMigrationAdmission.Rejected;
            }
            if (!_waterLife.TrySettleForcedHeldDrop(Held.MutationKind.SwimmingDrop, edge.Key.Ordinal, out detail))
                return _waterLife.FailureReason != null || _waterLifeRoute.FailureReason != null
                    ? BoltMigrationAdmission.Rejected : BoltMigrationAdmission.Pending;
            return BoltMigrationAdmission.Ready;
        }

        // The resolver calls this instead of publishing a mode-only promise. The
        // motor body is switched in the same stack, after both settlement receipts.
        internal bool CommitSwimmingEntry(PlayerMovementModes modes,
            PlayerWaterTransition edge, Action commitMode, out string detail)
        {
            detail = "Commit the exact settled edge once on its owning motor stack.";
            if (!ReferenceEquals(modes, _movementModes) || !ReferenceEquals(edge, _swimmingEntry)
                || edge == null || commitMode == null || _swimmingCommitAttempted
                || edge.Key.LifeGeneration != _waterLife.LifeGeneration
                || _waterPublisher.IsDisposed || _waterPublisher.FailureReason != null
                || !Current() || !Time.inFixedTimeStep || !_insideMovementStep
                || !_waterLife.TrySettleForcedHeldDrop(Held.MutationKind.SwimmingDrop, edge.Key.Ordinal, out detail)) return false;
            // A partial mode/body write is retained and faulted, never retried.
            _swimmingCommitAttempted = true;
            commitMode();
            if (!_swimActive)
            {
                if (_nativeGroundEnabled) _swimActive = true;
                else EnterSwimBody(edge.Grounded);
            }
            if (!Current() || !_swimActive || modes.Mode != PlayerMovementMode.Swim)
                throw new InvalidOperationException("The exact swimming mode/body commit did not read back.");
            _swimmingCommitted = true;
            if (!_waterPublisher.RetainSwimmingCommit(this, edge, out detail)) return false;
            _swimmingReleased = _waterLife.TryReleaseSwimmingDrop(edge.Key.Ordinal, out detail);
            return true; // Release is retryable; the owning commit is not.
        }

        internal bool PumpSwimmingEntryRelease(out string detail)
        {
            detail = "No committed swimming entry is awaiting release.";
            if (_swimmingEntry == null || !_swimmingCommitted) return true;
            if (_waterLife.IsDisposed || _waterLifeRoute.IsDisposed || _waterPublisher.IsDisposed || _waterLife.FailureReason != null
                || _waterLifeRoute.FailureReason != null || _waterPublisher.FailureReason != null
                || _swimmingEntry.Key.LifeGeneration != _waterLife.LifeGeneration)
            { detail = _waterLife.FailureReason ?? _waterLifeRoute.FailureReason ?? _waterPublisher.FailureReason
                ?? "Swimming life owner retired or changed generation."; return false; }
            if (!_swimmingReleased)
                _swimmingReleased = _waterLife.TryReleaseSwimmingDrop(_swimmingEntry.Key.Ordinal, out detail);
            if (_swimmingReleased && !_waterLifeRoute.HasPendingForcedTransition
                && _waterPublisher.IsSwimmingCommitApplied(_swimmingEntry.Key))
            {
                _swimmingEntry = null;
                _swimmingCommitAttempted = _swimmingCommitted = _swimmingReleased = false;
            }
            return true;
        }

        /// <summary>
        /// Prepares the existing motor with this actor's held native sensor. The aggregate
        /// owns the sensor lifetime and activates it and carrier physics at one game-loop
        /// boundary, then calls TryActivateWaterObservation before allowing any movement step.
        /// </summary>
        public static bool TryPrepare(PlayerBridge bridge,
            PlayerCarrier carrier,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            Func<bool> supportedModesAllowed,
            Func<PlayerNativeGroundDynamics> readGroundDynamics,
            Func<PlayerSwimDynamics> readSwimDynamics, PlayerStats serverStats,
            PlayerStaminaProfile staminaProfile,
            Func<PlayerStaminaConditions> readStaminaConditions,
            out PlayerGroundMovement movement, out string detail,
            StepHelper stepHelper = null, Func<bool> stepHelperAllowed = null)
        {
            movement = null;
            detail = "Bind the same held native water continuation restored on this carrier.";
            if (waterContacts == null || !ReferenceEquals(waterContacts.Carrier, carrier)
                || waterContacts.IsActive || waterContacts.FailureReason != null) return false;
            return TryPrepare(bridge, carrier, default(PlayerMovementModeSettings),
                supportedModesAllowed, readGroundDynamics, readSwimDynamics, serverStats,
                staminaProfile, readStaminaConditions, out movement, out detail,
                stepHelper, stepHelperAllowed, waterContacts);
        }

        private bool TryCreateWaterModes(bool preparing,
            out PlayerMovementModes modes, out string detail)
        {
            if (_waterContacts == null)
                return PlayerMovementModes.TryCreate(_bridge, _waterSettings, out modes, out detail);
            modes = null;
            detail = "Movement and native water must retain the same carrier owner.";
            if (!ReferenceEquals(_waterContacts.Carrier, _nativeCarrier)) return false;
            bool created = preparing
                ? PlayerMovementModes.TryPrepare(_bridge, _waterContacts, out modes, out detail)
                : PlayerMovementModes.TryCreate(_bridge, _waterContacts, out modes, out detail);
            if (!created || _waterLife == null) return created;
            if (HasPendingSwimmingEntry)
            { detail = "Retain the accepted swimming edge before replacing its water-mode owner."; modes.Dispose(); return false; }
            if (modes.TryBindSwimmingTransitionOwner(this, _bridge, _waterContacts, out detail)) return true;
            modes.Dispose();
            return false;
        }

        /// <summary>
        /// Completes native water admission AFTER motor and sensor activation, before the
        /// first physics step. A missing observation is failure, never a dry fallback.
        /// Failure holds this motor; the aggregate must also retain/retire its sensor owner.
        /// </summary>
        public bool TryActivateWaterObservation(out string detail)
        {
            detail = "Both the prepared motor and its exact native sensor must be active at the same boundary.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || !_activated
                || _insideMovementStep || Time.inFixedTimeStep || _waterContacts == null
                || _movementModes == null || !_waterContacts.IsActive) return false;
            if (!Current()) return false;
            if (!_movementModes.TryActivateWaterObservation(out detail))
            { Stop(detail); return false; }
            return true;
        }

        internal bool TryReadSurvivalEnvironmentWater(PlayerBridge bridge,
            PlayerCarrier carrier,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            out bool underwater, out bool swimming, out string detail)
        {
            underwater = swimming = false;
            detail = "The exact active motor must have settled its native water movement step.";
            if (Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || !ReferenceEquals(bridge, _bridge) || !ReferenceEquals(carrier, _nativeCarrier)
                || !ReferenceEquals(waterContacts, _waterContacts) || !IsActive || StopReason != null
                || _insideMovementStep || _swimAwaitingPhysics || HasPendingSwimmingEntry || _movementModes == null
                || _movementModes.IsDisposed) return false;
            if (_nativeModeSuspended || _sledAuthority != null)
            { detail = "The selected native mode has no admitted PAC underwater continuation on this motor."; return false; }
            if (!Current() || !BorrowedCarrierCurrent(true)
                || !_movementModes.TryReadNativeWaterState(waterContacts, out bool observedUnderwater,
                    out bool observedSwimming, out detail)) return false;
            if (observedSwimming != _swimActive)
            { detail = "The water-mode selection has not settled on the actor's active movement body."; return false; }
            underwater = observedUnderwater;
            swimming = observedSwimming;
            return true;
        }
    }
}
