// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public OpenACAI runtime attestation surface. This is a disclosed, unsigned
// digest of loader-owned runtime signals for protected servers and companion
// tools; it does not embed private anti-cheat behavior.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAIRuntimeAttestation
    {
        public const string ServiceKey = OpenACAIModding.RuntimeAttestationServiceKey;
        public const string EvidenceVersion = "openacai-runtime-attestation-v1";

        private static readonly object SyncRoot = new object();
        private static OpenACAIRuntimeAttestationSnapshot _snapshot;

        public static OpenACAIRuntimeAttestationSnapshot Snapshot => EnsureSnapshot();
        public static bool Ready => Snapshot.Ready;

        public static OpenACAIRuntimeAttestationSnapshot Refresh()
        {
            lock (SyncRoot)
            {
                _snapshot = CreateSnapshot();
                OpenACAIModding.SetService(ServiceKey, _snapshot);
                return _snapshot;
            }
        }

        internal static void InitializeDefaults()
        {
            Refresh();
        }

        internal static object CreateState()
        {
            return Snapshot;
        }

        private static OpenACAIRuntimeAttestationSnapshot EnsureSnapshot()
        {
            lock (SyncRoot)
            {
                if (_snapshot == null)
                {
                    _snapshot = CreateSnapshot();
                    OpenACAIModding.SetService(ServiceKey, _snapshot);
                }

                return _snapshot;
            }
        }

        private static OpenACAIRuntimeAttestationSnapshot CreateSnapshot()
        {
            var fileIntegrity = OpenACAIFileIntegrity.Snapshot;
            var compatibilityDependencies = OpenACAICompatibilityDependencies.Snapshot;
            var bepinExPluginCompatibility = OpenACAIBepInExPluginCompatibility.State;
            var runtime = OpenACAIModding.Runtime;
            var inventory = OpenACAIModding.ModInventory;
            var catalog = OpenACAIModding.ModInventoryCatalog;
            var nativeModHost = OpenACAINativeModHost.State;
            var nativeLifecycleRegistry = OpenACAINativeLifecycleRegistry.State;
            var nativeLifecycleDispatcher = OpenACAINativeLifecycleDispatcher.State;
            var nativeLifecycleInvocationBridge = OpenACAINativeLifecycleInvocationBridge.State;
            var nativeLifecycleInvocationAdapter = OpenACAINativeLifecycleInvocationAdapter.State;
            var nativeLifecycleInvocationTokenResolver = OpenACAINativeLifecycleInvocationTokenResolver.State;
            var nativeLifecycleInvocationJournal = OpenACAINativeLifecycleInvocationJournal.State;
            var nativeLifecycleInvocationScheduler = OpenACAINativeLifecycleInvocationScheduler.State;
            var nativeLifecycleInvocationExecutor = OpenACAINativeLifecycleInvocationExecutor.State;
            var shims = OpenACAIRuntime.CompatibilityShimsSnapshot()
                .Values
                .Where(shim => shim != null)
                .OrderBy(shim => shim.Id, StringComparer.OrdinalIgnoreCase)
                .Select(OpenACAIRuntimeAttestationShimSnapshot.FromShim)
                .ToArray();
            var dependencies = (compatibilityDependencies?.Dependencies ?? Array.Empty<OpenACAICompatibilityDependencyEntry>())
                .Where(dependency => dependency != null)
                .OrderBy(dependency => dependency.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .Select(OpenACAIRuntimeAttestationDependencySnapshot.FromDependency)
                .ToArray();

            var loaderReady = OpenACAILoaderCapabilities.IsReady && runtime != null && runtime.IsReady;
            var startupFailed = OpenACAILoaderCapabilities.StartupFailed || (runtime != null && runtime.StartupFailed);
            var publicServicesReady = runtime != null && inventory != null && catalog != null && bepinExPluginCompatibility != null && nativeModHost != null && nativeLifecycleRegistry != null && nativeLifecycleDispatcher != null && nativeLifecycleInvocationBridge != null && nativeLifecycleInvocationAdapter != null && nativeLifecycleInvocationTokenResolver != null && nativeLifecycleInvocationJournal != null && nativeLifecycleInvocationScheduler != null && nativeLifecycleInvocationExecutor != null && fileIntegrity != null && compatibilityDependencies != null;
            var librarySeparationReady = inventory != null
                && catalog != null
                && catalog.ModCount == inventory.Mods.Count
                && inventory.Mods.All(entry => entry != null && !entry.IsLibrary);
            var fileIntegrityReady = fileIntegrity != null && fileIntegrity.Ready;
            var compatibilityDependenciesReady = compatibilityDependencies != null && compatibilityDependencies.AllReady;
            var criticalIssueCount = fileIntegrity == null ? 0 : fileIntegrity.CriticalIssueCount;
            var failedShimCount = shims.Count(shim => shim.Enabled && shim.Failed);
            var readyShimCount = shims.Count(shim => shim.Ready);
            var readyDependencyCount = dependencies.Count(dependency => dependency.Ready);
            var packagedDependencyCount = dependencies.Count(dependency => dependency.Packaged);
            var dependencyWarningCount = compatibilityDependencies == null ? 0 : compatibilityDependencies.Warnings.Count;
            var dependencyBindingConflictCount = dependencies.Count(dependency => dependency.BindingConflict);
            var ready = loaderReady
                && !startupFailed
                && publicServicesReady
                && librarySeparationReady
                && fileIntegrityReady
                && compatibilityDependenciesReady
                && criticalIssueCount == 0
                && failedShimCount == 0;
            var status = startupFailed || failedShimCount > 0 || (fileIntegrity != null && fileIntegrity.CriticalIssueCount > 0)
                ? "FAILED"
                : ready ? "READY" : "CHECK";
            var detail = BuildDetail(
                ready,
                loaderReady,
                publicServicesReady,
                librarySeparationReady,
                fileIntegrityReady,
                compatibilityDependenciesReady,
                failedShimCount);

            var evidenceDigest = BuildEvidenceDigest(
                runtime,
                fileIntegrity,
                compatibilityDependencies,
                bepinExPluginCompatibility,
                nativeModHost,
                nativeLifecycleRegistry,
                nativeLifecycleDispatcher,
                nativeLifecycleInvocationBridge,
                nativeLifecycleInvocationAdapter,
                nativeLifecycleInvocationTokenResolver,
                nativeLifecycleInvocationJournal,
                nativeLifecycleInvocationScheduler,
                nativeLifecycleInvocationExecutor,
                inventory,
                catalog,
                shims,
                dependencies,
                loaderReady,
                startupFailed,
                publicServicesReady,
                librarySeparationReady,
                compatibilityDependenciesReady,
                ready,
                status);

            return new OpenACAIRuntimeAttestationSnapshot(
                EvidenceVersion,
                OpenACAIModding.LoaderName,
                OpenACAIEndnightRuntime.Version,
                OpenACAILoaderCapabilities.CompatibilityMode,
                Environment.Version.ToString(),
                ready,
                status,
                detail,
                publicServicesReady,
                librarySeparationReady,
                loaderReady,
                startupFailed,
                fileIntegrityReady,
                compatibilityDependenciesReady,
                fileIntegrity == null ? null : fileIntegrity.ManifestVersion,
                fileIntegrity == null ? null : fileIntegrity.ManifestSha256,
                fileIntegrity == null ? null : fileIntegrity.CompatibilityManifestSha256,
                fileIntegrity != null && fileIntegrity.ManifestsMatch,
                fileIntegrity == null ? 0 : fileIntegrity.TotalFileCount,
                fileIntegrity == null ? 0 : fileIntegrity.VerifiedFileCount,
                fileIntegrity == null ? 0 : fileIntegrity.IssueFileCount,
                criticalIssueCount,
                catalog == null ? 0 : catalog.ModCount,
                catalog == null ? 0 : catalog.LibraryCount,
                catalog == null ? 0 : catalog.CompatibilityEntryCount,
                catalog == null ? 0 : catalog.LegacyRedLoaderModCount,
                catalog == null ? 0 : catalog.LegacyBepInExPluginCount,
                catalog == null ? 0 : catalog.HostedCount,
                shims.Length,
                readyShimCount,
                failedShimCount,
                shims,
                dependencies.Length,
                readyDependencyCount,
                packagedDependencyCount,
                dependencyWarningCount,
                dependencyBindingConflictCount,
                dependencies,
                evidenceDigest,
                "openacai-attestation-v1:" + evidenceDigest,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static string BuildDetail(
            bool ready,
            bool loaderReady,
            bool publicServicesReady,
            bool librarySeparationReady,
            bool fileIntegrityReady,
            bool compatibilityDependenciesReady,
            int failedShimCount)
        {
            if (ready)
            {
                return "OpenACAI runtime attestation is ready: loader files, public services, compatibility dependencies, BepInEx metadata compatibility, inventory separation, and compatibility shims are disclosed.";
            }

            var issues = new List<string>();
            if (!loaderReady)
            {
                issues.Add("loader is not ready");
            }

            if (!publicServicesReady)
            {
                issues.Add("public SDK services are incomplete");
            }

            if (!librarySeparationReady)
            {
                issues.Add("library inventory is mixed with user-facing mods");
            }

            if (!fileIntegrityReady)
            {
                issues.Add("file integrity is not ready");
            }

            if (!compatibilityDependenciesReady)
            {
                issues.Add("compatibility dependencies are not ready");
            }

            if (failedShimCount > 0)
            {
                issues.Add(failedShimCount + " enabled compatibility shim(s) failed");
            }

            return "OpenACAI runtime attestation is waiting: " + string.Join(", ", issues) + ".";
        }

        private static string BuildEvidenceDigest(
            OpenACAIRuntimeSnapshot runtime,
            OpenACAIFileIntegritySnapshot fileIntegrity,
            OpenACAICompatibilityDependencySnapshot compatibilityDependencies,
            OpenACAIBepInExPluginCompatibilitySnapshot bepinExPluginCompatibility,
            OpenACAINativeModHostSnapshot nativeModHost,
            OpenACAINativeLifecycleRegistrySnapshot nativeLifecycleRegistry,
            OpenACAINativeLifecycleDispatcherSnapshot nativeLifecycleDispatcher,
            OpenACAINativeLifecycleInvocationBridgeSnapshot nativeLifecycleInvocationBridge,
            OpenACAINativeLifecycleInvocationAdapterSnapshot nativeLifecycleInvocationAdapter,
            OpenACAINativeLifecycleInvocationTokenResolverSnapshot nativeLifecycleInvocationTokenResolver,
            OpenACAINativeLifecycleInvocationJournalSnapshot nativeLifecycleInvocationJournal,
            OpenACAINativeLifecycleInvocationSchedulerSnapshot nativeLifecycleInvocationScheduler,
            OpenACAINativeLifecycleInvocationExecutorSnapshot nativeLifecycleInvocationExecutor,
            OpenACAIModInventorySnapshot inventory,
            OpenACAIModInventoryCatalogSnapshot catalog,
            IReadOnlyList<OpenACAIRuntimeAttestationShimSnapshot> shims,
            IReadOnlyList<OpenACAIRuntimeAttestationDependencySnapshot> dependencies,
            bool loaderReady,
            bool startupFailed,
            bool publicServicesReady,
            bool librarySeparationReady,
            bool compatibilityDependenciesReady,
            bool ready,
            string status)
        {
            var evidence = new StringBuilder();
            Append(evidence, "evidenceVersion", EvidenceVersion);
            Append(evidence, "loaderName", OpenACAIModding.LoaderName);
            Append(evidence, "loaderVersion", OpenACAIEndnightRuntime.Version);
            Append(evidence, "compatibilityMode", OpenACAILoaderCapabilities.CompatibilityMode);
            Append(evidence, "dotnetRuntimeVersion", Environment.Version.ToString());
            Append(evidence, "ready", ready);
            Append(evidence, "status", status);
            Append(evidence, "loaderReady", loaderReady);
            Append(evidence, "startupFailed", startupFailed);
            Append(evidence, "runtimeReady", runtime != null && runtime.IsReady);
            Append(evidence, "publicServicesReady", publicServicesReady);
            Append(evidence, "librarySeparationReady", librarySeparationReady);
            Append(evidence, "compatibilityDependenciesReady", compatibilityDependenciesReady);
            Append(evidence, "bepInExPluginCompatibilityReady", bepinExPluginCompatibility != null && bepinExPluginCompatibility.Ready);
            Append(evidence, "bepInExPluginCompatibilityCandidateCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.CandidateCount);
            Append(evidence, "bepInExPluginCompatibilityReadyCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.CompatibilityReadyCount);
            Append(evidence, "bepInExPluginCompatibilityIssueCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.CompatibilityIssueCount);
            Append(evidence, "bepInExPluginCompatibilityMetadataReadCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.MetadataReadCount);
            Append(evidence, "bepInExPluginCompatibilityMetadataIssueCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.MetadataIssueCount);
            Append(evidence, "bepInExPluginCompatibilityAttributeCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.PluginAttributeCount);
            Append(evidence, "bepInExPluginCompatibilityAssemblyReferenceCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.AssemblyReferenceCount);
            Append(evidence, "bepInExPluginCompatibilityResolvedAssemblyReferenceCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ResolvedAssemblyReferenceCount);
            Append(evidence, "bepInExPluginCompatibilityUnresolvedAssemblyReferenceCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.UnresolvedAssemblyReferenceCount);
            Append(evidence, "bepInExPluginCompatibilityFirstUnresolvedAssemblyReference", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstUnresolvedCandidateAssemblyReference == null ? "" : bepinExPluginCompatibility.FirstUnresolvedCandidateAssemblyReference.AssemblyName);
            Append(evidence, "bepInExPluginCompatibilityFirstUnresolvedAssemblyReferenceVersion", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstUnresolvedCandidateAssemblyReference == null ? "" : bepinExPluginCompatibility.FirstUnresolvedCandidateAssemblyReference.RequiredVersion);
            Append(evidence, "bepInExPluginCompatibilityExecutionPlanCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ExecutionPlanCount);
            Append(evidence, "bepInExPluginCompatibilityExecutionPlanReadyCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ExecutionPlanReadyCount);
            Append(evidence, "bepInExPluginCompatibilityExecutionPlanIssueCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ExecutionPlanIssueCount);
            Append(evidence, "bepInExPluginCompatibilityFirstExecutionPlanIssueType", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstExecutionPlanIssue == null ? "" : bepinExPluginCompatibility.FirstExecutionPlanIssue.PluginTypeName);
            Append(evidence, "bepInExPluginCompatibilityFirstExecutionPlanIssueCode", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstExecutionPlanIssue == null ? "" : bepinExPluginCompatibility.FirstExecutionPlanIssue.IssueCode);
            Append(evidence, "bepInExPluginCompatibilityLifecycleMethodCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LifecycleMethodCount);
            Append(evidence, "bepInExPluginCompatibilityLifecycleMethodReadyCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LifecycleMethodReadyCount);
            Append(evidence, "bepInExPluginCompatibilityLifecycleMethodIssueCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LifecycleMethodIssueCount);
            Append(evidence, "bepInExPluginCompatibilityFirstLifecycleMethodIssue", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstLifecycleMethodIssue == null ? "" : bepinExPluginCompatibility.FirstLifecycleMethodIssue.MethodName);
            Append(evidence, "bepInExPluginCompatibilityFirstLifecycleMethodIssueCode", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstLifecycleMethodIssue == null ? "" : bepinExPluginCompatibility.FirstLifecycleMethodIssue.IssueCode);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessReadyCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessReadyCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessIssueCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessIssueCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessAttributeCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessAttributeCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessUnsafeLocationCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessUnsafeLocationCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessAssemblyReferenceCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessAssemblyReferenceCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessResolvedAssemblyReferenceCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessResolvedAssemblyReferenceCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessUnresolvedAssemblyReferenceCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessUnresolvedAssemblyReferenceCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessFirstUnresolvedAssemblyReference", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstUnresolvedParserHarnessAssemblyReference == null ? "" : bepinExPluginCompatibility.FirstUnresolvedParserHarnessAssemblyReference.AssemblyName);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessFirstUnresolvedAssemblyReferenceVersion", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstUnresolvedParserHarnessAssemblyReference == null ? "" : bepinExPluginCompatibility.FirstUnresolvedParserHarnessAssemblyReference.RequiredVersion);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessExecutionPlanCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessExecutionPlanCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessExecutionPlanReadyCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessExecutionPlanReadyCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessExecutionPlanIssueCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessExecutionPlanIssueCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessFirstExecutionPlanIssueType", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstParserHarnessExecutionPlanIssue == null ? "" : bepinExPluginCompatibility.FirstParserHarnessExecutionPlanIssue.PluginTypeName);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessFirstExecutionPlanIssueCode", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstParserHarnessExecutionPlanIssue == null ? "" : bepinExPluginCompatibility.FirstParserHarnessExecutionPlanIssue.IssueCode);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessLifecycleMethodCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessLifecycleMethodCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessLifecycleMethodReadyCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessLifecycleMethodReadyCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessLifecycleMethodIssueCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.ParserHarnessLifecycleMethodIssueCount);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessFirstLifecycleMethodIssue", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstParserHarnessLifecycleMethodIssue == null ? "" : bepinExPluginCompatibility.FirstParserHarnessLifecycleMethodIssue.MethodName);
            Append(evidence, "bepInExPluginCompatibilityParserHarnessFirstLifecycleMethodIssueCode", bepinExPluginCompatibility == null || bepinExPluginCompatibility.FirstParserHarnessLifecycleMethodIssue == null ? "" : bepinExPluginCompatibility.FirstParserHarnessLifecycleMethodIssue.IssueCode);
            Append(evidence, "bepInExPluginCompatibilityPositiveMetadataParserVerified", bepinExPluginCompatibility != null && bepinExPluginCompatibility.PositiveMetadataParserVerified);
            Append(evidence, "bepInExPluginCompatibilityLoadEligibleCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LoadEligibleCount);
            Append(evidence, "bepInExPluginCompatibilityLoadGateOpenCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LoadGateOpenCount);
            Append(evidence, "bepInExPluginCompatibilityLoadGateBlockedCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LoadGateBlockedCount);
            Append(evidence, "bepInExPluginCompatibilityLoadAttemptRequestCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LoadAttemptRequestCount);
            Append(evidence, "bepInExPluginCompatibilityAcceptedLoadAttemptCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.AcceptedLoadAttemptCount);
            Append(evidence, "bepInExPluginCompatibilityBlockedLoadAttemptCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.BlockedLoadAttemptCount);
            Append(evidence, "bepInExPluginCompatibilityRejectedLoadAttemptCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.RejectedLoadAttemptCount);
            Append(evidence, "bepInExPluginCompatibilityLastLoadAttemptReason", bepinExPluginCompatibility == null ? "" : bepinExPluginCompatibility.LastLoadAttemptReason);
            Append(evidence, "bepInExPluginCompatibilityLoadedByOpenAcaiCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.PluginLoadedByOpenAcaiCount);
            Append(evidence, "bepInExPluginCompatibilityRuntimePluginLoadingEnabled", bepinExPluginCompatibility != null && bepinExPluginCompatibility.RuntimePluginLoadingEnabled);
            Append(evidence, "bepInExPluginCompatibilityLibraryExcludedCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.LibraryExcludedCount);
            Append(evidence, "bepInExPluginCompatibilityOpenAcaiNativeExcludedCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.OpenAcaiNativeExcludedCount);
            Append(evidence, "bepInExPluginCompatibilityRedLoaderExcludedCount", bepinExPluginCompatibility == null ? 0 : bepinExPluginCompatibility.RedLoaderCompatibilityExcludedCount);
            Append(evidence, "nativeModHostReady", nativeModHost != null && nativeModHost.Ready);
            Append(evidence, "nativeModHostCandidateCount", nativeModHost == null ? 0 : nativeModHost.CandidateCount);
            Append(evidence, "nativeModHostHostableCandidateCount", nativeModHost == null ? 0 : nativeModHost.HostableCandidateCount);
            Append(evidence, "nativeModHostLibraryExcludedCount", nativeModHost == null ? 0 : nativeModHost.LibraryExcludedCount);
            Append(evidence, "nativeModHostAssemblyMetadataReadCount", nativeModHost == null ? 0 : nativeModHost.AssemblyMetadataReadCount);
            Append(evidence, "nativeModHostAssemblyMetadataIssueCount", nativeModHost == null ? 0 : nativeModHost.AssemblyMetadataIssueCount);
            Append(evidence, "nativeModHostAssemblyReferenceCount", nativeModHost == null ? 0 : nativeModHost.AssemblyReferenceCount);
            Append(evidence, "nativeModHostResolvedAssemblyReferenceCount", nativeModHost == null ? 0 : nativeModHost.ResolvedAssemblyReferenceCount);
            Append(evidence, "nativeModHostUnresolvedAssemblyReferenceCount", nativeModHost == null ? 0 : nativeModHost.UnresolvedAssemblyReferenceCount);
            Append(evidence, "nativeModHostExecutionPlanReadyCount", nativeModHost == null ? 0 : nativeModHost.ExecutionPlanReadyCount);
            Append(evidence, "nativeModHostExecutionPlanIssueCount", nativeModHost == null ? 0 : nativeModHost.ExecutionPlanIssueCount);
            Append(evidence, "nativeModHostLifecycleHookDeclarationCount", nativeModHost == null ? 0 : nativeModHost.LifecycleHookDeclarationCount);
            Append(evidence, "nativeModHostLifecycleHookMethodReadyCount", nativeModHost == null ? 0 : nativeModHost.LifecycleHookMethodReadyCount);
            Append(evidence, "nativeModHostLifecycleHookMethodIssueCount", nativeModHost == null ? 0 : nativeModHost.LifecycleHookMethodIssueCount);
            Append(evidence, "nativeModHostEntrypointTypeMatchCount", nativeModHost == null ? 0 : nativeModHost.EntrypointTypeMatchCount);
            Append(evidence, "nativeModHostEntrypointMethodMatchCount", nativeModHost == null ? 0 : nativeModHost.EntrypointMethodMatchCount);
            Append(evidence, "nativeModHostValidationIssueCount", nativeModHost == null ? 0 : nativeModHost.ValidationIssueCount);
            Append(evidence, "nativeModHostRuntimeExecutionEnabled", nativeModHost != null && nativeModHost.RuntimeExecutionEnabled);
            Append(evidence, "nativeLifecycleRegistryReady", nativeLifecycleRegistry != null && nativeLifecycleRegistry.Ready);
            Append(evidence, "nativeLifecycleRegistryRegistrationCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.RegistrationCount);
            Append(evidence, "nativeLifecycleRegistryPlannedHookCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.PlannedHookCount);
            Append(evidence, "nativeLifecycleRegistryDeferredHookCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.DeferredHookCount);
            Append(evidence, "nativeLifecycleRegistryRegistrationIssueCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.RegistrationIssueCount);
            Append(evidence, "nativeLifecycleRegistryInvocationGateOpenCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.InvocationGateOpenCount);
            Append(evidence, "nativeLifecycleRegistryInvocationGateBlockedCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.InvocationGateBlockedCount);
            Append(evidence, "nativeLifecycleRegistryDryRunEvaluationCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.DryRunEvaluationCount);
            Append(evidence, "nativeLifecycleRegistryDryRunReadyCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.DryRunReadyCount);
            Append(evidence, "nativeLifecycleRegistryDryRunBlockedCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.DryRunBlockedCount);
            Append(evidence, "nativeLifecycleRegistryDryRunIssueCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.DryRunIssueCount);
            Append(evidence, "nativeLifecycleRegistryHookMethodReadyCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.HookMethodReadyCount);
            Append(evidence, "nativeLifecycleRegistryHookMethodIssueCount", nativeLifecycleRegistry == null ? 0 : nativeLifecycleRegistry.HookMethodIssueCount);
            Append(evidence, "nativeLifecycleRegistryRuntimeInvocationEnabled", nativeLifecycleRegistry != null && nativeLifecycleRegistry.RuntimeInvocationEnabled);
            Append(evidence, "nativeLifecycleDispatcherReady", nativeLifecycleDispatcher != null && nativeLifecycleDispatcher.Ready);
            Append(evidence, "nativeLifecycleDispatcherTargetCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchTargetCount);
            Append(evidence, "nativeLifecycleDispatcherMetadataReadyCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchMetadataReadyCount);
            Append(evidence, "nativeLifecycleDispatcherIssueCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchIssueCount);
            Append(evidence, "nativeLifecycleDispatcherGateOpenCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchGateOpenCount);
            Append(evidence, "nativeLifecycleDispatcherGateBlockedCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchGateBlockedCount);
            Append(evidence, "nativeLifecycleDispatcherIntentCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchIntentCount);
            Append(evidence, "nativeLifecycleDispatcherIntentMetadataReadyCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchIntentMetadataReadyCount);
            Append(evidence, "nativeLifecycleDispatcherQueuedIntentCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.QueuedDispatchIntentCount);
            Append(evidence, "nativeLifecycleDispatcherIntentIssueCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchIntentIssueCount);
            Append(evidence, "nativeLifecycleDispatcherIntentGateOpenCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchIntentGateOpenCount);
            Append(evidence, "nativeLifecycleDispatcherIntentGateBlockedCount", nativeLifecycleDispatcher == null ? 0 : nativeLifecycleDispatcher.DispatchIntentGateBlockedCount);
            Append(evidence, "nativeLifecycleDispatcherRuntimeDispatchEnabled", nativeLifecycleDispatcher != null && nativeLifecycleDispatcher.RuntimeDispatchEnabled);
            Append(evidence, "nativeLifecycleInvocationBridgeReady", nativeLifecycleInvocationBridge != null && nativeLifecycleInvocationBridge.Ready);
            Append(evidence, "nativeLifecycleInvocationBridgeRequestCount", nativeLifecycleInvocationBridge == null ? 0 : nativeLifecycleInvocationBridge.InvocationRequestCount);
            Append(evidence, "nativeLifecycleInvocationBridgeAdapterReadyCount", nativeLifecycleInvocationBridge == null ? 0 : nativeLifecycleInvocationBridge.AdapterReadyCount);
            Append(evidence, "nativeLifecycleInvocationBridgeIssueCount", nativeLifecycleInvocationBridge == null ? 0 : nativeLifecycleInvocationBridge.InvocationIssueCount);
            Append(evidence, "nativeLifecycleInvocationBridgeGateOpenCount", nativeLifecycleInvocationBridge == null ? 0 : nativeLifecycleInvocationBridge.InvocationGateOpenCount);
            Append(evidence, "nativeLifecycleInvocationBridgeGateBlockedCount", nativeLifecycleInvocationBridge == null ? 0 : nativeLifecycleInvocationBridge.InvocationGateBlockedCount);
            Append(evidence, "nativeLifecycleInvocationBridgeRuntimeInvocationEnabled", nativeLifecycleInvocationBridge != null && nativeLifecycleInvocationBridge.RuntimeInvocationEnabled);
            Append(evidence, "nativeLifecycleInvocationAdapterReady", nativeLifecycleInvocationAdapter != null && nativeLifecycleInvocationAdapter.Ready);
            Append(evidence, "nativeLifecycleInvocationAdapterBindingCount", nativeLifecycleInvocationAdapter == null ? 0 : nativeLifecycleInvocationAdapter.BindingCount);
            Append(evidence, "nativeLifecycleInvocationAdapterBindingReadyCount", nativeLifecycleInvocationAdapter == null ? 0 : nativeLifecycleInvocationAdapter.BindingReadyCount);
            Append(evidence, "nativeLifecycleInvocationAdapterBindingIssueCount", nativeLifecycleInvocationAdapter == null ? 0 : nativeLifecycleInvocationAdapter.BindingIssueCount);
            Append(evidence, "nativeLifecycleInvocationAdapterTokenIssuedCount", nativeLifecycleInvocationAdapter == null ? 0 : nativeLifecycleInvocationAdapter.BindingTokenIssuedCount);
            Append(evidence, "nativeLifecycleInvocationAdapterInvocationEligibleCount", nativeLifecycleInvocationAdapter == null ? 0 : nativeLifecycleInvocationAdapter.InvocationEligibleCount);
            Append(evidence, "nativeLifecycleInvocationAdapterGateOpenCount", nativeLifecycleInvocationAdapter == null ? 0 : nativeLifecycleInvocationAdapter.InvocationGateOpenCount);
            Append(evidence, "nativeLifecycleInvocationAdapterGateBlockedCount", nativeLifecycleInvocationAdapter == null ? 0 : nativeLifecycleInvocationAdapter.InvocationGateBlockedCount);
            Append(evidence, "nativeLifecycleInvocationAdapterRuntimeInvocationEnabled", nativeLifecycleInvocationAdapter != null && nativeLifecycleInvocationAdapter.RuntimeInvocationEnabled);
            Append(evidence, "nativeLifecycleInvocationTokenResolverReady", nativeLifecycleInvocationTokenResolver != null && nativeLifecycleInvocationTokenResolver.Ready);
            Append(evidence, "nativeLifecycleInvocationTokenResolverResolutionCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.TokenResolutionCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverResolvedCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.TokenResolvedCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverIssueCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.TokenIssueCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverInvocationEligibleCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.InvocationEligibleCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverGateOpenCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.InvocationGateOpenCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverGateBlockedCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.InvocationGateBlockedCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverLookupRequestCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.LookupRequestCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverAcceptedLookupCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.AcceptedLookupCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverRejectedLookupCount", nativeLifecycleInvocationTokenResolver == null ? 0 : nativeLifecycleInvocationTokenResolver.RejectedLookupCount);
            Append(evidence, "nativeLifecycleInvocationTokenResolverLastLookupStatus", nativeLifecycleInvocationTokenResolver == null ? "" : nativeLifecycleInvocationTokenResolver.LastLookupStatus);
            Append(evidence, "nativeLifecycleInvocationTokenResolverLastLookupReason", nativeLifecycleInvocationTokenResolver == null ? "" : nativeLifecycleInvocationTokenResolver.LastLookupReason);
            Append(evidence, "nativeLifecycleInvocationTokenResolverRuntimeInvocationEnabled", nativeLifecycleInvocationTokenResolver != null && nativeLifecycleInvocationTokenResolver.RuntimeInvocationEnabled);
            Append(evidence, "nativeLifecycleInvocationJournalReady", nativeLifecycleInvocationJournal != null && nativeLifecycleInvocationJournal.Ready);
            Append(evidence, "nativeLifecycleInvocationJournalEntryCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.JournalEntryCount);
            Append(evidence, "nativeLifecycleInvocationJournalJournaledEntryCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.JournaledEntryCount);
            Append(evidence, "nativeLifecycleInvocationJournalIssueCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.JournalIssueCount);
            Append(evidence, "nativeLifecycleInvocationJournalReplayEligibleCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.ReplayEligibleCount);
            Append(evidence, "nativeLifecycleInvocationJournalReplayGateOpenCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.ReplayGateOpenCount);
            Append(evidence, "nativeLifecycleInvocationJournalReplayGateBlockedCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.ReplayGateBlockedCount);
            Append(evidence, "nativeLifecycleInvocationJournalReplayPlanRequestCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.ReplayPlanRequestCount);
            Append(evidence, "nativeLifecycleInvocationJournalAcceptedReplayPlanCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.AcceptedReplayPlanCount);
            Append(evidence, "nativeLifecycleInvocationJournalRejectedReplayPlanCount", nativeLifecycleInvocationJournal == null ? 0 : nativeLifecycleInvocationJournal.RejectedReplayPlanCount);
            Append(evidence, "nativeLifecycleInvocationJournalLastReplayStatus", nativeLifecycleInvocationJournal == null ? "" : nativeLifecycleInvocationJournal.LastReplayStatus);
            Append(evidence, "nativeLifecycleInvocationJournalLastReplayReason", nativeLifecycleInvocationJournal == null ? "" : nativeLifecycleInvocationJournal.LastReplayReason);
            Append(evidence, "nativeLifecycleInvocationJournalRuntimeInvocationEnabled", nativeLifecycleInvocationJournal != null && nativeLifecycleInvocationJournal.RuntimeInvocationEnabled);
            Append(evidence, "nativeLifecycleInvocationSchedulerReady", nativeLifecycleInvocationScheduler != null && nativeLifecycleInvocationScheduler.Ready);
            Append(evidence, "nativeLifecycleInvocationSchedulerBatchCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.ScheduleBatchCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerEntryCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.ScheduleEntryCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerScheduledEntryCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.ScheduledEntryCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerIssueCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.ScheduleIssueCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerEligibleCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.ScheduleEligibleCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerGateOpenCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.ScheduleGateOpenCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerGateBlockedCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.ScheduleGateBlockedCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerPlanRequestCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.SchedulePlanRequestCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerAcceptedPlanCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.AcceptedSchedulePlanCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerRejectedPlanCount", nativeLifecycleInvocationScheduler == null ? 0 : nativeLifecycleInvocationScheduler.RejectedSchedulePlanCount);
            Append(evidence, "nativeLifecycleInvocationSchedulerLastScheduleStatus", nativeLifecycleInvocationScheduler == null ? "" : nativeLifecycleInvocationScheduler.LastScheduleStatus);
            Append(evidence, "nativeLifecycleInvocationSchedulerLastScheduleReason", nativeLifecycleInvocationScheduler == null ? "" : nativeLifecycleInvocationScheduler.LastScheduleReason);
            Append(evidence, "nativeLifecycleInvocationSchedulerRuntimeInvocationEnabled", nativeLifecycleInvocationScheduler != null && nativeLifecycleInvocationScheduler.RuntimeInvocationEnabled);
            Append(evidence, "nativeLifecycleInvocationExecutorReady", nativeLifecycleInvocationExecutor != null && nativeLifecycleInvocationExecutor.Ready);
            Append(evidence, "nativeLifecycleInvocationExecutorBatchCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionBatchCount);
            Append(evidence, "nativeLifecycleInvocationExecutorDecisionCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionDecisionCount);
            Append(evidence, "nativeLifecycleInvocationExecutorPlannedCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionPlannedCount);
            Append(evidence, "nativeLifecycleInvocationExecutorIssueCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionIssueCount);
            Append(evidence, "nativeLifecycleInvocationExecutorEligibleCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionEligibleCount);
            Append(evidence, "nativeLifecycleInvocationExecutorGateOpenCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionGateOpenCount);
            Append(evidence, "nativeLifecycleInvocationExecutorGateBlockedCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionGateBlockedCount);
            Append(evidence, "nativeLifecycleInvocationExecutorMethodInvocationCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.MethodInvocationCount);
            Append(evidence, "nativeLifecycleInvocationExecutorPlanRequestCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.ExecutionPlanRequestCount);
            Append(evidence, "nativeLifecycleInvocationExecutorAcceptedPlanCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.AcceptedExecutionPlanCount);
            Append(evidence, "nativeLifecycleInvocationExecutorRejectedPlanCount", nativeLifecycleInvocationExecutor == null ? 0 : nativeLifecycleInvocationExecutor.RejectedExecutionPlanCount);
            Append(evidence, "nativeLifecycleInvocationExecutorLastExecutionStatus", nativeLifecycleInvocationExecutor == null ? "" : nativeLifecycleInvocationExecutor.LastExecutionStatus);
            Append(evidence, "nativeLifecycleInvocationExecutorLastExecutionReason", nativeLifecycleInvocationExecutor == null ? "" : nativeLifecycleInvocationExecutor.LastExecutionReason);
            Append(evidence, "nativeLifecycleInvocationExecutorRuntimeInvocationEnabled", nativeLifecycleInvocationExecutor != null && nativeLifecycleInvocationExecutor.RuntimeInvocationEnabled);
            Append(evidence, "dependencyCount", dependencies == null ? 0 : dependencies.Count);
            Append(evidence, "readyDependencyCount", dependencies == null ? 0 : dependencies.Count(dependency => dependency.Ready));
            Append(evidence, "packagedDependencyCount", dependencies == null ? 0 : dependencies.Count(dependency => dependency.Packaged));
            Append(evidence, "dependencyWarningCount", compatibilityDependencies == null ? 0 : compatibilityDependencies.Warnings.Count);
            Append(evidence, "dependencyBindingConflictCount", dependencies == null ? 0 : dependencies.Count(dependency => dependency.BindingConflict));
            Append(evidence, "manifestVersion", fileIntegrity == null ? null : fileIntegrity.ManifestVersion);
            Append(evidence, "manifestSha256", fileIntegrity == null ? null : fileIntegrity.ManifestSha256);
            Append(evidence, "compatibilityManifestSha256", fileIntegrity == null ? null : fileIntegrity.CompatibilityManifestSha256);
            Append(evidence, "manifestsMatch", fileIntegrity != null && fileIntegrity.ManifestsMatch);
            Append(evidence, "totalFileCount", fileIntegrity == null ? 0 : fileIntegrity.TotalFileCount);
            Append(evidence, "verifiedFileCount", fileIntegrity == null ? 0 : fileIntegrity.VerifiedFileCount);
            Append(evidence, "issueFileCount", fileIntegrity == null ? 0 : fileIntegrity.IssueFileCount);
            Append(evidence, "criticalIssueCount", fileIntegrity == null ? 0 : fileIntegrity.CriticalIssueCount);
            Append(evidence, "inventoryEntryCount", inventory == null ? 0 : inventory.Entries.Count);
            Append(evidence, "modCount", catalog == null ? 0 : catalog.ModCount);
            Append(evidence, "libraryCount", catalog == null ? 0 : catalog.LibraryCount);
            Append(evidence, "compatibilityEntryCount", catalog == null ? 0 : catalog.CompatibilityEntryCount);
            Append(evidence, "legacyRedLoaderModCount", catalog == null ? 0 : catalog.LegacyRedLoaderModCount);
            Append(evidence, "legacyBepInExPluginCount", catalog == null ? 0 : catalog.LegacyBepInExPluginCount);
            Append(evidence, "hostedCount", catalog == null ? 0 : catalog.HostedCount);

            foreach (var dependency in dependencies ?? Array.Empty<OpenACAIRuntimeAttestationDependencySnapshot>())
            {
                Append(evidence, "dependency.assemblyName", dependency.AssemblyName);
                Append(evidence, "dependency.requiredVersion", dependency.RequiredVersion);
                Append(evidence, "dependency.bestVersion", dependency.BestVersion);
                Append(evidence, "dependency.packaged", dependency.Packaged);
                Append(evidence, "dependency.ready", dependency.Ready);
                Append(evidence, "dependency.bindingConflict", dependency.BindingConflict);
            }

            foreach (var shim in shims ?? Array.Empty<OpenACAIRuntimeAttestationShimSnapshot>())
            {
                Append(evidence, "shim.id", shim.Id);
                Append(evidence, "shim.status", shim.Status);
                Append(evidence, "shim.mode", shim.Mode);
                Append(evidence, "shim.enabled", shim.Enabled);
                Append(evidence, "shim.ready", shim.Ready);
                Append(evidence, "shim.failed", shim.Failed);
            }

            using (var sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(evidence.ToString())));
            }
        }

        private static void Append(StringBuilder builder, string key, object value)
        {
            builder.Append(key).Append('=').Append(value ?? "").Append('\n');
        }
    }

    public sealed class OpenACAIRuntimeAttestationSnapshot
    {
        private readonly IReadOnlyList<OpenACAIRuntimeAttestationShimSnapshot> _compatibilityShims;
        private readonly IReadOnlyList<OpenACAIRuntimeAttestationDependencySnapshot> _dependencies;

        internal OpenACAIRuntimeAttestationSnapshot(
            string evidenceVersion,
            string loaderName,
            string loaderVersion,
            string compatibilityMode,
            string dotnetRuntimeVersion,
            bool ready,
            string status,
            string detail,
            bool publicServicesReady,
            bool librarySeparationReady,
            bool loaderReady,
            bool startupFailed,
            bool fileIntegrityReady,
            bool compatibilityDependenciesReady,
            string manifestVersion,
            string manifestSha256,
            string compatibilityManifestSha256,
            bool manifestsMatch,
            int totalFileCount,
            int verifiedFileCount,
            int issueFileCount,
            int criticalIssueCount,
            int modCount,
            int libraryCount,
            int compatibilityEntryCount,
            int legacyRedLoaderModCount,
            int legacyBepInExPluginCount,
            int hostedCount,
            int compatibilityShimCount,
            int readyShimCount,
            int failedShimCount,
            IEnumerable<OpenACAIRuntimeAttestationShimSnapshot> compatibilityShims,
            int dependencyCount,
            int readyDependencyCount,
            int packagedDependencyCount,
            int dependencyWarningCount,
            int dependencyBindingConflictCount,
            IEnumerable<OpenACAIRuntimeAttestationDependencySnapshot> dependencies,
            string evidenceDigestSha256,
            string attestationToken,
            string generatedUtc)
        {
            EvidenceVersion = evidenceVersion ?? OpenACAIRuntimeAttestation.EvidenceVersion;
            LoaderName = loaderName ?? OpenACAIModding.LoaderName;
            LoaderVersion = loaderVersion ?? "";
            CompatibilityMode = compatibilityMode ?? "";
            DotnetRuntimeVersion = dotnetRuntimeVersion ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            PublicServicesReady = publicServicesReady;
            LibrarySeparationReady = librarySeparationReady;
            LoaderReady = loaderReady;
            StartupFailed = startupFailed;
            FileIntegrityReady = fileIntegrityReady;
            CompatibilityDependenciesReady = compatibilityDependenciesReady;
            ManifestVersion = manifestVersion;
            ManifestSha256 = manifestSha256;
            CompatibilityManifestSha256 = compatibilityManifestSha256;
            ManifestsMatch = manifestsMatch;
            TotalFileCount = totalFileCount;
            VerifiedFileCount = verifiedFileCount;
            IssueFileCount = issueFileCount;
            CriticalIssueCount = criticalIssueCount;
            ModCount = modCount;
            LibraryCount = libraryCount;
            CompatibilityEntryCount = compatibilityEntryCount;
            LegacyRedLoaderModCount = legacyRedLoaderModCount;
            LegacyBepInExPluginCount = legacyBepInExPluginCount;
            HostedCount = hostedCount;
            CompatibilityShimCount = compatibilityShimCount;
            ReadyShimCount = readyShimCount;
            FailedShimCount = failedShimCount;
            _compatibilityShims = (compatibilityShims ?? Array.Empty<OpenACAIRuntimeAttestationShimSnapshot>())
                .Where(shim => shim != null)
                .ToArray();
            DependencyCount = dependencyCount;
            ReadyDependencyCount = readyDependencyCount;
            PackagedDependencyCount = packagedDependencyCount;
            DependencyWarningCount = dependencyWarningCount;
            DependencyBindingConflictCount = dependencyBindingConflictCount;
            _dependencies = (dependencies ?? Array.Empty<OpenACAIRuntimeAttestationDependencySnapshot>())
                .Where(dependency => dependency != null)
                .ToArray();
            EvidenceDigestSha256 = evidenceDigestSha256 ?? "";
            AttestationToken = attestationToken ?? "";
            GeneratedUtc = generatedUtc;
        }

        public int Schema => 1;
        public string ServiceKey => OpenACAIRuntimeAttestation.ServiceKey;
        public string EvidenceVersion { get; }
        public string LoaderName { get; }
        public string LoaderVersion { get; }
        public string CompatibilityMode { get; }
        public string DotnetRuntimeVersion { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool PublicServicesReady { get; }
        public bool LibrarySeparationReady { get; }
        public bool LoaderReady { get; }
        public bool StartupFailed { get; }
        public bool FileIntegrityReady { get; }
        public bool CompatibilityDependenciesReady { get; }
        public string ManifestVersion { get; }
        public string ManifestSha256 { get; }
        public string CompatibilityManifestSha256 { get; }
        public bool ManifestsMatch { get; }
        public int TotalFileCount { get; }
        public int VerifiedFileCount { get; }
        public int IssueFileCount { get; }
        public int CriticalIssueCount { get; }
        public int ModCount { get; }
        public int LibraryCount { get; }
        public int CompatibilityEntryCount { get; }
        public int LegacyRedLoaderModCount { get; }
        public int LegacyBepInExPluginCount { get; }
        public int HostedCount { get; }
        public int CompatibilityShimCount { get; }
        public int ReadyShimCount { get; }
        public int FailedShimCount { get; }
        public int DependencyCount { get; }
        public int ReadyDependencyCount { get; }
        public int PackagedDependencyCount { get; }
        public int DependencyWarningCount { get; }
        public int DependencyBindingConflictCount { get; }
        public string EvidenceDigestSha256 { get; }
        public string AttestationToken { get; }
        public string GeneratedUtc { get; }
        public IReadOnlyList<OpenACAIRuntimeAttestationShimSnapshot> CompatibilityShims => _compatibilityShims;
        public IReadOnlyList<OpenACAIRuntimeAttestationDependencySnapshot> Dependencies => _dependencies;
    }

    public sealed class OpenACAIRuntimeAttestationDependencySnapshot
    {
        private OpenACAIRuntimeAttestationDependencySnapshot(
            string assemblyName,
            string requiredVersion,
            string bestVersion,
            bool packaged,
            bool ready,
            bool bindingConflict)
        {
            AssemblyName = assemblyName ?? "";
            RequiredVersion = requiredVersion ?? "";
            BestVersion = bestVersion ?? "";
            Packaged = packaged;
            Ready = ready;
            BindingConflict = bindingConflict;
        }

        public string AssemblyName { get; }
        public string RequiredVersion { get; }
        public string BestVersion { get; }
        public bool Packaged { get; }
        public bool Ready { get; }
        public bool BindingConflict { get; }

        internal static OpenACAIRuntimeAttestationDependencySnapshot FromDependency(OpenACAICompatibilityDependencyEntry dependency)
        {
            return new OpenACAIRuntimeAttestationDependencySnapshot(
                dependency.AssemblyName,
                dependency.RequiredVersion,
                dependency.BestVersion,
                dependency.Packaged,
                dependency.Ready,
                dependency.BindingConflict);
        }
    }

    public sealed class OpenACAIRuntimeAttestationShimSnapshot
    {
        private OpenACAIRuntimeAttestationShimSnapshot(
            string id,
            string displayName,
            string compatibilityTarget,
            string status,
            string mode,
            bool enabled,
            bool ready,
            bool failed)
        {
            Id = id ?? "";
            DisplayName = displayName ?? "";
            CompatibilityTarget = compatibilityTarget ?? "";
            Status = status ?? "";
            Mode = mode ?? "";
            Enabled = enabled;
            Ready = ready;
            Failed = failed;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string CompatibilityTarget { get; }
        public string Status { get; }
        public string Mode { get; }
        public bool Enabled { get; }
        public bool Ready { get; }
        public bool Failed { get; }

        internal static OpenACAIRuntimeAttestationShimSnapshot FromShim(OpenACAICompatibilityShimSnapshot shim)
        {
            return new OpenACAIRuntimeAttestationShimSnapshot(
                shim.Id,
                shim.DisplayName,
                shim.CompatibilityTarget,
                shim.Status,
                shim.Mode,
                shim.Enabled,
                shim.Ready,
                shim.Failed);
        }
    }
}
