// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle invocation token resolver. This maps
// diagnostic binding tokens back to sanitized invocation metadata without
// invoking mod code.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleInvocationTokenResolver
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationTokenResolver";
        public const int MaxResolutionCount = OpenACAINativeLifecycleInvocationAdapter.MaxBindingCount;
        public const int MaxLookupResultCount = 128;

        private static readonly bool RuntimeInvocationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoAuditedLookupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<OpenACAINativeLifecycleInvocationTokenLookupSnapshot> RecordedLookupResults = new List<OpenACAINativeLifecycleInvocationTokenLookupSnapshot>();
        private static OpenACAINativeLifecycleInvocationTokenResolverSnapshot _state = OpenACAINativeLifecycleInvocationTokenResolverSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _lookupRequestCount;
        private static int _acceptedLookupCount;
        private static int _rejectedLookupCount;
        private static string _lastLookupToken = "";
        private static string _lastLookupStatus = "INFO";
        private static string _lastLookupDetail = "No native lifecycle invocation token lookup has been requested.";
        private static string _lastLookupReason = "none";

        public static OpenACAINativeLifecycleInvocationTokenResolverSnapshot State
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
        public static bool ResolvingEnabled => true;
        public static bool RuntimeInvocationEnabled => RuntimeInvocationEnabledValue;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationTokenResolutionSnapshot> TokenResolutions => State.TokenResolutions;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationTokenLookupSnapshot> LookupResults => State.LookupResults;

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationTokenResolutionSnapshot> GetTokenResolutions()
        {
            return TokenResolutions;
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationTokenLookupSnapshot> GetLookupResults()
        {
            return LookupResults;
        }

        public static OpenACAINativeLifecycleInvocationTokenLookupSnapshot LookupBindingToken(string bindingToken)
        {
            var lookup = CreateLookup(State, bindingToken);
            RecordLookup(lookup);
            Refresh();
            return lookup;
        }

        public static bool TryResolveBindingToken(string bindingToken, out OpenACAINativeLifecycleInvocationTokenResolutionSnapshot resolution)
        {
            var lookup = LookupBindingToken(bindingToken);
            resolution = lookup == null ? null : lookup.Resolution;
            return lookup != null && lookup.Success && resolution != null && resolution.TokenResolved;
        }

        public static OpenACAINativeLifecycleInvocationTokenResolutionSnapshot ResolveBindingToken(string bindingToken)
        {
            var lookup = LookupBindingToken(bindingToken);
            return lookup == null ? null : lookup.Resolution;
        }

        public static OpenACAINativeLifecycleInvocationTokenResolverSnapshot Refresh()
        {
            return Refresh(OpenACAINativeLifecycleInvocationAdapter.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleInvocationTokenResolverSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle invocation token resolver provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleInvocationTokenResolverSnapshot Refresh(OpenACAINativeLifecycleInvocationAdapterSnapshot adapter)
        {
            var snapshot = CreateSnapshot(adapter);
            if (RecordAutomaticLookupAudits(snapshot))
            {
                snapshot = CreateSnapshot(adapter);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleInvocationTokenResolverSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleInvocationTokenResolverSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static OpenACAINativeLifecycleInvocationTokenResolverSnapshot CreateSnapshot(OpenACAINativeLifecycleInvocationAdapterSnapshot adapter)
        {
            adapter = adapter ?? OpenACAINativeLifecycleInvocationAdapterSnapshot.CreateUnknown();
            var resolutions = adapter.Bindings
                .Where(binding => binding != null)
                .OrderBy(binding => binding.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(binding => binding.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(binding => binding.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(CreateResolution)
                .Take(MaxResolutionCount)
                .ToArray();

            var resolvedCount = resolutions.Count(resolution => resolution.TokenResolved);
            var tokenIssueCount = resolutions.Count(resolution => resolution.Issue);
            var invocationEligibleCount = resolutions.Count(resolution => resolution.InvocationEligible);
            var invocationGateOpenCount = resolutions.Count(resolution => resolution.InvocationGateOpen);
            var invocationGateBlockedCount = resolutions.Count(resolution => resolution.InvocationGateBlocked);
            var status = tokenIssueCount == 0 ? "READY" : "CHECK";
            var detail = tokenIssueCount == 0
                ? "Modstoke native lifecycle invocation token resolver has indexed diagnostic binding tokens; runtime invocation remains explicitly gated."
                : tokenIssueCount + " native lifecycle invocation token resolution issue(s) were found.";

            int lookupRequestCount;
            int acceptedLookupCount;
            int rejectedLookupCount;
            string lastLookupToken;
            string lastLookupStatus;
            string lastLookupDetail;
            string lastLookupReason;
            OpenACAINativeLifecycleInvocationTokenLookupSnapshot[] lookupResults;
            lock (SyncRoot)
            {
                lookupRequestCount = _lookupRequestCount;
                acceptedLookupCount = _acceptedLookupCount;
                rejectedLookupCount = _rejectedLookupCount;
                lastLookupToken = _lastLookupToken ?? "";
                lastLookupStatus = _lastLookupStatus ?? "";
                lastLookupDetail = _lastLookupDetail ?? "";
                lastLookupReason = _lastLookupReason ?? "";
                lookupResults = RecordedLookupResults.ToArray();
            }

            return new OpenACAINativeLifecycleInvocationTokenResolverSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                adapter.Ready,
                status,
                detail,
                ResolvingEnabled,
                RuntimeInvocationEnabled,
                adapter.CandidateCount,
                adapter.DispatchTargetCount,
                adapter.DispatchIntentCount,
                adapter.QueuedDispatchIntentCount,
                adapter.InvocationRequestCount,
                adapter.BindingCount,
                adapter.BindingReadyCount,
                adapter.BindingIssueCount,
                adapter.BindingTokenIssuedCount,
                adapter.InvocationEligibleCount,
                adapter.InvocationGateOpenCount,
                adapter.InvocationGateBlockedCount,
                adapter.LibraryExcludedCount,
                resolutions,
                resolvedCount,
                tokenIssueCount,
                invocationEligibleCount,
                invocationGateOpenCount,
                invocationGateBlockedCount,
                lookupResults,
                lookupRequestCount,
                acceptedLookupCount,
                rejectedLookupCount,
                lastLookupToken,
                lastLookupStatus,
                lastLookupDetail,
                lastLookupReason);
        }

        private static bool RecordAutomaticLookupAudits(OpenACAINativeLifecycleInvocationTokenResolverSnapshot snapshot)
        {
            if (snapshot == null || snapshot.TokenResolvedCount == 0)
            {
                lock (SyncRoot)
                {
                    return AutoAuditedLookupKeys.RemoveWhere(key => !string.IsNullOrWhiteSpace(key)) > 0;
                }
            }

            var currentTokens = snapshot.TokenResolutions
                .Where(resolution => resolution != null && resolution.TokenResolved && !string.IsNullOrWhiteSpace(resolution.BindingToken))
                .Select(resolution => NormalizeToken(resolution.BindingToken))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentKeySet = new HashSet<string>(currentTokens.Select(token => "token:" + token), StringComparer.OrdinalIgnoreCase);
            var tokensToLookup = new List<string>();

            lock (SyncRoot)
            {
                AutoAuditedLookupKeys.RemoveWhere(key => !currentKeySet.Contains(key));
                foreach (var token in currentTokens)
                {
                    var key = "token:" + token;
                    if (AutoAuditedLookupKeys.Add(key))
                    {
                        tokensToLookup.Add(token);
                    }
                }
            }

            foreach (var token in tokensToLookup)
            {
                RecordLookup(CreateLookup(snapshot, token));
            }

            return tokensToLookup.Count > 0;
        }

        private static OpenACAINativeLifecycleInvocationTokenLookupSnapshot CreateLookup(OpenACAINativeLifecycleInvocationTokenResolverSnapshot snapshot, string bindingToken)
        {
            snapshot = snapshot ?? OpenACAINativeLifecycleInvocationTokenResolverSnapshot.CreateUnknown();
            var normalizedToken = NormalizeToken(bindingToken);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Native lifecycle invocation token lookup is invalid.";
            var success = false;
            var invocationEligible = false;
            var invocationGateOpen = false;
            var invocationGateBlocked = false;
            OpenACAINativeLifecycleInvocationTokenResolutionSnapshot resolution = null;

            if (string.IsNullOrWhiteSpace(normalizedToken))
            {
                reason = "missing-binding-token";
                detail = "A native lifecycle invocation binding token is required.";
            }
            else
            {
                resolution = snapshot.TokenResolutions.FirstOrDefault(candidate =>
                    candidate != null
                    && string.Equals(candidate.BindingToken, normalizedToken, StringComparison.OrdinalIgnoreCase)
                    && candidate.TokenResolved);
                if (resolution == null)
                {
                    reason = "binding-token-not-resolved";
                    detail = "No resolved native lifecycle invocation token matched '" + normalizedToken + "'.";
                }
                else
                {
                    success = true;
                    invocationGateOpen = RuntimeInvocationEnabled;
                    invocationGateBlocked = !RuntimeInvocationEnabled;
                    invocationEligible = RuntimeInvocationEnabled;
                    if (RuntimeInvocationEnabled)
                    {
                        status = "READY";
                        reason = "ready";
                        detail = "OpenACAI resolved the native lifecycle invocation token for runtime invocation.";
                    }
                    else
                    {
                        status = "DEFERRED";
                        reason = "runtime-invocation-disabled";
                        detail = "OpenACAI resolved the native lifecycle invocation token; runtime invocation remains disabled.";
                    }
                }
            }

            return new OpenACAINativeLifecycleInvocationTokenLookupSnapshot(
                normalizedToken,
                success,
                resolution != null && resolution.TokenResolved,
                resolution == null ? "" : resolution.ModId,
                resolution == null ? "" : resolution.DisplayName,
                resolution == null ? "" : resolution.HookName,
                resolution == null ? "" : resolution.InvocationKind,
                resolution == null ? "" : resolution.EntrypointTypeName,
                resolution == null ? "" : resolution.EntrypointMethodName,
                resolution == null ? "" : resolution.HookMethodName,
                resolution == null ? "" : resolution.HookMethodSource,
                resolution == null ? 0 : resolution.HookMethodParameterCount,
                RuntimeInvocationEnabled,
                invocationEligible,
                invocationGateOpen,
                invocationGateBlocked,
                status,
                reason,
                detail,
                resolution,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static void RecordLookup(OpenACAINativeLifecycleInvocationTokenLookupSnapshot lookup)
        {
            lock (SyncRoot)
            {
                _lookupRequestCount++;
                if (lookup != null && lookup.Success)
                {
                    _acceptedLookupCount++;
                }
                else
                {
                    _rejectedLookupCount++;
                }

                _lastLookupToken = lookup == null ? "" : lookup.BindingToken;
                _lastLookupStatus = lookup == null ? "CHECK" : lookup.Status;
                _lastLookupDetail = lookup == null ? "Native lifecycle invocation token lookup was null." : lookup.Detail;
                _lastLookupReason = lookup == null ? "null-lookup" : lookup.ReasonCode;
                if (lookup != null)
                {
                    RecordedLookupResults.Add(lookup);
                    while (RecordedLookupResults.Count > MaxLookupResultCount)
                    {
                        RecordedLookupResults.RemoveAt(0);
                    }
                }
            }
        }

        private static OpenACAINativeLifecycleInvocationTokenResolutionSnapshot CreateResolution(OpenACAINativeLifecycleInvocationBindingSnapshot binding)
        {
            binding = binding ?? new OpenACAINativeLifecycleInvocationBindingSnapshot(
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
                "missing-invocation-binding",
                "OpenACAI could not create a token resolution because the invocation binding was missing.",
                DateTimeOffset.UtcNow.ToString("O"));

            var token = NormalizeToken(binding.BindingToken);
            var tokenResolved = binding.BindingReady && binding.BindingTokenIssued && !string.IsNullOrWhiteSpace(token) && !binding.Issue;
            var invocationGateOpen = tokenResolved && RuntimeInvocationEnabled;
            var invocationGateBlocked = tokenResolved && !RuntimeInvocationEnabled;
            var invocationEligible = tokenResolved && RuntimeInvocationEnabled;
            var status = tokenResolved ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = tokenResolved
                ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled"
                : !string.IsNullOrWhiteSpace(binding.IssueCode) ? binding.IssueCode : "binding-token-not-issued";
            var detail = tokenResolved
                ? RuntimeInvocationEnabled
                    ? "OpenACAI resolved this native lifecycle invocation token for runtime invocation."
                    : "OpenACAI resolved this diagnostic binding token; runtime invocation remains disabled."
                : "OpenACAI could not resolve a diagnostic binding token because the binding is not ready.";

            return new OpenACAINativeLifecycleInvocationTokenResolutionSnapshot(
                binding.ModId,
                binding.DisplayName,
                binding.HookName,
                binding.InvocationKind,
                binding.EntrypointTypeName,
                binding.EntrypointMethodName,
                binding.HookMethodName,
                binding.HookMethodSource,
                binding.HookMethodParameterCount,
                token,
                binding.BindingTokenIssued,
                tokenResolved,
                binding.BindingReady,
                invocationEligible,
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
                "Modstoke Native Tokens",
                "OpenACAI-owned native lifecycle invocation token resolver diagnostics.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateResolutionSection(snapshot),
                    CreateLookupSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleInvocationTokenResolverSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Invocation Tokens",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle invocation token resolver snapshot."),
                    Row("Resolving", snapshot.ResolvingEnabled ? "Enabled" : "Disabled", snapshot.ResolvingEnabled ? "READY" : "CHECK", "Diagnostic binding tokens resolve to sanitized metadata without invoking mod code."),
                    Row("Runtime Invocation", snapshot.RuntimeInvocationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", "Native lifecycle method invocation remains disabled until verified in game."),
                    Row("Tokens", snapshot.TokenResolvedCount + "/" + snapshot.BindingTokenIssuedCount, snapshot.TokenIssueCount == 0 ? "READY" : "CHECK", snapshot.TokenIssueCount + " token resolution issue(s)."),
                    Row("Lookups", snapshot.LookupRequestCount.ToString(), snapshot.RejectedLookupCount == 0 ? "READY" : "CHECK", snapshot.AcceptedLookupCount + " accepted, " + snapshot.RejectedLookupCount + " rejected."),
                    Row("Invocation Gate", snapshot.InvocationGateOpenCount + " open", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", snapshot.InvocationGateBlockedCount + " resolved token(s) blocked by the disabled runtime invocation gate."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle invocation token resolution.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateResolutionSection(OpenACAINativeLifecycleInvocationTokenResolverSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Resolutions", snapshot.TokenResolutionCount.ToString(), snapshot.TokenIssueCount == 0 ? "READY" : "CHECK", snapshot.TokenResolvedCount + " diagnostic token(s) resolved.")
            };

            foreach (var resolution in snapshot.TokenResolutions.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(resolution.DisplayName) ? resolution.ModId : resolution.DisplayName,
                    string.IsNullOrWhiteSpace(resolution.HookName) ? "none" : resolution.HookName,
                    resolution.Status,
                    resolution.Detail + " Token " + (resolution.TokenResolved ? resolution.BindingToken : "none") + "."));
            }

            if (snapshot.TokenResolutionCount == 0)
            {
                rows.Add(Row("No Tokens", "0", "INFO", "No Modstoke-native lifecycle invocation tokens were indexed."));
            }

            return new OpenACAIMenuSectionSnapshot("Token Resolutions", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateLookupSection(OpenACAINativeLifecycleInvocationTokenResolverSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Lookup Results", snapshot.LookupResultCount.ToString(), snapshot.RejectedLookupCount == 0 ? "READY" : "CHECK", snapshot.AcceptedLookupCount + " accepted lookup(s)."),
                Row("Last Lookup", string.IsNullOrWhiteSpace(snapshot.LastLookupToken) ? "none" : snapshot.LastLookupToken, snapshot.LastLookupStatus, snapshot.LastLookupDetail)
            };

            foreach (var lookup in snapshot.LookupResults.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(lookup.HookName) ? "Token" : lookup.HookName,
                    lookup.BindingToken,
                    lookup.Status,
                    lookup.Detail));
            }

            if (snapshot.LookupResultCount == 0)
            {
                rows.Add(Row("No Lookups", "0", "INFO", "No native lifecycle invocation token lookups were recorded."));
            }

            return new OpenACAIMenuSectionSnapshot("Lookup Audit", rows);
        }

        private static string NormalizeToken(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleInvocationTokenResolver;
            public string DisplayName => "Modstoke Native Tokens";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 711;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle invocation token resolver diagnostics.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleInvocationTokenResolver.CreateContent();
            }
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationTokenResolverSnapshot
    {
        internal OpenACAINativeLifecycleInvocationTokenResolverSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool resolvingEnabled,
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
            int adapterInvocationEligibleCount,
            int adapterInvocationGateOpenCount,
            int adapterInvocationGateBlockedCount,
            int libraryExcludedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationTokenResolutionSnapshot> tokenResolutions,
            int tokenResolvedCount,
            int tokenIssueCount,
            int invocationEligibleCount,
            int invocationGateOpenCount,
            int invocationGateBlockedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationTokenLookupSnapshot> lookupResults,
            int lookupRequestCount,
            int acceptedLookupCount,
            int rejectedLookupCount,
            string lastLookupToken,
            string lastLookupStatus,
            string lastLookupDetail,
            string lastLookupReason)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleInvocationTokenResolver.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            ResolvingEnabled = resolvingEnabled;
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
            AdapterInvocationEligibleCount = Math.Max(0, adapterInvocationEligibleCount);
            AdapterInvocationGateOpenCount = Math.Max(0, adapterInvocationGateOpenCount);
            AdapterInvocationGateBlockedCount = Math.Max(0, adapterInvocationGateBlockedCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            TokenResolutions = tokenResolutions ?? Array.Empty<OpenACAINativeLifecycleInvocationTokenResolutionSnapshot>();
            TokenResolutionCount = TokenResolutions.Count;
            TokenResolvedCount = Math.Max(0, tokenResolvedCount);
            TokenIssueCount = Math.Max(0, tokenIssueCount);
            InvocationEligibleCount = Math.Max(0, invocationEligibleCount);
            InvocationGateOpenCount = Math.Max(0, invocationGateOpenCount);
            InvocationGateBlockedCount = Math.Max(0, invocationGateBlockedCount);
            LookupResults = lookupResults ?? Array.Empty<OpenACAINativeLifecycleInvocationTokenLookupSnapshot>();
            LookupResultCount = LookupResults.Count;
            LookupRequestCount = Math.Max(0, lookupRequestCount);
            AcceptedLookupCount = Math.Max(0, acceptedLookupCount);
            RejectedLookupCount = Math.Max(0, rejectedLookupCount);
            LastLookupToken = lastLookupToken ?? "";
            LastLookupStatus = lastLookupStatus ?? "";
            LastLookupDetail = lastLookupDetail ?? "";
            LastLookupReason = lastLookupReason ?? "";
            ResolvedBindingTokens = TokenResolutions
                .Where(resolution => resolution != null && resolution.TokenResolved)
                .Select(resolution => resolution.BindingToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool ResolvingEnabled { get; }
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
        public int AdapterInvocationEligibleCount { get; }
        public int AdapterInvocationGateOpenCount { get; }
        public int AdapterInvocationGateBlockedCount { get; }
        public int LibraryExcludedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationTokenResolutionSnapshot> TokenResolutions { get; }
        public int TokenResolutionCount { get; }
        public int TokenResolvedCount { get; }
        public int TokenIssueCount { get; }
        public int InvocationEligibleCount { get; }
        public int InvocationGateOpenCount { get; }
        public int InvocationGateBlockedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationTokenLookupSnapshot> LookupResults { get; }
        public int LookupResultCount { get; }
        public int LookupRequestCount { get; }
        public int AcceptedLookupCount { get; }
        public int RejectedLookupCount { get; }
        public string LastLookupToken { get; }
        public string LastLookupStatus { get; }
        public string LastLookupDetail { get; }
        public string LastLookupReason { get; }
        public IReadOnlyList<string> ResolvedBindingTokens { get; }

        internal static OpenACAINativeLifecycleInvocationTokenResolverSnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleInvocationTokenResolverSnapshot(
                OpenACAINativeLifecycleInvocationTokenResolver.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle invocation token resolver has not refreshed yet.",
                true,
                OpenACAINativeLifecycleInvocationTokenResolver.RuntimeInvocationEnabled,
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
                Array.Empty<OpenACAINativeLifecycleInvocationTokenResolutionSnapshot>(),
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleInvocationTokenLookupSnapshot>(),
                0,
                0,
                0,
                "",
                "INFO",
                "No native lifecycle invocation token lookup has been requested.",
                "none");
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationTokenLookupSnapshot
    {
        internal OpenACAINativeLifecycleInvocationTokenLookupSnapshot(
            string bindingToken,
            bool success,
            bool tokenResolved,
            string modId,
            string displayName,
            string hookName,
            string invocationKind,
            string entrypointTypeName,
            string entrypointMethodName,
            string hookMethodName,
            string hookMethodSource,
            int hookMethodParameterCount,
            bool runtimeInvocationEnabled,
            bool invocationEligible,
            bool invocationGateOpen,
            bool invocationGateBlocked,
            string status,
            string reasonCode,
            string detail,
            OpenACAINativeLifecycleInvocationTokenResolutionSnapshot resolution,
            string generatedUtc)
        {
            BindingToken = bindingToken ?? "";
            Success = success;
            TokenResolved = tokenResolved;
            ModId = modId ?? "";
            DisplayName = displayName ?? "";
            HookName = hookName ?? "";
            InvocationKind = invocationKind ?? "";
            EntrypointTypeName = entrypointTypeName ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            HookMethodName = hookMethodName ?? "";
            HookMethodSource = hookMethodSource ?? "";
            HookMethodParameterCount = Math.Max(0, hookMethodParameterCount);
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            InvocationEligible = invocationEligible;
            InvocationGateOpen = invocationGateOpen;
            InvocationGateBlocked = invocationGateBlocked;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            Resolution = resolution;
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public string BindingToken { get; }
        public bool Success { get; }
        public bool TokenResolved { get; }
        public string ModId { get; }
        public string DisplayName { get; }
        public string HookName { get; }
        public string InvocationKind { get; }
        public string EntrypointTypeName { get; }
        public string EntrypointMethodName { get; }
        public string HookMethodName { get; }
        public string HookMethodSource { get; }
        public int HookMethodParameterCount { get; }
        public bool RuntimeInvocationEnabled { get; }
        public bool InvocationEligible { get; }
        public bool InvocationGateOpen { get; }
        public bool InvocationGateBlocked { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public OpenACAINativeLifecycleInvocationTokenResolutionSnapshot Resolution { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleInvocationTokenResolutionSnapshot
    {
        internal OpenACAINativeLifecycleInvocationTokenResolutionSnapshot(
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
            bool tokenResolved,
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
            TokenResolved = tokenResolved;
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
        public bool TokenResolved { get; }
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
