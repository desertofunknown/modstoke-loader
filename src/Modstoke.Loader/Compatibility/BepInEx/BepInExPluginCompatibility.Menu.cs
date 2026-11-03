// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Modstoke.Loader
{
    public static partial class BepInExPluginCompatibility
    {
        private static IReadOnlyList<CommandSnapshot> CreateCommands()
        {
            var snapshot = State;
            return new[]
            {
                new CommandSnapshot(
                    "lastLoadAttempt",
                    "Last Load Attempt",
                    snapshot.LastLoadAttemptDetail,
                    snapshot.LastLoadAttemptStatus,
                    false,
                    false)
            };
        }

        private static bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
        {
            result = string.IsNullOrWhiteSpace(commandId)
                ? CommandResultSnapshot.Failure("Command id is required.")
                : CommandResultSnapshot.Failure("Unknown BepInEx compatibility command: " + commandId + ".");
            return false;
        }

        private static MenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new MenuContentSnapshot(
                "Modstoke BepInEx Gate",
                "Modstoke-owned metadata gate for legacy BepInEx plugin compatibility.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateParserHarnessSection(snapshot),
                    CreateCandidateSection(snapshot),
                    CreatePlanSection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateGateSection(BepInExPluginCompatibilitySnapshot snapshot)
        {
            var validationCandidateCount = snapshot.Candidates.Count(candidate => candidate != null && candidate.ValidationProbe);
            var invocationPreflightReadyCount = snapshot.LoadPlans.Count(plan => plan != null && plan.InvocationPreflight != null && plan.InvocationPreflight.PreflightReady);
            var invocationPipelineStageCount = snapshot.LoadPlans.Sum(plan => plan == null || plan.InvocationPreflight == null ? 0 : plan.InvocationPreflight.RequiredInvocationPipelineStageCount);
            var invocationPipelineOpenStageCount = snapshot.LoadPlans.Sum(plan => plan == null || plan.InvocationPreflight == null ? 0 : plan.InvocationPreflight.OpenInvocationPipelineStageCount);
            var activationPolicies = snapshot.LoadPlans
                .Select(plan => plan == null || plan.InvocationPreflight == null ? null : plan.InvocationPreflight.ActivationPolicy)
                .Concat(snapshot.LoadAttempts.Select(attempt => attempt == null || attempt.InvocationPreflight == null ? null : attempt.InvocationPreflight.ActivationPolicy))
                .Where(policy => policy != null && policy.Available)
                .ToArray();
            var activationPolicyCount = activationPolicies.Length;
            var activationPolicyBlockedCount = activationPolicies.Count(policy => policy != null
                && (policy.AssemblyLoadBlocked || policy.ConstructorInvocationBlocked || policy.LoadInvocationBlocked));
            var activationPolicyIssueCount = activationPolicies.Count(policy => policy != null && policy.Issue);
            var activationPolicyAssemblyBlockedCount = activationPolicies.Count(policy => policy != null && policy.AssemblyLoadBlocked);
            var activationPolicyConstructorBlockedCount = activationPolicies.Count(policy => policy != null && policy.ConstructorInvocationBlocked);
            var activationPolicyLoadBlockedCount = activationPolicies.Count(policy => policy != null && policy.LoadInvocationBlocked);
            var firstBlockedActivationPolicy = activationPolicies.FirstOrDefault(policy => policy != null
                && (policy.AssemblyLoadBlocked || policy.ConstructorInvocationBlocked || policy.LoadInvocationBlocked));
            var firstBlockedActivationReason = firstBlockedActivationPolicy == null
                ? "none"
                : firstBlockedActivationPolicy.AssemblyLoadBlocked && !string.IsNullOrWhiteSpace(firstBlockedActivationPolicy.AssemblyLoadBlockReasonCode)
                    ? firstBlockedActivationPolicy.AssemblyLoadBlockReasonCode
                    : firstBlockedActivationPolicy.ConstructorInvocationBlocked && !string.IsNullOrWhiteSpace(firstBlockedActivationPolicy.ConstructorInvocationBlockReasonCode)
                        ? firstBlockedActivationPolicy.ConstructorInvocationBlockReasonCode
                        : firstBlockedActivationPolicy.LoadInvocationBlocked && !string.IsNullOrWhiteSpace(firstBlockedActivationPolicy.LoadInvocationBlockReasonCode)
                            ? firstBlockedActivationPolicy.LoadInvocationBlockReasonCode
                            : "activation-policy-blocked";
            var activationPolicyStatus = activationPolicyCount == 0
                ? "INFO"
                : activationPolicyIssueCount > 0
                    ? "CHECK"
                    : activationPolicyBlockedCount == 0
                        ? "READY"
                        : "DEFERRED";
            var invocationStageAttemptCount = snapshot.LoadAttempts.Sum(attempt => attempt == null ? 0 : attempt.InvocationStageAttemptCount);
            var invocationStageAttemptRecordedCount = snapshot.LoadAttempts.Sum(attempt => attempt == null ? 0 : attempt.RecordedInvocationStageAttemptCount);
            var candidateStaticConstructorCount = snapshot.Candidates.Count(candidate => candidate != null
                && candidate.ExecutionPlan != null
                && candidate.ExecutionPlan.StaticConstructorFound);
            var candidateModuleInitializerCount = snapshot.Candidates.Where(candidate => candidate != null)
                .GroupBy(candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase)
                .Sum(group => group.Max(candidate => candidate.ExecutionPlan?.ModuleInitializerCount ?? 0));
            var parserHarnessModuleInitializerCount = snapshot.ParserHarnesses.Sum(harness => harness == null || harness.ExecutionPlan == null
                ? 0
                : harness.ExecutionPlan.ModuleInitializerCount);
            return new MenuSectionSnapshot(
                "BepInEx Compatibility Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "Modstoke-owned BepInEx plugin compatibility snapshot."),
                    Row("Plugin Root", ShortPath(snapshot.PluginRoot), snapshot.PluginRootExists ? "READY" : "INFO", snapshot.PluginRoot),
                    Row("Scanning", snapshot.ScanningEnabled ? "Enabled" : "Disabled", snapshot.ScanningEnabled ? "READY" : "CHECK", "Modstoke scans BepInEx plugin assemblies as metadata only."),
                    Row("Runtime Plugin Loading", snapshot.RuntimePluginLoadingEnabled ? "Enabled" : "Disabled", snapshot.RuntimePluginLoadingEnabled ? "READY" : "INFO", "BepInExPluginHost loads eligible IL2CPP BasePlugin candidates with Assembly.LoadFrom into the default load context."),
                    Row("Candidates", snapshot.CandidateCount.ToString(), snapshot.CompatibilityIssueCount == 0 ? "READY" : "CHECK", snapshot.CompatibilityReadyCount + " metadata-ready; " + snapshot.CompatibilityIssueCount + " compatibility issue(s)."),
                    Row("BepInPlugin Metadata", snapshot.PluginAttributeCount.ToString(), snapshot.CompatibilityIssueCount == 0 ? "READY" : "CHECK", snapshot.MetadataReadCount + " assembly metadata read, " + snapshot.MetadataIssueCount + " metadata issue(s)."),
                    Row("Candidate References", snapshot.ResolvedAssemblyReferenceCount + "/" + snapshot.AssemblyReferenceCount, snapshot.UnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.UnresolvedAssemblyReferenceCount + " unresolved BepInEx plugin candidate assembly reference(s)." + FirstUnresolvedDetail(snapshot.FirstUnresolvedCandidateAssemblyReference)),
                    Row("Candidate Execution Plans", snapshot.ExecutionPlanReadyCount + "/" + snapshot.ExecutionPlanCount, snapshot.ExecutionPlanIssueCount == 0 && snapshot.LifecycleMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.ExecutionPlanIssueCount + " execution plan issue(s); lifecycle/entrypoint methods " + snapshot.LifecycleMethodReadyCount + "/" + snapshot.LifecycleMethodCount + "." + FirstExecutionPlanIssueDetail(snapshot.FirstExecutionPlanIssue, snapshot.FirstLifecycleMethodIssue)),
                    Row("Activation Side Effects", candidateStaticConstructorCount + " cctor / " + candidateModuleInitializerCount + " init", candidateModuleInitializerCount == 0 && parserHarnessModuleInitializerCount == 0 ? "READY" : "CHECK", "Metadata-only side-effect preflight found " + candidateStaticConstructorCount + " candidate static constructor(s), " + candidateModuleInitializerCount + " candidate module initializer(s), and " + parserHarnessModuleInitializerCount + " parser-harness module initializer(s)."),
                    Row("Parser Harness", snapshot.ParserHarnessCount.ToString(), snapshot.ParserHarnessIssueCount == 0 ? snapshot.PositiveMetadataParserVerified ? "READY" : "INFO" : "CHECK", snapshot.ParserHarnessReadyCount + " positive parser proof(s), " + snapshot.ParserHarnessIssueCount + " issue(s), " + snapshot.ParserHarnessUnsafeLocationCount + " unsafe location(s)."),
                    Row("Harness References", snapshot.ParserHarnessResolvedAssemblyReferenceCount + "/" + snapshot.ParserHarnessAssemblyReferenceCount, snapshot.ParserHarnessUnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.ParserHarnessUnresolvedAssemblyReferenceCount + " unresolved parser harness assembly reference(s)." + FirstUnresolvedDetail(snapshot.FirstUnresolvedParserHarnessAssemblyReference)),
                    Row("Harness Execution Plans", snapshot.ParserHarnessExecutionPlanReadyCount + "/" + snapshot.ParserHarnessExecutionPlanCount, snapshot.ParserHarnessExecutionPlanIssueCount == 0 && snapshot.ParserHarnessLifecycleMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.ParserHarnessExecutionPlanIssueCount + " parser harness execution plan issue(s); lifecycle/entrypoint methods " + snapshot.ParserHarnessLifecycleMethodReadyCount + "/" + snapshot.ParserHarnessLifecycleMethodCount + "." + FirstExecutionPlanIssueDetail(snapshot.FirstParserHarnessExecutionPlanIssue, snapshot.FirstParserHarnessLifecycleMethodIssue)),
                    Row("Validation Candidates", validationCandidateCount.ToString(), "INFO", "Modstoke candidate probes are read only from Modstoke validation roots and are not mod inventory entries."),
                    Row("Invocation Preflight", invocationPreflightReadyCount + "/" + snapshot.LoadPlanCount, "INFO", invocationPreflightReadyCount + " load plan(s) describe a candidate the BepInEx host can load, create and invoke."),
                    Row("Activation Policy", activationPolicyBlockedCount + "/" + activationPolicyCount, activationPolicyStatus, "Modstoke activation policy flags assembly load " + activationPolicyAssemblyBlockedCount + ", constructors " + activationPolicyConstructorBlockedCount + ", and Load() calls " + activationPolicyLoadBlockedCount + "; first reason " + firstBlockedActivationReason + ". The BepInEx host does not consult this policy."),
                    Row("Invocation Pipeline", invocationPipelineOpenStageCount + "/" + invocationPipelineStageCount, "INFO", invocationPipelineOpenStageCount + " required stage(s) are open: Assembly.LoadFrom, instance creation and entrypoint invocation all run in the BepInEx host."),
                    Row("Stage Attempts", invocationStageAttemptRecordedCount + "/" + invocationStageAttemptCount, "INFO", invocationStageAttemptRecordedCount + " stage(s) recorded by diagnostic load attempts; attempts never run them."),
                    Row("Load Gate", snapshot.LoadGateOpenCount + " open", "READY", snapshot.LoadGateOpenCount + " candidate(s) passed the load gate; the host skips validation probes."),
                    Row("Load Attempts", snapshot.LoadAttemptRequestCount + " requested", snapshot.BlockedLoadAttemptCount == 0 ? "READY" : "INFO", snapshot.AcceptedLoadAttemptCount + " accepted, " + snapshot.RejectedLoadAttemptCount + " rejected. Load attempts are diagnostic and never load plugin code."),
                    Row("Modstoke Loads", snapshot.PluginLoadedByOpenAcaiCount.ToString(), "READY", "Candidates loaded by BepInExPluginHost in this session."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries stay in their own section and never appear as BepInEx plugin compatibility candidates."),
                    Row("Modstoke Native Excluded", snapshot.OpenAcaiNativeExcludedCount.ToString(), "READY", "Modstoke-native mods remain in the native mod host path."),
                    Row("RedLoader Excluded", snapshot.RedLoaderCompatibilityExcludedCount.ToString(), "READY", "Legacy RedLoader mods remain in the RedLoader compatibility path.")
                });
        }

        private static MenuSectionSnapshot CreateParserHarnessSection(BepInExPluginCompatibilitySnapshot snapshot)
        {
            var rows = new List<MenuRowSnapshot>
            {
                Row("Harness Root", ShortPath(snapshot.ParserHarnessRoot), snapshot.ParserHarnessRootExists ? "READY" : "INFO", snapshot.ParserHarnessRoot),
                Row("Positive Parser Proof", snapshot.PositiveMetadataParserVerified ? "Verified" : "Not present", snapshot.ParserHarnessIssueCount == 0 ? snapshot.PositiveMetadataParserVerified ? "READY" : "INFO" : "CHECK", "Valid BepInPlugin metadata can be proven from harness DLLs outside BepInEx/plugins."),
                Row("Harness Assemblies", snapshot.ParserHarnessCount.ToString(), snapshot.ParserHarnessIssueCount == 0 ? "READY" : "CHECK", snapshot.ParserHarnessReadyCount + " ready, " + snapshot.ParserHarnessIssueCount + " issue(s), " + snapshot.ParserHarnessAttributeCount + " BepInPlugin attribute(s)."),
                Row("Harness References", snapshot.ParserHarnessResolvedAssemblyReferenceCount + "/" + snapshot.ParserHarnessAssemblyReferenceCount, snapshot.ParserHarnessUnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.ParserHarnessUnresolvedAssemblyReferenceCount + " unresolved parser harness assembly reference(s)." + FirstUnresolvedDetail(snapshot.FirstUnresolvedParserHarnessAssemblyReference)),
                Row("Harness Execution Plans", snapshot.ParserHarnessExecutionPlanReadyCount + "/" + snapshot.ParserHarnessExecutionPlanCount, snapshot.ParserHarnessExecutionPlanIssueCount == 0 && snapshot.ParserHarnessLifecycleMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.ParserHarnessLifecycleMethodReadyCount + "/" + snapshot.ParserHarnessLifecycleMethodCount + " lifecycle/entrypoint method(s) ready." + FirstExecutionPlanIssueDetail(snapshot.FirstParserHarnessExecutionPlanIssue, snapshot.FirstParserHarnessLifecycleMethodIssue))
            };

            foreach (var harness in snapshot.ParserHarnesses.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(harness.PluginName) ? harness.HarnessId : harness.PluginName,
                    string.IsNullOrWhiteSpace(harness.PluginGuid) ? harness.AssemblyName : harness.PluginGuid,
                    harness.Status,
                    harness.Detail + " Type " + (string.IsNullOrWhiteSpace(harness.PluginTypeName) ? "unknown" : harness.PluginTypeName) + "; outside plugin root " + harness.LocatedOutsidePluginRoot + "; references " + harness.ResolvedAssemblyReferenceCount + "/" + harness.AssemblyReferenceCount + ";" + ExecutionPlanDetail(harness.ExecutionPlan) + FirstUnresolvedDetail(harness.FirstUnresolvedAssemblyReference)));
            }

            if (snapshot.ParserHarnessCount == 0)
            {
                rows.Add(Row("No Parser Harness", "0", "INFO", "No BepInPlugin parser harness DLLs were discovered under Modstoke/ValidationProbes/BepInExPluginMetadataHarness."));
            }

            return new MenuSectionSnapshot("Parser Harness", rows);
        }

        private static MenuSectionSnapshot CreateCandidateSection(BepInExPluginCompatibilitySnapshot snapshot)
        {
            var validationCandidateCount = snapshot.Candidates.Count(candidate => candidate != null && candidate.ValidationProbe);
            var rows = new List<MenuRowSnapshot>
            {
                Row("Candidates", snapshot.CandidateCount.ToString(), snapshot.CompatibilityIssueCount == 0 ? "READY" : "CHECK", snapshot.CompatibilityIssueCount + " compatibility issue(s)."),
                Row("Validation Probes", validationCandidateCount.ToString(), "INFO", "Validation-probe candidates stay under Modstoke/ValidationProbes/BepInExPluginCandidateHarness and do not appear in the public mod inventory."),
                Row("Candidate References", snapshot.ResolvedAssemblyReferenceCount + "/" + snapshot.AssemblyReferenceCount, snapshot.UnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.UnresolvedAssemblyReferenceCount + " unresolved BepInEx plugin candidate assembly reference(s)." + FirstUnresolvedDetail(snapshot.FirstUnresolvedCandidateAssemblyReference)),
                Row("Candidate Execution Plans", snapshot.ExecutionPlanReadyCount + "/" + snapshot.ExecutionPlanCount, snapshot.ExecutionPlanIssueCount == 0 && snapshot.LifecycleMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.LifecycleMethodReadyCount + "/" + snapshot.LifecycleMethodCount + " lifecycle/entrypoint method(s) ready." + FirstExecutionPlanIssueDetail(snapshot.FirstExecutionPlanIssue, snapshot.FirstLifecycleMethodIssue))
            };

            foreach (var candidate in snapshot.Candidates.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(candidate.PluginName) ? candidate.DisplayName : candidate.PluginName,
                    string.IsNullOrWhiteSpace(candidate.PluginGuid) ? candidate.AssemblyName : candidate.PluginGuid,
                    candidate.Status,
                    candidate.Detail + " Candidate " + candidate.CandidateId + "; type " + candidate.PluginTypeName + "; assembly " + candidate.AssemblyPath + "; loaded by Modstoke " + candidate.LoadedByOpenAcai + "; source " + candidate.CandidateSource + "; validation probe " + candidate.ValidationProbe + "; BepInPlugin attributes " + candidate.BepInExPluginAttributeCount + "; assembly metadata " + (candidate.AssemblyMetadataReadable ? "read" : "not read") + "; references " + candidate.ResolvedAssemblyReferenceCount + "/" + candidate.AssemblyReferenceCount + ";" + ExecutionPlanDetail(candidate.ExecutionPlan) + " load gate " + (candidate.LoadGateOpen ? "open" : candidate.LoadGateBlocked ? "blocked" : "closed") + "." + FirstUnresolvedDetail(candidate.FirstUnresolvedAssemblyReference)));
            }

            if (snapshot.CandidateCount == 0)
            {
                rows.Add(Row("No BepInEx Plugins", "0", "INFO", "No legacy BepInEx plugin candidates were discovered outside Modstoke support folders."));
            }

            return new MenuSectionSnapshot("BepInEx Candidates", rows);
        }

        private static MenuSectionSnapshot CreatePlanSection(BepInExPluginCompatibilitySnapshot snapshot)
        {
            var rows = new List<MenuRowSnapshot>
            {
                Row("Load Plans", snapshot.LoadPlanRequestCount + " requested", "INFO", snapshot.AcceptedLoadPlanCount + " accepted, " + snapshot.RejectedLoadPlanCount + " rejected."),
                Row("Last Plugin", string.IsNullOrWhiteSpace(snapshot.LastLoadPlanPluginId) ? "none" : snapshot.LastLoadPlanPluginId, "INFO", "Last BepInEx plugin compatibility plan target."),
                Row("Last Plan", snapshot.LastLoadPlanStatus, snapshot.LastLoadPlanStatus, snapshot.LastLoadPlanDetail),
                Row("Last Reason", snapshot.LastLoadPlanReason, snapshot.LastLoadPlanStatus, "Last BepInEx plugin compatibility plan reason code."),
                Row("Load Attempts", snapshot.LoadAttemptRequestCount + " requested", snapshot.BlockedLoadAttemptCount == 0 ? "INFO" : "DEFERRED", snapshot.AcceptedLoadAttemptCount + " accepted, " + snapshot.RejectedLoadAttemptCount + " rejected, " + snapshot.BlockedLoadAttemptCount + " blocked."),
                Row("Last Attempt", snapshot.LastLoadAttemptStatus, snapshot.LastLoadAttemptStatus, snapshot.LastLoadAttemptDetail),
                Row("Last Attempt Reason", snapshot.LastLoadAttemptReason, snapshot.LastLoadAttemptStatus, "Last BepInEx plugin compatibility load-attempt reason code.")
            };

            foreach (var plan in snapshot.LoadPlans.Take(10))
            {
                rows.Add(Row(
                    plan.Candidate?.DisplayName ?? (string.IsNullOrWhiteSpace(plan.PluginId) ? "BepInEx Plugin" : plan.PluginId),
                    plan.ValidationOnly ? "validation-only" : plan.CanLoadNow ? "load-now" : "metadata-only",
                    plan.Status,
                    plan.Detail + " Target " + plan.PluginId + "; type " + plan.Candidate?.PluginTypeName + "; assembly " + plan.Candidate?.AssemblyPath + "; source " + (string.IsNullOrWhiteSpace(plan.PlanSource) ? LoadPlanSourceUnknown : plan.PlanSource) + "; candidate source " + (plan.Candidate == null ? "none" : plan.Candidate.CandidateSource) + "; validation only " + plan.ValidationOnly + "." + InvocationPreflightDetail(plan.InvocationPreflight)));
            }

            foreach (var attempt in snapshot.LoadAttempts.Take(10))
            {
                rows.Add(Row(
                    attempt.Candidate?.DisplayName ?? (string.IsNullOrWhiteSpace(attempt.PluginId) ? "BepInEx Attempt" : attempt.PluginId),
                    attempt.ValidationOnly ? "validation-only" : attempt.PluginInvoked ? "plugin-invoked" : "no-invocation",
                    attempt.Status,
                    attempt.Detail + " Source " + (string.IsNullOrWhiteSpace(attempt.PlanSource) ? LoadPlanSourceUnknown : attempt.PlanSource) + "; candidate source " + (attempt.Candidate == null ? "none" : attempt.Candidate.CandidateSource) + "; validation only " + attempt.ValidationOnly + "; failure isolated " + attempt.FailureIsolated + "; execution started " + attempt.ExecutionStarted + "; stages recorded " + attempt.RecordedInvocationStageAttemptCount + "/" + attempt.InvocationStageAttemptCount + "." + InvocationPreflightDetail(attempt.InvocationPreflight)));
            }

            return new MenuSectionSnapshot("Load Plans", rows);
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unavailable";
            }

            try
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.GetFileName(trimmed);
            }
            catch
            {
                return path;
            }
        }

        private static string FirstUnresolvedDetail(BepInExPluginAssemblyReferenceSnapshot reference)
        {
            if (reference == null || reference.Resolved)
            {
                return "";
            }

            var name = string.IsNullOrWhiteSpace(reference.AssemblyName) ? "unknown" : reference.AssemblyName;
            var version = string.IsNullOrWhiteSpace(reference.RequiredVersion) ? "" : " " + reference.RequiredVersion;
            var detail = string.IsNullOrWhiteSpace(reference.Detail) ? "" : ": " + reference.Detail;
            return " First unresolved: " + name + version + detail;
        }

        private static string FirstExecutionPlanIssueDetail(
            BepInExPluginExecutionPlanSnapshot plan,
            BepInExPluginLifecycleMethodSnapshot method)
        {
            if (plan == null || !plan.Issue)
            {
                return "";
            }

            var typeName = string.IsNullOrWhiteSpace(plan.PluginTypeName) ? "unknown type" : plan.PluginTypeName;
            var issue = string.IsNullOrWhiteSpace(plan.IssueCode) ? "execution-plan-issue" : plan.IssueCode;
            var detail = string.IsNullOrWhiteSpace(plan.Detail) ? "" : ": " + plan.Detail;
            var methodDetail = method == null || !method.Issue
                ? ""
                : " First method issue: " + (string.IsNullOrWhiteSpace(method.MethodName) ? "unknown" : method.MethodName)
                    + " "
                    + (string.IsNullOrWhiteSpace(method.IssueCode) ? "method-issue" : method.IssueCode)
                    + (string.IsNullOrWhiteSpace(method.Detail) ? "" : ": " + method.Detail);
            return " First execution issue: " + typeName + " " + issue + detail + methodDetail;
        }

        private static string ExecutionPlanDetail(BepInExPluginExecutionPlanSnapshot plan)
        {
            if (plan == null || !plan.PluginTypeFound)
            {
                return " execution plan unavailable.";
            }

            var kind = string.IsNullOrWhiteSpace(plan.PluginBaseKind) ? "unknown" : plan.PluginBaseKind;
            var constructor = plan.ParameterlessConstructorFound
                ? plan.PublicParameterlessConstructorFound ? "public parameterless" : "parameterless"
                : "missing parameterless";
            var methodDetail = plan.LifecycleMethodCount == 0
                ? "no lifecycle/entrypoint methods declared"
                : "lifecycle/entrypoint methods " + plan.LifecycleMethodReadyCount + "/" + plan.LifecycleMethodCount;
            var baseChain = plan.BaseChainExternalAssemblyResolved
                ? "; external base " + (string.IsNullOrWhiteSpace(plan.BaseChainExternalAssemblyName) ? "resolved" : plan.BaseChainExternalAssemblyName)
                    + (string.IsNullOrWhiteSpace(plan.BaseChainExternalAssemblySource) ? "" : " via " + plan.BaseChainExternalAssemblySource)
                : "";
            var sideEffects = "; static ctor " + plan.StaticConstructorFound + "; module initializers " + plan.ModuleInitializerCount;
            return " execution " + plan.Status + " " + kind + "; constructor " + constructor + "; " + methodDetail + baseChain + sideEffects + ".";
        }

        private static string InvocationPreflightDetail(BepInExPluginInvocationPreflightSnapshot preflight)
        {
            if (preflight == null || !preflight.Available)
            {
                return " Invocation preflight unavailable.";
            }

            var entrypoint = string.IsNullOrWhiteSpace(preflight.EntrypointMethodName)
                ? preflight.InvocationKind
                : preflight.EntrypointMethodName;
            return " Invocation preflight " + preflight.Status
                + " " + preflight.InvocationKind
                + "; entrypoint " + (string.IsNullOrWhiteSpace(entrypoint) ? "unknown" : entrypoint)
                + "; assembly load required " + preflight.AssemblyLoadRequired
                + "; instance required " + preflight.InstanceCreationRequired
                + "; method invoke required " + preflight.MethodInvocationRequired
                + "; pipeline blocked " + preflight.BlockedInvocationPipelineStageCount + "/" + preflight.RequiredInvocationPipelineStageCount
                + "; gate " + (preflight.InvocationGateOpen ? "open" : preflight.InvocationGateBlocked ? "blocked" : "closed")
                + "; boundary " + (string.IsNullOrWhiteSpace(preflight.IsolationBoundary) ? "unknown" : preflight.IsolationBoundary)
                + ActivationPolicyDetail(preflight.ActivationPolicy)
                + ".";
        }

        private static string ActivationPolicyDetail(BepInExPluginActivationPolicySnapshot policy)
        {
            if (policy == null || !policy.Available)
            {
                return "; activation policy unavailable";
            }

            return "; activation policy " + policy.Status
                + "; assembly " + (policy.AssemblyLoadAllowed ? "allowed" : policy.AssemblyLoadBlocked ? "blocked" : "closed")
                + " " + (string.IsNullOrWhiteSpace(policy.AssemblyLoadBlockReasonCode) ? "none" : policy.AssemblyLoadBlockReasonCode)
                + "; constructor " + (policy.ConstructorInvocationAllowed ? "allowed" : policy.ConstructorInvocationBlocked ? "blocked" : "closed")
                + " " + (string.IsNullOrWhiteSpace(policy.ConstructorInvocationBlockReasonCode) ? "none" : policy.ConstructorInvocationBlockReasonCode)
                + "; Load " + (policy.LoadInvocationAllowed ? "allowed" : policy.LoadInvocationBlocked ? "blocked" : "closed")
                + " " + (string.IsNullOrWhiteSpace(policy.LoadInvocationBlockReasonCode) ? "none" : policy.LoadInvocationBlockReasonCode)
                + "; side effects " + policy.SideEffectRisk
                + " cctor " + policy.StaticConstructorFound
                + " module initializers " + policy.ModuleInitializerCount;
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider, ICommandProvider
        {
            public string Id => MenuProviderIds.BepInExPluginCompatibility;
            public string DisplayName => "Modstoke BepInEx Gate";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 705;
            public string Source => "Modstoke Loader";
            public string Description => "Show Modstoke-owned BepInEx plugin compatibility metadata gate state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return BepInExPluginCompatibility.CreateContent();
            }

            public IReadOnlyList<CommandSnapshot> GetCommands()
            {
                return BepInExPluginCompatibility.CreateCommands();
            }

            public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
            {
                return BepInExPluginCompatibility.TryInvokeCommand(commandId, out result);
            }
        }
    }
}
