// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using global::Sons.Wearable.Race;

namespace Modstoke.Sons
{
    /// <summary>Server-authored spawn assignments using the game's existing client protocol.</summary>
    public static class ServerPlayerSpawns
    {
        public const string ServiceKey = "openacai.sons.serverPlayerSpawns";

        private static PlayerSpawnState _state = new(
            PlayerSpawnHookStatus.NotAttempted, "The player-spawn authority guard has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedSendFailure;
        private static int _reportedGuardFailure;

        public static PlayerSpawnState State => Volatile.Read(ref _state);

        /// <summary>
        /// Queues a reliable spawn assignment for an exact remote session, preserving its observed player race.
        /// Vanilla handling moves a loaded local player, changes look direction, reapplies race and caches the
        /// assignment for native spawn handling. This is not a pure teleport or a persistent respawn setting.
        /// The caller owns authorization and destination safety. Queued does not confirm client application.
        /// </summary>
        public static PlayerSpawnStatus QueueSpawnAssignment(ServerSession recipient,
            Vector3 position, Quaternion rotation)
        {
            if (State.Status != PlayerSpawnHookStatus.Installed || !ServerSessions.HooksInstalled)
                return PlayerSpawnStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
                return PlayerSpawnStatus.WrongThread;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return PlayerSpawnStatus.InvalidPosition;
            if (!TryNormalizeRotation(rotation, out var normalized))
                return PlayerSpawnStatus.InvalidRotation;

            SetPlayerSpawnPoint request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return PlayerSpawnStatus.NotServer;
                var connection = ServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return PlayerSpawnStatus.SessionUnavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return PlayerSpawnStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return PlayerSpawnStatus.PlayerUnavailable;
                var playerState = player.GetState<IPlayerState>();
                if (playerState is null || playerState.WasCollected)
                    return PlayerSpawnStatus.RaceUnavailable;
                var race = playerState.PlayerRace;
                if (!Enum.IsDefined((PlayerRace.Race)race))
                    return PlayerSpawnStatus.RaceUnavailable;

                request = SetPlayerSpawnPoint.Create(connection, Bolt.ReliabilityModes.ReliableOrdered);
                request.Position = new UnityEngine.Vector3(position.X, position.Y, position.Z);
                request.Rotation = new UnityEngine.Quaternion(normalized.X, normalized.Y, normalized.Z, normalized.W);
                request.Race = race;

                var current = ServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return PlayerSpawnStatus.SessionUnavailable;
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (currentPlayer is null || currentPlayer.WasCollected || !currentPlayer || !currentPlayer.isAttached
                    || currentPlayer.Pointer != player.Pointer)
                    return PlayerSpawnStatus.PlayerUnavailable;

                // Send can queue before throwing. Never free or retry a possibly queued event.
                sendAttempted = true;
                request.Send();
                return PlayerSpawnStatus.Queued;
            }
            catch (Exception error)
            {
                ReportSendFailure(error);
                return sendAttempted ? PlayerSpawnStatus.SendUncertain : PlayerSpawnStatus.Failed;
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
                PublishState(PlayerSpawnHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Player-spawn authority guard was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(PlayerSpawnHookStatus.Installing, "Registering the player-spawn authority guard.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.PlayerSpawns);
            }
            catch (Exception error)
            {
                PublishState(PlayerSpawnHookStatus.Failed,
                    "Player-spawn authority guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Player-spawn authority guard failed: " + error);
                return;
            }

            PublishState(PlayerSpawnHookStatus.Installed,
                "Player-spawn authority guard registration succeeded. Multiplayer handling remains unverified.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event message)
        {
            if (message.TryCast<SetPlayerSpawnPoint>() == null)
                return DispatchVerdict.Forward;

            try
            {
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportGuardFailure("A player-spawn event ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }
                var source = message.RaisedBy;
                if (source == null)
                    return DispatchVerdict.Forward;

                // Spawn assignments are host-authored state, not client requests to relay to other peers.
                if (!BoltNetwork.isRunning || BoltNetwork.isServer || !BoltNetwork.isClient)
                    return DispatchVerdict.Reject;
                var server = BoltNetwork.server;
                return server != null && source.Pointer == server.Pointer && source.ConnectionId == server.ConnectionId
                    ? DispatchVerdict.Forward : DispatchVerdict.Reject;
            }
            catch (Exception error)
            {
                ReportGuardFailure("Reading player-spawn authority failed (" + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportGuardFailure("Releasing a rejected player-spawn event failed (" + error.GetType().FullName + ").");

        private static bool TryNormalizeRotation(Quaternion rotation, out Quaternion normalized)
        {
            normalized = default;
            if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y)
                || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
                return false;

            // Double precision avoids overflow/underflow when normalizing finite float components.
            var lengthSquared = (double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
                + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W;
            if (lengthSquared == 0)
                return false;
            var inverseLength = 1 / Math.Sqrt(lengthSquared);
            normalized = new Quaternion((float)(rotation.X * inverseLength), (float)(rotation.Y * inverseLength),
                (float)(rotation.Z * inverseLength), (float)(rotation.W * inverseLength));
            return true;
        }

        private static void PublishState(PlayerSpawnHookStatus status, string detail)
        {
            var state = new PlayerSpawnState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportSendFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedSendFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server spawn assignment failed ("
                    + error.GetType().FullName + "). Further send failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void ReportGuardFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedGuardFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Player-spawn event rejected: "
                    + detail + " Further guard failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum PlayerSpawnStatus
    {
        /// <summary>Send returned. This is not a client receipt, safe-arrival check or movement guarantee.</summary>
        Queued,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidPosition,
        InvalidRotation,
        SessionUnavailable,
        PlayerUnavailable,
        /// <summary>The current player race could not be read or is not a defined native race.</summary>
        RaceUnavailable,
        /// <summary>Preparation failed before Send was attempted.</summary>
        Failed,
        /// <summary>Send threw and may have queued the assignment. Do not blindly retry.</summary>
        SendUncertain
    }

    public enum PlayerSpawnHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class PlayerSpawnState
    {
        internal PlayerSpawnState(PlayerSpawnHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Authority hook registration, not proof of enforcement or client spawn application.</summary>
        public PlayerSpawnHookStatus Status { get; }
        public string Detail { get; }
    }
}
