// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Session-bound server commands received through game chat or negotiated client submissions.</summary>
    public static partial class Commands
    {
        public const int MaxNameLength = 64;
        /// <summary>This API's input limit in UTF-16 code units, including the slash and name.</summary>
        public const int MaxMessageLength = 4096;
        private const int SessionCommandBurst = 8;
        private const int SessionCommandsPerSecond = 4;
        private const int GlobalCommandBurst = 64;
        private const int GlobalCommandsPerSecond = 32;

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, Registration> Registrations = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConditionalWeakTable<ServerSession, CommandBudget> SessionBudgets = new();
        private static double _globalTokens = GlobalCommandBurst;
        private static long _globalRefillAt;
        private static int _reportedFailure;

        /// <summary>Hook registration status, not evidence of command execution or client delivery.</summary>
        public static bool HooksInstalled => ServerChat.State.Status == ServerChatHookStatus.Installed
            && ServerSessions.HooksInstalled;

        /// <summary>
        /// Observes recognized commands after chat policy accepts them, including denied and failed attempts.
        /// Runs on the game thread after the optional reply attempt. Observer errors do not change the outcome.
        /// </summary>
        public static event Action<CommandCompletion> Completed;

        /// <summary>
        /// Registers an unprefixed ASCII name using letters, digits, dots, underscores or hyphens.
        /// Authorization is mandatory and synchronous. Dispose removes this registration only;
        /// a command already captured by the chat callback may still finish. Duplicate names throw.
        /// </summary>
        public static IDisposable Register(string name, Func<CommandContext, bool> authorize,
            Func<CommandContext, CommandResult> execute)
            => Register(name, authorize, execute, CommandReplyChannel.NegotiatedNotice);

        /// <summary>
        /// Registers a command with a fixed reply channel, including denial and failure replies.
        /// GameChat needs no client mod, but requires the incoming pre-forward routing hook to execute.
        /// GameChat cannot acknowledge display or track replies; TrackReply rejects that reply without sending.
        /// </summary>
        public static IDisposable Register(string name, Func<CommandContext, bool> authorize,
            Func<CommandContext, CommandResult> execute,
            CommandReplyChannel replyChannel)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(authorize);
            ArgumentNullException.ThrowIfNull(execute);
            if (!Enum.IsDefined(replyChannel))
                throw new ArgumentOutOfRangeException(nameof(replyChannel));
            ValidateName(name);

            // These consume typed input, so they must not hide an already registered compatibility command.
            if (replyChannel is CommandReplyChannel.GameChat or CommandReplyChannel.InlineOnly)
                CheckLegacyName(name);

            var registration = new Registration(name, authorize, execute, replyChannel);
            lock (SyncRoot)
            {
                if (!Registrations.TryAdd(name, registration))
                    throw new InvalidOperationException("That command name is already registered.");
            }
            return registration;
        }

        private static void ValidateName(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.Length == 0 || name.Length > MaxNameLength)
                throw new ArgumentException("Command names must contain between 1 and 64 characters.", nameof(name));
            foreach (var character in name)
                if (!IsNameCharacter(character))
                    throw new ArgumentException("Command names may contain only ASCII letters, digits, dots, underscores and hyphens.", nameof(name));
        }

        private static void CheckLegacyName(string name)
        {
            var legacy = SonsSdk.Networking.ServerCommands.Commands;
            if (legacy != null)
                foreach (var legacyName in legacy.Keys)
                    if (string.Equals(name, legacyName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("That command name is already registered by a compatibility mod.");
        }

        internal static bool HasRegistrations
        {
            get { lock (SyncRoot) return Registrations.Count != 0; }
        }

        internal static Registration FindRegistration(string message)
        {
            var end = FindNameEnd(message);
            if (end < 0)
                return null;
            lock (SyncRoot)
                return Registrations.GetAlternateLookup<ReadOnlySpan<char>>()
                    .TryGetValue(message.AsSpan(1, end - 1), out var registration) ? registration : null;
        }

        // Called only from the server chat game thread, before policy and mod callbacks.
        internal static bool TryReserveCommand(ServerSession session)
        {
            if (session == null || !session.IsCurrent)
                return false;
            var now = Stopwatch.GetTimestamp();
            Refill(ref _globalTokens, ref _globalRefillAt, GlobalCommandBurst, GlobalCommandsPerSecond, now);
            var budget = SessionBudgets.GetValue(session, static _ => new CommandBudget());
            Refill(ref budget.Tokens, ref budget.RefillAt, SessionCommandBurst, SessionCommandsPerSecond, now);
            if (_globalTokens < 1 || budget.Tokens < 1)
                return false;
            _globalTokens--;
            budget.Tokens--;
            return true;
        }

        private static void Refill(ref double tokens, ref long refillAt, int burst, int perSecond, long now)
        {
            if (refillAt != 0 && now > refillAt)
                tokens = Math.Min(burst, tokens + (now - refillAt) * (perSecond / (double)Stopwatch.Frequency));
            refillAt = now;
        }

        internal static CommandStatus Dispatch(ChatRequest request, Registration registration)
            => Dispatch(request, registration, out _);

        internal static CommandStatus Dispatch(ChatRequest request, Registration registration,
            out string reportedReply)
        {
            ServerIdentitySnapshot identity = null;
            IdentityReadStatus? identityStatus = null;
            CommandResult result;
            try
            {
                result = Execute(request, registration, out identity, out identityStatus);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                result = new CommandResult(CommandStatus.Failed, "The command could not be completed.");
            }

            reportedReply = result.Reply;
            NoticeSendResult? replyStatus = null;
            NoticeDelivery replyDelivery = null;
            ChatSendResult? chatReplyStatus = null;
            // InlineOnly replies travel only in the submitting client's command result.
            if (result.Reply != null && registration.ReplyChannel != CommandReplyChannel.InlineOnly)
            {
                if (registration.ReplyChannel == CommandReplyChannel.GameChat)
                    chatReplyStatus = result.TrackReply
                        ? ChatSendResult.UnsupportedTracking
                        : ServerChat.SendToSession(request.SourceSession, result.Reply);
                else
                    replyStatus = result.TrackReply
                        ? ServerNotices.SendTrackedToSession(request.SourceSession, result.Reply, out replyDelivery)
                        : ServerNotices.SendToSession(request.SourceSession, result.Reply);
            }

            var handlers = Completed;
            if (handlers == null)
                return result.Status;
            var completion = new CommandCompletion(registration.Name, request.SourceSession,
                identity, identityStatus, result.Status, replyStatus, request.ReceivedAt, replyDelivery, chatReplyStatus);
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try { handler(completion); }
                catch (Exception error) { ReportFailure(error); }
            }
            return result.Status;
        }

        private static CommandResult Execute(ChatRequest request, Registration registration,
            out ServerIdentitySnapshot identity, out IdentityReadStatus? identityStatus)
        {
            identity = null;
            identityStatus = null;
            // InlineOnly answers only a client that can receive the reply, so typed chat never reaches it.
            if (registration.ReplyChannel == CommandReplyChannel.InlineOnly
                && (request.Origin != ChatRequestOrigin.ClientCommand || !request.InlineReplies))
                return Unavailable();
            if (!IsInputValid(request.Message))
                return new CommandResult(CommandStatus.InvalidArguments, "Command input is invalid.");
            if (!CanExecute(registration))
                return Unavailable();

            var read = ServerSessions.TryReadIdentity(request.SourceSession, out identity, out var status);
            identityStatus = status;
            if (!read)
                return Unavailable();

            var end = FindNameEnd(request.Message);
            var arguments = end == request.Message.Length ? string.Empty : request.Message.Substring(end + 1);
            var context = new CommandContext(registration.Name, arguments, identity, request.ReceivedAt, request.Origin);
            if (!registration.Authorize(context))
                return new CommandResult(CommandStatus.Denied, "You are not allowed to use this command.");

            // Authorization can call back into game code. Do not act on an identity or role it changed.
            read = ServerSessions.TryReadIdentity(request.SourceSession, out var current, out status);
            identityStatus = status;
            if (!CanExecute(registration) || !read || !SameIdentity(identity, current))
                return Unavailable();

            return registration.Execute(context)
                ?? new CommandResult(CommandStatus.Failed, "The command could not be completed.");
        }

        private static CommandResult Unavailable() =>
            new(CommandStatus.Unavailable, "The command is not available for this session.");

        private static bool CanExecute(Registration registration) => HooksInstalled
            && (registration.ReplyChannel != CommandReplyChannel.GameChat
                || ServerChat.State.RoutingStatus == ServerChatHookStatus.Installed);

        private static bool SameIdentity(ServerIdentitySnapshot first, ServerIdentitySnapshot second) =>
            ReferenceEquals(first.Session, second.Session) && first.GameSteamId == second.GameSteamId
            && first.IdentitySource == second.IdentitySource && first.RegisteredSteamId == second.RegisteredSteamId
            && first.RoleStatus == second.RoleStatus && first.StoredRole == second.StoredRole;

        private static int FindNameEnd(string message)
        {
            if (string.IsNullOrEmpty(message) || message[0] != '/' || message.Length == 1)
                return -1;
            var end = 1;
            while (end < message.Length && message[end] != ' ' && message[end] != '\t')
            {
                if (end > MaxNameLength || !IsNameCharacter(message[end]))
                    return -1;
                end++;
            }
            return end == 1 ? -1 : end;
        }

        private static bool IsNameCharacter(char value) => value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';

        internal static bool IsCommandTextValid(string message) => FindNameEnd(message) >= 0 && IsInputValid(message);

        internal static string ReadName(string message)
        {
            var end = FindNameEnd(message);
            return end < 0 ? null : message.Substring(1, end - 1);
        }

        private static bool IsInputValid(string message)
        {
            if (message.Length > MaxMessageLength)
                return false;
            for (var index = 0; index < message.Length; index++)
            {
                var character = message[index];
                if ((char.IsControl(character) && character != '\t') || character is '\u2028' or '\u2029')
                    return false;
                if (char.IsHighSurrogate(character))
                {
                    if (++index == message.Length || !char.IsLowSurrogate(message[index]))
                        return false;
                }
                else if (char.IsLowSurrogate(character))
                    return false;
            }
            return true;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server command callback failed ("
                    + error.GetType().FullName + "). Further command callback failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class CommandBudget
        {
            internal double Tokens = SessionCommandBurst;
            internal long RefillAt;
        }

        internal sealed class Registration : IDisposable
        {
            internal Registration(string name, Func<CommandContext, bool> authorize,
                Func<CommandContext, CommandResult> execute,
                CommandReplyChannel replyChannel)
            {
                Name = name;
                Authorize = authorize;
                Execute = execute;
                ReplyChannel = replyChannel;
            }

            internal string Name { get; }
            internal Func<CommandContext, bool> Authorize { get; }
            internal Func<CommandContext, CommandResult> Execute { get; }
            internal CommandReplyChannel ReplyChannel { get; }

            public void Dispose()
            {
                lock (SyncRoot)
                    if (Registrations.TryGetValue(Name, out var current) && ReferenceEquals(current, this))
                        Registrations.Remove(Name);
            }
        }
    }

    /// <summary>Managed input passed only after the source session and game identity were resolved.</summary>
    public sealed class CommandContext
    {
        internal CommandContext(string name, string arguments,
            ServerIdentitySnapshot identity, DateTimeOffset receivedAt, ChatRequestOrigin origin)
        {
            Name = name;
            Arguments = arguments;
            Identity = identity;
            ReceivedAt = receivedAt;
            Origin = origin;
        }

        public string Name { get; }
        /// <summary>Typed game chat or a negotiated client submission. InlineOnly commands only see ClientCommand.</summary>
        public ChatRequestOrigin Origin { get; }
        /// <summary>Untrusted text after the first space or tab. No trimming, tokenization or quote interpretation.</summary>
        public string Arguments { get; }
        public ServerSession Session => Identity.Session;
        /// <summary>Game observations, not independent authentication or a permission grant.</summary>
        public ServerIdentitySnapshot Identity { get; }
        public DateTimeOffset ReceivedAt { get; }
    }

    public enum CommandStatus
    {
        Succeeded,
        InvalidArguments,
        Denied,
        Unavailable,
        Failed
    }

    public enum CommandReplyChannel
    {
        /// <summary>Requires a Modstoke client with negotiated notice support.</summary>
        NegotiatedNotice,
        /// <summary>Native game chat to the exact session, with no receipt or client-mod requirement.</summary>
        GameChat,
        /// <summary>
        /// Only the submitting client's command result carries the reply; nothing is shown as a notice or
        /// chat line. Typed game-chat invocations, and clients that did not negotiate command replies, are
        /// refused as Unavailable before input checks, authorization or execution. TrackReply has no effect.
        /// </summary>
        InlineOnly
    }

    /// <summary>A handler's outcome and optional single-line reply on its registration's channel.</summary>
    public sealed class CommandResult
    {
        public CommandResult(CommandStatus status, string reply = null)
            : this(status, reply, false)
        {
        }

        /// <summary>
        /// Opts into negotiated notice receipts without an untracked fallback. GameChat replies are rejected
        /// with UnsupportedTracking when requested; the command outcome is unchanged. Null reply sends nothing.
        /// </summary>
        public CommandResult(CommandStatus status, string reply, bool trackReply)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            Status = status;
            Reply = reply;
            TrackReply = trackReply;
        }

        public CommandStatus Status { get; }
        /// <summary>Null skips the reply. The registered channel's content limits apply; rejection does not undo the action.</summary>
        public string Reply { get; }
        /// <summary>Requests negotiated notice tracking; GameChat rejects the reply without sending or changing the action outcome.</summary>
        public bool TrackReply { get; }
    }

    /// <summary>One local processing outcome, not a transaction or proof of client delivery.</summary>
    public sealed class CommandCompletion
    {
        internal CommandCompletion(string name, ServerSession session,
            ServerIdentitySnapshot identity, global::Modstoke.Sons.IdentityReadStatus? identityReadStatus,
            CommandStatus status, NoticeSendResult? replyStatus, DateTimeOffset receivedAt,
            NoticeDelivery replyDelivery, ChatSendResult? chatReplyStatus)
        {
            Name = name;
            Session = session;
            Identity = identity;
            IdentityReadStatus = identityReadStatus;
            Status = status;
            ReplyStatus = replyStatus;
            ReplyDelivery = replyDelivery;
            ChatReplyStatus = chatReplyStatus;
            ReceivedAt = receivedAt;
        }

        public string Name { get; }
        /// <summary>Null when the source could not be associated with a tracked remote session.</summary>
        public ServerSession Session { get; }
        /// <summary>The first identity read, or null if unavailable. It may no longer be current.</summary>
        public ServerIdentitySnapshot Identity { get; }
        /// <summary>Last identity read status, or null when input or hook checks prevented a read.</summary>
        public global::Modstoke.Sons.IdentityReadStatus? IdentityReadStatus { get; }
        public CommandStatus Status { get; }
        /// <summary>Negotiated notice result; null for GameChat or no reply. Queued does not mean delivered or displayed.</summary>
        public NoticeSendResult? ReplyStatus { get; }
        /// <summary>
        /// Retain to observe an opted-in notice reply, including an uncertain send. Null for GameChat, untracked or rejected sends.
        /// Its progress may change after Completed; client reports never amend the command's Status.
        /// </summary>
        public NoticeDelivery ReplyDelivery { get; }
        /// <summary>
        /// Native game-chat reply result, or null for negotiated notices or no reply. UnsupportedTracking means
        /// the reply was not sent. Queued is not proof of delivery or display; the command Status is independent.
        /// </summary>
        public ChatSendResult? ChatReplyStatus { get; }
        public DateTimeOffset ReceivedAt { get; }
    }
}
