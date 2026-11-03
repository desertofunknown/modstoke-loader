// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatClientState
    {
        private ulong _nextRebreatherSequence = 1;
        private StrictCombatRebreatherRefillRequest _pendingRebreather;

        internal bool HasPendingRebreatherRequest { get; private set; }
        internal StrictCombatRebreatherRefillRequest PendingRebreatherRequest => _pendingRebreather;

        internal bool TryBeginRebreather(StrictCombatRebreatherRefillMode mode, ulong expectedRevision, long nowMs, bool allowsTimedProgress = false)
        {
            if (!TryObserveTime(nowMs) || !HasBinding || HasPendingRequest || !HasSnapshot
                || Snapshot.LifeStage != StrictCombatLifeStage.Alive || Snapshot.LifeGeneration == 0)
                return false;
            var request = new StrictCombatRebreatherRefillRequest(Binding, _nextRebreatherSequence,
                Snapshot.LifeGeneration, expectedRevision, mode, allowsTimedProgress);
            if (!request.IsValid)
                return false;

            _pendingRebreather = request;
            _requestStartedMs = nowMs;
            _lastOutboundMs = 0;
            _attempts = 0;
            HasPendingRebreatherRequest = true;
            HasPendingRequest = true;
            return true;
        }

        internal bool TryTakeRebreatherOutbound(long nowMs, out StrictCombatRebreatherRefillRequest request)
        {
            request = default;
            if (!TryObserveTime(nowMs) || !HasPendingRequest || !HasPendingRebreatherRequest
                || _attempts >= MaximumAttempts
                || (_attempts > 0 && nowMs - _lastOutboundMs < RetryIntervalMs))
                return false;

            // Taking an attempt consumes it even if the transport cannot queue it.
            _attempts++;
            _lastOutboundMs = nowMs;
            request = _pendingRebreather;
            return true;
        }

        internal bool TryAcceptRebreatherResponse(in StrictCombatRebreatherRefillResponse response, long nowMs)
        {
            if (!TryObserveTime(nowMs) || !HasBinding || !HasPendingRequest || !HasPendingRebreatherRequest
                || !response.IsValid || !_pendingRebreather.SameRequest(response.Request))
                return false;

            // This reply does not join health, armour and resources. The client must request
            // ordinary ReadState after completion before projecting the resulting state.
            switch (response.Result)
            {
                case StrictCombatRebreatherRefillResult.Accepted:
                case StrictCombatRebreatherRefillResult.Denied:
                case StrictCombatRebreatherRefillResult.StaleLife:
                case StrictCombatRebreatherRefillResult.StaleRevision:
                case StrictCombatRebreatherRefillResult.Incapacitated:
                case StrictCombatRebreatherRefillResult.Unavailable:
                    ClearPending();
                    if (_nextRebreatherSequence == ulong.MaxValue)
                        Retire();
                    else
                        _nextRebreatherSequence++;
                    return true;
                case StrictCombatRebreatherRefillResult.Conflict:
                case StrictCombatRebreatherRefillResult.StaleSequence:
                case StrictCombatRebreatherRefillResult.OutOfOrder:
                    Retire();
                    return true;
                default:
                    return false;
            }
        }
    }
}
