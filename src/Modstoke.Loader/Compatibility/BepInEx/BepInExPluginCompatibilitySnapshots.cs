// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Modstoke.Loader
{
    public sealed class BepInExPluginCompatibilitySnapshot
    {
        internal BepInExPluginCompatibilitySnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool scanningEnabled,
            bool runtimePluginLoadingEnabled,
            string pluginRoot,
            bool pluginRootExists,
            string parserHarnessRoot,
            bool parserHarnessRootExists,
            IReadOnlyList<BepInExPluginMetadataHarnessSnapshot> parserHarnesses,
            int parserHarnessReadyCount,
            int parserHarnessIssueCount,
            int parserHarnessAttributeCount,
            int parserHarnessUnsafeLocationCount,
            int parserHarnessAssemblyReferenceCount,
            int parserHarnessResolvedAssemblyReferenceCount,
            int parserHarnessUnresolvedAssemblyReferenceCount,
            bool positiveMetadataParserVerified,
            int inventoryEntryCount,
            int legacyBepInExPluginCount,
            int libraryExcludedCount,
            int openAcaiNativeExcludedCount,
            int redLoaderCompatibilityExcludedCount,
            IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> candidates,
            int metadataReadCount,
            int metadataIssueCount,
            int pluginAttributeCount,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            int compatibilityReadyCount,
            int compatibilityIssueCount,
            int loadEligibleCount,
            int loadGateOpenCount,
            int loadGateBlockedCount,
            int pluginLoadedByOpenAcaiCount,
            IReadOnlyList<BepInExPluginCompatibilityPlanSnapshot> loadPlans,
            IReadOnlyList<BepInExPluginCompatibilityLoadAttemptSnapshot> loadAttempts,
            int loadPlanRequestCount,
            int acceptedLoadPlanCount,
            int rejectedLoadPlanCount,
            string lastLoadPlanPluginId,
            string lastLoadPlanStatus,
            string lastLoadPlanDetail,
            string lastLoadPlanReason,
            int loadAttemptRequestCount,
            int acceptedLoadAttemptCount,
            int blockedLoadAttemptCount,
            int rejectedLoadAttemptCount,
            string lastLoadAttemptPluginId,
            string lastLoadAttemptStatus,
            string lastLoadAttemptDetail,
            string lastLoadAttemptReason)
        {
            ServiceKey = serviceKey ?? BepInExPluginCompatibility.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            ScanningEnabled = scanningEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            PluginRoot = pluginRoot ?? "";
            PluginRootExists = pluginRootExists;
            ParserHarnessRoot = parserHarnessRoot ?? "";
            ParserHarnessRootExists = parserHarnessRootExists;
            ParserHarnesses = parserHarnesses ?? Array.Empty<BepInExPluginMetadataHarnessSnapshot>();
            ParserHarnessCount = ParserHarnesses.Count;
            FirstUnresolvedParserHarnessAssemblyReference = ParserHarnesses
                .Where(harness => harness != null)
                .SelectMany(harness => harness.AssemblyReferences)
                .FirstOrDefault(reference => reference != null && !reference.Resolved);
            ParserHarnessExecutionPlanCount = ParserHarnesses.Count(harness => harness != null && harness.ExecutionPlan != null && harness.ExecutionPlan.PluginTypeFound);
            ParserHarnessExecutionPlanReadyCount = ParserHarnesses.Count(harness => harness != null && harness.ExecutionPlan != null && harness.ExecutionPlan.Ready);
            ParserHarnessExecutionPlanIssueCount = ParserHarnesses.Count(harness => harness != null && harness.ExecutionPlan != null && harness.ExecutionPlan.Issue);
            ParserHarnessLifecycleMethodCount = ParserHarnesses.Sum(harness => harness == null || harness.ExecutionPlan == null ? 0 : harness.ExecutionPlan.LifecycleMethodCount);
            ParserHarnessLifecycleMethodReadyCount = ParserHarnesses.Sum(harness => harness == null || harness.ExecutionPlan == null ? 0 : harness.ExecutionPlan.LifecycleMethodReadyCount);
            ParserHarnessLifecycleMethodIssueCount = ParserHarnesses.Sum(harness => harness == null || harness.ExecutionPlan == null ? 0 : harness.ExecutionPlan.LifecycleMethodIssueCount);
            FirstParserHarnessExecutionPlanIssue = ParserHarnesses
                .Where(harness => harness != null)
                .Select(harness => harness.ExecutionPlan)
                .FirstOrDefault(plan => plan != null && plan.Issue);
            FirstParserHarnessLifecycleMethodIssue = ParserHarnesses
                .Where(harness => harness != null && harness.ExecutionPlan != null)
                .SelectMany(harness => harness.ExecutionPlan.LifecycleMethods)
                .FirstOrDefault(method => method != null && method.Issue);
            ParserHarnessReadyCount = Math.Max(0, parserHarnessReadyCount);
            ParserHarnessIssueCount = Math.Max(0, parserHarnessIssueCount);
            ParserHarnessAttributeCount = Math.Max(0, parserHarnessAttributeCount);
            ParserHarnessUnsafeLocationCount = Math.Max(0, parserHarnessUnsafeLocationCount);
            ParserHarnessAssemblyReferenceCount = Math.Max(0, parserHarnessAssemblyReferenceCount);
            ParserHarnessResolvedAssemblyReferenceCount = Math.Max(0, parserHarnessResolvedAssemblyReferenceCount);
            ParserHarnessUnresolvedAssemblyReferenceCount = Math.Max(0, parserHarnessUnresolvedAssemblyReferenceCount);
            PositiveMetadataParserVerified = positiveMetadataParserVerified;
            InventoryEntryCount = Math.Max(0, inventoryEntryCount);
            LegacyBepInExPluginCount = Math.Max(0, legacyBepInExPluginCount);
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            OpenAcaiNativeExcludedCount = Math.Max(0, openAcaiNativeExcludedCount);
            RedLoaderCompatibilityExcludedCount = Math.Max(0, redLoaderCompatibilityExcludedCount);
            Candidates = candidates ?? Array.Empty<BepInExPluginCompatibilityCandidateSnapshot>();
            CandidateCount = Candidates.Count;
            FirstUnresolvedCandidateAssemblyReference = Candidates
                .Where(candidate => candidate != null)
                .SelectMany(candidate => candidate.AssemblyReferences)
                .FirstOrDefault(reference => reference != null && !reference.Resolved);
            ExecutionPlanCount = Candidates.Count(candidate => candidate != null && candidate.ExecutionPlan != null && candidate.ExecutionPlan.PluginTypeFound);
            ExecutionPlanReadyCount = Candidates.Count(candidate => candidate != null && candidate.ExecutionPlan != null && candidate.ExecutionPlan.Ready);
            ExecutionPlanIssueCount = Candidates.Count(candidate => candidate != null && candidate.ExecutionPlan != null && candidate.ExecutionPlan.Issue);
            LifecycleMethodCount = Candidates.Sum(candidate => candidate == null || candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleMethodCount);
            LifecycleMethodReadyCount = Candidates.Sum(candidate => candidate == null || candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleMethodReadyCount);
            LifecycleMethodIssueCount = Candidates.Sum(candidate => candidate == null || candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleMethodIssueCount);
            FirstExecutionPlanIssue = Candidates
                .Where(candidate => candidate != null)
                .Select(candidate => candidate.ExecutionPlan)
                .FirstOrDefault(plan => plan != null && plan.Issue);
            FirstLifecycleMethodIssue = Candidates
                .Where(candidate => candidate != null && candidate.ExecutionPlan != null)
                .SelectMany(candidate => candidate.ExecutionPlan.LifecycleMethods)
                .FirstOrDefault(method => method != null && method.Issue);
            MetadataReadCount = Math.Max(0, metadataReadCount);
            MetadataIssueCount = Math.Max(0, metadataIssueCount);
            PluginAttributeCount = Math.Max(0, pluginAttributeCount);
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            CompatibilityReadyCount = Math.Max(0, compatibilityReadyCount);
            CompatibilityIssueCount = Math.Max(0, compatibilityIssueCount);
            LoadEligibleCount = Math.Max(0, loadEligibleCount);
            LoadGateOpenCount = Math.Max(0, loadGateOpenCount);
            LoadGateBlockedCount = Math.Max(0, loadGateBlockedCount);
            PluginLoadedByOpenAcaiCount = Math.Max(0, pluginLoadedByOpenAcaiCount);
            LoadPlans = loadPlans ?? Array.Empty<BepInExPluginCompatibilityPlanSnapshot>();
            LoadPlanCount = LoadPlans.Count;
            LoadAttempts = loadAttempts ?? Array.Empty<BepInExPluginCompatibilityLoadAttemptSnapshot>();
            LoadAttemptCount = LoadAttempts.Count;
            LoadPlanRequestCount = Math.Max(0, loadPlanRequestCount);
            AcceptedLoadPlanCount = Math.Max(0, acceptedLoadPlanCount);
            RejectedLoadPlanCount = Math.Max(0, rejectedLoadPlanCount);
            LastLoadPlanPluginId = lastLoadPlanPluginId ?? "";
            LastLoadPlanStatus = lastLoadPlanStatus ?? "";
            LastLoadPlanDetail = lastLoadPlanDetail ?? "";
            LastLoadPlanReason = lastLoadPlanReason ?? "";
            LoadAttemptRequestCount = Math.Max(0, loadAttemptRequestCount);
            AcceptedLoadAttemptCount = Math.Max(0, acceptedLoadAttemptCount);
            BlockedLoadAttemptCount = Math.Max(0, blockedLoadAttemptCount);
            RejectedLoadAttemptCount = Math.Max(0, rejectedLoadAttemptCount);
            LastLoadAttemptPluginId = lastLoadAttemptPluginId ?? "";
            LastLoadAttemptStatus = lastLoadAttemptStatus ?? "";
            LastLoadAttemptDetail = lastLoadAttemptDetail ?? "";
            LastLoadAttemptReason = lastLoadAttemptReason ?? "";
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool ScanningEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public string PluginRoot { get; }
        public bool PluginRootExists { get; }
        public string ParserHarnessRoot { get; }
        public bool ParserHarnessRootExists { get; }
        public IReadOnlyList<BepInExPluginMetadataHarnessSnapshot> ParserHarnesses { get; }
        public int ParserHarnessCount { get; }
        public int ParserHarnessReadyCount { get; }
        public int ParserHarnessIssueCount { get; }
        public int ParserHarnessAttributeCount { get; }
        public int ParserHarnessUnsafeLocationCount { get; }
        public int ParserHarnessAssemblyReferenceCount { get; }
        public int ParserHarnessResolvedAssemblyReferenceCount { get; }
        public int ParserHarnessUnresolvedAssemblyReferenceCount { get; }
        public BepInExPluginAssemblyReferenceSnapshot FirstUnresolvedParserHarnessAssemblyReference { get; }
        public int ParserHarnessExecutionPlanCount { get; }
        public int ParserHarnessExecutionPlanReadyCount { get; }
        public int ParserHarnessExecutionPlanIssueCount { get; }
        public int ParserHarnessLifecycleMethodCount { get; }
        public int ParserHarnessLifecycleMethodReadyCount { get; }
        public int ParserHarnessLifecycleMethodIssueCount { get; }
        public BepInExPluginExecutionPlanSnapshot FirstParserHarnessExecutionPlanIssue { get; }
        public BepInExPluginLifecycleMethodSnapshot FirstParserHarnessLifecycleMethodIssue { get; }
        public bool PositiveMetadataParserVerified { get; }
        public int InventoryEntryCount { get; }
        public int LegacyBepInExPluginCount { get; }
        public int LibraryExcludedCount { get; }
        public int OpenAcaiNativeExcludedCount { get; }
        public int RedLoaderCompatibilityExcludedCount { get; }
        public IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> Candidates { get; }
        public int CandidateCount { get; }
        public int MetadataReadCount { get; }
        public int MetadataIssueCount { get; }
        public int PluginAttributeCount { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public BepInExPluginAssemblyReferenceSnapshot FirstUnresolvedCandidateAssemblyReference { get; }
        public int ExecutionPlanCount { get; }
        public int ExecutionPlanReadyCount { get; }
        public int ExecutionPlanIssueCount { get; }
        public int LifecycleMethodCount { get; }
        public int LifecycleMethodReadyCount { get; }
        public int LifecycleMethodIssueCount { get; }
        public BepInExPluginExecutionPlanSnapshot FirstExecutionPlanIssue { get; }
        public BepInExPluginLifecycleMethodSnapshot FirstLifecycleMethodIssue { get; }
        public int CompatibilityReadyCount { get; }
        public int CompatibilityIssueCount { get; }
        public int LoadEligibleCount { get; }
        public int LoadGateOpenCount { get; }
        public int LoadGateBlockedCount { get; }
        public int PluginLoadedByOpenAcaiCount { get; }
        public IReadOnlyList<BepInExPluginCompatibilityPlanSnapshot> LoadPlans { get; }
        public int LoadPlanCount { get; }
        public IReadOnlyList<BepInExPluginCompatibilityLoadAttemptSnapshot> LoadAttempts { get; }
        public int LoadAttemptCount { get; }
        public int LoadPlanRequestCount { get; }
        public int AcceptedLoadPlanCount { get; }
        public int RejectedLoadPlanCount { get; }
        public string LastLoadPlanPluginId { get; }
        public string LastLoadPlanStatus { get; }
        public string LastLoadPlanDetail { get; }
        public string LastLoadPlanReason { get; }
        public int LoadAttemptRequestCount { get; }
        public int AcceptedLoadAttemptCount { get; }
        public int BlockedLoadAttemptCount { get; }
        public int RejectedLoadAttemptCount { get; }
        public string LastLoadAttemptPluginId { get; }
        public string LastLoadAttemptStatus { get; }
        public string LastLoadAttemptDetail { get; }
        public string LastLoadAttemptReason { get; }

        internal static BepInExPluginCompatibilitySnapshot CreateUnknown()
        {
            return new BepInExPluginCompatibilitySnapshot(
                BepInExPluginCompatibility.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke BepInEx plugin compatibility gate has not refreshed yet.",
                true,
                BepInExPluginCompatibility.RuntimePluginLoadingEnabled,
                "",
                false,
                "",
                false,
                Array.Empty<BepInExPluginMetadataHarnessSnapshot>(),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<BepInExPluginCompatibilityCandidateSnapshot>(),
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
                Array.Empty<BepInExPluginCompatibilityPlanSnapshot>(),
                Array.Empty<BepInExPluginCompatibilityLoadAttemptSnapshot>(),
                0,
                0,
                0,
                "",
                "INFO",
                "No BepInEx plugin compatibility load plan has been requested.",
                "none",
                0,
                0,
                0,
                0,
                "",
                "INFO",
                "No BepInEx plugin compatibility load attempt has been requested.",
                "none");
        }
    }

    public sealed class BepInExPluginMetadataHarnessSnapshot
    {
        internal BepInExPluginMetadataHarnessSnapshot(
            int sequenceNumber,
            string harnessId,
            string assemblyName,
            string assemblyPath,
            string assemblyVersion,
            long assemblySize,
            bool assemblyExists,
            bool assemblyMetadataReadable,
            IReadOnlyList<BepInExPluginAssemblyReferenceSnapshot> assemblyReferences,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            int bepInExPluginAttributeCount,
            bool bepInExPluginAttributeFound,
            string pluginGuid,
            string pluginName,
            string pluginVersion,
            string pluginTypeName,
            BepInExPluginExecutionPlanSnapshot executionPlan,
            bool locatedOutsidePluginRoot,
            bool positiveMetadataParserVerified,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            HarnessId = harnessId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyVersion = assemblyVersion ?? "";
            AssemblySize = Math.Max(0, assemblySize);
            AssemblyExists = assemblyExists;
            AssemblyMetadataReadable = assemblyMetadataReadable;
            AssemblyReferences = assemblyReferences ?? Array.Empty<BepInExPluginAssemblyReferenceSnapshot>();
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            FirstUnresolvedAssemblyReference = AssemblyReferences.FirstOrDefault(reference => reference != null && !reference.Resolved);
            BepInExPluginAttributeCount = Math.Max(0, bepInExPluginAttributeCount);
            BepInExPluginAttributeFound = bepInExPluginAttributeFound;
            PluginGuid = pluginGuid ?? "";
            PluginName = pluginName ?? "";
            PluginVersion = pluginVersion ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ExecutionPlan = executionPlan ?? BepInExPluginExecutionPlanSnapshot.CreateUnavailable(pluginTypeName, "execution-plan-unavailable", "BepInEx plugin execution plan metadata was not available.");
            LocatedOutsidePluginRoot = locatedOutsidePluginRoot;
            PositiveMetadataParserVerified = positiveMetadataParserVerified;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string HarnessId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyVersion { get; }
        public long AssemblySize { get; }
        public bool AssemblyExists { get; }
        public bool AssemblyMetadataReadable { get; }
        public IReadOnlyList<BepInExPluginAssemblyReferenceSnapshot> AssemblyReferences { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public BepInExPluginAssemblyReferenceSnapshot FirstUnresolvedAssemblyReference { get; }
        public int BepInExPluginAttributeCount { get; }
        public bool BepInExPluginAttributeFound { get; }
        public string PluginGuid { get; }
        public string PluginName { get; }
        public string PluginVersion { get; }
        public string PluginTypeName { get; }
        public BepInExPluginExecutionPlanSnapshot ExecutionPlan { get; }
        public bool LocatedOutsidePluginRoot { get; }
        public bool PositiveMetadataParserVerified { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class BepInExPluginCompatibilityCandidateSnapshot
    {
        internal BepInExPluginCompatibilityCandidateSnapshot(
            int sequenceNumber,
            string modId,
            string displayName,
            string assemblyName,
            string assemblyPath,
            string packagePath,
            string candidateSource,
            bool validationProbe,
            string assemblyVersion,
            long assemblySize,
            bool enabled,
            string inventoryStatus,
            string inventoryIssueCode,
            bool assemblyExists,
            bool assemblyMetadataReadable,
            IReadOnlyList<BepInExPluginAssemblyReferenceSnapshot> assemblyReferences,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            int bepInExPluginAttributeCount,
            bool bepInExPluginAttributeFound,
            string pluginGuid,
            string pluginName,
            string pluginVersion,
            string pluginTypeName,
            BepInExPluginExecutionPlanSnapshot executionPlan,
            bool compatibilityReady,
            bool runtimePluginLoadingEnabled,
            bool loadEligible,
            bool loadGateOpen,
            bool loadGateBlocked,
            bool loadedByOpenAcai,
            string status,
            string issueCode,
            string detail,
            string generatedUtc)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            ModId = modId ?? "";
            DisplayName = displayName ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            PackagePath = packagePath ?? "";
            CandidateSource = string.IsNullOrWhiteSpace(candidateSource) ? "inventory" : candidateSource.Trim();
            ValidationProbe = validationProbe;
            AssemblyVersion = assemblyVersion ?? "";
            AssemblySize = Math.Max(0, assemblySize);
            Enabled = enabled;
            InventoryStatus = inventoryStatus ?? "";
            InventoryIssueCode = inventoryIssueCode ?? "";
            AssemblyExists = assemblyExists;
            AssemblyMetadataReadable = assemblyMetadataReadable;
            AssemblyReferences = assemblyReferences ?? Array.Empty<BepInExPluginAssemblyReferenceSnapshot>();
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            FirstUnresolvedAssemblyReference = AssemblyReferences.FirstOrDefault(reference => reference != null && !reference.Resolved);
            BepInExPluginAttributeCount = Math.Max(0, bepInExPluginAttributeCount);
            BepInExPluginAttributeFound = bepInExPluginAttributeFound;
            PluginGuid = pluginGuid ?? "";
            PluginName = pluginName ?? "";
            PluginVersion = pluginVersion ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ExecutionPlan = executionPlan ?? BepInExPluginExecutionPlanSnapshot.CreateUnavailable(pluginTypeName, "execution-plan-unavailable", "BepInEx plugin execution plan metadata was not available.");
            CompatibilityReady = compatibilityReady;
            CandidateId = CreateCandidateId(AssemblyPath, PluginTypeName, PluginGuid, PluginVersion);
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            LoadEligible = loadEligible;
            LoadGateOpen = loadGateOpen;
            LoadGateBlocked = loadGateBlocked;
            LoadedByOpenAcai = loadedByOpenAcai;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string ModId { get; }
        public string CandidateId { get; }
        public string DisplayName { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string PackagePath { get; }
        public string CandidateSource { get; }
        public bool ValidationProbe { get; }
        public string AssemblyVersion { get; }
        public long AssemblySize { get; }
        public bool Enabled { get; }
        public string InventoryStatus { get; }
        public string InventoryIssueCode { get; }
        public bool AssemblyExists { get; }
        public bool AssemblyMetadataReadable { get; }
        public IReadOnlyList<BepInExPluginAssemblyReferenceSnapshot> AssemblyReferences { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public BepInExPluginAssemblyReferenceSnapshot FirstUnresolvedAssemblyReference { get; }
        public int BepInExPluginAttributeCount { get; }
        public bool BepInExPluginAttributeFound { get; }
        public string PluginGuid { get; }
        public string PluginName { get; }
        public string PluginVersion { get; }
        public string PluginTypeName { get; }
        public BepInExPluginExecutionPlanSnapshot ExecutionPlan { get; }
        public bool CompatibilityReady { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool LoadEligible { get; }
        public bool LoadGateOpen { get; }
        public bool LoadGateBlocked { get; }
        public bool LoadedByOpenAcai { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static string CreateCandidateId(string assemblyPath, string typeName, string guid, string version)
        {
            var path = string.IsNullOrWhiteSpace(assemblyPath) ? "" : Path.GetFullPath(assemblyPath).ToUpperInvariant();
            var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { path, typeName ?? "", guid ?? "", version ?? "" });
            return "bepinex-candidate-" + Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        }
    }

    public sealed class BepInExPluginExecutionPlanSnapshot
    {
        internal BepInExPluginExecutionPlanSnapshot(
            string pluginTypeName,
            bool pluginTypeFound,
            bool pluginTypePublic,
            bool pluginTypeAbstract,
            string baseTypeName,
            string pluginBaseKind,
            bool basePluginReferenceFound,
            bool baseUnityPluginReferenceFound,
            string baseChainMatchTypeName,
            int baseChainMatchDepth,
            bool baseChainExternalAssemblyResolved,
            string baseChainExternalAssemblyName,
            string baseChainExternalAssemblyPath,
            string baseChainExternalAssemblySource,
            string baseChainResolutionDetail,
            bool constructorFound,
            bool parameterlessConstructorFound,
            bool publicParameterlessConstructorFound,
            bool staticConstructorFound,
            bool moduleInitializerFound,
            int moduleInitializerCount,
            string requiredEntrypointMethodName,
            bool requiredEntrypointMethodFound,
            IReadOnlyList<BepInExPluginLifecycleMethodSnapshot> lifecycleMethods,
            bool ready,
            bool runtimeExecutionEnabled,
            bool executionGateOpen,
            bool executionGateBlocked,
            string status,
            string issueCode,
            string detail)
        {
            PluginTypeName = pluginTypeName ?? "";
            PluginTypeFound = pluginTypeFound;
            PluginTypePublic = pluginTypePublic;
            PluginTypeAbstract = pluginTypeAbstract;
            BaseTypeName = baseTypeName ?? "";
            PluginBaseKind = pluginBaseKind ?? "";
            BasePluginReferenceFound = basePluginReferenceFound;
            BaseUnityPluginReferenceFound = baseUnityPluginReferenceFound;
            BaseChainMatchTypeName = baseChainMatchTypeName ?? "";
            BaseChainMatchDepth = Math.Max(0, baseChainMatchDepth);
            BaseChainExternalAssemblyResolved = baseChainExternalAssemblyResolved;
            BaseChainExternalAssemblyName = baseChainExternalAssemblyName ?? "";
            BaseChainExternalAssemblyPath = baseChainExternalAssemblyPath ?? "";
            BaseChainExternalAssemblySource = baseChainExternalAssemblySource ?? "";
            BaseChainResolutionDetail = baseChainResolutionDetail ?? "";
            ConstructorFound = constructorFound;
            ParameterlessConstructorFound = parameterlessConstructorFound;
            PublicParameterlessConstructorFound = publicParameterlessConstructorFound;
            StaticConstructorFound = staticConstructorFound;
            ModuleInitializerFound = moduleInitializerFound;
            ModuleInitializerCount = Math.Max(0, moduleInitializerCount);
            RequiredEntrypointMethodName = requiredEntrypointMethodName ?? "";
            RequiredEntrypointMethodFound = requiredEntrypointMethodFound;
            LifecycleMethods = lifecycleMethods ?? Array.Empty<BepInExPluginLifecycleMethodSnapshot>();
            LifecycleMethodCount = LifecycleMethods.Count;
            LifecycleMethodReadyCount = LifecycleMethods.Count(method => method != null && method.Ready);
            LifecycleMethodIssueCount = LifecycleMethods.Count(method => method != null && method.Issue);
            Ready = ready;
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
            ExecutionGateOpen = executionGateOpen;
            ExecutionGateBlocked = executionGateBlocked;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string PluginTypeName { get; }
        public bool PluginTypeFound { get; }
        public bool PluginTypePublic { get; }
        public bool PluginTypeAbstract { get; }
        public string BaseTypeName { get; }
        public string PluginBaseKind { get; }
        public bool BasePluginReferenceFound { get; }
        public bool BaseUnityPluginReferenceFound { get; }
        public string BaseChainMatchTypeName { get; }
        public int BaseChainMatchDepth { get; }
        public bool BaseChainExternalAssemblyResolved { get; }
        public string BaseChainExternalAssemblyName { get; }
        public string BaseChainExternalAssemblyPath { get; }
        public string BaseChainExternalAssemblySource { get; }
        public string BaseChainResolutionDetail { get; }
        public bool ConstructorFound { get; }
        public bool ParameterlessConstructorFound { get; }
        public bool PublicParameterlessConstructorFound { get; }
        public bool StaticConstructorFound { get; }
        public bool ModuleInitializerFound { get; }
        public int ModuleInitializerCount { get; }
        public string RequiredEntrypointMethodName { get; }
        public bool RequiredEntrypointMethodFound { get; }
        public IReadOnlyList<BepInExPluginLifecycleMethodSnapshot> LifecycleMethods { get; }
        public int LifecycleMethodCount { get; }
        public int LifecycleMethodReadyCount { get; }
        public int LifecycleMethodIssueCount { get; }
        public bool Ready { get; }
        public bool RuntimeExecutionEnabled { get; }
        public bool ExecutionGateOpen { get; }
        public bool ExecutionGateBlocked { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginExecutionPlanSnapshot CreateUnavailable(
            string pluginTypeName,
            string issueCode,
            string detail)
        {
            return new BepInExPluginExecutionPlanSnapshot(
                pluginTypeName,
                false,
                false,
                false,
                "",
                "unknown",
                false,
                false,
                "",
                0,
                false,
                "",
                "",
                "",
                "",
                false,
                false,
                false,
                false,
                false,
                0,
                "",
                false,
                Array.Empty<BepInExPluginLifecycleMethodSnapshot>(),
                false,
                false,
                false,
                false,
                "INFO",
                issueCode,
                detail);
        }
    }

    public sealed class BepInExPluginLifecycleMethodSnapshot
    {
        internal BepInExPluginLifecycleMethodSnapshot(
            string methodName,
            string methodKind,
            bool methodFound,
            bool methodStatic,
            bool methodPublic,
            int methodParameterCount,
            bool ready,
            string status,
            string issueCode,
            string detail)
        {
            MethodName = methodName ?? "";
            MethodKind = methodKind ?? "";
            MethodFound = methodFound;
            MethodStatic = methodStatic;
            MethodPublic = methodPublic;
            MethodParameterCount = Math.Max(0, methodParameterCount);
            Ready = ready;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string MethodName { get; }
        public string MethodKind { get; }
        public bool MethodFound { get; }
        public bool MethodStatic { get; }
        public bool MethodPublic { get; }
        public int MethodParameterCount { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class BepInExPluginAssemblyReferenceSnapshot
    {
        internal BepInExPluginAssemblyReferenceSnapshot(
            string assemblyName,
            string requiredVersion,
            bool resolved,
            string source,
            string resolvedVersion,
            string resolvedPath,
            string detail)
        {
            AssemblyName = assemblyName ?? "";
            RequiredVersion = requiredVersion ?? "";
            Resolved = resolved;
            Source = source ?? "";
            ResolvedVersion = resolvedVersion ?? "";
            ResolvedPath = resolvedPath ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string AssemblyName { get; }
        public string RequiredVersion { get; }
        public bool Resolved { get; }
        public string Source { get; }
        public string ResolvedVersion { get; }
        public string ResolvedPath { get; }
        public string Detail { get; }
        public bool Issue => !Resolved;
    }

    public sealed class BepInExPluginInvocationPipelineStageSnapshot
    {
        internal BepInExPluginInvocationPipelineStageSnapshot(
            int sequenceNumber,
            string stageName,
            string stageKind,
            bool required,
            bool gateOpen,
            bool gateBlocked,
            bool completed,
            string isolationBoundary,
            string status,
            string issueCode,
            string detail)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            StageName = stageName ?? "";
            StageKind = stageKind ?? "";
            Required = required;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            Completed = completed;
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string StageName { get; }
        public string StageKind { get; }
        public bool Required { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool Completed { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class BepInExPluginInvocationStageAttemptSnapshot
    {
        internal BepInExPluginInvocationStageAttemptSnapshot(
            int sequenceNumber,
            string stageName,
            string stageKind,
            bool required,
            bool attemptRecorded,
            bool stageExecutionStarted,
            bool stageCompleted,
            bool gateOpen,
            bool gateBlocked,
            bool failureIsolated,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            StageName = stageName ?? "";
            StageKind = stageKind ?? "";
            Required = required;
            AttemptRecorded = attemptRecorded;
            StageExecutionStarted = stageExecutionStarted;
            StageCompleted = stageCompleted;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            FailureIsolated = failureIsolated;
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string StageName { get; }
        public string StageKind { get; }
        public bool Required { get; }
        public bool AttemptRecorded { get; }
        public bool StageExecutionStarted { get; }
        public bool StageCompleted { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool FailureIsolated { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class BepInExPluginActivationPolicySnapshot
    {
        internal BepInExPluginActivationPolicySnapshot(
            bool available,
            bool policyEvaluated,
            bool candidateReady,
            bool executionPlanReady,
            bool runtimePluginLoadingEnabled,
            bool assemblyLoadRequired,
            bool constructorInvocationRequired,
            bool loadInvocationRequired,
            bool assemblyLoadAllowed,
            bool constructorInvocationAllowed,
            bool loadInvocationAllowed,
            bool realPluginExecutionAllowed,
            bool assemblyLoadBlocked,
            bool constructorInvocationBlocked,
            bool loadInvocationBlocked,
            bool staticConstructorFound,
            bool moduleInitializerFound,
            int moduleInitializerCount,
            string assemblyLoadBlockReasonCode,
            string constructorInvocationBlockReasonCode,
            string loadInvocationBlockReasonCode,
            string sideEffectRisk,
            string status,
            string issueCode,
            string detail)
        {
            Available = available;
            PolicyEvaluated = policyEvaluated;
            CandidateReady = candidateReady;
            ExecutionPlanReady = executionPlanReady;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            AssemblyLoadRequired = assemblyLoadRequired;
            ConstructorInvocationRequired = constructorInvocationRequired;
            LoadInvocationRequired = loadInvocationRequired;
            AssemblyLoadAllowed = assemblyLoadAllowed;
            ConstructorInvocationAllowed = constructorInvocationAllowed;
            LoadInvocationAllowed = loadInvocationAllowed;
            RealPluginExecutionAllowed = realPluginExecutionAllowed;
            AssemblyLoadBlocked = assemblyLoadBlocked;
            ConstructorInvocationBlocked = constructorInvocationBlocked;
            LoadInvocationBlocked = loadInvocationBlocked;
            StaticConstructorFound = staticConstructorFound;
            ModuleInitializerFound = moduleInitializerFound;
            ModuleInitializerCount = Math.Max(0, moduleInitializerCount);
            AssemblyLoadBlockReasonCode = assemblyLoadBlockReasonCode ?? "";
            ConstructorInvocationBlockReasonCode = constructorInvocationBlockReasonCode ?? "";
            LoadInvocationBlockReasonCode = loadInvocationBlockReasonCode ?? "";
            SideEffectRisk = sideEffectRisk ?? "";
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool PolicyEvaluated { get; }
        public bool CandidateReady { get; }
        public bool ExecutionPlanReady { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool AssemblyLoadRequired { get; }
        public bool ConstructorInvocationRequired { get; }
        public bool LoadInvocationRequired { get; }
        public bool AssemblyLoadAllowed { get; }
        public bool ConstructorInvocationAllowed { get; }
        public bool LoadInvocationAllowed { get; }
        public bool RealPluginExecutionAllowed { get; }
        public bool AssemblyLoadBlocked { get; }
        public bool ConstructorInvocationBlocked { get; }
        public bool LoadInvocationBlocked { get; }
        public bool StaticConstructorFound { get; }
        public bool ModuleInitializerFound { get; }
        public int ModuleInitializerCount { get; }
        public string AssemblyLoadBlockReasonCode { get; }
        public string ConstructorInvocationBlockReasonCode { get; }
        public string LoadInvocationBlockReasonCode { get; }
        public string SideEffectRisk { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginActivationPolicySnapshot CreateUnavailable(
            string issueCode,
            string detail)
        {
            return new BepInExPluginActivationPolicySnapshot(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                0,
                issueCode,
                issueCode,
                issueCode,
                "unavailable",
                "INFO",
                issueCode,
                detail);
        }
    }

    public sealed class BepInExPluginInvocationPreflightSnapshot
    {
        internal BepInExPluginInvocationPreflightSnapshot(
            bool available,
            bool preflightReady,
            string pluginId,
            string assemblyPath,
            string pluginTypeName,
            string pluginBaseKind,
            string invocationKind,
            string entrypointMethodName,
            bool assemblyLoadRequired,
            bool instanceCreationRequired,
            bool methodInvocationRequired,
            IReadOnlyList<BepInExPluginInvocationPipelineStageSnapshot> invocationPipelineStages,
            bool runtimeExecutionEnabled,
            bool invocationGateOpen,
            bool invocationGateBlocked,
            string isolationBoundary,
            BepInExPluginActivationPolicySnapshot activationPolicy,
            string status,
            string issueCode,
            string detail)
        {
            Available = available;
            PreflightReady = preflightReady;
            PluginId = pluginId ?? "";
            AssemblyPath = assemblyPath ?? "";
            PluginTypeName = pluginTypeName ?? "";
            PluginBaseKind = pluginBaseKind ?? "";
            InvocationKind = invocationKind ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            AssemblyLoadRequired = assemblyLoadRequired;
            InstanceCreationRequired = instanceCreationRequired;
            MethodInvocationRequired = methodInvocationRequired;
            InvocationPipelineStages = invocationPipelineStages ?? Array.Empty<BepInExPluginInvocationPipelineStageSnapshot>();
            InvocationPipelineStageCount = InvocationPipelineStages.Count;
            RequiredInvocationPipelineStageCount = InvocationPipelineStages.Count(stage => stage != null && stage.Required);
            OpenInvocationPipelineStageCount = InvocationPipelineStages.Count(stage => stage != null && stage.GateOpen);
            BlockedInvocationPipelineStageCount = InvocationPipelineStages.Count(stage => stage != null && stage.GateBlocked);
            CompletedInvocationPipelineStageCount = InvocationPipelineStages.Count(stage => stage != null && stage.Completed);
            FirstBlockedInvocationPipelineStage = InvocationPipelineStages.FirstOrDefault(stage => stage != null && stage.GateBlocked);
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
            InvocationGateOpen = invocationGateOpen;
            InvocationGateBlocked = invocationGateBlocked;
            IsolationBoundary = isolationBoundary ?? "";
            ActivationPolicy = activationPolicy ?? BepInExPluginActivationPolicySnapshot.CreateUnavailable("activation-policy-unavailable", "BepInEx plugin activation policy was not available for this invocation preflight.");
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool PreflightReady { get; }
        public string PluginId { get; }
        public string AssemblyPath { get; }
        public string PluginTypeName { get; }
        public string PluginBaseKind { get; }
        public string InvocationKind { get; }
        public string EntrypointMethodName { get; }
        public bool AssemblyLoadRequired { get; }
        public bool InstanceCreationRequired { get; }
        public bool MethodInvocationRequired { get; }
        public IReadOnlyList<BepInExPluginInvocationPipelineStageSnapshot> InvocationPipelineStages { get; }
        public int InvocationPipelineStageCount { get; }
        public int RequiredInvocationPipelineStageCount { get; }
        public int OpenInvocationPipelineStageCount { get; }
        public int BlockedInvocationPipelineStageCount { get; }
        public int CompletedInvocationPipelineStageCount { get; }
        public BepInExPluginInvocationPipelineStageSnapshot FirstBlockedInvocationPipelineStage { get; }
        public bool RuntimeExecutionEnabled { get; }
        public bool InvocationGateOpen { get; }
        public bool InvocationGateBlocked { get; }
        public string IsolationBoundary { get; }
        public BepInExPluginActivationPolicySnapshot ActivationPolicy { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginInvocationPreflightSnapshot CreateUnavailable(
            string issueCode,
            string detail)
        {
            return new BepInExPluginInvocationPreflightSnapshot(
                false,
                false,
                "",
                "",
                "",
                "unknown",
                "unavailable",
                "",
                false,
                false,
                false,
                Array.Empty<BepInExPluginInvocationPipelineStageSnapshot>(),
                false,
                false,
                true,
                "openacai.metadata-gate",
                BepInExPluginActivationPolicySnapshot.CreateUnavailable(issueCode, detail),
                "INFO",
                issueCode,
                detail);
        }
    }

    public sealed class BepInExPluginCompatibilityPlanSnapshot
    {
        internal BepInExPluginCompatibilityPlanSnapshot(
            string pluginId,
            int sequenceNumber,
            bool accepted,
            bool canLoadNow,
            bool runtimePluginLoadingEnabled,
            bool loadGateOpen,
            bool loadGateBlocked,
            bool loadedByOpenAcai,
            string planSource,
            bool validationOnly,
            string status,
            string reasonCode,
            string detail,
            BepInExPluginInvocationPreflightSnapshot invocationPreflight,
            BepInExPluginCompatibilityCandidateSnapshot candidate,
            string generatedUtc)
        {
            PluginId = pluginId ?? "";
            SequenceNumber = Math.Max(0, sequenceNumber);
            Accepted = accepted;
            CanLoadNow = canLoadNow;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            LoadGateOpen = loadGateOpen;
            LoadGateBlocked = loadGateBlocked;
            LoadedByOpenAcai = loadedByOpenAcai;
            PlanSource = string.IsNullOrWhiteSpace(planSource) ? "unknown" : planSource.Trim();
            ValidationOnly = validationOnly;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            InvocationPreflight = invocationPreflight ?? BepInExPluginInvocationPreflightSnapshot.CreateUnavailable("invocation-preflight-unavailable", "BepInEx plugin invocation preflight was not available for this plan.");
            Candidate = candidate;
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public string PluginId { get; }
        public int SequenceNumber { get; }
        public bool Accepted { get; }
        public bool CanLoadNow { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool LoadGateOpen { get; }
        public bool LoadGateBlocked { get; }
        public bool LoadedByOpenAcai { get; }
        public string PlanSource { get; }
        public bool ValidationOnly { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public BepInExPluginInvocationPreflightSnapshot InvocationPreflight { get; }
        public BepInExPluginCompatibilityCandidateSnapshot Candidate { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class BepInExPluginCompatibilityLoadAttemptSnapshot
    {
        internal BepInExPluginCompatibilityLoadAttemptSnapshot(
            string attemptId,
            string pluginId,
            int sequenceNumber,
            string planSource,
            bool validationOnly,
            bool planAccepted,
            bool attemptAccepted,
            bool runtimePluginLoadingEnabled,
            bool loadGateOpen,
            bool loadGateBlocked,
            bool executionStarted,
            bool pluginInvoked,
            bool failureIsolated,
            string status,
            string reasonCode,
            string detail,
            BepInExPluginInvocationPreflightSnapshot invocationPreflight,
            IReadOnlyList<BepInExPluginInvocationStageAttemptSnapshot> invocationStageAttempts,
            BepInExPluginCompatibilityPlanSnapshot plan,
            BepInExPluginCompatibilityCandidateSnapshot candidate,
            string generatedUtc)
        {
            AttemptId = attemptId ?? "";
            PluginId = pluginId ?? "";
            SequenceNumber = Math.Max(0, sequenceNumber);
            PlanSource = string.IsNullOrWhiteSpace(planSource) ? "unknown" : planSource.Trim();
            ValidationOnly = validationOnly;
            PlanAccepted = planAccepted;
            AttemptAccepted = attemptAccepted;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            LoadGateOpen = loadGateOpen;
            LoadGateBlocked = loadGateBlocked;
            ExecutionStarted = executionStarted;
            PluginInvoked = pluginInvoked;
            FailureIsolated = failureIsolated;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            InvocationPreflight = invocationPreflight ?? BepInExPluginInvocationPreflightSnapshot.CreateUnavailable("invocation-preflight-unavailable", "BepInEx plugin invocation preflight was not available for this attempt.");
            InvocationStageAttempts = invocationStageAttempts ?? Array.Empty<BepInExPluginInvocationStageAttemptSnapshot>();
            InvocationStageAttemptCount = InvocationStageAttempts.Count;
            RecordedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.AttemptRecorded);
            BlockedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.GateBlocked);
            OpenInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.GateOpen);
            StartedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.StageExecutionStarted);
            CompletedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.StageCompleted);
            FirstBlockedInvocationStageAttempt = InvocationStageAttempts.FirstOrDefault(stage => stage != null && stage.GateBlocked);
            Plan = plan;
            Candidate = candidate;
            GeneratedUtc = generatedUtc ?? "";
        }

        public int Schema => 1;
        public string AttemptId { get; }
        public string PluginId { get; }
        public int SequenceNumber { get; }
        public string PlanSource { get; }
        public bool ValidationOnly { get; }
        public bool PlanAccepted { get; }
        public bool AttemptAccepted { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool LoadGateOpen { get; }
        public bool LoadGateBlocked { get; }
        public bool ExecutionStarted { get; }
        public bool PluginInvoked { get; }
        public bool FailureIsolated { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public BepInExPluginInvocationPreflightSnapshot InvocationPreflight { get; }
        public IReadOnlyList<BepInExPluginInvocationStageAttemptSnapshot> InvocationStageAttempts { get; }
        public int InvocationStageAttemptCount { get; }
        public int RecordedInvocationStageAttemptCount { get; }
        public int BlockedInvocationStageAttemptCount { get; }
        public int OpenInvocationStageAttemptCount { get; }
        public int StartedInvocationStageAttemptCount { get; }
        public int CompletedInvocationStageAttemptCount { get; }
        public BepInExPluginInvocationStageAttemptSnapshot FirstBlockedInvocationStageAttempt { get; }
        [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
        public BepInExPluginInvocationExecutorSnapshot InvocationExecutor => BepInExPluginInvocationExecutorSnapshot.Retired;
        public BepInExPluginCompatibilityPlanSnapshot Plan { get; }
        public BepInExPluginCompatibilityCandidateSnapshot Candidate { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
