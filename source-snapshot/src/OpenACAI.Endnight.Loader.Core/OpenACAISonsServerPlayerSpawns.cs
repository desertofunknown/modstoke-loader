// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;
using Sons.Wearable.Race;

namespace OpenACAI.Sons
{
    /// <summary>Server-authored spawn assignments using the game's existing client protocol.</summary>
    public static class OpenACAISonsServerPlayerSpawns
    {
        public const string ServiceKey = "openacai.sons.serverPlayerSpawns";

        private static OpenACAISonsPlayerSpawnState _state = new(
            OpenACAISonsPlayerSpawnHookStatus.NotAttempted, "The player-spawn authority guard has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedSendFailure;
        private static int _reportedGuardFailure;

        public static OpenACAISonsPlayerSpawnState State => Volatile.Read(ref _state);

        /// <summary>
        /// Queues a reliable spawn assignment for an exact remote session, preserving its observed player race.
        /// Vanilla handling moves a loaded local player, changes look direction, reapplies race and caches the
        /// assignment for native spawn handling. This is not a pure teleport or a persistent respawn setting.
        /// The caller owns authorization and destination safety. Queued does not confirm client application.
        /// </summary>
        public static OpenACAISonsPlayerSpawnStatus QueueSpawnAssignment(OpenACAISonsServerSession recipient,
            Vector3 position, Quaternion rotation)
        {
            if (State.Status != OpenACAISonsPlayerSpawnHookStatus.Installed || !OpenACAISonsServerSessions.HooksInstalled)
                return OpenACAISonsPlayerSpawnStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
                return OpenACAISonsPlayerSpawnStatus.WrongThread;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return OpenACAISonsPlayerSpawnStatus.InvalidPosition;
            if (!TryNormalizeRotation(rotation, out var normalized))
                return OpenACAISonsPlayerSpawnStatus.InvalidRotation;

            SetPlayerSpawnPoint request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsPlayerSpawnStatus.NotServer;
                var connection = OpenACAISonsServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return OpenACAISonsPlayerSpawnStatus.SessionUnavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return OpenACAISonsPlayerSpawnStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return OpenACAISonsPlayerSpawnStatus.PlayerUnavailable;
                var playerState = player.GetState<IPlayerState>();
                if (playerState is null || playerState.WasCollected)
                    return OpenACAISonsPlayerSpawnStatus.RaceUnavailable;
                var race = playerState.PlayerRace;
                if (!Enum.IsDefined((PlayerRace.Race)race))
                    return OpenACAISonsPlayerSpawnStatus.RaceUnavailable;

                request = SetPlayerSpawnPoint.Create(connection, Bolt.ReliabilityModes.ReliableOrdered);
                request.Position = new UnityEngine.Vector3(position.X, position.Y, position.Z);
                request.Rotation = new UnityEngine.Quaternion(normalized.X, normalized.Y, normalized.Z, normalized.W);
                request.Race = race;

                var current = OpenACAISonsServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return OpenACAISonsPlayerSpawnStatus.SessionUnavailable;
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (currentPlayer is null || currentPlayer.WasCollected || !currentPlayer || !currentPlayer.isAttached
                    || currentPlayer.Pointer != player.Pointer)
                    return OpenACAISonsPlayerSpawnStatus.PlayerUnavailable;

                // Send can queue before throwing. Never free or retry a possibly queued event.
                sendAttempted = true;
                request.Send();
                return OpenACAISonsPlayerSpawnStatus.Queued;
            }
            catch (Exception error)
            {
                ReportSendFailure(error);
                return sendAttempted ? OpenACAISonsPlayerSpawnStatus.SendUncertain : OpenACAISonsPlayerSpawnStatus.Failed;
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
                PublishState(OpenACAISonsPlayerSpawnHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Player-spawn authority guard was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsPlayerSpawnHookStatus.Installing, "Registering the player-spawn authority guard.");
            try
            {
                var target = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch(Event)");
                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerPlayerSpawns), nameof(BeforeDispatch))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-player-spawn-authority");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsPlayerSpawnHookStatus.Failed,
                    "Player-spawn authority guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Player-spawn authority guard failed: " + error);
                return;
            }

            PublishState(OpenACAISonsPlayerSpawnHookStatus.Installed,
                "Player-spawn authority guard registration succeeded. Multiplayer handling remains unverified.");
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            if (__0 == null || __0.TryCast<SetPlayerSpawnPoint>() == null)
                return true;

            var forward = false;
            try
            {
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportGuardFailure("A player-spawn event ran outside the loader's game thread.");
                    return false;
                }
                var source = __0.RaisedBy;
                if (source == null)
                {
                    forward = true;
                    return true;
                }

                // Spawn assignments are host-authored state, not client requests to relay to other peers.
                if (!BoltNetwork.isRunning || BoltNetwork.isServer || !BoltNetwork.isClient)
                    return false;
                var server = BoltNetwork.server;
                forward = server != null && source.Pointer == server.Pointer && source.ConnectionId == server.ConnectionId;
                return forward;
            }
            catch (Exception error)
            {
                ReportGuardFailure("Reading player-spawn authority failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                if (!forward)
                {
                    // Skipped dispatch never transfers storage ownership to native callbacks or outgoing channels.
                    try { __0.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportGuardFailure("Releasing a rejected player-spawn event failed (" + error.GetType().FullName + ").");
                    }
                }
            }
        }

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

        private static void PublishState(OpenACAISonsPlayerSpawnHookStatus status, string detail)
        {
            var state = new OpenACAISonsPlayerSpawnState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportSendFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedSendFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server spawn assignment failed ("
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Player-spawn event rejected: "
                    + detail + " Further guard failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsPlayerSpawnStatus
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

    public enum OpenACAISonsPlayerSpawnHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsPlayerSpawnState
    {
        internal OpenACAISonsPlayerSpawnState(OpenACAISonsPlayerSpawnHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Authority hook registration, not proof of enforcement or client spawn application.</summary>
        public OpenACAISonsPlayerSpawnHookStatus Status { get; }
        public string Detail { get; }
    }
}
