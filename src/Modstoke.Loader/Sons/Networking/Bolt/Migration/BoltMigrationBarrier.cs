// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Endnight.Extensions;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Explicit server-side receipt barrier for one snapshot and one complete connection cohort.
    /// This does not send traffic, freeze admission, or prove remote gameplay effects. Call IsCurrent
    /// at each phase; loss of the cohort after retirement requires an Unconfirmed result, not rollback.
    /// </summary>
    public sealed class BoltMigrationBarrier : IDisposable
    {
        private readonly object _gate = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private BoltMigrationSnapshot _snapshot;
        private IReadOnlyList<ServerSession> _sessions;
        private readonly List<Participant> _participants = new();
        private bool _localReady;
        private bool _localRepaired;
        private ulong _newNetworkId;
        private bool _invalid;
        private bool _disposed;

        private BoltMigrationBarrier(BoltMigrationSnapshot snapshot,
            IReadOnlyList<ControlPeer> peers,
            IReadOnlyList<ServerSession> sessions)
        {
            _snapshot = snapshot;
            _sessions = sessions;
            foreach (var peer in peers)
                _participants.Add(new Participant(peer));
        }

        /// <summary>The fixed snapshot, or null after disposal.</summary>
        public BoltMigrationSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

        public static bool TryCreate(BoltMigrationSnapshot snapshot,
            out BoltMigrationBarrier barrier, out string detail)
        {
            barrier = null;
            detail = "A valid migration snapshot is required.";
            if (snapshot == null || !snapshot.Plan.IsValid)
                return false;

            // A nonempty managed snapshot is the transport's owner-thread proof. An empty result
            // also means wrong thread or unavailable transport, so it cannot authorize native reads.
            var peers = ControlTransport.GetPeers();
            if (peers.Count == 0)
            {
                detail = "No current control peers establish the game owner thread and transport run.";
                return false;
            }

            var candidate = new BoltMigrationBarrier(snapshot, peers,
                ServerSessions.Sessions);
            if (!candidate.IsCurrent())
            {
                candidate.Dispose();
                detail = "Migration requires a supported running server and the exact complete, enrolled client cohort.";
                return false;
            }

            barrier = candidate;
            detail = "Captured the complete enrolled migration cohort; local readiness must be recorded explicitly.";
            return true;
        }

        /// <summary>Recorded evidence only; performs no native reads or implicit phase validation.</summary>
        public bool IsReady
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed || _invalid || !_localReady) return false;
                    foreach (var participant in _participants)
                        if (!participant.Ready) return false;
                    return true;
                }
            }
        }

        /// <summary>Recorded evidence only; performs no native reads or implicit phase validation.</summary>
        public bool IsRepaired(ulong newId)
        {
            lock (_gate)
            {
                if (_disposed || _invalid || !_localRepaired || newId == 0 || _newNetworkId != newId)
                    return false;
                foreach (var participant in _participants)
                    if (!participant.Repaired) return false;
                return true;
            }
        }

        public bool TryRecord(ControlPeer peer, BoltMigrationReceipt receipt)
        {
            lock (_gate)
            {
                if (!OnThread || !Accepts(receipt)) return false;
                Participant participant = null;
                foreach (var candidate in _participants)
                    if (ReferenceEquals(candidate.Peer, peer)) { participant = candidate; break; }
                if (participant == null || !IsCurrent()) return false;
                if (receipt.Kind == BoltMigrationReceiptKind.Aborted)
                { _invalid = true; return true; }
                if (receipt.Kind == BoltMigrationReceiptKind.Ready)
                    participant.Ready = true;
                else
                {
                    if (!RecordReplacement(receipt.NewNetworkId)) return false;
                    participant.Repaired = true;
                }
                return true;
            }
        }

        public bool RecordLocal(BoltMigrationReceipt receipt)
        {
            lock (_gate)
            {
                if (!OnThread || !Accepts(receipt) || !IsCurrent()) return false;
                if (receipt.Kind == BoltMigrationReceiptKind.Aborted)
                { _invalid = true; return true; }
                if (receipt.Kind == BoltMigrationReceiptKind.Ready)
                    _localReady = true;
                else
                {
                    if (!RecordReplacement(receipt.NewNetworkId)) return false;
                    _localRepaired = true;
                }
                return true;
            }
        }

        public bool TryGetReadyReceipt(out BoltMigrationReceipt receipt)
        {
            lock (_gate)
            {
                receipt = null;
                if (!IsCurrent() || !IsReady) return false;
                receipt = BoltMigrationReceipt.Create(_snapshot,
                    BoltMigrationReceiptKind.Ready);
                return true;
            }
        }

        public bool TryGetRepairReceipt(ulong newId, out BoltMigrationReceipt receipt)
        {
            lock (_gate)
            {
                receipt = null;
                if (!IsCurrent() || !IsRepaired(newId)) return false;
                receipt = BoltMigrationReceipt.Create(_snapshot,
                    BoltMigrationReceiptKind.Repaired, newId);
                return true;
            }
        }

        /// <summary>
        /// Revalidates the cohort only when explicitly called on its owner thread. Any observed
        /// membership, lifetime, enrollment or server failure permanently invalidates this barrier.
        /// </summary>
        public bool IsCurrent()
        {
            lock (_gate)
            {
                if (!OnThread || _disposed || _invalid) return false;
                try
                {
                    if (MatchesCurrent()) return true;
                }
                catch (Exception) { }
                _invalid = true;
                return false;
            }
        }

        private bool MatchesCurrent()
        {
            // Managed membership is always checked before Bolt, including during phase validation.
            var peers = ControlTransport.GetPeers();
            if (peers.Count != _participants.Count || peers.Count == 0
                || !ServerSessions.HooksInstalled
                || ControlTransport.State != ControlState.Active
                || !ReferenceEquals(_sessions, ServerSessions.Sessions)
                || _sessions.Count != _participants.Count)
                return false;

            var seenPeers = new HashSet<ControlPeer>(ReferenceEqualityComparer.Instance);
            var seenSessions = new HashSet<ServerSession>(ReferenceEqualityComparer.Instance);
            var runId = _participants[0].Peer.RunId;
            var socket = _participants[0].Peer.SocketPointer;
            foreach (var peer in peers)
            {
                if (peer == null || !seenPeers.Add(peer) || !peer.IsCurrent
                    || peer.RunId != runId || peer.SocketPointer != socket
                    || peer.ServerSession == null || !peer.ServerSession.IsCurrent
                    || !seenSessions.Add(peer.ServerSession)) return false;
                bool captured = false;
                foreach (var participant in _participants)
                    if (ReferenceEquals(participant.Peer, peer)) { captured = true; break; }
                if (!captured) return false;
            }
            foreach (var session in _sessions)
                if (session == null || !seenSessions.Remove(session)) return false;
            if (seenSessions.Count != 0
                || HarmonySupport.CreateState()?.SafeForHarmonyPatches != true
                || !GameBuild.IsRecognized
                || !BoltNetwork.isRunning || !BoltNetwork.isServer)
                return false;

            var connections = new HashSet<IntPtr>();
            foreach (var participant in _participants)
            {
                var peer = participant.Peer;
                if (ClientEnrollment.TryRead(peer, out var enrollment)
                        != EnrollmentReadStatus.Success
                    || !ReferenceEquals(enrollment.Peer, peer)
                    || enrollment.State != EnrollmentState.Enrolled
                    || (enrollment.RequiredCapabilities & BoltMigrationProtocol.Capability) == 0
                    || (enrollment.OfferedCapabilities & BoltMigrationProtocol.Capability) == 0)
                    return false;
                var connection = ControlTransport.Resolve(peer);
                if (connection == null || connection.WasCollected
                    || !connections.Add(connection.Pointer)
                    || !ReferenceEquals(peer.ServerSession, ServerSessions.ResolveObserved(connection)))
                    return false;
            }

            // Sessions are last-observed membership. Scan native clients as well so a client that
            // has not yet acquired a session/control peer cannot be silently omitted from the barrier.
            bool complete = true;
            BoltNetwork.clients.ForEach((Action<BoltConnection>)(connection =>
            {
                if (connection == null || connection.WasCollected || !connections.Remove(connection.Pointer))
                {
                    complete = false;
                    return;
                }
                bool matched = false;
                foreach (var participant in _participants)
                {
                    var peer = participant.Peer;
                    if (peer.ConnectionPointer == connection.Pointer && peer.ConnectionId == connection.ConnectionId
                        && ReferenceEquals(peer.ServerSession, ServerSessions.ResolveObserved(connection)))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched) complete = false;
            }));
            // Membership snapshots change on joins/closes, including a join followed by a departure
            // between phases. Never replace the captured snapshot with the new one.
            return complete && connections.Count == 0
                && ReferenceEquals(_sessions, ServerSessions.Sessions)
                && ControlTransport.State == ControlState.Active;
        }

        private bool OnThread => Environment.CurrentManagedThreadId == _thread;

        private bool Accepts(BoltMigrationReceipt receipt) => !_disposed && !_invalid
            && receipt != null && receipt.Matches(_snapshot)
            && (receipt.Kind == BoltMigrationReceiptKind.Ready
                || receipt.Kind == BoltMigrationReceiptKind.Repaired
                || receipt.Kind == BoltMigrationReceiptKind.Aborted);

        private bool RecordReplacement(ulong newId)
        {
            if (newId == 0 || newId == _snapshot.Plan.OldNetworkId
                || (_newNetworkId != 0 && _newNetworkId != newId))
            {
                _invalid = true;
                return false;
            }
            _newNetworkId = newId;
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _snapshot = null;
                _sessions = null;
                _participants.Clear();
            }
        }

        private sealed class Participant
        {
            internal readonly ControlPeer Peer;
            internal bool Ready;
            internal bool Repaired;
            internal Participant(ControlPeer peer) { Peer = peer; }
        }
    }
}
