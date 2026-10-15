// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;

namespace OpenACAI.Sons
{
    /// <summary>Explicit submissions to registered OpenACAI commands on a negotiated remote server.</summary>
    public static class OpenACAISonsClientCommands
    {
        public const int MaxCommandBytes = OpenACAISonsNoticeProtocol.MaxCommandBytes;
        public const int MaxPendingCommands = 16;
        public const int ResponseTimeoutSeconds = 30;

        /// <summary>
        /// Queued observation of a request's first terminal state, delivered on a later game-thread tick.
        /// Correlate by the returned handle, not its connection-local sequence. This does not prove server execution.
        /// </summary>
        public static event Action<OpenACAISonsClientCommand> Completed
        {
            add => OpenACAISonsServerNotices.ClientCommandCompleted += value;
            remove => OpenACAISonsServerNotices.ClientCommandCompleted -= value;
        }

        /// <summary>The current handshake negotiated this transport. It grants no command permission.</summary>
        public static bool IsSupported => OpenACAISonsServerNotices.ClientSupportsCommands;

        /// <summary>
        /// Sends slash-prefixed command text on the game thread without posting to public chat.
        /// Queued and SendUncertain return a handle; neither proves execution. No retry or fallback is made.
        /// </summary>
        public static OpenACAISonsClientCommandSendResult Submit(string command, out OpenACAISonsClientCommand request) =>
            OpenACAISonsServerNotices.SubmitClientCommand(command, out request);
    }

    public static partial class OpenACAISonsServerNotices
    {
        private static readonly Dictionary<long, OpenACAISonsClientCommand> ClientCommands = new();
        private static Queue<OpenACAISonsClientCommand> _completedClientCommands = new();
        private static bool _clientSupportsCommands;
        private static long _nextClientCommandSequence;

        internal static event Action<OpenACAISonsClientCommand> ClientCommandCompleted;

        internal static bool ClientSupportsCommands => ClientReady && Volatile.Read(ref _clientSupportsCommands);

        internal static void EnqueueClientCommandCompletion(OpenACAISonsClientCommand request) =>
            _completedClientCommands.Enqueue(request);

        private static void DispatchClientCommandCompletions()
        {
            if (_completedClientCommands.Count == 0)
                return;

            var batch = _completedClientCommands;
            _completedClientCommands = new Queue<OpenACAISonsClientCommand>();
            foreach (var request in batch)
            {
                var handlers = ClientCommandCompleted;
                if (handlers == null)
                    continue;
                foreach (Action<OpenACAISonsClientCommand> handler in handlers.GetInvocationList())
                {
                    try { handler(request); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }

        internal static OpenACAISonsClientCommandSendResult SubmitClientCommand(string command,
            out OpenACAISonsClientCommand request)
        {
            request = null;
            if (!HooksInstalled)
                return OpenACAISonsClientCommandSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsClientCommandSendResult.WrongThread;
            if (!OpenACAISonsNoticeProtocol.IsCommandValid(command))
                return OpenACAISonsClientCommandSendResult.InvalidCommand;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer)
                    return OpenACAISonsClientCommandSendResult.NotClient;
                var server = BoltNetwork.server;
                if (!BoltNetwork.isConnected || server == null || server.Pointer != _clientServer
                    || server.ConnectionId != _clientServerConnectionId || !ClientReady)
                    return OpenACAISonsClientCommandSendResult.ServerUnavailable;
                if (!ClientSupportsCommands)
                    return OpenACAISonsClientCommandSendResult.Unsupported;

                ExpireClientCommands(Environment.TickCount64);
                if (ClientCommands.Count >= OpenACAISonsClientCommands.MaxPendingCommands)
                    return OpenACAISonsClientCommandSendResult.PendingLimitReached;
                if (_nextClientCommandSequence == long.MaxValue)
                    return OpenACAISonsClientCommandSendResult.SequenceExhausted;

                var pending = new OpenACAISonsClientCommand(++_nextClientCommandSequence);
                ClientCommands.Add(pending.Sequence, pending);
                request = pending;
                // Reserve before native sending. An exception cannot prove the command was not queued.
                SendClientFrame("command", _clientToken, commandSequence: pending.Sequence, message: command);
                if (pending.State.Progress == OpenACAISonsClientCommandProgress.Sending)
                    pending.Update(OpenACAISonsClientCommandProgress.Queued);
                return OpenACAISonsClientCommandSendResult.Queued;
            }
            catch (Exception error)
            {
                if (request != null && request.State.Progress == OpenACAISonsClientCommandProgress.Sending)
                    request.Update(OpenACAISonsClientCommandProgress.SendUncertain);
                ReportFailure(error);
                return request == null ? OpenACAISonsClientCommandSendResult.Failed
                    : OpenACAISonsClientCommandSendResult.SendUncertain;
            }
        }

        private static void ReceiveClientCommand(ChatEvent chat, OpenACAISonsNoticeProtocol.Frame frame, byte[] token)
        {
            if (chat.Targets != (int)GlobalTargets.OnlyServer)
                return;
            var source = chat.RaisedBy;
            if (!ServerPeers.TryGetValue(source.Pointer, out var peer) || !peer.Snapshot.SupportsCommands
                || peer.Snapshot.Session == null || source.ConnectionId != peer.Snapshot.ConnectionId
                || !OpenACAISonsNoticeProtocol.Matches(peer.Token, token)
                || ResolvePeer(peer.Snapshot, out _, out _) != OpenACAISonsNoticeSendResult.Queued
                || frame.CommandSequence <= peer.LastCommandSequence)
                return;

            // Advance before user callbacks. Old sequences never execute again during this peer lifetime.
            peer.LastCommandSequence = frame.CommandSequence;
            var outcome = OpenACAISonsServerChat.DispatchClientCommand(peer.Snapshot.Session, frame.Message);
            if (!HooksInstalled || !BoltNetwork.isRunning || !BoltNetwork.isServer
                || ResolvePeer(peer.Snapshot, out _, out var connection) != OpenACAISonsNoticeSendResult.Queued)
                return;
            SendFrame(connection, "command-result", peer.Token, commandSequence: frame.CommandSequence,
                outcome: outcome switch
                {
                    OpenACAISonsClientCommandOutcome.Succeeded => "succeeded",
                    OpenACAISonsClientCommandOutcome.InvalidArguments => "invalid",
                    OpenACAISonsClientCommandOutcome.Denied => "denied",
                    OpenACAISonsClientCommandOutcome.Unavailable => "unavailable",
                    OpenACAISonsClientCommandOutcome.NotFound => "unknown",
                    OpenACAISonsClientCommandOutcome.RejectedByPolicy => "rejected",
                    _ => "failed"
                });
        }

        private static void ReceiveCommandResult(OpenACAISonsNoticeProtocol.Frame frame)
        {
            if (!ClientSupportsCommands || !BoltNetwork.isConnected
                || !ClientCommands.Remove(frame.CommandSequence, out var pending))
                return;
            if (Environment.TickCount64 >= pending.ExpiresAt)
            {
                pending.Update(OpenACAISonsClientCommandProgress.TimedOut);
                return;
            }

            var outcome = frame.Outcome switch
            {
                "succeeded" => OpenACAISonsClientCommandOutcome.Succeeded,
                "invalid" => OpenACAISonsClientCommandOutcome.InvalidArguments,
                "denied" => OpenACAISonsClientCommandOutcome.Denied,
                "unavailable" => OpenACAISonsClientCommandOutcome.Unavailable,
                "unknown" => OpenACAISonsClientCommandOutcome.NotFound,
                "rejected" => OpenACAISonsClientCommandOutcome.RejectedByPolicy,
                _ => OpenACAISonsClientCommandOutcome.Failed
            };
            pending.Update(OpenACAISonsClientCommandProgress.ServerReported, outcome);
        }

        private static void ExpireClientCommands(long now)
        {
            if (ClientCommands.Count == 0)
                return;
            var expired = new List<long>();
            foreach (var pending in ClientCommands.Values)
            {
                if (now >= pending.ExpiresAt)
                {
                    pending.Update(OpenACAISonsClientCommandProgress.TimedOut);
                    expired.Add(pending.Sequence);
                }
            }
            foreach (var sequence in expired)
                ClientCommands.Remove(sequence);
        }

        private static void CompleteClientCommands(OpenACAISonsClientCommandProgress progress)
        {
            foreach (var pending in ClientCommands.Values)
                pending.Update(progress);
            ClientCommands.Clear();
        }
    }

    /// <summary>One connection-bound request. Retains no command text, token or native connection.</summary>
    public sealed class OpenACAISonsClientCommand
    {
        private OpenACAISonsClientCommandState _state;

        internal OpenACAISonsClientCommand(long sequence)
        {
            Sequence = sequence;
            CreatedAt = DateTimeOffset.UtcNow;
            ExpiresAt = Environment.TickCount64 + OpenACAISonsClientCommands.ResponseTimeoutSeconds * 1000L;
            _state = new OpenACAISonsClientCommandState(OpenACAISonsClientCommandProgress.Sending, null);
        }

        /// <summary>Correlation sequence within one connection, not a globally unique ID or credential.</summary>
        public long Sequence { get; }
        public DateTimeOffset CreatedAt { get; }
        /// <summary>Immutable managed observation safe to read off-thread; server outcomes are reports, not attestation.</summary>
        public OpenACAISonsClientCommandState State => Volatile.Read(ref _state);
        internal long ExpiresAt { get; }

        internal void Update(OpenACAISonsClientCommandProgress progress, OpenACAISonsClientCommandOutcome? outcome = null)
        {
            if (_state.IsTerminal)
                return;
            var state = new OpenACAISonsClientCommandState(progress, outcome);
            Volatile.Write(ref _state, state);
            if (state.IsTerminal)
                OpenACAISonsServerNotices.EnqueueClientCommandCompletion(this);
        }
    }

    public sealed class OpenACAISonsClientCommandState
    {
        internal OpenACAISonsClientCommandState(OpenACAISonsClientCommandProgress progress,
            OpenACAISonsClientCommandOutcome? outcome)
        {
            Progress = progress;
            Outcome = outcome;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public OpenACAISonsClientCommandProgress Progress { get; }
        /// <summary>Present only for ServerReported. Succeeded is the handler's claim, not proof of game state or durability.</summary>
        public OpenACAISonsClientCommandOutcome? Outcome { get; }
        public DateTimeOffset ObservedAt { get; }
        public bool IsTerminal => Progress is not (OpenACAISonsClientCommandProgress.Sending
            or OpenACAISonsClientCommandProgress.Queued or OpenACAISonsClientCommandProgress.SendUncertain);
    }

    public enum OpenACAISonsClientCommandSendResult
    {
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

    public enum OpenACAISonsClientCommandProgress
    {
        Sending,
        Queued,
        SendUncertain,
        ServerReported,
        TimedOut,
        ServerUnavailable,
        Unavailable
    }

    public enum OpenACAISonsClientCommandOutcome
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
