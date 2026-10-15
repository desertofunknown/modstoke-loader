// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Crafting.Structures;
using Sons.Gameplay;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerWorld
    {
        /// <summary>Reads an attached cart or ghost cart, preserving raw replicated slot references rather than inferring occupants.</summary>
        public static bool TryReadGolfCart(ulong networkId, out OpenACAISonsGolfCartSnapshot snapshot,
            out OpenACAISonsWorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                status = CaptureGolfCart(networkId, out var entity, out var slots, out var before);
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                var ids = new ulong[before.SlotCount];
                for (var i = 0; i < ids.Length; i++)
                    ids[i] = slots.GetValueNetworkId(i).PackedValue;

                status = CaptureGolfCart(networkId, out _, out var currentSlots, out var after);
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = OpenACAISonsWorldReadStatus.BindingChanged;
                    return false;
                }
                for (var i = 0; i < ids.Length; i++)
                {
                    if (ids[i] != currentSlots.GetValueNetworkId(i).PackedValue)
                    {
                        status = OpenACAISonsWorldReadStatus.BindingChanged;
                        return false;
                    }
                }
                snapshot = new OpenACAISonsGolfCartSnapshot(entity, before.Kind, ids);
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsWorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>Reads the exact log-sled/mobile-storage binding and its raw replicated translation-controller reference.</summary>
        public static bool TryReadLogSled(ulong networkId, out OpenACAISonsLogSledSnapshot snapshot,
            out OpenACAISonsWorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                status = CaptureLogSled(networkId, out var entity, out var before);
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                status = CaptureLogSled(networkId, out _, out var after);
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = OpenACAISonsWorldReadStatus.BindingChanged;
                    return false;
                }
                snapshot = new OpenACAISonsLogSledSnapshot(entity, before.TranslationControllerNetworkId);
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsWorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static OpenACAISonsWorldReadStatus CaptureGolfCart(ulong networkId,
            out OpenACAISonsEntitySnapshot snapshot, out Bolt.NetworkArray_Entity slots, out GolfCartBinding binding)
        {
            snapshot = null;
            slots = null;
            binding = default;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (!TryCaptureVehicleEntity(entity, networkId, out snapshot, out var entityBinding))
                return OpenACAISonsWorldReadStatus.EntityUnavailable;

            var componentPointer = IntPtr.Zero;
            var kind = OpenACAISonsGolfCartKind.Cart;
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
                    kind = OpenACAISonsGolfCartKind.Ghost;
                    matches++;
                }
            }
            if (matches != 1)
                return OpenACAISonsWorldReadStatus.VehicleUnavailable;
            if (!entity.StateIs<IGolfCartState>())
                return OpenACAISonsWorldReadStatus.VehicleStateUnavailable;
            var state = entity.GetState<IGolfCartState>();
            if (state is null || state.WasCollected)
                return OpenACAISonsWorldReadStatus.VehicleStateUnavailable;
            slots = state.ConnectedSlots;
            if (slots is null || slots.WasCollected || slots.Length < 0)
                return OpenACAISonsWorldReadStatus.VehicleStateUnavailable;
            binding = new GolfCartBinding(entityBinding, componentPointer, state.Pointer, slots.Pointer, slots.Length, kind);
            return OpenACAISonsWorldReadStatus.Success;
        }

        private static OpenACAISonsWorldReadStatus CaptureLogSled(ulong networkId,
            out OpenACAISonsEntitySnapshot snapshot, out LogSledBinding binding)
        {
            snapshot = null;
            binding = default;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (!TryCaptureVehicleEntity(entity, networkId, out snapshot, out var entityBinding))
                return OpenACAISonsWorldReadStatus.EntityUnavailable;
            if (!entity.TryGetComponent<ScrewStructureWithStorageMobile>(out var mobile)
                || !IsLiveVehicleComponent(mobile) || !MatchesVehicleEntity(mobile.entity, entity))
                return OpenACAISonsWorldReadStatus.VehicleUnavailable;

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
                return OpenACAISonsWorldReadStatus.VehicleUnavailable;
            if (!entity.StateIs<IScrewStructureWithStorageMobileState>())
                return OpenACAISonsWorldReadStatus.VehicleStateUnavailable;
            var state = entity.GetState<IScrewStructureWithStorageMobileState>();
            if (state is null || state.WasCollected)
                return OpenACAISonsWorldReadStatus.VehicleStateUnavailable;
            binding = new LogSledBinding(entityBinding, controllerPointer, mobile.Pointer, state.Pointer,
                state.GetTranslationControllerNetworkIdValue().PackedValue);
            return OpenACAISonsWorldReadStatus.Success;
        }

        private static bool TryCaptureVehicleEntity(BoltEntity entity, ulong networkId,
            out OpenACAISonsEntitySnapshot snapshot, out VehicleEntityBinding binding)
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
            uint? ControllerConnectionId, OpenACAISonsServerSession SourceSession, OpenACAISonsServerSession ControllerSession);

        private readonly record struct GolfCartBinding(VehicleEntityBinding Entity, IntPtr ComponentPointer,
            IntPtr StatePointer, IntPtr SlotsPointer, int SlotCount, OpenACAISonsGolfCartKind Kind);

        private readonly record struct LogSledBinding(VehicleEntityBinding Entity, IntPtr ControllerPointer,
            IntPtr MobilePointer, IntPtr StatePointer, ulong TranslationControllerNetworkId);
    }

    public enum OpenACAISonsGolfCartKind { Cart, Ghost }

    public sealed class OpenACAISonsGolfCartSnapshot
    {
        internal OpenACAISonsGolfCartSnapshot(OpenACAISonsEntitySnapshot entity, OpenACAISonsGolfCartKind kind, ulong[] slotIds)
        {
            Entity = entity;
            Kind = kind;
            ConnectedSlotNetworkIds = Array.AsReadOnly(slotIds);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public OpenACAISonsEntitySnapshot Entity { get; }
        public OpenACAISonsGolfCartKind Kind { get; }
        /// <summary>Raw ordered links, not necessarily player IDs. Zero is no replicated reference, not proof of an empty seat.</summary>
        public IReadOnlyList<ulong> ConnectedSlotNetworkIds { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    public sealed class OpenACAISonsLogSledSnapshot
    {
        internal OpenACAISonsLogSledSnapshot(OpenACAISonsEntitySnapshot entity, ulong translationControllerNetworkId)
        {
            Entity = entity;
            TranslationControllerNetworkId = translationControllerNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public OpenACAISonsEntitySnapshot Entity { get; }
        /// <summary>Raw mobile-state link, which can be nonzero but unresolved. Not the sled's Bolt controller connection.</summary>
        public ulong TranslationControllerNetworkId { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
