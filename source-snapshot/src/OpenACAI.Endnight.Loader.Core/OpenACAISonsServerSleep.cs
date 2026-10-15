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
    /// <summary>Server policy before native Sleep events are routed to players.</summary>
    public static class OpenACAISonsServerSleep
    {
        public const string ServiceKey = "openacai.sons.serverSleep";

        private static OpenACAISonsServerSleepState _state = new(
            OpenACAISonsSleepHookStatus.NotAttempted, "The sleep policy hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before server dispatch, including server-local sleep broadcasts.
        /// Rejection affects this event only, not sleep votes, prior UI changes or native cooldowns.
        /// </summary>
        public static event Action<OpenACAISonsSleepRequest> Receiving;

        public static OpenACAISonsServerSleepState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsSleepHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server sleep policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsSleepHookStatus.Installing, "Registering the sleep routing hook.");
            try
            {
                var target = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch(Event)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerSleep), nameof(BeforeDispatch))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-sleep");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsSleepHookStatus.Failed,
                    "Sleep hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server sleep policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsSleepHookStatus.Installed,
                "Sleep hook registration succeeded. Live sleep and policy handling remain unverified.");
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;
            var sleep = __0?.TryCast<Sleep>();
            if (sleep == null)
                return true;

            var forward = false;
            OpenACAISonsSleepRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                {
                    forward = true;
                    return true;
                }
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The sleep callback ran outside the loader's game thread.");
                    return false;
                }

                var connection = sleep.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                    && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;

                request = new OpenACAISonsSleepRequest(sleep, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsSleepRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }

                forward = true;
                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading sleep context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
                if (!forward)
                {
                    // Rejected dispatch does not transfer storage to native listeners or outgoing channels.
                    try { sleep.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportDispatchFailure("Releasing rejected sleep storage failed ("
                            + error.GetType().FullName + ").");
                    }
                }
            }
        }

        private static void PublishState(OpenACAISonsSleepHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerSleepState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server sleep policy rejected an event: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsSleepHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerSleepState
    {
        internal OpenACAISonsServerSleepState(OpenACAISonsSleepHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Hook registration, not evidence of a received, denied or completed sleep.</summary>
        public OpenACAISonsSleepHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of a Sleep event's routing and payload, without retained native objects.</summary>
    public sealed class OpenACAISonsSleepRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsSleepRequest(Sleep source, uint? sourceConnectionId,
            ulong? registeredSteamId, OpenACAISonsServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RoutingTargets = source.Targets;
            var routedEntity = source.TargetEntity;
            RoutedEntityNetworkId = routedEntity == null ? null : routedEntity.NetworkId.PackedValue;
            var routedConnection = source.TargetConnection;
            RoutedConnectionId = routedConnection == null ? null : routedConnection.ConnectionId;
            Aborted = source.Aborted;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Immediate transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when resolvable, not an individual sleep voter or permission verdict.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Raw routing mode, not proof of valid or eligible recipients.</summary>
        public int RoutingTargets { get; }
        public ulong? RoutedEntityNetworkId { get; }
        public uint? RoutedConnectionId { get; }
        /// <summary>Raw payload flag. The inspected player receiver ignores it; it is not an abort command.</summary>
        public bool Aborted { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Stops this synchronous server dispatch. Sends no reply, clears no votes and cancels no active sleep.
        /// Returns false after dispatch or on another thread. Rejection cannot be undone.
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
