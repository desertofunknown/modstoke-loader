// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Sons
{
    /// <summary>Session-bound server commands received through game chat or negotiated client submissions.</summary>
    public static class OpenACAISonsCommands
    {
        public const int MaxNameLength = 64;
        /// <summary>This API's input limit in UTF-16 code units, including the slash and name.</summary>
        public const int MaxMessageLength = 4096;

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, Registration> Registrations = new(StringComparer.OrdinalIgnoreCase);
        private static int _reportedFailure;

        /// <summary>Hook registration status, not evidence of command execution or client delivery.</summary>
        public static bool HooksInstalled => OpenACAISonsServerChat.State.Status == OpenACAISonsServerChatHookStatus.Installed
            && OpenACAISonsServerSessions.HooksInstalled;

        /// <summary>
        /// Observes recognized commands after chat policy accepts them, including denied and failed attempts.
        /// Runs on the game thread after the optional reply attempt. Observer errors do not change the outcome.
        /// </summary>
        public static event Action<OpenACAISonsCommandCompletion> Completed;

        /// <summary>
        /// Registers an unprefixed ASCII name using letters, digits, dots, underscores or hyphens.
        /// Authorization is mandatory and synchronous. Dispose removes this registration only;
        /// a command already captured by the chat callback may still finish. Duplicate names throw.
        /// </summary>
        public static IDisposable Register(string name, Func<OpenACAISonsCommandContext, bool> authorize,
            Func<OpenACAISonsCommandContext, OpenACAISonsCommandResult> execute)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(authorize);
            ArgumentNullException.ThrowIfNull(execute);
            if (name.Length == 0 || name.Length > MaxNameLength)
                throw new ArgumentException("Command names must contain between 1 and 64 characters.", nameof(name));
            foreach (var character in name)
                if (!IsNameCharacter(character))
                    throw new ArgumentException("Command names may contain only ASCII letters, digits, dots, underscores and hyphens.", nameof(name));

            var registration = new Registration(name, authorize, execute);
            lock (SyncRoot)
            {
                if (!Registrations.TryAdd(name, registration))
                    throw new InvalidOperationException("That command name is already registered.");
            }
            return registration;
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
                return Registrations.TryGetValue(message.Substring(1, end - 1), out var registration) ? registration : null;
        }

        internal static OpenACAISonsCommandStatus Dispatch(OpenACAISonsChatRequest request, Registration registration)
        {
            OpenACAISonsServerIdentitySnapshot identity = null;
            OpenACAISonsIdentityReadStatus? identityStatus = null;
            OpenACAISonsCommandResult result;
            try
            {
                result = Execute(request, registration, out identity, out identityStatus);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                result = new OpenACAISonsCommandResult(OpenACAISonsCommandStatus.Failed, "The command could not be completed.");
            }

            OpenACAISonsNoticeSendResult? replyStatus = null;
            OpenACAISonsNoticeDelivery replyDelivery = null;
            if (result.Reply != null)
            {
                replyStatus = result.TrackReply
                    ? OpenACAISonsServerNotices.SendTrackedToSession(request.SourceSession, result.Reply, out replyDelivery)
                    : OpenACAISonsServerNotices.SendToSession(request.SourceSession, result.Reply);
            }

            var handlers = Completed;
            if (handlers == null)
                return result.Status;
            var completion = new OpenACAISonsCommandCompletion(registration.Name, request.SourceSession,
                identity, identityStatus, result.Status, replyStatus, request.ReceivedAt, replyDelivery);
            foreach (Action<OpenACAISonsCommandCompletion> handler in handlers.GetInvocationList())
            {
                try { handler(completion); }
                catch (Exception error) { ReportFailure(error); }
            }
            return result.Status;
        }

        private static OpenACAISonsCommandResult Execute(OpenACAISonsChatRequest request, Registration registration,
            out OpenACAISonsServerIdentitySnapshot identity, out OpenACAISonsIdentityReadStatus? identityStatus)
        {
            identity = null;
            identityStatus = null;
            if (!IsInputValid(request.Message))
                return new OpenACAISonsCommandResult(OpenACAISonsCommandStatus.InvalidArguments, "Command input is invalid.");
            if (!HooksInstalled)
                return Unavailable();

            var read = OpenACAISonsServerSessions.TryReadIdentity(request.SourceSession, out identity, out var status);
            identityStatus = status;
            if (!read)
                return Unavailable();

            var end = FindNameEnd(request.Message);
            var arguments = end == request.Message.Length ? string.Empty : request.Message.Substring(end + 1);
            var context = new OpenACAISonsCommandContext(registration.Name, arguments, identity, request.ReceivedAt);
            if (!registration.Authorize(context))
                return new OpenACAISonsCommandResult(OpenACAISonsCommandStatus.Denied, "You are not allowed to use this command.");

            // Authorization can call back into game code. Do not act on an identity or role it changed.
            read = OpenACAISonsServerSessions.TryReadIdentity(request.SourceSession, out var current, out status);
            identityStatus = status;
            if (!HooksInstalled || !read || !SameIdentity(identity, current))
                return Unavailable();

            return registration.Execute(context)
                ?? new OpenACAISonsCommandResult(OpenACAISonsCommandStatus.Failed, "The command could not be completed.");
        }

        private static OpenACAISonsCommandResult Unavailable() =>
            new(OpenACAISonsCommandStatus.Unavailable, "The command is not available for this session.");

        private static bool SameIdentity(OpenACAISonsServerIdentitySnapshot first, OpenACAISonsServerIdentitySnapshot second) =>
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server command callback failed ("
                    + error.GetType().FullName + "). Further command callback failures will not be logged.");
            }
            catch (Exception) { }
        }

        internal sealed class Registration : IDisposable
        {
            internal Registration(string name, Func<OpenACAISonsCommandContext, bool> authorize,
                Func<OpenACAISonsCommandContext, OpenACAISonsCommandResult> execute)
            {
                Name = name;
                Authorize = authorize;
                Execute = execute;
            }

            internal string Name { get; }
            internal Func<OpenACAISonsCommandContext, bool> Authorize { get; }
            internal Func<OpenACAISonsCommandContext, OpenACAISonsCommandResult> Execute { get; }

            public void Dispose()
            {
                lock (SyncRoot)
                    if (Registrations.TryGetValue(Name, out var current) && ReferenceEquals(current, this))
                        Registrations.Remove(Name);
            }
        }
    }

    /// <summary>Managed input passed only after the source session and game identity were resolved.</summary>
    public sealed class OpenACAISonsCommandContext
    {
        internal OpenACAISonsCommandContext(string name, string arguments,
            OpenACAISonsServerIdentitySnapshot identity, DateTimeOffset receivedAt)
        {
            Name = name;
            Arguments = arguments;
            Identity = identity;
            ReceivedAt = receivedAt;
        }

        public string Name { get; }
        /// <summary>Untrusted text after the first space or tab. No trimming, tokenization or quote interpretation.</summary>
        public string Arguments { get; }
        public OpenACAISonsServerSession Session => Identity.Session;
        /// <summary>Game observations, not independent authentication or a permission grant.</summary>
        public OpenACAISonsServerIdentitySnapshot Identity { get; }
        public DateTimeOffset ReceivedAt { get; }
    }

    public enum OpenACAISonsCommandStatus
    {
        Succeeded,
        InvalidArguments,
        Denied,
        Unavailable,
        Failed
    }

    /// <summary>A handler's outcome and optional single-line notice reply.</summary>
    public sealed class OpenACAISonsCommandResult
    {
        public OpenACAISonsCommandResult(OpenACAISonsCommandStatus status, string reply = null)
            : this(status, reply, false)
        {
        }

        /// <summary>Opts into negotiated notice receipts without an untracked fallback. Null reply still sends nothing.</summary>
        public OpenACAISonsCommandResult(OpenACAISonsCommandStatus status, string reply, bool trackReply)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            Status = status;
            Reply = reply;
            TrackReply = trackReply;
        }

        public OpenACAISonsCommandStatus Status { get; }
        /// <summary>Null skips the reply. Notice length/content rules apply; a rejected reply does not undo the action.</summary>
        public string Reply { get; }
        /// <summary>Requests reply tracking only; it grants no permission and does not change the command outcome.</summary>
        public bool TrackReply { get; }
    }

    /// <summary>One local processing outcome, not a transaction or proof of client delivery.</summary>
    public sealed class OpenACAISonsCommandCompletion
    {
        internal OpenACAISonsCommandCompletion(string name, OpenACAISonsServerSession session,
            OpenACAISonsServerIdentitySnapshot identity, OpenACAISonsIdentityReadStatus? identityReadStatus,
            OpenACAISonsCommandStatus status, OpenACAISonsNoticeSendResult? replyStatus, DateTimeOffset receivedAt,
            OpenACAISonsNoticeDelivery replyDelivery)
        {
            Name = name;
            Session = session;
            Identity = identity;
            IdentityReadStatus = identityReadStatus;
            Status = status;
            ReplyStatus = replyStatus;
            ReplyDelivery = replyDelivery;
            ReceivedAt = receivedAt;
        }

        public string Name { get; }
        /// <summary>Null when the source could not be associated with a tracked remote session.</summary>
        public OpenACAISonsServerSession Session { get; }
        /// <summary>The first identity read, or null if unavailable. It may no longer be current.</summary>
        public OpenACAISonsServerIdentitySnapshot Identity { get; }
        /// <summary>Last identity read status, or null when input or hook checks prevented a read.</summary>
        public OpenACAISonsIdentityReadStatus? IdentityReadStatus { get; }
        public OpenACAISonsCommandStatus Status { get; }
        /// <summary>Null when no reply was requested. Queued does not mean delivered or displayed.</summary>
        public OpenACAISonsNoticeSendResult? ReplyStatus { get; }
        /// <summary>
        /// Retain to observe an opted-in reply, including an uncertain send. Null for untracked or rejected sends.
        /// Its progress may change after Completed; client reports never amend the command's Status.
        /// </summary>
        public OpenACAISonsNoticeDelivery ReplyDelivery { get; }
        public DateTimeOffset ReceivedAt { get; }
    }
}
