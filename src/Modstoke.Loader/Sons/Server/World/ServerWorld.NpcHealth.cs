// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using global::Sons.Ai.Vail;
using global::Sons.StatSystem;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Copies the health stat of the single networked actor bound to an exact current-world entity,
        /// resolved as TryReadNpcState does. Field reads only: VailActor.GetHealth and GetMaxHealth are
        /// not called, and no stat callbacks run. A value is not damage history, attribution or a promise
        /// about the next hit; status effects and other server-local damage change it at any time.
        /// </summary>
        public static bool TryReadNpcHealth(ulong networkId, out NpcHealthSnapshot snapshot,
            out NpcStateReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckNpcStateContext();
                if (status != NpcStateReadStatus.Success) return false;
                status = NpcActorProbe.TryBind(networkId, out var probe);
                if (status != NpcStateReadStatus.Success) return false;
                status = probe.TryRead(out var health, out var maxHealth, out var dead);
                if (status != NpcStateReadStatus.Success) return false;
                status = CheckNpcStateContext();
                if (status != NpcStateReadStatus.Success) return false;

                snapshot = new NpcHealthSnapshot(networkId, probe.ActorInstanceId, probe.ActorTypeId,
                    probe.IsServerOwned, health, maxHealth, dead, UnityEngine.Time.frameCount);
                return true;
            }
            catch (Exception error)
            {
                status = NpcStateReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        // Retains the resolved actor binding so a later read in the same callback can prove it is unchanged.
        internal sealed class NpcActorProbe
        {
            private readonly BoltEntity _entity;
            private readonly VailActorNetworked _network;
            private readonly VailActor _actor;

            private NpcActorProbe(ulong networkId, BoltEntity entity, VailActorNetworked network, VailActor actor)
            {
                NetworkId = networkId;
                _entity = entity;
                _network = network;
                _actor = actor;
                ActorInstanceId = actor.GetInstanceID();
                ActorTypeId = (int)actor._id;
                IsServerOwned = entity.isOwner;
            }

            internal ulong NetworkId { get; }
            internal int ActorInstanceId { get; }
            internal int ActorTypeId { get; }
            internal bool IsServerOwned { get; }
            internal VailActor Actor => _actor;

            internal static NpcStateReadStatus TryBind(ulong networkId, out NpcActorProbe probe)
            {
                probe = null;
                if (networkId == 0) return NpcStateReadStatus.InvalidQuery;
                var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                if (!NpcStateLive(entity) || !entity.isAttached || entity.networkId.PackedValue != networkId)
                    return NpcStateReadStatus.EntityUnavailable;
                var attachment = entity._entity;
                var root = entity.transform;
                if (!NpcStateNative(attachment) || !NpcStateSame(attachment.UnityObject, entity) || !NpcStateLive(root))
                    return NpcStateReadStatus.EntityUnavailable;
                var status = FindNpcStateActor(entity, root, out var network, out var actor);
                if (status != NpcStateReadStatus.Success) return status;
                if (!NpcStateLive(actor) || !NpcStateLive(network) || !NpcStateSame(network._entity, entity)
                    || !NpcStateSame(network._vailActor, actor) || !NpcStateSame(actor._actorNetworked, network))
                    return NpcStateReadStatus.BindingChanged;
                probe = new NpcActorProbe(networkId, entity, network, actor);
                return NpcStateReadStatus.Success;
            }

            internal bool IsCurrent()
                => NpcStateLive(_entity) && _entity.isAttached && _entity.networkId.PackedValue == NetworkId
                    && _entity.isOwner == IsServerOwned
                    && NpcStateSame(BoltNetwork.FindEntity(new Bolt.NetworkId(NetworkId)), _entity)
                    && NpcStateLive(_network) && NpcStateSame(_network._entity, _entity)
                    && NpcStateLive(_actor) && NpcStateSame(_network._vailActor, _actor)
                    && NpcStateSame(_actor._actorNetworked, _network)
                    && _actor.GetInstanceID() == ActorInstanceId && (int)_actor._id == ActorTypeId;

            internal NpcStateReadStatus TryRead(out float health, out float maxHealth, out bool dead)
            {
                health = maxHealth = 0;
                dead = false;
                if (!IsCurrent()) return NpcStateReadStatus.BindingChanged;
                var status = FindHealthStat(_actor, out var manager, out var stats, out var count, out var stat);
                if (status != NpcStateReadStatus.Success) return status;
                health = stat._currentValue;
                maxHealth = stat._max;
                dead = _actor._isDead;
                if (FindHealthStat(_actor, out var currentManager, out var currentStats, out var currentCount,
                        out var current) != NpcStateReadStatus.Success
                    || !NpcStateSame(currentManager, manager) || !NpcStateSame(currentStats, stats)
                    || currentCount != count || !NpcStateSame(current, stat)
                    || BitConverter.SingleToInt32Bits(current._currentValue) != BitConverter.SingleToInt32Bits(health)
                    || BitConverter.SingleToInt32Bits(current._max) != BitConverter.SingleToInt32Bits(maxHealth)
                    || _actor._isDead != dead || !IsCurrent())
                    return NpcStateReadStatus.BindingChanged;
                return float.IsFinite(health) && float.IsFinite(maxHealth)
                    ? NpcStateReadStatus.Success : NpcStateReadStatus.HealthUnavailable;
            }

            // Native GetHealth takes the first stat whose exact class is HealthStat and reads its current value.
            private static NpcStateReadStatus FindHealthStat(VailActor actor, out StatsManager manager,
                out Il2CppSystem.Collections.Generic.List<Stat> stats, out int count, out Stat stat)
            {
                manager = null;
                stats = null;
                count = 0;
                stat = null;
                var owner = actor._statsManager;
                if (!NpcStateLive(owner)) return NpcStateReadStatus.HealthUnavailable;
                manager = owner._statsManager;
                if (!NpcStateNative(manager)) return NpcStateReadStatus.HealthUnavailable;
                stats = manager._stats;
                if (!NpcStateNative(stats)) return NpcStateReadStatus.HealthUnavailable;
                count = stats.Count;
                if (count < 0) return NpcStateReadStatus.HealthUnavailable;
                if (count > MaximumNpcStateLookupEntries) return NpcStateReadStatus.LookupLimitExceeded;
                var healthClass = Il2CppClassPointerStore<HealthStat>.NativeClassPtr;
                for (var index = 0; index < count; index++)
                {
                    var candidate = stats[index];
                    // A null entry before the health stat would make the native lookup throw.
                    if (!NpcStateNative(candidate)) return NpcStateReadStatus.HealthUnavailable;
                    if (IL2CPP.il2cpp_object_get_class(candidate.Pointer) != healthClass) continue;
                    stat = candidate;
                    return NpcStateReadStatus.Success;
                }
                return NpcStateReadStatus.HealthUnavailable;
            }
        }
    }

    /// <summary>
    /// Native-free copies of one actor's health stat at read time. These values are not damage dealt,
    /// not attribution to any player and not proof of invulnerability; they can change after this read.
    /// </summary>
    public sealed class NpcHealthSnapshot
    {
        internal NpcHealthSnapshot(ulong networkId, int actorInstanceId, int actorTypeId, bool isServerOwned,
            float health, float maxHealth, bool isDead, int frame)
        {
            NetworkId = networkId;
            ActorInstanceId = actorInstanceId;
            ActorTypeId = actorTypeId;
            IsServerOwned = isServerOwned;
            Health = health;
            MaxHealth = maxHealth;
            IsDead = isDead;
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
        /// <summary>Raw HealthStat._currentValue, the value native GetHealth returns. Not clamped or validated.</summary>
        public float Health { get; }
        /// <summary>Raw HealthStat._max field. GetMaxHealth is not called and may apply other rules.</summary>
        public float MaxHealth { get; }
        /// <summary>Raw _isDead flag. Health at or below zero is not interpreted as death.</summary>
        public bool IsDead { get; }
        /// <summary>Unity frame of the read, not a Bolt or simulation frame.</summary>
        public int Frame { get; }
        /// <summary>Read completion time, not the time health last changed.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
