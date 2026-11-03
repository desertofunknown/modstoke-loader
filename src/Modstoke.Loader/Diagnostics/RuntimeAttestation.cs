// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public Modstoke runtime attestation surface. This is a disclosed, unsigned
// digest of loader-owned runtime signals for protected servers and companion
// tools; it does not embed private anti-cheat behavior.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    public static class RuntimeAttestation
    {
        public const string ServiceKey = Modding.RuntimeAttestationServiceKey;
        public const string EvidenceVersion = "openacai-runtime-attestation-v1";

        private static readonly object SyncRoot = new object();
        private static RuntimeAttestationSnapshot _snapshot;

        public static RuntimeAttestationSnapshot Snapshot => EnsureSnapshot();
        public static bool Ready => Snapshot.Ready;

        public static RuntimeAttestationSnapshot Refresh()
        {
            lock (SyncRoot)
            {
                _snapshot = CreateSnapshot();
                Modding.SetService(ServiceKey, _snapshot);
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

        private static RuntimeAttestationSnapshot EnsureSnapshot()
        {
            lock (SyncRoot)
            {
                if (_snapshot == null)
                {
                    _snapshot = CreateSnapshot();
                    Modding.SetService(ServiceKey, _snapshot);
                }

                return _snapshot;
            }
        }

        private static RuntimeAttestationSnapshot CreateSnapshot()
        {
            var fileIntegrity = FileIntegrity.Snapshot;
            var compatibilityDependencies = CompatibilityDependencies.Snapshot;
            var bepinExPluginCompatibility = BepInExPluginCompatibility.State;
            var runtime = Modding.Runtime;
            var inventory = Modding.ModInventory;
            var catalog = Modding.ModInventoryCatalog;
            var nativeModHost = ModHost.State;
            var nativeModRuntime = ModRuntime.State;
            var shims = LoaderRuntime.CompatibilityShimsSnapshot()
                .Values
                .Where(shim => shim != null)
                .OrderBy(shim => shim.Id, StringComparer.OrdinalIgnoreCase)
                .Select(RuntimeAttestationShimSnapshot.FromShim)
                .ToArray();
            var dependencies = (compatibilityDependencies?.Dependencies ?? Array.Empty<CompatibilityDependencyEntry>())
                .Where(dependency => dependency != null)
                .OrderBy(dependency => dependency.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .Select(RuntimeAttestationDependencySnapshot.FromDependency)
                .ToArray();

            var loaderReady = LoaderCapabilities.IsReady && runtime != null && runtime.IsReady;
            var startupFailed = LoaderCapabilities.StartupFailed || (runtime != null && runtime.StartupFailed);
            var publicServicesReady = runtime != null && inventory != null && catalog != null && bepinExPluginCompatibility != null && nativeModHost != null && fileIntegrity != null && compatibilityDependencies != null;
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
            var harmonyPatchesReady = HarmonySupport.CreateState()?.SafeForHarmonyPatches == true;
            var ready = loaderReady
                && !startupFailed
                && publicServicesReady
                && librarySeparationReady
                && fileIntegrityReady
                && compatibilityDependenciesReady
                && harmonyPatchesReady
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
                harmonyPatchesReady,
                failedShimCount);

            var evidenceDigest = BuildEvidenceDigest(
                runtime,
                fileIntegrity,
                compatibilityDependencies,
                bepinExPluginCompatibility,
                nativeModHost,
                nativeModRuntime,
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

            return new RuntimeAttestationSnapshot(
                EvidenceVersion,
                Modding.LoaderName,
                RuntimeEntryPoint.Version,
                LoaderCapabilities.CompatibilityMode,
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
            bool harmonyPatchesReady,
            int failedShimCount)
        {
            if (ready)
            {
                return "Modstoke runtime attestation is ready: loader files, public services, compatibility dependencies, BepInEx metadata compatibility, inventory separation, and compatibility shims are disclosed.";
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

            if (!harmonyPatchesReady)
            {
                issues.Add("Harmony patch support did not pass the safety probe");
            }

            if (failedShimCount > 0)
            {
                issues.Add(failedShimCount + " enabled compatibility shim(s) failed");
            }

            return "Modstoke runtime attestation is waiting: " + string.Join(", ", issues) + ".";
        }

        private static string BuildEvidenceDigest(
            RuntimeSnapshot runtime,
            FileIntegritySnapshot fileIntegrity,
            CompatibilityDependencySnapshot compatibilityDependencies,
            BepInExPluginCompatibilitySnapshot bepinExPluginCompatibility,
            ModHostSnapshot nativeModHost,
            ModRuntimeSnapshot nativeModRuntime,
            ModInventorySnapshot inventory,
            ModInventoryCatalogSnapshot catalog,
            IReadOnlyList<RuntimeAttestationShimSnapshot> shims,
            IReadOnlyList<RuntimeAttestationDependencySnapshot> dependencies,
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
            Append(evidence, "loaderName", Modding.LoaderName);
            Append(evidence, "loaderVersion", RuntimeEntryPoint.Version);
            Append(evidence, "compatibilityMode", LoaderCapabilities.CompatibilityMode);
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
            Append(evidence, "nativeModRuntimeLoadCompleted", nativeModRuntime.LoadCompleted);
            Append(evidence, "nativeModRuntimeExecutionEnabled", nativeModRuntime.ExecutionEnabled);
            Append(evidence, "nativeModRuntimeModCount", nativeModRuntime.Mods.Count);
            Append(evidence, "nativeModRuntimeLoadedCount", nativeModRuntime.LoadedCount);
            Append(evidence, "nativeModRuntimeFailedCount", nativeModRuntime.FailedCount);
            Append(evidence, "nativeModRuntimeDeferredCount", nativeModRuntime.DeferredCount);
            Append(evidence, "nativeModRuntimeHookCount", nativeModRuntime.HookCount);
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

            foreach (var dependency in dependencies ?? Array.Empty<RuntimeAttestationDependencySnapshot>())
            {
                Append(evidence, "dependency.assemblyName", dependency.AssemblyName);
                Append(evidence, "dependency.requiredVersion", dependency.RequiredVersion);
                Append(evidence, "dependency.bestVersion", dependency.BestVersion);
                Append(evidence, "dependency.packaged", dependency.Packaged);
                Append(evidence, "dependency.ready", dependency.Ready);
                Append(evidence, "dependency.bindingConflict", dependency.BindingConflict);
            }

            foreach (var shim in shims ?? Array.Empty<RuntimeAttestationShimSnapshot>())
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

    public sealed class RuntimeAttestationSnapshot
    {
        private readonly IReadOnlyList<RuntimeAttestationShimSnapshot> _compatibilityShims;
        private readonly IReadOnlyList<RuntimeAttestationDependencySnapshot> _dependencies;

        internal RuntimeAttestationSnapshot(
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
            IEnumerable<RuntimeAttestationShimSnapshot> compatibilityShims,
            int dependencyCount,
            int readyDependencyCount,
            int packagedDependencyCount,
            int dependencyWarningCount,
            int dependencyBindingConflictCount,
            IEnumerable<RuntimeAttestationDependencySnapshot> dependencies,
            string evidenceDigestSha256,
            string attestationToken,
            string generatedUtc)
        {
            EvidenceVersion = evidenceVersion ?? RuntimeAttestation.EvidenceVersion;
            LoaderName = loaderName ?? Modding.LoaderName;
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
            _compatibilityShims = (compatibilityShims ?? Array.Empty<RuntimeAttestationShimSnapshot>())
                .Where(shim => shim != null)
                .ToArray();
            DependencyCount = dependencyCount;
            ReadyDependencyCount = readyDependencyCount;
            PackagedDependencyCount = packagedDependencyCount;
            DependencyWarningCount = dependencyWarningCount;
            DependencyBindingConflictCount = dependencyBindingConflictCount;
            _dependencies = (dependencies ?? Array.Empty<RuntimeAttestationDependencySnapshot>())
                .Where(dependency => dependency != null)
                .ToArray();
            EvidenceDigestSha256 = evidenceDigestSha256 ?? "";
            AttestationToken = attestationToken ?? "";
            GeneratedUtc = generatedUtc;
        }

        public int Schema => 1;
        public string ServiceKey => RuntimeAttestation.ServiceKey;
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
        public IReadOnlyList<RuntimeAttestationShimSnapshot> CompatibilityShims => _compatibilityShims;
        public IReadOnlyList<RuntimeAttestationDependencySnapshot> Dependencies => _dependencies;
    }

    public sealed class RuntimeAttestationDependencySnapshot
    {
        private RuntimeAttestationDependencySnapshot(
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

        internal static RuntimeAttestationDependencySnapshot FromDependency(CompatibilityDependencyEntry dependency)
        {
            return new RuntimeAttestationDependencySnapshot(
                dependency.AssemblyName,
                dependency.RequiredVersion,
                dependency.BestVersion,
                dependency.Packaged,
                dependency.Ready,
                dependency.BindingConflict);
        }
    }

    public sealed class RuntimeAttestationShimSnapshot
    {
        private RuntimeAttestationShimSnapshot(
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

        internal static RuntimeAttestationShimSnapshot FromShim(CompatibilityShimSnapshot shim)
        {
            return new RuntimeAttestationShimSnapshot(
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
