// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using global::Sons.Ai.Vail;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerCombatIngress
    {
        internal sealed class AcceptedHit
        {
            internal PlayerCombatIngress Owner { get; }
            internal Guid Migration { get; }
            internal ulong MigrationGeneration { get; }
            internal BoltPlayerSetup TargetSetup { get; }
            internal BoltEntity Target { get; }
            internal Bolt.Entity TargetAttachment { get; }
            internal ulong TargetNetworkId { get; }
            internal ulong Sequence { get; }
            internal long AcceptedAtTimestamp { get; }
            internal Origin Source { get; }
            internal EventPayload Payload { get; }
            internal AcceptedHit(PlayerCombatIngress owner, ulong sequence,
                long acceptedAtTimestamp, Origin source, EventPayload payload)
            {
                Owner = owner; Migration = owner.Migration; MigrationGeneration = owner._bridge.Plan.Generation;
                TargetSetup = owner._setup; Target = owner._target; TargetAttachment = owner._attachment;
                TargetNetworkId = owner.TargetNetworkId; Sequence = sequence; AcceptedAtTimestamp = acceptedAtTimestamp;
                Source = source; Payload = payload;
            }
        }

        /// <summary>Retained native identities and value copies at the real SendPlayerHit call.</summary>
        internal sealed class Origin
        {
            internal IImpactSender CallbackSender { get; }
            internal IImpactData Data { get; }
            internal IImpactSender DataSender { get; }
            internal Transform Root { get; }
            internal MonoBehaviour Stimuli { get; }
            internal VailActor Actor { get; }
            internal VailActorNetworked Network { get; }
            internal BoltEntity Entity { get; }
            internal Bolt.Entity Attachment { get; }
            internal int ActorInstanceId { get; }
            internal int ActorType { get; }
            internal VailActorClassId ActorClass { get; }
            internal ulong ActorNetworkId { get; }
            internal int ImpactId { get; }
            internal float AccumulatedDamage { get; }

            private Origin(IImpactSender callbackSender, IImpactData data, IImpactSender sender,
                Transform root, MonoBehaviour stimuli, VailActor actor, VailActorNetworked network,
                BoltEntity entity, Bolt.Entity attachment, VailActorClassId actorClass)
            {
                CallbackSender = callbackSender; Data = data; DataSender = sender;
                Root = root; Stimuli = stimuli; Actor = actor; Network = network;
                Entity = entity; Attachment = attachment; ActorClass = actorClass;
                ActorInstanceId = actor.GetInstanceID(); ActorType = (int)actor._id;
                ActorNetworkId = attachment.NetworkId.PackedValue;
                ImpactId = data.GetUniqueId(); AccumulatedDamage = data.AccumulatedDamage;
            }

            internal static Origin Capture(IImpactSender callbackSender, IImpactData data)
            {
                if (!Native(data)) return null;
                var sender = data.Sender;
                if (!Native(sender)) return null;
                // The native callback argument may be a forwarding node. The Vail branch
                // actually inspects data.Sender.GetRootTransform/GetSourceStimuli.
                var root = sender.GetRootTransform();
                var stimuli = sender.GetSourceStimuli();
                if (!Live(root) || !Live(stimuli)) return null;
                var actor = root.GetComponent<VailActor>();
                if (!Live(actor)) return null;
                var classes = VailTypes._typeToClass;
                int type = (int)actor._id;
                if (!Native(classes) || type < 0 || type >= classes.Length
                    || classes[type] == VailActorClassId.Other) return null;
                var network = actor._actorNetworked;
                if (!Live(network) || !Same(network._vailActor, actor)) return null;
                var entity = network._entity;
                if (!Live(entity) || !entity.isAttached) return null;
                var attachment = entity._entity;
                if (!Native(attachment) || !attachment.IsOwner || attachment.IsFrozen
                    || !Same(attachment.UnityObject, entity) || attachment.NetworkId.PackedValue == 0) return null;
                return new Origin(callbackSender, data, sender, root, stimuli, actor, network, entity, attachment, classes[type]);
            }

            internal bool StillMatchesProducer()
            {
                var classes = VailTypes._typeToClass;
                return Native(Data) && Same(Data.Sender, DataSender) && Data.GetUniqueId() == ImpactId
                    && Bits(Data.AccumulatedDamage, AccumulatedDamage)
                    && Same(DataSender.GetRootTransform(), Root) && Same(DataSender.GetSourceStimuli(), Stimuli)
                    && Live(Root) && Live(Stimuli) && Live(Actor) && Actor.GetInstanceID() == ActorInstanceId
                    && Same(Root.GetComponent<VailActor>(), Actor) && (int)Actor._id == ActorType
                    && Native(classes) && ActorType >= 0 && ActorType < classes.Length && classes[ActorType] == ActorClass
                    && Live(Network) && Same(Actor._actorNetworked, Network) && Same(Network._vailActor, Actor)
                    && Same(Network._entity, Entity) && Live(Entity) && Entity.isAttached
                    && Same(Entity._entity, Attachment) && Attachment.IsOwner && !Attachment.IsFrozen
                    && Same(Attachment.UnityObject, Entity) && Attachment.NetworkId.PackedValue == ActorNetworkId;
            }
        }

        /// <summary>Actual native event values at Send. Damage is pre-owner-armour, not health loss.</summary>
        internal sealed class EventPayload
        {
            private readonly IntPtr _event, _storage, _meta, _target;
            internal int RoutingTargets { get; }
            internal ulong PayloadTargetNetworkId { get; }
            internal int Damage { get; }
            internal int ImpactType { get; }
            internal float Weight { get; }
            internal float BurnTime { get; }
            internal Vector3 AttackerPosition { get; }

            internal EventPayload(HitPlayer hit, Bolt.Entity target)
            {
                if (!Native(hit.storage) || !Native(hit.Meta) || !Same(hit.TargetEntity, target)
                    || hit.Targets != (int)Bolt.EntityTargets.Everyone || hit.SourceConnection != null
                    || hit.TargetConnection != null || (int)hit.Reliability != 0)
                    throw new InvalidOperationException("Native HitPlayer factory envelope changed before Send.");
                _event = hit.Pointer; _storage = hit.storage.Pointer; _meta = hit.Meta.Pointer; _target = target.Pointer;
                RoutingTargets = hit.Targets;
                PayloadTargetNetworkId = hit.GetTargetNetworkIdValue().PackedValue;
                Damage = hit.Damage; ImpactType = hit.ImpactType; Weight = hit.Weight;
                BurnTime = hit.BurnTime; AttackerPosition = hit.AttackerPos;
            }

            internal bool Matches(Bolt.Event value)
            {
                if (!Native(value) || value.Pointer != _event || value.storage?.Pointer != _storage
                    || value.Meta?.Pointer != _meta || value.TargetEntity?.Pointer != _target
                    || value.Targets != RoutingTargets || value.SourceConnection != null
                    || value.TargetConnection != null || (int)value.Reliability != 0) return false;
                var hit = value.TryCast<HitPlayer>();
                if (!Native(hit)) return false;
                var position = hit.AttackerPos;
                return hit.GetTargetNetworkIdValue().PackedValue == PayloadTargetNetworkId
                    && hit.Damage == Damage && hit.ImpactType == ImpactType && Bits(hit.Weight, Weight)
                    && Bits(hit.BurnTime, BurnTime) && Bits(position.x, AttackerPosition.x)
                    && Bits(position.y, AttackerPosition.y) && Bits(position.z, AttackerPosition.z);
            }
        }

        private bool HasNativeReceiver()
        {
            var controller = _setup._damageController;
            if (!Live(controller) || !Same(controller, _controller) || !Same(controller._impactReceivers, _callback)) return false;
            Il2CppSystem.Delegate callback = controller._impactReceivers;
            if (!Native(callback)) return false;
            var entries = controller._impactReceivers.delegates;
            if (entries != null)
            {
                if (!Native(entries) || entries.Length != 1) return false;
                callback = entries[0];
                var nested = callback?.TryCast<Il2CppSystem.MulticastDelegate>();
                if (nested?.delegates != null) return false;
            }
            if (!Native(callback) || !Same(callback.Target, _setup)) return false;
            var method = callback.Method;
            return Native(method) && method.Name == nameof(BoltPlayerSetup.SendPlayerHit)
                && Same(method.DeclaringType, Il2CppType.Of<BoltPlayerSetup>());
        }

        private static bool Bits(float left, float right)
            => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    }
}
