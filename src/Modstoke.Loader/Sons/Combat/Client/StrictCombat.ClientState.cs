// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    // One synchronization lifetime for one exact transport peer.
    internal sealed partial class StrictCombatClientState
    {
        private const long RequestTimeoutMs = 8000;
        private const long RetryIntervalMs = 500;
        private const int MaximumAttempts = 8;
        private long _lastNowMs;
        private long _requestStartedMs;
        private long _lastOutboundMs;
        private int _attempts;
        private ulong _nextSequence = 1;
        private StrictCombatIntent _pending;

        internal bool IsRetired { get; private set; }
        internal bool HasBinding { get; private set; }
        internal bool HasPendingRequest { get; private set; }
        internal bool HasSnapshot { get; private set; }
        internal StrictCombatBinding Binding { get; private set; }
        internal StrictCombatSnapshot Snapshot { get; private set; }
        internal StrictCombatIntent PendingIntent => _pending;

        internal bool Tick(long nowMs) => TryObserveTime(nowMs);

        internal bool TryAcceptBinding(in StrictCombatBinding binding, long nowMs)
        {
            bool valid = StrictCombatProtocol.IsValidBinding(in binding);
            if (!TryObserveTime(nowMs) || !valid)
                return false;

            if (HasBinding)
            {
                if (binding.Generation < Binding.Generation)
                    return false;
                if (binding.Generation == Binding.Generation)
                {
                    if (binding.SameBinding(Binding))
                        return true;
                    Retire();
                    return false;
                }
                if (binding.Epoch == Binding.Epoch)
                {
                    Retire();
                    return false;
                }
            }

            ClearPending();
            _nextSequence = 1;
            _nextRebreatherSequence = 1;
            _nextRebreatherWearSequence = 1;
            Snapshot = default;
            HasSnapshot = false;
            Binding = binding;
            HasBinding = true;
            return true;
        }

        internal bool TryBegin(StrictCombatAction action, long nowMs,
            float directionX = 0, float directionY = 0, float directionZ = 0)
        {
            if (!TryObserveTime(nowMs) || !HasBinding || HasPendingRequest)
                return false;
            if (action != StrictCombatAction.ReadState && (!HasSnapshot || Snapshot.LifeGeneration == 0))
                return false;
            ulong life = action == StrictCombatAction.ReadState ? 0 : Snapshot.LifeGeneration;
            var intent = new StrictCombatIntent(Binding.Epoch, _nextSequence, life, action,
                directionX, directionY, directionZ);
            if (!intent.IsValid)
                return false;

            _pending = intent;
            _requestStartedMs = nowMs;
            _lastOutboundMs = 0;
            _attempts = 0;
            HasPendingRequest = true;
            return true;
        }

        internal bool TryTakeOutbound(long nowMs, out StrictCombatIntent intent)
        {
            intent = default;
            if (!TryObserveTime(nowMs) || !HasPendingRequest || HasPendingRebreatherRequest || HasPendingRebreatherWearRequest || _attempts >= MaximumAttempts
                || (_attempts > 0 && nowMs - _lastOutboundMs < RetryIntervalMs))
                return false;

            // Taking an attempt consumes it even if the transport cannot queue it.
            _attempts++;
            _lastOutboundMs = nowMs;
            intent = _pending;
            return true;
        }

        internal bool TryAcceptReceipt(in StrictCombatReceipt receipt, long nowMs)
        {
            if (!TryObserveTime(nowMs) || !HasBinding || !HasPendingRequest || HasPendingRebreatherRequest || HasPendingRebreatherWearRequest
                || receipt.Epoch != Binding.Epoch || receipt.Sequence != _pending.Sequence
                || receipt.Action != _pending.Action || receipt.RequestLifeGeneration != _pending.LifeGeneration
                || !StrictCombatProtocol.IsValidReceipt(in receipt))
                return false;

            StrictCombatSnapshot state = receipt.State;
            if (!HasSnapshot || state.Revision > Snapshot.Revision)
            {
                if (HasSnapshot
                    && (state.LifeGeneration < Snapshot.LifeGeneration
                        || (Snapshot.LifeStage != StrictCombatLifeStage.Uninitialized
                            && state.LifeStage == StrictCombatLifeStage.Uninitialized)
                        || (state.LifeGeneration == Snapshot.LifeGeneration
                            && Snapshot.LifeStage == StrictCombatLifeStage.Dead
                            && state.LifeStage != StrictCombatLifeStage.Dead)))
                {
                    Retire();
                    return false;
                }
                Snapshot = state;
                HasSnapshot = true;
            }
            else if (state.Revision == Snapshot.Revision)
            {
                if (state.OwnsPistol != Snapshot.OwnsPistol || state.Equipped != Snapshot.Equipped
                    || state.Magazine != Snapshot.Magazine || state.Reserve != Snapshot.Reserve
                    || state.ReloadRounds != Snapshot.ReloadRounds
                    || state.LifeStage != Snapshot.LifeStage || state.LifeGeneration != Snapshot.LifeGeneration
                    || BitConverter.SingleToInt32Bits(state.Health) != BitConverter.SingleToInt32Bits(Snapshot.Health)
                    || BitConverter.SingleToInt32Bits(state.MaximumHealth) != BitConverter.SingleToInt32Bits(Snapshot.MaximumHealth))
                {
                    Retire();
                    return true;
                }
                MergeSnapshotTimers(in state);
            }

            switch (receipt.Result)
            {
                case StrictCombatResult.ShotPending:
                    return true;
                case StrictCombatResult.Accepted:
                case StrictCombatResult.NoWeapon:
                case StrictCombatResult.NotEquipped:
                case StrictCombatResult.EmptyMagazine:
                case StrictCombatResult.AlreadyReloading:
                case StrictCombatResult.MagazineFull:
                case StrictCombatResult.NoReserve:
                case StrictCombatResult.Cooldown:
                case StrictCombatResult.EffectFailed:
                case StrictCombatResult.Incapacitated:
                case StrictCombatResult.StaleLife:
                case StrictCombatResult.NotReady:
                    ClearPending();
                    if (_nextSequence == ulong.MaxValue)
                        Retire();
                    else
                        _nextSequence++;
                    return true;
                case StrictCombatResult.InvalidIntent:
                case StrictCombatResult.OutOfOrder:
                case StrictCombatResult.Conflict:
                case StrictCombatResult.StaleSequence:
                case StrictCombatResult.Busy:
                case StrictCombatResult.Retired:
                    Retire();
                    return true;
                default:
                    return false;
            }
        }

        // A respawn outcome is a separate transaction. It must not complete or retarget
        // the ordinary intent that may still be waiting for its own receipt.
        internal bool TryAcceptRespawnOutcome(in StrictCombatRespawnOutcome outcome, long nowMs)
        {
            if (!TryObserveTime(nowMs) || !HasBinding || !HasSnapshot || HasPendingRebreatherRequest || HasPendingRebreatherWearRequest || !outcome.IsValid
                || outcome.Result != StrictCombatRespawnResult.Committed
                || !outcome.Request.Binding.SameBinding(Binding))
                return false;

            var state = outcome.State;
            if (Snapshot.LifeGeneration == outcome.Request.LifeGeneration
                && Snapshot.LifeStage == StrictCombatLifeStage.Dead && state.Revision > Snapshot.Revision)
            {
                Snapshot = state;
                return true;
            }
            // ReadState can observe the committed life before this outcome arrives.
            // Preserve its later damage, including another downing or death.
            if (Snapshot.LifeGeneration != state.LifeGeneration || Snapshot.Revision < state.Revision)
                return false;
            if (Snapshot.Revision > state.Revision) return true;
            if (state.OwnsPistol != Snapshot.OwnsPistol || state.Equipped != Snapshot.Equipped
                || state.Magazine != Snapshot.Magazine || state.Reserve != Snapshot.Reserve
                || state.ReloadRounds != Snapshot.ReloadRounds || state.LifeStage != Snapshot.LifeStage
                || BitConverter.SingleToInt32Bits(state.Health) != BitConverter.SingleToInt32Bits(Snapshot.Health)
                || BitConverter.SingleToInt32Bits(state.MaximumHealth) != BitConverter.SingleToInt32Bits(Snapshot.MaximumHealth))
                return false;
            MergeSnapshotTimers(in state);
            return true;
        }

        // Equal revisions may only shorten the observed countdowns.
        private void MergeSnapshotTimers(in StrictCombatSnapshot state)
        {
            Snapshot = new StrictCombatSnapshot(state.Revision, state.OwnsPistol, state.Equipped,
                state.Magazine, state.Reserve, state.ReloadRounds,
                Math.Min(state.ReloadRemainingMs, Snapshot.ReloadRemainingMs),
                Math.Min(state.FireRemainingMs, Snapshot.FireRemainingMs),
                state.LifeStage, state.LifeGeneration, state.Health, state.MaximumHealth,
                Math.Min(state.DownedRemainingMs, Snapshot.DownedRemainingMs));
        }

        internal void Retire()
        {
            IsRetired = true;
            ClearPending();
        }

        private bool TryObserveTime(long nowMs)
        {
            if (IsRetired || nowMs < 0 || nowMs < _lastNowMs)
                return false;
            _lastNowMs = nowMs;
            if (HasPendingRequest && nowMs - _requestStartedMs >= RequestTimeoutMs)
            {
                Retire();
                return false;
            }
            return true;
        }

        private void ClearPending()
        {
            HasPendingRequest = false;
            HasPendingRebreatherRequest = false;
            HasPendingRebreatherWearRequest = false;
            _pending = default;
            _pendingRebreather = default;
            _pendingRebreatherWear = default;
            _requestStartedMs = 0;
            _lastOutboundMs = 0;
            _attempts = 0;
        }
    }
}
