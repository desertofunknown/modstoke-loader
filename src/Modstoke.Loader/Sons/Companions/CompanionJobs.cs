// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using global::Sons.Ai.Vail;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons;

/// <summary>Transient server queues for native Get orders. Native endings do not establish delivery.</summary>
public static partial class CompanionJobs
{
    private const int MaximumQueues = 4;
    private const int MaximumPending = 8;
    private const int MaximumHistory = 32;
    private static readonly Dictionary<CompanionJobQueue, QueueContext> Queues = new();
    private static readonly List<QueueContext> PumpContexts = new(MaximumQueues);
    [ThreadStatic] private static QueueContext _submitting;
    [ThreadStatic] private static bool _authorizing;
    [ThreadStatic] private static bool _pumping;

    private sealed class Job
    {
        internal Guid Id = Guid.NewGuid();
        internal int Index;
        internal string Name;
        internal CompanionDropMode DropMode;
        internal Robby.RobbyGetOrder Order;
        internal VailControllerOverride Controller;
        internal Func<bool> Authorize;
        internal DateTimeOffset Created = DateTimeOffset.UtcNow;
        internal DateTimeOffset Updated = DateTimeOffset.UtcNow;
        internal long Enqueued = Stopwatch.GetTimestamp();
        internal CompanionJobStatus Status = CompanionJobStatus.Queued;
        internal string Detail = "Waiting for a native idle boundary; not submitted.";
    }

    private sealed class QueueContext
    {
        internal CompanionJobQueue Handle;
        internal WorldRecordContext World;
        internal PlayerHandle Player;
        internal BoltEntity Entity;
        internal Bolt.Entity Attachment;
        internal Robby Robby;
        internal VailActor Actor;
        internal readonly List<Job> Jobs = new();
        internal Job Active;
        internal Robby.RunningRobbyOrder Running;
        internal CompanionOrderReference Reference;
        internal bool Refused;
        internal int AttemptFrame = -1;
    }

    /// <summary>Lists enabled native Get orders, without running native eligibility predicates.</summary>
    public static bool TryGetOrders(ulong targetId, out IReadOnlyList<CompanionJobOption> options, out string problem)
    {
        options = Array.Empty<CompanionJobOption>();
        problem = "Companion queues are unavailable.";
        if (!Available()) return false;
        try
        {
            if (!TryTarget(targetId, out _, out _, out var robby, out _, out problem)) return false;
            var orders = robby._orderData?._getItemOrders;
            if (orders == null || orders.WasCollected || orders.Count > 64) { problem = "Native Get orders are unavailable."; return false; }
            var result = new List<CompanionJobOption>();
            for (int index = 0; index < orders.Count; index++)
                if (TryOrder(robby, index, out var order))
                    result.Add(new(index, order._itemName, order._restartAfterExpire));
            options = result.AsReadOnly();
            problem = null;
            return true;
        }
        catch (Exception error) { problem = "Native Get orders could not be read."; Report(error); return false; }
    }

    /// <summary>Retains one current player, world and companion lifetime. Does not take over an existing order.</summary>
    public static bool TryOpen(PlayerHandle player, ulong targetId,
        out CompanionJobQueue queue, out string problem)
    {
        queue = null;
        problem = "Companion queues are unavailable.";
        if (!Available() || _authorizing || _submitting != null) return false;
        try
        {
            Pump();
            if (!WorldRecords.TryGetCurrent(out var world, out _)
                || !ServerWorld.TryValidatePlayerBinding(player, out _))
            { problem = "The current world and player binding are required."; return false; }
            if (!TryTarget(targetId, out var entity, out var attachment, out var robby, out var actor, out problem)) return false;
            foreach (var current in Queues.Values)
                if (Same(current.Entity, entity)) { problem = "This companion already has a queue owner."; return false; }
            if (Queues.Count >= MaximumQueues) { problem = "The server already has four companion queues."; return false; }
            if (robby._activeOrder != null || robby._activeOverride != null || robby._currentOrderOverride != null
                || robby._waitingForOrders || robby._interactingPlayer != null)
            { problem = "Finish the companion's current native order or interaction before opening a queue."; return false; }
            var handle = new CompanionJobQueue(targetId);
            var context = new QueueContext { Handle = handle, World = world, Player = player, Entity = entity,
                Attachment = attachment, Robby = robby, Actor = actor };
            if (!Current(context)) { problem = "The companion or player context changed."; return false; }
            Queues.Add(handle, context);
            queue = handle;
            problem = null;
            return true;
        }
        catch (Exception error) { problem = "The companion queue could not be opened."; Report(error); return false; }
    }

    /// <summary>
    /// Adds a native Get request. Authorization runs on the game thread now and before submission;
    /// keep it bounded and free of native actions. Waiting entries expire after one hour.
    /// </summary>
    public static bool TryEnqueue(CompanionJobQueue queue, int orderIndex,
        CompanionDropMode dropMode, Func<bool> authorize, out Guid id, out string problem)
    {
        id = Guid.Empty;
        problem = "The queue is unavailable or its context changed.";
        if (!Available() || _authorizing || _submitting != null || !GetCurrent(queue, out var context)) return false;
        try
        {
            if (authorize == null || !Authorize(context, authorize)) { problem = "Queue permission was denied."; return false; }
            if ((int)dropMode < 0 || (int)dropMode > 4 || !TryOrder(context.Robby, orderIndex, out var order))
            { problem = "Choose an enabled native Get order and a supported drop mode."; return false; }
            int pending = 0;
            foreach (var job in context.Jobs)
                if (job.Status == CompanionJobStatus.Queued) pending++;
            if (pending >= MaximumPending)
            { problem = "This queue already has eight waiting jobs."; return false; }
            while (context.Jobs.Count >= MaximumHistory)
            {
                int old = context.Jobs.FindIndex(job => Terminal(job.Status));
                if (old < 0) { problem = "The queue history is full."; return false; }
                context.Jobs.RemoveAt(old);
            }
            var added = new Job { Index = orderIndex, Name = order._itemName, DropMode = dropMode,
                Order = order, Controller = order._controllerOverride, Authorize = authorize };
            context.Jobs.Add(added);
            id = added.Id;
            problem = null;
            return true;
        }
        catch (Exception error) { Retire(context, "Queue preparation failed; native work was not cancelled."); Report(error); return false; }
    }

    /// <summary>Copies at most 32 receipts. Closed handles retain copied receipts only.</summary>
    public static bool TryRead(CompanionJobQueue queue,
        out IReadOnlyList<CompanionJobReceipt> receipts, out string problem)
    {
        receipts = Array.Empty<CompanionJobReceipt>();
        problem = "The queue is unavailable on this thread.";
        if (Environment.CurrentManagedThreadId != _thread || queue == null) return false;
        if (Queues.TryGetValue(queue, out var context))
        {
            if (!Current(context)) Retire(context, "The player, world or companion context ended; native outcome is unknown.");
            else { receipts = Copy(context); problem = null; return true; }
        }
        if (!queue.IsClosed) return false;
        receipts = queue.ClosedReceipts ?? receipts;
        problem = null;
        return true;
    }

    /// <summary>Removes an unstarted entry only. A running or resting native job is never cancelled.</summary>
    public static bool CancelPending(CompanionJobQueue queue, Guid id, out string problem)
    {
        problem = "The queue is unavailable or its context changed.";
        if (!Available() || _authorizing || _submitting != null || !GetCurrent(queue, out var context)) return false;
        var job = context.Jobs.Find(candidate => candidate.Id == id);
        if (job == null || job.Status != CompanionJobStatus.Queued)
        { problem = "Only a waiting entry can be removed; no native action was cancelled."; return false; }
        Set(job, CompanionJobStatus.Cancelled, "Removed before submission; no native action was cancelled.");
        problem = null;
        return true;
    }

    /// <summary>Releases this queue. Pending entries are removed; running native work is left alone.</summary>
    public static void Close(CompanionJobQueue queue)
    {
        if (Environment.CurrentManagedThreadId == _thread && queue != null && Queues.TryGetValue(queue, out var context))
            Retire(context, "Queue closed; any submitted native work continues independently.", true);
    }

    private static bool GetCurrent(CompanionJobQueue handle, out QueueContext context)
    {
        context = null;
        if (handle == null || !Queues.TryGetValue(handle, out context)) return false;
        if (Current(context)) return true;
        Retire(context, "The player, world or companion context ended; native outcome is unknown.");
        return false;
    }

    private static bool Authorize(QueueContext context, Func<bool> callback)
    {
        if (_authorizing || context.Handle.IsClosed) return false;
        _authorizing = true;
        try { return callback() && !context.Handle.IsClosed && Current(context); }
        catch (Exception error) { Report(error); return false; }
        finally { _authorizing = false; }
    }

    private static bool Terminal(CompanionJobStatus status) => status != CompanionJobStatus.Queued
        && status != CompanionJobStatus.Submitted && status != CompanionJobStatus.Resting;

    private static void Set(Job job, CompanionJobStatus status, string detail)
    {
        job.Status = status; job.Detail = detail; job.Updated = DateTimeOffset.UtcNow;
        if (Terminal(status)) { job.Authorize = null; job.Order = null; job.Controller = null; }
    }

    private static CompanionJobReceipt CopyReceipt(Job job) => new(
        job.Id, job.Index, job.Name, job.DropMode, job.Status, job.Detail, job.Created, job.Updated);

    private static IReadOnlyList<CompanionJobReceipt> Copy(QueueContext context) =>
        context.Jobs.ConvertAll(CopyReceipt).AsReadOnly();

    private static void Retire(QueueContext context, string reason, bool cancelled = false)
    {
        if (context.Handle.IsClosed) return;
        foreach (var job in context.Jobs)
            if (!Terminal(job.Status)) Set(job, cancelled && job.Status == CompanionJobStatus.Queued
                ? CompanionJobStatus.Cancelled : CompanionJobStatus.ContextLost, reason);
        context.Handle.ClosedReceipts = Copy(context);
        context.Handle.IsClosed = true;
        Queues.Remove(context.Handle);
        context.Active = null; context.Running = null; context.Reference = null;
    }

    internal static void Pump()
    {
        if (Environment.CurrentManagedThreadId != _thread || Queues.Count == 0 || _authorizing || _submitting != null || _pumping) return;
        _pumping = true;
        try
        {
            PumpContexts.AddRange(Queues.Values);
            foreach (var context in PumpContexts)
            {
                if (!Current(context)) { Retire(context, "The player, world or companion context ended; native outcome is unknown."); continue; }
                foreach (var job in context.Jobs)
                {
                    if (Terminal(job.Status)) continue;
                    if (!Authorize(context, job.Authorize))
                    { Retire(context, "Queue authorization ended; submitted native work was not cancelled."); break; }
                    if (job.Status == CompanionJobStatus.Queued
                        && Stopwatch.GetElapsedTime(job.Enqueued).TotalHours >= 1)
                        Set(job, CompanionJobStatus.Expired, "Expired while waiting; never submitted.");
                }
            }
        }
        finally { PumpContexts.Clear(); _pumping = false; }
    }

    private static void Dispatch(QueueContext context)
    {
        if (_authorizing || _submitting != null || context.Handle.IsClosed || context.Active != null
            || context.AttemptFrame == Time.frameCount) return;
        context.AttemptFrame = Time.frameCount;
        if (!Current(context)) { Retire(context, "The player, world or companion context changed."); return; }
        if (!Idle(context)) return;
        var job = context.Jobs.Find(candidate => candidate.Status == CompanionJobStatus.Queued);
        if (job == null) return;
        if (Stopwatch.GetElapsedTime(job.Enqueued).TotalHours >= 1)
        { Set(job, CompanionJobStatus.Expired, "Expired while waiting; never submitted."); return; }
        if (!Authorize(context, job.Authorize)) { Retire(context, "Queue authorization ended before submission."); return; }
        if (!TryOrder(context.Robby, job.Index, out var order) || !Same(order, job.Order)
            || !Same(order._controllerOverride, job.Controller) || order._itemName != job.Name)
        { Set(job, CompanionJobStatus.Rejected, "Native order data changed; never submitted."); return; }
        // Authorization is allowed to inspect current policy, but cannot make a stale idle observation usable.
        if (!Current(context) || !Idle(context)) return;
        context.Active = job;
        context.Refused = false;
        Set(job, CompanionJobStatus.Submitted, "Native request submitted; execution and delivery are not confirmed.");
        _submitting = context;
        try
        {
            context.Robby.ServerGiveOrders(Robby.OrderType.Get, job.Index, (int)job.DropMode, context.Player.Player.transform);
            if (context.Handle.IsClosed) return;
            var running = context.Robby._activeOrder;
            if (context.Refused)
            {
                Set(job, CompanionJobStatus.Refused, "Native refusal returned; no delivery is established.");
                context.Active = null;
            }
            else if (running != null && !running.WasCollected && Same(running._order, job.Order))
            {
                context.Running = running;
                context.Reference = new(context.Entity, context.Attachment, context.Robby, running);
            }
            else
            {
                Set(job, CompanionJobStatus.Uncertain, "Submission returned without the expected order; outcome is unknown.");
                Retire(context, "Queue stopped after an uncertain native submission.");
            }
        }
        catch (Exception error)
        {
            Set(job, CompanionJobStatus.Uncertain, "Native submission failed; effects and delivery are unknown.");
            Retire(context, "Queue stopped after a native submission error."); Report(error);
        }
        finally { _submitting = null; }
    }

    private static void Observe(CompanionObservation observation)
    {
        if (Environment.CurrentManagedThreadId != _thread || observation?.Before == null || Queues.Count == 0) return;
        foreach (var context in new List<QueueContext>(Queues.Values))
        {
            if (context.Handle.NetworkId != observation.Before.NetworkId || context.Active == null) continue;
            if (ReferenceEquals(context, _submitting) && observation.Kind == CompanionObservationKind.RefusalReturned)
            { context.Refused = true; continue; }
            if (observation.Kind != CompanionObservationKind.CompletionReturned || context.Reference == null
                || !context.Reference.RefersToSameOrder(observation.Before.ActiveOrder?.Reference)) continue;
            if (observation.AfterStatus != CompanionReadStatus.Success || observation.After == null)
            { Retire(context, "Completion returned without a current after-state; native outcome is unknown."); continue; }
            if (observation.RestartAfterRest == true
                && context.Reference.RefersToSameOrder(observation.After.ActiveOrder?.Reference))
                Set(context.Active, CompanionJobStatus.Resting, "Native rest/restart returned; job retained and delivery unknown.");
            else if (observation.RestartAfterRest == false && observation.After.ActiveOrder == null)
            {
                Set(context.Active, CompanionJobStatus.NativeEnded, "Native order ended; success and resource delivery are unknown.");
                context.Active = null; context.Running = null; context.Reference = null;
            }
            else Retire(context, "Completion changed the tracked order unexpectedly; native outcome is unknown.");
        }
    }
}
