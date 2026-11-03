// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Negotiates protocol participation for an exact connection. Enrollment is not account
    /// authentication, proof of unmodified software, permission, or gameplay admission.
    /// </summary>
    public static partial class ClientEnrollment
    {
        public const ushort ProtocolVersion = EnrollmentWire.ProtocolVersion;
        private const double NegotiationSeconds = 15;
        private const double RetrySeconds = 1;
        private const int MaxAttemptsPerMessage = 8;
        private static readonly Dictionary<Guid, Enrollment> Entries = new();
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static bool _selected;
        private static bool _faulted;
        private static ulong _localCapabilities;
        private static ulong _requiredClientCapabilities;

        internal static void Initialize(HarmonySupportSnapshot support)
        {
            if (Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) != 0)
                return;
            _gateSupport = support;
            ControlTransport.Opened += OnOpened;
            ControlTransport.Closed += OnClosed;
            ControlTransport.Received += OnReceived;
        }

        /// <summary>
        /// Select on both peers before Bolt starts. Capability bits belong to the consuming
        /// protocol; received claims only describe compatibility and cannot grant authority.
        /// Selection reserves the negotiation transport and does not enable strict combat.
        /// </summary>
        public static ControlResult SelectForNextStart(bool selected,
            ulong localCapabilities, ulong requiredClientCapabilities)
        {
            if (_ownerThreadId == 0)
                return ControlResult.Unavailable;
            if (!OnOwnerThread)
                return ControlResult.WrongThread;
            var result = ControlTransport.SelectForNextStart(selected);
            if (result != ControlResult.Success)
                return result;
            ResetForStart();
            ModAdmission.ClearSelection();
            _gateSelected = false;
            _selected = selected;
            _localCapabilities = localCapabilities;
            _requiredClientCapabilities = requiredClientCapabilities;
            return result;
        }

        /// <summary>
        /// Reads current local protocol state on the game thread. Do not cache this snapshot
        /// as authorization; a reconnect, transport failure or protocol rejection retires it.
        /// </summary>
        public static EnrollmentReadStatus TryRead(ControlPeer peer,
            out EnrollmentSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0)
                return EnrollmentReadStatus.Unavailable;
            if (!OnOwnerThread)
                return EnrollmentReadStatus.WrongThread;
            if (!_selected || _faulted)
                return EnrollmentReadStatus.Unavailable;
            if (peer == null || !Entries.TryGetValue(peer.PeerId, out var entry)
                || !ReferenceEquals(entry.Peer, peer))
                return EnrollmentReadStatus.PeerUnavailable;
            try
            {
                Refresh(entry, Now);
                snapshot = new EnrollmentSnapshot(peer, entry.State, entry.Reason,
                    entry.RequiredCapabilities, entry.OfferedCapabilities);
                return EnrollmentReadStatus.Success;
            }
            catch (Exception error)
            {
                Retire(entry, EnrollmentEndReason.TransportUnavailable);
                ReportFailure(error);
                return EnrollmentReadStatus.Failed;
            }
        }

        /// <summary>Looks up the exact current remote-client session, never a player or Steam ID.</summary>
        public static EnrollmentReadStatus TryRead(ServerSession session,
            out EnrollmentSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0)
                return EnrollmentReadStatus.Unavailable;
            if (!OnOwnerThread)
                return EnrollmentReadStatus.WrongThread;
            if (!_selected || _faulted)
                return EnrollmentReadStatus.Unavailable;
            if (session != null)
                foreach (var entry in Entries.Values)
                    if (ReferenceEquals(entry.Peer.ServerSession, session))
                        return TryRead(entry.Peer, out snapshot);
            return EnrollmentReadStatus.PeerUnavailable;
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal static bool TryGetModAdmissionNonce(ControlPeer peer, out byte[] nonce)
        {
            nonce = null;
            var entry = FindModAdmissionEntry(peer);
            if (entry == null)
                return false;
            nonce = (byte[])entry.Nonce.Clone();
            return true;
        }

        internal static bool ModAdmissionNonceMatches(ControlPeer peer, ReadOnlySpan<byte> nonce)
        {
            var entry = FindModAdmissionEntry(peer);
            return entry != null && CryptographicOperations.FixedTimeEquals(entry.Nonce, nonce);
        }

        private static Enrollment FindModAdmissionEntry(ControlPeer peer)
        {
            if (!OnOwnerThread || !_selected || _faulted || peer == null
                || !Entries.TryGetValue(peer.PeerId, out var entry) || !ReferenceEquals(entry.Peer, peer))
                return null;
            Refresh(entry, Now);
            if (entry.State != EnrollmentState.Enrolled || entry.Nonce == null
                || (entry.RequiredCapabilities & ModAdmission.ProtocolCapability) == 0
                || (entry.OfferedCapabilities & ModAdmission.ProtocolCapability) == 0)
                return null;
            return entry;
        }

        internal static void ResetForStart()
        {
            if (!OnOwnerThread)
                return;
            Entries.Clear();
            ModAdmission.ResetForStart();
            _faulted = false;
            ResetGateConnection();
        }

        internal static void Tick()
        {
            if (!OnOwnerThread || !_selected || _faulted)
                return;
            try
            {
                List<Guid> closed = null;
                foreach (var entry in Entries.Values)
                    if (!entry.Peer.IsCurrent) (closed ??= new List<Guid>()).Add(entry.Peer.PeerId);
                if (closed != null)
                    foreach (var id in closed) Entries.Remove(id);

                // Lifecycle notifications are bounded, so reconcile the current handles as well.
                foreach (var peer in ControlTransport.GetPeers())
                    Observe(peer);
                var now = Now;
                foreach (var entry in Entries.Values)
                {
                    Refresh(entry, now);
                    SendNext(entry, now);
                }
            }
            catch (Exception error)
            {
                _faulted = true;
                foreach (var entry in Entries.Values)
                    Retire(entry, EnrollmentEndReason.TransportUnavailable);
                ReportFailure(error);
            }
        }

        private static Enrollment Observe(ControlPeer peer)
        {
            if (peer == null || !_selected || _faulted
                || ControlTransport.State != ControlState.Active
                || ControlTransport.Resolve(peer) == null)
                return null;
            if (Entries.TryGetValue(peer.PeerId, out var existing))
                return ReferenceEquals(existing.Peer, peer) ? existing : null;
            if (Entries.Count >= ControlTransport.MaxPeers)
                return null;
            var entry = new Enrollment(peer, Now + NegotiationSeconds);
            if (peer.ServerSession != null)
            {
                entry.Nonce = RandomNumberGenerator.GetBytes(EnrollmentWire.NonceBytes);
                entry.RequiredCapabilities = _requiredClientCapabilities;
                entry.Outgoing = EnrollmentMessageKind.Challenge;
            }
            Entries.Add(peer.PeerId, entry);
            return entry;
        }

        private static void OnOpened(ControlPeer peer)
        {
            if (!OnOwnerThread || !_selected || _faulted)
                return;
            var entry = Observe(peer);
            if (entry != null) SendNext(entry, Now);
        }

        private static void OnClosed(ControlPeer peer)
        {
            if (OnOwnerThread && peer != null)
                Entries.Remove(peer.PeerId);
        }

        private static void Refresh(Enrollment entry, double now)
        {
            if (entry.State == EnrollmentState.Retired)
                return;
            if (ControlTransport.State != ControlState.Active)
                Retire(entry, EnrollmentEndReason.TransportUnavailable);
            else if (ControlTransport.Resolve(entry.Peer, out var held) == null)
            {
                // Paused session tracking holds the peer; the transport settles it on a later frame.
                if (!held) Retire(entry, EnrollmentEndReason.TransportUnavailable);
            }
            else if (entry.State == EnrollmentState.Pending && now >= entry.Deadline)
            {
                // A host that never challenged is not taking part. Release the client until its first frame.
                if (entry.Peer.ServerSession == null && entry.Nonce == null)
                {
                    entry.State = EnrollmentState.HostNotParticipating;
                    ModAdmission.HostNotParticipating(entry.Peer);
                }
                else
                    Retire(entry, EnrollmentEndReason.Expired);
            }
        }

        private static void Retire(Enrollment entry, EnrollmentEndReason reason)
        {
            if (entry.State == EnrollmentState.Retired)
                return;
            entry.State = EnrollmentState.Retired;
            entry.Reason = reason;
            entry.Nonce = null;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke client enrollment failed ("
                    + error.GetType().FullName + ").");
            }
            catch (Exception) { }
        }

        private sealed class Enrollment
        {
            internal readonly ControlPeer Peer;
            internal double Deadline;
            internal EnrollmentState State;
            internal EnrollmentEndReason Reason;
            internal byte[] Nonce;
            internal ulong RequiredCapabilities;
            internal ulong OfferedCapabilities;
            internal EnrollmentMessageKind Outgoing;
            internal int Attempts;
            internal double NextSend;
            internal bool SentWhileReleased;

            internal Enrollment(ControlPeer peer, double deadline)
            {
                Peer = peer;
                Deadline = deadline;
            }
        }
    }

    /// <summary>
    /// HostNotParticipating is a joining client whose host sent no enrollment frame in time. Its
    /// gameplay is released. A later host frame returns it to Pending for ordinary negotiation, unless
    /// gameplay was already sent while released: then it retires with HostAnsweredLate and disconnects.
    /// </summary>
    public enum EnrollmentState { Pending, Enrolled, Retired, HostNotParticipating }
    public enum EnrollmentEndReason
    {
        None, TransportUnavailable, Expired, IncompatibleCapabilities, InvalidMessage, RemoteRejected, SendFailed,
        HostAnsweredLate
    }
    public enum EnrollmentReadStatus { Success, Unavailable, WrongThread, PeerUnavailable, Failed }

    /// <summary>A local protocol observation. Capability claims are untrusted and confer no permissions.</summary>
    public sealed class EnrollmentSnapshot
    {
        internal EnrollmentSnapshot(ControlPeer peer,
            EnrollmentState state, EnrollmentEndReason reason,
            ulong requiredCapabilities, ulong offeredCapabilities)
        {
            Peer = peer;
            State = state;
            EndReason = reason;
            RequiredCapabilities = requiredCapabilities;
            OfferedCapabilities = offeredCapabilities;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ControlPeer Peer { get; }
        public EnrollmentState State { get; }
        public EnrollmentEndReason EndReason { get; }
        public ulong RequiredCapabilities { get; }
        public ulong OfferedCapabilities { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
