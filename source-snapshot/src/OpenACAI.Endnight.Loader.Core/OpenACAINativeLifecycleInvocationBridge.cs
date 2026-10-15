// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle invocation bridge. This consumes dispatcher
// intentions and publishes invocation requests without invoking mod code.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleInvocationBridge
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationBridge";
        public const int MaxInvocationRequestCount = OpenACAINativeLifecycleDispatcher.MaxDispatchIntentCount;
        public const int MaxHookNameLength = OpenACAINativeModHost.MaxLifecycleHookLength;

        private static readonly bool RuntimeInvocationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoPlannedInvocationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static OpenACAINativeLifecycleInvocationBridgeSnapshot _state = OpenACAINativeLifecycleInvocationBridgeSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _invocationPlanRequestCount;
        private static int _acceptedInvocationPlanCount;
        private static int _rejectedInvocationPlanCount;
        private static string _lastInvocationHookName = "";
        private static string _lastInvocationStatus = "INFO";
        private static string _lastInvocationDetail = "No native lifecycle invocation plan has been requested.";
        private static string _lastInvocationReason = "none";

        public static OpenACAINativeLifecycleInvocationBridgeSnapshot State
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
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationRequestSnapshot> InvocationRequests => State.InvocationRequests;

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationRequestSnapshot> GetInvocationRequests()
        {
            return InvocationRequests;
        }

        public static OpenACAINativeLifecycleInvocationPlan PlanInvocation(string hookName)
        {
            var plan = CreateInvocationPlan(State, hookName);
            RecordInvocationPlan(plan);
            Refresh();
            return plan;
        }

        public static OpenACAINativeLifecycleInvocationBridgeSnapshot Refresh()
        {
            return Refresh(OpenACAINativeLifecycleDispatcher.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleInvocationBridgeSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle invocation bridge provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleInvocationBridgeSnapshot Refresh(OpenACAINativeLifecycleDispatcherSnapshot dispatcher)
        {
            var snapshot = CreateSnapshot(dispatcher);
            if (RecordAutomaticInvocationPlans(snapshot))
            {
                snapshot = CreateSnapshot(dispatcher);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleInvocationBridgeSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleInvocationBridgeSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordInvocationPlan(OpenACAINativeLifecycleInvocationPlan plan)
        {
            lock (SyncRoot)
            {
                _invocationPlanRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedInvocationPlanCount++;
                }
                else
                {
                    _rejectedInvocationPlanCount++;
                }

                _lastInvocationHookName = plan == null ? "" : plan.HookName;
                _lastInvocationStatus = plan == null ? "CHECK" : plan.Status;
                _lastInvocationDetail = plan == null ? "Native lifecycle invocation plan was null." : plan.Detail;
                _lastInvocationReason = plan == null ? "null-plan" : plan.ReasonCode;
            }
        }

        private static bool RecordAutomaticInvocationPlans(OpenACAINativeLifecycleInvocationBridgeSnapshot snapshot)
        {
            if (snapshot == null || snapshot.InvocationRequestCount == 0)
            {
                lock (SyncRoot)
                {
                    return AutoPlannedInvocationKeys.RemoveWhere(key => !string.IsNullOrWhiteSpace(key)) > 0;
                }
            }

            var currentKeys = snapshot.InvocationRequests
                .Where(request => request != null && request.AdapterReady && !string.IsNullOrWhiteSpace(request.HookName))
                .Select(request => "hook:" + NormalizeHookName(request.HookName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentKeySet = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);
            var hooksToPlan = new List<string>();

            lock (SyncRoot)
            {
                AutoPlannedInvocationKeys.RemoveWhere(key => !currentKeySet.Contains(key));
                foreach (var key in currentKeys)
                {
                    if (AutoPlannedInvocationKeys.Add(key))
                    {
                        hooksToPlan.Add(key.Substring("hook:".Length));
                    }
                }
            }

            foreach (var hookName in hooksToPlan)
            {
                RecordInvocationPlan(CreateInvocationPlan(snapshot, hookName));
            }

            return hooksToPlan.Count > 0;
        }

        private static OpenACAINativeLifecycleInvocationPlan CreateInvocationPlan(OpenACAINativeLifecycleInvocationBridgeSnapshot snapshot, string hookName)
        {
            snapshot = snapshot ?? OpenACAINativeLifecycleInvocationBridgeSnapshot.CreateUnknown();
            var normalizedHookName = NormalizeHookName(hookName);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Native lifecycle invocation plan is invalid.";
            var accepted = false;
            var canInvokeNow = false;
            IReadOnlyList<OpenACAINativeLifecycleInvocationRequestSnapshot> requests = Array.Empty<OpenACAINativeLifecycleInvocationRequestSnapshot>();

            if (string.IsNullOrWhiteSpace(normalizedHookName))
            {
                reason = "missing-hook-name";
                detail = "A native lifecycle hook name is required.";
            }
            else
            {
                requests = snapshot.InvocationRequests
                    .Where(request => request != null && string.Equals(request.HookName, normalizedHookName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (requests.Count == 0)
                {
                    reason = "no-invocation-requests";
                    detail = "No native lifecycle invocation requests matched hook '" + normalizedHookName + "'.";
                }
                else if (requests.Any(request => request.Issue))
                {
                    reason = "invocation-request-issues";
                    detail = requests.Count(request => request.Issue) + " native lifecycle invocation request issue(s) were found for hook '" + normalizedHookName + "'.";
                }
                else if (requests.Any(request => !request.AdapterReady))
                {
                    reason = "invocation-adapter-not-ready";
                    detail = "One or more native lifecycle invocation adapters are not metadata-ready for hook '" + normalizedHookName + "'.";
                }
                else
                {
                    accepted = true;
                    if (!RuntimeInvocationEnabled)
                    {
                        status = "DEFERRED";
                        reason = "runtime-invocation-disabled";
                        detail = "OpenACAI accepted the native lifecycle invocation plan; runtime invocation remains disabled.";
                    }
                    else
                    {
                        canInvokeNow = true;
                        status = "READY";
                        reason = "ready";
                        detail = "Modstoke native lifecycle invocation plan is ready for method invocation.";
                    }
                }
            }

            return new OpenACAINativeLifecycleInvocationPlan(
                normalizedHookName,
                requests.Count,
                requests.Count(request => request.AdapterReady),
                requests.Count(request => request.Issue),
                requests.Count(request => request.InvocationGateOpen),
                requests.Count(request => request.InvocationGateBlocked),
                accepted,
                canInvokeNow,
                RuntimeInvocationEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static OpenACAINativeLifecycleInvocationBridgeSnapshot CreateSnapshot(OpenACAINativeLifecycleDispatcherSnapshot dispatcher)
        {
            dispatcher = dispatcher ?? OpenACAINativeLifecycleDispatcherSnapshot.CreateUnknown();
            var requests = dispatcher.DispatchIntentions
                .Where(intention => intention != null)
                .OrderBy(intention => intention.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(intention => intention.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(intention => intention.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(CreateRequest)
                .Take(MaxInvocationRequestCount)
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
                requestCount = _invocationPlanRequestCount;
                acceptedCount = _acceptedInvocationPlanCount;
                rejectedCount = _rejectedInvocationPlanCount;
                lastHookName = _lastInvocationHookName ?? "";
                lastStatus = _lastInvocationStatus ?? "";
                lastDetail = _lastInvocationDetail ?? "";
                lastReason = _lastInvocationReason ?? "";
            }

            var adapterReadyCount = requests.Count(request => request.AdapterReady);
            var invocationIssueCount = requests.Count(request => request.Issue);
            var invocationGateOpenCount = requests.Count(request => request.InvocationGateOpen);
            var invocationGateBlockedCount = requests.Count(request => request.InvocationGateBlocked);
            var status = invocationIssueCount == 0 ? "READY" : "CHECK";
            var detail = invocationIssueCount == 0
                ? "Modstoke native lifecycle invocation bridge has accepted metadata-ready requests; runtime invocation remains explicitly gated."
                : invocationIssueCount + " native lifecycle invocation request issue(s) were found.";

            return new OpenACAINativeLifecycleInvocationBridgeSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                dispatcher.Ready,
                status,
                detail,
                PlanningEnabled,
                RuntimeInvocationEnabled,
                dispatcher.CandidateCount,
                dispatcher.DispatchTargetCount,
                dispatcher.DispatchIntentCount,
                dispatcher.QueuedDispatchIntentCount,
                dispatcher.DispatchIntentIssueCount,
                dispatcher.LibraryExcludedCount,
                requests,
                adapterReadyCount,
                invocationIssueCount,
                invocationGateOpenCount,
                invocationGateBlockedCount,
                requestCount,
                acceptedCount,
                rejectedCount,
                lastHookName,
                lastStatus,
                lastDetail,
                lastReason);
        }

        private static OpenACAINativeLifecycleInvocationRequestSnapshot CreateRequest(OpenACAINativeLifecycleDispatchIntentSnapshot intention)
        {
            var adapterReady = intention.MetadataReady && intention.Queued && !intention.Issue;
            var invocationGateOpen = adapterReady && RuntimeInvocationEnabled;
            var invocationGateBlocked = adapterReady && !RuntimeInvocationEnabled;
            var status = adapterReady ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = adapterReady
                ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled"
                : !string.IsNullOrWhiteSpace(intention.IssueCode) ? intention.IssueCode : "dispatch-intention-not-ready";
            var detail = adapterReady
                ? RuntimeInvocationEnabled
                    ? "Modstoke native lifecycle invocation request is ready for method invocation."
                    : "OpenACAI accepted this lifecycle invocation request as metadata-only evidence; runtime invocation remains disabled."
                : "OpenACAI rejected this lifecycle invocation request because dispatch intention metadata is not ready.";

            return new OpenACAINativeLifecycleInvocationRequestSnapshot(
                intention.ModId,
                intention.DisplayName,
                intention.HookName,
                intention.InvocationKind,
                intention.EntrypointTypeName,
                intention.EntrypointMethodName,
                intention.HookMethodName,
                intention.HookMethodSource,
                intention.HookMethodParameterCount,
                adapterReady,
                RuntimeInvocationEnabled,
                invocationGateOpen,
                invocationGateBlocked,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Invocation",
                "OpenACAI-owned native lifecycle invocation bridge planning.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateRequestSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleInvocationBridgeSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Invocation Bridge",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle invocation bridge snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Invocation requests are planned without invoking mod code."),
                    Row("Runtime Invocation", snapshot.RuntimeInvocationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", "Native lifecycle method invocation remains disabled until verified in game."),
                    Row("Invocation Requests", snapshot.InvocationRequestCount.ToString(), snapshot.InvocationIssueCount == 0 ? "READY" : "CHECK", snapshot.AdapterReadyCount + " adapter-ready request(s); " + snapshot.InvocationIssueCount + " issue(s)."),
                    Row("Invocation Gate", snapshot.InvocationGateOpenCount + " open", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", snapshot.InvocationGateBlockedCount + " request(s) blocked by the disabled runtime invocation gate."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle invocation planning.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateRequestSection(OpenACAINativeLifecycleInvocationBridgeSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Requests", snapshot.InvocationRequestCount.ToString(), snapshot.InvocationIssueCount == 0 ? "READY" : "CHECK", snapshot.InvocationIssueCount + " invocation request issue(s).")
            };

            foreach (var request in snapshot.InvocationRequests.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(request.DisplayName) ? request.ModId : request.DisplayName,
                    request.HookName,
                    request.Status,
                    request.Detail + " Method " + (string.IsNullOrWhiteSpace(request.HookMethodName) ? "none" : request.HookMethodName) + "; gate " + (request.InvocationGateOpen ? "open" : request.InvocationGateBlocked ? "blocked" : "closed") + "."));
            }

            if (snapshot.InvocationRequestCount == 0)
            {
                rows.Add(Row("No Invocation Requests", "0", "INFO", "No Modstoke-native lifecycle invocation requests were discovered."));
            }

            return new OpenACAIMenuSectionSnapshot("Invocation Requests", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAINativeLifecycleInvocationBridgeSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Invocation Activity",
                new[]
                {
                    Row("Plans", snapshot.InvocationPlanRequestCount + " requested", "INFO", snapshot.AcceptedInvocationPlanCount + " accepted, " + snapshot.RejectedInvocationPlanCount + " rejected."),
                    Row("Last Hook", string.IsNullOrWhiteSpace(snapshot.LastInvocationHookName) ? "none" : snapshot.LastInvocationHookName, "INFO", "Last native lifecycle invocation plan hook."),
                    Row("Last Plan", snapshot.LastInvocationStatus, snapshot.LastInvocationStatus, snapshot.LastInvocationDetail),
                    Row("Last Reason", snapshot.LastInvocationReason, snapshot.LastInvocationStatus, "Last native lifecycle invocation plan reason code.")
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
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleInvocationBridge;
            public string DisplayName => "Modstoke Native Invocation";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 709;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle invocation bridge planning.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleInvocationBridge.CreateContent();
            }
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationBridgeSnapshot
    {
        internal OpenACAINativeLifecycleInvocationBridgeSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool planningEnabled,
            bool runtimeInvocationEnabled,
            int candidateCount,
            int dispatchTargetCount,
            int dispatchIntentCount,
            int queuedDispatchIntentCount,
            int dispatchIntentIssueCount,
            int libraryExcludedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationRequestSnapshot> invocationRequests,
            int adapterReadyCount,
            int invocationIssueCount,
            int invocationGateOpenCount,
            int invocationGateBlockedCount,
            int invocationPlanRequestCount,
            int acceptedInvocationPlanCount,
            int rejectedInvocationPlanCount,
            string lastInvocationHookName,
            string lastInvocationStatus,
            string lastInvocationDetail,
            string lastInvocationReason)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleInvocationBridge.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            PlanningEnabled = planningEnabled;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            CandidateCount = Math.Max(0, candidateCount);
            DispatchTargetCount = Math.Max(0, dispatchTargetCount);
            DispatchIntentCount = Math.Max(0, dispatchIntentCount);
            QueuedDispatchIntentCount = Math.Max(0, queuedDispatchIntentCount);
            DispatchIntentIssueCount = Math.Max(0, dispatchIntentIssueCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            InvocationRequests = invocationRequests ?? Array.Empty<OpenACAINativeLifecycleInvocationRequestSnapshot>();
            InvocationRequestCount = InvocationRequests.Count;
            AdapterReadyCount = Math.Max(0, adapterReadyCount);
            InvocationIssueCount = Math.Max(0, invocationIssueCount);
            InvocationGateOpenCount = Math.Max(0, invocationGateOpenCount);
            InvocationGateBlockedCount = Math.Max(0, invocationGateBlockedCount);
            InvocationPlanRequestCount = Math.Max(0, invocationPlanRequestCount);
            AcceptedInvocationPlanCount = Math.Max(0, acceptedInvocationPlanCount);
            RejectedInvocationPlanCount = Math.Max(0, rejectedInvocationPlanCount);
            LastInvocationHookName = lastInvocationHookName ?? "";
            LastInvocationStatus = lastInvocationStatus ?? "";
            LastInvocationDetail = lastInvocationDetail ?? "";
            LastInvocationReason = lastInvocationReason ?? "";
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
        public int DispatchTargetCount { get; }
        public int DispatchIntentCount { get; }
        public int QueuedDispatchIntentCount { get; }
        public int DispatchIntentIssueCount { get; }
        public int LibraryExcludedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationRequestSnapshot> InvocationRequests { get; }
        public int InvocationRequestCount { get; }
        public int AdapterReadyCount { get; }
        public int InvocationIssueCount { get; }
        public int InvocationGateOpenCount { get; }
        public int InvocationGateBlockedCount { get; }
        public int InvocationPlanRequestCount { get; }
        public int AcceptedInvocationPlanCount { get; }
        public int RejectedInvocationPlanCount { get; }
        public string LastInvocationHookName { get; }
        public string LastInvocationStatus { get; }
        public string LastInvocationDetail { get; }
        public string LastInvocationReason { get; }

        internal static OpenACAINativeLifecycleInvocationBridgeSnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleInvocationBridgeSnapshot(
                OpenACAINativeLifecycleInvocationBridge.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle invocation bridge has not refreshed yet.",
                true,
                OpenACAINativeLifecycleInvocationBridge.RuntimeInvocationEnabled,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleInvocationRequestSnapshot>(),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "",
                "INFO",
                "No native lifecycle invocation plan has been requested.",
                "none");
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationRequestSnapshot
    {
        internal OpenACAINativeLifecycleInvocationRequestSnapshot(
            string modId,
            string displayName,
            string hookName,
            string invocationKind,
            string entrypointTypeName,
            string entrypointMethodName,
            string hookMethodName,
            string hookMethodSource,
            int hookMethodParameterCount,
            bool adapterReady,
            bool runtimeInvocationEnabled,
            bool invocationGateOpen,
            bool invocationGateBlocked,
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
            AdapterReady = adapterReady;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            InvocationGateOpen = invocationGateOpen;
            InvocationGateBlocked = invocationGateBlocked;
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
        public bool AdapterReady { get; }
        public bool RuntimeInvocationEnabled { get; }
        public bool InvocationGateOpen { get; }
        public bool InvocationGateBlocked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleInvocationPlan
    {
        internal OpenACAINativeLifecycleInvocationPlan(
            string hookName,
            int requestCount,
            int adapterReadyCount,
            int issueCount,
            int invocationGateOpenCount,
            int invocationGateBlockedCount,
            bool accepted,
            bool canInvokeNow,
            bool runtimeInvocationEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            HookName = hookName ?? "";
            RequestCount = Math.Max(0, requestCount);
            AdapterReadyCount = Math.Max(0, adapterReadyCount);
            IssueCount = Math.Max(0, issueCount);
            InvocationGateOpenCount = Math.Max(0, invocationGateOpenCount);
            InvocationGateBlockedCount = Math.Max(0, invocationGateBlockedCount);
            Accepted = accepted;
            CanInvokeNow = canInvokeNow;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public string HookName { get; }
        public int RequestCount { get; }
        public int AdapterReadyCount { get; }
        public int IssueCount { get; }
        public int InvocationGateOpenCount { get; }
        public int InvocationGateBlockedCount { get; }
        public bool Accepted { get; }
        public bool CanInvokeNow { get; }
        public bool RuntimeInvocationEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }
}
