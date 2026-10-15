// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle registry gate. This consumes validated
// native-host execution-plan metadata and publishes planned lifecycle hook
// registrations without invoking Modstoke-native mod code.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleRegistry
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleRegistry";
        public const int MaxRegistrationCount = 512;

        private static readonly bool RuntimeInvocationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static OpenACAINativeLifecycleRegistrySnapshot _state = OpenACAINativeLifecycleRegistrySnapshot.CreateUnknown();
        private static bool _initialized;

        public static OpenACAINativeLifecycleRegistrySnapshot State
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
        public static bool RuntimeInvocationEnabled => RuntimeInvocationEnabledValue;
        public static IReadOnlyList<OpenACAINativeLifecycleRegistrationSnapshot> PlannedRegistrations => State.PlannedRegistrations;

        public static IReadOnlyList<OpenACAINativeLifecycleRegistrationSnapshot> GetPlannedRegistrations()
        {
            return PlannedRegistrations;
        }

        public static OpenACAINativeLifecycleRegistrySnapshot Refresh()
        {
            return Refresh(OpenACAINativeModHost.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleRegistrySnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleRegistrySnapshot Refresh(OpenACAINativeModHostSnapshot nativeHost)
        {
            var snapshot = CreateSnapshot(nativeHost);
            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleRegistrySnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleRegistrySnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static OpenACAINativeLifecycleRegistrySnapshot CreateSnapshot(OpenACAINativeModHostSnapshot nativeHost)
        {
            nativeHost = nativeHost ?? OpenACAINativeModHostSnapshot.CreateUnknown();
            var registrations = nativeHost.Candidates
                .Where(candidate => candidate != null)
                .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.ModId, StringComparer.OrdinalIgnoreCase)
                .SelectMany(CreateRegistrations)
                .Take(MaxRegistrationCount)
                .ToArray();

            var acceptedPlanCount = nativeHost.Candidates.Count(candidate => candidate != null && candidate.Hostable && candidate.ExecutionPlanReady);
            var rejectedPlanCount = nativeHost.Candidates.Count(candidate => candidate != null && candidate.ExecutionPlanIssue);
            var registrationIssueCount = registrations.Count(registration => registration.Issue);
            var plannedHookCount = registrations.Count(registration => registration.Planned);
            var deferredHookCount = registrations.Count(registration => string.Equals(registration.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase));
            var invocationGateOpenCount = registrations.Count(registration => registration.InvocationGateOpen);
            var invocationGateBlockedCount = registrations.Count(registration => registration.InvocationGateBlocked);
            var dryRunEvaluationCount = registrations.Sum(registration => registration.DryRunEvaluationCount);
            var dryRunReadyCount = registrations.Sum(registration => registration.DryRunReadyCount);
            var dryRunBlockedCount = registrations.Sum(registration => registration.DryRunBlockedCount);
            var dryRunIssueCount = registrations.Sum(registration => registration.DryRunIssueCount);
            var hookMethodReadyCount = registrations.Sum(registration => registration.HookMethodReadyCount);
            var hookMethodIssueCount = registrations.Sum(registration => registration.HookMethodIssueCount);
            var status = registrationIssueCount == 0 ? "READY" : "CHECK";
            var detail = registrationIssueCount == 0
                ? "Modstoke native lifecycle registry has accepted validated lifecycle metadata; runtime invocation remains explicitly gated."
                : registrationIssueCount + " native lifecycle registration issue(s) were found.";

            return new OpenACAINativeLifecycleRegistrySnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                nativeHost.Ready,
                status,
                detail,
                PlanningEnabled,
                RuntimeInvocationEnabled,
                nativeHost.CandidateCount,
                nativeHost.HostableCandidateCount,
                nativeHost.ExecutionPlanReadyCount,
                nativeHost.ExecutionPlanIssueCount,
                nativeHost.LifecycleHookDeclarationCount,
                nativeHost.LibraryExcludedCount,
                acceptedPlanCount,
                rejectedPlanCount,
                registrations,
                plannedHookCount,
                deferredHookCount,
                registrationIssueCount,
                invocationGateOpenCount,
                invocationGateBlockedCount,
                dryRunEvaluationCount,
                dryRunReadyCount,
                dryRunBlockedCount,
                dryRunIssueCount,
                hookMethodReadyCount,
                hookMethodIssueCount);
        }

        private static IEnumerable<OpenACAINativeLifecycleRegistrationSnapshot> CreateRegistrations(OpenACAINativeModCandidateSnapshot candidate)
        {
            var plan = candidate.ExecutionPlan ?? OpenACAINativeModExecutionPlanSnapshot.Empty(candidate.ModId);
            if (plan.LifecycleHookPlanCount == 0)
            {
                yield break;
            }

            foreach (var hookPlan in plan.LifecycleHookPlans)
            {
                if (hookPlan == null)
                {
                    continue;
                }

                var hook = hookPlan.HookName;
                if (string.IsNullOrWhiteSpace(hook))
                {
                    continue;
                }

                var ready = candidate.Hostable && plan.Ready && hookPlan.Ready;
                var status = ready ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "CHECK";
                var issueCode = ready
                    ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled"
                    : hookPlan.Issue ? hookPlan.IssueCode : string.IsNullOrWhiteSpace(candidate.IssueCode) ? plan.IssueCode : candidate.IssueCode;
                var invocationGateOpen = ready && RuntimeInvocationEnabled;
                var invocationGateBlocked = ready && !RuntimeInvocationEnabled;
                var dryRunEvaluated = true;
                var dryRunReady = ready;
                var dryRunStatus = dryRunReady ? "READY" : "CHECK";
                var dryRunIssueCode = dryRunReady ? "" : string.IsNullOrWhiteSpace(issueCode) ? "registration-not-ready" : issueCode;
                var dryRunDetail = dryRunReady
                    ? "Dry-run accepted this lifecycle hook registration metadata; runtime invocation remains controlled by the invocation gate."
                    : "Dry-run rejected this lifecycle hook registration metadata.";
                var detail = ready
                    ? RuntimeInvocationEnabled
                        ? "Modstoke native lifecycle hook is ready for invocation."
                        : "OpenACAI accepted this lifecycle hook registration plan; runtime invocation remains disabled."
                    : hookPlan.Issue
                        ? "OpenACAI rejected this lifecycle hook registration because the hook method plan is not ready."
                        : "OpenACAI rejected this lifecycle hook registration because the native execution plan is not hostable.";

                yield return new OpenACAINativeLifecycleRegistrationSnapshot(
                    candidate.ModId,
                    candidate.DisplayName,
                    hook,
                    plan.InvocationKind,
                    plan.EntrypointTypeName,
                    plan.EntrypointMethodName,
                    hookPlan.MethodName,
                    hookPlan.MethodDeclared,
                    hookPlan.MethodSource,
                    hookPlan.MethodFound,
                    hookPlan.MethodStatic,
                    hookPlan.MethodPublic,
                    hookPlan.MethodParameterCount,
                    hookPlan.Ready,
                    hookPlan.Status,
                    hookPlan.IssueCode,
                    hookPlan.Detail,
                    ready,
                    RuntimeInvocationEnabled,
                    invocationGateOpen,
                    invocationGateBlocked,
                    dryRunEvaluated,
                    dryRunReady,
                    dryRunStatus,
                    dryRunIssueCode,
                    dryRunDetail,
                    status,
                    issueCode,
                    detail,
                    DateTimeOffset.UtcNow.ToString("O"));
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Lifecycle",
                "OpenACAI-owned native lifecycle hook registration planning.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateRegistrationSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleRegistrySnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Lifecycle Registry",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle registry snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Validated native lifecycle metadata is accepted without invoking mod code."),
                    Row("Runtime Invocation", snapshot.RuntimeInvocationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", "Native lifecycle hook invocation remains disabled until verified in game."),
                    Row("Native Plans", snapshot.AcceptedPlanCount + "/" + snapshot.CandidateCount, snapshot.ExecutionPlanIssueCount == 0 ? "READY" : "CHECK", snapshot.ExecutionPlanReadyCount + " execution plan(s) ready; " + snapshot.ExecutionPlanIssueCount + " issue(s)."),
                    Row("Lifecycle Hooks", snapshot.PlannedHookCount + "/" + snapshot.LifecycleHookDeclarationCount, snapshot.RegistrationIssueCount == 0 ? "READY" : "CHECK", snapshot.DeferredHookCount + " deferred hook registration(s)."),
                    Row("Hook Methods", snapshot.HookMethodReadyCount + "/" + snapshot.PlannedHookCount, snapshot.HookMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.HookMethodIssueCount + " hook method issue(s)."),
                    Row("Invocation Gate", snapshot.InvocationGateOpenCount + " open", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", snapshot.InvocationGateBlockedCount + " hook invocation(s) blocked by the disabled runtime gate."),
                    Row("Dry Run", snapshot.DryRunReadyCount + "/" + snapshot.DryRunEvaluationCount, snapshot.DryRunIssueCount == 0 ? "READY" : "CHECK", snapshot.DryRunBlockedCount + " dry-run-ready hook(s) remain runtime-blocked."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle hook registration planning.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateRegistrationSection(OpenACAINativeLifecycleRegistrySnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Registrations", snapshot.RegistrationCount.ToString(), snapshot.RegistrationIssueCount == 0 ? "READY" : "CHECK", snapshot.RegistrationIssueCount + " registration issue(s).")
            };

            foreach (var registration in snapshot.PlannedRegistrations.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(registration.DisplayName) ? registration.ModId : registration.DisplayName,
                    registration.HookName,
                    registration.Status,
                    registration.Detail + " Method " + (string.IsNullOrWhiteSpace(registration.HookMethodName) ? "none" : registration.HookMethodName) + "; dry-run " + registration.DryRunStatus + "; invocation " + registration.InvocationKind + "."));
            }

            if (snapshot.RegistrationCount == 0)
            {
                rows.Add(Row("No Lifecycle Hooks", "0", "INFO", "No Modstoke-native lifecycle hook declarations were discovered."));
            }

            return new OpenACAIMenuSectionSnapshot("Planned Registrations", rows);
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleRegistry;
            public string DisplayName => "Modstoke Native Lifecycle";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 707;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle hook registration planning.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleRegistry.CreateContent();
            }
        }
    }

    public sealed class OpenACAINativeLifecycleRegistrySnapshot
    {
        internal OpenACAINativeLifecycleRegistrySnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool planningEnabled,
            bool runtimeInvocationEnabled,
            int candidateCount,
            int hostableCandidateCount,
            int executionPlanReadyCount,
            int executionPlanIssueCount,
            int lifecycleHookDeclarationCount,
            int libraryExcludedCount,
            int acceptedPlanCount,
            int rejectedPlanCount,
            IReadOnlyList<OpenACAINativeLifecycleRegistrationSnapshot> plannedRegistrations,
            int plannedHookCount,
            int deferredHookCount,
            int registrationIssueCount,
            int invocationGateOpenCount,
            int invocationGateBlockedCount,
            int dryRunEvaluationCount,
            int dryRunReadyCount,
            int dryRunBlockedCount,
            int dryRunIssueCount,
            int hookMethodReadyCount,
            int hookMethodIssueCount)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleRegistry.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            PlanningEnabled = planningEnabled;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            CandidateCount = Math.Max(0, candidateCount);
            HostableCandidateCount = Math.Max(0, hostableCandidateCount);
            ExecutionPlanReadyCount = Math.Max(0, executionPlanReadyCount);
            ExecutionPlanIssueCount = Math.Max(0, executionPlanIssueCount);
            LifecycleHookDeclarationCount = Math.Max(0, lifecycleHookDeclarationCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            AcceptedPlanCount = Math.Max(0, acceptedPlanCount);
            RejectedPlanCount = Math.Max(0, rejectedPlanCount);
            PlannedRegistrations = plannedRegistrations ?? Array.Empty<OpenACAINativeLifecycleRegistrationSnapshot>();
            RegistrationCount = PlannedRegistrations.Count;
            PlannedHookCount = Math.Max(0, plannedHookCount);
            DeferredHookCount = Math.Max(0, deferredHookCount);
            RegistrationIssueCount = Math.Max(0, registrationIssueCount);
            InvocationGateOpenCount = Math.Max(0, invocationGateOpenCount);
            InvocationGateBlockedCount = Math.Max(0, invocationGateBlockedCount);
            DryRunEvaluationCount = Math.Max(0, dryRunEvaluationCount);
            DryRunReadyCount = Math.Max(0, dryRunReadyCount);
            DryRunBlockedCount = Math.Max(0, dryRunBlockedCount);
            DryRunIssueCount = Math.Max(0, dryRunIssueCount);
            HookMethodReadyCount = Math.Max(0, hookMethodReadyCount);
            HookMethodIssueCount = Math.Max(0, hookMethodIssueCount);
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool PlanningEnabled { get; }
        public bool RuntimeInvocationEnabled { get; }
        public int CandidateCount { get; }
        public int HostableCandidateCount { get; }
        public int ExecutionPlanReadyCount { get; }
        public int ExecutionPlanIssueCount { get; }
        public int LifecycleHookDeclarationCount { get; }
        public int LibraryExcludedCount { get; }
        public int AcceptedPlanCount { get; }
        public int RejectedPlanCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleRegistrationSnapshot> PlannedRegistrations { get; }
        public int RegistrationCount { get; }
        public int PlannedHookCount { get; }
        public int DeferredHookCount { get; }
        public int RegistrationIssueCount { get; }
        public int InvocationGateOpenCount { get; }
        public int InvocationGateBlockedCount { get; }
        public int DryRunEvaluationCount { get; }
        public int DryRunReadyCount { get; }
        public int DryRunBlockedCount { get; }
        public int DryRunIssueCount { get; }
        public int HookMethodReadyCount { get; }
        public int HookMethodIssueCount { get; }

        internal static OpenACAINativeLifecycleRegistrySnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleRegistrySnapshot(
                OpenACAINativeLifecycleRegistry.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle registry has not refreshed yet.",
                true,
                OpenACAINativeLifecycleRegistry.RuntimeInvocationEnabled,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleRegistrationSnapshot>(),
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
                0);
        }
    }

    public sealed class OpenACAINativeLifecycleRegistrationSnapshot
    {
        internal OpenACAINativeLifecycleRegistrationSnapshot(
            string modId,
            string displayName,
            string hookName,
            string invocationKind,
            string entrypointTypeName,
            string entrypointMethodName,
            string hookMethodName,
            bool hookMethodDeclared,
            string hookMethodSource,
            bool hookMethodFound,
            bool hookMethodStatic,
            bool hookMethodPublic,
            int hookMethodParameterCount,
            bool hookMethodReady,
            string hookMethodStatus,
            string hookMethodIssueCode,
            string hookMethodDetail,
            bool planned,
            bool runtimeInvocationEnabled,
            bool invocationGateOpen,
            bool invocationGateBlocked,
            bool dryRunEvaluated,
            bool dryRunReady,
            string dryRunStatus,
            string dryRunIssueCode,
            string dryRunDetail,
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
            HookMethodDeclared = hookMethodDeclared;
            HookMethodSource = hookMethodSource ?? "";
            HookMethodFound = hookMethodFound;
            HookMethodStatic = hookMethodStatic;
            HookMethodPublic = hookMethodPublic;
            HookMethodParameterCount = Math.Max(0, hookMethodParameterCount);
            HookMethodReady = hookMethodReady;
            HookMethodStatus = hookMethodStatus ?? "";
            HookMethodIssueCode = hookMethodIssueCode ?? "";
            HookMethodDetail = hookMethodDetail ?? "";
            HookMethodReadyCount = hookMethodReady ? 1 : 0;
            HookMethodIssueCount = string.Equals(HookMethodStatus, "CHECK", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            Planned = planned;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            InvocationGateOpen = invocationGateOpen;
            InvocationGateBlocked = invocationGateBlocked;
            DryRunEvaluated = dryRunEvaluated;
            DryRunReady = dryRunReady;
            DryRunStatus = dryRunStatus ?? "";
            DryRunIssueCode = dryRunIssueCode ?? "";
            DryRunDetail = dryRunDetail ?? "";
            DryRunEvaluationCount = dryRunEvaluated ? 1 : 0;
            DryRunReadyCount = dryRunReady ? 1 : 0;
            DryRunBlockedCount = invocationGateBlocked ? 1 : 0;
            DryRunIssueCount = string.Equals(DryRunStatus, "CHECK", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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
        public bool HookMethodDeclared { get; }
        public string HookMethodSource { get; }
        public bool HookMethodFound { get; }
        public bool HookMethodStatic { get; }
        public bool HookMethodPublic { get; }
        public int HookMethodParameterCount { get; }
        public bool HookMethodReady { get; }
        public string HookMethodStatus { get; }
        public string HookMethodIssueCode { get; }
        public string HookMethodDetail { get; }
        public int HookMethodReadyCount { get; }
        public int HookMethodIssueCount { get; }
        public bool Planned { get; }
        public bool RuntimeInvocationEnabled { get; }
        public bool InvocationGateOpen { get; }
        public bool InvocationGateBlocked { get; }
        public bool DryRunEvaluated { get; }
        public bool DryRunReady { get; }
        public string DryRunStatus { get; }
        public string DryRunIssueCode { get; }
        public string DryRunDetail { get; }
        public int DryRunEvaluationCount { get; }
        public int DryRunReadyCount { get; }
        public int DryRunBlockedCount { get; }
        public int DryRunIssueCount { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
