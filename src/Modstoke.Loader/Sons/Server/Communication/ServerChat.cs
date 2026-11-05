// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Server-side policy for game chat and negotiated client command submissions.</summary>
    public static partial class ServerChat
    {
        public const string ServiceKey = "openacai.sons.serverChat";

        private static ServerChatState _state = new(
            ServerChatHookStatus.NotAttempted, "The chat hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;
        private static int _reportedForwardFailure;

        /// <summary>
        /// Runs synchronously before the owned SDK command handler. When routing is installed,
        /// remote game chat is checked before forwarding; local chat uses the native callback boundary.
        /// Rejecting a request or throwing stops the remaining handlers and the intercepted operation.
        /// </summary>
        public static event Action<ChatRequest> Receiving;

        // Host event observer: sees game chat that passed policy and is being forwarded.
        internal static Action<ChatRequest> Forwarded;

        public static ServerChatState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(ServerChatHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server chat policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(ServerChatHookStatus.Installing, "Registering the server chat hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { typeof(ChatEvent) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(ChatEvent)");

                var prefix = new HarmonyMethod(typeof(ServerChat), nameof(BeforeChat))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-chat");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(ServerChatHookStatus.Failed, "Chat hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server chat policy hook failed: " + error);
                return;
            }

            PublishState(ServerChatHookStatus.Installed,
                "Chat hook registration succeeded. This is not evidence of a received or blocked message.");
            InstallRoutingHook(addStartupWarning);
        }

        private static bool BeforeChat(ChatEvent __0)
        {
            // Incoming events were already checked before routing. Native rebroadcasts retain
            // their null transport source; never infer a new source from the claimed Sender.
            try
            {
                if (State.RoutingStatus == ServerChatHookStatus.Installed && __0.RaisedBy != null)
                    return true;
            }
            catch (Exception error)
            {
                ReportDispatchFailure("Reading chat callback source failed (" + error.GetType().FullName + ").");
                return false;
            }
            return ApplyGameChat(__0);
        }

        private static void InstallRoutingHook(Action<string> addStartupWarning)
        {
            PublishRoutingState(ServerChatHookStatus.Installing, "Registering the incoming chat routing hook.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.Chat);
            }
            catch (Exception error)
            {
                PublishRoutingState(ServerChatHookStatus.Failed,
                    "Chat routing registration failed; callback-only policy remains, without a pre-forwarding guarantee.");
                addStartupWarning?.Invoke("Server chat routing hook failed; native patch state is uncertain: " + error);
                return;
            }

            PublishRoutingState(ServerChatHookStatus.Installed,
                "Incoming chat routing hook registered. This is not evidence of blocked delivery to peers.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source)
        {
            if (State.RoutingStatus != ServerChatHookStatus.Installed
                || Receiving == null && !Commands.HasRegistrations && Forwarded == null)
                return DispatchVerdict.Forward;

            var chat = source.TryCast<ChatEvent>();
            if (chat == null)
                return DispatchVerdict.Forward;

            try
            {
                if (!BoltNetwork.isServer || chat.RaisedBy == null)
                    return DispatchVerdict.Forward;

                // Negotiated notices, receipts and client commands have their own dispatcher
                // and lifetime. They must not also be interpreted as ordinary game chat.
                if (chat.Message?.StartsWith(NoticeProtocol.Prefix, StringComparison.Ordinal) == true)
                    return DispatchVerdict.Forward;

                return ApplyGameChat(chat) ? DispatchVerdict.Forward : DispatchVerdict.Reject;
            }
            catch (Exception error)
            {
                // With only the host observer listening, chat is delivered as it was without one.
                if (Receiving == null && !Commands.HasRegistrations)
                    return DispatchVerdict.Forward;
                ReportDispatchFailure("Reading incoming chat routing context failed (" + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportDispatchFailure("Releasing rejected chat storage failed (" + error.GetType().FullName + ").");

        private static bool ApplyGameChat(ChatEvent source)
        {
            var handlers = Receiving;
            var forwarded = Forwarded;
            if (handlers == null && !Commands.HasRegistrations)
            {
                ObserveOnly(source, forwarded);
                return true;
            }

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The chat callback ran outside the loader's game thread.");
                return false;
            }

            ChatRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                var message = source.Message;
                var command = Commands.FindRegistration(message);
                if (handlers == null && command == null)
                {
                    ObserveOnly(source, forwarded);
                    return true;
                }

                request = ReadRequest(source, message);
                if (command != null && !Commands.TryReserveCommand(request.SourceSession))
                    return false;
                if (!ApplyPolicy(request, handlers))
                    return false;

                if (command != null)
                {
                    Commands.Dispatch(request, command);
                    return false;
                }

                NotifyForwarded(forwarded, request);
                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading chat context or running a policy handler failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        internal static ClientCommandOutcome DispatchClientCommand(
            ServerSession session, string message, bool inlineReplies, out string reportedReply, out bool rateLimited)
        {
            reportedReply = null;
            rateLimited = false;
            if (!Commands.HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId)
                return ClientCommandOutcome.Unavailable;

            ChatRequest request = null;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return ClientCommandOutcome.Unavailable;
                var connection = ServerSessions.ResolveCurrent(session);
                if (connection == null)
                    return ClientCommandOutcome.Unavailable;

                if (!Commands.TryReserveCommand(session))
                {
                    rateLimited = true;
                    return ClientCommandOutcome.Unavailable;
                }
                var handlers = Receiving;
                var command = Commands.FindRegistration(message);
                ulong? registeredSteamId = null;
                if (GameServerManager.GetRegisteredClient(connection, out var steamId) && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;
                request = new ChatRequest(message, 0, connection.ConnectionId, registeredSteamId,
                    session, ChatRequestOrigin.ClientCommand) { InlineReplies = inlineReplies };
                if (!ApplyPolicy(request, handlers))
                    return ClientCommandOutcome.RejectedByPolicy;
                if (command == null)
                    return ClientCommandOutcome.NotFound;

                return Commands.Dispatch(request, command, out reportedReply) switch
                {
                    CommandStatus.Succeeded => ClientCommandOutcome.Succeeded,
                    CommandStatus.InvalidArguments => ClientCommandOutcome.InvalidArguments,
                    CommandStatus.Denied => ClientCommandOutcome.Denied,
                    CommandStatus.Unavailable => ClientCommandOutcome.Unavailable,
                    _ => ClientCommandOutcome.Failed
                };
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Processing a client command failed (" + error.GetType().FullName + ").");
                return ClientCommandOutcome.Failed;
            }
            finally
            {
                request?.Close();
            }
        }

        private static bool ApplyPolicy(ChatRequest request, Action<ChatRequest> handlers)
        {
            if (handlers != null)
            {
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }
            }
            return true;
        }

        private static ChatRequest ReadRequest(ChatEvent source, string message)
        {
            var connection = source.RaisedBy;
            uint? connectionId = connection == null ? null : connection.ConnectionId;
            ulong? registeredSteamId = null;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
            {
                if (steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;
            }

            return new ChatRequest(message, source.Sender.PackedValue,
                connectionId, registeredSteamId, ServerSessions.ResolveSource(connection));
        }

        // Only the host observer is listening, so nothing here may change whether the message is delivered.
        private static void ObserveOnly(ChatEvent source, Action<ChatRequest> forwarded)
        {
            if (forwarded == null || Environment.CurrentManagedThreadId != _ownerThreadId)
                return;
            ChatRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return;
                request = ReadRequest(source, source.Message);
                NotifyForwarded(forwarded, request);
            }
            catch (Exception error)
            {
                ReportForwardFailure(error);
            }
            finally
            {
                request?.Close();
            }
        }

        private static void NotifyForwarded(Action<ChatRequest> forwarded, ChatRequest request)
        {
            if (forwarded == null)
                return;
            try
            {
                forwarded(request);
            }
            catch (Exception error)
            {
                ReportForwardFailure(error);
            }
        }

        private static void ReportForwardFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedForwardFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Observing forwarded chat failed; the message was still delivered: "
                    + error.GetType().FullName + ": " + error.Message);
            }
            catch (Exception) { }
        }

        private static void PublishState(ServerChatHookStatus status, string detail)
        {
            var state = new ServerChatState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void PublishRoutingState(ServerChatHookStatus status, string detail)
        {
            var previous = State;
            var state = new ServerChatState(previous.Status, previous.Detail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            // A broken policy must not flood the log with player-triggered exceptions.
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Server chat policy rejected a message: " + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ServerChatHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerChatState
    {
        internal ServerChatState(ServerChatHookStatus status, string detail)
            : this(status, detail, ServerChatHookStatus.NotAttempted,
                "Incoming chat routing has not been installed.")
        {
        }

        internal ServerChatState(ServerChatHookStatus status, string detail,
            ServerChatHookStatus routingStatus, string routingDetail)
        {
            Status = status;
            Detail = detail;
            RoutingStatus = routingStatus;
            RoutingDetail = routingDetail;
        }

        public ServerChatHookStatus Status { get; }
        public string Detail { get; }

        /// <summary>Incoming game-chat routing registration, distinct from callback policy and live enforcement.</summary>
        public ServerChatHookStatus RoutingStatus { get; }

        /// <summary>Details of the pre-forwarding hook or its installation failure.</summary>
        public string RoutingDetail { get; }
    }

    /// <summary>A managed snapshot. It does not retain the native event or connection.</summary>
    public sealed class ChatRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal ChatRequest(string message, ulong claimedSenderNetworkId,
            uint? sourceConnectionId, ulong? registeredSteamId, ServerSession sourceSession,
            ChatRequestOrigin origin = ChatRequestOrigin.GameChat)
        {
            Message = message;
            ClaimedSenderNetworkId = claimedSenderNetworkId;
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            ReceivedAt = DateTimeOffset.UtcNow;
            Origin = origin;
        }

        public string Message { get; }

        /// <summary>How the request entered policy; neither origin grants permission.</summary>
        public ChatRequestOrigin Origin { get; }

        // The submitting client negotiated inline command replies; required for InlineOnly commands.
        internal bool InlineReplies { get; init; }

        /// <summary>Payload supplied by the sender, not an authenticated player identity.</summary>
        public ulong ClaimedSenderNetworkId { get; }

        /// <summary>Transport connection ID, or null for a server-local event. IDs can be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }

        /// <summary>
        /// Connection-lifetime handle, or null for local events, unavailable tracking or a departed source.
        /// Its live metadata may change; this request's RegisteredSteamId is an event-time copy.
        /// </summary>
        public ServerSession SourceSession { get; }

        /// <summary>
        /// Steam ID in the game's registration table for the source connection, or null when absent.
        /// This is not a separate Steam authentication or permission verdict.
        /// </summary>
        public ulong? RegisteredSteamId { get; }

        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Stops the intercepted routing, callback or client-command operation without undoing prior effects.
        /// Returns false outside synchronous dispatch or on another thread.
        /// Rejection cannot be undone and is not an acknowledgement from any client.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;

            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close()
        {
            _acceptingDecisions = false;
        }
    }

    public enum ChatRequestOrigin
    {
        GameChat,
        ClientCommand
    }
}
