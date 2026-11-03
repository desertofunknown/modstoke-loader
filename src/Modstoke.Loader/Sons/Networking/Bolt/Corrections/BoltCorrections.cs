// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    /// <summary>
    /// Sends committed server values to one explicitly bound, enrolled property owner. The mode
    /// shares Binding through its authorized setup, forwards datagrams to TryReceive and drives
    /// Update on the game thread. No subscriptions, enrollment or gameplay rules are automatic.
    /// Use a new instance/binding for each life or attachment; dispose on mode retirement.
    /// </summary>
    public sealed class BoltCorrectionServer : IDisposable
    {
        private const int RetryIntervalMs = 500;
        private const int TimeoutMs = 8000;
        private const int MaximumSends = 8;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly ControlPeer _peer;
        private BoltCorrectionTarget _target;
        private BoltCorrection _latest;
        private long _started;
        private long _observed;
        private long _lastSend;
        private int _sends;
        private bool _attempted;
        private bool _busy;

        private BoltCorrectionServer(ControlPeer peer,
            BoltCorrectionBinding binding, BoltCorrectionTarget target)
        { _peer = peer; Binding = binding; _target = target; }

        public BoltCorrectionBinding Binding { get; }
        public BoltCorrectionStatus Status { get; private set; }
        public ulong Revision => _latest.Revision;
        /// <summary>A report from the remote client, not evidence of client integrity or lasting correction.</summary>
        public BoltCorrectionApplyResult PeerResult { get; private set; }
        public bool IsRetired => Status == BoltCorrectionStatus.Retired;

        public static bool TryCreate(ControlPeer peer, ulong entityNetworkId,
            ulong generation, int stateTypeId, int propertyIndex, BoltScalarKind kind,
            out BoltCorrectionServer server)
        {
            server = null;
            try
            {
                if (!BoltCorrectionPeers.IsNegotiated(peer, client: false)) return false;
                var binding = new BoltCorrectionBinding(Guid.NewGuid(), generation,
                    entityNetworkId, stateTypeId, propertyIndex, kind);
                if (!binding.IsValid || !BoltCorrectionTarget.TryCapture(peer, binding,
                    localOwner: false, out var target)) return false;
                server = new(peer, binding, target);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Keeps only the latest committed revision, coalescing superseded pending values. A true
        /// return means retained locally, not sent/applied. Equal revisions never reset retry budgets.
        /// Native target validity is checked when sending, not for every superseding queued value.
        /// Do not queue client proposals as authoritative values without the mode's validation.
        /// </summary>
        public bool TryQueue(ulong revision, BoltScalar value)
        {
            if (!OnThread || _busy || IsRetired || PeerResult is BoltCorrectionApplyResult.Rejected
                or BoltCorrectionApplyResult.Unconfirmed) return false;
            var candidate = new BoltCorrection(Binding, revision, value);
            if (!candidate.IsValid || revision < _latest.Revision) return false;
            if (revision == _latest.Revision)
                return value.SameBits(_latest.Value) && Status is BoltCorrectionStatus.Pending
                    or BoltCorrectionStatus.PeerApplied or BoltCorrectionStatus.PeerUnchanged;
            if (!_peer.IsCurrent) { Retire(); return false; }
            long now = Environment.TickCount64;
            if (now < 0 || now < _observed) { Retire(); return false; }
            _latest = candidate;
            _started = _observed = now;
            _sends = 0;
            // Preserve pacing across superseding revisions, not just retries of one value.
            PeerResult = BoltCorrectionApplyResult.None;
            Status = BoltCorrectionStatus.Pending;
            return true;
        }

        /// <summary>At most one send per call. Idle adapters do not resolve native entities or scan players.</summary>
        public bool Update()
        {
            if (!OnThread || _busy || IsRetired) return false;
            if (!_peer.IsCurrent) { Retire(); return false; }
            if (Status != BoltCorrectionStatus.Pending) return true;
            long now = Environment.TickCount64;
            if (now < _observed) { Retire(); return false; }
            _observed = now;
            if (now - _started >= TimeoutMs)
            {
                Status = BoltCorrectionStatus.TimedOut;
                return false; // The client may have applied it while its receipt was lost.
            }
            if (_sends >= MaximumSends || (_attempted && now - _lastSend < RetryIntervalMs)) return true;
            _busy = true;
            try
            {
                if (!Current()) { Retire(); return false; }
                Span<byte> bytes = stackalloc byte[BoltCorrectionProtocol.MessageLength];
                if (!BoltCorrectionProtocol.TryWriteCorrection(in _latest, bytes, out int length))
                { Retire(); return false; }
                _lastSend = now;
                _attempted = true;
                var result = Datagrams.Send(_peer, bytes.Slice(0, length));
                if (IsRetired) return false;
                if (result == DatagramSendResult.Queued) { _sends++; return true; }
                if (result == DatagramSendResult.WindowFull) return true;
                if (result == DatagramSendResult.Failed)
                { Status = BoltCorrectionStatus.Uncertain; return false; }
                Retire();
                return false;
            }
            catch (Exception)
            {
                if (!IsRetired) Status = BoltCorrectionStatus.Uncertain;
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>Consumes only a receipt for this exact peer, binding, revision and scalar value.</summary>
        public bool TryReceive(Datagram message)
        {
            if (!OnThread || _busy || IsRetired || message == null || !ReferenceEquals(message.Peer, _peer)
                || !BoltCorrectionProtocol.TryReadReceipt(message.Payload.Span, out var receipt, out var result)
                || receipt.Binding != Binding || receipt.Revision != _latest.Revision
                || !receipt.Value.SameBits(_latest.Value)) return false;
            if (!Current()) { Retire(); return false; }
            if (PeerResult != BoltCorrectionApplyResult.None)
            {
                if (PeerResult != result) Status = BoltCorrectionStatus.Uncertain;
                return true;
            }
            PeerResult = result;
            Status = result switch
            {
                BoltCorrectionApplyResult.Applied => BoltCorrectionStatus.PeerApplied,
                BoltCorrectionApplyResult.Unchanged => BoltCorrectionStatus.PeerUnchanged,
                BoltCorrectionApplyResult.Rejected => BoltCorrectionStatus.PeerRejected,
                _ => BoltCorrectionStatus.Uncertain
            };
            return true;
        }

        public void Dispose()
        {
            if (!OnThread) throw new InvalidOperationException("Correction adapters are game-thread bound.");
            Retire();
        }

        private bool OnThread => Environment.CurrentManagedThreadId == _thread;
        private bool Current()
        {
            try { return !IsRetired && BoltCorrectionPeers.IsNegotiated(_peer, client: false)
                && _target != null && _target.IsCurrent(); }
            catch (Exception) { return false; }
        }
        private void Retire() { Status = BoltCorrectionStatus.Retired; _target = null; }
    }

    /// <summary>
    /// Applies corrections only to a property explicitly authorized by the local mode. The mode
    /// must authenticate and match Binding against its current life before TryCreate; do not
    /// create adapters automatically for arbitrary received bindings. Forward datagrams explicitly.
    /// One-shot state correction does not replace health/inventory/physics owners or other writers.
    /// </summary>
    public sealed class BoltCorrectionClient : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly ControlPeer _peer;
        private BoltCorrectionTarget _target;
        private BoltCorrection _last;
        private bool _busy;
        private bool _terminal;

        private BoltCorrectionClient(ControlPeer peer,
            BoltCorrectionBinding binding, BoltCorrectionTarget target)
        { _peer = peer; Binding = binding; _target = target; }

        public BoltCorrectionBinding Binding { get; }
        public ulong Revision => _last.Revision;
        /// <summary>Local apply/readback result at that revision, not continuous authority or remote delivery.</summary>
        public BoltCorrectionApplyResult LastResult { get; private set; }
        public bool IsRetired { get; private set; }

        public static bool TryCreate(ControlPeer peer, BoltCorrectionBinding binding,
            out BoltCorrectionClient client)
        {
            client = null;
            try
            {
                if (!binding.IsValid || !BoltCorrectionPeers.IsNegotiated(peer, client: true)
                    || !BoltCorrectionTarget.TryCapture(peer, binding, localOwner: true, out var target))
                    return false;
                client = new(peer, binding, target);
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool TryReceive(Datagram message)
        {
            if (Environment.CurrentManagedThreadId != _thread || IsRetired || _busy
                || message == null || !ReferenceEquals(message.Peer, _peer)
                || !BoltCorrectionProtocol.TryReadCorrection(message.Payload.Span, out var correction)
                || correction.Binding != Binding) return false;
            _busy = true;
            try
            {
                if (!BoltCorrectionPeers.IsNegotiated(_peer, client: true)) { Retire(); return false; }
                if (correction.Revision < _last.Revision) return true;
                if (correction.Revision == _last.Revision)
                {
                    if (!correction.Value.SameBits(_last.Value)) { Retire(); return true; }
                    // An uncertain native application must never be repeated to recover a lost receipt.
                    SendReceipt();
                    return true;
                }
                if (_terminal)
                {
                    // A newer revision can overtake the failure receipt; explicitly reject it too.
                    _last = correction;
                    LastResult = BoltCorrectionApplyResult.Rejected;
                    SendReceipt();
                    return true;
                }
                _last = correction;
                LastResult = BoltCorrectionApplyResult.Unconfirmed;
                var target = _target;
                if (target == null)
                    LastResult = BoltCorrectionApplyResult.Rejected;
                else
                    LastResult = target.Apply(correction.Value);
                _terminal = LastResult is BoltCorrectionApplyResult.Rejected
                    or BoltCorrectionApplyResult.Unconfirmed;
                if (_terminal) _target = null;
                if (!IsRetired) SendReceipt();
                return true;
            }
            catch (Exception)
            {
                _terminal = true;
                _target = null;
                LastResult = BoltCorrectionApplyResult.Unconfirmed;
                return true;
            }
            finally { _busy = false; }
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Correction adapters are game-thread bound.");
            Retire();
        }

        private void SendReceipt()
        {
            if (IsRetired || LastResult == BoltCorrectionApplyResult.None) return;
            try
            {
                Span<byte> bytes = stackalloc byte[BoltCorrectionProtocol.MessageLength];
                if (BoltCorrectionProtocol.TryWriteReceipt(in _last, LastResult, bytes, out int length))
                    Datagrams.Send(_peer, bytes.Slice(0, length));
            }
            catch (Exception) { /* Transport failure must not change the cached native apply result. */ }
            // No independent resend queue: a duplicate correction resends the cached receipt.
        }

        private void Retire() { IsRetired = true; _terminal = true; _target = null; }
    }

    public enum BoltCorrectionStatus
    {
        Ready, Pending, PeerApplied, PeerUnchanged, PeerRejected, Uncertain, TimedOut, Retired
    }

    internal static class BoltCorrectionPeers
    {
        internal static bool IsNegotiated(ControlPeer peer, bool client)
        {
            const ulong capability = BoltCorrectionProtocol.Capability;
            return peer != null && (client ? peer.ServerSession == null : peer.ServerSession != null)
                && ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, peer) && enrollment.State == EnrollmentState.Enrolled
                && (enrollment.RequiredCapabilities & capability) == capability
                && (enrollment.OfferedCapabilities & capability) == capability
                && (client ? BoltNetwork.isClient && !BoltNetwork.isServer : BoltNetwork.isServer);
        }
    }
}
