// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Read-only observations of network entities in the server's current world.</summary>
    public static partial class OpenACAISonsServerWorld
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Reads the game's registered player entity for an exact live remote session.
        /// A connected player may not yet have an attached entity. No local host player is inferred.
        /// </summary>
        public static bool TryReadPlayer(OpenACAISonsServerSession session, out OpenACAISonsEntitySnapshot snapshot,
            out OpenACAISonsWorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                if (!OpenACAISonsServerSessions.HooksInstalled)
                {
                    status = OpenACAISonsWorldReadStatus.Unavailable;
                    return false;
                }

                var connection = OpenACAISonsServerSessions.ResolveCurrent(session);
                if (connection == null)
                {
                    status = OpenACAISonsServerSessions.HooksInstalled
                        ? OpenACAISonsWorldReadStatus.SessionUnavailable : OpenACAISonsWorldReadStatus.Unavailable;
                    return false;
                }

                var entity = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                var captured = Capture(entity, null, connection);
                if (!OpenACAISonsServerSessions.HooksInstalled)
                {
                    status = OpenACAISonsWorldReadStatus.Unavailable;
                    return false;
                }
                if (captured == null)
                {
                    status = OpenACAISonsWorldReadStatus.EntityUnavailable;
                    return false;
                }
                if (!ReferenceEquals(captured.SourceSession, session))
                {
                    status = OpenACAISonsWorldReadStatus.SessionUnavailable;
                    return false;
                }

                snapshot = captured;
                status = OpenACAISonsWorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsWorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Looks up an attached entity by its complete current-world network ID.
        /// The lookup grants no access permission and does not establish a player identity.
        /// </summary>
        public static bool TryReadEntity(ulong networkId, out OpenACAISonsEntitySnapshot snapshot,
            out OpenACAISonsWorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;

                var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                snapshot = Capture(entity, networkId, null);
                status = snapshot == null ? OpenACAISonsWorldReadStatus.EntityUnavailable : OpenACAISonsWorldReadStatus.Success;
                return snapshot != null;
            }
            catch (Exception error)
            {
                status = OpenACAISonsWorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static OpenACAISonsWorldReadStatus CheckContext()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsWorldReadStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return OpenACAISonsWorldReadStatus.WrongThread;
            return BoltNetwork.isRunning && BoltNetwork.isServer
                ? OpenACAISonsWorldReadStatus.Success : OpenACAISonsWorldReadStatus.NotServer;
        }

        private static OpenACAISonsEntitySnapshot Capture(BoltEntity entity, ulong? expectedNetworkId, BoltConnection expectedSource)
        {
            if (entity is null || entity.WasCollected || !entity || !entity.isAttached)
                return null;

            var networkId = entity.networkId.PackedValue;
            if (expectedNetworkId.HasValue && networkId != expectedNetworkId.Value)
                return null;
            var source = entity.source;
            if (expectedSource != null && (source == null || source.Pointer != expectedSource.Pointer
                || source.ConnectionId != expectedSource.ConnectionId))
                return null;

            var transform = entity.transform;
            if (transform is null || transform.WasCollected || !transform)
                return null;
            var controller = entity.controller;
            uint? sourceId = source == null ? null : source.ConnectionId;
            uint? controllerId = controller == null ? null : controller.ConnectionId;
            var position = transform.position;
            var rotation = transform.rotation;
            var prefabId = entity.prefabId.Value;
            var serverOwned = entity.isOwner;
            var sourceSession = OpenACAISonsServerSessions.ResolveSource(source);
            var controllerSession = source != null && controller != null && source.Pointer == controller.Pointer
                ? sourceSession : OpenACAISonsServerSessions.ResolveSource(controller);

            return new OpenACAISonsEntitySnapshot(networkId, prefabId,
                new Vector3(position.x, position.y, position.z),
                new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                serverOwned, sourceId, controllerId, sourceSession, controllerSession);
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server world read failed ("
                    + error.GetType().FullName + "). Further read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsWorldReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        NotServer,
        SessionUnavailable,
        EntityUnavailable,
        Failed,
        PlayerStateUnavailable,
        BlueprintUnavailable,
        BindingChanged,
        VehicleUnavailable,
        VehicleStateUnavailable
    }

    /// <summary>
    /// Managed copies of server-observed entity fields, with no retained native objects.
    /// The transform may reflect client replication; successful reading does not validate gameplay.
    /// </summary>
    public sealed class OpenACAISonsEntitySnapshot
    {
        internal OpenACAISonsEntitySnapshot(ulong networkId, int prefabId, Vector3 position, Quaternion rotation,
            bool isServerOwned, uint? sourceConnectionId, uint? controllerConnectionId,
            OpenACAISonsServerSession sourceSession, OpenACAISonsServerSession controllerSession)
        {
            NetworkId = networkId;
            PrefabId = prefabId;
            Position = position;
            Rotation = rotation;
            IsServerOwned = isServerOwned;
            SourceConnectionId = sourceConnectionId;
            ControllerConnectionId = controllerConnectionId;
            SourceSession = sourceSession;
            ControllerSession = controllerSession;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Current-world entity ID, not a persistent save or cross-session identity.</summary>
        public ulong NetworkId { get; }
        /// <summary>Bolt prefab ID, not an inventory item ID.</summary>
        public int PrefabId { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        /// <summary>Bolt's local-owner flag on the server, not proof of authority over each field.</summary>
        public bool IsServerOwned { get; }
        /// <summary>Observed Bolt source connection, or null when no reference is reported.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Observed Bolt controller connection, or null when no reference is reported.</summary>
        public uint? ControllerConnectionId { get; }
        /// <summary>Current source handle when resolvable. Its metadata can change after this read.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
        /// <summary>Current controller handle when resolvable. Null does not imply local or server control.</summary>
        public OpenACAISonsServerSession ControllerSession { get; }
        /// <summary>Read completion time, not a replication or simulation timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
