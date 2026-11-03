// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Ai.Vail;
using global::Sons.Ai.Vail.StimuliTypes;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons;

public static partial class CompanionJobs
{
    private static ServerCompanionState _state = new(CompanionHookStatus.NotAttempted, "Companion queue hooks have not been installed.");
    private static int _attempted;
    private static int _thread;
    private static int _reported;
    [ThreadStatic] private static TickScope _tick;
    [ThreadStatic] private static int _performDepth;
    [ThreadStatic] private static int _selectionDepth;

    public static ServerCompanionState State => Volatile.Read(ref _state);

    private sealed class TickScope
    {
        internal TickScope Previous;
        internal VailActor Actor;
        internal bool Visited;
    }

    internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0) return;
        _thread = Environment.CurrentManagedThreadId;
        if (support == null || !support.SafeForHarmonyPatches
            || ServerCompanions.ObservationState.Status != CompanionHookStatus.Installed)
        { _state = new(CompanionHookStatus.Disabled, "Safe native patches and companion observations are required."); return; }
        _state = new(CompanionHookStatus.Installing, "Registering companion scheduling and lifetime boundaries.");
        var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-companion-jobs");
        try
        {
            Patch(typeof(VailActor), "Tick", new[] { typeof(float), typeof(float), typeof(bool), typeof(int) }, nameof(BeforeTick), null, nameof(FinishTick));
            Patch(typeof(VailActor), "UpdateActiveAction", Type.EmptyTypes, nameof(BeforeUpdateAction), nameof(AfterUpdateAction), null);
            Patch(typeof(VailActor), "PerformAction", new[] { typeof(Group), typeof(IStimuli), typeof(Thought), typeof(bool) }, nameof(BeforePerform), null, nameof(FinishPerform));
            Patch(typeof(VailActor), "ChooseNextAction", new[] { typeof(global::Sons.Ai.Vail.Priority) }, nameof(BeforeSelection), null, nameof(FinishSelection));
            foreach (string name in new[] { "OnActorEnabled", "OnDisable", "OnDeath" })
                Patch(typeof(VailActor), name, Type.EmptyTypes, nameof(RetireActor), null, null);
            Patch(typeof(VailActor), "Deactivate", new[] { typeof(bool) }, nameof(RetireActor), null, null);
            Patch(typeof(BoltEntity), "OnDisable", Type.EmptyTypes, nameof(RetireEntity), null, null);
            Patch(typeof(BoltEntity), "OnDestroy", Type.EmptyTypes, nameof(RetireEntity), null, null);
            Patch(typeof(Robby), "OnDestroy", Type.EmptyTypes, nameof(RetireCompanion), null, null);
            Patch(typeof(Robby), "SetInjuredState", new[] { typeof(Robby.InjuredState) }, nameof(RetireCompanion), null, null);
            Patch(typeof(Robby), "ServerStartTakingOrders", new[] { typeof(PlayerStimuli) }, nameof(BeforeManualOrder), null, null);
            Patch(typeof(Robby), "OnGiveOrderServer", new[] { typeof(Robby.OrderType), typeof(int), typeof(int), typeof(Transform) }, nameof(BeforeManualOrder), null, null);
            Patch(typeof(Robby), "RunOrder", new[] { typeof(Robby.RobbyOrder), typeof(Transform), typeof(int) }, nameof(BeforeManualOrder), null, null);
            Patch(typeof(Robby), "StartFollowOrder", new[] { typeof(Transform) }, nameof(BeforeManualOrder), null, null);
            Patch(typeof(BoltCore), "BeginStart", new[] { typeof(Bolt.ControlCommandStart) }, nameof(RetireAll), null, null);
            Patch(typeof(BoltCore), "BeginShutdown", new[] { typeof(Bolt.ControlCommandShutdown) }, nameof(RetireAll), null, null);
            Patch(typeof(BoltCore), "ShutdownImmediate", Type.EmptyTypes, nameof(RetireAll), null, null);
            ServerCompanions.Observed += Observe;
            _state = new(CompanionHookStatus.Installed,
                "Native Get queues registered; dispatch and multiplayer behavior still require in-game qualification. Endings do not prove delivery.");

            void Patch(Type type, string name, Type[] args, string prefix, string postfix, string finalizer)
            {
                var method = AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
                HarmonyMethod Hook(string hook) => hook == null ? null : new HarmonyMethod(typeof(CompanionJobs), hook)
                    { priority = HarmonyLib.Priority.Last };
                harmony.Patch(method, prefix: Hook(prefix), postfix: Hook(postfix), finalizer: Hook(finalizer));
            }
        }
        catch (Exception error)
        {
            _state = new(CompanionHookStatus.Failed, "Queue registration failed; partial hooks cannot submit jobs.");
            try { harmony.UnpatchSelf(); }
            catch (Exception) { }
            warning?.Invoke("Companion queue registration failed: " + error);
        }
    }

    private static bool Available()
    {
        if (_thread == 0 || Environment.CurrentManagedThreadId != _thread
            || State.Status != CompanionHookStatus.Installed) return false;
        try { return BoltNetwork.isRunning && BoltNetwork.isServer; }
        catch (Exception error) { Report(error); return false; }
    }

    private static void BeforeTick(VailActor __instance, bool __runOriginal, out TickScope __state)
    {
        __state = null;
        if (!__runOriginal || Queues.Count == 0 || !Available()) return;
        __state = new TickScope { Previous = _tick, Actor = __instance };
        _tick = __state;
    }

    private static Exception FinishTick(Exception __exception, TickScope __state)
    {
        if (__state != null && ReferenceEquals(_tick, __state)) _tick = __state.Previous;
        return __exception;
    }

    private static void BeforePerform(out int __state) { __state = ++_performDepth; }
    private static Exception FinishPerform(Exception __exception, int __state)
    {
        if (__state > 0) _performDepth = __state - 1;
        return __exception;
    }
    private static void BeforeSelection(out int __state) { __state = ++_selectionDepth; }
    private static Exception FinishSelection(Exception __exception, int __state)
    {
        if (__state > 0) _selectionDepth = __state - 1;
        return __exception;
    }

    private static void BeforeUpdateAction(VailActor __instance, bool __runOriginal, out TickScope __state)
    {
        __state = null;
        if (!__runOriginal || _tick == null || _tick.Previous != null || _tick.Visited || _performDepth != 0 || _selectionDepth != 0
            || !Same(__instance, _tick.Actor)) return;
        _tick.Visited = true;
        __state = _tick;
    }

    private static void AfterUpdateAction(bool __result, bool __runOriginal, TickScope __state)
    {
        if (!__runOriginal || __result || __state == null || !ReferenceEquals(_tick, __state)
            || _performDepth != 0 || _selectionDepth != 0 || !Available() || Queues.Count == 0) return;
        bool hasQueue = false;
        foreach (var context in Queues.Values)
            if (Same(context.Actor, __state.Actor)) { hasQueue = true; break; }
        if (!hasQueue) return;
        foreach (var context in new List<QueueContext>(Queues.Values))
        {
            if (!Same(context.Actor, __state.Actor)) continue;
            try { Dispatch(context); }
            catch (Exception error) { Retire(context, "Queue scheduling failed; native outcome is unknown."); Report(error); }
        }
        // Do not alter the return value: Tick still owns native action selection and interruption.
    }

    // Static predicates keep these per-object lifetime hooks allocation-free while no queue is open.
    private static void RetireActor(VailActor __instance) => RetireMatching(__instance, static (context, actor) => Same(context.Actor, actor), "Companion actor lifetime changed.");
    private static void RetireEntity(BoltEntity __instance) => RetireMatching(__instance, static (context, entity) => Same(context.Entity, entity), "Companion entity lifetime changed.");
    private static void RetireCompanion(Robby __instance) => RetireMatching(__instance, static (context, robby) => Same(context.Robby, robby), "Companion lifetime or injury state changed.");
    private static void BeforeManualOrder(Robby __instance) => RetireMatching(__instance, static (context, robby) => !ReferenceEquals(context, _submitting)
        && Same(context.Robby, robby), "A native or manual order took control; queued work was retired.");
    private static void RetireAll() => RetireMatching<object>(null, static (_, _) => true, "The network lifetime ended or restarted.");

    private static void RetireMatching<T>(T target, Func<QueueContext, T, bool> predicate, string reason)
    {
        if (Queues.Count == 0 || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            foreach (var context in new List<QueueContext>(Queues.Values)) if (predicate(context, target)) Retire(context, reason);
        }
        catch (Exception error) { Report(error); }
    }

    private static void Report(Exception error)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;
        try { RuntimeEntryPoint.LogSource?.LogWarning("Companion queue operation failed (" + error.GetType().FullName + "). Further failures will not be logged."); }
        catch (Exception) { }
    }
}
