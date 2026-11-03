// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Additional server policy for native AdminCommand requests, before native permission checks.</summary>
    public static class ServerAdminRequests
    {
        public const string ServiceKey = "openacai.sons.serverAdminRequests";

        private static ServerAdminRequestState _state = new(
            AdminRequestHookStatus.NotAttempted, "The admin-request hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the server's AdminCommand callback. Allowing a request
        /// only resumes native handling; it does not grant permission or confirm execution.
        /// </summary>
        public static event Action<AdminRequest> Receiving;

        public static ServerAdminRequestState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(AdminRequestHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server admin-request policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(AdminRequestHookStatus.Installing, "Registering the admin-request hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent),
                    new[] { typeof(AdminCommand) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(AdminCommand)");

                var prefix = new HarmonyMethod(typeof(ServerAdminRequests), nameof(BeforeRequest))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-admin-requests");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(AdminRequestHookStatus.Failed,
                    "Admin-request hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server admin-request policy hook failed: " + error);
                return;
            }

            PublishState(AdminRequestHookStatus.Installed,
                "Admin-request hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeRequest(AdminCommand __0, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            var handlers = Receiving;
            if (handlers == null)
                return true;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The admin-request callback ran outside the loader's game thread.");
                return false;
            }

            AdminRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                var connection = __0.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }

                request = new AdminRequest(__0.Command, __0.Data, connectionId, registeredSteamId,
                    ServerSessions.ResolveSource(connection));
                foreach (Action<AdminRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }

                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading admin-request context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static void PublishState(AdminRequestHookStatus status, string detail)
        {
            var state = new ServerAdminRequestState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server admin-request policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum AdminRequestHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerAdminRequestState
    {
        internal ServerAdminRequestState(AdminRequestHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public AdminRequestHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of an unvalidated native admin request, with no retained native objects.</summary>
    public sealed class AdminRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal AdminRequest(string command, string data, uint? sourceConnectionId,
            ulong? registeredSteamId, ServerSession sourceSession)
        {
            Command = command;
            Data = data;
            SourceConnectionId = sourceConnectionId;
            RegisteredSteamId = registeredSteamId;
            SourceSession = sourceSession;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Raw, case-sensitive native command name. May be null or unknown.</summary>
        public string Command { get; }
        /// <summary>Untrusted argument string, preserved without parsing, trimming or validation. May be null.</summary>
        public string Data { get; }
        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Exact source lifetime when resolvable. Its metadata can change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this native server callback without undoing prior effects or sending a denial message.
        /// Returns false outside synchronous dispatch or on another thread. Rejection cannot be undone.
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
}
