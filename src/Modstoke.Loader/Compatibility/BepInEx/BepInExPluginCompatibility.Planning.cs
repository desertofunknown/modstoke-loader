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
        private static bool RecordAutomaticLoadPlans(BepInExPluginCompatibilitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.CompatibilityReadyCount == 0)
            {
                lock (SyncRoot)
                {
                    return AutoPlannedLoadKeys.RemoveWhere(key => !string.IsNullOrWhiteSpace(key)) > 0;
                }
            }

            var currentPlugins = snapshot.Candidates
                .Where(candidate => candidate != null && candidate.CompatibilityReady && !candidate.ValidationProbe)
                .Select(PlanKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentKeySet = new HashSet<string>(currentPlugins, StringComparer.OrdinalIgnoreCase);
            var pluginsToPlan = new List<string>();

            lock (SyncRoot)
            {
                AutoPlannedLoadKeys.RemoveWhere(key => !currentKeySet.Contains(key));
                foreach (var pluginKey in currentPlugins)
                {
                    if (AutoPlannedLoadKeys.Add(pluginKey))
                    {
                        pluginsToPlan.Add(pluginKey);
                    }
                }
            }

            foreach (var pluginKey in pluginsToPlan)
            {
                RecordLoadPlan(CreateLoadPlan(snapshot, pluginKey));
            }

            return pluginsToPlan.Count > 0;
        }

        private static BepInExPluginCompatibilityPlanSnapshot CreateLoadPlan(
            BepInExPluginCompatibilitySnapshot snapshot,
            string idOrAssemblyName)
        {
            snapshot = snapshot ?? BepInExPluginCompatibilitySnapshot.CreateUnknown();
            var normalizedId = NormalizeId(idOrAssemblyName);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "BepInEx plugin compatibility load plan is invalid.";
            var accepted = false;
            var canLoadNow = false;
            BepInExPluginCompatibilityCandidateSnapshot candidate = null;

            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                reason = "missing-plugin-id";
                detail = "A BepInEx plugin id, guid, name, or assembly name is required.";
            }
            else
            {
                candidate = FindCandidate(snapshot, normalizedId, out var ambiguous);
                if (candidate == null)
                {
                    reason = ambiguous ? "ambiguous-bepinex-plugin" : "unknown-bepinex-plugin";
                    detail = ambiguous
                        ? "Multiple BepInEx candidates matched '" + normalizedId + "'. Use a CandidateId to select the exact file and type."
                        : "No BepInEx plugin compatibility candidate matched '" + normalizedId + "'.";
                }
                else if (!candidate.Enabled)
                {
                    reason = "plugin-disabled";
                    detail = "The BepInEx plugin compatibility candidate is disabled.";
                }
                else if (!candidate.AssemblyExists)
                {
                    reason = "missing-assembly";
                    detail = "The BepInEx plugin compatibility candidate has no loadable assembly.";
                }
                else if (!candidate.CompatibilityReady)
                {
                    reason = string.IsNullOrWhiteSpace(candidate.IssueCode) ? "compatibility-not-ready" : candidate.IssueCode;
                    detail = candidate.Detail;
                }
                else
                {
                    accepted = true;
                    canLoadNow = true;
                    status = "READY";
                    reason = "ready";
                    detail = "The candidate is eligible for the BepInEx host, which loads it with Assembly.LoadFrom; planning does not load it.";
                }
            }

            return new BepInExPluginCompatibilityPlanSnapshot(
                candidate == null ? normalizedId : CandidatePlanId(candidate),
                candidate == null ? 0 : candidate.SequenceNumber,
                accepted,
                canLoadNow,
                RuntimePluginLoadingEnabled,
                candidate != null && candidate.LoadGateOpen,
                candidate != null && candidate.LoadGateBlocked,
                false,
                candidate == null ? LoadPlanSourceLookup : LoadPlanSourceCandidate,
                false,
                status,
                reason,
                detail,
                CreateInvocationPreflight(candidate),
                candidate,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static BepInExPluginInvocationPreflightSnapshot CreateInvocationPreflight(
            BepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            if (candidate == null)
            {
                return BepInExPluginInvocationPreflightSnapshot.CreateUnavailable(
                    "no-candidate",
                    "No BepInEx compatibility candidate was available for invocation preflight.");
            }

            var executionPlan = candidate.ExecutionPlan;
            var pluginId = CandidatePlanId(candidate);
            var pluginTypeName = executionPlan == null || string.IsNullOrWhiteSpace(executionPlan.PluginTypeName)
                ? candidate.PluginTypeName
                : executionPlan.PluginTypeName;
            var pluginBaseKind = executionPlan == null || string.IsNullOrWhiteSpace(executionPlan.PluginBaseKind)
                ? "unknown"
                : executionPlan.PluginBaseKind;
            var invocationKind = DetermineInvocationKind(executionPlan);
            var entrypointMethodName = DetermineInvocationEntrypoint(executionPlan);
            var assemblyLoadRequired = candidate.AssemblyExists && !string.IsNullOrWhiteSpace(candidate.AssemblyPath);
            var instanceCreationRequired = executionPlan != null && executionPlan.ParameterlessConstructorFound;
            var methodInvocationRequired = executionPlan != null
                && (string.Equals(pluginBaseKind, "base-plugin", StringComparison.Ordinal)
                    || executionPlan.LifecycleMethodReadyCount > 0);
            var preflightReady = false;
            var status = "CHECK";
            var issueCode = "invocation-preflight-not-ready";
            var detail = "BepInEx plugin invocation preflight is not ready.";

            if (!candidate.CompatibilityReady)
            {
                issueCode = string.IsNullOrWhiteSpace(candidate.IssueCode) ? "compatibility-not-ready" : candidate.IssueCode;
                detail = "BepInEx plugin invocation preflight is blocked by candidate compatibility state: " + candidate.Detail;
            }
            else if (executionPlan == null)
            {
                issueCode = "execution-plan-unavailable";
                detail = "BepInEx plugin invocation preflight requires execution-plan metadata.";
            }
            else if (!executionPlan.Ready)
            {
                issueCode = string.IsNullOrWhiteSpace(executionPlan.IssueCode)
                    ? "execution-plan-not-ready"
                    : executionPlan.IssueCode;
                detail = "BepInEx plugin invocation preflight is blocked by execution-plan metadata: " + executionPlan.Detail;
            }
            else if (!assemblyLoadRequired)
            {
                issueCode = "assembly-load-target-missing";
                detail = "BepInEx plugin invocation preflight requires a candidate assembly path.";
            }
            else
            {
                preflightReady = true;
                status = "READY";
                issueCode = "";
                detail = "The BepInEx host loads the assembly with Assembly.LoadFrom into the default load context, creates "
                    + pluginTypeName + " and invokes "
                    + (string.IsNullOrWhiteSpace(entrypointMethodName) ? invocationKind : entrypointMethodName) + ".";
            }

            var activationPolicy = CreateActivationPolicy(
                candidate,
                executionPlan,
                assemblyLoadRequired,
                instanceCreationRequired,
                methodInvocationRequired);
            var invocationPipelineStages = CreateInvocationPipelineStages(
                pluginTypeName,
                invocationKind,
                entrypointMethodName,
                preflightReady,
                assemblyLoadRequired,
                instanceCreationRequired,
                methodInvocationRequired,
                InvocationBoundaryMetadataGate);

            return new BepInExPluginInvocationPreflightSnapshot(
                true,
                preflightReady,
                pluginId,
                candidate.AssemblyPath,
                pluginTypeName,
                pluginBaseKind,
                invocationKind,
                entrypointMethodName,
                assemblyLoadRequired,
                instanceCreationRequired,
                methodInvocationRequired,
                invocationPipelineStages,
                RuntimePluginLoadingEnabled,
                preflightReady,
                false,
                InvocationBoundaryMetadataGate,
                activationPolicy,
                status,
                issueCode,
                detail);
        }

        private static BepInExPluginActivationPolicySnapshot CreateActivationPolicy(
            BepInExPluginCompatibilityCandidateSnapshot candidate,
            BepInExPluginExecutionPlanSnapshot executionPlan,
            bool assemblyLoadRequired,
            bool constructorInvocationRequired,
            bool loadInvocationRequired)
        {
            if (candidate == null)
            {
                return BepInExPluginActivationPolicySnapshot.CreateUnavailable(
                    "no-candidate",
                    "No BepInEx compatibility candidate was available for activation policy evaluation.");
            }

            var candidateReady = candidate.CompatibilityReady;
            var executionPlanReady = executionPlan != null && executionPlan.Ready;
            var staticConstructorFound = executionPlan != null && executionPlan.StaticConstructorFound;
            var moduleInitializerFound = executionPlan != null && executionPlan.ModuleInitializerFound;
            var moduleInitializerCount = executionPlan == null ? 0 : executionPlan.ModuleInitializerCount;
            var policyEvaluated = candidateReady && executionPlan != null;

            var assemblyLoadBlockReason = "";
            var constructorInvocationBlockReason = "";
            var loadInvocationBlockReason = "";
            var status = "CHECK";
            var issueCode = "activation-policy-not-ready";
            var detail = "Modstoke BepInEx activation policy is not ready.";

            if (!candidateReady)
            {
                issueCode = string.IsNullOrWhiteSpace(candidate.IssueCode) ? "compatibility-not-ready" : candidate.IssueCode;
                detail = "Modstoke BepInEx activation policy is blocked by candidate compatibility state: " + candidate.Detail;
                assemblyLoadBlockReason = issueCode;
            }
            else if (executionPlan == null)
            {
                issueCode = "execution-plan-unavailable";
                detail = "Modstoke BepInEx activation policy requires execution-plan metadata before any runtime activation boundary can open.";
                assemblyLoadBlockReason = issueCode;
            }
            else if (!executionPlanReady)
            {
                issueCode = string.IsNullOrWhiteSpace(executionPlan.IssueCode)
                    ? "execution-plan-not-ready"
                    : executionPlan.IssueCode;
                detail = "Modstoke BepInEx activation policy is blocked by execution-plan metadata: " + executionPlan.Detail;
                assemblyLoadBlockReason = issueCode;
            }
            else if (!assemblyLoadRequired)
            {
                issueCode = "assembly-load-target-missing";
                detail = "Modstoke BepInEx activation policy requires a candidate assembly path before any activation boundary can open.";
                assemblyLoadBlockReason = issueCode;
            }
            else if (moduleInitializerFound)
            {
                issueCode = "bepinex-plugin-module-initializer-present";
                detail = "Modstoke BepInEx activation policy flags this assembly because module initializers can run when it loads, before the constructor or Load().";
                assemblyLoadBlockReason = issueCode;
            }

            var assemblyLoadAllowed = policyEvaluated
                && executionPlanReady
                && assemblyLoadRequired
                && !moduleInitializerFound
                && string.IsNullOrWhiteSpace(assemblyLoadBlockReason);
            var assemblyLoadBlocked = assemblyLoadRequired && !assemblyLoadAllowed;

            if (constructorInvocationRequired)
            {
                if (staticConstructorFound)
                {
                    constructorInvocationBlockReason = "bepinex-plugin-static-constructor-present";
                }
                else if (!assemblyLoadAllowed)
                {
                    constructorInvocationBlockReason = string.IsNullOrWhiteSpace(assemblyLoadBlockReason)
                        ? "assembly-load-not-allowed"
                        : assemblyLoadBlockReason;
                }
            }
            else
            {
                constructorInvocationBlockReason = "not-required";
            }

            var constructorInvocationAllowed = constructorInvocationRequired
                && assemblyLoadAllowed
                && !staticConstructorFound;
            var constructorInvocationBlocked = constructorInvocationRequired && !constructorInvocationAllowed;

            if (loadInvocationRequired)
            {
                if (!constructorInvocationAllowed)
                {
                    loadInvocationBlockReason = string.IsNullOrWhiteSpace(constructorInvocationBlockReason)
                        ? "plugin-instance-creation-not-allowed"
                        : constructorInvocationBlockReason;
                }
            }
            else
            {
                loadInvocationBlockReason = "not-required";
            }

            var loadInvocationAllowed = loadInvocationRequired && constructorInvocationAllowed;
            var loadInvocationBlocked = loadInvocationRequired && !loadInvocationAllowed;
            var realPluginExecutionAllowed = (!assemblyLoadRequired || assemblyLoadAllowed)
                && (!constructorInvocationRequired || constructorInvocationAllowed)
                && (!loadInvocationRequired || loadInvocationAllowed);

            if (realPluginExecutionAllowed)
            {
                status = "READY";
                issueCode = "";
                detail = "Modstoke BepInEx activation policy allows the required real-plugin runtime boundaries for this candidate.";
            }
            else if (string.Equals(status, "CHECK", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(assemblyLoadBlockReason)
                && (constructorInvocationBlocked || loadInvocationBlocked))
            {
                issueCode = constructorInvocationBlocked
                    ? constructorInvocationBlockReason
                    : loadInvocationBlockReason;
                detail = "Modstoke BepInEx activation policy blocks before "
                    + (constructorInvocationBlocked ? "plugin constructor invocation" : "BasePlugin.Load()")
                    + " for this candidate.";
            }

            var sideEffectRisk = ActivationPolicyNoSideEffectRisk;
            if (moduleInitializerFound)
            {
                sideEffectRisk = ActivationPolicyModuleInitializerRisk;
            }
            else if (staticConstructorFound)
            {
                sideEffectRisk = ActivationPolicyStaticConstructorRisk;
            }

            if (string.IsNullOrWhiteSpace(constructorInvocationBlockReason) && constructorInvocationBlocked)
            {
                constructorInvocationBlockReason = "plugin-instance-creation-not-allowed";
            }

            if (string.IsNullOrWhiteSpace(loadInvocationBlockReason) && loadInvocationBlocked)
            {
                loadInvocationBlockReason = "plugin-load-invocation-not-allowed";
            }

            return new BepInExPluginActivationPolicySnapshot(
                true,
                policyEvaluated,
                candidateReady,
                executionPlanReady,
                RuntimePluginLoadingEnabled,
                assemblyLoadRequired,
                constructorInvocationRequired,
                loadInvocationRequired,
                assemblyLoadAllowed,
                constructorInvocationAllowed,
                loadInvocationAllowed,
                realPluginExecutionAllowed,
                assemblyLoadBlocked,
                constructorInvocationBlocked,
                loadInvocationBlocked,
                staticConstructorFound,
                moduleInitializerFound,
                moduleInitializerCount,
                assemblyLoadBlockReason,
                constructorInvocationBlockReason,
                loadInvocationBlockReason,
                sideEffectRisk,
                status,
                issueCode,
                detail);
        }

        private static string DetermineInvocationKind(BepInExPluginExecutionPlanSnapshot executionPlan)
        {
            if (executionPlan == null)
            {
                return "unavailable";
            }

            if (string.Equals(executionPlan.PluginBaseKind, "base-plugin", StringComparison.Ordinal))
            {
                return "base-plugin-load";
            }

            if (string.Equals(executionPlan.PluginBaseKind, "base-unity-plugin", StringComparison.Ordinal))
            {
                return "base-unity-plugin-lifecycle";
            }

            return "unknown";
        }

        private static string DetermineInvocationEntrypoint(BepInExPluginExecutionPlanSnapshot executionPlan)
        {
            if (executionPlan == null)
            {
                return "";
            }

            if (!string.IsNullOrWhiteSpace(executionPlan.RequiredEntrypointMethodName))
            {
                return executionPlan.RequiredEntrypointMethodName;
            }

            var lifecycleMethod = executionPlan.LifecycleMethods
                .FirstOrDefault(method => method != null && method.Ready);
            return lifecycleMethod == null ? "Unity lifecycle" : lifecycleMethod.MethodName;
        }

        private static IReadOnlyList<BepInExPluginInvocationPipelineStageSnapshot> CreateInvocationPipelineStages(
            string pluginTypeName,
            string invocationKind,
            string entrypointMethodName,
            bool preflightReady,
            bool assemblyLoadRequired,
            bool instanceCreationRequired,
            bool methodInvocationRequired,
            string isolationBoundary)
        {
            return new[]
            {
                CreateInvocationPipelineStage(
                    1,
                    "Assembly Load",
                    "assembly-load",
                    assemblyLoadRequired,
                    preflightReady,
                    isolationBoundary,
                    "Load the candidate plugin assembly with Assembly.LoadFrom into the default load context."),
                CreateInvocationPipelineStage(
                    2,
                    "Instance Create",
                    "instance-create",
                    instanceCreationRequired,
                    preflightReady,
                    isolationBoundary,
                    "Create the BepInEx plugin instance for " + (string.IsNullOrWhiteSpace(pluginTypeName) ? "the resolved plugin type" : pluginTypeName) + "."),
                CreateInvocationPipelineStage(
                    3,
                    "Entrypoint Invoke",
                    string.IsNullOrWhiteSpace(invocationKind) ? "entrypoint-invoke" : invocationKind,
                    methodInvocationRequired,
                    preflightReady,
                    isolationBoundary,
                    "Invoke " + (string.IsNullOrWhiteSpace(entrypointMethodName) ? "the resolved plugin entrypoint" : entrypointMethodName) + " from the Modstoke BepInEx host.")
            };
        }

        private static BepInExPluginInvocationPipelineStageSnapshot CreateInvocationPipelineStage(
            int sequenceNumber,
            string stageName,
            string stageKind,
            bool required,
            bool preflightReady,
            string isolationBoundary,
            string actionDetail)
        {
            var gateOpen = required && preflightReady;
            var status = "CHECK";
            var issueCode = "invocation-stage-preflight-not-ready";
            var detail = (actionDetail ?? "").Trim();

            if (!required)
            {
                status = "INFO";
                issueCode = "not-required";
                detail = stageName + " is not required for this BepInEx compatibility candidate.";
            }
            else if (gateOpen)
            {
                status = "READY";
                issueCode = "";
                detail = stageName + " runs in the Modstoke BepInEx host. " + detail;
            }
            else if (string.IsNullOrWhiteSpace(detail))
            {
                detail = stageName + " is waiting on invocation preflight readiness.";
            }
            else
            {
                detail = stageName + " is waiting on invocation preflight readiness. " + detail;
            }

            return new BepInExPluginInvocationPipelineStageSnapshot(
                sequenceNumber,
                stageName,
                stageKind,
                required,
                gateOpen,
                false,
                false,
                isolationBoundary,
                status,
                issueCode,
                detail);
        }

        private static void RecordLoadPlan(BepInExPluginCompatibilityPlanSnapshot plan)
        {
            lock (SyncRoot)
            {
                _loadPlanRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedLoadPlanCount++;
                }
                else
                {
                    _rejectedLoadPlanCount++;
                }

                _lastLoadPlanPluginId = plan == null ? "" : plan.PluginId;
                _lastLoadPlanStatus = plan == null ? "CHECK" : plan.Status;
                _lastLoadPlanDetail = plan == null ? "BepInEx plugin compatibility load plan was null." : plan.Detail;
                _lastLoadPlanReason = plan == null ? "null-plan" : plan.ReasonCode;
                if (plan != null)
                {
                    RecordedLoadPlans.Add(plan);
                    while (RecordedLoadPlans.Count > MaxLoadPlanCount)
                    {
                        RecordedLoadPlans.RemoveAt(0);
                    }
                }
            }
        }

        private static BepInExPluginCompatibilityLoadAttemptSnapshot CreateLoadAttempt(
            BepInExPluginCompatibilityPlanSnapshot plan)
        {
            var generatedUtc = DateTimeOffset.UtcNow.ToString("O");
            var attemptId = "bepinex-attempt-" + generatedUtc.Replace(":", "").Replace(".", "").Replace("+", "-");
            var status = "CHECK";
            var reason = "invalid-plan";
            var detail = "BepInEx plugin compatibility load attempt could not be planned.";
            var attemptAccepted = false;
            var loadGateOpen = false;
            var loadGateBlocked = true;

            if (plan == null)
            {
                detail = "BepInEx plugin compatibility load attempt was rejected because the load plan was null.";
            }
            else if (!plan.Accepted)
            {
                reason = string.IsNullOrWhiteSpace(plan.ReasonCode) ? "plan-rejected" : plan.ReasonCode;
                detail = "BepInEx plugin compatibility load attempt was rejected before execution: " + plan.Detail;
            }
            else
            {
                attemptAccepted = true;
                loadGateOpen = plan.LoadGateOpen;
                loadGateBlocked = !loadGateOpen;
                status = "DEFERRED";
                if (!plan.CanLoadNow || !plan.LoadGateOpen)
                {
                    reason = "load-gate-blocked";
                    detail = "Modstoke recorded a BepInEx load attempt, but the candidate is not eligible for the BepInEx host and no plugin code was invoked.";
                }
                else
                {
                    // The attempt itself never loads, so it still counts as blocked.
                    reason = "host-owned-load";
                    detail = "Modstoke recorded a diagnostic BepInEx load attempt without loading the plugin. BepInExPluginHost loads eligible plugins with Assembly.LoadFrom into the default load context, at startup or through IL2CPPChainloader.LoadPlugins.";
                    loadGateBlocked = true;
                }
            }

            var invocationStageAttempts = CreateInvocationStageAttempts(plan);

            return new BepInExPluginCompatibilityLoadAttemptSnapshot(
                attemptId,
                plan == null ? "" : plan.PluginId,
                plan == null ? 0 : plan.SequenceNumber,
                plan == null ? LoadPlanSourceUnknown : plan.PlanSource,
                plan != null && plan.ValidationOnly,
                plan != null && plan.Accepted,
                attemptAccepted,
                RuntimePluginLoadingEnabled,
                loadGateOpen,
                loadGateBlocked,
                false,
                false,
                true,
                status,
                reason,
                detail,
                plan == null ? null : plan.InvocationPreflight,
                invocationStageAttempts,
                plan,
                plan == null ? null : plan.Candidate,
                generatedUtc);
        }

        private static IReadOnlyList<BepInExPluginInvocationStageAttemptSnapshot> CreateInvocationStageAttempts(
            BepInExPluginCompatibilityPlanSnapshot plan)
        {
            if (plan == null || plan.InvocationPreflight == null || plan.InvocationPreflight.InvocationPipelineStageCount == 0)
            {
                return Array.Empty<BepInExPluginInvocationStageAttemptSnapshot>();
            }

            var stages = new List<BepInExPluginInvocationStageAttemptSnapshot>();
            foreach (var stage in plan.InvocationPreflight.InvocationPipelineStages)
            {
                if (stage == null)
                {
                    continue;
                }

                stages.Add(CreateInvocationStageAttempt(plan, stage));
            }

            return stages;
        }

        private static BepInExPluginInvocationStageAttemptSnapshot CreateInvocationStageAttempt(
            BepInExPluginCompatibilityPlanSnapshot plan,
            BepInExPluginInvocationPipelineStageSnapshot stage)
        {
            var attemptRecorded = plan != null && plan.Accepted && stage.Required;
            var gateOpen = attemptRecorded && stage.GateOpen;
            var status = "CHECK";
            var reasonCode = "invocation-stage-attempt-not-ready";
            var detail = stage.StageName + " stage attempt is not ready.";

            if (!stage.Required)
            {
                status = "INFO";
                reasonCode = "not-required";
                detail = stage.StageName + " stage attempt was not recorded because the stage is not required.";
            }
            else if (!attemptRecorded)
            {
                status = "CHECK";
                reasonCode = "plan-not-accepted";
                detail = stage.StageName + " stage attempt was not recorded because the compatibility plan was not accepted.";
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = stage.StageName + " stage is run by the BepInEx host, not by this diagnostic attempt.";
            }

            return new BepInExPluginInvocationStageAttemptSnapshot(
                stage.SequenceNumber,
                stage.StageName,
                stage.StageKind,
                stage.Required,
                attemptRecorded,
                false,
                false,
                gateOpen,
                false,
                true,
                stage.IsolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static void RecordLoadAttempt(BepInExPluginCompatibilityLoadAttemptSnapshot attempt)
        {
            lock (SyncRoot)
            {
                _loadAttemptRequestCount++;
                if (attempt != null && attempt.AttemptAccepted)
                {
                    _acceptedLoadAttemptCount++;
                }
                else
                {
                    _rejectedLoadAttemptCount++;
                }

                if (attempt != null && attempt.LoadGateBlocked)
                {
                    _blockedLoadAttemptCount++;
                }

                _lastLoadAttemptPluginId = attempt == null ? "" : attempt.PluginId;
                _lastLoadAttemptStatus = attempt == null ? "CHECK" : attempt.Status;
                _lastLoadAttemptDetail = attempt == null ? "BepInEx plugin compatibility load attempt was null." : attempt.Detail;
                _lastLoadAttemptReason = attempt == null ? "null-attempt" : attempt.ReasonCode;

                if (attempt != null)
                {
                    RecordedLoadAttempts.Add(attempt);
                    while (RecordedLoadAttempts.Count > MaxLoadAttemptCount)
                    {
                        RecordedLoadAttempts.RemoveAt(0);
                    }
                }
            }
        }

        private static BepInExPluginCompatibilityCandidateSnapshot FindCandidate(
            BepInExPluginCompatibilitySnapshot snapshot,
            string id,
            out bool ambiguous)
        {
            ambiguous = false;
            if (snapshot == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var normalized = NormalizeId(id);
            var exact = snapshot.Candidates.FirstOrDefault(candidate => candidate != null
                && string.Equals(candidate.CandidateId, normalized, StringComparison.Ordinal));
            if (exact != null)
                return exact;

            var matches = snapshot.Candidates.Where(candidate =>
                candidate != null
                && (string.Equals(candidate.ModId, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.DisplayName, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.AssemblyName, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(candidate.AssemblyPath), normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.PluginGuid, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.PluginName, normalized, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(normalized, "Modstoke Endnight Loader Core", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.PluginGuid, RuntimeEntryPoint.PluginGuid, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(candidate.AssemblyPath, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.PluginTypeName, normalized, StringComparison.Ordinal)))
                .Take(2).ToArray();
            ambiguous = matches.Length > 1;
            return matches.Length == 1 ? matches[0] : null;
        }
    }
}
