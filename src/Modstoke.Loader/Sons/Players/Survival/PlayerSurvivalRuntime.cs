// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Environment;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Settings;
using global::Sons.StatSystem;
using UnityEngine;
using Environment = System.Environment;

namespace Modstoke.Sons
{
    /// <summary>
    /// Prepared actor-local survival clock. Pump from the aggregate's rendered update; there
    /// is no second update loop, native Update, host effect dispatch or second restore.
    /// Native sleep callbacks change only this actor's private rate profiles.
    /// Keep all other stat/action writers serialized with this owner and its intake endpoint.
    /// </summary>
    public sealed partial class PlayerSurvivalRuntime : IDisposable
    {
        private const int MaximumRetainedFrames = 4096;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly PlayerAuthorityDynamics _dynamics;
        private readonly PlayerInventoryAuthority _authority;
        private readonly PlayerSurvivalSnapshot _snapshot;
        private readonly PlayerSurvivalSettings _settings;
        private readonly PlayerStats _stats;
        private readonly Vitals _vitals;
        private readonly StatsManager _manager;
        private readonly Stat[] _values;
        private readonly IntPtr[] _registeredStatOrder;
        private readonly Func<PlayerSurvivalEnvironment> _readEnvironment;
        private readonly Func<bool> _externalMutationPending;
        private readonly Queue<Frame> _frames = new();
        private InventoryConsumption _consumption;
        private int _observedFrame;
        private bool _busy, _activationAttempted, _advancingCooking, _godModeDeathCheckSuppressed;
        private float _smellAge, _damageBefore, _damageTargetBefore;

        public bool IsActive { get; private set; }
        public bool IsDisposed { get; private set; }
        public string FailureReason { get; private set; }
        public PlayerSurvivalProgress Progress { get; private set; }
        public int PendingFrames => _frames.Count;
        public bool HasLifeBoundary => Progress.RequiresLifeTransition || Progress.StatDamageDue > 0;
        public bool MustDeferStatWriters => !IsActive || IsDisposed || FailureReason != null || _busy
            || _frames.Count != 0 || HasLifeBoundary || CustodyPending;

        private bool CustodyPending => _authority.HasNativeConsumptionReservation
            || _consumption?.HasPendingAcknowledgement == true || _authority.HasPendingNativePowerProjection
            || _authority.HasPendingNativePowerSupply || _authority.HasNativeDeathTransfer;

        private readonly record struct Frame(PlayerSurvivalEnvironment Environment,
            float GameSeconds, float ScaledSeconds, float ScaledTime, int ColdPenalty, int RegenerationPenalty,
            int DamageSetting, int CurrentView, bool Creative, bool InfiniteEnergy, bool GodMode, SleepRates Rates,
            PlayerLifePhase LifePhase, ulong LifeSequence);

        private PlayerSurvivalRuntime(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerAuthorityDynamics dynamics,
            PlayerInventoryAuthority authority, PlayerSurvivalSettings settings,
            PlayerStats stats, Func<PlayerSurvivalEnvironment> readEnvironment,
            Func<bool> externalMutationPending)
        {
            _bridge = bridge; _carrier = carrier; _dynamics = dynamics; _authority = authority;
            _snapshot = carrier.RestoredSurvival;
            _settings = settings; _stats = stats; _vitals = stats._vitals;
            _readEnvironment = readEnvironment; _externalMutationPending = externalMutationPending;
            _values = new Stat[] { _vitals._health, _vitals._sickness, _vitals._stamina, _vitals._vitality,
                _vitals._hydration, _vitals._fullness, _vitals._rested, _vitals._strength, _vitals._temperature };
            _manager = _vitals._statsManager;
            var registered = _manager?._stats;
            if (registered == null || registered.WasCollected || registered.Count != _values.Length)
                throw new InvalidOperationException("The registered survival stats are unavailable.");
            _registeredStatOrder = new IntPtr[_values.Length];
            for (int i = 0; i < _values.Length; i++)
            {
                if (_values[i] == null || _values[i].WasCollected || registered[i] == null || registered[i].WasCollected)
                    throw new InvalidOperationException("An exact survival stat is unavailable.");
                int count = 0;
                for (int j = 0; j < registered.Count; j++)
                {
                    var stat = registered[j];
                    if (stat != null && !stat.WasCollected && stat.Pointer == _values[i].Pointer) count++;
                }
                if (count != 1) throw new InvalidOperationException("The survival stat registration is not exact.");
                _registeredStatOrder[i] = registered[i].Pointer;
            }
            _smellAge = _snapshot.ReadScalar(PlayerSurvivalScalar.SkunkSprayAge);
        }

        public static bool TryPrepare(PlayerBridge bridge, PlayerCarrier carrier,
            PlayerAuthorityDynamics dynamics, PlayerInventoryAuthority authority,
            Func<PlayerSurvivalSnapshot, PlayerSurvivalSettings, bool> approveSettings,
            Func<PlayerSurvivalEnvironment> readEnvironment, Func<bool> externalMutationPending,
            out PlayerSurvivalRuntime runtime, out string detail)
        {
            runtime = null;
            detail = "An exact restored carrier, native settings approval and actor-local environment reader are required.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true || bridge.IsDisposed
                || carrier == null || carrier.IsDisposed || carrier.IsPhysicsActive || dynamics == null
                || dynamics.IsDisposed || authority == null || authority.IsDisposed || authority.IsFaulted
                || approveSettings == null || readEnvironment == null || externalMutationPending == null
                || Time.inFixedTimeStep || !carrier.IsSurvivalRestored
                || carrier.RestoredSurvival?.Settings == null) return false;
            PlayerSurvivalRuntime candidate = null;
            try
            {
                var snapshot = carrier.RestoredSurvival;
                var settings = snapshot.Settings;
                int frame = Time.frameCount;
                if (!carrier.TryReadRestoredServerStats(out var stats)
                    || !snapshot.MatchesRestored(stats) || !approveSettings(snapshot, settings)
                    || frame != Time.frameCount || !ReferenceEquals(carrier.RestoredSurvival, snapshot)
                    || !snapshot.MatchesRestored(stats)) return false;
                candidate = new(bridge, carrier, dynamics, authority, settings, stats, readEnvironment, externalMutationPending);
                candidate.ValidateNumerics();
                candidate.PrepareSleepProfiles();
                if (!dynamics.TryBindSurvival(candidate, carrier, bridge)) { candidate.Dispose(); return false; }
                candidate.RequireCurrent();
                bridge.Retired += candidate.Dispose;
                runtime = candidate;
                detail = "Actor survival prepared without restoration, elapsed time, physics or native effects.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Survival preparation refused: " + error.Message; return false; }
        }

        /// <summary>
        /// Activate once after aggregate/bridge activation. The exact consumption owner binds
        /// this clock exclusively; its ordinary public per-frame clock cannot run in parallel.
        /// </summary>
        public bool TryActivate(InventoryConsumption consumption,
            out string detail)
        {
            detail = "Survival activation requires the exact ready aggregate and consumption endpoint.";
            if (Environment.CurrentManagedThreadId != _thread || IsDisposed || _activationAttempted
                || consumption == null || Time.inFixedTimeStep) return false;
            _activationAttempted = true;
            try
            {
                RequireCurrent();
                if (!_bridge.IsGameplayActive || !_authority.ConsumptionCurrent(consumption)
                    || consumption.IsDisposed || consumption.IsFaulted || consumption.HasPendingAcknowledgement
                    || CustodyPending || _externalMutationPending()
                    || !_snapshot.MatchesRestored(_stats)) return Fault("The survival activation baseline is no longer idle/current.", out detail);
                RequireCurrent();
                if (!consumption.TryBindSurvivalCooking(this, out detail))
                    return Fault(detail ?? "The exact consumption owner refused the survival clock.", out detail);
                _consumption = consumption;
                ActivateSleepProfiles();
                _observedFrame = Time.frameCount;
                IsActive = true;
                Progress = ReadProgress(0, _observedFrame, -1, 0);
                detail = "One actor survival clock activated; no preparation time or second restore applied.";
                return true;
            }
            catch (Exception error) { return Fault("Survival activation failed: " + error.Message, out detail); }
        }

        internal bool OwnsCookingActor(PlayerBridge bridge,
            PlayerInventoryAuthority authority, PlayerStats stats)
            => Environment.CurrentManagedThreadId == _thread && !IsDisposed && FailureReason == null
                && ReferenceEquals(_bridge, bridge) && ReferenceEquals(_authority, authority)
                && stats != null && !stats.WasCollected && !_stats.WasCollected && stats.Pointer == _stats.Pointer;

        /// <summary>
        /// Call every rendered frame even while custody is pending. Retained frames preserve
        /// frame-sensitive native temperature changes. Intake linearizes before frames retained
        /// during its reservation; drain older frames before admitting any new mutation.
        /// </summary>
        public bool TryAdvance(out PlayerSurvivalProgress progress, out string detail)
        {
            progress = Progress;
            detail = "The active actor survival clock must run once per rendered frame.";
            if (Environment.CurrentManagedThreadId != _thread || !IsActive || IsDisposed || _busy
                || FailureReason != null || Time.inFixedTimeStep) return false;
            _busy = true;
            try
            {
                RequireCurrent();
                if (!_bridge.IsGameplayActive) throw new InvalidOperationException("Gameplay ended; retire the clock instead of rebasing it.");
                if (_consumption.IsDisposed || _consumption.IsFaulted) throw new InvalidOperationException("Consumption ownership ended.");
                int frame = Time.frameCount;
                if (_observedFrame != frame)
                {
                    if (unchecked(_observedFrame + 1) != frame)
                        throw new InvalidOperationException("A rendered survival frame was missed; its game-time delta cannot be reconstructed.");
                    if (_frames.Count == MaximumRetainedFrames)
                        throw new InvalidOperationException("Survival custody backlog is full; no elapsed frame was discarded.");
                    var environment = _readEnvironment();
                    RequireCurrent();
                    var state = _stats._playerStateCache;
                    if (state == null || state.WasCollected)
                        throw new InvalidOperationException("The exact actor view is unavailable.");
                    float game = (float)TimeOfDayHolder.GetTimeOfDayDelta(), scaled = Time.deltaTime, scaledTime = Time.time;
                    if (Time.frameCount != frame || environment.Frame != frame || !ValidEnvironment(environment)
                        || !FiniteNonnegative(game) || !FiniteNonnegative(scaled) || !FiniteNonnegative(scaledTime)
                        || Cheats.Setup == null || Cheats.Overrides == null)
                        throw new InvalidOperationException("The exact server environment, clock or native cheat settings are unavailable.");
                    _frames.Enqueue(new(environment, game, scaled, scaledTime,
                        GameSetupManager.GetColdPenaltiesSetting()._intValue,
                        GameSetupManager.GetStatRegenerationPenaltySetting()._intValue,
                        GameSetupManager.GetPlayerStatsDamageSetting()._intValue,
                        state.CurrentView,
                        GameSetupManager.GetCreativeModeSetting(),
                        Cheats.Setup._InfiniteEnergy_k__BackingField || Cheats.Overrides._InfiniteEnergy_k__BackingField,
                        Cheats.GodModeEnabled, CaptureSleepRates(),
                        _lifeAuthority?.Phase ?? PlayerLifePhase.Alive,
                        _lifeAuthority?.TransitionSequence ?? 0));
                    _observedFrame = frame;
                }
                if (LifeNumbersBlocked || CustodyPending || _externalMutationPending())
                { detail = "Survival frames retained behind exact custody/life ownership; no stat writes."; return true; }
                RequireCurrent();
                // A scheduling bound, not a simulation timestep: remaining frames stay queued.
                for (int i = 0; i < 64 && _frames.Count != 0; i++)
                {
                    if (LifeNumbersBlocked || CustodyPending || _externalMutationPending()) break;
                    RequireCurrent();
                    var next = _frames.Peek();
                    if (next.LifePhase != PlayerLifePhase.Alive
                        && (_lifeAuthority == null || next.LifePhase != _lifeAuthority.Phase
                            || next.LifeSequence != _lifeAuthority.TransitionSequence))
                        throw new InvalidOperationException("A life change crossed retained downed/get-up frames without draining them.");
                    AdvanceFrame(next);
                    _frames.Dequeue();
                    if (LifeNumbersBlocked) break;
                }
                progress = Progress;
                detail = LifeNumbersBlocked ? "Survival reached an explicit damage/life boundary."
                    : _frames.Count != 0 ? "Survival catch-up retained; new stat mutations must wait."
                    : "Actor survival advanced; source presentation delivery remains aggregate-owned.";
                return true;
            }
            catch (Exception error)
            { return Fault("Survival progression failed; never replay a partial frame: " + error.Message, out detail); }
            finally { _busy = false; progress = Progress; }
        }

        /// <summary>
        /// Before a revive/final-death mutation, drain frames against the OLD life state.
        /// Call before accepting the request or touching life/health storage. False does
        /// not accept, reject, retain or replay a native request. A due damage tick must
        /// settle first; native synchronous request lifetimes remain the caller's boundary.
        /// </summary>
        internal bool TryDrainBeforeLifeChange(PlayerLifeAuthority owner, out string detail)
        {
            detail = "The exact life owner must drain the old life before changing its numbers.";
            if (!LifeMutationReady(owner) || Progress.StatDamageDue > 0) return false;
            if (!TryAdvance(out _, out detail)) return false;
            return _observedFrame == Time.frameCount && _frames.Count == 0 && Progress.StatDamageDue == 0
                && !CustodyPending && !_externalMutationPending();
        }

        // Only the existing cooking endpoint can use its raw numeric path here. This is
        // NOT intake admission: native Vitals.Update runs cooking before GodMode recovery
        // and also while downed, without re-enabling eating or other accepted mutations.
        internal bool OwnsCookingNumericStep(InventoryConsumption consumption)
            => _busy && _advancingCooking && IsActive && ReferenceEquals(_consumption, consumption)
                && OwnsCookingActor(_bridge, _authority, _stats) && !CustodyPending;

        /// <summary>Native CheckDeath's real GodMode branch, after exact damage settlement.</summary>
        internal bool TryAcknowledgeGodModeDeathBypass(PlayerLifeAuthority owner, out string detail)
        {
            detail = "GodMode recovery requires the exact settled alive life owner and native cheat state.";
            if (!LifeMutationReady(owner) || owner.Phase != PlayerLifePhase.Alive
                || HasNativeLifeLatch() || Progress.StatDamageDue != 0 || !Progress.RequiresLifeTransition
                || _vitals._health._currentValue > Mathf.Epsilon || !Cheats.GodModeEnabled) return false;
            _godModeDeathCheckSuppressed = true;
            detail = "Native GodMode suppressed CheckDeath; the next ordered Vitals step may run without a life latch.";
            return true;
        }

        private bool LifeNumbersBlocked
        {
            get
            {
                if (_lifeAuthority != null && (_lifeAuthority.IsDisposed || _lifeAuthority.FailureReason != null)) return true;
                if (Progress.StatDamageDue > 0) return true;
                if (!RequiresLife()) return false;
                if (!HasNativeLifeLatch())
                {
                    // Do not re-evaluate a historical cheat setting while replaying frames.
                    // Only the exact life owner can acknowledge the native death-check branch.
                    return !_godModeDeathCheckSuppressed;
                }
                var transition = AcknowledgedLifeTransition;
                if (_lifeAuthority == null || _lifeAuthority.IsDisposed || _lifeAuthority.FailureReason != null
                    || transition == null || !ReferenceEquals(transition.Owner, _lifeAuthority)
                    || transition.Sequence != _lifeAuthority.TransitionSequence
                    || transition.LifeGeneration != _lifeAuthority.LifeGeneration) return true;
                return !MatchesNumericLifeState(_lifeAuthority);
            }
        }

        // Transition receipts pin the values AT the commit, not every later native tick.
        // The parent life owner uses this after acknowledging that exact commit, while
        // retaining immutable transition values for ordered source action delivery.
        internal bool MatchesNumericLifeState(PlayerLifeAuthority owner)
            => Environment.CurrentManagedThreadId == _thread && IsActive && !IsDisposed
                && FailureReason == null && _bridge.IsGameplayActive
                && ReferenceEquals(owner, _lifeAuthority) && owner != null && !owner.IsDisposed
                && owner.FailureReason == null && ReferenceEquals(AcknowledgedLifeTransition, owner.PendingTransition)
                && AcknowledgedLifeTransition != null
                && AcknowledgedLifeTransition.Sequence == owner.TransitionSequence
                && AcknowledgedLifeTransition.LifeGeneration == owner.LifeGeneration
                && (AcknowledgedLifeTransition.Phase == owner.Phase
                    || (owner.Phase == PlayerLifePhase.Alive
                        && AcknowledgedLifeTransition.Phase == PlayerLifePhase.Reviving
                        && AcknowledgedLifeTransition.SourceAction == PlayerLifeSourceAction.ResetFromHeal
                        && owner.SourceCompletedSequence == AcknowledgedLifeTransition.Sequence))
                && !_stats._respawning && !_stats._doFinalDeath && !_stats._isHealing
                && _stats._playerStateCache != null && !_stats._playerStateCache.WasCollected
                && (owner.Phase switch
            {
                PlayerLifePhase.Alive => !_vitals._hasDied && !_stats._playerIsDowned
                    && !_stats._healing && _stats._dyingState == PlayerStats.DyingState.Alive
                    && _stats._playerStateCache.Dying == 0 && !_stats._playerStateCache.IsInMidAction,
                PlayerLifePhase.Downed => _vitals._hasDied && _stats._playerIsDowned
                    && !_stats._healing && _stats._dyingState == PlayerStats.DyingState.Dying
                    && _stats._playerStateCache.Dying == 1 && _stats._playerStateCache.IsInMidAction,
                PlayerLifePhase.Reviving => !_vitals._hasDied && !_stats._playerIsDowned
                    && _stats._dyingState == PlayerStats.DyingState.Alive && _stats._healing
                    && _stats._playerStateCache.Dying == 0 && _stats._playerStateCache.IsInMidAction,
                PlayerLifePhase.Dead => _vitals._hasDied && !_stats._playerIsDowned
                    && !_stats._healing && _stats._dyingState == PlayerStats.DyingState.Dead
                    && _stats._playerStateCache.Dying == 2 && _stats._playerStateCache.IsInMidAction,
                _ => false
            });

        /// <summary>
        /// The life/damage owner has resolved this exact due tick. Applied means its numeric
        /// damage and target clamp are already committed; false means independently authorized
        /// native damage immunity, not a retry/drop. This does not call CheckDeath or revive.
        /// </summary>
        public bool TryCompleteStatDamage(ulong revision, bool applied, out string detail)
        {
            detail = "The exact pending stat damage and verified life-owner result are required.";
            if (!IsActive || IsDisposed || _busy || Environment.CurrentManagedThreadId != _thread
                || revision != Progress.Revision || Progress.StatDamageDue <= 0 || CustodyPending) return false;
            try
            {
                RequireCurrent();
                float expected = applied ? Clamp(_vitals._health, _damageBefore - Progress.StatDamageDue) : _damageBefore;
                float target = applied ? MathF.Min(expected, _vitals._health._max - _vitals._sickness._currentValue) : _damageTargetBefore;
                if (_vitals._health._currentValue != expected || _vitals._targetHealth != target) return false;
                _stats._timeUntilNextStatDamageTick = _stats._gameSecondsBetweenStatDamageTicks;
                _godModeDeathCheckSuppressed = false;
                Progress = ReadProgress(Progress.Revision, Progress.SourceFrame, Progress.ExpiredCookingRecipeId, 0);
                detail = "The exact damage tick settled; a zero-health actor still requires a life transition.";
                return true;
            }
            catch (Exception error) { return Fault("Stat damage completion lost ownership: " + error.Message, out detail); }
        }

        private void RequireCurrent()
        {
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread || !GameBuild.IsRecognized
                || _authority.IsDisposed || _authority.IsFaulted || _bridge.IsDisposed
                || !_bridge.TryReadCurrentTarget(out var entity) || entity.Pointer != _carrier.Entity.Pointer
                || !_carrier.TryReadRestoredServerStats(out var stats) || stats.Pointer != _stats.Pointer
                || !ReferenceEquals(_carrier.RestoredSurvival, _snapshot)
                || !_dynamics.SurvivalCurrent(this) || _stats.enabled || _vitals.enabled
                || _stats._vitals.Pointer != _vitals.Pointer
                || Combat.StrictCombatLifeHooks.HasConsumptionHealthOwner(_vitals))
                throw new InvalidOperationException("The exact survival actor/aggregate binding is no longer current.");
            RequireSmellReceiver();
            var manager = _vitals._statsManager;
            var registered = manager?._stats;
            if (manager == null || manager.WasCollected || manager.Pointer != _manager.Pointer
                || registered == null || registered.WasCollected || registered.Count != _values.Length)
                throw new InvalidOperationException("The registered survival stats changed.");
            for (int i = 0; i < _values.Length; i++)
            {
                var stat = registered[i];
                if (_values[i].WasCollected || stat == null || stat.WasCollected || stat.Pointer != _registeredStatOrder[i])
                    throw new InvalidOperationException("An exact survival stat was replaced.");
            }
            RequireSleepBinding();
        }

        private bool Fault(string reason, out string detail)
        {
            FailureReason ??= reason;
            IsActive = false;
            detail = FailureReason;
            // Retain the failed frame and actor state for the aggregate; no refund or restart.
            return false;
        }

        public void Dispose()
        {
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread) return;
            IsDisposed = true; IsActive = false;
            _bridge.Retired -= Dispose;
            ReleaseSleepProfiles();
            _dynamics.ReleaseSurvival(this);
            _consumption = null;
            _smellReceiver = null;
            _frames.Clear();
        }

        private static bool FiniteNonnegative(float value) => float.IsFinite(value) && value >= 0;
        private static bool ValidEnvironment(PlayerSurvivalEnvironment e)
            => float.IsFinite(e.StatAmbientTemperature) && float.IsFinite(e.BodyAmbientTemperature)
                && float.IsFinite(e.ExternalWetness) && e.ExternalWetness >= 0 && e.ExternalWetness <= 1
                && float.IsFinite(e.TemperatureVolumeTemperature) && e.HeldOnlyItemCount >= 0;
    }
}
