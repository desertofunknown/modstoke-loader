// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Server-side policy for game chat and negotiated client command submissions.</summary>
    public static class OpenACAISonsServerChat
    {
        public const string ServiceKey = "openacai.sons.serverChat";

        private static OpenACAISonsServerChatState _state = new(
            OpenACAISonsServerChatHookStatus.NotAttempted, "The chat hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the native server callback and the owned SDK command handler.
        /// Rejecting a request or throwing stops the remaining handlers and that callback.
        /// </summary>
        public static event Action<OpenACAISonsChatRequest> Receiving;

        public static OpenACAISonsServerChatState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsServerChatHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server chat policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsServerChatHookStatus.Installing, "Registering the server chat hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { typeof(ChatEvent) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(ChatEvent)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerChat), nameof(BeforeChat))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-chat");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsServerChatHookStatus.Failed, "Chat hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server chat policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsServerChatHookStatus.Installed,
                "Chat hook registration succeeded. This is not evidence of a received or blocked message.");
        }

        private static bool BeforeChat(ChatEvent __0)
        {
            var handlers = Receiving;
            if (handlers == null && !OpenACAISonsCommands.HasRegistrations)
                return true;

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The chat callback ran outside the loader's game thread.");
                return false;
            }

            OpenACAISonsChatRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                var message = __0.Message;
                var command = OpenACAISonsCommands.FindRegistration(message);
                if (handlers == null && command == null)
                    return true;

                var connection = __0.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }

                request = new OpenACAISonsChatRequest(message, __0.Sender.PackedValue,
                    connectionId, registeredSteamId, OpenACAISonsServerSessions.ResolveSource(connection));
                if (!ApplyPolicy(request, handlers))
                    return false;

                if (command != null)
                {
                    OpenACAISonsCommands.Dispatch(request, command);
                    return false;
                }

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

        internal static OpenACAISonsClientCommandOutcome DispatchClientCommand(
            OpenACAISonsServerSession session, string message)
        {
            if (!OpenACAISonsCommands.HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsClientCommandOutcome.Unavailable;

            OpenACAISonsChatRequest request = null;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsClientCommandOutcome.Unavailable;
                var connection = OpenACAISonsServerSessions.ResolveCurrent(session);
                if (connection == null)
                    return OpenACAISonsClientCommandOutcome.Unavailable;

                var handlers = Receiving;
                var command = OpenACAISonsCommands.FindRegistration(message);
                ulong? registeredSteamId = null;
                if (GameServerManager.GetRegisteredClient(connection, out var steamId) && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;
                request = new OpenACAISonsChatRequest(message, 0, connection.ConnectionId, registeredSteamId,
                    session, OpenACAISonsChatRequestOrigin.ClientCommand);
                if (!ApplyPolicy(request, handlers))
                    return OpenACAISonsClientCommandOutcome.RejectedByPolicy;
                if (command == null)
                    return OpenACAISonsClientCommandOutcome.NotFound;

                return OpenACAISonsCommands.Dispatch(request, command) switch
                {
                    OpenACAISonsCommandStatus.Succeeded => OpenACAISonsClientCommandOutcome.Succeeded,
                    OpenACAISonsCommandStatus.InvalidArguments => OpenACAISonsClientCommandOutcome.InvalidArguments,
                    OpenACAISonsCommandStatus.Denied => OpenACAISonsClientCommandOutcome.Denied,
                    OpenACAISonsCommandStatus.Unavailable => OpenACAISonsClientCommandOutcome.Unavailable,
                    _ => OpenACAISonsClientCommandOutcome.Failed
                };
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Processing a client command failed (" + error.GetType().FullName + ").");
                return OpenACAISonsClientCommandOutcome.Failed;
            }
            finally
            {
                request?.Close();
            }
        }

        private static bool ApplyPolicy(OpenACAISonsChatRequest request, Action<OpenACAISonsChatRequest> handlers)
        {
            if (handlers != null)
            {
                foreach (Action<OpenACAISonsChatRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }
            }
            return true;
        }

        private static void PublishState(OpenACAISonsServerChatHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerChatState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            // A broken policy must not flood the log with player-triggered exceptions.
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning(
                    "Server chat policy rejected a message: " + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsServerChatHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerChatState
    {
        internal OpenACAISonsServerChatState(OpenACAISonsServerChatHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public OpenACAISonsServerChatHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>A managed snapshot. It does not retain the native event or connection.</summary>
    public sealed class OpenACAISonsChatRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsChatRequest(string message, ulong claimedSenderNetworkId,
            uint? sourceConnectionId, ulong? registeredSteamId, OpenACAISonsServerSession sourceSession,
            OpenACAISonsChatRequestOrigin origin = OpenACAISonsChatRequestOrigin.GameChat)
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
        public OpenACAISonsChatRequestOrigin Origin { get; }

        /// <summary>Payload supplied by the sender, not an authenticated player identity.</summary>
        public ulong ClaimedSenderNetworkId { get; }

        /// <summary>Transport connection ID, or null for a server-local event. IDs can be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }

        /// <summary>
        /// Connection-lifetime handle, or null for local events, unavailable tracking or a departed source.
        /// Its live metadata may change; this request's RegisteredSteamId is an event-time copy.
        /// </summary>
        public OpenACAISonsServerSession SourceSession { get; }

        /// <summary>
        /// Steam ID in the game's registration table for the source connection, or null when absent.
        /// This is not a separate Steam authentication or permission verdict.
        /// </summary>
        public ulong? RegisteredSteamId { get; }

        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Stops this callback's handling. Returns false outside synchronous dispatch or on another thread.
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

    public enum OpenACAISonsChatRequestOrigin
    {
        GameChat,
        ClientCommand
    }
}
