// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Calls the game's position-event validator for an exact remote session and expected game identity.
        /// The position must already be in world space. This does not resolve placement coordinates or enforce policy.
        /// </summary>
        public static NativeRangeResult ValidatePositionRange(ServerSession session,
            ulong expectedSteamId, Vector3 worldPosition, int maxDistance)
        {
            NativeRangeResult Result(NativeRangeStatus status, ulong? playerId = null) =>
                new(status, session, expectedSteamId, playerId, null);

            if (expectedSteamId == 0 || maxDistance < 0)
                return Result(NativeRangeStatus.InvalidArgument);
            if (!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y) || !float.IsFinite(worldPosition.Z))
                return Result(NativeRangeStatus.NonFinitePosition);

            try
            {
                var status = CaptureRangeSource(session, expectedSteamId, out var before, out var connection);
                if (status.HasValue)
                    return Result(status.Value);

                var position = new UnityEngine.Vector3(worldPosition.X, worldPosition.Y, worldPosition.Z);
                var accepted = CoopServerCallbacks.ValidateEvent(position, connection, maxDistance);
                status = CaptureRangeSource(session, expectedSteamId, out var after, out _);
                if (status.HasValue)
                    return Result(status.Value);
                if (before != after)
                    return Result(NativeRangeStatus.BindingChanged);

                return Result(accepted ? NativeRangeStatus.Accepted : NativeRangeStatus.Rejected,
                    before.PlayerNetworkId);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return Result(NativeRangeStatus.Failed);
            }
        }

        /// <summary>
        /// Calls the game's structure-hit validator with its native floating-point distance limit and player-state check.
        /// A completed native result is an observation, not permission to damage or mutate the target.
        /// </summary>
        public static NativeRangeResult ValidateStructureHitRange(ServerSession session,
            ulong expectedSteamId, ulong targetNetworkId, float maxDistance)
        {
            NativeRangeResult Result(NativeRangeStatus status, ulong? playerId = null) =>
                new(status, session, expectedSteamId, playerId, targetNetworkId);

            if (expectedSteamId == 0 || targetNetworkId == 0 || !float.IsFinite(maxDistance) || maxDistance < 0)
                return Result(NativeRangeStatus.InvalidArgument);

            try
            {
                var status = CaptureRangeSource(session, expectedSteamId, out var before, out var connection);
                if (status.HasValue)
                    return Result(status.Value);
                status = CaptureRangeTarget(targetNetworkId, out var target);
                if (status.HasValue)
                    return Result(status.Value);
                var targetPointer = target.Pointer;

                var accepted = CoopServerCallbacks.ValidateStructureHitEvent(target, connection, maxDistance, out var nativePlayer);
                status = CaptureRangeSource(session, expectedSteamId, out var after, out _);
                if (status.HasValue)
                    return Result(status.Value);
                if (before != after)
                    return Result(NativeRangeStatus.BindingChanged);
                if (!IsLiveRangeEntity(nativePlayer) || nativePlayer.Pointer != before.PlayerPointer
                    || nativePlayer.networkId.PackedValue != before.PlayerNetworkId)
                    return Result(NativeRangeStatus.NativePlayerMismatch);

                status = CaptureRangeTarget(targetNetworkId, out var currentTarget);
                if (status.HasValue)
                    return Result(status.Value);
                if (currentTarget.Pointer != targetPointer)
                    return Result(NativeRangeStatus.BindingChanged);

                return Result(accepted ? NativeRangeStatus.Accepted : NativeRangeStatus.Rejected,
                    before.PlayerNetworkId);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return Result(NativeRangeStatus.Failed);
            }
        }

        private static NativeRangeStatus? CaptureRangeSource(ServerSession session,
            ulong expectedSteamId, out RangeBinding binding, out BoltConnection connection)
        {
            binding = default;
            connection = null;
            var context = CheckContext();
            if (context != WorldReadStatus.Success)
                return context switch
                {
                    WorldReadStatus.WrongThread => NativeRangeStatus.WrongThread,
                    WorldReadStatus.NotServer => NativeRangeStatus.NotServer,
                    _ => NativeRangeStatus.Unavailable
                };
            connection = ServerSessions.ResolveCurrent(session);
            if (connection == null)
                return ServerSessions.HooksInstalled
                    ? NativeRangeStatus.SessionUnavailable : NativeRangeStatus.Unavailable;

            var connectionPointer = connection.Pointer;
            var connectionId = connection.ConnectionId;
            var transport = connection._udp;
            if (transport is null || transport.WasCollected || !transport.IsConnected)
                return NativeRangeStatus.TransportUnavailable;

            var dedicated = GameServerManager.IsDedicatedServer;
            ulong steamId;
            if (dedicated)
            {
                if (!GameServerManager.GetRegisteredClient(connection, out var registered))
                    return NativeRangeStatus.IdentityUnavailable;
                steamId = registered.m_SteamID;
            }
            else
                steamId = MultiplayerUtilities.GetSteamId(connection);
            if (steamId == 0 || steamId != expectedSteamId)
                return NativeRangeStatus.IdentityUnavailable;

            var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
            if (!MatchesPlayerSession(player, connection, session))
                return NativeRangeStatus.PlayerUnavailable;
            var playerPointer = player.Pointer;
            var playerId = player.networkId.PackedValue;
            if (playerId == 0)
                return NativeRangeStatus.PlayerUnavailable;

            // The native validators select UserData, not the registered-player lookup above.
            var userData = connection.UserData;
            var nativePlayer = userData is null || userData.WasCollected ? null : userData.TryCast<BoltEntity>();
            if (!IsLiveRangeEntity(nativePlayer) || nativePlayer.Pointer != playerPointer
                || nativePlayer.networkId.PackedValue != playerId)
                return NativeRangeStatus.NativePlayerMismatch;
            var positionStatus = CheckRangePosition(player, NativeRangeStatus.PlayerUnavailable);
            if (positionStatus.HasValue)
                return positionStatus;

            binding = new RangeBinding(connectionPointer, connectionId, transport.Pointer, playerPointer, playerId, dedicated);
            return null;
        }

        private static NativeRangeStatus? CaptureRangeTarget(ulong networkId, out BoltEntity target)
        {
            target = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (!IsLiveRangeEntity(target) || target.networkId.PackedValue != networkId)
                return NativeRangeStatus.TargetUnavailable;
            return CheckRangePosition(target, NativeRangeStatus.TargetUnavailable);
        }

        private static bool IsLiveRangeEntity(BoltEntity entity) =>
            entity is not null && !entity.WasCollected && entity && entity.isAttached;

        private static NativeRangeStatus? CheckRangePosition(BoltEntity entity,
            NativeRangeStatus unavailable)
        {
            var transform = entity.transform;
            if (transform is null || transform.WasCollected || !transform)
                return unavailable;
            var position = transform.position;
            return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
                ? null : NativeRangeStatus.NonFinitePosition;
        }

        private readonly record struct RangeBinding(IntPtr ConnectionPointer, uint ConnectionId, IntPtr TransportPointer,
            IntPtr PlayerPointer, ulong PlayerNetworkId, bool DedicatedIdentity);
    }

    public enum NativeRangeStatus
    {
        Unavailable,
        Accepted,
        Rejected,
        WrongThread,
        NotServer,
        InvalidArgument,
        SessionUnavailable,
        TransportUnavailable,
        IdentityUnavailable,
        PlayerUnavailable,
        NativePlayerMismatch,
        TargetUnavailable,
        NonFinitePosition,
        BindingChanged,
        Failed
    }

    /// <summary>
    /// A native validation observation with no retained native objects. Only Accepted and Rejected are completed verdicts.
    /// All other statuses mean that no trustworthy verdict is available, even if the native method was called.
    /// </summary>
    public sealed class NativeRangeResult
    {
        internal NativeRangeResult(NativeRangeStatus status, ServerSession session,
            ulong expectedSteamId, ulong? playerNetworkId, ulong? targetNetworkId)
        {
            Status = status;
            Session = session;
            ExpectedSteamId = expectedSteamId;
            PlayerNetworkId = playerNetworkId;
            TargetNetworkId = targetNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public NativeRangeStatus Status { get; }
        /// <summary>The requested lifetime handle, which can close after validation.</summary>
        public ServerSession Session { get; }
        /// <summary>The caller's expected game identity. This is not independent Steam authentication.</summary>
        public ulong ExpectedSteamId { get; }
        /// <summary>The bound player ID on Accepted or Rejected; null when no trustworthy verdict is available.</summary>
        public ulong? PlayerNetworkId { get; }
        /// <summary>The requested current-world target ID, or null for a position query. Not a persistent identity.</summary>
        public ulong? TargetNetworkId { get; }
        /// <summary>Local completion time, not a simulation timestamp or guarantee of continued validity.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
