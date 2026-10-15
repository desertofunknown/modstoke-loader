// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle invocation scheduler. This consumes the
// replay journal and publishes deterministic per-hook batches without invoking
// mod code.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleInvocationScheduler
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationScheduler";
        public const int MaxScheduleEntryCount = OpenACAINativeLifecycleInvocationJournal.MaxJournalEntryCount;
        public const int MaxScheduleBatchCount = 128;
        public const int MaxSchedulePlanCount = 128;

        private static readonly bool RuntimeInvocationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoPlannedScheduleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot> RecordedSchedulePlans = new List<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot>();
        private static OpenACAINativeLifecycleInvocationSchedulerSnapshot _state = OpenACAINativeLifecycleInvocationSchedulerSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _schedulePlanRequestCount;
        private static int _acceptedSchedulePlanCount;
        private static int _rejectedSchedulePlanCount;
        private static string _lastScheduleHookName = "";
        private static string _lastScheduleStatus = "INFO";
        private static string _lastScheduleDetail = "No native lifecycle invocation schedule plan has been requested.";
        private static string _lastScheduleReason = "none";

        public static OpenACAINativeLifecycleInvocationSchedulerSnapshot State
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
        public static bool SchedulingEnabled => true;
        public static bool RuntimeInvocationEnabled => RuntimeInvocationEnabledValue;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleBatchSnapshot> ScheduleBatches => State.ScheduleBatches;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> ScheduleEntries => State.ScheduleEntries;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot> SchedulePlans => State.SchedulePlans;

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleBatchSnapshot> GetScheduleBatches()
        {
            return ScheduleBatches;
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> GetScheduleEntries()
        {
            return ScheduleEntries;
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot> GetSchedulePlans()
        {
            return SchedulePlans;
        }

        public static OpenACAINativeLifecycleInvocationSchedulePlanSnapshot PlanSchedule(string hookName)
        {
            var plan = CreateSchedulePlan(State, hookName);
            RecordSchedulePlan(plan);
            Refresh();
            return plan;
        }

        public static OpenACAINativeLifecycleInvocationSchedulerSnapshot Refresh()
        {
            return Refresh(OpenACAINativeLifecycleInvocationJournal.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleInvocationSchedulerSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle invocation scheduler provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleInvocationSchedulerSnapshot Refresh(OpenACAINativeLifecycleInvocationJournalSnapshot journal)
        {
            var snapshot = CreateSnapshot(journal);
            if (RecordAutomaticSchedulePlans(snapshot))
            {
                snapshot = CreateSnapshot(journal);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleInvocationSchedulerSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleInvocationSchedulerSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static OpenACAINativeLifecycleInvocationSchedulerSnapshot CreateSnapshot(OpenACAINativeLifecycleInvocationJournalSnapshot journal)
        {
            journal = journal ?? OpenACAINativeLifecycleInvocationJournalSnapshot.CreateUnknown();
            var sourceEntries = journal.JournalEntries
                .Where(entry => entry != null)
                .OrderBy(entry => HookOrder(entry.HookName))
                .ThenBy(entry => entry.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SequenceNumber)
                .Take(MaxScheduleEntryCount)
                .ToArray();

            var scheduleEntries = new List<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot>();
            var batchSequence = 0;
            foreach (var group in sourceEntries.GroupBy(entry => NormalizeHookName(entry.HookName), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => HookOrder(group.Key))
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxScheduleBatchCount))
            {
                batchSequence++;
                var hookSequence = 0;
                foreach (var entry in group
                    .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.SequenceNumber))
                {
                    hookSequence++;
                    scheduleEntries.Add(CreateScheduleEntry(scheduleEntries.Count + 1, batchSequence, hookSequence, entry));
                }
            }

            var batches = scheduleEntries
                .GroupBy(entry => NormalizeHookName(entry.HookName), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => HookOrder(group.Key))
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select((group, index) => CreateScheduleBatch(index + 1, group.Key, group.ToArray()))
                .Take(MaxScheduleBatchCount)
                .ToArray();

            var entries = scheduleEntries.Take(MaxScheduleEntryCount).ToArray();
            var scheduledEntryCount = entries.Count(entry => entry.Scheduled);
            var scheduleIssueCount = entries.Count(entry => entry.Issue);
            var scheduleEligibleCount = entries.Count(entry => entry.ScheduleEligible);
            var scheduleGateOpenCount = entries.Count(entry => entry.ScheduleGateOpen);
            var scheduleGateBlockedCount = entries.Count(entry => entry.ScheduleGateBlocked);
            var batchIssueCount = batches.Count(batch => batch.Issue);
            var status = scheduleIssueCount == 0 && batchIssueCount == 0 ? "READY" : "CHECK";
            var detail = scheduleIssueCount == 0 && batchIssueCount == 0
                ? "Modstoke native lifecycle invocation scheduler has produced deterministic replay batches; runtime invocation remains explicitly gated."
                : scheduleIssueCount + " native lifecycle invocation schedule issue(s) were found.";

            int schedulePlanRequestCount;
            int acceptedSchedulePlanCount;
            int rejectedSchedulePlanCount;
            string lastScheduleHookName;
            string lastScheduleStatus;
            string lastScheduleDetail;
            string lastScheduleReason;
            OpenACAINativeLifecycleInvocationSchedulePlanSnapshot[] schedulePlans;
            lock (SyncRoot)
            {
                schedulePlanRequestCount = _schedulePlanRequestCount;
                acceptedSchedulePlanCount = _acceptedSchedulePlanCount;
                rejectedSchedulePlanCount = _rejectedSchedulePlanCount;
                lastScheduleHookName = _lastScheduleHookName ?? "";
                lastScheduleStatus = _lastScheduleStatus ?? "";
                lastScheduleDetail = _lastScheduleDetail ?? "";
                lastScheduleReason = _lastScheduleReason ?? "";
                schedulePlans = RecordedSchedulePlans.ToArray();
            }

            return new OpenACAINativeLifecycleInvocationSchedulerSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                journal.Ready,
                status,
                detail,
                SchedulingEnabled,
                RuntimeInvocationEnabled,
                journal.CandidateCount,
                journal.DispatchTargetCount,
                journal.DispatchIntentCount,
                journal.QueuedDispatchIntentCount,
                journal.InvocationRequestCount,
                journal.BindingCount,
                journal.BindingReadyCount,
                journal.BindingIssueCount,
                journal.BindingTokenIssuedCount,
                journal.TokenResolutionCount,
                journal.TokenResolvedCount,
                journal.TokenIssueCount,
                journal.JournalEntryCount,
                journal.JournaledEntryCount,
                journal.JournalIssueCount,
                journal.ReplayPlanRequestCount,
                journal.AcceptedReplayPlanCount,
                journal.RejectedReplayPlanCount,
                journal.LibraryExcludedCount,
                batches,
                entries,
                scheduledEntryCount,
                scheduleIssueCount,
                scheduleEligibleCount,
                scheduleGateOpenCount,
                scheduleGateBlockedCount,
                schedulePlans,
                schedulePlanRequestCount,
                acceptedSchedulePlanCount,
                rejectedSchedulePlanCount,
                lastScheduleHookName,
                lastScheduleStatus,
                lastScheduleDetail,
                lastScheduleReason);
        }

        private static OpenACAINativeLifecycleInvocationScheduleEntrySnapshot CreateScheduleEntry(
            int scheduleSequenceNumber,
            int batchSequenceNumber,
            int hookSequenceNumber,
            OpenACAINativeLifecycleInvocationJournalEntrySnapshot entry)
        {
            entry = entry ?? new OpenACAINativeLifecycleInvocationJournalEntrySnapshot(
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
                false,
                false,
                RuntimeInvocationEnabled,
                false,
                false,
                "CHECK",
                "missing-journal-entry",
                "OpenACAI could not schedule a native lifecycle invocation because journal metadata was missing.",
                DateTimeOffset.UtcNow.ToString("O"));

            var scheduled = entry.Journaled && !entry.Issue;
            var scheduleGateOpen = scheduled && RuntimeInvocationEnabled;
            var scheduleGateBlocked = scheduled && !RuntimeInvocationEnabled;
            var scheduleEligible = scheduled && RuntimeInvocationEnabled;
            var status = scheduled ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = scheduled
                ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled"
                : !string.IsNullOrWhiteSpace(entry.IssueCode) ? entry.IssueCode : "journal-entry-not-ready";
            var detail = scheduled
                ? RuntimeInvocationEnabled
                    ? "OpenACAI scheduled this native lifecycle invocation for runtime replay."
                    : "OpenACAI scheduled this native lifecycle invocation as metadata-only evidence; runtime invocation remains disabled."
                : "OpenACAI could not schedule this native lifecycle invocation because the journal entry is not ready.";

            return new OpenACAINativeLifecycleInvocationScheduleEntrySnapshot(
                scheduleSequenceNumber,
                batchSequenceNumber,
                hookSequenceNumber,
                HookOrder(entry.HookName),
                entry.SequenceNumber,
                entry.ModId,
                entry.DisplayName,
                entry.HookName,
                entry.InvocationKind,
                entry.EntrypointTypeName,
                entry.EntrypointMethodName,
                entry.HookMethodName,
                entry.HookMethodSource,
                entry.HookMethodParameterCount,
                entry.BindingToken,
                entry.Journaled,
                scheduled,
                scheduleEligible,
                RuntimeInvocationEnabled,
                scheduleGateOpen,
                scheduleGateBlocked,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static OpenACAINativeLifecycleInvocationScheduleBatchSnapshot CreateScheduleBatch(
            int batchSequenceNumber,
            string hookName,
            IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> entries)
        {
            entries = entries ?? Array.Empty<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot>();
            var scheduledEntryCount = entries.Count(entry => entry != null && entry.Scheduled);
            var issueCount = entries.Count(entry => entry != null && entry.Issue);
            var scheduleGateOpenCount = entries.Count(entry => entry != null && entry.ScheduleGateOpen);
            var scheduleGateBlockedCount = entries.Count(entry => entry != null && entry.ScheduleGateBlocked);
            var scheduleEligibleCount = entries.Count(entry => entry != null && entry.ScheduleEligible);
            var status = issueCount == 0 ? scheduledEntryCount > 0 ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "INFO" : "CHECK";
            var issueCode = issueCount == 0
                ? scheduledEntryCount > 0 ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled" : "empty-batch"
                : "schedule-entry-issues";
            var detail = issueCount == 0
                ? scheduledEntryCount > 0
                    ? RuntimeInvocationEnabled
                        ? "OpenACAI scheduled this hook batch for runtime replay."
                        : "OpenACAI scheduled this hook batch as metadata-only evidence; runtime invocation remains disabled."
                    : "OpenACAI found no scheduled entries for this hook batch."
                : issueCount + " schedule entry issue(s) were found in this hook batch.";

            return new OpenACAINativeLifecycleInvocationScheduleBatchSnapshot(
                batchSequenceNumber,
                NormalizeHookName(hookName),
                HookOrder(hookName),
                entries,
                scheduledEntryCount,
                issueCount,
                scheduleEligibleCount,
                RuntimeInvocationEnabled,
                scheduleGateOpenCount,
                scheduleGateBlockedCount,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static bool RecordAutomaticSchedulePlans(OpenACAINativeLifecycleInvocationSchedulerSnapshot snapshot)
        {
            if (snapshot == null || snapshot.ScheduledEntryCount == 0)
            {
                lock (SyncRoot)
                {
                    return AutoPlannedScheduleKeys.RemoveWhere(key => !string.IsNullOrWhiteSpace(key)) > 0;
                }
            }

            var currentHooks = snapshot.ScheduleBatches
                .Where(batch => batch != null && batch.ScheduledEntryCount > 0 && !string.IsNullOrWhiteSpace(batch.HookName))
                .Select(batch => NormalizeHookName(batch.HookName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentKeySet = new HashSet<string>(currentHooks.Select(hook => "hook:" + hook), StringComparer.OrdinalIgnoreCase);
            var hooksToPlan = new List<string>();

            lock (SyncRoot)
            {
                AutoPlannedScheduleKeys.RemoveWhere(key => !currentKeySet.Contains(key));
                foreach (var hookName in currentHooks)
                {
                    var key = "hook:" + hookName;
                    if (AutoPlannedScheduleKeys.Add(key))
                    {
                        hooksToPlan.Add(hookName);
                    }
                }
            }

            foreach (var hookName in hooksToPlan)
            {
                RecordSchedulePlan(CreateSchedulePlan(snapshot, hookName));
            }

            return hooksToPlan.Count > 0;
        }

        private static OpenACAINativeLifecycleInvocationSchedulePlanSnapshot CreateSchedulePlan(
            OpenACAINativeLifecycleInvocationSchedulerSnapshot snapshot,
            string hookName)
        {
            snapshot = snapshot ?? OpenACAINativeLifecycleInvocationSchedulerSnapshot.CreateUnknown();
            var normalizedHookName = NormalizeHookName(hookName);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Native lifecycle invocation schedule plan is invalid.";
            var accepted = false;
            var canScheduleNow = false;
            OpenACAINativeLifecycleInvocationScheduleBatchSnapshot batch = null;

            if (string.IsNullOrWhiteSpace(normalizedHookName))
            {
                reason = "missing-hook-name";
                detail = "A native lifecycle hook name is required for schedule planning.";
            }
            else
            {
                batch = snapshot.ScheduleBatches.FirstOrDefault(candidate =>
                    candidate != null
                    && string.Equals(candidate.HookName, normalizedHookName, StringComparison.OrdinalIgnoreCase));
                if (batch == null || batch.EntryCount == 0)
                {
                    reason = "hook-batch-not-scheduled";
                    detail = "No native lifecycle invocation schedule batch matched hook '" + normalizedHookName + "'.";
                }
                else if (batch.Issue)
                {
                    reason = "schedule-batch-issues";
                    detail = batch.ScheduleIssueCount + " native lifecycle invocation schedule issue(s) were found for hook '" + normalizedHookName + "'.";
                }
                else if (batch.ScheduledEntryCount == 0)
                {
                    reason = "hook-batch-empty";
                    detail = "The native lifecycle invocation schedule batch for hook '" + normalizedHookName + "' has no scheduled entries.";
                }
                else
                {
                    accepted = true;
                    if (!RuntimeInvocationEnabled)
                    {
                        status = "DEFERRED";
                        reason = "runtime-invocation-disabled";
                        detail = "OpenACAI accepted the native lifecycle invocation schedule plan; runtime invocation remains disabled.";
                    }
                    else
                    {
                        canScheduleNow = true;
                        status = "READY";
                        reason = "ready";
                        detail = "Modstoke native lifecycle invocation schedule plan is ready for runtime replay.";
                    }
                }
            }

            return new OpenACAINativeLifecycleInvocationSchedulePlanSnapshot(
                normalizedHookName,
                batch == null ? 0 : batch.BatchSequenceNumber,
                batch == null ? HookOrder(normalizedHookName) : batch.HookOrder,
                batch == null ? 0 : batch.EntryCount,
                batch == null ? 0 : batch.ScheduledEntryCount,
                batch == null ? 0 : batch.ScheduleIssueCount,
                batch == null ? 0 : batch.ScheduleGateOpenCount,
                batch == null ? 0 : batch.ScheduleGateBlockedCount,
                accepted,
                canScheduleNow,
                RuntimeInvocationEnabled,
                status,
                reason,
                detail,
                batch,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static void RecordSchedulePlan(OpenACAINativeLifecycleInvocationSchedulePlanSnapshot plan)
        {
            lock (SyncRoot)
            {
                _schedulePlanRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedSchedulePlanCount++;
                }
                else
                {
                    _rejectedSchedulePlanCount++;
                }

                _lastScheduleHookName = plan == null ? "" : plan.HookName;
                _lastScheduleStatus = plan == null ? "CHECK" : plan.Status;
                _lastScheduleDetail = plan == null ? "Native lifecycle invocation schedule plan was null." : plan.Detail;
                _lastScheduleReason = plan == null ? "null-plan" : plan.ReasonCode;
                if (plan != null)
                {
                    RecordedSchedulePlans.Add(plan);
                    while (RecordedSchedulePlans.Count > MaxSchedulePlanCount)
                    {
                        RecordedSchedulePlans.RemoveAt(0);
                    }
                }
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Scheduler",
                "OpenACAI-owned native lifecycle invocation ordering and scheduling.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateBatchSection(snapshot),
                    CreatePlanSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleInvocationSchedulerSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Invocation Scheduler",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle invocation scheduler snapshot."),
                    Row("Scheduling", snapshot.SchedulingEnabled ? "Enabled" : "Disabled", snapshot.SchedulingEnabled ? "READY" : "CHECK", "Journal entries are grouped into deterministic hook batches without invoking mod code."),
                    Row("Runtime Invocation", snapshot.RuntimeInvocationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", "Native lifecycle method invocation remains disabled until verified in game."),
                    Row("Batches", snapshot.ScheduleBatchCount.ToString(), snapshot.ScheduleIssueCount == 0 ? "READY" : "CHECK", snapshot.ScheduledEntryCount + " scheduled entry or entries; " + snapshot.ScheduleIssueCount + " issue(s)."),
                    Row("Schedule Gate", snapshot.ScheduleGateOpenCount + " open", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", snapshot.ScheduleGateBlockedCount + " scheduled invocation(s) blocked by the disabled runtime invocation gate."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle invocation scheduling.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateBatchSection(OpenACAINativeLifecycleInvocationSchedulerSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Batches", snapshot.ScheduleBatchCount.ToString(), snapshot.ScheduleIssueCount == 0 ? "READY" : "CHECK", "Deterministic per-hook replay batches.")
            };

            foreach (var batch in snapshot.ScheduleBatches.Take(10))
            {
                rows.Add(Row(
                    "#" + batch.BatchSequenceNumber + " " + (string.IsNullOrWhiteSpace(batch.HookName) ? "hook" : batch.HookName),
                    batch.ScheduledEntryCount + "/" + batch.EntryCount,
                    batch.Status,
                    batch.Detail));
            }

            if (snapshot.ScheduleBatchCount == 0)
            {
                rows.Add(Row("No Batches", "0", "INFO", "No Modstoke-native lifecycle invocation batches were scheduled."));
            }

            return new OpenACAIMenuSectionSnapshot("Replay Batches", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreatePlanSection(OpenACAINativeLifecycleInvocationSchedulerSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Schedule Plans", snapshot.SchedulePlanRequestCount + " requested", "INFO", snapshot.AcceptedSchedulePlanCount + " accepted, " + snapshot.RejectedSchedulePlanCount + " rejected."),
                Row("Last Hook", string.IsNullOrWhiteSpace(snapshot.LastScheduleHookName) ? "none" : snapshot.LastScheduleHookName, "INFO", "Last native lifecycle invocation schedule hook."),
                Row("Last Schedule", snapshot.LastScheduleStatus, snapshot.LastScheduleStatus, snapshot.LastScheduleDetail),
                Row("Last Reason", snapshot.LastScheduleReason, snapshot.LastScheduleStatus, "Last native lifecycle invocation schedule reason code.")
            };

            foreach (var plan in snapshot.SchedulePlans.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(plan.HookName) ? "Schedule" : plan.HookName,
                    plan.ScheduledEntryCount + "/" + plan.EntryCount,
                    plan.Status,
                    plan.Detail));
            }

            return new OpenACAIMenuSectionSnapshot("Schedule Plans", rows);
        }

        private static int HookOrder(string hookName)
        {
            var normalized = NormalizeHookName(hookName).ToLowerInvariant();
            switch (normalized)
            {
                case "initialize":
                case "init":
                    return 100;
                case "awake":
                    return 120;
                case "load":
                    return 150;
                case "start":
                    return 200;
                case "enable":
                case "onenable":
                    return 250;
                case "fixedupdate":
                    return 450;
                case "update":
                    return 500;
                case "lateupdate":
                    return 600;
                case "disable":
                case "ondisable":
                    return 900;
                case "shutdown":
                    return 1000;
                case "dispose":
                    return 1100;
                default:
                    return string.IsNullOrWhiteSpace(normalized) ? 0 : 5000;
            }
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
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleInvocationScheduler;
            public string DisplayName => "Modstoke Native Scheduler";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 713;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle invocation ordering and scheduling diagnostics.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleInvocationScheduler.CreateContent();
            }
        }
    }
}
