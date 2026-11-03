// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Modstoke.Sons
{
    public sealed partial class BoltMigrationCoordinator
    {
        private static ulong Capabilities(BoltMigrationPlan plan)
            => BoltMigrationProtocol.Capability | (plan.PrefabId == BoltPrefabs.PlayerNetwork.Value
                ? PlayerBridgeProtocol.Capability : 0);

        private bool Authorized(ControlPeer peer, ulong capabilities)
        {
            if (_disposeRequested || peer == null || !peer.IsCurrent
                || !BoltMigrationTransport.IsAvailable
                || !ReadEnrollment(peer, capabilities, out _)
                || !_factory.SupportsDispatcher(peer, BoltMigrationProtocol.PrepareSourceVersion)
                || _disposeRequested || !ReadEnrollment(peer, capabilities, out _)) return false;
            var connection = ControlTransport.Resolve(peer);
            if (connection == null || connection.WasCollected || !BoltNetwork.isRunning) return false;
            return peer.ServerSession != null
                ? BoltNetwork.isServer && peer.ServerSession.IsCurrent
                    && ReferenceEquals(peer.ServerSession, ServerSessions.ResolveObserved(connection))
                : BoltNetwork.isClient && !BoltNetwork.isServer
                    && BoltNetwork.server?.Pointer == connection.Pointer;
        }

        private static bool ReadEnrollment(ControlPeer peer, ulong capabilities,
            out EnrollmentSnapshot enrollment)
        {
            enrollment = null;
            return peer != null && peer.IsCurrent
                && ClientEnrollment.TryRead(peer, out enrollment) == EnrollmentReadStatus.Success
                && enrollment != null && ReferenceEquals(enrollment.Peer, peer)
                && enrollment.State == EnrollmentState.Enrolled
                && ClientEnrollment.AllowsGameTraffic(peer)
                && (enrollment.RequiredCapabilities & capabilities) == capabilities
                && (enrollment.OfferedCapabilities & capabilities) == capabilities;
        }

        private bool TryCaptureCohort(ControlPeer source, ulong capabilities,
            out Participant[] participants, out IReadOnlyList<ServerSession> sessions)
        {
            participants = null;
            sessions = ServerSessions.Sessions;
            var peers = ControlTransport.GetPeers();
            if (peers.Count == 0 || peers.Count > MaxParticipants || sessions.Count != peers.Count) return false;
            var captured = new Participant[peers.Count];
            var seen = new HashSet<ServerSession>(ReferenceEqualityComparer.Instance);
            bool hasSource = false;
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer == null || peer.RunId != source.RunId || peer.SocketPointer != source.SocketPointer
                    || peer.ServerSession == null || !seen.Add(peer.ServerSession)
                    || !Authorized(peer, capabilities) || !ReadEnrollment(peer, capabilities, out var enrollment))
                    return false;
                hasSource |= ReferenceEquals(source, peer);
                captured[i] = new Participant(peer, enrollment);
            }
            foreach (var session in sessions)
                if (session == null || !seen.Remove(session)) return false;
            if (!hasSource || seen.Count != 0 || !ReferenceEquals(sessions, ServerSessions.Sessions)) return false;
            participants = captured;
            return true;
        }

        private bool CaptureServer(Transaction transaction)
        {
            if (!Authorized(transaction.Peer, transaction.Request.RequiredCapabilities)
                || !ReadEnrollment(transaction.Peer, transaction.Request.RequiredCapabilities, out var enrollment)) return false;
            transaction.Participants = new[] { new Participant(transaction.Peer, enrollment) };
            return Current(transaction);
        }

        private bool Current(Transaction transaction)
        {
            if (_disposeRequested || transaction.Terminal || !ReferenceEquals(_active, transaction)
                || transaction.Peer.RunId != _historyRun || transaction.Participants == null
                || !BoltMigrationTransport.IsAvailable) return false;
            var peers = ControlTransport.GetPeers();
            if (peers.Count != transaction.Participants.Length) return false;
            foreach (var participant in transaction.Participants)
            {
                bool found = false;
                foreach (var peer in peers)
                    if (ReferenceEquals(peer, participant.Peer)) { found = true; break; }
                if (!found || !Authorized(participant.Peer, transaction.Request.RequiredCapabilities)
                    || !ReadEnrollment(participant.Peer, transaction.Request.RequiredCapabilities, out var enrollment)
                    || enrollment.RequiredCapabilities != participant.Required
                    || enrollment.OfferedCapabilities != participant.Offered) return false;
            }
            if (transaction.Role == BoltMigrationRole.Destination
                && !ReferenceEquals(transaction.ServerSessions, ServerSessions.Sessions)) return false;
            return transaction.Barrier == null || transaction.Barrier.IsCurrent();
        }

        private bool BeginRun(ControlPeer peer)
        {
            if (peer == null || !peer.IsCurrent || !BoltMigrationTransport.IsAvailable) return false;
            if (_historyRun == peer.RunId) return true;
            if (_active != null && !_active.Terminal) Fail(_active, "The original Bolt transport run ended.");
            if (_active != null && !_active.Terminal) return false;
            _active?.Barrier?.Dispose();
            _active = null;
            _historyRun = peer.RunId;
            AttemptedEntities.Clear();
            AttemptedIds.Clear();
            return true;
        }

        private static bool Reserve(ControlPeer peer, ulong oldId, Guid id)
        {
            if (peer.RunId != _historyRun || oldId == 0 || id == Guid.Empty
                || AttemptedEntities.Count >= MaxTransactionsPerRun
                || AttemptedEntities.Contains(oldId) || AttemptedIds.Contains(id)) return false;
            AttemptedEntities.Add(oldId);
            AttemptedIds.Add(id);
            return true;
        }

        private static bool Expired(Transaction transaction)
            => Stopwatch.GetElapsedTime(transaction.Started).TotalSeconds >= TransactionTimeoutSeconds;

        private bool Check(Transaction transaction)
        {
            if (transaction.PendingFailure != null)
            { Fail(transaction, transaction.PendingFailure); return false; }
            if (!Current(transaction) || Expired(transaction))
            {
                Fail(transaction, "Migration correlation, lifetime or frozen cohort is no longer current.");
                return false;
            }
            return true;
        }

        private void Fail(Transaction transaction, string detail)
        {
            if (transaction.Terminal) return;
            transaction.PendingFailure ??= detail;
            detail = transaction.PendingFailure;
            // Revoke local aggregate authority before attempting cancellation or resuming a source.
            transaction.Barrier?.Dispose();
            transaction.Barrier = null;
            var session = transaction.Session;
            bool uncertain = transaction.RetirementPossible
                || session?.Status == BoltMigrationStatus.Unconfirmed;
            if (!uncertain && transaction.Adapter is PlayerMigrationAdapter guarded
                && guarded.ProtectionReleasePending)
            {
                // Native callbacks still own their stack. Keep this transaction inert until
                // ordinary Update can cancel it without partially restoring the player's rig.
                return;
            }
            if (!uncertain && session == null && transaction.Role == BoltMigrationRole.Source
                && transaction.Adapter is PlayerMigrationAdapter player
                && !player.TryCancelAdmissionProtection(out _)) uncertain = true;
            if (!uncertain && session != null && session.Status != BoltMigrationStatus.Aborted)
            {
                if (session.Status != BoltMigrationStatus.Prepared
                    || !session.TryAbort(out var aborted)) uncertain = true;
                else if (aborted.Status != BoltMigrationStatus.Aborted) uncertain = true;
            }
            if (uncertain && transaction.Adapter is PlayerMigrationAdapter retained)
            {
                // A completed kernel session can still be waiting for runtime activation.
                // Revoke that runtime too; a terminal coordinator result alone cannot stop it.
                try { retained.RetainUnconfirmed(detail); }
                catch (Exception error)
                { detail += " Player runtime retirement was unconfirmed (" + error.GetType().Name + ")."; }
            }
            // Best-effort revocation only. Reliable delivery is not retried; other peers have the
            // same bounded timeout and must never interpret Aborted as a post-retirement rollback.
            if (transaction.Snapshot != null)
            {
                var aborted = BoltMigrationReceipt.Create(transaction.Snapshot,
                    BoltMigrationReceiptKind.Aborted);
                if (transaction.Participants != null)
                    foreach (var participant in transaction.Participants)
                        if (transaction.Role != BoltMigrationRole.Destination || participant.SnapshotSent)
                            BoltMigrationTransport.SendReceipt(participant.Peer, aborted);
            }
            Finish(transaction, new BoltMigrationResult(uncertain
                ? BoltMigrationStatus.Unconfirmed : BoltMigrationStatus.Aborted,
                session?.LastResult.MutationAttempted == true,
                detail + (uncertain ? " Effects are unconfirmed; no rollback or retry is authorized."
                    : " Local preparation cancelled; remote cleanup remains subject to delivery and timeout.")));
        }

        private void Finish(Transaction transaction, BoltMigrationResult result)
        {
            if (transaction.Terminal) return;
            transaction.Terminal = true;
            transaction.PendingFailure = null;
            transaction.Barrier?.Dispose();
            transaction.Barrier = null;
            LastRequestId = transaction.Request.RequestedPlan.Id;
            LastRequestGeneration = transaction.Request.RequestedPlan.Generation;
            LastSession = transaction.Session;
            LastResult = result;
            // Retain an uncertain session and its adapter/reference ownership until the run ends.
            if (result.Status != BoltMigrationStatus.Unconfirmed) _active = null;
            var handlers = Finished;
            if (handlers == null) return;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try { handler(transaction.Session, result); }
                catch (Exception) { }
            }
        }

        private static Participant FindParticipant(Transaction transaction, ControlPeer peer)
        {
            if (transaction.Participants != null)
                foreach (var participant in transaction.Participants)
                    if (ReferenceEquals(participant.Peer, peer)) return participant;
            return null;
        }

        private bool SendReceipt(Transaction transaction, ControlPeer peer,
            BoltMigrationReceipt receipt)
        {
            if (!Check(transaction)) return false;
            var sent = BoltMigrationTransport.SendReceipt(peer, receipt);
            if (sent == BoltMigrationSendResult.Queued) return true;
            Fail(transaction, "Receipt delivery was " + sent + "; the phase will not be replayed.");
            return false;
        }
    }
}
