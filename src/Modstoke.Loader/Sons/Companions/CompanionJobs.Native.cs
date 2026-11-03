// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Ai.Vail;
using global::Sons.Gameplay;
using global::Sons.Multiplayer;

namespace Modstoke.Sons;

public static partial class CompanionJobs
{
    /// <summary>
    /// Copies current queue preconditions on the game thread without submitting, expiring or retiring jobs.
    /// Does not invoke authorization callbacks or native eligibility predicates. Success is a snapshot,
    /// not permission to submit or proof that the scheduler's native tick boundary has been reached.
    /// </summary>
    /// <remarks>Shared lifetime validation may invalidate stale world, session or player bindings.</remarks>
    /// <returns>
    /// ContextChanged for a missing, closed or unverifiable queue; Unavailable during native submission
    /// or without installed queue hooks. Other failures return no snapshot, never a partially valid result.
    /// </returns>
    public static CompanionReadStatus TryReadPreconditions(CompanionJobQueue queue,
        out CompanionQueuePreconditions snapshot)
    {
        snapshot = null;
        if (_thread == 0) return CompanionReadStatus.Unavailable;
        if (Environment.CurrentManagedThreadId != _thread) return CompanionReadStatus.WrongThread;
        if (State.Status != CompanionHookStatus.Installed || _submitting != null)
            return CompanionReadStatus.Unavailable;
        try
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return CompanionReadStatus.NotServer;
            // GetCurrent would retire a stale queue; observation must leave its jobs and receipts alone.
            if (queue == null || queue.IsClosed || !Queues.TryGetValue(queue, out var context) || !Current(context))
                return CompanionReadStatus.ContextChanged;

            int pending = 0;
            Job next = null;
            foreach (var job in context.Jobs)
            {
                if (job.Status != CompanionJobStatus.Queued) continue;
                pending++;
                next ??= job;
            }

            bool idle = Idle(context);
            bool? matches = null;
            bool? expired = null;
            if (next != null)
            {
                matches = TryOrder(context.Robby, next.Index, out var order) && Same(order, next.Order)
                    && Same(order._controllerOverride, next.Controller) && order._itemName == next.Name;
                expired = Stopwatch.GetElapsedTime(next.Enqueued).TotalHours >= 1;
            }
            if (!Current(context)) return CompanionReadStatus.ContextChanged;

            snapshot = new CompanionQueuePreconditions(queue.NetworkId, idle, pending,
                Receipt(context.Active), Receipt(next), matches, expired);
            return CompanionReadStatus.Success;
        }
        catch (Exception error) { Report(error); return CompanionReadStatus.Failed; }

        static CompanionJobReceipt Receipt(Job job) => job == null ? null : CopyReceipt(job);
    }

    /// <summary>Lists currently active, live, server-owned Kelvin actors; not dormant world actors.</summary>
    public static bool TryListCompanions(out IReadOnlyList<ulong> targetIds, out string problem)
    {
        targetIds = Array.Empty<ulong>();
        problem = "Active companion enumeration is unavailable.";
        if (!Available()) return false;
        try
        {
            var manager = VailActorManager._instance;
            var actors = manager == null || manager.WasCollected || !manager ? null : manager._activeActors;
            if (actors == null || actors.WasCollected || actors.Count > 256) return false;
            int count = actors.Count;
            var ids = new List<ulong>();
            for (int index = 0; index < count; index++)
            {
                var actor = actors[index];
                if (actor == null || actor.WasCollected || !actor || !actor.isActiveAndEnabled) continue;
                var entity = actor.GetComponentInParent<BoltEntity>();
                if (entity == null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner) continue;
                ulong id = entity.networkId.PackedValue;
                if (!ids.Contains(id) && TryTarget(id, out _, out _, out _, out var current, out _) && Same(actor, current)) ids.Add(id);
            }
            if (actors.Count != count || !Same(manager, VailActorManager._instance)) return false;
            ids.Sort();
            targetIds = ids.AsReadOnly(); problem = null; return true;
        }
        catch (Exception error) { Report(error); return false; }
    }

    private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second) => first != null && second != null
        && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;

    private static bool TryTarget(ulong id, out BoltEntity entity, out Bolt.Entity attachment,
        out Robby robby, out VailActor actor, out string problem)
    {
        entity = null; attachment = null; robby = null; actor = null;
        problem = "A live, uninjured, server-owned companion is required.";
        if (id == 0) return false;
        entity = BoltNetwork.FindEntity(new Bolt.NetworkId(id));
        if (entity == null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner) return false;
        attachment = entity._entity;
        if (attachment == null || attachment.WasCollected
            || !MultiplayerUtilities.TryGetComponentInEntityChildren<Robby>(entity, out robby, false)
            || robby == null || robby.WasCollected || !robby || !robby.isActiveAndEnabled) return false;
        actor = robby._actor;
        if (actor == null || actor.WasCollected || !actor || !actor.isActiveAndEnabled
            || actor.IsDead() || actor.IsDying() || actor.IsRobbyInjured()
            || robby._injuredState != Robby.InjuredState.None) return false;
        problem = null;
        return true;
    }

    private static bool TryOrder(Robby robby, int index, out Robby.RobbyGetOrder order)
    {
        order = null;
        var data = robby._orderData;
        if (data == null || data.WasCollected || !data) return false;
        var list = data._getItemOrders;
        if (list == null || list.WasCollected || list.Count > 64 || index < 0 || index >= list.Count) return false;
        var entry = list[index];
        if (entry == null || entry.WasCollected || !entry._enabled) return false;
        var controller = entry._controllerOverride;
        string name = entry._itemName;
        if (controller == null || controller.WasCollected || !controller || string.IsNullOrWhiteSpace(name) || name.Length > 128) return false;
        order = entry; return true;
    }

    private static bool Current(QueueContext context)
    {
        try
        {
            if (!Available() || context.Handle.IsClosed || !context.World.IsCurrent
                || !ServerWorld.TryValidatePlayerBinding(context.Player, out _)
                || context.Player.SourceSession != null && ServerSessions.HasDisconnectAttempt(context.Player.SourceSession)
                || !TryTarget(context.Handle.NetworkId, out var entity, out var attachment, out var robby, out var actor, out _)
                || !Same(entity, context.Entity) || !Same(attachment, context.Attachment)
                || !Same(robby, context.Robby) || !Same(actor, context.Actor)) return false;
            if (robby._waitingForOrders || robby._interactingPlayer != null) return false;
            if (ReferenceEquals(context, _submitting)) return true;
            var running = robby._activeOrder;
            return context.Active == null ? running == null : Same(running, context.Running);
        }
        catch (Exception error) { Report(error); return false; }
    }

    private static bool Idle(QueueContext context)
    {
        var actor = context.Actor;
        var robby = context.Robby;
        var state = actor.GetState();
        var priority = actor.CurrentThoughtPriority;
        if (state is null || state.WasCollected || priority is null || priority.WasCollected
            || state != global::Sons.Ai.Vail.State.World || priority != global::Sons.Ai.Vail.Priority.Ignored
            || actor._activeActionInstance != null || actor._activeActionAsset != null
            || actor._activeGroup != null || actor._activeThought != null || actor._isFollowingSequence
            || robby._activeOrder != null || robby._activeOverride != null || robby._currentOrderOverride != null) return false;
        var next = actor._nextInSequence;
        if (next != null && (next.WasCollected || next.Count != 0)) return false;
        var completed = robby._orderCompleteStimuliInstance;
        return completed != null && !completed.WasCollected && completed && !completed.enabled;
    }
}
