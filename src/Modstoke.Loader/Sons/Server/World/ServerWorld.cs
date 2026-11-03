// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Read-only observations of network entities in the server's current world.</summary>
    public static partial class ServerWorld
    {
        /// <summary>Maximum requested entries, including duplicates, read by one bulk entity lookup.</summary>
        public const int MaximumEntityBatchEntries = 128;

        private static int _ownerThreadId;
        private static int _reportedFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>True while this process runs a listen or dedicated server with a live game session.</summary>
        /// <remarks>Reads the current Bolt and game flags without allocating; call it on the game thread.
        /// The game clears its session state before Bolt finishes stopping, so this turns false first during
        /// shutdown. It describes this process only and grants nothing to any player.</remarks>
        public static bool IsServerActive => BoltNetwork.isRunning && BoltNetwork.isServer && GameServerManager.HasDedicatedState;

        /// <summary>
        /// Reads the game's registered player entity for an exact live remote session.
        /// A connected player may not yet have an attached entity. No local host player is inferred.
        /// </summary>
        public static bool TryReadPlayer(ServerSession session, out EntitySnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                if (!ServerSessions.HooksInstalled)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }

                var connection = ServerSessions.ResolveCurrent(session);
                if (connection == null)
                {
                    status = ServerSessions.HooksInstalled
                        ? WorldReadStatus.SessionUnavailable : WorldReadStatus.Unavailable;
                    return false;
                }

                var entity = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                var captured = MatchesPlayerSession(entity, connection, session) ? Capture(entity, null, null) : null;
                if (!ServerSessions.HooksInstalled)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }
                if (captured == null)
                {
                    status = WorldReadStatus.EntityUnavailable;
                    return false;
                }
                if (!MatchesPlayerSession(entity, connection, session))
                {
                    status = WorldReadStatus.SessionUnavailable;
                    return false;
                }

                snapshot = captured;
                status = WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Looks up an attached entity by its complete current-world network ID.
        /// The lookup grants no access permission and does not establish a player identity.
        /// </summary>
        public static bool TryReadEntity(ulong networkId, out EntitySnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                snapshot = Capture(entity, networkId, null);
                status = snapshot == null ? WorldReadStatus.EntityUnavailable : WorldReadStatus.Success;
                return snapshot != null;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Copies an entity's description on the live server's owner thread without retaining native objects.
        /// Returns true with Success even for a missing entity or partial description; inspect the read flags.
        /// The lookup grants no access permission and does not establish a player identity.
        /// </summary>
        public static bool TryDescribeEntity(ulong networkId, out InteractionDescription description,
            out WorldReadStatus status)
        {
            description = InteractionDescription.Unread;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                description = InteractionDescription.Capture(entity);
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Looks up the first MaximumEntityBatchEntries exact current-world network IDs without scanning entities.
        /// Input order and duplicates are preserved. Do not modify the input list during this call.
        /// Returns true with Success or Truncated when a batch is produced, even if individual reads fail;
        /// inspect every entry's status. Invalid input or an unavailable initial context returns false and null.
        /// Reads are sequential, not an atomic world snapshot, and grant no access permission or player identity.
        /// </summary>
        public static bool TryReadEntities(IReadOnlyList<ulong> networkIds, out EntityBatchSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                if (networkIds == null)
                {
                    status = WorldReadStatus.InvalidQuery;
                    return false;
                }

                var requestedCount = networkIds.Count;
                var count = Math.Min(requestedCount, MaximumEntityBatchEntries);
                var requestedIds = new ulong[count];
                for (var i = 0; i < count; i++)
                    requestedIds[i] = networkIds[i];

                var entries = new EntityReadResult[count];
                for (var i = 0; i < count; i++)
                {
                    TryReadEntity(requestedIds[i], out var entity, out var entryStatus);
                    entries[i] = new EntityReadResult(requestedIds[i], entity, entryStatus);
                }

                snapshot = new EntityBatchSnapshot(requestedCount, entries);
                status = snapshot.Truncated ? WorldReadStatus.Truncated : WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static WorldReadStatus CheckContext()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return WorldReadStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return WorldReadStatus.WrongThread;
            return IsServerActive ? WorldReadStatus.Success : WorldReadStatus.NotServer;
        }

        private static EntitySnapshot Capture(BoltEntity entity, ulong? expectedNetworkId, BoltConnection expectedSource)
            => TryCapture(entity, expectedNetworkId, expectedSource, out var observation)
                ? observation.ToSnapshot() : null;

        private static bool TryCapture(BoltEntity entity, ulong? expectedNetworkId, BoltConnection expectedSource,
            out EntityObservation observation)
        {
            observation = default;
            if (entity is null || entity.WasCollected || !entity || !entity.isAttached)
                return false;

            var networkId = entity.networkId.PackedValue;
            if (expectedNetworkId.HasValue && networkId != expectedNetworkId.Value)
                return false;
            var source = entity.source;
            if (expectedSource != null && (source == null || source.Pointer != expectedSource.Pointer
                || source.ConnectionId != expectedSource.ConnectionId))
                return false;

            var transform = entity.transform;
            if (transform is null || transform.WasCollected || !transform)
                return false;
            var controller = entity.controller;
            uint? sourceId = source == null ? null : source.ConnectionId;
            uint? controllerId = controller == null ? null : controller.ConnectionId;
            var position = transform.position;
            var rotation = transform.rotation;
            var prefabId = entity.prefabId.Value;
            var serverOwned = entity.isOwner;
            var sourceSession = ServerSessions.ResolveSource(source);
            var controllerSession = source != null && controller != null && source.Pointer == controller.Pointer
                ? sourceSession : ServerSessions.ResolveSource(controller);

            observation = new EntityObservation(networkId, prefabId,
                new Vector3(position.x, position.y, position.z),
                new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                serverOwned, sourceId, controllerId, sourceSession, controllerSession);
            return true;
        }

        private readonly record struct EntityObservation(ulong NetworkId, int PrefabId, Vector3 Position,
            Quaternion Rotation, bool IsServerOwned, uint? SourceConnectionId, uint? ControllerConnectionId,
            ServerSession SourceSession, ServerSession ControllerSession)
        {
            internal EntitySnapshot ToSnapshot() => new(NetworkId, PrefabId, Position, Rotation,
                IsServerOwned, SourceConnectionId, ControllerConnectionId, SourceSession, ControllerSession);
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server world read failed ("
                    + error.GetType().FullName + "). Further read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum WorldReadStatus
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
        VehicleStateUnavailable,
        /// <summary>The bulk entity request has no input list.</summary>
        InvalidQuery,
        /// <summary>A bulk result is available, but requests beyond its entry limit were not read.</summary>
        Truncated
    }

    /// <summary>
    /// Managed copies of server-observed entity fields, with no retained native objects.
    /// The transform may reflect client replication; successful reading does not validate gameplay.
    /// </summary>
    public sealed class EntitySnapshot
    {
        internal EntitySnapshot(ulong networkId, int prefabId, Vector3 position, Quaternion rotation,
            bool isServerOwned, uint? sourceConnectionId, uint? controllerConnectionId,
            ServerSession sourceSession, ServerSession controllerSession)
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
        public ServerSession SourceSession { get; }
        /// <summary>Current controller handle when resolvable. Null does not imply local or server control.</summary>
        public ServerSession ControllerSession { get; }
        /// <summary>Read completion time, not a replication or simulation timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>One exact-ID lookup result. Failure preserves the requested ID and carries no entity snapshot.</summary>
    public sealed class EntityReadResult
    {
        internal EntityReadResult(ulong requestedNetworkId, EntitySnapshot snapshot,
            WorldReadStatus status)
        {
            RequestedNetworkId = requestedNetworkId;
            Snapshot = snapshot;
            Status = status;
        }

        /// <summary>The unmodified requested ID, including zero or an unresolved ID.</summary>
        public ulong RequestedNetworkId { get; }
        /// <summary>The individual read's outcome, independent of the batch's completion status.</summary>
        public WorldReadStatus Status { get; }
        /// <summary>Copied entity fields on Success; otherwise null. Session handle metadata can change.</summary>
        public EntitySnapshot Snapshot { get; }
    }

    /// <summary>
    /// Bounded, ordered lookup results containing no native entity, transform or connection references.
    /// Entries are sequential observations, not simultaneous or persistent cross-world identities.
    /// </summary>
    public sealed class EntityBatchSnapshot
    {
        internal EntityBatchSnapshot(int requestedCount, EntityReadResult[] entries)
        {
            RequestedCount = requestedCount;
            Entries = Array.AsReadOnly(entries);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Input list size observed before lookup, including duplicates and unread trailing IDs.</summary>
        public int RequestedCount { get; }
        /// <summary>Results for the input prefix, in the same order. An unavailable entry is never omitted.</summary>
        public IReadOnlyList<EntityReadResult> Entries { get; }
        /// <summary>True when trailing requested IDs were not read because the batch limit was reached.</summary>
        public bool Truncated => Entries.Count < RequestedCount;
        /// <summary>Batch completion time, not a replication or simulation timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
