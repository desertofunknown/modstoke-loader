// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerNotices
    {
        public const int MaxPendingNoticesPerPeer = 32;
        public const int ReceiptTimeoutSeconds = 150;

        /// <summary>
        /// Sends only to a peer that explicitly negotiated receipts. The returned handle tracks client claims,
        /// not proof of display or reading. SendUncertain also returns a handle; do not automatically retry it.
        /// </summary>
        public static OpenACAISonsNoticeSendResult SendTracked(OpenACAISonsNoticePeer recipient, string message,
            out OpenACAISonsNoticeDelivery delivery)
        {
            delivery = null;
            if (!HooksInstalled)
                return OpenACAISonsNoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsNoticeSendResult.WrongThread;
            if (!OpenACAISonsNoticeProtocol.IsMessageValid(message))
                return OpenACAISonsNoticeSendResult.InvalidMessage;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsNoticeSendResult.NotServer;
                var availability = ResolvePeer(recipient, out var peer, out var connection);
                if (availability != OpenACAISonsNoticeSendResult.Queued)
                    return availability;
                if (!recipient.SupportsReceipts)
                    return OpenACAISonsNoticeSendResult.ReceiptsUnsupported;
                if (peer.Pending.Count >= MaxPendingNoticesPerPeer)
                    return OpenACAISonsNoticeSendResult.PendingLimitReached;

                var pending = new OpenACAISonsNoticeDelivery(recipient);
                peer.Pending.Add(pending.Id, pending);
                delivery = pending;
                // Reserve the ID before native sending; an exception cannot prove that nothing was queued.
                SendFrame(connection, "notice", peer.Token, message, noticeId: pending.Id);
                if (pending.State.Status == OpenACAISonsNoticeDeliveryStatus.Sending)
                    pending.Update(OpenACAISonsNoticeDeliveryStatus.Queued);
                return OpenACAISonsNoticeSendResult.Queued;
            }
            catch (Exception error)
            {
                if (delivery != null && delivery.State.Status == OpenACAISonsNoticeDeliveryStatus.Sending)
                    delivery.Update(OpenACAISonsNoticeDeliveryStatus.SendUncertain);
                ReportFailure(error);
                return delivery == null ? OpenACAISonsNoticeSendResult.Failed : OpenACAISonsNoticeSendResult.SendUncertain;
            }
        }

        /// <summary>Tracks a notice for an exact session with negotiated receipt support. No fallback send is made.</summary>
        public static OpenACAISonsNoticeSendResult SendTrackedToSession(OpenACAISonsServerSession recipient,
            string message, out OpenACAISonsNoticeDelivery delivery)
        {
            delivery = null;
            if (!HooksInstalled || !OpenACAISonsServerSessions.HooksInstalled)
                return OpenACAISonsNoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsNoticeSendResult.WrongThread;
            if (!OpenACAISonsNoticeProtocol.IsMessageValid(message))
                return OpenACAISonsNoticeSendResult.InvalidMessage;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsNoticeSendResult.NotServer;
                if (recipient == null)
                    return OpenACAISonsNoticeSendResult.PeerUnavailable;
                foreach (var peer in ServerPeers.Values)
                {
                    if (ReferenceEquals(peer.Snapshot.Session, recipient))
                        return SendTracked(peer.Snapshot, message, out delivery);
                }
                return OpenACAISonsNoticeSendResult.PeerUnavailable;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return delivery == null ? OpenACAISonsNoticeSendResult.Failed : OpenACAISonsNoticeSendResult.SendUncertain;
            }
        }

        private static OpenACAISonsNoticeSendResult ResolvePeer(OpenACAISonsNoticePeer recipient,
            out Peer peer, out BoltConnection connection)
        {
            peer = null;
            connection = null;
            if (recipient == null || !ServerPeers.TryGetValue(recipient.Connection, out peer)
                || !ReferenceEquals(peer.Snapshot, recipient) || !peer.Acknowledged)
                return OpenACAISonsNoticeSendResult.PeerUnavailable;
            if (recipient.Session != null && !OpenACAISonsServerSessions.HooksInstalled)
                return OpenACAISonsNoticeSendResult.Unavailable;

            connection = recipient.Session == null
                ? FindClient(recipient.Connection, recipient.ConnectionId)
                : OpenACAISonsServerSessions.ResolveCurrent(recipient.Session);
            if (recipient.Session != null && !OpenACAISonsServerSessions.HooksInstalled)
                return OpenACAISonsNoticeSendResult.Unavailable;
            if (connection == null)
            {
                ForgetConnection(recipient.Connection);
                return OpenACAISonsNoticeSendResult.PeerUnavailable;
            }
            return OpenACAISonsNoticeSendResult.Queued;
        }

        private static void ReceiveReceipt(ChatEvent chat, OpenACAISonsNoticeProtocol.Frame frame, byte[] token)
        {
            if (chat.Targets != (int)GlobalTargets.OnlyServer)
                return;
            var source = chat.RaisedBy;
            if (!ServerPeers.TryGetValue(source.Pointer, out var peer) || !peer.Snapshot.SupportsReceipts
                || source.ConnectionId != peer.Snapshot.ConnectionId
                || !OpenACAISonsNoticeProtocol.Matches(peer.Token, token)
                || ResolvePeer(peer.Snapshot, out _, out _) != OpenACAISonsNoticeSendResult.Queued
                || !peer.Pending.TryGetValue(frame.NoticeId.Value, out var pending))
                return;

            if (Environment.TickCount64 >= pending.ExpiresAt)
            {
                peer.Pending.Remove(pending.Id);
                pending.Update(OpenACAISonsNoticeDeliveryStatus.TimedOut);
                return;
            }
            var status = frame.Outcome switch
            {
                "accepted" => OpenACAISonsNoticeDeliveryStatus.ClientAccepted,
                "hud" => OpenACAISonsNoticeDeliveryStatus.ClientHudInvoked,
                "expired" => OpenACAISonsNoticeDeliveryStatus.ClientExpired,
                "evicted" => OpenACAISonsNoticeDeliveryStatus.ClientQueueEvicted,
                "failed" => OpenACAISonsNoticeDeliveryStatus.ClientDisplayFailed,
                _ => throw new InvalidOperationException("Unknown decoded notice receipt.")
            };
            if (status != OpenACAISonsNoticeDeliveryStatus.ClientAccepted)
                peer.Pending.Remove(pending.Id);
            if (pending.State.Status != status)
                pending.Update(status);
        }

        private static void ExpireTrackedNotices(long now)
        {
            foreach (var peer in ServerPeers.Values)
            {
                if (peer.Snapshot.Session?.ClosedAt != null)
                {
                    CompletePending(peer, OpenACAISonsNoticeDeliveryStatus.PeerUnavailable);
                    continue;
                }
                if (peer.Snapshot.Session != null && !OpenACAISonsServerSessions.HooksInstalled)
                {
                    CompletePending(peer, OpenACAISonsNoticeDeliveryStatus.Unavailable);
                    continue;
                }
                if (peer.Pending.Count == 0)
                    continue;
                var expired = new List<Guid>();
                foreach (var pending in peer.Pending.Values)
                {
                    if (now >= pending.ExpiresAt)
                    {
                        pending.Update(OpenACAISonsNoticeDeliveryStatus.TimedOut);
                        expired.Add(pending.Id);
                    }
                }
                foreach (var id in expired)
                    peer.Pending.Remove(id);
            }
        }

        private static void CompletePending(Peer peer, OpenACAISonsNoticeDeliveryStatus status)
        {
            foreach (var pending in peer.Pending.Values)
                pending.Update(status);
            peer.Pending.Clear();
        }

        private static void SendDisplayReceipt(IntPtr server, byte[] token, Guid id, OpenACAISonsNoticeDisplayOutcome outcome)
        {
            var text = outcome switch
            {
                OpenACAISonsNoticeDisplayOutcome.HudInvoked => "hud",
                OpenACAISonsNoticeDisplayOutcome.Expired => "expired",
                OpenACAISonsNoticeDisplayOutcome.QueueEvicted => "evicted",
                _ => "failed"
            };
            SendClientReceipt(server, token, id, text);
        }

        private static void SendClientReceipt(IntPtr expectedServer, byte[] expectedToken, Guid id, string outcome)
        {
            try
            {
                if (!ClientSupportsReceipts || !ReferenceEquals(_clientToken, expectedToken)
                    || _clientServer != expectedServer || !BoltNetwork.isRunning || !BoltNetwork.isClient
                    || !BoltNetwork.isConnected)
                    return;
                var server = BoltNetwork.server;
                if (server == null || server.Pointer != expectedServer)
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
    public sealed class OpenACAISonsNoticeDelivery
    {
        private OpenACAISonsNoticeDeliveryState _state;

        internal OpenACAISonsNoticeDelivery(OpenACAISonsNoticePeer peer)
        {
            Id = Guid.NewGuid();
            Peer = peer;
            CreatedAt = DateTimeOffset.UtcNow;
            ExpiresAt = Environment.TickCount64 + OpenACAISonsServerNotices.ReceiptTimeoutSeconds * 1000L;
            _state = new OpenACAISonsNoticeDeliveryState(OpenACAISonsNoticeDeliveryStatus.Sending);
        }

        public Guid Id { get; }
        public OpenACAISonsNoticePeer Peer { get; }
        public DateTimeOffset CreatedAt { get; }
        /// <summary>Immutable managed progress snapshot, safe to read off-thread. Client outcomes are untrusted claims.</summary>
        public OpenACAISonsNoticeDeliveryState State => Volatile.Read(ref _state);
        internal long ExpiresAt { get; }

        internal void Update(OpenACAISonsNoticeDeliveryStatus status)
        {
            if (!_state.IsTerminal)
                Volatile.Write(ref _state, new OpenACAISonsNoticeDeliveryState(status));
        }
    }

    public sealed class OpenACAISonsNoticeDeliveryState
    {
        internal OpenACAISonsNoticeDeliveryState(OpenACAISonsNoticeDeliveryStatus status)
        {
            Status = status;
            ObservedAt = DateTimeOffset.UtcNow;
        }
        public OpenACAISonsNoticeDeliveryStatus Status { get; }
        /// <summary>Server observation time, not the client's clock or display timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
        public bool IsTerminal => Status is not (OpenACAISonsNoticeDeliveryStatus.Sending
            or OpenACAISonsNoticeDeliveryStatus.Queued or OpenACAISonsNoticeDeliveryStatus.SendUncertain
            or OpenACAISonsNoticeDeliveryStatus.ClientAccepted);
    }

    public enum OpenACAISonsNoticeDeliveryStatus
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
