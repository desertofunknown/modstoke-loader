// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle invocation adapter. This binds invocation
// requests into diagnostic method-binding tokens without invoking mod code.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleInvocationAdapter
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationAdapter";
        public const int MaxBindingCount = OpenACAINativeLifecycleInvocationBridge.MaxInvocationRequestCount;
        public const int MaxHookNameLength = OpenACAINativeModHost.MaxLifecycleHookLength;

        private static readonly bool RuntimeInvocationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static OpenACAINativeLifecycleInvocationAdapterSnapshot _state = OpenACAINativeLifecycleInvocationAdapterSnapshot.CreateUnknown();
        private static bool _initialized;

        public static OpenACAINativeLifecycleInvocationAdapterSnapshot State
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
        public static bool BindingEnabled => true;
        public static bool RuntimeInvocationEnabled => RuntimeInvocationEnabledValue;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationBindingSnapshot> Bindings => State.Bindings;

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationBindingSnapshot> GetBindings()
        {
            return Bindings;
        }

        public static IReadOnlyList<string> GetBindingTokens()
        {
            return State.BindingTokens;
        }

        public static OpenACAINativeLifecycleInvocationAdapterSnapshot Refresh()
        {
            return Refresh(OpenACAINativeLifecycleInvocationBridge.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleInvocationAdapterSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle invocation adapter provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleInvocationAdapterSnapshot Refresh(OpenACAINativeLifecycleInvocationBridgeSnapshot bridge)
        {
            var snapshot = CreateSnapshot(bridge);
            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleInvocationAdapterSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleInvocationAdapterSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static OpenACAINativeLifecycleInvocationAdapterSnapshot CreateSnapshot(OpenACAINativeLifecycleInvocationBridgeSnapshot bridge)
        {
            bridge = bridge ?? OpenACAINativeLifecycleInvocationBridgeSnapshot.CreateUnknown();
            var bindings = bridge.InvocationRequests
                .Where(request => request != null)
                .OrderBy(request => request.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(request => request.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(request => request.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(CreateBinding)
                .Take(MaxBindingCount)
                .ToArray();

            var bindingReadyCount = bindings.Count(binding => binding.BindingReady);
            var bindingIssueCount = bindings.Count(binding => binding.Issue);
            var bindingTokenIssuedCount = bindings.Count(binding => binding.BindingTokenIssued);
            var invocationEligibleCount = bindings.Count(binding => binding.InvocationEligible);
            var invocationGateOpenCount = bindings.Count(binding => binding.InvocationGateOpen);
            var invocationGateBlockedCount = bindings.Count(binding => binding.InvocationGateBlocked);
            var status = bindingIssueCount == 0 ? "READY" : "CHECK";
            var detail = bindingIssueCount == 0
                ? "Modstoke native lifecycle invocation adapter has bound metadata-ready requests; runtime invocation remains explicitly gated."
                : bindingIssueCount + " native lifecycle invocation adapter binding issue(s) were found.";

            return new OpenACAINativeLifecycleInvocationAdapterSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                bridge.Ready,
                status,
                detail,
                BindingEnabled,
                RuntimeInvocationEnabled,
                bridge.CandidateCount,
                bridge.DispatchTargetCount,
                bridge.DispatchIntentCount,
                bridge.QueuedDispatchIntentCount,
                bridge.InvocationRequestCount,
                bridge.AdapterReadyCount,
                bridge.InvocationIssueCount,
                bridge.InvocationGateOpenCount,
                bridge.InvocationGateBlockedCount,
                bridge.InvocationPlanRequestCount,
                bridge.AcceptedInvocationPlanCount,
                bridge.RejectedInvocationPlanCount,
                bridge.LibraryExcludedCount,
                bindings,
                bindingReadyCount,
                bindingIssueCount,
                bindingTokenIssuedCount,
                invocationEligibleCount,
                invocationGateOpenCount,
                invocationGateBlockedCount);
        }

        private static OpenACAINativeLifecycleInvocationBindingSnapshot CreateBinding(OpenACAINativeLifecycleInvocationRequestSnapshot request)
        {
            request = request ?? new OpenACAINativeLifecycleInvocationRequestSnapshot(
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
                RuntimeInvocationEnabled,
                false,
                false,
                "CHECK",
                "missing-invocation-request",
                "OpenACAI could not create a lifecycle invocation binding because the request was missing.",
                DateTimeOffset.UtcNow.ToString("O"));

            var bindingReady = request.AdapterReady && !request.Issue;
            var bindingToken = bindingReady ? CreateBindingToken(request) : "";
            var bindingTokenIssued = !string.IsNullOrWhiteSpace(bindingToken);
            var invocationGateOpen = bindingReady && RuntimeInvocationEnabled;
            var invocationGateBlocked = bindingReady && !RuntimeInvocationEnabled;
            var invocationEligible = bindingReady && RuntimeInvocationEnabled;
            var status = bindingReady ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = bindingReady
                ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled"
                : !string.IsNullOrWhiteSpace(request.IssueCode) ? request.IssueCode : "invocation-request-not-ready";
            var detail = bindingReady
                ? RuntimeInvocationEnabled
                    ? "Modstoke native lifecycle invocation binding is eligible for method invocation."
                    : "OpenACAI bound this lifecycle invocation request to a diagnostic token; runtime invocation remains disabled."
                : "OpenACAI rejected this lifecycle invocation binding because invocation request metadata is not ready.";

            return new OpenACAINativeLifecycleInvocationBindingSnapshot(
                request.ModId,
                request.DisplayName,
                NormalizeHookName(request.HookName),
                request.InvocationKind,
                request.EntrypointTypeName,
                request.EntrypointMethodName,
                request.HookMethodName,
                request.HookMethodSource,
                request.HookMethodParameterCount,
                bindingToken,
                bindingTokenIssued,
                bindingReady,
                invocationEligible,
                RuntimeInvocationEnabled,
                invocationGateOpen,
                invocationGateBlocked,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static string CreateBindingToken(OpenACAINativeLifecycleInvocationRequestSnapshot request)
        {
            var canonical = string.Join("|", new[]
            {
                "openacai-native-lifecycle-binding-v1",
                NormalizeTokenPart(request.ModId),
                NormalizeTokenPart(request.HookName),
                NormalizeTokenPart(request.InvocationKind),
                NormalizeTokenPart(request.EntrypointTypeName),
                NormalizeTokenPart(request.EntrypointMethodName),
                NormalizeTokenPart(request.HookMethodName),
                NormalizeTokenPart(request.HookMethodSource),
                Math.Max(0, request.HookMethodParameterCount).ToString()
            });
            using (var sha = SHA256.Create())
            {
                var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
                return "openacai-bind-v1:" + hash.Substring(0, 32);
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Adapter",
                "OpenACAI-owned native lifecycle invocation adapter diagnostics.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateBindingSection(snapshot),
                    CreateTokenSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleInvocationAdapterSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Invocation Adapter",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle invocation adapter snapshot."),
                    Row("Binding", snapshot.BindingEnabled ? "Enabled" : "Disabled", snapshot.BindingEnabled ? "READY" : "CHECK", "Invocation requests are bound to diagnostic tokens without invoking mod code."),
                    Row("Runtime Invocation", snapshot.RuntimeInvocationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", "Native lifecycle method invocation remains disabled until verified in game."),
                    Row("Bindings", snapshot.BindingCount.ToString(), snapshot.BindingIssueCount == 0 ? "READY" : "CHECK", snapshot.BindingReadyCount + " binding-ready request(s); " + snapshot.BindingIssueCount + " issue(s)."),
                    Row("Tokens", snapshot.BindingTokenIssuedCount.ToString(), snapshot.BindingIssueCount == 0 ? "READY" : "CHECK", snapshot.InvocationEligibleCount + " token(s) eligible for runtime invocation."),
                    Row("Invocation Gate", snapshot.InvocationGateOpenCount + " open", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", snapshot.InvocationGateBlockedCount + " binding(s) blocked by the disabled runtime invocation gate."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle invocation adapter binding.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateBindingSection(OpenACAINativeLifecycleInvocationAdapterSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Bindings", snapshot.BindingCount.ToString(), snapshot.BindingIssueCount == 0 ? "READY" : "CHECK", snapshot.BindingTokenIssuedCount + " diagnostic token(s) issued.")
            };

            foreach (var binding in snapshot.Bindings.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(binding.DisplayName) ? binding.ModId : binding.DisplayName,
                    binding.HookName,
                    binding.Status,
                    binding.Detail + " Method " + (string.IsNullOrWhiteSpace(binding.HookMethodName) ? "none" : binding.HookMethodName) + "; token " + (binding.BindingTokenIssued ? "issued" : "none") + "."));
            }

            if (snapshot.BindingCount == 0)
            {
                rows.Add(Row("No Bindings", "0", "INFO", "No Modstoke-native lifecycle invocation bindings were created."));
            }

            return new OpenACAIMenuSectionSnapshot("Invocation Bindings", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateTokenSection(OpenACAINativeLifecycleInvocationAdapterSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Binding Tokens", snapshot.BindingTokenIssuedCount.ToString(), snapshot.BindingIssueCount == 0 ? "READY" : "CHECK", "Diagnostic binding tokens are stable for matching mod/hook/method metadata.")
            };

            foreach (var binding in snapshot.Bindings.Where(binding => binding.BindingTokenIssued).Take(10))
            {
                rows.Add(Row(
                    binding.HookName,
                    binding.BindingToken,
                    binding.Status,
                    binding.InvocationEligible ? "Runtime invocation is eligible." : "Runtime invocation remains blocked."));
            }

            if (snapshot.BindingTokenIssuedCount == 0)
            {
                rows.Add(Row("No Tokens", "0", "INFO", "No diagnostic binding tokens were issued."));
            }

            return new OpenACAIMenuSectionSnapshot("Binding Tokens", rows);
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

        private static string NormalizeTokenPart(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleInvocationAdapter;
            public string DisplayName => "Modstoke Native Adapter";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 710;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle invocation adapter diagnostics.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleInvocationAdapter.CreateContent();
            }
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationAdapterSnapshot
    {
        internal OpenACAINativeLifecycleInvocationAdapterSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool bindingEnabled,
            bool runtimeInvocationEnabled,
            int candidateCount,
            int dispatchTargetCount,
            int dispatchIntentCount,
            int queuedDispatchIntentCount,
            int invocationRequestCount,
            int adapterReadyCount,
            int invocationIssueCount,
            int bridgeInvocationGateOpenCount,
            int bridgeInvocationGateBlockedCount,
            int bridgePlanRequestCount,
            int bridgeAcceptedPlanCount,
            int bridgeRejectedPlanCount,
            int libraryExcludedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationBindingSnapshot> bindings,
            int bindingReadyCount,
            int bindingIssueCount,
            int bindingTokenIssuedCount,
            int invocationEligibleCount,
            int invocationGateOpenCount,
            int invocationGateBlockedCount)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleInvocationAdapter.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            BindingEnabled = bindingEnabled;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            CandidateCount = Math.Max(0, candidateCount);
            DispatchTargetCount = Math.Max(0, dispatchTargetCount);
            DispatchIntentCount = Math.Max(0, dispatchIntentCount);
            QueuedDispatchIntentCount = Math.Max(0, queuedDispatchIntentCount);
            InvocationRequestCount = Math.Max(0, invocationRequestCount);
            AdapterReadyCount = Math.Max(0, adapterReadyCount);
            InvocationIssueCount = Math.Max(0, invocationIssueCount);
            BridgeInvocationGateOpenCount = Math.Max(0, bridgeInvocationGateOpenCount);
            BridgeInvocationGateBlockedCount = Math.Max(0, bridgeInvocationGateBlockedCount);
            BridgePlanRequestCount = Math.Max(0, bridgePlanRequestCount);
            BridgeAcceptedPlanCount = Math.Max(0, bridgeAcceptedPlanCount);
            BridgeRejectedPlanCount = Math.Max(0, bridgeRejectedPlanCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            Bindings = bindings ?? Array.Empty<OpenACAINativeLifecycleInvocationBindingSnapshot>();
            BindingCount = Bindings.Count;
            BindingReadyCount = Math.Max(0, bindingReadyCount);
            BindingIssueCount = Math.Max(0, bindingIssueCount);
            BindingTokenIssuedCount = Math.Max(0, bindingTokenIssuedCount);
            InvocationEligibleCount = Math.Max(0, invocationEligibleCount);
            InvocationGateOpenCount = Math.Max(0, invocationGateOpenCount);
            InvocationGateBlockedCount = Math.Max(0, invocationGateBlockedCount);
            BindingTokens = Bindings
                .Where(binding => binding != null && binding.BindingTokenIssued)
                .Select(binding => binding.BindingToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool BindingEnabled { get; }
        public bool RuntimeInvocationEnabled { get; }
        public int CandidateCount { get; }
        public int DispatchTargetCount { get; }
        public int DispatchIntentCount { get; }
        public int QueuedDispatchIntentCount { get; }
        public int InvocationRequestCount { get; }
        public int AdapterReadyCount { get; }
        public int InvocationIssueCount { get; }
        public int BridgeInvocationGateOpenCount { get; }
        public int BridgeInvocationGateBlockedCount { get; }
        public int BridgePlanRequestCount { get; }
        public int BridgeAcceptedPlanCount { get; }
        public int BridgeRejectedPlanCount { get; }
        public int LibraryExcludedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationBindingSnapshot> Bindings { get; }
        public int BindingCount { get; }
        public int BindingReadyCount { get; }
        public int BindingIssueCount { get; }
        public int BindingTokenIssuedCount { get; }
        public int InvocationEligibleCount { get; }
        public int InvocationGateOpenCount { get; }
        public int InvocationGateBlockedCount { get; }
        public IReadOnlyList<string> BindingTokens { get; }

        internal static OpenACAINativeLifecycleInvocationAdapterSnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleInvocationAdapterSnapshot(
                OpenACAINativeLifecycleInvocationAdapter.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle invocation adapter has not refreshed yet.",
                true,
                OpenACAINativeLifecycleInvocationAdapter.RuntimeInvocationEnabled,
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
                Array.Empty<OpenACAINativeLifecycleInvocationBindingSnapshot>(),
                0,
                0,
                0,
                0,
                0,
                0);
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationBindingSnapshot
    {
        internal OpenACAINativeLifecycleInvocationBindingSnapshot(
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
            bool bindingTokenIssued,
            bool bindingReady,
            bool invocationEligible,
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
            BindingToken = bindingToken ?? "";
            BindingTokenIssued = bindingTokenIssued;
            BindingReady = bindingReady;
            InvocationEligible = invocationEligible;
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
        public string BindingToken { get; }
        public bool BindingTokenIssued { get; }
        public bool BindingReady { get; }
        public bool InvocationEligible { get; }
        public bool RuntimeInvocationEnabled { get; }
        public bool InvocationGateOpen { get; }
        public bool InvocationGateBlocked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
