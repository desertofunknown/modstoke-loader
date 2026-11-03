// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;

namespace Modstoke.Sons
{
    /// <summary>Explicit submissions to registered Modstoke commands on a remote server or the current listen host.</summary>
    public static class ClientCommands
    {
        public const int MaxCommandBytes = NoticeProtocol.MaxCommandBytes;
        public const int MaxReplyBytes = NoticeProtocol.MaxReplyBytes;
        public const int MaxPendingCommands = 16;
        public const int ResponseTimeoutSeconds = 30;

        /// <summary>
        /// Queued observation of a request's first terminal state, delivered on a later game-thread tick.
        /// Correlate by the returned handle, not its connection-local sequence. This does not prove server execution.
        /// </summary>
        public static event Action<ClientCommand> Completed
        {
            add => ServerNotices.ClientCommandCompleted += value;
            remove => ServerNotices.ClientCommandCompleted -= value;
        }

        /// <summary>
        /// A remote handshake or observed listen-host session supports commands. Grants no permission.
        /// Listen-host identity observations are bounded to 250 ms; submissions revalidate the exact player and world.
        /// Off-thread reads observe the last game-thread listen-host snapshot.
        /// </summary>
        public static bool IsSupported => ServerNotices.HostCommandsSupported
            || ServerNotices.ClientSupportsCommands;

        /// <summary>
        /// The current remote handshake or observed listen-host session supports inline reply snapshots.
        /// Listen-host identity observations are bounded to 250 ms; this capability grants no permission.
        /// Off-thread reads observe the last game-thread listen-host snapshot.
        /// </summary>
        public static bool SupportsReplies => ServerNotices.HostCommandsSupported
            || ServerNotices.ClientSupportsCommandReplies;

        /// <summary>
        /// Gets the current command session's managed identity on the game thread.
        /// Changes with each remote handshake or listen-host player/world lifetime. This cache key grants no permission.
        /// </summary>
        public static bool TryGetCurrentSession(out Guid sessionId) =>
            ServerNotices.TryGetCurrentCommandSession(out sessionId);

        /// <summary>
        /// Sends slash-prefixed command text on the game thread without posting to public chat.
        /// Queued and SendUncertain return a handle; neither proves execution. No retry or fallback is made.
        /// Remote preparation failures before native Send starts return no handle or pending request.
        /// For a listen host, Queued means accepted: the handler runs synchronously and the handle already holds
        /// its ServerReported result. Completed is still delivered on a later tick. TrackReply is unsupported locally.
        /// </summary>
        public static ClientCommandSendResult Submit(string command, out ClientCommand request) =>
            ServerNotices.SubmitClientCommand(command, null, out request);

        /// <summary>
        /// Sends on the game thread only for the nonempty identity captured by TryGetCurrentSession.
        /// A session mismatch returns ServerUnavailable without a request or command dispatch.
        /// Delivery, host execution and no-retry behavior match the unbound overload. Identity grants no permission.
        /// </summary>
        public static ClientCommandSendResult Submit(string command, Guid expectedSessionId,
            out ClientCommand request) =>
            ServerNotices.SubmitClientCommand(command, expectedSessionId, out request);
    }

    public static partial class ServerNotices
    {
        private static readonly Dictionary<long, ClientCommand> ClientCommands = new();
        private static Queue<ClientCommand> _completedClientCommands = new();
        private static bool _clientSupportsCommands;
        private static bool _clientSupportsCommandReplies;
        private static Guid _clientCommandSessionId;
        private static long _nextClientCommandSequence;
        private static int _preparingClientCommands;

        internal static event Action<ClientCommand> ClientCommandCompleted;

        internal static bool ClientSupportsCommands => ClientReady && Volatile.Read(ref _clientSupportsCommands);

        internal static bool ClientSupportsCommandReplies => ClientSupportsCommands
            && Volatile.Read(ref _clientSupportsCommandReplies);

        internal static bool TryGetCurrentCommandSession(out Guid sessionId)
        {
            sessionId = Guid.Empty;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return false;
            if (TryGetHostCommandSession(out var host))
            {
                sessionId = host.Id;
                return true;
            }
            if (!ClientSupportsCommands
                || _clientCommandSessionId == Guid.Empty)
                return false;
            var currentSessionId = _clientCommandSessionId;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer || !BoltNetwork.isConnected)
                    return false;
                var server = BoltNetwork.server;
                if (server == null || server.Pointer != _clientServer || server.ConnectionId != _clientServerConnectionId
                    || !ClientSupportsCommands || _clientCommandSessionId != currentSessionId)
                    return false;
                sessionId = currentSessionId;
                return true;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return false;
            }
        }

        internal static void EnqueueClientCommandCompletion(ClientCommand request) =>
            _completedClientCommands.Enqueue(request);

        private static void DispatchClientCommandCompletions()
        {
            if (_completedClientCommands.Count == 0)
                return;

            var batch = _completedClientCommands;
            _completedClientCommands = new Queue<ClientCommand>();
            foreach (var request in batch)
            {
                var handlers = ClientCommandCompleted;
                if (handlers == null)
                    continue;
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    try { handler(request); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }

        internal static ClientCommandSendResult SubmitClientCommand(string command, Guid? expectedSessionId,
            out ClientCommand request)
        {
            request = null;
            if (!HooksInstalled)
                return ClientCommandSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return ClientCommandSendResult.WrongThread;
            if (!NoticeProtocol.IsCommandValid(command))
                return ClientCommandSendResult.InvalidCommand;
            if (expectedSessionId == Guid.Empty)
                return ClientCommandSendResult.ServerUnavailable;

            ChatEvent chat = null;
            var sendAttempted = false;
            var preparing = false;
            try
            {
                if (BoltNetwork.isRunning && BoltNetwork.isServer)
                    return SubmitHostClientCommand(command, expectedSessionId, out request);
                if (!BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer)
                    return ClientCommandSendResult.NotClient;
                var server = BoltNetwork.server;
                if (!BoltNetwork.isConnected || server == null || server.Pointer != _clientServer
                    || server.ConnectionId != _clientServerConnectionId || !ClientReady)
                    return ClientCommandSendResult.ServerUnavailable;
                if (!ClientSupportsCommands)
                    return ClientCommandSendResult.Unsupported;

                var sessionId = _clientCommandSessionId;
                if (expectedSessionId.HasValue && expectedSessionId.Value != sessionId)
                    return ClientCommandSendResult.ServerUnavailable;
                ExpireClientCommands(Environment.TickCount64);
                if (ClientCommands.Count + _preparingClientCommands >= global::Modstoke.Sons.ClientCommands.MaxPendingCommands)
                    return ClientCommandSendResult.PendingLimitReached;
                if (_nextClientCommandSequence == long.MaxValue)
                    return ClientCommandSendResult.SequenceExhausted;

                _preparingClientCommands++;
                preparing = true;
                var pending = new ClientCommand(++_nextClientCommandSequence);
                var text = NoticeProtocol.Encode("command", _clientToken,
                    commandSequence: pending.Sequence, message: command);
                chat = ChatEvent.Create(GlobalTargets.OnlyServer, ReliabilityModes.ReliableOrdered);
                chat.Sender = new NetworkId(0);
                chat.Message = text;

                // Native preparation can reenter submission or replace the negotiated session.
                if (!TryGetCurrentCommandSession(out var currentSessionId) || currentSessionId != sessionId)
                    return ClientCommandSendResult.ServerUnavailable;
                if (_nextClientCommandSequence != pending.Sequence)
                    return ClientCommandSendResult.Failed;

                ClientCommands.Add(pending.Sequence, pending);
                _preparingClientCommands--;
                preparing = false;
                request = pending;
                // Admit only after preparation, but before Send can synchronously return a result.
                sendAttempted = true;
                SendOwnFrame(chat);
                if (pending.State.Progress == ClientCommandProgress.Sending)
                    pending.Update(ClientCommandProgress.Queued);
                return ClientCommandSendResult.Queued;
            }
            catch (Exception error)
            {
                if (request != null && request.State.Progress == ClientCommandProgress.Sending)
                    request.Update(ClientCommandProgress.SendUncertain);
                ReportFailure(error);
                return request == null ? ClientCommandSendResult.Failed
                    : ClientCommandSendResult.SendUncertain;
            }
            finally
            {
                if (preparing)
                    _preparingClientCommands--;
                if (chat != null && !sendAttempted)
                {
                    try { chat.FreeStorage(); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }

        private static void ReceiveClientCommand(ChatEvent chat, NoticeProtocol.Frame frame, byte[] token)
        {
            if (chat.Targets != (int)GlobalTargets.OnlyServer)
                return;
            var source = chat.RaisedBy;
            if (!ServerPeers.TryGetValue(source.Pointer, out var peer) || !peer.Snapshot.SupportsCommands
                || peer.Snapshot.Session == null || source.ConnectionId != peer.Snapshot.ConnectionId
                || !NoticeProtocol.Matches(peer.Token, token)
                || ResolvePeer(peer.Snapshot, out _, out _) != NoticeSendResult.Queued)
                return;
            if (frame.CommandSequence <= peer.LastCommandSequence)
            {
                ClientSignals.RecordReplayedCommand(peer.Snapshot.Session);
                return;
            }

            // Advance before user callbacks. Old sequences never execute again during this peer lifetime.
            peer.LastCommandSequence = frame.CommandSequence;
            var outcome = ServerChat.DispatchClientCommand(peer.Snapshot.Session, frame.Message,
                peer.Snapshot.SupportsCommandReplies, out var reportedReply, out var rateLimited);
            if (rateLimited)
            {
                ClientSignals.RecordRateLimitedCommand(peer.Snapshot.Session);
                return;
            }
            if (outcome == ClientCommandOutcome.NotFound)
                ClientSignals.RecordUnknownCommand(peer.Snapshot.Session, Commands.ReadName(frame.Message));
            if (!HooksInstalled || !BoltNetwork.isRunning || !BoltNetwork.isServer
                || ResolvePeer(peer.Snapshot, out _, out var connection) != NoticeSendResult.Queued)
                return;
            SendFrame(connection, "command-result", peer.Token, commandSequence: frame.CommandSequence,
                message: peer.Snapshot.SupportsCommandReplies ? reportedReply : null,
                outcome: outcome switch
                {
                    ClientCommandOutcome.Succeeded => "succeeded",
                    ClientCommandOutcome.InvalidArguments => "invalid",
                    ClientCommandOutcome.Denied => "denied",
                    ClientCommandOutcome.Unavailable => "unavailable",
                    ClientCommandOutcome.NotFound => "unknown",
                    ClientCommandOutcome.RejectedByPolicy => "rejected",
                    _ => "failed"
                });
        }

        private static void ReceiveCommandResult(NoticeProtocol.Frame frame)
        {
            if (!ClientSupportsCommands || !BoltNetwork.isConnected
                || (!ClientSupportsCommandReplies && (frame.Message != null || frame.ReplyTruncated))
                || !ClientCommands.Remove(frame.CommandSequence, out var pending))
                return;
            if (Environment.TickCount64 >= pending.ExpiresAt)
            {
                pending.Update(ClientCommandProgress.TimedOut);
                return;
            }

            var outcome = frame.Outcome switch
            {
                "succeeded" => ClientCommandOutcome.Succeeded,
                "invalid" => ClientCommandOutcome.InvalidArguments,
                "denied" => ClientCommandOutcome.Denied,
                "unavailable" => ClientCommandOutcome.Unavailable,
                "unknown" => ClientCommandOutcome.NotFound,
                "rejected" => ClientCommandOutcome.RejectedByPolicy,
                _ => ClientCommandOutcome.Failed
            };
            pending.Update(ClientCommandProgress.ServerReported, outcome, frame.Message, frame.ReplyTruncated);
        }

        private static void ExpireClientCommands(long now)
        {
            if (ClientCommands.Count == 0)
                return;
            List<long> expired = null;
            foreach (var pending in ClientCommands.Values)
            {
                if (now >= pending.ExpiresAt)
                {
                    pending.Update(ClientCommandProgress.TimedOut);
                    (expired ??= new List<long>()).Add(pending.Sequence);
                }
            }
            if (expired != null)
                foreach (var sequence in expired)
                    ClientCommands.Remove(sequence);
        }

        private static void CompleteClientCommands(ClientCommandProgress progress)
        {
            foreach (var pending in ClientCommands.Values)
                pending.Update(progress);
            ClientCommands.Clear();
        }
    }

    /// <summary>One remote or listen-host session request. Retains no command text, token or native connection.</summary>
    public sealed class ClientCommand
    {
        private ClientCommandState _state;

        internal ClientCommand(long sequence)
        {
            Sequence = sequence;
            CreatedAt = DateTimeOffset.UtcNow;
            ExpiresAt = Environment.TickCount64 + ClientCommands.ResponseTimeoutSeconds * 1000L;
            _state = new ClientCommandState(ClientCommandProgress.Sending, null);
        }

        /// <summary>Correlation sequence within one command route, not a globally unique ID or credential.</summary>
        public long Sequence { get; }
        public DateTimeOffset CreatedAt { get; }
        /// <summary>Immutable managed observation safe to read off-thread; server outcomes are reports, not attestation.</summary>
        public ClientCommandState State => Volatile.Read(ref _state);
        internal long ExpiresAt { get; }

        internal void Update(ClientCommandProgress progress, ClientCommandOutcome? outcome = null,
            string reply = null, bool replyTruncated = false)
        {
            if (_state.IsTerminal)
                return;
            var state = new ClientCommandState(progress, outcome, reply, replyTruncated);
            Volatile.Write(ref _state, state);
            if (state.IsTerminal)
                ServerNotices.EnqueueClientCommandCompletion(this);
        }
    }

    public sealed class ClientCommandState
    {
        internal ClientCommandState(ClientCommandProgress progress,
            ClientCommandOutcome? outcome)
            : this(progress, outcome, null, false)
        {
        }

        internal ClientCommandState(ClientCommandProgress progress,
            ClientCommandOutcome? outcome, string reply, bool replyTruncated)
        {
            Progress = progress;
            Outcome = outcome;
            Reply = reply;
            ReplyTruncated = replyTruncated;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ClientCommandProgress Progress { get; }
        /// <summary>Present only for ServerReported. Succeeded is the handler's claim, not proof of game state or durability.</summary>
        public ClientCommandOutcome? Outcome { get; }
        /// <summary>
        /// Optional server-reported text for ServerReported, not proof of delivery on another channel or durable state.
        /// Treat it as untrusted display text; null means no inline reply was reported.
        /// </summary>
        public string Reply { get; }
        /// <summary>The inline reply is a bounded prefix of the server's text; it may be empty.</summary>
        public bool ReplyTruncated { get; }
        public DateTimeOffset ObservedAt { get; }
        public bool IsTerminal => Progress is not (ClientCommandProgress.Sending
            or ClientCommandProgress.Queued or ClientCommandProgress.SendUncertain);
    }

    public enum ClientCommandSendResult
    {
        /// <summary>Accepted request. A listen-host handle already contains its synchronous ServerReported result.</summary>
        Queued,
        Unavailable,
        WrongThread,
        InvalidCommand,
        NotClient,
        ServerUnavailable,
        Unsupported,
        PendingLimitReached,
        SequenceExhausted,
        Failed,
        SendUncertain
    }

    public enum ClientCommandProgress
    {
        Sending,
        Queued,
        SendUncertain,
        ServerReported,
        TimedOut,
        ServerUnavailable,
        Unavailable
    }

    public enum ClientCommandOutcome
    {
        Succeeded,
        InvalidArguments,
        Denied,
        Unavailable,
        Failed,
        NotFound,
        RejectedByPolicy
    }
}
