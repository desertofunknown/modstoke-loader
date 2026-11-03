// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;

namespace Modstoke.Loader
{
    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleRegistrySnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleRegistrySnapshot(LifecycleView view)
        {
            _view = view;
            PlannedRegistrations = view.Map(row => new LifecycleRegistrationSnapshot(row));
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleRegistry.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool PlanningEnabled => false;
        public bool RuntimeInvocationEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int HostableCandidateCount => _view.Runtime.LoadedCount;
        public int ExecutionPlanReadyCount => _view.Runtime.LoadedCount;
        public int ExecutionPlanIssueCount => _view.Runtime.FailedCount;
        public int LifecycleHookDeclarationCount => _view.Count;
        public int LibraryExcludedCount => 0;
        public int AcceptedPlanCount => _view.Runtime.LoadedCount;
        public int RejectedPlanCount => _view.Runtime.FailedCount;
        public IReadOnlyList<LifecycleRegistrationSnapshot> PlannedRegistrations { get; }
        public int RegistrationCount => _view.Count;
        public int PlannedHookCount => _view.ReadyCount;
        public int DeferredHookCount => _view.BlockedCount;
        public int RegistrationIssueCount => _view.FailedCount;
        public int InvocationGateOpenCount => _view.ActiveCount;
        public int InvocationGateBlockedCount => _view.BlockedCount;
        public int DryRunEvaluationCount => _view.Count;
        public int DryRunReadyCount => _view.ReadyCount;
        public int DryRunBlockedCount => _view.BlockedCount;
        public int DryRunIssueCount => _view.FailedCount;
        public int HookMethodReadyCount => _view.ReadyCount;
        public int HookMethodIssueCount => _view.FailedCount;
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleRegistrationSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleRegistrationSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public bool HookMethodDeclared => _row.Known;
        public string HookMethodSource => "";
        public bool HookMethodFound => _row.Known;
        public bool HookMethodStatic => _row.Known;
        public bool HookMethodPublic => _row.Known;
        public int HookMethodParameterCount => 0;
        public bool HookMethodReady => _row.Ready;
        public string HookMethodStatus => _row.Status;
        public string HookMethodIssueCode => _row.IssueCode;
        public string HookMethodDetail => _row.Detail;
        public int HookMethodReadyCount => _row.Ready ? 1 : 0;
        public int HookMethodIssueCount => _row.Failed ? 1 : 0;
        public bool Planned => _row.Ready;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool InvocationGateOpen => _row.Active;
        public bool InvocationGateBlocked => !_row.Active;
        public bool DryRunEvaluated => _row.Known;
        public bool DryRunReady => _row.Ready;
        public string DryRunStatus => _row.Status;
        public string DryRunIssueCode => _row.IssueCode;
        public string DryRunDetail => _row.Detail;
        public int DryRunEvaluationCount => _row.Known ? 1 : 0;
        public int DryRunReadyCount => _row.Ready ? 1 : 0;
        public int DryRunBlockedCount => _row.Active ? 0 : 1;
        public int DryRunIssueCount => _row.Failed ? 1 : 0;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleDispatchPlannerSnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleDispatchPlannerSnapshot(LifecycleView view)
        {
            _view = view;
            DispatchTargets = view.Map(row => new LifecycleDispatchTargetSnapshot(row));
            DispatchIntentions = view.Map(row => new LifecycleDispatchIntentSnapshot(row));
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleDispatchPlanner.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool PlanningEnabled => false;
        public bool RuntimeDispatchEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int RegistrationCount => _view.Count;
        public int PlannedHookCount => _view.ReadyCount;
        public int RegistrationIssueCount => _view.FailedCount;
        public int HookMethodReadyCount => _view.ReadyCount;
        public int HookMethodIssueCount => _view.FailedCount;
        public int DryRunReadyCount => _view.ReadyCount;
        public int DryRunIssueCount => _view.FailedCount;
        public int LibraryExcludedCount => 0;
        public IReadOnlyList<LifecycleDispatchTargetSnapshot> DispatchTargets { get; }
        public int DispatchTargetCount => _view.Count;
        public int DispatchMetadataReadyCount => _view.ReadyCount;
        public int DispatchIssueCount => _view.FailedCount;
        public int DispatchGateOpenCount => _view.ActiveCount;
        public int DispatchGateBlockedCount => _view.BlockedCount;
        public IReadOnlyList<LifecycleDispatchIntentSnapshot> DispatchIntentions { get; }
        public int DispatchIntentCount => _view.Count;
        public int DispatchIntentMetadataReadyCount => _view.ReadyCount;
        public int QueuedDispatchIntentCount => _view.ActiveCount;
        public int DispatchIntentIssueCount => _view.FailedCount;
        public int DispatchIntentGateOpenCount => _view.ActiveCount;
        public int DispatchIntentGateBlockedCount => _view.BlockedCount;
        public int DispatchPlanRequestCount => 0;
        public int AcceptedDispatchPlanCount => 0;
        public int RejectedDispatchPlanCount => 0;
        public string LastDispatchHookName => "";
        public string LastDispatchStatus => "";
        public string LastDispatchDetail => "";
        public string LastDispatchReason => "";
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleDispatchTargetSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleDispatchTargetSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public bool MetadataReady => _row.Ready;
        public bool RuntimeDispatchEnabled => _row.RuntimeInvocationEnabled;
        public bool DispatchGateOpen => _row.Active;
        public bool DispatchGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleDispatchIntentSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleDispatchIntentSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public bool MetadataReady => _row.Ready;
        public bool Queued => _row.Active;
        public bool RuntimeDispatchEnabled => _row.RuntimeInvocationEnabled;
        public bool DispatchGateOpen => _row.Active;
        public bool DispatchGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleDispatchPlan
    {
        private readonly LifecycleHookGroup _group;

        internal LifecycleDispatchPlan(LifecycleHookGroup group)
        {
            _group = group;
        }

        public string HookName => _group.HookName;
        public int TargetCount => _group.Count;
        public int MetadataReadyTargetCount => _group.ReadyCount;
        public int IssueTargetCount => _group.FailedCount;
        public int DispatchGateOpenCount => _group.ActiveCount;
        public int DispatchGateBlockedCount => _group.BlockedCount;
        public bool Accepted => _group.Accepted;
        public bool CanDispatchNow => _group.ActiveCount > 0;
        public bool RuntimeDispatchEnabled => _group.RuntimeInvocationEnabled;
        public string Status => _group.Status;
        public string ReasonCode => _group.ReasonCode;
        public string Detail => _group.Detail;
        public string GeneratedUtc => _group.GeneratedUtc;
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleInvocationPlannerSnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleInvocationPlannerSnapshot(LifecycleView view)
        {
            _view = view;
            InvocationRequests = view.Map(row => new LifecycleInvocationRequestSnapshot(row));
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleInvocationPlanner.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool PlanningEnabled => false;
        public bool RuntimeInvocationEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int DispatchTargetCount => _view.Count;
        public int DispatchIntentCount => _view.Count;
        public int QueuedDispatchIntentCount => _view.ActiveCount;
        public int DispatchIntentIssueCount => _view.FailedCount;
        public int LibraryExcludedCount => 0;
        public IReadOnlyList<LifecycleInvocationRequestSnapshot> InvocationRequests { get; }
        public int InvocationRequestCount => _view.Count;
        public int AdapterReadyCount => _view.ReadyCount;
        public int InvocationIssueCount => _view.FailedCount;
        public int InvocationGateOpenCount => _view.ActiveCount;
        public int InvocationGateBlockedCount => _view.BlockedCount;
        public int InvocationPlanRequestCount => 0;
        public int AcceptedInvocationPlanCount => 0;
        public int RejectedInvocationPlanCount => 0;
        public string LastInvocationHookName => "";
        public string LastInvocationStatus => "";
        public string LastInvocationDetail => "";
        public string LastInvocationReason => "";
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleInvocationRequestSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleInvocationRequestSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public bool AdapterReady => _row.Ready;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool InvocationGateOpen => _row.Active;
        public bool InvocationGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleInvocationPlan
    {
        private readonly LifecycleHookGroup _group;

        internal LifecycleInvocationPlan(LifecycleHookGroup group)
        {
            _group = group;
        }

        public string HookName => _group.HookName;
        public int RequestCount => _group.Count;
        public int AdapterReadyCount => _group.ReadyCount;
        public int IssueCount => _group.FailedCount;
        public int InvocationGateOpenCount => _group.ActiveCount;
        public int InvocationGateBlockedCount => _group.BlockedCount;
        public bool Accepted => _group.Accepted;
        public bool CanInvokeNow => _group.ActiveCount > 0;
        public bool RuntimeInvocationEnabled => _group.RuntimeInvocationEnabled;
        public string Status => _group.Status;
        public string ReasonCode => _group.ReasonCode;
        public string Detail => _group.Detail;
        public string GeneratedUtc => _group.GeneratedUtc;
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleBindingsSnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleBindingsSnapshot(LifecycleView view)
        {
            _view = view;
            Bindings = view.Map(row => new LifecycleBindingSnapshot(row));
            BindingTokens = view.Map(row => row.Token);
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleBindings.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool BindingEnabled => false;
        public bool RuntimeInvocationEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int DispatchTargetCount => _view.Count;
        public int DispatchIntentCount => _view.Count;
        public int QueuedDispatchIntentCount => _view.ActiveCount;
        public int InvocationRequestCount => _view.Count;
        public int AdapterReadyCount => _view.ReadyCount;
        public int InvocationIssueCount => _view.FailedCount;
        public int BridgeInvocationGateOpenCount => _view.ActiveCount;
        public int BridgeInvocationGateBlockedCount => _view.BlockedCount;
        public int BridgePlanRequestCount => 0;
        public int BridgeAcceptedPlanCount => 0;
        public int BridgeRejectedPlanCount => 0;
        public int LibraryExcludedCount => 0;
        public IReadOnlyList<LifecycleBindingSnapshot> Bindings { get; }
        public int BindingCount => _view.Count;
        public int BindingReadyCount => _view.ReadyCount;
        public int BindingIssueCount => _view.FailedCount;
        public int BindingTokenIssuedCount => _view.Count;
        public int InvocationEligibleCount => _view.ActiveCount;
        public int InvocationGateOpenCount => _view.ActiveCount;
        public int InvocationGateBlockedCount => _view.BlockedCount;
        public IReadOnlyList<string> BindingTokens { get; }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleBindingSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleBindingSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public string BindingToken => _row.Token;
        public bool BindingTokenIssued => _row.Known;
        public bool BindingReady => _row.Ready;
        public bool InvocationEligible => _row.Active;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool InvocationGateOpen => _row.Active;
        public bool InvocationGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleTokenResolverSnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleTokenResolverSnapshot(LifecycleView view)
        {
            _view = view;
            TokenResolutions = view.Map(row => new LifecycleTokenResolutionSnapshot(row));
            ResolvedBindingTokens = view.Map(row => row.Token);
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleTokenResolver.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool ResolvingEnabled => false;
        public bool RuntimeInvocationEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int DispatchTargetCount => _view.Count;
        public int DispatchIntentCount => _view.Count;
        public int QueuedDispatchIntentCount => _view.ActiveCount;
        public int InvocationRequestCount => _view.Count;
        public int BindingCount => _view.Count;
        public int BindingReadyCount => _view.ReadyCount;
        public int BindingIssueCount => _view.FailedCount;
        public int BindingTokenIssuedCount => _view.Count;
        public int AdapterInvocationEligibleCount => _view.ActiveCount;
        public int AdapterInvocationGateOpenCount => _view.ActiveCount;
        public int AdapterInvocationGateBlockedCount => _view.BlockedCount;
        public int LibraryExcludedCount => 0;
        public IReadOnlyList<LifecycleTokenResolutionSnapshot> TokenResolutions { get; }
        public int TokenResolutionCount => _view.Count;
        public int TokenResolvedCount => _view.Count;
        public int TokenIssueCount => _view.FailedCount;
        public int InvocationEligibleCount => _view.ActiveCount;
        public int InvocationGateOpenCount => _view.ActiveCount;
        public int InvocationGateBlockedCount => _view.BlockedCount;
        public IReadOnlyList<LifecycleTokenLookupSnapshot> LookupResults => Array.Empty<LifecycleTokenLookupSnapshot>();
        public int LookupResultCount => 0;
        public int LookupRequestCount => 0;
        public int AcceptedLookupCount => 0;
        public int RejectedLookupCount => 0;
        public string LastLookupToken => "";
        public string LastLookupStatus => "";
        public string LastLookupDetail => "";
        public string LastLookupReason => "";
        public IReadOnlyList<string> ResolvedBindingTokens { get; }
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleTokenLookupSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleTokenLookupSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string BindingToken => _row.Token;
        public bool Success => _row.Known;
        public bool TokenResolved => _row.Known;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool InvocationEligible => _row.Active;
        public bool InvocationGateOpen => _row.Active;
        public bool InvocationGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string ReasonCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public LifecycleTokenResolutionSnapshot Resolution => _row.Known ? new LifecycleTokenResolutionSnapshot(_row) : null;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleTokenResolutionSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleTokenResolutionSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public string BindingToken => _row.Token;
        public bool BindingTokenIssued => _row.Known;
        public bool TokenResolved => _row.Known;
        public bool BindingReady => _row.Ready;
        public bool InvocationEligible => _row.Active;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool InvocationGateOpen => _row.Active;
        public bool InvocationGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleJournalSnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleJournalSnapshot(LifecycleView view)
        {
            _view = view;
            JournalEntries = view.Map(row => new LifecycleJournalEntrySnapshot(row));
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleJournal.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool JournalingEnabled => false;
        public bool RuntimeInvocationEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int DispatchTargetCount => _view.Count;
        public int DispatchIntentCount => _view.Count;
        public int QueuedDispatchIntentCount => _view.ActiveCount;
        public int InvocationRequestCount => _view.Count;
        public int BindingCount => _view.Count;
        public int BindingReadyCount => _view.ReadyCount;
        public int BindingIssueCount => _view.FailedCount;
        public int BindingTokenIssuedCount => _view.Count;
        public int TokenResolutionCount => _view.Count;
        public int TokenResolvedCount => _view.Count;
        public int TokenIssueCount => _view.FailedCount;
        public int LookupRequestCount => 0;
        public int AcceptedLookupCount => 0;
        public int RejectedLookupCount => 0;
        public int LibraryExcludedCount => 0;
        public IReadOnlyList<LifecycleJournalEntrySnapshot> JournalEntries { get; }
        public int JournalEntryCount => _view.Count;
        public int JournaledEntryCount => _view.ReadyCount;
        public int JournalIssueCount => _view.FailedCount;
        public int ReplayEligibleCount => _view.ActiveCount;
        public int ReplayGateOpenCount => _view.ActiveCount;
        public int ReplayGateBlockedCount => _view.BlockedCount;
        public IReadOnlyList<LifecycleReplayPlanSnapshot> ReplayPlans => Array.Empty<LifecycleReplayPlanSnapshot>();
        public int ReplayPlanCount => 0;
        public int ReplayPlanRequestCount => 0;
        public int AcceptedReplayPlanCount => 0;
        public int RejectedReplayPlanCount => 0;
        public string LastReplayToken => "";
        public string LastReplayStatus => "";
        public string LastReplayDetail => "";
        public string LastReplayReason => "";
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleJournalEntrySnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleJournalEntrySnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public int SequenceNumber => _row.Sequence;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public string BindingToken => _row.Token;
        public bool BindingTokenIssued => _row.Known;
        public bool TokenResolved => _row.Known;
        public bool BindingReady => _row.Ready;
        public bool Journaled => _row.Ready;
        public bool ReplayEligible => _row.Active;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool ReplayGateOpen => _row.Active;
        public bool ReplayGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleReplayPlanSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleReplayPlanSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public string BindingToken => _row.Token;
        public int SequenceNumber => _row.Sequence;
        public bool Accepted => _row.Known;
        public bool CanReplayNow => false;
        public bool Journaled => _row.Ready;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool ReplayEligible => _row.Active;
        public bool ReplayGateOpen => _row.Active;
        public bool ReplayGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string ReasonCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public LifecycleJournalEntrySnapshot Entry => _row.Known ? new LifecycleJournalEntrySnapshot(_row) : null;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleSchedulePlannerSnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleSchedulePlannerSnapshot(LifecycleView view)
        {
            _view = view;
            ScheduleBatches = Array.AsReadOnly(view.Groups.Select(group => new LifecycleScheduleBatchSnapshot(group)).ToArray());
            ScheduleEntries = view.Map(row => new LifecycleScheduleEntrySnapshot(row));
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleSchedulePlanner.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool SchedulingEnabled => false;
        public bool RuntimeInvocationEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int DispatchTargetCount => _view.Count;
        public int DispatchIntentCount => _view.Count;
        public int QueuedDispatchIntentCount => _view.ActiveCount;
        public int InvocationRequestCount => _view.Count;
        public int BindingCount => _view.Count;
        public int BindingReadyCount => _view.ReadyCount;
        public int BindingIssueCount => _view.FailedCount;
        public int BindingTokenIssuedCount => _view.Count;
        public int TokenResolutionCount => _view.Count;
        public int TokenResolvedCount => _view.Count;
        public int TokenIssueCount => _view.FailedCount;
        public int JournalEntryCount => _view.Count;
        public int JournaledEntryCount => _view.ReadyCount;
        public int JournalIssueCount => _view.FailedCount;
        public int ReplayPlanRequestCount => 0;
        public int AcceptedReplayPlanCount => 0;
        public int RejectedReplayPlanCount => 0;
        public int LibraryExcludedCount => 0;
        public IReadOnlyList<LifecycleScheduleBatchSnapshot> ScheduleBatches { get; }
        public int ScheduleBatchCount => ScheduleBatches.Count;
        public IReadOnlyList<LifecycleScheduleEntrySnapshot> ScheduleEntries { get; }
        public int ScheduleEntryCount => _view.Count;
        public int ScheduledEntryCount => _view.ReadyCount;
        public int ScheduleIssueCount => _view.FailedCount;
        public int ScheduleEligibleCount => _view.ActiveCount;
        public int ScheduleGateOpenCount => _view.ActiveCount;
        public int ScheduleGateBlockedCount => _view.BlockedCount;
        public IReadOnlyList<LifecycleSchedulePlanSnapshot> SchedulePlans => Array.Empty<LifecycleSchedulePlanSnapshot>();
        public int SchedulePlanCount => 0;
        public int SchedulePlanRequestCount => 0;
        public int AcceptedSchedulePlanCount => 0;
        public int RejectedSchedulePlanCount => 0;
        public string LastScheduleHookName => "";
        public string LastScheduleStatus => "";
        public string LastScheduleDetail => "";
        public string LastScheduleReason => "";
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleScheduleBatchSnapshot
    {
        private readonly LifecycleHookGroup _group;

        internal LifecycleScheduleBatchSnapshot(LifecycleHookGroup group)
        {
            _group = group;
            Entries = Array.AsReadOnly(group.Rows.Select(row => new LifecycleScheduleEntrySnapshot(row)).ToArray());
        }

        public int Schema => 1;
        public int BatchSequenceNumber => _group.Sequence;
        public string HookName => _group.HookName;
        public int HookOrder => _group.HookOrder;
        public IReadOnlyList<LifecycleScheduleEntrySnapshot> Entries { get; }
        public int EntryCount => _group.Count;
        public int ScheduledEntryCount => _group.ReadyCount;
        public int ScheduleIssueCount => _group.FailedCount;
        public int ScheduleEligibleCount => _group.ActiveCount;
        public bool RuntimeInvocationEnabled => _group.RuntimeInvocationEnabled;
        public int ScheduleGateOpenCount => _group.ActiveCount;
        public int ScheduleGateBlockedCount => _group.BlockedCount;
        public string Status => _group.Status;
        public string IssueCode => _group.ReasonCode;
        public string Detail => _group.Detail;
        public string GeneratedUtc => _group.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleScheduleEntrySnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleScheduleEntrySnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public int ScheduleSequenceNumber => _row.Sequence;
        public int BatchSequenceNumber => _row.BatchSequence;
        public int HookSequenceNumber => _row.HookSequence;
        public int HookOrder => _row.HookOrder;
        public int JournalSequenceNumber => _row.Sequence;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public string BindingToken => _row.Token;
        public bool Journaled => _row.Ready;
        public bool Scheduled => _row.Ready;
        public bool ScheduleEligible => _row.Active;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool ScheduleGateOpen => _row.Active;
        public bool ScheduleGateBlocked => !_row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleSchedulePlanSnapshot
    {
        private readonly LifecycleHookGroup _group;

        internal LifecycleSchedulePlanSnapshot(LifecycleHookGroup group)
        {
            _group = group;
            Batch = new LifecycleScheduleBatchSnapshot(group);
        }

        public int Schema => 1;
        public string HookName => _group.HookName;
        public int BatchSequenceNumber => _group.Sequence;
        public int HookOrder => _group.HookOrder;
        public int EntryCount => _group.Count;
        public int ScheduledEntryCount => _group.ReadyCount;
        public int ScheduleIssueCount => _group.FailedCount;
        public int ScheduleGateOpenCount => _group.ActiveCount;
        public int ScheduleGateBlockedCount => _group.BlockedCount;
        public bool Accepted => _group.Accepted;
        public bool CanScheduleNow => _group.ActiveCount > 0;
        public bool RuntimeInvocationEnabled => _group.RuntimeInvocationEnabled;
        public string Status => _group.Status;
        public string ReasonCode => _group.ReasonCode;
        public string Detail => _group.Detail;
        public LifecycleScheduleBatchSnapshot Batch { get; }
        public string GeneratedUtc => _group.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleExecutionPlannerSnapshot
    {
        private readonly LifecycleView _view;

        internal LifecycleExecutionPlannerSnapshot(LifecycleView view)
        {
            _view = view;
            ExecutionBatches = Array.AsReadOnly(view.Groups.Select(group => new LifecycleExecutionBatchSnapshot(group)).ToArray());
            ExecutionDecisions = view.Map(row => new LifecycleExecutionDecisionSnapshot(row));
        }

        public int Schema => 1;
        public string ServiceKey => LifecycleExecutionPlanner.ServiceKey;
        public string GeneratedUtc => _view.GeneratedUtc;
        public bool Ready => _view.Ready;
        public string Status => _view.Status;
        public string Detail => _view.Detail;
        public bool ExecutionPlanningEnabled => false;
        public bool RuntimeInvocationEnabled => _view.RuntimeInvocationEnabled;
        public int CandidateCount => _view.Runtime.Mods.Count;
        public int DispatchTargetCount => _view.Count;
        public int DispatchIntentCount => _view.Count;
        public int QueuedDispatchIntentCount => _view.ActiveCount;
        public int InvocationRequestCount => _view.Count;
        public int BindingCount => _view.Count;
        public int BindingReadyCount => _view.ReadyCount;
        public int BindingIssueCount => _view.FailedCount;
        public int BindingTokenIssuedCount => _view.Count;
        public int TokenResolutionCount => _view.Count;
        public int TokenResolvedCount => _view.Count;
        public int TokenIssueCount => _view.FailedCount;
        public int JournalEntryCount => _view.Count;
        public int JournaledEntryCount => _view.ReadyCount;
        public int JournalIssueCount => _view.FailedCount;
        public int ReplayPlanRequestCount => 0;
        public int AcceptedReplayPlanCount => 0;
        public int RejectedReplayPlanCount => 0;
        public int ScheduleBatchCount => ExecutionBatches.Count;
        public int ScheduleEntryCount => _view.Count;
        public int ScheduledEntryCount => _view.ReadyCount;
        public int ScheduleIssueCount => _view.FailedCount;
        public int SchedulePlanRequestCount => 0;
        public int AcceptedSchedulePlanCount => 0;
        public int RejectedSchedulePlanCount => 0;
        public int LibraryExcludedCount => 0;
        public IReadOnlyList<LifecycleExecutionBatchSnapshot> ExecutionBatches { get; }
        public int ExecutionBatchCount => ExecutionBatches.Count;
        public IReadOnlyList<LifecycleExecutionDecisionSnapshot> ExecutionDecisions { get; }
        public int ExecutionDecisionCount => _view.Count;
        public int ExecutionPlannedCount => _view.ReadyCount;
        public int ExecutionIssueCount => _view.FailedCount;
        public int ExecutionEligibleCount => _view.ActiveCount;
        public int ExecutionGateOpenCount => _view.ActiveCount;
        public int ExecutionGateBlockedCount => _view.BlockedCount;
        public int MethodInvocationCount => _view.ActiveCount;
        public IReadOnlyList<LifecycleExecutionPlanSnapshot> ExecutionPlans => Array.Empty<LifecycleExecutionPlanSnapshot>();
        public int ExecutionPlanCount => 0;
        public int ExecutionPlanRequestCount => 0;
        public int AcceptedExecutionPlanCount => 0;
        public int RejectedExecutionPlanCount => 0;
        public string LastExecutionHookName => "";
        public string LastExecutionStatus => "";
        public string LastExecutionDetail => "";
        public string LastExecutionReason => "";
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleExecutionBatchSnapshot
    {
        private readonly LifecycleHookGroup _group;

        internal LifecycleExecutionBatchSnapshot(LifecycleHookGroup group)
        {
            _group = group;
            Decisions = Array.AsReadOnly(group.Rows.Select(row => new LifecycleExecutionDecisionSnapshot(row)).ToArray());
        }

        public int Schema => 1;
        public int BatchSequenceNumber => _group.Sequence;
        public string HookName => _group.HookName;
        public int HookOrder => _group.HookOrder;
        public IReadOnlyList<LifecycleExecutionDecisionSnapshot> Decisions { get; }
        public int DecisionCount => _group.Count;
        public int ExecutionPlannedCount => _group.ReadyCount;
        public int ExecutionIssueCount => _group.FailedCount;
        public int ExecutionEligibleCount => _group.ActiveCount;
        public bool RuntimeInvocationEnabled => _group.RuntimeInvocationEnabled;
        public int ExecutionGateOpenCount => _group.ActiveCount;
        public int ExecutionGateBlockedCount => _group.BlockedCount;
        public int MethodInvocationCount => _group.ActiveCount;
        public string Status => _group.Status;
        public string IssueCode => _group.ReasonCode;
        public string Detail => _group.Detail;
        public string GeneratedUtc => _group.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleExecutionDecisionSnapshot
    {
        private readonly LifecycleHookRow _row;

        internal LifecycleExecutionDecisionSnapshot(LifecycleHookRow row)
        {
            _row = row;
        }

        public int Schema => 1;
        public int ExecutionSequenceNumber => _row.Sequence;
        public int BatchSequenceNumber => _row.BatchSequence;
        public int HookSequenceNumber => _row.HookSequence;
        public int HookOrder => _row.HookOrder;
        public int ScheduleSequenceNumber => _row.Sequence;
        public int JournalSequenceNumber => _row.Sequence;
        public string ModId => _row.ModId;
        public string DisplayName => _row.DisplayName;
        public string HookName => _row.HookName;
        public string InvocationKind => "";
        public string EntrypointTypeName => "";
        public string EntrypointMethodName => "";
        public string HookMethodName => _row.MethodName;
        public string HookMethodSource => "";
        public int HookMethodParameterCount => 0;
        public string BindingToken => _row.Token;
        public bool Journaled => _row.Ready;
        public bool Scheduled => _row.Ready;
        public bool ExecutionPlanned => _row.Ready;
        public bool ExecutionEligible => _row.Active;
        public bool RuntimeInvocationEnabled => _row.RuntimeInvocationEnabled;
        public bool ExecutionGateOpen => _row.Active;
        public bool ExecutionGateBlocked => !_row.Active;
        public bool MethodInvoked => _row.Active;
        public string Status => _row.Status;
        public string IssueCode => _row.IssueCode;
        public string Detail => _row.Detail;
        public string GeneratedUtc => _row.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(ModRuntime.RetiredLifecycleMessage)]
    public sealed class LifecycleExecutionPlanSnapshot
    {
        private readonly LifecycleHookGroup _group;

        internal LifecycleExecutionPlanSnapshot(LifecycleHookGroup group)
        {
            _group = group;
            Batch = new LifecycleExecutionBatchSnapshot(group);
        }

        public int Schema => 1;
        public string HookName => _group.HookName;
        public int BatchSequenceNumber => _group.Sequence;
        public int HookOrder => _group.HookOrder;
        public int DecisionCount => _group.Count;
        public int ExecutionPlannedCount => _group.ReadyCount;
        public int ExecutionIssueCount => _group.FailedCount;
        public int ExecutionGateOpenCount => _group.ActiveCount;
        public int ExecutionGateBlockedCount => _group.BlockedCount;
        public int MethodInvocationCount => _group.ActiveCount;
        public bool Accepted => _group.Accepted;
        public bool CanExecuteNow => _group.ActiveCount > 0;
        public bool RuntimeInvocationEnabled => _group.RuntimeInvocationEnabled;
        public string Status => _group.Status;
        public string ReasonCode => _group.ReasonCode;
        public string Detail => _group.Detail;
        public LifecycleExecutionBatchSnapshot Batch { get; }
        public string GeneratedUtc => _group.GeneratedUtc;
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
