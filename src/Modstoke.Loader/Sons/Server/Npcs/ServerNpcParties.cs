// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Ai.Vail;
using global::Sons.Characters;
using UnityEngine;

namespace Modstoke.Sons;

/// <summary>Bounded policy for native queued search-party scheduling, before targets or actors are created.</summary>
public static class ServerNpcParties
{
    private const int MaximumQueuedEvents = 256;
    private const int CandidatesPerFrame = 8;
    private const float ReleaseAgeHours = 1.25f;
    private static NpcPartyState _state = new(NpcPartyHookStatus.NotAttempted,
        "NPC party scheduling hooks have not been installed.");
    private static int _installationAttempted, _ownerThread, _reportedFailure;
    private static int _frame = -1, _candidates;
    [ThreadStatic] private static int _updateDepth, _directDepth, _runDepth;
    [ThreadStatic] private static UpdateScope _scope;
    [ThreadStatic] private static RunScope _run;
    [ThreadStatic] private static bool _publishing;

    public static NpcPartyState State => Volatile.Read(ref _state);
    /// <summary>Runs synchronously inside a supported queued scheduling call. Keep handlers bounded.</summary>
    public static event Action<NpcPartyRequest> Considering;
    /// <summary>Reports a native scheduling return, not a spawn or encounter-completion receipt.</summary>
    public static event Action<NpcPartyAttempt> Attempted;

    private sealed class UpdateScope
    {
        internal int Depth;
        internal UpdateScope Previous;
        internal VailWorldEvents Manager;
        internal VailWorldSimulation Simulation;
        internal WorldRecordContext World;
    }

    private sealed class RunScope
    {
        internal int Depth;
        internal UpdateScope Update;
        internal RunScope Previous;
        internal VailWorldEventData.EventBase Event;
        internal VailWorldEvents.QueuedEvent Queued;
        internal float StartTime;
        internal int Day;
        internal NpcPartyRequest Request;
        internal bool Skipped;
    }

    internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
    {
        if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
        if (support == null || !support.SafeForHarmonyPatches)
        {
            SetState(NpcPartyHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
            return;
        }
        _ownerThread = Environment.CurrentManagedThreadId;
        SetState(NpcPartyHookStatus.Installing, "Registering queued NPC party scheduling hooks.");
        try
        {
            var update = AccessTools.Method(typeof(VailWorldEvents), nameof(VailWorldEvents.Update), Type.EmptyTypes)
                ?? throw new MissingMethodException("VailWorldEvents", "Update");
            var run = AccessTools.Method(typeof(VailWorldEvents), nameof(VailWorldEvents.RunEvent),
                new[] { typeof(VailWorldEventData.EventBase), typeof(float), typeof(int), typeof(Transform) })
                ?? throw new MissingMethodException("VailWorldEvents", "RunEvent");
            var direct = AccessTools.Method(typeof(VailWorldEvents), nameof(VailWorldEvents.RunEventNow),
                new[] { typeof(VailWorldEventData.EventBase) })
                ?? throw new MissingMethodException("VailWorldEvents", "RunEventNow");
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-npc-parties");
            harmony.Patch(update,
                prefix: new HarmonyMethod(typeof(ServerNpcParties), nameof(BeforeUpdate)) { priority = HarmonyLib.Priority.Last },
                finalizer: new HarmonyMethod(typeof(ServerNpcParties), nameof(FinishUpdate)));
            harmony.Patch(direct,
                prefix: new HarmonyMethod(typeof(ServerNpcParties), nameof(BeforeDirect)) { priority = HarmonyLib.Priority.First },
                finalizer: new HarmonyMethod(typeof(ServerNpcParties), nameof(FinishDirect)));
            harmony.Patch(run,
                prefix: new HarmonyMethod(typeof(ServerNpcParties), nameof(BeforeRun)) { priority = HarmonyLib.Priority.Last },
                postfix: new HarmonyMethod(typeof(ServerNpcParties), nameof(AfterRun)) { priority = HarmonyLib.Priority.Last },
                finalizer: new HarmonyMethod(typeof(ServerNpcParties), nameof(FinishRun)));
        }
        catch (Exception error)
        {
            SetState(NpcPartyHookStatus.Failed, "NPC party registration failed; partial hooks remain inactive.");
            addStartupWarning?.Invoke("NPC party scheduling hook failed: " + error);
            return;
        }
        SetState(NpcPartyHookStatus.Installed,
            "Queued search-party scheduling hooks registered. Native multiplayer behavior remains unqualified.");
    }

    private static void BeforeUpdate(VailWorldEvents __instance, bool __runOriginal, out int __state)
    {
        __state = ++_updateDepth;
        if (!__runOriginal || _publishing || (Considering == null && Attempted == null)
            || State.Status != NpcPartyHookStatus.Installed) return;
        try
        {
            var simulation = VailWorldSimulation._instance;
            if (!OnServerThread() || !Live(__instance) || !Live(simulation)
                || !WorldRecords.TryGetCurrent(out var world, out _)) return;
            _scope = new UpdateScope { Depth = __state, Previous = _scope,
                Manager = __instance, Simulation = simulation, World = world };
        }
        catch (Exception error) { ReportFailure(error); }
    }

    private static Exception FinishUpdate(Exception __exception, int __state)
    {
        if (__state > 0)
        {
            if (_scope?.Depth == __state) _scope = _scope.Previous;
            _updateDepth = __state - 1;
        }
        return __exception;
    }

    private static void BeforeDirect(out int __state) => __state = ++_directDepth;
    private static Exception FinishDirect(Exception __exception, int __state)
    {
        if (__state > 0) _directDepth = __state - 1;
        return __exception;
    }

    private static bool BeforeRun(VailWorldEvents __instance, VailWorldEventData.EventBase __0,
        float __1, int __2, Transform __3, bool __runOriginal, ref bool __result, out RunScope __state)
    {
        __state = new RunScope { Depth = ++_runDepth, Previous = _run };
        _run = __state;
        if (!__runOriginal || _runDepth != 1 || _directDepth != 0 || _publishing
            || State.Status != NpcPartyHookStatus.Installed || !ReferenceEquals(__3, null)
            || (Considering == null && Attempted == null)) return true;
        var current = __state;
        try
        {
            var scope = _scope;
            if (!Current(scope) || !Same(scope.Manager, __instance) || __0 == null || __0.WasCollected
                || __0.TryCast<VailWorldEventData.SearchPartyEvent>() == null || !float.IsFinite(__1) || __2 < 0)
                return true;
            var frame = Time.frameCount;
            if (_frame != frame) { _frame = frame; _candidates = 0; }
            if (_candidates >= CandidatesPerFrame) return true;
            _candidates++;
            current.Update = scope;
            current.Event = __0;
            current.StartTime = __1;
            current.Day = __2;
            if (!FindQueued(current, out var queued, out var age)) return true;
            current.Queued = queued;
            var request = new NpcPartyRequest(scope.World, __2, age);
            current.Request = request;
            _publishing = true;
            var handlers = Considering;
            if (handlers != null)
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsDeferred) break;
                }
            if (request.IsDeferred && CanDefer(request))
            {
                current.Skipped = true;
                __result = false;
                return false;
            }
        }
        catch (Exception error) { ReportFailure(error); }
        finally
        {
            current.Request?.Close();
            _publishing = false;
        }
        return true;
    }

    private static void AfterRun(bool __result, bool __runOriginal, RunScope __state)
    {
        if (!__runOriginal || __state?.Request == null || __state.Skipped || _publishing
            || !ReferenceEquals(_run, __state)) return;
        try
        {
            if (!Current(__state.Update)) return;
            var handlers = Attempted;
            if (handlers == null) return;
            var observation = new NpcPartyAttempt(__state.Request, __result);
            _publishing = true;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers)) handler(observation);
        }
        catch (Exception error) { ReportFailure(error); }
        finally { _publishing = false; }
    }

    private static Exception FinishRun(Exception __exception, RunScope __state)
    {
        if (__state != null && __state.Depth > 0)
        {
            __state.Request?.Close();
            if (ReferenceEquals(_run, __state)) _run = __state.Previous;
            _runDepth = __state.Depth - 1;
        }
        return __exception;
    }

    internal static bool CanDefer(NpcPartyRequest request)
    {
        var run = _run;
        if (!_publishing || run?.Request == null || !ReferenceEquals(run.Request, request)
            || Environment.CurrentManagedThreadId != _ownerThread || _runDepth != run.Depth || _directDepth != 0)
            return false;
        try
        {
            return FindQueued(run, out var queued, out var age) && Same(queued, run.Queued) && age < ReleaseAgeHours;
        }
        catch (Exception error) { ReportFailure(error); return false; }
    }

    private static bool FindQueued(RunScope run, out VailWorldEvents.QueuedEvent match, out float age)
    {
        match = null;
        age = 0;
        if (!Current(run.Update)) return false;
        var manager = run.Update.Manager;
        var queue = manager._queuedEvents;
        if (queue == null || queue.WasCollected) return false;
        var count = queue.Count;
        if (count < 1 || count > MaximumQueuedEvents) return false;
        var now = run.Update.Simulation.TimeInHours;
        age = now - run.StartTime;
        if (!float.IsFinite(now) || !float.IsFinite(age) || age < 0 || age > 1.5f) return false;
        for (var index = 0; index < count; index++)
        {
            var entry = queue[index];
            if (entry == null || entry.WasCollected) return false;
            if (!Same(entry._event, run.Event) || entry.day != run.Day || entry._startTimeInHours != run.StartTime
                || !ReferenceEquals(entry._forceTarget, null)) continue;
            if (match != null) return false;
            match = entry;
        }
        return match != null && queue.Count == count && Same(queue, manager._queuedEvents) && Current(run.Update);
    }

    private static bool Current(UpdateScope scope) => scope != null && ReferenceEquals(_scope, scope)
        && scope.Depth == _updateDepth && OnServerThread() && scope.World.IsCurrent
        && Live(scope.Manager) && Live(scope.Simulation) && Same(scope.Simulation, VailWorldSimulation._instance);
    private static bool OnServerThread() => Environment.CurrentManagedThreadId == _ownerThread
        && BoltNetwork.isRunning && BoltNetwork.isServer;
    private static bool Live(Behaviour value) => value != null && !value.WasCollected && value && value.isActiveAndEnabled;
    private static bool Same(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase left,
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase right) => left != null && right != null
        && !left.WasCollected && !right.WasCollected && left.Pointer == right.Pointer;
    private static void SetState(NpcPartyHookStatus status, string detail) =>
        Volatile.Write(ref _state, new NpcPartyState(status, detail));
    private static void ReportFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
        try { RuntimeEntryPoint.LogSource?.LogWarning("NPC party pacing unavailable ("
            + error.GetType().FullName + "). Native scheduling continues; further failures will not be logged."); }
        catch (Exception) { }
    }
}
