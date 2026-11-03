// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Ai.Vail.StimuliTypes;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons;

public static partial class CompanionJobs
{
    /// <summary>
    /// Copies current order data, follow-target position and drop-target fields for one exact, live,
    /// uninjured, server-owned companion. No queue is required and no jobs or native orders are changed.
    /// Reads a fixed number of fields and retains at most 128 characters per native string.
    /// </summary>
    /// <remarks>
    /// Requires the existing companion hooks, game thread and current world. Native eligibility,
    /// completion and target-selection methods are not invoked. Optional target failures are explicit
    /// in the snapshot; failure to read the active order returns no snapshot. Shared world validation
    /// may initialize or invalidate its managed context. All copied values can change after observation.
    /// </remarks>
    public static CompanionReadStatus TryReadCurrentOrder(ulong targetId,
        out CompanionCurrentOrderSnapshot snapshot)
    {
        snapshot = null;
        if (_thread == 0) return CompanionReadStatus.Unavailable;
        if (Environment.CurrentManagedThreadId != _thread) return CompanionReadStatus.WrongThread;
        if (State.Status != CompanionHookStatus.Installed || _submitting != null)
            return CompanionReadStatus.Unavailable;
        try
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return CompanionReadStatus.NotServer;
            if (!WorldRecords.TryGetCurrent(out var world, out _))
                return CompanionReadStatus.Unavailable;
            if (!TryTarget(targetId, out var entity, out var attachment, out var robby, out var actor, out _))
                return CompanionReadStatus.TargetUnavailable;

            var running = robby._activeOrder;
            Robby.RobbyOrder order = null;
            CompanionRunningOrderSnapshot activeOrder = null;
            if (running is not null)
            {
                if (running.WasCollected) return CompanionReadStatus.TargetUnavailable;
                order = running._order;
                if (order is null || order.WasCollected) return CompanionReadStatus.TargetUnavailable;
                activeOrder = CopyRunningOrder(running, order);
            }

            bool waiting = robby._waitingForOrders;
            var follow = CopyOrderTarget(robby._followTransform);
            var drop = CopyDropTarget(robby._dropStimuliInstance);

            // Resolve the same network ID again; a reused ID must not lend validity to a previous actor.
            if (!Available() || !world.IsCurrent
                || !TryTarget(targetId, out var currentEntity, out var currentAttachment,
                    out var currentRobby, out var currentActor, out _)
                || !Same(entity, currentEntity) || !Same(attachment, currentAttachment)
                || !Same(robby, currentRobby) || !Same(actor, currentActor))
                return CompanionReadStatus.ContextChanged;
            var currentRunning = robby._activeOrder;
            if (running is null ? currentRunning is not null
                : !Same(running, currentRunning) || !Same(order, running._order))
                return CompanionReadStatus.ContextChanged;

            snapshot = new(targetId, activeOrder, waiting, follow, drop);
            return CompanionReadStatus.Success;
        }
        catch (Exception error) { Report(error); return CompanionReadStatus.Failed; }
    }

    private static CompanionRunningOrderSnapshot CopyRunningOrder(
        Robby.RunningRobbyOrder running, Robby.RobbyOrder order)
    {
        var get = order.TryCast<Robby.RobbyGetOrder>();
        var kind = get is not null ? CompanionOrderKind.Get
            : order.TryCast<Robby.RobbyBuildOrder>() is not null ? CompanionOrderKind.Build
            : order.TryCast<Robby.RobbyStayOrder>() is not null ? CompanionOrderKind.Stay
            : order.TryCast<Robby.RobbyClearOrder>() is not null ? CompanionOrderKind.ClearArea
            : order.TryCast<Robby.RobbyMaintainOrder>() is not null ? CompanionOrderKind.MaintainBase
            : CompanionOrderKind.Other;
        string name = CopyOrderText(order._itemName, out bool nameTruncated);
        string itemType = CopyOrderText(get?._itemType, out bool itemTypeTruncated);
        return new(kind, name, nameTruncated, order._enabled, running._startTime, running._receivedCompleted,
            (int)running._dropLocationType, itemType, itemTypeTruncated,
            get?._expireTimeMinutes, get?._restartAfterExpire, get?._checkCompleteOnDrop);
    }

    private static string CopyOrderText(string value, out bool truncated)
    {
        truncated = value is not null && value.Length > 128;
        return truncated ? value.Substring(0, 128) : value;
    }

    private static CompanionTargetSnapshot CopyOrderTarget(Component target)
    {
        if (target is null) return new(CompanionDetailStatus.Absent, null);
        try
        {
            if (!target.WasCollected && target)
            {
                var transform = target.transform;
                if (transform is not null && !transform.WasCollected && transform)
                {
                    var position = transform.position;
                    if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z))
                        return new(CompanionDetailStatus.Available, position);
                }
            }
        }
        catch (Exception error) { Report(error); }
        return new(CompanionDetailStatus.Unavailable, null);
    }

    private static CompanionDropTargetSnapshot CopyDropTarget(RobbyDropStimuli drop)
    {
        if (drop is null)
            return new(CompanionDetailStatus.Absent, null, null, null,
                new(CompanionDetailStatus.Unavailable, null));
        try
        {
            if (!drop.WasCollected && drop)
            {
                bool enabled = drop.enabled;
                bool holder = drop._targetIsHolder;
                float distance = drop._stopMoveDistance;
                var target = CopyOrderTarget(drop._dropTarget);
                return new(CompanionDetailStatus.Available, enabled, holder,
                    float.IsFinite(distance) ? distance : null, target);
            }
        }
        catch (Exception error) { Report(error); }
        return new(CompanionDetailStatus.Unavailable, null, null, null,
            new(CompanionDetailStatus.Unavailable, null));
    }
}
