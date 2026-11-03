// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Retired native lifecycle planner surface. ModRuntime binds and invokes
// lifecycle hooks; these classes keep the old read-only API and derive their
// data from ModRuntime.State instead of a separate planning chain.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Modstoke.Loader
{
    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleRegistry
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleRegistry";
        public const int MaxRegistrationCount = 512;

        public static LifecycleRegistrySnapshot State => LifecycleView.Current.Registry;
        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => false;
        public static bool RuntimeInvocationEnabled => State.RuntimeInvocationEnabled;
        public static IReadOnlyList<LifecycleRegistrationSnapshot> PlannedRegistrations => State.PlannedRegistrations;

        public static IReadOnlyList<LifecycleRegistrationSnapshot> GetPlannedRegistrations()
        {
            return PlannedRegistrations;
        }

        public static LifecycleRegistrySnapshot Refresh()
        {
            return State;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleDispatchPlanner
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleDispatcher";
        public const int MaxDispatchTargetCount = 512;
        public const int MaxDispatchIntentCount = 512;
        public const int MaxHookNameLength = ModHost.MaxLifecycleHookLength;

        public static LifecycleDispatchPlannerSnapshot State => LifecycleView.Current.Dispatcher;
        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => false;
        public static bool RuntimeDispatchEnabled => State.RuntimeDispatchEnabled;
        public static IReadOnlyList<LifecycleDispatchTargetSnapshot> DispatchTargets => State.DispatchTargets;
        public static IReadOnlyList<LifecycleDispatchIntentSnapshot> DispatchIntentions => State.DispatchIntentions;

        public static IReadOnlyList<LifecycleDispatchTargetSnapshot> GetDispatchTargets()
        {
            return DispatchTargets;
        }

        public static IReadOnlyList<LifecycleDispatchIntentSnapshot> GetDispatchIntentions()
        {
            return DispatchIntentions;
        }

        public static LifecycleDispatchPlan PlanDispatch(string hookName)
        {
            return new LifecycleDispatchPlan(LifecycleView.Current.Group(hookName));
        }

        public static LifecycleDispatchPlannerSnapshot Refresh()
        {
            return State;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleInvocationPlanner
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationBridge";
        public const int MaxInvocationRequestCount = LifecycleDispatchPlanner.MaxDispatchIntentCount;
        public const int MaxHookNameLength = ModHost.MaxLifecycleHookLength;

        public static LifecycleInvocationPlannerSnapshot State => LifecycleView.Current.Invocation;
        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => false;
        public static bool RuntimeInvocationEnabled => State.RuntimeInvocationEnabled;
        public static IReadOnlyList<LifecycleInvocationRequestSnapshot> InvocationRequests => State.InvocationRequests;

        public static IReadOnlyList<LifecycleInvocationRequestSnapshot> GetInvocationRequests()
        {
            return InvocationRequests;
        }

        public static LifecycleInvocationPlan PlanInvocation(string hookName)
        {
            return new LifecycleInvocationPlan(LifecycleView.Current.Group(hookName));
        }

        public static LifecycleInvocationPlannerSnapshot Refresh()
        {
            return State;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleBindings
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationAdapter";
        public const int MaxBindingCount = LifecycleInvocationPlanner.MaxInvocationRequestCount;
        public const int MaxHookNameLength = ModHost.MaxLifecycleHookLength;

        public static LifecycleBindingsSnapshot State => LifecycleView.Current.Bindings;
        public static bool Ready => State.Ready;
        public static bool BindingEnabled => false;
        public static bool RuntimeInvocationEnabled => State.RuntimeInvocationEnabled;
        public static IReadOnlyList<LifecycleBindingSnapshot> Bindings => State.Bindings;

        public static IReadOnlyList<LifecycleBindingSnapshot> GetBindings()
        {
            return Bindings;
        }

        public static IReadOnlyList<string> GetBindingTokens()
        {
            return State.BindingTokens;
        }

        public static LifecycleBindingsSnapshot Refresh()
        {
            return State;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleTokenResolver
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationTokenResolver";
        public const int MaxResolutionCount = LifecycleBindings.MaxBindingCount;
        public const int MaxLookupResultCount = 128;

        public static LifecycleTokenResolverSnapshot State => LifecycleView.Current.Tokens;
        public static bool Ready => State.Ready;
        public static bool ResolvingEnabled => false;
        public static bool RuntimeInvocationEnabled => State.RuntimeInvocationEnabled;
        public static IReadOnlyList<LifecycleTokenResolutionSnapshot> TokenResolutions => State.TokenResolutions;
        public static IReadOnlyList<LifecycleTokenLookupSnapshot> LookupResults => State.LookupResults;

        public static IReadOnlyList<LifecycleTokenResolutionSnapshot> GetTokenResolutions()
        {
            return TokenResolutions;
        }

        public static IReadOnlyList<LifecycleTokenLookupSnapshot> GetLookupResults()
        {
            return LookupResults;
        }

        public static LifecycleTokenLookupSnapshot LookupBindingToken(string bindingToken)
        {
            return new LifecycleTokenLookupSnapshot(LifecycleView.Current.Find(bindingToken));
        }

        public static bool TryResolveBindingToken(string bindingToken, out LifecycleTokenResolutionSnapshot resolution)
        {
            var row = LifecycleView.Current.Find(bindingToken);
            resolution = row.Known ? new LifecycleTokenResolutionSnapshot(row) : null;
            return row.Known;
        }

        public static LifecycleTokenResolutionSnapshot ResolveBindingToken(string bindingToken)
        {
            return new LifecycleTokenResolutionSnapshot(LifecycleView.Current.Find(bindingToken));
        }

        public static LifecycleTokenResolverSnapshot Refresh()
        {
            return State;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleJournal
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationJournal";
        public const int MaxJournalEntryCount = LifecycleTokenResolver.MaxResolutionCount;
        public const int MaxReplayPlanCount = 128;

        public static LifecycleJournalSnapshot State => LifecycleView.Current.Journal;
        public static bool Ready => State.Ready;
        public static bool JournalingEnabled => false;
        public static bool RuntimeInvocationEnabled => State.RuntimeInvocationEnabled;
        public static IReadOnlyList<LifecycleJournalEntrySnapshot> JournalEntries => State.JournalEntries;
        public static IReadOnlyList<LifecycleReplayPlanSnapshot> ReplayPlans => State.ReplayPlans;

        public static IReadOnlyList<LifecycleJournalEntrySnapshot> GetJournalEntries()
        {
            return JournalEntries;
        }

        public static IReadOnlyList<LifecycleReplayPlanSnapshot> GetReplayPlans()
        {
            return ReplayPlans;
        }

        public static LifecycleReplayPlanSnapshot PlanReplay(string bindingToken)
        {
            return new LifecycleReplayPlanSnapshot(LifecycleView.Current.Find(bindingToken));
        }

        public static LifecycleJournalSnapshot Refresh()
        {
            return State;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleSchedulePlanner
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationScheduler";
        public const int MaxScheduleEntryCount = LifecycleJournal.MaxJournalEntryCount;
        public const int MaxScheduleBatchCount = 128;
        public const int MaxSchedulePlanCount = 128;

        public static LifecycleSchedulePlannerSnapshot State => LifecycleView.Current.Scheduler;
        public static bool Ready => State.Ready;
        public static bool SchedulingEnabled => false;
        public static bool RuntimeInvocationEnabled => State.RuntimeInvocationEnabled;
        public static IReadOnlyList<LifecycleScheduleBatchSnapshot> ScheduleBatches => State.ScheduleBatches;
        public static IReadOnlyList<LifecycleScheduleEntrySnapshot> ScheduleEntries => State.ScheduleEntries;
        public static IReadOnlyList<LifecycleSchedulePlanSnapshot> SchedulePlans => State.SchedulePlans;

        public static IReadOnlyList<LifecycleScheduleBatchSnapshot> GetScheduleBatches()
        {
            return ScheduleBatches;
        }

        public static IReadOnlyList<LifecycleScheduleEntrySnapshot> GetScheduleEntries()
        {
            return ScheduleEntries;
        }

        public static IReadOnlyList<LifecycleSchedulePlanSnapshot> GetSchedulePlans()
        {
            return SchedulePlans;
        }

        public static LifecycleSchedulePlanSnapshot PlanSchedule(string hookName)
        {
            return new LifecycleSchedulePlanSnapshot(LifecycleView.Current.Group(hookName));
        }

        public static LifecycleSchedulePlannerSnapshot Refresh()
        {
            return State;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public static class LifecycleExecutionPlanner
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationExecutor";
        public const int MaxExecutionDecisionCount = LifecycleSchedulePlanner.MaxScheduleEntryCount;
        public const int MaxExecutionBatchCount = LifecycleSchedulePlanner.MaxScheduleBatchCount;
        public const int MaxExecutionPlanCount = 128;

        public static LifecycleExecutionPlannerSnapshot State => LifecycleView.Current.Executor;
        public static bool Ready => State.Ready;
        public static bool ExecutionPlanningEnabled => false;
        public static bool RuntimeInvocationEnabled => State.RuntimeInvocationEnabled;
        public static IReadOnlyList<LifecycleExecutionBatchSnapshot> ExecutionBatches => State.ExecutionBatches;
        public static IReadOnlyList<LifecycleExecutionDecisionSnapshot> ExecutionDecisions => State.ExecutionDecisions;
        public static IReadOnlyList<LifecycleExecutionPlanSnapshot> ExecutionPlans => State.ExecutionPlans;

        public static IReadOnlyList<LifecycleExecutionBatchSnapshot> GetExecutionBatches()
        {
            return ExecutionBatches;
        }

        public static IReadOnlyList<LifecycleExecutionDecisionSnapshot> GetExecutionDecisions()
        {
            return ExecutionDecisions;
        }

        public static IReadOnlyList<LifecycleExecutionPlanSnapshot> GetExecutionPlans()
        {
            return ExecutionPlans;
        }

        public static LifecycleExecutionPlanSnapshot PlanExecution(string hookName)
        {
            return new LifecycleExecutionPlanSnapshot(LifecycleView.Current.Group(hookName));
        }

        public static LifecycleExecutionPlannerSnapshot Refresh()
        {
            return State;
        }
    }

    // One immutable view per ModRuntime snapshot; rebuilt only when the runtime publishes.
    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    internal sealed class LifecycleView
    {
        private static LifecycleView _current;

        private LifecycleView(ModRuntimeSnapshot runtime)
        {
            Runtime = runtime;
            GeneratedUtc = DateTimeOffset.UtcNow.ToString("O");
            RuntimeInvocationEnabled = runtime.ExecutionEnabled && !runtime.Stopped;

            // A stable sort keeps load order inside each hook.
            var hooks = runtime.Mods
                .SelectMany(mod => mod.Hooks.Select(hook => (Mod: mod, Hook: hook)))
                .OrderBy(pair => HookOrderOf(pair.Hook.Name))
                .ToArray();
            var rows = new List<LifecycleHookRow>(hooks.Length);
            var groups = new List<LifecycleHookGroup>();
            foreach (var batch in hooks.GroupBy(pair => pair.Hook.Name))
            {
                var batchRows = batch.Select((pair, index) => new LifecycleHookRow(this, pair.Mod, pair.Hook,
                    rows.Count + index + 1, groups.Count + 1, index + 1)).ToArray();
                rows.AddRange(batchRows);
                groups.Add(new LifecycleHookGroup(this, batch.Key, batchRows, groups.Count + 1));
            }

            Rows = rows;
            Groups = groups;
            Count = rows.Count;
            FailedCount = rows.Count(row => row.Failed);
            ActiveCount = rows.Count(row => row.Active);
            Ready = runtime.LoadCompleted;
            Status = !runtime.LoadCompleted ? "PENDING" : runtime.FailedCount > 0 ? "CHECK" : "READY";
            Detail = "Derived from ModRuntime.State: " + Count + " bound hook(s) across " + runtime.Mods.Count
                + " native mod(s), " + runtime.FailedCount + " failed.";

            Registry = new LifecycleRegistrySnapshot(this);
            Dispatcher = new LifecycleDispatchPlannerSnapshot(this);
            Invocation = new LifecycleInvocationPlannerSnapshot(this);
            Bindings = new LifecycleBindingsSnapshot(this);
            Tokens = new LifecycleTokenResolverSnapshot(this);
            Journal = new LifecycleJournalSnapshot(this);
            Scheduler = new LifecycleSchedulePlannerSnapshot(this);
            Executor = new LifecycleExecutionPlannerSnapshot(this);
        }

        internal static LifecycleView Current
        {
            get
            {
                var runtime = ModRuntime.State;
                var current = Volatile.Read(ref _current);
                if (current == null || current.Runtime != runtime)
                {
                    current = new LifecycleView(runtime);
                    Volatile.Write(ref _current, current);
                }

                return current;
            }
        }

        internal ModRuntimeSnapshot Runtime { get; }
        internal string GeneratedUtc { get; }
        internal bool RuntimeInvocationEnabled { get; }
        internal IReadOnlyList<LifecycleHookRow> Rows { get; }
        internal IReadOnlyList<LifecycleHookGroup> Groups { get; }
        internal int Count { get; }
        internal int FailedCount { get; }
        internal int ActiveCount { get; }
        internal int ReadyCount => Count - FailedCount;
        internal int BlockedCount => Count - ActiveCount;
        internal bool Ready { get; }
        internal string Status { get; }
        internal string Detail { get; }
        internal LifecycleRegistrySnapshot Registry { get; }
        internal LifecycleDispatchPlannerSnapshot Dispatcher { get; }
        internal LifecycleInvocationPlannerSnapshot Invocation { get; }
        internal LifecycleBindingsSnapshot Bindings { get; }
        internal LifecycleTokenResolverSnapshot Tokens { get; }
        internal LifecycleJournalSnapshot Journal { get; }
        internal LifecycleSchedulePlannerSnapshot Scheduler { get; }
        internal LifecycleExecutionPlannerSnapshot Executor { get; }

        internal IReadOnlyList<T> Map<T>(Func<LifecycleHookRow, T> create)
        {
            return Array.AsReadOnly(Rows.Select(create).ToArray());
        }

        internal LifecycleHookRow Find(string bindingToken)
        {
            return Rows.FirstOrDefault(row => string.Equals(row.Token, bindingToken, StringComparison.OrdinalIgnoreCase))
                ?? LifecycleHookRow.Unknown(this, bindingToken);
        }

        internal LifecycleHookGroup Group(string hookName)
        {
            var name = ModRuntime.TryParseHook((hookName ?? "").Trim(), out var hook)
                ? hook.ToString().ToLowerInvariant()
                : (hookName ?? "").Trim();
            return Groups.FirstOrDefault(group => group.HookName == name)
                ?? new LifecycleHookGroup(this, name, Array.Empty<LifecycleHookRow>(), 0);
        }

        internal static int HookOrderOf(string hookName)
        {
            return ModRuntime.TryParseHook(hookName, out var hook) ? (int)hook : -1;
        }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    internal sealed class LifecycleHookRow
    {
        internal LifecycleHookRow(LifecycleView view, ModRuntimeEntry mod, ModRuntimeHook hook,
            int sequence, int batchSequence, int hookSequence)
        {
            View = view;
            Known = true;
            ModId = mod.ModId ?? "";
            DisplayName = mod.Name ?? "";
            HookName = hook.Name;
            HookOrder = LifecycleView.HookOrderOf(hook.Name);
            MethodName = hook.MethodName ?? "";
            Token = ModId + ":" + HookName;
            Sequence = sequence;
            BatchSequence = batchSequence;
            HookSequence = hookSequence;
            Active = mod.Active;
            Failed = mod.Failed;
            Status = Failed ? "CHECK" : Active ? "READY" : "INFO";
            IssueCode = Failed ? "mod-failed" : "";
            Detail = mod.Detail ?? "";
        }

        private LifecycleHookRow(LifecycleView view, string token)
        {
            View = view;
            ModId = DisplayName = HookName = MethodName = "";
            Token = token ?? "";
            HookOrder = -1;
            Status = "CHECK";
            IssueCode = "unknown-token";
            Detail = "No bound native lifecycle hook matches this token.";
        }

        internal static LifecycleHookRow Unknown(LifecycleView view, string token)
        {
            return new LifecycleHookRow(view, token);
        }

        internal LifecycleView View { get; }
        internal bool Known { get; }
        internal string ModId { get; }
        internal string DisplayName { get; }
        internal string HookName { get; }
        internal int HookOrder { get; }
        internal string MethodName { get; }
        internal string Token { get; }
        internal int Sequence { get; }
        internal int BatchSequence { get; }
        internal int HookSequence { get; }
        internal bool Active { get; }
        internal bool Failed { get; }
        internal bool Ready => Known && !Failed;
        internal string Status { get; }
        internal string IssueCode { get; }
        internal string Detail { get; }
        internal string GeneratedUtc => View.GeneratedUtc;
        internal bool RuntimeInvocationEnabled => View.RuntimeInvocationEnabled;
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    internal sealed class LifecycleHookGroup
    {
        internal LifecycleHookGroup(LifecycleView view, string hookName, IReadOnlyList<LifecycleHookRow> rows, int sequence)
        {
            View = view;
            HookName = hookName ?? "";
            HookOrder = LifecycleView.HookOrderOf(HookName);
            Rows = rows;
            Sequence = sequence;
            Count = rows.Count;
            FailedCount = rows.Count(row => row.Failed);
            ActiveCount = rows.Count(row => row.Active);
            Status = Count == 0 || FailedCount > 0 ? "CHECK" : ActiveCount == Count ? "READY" : "INFO";
            ReasonCode = Count > 0 ? FailedCount > 0 ? "mod-failed" : ""
                : HookOrder < 0 ? "unsupported-hook" : "no-bound-hooks";
            Detail = Count > 0
                ? Count + " bound hook(s), " + ActiveCount + " active, " + FailedCount + " failed."
                : HookOrder < 0 ? "Unsupported native lifecycle hook." : "No native mod has bound this lifecycle hook.";
        }

        internal LifecycleView View { get; }
        internal string HookName { get; }
        internal int HookOrder { get; }
        internal IReadOnlyList<LifecycleHookRow> Rows { get; }
        internal int Sequence { get; }
        internal int Count { get; }
        internal int FailedCount { get; }
        internal int ActiveCount { get; }
        internal int ReadyCount => Count - FailedCount;
        internal int BlockedCount => Count - ActiveCount;
        internal bool Accepted => Count > 0;
        internal string Status { get; }
        internal string ReasonCode { get; }
        internal string Detail { get; }
        internal string GeneratedUtc => View.GeneratedUtc;
        internal bool RuntimeInvocationEnabled => View.RuntimeInvocationEnabled;
    }
}
