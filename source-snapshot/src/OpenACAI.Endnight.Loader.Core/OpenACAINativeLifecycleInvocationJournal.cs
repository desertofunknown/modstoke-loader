// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native lifecycle invocation journal. This consumes resolved
// diagnostic invocation tokens and publishes replay intent metadata without
// invoking mod code.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeLifecycleInvocationJournal
    {
        public const string ServiceKey = "openacai.loader.nativeLifecycleInvocationJournal";
        public const int MaxJournalEntryCount = OpenACAINativeLifecycleInvocationTokenResolver.MaxResolutionCount;
        public const int MaxReplayPlanCount = 128;

        private static readonly bool RuntimeInvocationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoPlannedReplayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<OpenACAINativeLifecycleInvocationReplayPlanSnapshot> RecordedReplayPlans = new List<OpenACAINativeLifecycleInvocationReplayPlanSnapshot>();
        private static OpenACAINativeLifecycleInvocationJournalSnapshot _state = OpenACAINativeLifecycleInvocationJournalSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _replayPlanRequestCount;
        private static int _acceptedReplayPlanCount;
        private static int _rejectedReplayPlanCount;
        private static string _lastReplayToken = "";
        private static string _lastReplayStatus = "INFO";
        private static string _lastReplayDetail = "No native lifecycle invocation replay plan has been requested.";
        private static string _lastReplayReason = "none";

        public static OpenACAINativeLifecycleInvocationJournalSnapshot State
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
        public static bool JournalingEnabled => true;
        public static bool RuntimeInvocationEnabled => RuntimeInvocationEnabledValue;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationJournalEntrySnapshot> JournalEntries => State.JournalEntries;
        public static IReadOnlyList<OpenACAINativeLifecycleInvocationReplayPlanSnapshot> ReplayPlans => State.ReplayPlans;

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationJournalEntrySnapshot> GetJournalEntries()
        {
            return JournalEntries;
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationReplayPlanSnapshot> GetReplayPlans()
        {
            return ReplayPlans;
        }

        public static OpenACAINativeLifecycleInvocationReplayPlanSnapshot PlanReplay(string bindingToken)
        {
            var plan = CreateReplayPlan(State, bindingToken);
            RecordReplayPlan(plan);
            Refresh();
            return plan;
        }

        public static OpenACAINativeLifecycleInvocationJournalSnapshot Refresh()
        {
            return Refresh(OpenACAINativeLifecycleInvocationTokenResolver.State);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeLifecycleInvocationJournalSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native lifecycle invocation journal provider could not be registered.");
            }
        }

        internal static OpenACAINativeLifecycleInvocationJournalSnapshot Refresh(OpenACAINativeLifecycleInvocationTokenResolverSnapshot resolver)
        {
            var snapshot = CreateSnapshot(resolver);
            if (RecordAutomaticReplayPlans(snapshot))
            {
                snapshot = CreateSnapshot(resolver);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeLifecycleInvocationJournalSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeLifecycleInvocationJournalSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static OpenACAINativeLifecycleInvocationJournalSnapshot CreateSnapshot(OpenACAINativeLifecycleInvocationTokenResolverSnapshot resolver)
        {
            resolver = resolver ?? OpenACAINativeLifecycleInvocationTokenResolverSnapshot.CreateUnknown();
            var sequence = 0;
            var entries = resolver.TokenResolutions
                .Where(resolution => resolution != null)
                .OrderBy(resolution => resolution.HookName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(resolution => resolution.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(resolution => resolution.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(resolution => CreateEntry(++sequence, resolution))
                .Take(MaxJournalEntryCount)
                .ToArray();

            var journaledCount = entries.Count(entry => entry.Journaled);
            var journalIssueCount = entries.Count(entry => entry.Issue);
            var replayEligibleCount = entries.Count(entry => entry.ReplayEligible);
            var replayGateOpenCount = entries.Count(entry => entry.ReplayGateOpen);
            var replayGateBlockedCount = entries.Count(entry => entry.ReplayGateBlocked);
            var status = journalIssueCount == 0 ? "READY" : "CHECK";
            var detail = journalIssueCount == 0
                ? "Modstoke native lifecycle invocation journal has recorded replay intents; runtime invocation remains explicitly gated."
                : journalIssueCount + " native lifecycle invocation journal issue(s) were found.";

            int replayPlanRequestCount;
            int acceptedReplayPlanCount;
            int rejectedReplayPlanCount;
            string lastReplayToken;
            string lastReplayStatus;
            string lastReplayDetail;
            string lastReplayReason;
            OpenACAINativeLifecycleInvocationReplayPlanSnapshot[] replayPlans;
            lock (SyncRoot)
            {
                replayPlanRequestCount = _replayPlanRequestCount;
                acceptedReplayPlanCount = _acceptedReplayPlanCount;
                rejectedReplayPlanCount = _rejectedReplayPlanCount;
                lastReplayToken = _lastReplayToken ?? "";
                lastReplayStatus = _lastReplayStatus ?? "";
                lastReplayDetail = _lastReplayDetail ?? "";
                lastReplayReason = _lastReplayReason ?? "";
                replayPlans = RecordedReplayPlans.ToArray();
            }

            return new OpenACAINativeLifecycleInvocationJournalSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                resolver.Ready,
                status,
                detail,
                JournalingEnabled,
                RuntimeInvocationEnabled,
                resolver.CandidateCount,
                resolver.DispatchTargetCount,
                resolver.DispatchIntentCount,
                resolver.QueuedDispatchIntentCount,
                resolver.InvocationRequestCount,
                resolver.BindingCount,
                resolver.BindingReadyCount,
                resolver.BindingIssueCount,
                resolver.BindingTokenIssuedCount,
                resolver.TokenResolutionCount,
                resolver.TokenResolvedCount,
                resolver.TokenIssueCount,
                resolver.LookupRequestCount,
                resolver.AcceptedLookupCount,
                resolver.RejectedLookupCount,
                resolver.LibraryExcludedCount,
                entries,
                journaledCount,
                journalIssueCount,
                replayEligibleCount,
                replayGateOpenCount,
                replayGateBlockedCount,
                replayPlans,
                replayPlanRequestCount,
                acceptedReplayPlanCount,
                rejectedReplayPlanCount,
                lastReplayToken,
                lastReplayStatus,
                lastReplayDetail,
                lastReplayReason);
        }

        private static OpenACAINativeLifecycleInvocationJournalEntrySnapshot CreateEntry(
            int sequenceNumber,
            OpenACAINativeLifecycleInvocationTokenResolutionSnapshot resolution)
        {
            resolution = resolution ?? new OpenACAINativeLifecycleInvocationTokenResolutionSnapshot(
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
                RuntimeInvocationEnabled,
                false,
                false,
                "CHECK",
                "missing-token-resolution",
                "OpenACAI could not journal a native lifecycle invocation because token resolution metadata was missing.",
                DateTimeOffset.UtcNow.ToString("O"));

            var tokenResolved = resolution.TokenResolved && !string.IsNullOrWhiteSpace(resolution.BindingToken) && !resolution.Issue;
            var journaled = tokenResolved;
            var replayGateOpen = journaled && RuntimeInvocationEnabled;
            var replayGateBlocked = journaled && !RuntimeInvocationEnabled;
            var replayEligible = journaled && RuntimeInvocationEnabled;
            var status = journaled ? RuntimeInvocationEnabled ? "READY" : "DEFERRED" : "CHECK";
            var issueCode = journaled
                ? RuntimeInvocationEnabled ? "" : "runtime-invocation-disabled"
                : !string.IsNullOrWhiteSpace(resolution.IssueCode) ? resolution.IssueCode : "token-not-resolved";
            var detail = journaled
                ? RuntimeInvocationEnabled
                    ? "OpenACAI journaled this native lifecycle invocation for runtime replay."
                    : "OpenACAI journaled this native lifecycle invocation as metadata-only replay evidence; runtime invocation remains disabled."
                : "OpenACAI could not journal this native lifecycle invocation because token resolution metadata is not ready.";

            return new OpenACAINativeLifecycleInvocationJournalEntrySnapshot(
                sequenceNumber,
                resolution.ModId,
                resolution.DisplayName,
                resolution.HookName,
                resolution.InvocationKind,
                resolution.EntrypointTypeName,
                resolution.EntrypointMethodName,
                resolution.HookMethodName,
                resolution.HookMethodSource,
                resolution.HookMethodParameterCount,
                resolution.BindingToken,
                resolution.BindingTokenIssued,
                resolution.TokenResolved,
                resolution.BindingReady,
                journaled,
                replayEligible,
                RuntimeInvocationEnabled,
                replayGateOpen,
                replayGateBlocked,
                status,
                issueCode,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static bool RecordAutomaticReplayPlans(OpenACAINativeLifecycleInvocationJournalSnapshot snapshot)
        {
            if (snapshot == null || snapshot.JournaledEntryCount == 0)
            {
                lock (SyncRoot)
                {
                    return AutoPlannedReplayKeys.RemoveWhere(key => !string.IsNullOrWhiteSpace(key)) > 0;
                }
            }

            var currentTokens = snapshot.JournalEntries
                .Where(entry => entry != null && entry.Journaled && !string.IsNullOrWhiteSpace(entry.BindingToken))
                .Select(entry => NormalizeToken(entry.BindingToken))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentKeySet = new HashSet<string>(currentTokens.Select(token => "token:" + token), StringComparer.OrdinalIgnoreCase);
            var tokensToPlan = new List<string>();

            lock (SyncRoot)
            {
                AutoPlannedReplayKeys.RemoveWhere(key => !currentKeySet.Contains(key));
                foreach (var token in currentTokens)
                {
                    var key = "token:" + token;
                    if (AutoPlannedReplayKeys.Add(key))
                    {
                        tokensToPlan.Add(token);
                    }
                }
            }

            foreach (var token in tokensToPlan)
            {
                RecordReplayPlan(CreateReplayPlan(snapshot, token));
            }

            return tokensToPlan.Count > 0;
        }

        private static OpenACAINativeLifecycleInvocationReplayPlanSnapshot CreateReplayPlan(
            OpenACAINativeLifecycleInvocationJournalSnapshot snapshot,
            string bindingToken)
        {
            snapshot = snapshot ?? OpenACAINativeLifecycleInvocationJournalSnapshot.CreateUnknown();
            var normalizedToken = NormalizeToken(bindingToken);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Native lifecycle invocation replay plan is invalid.";
            var accepted = false;
            var canReplayNow = false;
            OpenACAINativeLifecycleInvocationJournalEntrySnapshot entry = null;

            if (string.IsNullOrWhiteSpace(normalizedToken))
            {
                reason = "missing-binding-token";
                detail = "A native lifecycle invocation binding token is required for replay planning.";
            }
            else
            {
                entry = snapshot.JournalEntries.FirstOrDefault(candidate =>
                    candidate != null
                    && string.Equals(candidate.BindingToken, normalizedToken, StringComparison.OrdinalIgnoreCase)
                    && candidate.Journaled);
                if (entry == null)
                {
                    reason = "binding-token-not-journaled";
                    detail = "No journaled native lifecycle invocation matched '" + normalizedToken + "'.";
                }
                else if (entry.Issue)
                {
                    reason = "journal-entry-issue";
                    detail = "The native lifecycle invocation journal entry has an issue and cannot be replay-planned.";
                }
                else
                {
                    accepted = true;
                    if (!RuntimeInvocationEnabled)
                    {
                        status = "DEFERRED";
                        reason = "runtime-invocation-disabled";
                        detail = "OpenACAI accepted the native lifecycle invocation replay plan; runtime invocation remains disabled.";
                    }
                    else
                    {
                        canReplayNow = true;
                        status = "READY";
                        reason = "ready";
                        detail = "Modstoke native lifecycle invocation replay plan is ready for method invocation.";
                    }
                }
            }

            return new OpenACAINativeLifecycleInvocationReplayPlanSnapshot(
                normalizedToken,
                entry == null ? 0 : entry.SequenceNumber,
                accepted,
                canReplayNow,
                entry != null && entry.Journaled,
                entry == null ? "" : entry.ModId,
                entry == null ? "" : entry.DisplayName,
                entry == null ? "" : entry.HookName,
                entry == null ? "" : entry.InvocationKind,
                entry == null ? "" : entry.EntrypointTypeName,
                entry == null ? "" : entry.EntrypointMethodName,
                entry == null ? "" : entry.HookMethodName,
                entry == null ? "" : entry.HookMethodSource,
                entry == null ? 0 : entry.HookMethodParameterCount,
                RuntimeInvocationEnabled,
                canReplayNow,
                entry != null && RuntimeInvocationEnabled,
                entry != null && !RuntimeInvocationEnabled,
                status,
                reason,
                detail,
                entry,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static void RecordReplayPlan(OpenACAINativeLifecycleInvocationReplayPlanSnapshot plan)
        {
            lock (SyncRoot)
            {
                _replayPlanRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedReplayPlanCount++;
                }
                else
                {
                    _rejectedReplayPlanCount++;
                }

                _lastReplayToken = plan == null ? "" : plan.BindingToken;
                _lastReplayStatus = plan == null ? "CHECK" : plan.Status;
                _lastReplayDetail = plan == null ? "Native lifecycle invocation replay plan was null." : plan.Detail;
                _lastReplayReason = plan == null ? "null-plan" : plan.ReasonCode;
                if (plan != null)
                {
                    RecordedReplayPlans.Add(plan);
                    while (RecordedReplayPlans.Count > MaxReplayPlanCount)
                    {
                        RecordedReplayPlans.RemoveAt(0);
                    }
                }
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Journal",
                "OpenACAI-owned native lifecycle invocation replay journal.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateJournalSection(snapshot),
                    CreateReplaySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeLifecycleInvocationJournalSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Invocation Journal",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native lifecycle invocation journal snapshot."),
                    Row("Journaling", snapshot.JournalingEnabled ? "Enabled" : "Disabled", snapshot.JournalingEnabled ? "READY" : "CHECK", "Resolved invocation tokens are journaled without invoking mod code."),
                    Row("Runtime Invocation", snapshot.RuntimeInvocationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", "Native lifecycle method invocation remains disabled until verified in game."),
                    Row("Journal Entries", snapshot.JournalEntryCount.ToString(), snapshot.JournalIssueCount == 0 ? "READY" : "CHECK", snapshot.JournaledEntryCount + " journaled; " + snapshot.JournalIssueCount + " issue(s)."),
                    Row("Replay Gate", snapshot.ReplayGateOpenCount + " open", snapshot.RuntimeInvocationEnabled ? "READY" : "INFO", snapshot.ReplayGateBlockedCount + " journaled invocation(s) blocked by the disabled runtime invocation gate."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries remain outside native lifecycle invocation replay planning.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateJournalSection(OpenACAINativeLifecycleInvocationJournalSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Entries", snapshot.JournalEntryCount.ToString(), snapshot.JournalIssueCount == 0 ? "READY" : "CHECK", snapshot.JournaledEntryCount + " replay intent(s) journaled.")
            };

            foreach (var entry in snapshot.JournalEntries.Take(10))
            {
                rows.Add(Row(
                    "#" + entry.SequenceNumber + " " + (string.IsNullOrWhiteSpace(entry.HookName) ? "hook" : entry.HookName),
                    string.IsNullOrWhiteSpace(entry.BindingToken) ? "no token" : entry.BindingToken,
                    entry.Status,
                    entry.Detail + " Method " + (string.IsNullOrWhiteSpace(entry.HookMethodName) ? "none" : entry.HookMethodName) + "."));
            }

            if (snapshot.JournalEntryCount == 0)
            {
                rows.Add(Row("No Journal Entries", "0", "INFO", "No Modstoke-native lifecycle invocation replay entries were journaled."));
            }

            return new OpenACAIMenuSectionSnapshot("Replay Journal", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateReplaySection(OpenACAINativeLifecycleInvocationJournalSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Replay Plans", snapshot.ReplayPlanRequestCount + " requested", "INFO", snapshot.AcceptedReplayPlanCount + " accepted, " + snapshot.RejectedReplayPlanCount + " rejected."),
                Row("Last Replay", string.IsNullOrWhiteSpace(snapshot.LastReplayToken) ? "none" : snapshot.LastReplayToken, snapshot.LastReplayStatus, snapshot.LastReplayDetail),
                Row("Last Reason", snapshot.LastReplayReason, snapshot.LastReplayStatus, "Last native lifecycle invocation replay reason code.")
            };

            foreach (var plan in snapshot.ReplayPlans.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(plan.HookName) ? "Replay" : plan.HookName,
                    plan.BindingToken,
                    plan.Status,
                    plan.Detail));
            }

            return new OpenACAIMenuSectionSnapshot("Replay Plans", rows);
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
            public string Id => OpenACAIMenuProviderIds.NativeLifecycleInvocationJournal;
            public string DisplayName => "Modstoke Native Journal";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 712;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native lifecycle invocation replay journal diagnostics.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeLifecycleInvocationJournal.CreateContent();
            }
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationJournalSnapshot
    {
        internal OpenACAINativeLifecycleInvocationJournalSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool journalingEnabled,
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
            int lookupRequestCount,
            int acceptedLookupCount,
            int rejectedLookupCount,
            int libraryExcludedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationJournalEntrySnapshot> journalEntries,
            int journaledEntryCount,
            int journalIssueCount,
            int replayEligibleCount,
            int replayGateOpenCount,
            int replayGateBlockedCount,
            IReadOnlyList<OpenACAINativeLifecycleInvocationReplayPlanSnapshot> replayPlans,
            int replayPlanRequestCount,
            int acceptedReplayPlanCount,
            int rejectedReplayPlanCount,
            string lastReplayToken,
            string lastReplayStatus,
            string lastReplayDetail,
            string lastReplayReason)
        {
            ServiceKey = serviceKey ?? OpenACAINativeLifecycleInvocationJournal.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            JournalingEnabled = journalingEnabled;
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
            LookupRequestCount = Math.Max(0, lookupRequestCount);
            AcceptedLookupCount = Math.Max(0, acceptedLookupCount);
            RejectedLookupCount = Math.Max(0, rejectedLookupCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            JournalEntries = journalEntries ?? Array.Empty<OpenACAINativeLifecycleInvocationJournalEntrySnapshot>();
            JournalEntryCount = JournalEntries.Count;
            JournaledEntryCount = Math.Max(0, journaledEntryCount);
            JournalIssueCount = Math.Max(0, journalIssueCount);
            ReplayEligibleCount = Math.Max(0, replayEligibleCount);
            ReplayGateOpenCount = Math.Max(0, replayGateOpenCount);
            ReplayGateBlockedCount = Math.Max(0, replayGateBlockedCount);
            ReplayPlans = replayPlans ?? Array.Empty<OpenACAINativeLifecycleInvocationReplayPlanSnapshot>();
            ReplayPlanCount = ReplayPlans.Count;
            ReplayPlanRequestCount = Math.Max(0, replayPlanRequestCount);
            AcceptedReplayPlanCount = Math.Max(0, acceptedReplayPlanCount);
            RejectedReplayPlanCount = Math.Max(0, rejectedReplayPlanCount);
            LastReplayToken = lastReplayToken ?? "";
            LastReplayStatus = lastReplayStatus ?? "";
            LastReplayDetail = lastReplayDetail ?? "";
            LastReplayReason = lastReplayReason ?? "";
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool JournalingEnabled { get; }
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
        public int LookupRequestCount { get; }
        public int AcceptedLookupCount { get; }
        public int RejectedLookupCount { get; }
        public int LibraryExcludedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationJournalEntrySnapshot> JournalEntries { get; }
        public int JournalEntryCount { get; }
        public int JournaledEntryCount { get; }
        public int JournalIssueCount { get; }
        public int ReplayEligibleCount { get; }
        public int ReplayGateOpenCount { get; }
        public int ReplayGateBlockedCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleInvocationReplayPlanSnapshot> ReplayPlans { get; }
        public int ReplayPlanCount { get; }
        public int ReplayPlanRequestCount { get; }
        public int AcceptedReplayPlanCount { get; }
        public int RejectedReplayPlanCount { get; }
        public string LastReplayToken { get; }
        public string LastReplayStatus { get; }
        public string LastReplayDetail { get; }
        public string LastReplayReason { get; }

        internal static OpenACAINativeLifecycleInvocationJournalSnapshot CreateUnknown()
        {
            return new OpenACAINativeLifecycleInvocationJournalSnapshot(
                OpenACAINativeLifecycleInvocationJournal.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native lifecycle invocation journal has not refreshed yet.",
                true,
                OpenACAINativeLifecycleInvocationJournal.RuntimeInvocationEnabled,
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
                Array.Empty<OpenACAINativeLifecycleInvocationJournalEntrySnapshot>(),
                0,
                0,
                0,
                0,
                0,
                Array.Empty<OpenACAINativeLifecycleInvocationReplayPlanSnapshot>(),
                0,
                0,
                0,
                "",
                "INFO",
                "No native lifecycle invocation replay plan has been requested.",
                "none");
        }
    }

    public sealed class OpenACAINativeLifecycleInvocationJournalEntrySnapshot
    {
        internal OpenACAINativeLifecycleInvocationJournalEntrySnapshot(
            int sequenceNumber,
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
            bool journaled,
            bool replayEligible,
            bool runtimeInvocationEnabled,
            bool replayGateOpen,
            bool replayGateBlocked,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
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
            Journaled = journaled;
            ReplayEligible = replayEligible;
            RuntimeInvocationEnabled = runtimeInvocationEnabled;
            ReplayGateOpen = replayGateOpen;
            ReplayGateBlocked = replayGateBlocked;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
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
        public bool Journaled { get; }
        public bool ReplayEligible { get; }
        public bool RuntimeInvocationEnabled { get; }
        public bool ReplayGateOpen { get; }
        public bool ReplayGateBlocked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeLifecycleInvocationReplayPlanSnapshot
    {
        internal OpenACAINativeLifecycleInvocationReplayPlanSnapshot(
            string bindingToken,
            int sequenceNumber,
            bool accepted,
            bool canReplayNow,
            bool journaled,
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
            bool replayEligible,
            bool replayGateOpen,
            bool replayGateBlocked,
            string status,
            string reasonCode,
            string detail,
            OpenACAINativeLifecycleInvocationJournalEntrySnapshot entry,
            string generatedUtc)
        {
            BindingToken = bindingToken ?? "";
            SequenceNumber = Math.Max(0, sequenceNumber);
            Accepted = accepted;
            CanReplayNow = canReplayNow;
            Journaled = journaled;
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
            ReplayEligible = replayEligible;
            ReplayGateOpen = replayGateOpen;
            ReplayGateBlocked = replayGateBlocked;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            Entry = entry;
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public string BindingToken { get; }
        public int SequenceNumber { get; }
        public bool Accepted { get; }
        public bool CanReplayNow { get; }
        public bool Journaled { get; }
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
        public bool ReplayEligible { get; }
        public bool ReplayGateOpen { get; }
        public bool ReplayGateBlocked { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public OpenACAINativeLifecycleInvocationJournalEntrySnapshot Entry { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
