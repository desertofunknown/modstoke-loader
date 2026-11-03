// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Server requests and routing policy for the game's multiplayer revive message.</summary>
    public static class ServerPlayerRevives
    {
        public const string ServiceKey = "openacai.sons.serverPlayerRevives";

        private static ServerPlayerReviveState _state = new(
            PlayerReviveHookStatus.NotAttempted, "The player-revive hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;
        private static int _reportedSendFailure;

        /// <summary>
        /// Runs synchronously before the server routes received or locally created PlayerHealed events.
        /// Peer-originated revives are part of normal gameplay; no default permission or proximity rule is imposed.
        /// </summary>
        public static event Action<PlayerReviveRequest> Receiving;

        public static ServerPlayerReviveState State => Volatile.Read(ref _state);

        /// <summary>
        /// Queues the native revive request for an exact remote session observed in the Dying state.
        /// The client must still be dying when it handles the request. This does not set arbitrary health
        /// or resurrect a fully dead player. The caller owns authorization; local events also pass Receiving.
        /// </summary>
        public static PlayerReviveStatus QueueRevive(ServerSession recipient)
        {
            if (State.Status != PlayerReviveHookStatus.Installed || !ServerSessions.HooksInstalled)
                return PlayerReviveStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
                return PlayerReviveStatus.WrongThread;

            PlayerHealed request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return PlayerReviveStatus.NotServer;
                var connection = ServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return PlayerReviveStatus.SessionUnavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return PlayerReviveStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return PlayerReviveStatus.PlayerUnavailable;
                var playerState = player.GetState<IPlayerState>();
                if (playerState is null || playerState.WasCollected)
                    return PlayerReviveStatus.PlayerStateUnavailable;
                if (playerState.Dying != (int)PlayerStats.DyingState.Dying)
                    return PlayerReviveStatus.NotDying;

                request = PlayerHealed.Create(connection, Bolt.ReliabilityModes.ReliableOrdered);
                request.HealTarget = player;
                request.HealingItemId = 0;

                var current = ServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return PlayerReviveStatus.SessionUnavailable;
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (currentPlayer is null || currentPlayer.WasCollected || !currentPlayer || !currentPlayer.isAttached
                    || currentPlayer.Pointer != player.Pointer)
                    return PlayerReviveStatus.PlayerUnavailable;
                if (playerState.Dying != (int)PlayerStats.DyingState.Dying)
                    return PlayerReviveStatus.NotDying;

                // Send can queue before throwing; storage ownership is uncertain once it starts.
                sendAttempted = true;
                request.Send();
                return PlayerReviveStatus.Queued;
            }
            catch (Exception error)
            {
                ReportSendFailure(error);
                return sendAttempted ? PlayerReviveStatus.SendUncertain : PlayerReviveStatus.Failed;
            }
            finally
            {
                if (request != null && !sendAttempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception error) { ReportSendFailure(error); }
                }
            }
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(PlayerReviveHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server player-revive policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(PlayerReviveHookStatus.Installing, "Registering the player-revive routing hook.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.PlayerRevives);
            }
            catch (Exception error)
            {
                PublishState(PlayerReviveHookStatus.Failed,
                    "Player-revive hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server player-revive policy hook failed: " + error);
                return;
            }

            PublishState(PlayerReviveHookStatus.Installed,
                "Player-revive hook registration succeeded. Live revival and policy handling remain unverified.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source)
        {
            var handlers = Receiving;
            if (handlers == null)
                return DispatchVerdict.Forward;
            var revive = source.TryCast<PlayerHealed>();
            if (revive == null)
                return DispatchVerdict.Forward;

            PlayerReviveRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return DispatchVerdict.Forward;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The player-revive callback ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }

                var connection = revive.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }
                request = new PlayerReviveRequest(revive, connectionId, registeredSteamId,
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
                ReportDispatchFailure("Reading player-revive context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
            finally
            {
                request?.Close();
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportDispatchFailure("Releasing rejected player-revive storage failed (" + error.GetType().FullName + ").");

        private static void PublishState(PlayerReviveHookStatus status, string detail)
        {
            var state = new ServerPlayerReviveState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportSendFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedSendFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server player-revive request failed ("
                    + error.GetType().FullName + "). Further send failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server player-revive policy rejected an event: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum PlayerReviveStatus
    {
        /// <summary>Send returned. This does not confirm policy approval, delivery or completed revival.</summary>
        Queued,
        Unavailable,
        WrongThread,
        NotServer,
        SessionUnavailable,
        PlayerUnavailable,
        PlayerStateUnavailable,
        /// <summary>The server-observed state is not Dying; this is not a fresh client-state confirmation.</summary>
        NotDying,
        /// <summary>Preparation failed before Send was attempted.</summary>
        Failed,
        /// <summary>Send threw and may have queued the request. Do not blindly retry.</summary>
        SendUncertain
    }

    public enum PlayerReviveHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerPlayerReviveState
    {
        internal ServerPlayerReviveState(PlayerReviveHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Hook registration, not evidence of a received, allowed or completed revive.</summary>
        public PlayerReviveHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of revive routing and payload fields, with no retained native objects.</summary>
    public sealed class PlayerReviveRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal PlayerReviveRequest(PlayerHealed source, uint? sourceConnectionId,
            ulong? registeredSteamId, ServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RoutingTargets = source.Targets;
            var routedEntity = source.TargetEntity;
            RoutedEntityNetworkId = routedEntity == null ? null : routedEntity.NetworkId.PackedValue;
            var routedConnection = source.TargetConnection;
            RoutedConnectionId = routedConnection == null ? null : routedConnection.ConnectionId;
            RequestedTargetNetworkId = source.GetHealTargetNetworkIdValue().PackedValue;
            RequestedHealingItemId = source.HealingItemId;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when resolvable, not proof of an authorized or nearby reviver.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Raw native routing mode; no recipient or routing validity is inferred.</summary>
        public int RoutingTargets { get; }
        /// <summary>Routing-envelope entity, distinct from the payload's HealTarget.</summary>
        public ulong? RoutedEntityNetworkId { get; }
        /// <summary>Explicit routing-envelope connection, if present.</summary>
        public uint? RoutedConnectionId { get; }
        /// <summary>Raw payload HealTarget ID. Native handling compares the resolved target to LocalPlayer.Entity.</summary>
        public ulong RequestedTargetNetworkId { get; }
        /// <summary>Raw item field, not proof of possession or consumption. The inspected receiver ignores it.</summary>
        public int RequestedHealingItemId { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>Stops this synchronous server dispatch. It sends no denial reply and undoes no prior effects.</summary>
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
