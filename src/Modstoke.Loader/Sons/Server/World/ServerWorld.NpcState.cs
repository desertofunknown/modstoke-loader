// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Ai.Vail;
using global::Sons.Animation.Mae;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Maximum transforms, and separately network-component entries, inspected per exact NPC lookup.</summary>
        public const int MaximumNpcStateLookupEntries = 256;

        /// <summary>Maximum retained characters in each copied NPC state or action name.</summary>
        public const int MaximumNpcStateNameLength = 128;

        /// <summary>
        /// Copies the single networked actor bound to an exact current-world entity. Searches only that
        /// entity's hierarchy, excluding nested entities, with a fixed lookup limit. Missing, ambiguous
        /// or over-limit bindings return false without a snapshot. Dead or disabled actors are included.
        /// Returns true for Success, Incomplete or Truncated; inspect the optional observations and flags.
        /// This is a sequential field observation, not an atomic AI snapshot or permission to change AI.
        /// </summary>
        public static bool TryReadNpcState(ulong networkId, out NpcStateSnapshot snapshot,
            out NpcStateReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckNpcStateContext();
                if (status != NpcStateReadStatus.Success) return false;
                if (networkId == 0)
                {
                    status = NpcStateReadStatus.InvalidQuery;
                    return false;
                }

                var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                if (!NpcStateLive(entity) || !entity.isAttached || entity.networkId.PackedValue != networkId)
                {
                    status = NpcStateReadStatus.EntityUnavailable;
                    return false;
                }
                var attachment = entity._entity;
                var root = entity.transform;
                if (!NpcStateNative(attachment) || !NpcStateSame(attachment.UnityObject, entity) || !NpcStateLive(root))
                {
                    status = NpcStateReadStatus.EntityUnavailable;
                    return false;
                }
                status = FindNpcStateActor(entity, root, out var network, out var actor);
                if (status != NpcStateReadStatus.Success) return false;
                if (!NpcStateLive(actor) || !NpcStateLive(network) || !NpcStateSame(network._entity, entity)
                    || !NpcStateSame(network._vailActor, actor) || !NpcStateSame(actor._actorNetworked, network))
                {
                    status = NpcStateReadStatus.BindingChanged;
                    return false;
                }

                var instanceId = actor.GetInstanceID();
                var typeId = (int)actor._id;
                var worldSimId = actor._worldSimUniqueId;
                var isServerOwned = entity.isOwner;
                var active = actor.isActiveAndEnabled;
                var dead = actor._isDead;
                var state = actor._currentState;
                if (!NpcStateNative(state))
                {
                    status = NpcStateReadStatus.Unavailable;
                    return false;
                }
                var stateId = state._id;
                var truncated = false;
                var stateName = CopyNpcStateName(state._name, ref truncated);
                var asset = actor._activeActionAsset;
                var sequence = actor._activeActionInstance;
                var stimulus = actor._activeStimuli;
                var actionStatus = CopyNpcStateAction(actor, asset, sequence, out var action);
                var targetStatus = CopyNpcStateTarget(stimulus, out var target);

                if (!NpcStateLive(entity) || !entity.isAttached || entity.networkId.PackedValue != networkId
                    || entity.isOwner != isServerOwned || !NpcStateSame(entity._entity, attachment)
                    || !NpcStateSame(attachment.UnityObject, entity)
                    || !NpcStateSame(BoltNetwork.FindEntity(new Bolt.NetworkId(networkId)), entity)
                    || !NpcStateLive(root) || !NpcStateSame(entity.transform, root)
                    || !NpcStateLive(network) || !NpcStateSame(network._entity, entity)
                    || !NpcStateLive(actor) || !NpcStateSame(network._vailActor, actor)
                    || !NpcStateSame(actor._actorNetworked, network) || actor.GetInstanceID() != instanceId
                    || (int)actor._id != typeId || actor._worldSimUniqueId != worldSimId
                    || actionStatus != NpcObservationStatus.Unavailable
                        && (!NpcStateSameOptional(actor._activeActionAsset, asset)
                            || !NpcStateSameOptional(actor._activeActionInstance, sequence))
                    || targetStatus != NpcObservationStatus.Unavailable
                        && !NpcStateSameOptional(actor._activeStimuli, stimulus))
                {
                    status = NpcStateReadStatus.BindingChanged;
                    return false;
                }
                var networkTransform = network.transform;
                if (!NpcStateLive(networkTransform) || !networkTransform.IsChildOf(root))
                {
                    status = NpcStateReadStatus.BindingChanged;
                    return false;
                }
                status = CheckNpcStateContext();
                if (status != NpcStateReadStatus.Success) return false;

                snapshot = new NpcStateSnapshot(networkId, instanceId, typeId, worldSimId,
                    isServerOwned, active, dead, stateId, stateName, truncated, actionStatus, action, targetStatus, target);
                status = snapshot.HasUnavailableObservations ? NpcStateReadStatus.Incomplete
                    : snapshot.IsTruncated ? NpcStateReadStatus.Truncated
                    : NpcStateReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = NpcStateReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static NpcStateReadStatus CheckNpcStateContext() => CheckActiveNpcContext() switch
        {
            ActiveNpcReadStatus.Success => NpcStateReadStatus.Success,
            ActiveNpcReadStatus.WrongThread => NpcStateReadStatus.WrongThread,
            ActiveNpcReadStatus.NotServer => NpcStateReadStatus.NotServer,
            _ => NpcStateReadStatus.Unavailable
        };

        private static NpcStateReadStatus FindNpcStateActor(BoltEntity entity, UnityEngine.Transform root,
            out VailActorNetworked network, out VailActor actor)
        {
            network = null;
            actor = null;
            var pending = new UnityEngine.Transform[MaximumNpcStateLookupEntries];
            pending[0] = root;
            var pendingCount = 1;
            var inspected = 0;
            var componentsInspected = 0;
            while (pendingCount != 0)
            {
                var node = pending[--pendingCount];
                inspected++;
                if (!NpcStateLive(node)) return NpcStateReadStatus.BindingChanged;
                if (node.TryGetComponent<BoltEntity>(out var nested) && !NpcStateSame(nested, entity))
                    continue;
                var components = node.GetComponents<VailActorNetworked>();
                if (components is null || components.WasCollected) return NpcStateReadStatus.Unavailable;
                if (components.Length > MaximumNpcStateLookupEntries - componentsInspected)
                    return NpcStateReadStatus.LookupLimitExceeded;
                componentsInspected += components.Length;
                foreach (var candidate in components)
                {
                    // Use the cached attachment; the public entity getter can bind a parent lazily.
                    if (!NpcStateLive(candidate) || !NpcStateSame(candidate._entity, entity)) continue;
                    var candidateActor = candidate._vailActor;
                    if (!NpcStateLive(candidateActor) || !NpcStateSame(candidateActor._actorNetworked, candidate))
                        return NpcStateReadStatus.Unavailable;
                    if (network is not null) return NpcStateReadStatus.AmbiguousActor;
                    network = candidate;
                    actor = candidateActor;
                }
                var childCount = node.childCount;
                if (childCount > MaximumNpcStateLookupEntries - inspected - pendingCount)
                    return NpcStateReadStatus.LookupLimitExceeded;
                for (var index = 0; index < childCount; index++)
                    pending[pendingCount++] = node.GetChild(index);
                if (node.childCount != childCount) return NpcStateReadStatus.BindingChanged;
            }
            return network is null ? NpcStateReadStatus.NpcUnavailable : NpcStateReadStatus.Success;
        }

        private static NpcObservationStatus CopyNpcStateAction(VailActor actor, MaeSequence asset,
            MaeSequence sequence, out NpcActionObservation observation)
        {
            observation = null;
            if (asset is null && sequence is null) return NpcObservationStatus.Absent;
            if (asset is not null && !NpcStateLive(asset) || sequence is not null && !NpcStateLive(sequence))
                return NpcObservationStatus.Unavailable;
            var truncated = false;
            var assetName = asset is null ? null : CopyNpcStateName(asset.name, ref truncated);
            var target = sequence is null ? null : sequence._target;
            bool? targetsActor = sequence is null || target is not null && !NpcStateNative(target)
                ? null : NpcStateSame(target, actor);
            observation = new NpcActionObservation(asset is null ? null : asset.GetInstanceID(),
                assetName, truncated, sequence is null ? null : sequence.GetInstanceID(),
                sequence is null ? null : sequence._current, sequence is null ? null : sequence._InUse_k__BackingField,
                sequence is null ? null : sequence._ended, sequence is null ? null : sequence._interrupted,
                sequence is null ? null : sequence._failed, targetsActor);
            if (asset is not null && !NpcStateLive(asset) || sequence is not null
                && (!NpcStateLive(sequence) || !NpcStateSameOptional(sequence._target, target)))
            {
                observation = null;
                return NpcObservationStatus.Unavailable;
            }
            return NpcObservationStatus.Available;
        }

        private static NpcObservationStatus CopyNpcStateTarget(VailActor.ActiveStimuliInfo stimulus,
            out NpcTargetObservation observation)
        {
            observation = null;
            if (stimulus is null) return NpcObservationStatus.Absent;
            if (!NpcStateNative(stimulus)) return NpcObservationStatus.Unavailable;
            var active = stimulus._active;
            var hasPosition = stimulus._hasPosition;
            Vector3? position = null;
            if (hasPosition)
            {
                var stored = stimulus._position;
                if (Finite(stored))
                    position = new Vector3(stored.x, stored.y, stored.z);
            }
            var transform = stimulus._transform;
            int? transformId = NpcStateLive(transform) ? transform.GetInstanceID() : null;
            Vector3? transformPosition = null;
            if (transformId.HasValue)
            {
                var current = transform.position;
                if (Finite(current))
                    transformPosition = new Vector3(current.x, current.y, current.z);
            }
            var complete = (!hasPosition || position.HasValue)
                && (transform is null || transformId.HasValue && transformPosition.HasValue);
            if (!NpcStateNative(stimulus) || !NpcStateSameOptional(stimulus._transform, transform)
                || transformId.HasValue && (!NpcStateLive(transform) || transform.GetInstanceID() != transformId.Value))
                return NpcObservationStatus.Unavailable;
            observation = new NpcTargetObservation(active, hasPosition, position, transformId,
                transformPosition, complete);
            return NpcObservationStatus.Available;
        }

        private static string CopyNpcStateName(string name, ref bool truncated)
        {
            if (name is null || name.Length <= MaximumNpcStateNameLength) return name;
            truncated = true;
            var length = MaximumNpcStateNameLength;
            if (char.IsHighSurrogate(name[length - 1]) && char.IsLowSurrogate(name[length])) length--;
            return name.Substring(0, length);
        }

        private static bool NpcStateNative(Il2CppObjectBase value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool NpcStateLive(UnityEngine.Object value) => NpcStateNative(value) && value;
        private static bool NpcStateSame(Il2CppObjectBase left, Il2CppObjectBase right)
            => NpcStateNative(left) && NpcStateNative(right) && left.Pointer == right.Pointer;
        private static bool NpcStateSameOptional(Il2CppObjectBase left, Il2CppObjectBase right)
            => left is null ? right is null : NpcStateSame(left, right);
    }

    public enum NpcStateReadStatus
    {
        Success,
        /// <summary>A snapshot exists, but an optional observation is unavailable or contains unknown values.</summary>
        Incomplete,
        /// <summary>A snapshot exists, but a copied name was shortened. Incomplete can also have shortened names.</summary>
        Truncated,
        InvalidQuery,
        Unavailable,
        WrongThread,
        NotServer,
        EntityUnavailable,
        NpcUnavailable,
        AmbiguousActor,
        /// <summary>No snapshot: the bounded hierarchy lookup cannot establish a single matching actor.</summary>
        LookupLimitExceeded,
        BindingChanged,
        Failed,
        /// <summary>No snapshot: the actor has no readable HealthStat, or a stat entry would make native lookup fail.</summary>
        HealthUnavailable,
        /// <summary>No snapshot: the actor's damage controller has no live node that its native ID lookup would return.</summary>
        DamageNodeUnavailable
    }

    public enum NpcObservationStatus
    {
        Unavailable,
        /// <summary>The corresponding native reference was null, not proof that AI has no work or target.</summary>
        Absent,
        Available
    }

    /// <summary>Native-free copies of one attached actor's fields, with no continuing liveness or authority guarantee.</summary>
    public sealed class NpcStateSnapshot
    {
        internal NpcStateSnapshot(ulong networkId, int actorInstanceId, int actorTypeId, int worldSimulationId,
            bool isServerOwned, bool isActiveAndEnabled, bool isDead, int stateId, string stateName, bool stateNameTruncated,
            NpcObservationStatus actionStatus, NpcActionObservation action,
            NpcObservationStatus targetStatus, NpcTargetObservation target)
        {
            NetworkId = networkId;
            ActorInstanceId = actorInstanceId;
            ActorTypeId = actorTypeId;
            WorldSimulationId = worldSimulationId;
            IsServerOwned = isServerOwned;
            IsActiveAndEnabled = isActiveAndEnabled;
            IsDead = isDead;
            StateId = stateId;
            StateName = stateName;
            StateNameTruncated = stateNameTruncated;
            ActionStatus = actionStatus;
            Action = action;
            TargetStatus = targetStatus;
            Target = target;
            CapturedAtUtc = DateTimeOffset.UtcNow;
        }

        public ulong NetworkId { get; }
        /// <summary>Unity component ID, not a durable identity across pooling or world changes.</summary>
        public int ActorInstanceId { get; }
        /// <summary>Raw VailActorTypeId value from VailActor._id.</summary>
        public int ActorTypeId { get; }
        /// <summary>Raw _worldSimUniqueId; zero or other sentinel values are not interpreted as save identities.</summary>
        public int WorldSimulationId { get; }
        public bool IsServerOwned { get; }
        public bool IsActiveAndEnabled { get; }
        /// <summary>Raw _isDead flag. False is not a health, injury, dying-state or revival eligibility check.</summary>
        public bool IsDead { get; }
        /// <summary>Raw _currentState._id, with game-build-specific meaning.</summary>
        public int StateId { get; }
        /// <summary>Copied _currentState._name, possibly null or shortened; not a stable identifier.</summary>
        public string StateName { get; }
        public bool StateNameTruncated { get; }
        public NpcObservationStatus ActionStatus { get; }
        public NpcActionObservation Action { get; }
        public NpcObservationStatus TargetStatus { get; }
        public NpcTargetObservation Target { get; }
        public DateTimeOffset CapturedAtUtc { get; }
        public bool IsTruncated => StateNameTruncated || Action?.NameTruncated == true;
        public bool HasUnavailableObservations => ActionStatus == NpcObservationStatus.Unavailable
            || TargetStatus == NpcObservationStatus.Unavailable
            || Action?.SequenceInstanceId is not null && !Action.TargetsObservedActor.HasValue
            || Target?.IsComplete == false;
        public bool IsComplete => !HasUnavailableObservations && !IsTruncated;
    }

    /// <summary>Raw action asset and instance fields; no scheduling, priority or interruptibility is inferred.</summary>
    public sealed class NpcActionObservation
    {
        internal NpcActionObservation(int? assetInstanceId, string assetName, bool nameTruncated,
            int? sequenceInstanceId, int? currentStepIndex, bool? inUse, bool? ended, bool? interrupted,
            bool? failed, bool? targetsObservedActor)
        {
            AssetInstanceId = assetInstanceId;
            AssetName = assetName;
            NameTruncated = nameTruncated;
            SequenceInstanceId = sequenceInstanceId;
            CurrentStepIndex = currentStepIndex;
            InUse = inUse;
            Ended = ended;
            Interrupted = interrupted;
            Failed = failed;
            TargetsObservedActor = targetsObservedActor;
        }

        public int? AssetInstanceId { get; }
        /// <summary>Unity asset name, possibly null or shortened; not a unique or stable action identifier.</summary>
        public string AssetName { get; }
        public bool NameTruncated { get; }
        public int? SequenceInstanceId { get; }
        /// <summary>Raw MaeSequence._current; not validated against the action list.</summary>
        public int? CurrentStepIndex { get; }
        /// <summary>Null sequence fields mean no readable instance, not false or zero.</summary>
        public bool? InUse { get; }
        public bool? Ended { get; }
        public bool? Interrupted { get; }
        public bool? Failed { get; }
        /// <summary>Whether the sequence's animator target is this actor, not the actor's opponent or stimulus.</summary>
        public bool? TargetsObservedActor { get; }
    }

    /// <summary>Copied active-stimulus cache fields, not visibility, hostility, reachability or a validated attack target.</summary>
    public sealed class NpcTargetObservation
    {
        internal NpcTargetObservation(bool active, bool hasStoredPosition, Vector3? storedPosition,
            int? transformInstanceId, Vector3? transformPosition, bool isComplete)
        {
            Active = active;
            HasStoredPosition = hasStoredPosition;
            StoredPosition = storedPosition;
            TransformInstanceId = transformInstanceId;
            TransformPosition = transformPosition;
            IsComplete = isComplete;
        }

        public bool Active { get; }
        public bool HasStoredPosition { get; }
        /// <summary>
        /// Raw cached _position when _hasPosition is true and finite. May be stale, including when inactive;
        /// does not invoke the stimulus position getter or refresh tracking. Null never means world origin.
        /// </summary>
        public Vector3? StoredPosition { get; }
        /// <summary>Live cached transform's Unity ID, if readable; not a player, actor or network identity.</summary>
        public int? TransformInstanceId { get; }
        /// <summary>Observed world position of that transform, if live and finite; not the stimulus position getter.</summary>
        public Vector3? TransformPosition { get; }
        public bool IsComplete { get; }
    }
}
