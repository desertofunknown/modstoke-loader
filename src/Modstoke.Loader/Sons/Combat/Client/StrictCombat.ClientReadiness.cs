// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatReadinessClientStatus
    {
        Unavailable, Pending, Accepted, Rejected, Expired, Uncertain
    }

    public sealed partial class StrictCombatClient
    {
        private readonly bool _requiresReadiness;
        private StrictCombatReadinessClientStatus _readinessStatus;
        private StrictCombatReadinessRequest _readinessRequest;
        private StrictCombatReadinessResponse _readinessResponse;
        private bool _hasReadinessResponse;
        private long _readinessStarted;
        private long _readinessObserved;
        private long _readinessLastSend;
        private int _readinessSends;

        public bool ReadinessRequired => _requiresReadiness;

        public StrictCombatReadinessClientStatus ReadinessStatus
        {
            get
            {
                if (!ObserveReadiness(out _, out _, out var state))
                    return _readinessStatus == StrictCombatReadinessClientStatus.Rejected
                        || _readinessStatus == StrictCombatReadinessClientStatus.Expired
                        || _readinessStatus == StrictCombatReadinessClientStatus.Uncertain
                        ? _readinessStatus : StrictCombatReadinessClientStatus.Unavailable;
                // An old acceptance never describes the newly observed life.
                return _readinessStatus == StrictCombatReadinessClientStatus.Accepted
                    && state.LifeGeneration != _readinessRequest.LifeGeneration
                    ? StrictCombatReadinessClientStatus.Unavailable : _readinessStatus;
            }
        }

        /// <summary>The life claimed by the last request, or zero before a request exists.</summary>
        public ulong ReadinessLifeGeneration => _readinessRequest.LifeGeneration;

        /// <summary>
        /// The mode owner calls this only after its native controls and health, armour,
        /// resources and ammunition projections are ready. This claims synchronization;
        /// neither the claim nor its acknowledgement proves client integrity.
        /// </summary>
        public bool TryBeginReadiness()
        {
            if (!ObserveReadiness(out long now, out var binding, out var state)
                || state.LifeStage != StrictCombatLifeStage.Alive || state.Revision == 0
                || !state.OwnsPistol || !state.Equipped || !HasReadinessNativeOwner(in state))
                return false;
            if (_readinessRequest.IsValid)
            {
                if (state.LifeGeneration == _readinessRequest.LifeGeneration)
                    return _readinessStatus == StrictCombatReadinessClientStatus.Pending
                        || _readinessStatus == StrictCombatReadinessClientStatus.Accepted;
                if (_readinessStatus != StrictCombatReadinessClientStatus.Accepted
                    || state.LifeGeneration < _readinessRequest.LifeGeneration)
                    return false;
            }
            else if (_readinessStatus != StrictCombatReadinessClientStatus.Unavailable) return false;

            _readinessRequest = new StrictCombatReadinessRequest(binding, Guid.NewGuid(),
                state.LifeGeneration, state.Revision);
            _readinessResponse = default;
            _hasReadinessResponse = false;
            _readinessStarted = now;
            _readinessObserved = now;
            _readinessLastSend = 0;
            _readinessSends = 0;
            _readinessStatus = StrictCombatReadinessClientStatus.Pending;
            return true;
        }

        /// <summary>Drives bounded retries of the exact claim; transport queuing is not acknowledgement.</summary>
        public bool UpdateReadiness()
        {
            if (!IsCurrent(out _)) return false;
            if (!_requiresReadiness) return true;
            // Setup has no claim yet. A later life also needs an explicit claim after
            // its native handoff; observing that life does not terminate synchronization.
            if (!_readinessRequest.IsValid)
                return _readinessStatus == StrictCombatReadinessClientStatus.Unavailable;
            if (!ObserveReadiness(out long now, out _, out _)) return false;
            if (_readinessStatus != StrictCombatReadinessClientStatus.Pending)
                return _readinessStatus == StrictCombatReadinessClientStatus.Accepted;
            if (_readinessSends >= 8 || (_readinessSends != 0 && now - _readinessLastSend < 500))
                return true;
            Span<byte> payload = stackalloc byte[StrictCombatReadinessProtocol.RequestLength];
            if (!StrictCombatReadinessProtocol.TryWriteRequest(in _readinessRequest, payload, out int length))
            { _readinessStatus = StrictCombatReadinessClientStatus.Uncertain; return false; }
            _readinessLastSend = now;
            _readinessSends++;
            var result = Datagrams.Send(_peer, payload.Slice(0, length));
            if (result == DatagramSendResult.Queued
                || result == DatagramSendResult.WindowFull) return true;
            _readinessStatus = StrictCombatReadinessClientStatus.Uncertain;
            return false;
        }

        public bool TryReceiveReadiness(Datagram message)
        {
            if (message == null || !ReferenceEquals(message.Peer, _peer)
                || !ObserveReadiness(out _, out _, out var state)
                || !StrictCombatReadinessProtocol.TryReadResponse(message.Payload.Span, out var response)
                || !_readinessRequest.IsValid || !response.Request.SameRequest(in _readinessRequest)
                || state.LifeGeneration != _readinessRequest.LifeGeneration)
                return false;
            if (_hasReadinessResponse)
            {
                if (_readinessResponse.SameResponse(in response)) return true;
                _readinessStatus = StrictCombatReadinessClientStatus.Uncertain;
                return false;
            }
            if (_readinessStatus != StrictCombatReadinessClientStatus.Pending) return false;
            _readinessResponse = response;
            _hasReadinessResponse = true;
            _readinessStatus = response.Result == StrictCombatReadinessResult.Accepted
                ? StrictCombatReadinessClientStatus.Accepted : StrictCombatReadinessClientStatus.Rejected;
            return true;
        }

        private bool ObserveReadiness(out long now, out StrictCombatBinding binding,
            out StrictCombatSnapshot state)
        {
            now = 0;
            binding = default;
            state = default;
            if (!_requiresReadiness || Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            if (!IsCurrent(out now) || !TryRead(out binding, out state)) return false;
            if (!_readinessRequest.IsValid) return true;
            var expected = _readinessRequest.Binding;
            if (now < _readinessObserved || !binding.SameBinding(in expected)
                || state.LifeGeneration < _readinessRequest.LifeGeneration)
            { ReleaseReadiness(); return false; }
            _readinessObserved = now;
            if (_readinessStatus == StrictCombatReadinessClientStatus.Pending)
            {
                if (now - _readinessStarted >= 8000)
                { _readinessStatus = StrictCombatReadinessClientStatus.Expired; return false; }
                if (state.LifeGeneration != _readinessRequest.LifeGeneration || !HasReadinessNativeOwner(in state))
                { _readinessStatus = StrictCombatReadinessClientStatus.Uncertain; return false; }
            }
            return true;
        }

        private bool HasReadinessNativeOwner(in StrictCombatSnapshot state)
        {
            var barrier = _nativeLifeBarrier;
            return state.LifeGeneration != 0 && barrier != null && !barrier.IsRetired
                && ReferenceEquals(barrier.Owner, this) && ReferenceEquals(barrier.Player, _localPlayer)
                && barrier.LifeGeneration == state.LifeGeneration && IsCurrentNativeHealthOwner(barrier);
        }

        private void ReleaseReadiness()
        {
            if (_requiresReadiness && (_readinessStatus == StrictCombatReadinessClientStatus.Pending
                || _readinessStatus == StrictCombatReadinessClientStatus.Accepted))
                _readinessStatus = StrictCombatReadinessClientStatus.Uncertain;
        }

        private static bool ReadinessNegotiated(ControlPeer peer)
        {
            return peer != null && peer.ServerSession == null
                && ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, peer) && enrollment.State == EnrollmentState.Enrolled
                && ReadinessNegotiated(enrollment);
        }

        // For an enrollment the caller has already matched to this peer.
        private static bool ReadinessNegotiated(EnrollmentSnapshot enrollment)
        {
            return HasCapabilities(enrollment, StrictCombatReadinessProtocol.RequiredCapabilities);
        }
    }
}
