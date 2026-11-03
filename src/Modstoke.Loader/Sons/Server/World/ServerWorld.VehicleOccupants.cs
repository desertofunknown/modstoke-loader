// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Gameplay;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Maximum number of indexed connection observations returned by one golf-cart read.</summary>
        public const int MaximumGolfCartOccupantSlots = 32;

        /// <summary>
        /// Reads a physical cart's local connection flags, local actor presence and replicated slot references
        /// on the server game thread. Ghost carts return VehicleUnavailable. No seat is declared safe or empty.
        /// Returns true with Success or Truncated; unavailable per-slot sources remain explicit in the snapshot.
        /// </summary>
        public static bool TryReadGolfCartOccupants(ulong networkId, out GolfCartOccupantsSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureGolfCartOccupants(networkId, out var entity, out var before, out var values);
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureGolfCartOccupants(networkId, out _, out var after, out var currentValues);
                if (status != WorldReadStatus.Success)
                    return false;
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                if (before != after || values.Length != currentValues.Length)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] != currentValues[i])
                    {
                        status = WorldReadStatus.BindingChanged;
                        return false;
                    }
                }

                var slots = new GolfCartOccupantSlotSnapshot[values.Length];
                for (var i = 0; i < slots.Length; i++)
                {
                    var value = values[i];
                    slots[i] = new GolfCartOccupantSlotSnapshot(i, value.LocalConnected,
                        value.HasLocalActor, value.NetworkId, value.ReferenceStatus);
                }
                snapshot = new GolfCartOccupantsSnapshot(entity, before.Cart.SlotCount,
                    before.LocalConnectionCount, before.LocalActorCount, slots);
                status = snapshot.IsTruncated ? WorldReadStatus.Truncated : WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static WorldReadStatus CaptureGolfCartOccupants(ulong networkId,
            out EntitySnapshot entity, out GolfCartOccupantsBinding binding,
            out GolfCartOccupantSlotValues[] values)
        {
            binding = default;
            values = null;
            var status = CaptureGolfCart(networkId, out entity, out var networkSlots, out var cartBinding);
            if (status != WorldReadStatus.Success)
                return status;
            if (cartBinding.Kind != GolfCartKind.Cart)
                return WorldReadStatus.VehicleUnavailable;

            // This constructor wraps the controller already matched by CaptureGolfCart; it does not create a cart.
            var cart = new GolfCartController(cartBinding.ComponentPointer);
            if (!IsLiveVehicleComponent(cart))
                return WorldReadStatus.VehicleUnavailable;
            var cartEntity = cart.entity;
            if (!IsLiveRangeEntity(cartEntity) || cartEntity.Pointer != cartBinding.Entity.EntityPointer
                || cartEntity.networkId.PackedValue != networkId)
                return WorldReadStatus.BindingChanged;

            var localConnections = cart._isLocalConnectedSlots;
            var localActors = cart._localConnectedActors;
            int? localConnectionCount = localConnections is not null && !localConnections.WasCollected
                && localConnections.Length >= 0 ? localConnections.Length : null;
            int? localActorCount = localActors is not null && !localActors.WasCollected
                && localActors.Length >= 0 ? localActors.Length : null;
            binding = new GolfCartOccupantsBinding(cartBinding,
                localConnectionCount.HasValue ? localConnections.Pointer : IntPtr.Zero, localConnectionCount,
                localActorCount.HasValue ? localActors.Pointer : IntPtr.Zero, localActorCount);
            var count = Math.Max(cartBinding.SlotCount, Math.Max(localConnectionCount ?? 0, localActorCount ?? 0));
            values = new GolfCartOccupantSlotValues[Math.Min(count, MaximumGolfCartOccupantSlots)];
            for (var i = 0; i < values.Length; i++)
            {
                bool? localConnected = i < (localConnectionCount ?? 0) ? localConnections[i] : null;
                bool? hasLocalActor = null;
                var actorPointer = IntPtr.Zero;
                if (i < (localActorCount ?? 0))
                {
                    var actor = localActors[i];
                    if (actor is null)
                        hasLocalActor = false;
                    else if (!actor.WasCollected)
                    {
                        actorPointer = actor.Pointer;
                        hasLocalActor = IsLiveVehicleComponent(actor);
                    }
                }

                ulong? slotId = i < cartBinding.SlotCount ? networkSlots.GetValueNetworkId(i).PackedValue : null;
                var referenceStatus = GolfCartSlotReferenceStatus.Unavailable;
                var referenceBinding = default(VehicleEntityBinding);
                if (slotId.HasValue)
                {
                    referenceStatus = GolfCartSlotReferenceStatus.NoReference;
                    if (slotId.Value != 0)
                    {
                        var linkedEntity = BoltNetwork.FindEntity(new Bolt.NetworkId(slotId.Value));
                        referenceStatus = TryCaptureVehicleEntity(linkedEntity, slotId.Value, out _, out referenceBinding)
                            ? GolfCartSlotReferenceStatus.Resolved
                            : GolfCartSlotReferenceStatus.Unresolved;
                    }
                }
                values[i] = new GolfCartOccupantSlotValues(localConnected, hasLocalActor, actorPointer,
                    slotId, referenceStatus, referenceBinding);
            }
            return WorldReadStatus.Success;
        }

        private readonly record struct GolfCartOccupantsBinding(GolfCartBinding Cart,
            IntPtr LocalConnectionsPointer, int? LocalConnectionCount, IntPtr LocalActorsPointer, int? LocalActorCount);

        private readonly record struct GolfCartOccupantSlotValues(bool? LocalConnected, bool? HasLocalActor,
            IntPtr ActorPointer, ulong? NetworkId, GolfCartSlotReferenceStatus ReferenceStatus,
            VehicleEntityBinding ReferenceBinding);
    }

    public enum GolfCartSlotReferenceStatus
    {
        /// <summary>The replicated slot array does not contain this index.</summary>
        Unavailable,
        /// <summary>The raw replicated reference is zero; this does not establish an empty seat.</summary>
        NoReference,
        /// <summary>A nonzero reference was copied, but a matching live attached entity could not be captured.</summary>
        Unresolved,
        /// <summary>A matching live attached entity was captured. It need not be a player or the occupant itself.</summary>
        Resolved
    }

    public enum GolfCartConnectionObservation
    {
        /// <summary>No positive signal was observed and at least one source is unavailable or unresolved.</summary>
        Unknown,
        /// <summary>A local connection flag, live local actor or resolved replicated link was observed.</summary>
        ConnectionObserved,
        /// <summary>All three sources were readable and negative. Not proof of an empty or available seat.</summary>
        NoConnectionObserved
    }

    /// <summary>Copied connection observations for one native slot index, not an authenticated occupant identity.</summary>
    public sealed class GolfCartOccupantSlotSnapshot
    {
        internal GolfCartOccupantSlotSnapshot(int slotIndex, bool? localConnected, bool? hasLocalActor,
            ulong? connectedSlotNetworkId, GolfCartSlotReferenceStatus referenceStatus)
        {
            SlotIndex = slotIndex;
            LocalConnected = localConnected;
            HasLocalActor = hasLocalActor;
            ConnectedSlotNetworkId = connectedSlotNetworkId;
            ReferenceStatus = referenceStatus;
            Connection = localConnected == true || hasLocalActor == true
                || referenceStatus == GolfCartSlotReferenceStatus.Resolved
                ? GolfCartConnectionObservation.ConnectionObserved
                : localConnected == false && hasLocalActor == false
                    && referenceStatus == GolfCartSlotReferenceStatus.NoReference
                    ? GolfCartConnectionObservation.NoConnectionObserved
                    : GolfCartConnectionObservation.Unknown;
        }

        public int SlotIndex { get; }
        /// <summary>Copied _isLocalConnectedSlots value. Null means the local array/index was unavailable.</summary>
        public bool? LocalConnected { get; }
        /// <summary>Live Unity actor presence in _localConnectedActors, not player presence. Null means unavailable.</summary>
        public bool? HasLocalActor { get; }
        /// <summary>Raw link; null means no indexed source, zero means no reference. A driver link may name a ghost cart.</summary>
        public ulong? ConnectedSlotNetworkId { get; }
        public GolfCartSlotReferenceStatus ReferenceStatus { get; }
        /// <summary>Connection evidence only, never ownership, permission to enter, or proof of physical occupancy.</summary>
        public GolfCartConnectionObservation Connection { get; }
    }

    /// <summary>
    /// Detached physical-cart observations with matching bindings and values across two reads.
    /// This is not an atomic simulation snapshot, a roster of players, or an authority grant.
    /// </summary>
    public sealed class GolfCartOccupantsSnapshot
    {
        internal GolfCartOccupantsSnapshot(EntitySnapshot entity, int replicatedSlotCount,
            int? localConnectionSlotCount, int? localActorSlotCount, GolfCartOccupantSlotSnapshot[] slots)
        {
            Entity = entity;
            ReplicatedSlotCount = replicatedSlotCount;
            LocalConnectionSlotCount = localConnectionSlotCount;
            LocalActorSlotCount = localActorSlotCount;
            SlotCount = Math.Max(replicatedSlotCount, Math.Max(localConnectionSlotCount ?? 0, localActorSlotCount ?? 0));
            Slots = Array.AsReadOnly(slots);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public EntitySnapshot Entity { get; }
        public int ReplicatedSlotCount { get; }
        /// <summary>Null means the local connection array was unavailable, not that it contained zero entries.</summary>
        public int? LocalConnectionSlotCount { get; }
        /// <summary>Null means the local actor array was unavailable, not that it contained zero entries.</summary>
        public int? LocalActorSlotCount { get; }
        /// <summary>Largest observed source-array length, not a declared seat capacity or occupant count.</summary>
        public int SlotCount { get; }
        /// <summary>Bounded prefix of native indices. Missing local or replicated sources remain nullable per entry.</summary>
        public IReadOnlyList<GolfCartOccupantSlotSnapshot> Slots { get; }
        public bool IsTruncated => Slots.Count < SlotCount;
        /// <summary>Local read completion time, not a replication or simulation timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
