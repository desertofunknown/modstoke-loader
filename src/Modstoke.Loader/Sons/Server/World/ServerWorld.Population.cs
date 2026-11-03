// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

extern alias SonsGame;

using System;
using System.Collections.Generic;
using global::Sons.Ai.Vail;
using global::Sons.Multiplayer;
using NativeServerInfo = SonsGame::CoopServerInfo;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Maximum registered actor entries inspected by one active-NPC read.</summary>
        public const int MaximumActiveNpcEntries = 256;

        /// <summary>
        /// Copies live, enabled actors from the game's active actor registry, without scanning scenes.
        /// Optional type filtering uses the raw VailActorTypeId value. Dormant world actors are not included;
        /// registry membership does not establish that an actor is alive or running AI.
        /// Returns true with a snapshot for Success, Truncated or Incomplete; inspect its coverage fields.
        /// maximumResults must be between 1 and MaximumActiveNpcEntries. Results follow registry order,
        /// which is not stable across calls and is not a distance ordering.
        /// </summary>
        public static bool TryReadActiveNpcs(out ActiveNpcSnapshot snapshot,
            out ActiveNpcReadStatus status, int maximumResults = 128, int? actorTypeId = null)
            => CaptureActiveNpcs(null, 0, maximumResults, actorTypeId, out snapshot, out status);

        /// <summary>
        /// Copies registered active actors whose observed transform positions are inside or on a 3D sphere.
        /// The center and nonnegative radius must be finite; maximumResults is 1..MaximumActiveNpcEntries.
        /// This is not visibility, navigation reachability or a nearest-actor query. As with TryReadActiveNpcs,
        /// true may report Truncated or Incomplete and does not establish the world's total population.
        /// </summary>
        public static bool TryReadActiveNpcsInRadius(Vector3 center, float radius,
            out ActiveNpcSnapshot snapshot, out ActiveNpcReadStatus status,
            int maximumResults = 128, int? actorTypeId = null)
            => CaptureActiveNpcs(center, radius, maximumResults, actorTypeId, out snapshot, out status);

        private static bool CaptureActiveNpcs(Vector3? center, float radius, int maximumResults, int? actorTypeId,
            out ActiveNpcSnapshot snapshot, out ActiveNpcReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckActiveNpcContext();
                if (status != ActiveNpcReadStatus.Success)
                    return false;
                if (maximumResults < 1 || maximumResults > MaximumActiveNpcEntries
                    || center.HasValue && (!Finite(center.Value) || !float.IsFinite(radius) || radius < 0))
                {
                    status = ActiveNpcReadStatus.InvalidQuery;
                    return false;
                }

                var manager = VailActorManager._instance;
                if (manager is null || manager.WasCollected || !manager)
                {
                    status = ActiveNpcReadStatus.Unavailable;
                    return false;
                }
                var actors = manager._activeActors;
                if (actors is null || actors.WasCollected)
                {
                    status = ActiveNpcReadStatus.Unavailable;
                    return false;
                }
                var registered = actors.Count;
                var version = actors._version;
                if (registered < 0)
                {
                    status = ActiveNpcReadStatus.Unavailable;
                    return false;
                }

                var inspected = Math.Min(registered, MaximumActiveNpcEntries);
                var results = new List<ActiveNpcObservation>(Math.Min(inspected, maximumResults));
                var identities = new HashSet<int>();
                var unreadable = 0;
                var matches = 0;
                var radiusSquared = (double)radius * radius;
                for (var index = 0; index < inspected; index++)
                {
                    var actor = actors[index];
                    if (actor is null || actor.WasCollected || !actor)
                    {
                        unreadable++;
                        continue;
                    }
                    if (!actor.isActiveAndEnabled)
                        continue;
                    var typeId = (int)actor._id;
                    if (actorTypeId.HasValue && actorTypeId.Value != typeId)
                        continue;
                    var transform = actor.transform;
                    if (transform is null || transform.WasCollected || !transform)
                    {
                        unreadable++;
                        continue;
                    }
                    var instanceId = actor.GetInstanceID();
                    var position = transform.position;
                    if (!Finite(position))
                    {
                        unreadable++;
                        continue;
                    }
                    if (actor.WasCollected || !actor || !actor.isActiveAndEnabled
                        || actor.GetInstanceID() != instanceId || (int)actor._id != typeId
                        || transform.WasCollected || !transform)
                    {
                        status = ActiveNpcReadStatus.BindingChanged;
                        return false;
                    }
                    var currentTransform = actor.transform;
                    if (currentTransform is null || currentTransform.WasCollected || !currentTransform
                        || currentTransform.Pointer != transform.Pointer)
                    {
                        status = ActiveNpcReadStatus.BindingChanged;
                        return false;
                    }
                    if (!identities.Add(instanceId))
                        continue;
                    if (center.HasValue)
                    {
                        var dx = (double)position.x - center.Value.X;
                        var dy = (double)position.y - center.Value.Y;
                        var dz = (double)position.z - center.Value.Z;
                        if (dx * dx + dy * dy + dz * dz > radiusSquared)
                            continue;
                    }
                    matches++;
                    if (results.Count < maximumResults)
                        results.Add(new ActiveNpcObservation(instanceId, typeId,
                            new Vector3(position.x, position.y, position.z)));
                }

                var currentManager = VailActorManager._instance;
                if (manager.WasCollected || !manager || currentManager is null || currentManager.WasCollected
                    || !currentManager || currentManager.Pointer != manager.Pointer || actors.WasCollected)
                {
                    status = ActiveNpcReadStatus.BindingChanged;
                    return false;
                }
                var currentActors = currentManager._activeActors;
                if (currentActors is null || currentActors.WasCollected || currentActors.Pointer != actors.Pointer
                    || currentActors.Count != registered || currentActors._version != version)
                {
                    status = ActiveNpcReadStatus.BindingChanged;
                    return false;
                }
                status = CheckActiveNpcContext();
                if (status != ActiveNpcReadStatus.Success)
                    return false;

                snapshot = new ActiveNpcSnapshot(results, registered, inspected, matches, unreadable);
                status = unreadable != 0 ? ActiveNpcReadStatus.Incomplete
                    : snapshot.IsTruncated ? ActiveNpcReadStatus.Truncated
                    : ActiveNpcReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = ActiveNpcReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static ActiveNpcReadStatus CheckActiveNpcContext() => CheckContext() switch
        {
            WorldReadStatus.Success => ActiveNpcReadStatus.Success,
            WorldReadStatus.WrongThread => ActiveNpcReadStatus.WrongThread,
            WorldReadStatus.NotServer => ActiveNpcReadStatus.NotServer,
            _ => ActiveNpcReadStatus.Unavailable
        };

        /// <summary>
        /// Reads the current server's population fields and replicated array lengths.
        /// These observations do not establish supported capacity or authenticated player totals.
        /// </summary>
        public static bool TryReadPopulation(out PopulationSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                var info = NativeServerInfo._instance;
                if (info is null || info.WasCollected || !info)
                {
                    status = WorldReadStatus.EntityUnavailable;
                    return false;
                }
                var entity = info.entity;
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner)
                {
                    status = WorldReadStatus.EntityUnavailable;
                    return false;
                }
                var attachment = entity._entity;
                if (attachment is null || attachment.WasCollected)
                {
                    status = WorldReadStatus.EntityUnavailable;
                    return false;
                }
                var networkId = entity.networkId.PackedValue;
                var state = info.state;
                if (state is null || state.WasCollected)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }
                var clients = state.Clients;
                var colors = state.ClientsColor;
                var steamIds = state.SteamIds;
                var roles = state.PlayerRoles;
                if (clients is null || clients.WasCollected || colors is null || colors.WasCollected
                    || steamIds is null || steamIds.WasCollected || roles is null || roles.WasCollected)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }

                var dedicated = GameServerManager.IsDedicatedServer;
                var runtimeMaximum = GameServerManager.MaxPlayers;
                var replicatedMaximum = state.MaxPlayers;
                var playerCount = state.PlayerCount;
                var clientSlots = clients.Length;
                var colorSlots = colors.Length;
                var steamIdSlots = steamIds.Length;
                var roleSlots = roles.Length;
                if (runtimeMaximum <= 0 || replicatedMaximum <= 0 || playerCount < 0
                    || clientSlots < 0 || colorSlots < 0 || steamIdSlots < 0 || roleSlots < 0)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }

                var currentInfo = NativeServerInfo._instance;
                if (currentInfo is null || currentInfo.WasCollected || !currentInfo || currentInfo.Pointer != info.Pointer)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                var currentEntity = currentInfo.entity;
                if (currentEntity is null || currentEntity.WasCollected || !currentEntity
                    || !currentEntity.isAttached || !currentEntity.isOwner || currentEntity.Pointer != entity.Pointer
                    || currentEntity.networkId.PackedValue != networkId)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                var currentAttachment = currentEntity._entity;
                var currentState = currentInfo.state;
                if (currentAttachment is null || currentAttachment.WasCollected || currentAttachment.Pointer != attachment.Pointer
                    || currentState is null || currentState.WasCollected || currentState.Pointer != state.Pointer)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                var currentClients = currentState.Clients;
                var currentColors = currentState.ClientsColor;
                var currentSteamIds = currentState.SteamIds;
                var currentRoles = currentState.PlayerRoles;
                if (currentClients is null || currentClients.WasCollected || currentClients.Pointer != clients.Pointer
                    || currentColors is null || currentColors.WasCollected || currentColors.Pointer != colors.Pointer
                    || currentSteamIds is null || currentSteamIds.WasCollected || currentSteamIds.Pointer != steamIds.Pointer
                    || currentRoles is null || currentRoles.WasCollected || currentRoles.Pointer != roles.Pointer
                    || currentClients.Length != clientSlots || currentColors.Length != colorSlots
                    || currentSteamIds.Length != steamIdSlots || currentRoles.Length != roleSlots
                    || currentState.MaxPlayers != replicatedMaximum || currentState.PlayerCount != playerCount
                    || GameServerManager.MaxPlayers != runtimeMaximum || GameServerManager.IsDedicatedServer != dedicated)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                snapshot = new PopulationSnapshot(dedicated, networkId, runtimeMaximum,
                    replicatedMaximum, playerCount, clientSlots, colorSlots, steamIdSlots, roleSlots);
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }
    }

    public enum ActiveNpcReadStatus
    {
        Success,
        /// <summary>A snapshot is available, but the inspection or result limit omitted entries.</summary>
        Truncated,
        /// <summary>A snapshot is available, but some entries could not be read. It may also be truncated.</summary>
        Incomplete,
        InvalidQuery,
        Unavailable,
        WrongThread,
        NotServer,
        BindingChanged,
        Failed
    }

    /// <summary>Immutable, native-free observation of an active registered actor at read time.</summary>
    public sealed class ActiveNpcObservation
    {
        internal ActiveNpcObservation(int actorInstanceId, int actorTypeId, Vector3 position)
        {
            ActorInstanceId = actorInstanceId;
            ActorTypeId = actorTypeId;
            Position = position;
        }

        /// <summary>
        /// Unity component instance ID, not a save or network identity. A pooled component can represent
        /// another actor later; this value is not a durable handle and grants no mutation authority.
        /// </summary>
        public int ActorInstanceId { get; }
        /// <summary>The observed VailActor._id value, expressed as a raw VailActorTypeId integer.</summary>
        public int ActorTypeId { get; }
        /// <summary>Copied world-space transform position, not a navigation target or validated location.</summary>
        public Vector3 Position { get; }
    }

    /// <summary>
    /// Immutable copies from a bounded prefix of the active actor registry. No native objects are retained.
    /// Coverage describes this read and its filters, not dormant actors, save contents or a frozen world.
    /// </summary>
    public sealed class ActiveNpcSnapshot
    {
        internal ActiveNpcSnapshot(List<ActiveNpcObservation> actors,
            int registeredEntries, int inspectedEntries, int observedMatches, int unreadableEntries)
        {
            Actors = Array.AsReadOnly(actors.ToArray());
            RegisteredEntries = registeredEntries;
            InspectedEntries = inspectedEntries;
            ObservedMatches = observedMatches;
            UnreadableEntries = unreadableEntries;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Distinct live, enabled actors matching the query, up to the requested result limit.</summary>
        public IReadOnlyList<ActiveNpcObservation> Actors { get; }
        /// <summary>Raw registry length, including entries that may be inactive, duplicated or unreadable.</summary>
        public int RegisteredEntries { get; }
        /// <summary>Registry entries visited, at most MaximumActiveNpcEntries.</summary>
        public int InspectedEntries { get; }
        /// <summary>Distinct readable matches in the inspected prefix, including matches beyond the result limit.</summary>
        public int ObservedMatches { get; }
        /// <summary>Entries that could not be classified or read; known type mismatches and inactive actors are excluded.</summary>
        public int UnreadableEntries { get; }
        /// <summary>The inspection limit left registry entries unvisited, or the result limit omitted observed matches.</summary>
        public bool IsTruncated => InspectedEntries < RegisteredEntries || Actors.Count < ObservedMatches;
        /// <summary>Every registry entry was inspected, with no unreadable entries or omitted matching results.</summary>
        public bool IsComplete => !IsTruncated && UnreadableEntries == 0;
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>
    /// Immutable copies of observed server population fields, without native references or player identities.
    /// Maxima and slot lengths are not capacity guarantees; mismatches remain visible to callers.
    /// </summary>
    public sealed class PopulationSnapshot
    {
        internal PopulationSnapshot(bool isDedicatedServer, ulong serverInfoNetworkId,
            int runtimeMaximumPlayers, int replicatedMaximumPlayers, int replicatedPlayerCount,
            int clientSlots, int clientColorSlots, int steamIdSlots, int roleSlots)
        {
            IsDedicatedServer = isDedicatedServer;
            ServerInfoNetworkId = serverInfoNetworkId;
            RuntimeMaximumPlayers = runtimeMaximumPlayers;
            ReplicatedMaximumPlayers = replicatedMaximumPlayers;
            ReplicatedPlayerCount = replicatedPlayerCount;
            ClientSlots = clientSlots;
            ClientColorSlots = clientColorSlots;
            SteamIdSlots = steamIdSlots;
            RoleSlots = roleSlots;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Read completion time, not a replication or simulation timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
        /// <summary>The loader's observed dedicated-server mode.</summary>
        public bool IsDedicatedServer { get; }
        /// <summary>Current-world server-info entity ID, not a persistent save or cross-session identity.</summary>
        public ulong ServerInfoNetworkId { get; }
        /// <summary>The current GameServerManager.MaxPlayers value.</summary>
        public int RuntimeMaximumPlayers { get; }
        /// <summary>The server-info state's reported maximum.</summary>
        public int ReplicatedMaximumPlayers { get; }
        /// <summary>The state's reported player count, not a count of connected or authenticated sessions.</summary>
        public int ReplicatedPlayerCount { get; }
        /// <summary>Length of the replicated Clients array, not its occupied slot count.</summary>
        public int ClientSlots { get; }
        /// <summary>Length of the replicated ClientsColor array.</summary>
        public int ClientColorSlots { get; }
        /// <summary>Length of the replicated SteamIds array; no identities are read.</summary>
        public int SteamIdSlots { get; }
        /// <summary>Length of the replicated PlayerRoles array.</summary>
        public int RoleSlots { get; }
    }
}
