// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenACAI.Loader
{
    public sealed class OpenACAINativeLifecycleInvocationSchedulerSnapshot
    {
        internal OpenACAINativeLifecycleInvocationSchedulerSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool schedulingEnabled,
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
            int libraryExcludedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleBatchSnapshot> scheduleBatches,
            IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> scheduleEntries,
            int scheduledEntryCount,
            int scheduleIssueCount,
            int scheduleEligibleCount,
            int scheduleGateOpenCount,
            int scheduleGateBlockedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot> schedulePlans,
            int schedulePlanRequestCount,
            int acceptedSchedulePlanCount,
            int rejectedSchedulePlanCount,
            string lastScheduleHookName,
            string lastScheduleStatus,
            string lastScheduleDetail,
            string lastScheduleReason)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleInvocationScheduler.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            SchedulingEnabled = schedulingEnabled;
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
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            ScheduleBatches = scheduleBatches ?? Array.Empty<OpenACAINativeLifecycleInvocationScheduleBatchSnapshot>();
            ScheduleBatchCount = ScheduleBatches.Count;
            ScheduleEntries = scheduleEntries ?? Array.Empty<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot>();
            ScheduleEntryCount = ScheduleEntries.Count;
            ScheduledEntryCount = Math.Max(0, scheduledEntryCount);
            ScheduleIssueCount = Math.Max(0, scheduleIssueCount);
            ScheduleEligibleCount = Math.Max(0, scheduleEligibleCount);
            ScheduleGateOpenCount = Math.Max(0, scheduleGateOpenCount);
            ScheduleGateBlockedCount = Math.Max(0, scheduleGateBlockedCount);
            SchedulePlans = schedulePlans ?? Array.Empty<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot>();
            SchedulePlanCount = SchedulePlans.Count;
            SchedulePlanRequestCount = Math.Max(0, schedulePlanRequestCount);
            AcceptedSchedulePlanCount = Math.Max(0, acceptedSchedulePlanCount);
            RejectedSchedulePlanCount = Math.Max(0, rejectedSchedulePlanCount);
            LastScheduleHookName = lastScheduleHookName ?? "";
            LastScheduleStatus = lastScheduleStatus ?? "";
            LastScheduleDetail = lastScheduleDetail ?? "";
            LastScheduleReason = lastScheduleReason ?? "";
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool SchedulingEnabled { get; }
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
        public int LibraryExcludedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleBatchSnapshot> ScheduleBatches { get; }
        public int ScheduleBatchCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> ScheduleEntries { get; }
        public int ScheduleEntryCount { get; }
        public int ScheduledEntryCount { get; }
        public int ScheduleIssueCount { get; }
        public int ScheduleEligibleCount { get; }
        public int ScheduleGateOpenCount { get; }
        public int ScheduleGateBlockedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot> SchedulePlans { get; }
        public int SchedulePlanCount { get; }
        public int SchedulePlanRequestCount { get; }
        public int AcceptedSchedulePlanCount { get; }
        public int RejectedSchedulePlanCount { get; }
        public string LastScheduleHookName { get; }
        public string LastScheduleStatus { get; }
        public string LastScheduleDetail { get; }
        public string LastScheduleReason { get; }

        internal static OpenACAINativeLifecycleInvocationSchedulerSnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleInvocationSchedulerSnapshot(
                OpenACAINativeLifecycleInvocationScheduler.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle invocation scheduler has not refreshed yet.",
                true,
                OpenACAINativeLifecycleInvocationScheduler.RuntimeInvocationEnabled,
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
                Array.Empty<OpenACAINativeLifecycleInvocationScheduleBatchSnapshot>(),
                Array.Empty<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot>(),
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot>(),
                0,
                0,
                0,
                "",
                "INFO",
                "No native lifecycle invocation schedule plan has been requested.",
                "none");
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationScheduleBatchSnapshot
    {
        internal OpenACAINativeLifecycleInvocationScheduleBatchSnapshot(
            int batchSequenceNumber,
            string hookName,
            int hookOrder,
            IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> entries,
            int scheduledEntryCount,
            int scheduleIssueCount,
            int scheduleEligibleCount,
            bool runtimeInvocationEnabled,
            int scheduleGateOpenCount,
            int scheduleGateBlockedCount,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            BatchSequenceNumber = Math.Max(0, batchSequenceNumber);
            HookName = hookName ?? "";
            HookOrder = Math.Max(0, hookOrder);
            Entries = entries ?? Array.Empty<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot>();
            EntryCount = Entries.Count;
            ScheduledEntryCount = Math.Max(0, scheduledEntryCount);
            ScheduleIssueCount = Math.Max(0, scheduleIssueCount);
            ScheduleEligibleCount = Math.Max(0, scheduleEligibleCount);
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            ScheduleGateOpenCount = Math.Max(0, scheduleGateOpenCount);
            ScheduleGateBlockedCount = Math.Max(0, scheduleGateBlockedCount);
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public int BatchSequenceNumber { get; }
        public string HookName { get; }
        public int HookOrder { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> Entries { get; }
        public int EntryCount { get; }
        public int ScheduledEntryCount { get; }
        public int ScheduleIssueCount { get; }
        public int ScheduleEligibleCount { get; }
        public bool RuntimeInvocationEnabled { get; }
        public int ScheduleGateOpenCount { get; }
        public int ScheduleGateBlockedCount { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleInvocationScheduleEntrySnapshot
    {
        internal OpenACAINativeLifecycleInvocationScheduleEntrySnapshot(
            int scheduleSequenceNumber,
            int batchSequenceNumber,
            int hookSequenceNumber,
            int hookOrder,
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
            bool scheduleEligible,
            bool runtimeInvocationEnabled,
            bool scheduleGateOpen,
            bool scheduleGateBlocked,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            ScheduleSequenceNumber = Math.Max(0, scheduleSequenceNumber);
            BatchSequenceNumber = Math.Max(0, batchSequenceNumber);
            HookSequenceNumber = Math.Max(0, hookSequenceNumber);
            HookOrder = Math.Max(0, hookOrder);
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
            ScheduleEligible = scheduleEligible;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            ScheduleGateOpen = scheduleGateOpen;
            ScheduleGateBlocked = scheduleGateBlocked;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public int ScheduleSequenceNumber { get; }
        public int BatchSequenceNumber { get; }
        public int HookSequenceNumber { get; }
        public int HookOrder { get; }
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
        public bool ScheduleEligible { get; }
        public bool RuntimeInvocationEnabled { get; }
        public bool ScheduleGateOpen { get; }
        public bool ScheduleGateBlocked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleInvocationSchedulePlanSnapshot
    {
        internal OpenACAINativeLifecycleInvocationSchedulePlanSnapshot(
            string hookName,
            int batchSequenceNumber,
            int hookOrder,
            int entryCount,
            int scheduledEntryCount,
            int scheduleIssueCount,
            int scheduleGateOpenCount,
            int scheduleGateBlockedCount,
            bool accepted,
            bool canScheduleNow,
            bool runtimeInvocationEnabled,
            string status,
            string reasonCode,
            string detail,
            OpenACAINativeLifecycleInvocationScheduleBatchSnapshot batch,
            string generatedUtc)
        {
            HookName = hookName ?? "";
            BatchSequenceNumber = Math.Max(0, batchSequenceNumber);
            HookOrder = Math.Max(0, hookOrder);
            EntryCount = Math.Max(0, entryCount);
            ScheduledEntryCount = Math.Max(0, scheduledEntryCount);
            ScheduleIssueCount = Math.Max(0, scheduleIssueCount);
            ScheduleGateOpenCount = Math.Max(0, scheduleGateOpenCount);
            ScheduleGateBlockedCount = Math.Max(0, scheduleGateBlockedCount);
            Accepted = accepted;
            CanScheduleNow = canScheduleNow;
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
        public int EntryCount { get; }
        public int ScheduledEntryCount { get; }
        public int ScheduleIssueCount { get; }
        public int ScheduleGateOpenCount { get; }
        public int ScheduleGateBlockedCount { get; }
        public bool Accepted { get; }
        public bool CanScheduleNow { get; }
        public bool RuntimeInvocationEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public OpenACAINativeLifecycleInvocationScheduleBatchSnapshot Batch { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
