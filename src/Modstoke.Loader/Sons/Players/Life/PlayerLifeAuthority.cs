// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Settings;
using UnityEngine;
using Environment = System.Environment;

namespace Modstoke.Sons
{
    /// <summary>
    /// Game-thread life authority for one restored remote server carrier. It never calls
    /// Hit, CheckDeath, SetDyingState or HealedMp: those bodies use LocalPlayer. Server
    /// commits retain a separate required source action; no source completion is inferred.
    /// All entry points belong to trusted, serialized server gameplay, not raw client RPCs.
    /// </summary>
    public sealed partial class PlayerLifeAuthority : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly PlayerSurvivalRuntime _survival;
        private PlayerStats _stats;
        private Vitals _vitals;
        private IPlayerState _state;
        private PlayerInventoryAuthority _inventory;
        private readonly List<PlayerLifeTransition> _sourceTransitions = new(2);
        private ulong _transitionSequence, _deathTransaction, _deathLastTransaction, _deathBagId;
        private bool _busy;
        private bool _aliveHitDetection;

        public Guid Migration => _bridge.Plan.Id;
        public ulong ActorNetworkId => _bridge.NetworkId;
        /// <summary>Bridge-scoped initial life. No unqualified respawn/adoption API advances it.</summary>
        public ulong LifeGeneration { get; } = 1;
        public ulong TransitionSequence => _transitionSequence;
        public PlayerLifePhase Phase { get; private set; }
        public PlayerLifeTransition PendingTransition { get; private set; }
        public PlayerStatDamageReceipt? LastStatDamage { get; private set; }
        public bool HasPendingSourceAction => _sourceTransitions.Count != 0;
        public int PendingSourceTransitionCount => _sourceTransitions.Count;
        public bool BlocksActorActions => IsDisposed || FailureReason != null
            || Phase != PlayerLifePhase.Alive || HasPendingSourceAction || _deathTransaction != 0
            || _sourceRoute?.HasPendingForcedTransition == true;
        public bool IsDisposed { get; private set; }
        public string FailureReason { get; private set; }
        internal bool HitDetection { get; private set; }

        private PlayerLifeAuthority(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerSurvivalRuntime survival)
        { _bridge = bridge; _carrier = carrier; _survival = survival; }

        /// <summary>
        /// The aggregate supplies its authenticated, server-approved PLF1 observation paired
        /// with the already approved survival payload. A digest match is correlation, not authentication.
        /// No approval delegate, native lifecycle or source presentation runs here.
        /// </summary>
        public static bool TryPrepare(PlayerBridge bridge, PlayerCarrier carrier,
            PlayerSurvivalRuntime survival, PlayerLifeAdmission approvedAdmission,
            out PlayerLifeAuthority owner, out string detail)
        {
            owner = null;
            detail = "An exact restored remote server carrier, survival owner and approved life/survival pair are required.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true || bridge.IsDisposed
                || bridge.Peer.ServerSession == null || carrier == null || carrier.IsDisposed
                || carrier.IsPhysicsActive || survival == null || survival.IsActive || survival.IsDisposed
                || approvedAdmission == null || Time.inFixedTimeStep) return false;
            PlayerLifeAuthority candidate = null;
            try
            {
                if (approvedAdmission.SourceNetworkId != bridge.Plan.OldNetworkId
                    || !approvedAdmission.Matches(carrier.RestoredSurvival)
                    || !carrier.TryReadRestoredServerStats(out var stats)
                    || !bridge.TryReadCurrentTarget(out var target) || target.Pointer != carrier.Entity.Pointer
                    || !InitialAlive(stats)) return false;
                candidate = new(bridge, carrier, survival);
                if (!survival.TryBindLifeAuthority(candidate, bridge, carrier, out candidate._stats, out candidate._inventory))
                    return false;
                candidate._vitals = stats._vitals;
                candidate._state = stats._playerStateCache;
                candidate.HitDetection = approvedAdmission.HitDetection;
                candidate._aliveHitDetection = approvedAdmission.HitDetection;
                candidate.RequireCurrent();
                bridge.Retired += candidate.Dispose;
                owner = candidate;
                detail = "Server life owner prepared. Source life actions and subsequent hit-detection mode changes remain separate ownership.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Life preparation refused: " + error.Message; return false; }
        }

        /// <summary>
        /// Pump after survival advances, before numeric publication. Commits one due tick
        /// exactly once, or consumes it under the captured HitDetection immunity. Health
        /// changes from other owners must enter at this same serialized life boundary.
        /// </summary>
        public bool TrySettleSurvivalBoundary(out string detail)
        {
            detail = "An active, idle exact life/survival owner is required.";
            if (!Ready()) return false;
            _busy = true;
            try
            {
                RequireCurrent();
                if (_survival.Progress.StatDamageDue > 0)
                {
                    if (!_survival.TryCommitOwnedStatDamage(this, out var receipt, out detail)) return false;
                    LastStatDamage = receipt;
                }
                if (Phase != PlayerLifePhase.Alive)
                {
                    if (PendingTransition == null || !MatchesCurrentLifeState())
                        return Fault("Actor life state changed outside its retained transition.", out detail);
                    return _survival.TryAcknowledgeLifeTransition(this, PendingTransition, out detail);
                }
                RequireCurrent();
                if (_stats._healing || _stats._isHealing)
                    return Fault("An unowned healing continuation cannot be adopted by the life owner.", out detail);
                if (_vitals._hasDied || _stats._playerIsDowned || _stats._dyingState != PlayerStats.DyingState.Alive
                    || _stats._respawning || _stats._doFinalDeath || _state.Dying != 0)
                    return Fault("An unowned native life transition cannot be adopted as a server commit.", out detail);
                if (_vitals._health._currentValue > Mathf.Epsilon)
                { detail = "Exact stat damage settled; the actor remains alive."; return true; }
                // CheckDeath 183114C48..CD3: GodMode gates the death latch, not ApplyDamage.
                if (Cheats.GodModeEnabled)
                {
                    return _survival.TryAcknowledgeGodModeDeathBypass(this, out detail);
                }
                _ = checked((uint)(_transitionSequence + 1));
                if (!TrySettleDowningDrop(out detail)) return false;
                _vitals._hasDied = true;
                _stats._playerIsDowned = true;
                _stats._dyingState = PlayerStats.DyingState.Dying;
                _state.Dying = (int)PlayerStats.DyingState.Dying;
                _state.IsInMidAction = true;
                HitDetection = false;
                Phase = PlayerLifePhase.Downed;
                Commit(PlayerLifeSourceAction.StartDeath, null, null);
                if (!_sourceRoute.TryReleaseForcedDrop(PlayerHeldOnlyAuthority.MutationKind.KnockDownDrop,
                    _transitionSequence, out detail))
                    return Fault("Committed downing lost its forced-drop release: " + detail, out detail);
                return _survival.TryAcknowledgeLifeTransition(this, PendingTransition, out detail);
            }
            catch (Exception error) { return Fault("Life/stat damage commit is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        /// <summary>
        /// Consume the existing OPEN server-created PlayerHealed request, with exact expected
        /// generation/transition. Reject() claims native dispatch before any write. Peer
        /// requests need the separate reviver/reach/custody authority and are not admitted here.
        /// This commits native revive numbers but does NOT claim source get-up completion.
        /// </summary>
        public bool TryApplyServerReviveRequest(PlayerReviveRequest request,
            ulong lifeGeneration, ulong downedTransition, out string detail)
        {
            detail = "An open exact server-created revive request for this retained downed transition is required.";
            if (!Ready() || request == null || request.IsRejected || _deathTransaction != 0
                || lifeGeneration != LifeGeneration || downedTransition != _transitionSequence
                || !BurningPromptSettled
                || Phase != PlayerLifePhase.Downed
                || request.SourceConnectionId.HasValue || request.SourceSession != null
                || request.RequestedTargetNetworkId != ActorNetworkId
                || request.RoutedConnectionId != _bridge.Peer.ServerSession.ConnectionId) return false;
            _busy = true;
            try
            {
                RequireCurrent();
                if (!MatchesCurrentLifeState() || _survival.Progress.StatDamageDue > 0) return false;
                if (!_survival.TryDrainBeforeLifeChange(this, out detail)) return false;
                float requested = (float)_vitals._lowHealthThreshold + 1f;
                float health = Mathf.Clamp(requested, _vitals._health._min, _vitals._health._max);
                float target = MathF.Min(requested, _vitals._health._max - _vitals._sickness._currentValue);
                if (!float.IsFinite(health) || !float.IsFinite(target) || health <= Mathf.Epsilon
                    || target <= Mathf.Epsilon) return false;
                _ = checked((uint)(_transitionSequence + 1));
                if (!request.Reject()) return false;
                // HealedMp 18311BDD5..BE0F performs two SetHealth calls. Only the
                // final low-threshold+1 storage survives; neither call invokes death.
                _vitals._health._currentValue = health;
                _vitals._targetHealth = target;
                _vitals._hasDied = false;
                _stats._playerDeathCount = 0;
                _stats._dyingState = PlayerStats.DyingState.Alive;
                _state.Dying = (int)PlayerStats.DyingState.Alive;
                _stats._playerIsDowned = false;
                _stats._healing = true;
                _state.IsInMidAction = true;
                Phase = PlayerLifePhase.Reviving;
                Commit(PlayerLifeSourceAction.ResetFromHeal, request, null);
                return _survival.TryAcknowledgeLifeTransition(this, PendingTransition, out detail);
            }
            catch (Exception error) { return Fault("Server revive commit is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        /// <summary>
        /// Trusted final-death decision after the actor's timer/input/action authority.
        /// Uses the existing exact inventory transfer, not an approval callback. A null
        /// bag explicitly authorizes permanent deletion and must never be a missing-bag fallback.
        /// </summary>
        public bool TryBeginFinalDeath(ulong lifeGeneration, ulong downedTransition, ulong transaction,
            InventoryDeathBag bag, out string detail)
        {
            detail = "Final death needs this exact downed life and settled source item custody.";
            if (!Ready() || _deathTransaction != 0 || lifeGeneration != LifeGeneration
                || !BurningPromptSettled
                || downedTransition != _transitionSequence || Phase != PlayerLifePhase.Downed) return false;
            _busy = true;
            try
            {
                RequireCurrent();
                if (!MatchesCurrentLifeState()) return false;
                if (!_survival.TryDrainBeforeLifeChange(this, out detail)) return false;
                if (!_inventory.TryBeginDeathRemoval(transaction, LifeGeneration, bag, out detail)) return false;
                _deathTransaction = transaction;
                _deathLastTransaction = _inventory.LastTransaction;
                if (!_inventory.TryReadDeathReceipt(transaction, out var reserved)
                    || reserved.Restoring || reserved.Life != LifeGeneration
                    || reserved.LastTransaction != _deathLastTransaction)
                    return Fault("Reserved death custody lost its exact receipt.", out detail);
                _deathBagId = reserved.BagNetworkId;
                return true;
            }
            catch (Exception error) { return Fault("Death custody reservation is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        /// <summary>Poll after the existing inventory pump. Reserved/removing is never completion.</summary>
        public bool TryCompleteFinalDeath(out string detail)
        {
            detail = "The exact applied inventory death receipt is required; source respawn remains separate.";
            if (_busy || IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _deathTransaction == 0 || Time.inFixedTimeStep) return false;
            _busy = true;
            try
            {
                RequireCurrent();
                if (!_inventory.TryReadDeathReceipt(_deathTransaction, out var receipt)) return false;
                if (receipt.State == InventoryDeathState.Unconfirmed)
                    return Fault(receipt.Detail, out detail);
                if (receipt.State != InventoryDeathState.Applied) return false;
                if (!_survival.LifeMutationReady(this) || receipt.Life != LifeGeneration || receipt.Restoring
                    || receipt.BagNetworkId != _deathBagId || receipt.Completed != receipt.Total
                    || receipt.LastTransaction != _deathLastTransaction || _inventory.LastTransaction != _deathLastTransaction
                    || Phase != PlayerLifePhase.Downed || !MatchesCurrentLifeState())
                    return Fault("Applied death custody no longer matches its actor/life/transition.", out detail);
                if (!_survival.TryDrainBeforeLifeChange(this, out detail)) return false;
                _ = checked((uint)(_transitionSequence + 1));
                _stats._dyingState = PlayerStats.DyingState.Dead;
                _state.Dying = (int)PlayerStats.DyingState.Dead;
                _stats._playerIsDowned = false;
                Phase = PlayerLifePhase.Dead;
                Commit(PlayerLifeSourceAction.Respawn, null, receipt);
                _deathTransaction = 0;
                return _survival.TryAcknowledgeLifeTransition(this, PendingTransition, out detail);
            }
            catch (Exception error) { return Fault("Final death commit is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        private bool MatchesCurrentLifeState()
            => ReferenceEquals(_survival.AcknowledgedLifeTransition, PendingTransition)
                ? _survival.MatchesNumericLifeState(this) : MatchesCommittedTransition(PendingTransition);

        internal bool MatchesCommittedTransition(PlayerLifeTransition transition)
        {
            RequireCurrent();
            if (transition == null || !ReferenceEquals(transition, PendingTransition)
                || !ReferenceEquals(transition.Owner, this) || transition.LifeGeneration != LifeGeneration
                || transition.Sequence != _transitionSequence || transition.Phase != Phase
                || transition.Health != _vitals._health._currentValue || transition.TargetHealth != _vitals._targetHealth
                || _stats._respawning || _stats._doFinalDeath || _stats._isHealing || !_state.IsInMidAction) return false;
            return Phase switch
            {
                PlayerLifePhase.Downed => _vitals._hasDied && _stats._playerIsDowned
                    && !_stats._healing && _stats._dyingState == PlayerStats.DyingState.Dying && _state.Dying == 1,
                PlayerLifePhase.Reviving => !_vitals._hasDied && !_stats._playerIsDowned
                    && _stats._healing && _stats._dyingState == PlayerStats.DyingState.Alive && _state.Dying == 0,
                PlayerLifePhase.Dead => _vitals._hasDied && !_stats._playerIsDowned
                    && !_stats._healing && _stats._dyingState == PlayerStats.DyingState.Dead && _state.Dying == 2,
                _ => false
            };
        }

        private void Commit(PlayerLifeSourceAction action, PlayerReviveRequest revive,
            InventoryDeathReceipt? death)
        {
            PendingTransition = new(this, ++_transitionSequence, _survival.Progress.Revision, Phase,
                action, _vitals._health._currentValue, _vitals._targetHealth, revive, death);
            if (!MatchesCommittedTransition(PendingTransition))
                throw new InvalidOperationException("Server life mutation did not retain its exact actor readback.");
            if (action == PlayerLifeSourceAction.StartDeath && _stats._isOnFire)
                _survival.BeginLifeBurning(this, PendingTransition);
            _sourceTransitions.Add(PendingTransition);
        }

        /// <summary>
        /// Ordered retained actions, including an earlier downing before a later revive/death.
        /// Reading or publishing them never acknowledges source execution. No dequeue API is
        /// exposed until a concrete source callback owner can provide exact completion evidence.
        /// </summary>
        public PlayerLifeTransition[] GetPendingSourceTransitions()
        {
            RequireCurrent();
            return _sourceTransitions.ToArray();
        }

        private bool Ready()
        {
            if (_busy || IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread) return false;
            try { return _survival.LifeMutationReady(this); }
            catch (Exception error) { return Fault("Life ownership check failed: " + error.Message, out _); }
        }

        private void RequireCurrent()
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || !BoltNetwork.isServer || !_survival.LifeOwnerCurrent(this)
                || !_bridge.TryReadCurrentTarget(out var target) || target.Pointer != _carrier.Entity.Pointer
                || !_carrier.TryReadRestoredServerStats(out var stats) || stats.Pointer != _stats.Pointer
                || _stats._vitals?.Pointer != _vitals.Pointer || _stats._playerStateCache?.Pointer != _state.Pointer
                || target.GetState<IPlayerState>()?.Pointer != _state.Pointer
                || _inventory.IsDisposed || _inventory.IsFaulted)
                throw new InvalidOperationException("The exact server actor life ownership ended.");
            if (!float.IsFinite(_vitals._health._currentValue) || !float.IsFinite(_vitals._targetHealth)
                || !float.IsFinite(_vitals._health._min) || !float.IsFinite(_vitals._health._max)
                || _vitals._health._max <= _vitals._health._min || !float.IsFinite(_vitals._sickness._currentValue))
                throw new InvalidOperationException("Actor life storage is nonfinite or has invalid health bounds.");
        }

        private static bool InitialAlive(PlayerStats stats)
            => stats != null && !stats.WasCollected && stats._vitals != null && !stats._vitals.WasCollected
                && stats._vitals._health._currentValue > Mathf.Epsilon && !stats._vitals._hasDied
                && !stats._playerIsDowned && stats._dyingState == PlayerStats.DyingState.Alive
                && !stats._respawning && !stats._doFinalDeath && !stats._healing && !stats._isHealing
                && stats._playerStateCache != null && !stats._playerStateCache.WasCollected
                && stats._playerStateCache.Dying == 0;

        private bool Fault(string reason, out string detail)
        { FailureReason ??= reason; detail = FailureReason; return false; }

        public void Dispose()
        {
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread) return;
            IsDisposed = true;
            _bridge.Retired -= Dispose;
            // Never restore a native lifecycle or refund unresolved item custody on retirement.
        }
    }
}
