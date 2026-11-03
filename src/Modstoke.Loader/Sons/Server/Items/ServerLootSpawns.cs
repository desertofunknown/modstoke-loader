// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Opt-in server policy before native routing of exact SpawnLootItemEvent events.</summary>
    public static class ServerLootSpawns
    {
        public const string ServiceKey = "openacai.sons.serverLootSpawns";

        private static ServerLootSpawnState _state = new(
            LootSpawnHookStatus.NotAttempted, "The loot spawn routing hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before server dispatch, including local sends, which are allowed by default.
        /// Rejection does not undo a host-local spawn performed before replication.
        /// </summary>
        public static event Action<LootSpawnRequest> Receiving;

        public static ServerLootSpawnState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(LootSpawnHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server loot spawn policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(LootSpawnHookStatus.Installing, "Registering the loot spawn routing hook.");
            try
            {
                if (Il2CppClassPointerStore<SpawnLootItemEvent>.NativeClassPtr == IntPtr.Zero)
                    throw new InvalidOperationException("The native SpawnLootItemEvent class is unavailable.");

                ServerDispatchGate.Register(DispatchRoute.LootSpawns);
            }
            catch (Exception error)
            {
                PublishState(LootSpawnHookStatus.Failed,
                    "Loot spawn hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server loot spawn policy hook failed: " + error);
                return;
            }

            PublishState(LootSpawnHookStatus.Installed,
                "Loot spawn hook registration succeeded. Live routing and policy handling remain unverified.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source)
        {
            var handlers = Receiving;
            if (handlers == null)
                return DispatchVerdict.Forward;

            try
            {
                if (IL2CPP.il2cpp_object_get_class(source.Pointer)
                    != Il2CppClassPointerStore<SpawnLootItemEvent>.NativeClassPtr)
                    return DispatchVerdict.Forward;
            }
            catch (Exception error)
            {
                // Unknown events never enter the rejection path or transfer storage ownership here.
                ReportDispatchFailure("Identifying a loot spawn event failed (" + error.GetType().FullName + ").");
                return DispatchVerdict.Forward;
            }

            LootSpawnRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return DispatchVerdict.Forward;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The loot spawn callback ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }

                var loot = source.TryCast<SpawnLootItemEvent>()
                    ?? throw new InvalidOperationException("The recognized loot spawn payload is unavailable.");
                var connection = source.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                    && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;

                request = new LootSpawnRequest(source, loot, connectionId, registeredSteamId,
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
                ReportDispatchFailure("Reading loot spawn context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
            finally
            {
                request?.Close();
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportDispatchFailure("Releasing rejected loot spawn storage failed (" + error.GetType().FullName + ").");

        private static void PublishState(LootSpawnHookStatus status, string detail)
        {
            var state = new ServerLootSpawnState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server loot spawn policy dispatch failed: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum LootSpawnHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    public sealed class ServerLootSpawnState
    {
        internal ServerLootSpawnState(LootSpawnHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Hook registration, not evidence of receipt, rejection or a spawned item.</summary>
        public LootSpawnHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Unvalidated routing and payload copies; no native wrappers or ownership proof are retained.</summary>
    public sealed class LootSpawnRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal LootSpawnRequest(Bolt.Event source, SpawnLootItemEvent loot,
            uint? sourceConnectionId, ulong? registeredSteamId, ServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RoutingTargets = source.Targets;
            var entity = source.TargetEntity;
            RoutedEntityNetworkId = entity == null ? null : entity.NetworkId.PackedValue;
            var connection = source.TargetConnection;
            RoutedConnectionId = connection == null ? null : connection.ConnectionId;
            RequestedItemId = loot.ItemId;
            var position = loot.Position;
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            var rotation = loot.Rotation;
            RequestedRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            RequestedDynamic = loot.Dynamic;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Immediate transport source, or null for server-local events. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when available, not a permission verdict.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication.</summary>
        public ulong? RegisteredSteamId { get; }
        public int RoutingTargets { get; }
        public ulong? RoutedEntityNetworkId { get; }
        public uint? RoutedConnectionId { get; }
        public int RequestedItemId { get; }
        public Vector3 RequestedPosition { get; }
        public Quaternion RequestedRotation { get; }
        public bool RequestedDynamic { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Stops this dispatch, not an earlier host-local spawn. Rejection cannot be undone.
        /// Returns false after dispatch or on another thread.
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
