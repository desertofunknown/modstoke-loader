// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay;

namespace Modstoke.Sons
{
    public enum CompanionReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        NotServer,
        TargetUnavailable,
        ContextChanged,
        Failed
    }

    public enum CompanionObservationKind
    {
        OrderSelectionReturned,
        OrderRunReturned,
        FollowOrderReturned,
        CompletionReturned,
        RefusalReturned
    }

    public enum CompanionOrderKind
    {
        Other,
        Get,
        Build,
        Stay,
        ClearArea,
        MaintainBase
    }

    /// <summary>A native method returned; this does not prove success or item delivery.</summary>
    public sealed class CompanionObservation
    {
        internal CompanionObservation(CompanionObservationKind kind,
            CompanionSnapshot before, CompanionSnapshot after,
            CompanionReadStatus afterStatus, bool? restartAfterRest)
        {
            Kind = kind;
            Before = before;
            After = after;
            AfterStatus = afterStatus;
            RestartAfterRest = restartAfterRest;
        }

        public CompanionObservationKind Kind { get; }
        public CompanionSnapshot Before { get; }
        /// <summary>Null means the after snapshot is unavailable, not that the active order is absent.</summary>
        public CompanionSnapshot After { get; }
        public CompanionReadStatus AfterStatus { get; }
        public bool? RestartAfterRest { get; }
    }

    /// <summary>Copied companion state. Flags do not prove an action is executing.</summary>
    public sealed class CompanionSnapshot
    {
        internal CompanionSnapshot(ulong networkId, CompanionOrderSnapshot activeOrder,
            int injuredState, bool hasNativeActor, bool hasInteractingPlayer, bool hasFollowTarget,
            bool hasActiveOverride, bool hasOrderOverride, bool orderOverrideIsActive)
        {
            NetworkId = networkId;
            ActiveOrder = activeOrder;
            InjuredState = injuredState;
            HasNativeActor = hasNativeActor;
            HasInteractingPlayer = hasInteractingPlayer;
            HasFollowTarget = hasFollowTarget;
            HasActiveOverride = hasActiveOverride;
            HasOrderOverride = hasOrderOverride;
            OrderOverrideIsActive = orderOverrideIsActive;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong NetworkId { get; }
        /// <summary>Null means no active order was captured. Follow can retain an older active order.</summary>
        public CompanionOrderSnapshot ActiveOrder { get; }
        /// <summary>Raw native injury enum value, not a complete life-state assessment.</summary>
        public int InjuredState { get; }
        public bool HasNativeActor { get; }
        public bool HasInteractingPlayer { get; }
        public bool HasFollowTarget { get; }
        public bool HasActiveOverride { get; }
        public bool HasOrderOverride { get; }
        public bool OrderOverrideIsActive { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    public sealed class CompanionOrderSnapshot
    {
        internal CompanionOrderSnapshot(CompanionOrderReference reference,
            CompanionOrderKind kind, string name, float nativeStartTime,
            bool receivedCompletionCallback, int dropLocationType)
        {
            Reference = reference;
            Kind = kind;
            Name = name;
            NativeStartTime = nativeStartTime;
            ReceivedCompletionCallback = receivedCompletionCallback;
            DropLocationType = dropLocationType;
        }

        public CompanionOrderReference Reference { get; }
        public CompanionOrderKind Kind { get; }
        public string Name { get; }
        /// <summary>Unity simulation time, not wall time or a deadline.</summary>
        public float NativeStartTime { get; }
        public bool ReceivedCompletionCallback { get; }
        public int DropLocationType { get; }
    }

    /// <summary>Opaque identity of a captured order lifetime; it does not establish current validity.</summary>
    public sealed class CompanionOrderReference
    {
        private readonly BoltEntity _entity;
        private readonly Bolt.Entity _attachment;
        private readonly Robby _companion;
        private readonly Robby.RunningRobbyOrder _order;
        private readonly IntPtr _entityPointer;
        private readonly IntPtr _attachmentPointer;
        private readonly IntPtr _companionPointer;
        private readonly IntPtr _orderPointer;

        internal CompanionOrderReference(BoltEntity entity, Bolt.Entity attachment,
            Robby companion, Robby.RunningRobbyOrder order)
        {
            _entity = entity;
            _attachment = attachment;
            _companion = companion;
            _order = order;
            _entityPointer = entity.Pointer;
            _attachmentPointer = attachment.Pointer;
            _companionPointer = companion.Pointer;
            _orderPointer = order.Pointer;
        }

        /// <summary>Compares retained identities without reading current native state.</summary>
        public bool RefersToSameOrder(CompanionOrderReference other)
        {
            bool same = other != null
                && _entityPointer == other._entityPointer
                && _attachmentPointer == other._attachmentPointer
                && _companionPointer == other._companionPointer
                && _orderPointer == other._orderPointer;
            // Keep the captured wrappers rooted until both identities have been compared.
            GC.KeepAlive(_entity);
            GC.KeepAlive(_attachment);
            GC.KeepAlive(_companion);
            GC.KeepAlive(_order);
            GC.KeepAlive(other);
            return same;
        }
    }
}
