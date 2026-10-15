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
    /// <summary>Server requests and routing policy for the game's multiplayer revive message.</summary>
    public static class OpenACAISonsServerPlayerRevives
    {
        public const string ServiceKey = "openacai.sons.serverPlayerRevives";

        private static OpenACAISonsServerPlayerReviveState _state = new(
            OpenACAISonsPlayerReviveHookStatus.NotAttempted, "The player-revive hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;
        private static int _reportedSendFailure;

        /// <summary>
        /// Runs synchronously before the server routes received or locally created PlayerHealed events.
        /// Peer-originated revives are part of normal gameplay; no default permission or proximity rule is imposed.
        /// </summary>
        public static event Action<OpenACAISonsPlayerReviveRequest> Receiving;

        public static OpenACAISonsServerPlayerReviveState State => Volatile.Read(ref _state);

        /// <summary>
        /// Queues the native revive request for an exact remote session observed in the Dying state.
        /// The client must still be dying when it handles the request. This does not set arbitrary health
        /// or resurrect a fully dead player. The caller owns authorization; local events also pass Receiving.
        /// </summary>
        public static OpenACAISonsPlayerReviveStatus QueueRevive(OpenACAISonsServerSession recipient)
        {
            if (State.Status != OpenACAISonsPlayerReviveHookStatus.Installed || !OpenACAISonsServerSessions.HooksInstalled)
                return OpenACAISonsPlayerReviveStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
                return OpenACAISonsPlayerReviveStatus.WrongThread;

            PlayerHealed request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsPlayerReviveStatus.NotServer;
                var connection = OpenACAISonsServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return OpenACAISonsPlayerReviveStatus.SessionUnavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return OpenACAISonsPlayerReviveStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return OpenACAISonsPlayerReviveStatus.PlayerUnavailable;
                var playerState = player.GetState<IPlayerState>();
                if (playerState is null || playerState.WasCollected)
                    return OpenACAISonsPlayerReviveStatus.PlayerStateUnavailable;
                if (playerState.Dying != (int)PlayerStats.DyingState.Dying)
                    return OpenACAISonsPlayerReviveStatus.NotDying;

                request = PlayerHealed.Create(connection, Bolt.ReliabilityModes.ReliableOrdered);
                request.HealTarget = player;
                request.HealingItemId = 0;

                var current = OpenACAISonsServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return OpenACAISonsPlayerReviveStatus.SessionUnavailable;
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (currentPlayer is null || currentPlayer.WasCollected || !currentPlayer || !currentPlayer.isAttached
                    || currentPlayer.Pointer != player.Pointer)
                    return OpenACAISonsPlayerReviveStatus.PlayerUnavailable;
                if (playerState.Dying != (int)PlayerStats.DyingState.Dying)
                    return OpenACAISonsPlayerReviveStatus.NotDying;

                // Send can queue before throwing; storage ownership is uncertain once it starts.
                sendAttempted = true;
                request.Send();
                return OpenACAISonsPlayerReviveStatus.Queued;
            }
            catch (Exception error)
            {
                ReportSendFailure(error);
                return sendAttempted ? OpenACAISonsPlayerReviveStatus.SendUncertain : OpenACAISonsPlayerReviveStatus.Failed;
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

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsPlayerReviveHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server player-revive policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsPlayerReviveHookStatus.Installing, "Registering the player-revive routing hook.");
            try
            {
                var target = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch(Event)");
                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerPlayerRevives), nameof(BeforeDispatch))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-player-revives");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsPlayerReviveHookStatus.Failed,
                    "Player-revive hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server player-revive policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsPlayerReviveHookStatus.Installed,
                "Player-revive hook registration succeeded. Live revival and policy handling remain unverified.");
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;
            var revive = __0?.TryCast<PlayerHealed>();
            if (revive == null)
                return true;

            var forward = false;
            OpenACAISonsPlayerReviveRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                {
                    forward = true;
                    return true;
                }
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The player-revive callback ran outside the loader's game thread.");
                    return false;
                }

                var connection = revive.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }
                request = new OpenACAISonsPlayerReviveRequest(revive, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsPlayerReviveRequest> handler in handlers.GetInvocationList())
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
                ReportDispatchFailure("Reading player-revive context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
                if (!forward)
                {
                    // Skipped dispatch never transfers storage to native callbacks or outgoing channels.
                    try { revive.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportDispatchFailure("Releasing rejected player-revive storage failed ("
                            + error.GetType().FullName + ").");
                    }
                }
            }
        }

        private static void PublishState(OpenACAISonsPlayerReviveHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerPlayerReviveState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportSendFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedSendFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server player-revive request failed ("
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server player-revive policy rejected an event: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsPlayerReviveStatus
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

    public enum OpenACAISonsPlayerReviveHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerPlayerReviveState
    {
        internal OpenACAISonsServerPlayerReviveState(OpenACAISonsPlayerReviveHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Hook registration, not evidence of a received, allowed or completed revive.</summary>
        public OpenACAISonsPlayerReviveHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of revive routing and payload fields, with no retained native objects.</summary>
    public sealed class OpenACAISonsPlayerReviveRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsPlayerReviveRequest(PlayerHealed source, uint? sourceConnectionId,
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
            RequestedTargetNetworkId = source.GetHealTargetNetworkIdValue().PackedValue;
            RequestedHealingItemId = source.HealingItemId;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when resolvable, not proof of an authorized or nearby reviver.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
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
