// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Server policy before native world-locator state messages reach local listeners or other peers.</summary>
    public static partial class ServerWorldLocators
    {
        public const string ServiceKey = "openacai.sons.serverWorldLocators";

        private static ServerWorldLocatorState _state = new(
            WorldLocatorHookStatus.NotAttempted, "The world-locator routing hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the server routes received or locally created WorldObjectLocatorState events.
        /// Client-originated messages are part of normal gameplay; no default permission rule is imposed.
        /// </summary>
        public static event Action<WorldLocatorRequest> Receiving;

        public static ServerWorldLocatorState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(WorldLocatorHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server world-locator policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(WorldLocatorHookStatus.Installing, "Registering the world-locator routing hook.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.WorldLocators);
            }
            catch (Exception error)
            {
                PublishState(WorldLocatorHookStatus.Failed,
                    "World-locator hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server world-locator policy hook failed: " + error);
                return;
            }

            PublishState(WorldLocatorHookStatus.Installed,
                "World-locator hook registration succeeded. This is not evidence of a received or blocked message.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source)
        {
            var handlers = Receiving;
            if (handlers == null)
                return DispatchVerdict.Forward;

            var change = source.TryCast<WorldObjectLocatorState>();
            if (change == null)
                return DispatchVerdict.Forward;

            WorldLocatorRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return DispatchVerdict.Forward;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The world-locator dispatch ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }

                var connection = change.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                    && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;

                request = new WorldLocatorRequest(change, connectionId, registeredSteamId,
                    ServerSessions.ResolveSource(connection));
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected)
                        return DispatchVerdict.Reject;
                }

                return DispatchVerdict.Forward;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading world-locator context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
            finally
            {
                request?.Close();
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportDispatchFailure("Releasing rejected world-locator storage failed (" + error.GetType().FullName + ").");

        private static void PublishState(WorldLocatorHookStatus status, string detail)
        {
            var state = new ServerWorldLocatorState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server world-locator policy rejected a message: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum WorldLocatorHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    /// <summary>Known native locator states. A message may contain a different raw integer.</summary>
    public enum WorldLocatorState
    {
        None = -1,
        Default = 0,
        Cleared = 1,
        Damaged = 2,
        Destroyed = 3,
        Burning = 4
    }

    public sealed class ServerWorldLocatorState
    {
        internal ServerWorldLocatorState(WorldLocatorHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public WorldLocatorHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Unvalidated locator and routing fields copied without resolving or retaining native objects.</summary>
    public sealed class WorldLocatorRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal WorldLocatorRequest(WorldObjectLocatorState source, uint? sourceConnectionId,
            ulong? registeredSteamId, ServerSession sourceSession)
        {
            var id = source.Guid;
            RawLocatorId = new Guid(id._a, id._b, id._c, id._d, id._e, id._f, id._g, id._h, id._i, id._j, id._k);
            // Native WorldLocatorId copies only the first 12 bytes of the GUID.
            LocatorId = new Guid(id._a, id._b, id._c, id._d, id._e, id._f, id._g, 0, 0, 0, 0);
            RequestedState = source.State;
            KnownRequestedState = RequestedState switch
            {
                -1 => WorldLocatorState.None,
                0 => WorldLocatorState.Default,
                1 => WorldLocatorState.Cleared,
                2 => WorldLocatorState.Damaged,
                3 => WorldLocatorState.Destroyed,
                4 => WorldLocatorState.Burning,
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
        public WorldLocatorState? KnownRequestedState { get; }
        /// <summary>Raw Bolt routing mode. No target validity or delivery is inferred.</summary>
        public int RoutingTargets { get; }
        /// <summary>Immediate transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Event-time game registration, not an independent authentication or permission verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Exact source lifetime when resolvable. Its metadata can change after this callback.</summary>
        public ServerSession SourceSession { get; }
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
