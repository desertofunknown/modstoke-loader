// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatClientState
    {
        private ulong _nextRebreatherWearSequence = 1;
        private StrictCombatRebreatherWearRequest _pendingRebreatherWear;

        internal bool HasPendingRebreatherWearRequest { get; private set; }
        internal StrictCombatRebreatherWearRequest PendingRebreatherWearRequest => _pendingRebreatherWear;

        internal bool TryBeginRebreatherWear(bool isWorn, ulong expectedRevision, long nowMs, bool allowsTimedProgress = false)
        {
            if (!TryObserveTime(nowMs) || !HasBinding || HasPendingRequest || !HasSnapshot
                || Snapshot.LifeStage != StrictCombatLifeStage.Alive || Snapshot.LifeGeneration == 0)
                return false;
            var request = new StrictCombatRebreatherWearRequest(Binding, _nextRebreatherWearSequence,
                Snapshot.LifeGeneration, expectedRevision, isWorn, allowsTimedProgress);
            if (!request.IsValid)
                return false;

            _pendingRebreatherWear = request;
            _requestStartedMs = nowMs;
            _lastOutboundMs = 0;
            _attempts = 0;
            HasPendingRebreatherWearRequest = true;
            HasPendingRequest = true;
            return true;
        }

        internal bool TryTakeRebreatherWearOutbound(long nowMs, out StrictCombatRebreatherWearRequest request)
        {
            request = default;
            if (!TryObserveTime(nowMs) || !HasPendingRequest || !HasPendingRebreatherWearRequest
                || _attempts >= MaximumAttempts
                || (_attempts > 0 && nowMs - _lastOutboundMs < RetryIntervalMs))
                return false;

            // Taking an attempt consumes it even if the transport cannot queue it.
            _attempts++;
            _lastOutboundMs = nowMs;
            request = _pendingRebreatherWear;
            return true;
        }

        internal bool TryAcceptRebreatherWearResponse(in StrictCombatRebreatherWearResponse response, long nowMs)
        {
            if (!TryObserveTime(nowMs) || !HasBinding || !HasPendingRequest || !HasPendingRebreatherWearRequest
                || !response.IsValid || !_pendingRebreatherWear.SameRequest(response.Request))
                return false;

            // This reply does not join health, armour and resources. The client must request
            // ordinary ReadState after completion before projecting the resulting state.
            switch (response.Result)
            {
                case StrictCombatRebreatherWearResult.Accepted:
                case StrictCombatRebreatherWearResult.Denied:
                case StrictCombatRebreatherWearResult.StaleLife:
                case StrictCombatRebreatherWearResult.StaleRevision:
                case StrictCombatRebreatherWearResult.Incapacitated:
                case StrictCombatRebreatherWearResult.Unavailable:
                case StrictCombatRebreatherWearResult.Unchanged:
                    ClearPending();
                    if (_nextRebreatherWearSequence == ulong.MaxValue)
                        Retire();
                    else
                        _nextRebreatherWearSequence++;
                    return true;
                case StrictCombatRebreatherWearResult.Conflict:
                case StrictCombatRebreatherWearResult.StaleSequence:
                case StrictCombatRebreatherWearResult.OutOfOrder:
                    Retire();
                    return true;
                default:
                    return false;
            }
        }
    }
}
