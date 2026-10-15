// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Server policy before native world-locator state messages reach local listeners or other peers.</summary>
    public static partial class OpenACAISonsServerWorldLocators
    {
        public const string ServiceKey = "openacai.sons.serverWorldLocators";

        private static OpenACAISonsServerWorldLocatorState _state = new(
            OpenACAISonsWorldLocatorHookStatus.NotAttempted, "The world-locator routing hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the server routes received or locally created WorldObjectLocatorState events.
        /// Client-originated messages are part of normal gameplay; no default permission rule is imposed.
        /// </summary>
        public static event Action<OpenACAISonsWorldLocatorRequest> Receiving;

        public static OpenACAISonsServerWorldLocatorState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsWorldLocatorHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server world-locator policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsWorldLocatorHookStatus.Installing, "Registering the world-locator routing hook.");
            try
            {
                var target = AccessTools.Method(typeof(EventDispatcher), nameof(EventDispatcher.Dispatch), new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(EventDispatcher).FullName, "Dispatch(Event)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerWorldLocators), nameof(BeforeDispatch))
                {
                    priority = HarmonyLib.Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-world-locators");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsWorldLocatorHookStatus.Failed,
                    "World-locator hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server world-locator policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsWorldLocatorHookStatus.Installed,
                "World-locator hook registration succeeded. This is not evidence of a received or blocked message.");
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;

            var change = __0?.TryCast<WorldObjectLocatorState>();
            if (change == null)
                return true;

            var forward = false;
            OpenACAISonsWorldLocatorRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                {
                    forward = true;
                    return true;
                }
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The world-locator dispatch ran outside the loader's game thread.");
                    return false;
                }

                var connection = change.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                    && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;

                request = new OpenACAISonsWorldLocatorRequest(change, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsWorldLocatorRequest> handler in handlers.GetInvocationList())
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
                ReportDispatchFailure("Reading world-locator context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
                if (!forward)
                {
                    // Dispatch will not release this event or transfer its storage to an outgoing channel.
                    try { change.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportDispatchFailure("Releasing rejected world-locator storage failed ("
                            + error.GetType().FullName + ").");
                    }
                }
            }
        }

        private static void PublishState(OpenACAISonsWorldLocatorHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerWorldLocatorState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server world-locator policy rejected a message: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsWorldLocatorHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    /// <summary>Known native locator states. A message may contain a different raw integer.</summary>
    public enum OpenACAISonsWorldLocatorState
    {
        None = -1,
        Default = 0,
        Cleared = 1,
        Damaged = 2,
        Destroyed = 3,
        Burning = 4
    }

    public sealed class OpenACAISonsServerWorldLocatorState
    {
        internal OpenACAISonsServerWorldLocatorState(OpenACAISonsWorldLocatorHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public OpenACAISonsWorldLocatorHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Unvalidated locator and routing fields copied without resolving or retaining native objects.</summary>
    public sealed class OpenACAISonsWorldLocatorRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsWorldLocatorRequest(WorldObjectLocatorState source, uint? sourceConnectionId,
            ulong? registeredSteamId, OpenACAISonsServerSession sourceSession)
        {
            var id = source.Guid;
            RawLocatorId = new Guid(id._a, id._b, id._c, id._d, id._e, id._f, id._g, id._h, id._i, id._j, id._k);
            // Native WorldLocatorId copies only the first 12 bytes of the GUID.
            LocatorId = new Guid(id._a, id._b, id._c, id._d, id._e, id._f, id._g, 0, 0, 0, 0);
            RequestedState = source.State;
            KnownRequestedState = RequestedState switch
            {
                -1 => OpenACAISonsWorldLocatorState.None,
                0 => OpenACAISonsWorldLocatorState.Default,
                1 => OpenACAISonsWorldLocatorState.Cleared,
                2 => OpenACAISonsWorldLocatorState.Damaged,
                3 => OpenACAISonsWorldLocatorState.Destroyed,
                4 => OpenACAISonsWorldLocatorState.Burning,
                _ => null
            };
            RoutingTargets = source.Targets;
            SourceConnectionId = sourceConnectionId;
            RegisteredSteamId = registeredSteamId;
            SourceSession = sourceSession;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>All 16 payload bytes, including the four bytes ignored by native locator lookup.</summary>
        public Guid RawLocatorId { get; }
        /// <summary>
        /// Managed comparison key matching the native 12-byte locator ID, with the final four bytes zeroed.
        /// Not a Bolt network ID, resolved object or proof that the locator exists.
        /// </summary>
        public Guid LocatorId { get; }
        public int RequestedState { get; }
        /// <summary>Null for an unknown integer; the raw RequestedState remains available.</summary>
        public OpenACAISonsWorldLocatorState? KnownRequestedState { get; }
        /// <summary>Raw Bolt routing mode. No target validity or delivery is inferred.</summary>
        public int RoutingTargets { get; }
        /// <summary>Immediate transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Event-time game registration, not an independent authentication or permission verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Exact source lifetime when resolvable. Its metadata can change after this callback.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Stops this message's native server routing and local delivery, not earlier changes or other messages.
        /// Returns false after synchronous dispatch or on another thread. Rejection cannot be undone.
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
