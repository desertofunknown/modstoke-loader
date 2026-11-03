// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Ai.Vail;
using global::Sons.Ai.Vail.StimuliQueries;
using UnityEngine;

namespace Modstoke.Sons;

/// <summary>A server policy window inside normal native lost-sight thought selection.</summary>
public static partial class ServerNpcSearch
{
    private static NpcSearchState _state = new(NpcSearchHookStatus.NotAttempted,
        "NPC search hooks have not been installed.");
    private static int _installationAttempted;
    private static int _ownerThread;
    private static int _reportedFailure;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static SearchScope _scope;
    [ThreadStatic] private static bool _publishing;
    private const int MaxCachedVerdicts = 1024;
    private static readonly Dictionary<IntPtr, SearchVerdict> SearchVerdicts = new();
    private static int _definitionGeneration;
    private static int _cachedGeneration;

    /// <summary>
    /// Synchronous advisory policy for CombatLostSightOfEnemy / RunToAndSearch candidates.
    /// Native visibility, conditions, priority, interruption, navigation and fallback selection remain
    /// in control. Keep callbacks bounded and do not invoke native actions from a callback.
    /// No subscribers or an unavailable observation leaves native selection unchanged.
    /// </summary>
    public static event Action<NpcSearchRequest> Considering;
    public static NpcSearchState State => Volatile.Read(ref _state);

    internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
    {
        if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
        if (support == null || !support.SafeForHarmonyPatches)
        {
            SetState(NpcSearchHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
            return;
        }
        _ownerThread = Environment.CurrentManagedThreadId;
        GameRuntime.SceneChanged += _ => Interlocked.Increment(ref _definitionGeneration);
        GameRuntime.WorldChanged += _ => Interlocked.Increment(ref _definitionGeneration);
        SetState(NpcSearchHookStatus.Installing, "Registering native NPC search selection hooks.");
        try
        {
            var group = AccessTools.Method(typeof(Group), nameof(Group.TryRunThought),
                new[] { typeof(Thought), typeof(VailActor), typeof(bool), typeof(bool) })
                ?? throw new MissingMethodException("Group", "TryRunThought");
            var thought = AccessTools.Method(typeof(Thought), nameof(Thought.CanRun),
                new[] { typeof(VailActor), typeof(IStimuli).MakeByRefType(), typeof(ConditionSet), typeof(bool) })
                ?? throw new MissingMethodException("Thought", "CanRun");
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-npc-search");
            harmony.Patch(group,
                prefix: new HarmonyMethod(typeof(ServerNpcSearch), nameof(BeforeThought)) { priority = HarmonyLib.Priority.Last },
                finalizer: new HarmonyMethod(typeof(ServerNpcSearch), nameof(FinishThought)));
            harmony.Patch(thought,
                postfix: new HarmonyMethod(typeof(ServerNpcSearch), nameof(AfterCanRun)) { priority = HarmonyLib.Priority.Last });
        }
        catch (Exception error)
        {
            SetState(NpcSearchHookStatus.Failed, "NPC search registration failed; partial hooks remain inactive.");
            addStartupWarning?.Invoke("NPC search hook failed: " + error);
            return;
        }
        SetState(NpcSearchHookStatus.Installed,
            "Native lost-sight search selection hooks registered. Multiplayer behavior is not yet qualified.");
    }

    private sealed class SearchScope
    {
        internal int Depth;
        internal SearchScope Previous;
        internal Group Group;
        internal Thought Thought;
        internal VailActor Actor;
        internal NpcSearchRequest Request;
    }

    private readonly record struct SearchVerdict(IntPtr Group, bool IsSearch);

    private static void BeforeThought(Group __instance, Thought __0, VailActor __1, bool __2, bool __3,
        bool __runOriginal, out int __state)
    {
        __state = ++_depth;
        if (!__runOriginal || __2 || __3 || _publishing || Considering == null
            || State.Status != NpcSearchHookStatus.Installed) return;
        try
        {
            if (!OnServerThread() || !IsSearchCached(__instance, __0) || !IsLiveActor(__1)) return;
            _scope = new SearchScope { Depth = __state, Previous = _scope, Group = __instance, Thought = __0, Actor = __1 };
        }
        catch (Exception error) { ReportFailure(error); }
    }

    // Finalizers restore nesting on both ordinary returns and native/other-plugin exceptions.
    private static Exception FinishThought(Exception __exception, int __state)
    {
        if (__state > 0)
        {
            if (_scope?.Depth == __state) _scope = _scope.Previous;
            _depth = __state - 1;
        }
        return __exception;
    }

    private static void AfterCanRun(Thought __instance, VailActor __0, IStimuli __1, bool __3,
        bool __runOriginal, ref bool __result)
    {
        var scope = _scope;
        var handlers = Considering;
        if (!__runOriginal || !__result || __3 || _publishing || handlers == null || scope == null
            || scope.Depth != _depth || State.Status != NpcSearchHookStatus.Installed) return;
        NpcSearchRequest request = null;
        try
        {
            if (!OnServerThread() || !Same(scope.Thought, __instance) || !Same(scope.Actor, __0)
                || !IsLiveActor(__0) || !IsSearchCached(scope.Group, __instance)
                || __instance._stimuliQuery?.TryCast<StimuliQueryLostSight>() == null) return;
            var memory = __1?.TryCast<LastSeenStimuli>();
            if (memory == null || memory.WasCollected) return;
            var position = memory.Position();
            var age = memory.TimeSinceSeen();
            if (!Finite(position) || !float.IsFinite(age) || age < 0 || age > 10) return;
            request = new NpcSearchRequest((int)__0.TypeId, position, age);
            scope.Request = request;
            _publishing = true;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                handler(request);
                if (request.IsDeferred) break;
            }
            if (request.IsDeferred && IsCurrent(request) && IsLiveActor(scope.Actor)) __result = false;
        }
        catch (Exception error) { ReportFailure(error); }
        finally
        {
            request?.Close();
            if (request != null) { scope.Request = null; _publishing = false; }
        }
    }

    internal static bool IsCurrent(NpcSearchRequest request) =>
        Environment.CurrentManagedThreadId == _ownerThread && _publishing && _scope?.Depth == _depth
        && ReferenceEquals(_scope.Request, request);

    private static bool OnServerThread() => Environment.CurrentManagedThreadId == _ownerThread
        && BoltNetwork.isRunning && BoltNetwork.isServer;

    private static bool IsLiveActor(VailActor actor) => actor != null && !actor.WasCollected && actor
        && actor.isActiveAndEnabled && !actor.IsDead() && !actor.IsDying();

    // Groups and thoughts are shared AI definitions, so a verdict holds until a scene or world change
    // can unload them and let their native addresses be reused.
    private static bool IsSearchCached(Group group, Thought thought)
    {
        if (group == null || group.WasCollected || !group || thought == null || thought.WasCollected) return false;
        var generation = Volatile.Read(ref _definitionGeneration);
        if (generation != _cachedGeneration)
        {
            SearchVerdicts.Clear();
            _cachedGeneration = generation;
        }
        var groupPointer = group.Pointer;
        var thoughtPointer = thought.Pointer;
        if (SearchVerdicts.TryGetValue(thoughtPointer, out var cached) && cached.Group == groupPointer)
            return cached.IsSearch;
        var isSearch = IsSearch(group, thought);
        if (SearchVerdicts.Count >= MaxCachedVerdicts) SearchVerdicts.Clear();
        SearchVerdicts[thoughtPointer] = new SearchVerdict(groupPointer, isSearch);
        return isSearch;
    }

    private static bool IsSearch(Group group, Thought thought)
    {
        if (group == null || group.WasCollected || !group || thought == null || thought.WasCollected
            || !string.Equals(group.name, "CombatLostSightOfEnemy", StringComparison.Ordinal)
            || !string.Equals(thought._stimuliQueryType, "Lost Sight", StringComparison.Ordinal)
            || !string.Equals(thought._stimuliTargetId, "Groups/Enemy", StringComparison.Ordinal)) return false;
        var action = thought._action;
        var sequence = action == null || action.WasCollected ? null : action._sequence;
        return sequence != null && !sequence.WasCollected && sequence
            && string.Equals(sequence.name, "RunToAndSearch", StringComparison.Ordinal);
    }

    private static bool Same(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase left,
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase right) => left != null && right != null
        && !left.WasCollected && !right.WasCollected && left.Pointer == right.Pointer;

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static void SetState(NpcSearchHookStatus status, string detail) =>
        Volatile.Write(ref _state, new NpcSearchState(status, detail));
    private static void ReportFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
        try { RuntimeEntryPoint.LogSource?.LogWarning("NPC search coordination unavailable ("
            + error.GetType().FullName + "). Native selection is unchanged; further failures will not be logged."); }
        catch (Exception) { }
    }
}
