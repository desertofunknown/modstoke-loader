// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;
using global::Sons.Crafting.Structures;
using global::Sons.Gameplay;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Reads immutable candidate membership on the game thread without granting vehicle authority.</summary>
        /// <remarks>
        /// Covers active, enabled carts, including those awaiting Start; may also retain inactive candidates.
        /// Callers must recheck native lifetime, isActiveAndEnabled and seats. Objects remain live references.
        /// Returns false when lifecycle coverage is unavailable or the caller is off-thread; use native discovery then.
        /// </remarks>
        public static bool TryGetGolfCartCandidates(out IReadOnlyList<UnityEngine.Behaviour> carts)
            => VehiclePreservation.TryGetGolfCartCandidates(out carts);

        /// <summary>Reads an attached cart or ghost cart, preserving raw replicated slot references rather than inferring occupants.</summary>
        public static bool TryReadGolfCart(ulong networkId, out GolfCartSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureGolfCart(networkId, out var entity, out var slots, out var before);
                if (status != WorldReadStatus.Success)
                    return false;
                var ids = new ulong[before.SlotCount];
                for (var i = 0; i < ids.Length; i++)
                    ids[i] = slots.GetValueNetworkId(i).PackedValue;

                status = CaptureGolfCart(networkId, out _, out var currentSlots, out var after);
                if (status != WorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                for (var i = 0; i < ids.Length; i++)
                {
                    if (ids[i] != currentSlots.GetValueNetworkId(i).PackedValue)
                    {
                        status = WorldReadStatus.BindingChanged;
                        return false;
                    }
                }
                snapshot = new GolfCartSnapshot(entity, before.Kind, ids);
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
        /// Reads replicated cart or ghost-cart motion and power on the server game thread, not local driver inputs.
        /// Non-finite values return VehicleStateUnavailable; a changing binding or value returns BindingChanged.
        /// </summary>
        public static bool TryReadGolfCartDynamics(ulong networkId, out GolfCartDynamicsSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureGolfCart(networkId, out var entity, out _, out var state, out var before);
                if (status != WorldReadStatus.Success)
                    return false;
                var values = ReadGolfCartDynamics(state);
                if (!values.IsFinite)
                {
                    status = WorldReadStatus.VehicleStateUnavailable;
                    return false;
                }

                status = CaptureGolfCart(networkId, out _, out _, out var currentState, out var after);
                if (status != WorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                var currentValues = ReadGolfCartDynamics(currentState);
                if (!currentValues.IsFinite)
                {
                    status = WorldReadStatus.VehicleStateUnavailable;
                    return false;
                }
                if (values != currentValues)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                snapshot = new GolfCartDynamicsSnapshot(entity, before.Kind, values.Velocity,
                    values.SteeringWheelRotation, values.Power, values.FrontLeftWheelRpm,
                    values.FrontRightWheelRpm, values.BackLeftWheelRpm, values.BackRightWheelRpm);
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>Reads the exact log-sled/mobile-storage binding and its raw replicated translation-controller reference.</summary>
        public static bool TryReadLogSled(ulong networkId, out LogSledSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureLogSled(networkId, out var entity, out var before);
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureLogSled(networkId, out _, out var after);
                if (status != WorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                snapshot = new LogSledSnapshot(entity, before.TranslationControllerNetworkId);
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static WorldReadStatus CaptureGolfCart(ulong networkId,
            out EntitySnapshot snapshot, out Bolt.NetworkArray_Entity slots, out GolfCartBinding binding)
            => CaptureGolfCart(networkId, out snapshot, out slots, out _, out binding);

        private static WorldReadStatus CaptureGolfCart(ulong networkId,
            out EntitySnapshot snapshot, out Bolt.NetworkArray_Entity slots, out IGolfCartState state,
            out GolfCartBinding binding)
        {
            snapshot = null;
            slots = null;
            state = null;
            binding = default;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (!TryCaptureVehicleEntity(entity, networkId, out snapshot, out var entityBinding))
                return WorldReadStatus.EntityUnavailable;

            var componentPointer = IntPtr.Zero;
            var kind = GolfCartKind.Cart;
            var matches = 0;
            foreach (var cart in entity.GetComponentsInChildren<GolfCartController>(true))
            {
                if (IsLiveVehicleComponent(cart) && MatchesVehicleEntity(cart.entity, entity))
                {
                    componentPointer = cart.Pointer;
                    matches++;
                }
            }
            foreach (var ghost in entity.GetComponentsInChildren<GolfCartGhostController>(true))
            {
                if (IsLiveVehicleComponent(ghost) && MatchesVehicleEntity(ghost.entity, entity))
                {
                    componentPointer = ghost.Pointer;
                    kind = GolfCartKind.Ghost;
                    matches++;
                }
            }
            if (matches != 1)
                return WorldReadStatus.VehicleUnavailable;
            if (!entity.StateIs<IGolfCartState>())
                return WorldReadStatus.VehicleStateUnavailable;
            state = entity.GetState<IGolfCartState>();
            if (state is null || state.WasCollected)
                return WorldReadStatus.VehicleStateUnavailable;
            slots = state.ConnectedSlots;
            if (slots is null || slots.WasCollected || slots.Length < 0)
                return WorldReadStatus.VehicleStateUnavailable;
            binding = new GolfCartBinding(entityBinding, componentPointer, state.Pointer, slots.Pointer, slots.Length, kind);
            return WorldReadStatus.Success;
        }

        private static GolfCartDynamicsValues ReadGolfCartDynamics(IGolfCartState state)
        {
            var velocity = state.Velocity;
            return new GolfCartDynamicsValues(new Vector3(velocity.x, velocity.y, velocity.z),
                state.SteeringWheelRotation, state.Power, state.FrontLeftWheelRpm,
                state.FrontRightWheelRpm, state.BackLeftWheelRpm, state.BackRightRpm);
        }

        private static WorldReadStatus CaptureLogSled(ulong networkId,
            out EntitySnapshot snapshot, out LogSledBinding binding)
        {
            snapshot = null;
            binding = default;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (!TryCaptureVehicleEntity(entity, networkId, out snapshot, out var entityBinding))
                return WorldReadStatus.EntityUnavailable;
            if (!entity.TryGetComponent<ScrewStructureWithStorageMobile>(out var mobile)
                || !IsLiveVehicleComponent(mobile) || !MatchesVehicleEntity(mobile.entity, entity))
                return WorldReadStatus.VehicleUnavailable;

            var controllerPointer = IntPtr.Zero;
            var matches = 0;
            foreach (var controller in entity.GetComponentsInChildren<LogSledController>(true))
            {
                if (!IsLiveVehicleComponent(controller))
                    continue;
                var linkedMobile = controller._screwStructure;
                if (IsLiveVehicleComponent(linkedMobile) && linkedMobile.Pointer == mobile.Pointer
                    && MatchesVehicleEntity(controller.GetBoltEntity(), entity))
                {
                    controllerPointer = controller.Pointer;
                    matches++;
                }
            }
            if (matches != 1)
                return WorldReadStatus.VehicleUnavailable;
            if (!entity.StateIs<IScrewStructureWithStorageMobileState>())
                return WorldReadStatus.VehicleStateUnavailable;
            var state = entity.GetState<IScrewStructureWithStorageMobileState>();
            if (state is null || state.WasCollected)
                return WorldReadStatus.VehicleStateUnavailable;
            binding = new LogSledBinding(entityBinding, controllerPointer, mobile.Pointer, state.Pointer,
                state.GetTranslationControllerNetworkIdValue().PackedValue);
            return WorldReadStatus.Success;
        }

        private static bool TryCaptureVehicleEntity(BoltEntity entity, ulong networkId,
            out EntitySnapshot snapshot, out VehicleEntityBinding binding)
        {
            snapshot = null;
            binding = default;
            if (networkId == 0 || !IsLiveRangeEntity(entity) || entity.networkId.PackedValue != networkId)
                return false;
            snapshot = Capture(entity, networkId, null);
            if (snapshot == null)
                return false;
            var source = entity.source;
            var controller = entity.controller;
            // Retain native connection identity only in this read's private binding, never in the public snapshot.
            binding = new VehicleEntityBinding(entity.Pointer, source?.Pointer ?? IntPtr.Zero,
                controller?.Pointer ?? IntPtr.Zero, snapshot.PrefabId, snapshot.IsServerOwned,
                snapshot.SourceConnectionId, snapshot.ControllerConnectionId, snapshot.SourceSession, snapshot.ControllerSession);
            return true;
        }

        private static bool IsLiveVehicleComponent(UnityEngine.Component component) =>
            component is not null && !component.WasCollected && component;

        private static bool MatchesVehicleEntity(BoltEntity candidate, BoltEntity expected) =>
            IsLiveRangeEntity(candidate) && candidate.Pointer == expected.Pointer
            && candidate.networkId.PackedValue == expected.networkId.PackedValue;

        private readonly record struct VehicleEntityBinding(IntPtr EntityPointer, IntPtr SourcePointer,
            IntPtr ControllerPointer, int PrefabId, bool IsServerOwned, uint? SourceConnectionId,
            uint? ControllerConnectionId, ServerSession SourceSession, ServerSession ControllerSession);

        private readonly record struct GolfCartBinding(VehicleEntityBinding Entity, IntPtr ComponentPointer,
            IntPtr StatePointer, IntPtr SlotsPointer, int SlotCount, GolfCartKind Kind);

        private readonly record struct GolfCartDynamicsValues(Vector3 Velocity, float SteeringWheelRotation,
            float Power, float FrontLeftWheelRpm, float FrontRightWheelRpm, float BackLeftWheelRpm,
            float BackRightWheelRpm)
        {
            public bool IsFinite => float.IsFinite(Velocity.X) && float.IsFinite(Velocity.Y)
                && float.IsFinite(Velocity.Z) && float.IsFinite(SteeringWheelRotation) && float.IsFinite(Power)
                && float.IsFinite(FrontLeftWheelRpm) && float.IsFinite(FrontRightWheelRpm)
                && float.IsFinite(BackLeftWheelRpm) && float.IsFinite(BackRightWheelRpm);
        }

        private readonly record struct LogSledBinding(VehicleEntityBinding Entity, IntPtr ControllerPointer,
            IntPtr MobilePointer, IntPtr StatePointer, ulong TranslationControllerNetworkId);
    }

    public enum GolfCartKind { Cart, Ghost }

    public sealed class GolfCartSnapshot
    {
        internal GolfCartSnapshot(EntitySnapshot entity, GolfCartKind kind, ulong[] slotIds)
        {
            Entity = entity;
            Kind = kind;
            ConnectedSlotNetworkIds = Array.AsReadOnly(slotIds);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public EntitySnapshot Entity { get; }
        public GolfCartKind Kind { get; }
        /// <summary>Raw ordered links, not necessarily player IDs. Zero is no replicated reference, not proof of an empty seat.</summary>
        public IReadOnlyList<ulong> ConnectedSlotNetworkIds { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>
    /// Detached copies of IGolfCartState values for the exact observed cart kind.
    /// These are replica observations, not authoritative physics, battery capacity, occupant or drive-eligibility proof.
    /// </summary>
    public sealed class GolfCartDynamicsSnapshot
    {
        internal GolfCartDynamicsSnapshot(EntitySnapshot entity, GolfCartKind kind,
            Vector3 velocity, float steeringWheelRotation, float power, float frontLeftWheelRpm,
            float frontRightWheelRpm, float backLeftWheelRpm, float backRightWheelRpm)
        {
            Entity = entity;
            Kind = kind;
            Velocity = velocity;
            SteeringWheelRotation = steeringWheelRotation;
            Power = power;
            FrontLeftWheelRpm = frontLeftWheelRpm;
            FrontRightWheelRpm = frontRightWheelRpm;
            BackLeftWheelRpm = backLeftWheelRpm;
            BackRightWheelRpm = backRightWheelRpm;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public EntitySnapshot Entity { get; }
        public GolfCartKind Kind { get; }
        /// <summary>Replicated Velocity, not a fresh Rigidbody measurement or validated movement.</summary>
        public Vector3 Velocity { get; }
        /// <summary>Raw replicated steering-wheel rotation, not a driver-input or road-wheel angle.</summary>
        public float SteeringWheelRotation { get; }
        /// <summary>Raw replicated Power in the game's scale, not a normalized battery percentage.</summary>
        public float Power { get; }
        public float FrontLeftWheelRpm { get; }
        public float FrontRightWheelRpm { get; }
        public float BackLeftWheelRpm { get; }
        /// <summary>Copied from the native state's BackRightRpm property.</summary>
        public float BackRightWheelRpm { get; }
        /// <summary>Local read completion time, not a replication or simulation timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    public sealed class LogSledSnapshot
    {
        internal LogSledSnapshot(EntitySnapshot entity, ulong translationControllerNetworkId)
        {
            Entity = entity;
            TranslationControllerNetworkId = translationControllerNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public EntitySnapshot Entity { get; }
        /// <summary>Raw mobile-state link, which can be nonzero but unresolved. Not the sled's Bolt controller connection.</summary>
        public ulong TranslationControllerNetworkId { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
