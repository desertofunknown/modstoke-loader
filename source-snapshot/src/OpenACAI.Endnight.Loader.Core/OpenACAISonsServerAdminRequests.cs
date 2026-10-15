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
    /// <summary>Additional server policy for native AdminCommand requests, before native permission checks.</summary>
    public static class OpenACAISonsServerAdminRequests
    {
        public const string ServiceKey = "openacai.sons.serverAdminRequests";

        private static OpenACAISonsServerAdminRequestState _state = new(
            OpenACAISonsAdminRequestHookStatus.NotAttempted, "The admin-request hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the server's AdminCommand callback. Allowing a request
        /// only resumes native handling; it does not grant permission or confirm execution.
        /// </summary>
        public static event Action<OpenACAISonsAdminRequest> Receiving;

        public static OpenACAISonsServerAdminRequestState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsAdminRequestHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server admin-request policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsAdminRequestHookStatus.Installing, "Registering the admin-request hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent),
                    new[] { typeof(AdminCommand) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(AdminCommand)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerAdminRequests), nameof(BeforeRequest))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-admin-requests");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsAdminRequestHookStatus.Failed,
                    "Admin-request hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server admin-request policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsAdminRequestHookStatus.Installed,
                "Admin-request hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeRequest(AdminCommand __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The admin-request callback ran outside the loader's game thread.");
                return false;
            }

            OpenACAISonsAdminRequest request = null;
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

                request = new OpenACAISonsAdminRequest(__0.Command, __0.Data, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsAdminRequest> handler in handlers.GetInvocationList())
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

        private static void PublishState(OpenACAISonsAdminRequestHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerAdminRequestState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server admin-request policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsAdminRequestHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerAdminRequestState
    {
        internal OpenACAISonsServerAdminRequestState(OpenACAISonsAdminRequestHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public OpenACAISonsAdminRequestHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of an unvalidated native admin request, with no retained native objects.</summary>
    public sealed class OpenACAISonsAdminRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsAdminRequest(string command, string data, uint? sourceConnectionId,
            ulong? registeredSteamId, OpenACAISonsServerSession sourceSession)
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
        public OpenACAISonsServerSession SourceSession { get; }
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
