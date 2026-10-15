// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenACAI.Loader
{
    public sealed class OpenACAINativeLifecycleInvocationExecutorSnapshot
    {
        internal OpenACAINativeLifecycleInvocationExecutorSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool executionPlanningEnabled,
            bool runtimeInvocationEnabled,
            int candidateCount,
            int dispatchTargetCount,
            int dispatchIntentCount,
            int queuedDispatchIntentCount,
            int invocationRequestCount,
            int bindingCount,
            int bindingReadyCount,
            int bindingIssueCount,
            int bindingTokenIssuedCount,
            int tokenResolutionCount,
            int tokenResolvedCount,
            int tokenIssueCount,
            int journalEntryCount,
            int journaledEntryCount,
            int journalIssueCount,
            int replayPlanRequestCount,
            int acceptedReplayPlanCount,
            int rejectedReplayPlanCount,
            int scheduleBatchCount,
            int scheduleEntryCount,
            int scheduledEntryCount,
            int scheduleIssueCount,
            int schedulePlanRequestCount,
            int acceptedSchedulePlanCount,
            int rejectedSchedulePlanCount,
            int libraryExcludedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionBatchSnapshot> executionBatches,
            IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> executionDecisions,
            int executionPlannedCount,
            int executionIssueCount,
            int executionEligibleCount,
            int executionGateOpenCount,
            int executionGateBlockedCount,
            int methodInvocationCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot> executionPlans,
            int executionPlanRequestCount,
            int acceptedExecutionPlanCount,
            int rejectedExecutionPlanCount,
            string lastExecutionHookName,
            string lastExecutionStatus,
            string lastExecutionDetail,
            string lastExecutionReason)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleInvocationExecutor.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            ExecutionPlanningEnabled = executionPlanningEnabled;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            CandidateCount = Math.Max(0, candidateCount);
            DispatchTargetCount = Math.Max(0, dispatchTargetCount);
            DispatchIntentCount = Math.Max(0, dispatchIntentCount);
            QueuedDispatchIntentCount = Math.Max(0, queuedDispatchIntentCount);
            InvocationRequestCount = Math.Max(0, invocationRequestCount);
            BindingCount = Math.Max(0, bindingCount);
            BindingReadyCount = Math.Max(0, bindingReadyCount);
            BindingIssueCount = Math.Max(0, bindingIssueCount);
            BindingTokenIssuedCount = Math.Max(0, bindingTokenIssuedCount);
            TokenResolutionCount = Math.Max(0, tokenResolutionCount);
            TokenResolvedCount = Math.Max(0, tokenResolvedCount);
            TokenIssueCount = Math.Max(0, tokenIssueCount);
            JournalEntryCount = Math.Max(0, journalEntryCount);
            JournaledEntryCount = Math.Max(0, journaledEntryCount);
            JournalIssueCount = Math.Max(0, journalIssueCount);
            ReplayPlanRequestCount = Math.Max(0, replayPlanRequestCount);
            AcceptedReplayPlanCount = Math.Max(0, acceptedReplayPlanCount);
            RejectedReplayPlanCount = Math.Max(0, rejectedReplayPlanCount);
            ScheduleBatchCount = Math.Max(0, scheduleBatchCount);
            ScheduleEntryCount = Math.Max(0, scheduleEntryCount);
            ScheduledEntryCount = Math.Max(0, scheduledEntryCount);
            ScheduleIssueCount = Math.Max(0, scheduleIssueCount);
            SchedulePlanRequestCount = Math.Max(0, schedulePlanRequestCount);
            AcceptedSchedulePlanCount = Math.Max(0, acceptedSchedulePlanCount);
            RejectedSchedulePlanCount = Math.Max(0, rejectedSchedulePlanCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            ExecutionBatches = executionBatches ?? Array.Empty<OpenACAINativeLifecycleInvocationExecutionBatchSnapshot>();
            ExecutionBatchCount = ExecutionBatches.Count;
            ExecutionDecisions = executionDecisions ?? Array.Empty<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot>();
            ExecutionDecisionCount = ExecutionDecisions.Count;
            ExecutionPlannedCount = Math.Max(0, executionPlannedCount);
            ExecutionIssueCount = Math.Max(0, executionIssueCount);
            ExecutionEligibleCount = Math.Max(0, executionEligibleCount);
            ExecutionGateOpenCount = Math.Max(0, executionGateOpenCount);
            ExecutionGateBlockedCount = Math.Max(0, executionGateBlockedCount);
            MethodInvocationCount = Math.Max(0, methodInvocationCount);
            ExecutionPlans = executionPlans ?? Array.Empty<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot>();
            ExecutionPlanCount = ExecutionPlans.Count;
            ExecutionPlanRequestCount = Math.Max(0, executionPlanRequestCount);
            AcceptedExecutionPlanCount = Math.Max(0, acceptedExecutionPlanCount);
            RejectedExecutionPlanCount = Math.Max(0, rejectedExecutionPlanCount);
            LastExecutionHookName = lastExecutionHookName ?? "";
            LastExecutionStatus = lastExecutionStatus ?? "";
            LastExecutionDetail = lastExecutionDetail ?? "";
            LastExecutionReason = lastExecutionReason ?? "";
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool ExecutionPlanningEnabled { get; }
        public bool RuntimeInvocationEnabled { get; }
        public int CandidateCount { get; }
        public int DispatchTargetCount { get; }
        public int DispatchIntentCount { get; }
        public int QueuedDispatchIntentCount { get; }
        public int InvocationRequestCount { get; }
        public int BindingCount { get; }
        public int BindingReadyCount { get; }
        public int BindingIssueCount { get; }
        public int BindingTokenIssuedCount { get; }
        public int TokenResolutionCount { get; }
        public int TokenResolvedCount { get; }
        public int TokenIssueCount { get; }
        public int JournalEntryCount { get; }
        public int JournaledEntryCount { get; }
        public int JournalIssueCount { get; }
        public int ReplayPlanRequestCount { get; }
        public int AcceptedReplayPlanCount { get; }
        public int RejectedReplayPlanCount { get; }
        public int ScheduleBatchCount { get; }
        public int ScheduleEntryCount { get; }
        public int ScheduledEntryCount { get; }
        public int ScheduleIssueCount { get; }
        public int SchedulePlanRequestCount { get; }
        public int AcceptedSchedulePlanCount { get; }
        public int RejectedSchedulePlanCount { get; }
        public int LibraryExcludedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionBatchSnapshot> ExecutionBatches { get; }
        public int ExecutionBatchCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> ExecutionDecisions { get; }
        public int ExecutionDecisionCount { get; }
        public int ExecutionPlannedCount { get; }
        public int ExecutionIssueCount { get; }
        public int ExecutionEligibleCount { get; }
        public int ExecutionGateOpenCount { get; }
        public int ExecutionGateBlockedCount { get; }
        public int MethodInvocationCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot> ExecutionPlans { get; }
        public int ExecutionPlanCount { get; }
        public int ExecutionPlanRequestCount { get; }
        public int AcceptedExecutionPlanCount { get; }
        public int RejectedExecutionPlanCount { get; }
        public string LastExecutionHookName { get; }
        public string LastExecutionStatus { get; }
        public string LastExecutionDetail { get; }
        public string LastExecutionReason { get; }

        internal static OpenACAINativeLifecycleInvocationExecutorSnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleInvocationExecutorSnapshot(
                OpenACAINativeLifecycleInvocationExecutor.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle invocation executor has not refreshed yet.",
                true,
                OpenACAINativeLifecycleInvocationExecutor.RuntimeInvocationEnabled,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleInvocationExecutionBatchSnapshot>(),
                Array.Empty<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot>(),
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot>(),
                0,
                0,
                0,
                "",
                "INFO",
                "No native lifecycle invocation execution plan has been requested.",
                "none");
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationExecutionBatchSnapshot
    {
        internal OpenACAINativeLifecycleInvocationExecutionBatchSnapshot(
            int batchSequenceNumber,
            string hookName,
            int hookOrder,
            IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> decisions,
            int executionPlannedCount,
            int executionIssueCount,
            int executionEligibleCount,
            bool runtimeInvocationEnabled,
            int executionGateOpenCount,
            int executionGateBlockedCount,
            int methodInvocationCount,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            BatchSequenceNumber = Math.Max(0, batchSequenceNumber);
            HookName = hookName ?? "";
            HookOrder = Math.Max(0, hookOrder);
            Decisions = decisions ?? Array.Empty<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot>();
            DecisionCount = Decisions.Count;
            ExecutionPlannedCount = Math.Max(0, executionPlannedCount);
            ExecutionIssueCount = Math.Max(0, executionIssueCount);
            ExecutionEligibleCount = Math.Max(0, executionEligibleCount);
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            ExecutionGateOpenCount = Math.Max(0, executionGateOpenCount);
            ExecutionGateBlockedCount = Math.Max(0, executionGateBlockedCount);
            MethodInvocationCount = Math.Max(0, methodInvocationCount);
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public int BatchSequenceNumber { get; }
        public string HookName { get; }
        public int HookOrder { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> Decisions { get; }
        public int DecisionCount { get; }
        public int ExecutionPlannedCount { get; }
        public int ExecutionIssueCount { get; }
        public int ExecutionEligibleCount { get; }
        public bool RuntimeInvocationEnabled { get; }
        public int ExecutionGateOpenCount { get; }
        public int ExecutionGateBlockedCount { get; }
        public int MethodInvocationCount { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot
    {
        internal OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot(
            int executionSequenceNumber,
            int batchSequenceNumber,
            int hookSequenceNumber,
            int hookOrder,
            int scheduleSequenceNumber,
            int journalSequenceNumber,
            string modId,
            string displayName,
            string hookName,
            string invocationKind,
            string entrypointTypeName,
            string entrypointMethodName,
            string hookMethodName,
            string hookMethodSource,
            int hookMethodParameterCount,
            string bindingToken,
            bool journaled,
            bool scheduled,
            bool executionPlanned,
            bool executionEligible,
            bool runtimeInvocationEnabled,
            bool executionGateOpen,
            bool executionGateBlocked,
            bool methodInvoked,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            ExecutionSequenceNumber = Math.Max(0, executionSequenceNumber);
            BatchSequenceNumber = Math.Max(0, batchSequenceNumber);
            HookSequenceNumber = Math.Max(0, hookSequenceNumber);
            HookOrder = Math.Max(0, hookOrder);
            ScheduleSequenceNumber = Math.Max(0, scheduleSequenceNumber);
            JournalSequenceNumber = Math.Max(0, journalSequenceNumber);
            ModId = modId ?? "";
            DisplayName = displayName ?? "";
            HookName = hookName ?? "";
            InvocationKind = invocationKind ?? "";
            EntrypointTypeName = entrypointTypeName ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            HookMethodName = hookMethodName ?? "";
            HookMethodSource = hookMethodSource ?? "";
            HookMethodParameterCount = Math.Max(0, hookMethodParameterCount);
            BindingToken = bindingToken ?? "";
            Journaled = journaled;
            Scheduled = scheduled;
            ExecutionPlanned = executionPlanned;
            ExecutionEligible = executionEligible;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            ExecutionGateOpen = executionGateOpen;
            ExecutionGateBlocked = executionGateBlocked;
            MethodInvoked = methodInvoked;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public int ExecutionSequenceNumber { get; }
        public int BatchSequenceNumber { get; }
        public int HookSequenceNumber { get; }
        public int HookOrder { get; }
        public int ScheduleSequenceNumber { get; }
        public int JournalSequenceNumber { get; }
        public string ModId { get; }
        public string DisplayName { get; }
        public string HookName { get; }
        public string InvocationKind { get; }
        public string EntrypointTypeName { get; }
        public string EntrypointMethodName { get; }
        public string HookMethodName { get; }
        public string HookMethodSource { get; }
        public int HookMethodParameterCount { get; }
        public string BindingToken { get; }
        public bool Journaled { get; }
        public bool Scheduled { get; }
        public bool ExecutionPlanned { get; }
        public bool ExecutionEligible { get; }
        public bool RuntimeInvocationEnabled { get; }
        public bool ExecutionGateOpen { get; }
        public bool ExecutionGateBlocked { get; }
        public bool MethodInvoked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleInvocationExecutionPlanSnapshot
    {
        internal OpenACAINativeLifecycleInvocationExecutionPlanSnapshot(
            string hookName,
            int batchSequenceNumber,
            int hookOrder,
            int decisionCount,
            int executionPlannedCount,
            int executionIssueCount,
            int executionGateOpenCount,
            int executionGateBlockedCount,
            int methodInvocationCount,
            bool accepted,
            bool canExecuteNow,
            bool runtimeInvocationEnabled,
            string status,
            string reasonCode,
            string detail,
            OpenACAINativeLifecycleInvocationExecutionBatchSnapshot batch,
            string generatedUtc)
        {
            HookName = hookName ?? "";
            BatchSequenceNumber = Math.Max(0, batchSequenceNumber);
            HookOrder = Math.Max(0, hookOrder);
            DecisionCount = Math.Max(0, decisionCount);
            ExecutionPlannedCount = Math.Max(0, executionPlannedCount);
            ExecutionIssueCount = Math.Max(0, executionIssueCount);
            ExecutionGateOpenCount = Math.Max(0, executionGateOpenCount);
            ExecutionGateBlockedCount = Math.Max(0, executionGateBlockedCount);
            MethodInvocationCount = Math.Max(0, methodInvocationCount);
            Accepted = accepted;
            CanExecuteNow = canExecuteNow;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            Batch = batch;
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public string HookName { get; }
        public int BatchSequenceNumber { get; }
        public int HookOrder { get; }
        public int DecisionCount { get; }
        public int ExecutionPlannedCount { get; }
        public int ExecutionIssueCount { get; }
        public int ExecutionGateOpenCount { get; }
        public int ExecutionGateBlockedCount { get; }
        public int MethodInvocationCount { get; }
        public bool Accepted { get; }
        public bool CanExecuteNow { get; }
        public bool RuntimeInvocationEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public OpenACAINativeLifecycleInvocationExecutionBatchSnapshot Batch { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
