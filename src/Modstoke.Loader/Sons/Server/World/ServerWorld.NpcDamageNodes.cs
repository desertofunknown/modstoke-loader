// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Ai.Vail;
using global::Sons.Weapon;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Copies the damage node that the actor's damage controller would return for a node ID, resolving
        /// the actor as TryReadNpcState does. The lookup mirrors native GetNodeById and GetId with field
        /// reads only, taking the first node in controller order whose map entry has that ID; no native
        /// lookup, impact or damage method runs. A sender-chosen ID that resolves proves only that such a
        /// node exists, not that a hit touched it, nor the weapon, range or resulting health change.
        /// </summary>
        public static bool TryReadNpcDamageNode(ulong npcNetworkId, int damageNodeId,
            out NpcDamageNodeSnapshot snapshot, out NpcStateReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckNpcStateContext();
                if (status != NpcStateReadStatus.Success) return false;
                status = NpcActorProbe.TryBind(npcNetworkId, out var probe);
                if (status != NpcStateReadStatus.Success) return false;

                var actor = probe.Actor;
                status = FindNpcDamageNode(actor, damageNodeId, out var controller, out var nodes, out var count, out var node);
                if (status != NpcStateReadStatus.Success) return false;
                var fields = new DamageNodeFields(node, true);
                if (!fields.Valid())
                {
                    status = NpcStateReadStatus.DamageNodeUnavailable;
                    return false;
                }
                if (FindNpcDamageNode(actor, damageNodeId, out var currentController, out var currentNodes,
                        out var currentCount, out var current) != NpcStateReadStatus.Success
                    || !NpcStateSame(currentController, controller) || !NpcStateSame(currentNodes, nodes)
                    || currentCount != count || !NpcStateSame(current, node) || !fields.MatchesNode(current)
                    || !fields.MatchesFields(new DamageNodeFields(current, true)) || !probe.IsCurrent())
                {
                    status = NpcStateReadStatus.BindingChanged;
                    return false;
                }
                status = CheckNpcStateContext();
                if (status != NpcStateReadStatus.Success) return false;

                snapshot = new NpcDamageNodeSnapshot(npcNetworkId, probe.ActorInstanceId, probe.ActorTypeId,
                    probe.IsServerOwned, damageNodeId, fields, UnityEngine.Time.frameCount);
                return true;
            }
            catch (Exception error)
            {
                status = NpcStateReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static NpcStateReadStatus FindNpcDamageNode(VailActor actor, int damageNodeId,
            out DamageController controller, out Il2CppSystem.Collections.Generic.List<DamageNode> nodes,
            out int count, out DamageNode node)
        {
            nodes = null;
            count = 0;
            node = null;
            controller = actor._damageController;
            if (!NpcStateLive(controller)) return NpcStateReadStatus.DamageNodeUnavailable;
            nodes = controller._damageNodes;
            if (!NpcStateNative(nodes)) return NpcStateReadStatus.DamageNodeUnavailable;
            count = nodes.Count;
            if (count < 0) return NpcStateReadStatus.DamageNodeUnavailable;
            if (count > MaximumNpcStateLookupEntries) return NpcStateReadStatus.LookupLimitExceeded;
            for (var index = 0; index < count; index++)
            {
                var candidate = nodes[index];
                // Native GetNodeById throws on a null entry before its match, so no later node is reachable.
                if (!NpcStateNative(candidate)) return NpcStateReadStatus.DamageNodeUnavailable;
                var status = ReadDamageNodeId(candidate, out var id);
                if (status != NpcStateReadStatus.Success) return status;
                if (id != damageNodeId) continue;
                node = candidate;
                return NpcStateLive(candidate) && NpcStateLive(candidate.gameObject)
                    ? NpcStateReadStatus.Success : NpcStateReadStatus.DamageNodeUnavailable;
            }
            return NpcStateReadStatus.DamageNodeUnavailable;
        }

        // Native DamageNode.GetId: -1 without a live map, else the first map entry whose name equals _name.
        private static NpcStateReadStatus ReadDamageNodeId(DamageNode node, out int id)
        {
            id = -1;
            var map = node._nodeMap;
            if (!NpcStateLive(map)) return NpcStateReadStatus.Success;
            var ids = map._ids;
            if (!NpcStateNative(ids)) return NpcStateReadStatus.DamageNodeUnavailable;
            var count = ids.Count;
            if (count < 0) return NpcStateReadStatus.DamageNodeUnavailable;
            if (count > MaximumNpcStateLookupEntries) return NpcStateReadStatus.LookupLimitExceeded;
            var name = node._name;
            for (var index = 0; index < count; index++)
            {
                var entry = ids[index];
                if (!NpcStateNative(entry)) return NpcStateReadStatus.DamageNodeUnavailable;
                if (!string.Equals(entry._name, name, StringComparison.Ordinal)) continue;
                id = entry._id;
                return NpcStateReadStatus.Success;
            }
            return NpcStateReadStatus.Success;
        }
    }

    /// <summary>
    /// Copied fields of one actor damage node at read time. Native values are mutated by every impact,
    /// and these copies do not establish native impact admission, armour resolution or the final health change.
    /// </summary>
    public sealed class NpcDamageNodeSnapshot
    {
        private readonly ServerWorld.DamageNodeFields _fields;

        internal NpcDamageNodeSnapshot(ulong networkId, int actorInstanceId, int actorTypeId, bool isServerOwned,
            int nodeId, ServerWorld.DamageNodeFields fields, int frame)
        {
            NetworkId = networkId;
            ActorInstanceId = actorInstanceId;
            ActorTypeId = actorTypeId;
            IsServerOwned = isServerOwned;
            NodeId = nodeId;
            _fields = fields;
            Frame = frame;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong NetworkId { get; }
        /// <summary>Unity component ID, not a durable identity across pooling or world changes.</summary>
        public int ActorInstanceId { get; }
        /// <summary>Raw VailActorTypeId value from VailActor._id.</summary>
        public int ActorTypeId { get; }
        /// <summary>Bolt's local-owner flag. False means this is the host's copy of an entity it does not own.</summary>
        public bool IsServerOwned { get; }
        /// <summary>The requested node-map ID this node resolved from, not a weapon or item ID.</summary>
        public int NodeId { get; }
        /// <summary>Unity frame of the read, not a Bolt or simulation frame.</summary>
        public int Frame { get; }
        /// <summary>Read completion time, not the time of any impact.</summary>
        public DateTimeOffset ObservedAt { get; }
        /// <summary>An ephemeral Unity identifier; never a durable ownership identifier.</summary>
        public int NodeInstanceId => _fields.NodeInstanceId;
        public float ProjectileMultiplier => _fields.ProjectileMultiplier;
        /// <summary>Melee multipliers, always copied for an actor node read.</summary>
        public MeleeNodeSettings MeleeSettings => _fields.MeleeSettings;
        public float AbsorptionRemaining => _fields.AbsorptionRemaining;
        public bool UseBreak => _fields.UseBreak;
        public float BreakAmount => _fields.BreakAmount;
        public float AppliedNodeDamage => _fields.AppliedNodeDamage;
        public bool TakeDamageAfterBreak => _fields.TakeDamageAfterBreak;
        public bool UpdateRemainingDamage => _fields.UpdateRemainingDamage;
        public bool IsBroken => _fields.IsBroken;
        public bool IsEnabled => _fields.IsEnabled;
        public bool ActiveInHierarchy => _fields.ActiveInHierarchy;
        public bool IsDodging => _fields.IsDodging;
        public bool IsParrying => _fields.IsParrying;
        public bool IsArmour => _fields.IsArmour;
        public bool CanBlock => _fields.CanBlock;
        public bool IsBlocking => _fields.IsBlocking;
        public bool VerifyBlockDirection => _fields.VerifyBlockDirection;
        public bool UsesTeamDamage => _fields.UsesTeamDamage;
        public float TeamDamageMultiplier => _fields.TeamDamageMultiplier;
        public bool HasParent => _fields.HasParent;
        public bool HasChild => _fields.HasChild;
        public bool HasValidationOverride => _fields.HasValidationOverride;
        public bool HasBreakCallback => _fields.HasBreakCallback;
        public bool HasBlockCallback => _fields.HasBlockCallback;
        public bool HasDismemberCallback => _fields.HasDismemberCallback;
        public bool HasTeamStimuliType => _fields.HasTeamStimuliType;
        /// <summary>Raw last impact ID; it changes with every impact, including server-local ones.</summary>
        public int LastImpactId => _fields.LastImpactId;
        public float LastImpactTime => _fields.LastImpactTime;
        public bool IsBaseDamageNode => _fields.IsBaseDamageNode;
    }
}
