// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;

namespace Modstoke.Sons
{
    public static partial class ServerNotices
    {
        public const int MaxPendingNoticesPerPeer = 32;
        public const int ReceiptTimeoutSeconds = 150;

        /// <summary>
        /// Sends only to a peer that explicitly negotiated receipts. The returned handle tracks client claims,
        /// not proof of display or reading. SendUncertain also returns a handle; do not automatically retry it.
        /// </summary>
        public static NoticeSendResult SendTracked(NoticePeer recipient, string message,
            out NoticeDelivery delivery)
        {
            delivery = null;
            if (!HooksInstalled)
                return NoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return NoticeSendResult.WrongThread;
            if (!NoticeProtocol.IsMessageValid(message))
                return NoticeSendResult.InvalidMessage;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return NoticeSendResult.NotServer;
                var availability = ResolvePeer(recipient, out var peer, out var connection);
                if (availability != NoticeSendResult.Queued)
                    return availability;
                if (!recipient.SupportsReceipts)
                    return NoticeSendResult.ReceiptsUnsupported;
                if (peer.Pending.Count >= MaxPendingNoticesPerPeer)
                    return NoticeSendResult.PendingLimitReached;

                var pending = new NoticeDelivery(recipient);
                var text = NoticeProtocol.Encode("notice", peer.Token, message, noticeId: pending.Id);
                peer.Pending.Add(pending.Id, pending);
                delivery = pending;
                // Reserve the ID before native sending; an exception cannot prove that nothing was queued.
                SendPayload(ChatEvent.Create(connection, ReliabilityModes.ReliableOrdered), text);
                if (pending.State.Status == NoticeDeliveryStatus.Sending)
                    pending.Update(NoticeDeliveryStatus.Queued);
                return NoticeSendResult.Queued;
            }
            catch (Exception error)
            {
                if (delivery != null && delivery.State.Status == NoticeDeliveryStatus.Sending)
                    delivery.Update(NoticeDeliveryStatus.SendUncertain);
                ReportFailure(error);
                return delivery == null ? NoticeSendResult.Failed : NoticeSendResult.SendUncertain;
            }
        }

        /// <summary>Tracks a notice for an exact session with negotiated receipt support. No fallback send is made.</summary>
        public static NoticeSendResult SendTrackedToSession(ServerSession recipient,
            string message, out NoticeDelivery delivery)
        {
            delivery = null;
            if (!HooksInstalled || !ServerSessions.HooksInstalled)
                return NoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return NoticeSendResult.WrongThread;
            if (!NoticeProtocol.IsMessageValid(message))
                return NoticeSendResult.InvalidMessage;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return NoticeSendResult.NotServer;
                if (recipient == null)
                    return NoticeSendResult.PeerUnavailable;
                foreach (var peer in ServerPeers.Values)
                {
                    if (ReferenceEquals(peer.Snapshot.Session, recipient))
                        return SendTracked(peer.Snapshot, message, out delivery);
                }
                return NoticeSendResult.PeerUnavailable;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return delivery == null ? NoticeSendResult.Failed : NoticeSendResult.SendUncertain;
            }
        }

        private static NoticeSendResult ResolvePeer(NoticePeer recipient,
            out Peer peer, out BoltConnection connection)
        {
            peer = null;
            connection = null;
            if (recipient == null || !ServerPeers.TryGetValue(recipient.Connection, out peer)
                || !ReferenceEquals(peer.Snapshot, recipient) || !peer.Acknowledged)
                return NoticeSendResult.PeerUnavailable;
            if (recipient.Session != null && !ServerSessions.HooksInstalled)
                return NoticeSendResult.Unavailable;

            connection = recipient.Session == null
                ? FindClient(recipient.Connection, recipient.ConnectionId)
                : ServerSessions.ResolveCurrent(recipient.Session);
            // ResolveCurrent disables session tracking when its native read fails.
            if (recipient.Session != null && !ServerSessions.HooksInstalled)
                return NoticeSendResult.Unavailable;
            if (connection == null)
            {
                ForgetConnection(recipient.Connection);
                return NoticeSendResult.PeerUnavailable;
            }
            return NoticeSendResult.Queued;
        }

        private static void ReceiveReceipt(ChatEvent chat, NoticeProtocol.Frame frame, byte[] token)
        {
            if (chat.Targets != (int)GlobalTargets.OnlyServer)
                return;
            var source = chat.RaisedBy;
            if (!ServerPeers.TryGetValue(source.Pointer, out var peer) || !peer.Snapshot.SupportsReceipts
                || source.ConnectionId != peer.Snapshot.ConnectionId
                || !NoticeProtocol.Matches(peer.Token, token)
                || ResolvePeer(peer.Snapshot, out _, out _) != NoticeSendResult.Queued
                || !peer.Pending.TryGetValue(frame.NoticeId.Value, out var pending))
                return;

            if (Environment.TickCount64 >= pending.ExpiresAt)
            {
                peer.Pending.Remove(pending.Id);
                pending.Update(NoticeDeliveryStatus.TimedOut);
                return;
            }
            var status = frame.Outcome switch
            {
                "accepted" => NoticeDeliveryStatus.ClientAccepted,
                "hud" => NoticeDeliveryStatus.ClientHudInvoked,
                "expired" => NoticeDeliveryStatus.ClientExpired,
                "evicted" => NoticeDeliveryStatus.ClientQueueEvicted,
                "failed" => NoticeDeliveryStatus.ClientDisplayFailed,
                _ => throw new InvalidOperationException("Unknown decoded notice receipt.")
            };
            if (status != NoticeDeliveryStatus.ClientAccepted)
                peer.Pending.Remove(pending.Id);
            if (pending.State.Status != status)
                pending.Update(status);
        }

        private static void ExpireTrackedNotices(long now)
        {
            List<Guid> expired = null;
            foreach (var peer in ServerPeers.Values)
            {
                if (peer.Snapshot.Session?.ClosedAt != null)
                {
                    CompletePending(peer, NoticeDeliveryStatus.PeerUnavailable);
                    continue;
                }
                if (peer.Snapshot.Session != null && !ServerSessions.HooksInstalled)
                {
                    CompletePending(peer, NoticeDeliveryStatus.Unavailable);
                    continue;
                }
                if (peer.Pending.Count == 0)
                    continue;
                foreach (var pending in peer.Pending.Values)
                {
                    if (now >= pending.ExpiresAt)
                    {
                        pending.Update(NoticeDeliveryStatus.TimedOut);
                        (expired ??= new List<Guid>()).Add(pending.Id);
                    }
                }
                if (expired != null)
                {
                    foreach (var id in expired)
                        peer.Pending.Remove(id);
                    expired.Clear();
                }
            }
        }

        private static void CompletePending(Peer peer, NoticeDeliveryStatus status)
        {
            foreach (var pending in peer.Pending.Values)
                pending.Update(status);
            peer.Pending.Clear();
        }

        private static void SendDisplayReceipt(IntPtr server, byte[] token, Guid id, NoticeDisplayOutcome outcome)
        {
            var text = outcome switch
            {
                NoticeDisplayOutcome.HudInvoked => "hud",
                NoticeDisplayOutcome.Expired => "expired",
                NoticeDisplayOutcome.QueueEvicted => "evicted",
                _ => "failed"
            };
            SendClientReceipt(server, token, id, text);
        }

        private static void SendClientReceipt(IntPtr expectedServer, byte[] expectedToken, Guid id, string outcome)
        {
            try
            {
                if (!ClientSupportsReceipts || !ReferenceEquals(_clientToken, expectedToken)
                    || _clientServer != expectedServer || !BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer
                    || !BoltNetwork.isConnected)
                    return;
                var server = BoltNetwork.server;
                if (server == null || server.Pointer != expectedServer || server.ConnectionId != _clientServerConnectionId)
                    return;

                SendClientFrame("receipt", expectedToken, noticeId: id, outcome: outcome);
            }
            catch (Exception error)
            {
                ReportFailure(error);
            }
        }
    }

    /// <summary>One notice bound to one negotiated peer lifetime. No text, token or native objects are retained.</summary>
    public sealed class NoticeDelivery
    {
        private NoticeDeliveryState _state;

        internal NoticeDelivery(NoticePeer peer)
        {
            Id = Guid.NewGuid();
            Peer = peer;
            CreatedAt = DateTimeOffset.UtcNow;
            ExpiresAt = Environment.TickCount64 + ServerNotices.ReceiptTimeoutSeconds * 1000L;
            _state = new NoticeDeliveryState(NoticeDeliveryStatus.Sending);
        }

        public Guid Id { get; }
        public NoticePeer Peer { get; }
        public DateTimeOffset CreatedAt { get; }
        /// <summary>Immutable managed progress snapshot, safe to read off-thread. Client outcomes are untrusted claims.</summary>
        public NoticeDeliveryState State => Volatile.Read(ref _state);
        internal long ExpiresAt { get; }

        internal void Update(NoticeDeliveryStatus status)
        {
            if (!_state.IsTerminal)
                Volatile.Write(ref _state, new NoticeDeliveryState(status));
        }
    }

    public sealed class NoticeDeliveryState
    {
        internal NoticeDeliveryState(NoticeDeliveryStatus status)
        {
            Status = status;
            ObservedAt = DateTimeOffset.UtcNow;
        }
        public NoticeDeliveryStatus Status { get; }
        /// <summary>Server observation time, not the client's clock or display timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
        public bool IsTerminal => Status is not (NoticeDeliveryStatus.Sending
            or NoticeDeliveryStatus.Queued or NoticeDeliveryStatus.SendUncertain
            or NoticeDeliveryStatus.ClientAccepted);
    }

    public enum NoticeDeliveryStatus
    {
        Sending,
        Queued,
        SendUncertain,
        ClientAccepted,
        ClientHudInvoked,
        ClientExpired,
        ClientQueueEvicted,
        ClientDisplayFailed,
        TimedOut,
        PeerUnavailable,
        Unavailable
    }
}
