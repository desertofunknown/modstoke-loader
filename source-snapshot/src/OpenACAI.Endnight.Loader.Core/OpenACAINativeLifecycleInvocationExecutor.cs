// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle invocation executor planning. This consumes
// scheduled metadata and publishes would-invoke decisions without invoking mod
// code.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleInvocationExecutor
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationExecutor";
        public const int MaxExecutionDecisionCount = OpenACAINativeLifecycleInvocationScheduler.MaxScheduleEntryCount;
        public const int MaxExecutionBatchCount = OpenACAINativeLifecycleInvocationScheduler.MaxScheduleBatchCount;
        public const int MaxExecutionPlanCount = 128;

        private static readonly bool RuntimeInvocationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoPlannedExecutionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot> RecordedExecutionPlans = new List<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot>();
        private static OpenACAINativeLifecycleInvocationExecutorSnapshot _state = OpenACAINativeLifecycleInvocationExecutorSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _executionPlanRequestCount;
        private static int _acceptedExecutionPlanCount;
        private static int _rejectedExecutionPlanCount;
        private static string _lastExecutionHookName = "";
        private static string _lastExecutionStatus = "INFO";
        private static string _lastExecutionDetail = "No native lifecycle invocation execution plan has been requested.";
        private static string _lastExecutionReason = "none";

        public static OpenACAINativeLifecycleInvocationExecutorSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static bool Ready => State.Ready;
        public static bool ExecutionPlanningEnabled => true;
        public static bool RuntimeInvocationEnabled => RuntimeInvocationEnabledValue;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionBatchSnapshot> ExecutionBatches => State.ExecutionBatches;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> ExecutionDecisions => State.ExecutionDecisions;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot> ExecutionPlans => State.ExecutionPlans;

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionBatchSnapshot> GetExecutionBatches()
        {
            return ExecutionBatches;
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> GetExecutionDecisions()
        {
            return ExecutionDecisions;
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot> GetExecutionPlans()
        {
            return ExecutionPlans;
        }

        public static OpenACAINativeLifecycleInvocationExecutionPlanSnapshot PlanExecution(string hookName)
        {
            var plan = CreateExecutionPlan(State, hookName);
            RecordExecutionPlan(plan);
            Refresh();
            return plan;
        }

        public static OpenACAINativeLifecycleInvocationExecutorSnapshot Refresh()
        {
            return Refresh(OpenACAINativeLifecycleInvocationScheduler.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleInvocationExecutorSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle invocation executor provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleInvocationExecutorSnapshot Refresh(OpenACAINativeLifecycleInvocationSchedulerSnapshot scheduler)
        {
            var snapshot = CreateSnapshot(scheduler);
            if (RecordAutomaticExecutionPlans(snapshot))
            {
                snapshot = CreateSnapshot(scheduler);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleInvocationExecutorSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleInvocationExecutorSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static OpenACAINativeLifecycleInvocationExecutorSnapshot CreateSnapshot(OpenACAINativeLifecycleInvocationSchedulerSnapshot scheduler)
        {
            scheduler = scheduler ?? OpenACAINativeLifecycleInvocationSchedulerSnapshot.CreateUnknown();
            var decisions = new List<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot>();
            foreach (var batch in scheduler.ScheduleBatches
                .Where(batch => batch != null)
                .OrderBy(batch => batch.HookOrder)
                .ThenBy(batch => batch.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(batch => batch.BatchSequenceNumber)
                .Take(MaxExecutionBatchCount))
            {
                var hookSequence = 0;
                foreach (var entry in batch.Entries
                    .Where(entry => entry != null)
                    .OrderBy(entry => entry.HookSequenceNumber)
                    .ThenBy(entry => entry.ScheduleSequenceNumber)
                    .Take(MaxExecutionDecisionCount))
                {
                    hookSequence++;
                    decisions.Add(CreateExecutionDecision(decisions.Count + 1, batch.BatchSequenceNumber, hookSequence, entry));
                    if (decisions.Count >= MaxExecutionDecisionCount)
                    {
                        break;
                    }
                }

                if (decisions.Count >= MaxExecutionDecisionCount)
                {
                    break;
                }
            }

            var decisionArray = decisions.Take(MaxExecutionDecisionCount).ToArray();
            var batches = decisionArray
                .GroupBy(decision => new { decision.BatchSequenceNumber, decision.HookName, decision.HookOrder })
                .OrderBy(group => group.Key.HookOrder)
                .ThenBy(group => group.Key.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.BatchSequenceNumber)
                .Select(group => CreateExecutionBatch(group.Key.BatchSequenceNumber, group.Key.HookName, group.Key.HookOrder, group.ToArray()))
                .Take(MaxExecutionBatchCount)
                .ToArray();

            var executionPlannedCount = decisionArray.Count(decision => decision.ExecutionPlanned);
            var executionIssueCount = decisionArray.Count(decision => decision.Issue);
            var executionEligibleCount = decisionArray.Count(decision => decision.ExecutionEligible);
            var executionGateOpenCount = decisionArray.Count(decision => decision.ExecutionGateOpen);
            var executionGateBlockedCount = decisionArray.Count(decision => decision.ExecutionGateBlocked);
            var methodInvocationCount = decisionArray.Count(decision => decision.MethodInvoked);
            var batchIssueCount = batches.Count(batch => batch.Issue);
            var status = executionIssueCount == 0 && batchIssueCount == 0 ? "READY" : "CHECK";
            var detail = executionIssueCount == 0 && batchIssueCount == 0
                ? "Modstoke native lifecycle invocation executor has produced would-invoke decisions; method invocation remains explicitly disabled."
                : executionIssueCount + " native lifecycle invocation execution issue(s) were found.";

            int executionPlanRequestCount;
            int acceptedExecutionPlanCount;
            int rejectedExecutionPlanCount;
            string lastExecutionHookName;
            string lastExecutionStatus;
            string lastExecutionDetail;
            string lastExecutionReason;
            OpenACAINativeLifecycleInvocationExecutionPlanSnapshot[] executionPlans;
            lock (SyncRoot)
            {
                executionPlanRequestCount = _executionPlanRequestCount;
                acceptedExecutionPlanCount = _acceptedExecutionPlanCount;
                rejectedExecutionPlanCount = _rejectedExecutionPlanCount;
                lastExecutionHookName = _lastExecutionHookName ?? "";
                lastExecutionStatus = _lastExecutionStatus ?? "";
                lastExecutionDetail = _lastExecutionDetail ?? "";
                lastExecutionReason = _lastExecutionReason ?? "";
                executionPlans = RecordedExecutionPlans.ToArray();
            }

            return new OpenACAINativeLifecycleInvocationExecutorSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                scheduler.Ready,
                status,
                detail,
                ExecutionPlanningEnabled,
                RuntimeInvocationEnabled,
                scheduler.CandidateCount,
                scheduler.DispatchTargetCount,
                scheduler.DispatchIntentCount,
                scheduler.QueuedDispatchIntentCount,
                scheduler.InvocationRequestCount,
                scheduler.BindingCount,
                scheduler.BindingReadyCount,
                scheduler.BindingIssueCount,
                scheduler.BindingTokenIssuedCount,
                scheduler.TokenResolutionCount,
                scheduler.TokenResolvedCount,
                scheduler.TokenIssueCount,
                scheduler.JournalEntryCount,
                scheduler.JournaledEntryCount,
                scheduler.JournalIssueCount,
                scheduler.ReplayPlanRequestCount,
                scheduler.AcceptedReplayPlanCount,
                scheduler.RejectedReplayPlanCount,
                scheduler.ScheduleBatchCount,
                scheduler.ScheduleEntryCount,
                scheduler.ScheduledEntryCount,
                scheduler.ScheduleIssueCount,
                scheduler.SchedulePlanRequestCount,
                scheduler.AcceptedSchedulePlanCount,
                scheduler.RejectedSchedulePlanCount,
                scheduler.LibraryExcludedCount,
                batches,
                decisionArray,
                executionPlannedCount,
                executionIssueCount,
                executionEligibleCount,
                executionGateOpenCount,
                executionGateBlockedCount,
                methodInvocationCount,
                executionPlans,
                executionPlanRequestCount,
                acceptedExecutionPlanCount,
                rejectedExecutionPlanCount,
                lastExecutionHookName,
                lastExecutionStatus,
                lastExecutionDetail,
                lastExecutionReason);
        }

        private static OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot CreateExecutionDecision(
            int executionSequenceNumber,
            int batchSequenceNumber,
            int hookSequenceNumber,
            OpenACAINativeLifecycleInvocationScheduleEntrySnapshot entry)
        {
            entry = entry ?? new OpenACAINativeLifecycleInvocationScheduleEntrySnapshot(
                0,
                0,
                0,
                0,
                0,
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                0,
                "",
                false,
                false,
                false,
                RuntimeInvocationEnabled,
                false,
                false,
                "CHECK",
                "missing-schedule-entry",
                "OpenACAI could not plan a native lifecycle invocation execution because schedule metadata was missing.",
                DateTimeOffset.UtcNow.ToString("O"));

            var executionPlanned = entry.Scheduled && !entry.Issue;
            var executionGateOpen = executionPlanned && RuntimeInvocationEnabled;
            var executionGateBlocked = executionPlanned && !RuntimeInvocationEnabled;
            var executionEligible = executionPlanned && RuntimeInvocationEnabled;
            var methodInvoked = false;
            var status = executionPlanned ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = executionPlanned
                ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled"
                : !string.IsNullOrWhiteSpace(entry.IssueCode) ? entry.IssueCode : "schedule-entry-not-ready";
            var detail = executionPlanned
                ? RuntimeInvocationEnabled
                    ? "OpenACAI planned this native lifecycle method invocation for execution."
                    : "OpenACAI planned this native lifecycle method invocation as metadata-only evidence; method invocation remains disabled."
                : "OpenACAI could not plan this native lifecycle method invocation because the schedule entry is not ready.";

            return new OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot(
                executionSequenceNumber,
                batchSequenceNumber,
                hookSequenceNumber,
                entry.HookOrder,
                entry.ScheduleSequenceNumber,
                entry.JournalSequenceNumber,
                entry.ModId,
                entry.DisplayName,
                NormalizeHookName(entry.HookName),
                entry.InvocationKind,
                entry.EntrypointTypeName,
                entry.EntrypointMethodName,
                entry.HookMethodName,
                entry.HookMethodSource,
                entry.HookMethodParameterCount,
                entry.BindingToken,
                entry.Journaled,
                entry.Scheduled,
                executionPlanned,
                executionEligible,
                RuntimeInvocationEnabled,
                executionGateOpen,
                executionGateBlocked,
                methodInvoked,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static OpenACAINativeLifecycleInvocationExecutionBatchSnapshot CreateExecutionBatch(
            int batchSequenceNumber,
            string hookName,
            int hookOrder,
            IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> decisions)
        {
            decisions = decisions ?? Array.Empty<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot>();
            var executionPlannedCount = decisions.Count(decision => decision != null && decision.ExecutionPlanned);
            var executionIssueCount = decisions.Count(decision => decision != null && decision.Issue);
            var executionGateOpenCount = decisions.Count(decision => decision != null && decision.ExecutionGateOpen);
            var executionGateBlockedCount = decisions.Count(decision => decision != null && decision.ExecutionGateBlocked);
            var executionEligibleCount = decisions.Count(decision => decision != null && decision.ExecutionEligible);
            var methodInvocationCount = decisions.Count(decision => decision != null && decision.MethodInvoked);
            var status = executionIssueCount == 0 ? executionPlannedCount > 0 ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "INFO" : "CHECK";
            var issueCode = executionIssueCount == 0
                ? executionPlannedCount > 0 ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled" : "empty-batch"
                : "execution-decision-issues";
            var detail = executionIssueCount == 0
                ? executionPlannedCount > 0
                    ? RuntimeInvocationEnabled
                        ? "OpenACAI planned this hook batch for method invocation."
                        : "OpenACAI planned this hook batch as metadata-only evidence; method invocation remains disabled."
                    : "OpenACAI found no execution decisions for this hook batch."
                : executionIssueCount + " execution decision issue(s) were found in this hook batch.";

            return new OpenACAINativeLifecycleInvocationExecutionBatchSnapshot(
                batchSequenceNumber,
                NormalizeHookName(hookName),
                Math.Max(0, hookOrder),
                decisions,
                executionPlannedCount,
                executionIssueCount,
                executionEligibleCount,
                RuntimeInvocationEnabled,
                executionGateOpenCount,
                executionGateBlockedCount,
                methodInvocationCount,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static bool RecordAutomaticExecutionPlans(OpenACAINativeLifecycleInvocationExecutorSnapshot snapshot)
        {
            if (snapshot == null || snapshot.ExecutionPlannedCount == 0)
            {
                lock (SyncRoot)
                {
                    return AutoPlannedExecutionKeys.RemoveWhere(key => !string.IsNullOrWhiteSpace(key)) > 0;
                }
            }

            var currentHooks = snapshot.ExecutionBatches
                .Where(batch => batch != null && batch.ExecutionPlannedCount > 0 && !string.IsNullOrWhiteSpace(batch.HookName))
                .Select(batch => NormalizeHookName(batch.HookName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentKeySet = new HashSet<string>(currentHooks.Select(hook => "hook:" + hook), StringComparer.OrdinalIgnoreCase);
            var hooksToPlan = new List<string>();

            lock (SyncRoot)
            {
                AutoPlannedExecutionKeys.RemoveWhere(key => !currentKeySet.Contains(key));
                foreach (var hookName in currentHooks)
                {
                    var key = "hook:" + hookName;
                    if (AutoPlannedExecutionKeys.Add(key))
                    {
                        hooksToPlan.Add(hookName);
                    }
                }
            }

            foreach (var hookName in hooksToPlan)
            {
                RecordExecutionPlan(CreateExecutionPlan(snapshot, hookName));
            }

            return hooksToPlan.Count > 0;
        }

        private static OpenACAINativeLifecycleInvocationExecutionPlanSnapshot CreateExecutionPlan(
            OpenACAINativeLifecycleInvocationExecutorSnapshot snapshot,
            string hookName)
        {
            snapshot = snapshot ?? OpenACAINativeLifecycleInvocationExecutorSnapshot.CreateUnknown();
            var normalizedHookName = NormalizeHookName(hookName);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Native lifecycle invocation execution plan is invalid.";
            var accepted = false;
            var canExecuteNow = false;
            OpenACAINativeLifecycleInvocationExecutionBatchSnapshot batch = null;

            if (string.IsNullOrWhiteSpace(normalizedHookName))
            {
                reason = "missing-hook-name";
                detail = "A native lifecycle hook name is required for execution planning.";
            }
            else
            {
                batch = snapshot.ExecutionBatches.FirstOrDefault(candidate =>
                    candidate != null
                    && string.Equals(candidate.HookName, normalizedHookName, StringComparison.OrdinalIgnoreCase));
                if (batch == null || batch.DecisionCount == 0)
                {
                    reason = "hook-batch-not-planned";
                    detail = "No native lifecycle invocation execution batch matched hook '" + normalizedHookName + "'.";
                }
                else if (batch.Issue)
                {
                    reason = "execution-batch-issues";
                    detail = batch.ExecutionIssueCount + " native lifecycle invocation execution issue(s) were found for hook '" + normalizedHookName + "'.";
                }
                else if (batch.ExecutionPlannedCount == 0)
                {
                    reason = "hook-batch-empty";
                    detail = "The native lifecycle invocation execution batch for hook '" + normalizedHookName + "' has no execution-planned decisions.";
                }
                else
                {
                    accepted = true;
                    if (!RuntimeInvocationEnabled)
                    {
                        status = "DEFERRED";
                        reason = "runtime-invocation-disabled";
                        detail = "OpenACAI accepted the native lifecycle invocation execution plan; method invocation remains disabled.";
                    }
                    else
                    {
                        canExecuteNow = true;
                        status = "READY";
                        reason = "ready";
                        detail = "Modstoke native lifecycle invocation execution plan is ready for method invocation.";
                    }
                }
            }

            return new OpenACAINativeLifecycleInvocationExecutionPlanSnapshot(
                normalizedHookName,
                batch == null ? 0 : batch.BatchSequenceNumber,
                batch == null ? 0 : batch.HookOrder,
                batch == null ? 0 : batch.DecisionCount,
                batch == null ? 0 : batch.ExecutionPlannedCount,
                batch == null ? 0 : batch.ExecutionIssueCount,
                batch == null ? 0 : batch.ExecutionGateOpenCount,
                batch == null ? 0 : batch.ExecutionGateBlockedCount,
                batch == null ? 0 : batch.MethodInvocationCount,
                accepted,
                canExecuteNow,
                RuntimeInvocationEnabled,
                status,
                reason,
                detail,
                batch,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static void RecordExecutionPlan(OpenACAINativeLifecycleInvocationExecutionPlanSnapshot plan)
        {
            lock (SyncRoot)
            {
                _executionPlanRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedExecutionPlanCount++;
                }
                else
                {
                    _rejectedExecutionPlanCount++;
                }

                _lastExecutionHookName = plan == null ? "" : plan.HookName;
                _lastExecutionStatus = plan == null ? "CHECK" : plan.Status;
                _lastExecutionDetail = plan == null ? "Native lifecycle invocation execution plan was null." : plan.Detail;
                _lastExecutionReason = plan == null ? "null-plan" : plan.ReasonCode;
                if (plan != null)
                {
                    RecordedExecutionPlans.Add(plan);
                    while (RecordedExecutionPlans.Count > MaxExecutionPlanCount)
                    {
                        RecordedExecutionPlans.RemoveAt(0);
                    }
                }
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Executor",
                "OpenACAI-owned native lifecycle invocation execution planning.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateDecisionSection(snapshot),
                    CreatePlanSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleInvocationExecutorSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Invocation Executor",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle invocation executor snapshot."),
                    Row("Execution Planning", snapshot.ExecutionPlanningEnabled ? "Enabled" : "Disabled", snapshot.ExecutionPlanningEnabled ? "READY" : "CHECK", "Scheduled invocation metadata is converted into would-invoke decisions without invoking mod code."),
                    Row("Runtime Invocation", snapshot.RuntimeInvocationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", "Native lifecycle method invocation remains disabled until verified in game."),
                    Row("Decisions", snapshot.ExecutionDecisionCount.ToString(), snapshot.ExecutionIssueCount == 0 ? "READY" : "CHECK", snapshot.ExecutionPlannedCount + " execution-planned decision or decisions; " + snapshot.ExecutionIssueCount + " issue(s)."),
                    Row("Execution Gate", snapshot.ExecutionGateOpenCount + " open", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", snapshot.ExecutionGateBlockedCount + " execution-planned invocation(s) blocked by the disabled runtime invocation gate."),
                    Row("Method Invocations", snapshot.MethodInvocationCount.ToString(), snapshot.MethodInvocationCount == 0 ? "READY" : "CHECK", "The OpenACAI executor does not call mod lifecycle methods while runtime invocation is disabled."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle invocation execution planning.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateDecisionSection(OpenACAINativeLifecycleInvocationExecutorSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Execution Batches", snapshot.ExecutionBatchCount.ToString(), snapshot.ExecutionIssueCount == 0 ? "READY" : "CHECK", "Per-hook would-invoke execution batches.")
            };

            foreach (var batch in snapshot.ExecutionBatches.Take(10))
            {
                rows.Add(Row(
                    "#" + batch.BatchSequenceNumber + " " + (string.IsNullOrWhiteSpace(batch.HookName) ? "hook" : batch.HookName),
                    batch.ExecutionPlannedCount + "/" + batch.DecisionCount,
                    batch.Status,
                    batch.Detail));
            }

            if (snapshot.ExecutionBatchCount == 0)
            {
                rows.Add(Row("No Execution Batches", "0", "INFO", "No Modstoke-native lifecycle invocation execution batches were planned."));
            }

            return new OpenACAIMenuSectionSnapshot("Execution Decisions", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreatePlanSection(OpenACAINativeLifecycleInvocationExecutorSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Execution Plans", snapshot.ExecutionPlanRequestCount + " requested", "INFO", snapshot.AcceptedExecutionPlanCount + " accepted, " + snapshot.RejectedExecutionPlanCount + " rejected."),
                Row("Last Hook", string.IsNullOrWhiteSpace(snapshot.LastExecutionHookName) ? "none" : snapshot.LastExecutionHookName, "INFO", "Last native lifecycle invocation execution hook."),
                Row("Last Execution", snapshot.LastExecutionStatus, snapshot.LastExecutionStatus, snapshot.LastExecutionDetail),
                Row("Last Reason", snapshot.LastExecutionReason, snapshot.LastExecutionStatus, "Last native lifecycle invocation execution reason code.")
            };

            foreach (var plan in snapshot.ExecutionPlans.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(plan.HookName) ? "Execution" : plan.HookName,
                    plan.ExecutionPlannedCount + "/" + plan.DecisionCount,
                    plan.Status,
                    plan.Detail));
            }

            return new OpenACAIMenuSectionSnapshot("Execution Plans", rows);
        }

        private static string NormalizeHookName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleInvocationExecutor;
            public string DisplayName => "Modstoke Native Executor";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 714;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle invocation execution planning diagnostics.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleInvocationExecutor.CreateContent();
            }
        }
    }
}
