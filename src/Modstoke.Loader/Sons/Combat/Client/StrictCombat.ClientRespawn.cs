// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatRespawnClientStatus
    {
        Unavailable, Requested, Offered, Placed, Committed, Rejected, Uncertain, Expired, WrongThread, Busy
    }

    public sealed partial class StrictCombatClient
    {
        private StrictCombatRespawnRequest _respawnRequest;
        private StrictCombatRespawnOffer _respawnOffer;
        private StrictCombatRespawnReport _respawnReport;
        private StrictCombatRespawnOutcome _respawnOutcome;
        private StrictCombatRespawnClientStatus _respawnStatus;
        private long _respawnStarted, _respawnLastSent;
        private int _respawnAttempts;
        private bool _placingRespawn;

        /// <summary>The last observed transaction state; UpdateRespawn advances deadlines.</summary>
        public StrictCombatRespawnClientStatus RespawnStatus => _respawnStatus;

        /// <summary>Requests authorization for this dead life. Does not move or revive the player.</summary>
        public bool TryRequestRespawn(out StrictCombatRespawnRequest request)
        {
            request = default;
            if (_state.HasPendingRebreatherRequest || _state.HasPendingRebreatherWearRequest || _placingRespawn || _projectingLife || _projectingHealth || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment || EquipmentBlocksRespawn || !IsRespawnCurrent(out long now)
                || !TryRead(out var binding, out var state)) return false;
            AdvanceRespawnDeadline(now);
            if (_respawnRequest.IsValid)
            {
                if (_respawnStatus != StrictCombatRespawnClientStatus.Committed)
                {
                    if ((_respawnStatus == StrictCombatRespawnClientStatus.Requested
                            || _respawnStatus == StrictCombatRespawnClientStatus.Offered
                            || _respawnStatus == StrictCombatRespawnClientStatus.Placed)
                        && state.LifeGeneration == _respawnRequest.LifeGeneration
                        && state.LifeStage == StrictCombatLifeStage.Dead)
                    { request = _respawnRequest; return true; }
                    return false;
                }
                if (state.LifeGeneration <= _respawnRequest.LifeGeneration) return false;
            }
            if (_nativeLifeBarrier == null || _nativeLifeBarrier.IsRetired || _localLife == null
                || !_localLife.OwnedBy(_nativeLifeBarrier) || !_localLife.CanPlaceRespawn(in binding, in state))
                return false;
            var candidate = new StrictCombatRespawnRequest(binding, Guid.NewGuid(), state.LifeGeneration);
            if (!candidate.IsValid) return false;
            ResetRespawn();
            _respawnRequest = request = candidate;
            _respawnStarted = now;
            _respawnStatus = StrictCombatRespawnClientStatus.Requested;
            return true;
        }

        /// <summary>Accepts an offer or outcome from the exact enrolled server; never performs placement.</summary>
        public bool TryReceiveRespawn(Datagram message)
        {
            if (message == null || !ReferenceEquals(message.Peer, _peer) || _placingRespawn
                || _projectingLife || _projectingHealth || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment || EquipmentBlocksRespawn || !IsRespawnCurrent(out long now)) return false;
            AdvanceRespawnDeadline(now);
            if (StrictCombatRespawnProtocol.TryReadOffer(message.Payload.Span, out var offer))
            {
                if (!_respawnRequest.SameRequest(offer.Request)) return false;
                if (_respawnOffer.IsValid)
                {
                    if (_respawnOffer.SameOffer(in offer)) return true;
                    if (!RespawnFinished()) FailRespawn();
                    return false;
                }
                if (_respawnStatus != StrictCombatRespawnClientStatus.Requested) return false;
                _respawnOffer = offer;
                _respawnStatus = StrictCombatRespawnClientStatus.Offered;
                return true;
            }
            if (!StrictCombatRespawnProtocol.TryReadOutcome(message.Payload.Span, out var outcome)
                || !_respawnRequest.SameRequest(outcome.Request) || !_respawnOffer.IsValid
                || outcome.OperationId != _respawnOffer.OperationId) return false;
            if (_respawnOutcome.IsValid)
            {
                Span<byte> prior = stackalloc byte[StrictCombatRespawnProtocol.OutcomeLength];
                return StrictCombatRespawnProtocol.TryWriteOutcome(in _respawnOutcome, prior, out _)
                    && message.Payload.Span.SequenceEqual(prior);
            }
            if (outcome.Result == StrictCombatRespawnResult.Committed)
            {
                if (_respawnStatus != StrictCombatRespawnClientStatus.Placed
                    || _respawnReport.Placement != StrictCombatRespawnPlacement.Placed
                    || !TryCommitRespawn(in outcome, now))
                {
                    if (!RespawnFinished()) FailRespawn();
                    return false;
                }
                _respawnOutcome = outcome;
                _respawnStatus = StrictCombatRespawnClientStatus.Committed;
                return true;
            }
            if (RespawnFinished() && _respawnStatus != StrictCombatRespawnClientStatus.Rejected
                && _respawnStatus != StrictCombatRespawnClientStatus.Uncertain) return false;
            _respawnOutcome = outcome;
            if (_respawnReport.Placement == StrictCombatRespawnPlacement.Placed
                || outcome.Result == StrictCombatRespawnResult.Uncertain)
                FailRespawn();
            else _respawnStatus = StrictCombatRespawnClientStatus.Rejected;
            return true;
        }

        /// <summary>
        /// Attempts the authorized placement once, retaining inventory. Placed is a local
        /// observation, not server acknowledgement, safe terrain or client-integrity proof.
        /// </summary>
        public StrictCombatRespawnClientStatus ApplyRespawnPlacement()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return StrictCombatRespawnClientStatus.WrongThread;
            if (_placingRespawn || _projectingLife || _projectingHealth || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment || EquipmentBlocksRespawn) return StrictCombatRespawnClientStatus.Busy;
            if (!IsRespawnCurrent(out long now)) return StrictCombatRespawnClientStatus.Unavailable;
            AdvanceRespawnDeadline(now);
            if (_respawnStatus != StrictCombatRespawnClientStatus.Offered) return _respawnStatus;
            // Consume the attempt before entering native code or any of its callbacks.
            _placingRespawn = true;
            _respawnStatus = StrictCombatRespawnClientStatus.Uncertain;
            StrictCombatRespawnPlacement placement = StrictCombatRespawnPlacement.Rejected;
            try
            {
                if (TryRead(out var binding, out var state) && MatchesRespawnBinding(in binding)
                    && state.LifeGeneration == _respawnRequest.LifeGeneration
                    && state.LifeStage == StrictCombatLifeStage.Dead && _localLife != null
                    && _nativeLifeBarrier != null && _localLife.OwnedBy(_nativeLifeBarrier))
                    placement = _localLife.PlaceRespawn(in binding, in state, in _respawnOffer);
            }
            catch (Exception) { placement = StrictCombatRespawnPlacement.Uncertain; }
            finally { _placingRespawn = false; }
            _respawnReport = new StrictCombatRespawnReport(_respawnRequest, _respawnOffer.OperationId, placement);
            _respawnAttempts = 0;
            _respawnLastSent = 0;
            if (placement == StrictCombatRespawnPlacement.Placed) _respawnStatus = StrictCombatRespawnClientStatus.Placed;
            else if (placement == StrictCombatRespawnPlacement.Rejected) _respawnStatus = StrictCombatRespawnClientStatus.Rejected;
            else FailRespawn();
            return _respawnStatus;
        }

        /// <summary>Drives bounded request/report delivery. Never selects a destination or moves the player.</summary>
        public bool UpdateRespawn()
        {
            if (_placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment || EquipmentBlocksRespawn || !IsRespawnCurrent(out long now)) return false;
            AdvanceRespawnDeadline(now);
            if (_respawnOutcome.IsValid || !_respawnRequest.IsValid
                || _respawnStatus == StrictCombatRespawnClientStatus.Expired
                || now - _respawnStarted >= 2L * StrictCombatRespawnProtocol.MaximumLifetimeMs) return true;
            bool report = _respawnReport.IsValid;
            if (_respawnStatus == StrictCombatRespawnClientStatus.Uncertain
                && _respawnReport.Placement == StrictCombatRespawnPlacement.Placed) return true;
            if (!report && _respawnStatus != StrictCombatRespawnClientStatus.Requested) return true;
            if (_respawnAttempts >= StrictCombatRespawnProtocol.MaximumAttempts
                || (_respawnAttempts != 0 && now - _respawnLastSent < StrictCombatRespawnProtocol.RetryIntervalMs)) return true;
            Span<byte> payload = stackalloc byte[StrictCombatRespawnProtocol.ReportLength];
            int length;
            bool encoded = report ? StrictCombatRespawnProtocol.TryWriteReport(in _respawnReport, payload, out length)
                : StrictCombatRespawnProtocol.TryWriteRequest(in _respawnRequest, payload, out length);
            if (!encoded) { FailRespawn(); return false; }
            _respawnAttempts++;
            _respawnLastSent = now;
            Datagrams.Send(_peer, payload.Slice(0, length));
            return true;
        }

        public bool TryReadRespawnOutcome(out StrictCombatRespawnOutcome outcome)
        {
            outcome = default;
            if (!IsRespawnCurrent(out long now)) return false;
            AdvanceRespawnDeadline(now);
            if (!_respawnOutcome.IsValid) return false;
            outcome = _respawnOutcome;
            return true;
        }

        private bool TryCommitRespawn(in StrictCombatRespawnOutcome outcome, long now)
        {
            var barrier = _nativeLifeBarrier;
            try
            {
                if (barrier == null || barrier.IsRetired || _localPlayer == null || _localLife == null
                    || !_localLife.OwnedBy(barrier) || !ReferenceEquals(barrier.Owner, this)
                    || !ReferenceEquals(barrier.Player, _localPlayer)
                    || barrier.LifeGeneration != _respawnRequest.LifeGeneration
                    || !TryRead(out var binding, out var state) || !MatchesRespawnBinding(in binding)
                    || !_localPlayer.Matches(in binding) || !_localPlayer.IsCurrent(out _)
                    || !_localLife.CanCompleteRespawn(in binding, in state)) return false;
                // No native calls between the accepted-state update and barrier handoff.
                if (!_state.TryAcceptRespawnOutcome(in outcome, now)
                    || !barrier.TryAdvanceLife(_respawnRequest.LifeGeneration, outcome.State.LifeGeneration))
                { FailRespawn(); return false; }
                return true;
            }
            catch (Exception) { FailRespawn(); return false; }
        }

        private bool RetainsRespawnContainment(StrictCombatLifeBarrier barrier, in StrictCombatSnapshot state)
        {
            long now = Environment.TickCount64;
            return _respawnStatus == StrictCombatRespawnClientStatus.Placed && _respawnRequest.IsValid
                && _respawnReport.Placement == StrictCombatRespawnPlacement.Placed
                && barrier.LifeGeneration == _respawnRequest.LifeGeneration
                && state.LifeGeneration == _respawnRequest.LifeGeneration + 1
                && MatchesRespawnBinding(_state.Binding) && now >= _respawnStarted
                && now - _respawnStarted < 2L * StrictCombatRespawnProtocol.MaximumLifetimeMs;
        }

        private bool IsRespawnCurrent(out long now)
        {
            if (!IsCurrent(out now)) return false;
            return ClientEnrollment.TryRead(_peer, out var enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, _peer) && enrollment.State == EnrollmentState.Enrolled
                && (enrollment.RequiredCapabilities & StrictCombatRespawnProtocol.RequiredCapabilities)
                    == StrictCombatRespawnProtocol.RequiredCapabilities
                && (enrollment.OfferedCapabilities & StrictCombatRespawnProtocol.RequiredCapabilities)
                    == StrictCombatRespawnProtocol.RequiredCapabilities;
        }

        private bool MatchesRespawnBinding(in StrictCombatBinding binding)
            => _respawnRequest.Binding.SameBinding(in binding);

        private bool RespawnFinished()
            => _respawnStatus == StrictCombatRespawnClientStatus.Committed || _respawnStatus == StrictCombatRespawnClientStatus.Rejected
                || _respawnStatus == StrictCombatRespawnClientStatus.Uncertain || _respawnStatus == StrictCombatRespawnClientStatus.Expired;

        private void AdvanceRespawnDeadline(long now)
        {
            if (!_respawnRequest.IsValid || RespawnFinished()) return;
            if (_respawnStatus == StrictCombatRespawnClientStatus.Placed)
            {
                if (now - _respawnStarted >= 2L * StrictCombatRespawnProtocol.MaximumLifetimeMs) FailRespawn();
            }
            else if (now - _respawnStarted >= StrictCombatRespawnProtocol.MaximumLifetimeMs)
                _respawnStatus = StrictCombatRespawnClientStatus.Expired;
        }

        private void FailRespawn()
        {
            _respawnStatus = StrictCombatRespawnClientStatus.Uncertain;
            _nativeLifeBarrier?.Retire();
        }

        private void ResetRespawn()
        {
            _respawnRequest = default;
            _respawnOffer = default;
            _respawnReport = default;
            _respawnOutcome = default;
            _respawnStatus = StrictCombatRespawnClientStatus.Unavailable;
            _respawnStarted = _respawnLastSent = 0;
            _respawnAttempts = 0;
        }
    }
}
