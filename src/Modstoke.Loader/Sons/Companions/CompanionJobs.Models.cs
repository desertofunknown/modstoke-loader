// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Native destination behavior for a companion order.</summary>
    public enum CompanionDropMode
    {
        DropHere = 0,
        GiveToMe = 1,
        FollowMe = 2,
        FillHolder = 3,
        FillLogSled = 4
    }

    /// <summary>Observed state of a queued companion job.</summary>
    public enum CompanionJobStatus
    {
        Queued,
        Submitted,
        Resting,
        /// <summary>The native order ended; this does not confirm successful delivery.</summary>
        NativeEnded,
        Refused,
        /// <summary>The queued job was cancelled. Queued cancellation never stops a running native order.</summary>
        Cancelled,
        ContextLost,
        Uncertain,
        Expired,
        Rejected
    }

    /// <summary>An immutable companion order option.</summary>
    public sealed class CompanionJobOption
    {
        internal CompanionJobOption(int index, string name, bool restartsAfterRest)
        {
            Index = index;
            Name = name;
            RestartsAfterRest = restartsAfterRest;
        }

        public int Index { get; }
        public string Name { get; }
        public bool RestartsAfterRest { get; }
    }

    /// <summary>An immutable job observation. A native order ending does not confirm successful delivery.</summary>
    public sealed class CompanionJobReceipt
    {
        internal CompanionJobReceipt(Guid id, int orderIndex, string orderName,
            CompanionDropMode dropMode, CompanionJobStatus status,
            string detail, DateTimeOffset queuedAt, DateTimeOffset updatedAt)
        {
            Id = id;
            OrderIndex = orderIndex;
            OrderName = orderName;
            DropMode = dropMode;
            Status = status;
            Detail = detail;
            QueuedAt = queuedAt;
            UpdatedAt = updatedAt;
        }

        public Guid Id { get; }
        public int OrderIndex { get; }
        public string OrderName { get; }
        public CompanionDropMode DropMode { get; }
        public CompanionJobStatus Status { get; }
        public string Detail { get; }
        public DateTimeOffset QueuedAt { get; }
        public DateTimeOffset UpdatedAt { get; }
    }

    /// <summary>
    /// Copied preconditions for an exact current queue lifetime. No native references are retained.
    /// Authorization and native eligibility are not evaluated; all values can change after observation.
    /// </summary>
    public sealed class CompanionQueuePreconditions
    {
        internal CompanionQueuePreconditions(ulong networkId, bool nativeIdle, int pendingJobs,
            CompanionJobReceipt activeJob, CompanionJobReceipt nextJob,
            bool? nextOrderMatches, bool? nextJobExpired)
        {
            NetworkId = networkId;
            NativeIdle = nativeIdle;
            PendingJobs = pendingJobs;
            ActiveJob = activeJob;
            NextJob = nextJob;
            NextOrderMatches = nextOrderMatches;
            NextJobExpired = nextJobExpired;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong NetworkId { get; }
        /// <summary>
        /// The scheduler's current native idle predicate, not a qualified tick boundary or dispatch guarantee.
        /// False also covers unavailable native state required by that predicate.
        /// </summary>
        public bool NativeIdle { get; }
        /// <summary>Entries still marked Queued, including any whose expiry has not yet been processed.</summary>
        public int PendingJobs { get; }
        /// <summary>Tracked submitted or resting job, or null. Tracking does not prove native execution.</summary>
        public CompanionJobReceipt ActiveJob { get; }
        /// <summary>First entry still marked Queued, or null. Expired or changed entries are not skipped.</summary>
        public CompanionJobReceipt NextJob { get; }
        /// <summary>
        /// Whether the next job still selects the same enabled native order, controller and name.
        /// False includes unavailable order data; null means no waiting job. Native eligibility is not checked.
        /// </summary>
        public bool? NextOrderMatches { get; }
        /// <summary>One-hour monotonic waiting expiry, without changing its receipt; null means no waiting job.</summary>
        public bool? NextJobExpired { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Availability of an optional native field captured with a companion order.</summary>
    public enum CompanionDetailStatus
    {
        Unavailable,
        Absent,
        Available
    }

    /// <summary>A copied target position, not a navigation destination or proof of reachability.</summary>
    public sealed class CompanionTargetSnapshot
    {
        internal CompanionTargetSnapshot(CompanionDetailStatus status, Vector3? position)
        {
            Status = status;
            Position = position;
        }

        /// <summary>Absent means the native reference was null; destroyed or unreadable targets are Unavailable.</summary>
        public CompanionDetailStatus Status { get; }
        /// <summary>Finite world position of the referenced object's transform, only when Available.</summary>
        public Vector3? Position { get; }
    }

    /// <summary>Copied drop-stimulus fields. These do not establish an available holder or item delivery.</summary>
    public sealed class CompanionDropTargetSnapshot
    {
        internal CompanionDropTargetSnapshot(CompanionDetailStatus status,
            bool? enabled, bool? targetIsHolder, float? nativeStopMoveDistance, CompanionTargetSnapshot target)
        {
            Status = status;
            Enabled = enabled;
            TargetIsHolder = targetIsHolder;
            NativeStopMoveDistance = nativeStopMoveDistance;
            Target = target;
        }

        /// <summary>Availability of the companion's drop-stimulus component, independently of its target.</summary>
        public CompanionDetailStatus Status { get; }
        /// <summary>Component enabled flag, not whether the actor is using it. Null when the component is unavailable or absent.</summary>
        public bool? Enabled { get; }
        /// <summary>Raw native target-is-holder flag, not a capacity or acceptance check.</summary>
        public bool? TargetIsHolder { get; }
        /// <summary>Raw native stop-movement distance; null when absent, unreadable or non-finite.</summary>
        public float? NativeStopMoveDistance { get; }
        /// <summary>The drop stimulus's target, not the stimulus's own transform. Unavailable when its component cannot be read.</summary>
        public CompanionTargetSnapshot Target { get; }
    }

    /// <summary>Copied running-order fields without a retained native order reference.</summary>
    public sealed class CompanionRunningOrderSnapshot
    {
        internal CompanionRunningOrderSnapshot(CompanionOrderKind kind, string name,
            bool nameTruncated, bool enabled, float nativeStartTime, bool receivedCompletionCallback,
            int nativeDropLocationType, string nativeGetItemType, bool getItemTypeTruncated,
            float? nativeGetExpireTimeMinutes, bool? getRestartsAfterExpire, bool? getChecksCompleteOnDrop)
        {
            Kind = kind;
            Name = name;
            NameTruncated = nameTruncated;
            Enabled = enabled;
            NativeStartTime = nativeStartTime;
            ReceivedCompletionCallback = receivedCompletionCallback;
            NativeDropLocationType = nativeDropLocationType;
            NativeGetItemType = nativeGetItemType;
            GetItemTypeTruncated = getItemTypeTruncated;
            NativeGetExpireTimeMinutes = nativeGetExpireTimeMinutes;
            GetRestartsAfterExpire = getRestartsAfterExpire;
            GetChecksCompleteOnDrop = getChecksCompleteOnDrop;
        }

        /// <summary>Native order-data subtype. Other preserves unknown subtypes without guessing their behavior.</summary>
        public CompanionOrderKind Kind { get; }
        /// <summary>Native item name, copied to at most 128 characters; null preserves a missing native string.</summary>
        public string Name { get; }
        public bool NameTruncated { get; }
        /// <summary>Order-data enabled flag, not current eligibility.</summary>
        public bool Enabled { get; }
        /// <summary>Raw Unity simulation start time, not a wall-clock timestamp or completion deadline.</summary>
        public float NativeStartTime { get; }
        /// <summary>Raw native completion-callback flag; does not prove successful work or delivery.</summary>
        public bool ReceivedCompletionCallback { get; }
        /// <summary>Raw running-order drop enum, retained even when the SDK does not recognize it.</summary>
        public int NativeDropLocationType { get; }
        /// <summary>Known running-order drop mode, or null for an unknown raw value.</summary>
        public CompanionDropMode? DropMode => NativeDropLocationType is >= 0 and <= 4
            ? (CompanionDropMode)NativeDropLocationType : null;
        /// <summary>Raw Get-order item-type string, not an inventory ID; at most 128 characters. Null also applies to non-Get orders.</summary>
        public string NativeGetItemType { get; }
        public bool GetItemTypeTruncated { get; }
        /// <summary>Raw Get-order configuration, not time remaining. Null for non-Get orders; native non-finite values are preserved.</summary>
        public float? NativeGetExpireTimeMinutes { get; }
        /// <summary>Raw Get-order restart policy, not a promise of a future restart; null for other order kinds.</summary>
        public bool? GetRestartsAfterExpire { get; }
        /// <summary>Raw Get-order completion policy, not a delivery receipt; null for other order kinds.</summary>
        public bool? GetChecksCompleteOnDrop { get; }
    }

    /// <summary>
    /// On-demand copied order and target state for one current companion. It retains no native objects.
    /// Fields describe an observation, not an atomic native state, current execution or future validity.
    /// </summary>
    public sealed class CompanionCurrentOrderSnapshot
    {
        internal CompanionCurrentOrderSnapshot(ulong networkId,
            CompanionRunningOrderSnapshot activeOrder, bool waitingForOrders,
            CompanionTargetSnapshot followTarget, CompanionDropTargetSnapshot dropTarget)
        {
            NetworkId = networkId;
            ActiveOrder = activeOrder;
            WaitingForOrders = waitingForOrders;
            FollowTarget = followTarget;
            DropTarget = dropTarget;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong NetworkId { get; }
        /// <summary>Null means the active-order field was absent. A follow target can coexist with an older retained order.</summary>
        public CompanionRunningOrderSnapshot ActiveOrder { get; }
        /// <summary>Raw waiting-for-orders flag, not a queue submission precondition.</summary>
        public bool WaitingForOrders { get; }
        /// <summary>Position copied from the companion's follow-transform field, not proof it is following.</summary>
        public CompanionTargetSnapshot FollowTarget { get; }
        public CompanionDropTargetSnapshot DropTarget { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>
    /// Opaque queue handle identified by its managed object reference. Callers retain this handle,
    /// not a durable ID. Cancelling queued jobs never stops a running native order.
    /// </summary>
    public sealed class CompanionJobQueue
    {
        internal CompanionJobQueue(ulong networkId)
        {
            NetworkId = networkId;
        }

        /// <summary>The observed companion network ID, not a durable queue identity.</summary>
        public ulong NetworkId { get; }

        /// <summary>Copied retirement state; API reads must revalidate the handle against the live context.</summary>
        public bool IsClosed { get; internal set; }
        internal System.Collections.Generic.IReadOnlyList<CompanionJobReceipt> ClosedReceipts { get; set; }
    }
}
