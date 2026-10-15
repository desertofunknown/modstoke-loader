// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle dispatcher gate. This consumes validated
// native lifecycle registration metadata and publishes dispatch plans without
// invoking Modstoke-native mod code.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleDispatcher
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleDispatcher";
        public const int MaxDispatchTargetCount = 512;
        public const int MaxDispatchIntentCount = 512;
        public const int MaxHookNameLength = OpenACAINativeModHost.MaxLifecycleHookLength;

        private static readonly bool RuntimeDispatchEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoPlannedDispatchKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static OpenACAINativeLifecycleDispatcherSnapshot _state = OpenACAINativeLifecycleDispatcherSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _dispatchPlanRequestCount;
        private static int _acceptedDispatchPlanCount;
        private static int _rejectedDispatchPlanCount;
        private static string _lastDispatchHookName = "";
        private static string _lastDispatchStatus = "INFO";
        private static string _lastDispatchDetail = "No native lifecycle dispatch plan has been requested.";
        private static string _lastDispatchReason = "none";

        public static OpenACAINativeLifecycleDispatcherSnapshot State
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
        public static bool PlanningEnabled => true;
        public static bool RuntimeDispatchEnabled => RuntimeDispatchEnabledValue;
        public static IReadOnlyList<OpenACAINativeLifecycleDispatchTargetSnapshot> DispatchTargets => State.DispatchTargets;
        public static IReadOnlyList<OpenACAINativeLifecycleDispatchIntentSnapshot> DispatchIntentions => State.DispatchIntentions;

        public static IReadOnlyList<OpenACAINativeLifecycleDispatchTargetSnapshot> GetDispatchTargets()
        {
            return DispatchTargets;
        }

        public static IReadOnlyList<OpenACAINativeLifecycleDispatchIntentSnapshot> GetDispatchIntentions()
        {
            return DispatchIntentions;
        }

        public static OpenACAINativeLifecycleDispatchPlan PlanDispatch(string hookName)
        {
            var snapshot = State;
            var plan = CreateDispatchPlan(snapshot, hookName);
            RecordDispatchPlan(plan);
            Refresh();
            return plan;
        }

        private static OpenACAINativeLifecycleDispatchPlan CreateDispatchPlan(OpenACAINativeLifecycleDispatcherSnapshot snapshot, string hookName)
        {
            snapshot = snapshot ?? OpenACAINativeLifecycleDispatcherSnapshot.CreateUnknown();
            var normalizedHookName = NormalizeHookName(hookName);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Native lifecycle dispatch plan is invalid.";
            var accepted = false;
            var canDispatchNow = false;
            IReadOnlyList<OpenACAINativeLifecycleDispatchTargetSnapshot> targets = Array.Empty<OpenACAINativeLifecycleDispatchTargetSnapshot>();

            if (string.IsNullOrWhiteSpace(normalizedHookName))
            {
                reason = "missing-hook-name";
                detail = "A native lifecycle hook name is required.";
            }
            else
            {
                targets = snapshot.DispatchTargets
                    .Where(target => target != null && string.Equals(target.HookName, normalizedHookName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (targets.Count == 0)
                {
                    reason = "no-dispatch-targets";
                    detail = "No native lifecycle dispatch targets matched hook '" + normalizedHookName + "'.";
                }
                else if (targets.Any(target => target.Issue))
                {
                    reason = "dispatch-target-issues";
                    detail = targets.Count(target => target.Issue) + " native lifecycle dispatch target issue(s) were found for hook '" + normalizedHookName + "'.";
                }
                else if (targets.Any(target => !target.MetadataReady))
                {
                    reason = "dispatch-target-not-ready";
                    detail = "One or more native lifecycle dispatch targets are not metadata-ready for hook '" + normalizedHookName + "'.";
                }
                else
                {
                    accepted = true;
                    if (!RuntimeDispatchEnabled)
                    {
                        status = "DEFERRED";
                        reason = "runtime-dispatch-disabled";
                        detail = "OpenACAI accepted the native lifecycle dispatch plan; runtime dispatch remains disabled.";
                    }
                    else
                    {
                        canDispatchNow = true;
                        status = "READY";
                        reason = "ready";
                        detail = "Modstoke native lifecycle dispatch plan is ready for invocation.";
                    }
                }
            }

            var plan = new OpenACAINativeLifecycleDispatchPlan(
                normalizedHookName,
                targets.Count,
                targets.Count(target => target.MetadataReady),
                targets.Count(target => target.Issue),
                targets.Count(target => target.DispatchGateOpen),
                targets.Count(target => target.DispatchGateBlocked),
                accepted,
                canDispatchNow,
                RuntimeDispatchEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
            return plan;
        }

        public static OpenACAINativeLifecycleDispatcherSnapshot Refresh()
        {
            return Refresh(OpenACAINativeLifecycleRegistry.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleDispatcherSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle dispatcher provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleDispatcherSnapshot Refresh(OpenACAINativeLifecycleRegistrySnapshot registry)
        {
            var snapshot = CreateSnapshot(registry);
            if (RecordAutomaticDispatchPlans(snapshot))
            {
                snapshot = CreateSnapshot(registry);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleDispatcherSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleDispatcherSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordDispatchPlan(OpenACAINativeLifecycleDispatchPlan plan)
        {
            lock (SyncRoot)
            {
                _dispatchPlanRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedDispatchPlanCount++;
                }
                else
                {
                    _rejectedDispatchPlanCount++;
                }

                _lastDispatchHookName = plan == null ? "" : plan.HookName;
                _lastDispatchStatus = plan == null ? "CHECK" : plan.Status;
                _lastDispatchDetail = plan == null ? "Native lifecycle dispatch plan was null." : plan.Detail;
                _lastDispatchReason = plan == null ? "null-plan" : plan.ReasonCode;
            }
        }

        private static bool RecordAutomaticDispatchPlans(OpenACAINativeLifecycleDispatcherSnapshot snapshot)
        {
            if (snapshot == null || snapshot.DispatchIntentCount == 0)
            {
                lock (SyncRoot)
                {
                    return AutoPlannedDispatchKeys.RemoveWhere(key => !string.IsNullOrWhiteSpace(key)) > 0;
                }
            }

            var currentKeys = snapshot.DispatchIntentions
                .Where(intention => intention != null && intention.MetadataReady && !string.IsNullOrWhiteSpace(intention.HookName))
                .Select(intention => "hook:" + NormalizeHookName(intention.HookName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentKeySet = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);
            var hooksToPlan = new List<string>();

            lock (SyncRoot)
            {
                AutoPlannedDispatchKeys.RemoveWhere(key => !currentKeySet.Contains(key));
                foreach (var key in currentKeys)
                {
                    if (AutoPlannedDispatchKeys.Add(key))
                    {
                        hooksToPlan.Add(key.Substring("hook:".Length));
                    }
                }
            }

            foreach (var hookName in hooksToPlan)
            {
                RecordDispatchPlan(CreateDispatchPlan(snapshot, hookName));
            }

            return hooksToPlan.Count > 0;
        }

        private static OpenACAINativeLifecycleDispatcherSnapshot CreateSnapshot(OpenACAINativeLifecycleRegistrySnapshot registry)
        {
            registry = registry ?? OpenACAINativeLifecycleRegistrySnapshot.CreateUnknown();
            var targets = registry.PlannedRegistrations
                .Where(registration => registration != null)
                .OrderBy(registration => registration.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(registration => registration.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(registration => registration.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(CreateTarget)
                .Take(MaxDispatchTargetCount)
                .ToArray();
            var intentions = targets
                .Select(CreateIntention)
                .Take(MaxDispatchIntentCount)
                .ToArray();

            int requestCount;
            int acceptedCount;
            int rejectedCount;
            string lastHookName;
            string lastStatus;
            string lastDetail;
            string lastReason;
            lock (SyncRoot)
            {
                requestCount = _dispatchPlanRequestCount;
                acceptedCount = _acceptedDispatchPlanCount;
                rejectedCount = _rejectedDispatchPlanCount;
                lastHookName = _lastDispatchHookName ?? "";
                lastStatus = _lastDispatchStatus ?? "";
                lastDetail = _lastDispatchDetail ?? "";
                lastReason = _lastDispatchReason ?? "";
            }

            var dispatchMetadataReadyCount = targets.Count(target => target.MetadataReady);
            var dispatchIssueCount = targets.Count(target => target.Issue);
            var dispatchGateOpenCount = targets.Count(target => target.DispatchGateOpen);
            var dispatchGateBlockedCount = targets.Count(target => target.DispatchGateBlocked);
            var dispatchIntentMetadataReadyCount = intentions.Count(intention => intention.MetadataReady);
            var queuedDispatchIntentCount = intentions.Count(intention => intention.Queued);
            var dispatchIntentIssueCount = intentions.Count(intention => intention.Issue);
            var dispatchIntentGateOpenCount = intentions.Count(intention => intention.DispatchGateOpen);
            var dispatchIntentGateBlockedCount = intentions.Count(intention => intention.DispatchGateBlocked);
            var status = dispatchIssueCount == 0 ? "READY" : "CHECK";
            var detail = dispatchIssueCount == 0
                ? "Modstoke native lifecycle dispatcher has accepted metadata-ready targets; runtime dispatch remains explicitly gated."
                : dispatchIssueCount + " native lifecycle dispatch target issue(s) were found.";

            return new OpenACAINativeLifecycleDispatcherSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                registry.Ready,
                status,
                detail,
                PlanningEnabled,
                RuntimeDispatchEnabled,
                registry.CandidateCount,
                registry.RegistrationCount,
                registry.PlannedHookCount,
                registry.RegistrationIssueCount,
                registry.HookMethodReadyCount,
                registry.HookMethodIssueCount,
                registry.DryRunReadyCount,
                registry.DryRunIssueCount,
                registry.LibraryExcludedCount,
                targets,
                dispatchMetadataReadyCount,
                dispatchIssueCount,
                dispatchGateOpenCount,
                dispatchGateBlockedCount,
                intentions,
                dispatchIntentMetadataReadyCount,
                queuedDispatchIntentCount,
                dispatchIntentIssueCount,
                dispatchIntentGateOpenCount,
                dispatchIntentGateBlockedCount,
                requestCount,
                acceptedCount,
                rejectedCount,
                lastHookName,
                lastStatus,
                lastDetail,
                lastReason);
        }

        private static OpenACAINativeLifecycleDispatchTargetSnapshot CreateTarget(OpenACAINativeLifecycleRegistrationSnapshot registration)
        {
            var metadataReady = registration.Planned
                && registration.HookMethodReady
                && registration.DryRunReady
                && !registration.Issue;
            var dispatchGateOpen = metadataReady && RuntimeDispatchEnabled;
            var dispatchGateBlocked = metadataReady && !RuntimeDispatchEnabled;
            var status = metadataReady ? RuntimeDispatchEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = metadataReady
                ? RuntimeDispatchEnabled ? "" : "runtime-dispatch-disabled"
                : !string.IsNullOrWhiteSpace(registration.IssueCode) ? registration.IssueCode
                    : !string.IsNullOrWhiteSpace(registration.HookMethodIssueCode) ? registration.HookMethodIssueCode
                        : !string.IsNullOrWhiteSpace(registration.DryRunIssueCode) ? registration.DryRunIssueCode
                            : "registration-not-ready";
            var detail = metadataReady
                ? RuntimeDispatchEnabled
                    ? "Modstoke native lifecycle dispatch target is ready for invocation."
                    : "OpenACAI accepted this lifecycle dispatch target; runtime dispatch remains disabled."
                : "OpenACAI rejected this lifecycle dispatch target because registration metadata is not ready.";

            return new OpenACAINativeLifecycleDispatchTargetSnapshot(
                registration.ModId,
                registration.DisplayName,
                registration.HookName,
                registration.InvocationKind,
                registration.EntrypointTypeName,
                registration.EntrypointMethodName,
                registration.HookMethodName,
                registration.HookMethodSource,
                registration.HookMethodParameterCount,
                metadataReady,
                RuntimeDispatchEnabled,
                dispatchGateOpen,
                dispatchGateBlocked,
                status,
                issueCode,
                detail);
        }

        private static OpenACAINativeLifecycleDispatchIntentSnapshot CreateIntention(OpenACAINativeLifecycleDispatchTargetSnapshot target)
        {
            target = target ?? new OpenACAINativeLifecycleDispatchTargetSnapshot(
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                0,
                false,
                RuntimeDispatchEnabled,
                false,
                false,
                "CHECK",
                "missing-dispatch-target",
                "OpenACAI could not create a lifecycle dispatch intention because the target was missing.");
            var metadataReady = target.MetadataReady && !target.Issue;
            var dispatchGateOpen = metadataReady && RuntimeDispatchEnabled;
            var dispatchGateBlocked = metadataReady && !RuntimeDispatchEnabled;
            var queued = dispatchGateBlocked;
            var status = metadataReady ? RuntimeDispatchEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = metadataReady
                ? RuntimeDispatchEnabled ? "" : "runtime-dispatch-disabled"
                : !string.IsNullOrWhiteSpace(target.IssueCode) ? target.IssueCode : "dispatch-target-not-ready";
            var detail = metadataReady
                ? RuntimeDispatchEnabled
                    ? "Modstoke native lifecycle dispatch intention is ready for runtime dispatch."
                    : "OpenACAI queued this lifecycle dispatch intention as metadata-only evidence; runtime dispatch remains disabled."
                : "OpenACAI rejected this lifecycle dispatch intention because dispatch target metadata is not ready.";

            return new OpenACAINativeLifecycleDispatchIntentSnapshot(
                target.ModId,
                target.DisplayName,
                target.HookName,
                target.InvocationKind,
                target.EntrypointTypeName,
                target.EntrypointMethodName,
                target.HookMethodName,
                target.HookMethodSource,
                target.HookMethodParameterCount,
                metadataReady,
                queued,
                RuntimeDispatchEnabled,
                dispatchGateOpen,
                dispatchGateBlocked,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Dispatch",
                "OpenACAI-owned native lifecycle dispatch planning.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateTargetSection(snapshot),
                    CreateIntentionSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleDispatcherSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Lifecycle Dispatcher",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle dispatcher snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Validated native lifecycle targets are planned without invoking mod code."),
                    Row("Runtime Dispatch", snapshot.RuntimeDispatchEnabled ? "Enabled" : "Disabled", snapshot.RuntimeDispatchEnabled ? "READY" : "INFO", "Native lifecycle method dispatch remains disabled until verified in game."),
                    Row("Dispatch Targets", snapshot.DispatchTargetCount.ToString(), snapshot.DispatchIssueCount == 0 ? "READY" : "CHECK", snapshot.DispatchMetadataReadyCount + " metadata-ready target(s); " + snapshot.DispatchIssueCount + " issue(s)."),
                    Row("Dispatch Intentions", snapshot.DispatchIntentCount.ToString(), snapshot.DispatchIntentIssueCount == 0 ? "READY" : "CHECK", snapshot.QueuedDispatchIntentCount + " metadata-only intention(s) queued; " + snapshot.DispatchIntentIssueCount + " issue(s)."),
                    Row("Dispatch Gate", snapshot.DispatchGateOpenCount + " open", snapshot.RuntimeDispatchEnabled ? "READY" : "INFO", snapshot.DispatchGateBlockedCount + " target(s) blocked by the disabled runtime dispatch gate."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle dispatch planning.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateTargetSection(OpenACAINativeLifecycleDispatcherSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Targets", snapshot.DispatchTargetCount.ToString(), snapshot.DispatchIssueCount == 0 ? "READY" : "CHECK", snapshot.DispatchIssueCount + " dispatch target issue(s).")
            };

            foreach (var target in snapshot.DispatchTargets.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(target.DisplayName) ? target.ModId : target.DisplayName,
                    target.HookName,
                    target.Status,
                    target.Detail + " Method " + (string.IsNullOrWhiteSpace(target.HookMethodName) ? "none" : target.HookMethodName) + "; gate " + (target.DispatchGateOpen ? "open" : target.DispatchGateBlocked ? "blocked" : "closed") + "."));
            }

            if (snapshot.DispatchTargetCount == 0)
            {
                rows.Add(Row("No Dispatch Targets", "0", "INFO", "No Modstoke-native lifecycle dispatch targets were discovered."));
            }

            return new OpenACAIMenuSectionSnapshot("Dispatch Targets", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateIntentionSection(OpenACAINativeLifecycleDispatcherSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Intentions", snapshot.DispatchIntentCount.ToString(), snapshot.DispatchIntentIssueCount == 0 ? "READY" : "CHECK", snapshot.QueuedDispatchIntentCount + " queued while runtime dispatch is disabled.")
            };

            foreach (var intention in snapshot.DispatchIntentions.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(intention.DisplayName) ? intention.ModId : intention.DisplayName,
                    intention.HookName,
                    intention.Status,
                    intention.Detail + " Method " + (string.IsNullOrWhiteSpace(intention.HookMethodName) ? "none" : intention.HookMethodName) + "; queued " + intention.Queued + "."));
            }

            if (snapshot.DispatchIntentCount == 0)
            {
                rows.Add(Row("No Dispatch Intentions", "0", "INFO", "No Modstoke-native lifecycle dispatch intentions were queued."));
            }

            return new OpenACAIMenuSectionSnapshot("Dispatch Intentions", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAINativeLifecycleDispatcherSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Dispatch Activity",
                new[]
                {
                    Row("Plans", snapshot.DispatchPlanRequestCount + " requested", "INFO", snapshot.AcceptedDispatchPlanCount + " accepted, " + snapshot.RejectedDispatchPlanCount + " rejected."),
                    Row("Queue", snapshot.QueuedDispatchIntentCount + " queued", snapshot.DispatchIntentIssueCount == 0 ? "READY" : "CHECK", snapshot.DispatchIntentGateBlockedCount + " intention(s) blocked by the disabled runtime dispatch gate."),
                    Row("Last Hook", string.IsNullOrWhiteSpace(snapshot.LastDispatchHookName) ? "none" : snapshot.LastDispatchHookName, "INFO", "Last native lifecycle dispatch plan hook."),
                    Row("Last Plan", snapshot.LastDispatchStatus, snapshot.LastDispatchStatus, snapshot.LastDispatchDetail),
                    Row("Last Reason", snapshot.LastDispatchReason, snapshot.LastDispatchStatus, "Last native lifecycle dispatch plan reason code.")
                });
        }

        private static string NormalizeHookName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            return trimmed.Length <= MaxHookNameLength ? trimmed : trimmed.Substring(0, MaxHookNameLength);
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleDispatcher;
            public string DisplayName => "Modstoke Native Dispatch";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 708;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle dispatch planning.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleDispatcher.CreateContent();
            }
        }
    }

    public sealed class OpenACAINativeLifecycleDispatcherSnapshot
    {
        internal OpenACAINativeLifecycleDispatcherSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool planningEnabled,
            bool runtimeDispatchEnabled,
            int candidateCount,
            int registrationCount,
            int plannedHookCount,
            int registrationIssueCount,
            int hookMethodReadyCount,
            int hookMethodIssueCount,
            int dryRunReadyCount,
            int dryRunIssueCount,
            int libraryExcludedCount,
            IReadOnlyList<OpenACAINativeLifecycleDispatchTargetSnapshot> dispatchTargets,
            int dispatchMetadataReadyCount,
            int dispatchIssueCount,
            int dispatchGateOpenCount,
            int dispatchGateBlockedCount,
            IReadOnlyList<OpenACAINativeLifecycleDispatchIntentSnapshot> dispatchIntentions,
            int dispatchIntentMetadataReadyCount,
            int queuedDispatchIntentCount,
            int dispatchIntentIssueCount,
            int dispatchIntentGateOpenCount,
            int dispatchIntentGateBlockedCount,
            int dispatchPlanRequestCount,
            int acceptedDispatchPlanCount,
            int rejectedDispatchPlanCount,
            string lastDispatchHookName,
            string lastDispatchStatus,
            string lastDispatchDetail,
            string lastDispatchReason)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleDispatcher.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            PlanningEnabled = planningEnabled;
            RuntimeDispatchEnabled = runtimeDispatchEnabled;
            CandidateCount = Math.Max(0, candidateCount);
            RegistrationCount = Math.Max(0, registrationCount);
            PlannedHookCount = Math.Max(0, plannedHookCount);
            RegistrationIssueCount = Math.Max(0, registrationIssueCount);
            HookMethodReadyCount = Math.Max(0, hookMethodReadyCount);
            HookMethodIssueCount = Math.Max(0, hookMethodIssueCount);
            DryRunReadyCount = Math.Max(0, dryRunReadyCount);
            DryRunIssueCount = Math.Max(0, dryRunIssueCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            DispatchTargets = dispatchTargets ?? Array.Empty<OpenACAINativeLifecycleDispatchTargetSnapshot>();
            DispatchTargetCount = DispatchTargets.Count;
            DispatchMetadataReadyCount = Math.Max(0, dispatchMetadataReadyCount);
            DispatchIssueCount = Math.Max(0, dispatchIssueCount);
            DispatchGateOpenCount = Math.Max(0, dispatchGateOpenCount);
            DispatchGateBlockedCount = Math.Max(0, dispatchGateBlockedCount);
            DispatchIntentions = dispatchIntentions ?? Array.Empty<OpenACAINativeLifecycleDispatchIntentSnapshot>();
            DispatchIntentCount = DispatchIntentions.Count;
            DispatchIntentMetadataReadyCount = Math.Max(0, dispatchIntentMetadataReadyCount);
            QueuedDispatchIntentCount = Math.Max(0, queuedDispatchIntentCount);
            DispatchIntentIssueCount = Math.Max(0, dispatchIntentIssueCount);
            DispatchIntentGateOpenCount = Math.Max(0, dispatchIntentGateOpenCount);
            DispatchIntentGateBlockedCount = Math.Max(0, dispatchIntentGateBlockedCount);
            DispatchPlanRequestCount = Math.Max(0, dispatchPlanRequestCount);
            AcceptedDispatchPlanCount = Math.Max(0, acceptedDispatchPlanCount);
            RejectedDispatchPlanCount = Math.Max(0, rejectedDispatchPlanCount);
            LastDispatchHookName = lastDispatchHookName ?? "";
            LastDispatchStatus = lastDispatchStatus ?? "";
            LastDispatchDetail = lastDispatchDetail ?? "";
            LastDispatchReason = lastDispatchReason ?? "";
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool PlanningEnabled { get; }
        public bool RuntimeDispatchEnabled { get; }
        public int CandidateCount { get; }
        public int RegistrationCount { get; }
        public int PlannedHookCount { get; }
        public int RegistrationIssueCount { get; }
        public int HookMethodReadyCount { get; }
        public int HookMethodIssueCount { get; }
        public int DryRunReadyCount { get; }
        public int DryRunIssueCount { get; }
        public int LibraryExcludedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleDispatchTargetSnapshot> DispatchTargets { get; }
        public int DispatchTargetCount { get; }
        public int DispatchMetadataReadyCount { get; }
        public int DispatchIssueCount { get; }
        public int DispatchGateOpenCount { get; }
        public int DispatchGateBlockedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleDispatchIntentSnapshot> DispatchIntentions { get; }
        public int DispatchIntentCount { get; }
        public int DispatchIntentMetadataReadyCount { get; }
        public int QueuedDispatchIntentCount { get; }
        public int DispatchIntentIssueCount { get; }
        public int DispatchIntentGateOpenCount { get; }
        public int DispatchIntentGateBlockedCount { get; }
        public int DispatchPlanRequestCount { get; }
        public int AcceptedDispatchPlanCount { get; }
        public int RejectedDispatchPlanCount { get; }
        public string LastDispatchHookName { get; }
        public string LastDispatchStatus { get; }
        public string LastDispatchDetail { get; }
        public string LastDispatchReason { get; }

        internal static OpenACAINativeLifecycleDispatcherSnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleDispatcherSnapshot(
                OpenACAINativeLifecycleDispatcher.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle dispatcher has not refreshed yet.",
                true,
                OpenACAINativeLifecycleDispatcher.RuntimeDispatchEnabled,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleDispatchTargetSnapshot>(),
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleDispatchIntentSnapshot>(),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "",
                "INFO",
                "No native lifecycle dispatch plan has been requested.",
                "none");
        }
    }

    public sealed class OpenACAINativeLifecycleDispatchTargetSnapshot
    {
        internal OpenACAINativeLifecycleDispatchTargetSnapshot(
            string modId,
            string displayName,
            string hookName,
            string invocationKind,
            string entrypointTypeName,
            string entrypointMethodName,
            string hookMethodName,
            string hookMethodSource,
            int hookMethodParameterCount,
            bool metadataReady,
            bool runtimeDispatchEnabled,
            bool dispatchGateOpen,
            bool dispatchGateBlocked,
            string status,
            string issueCode,
            string detail)
        {
            ModId = modId ?? "";
            DisplayName = displayName ?? "";
            HookName = hookName ?? "";
            InvocationKind = invocationKind ?? "";
            EntrypointTypeName = entrypointTypeName ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            HookMethodName = hookMethodName ?? "";
            HookMethodSource = hookMethodSource ?? "";
            HookMethodParameterCount = Math.Max(0, hookMethodParameterCount);
            MetadataReady = metadataReady;
            RuntimeDispatchEnabled = runtimeDispatchEnabled;
            DispatchGateOpen = dispatchGateOpen;
            DispatchGateBlocked = dispatchGateBlocked;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string ModId { get; }
        public string DisplayName { get; }
        public string HookName { get; }
        public string InvocationKind { get; }
        public string EntrypointTypeName { get; }
        public string EntrypointMethodName { get; }
        public string HookMethodName { get; }
        public string HookMethodSource { get; }
        public int HookMethodParameterCount { get; }
        public bool MetadataReady { get; }
        public bool RuntimeDispatchEnabled { get; }
        public bool DispatchGateOpen { get; }
        public bool DispatchGateBlocked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleDispatchIntentSnapshot
    {
        internal OpenACAINativeLifecycleDispatchIntentSnapshot(
            string modId,
            string displayName,
            string hookName,
            string invocationKind,
            string entrypointTypeName,
            string entrypointMethodName,
            string hookMethodName,
            string hookMethodSource,
            int hookMethodParameterCount,
            bool metadataReady,
            bool queued,
            bool runtimeDispatchEnabled,
            bool dispatchGateOpen,
            bool dispatchGateBlocked,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            ModId = modId ?? "";
            DisplayName = displayName ?? "";
            HookName = hookName ?? "";
            InvocationKind = invocationKind ?? "";
            EntrypointTypeName = entrypointTypeName ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            HookMethodName = hookMethodName ?? "";
            HookMethodSource = hookMethodSource ?? "";
            HookMethodParameterCount = Math.Max(0, hookMethodParameterCount);
            MetadataReady = metadataReady;
            Queued = queued;
            RuntimeDispatchEnabled = runtimeDispatchEnabled;
            DispatchGateOpen = dispatchGateOpen;
            DispatchGateBlocked = dispatchGateBlocked;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public string ModId { get; }
        public string DisplayName { get; }
        public string HookName { get; }
        public string InvocationKind { get; }
        public string EntrypointTypeName { get; }
        public string EntrypointMethodName { get; }
        public string HookMethodName { get; }
        public string HookMethodSource { get; }
        public int HookMethodParameterCount { get; }
        public bool MetadataReady { get; }
        public bool Queued { get; }
        public bool RuntimeDispatchEnabled { get; }
        public bool DispatchGateOpen { get; }
        public bool DispatchGateBlocked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleDispatchPlan
    {
        internal OpenACAINativeLifecycleDispatchPlan(
            string hookName,
            int targetCount,
            int metadataReadyTargetCount,
            int issueTargetCount,
            int dispatchGateOpenCount,
            int dispatchGateBlockedCount,
            bool accepted,
            bool canDispatchNow,
            bool runtimeDispatchEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            HookName = hookName ?? "";
            TargetCount = Math.Max(0, targetCount);
            MetadataReadyTargetCount = Math.Max(0, metadataReadyTargetCount);
            IssueTargetCount = Math.Max(0, issueTargetCount);
            DispatchGateOpenCount = Math.Max(0, dispatchGateOpenCount);
            DispatchGateBlockedCount = Math.Max(0, dispatchGateBlockedCount);
            Accepted = accepted;
            CanDispatchNow = canDispatchNow;
            RuntimeDispatchEnabled = runtimeDispatchEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public string HookName { get; }
        public int TargetCount { get; }
        public int MetadataReadyTargetCount { get; }
        public int IssueTargetCount { get; }
        public int DispatchGateOpenCount { get; }
        public int DispatchGateBlockedCount { get; }
        public bool Accepted { get; }
        public bool CanDispatchNow { get; }
        public bool RuntimeDispatchEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }
}
