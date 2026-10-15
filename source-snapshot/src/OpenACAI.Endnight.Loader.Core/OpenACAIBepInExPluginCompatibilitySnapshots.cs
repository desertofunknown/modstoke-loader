// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenACAI.Loader
{
    public sealed class OpenACAIBepInExPluginCompatibilitySnapshot
    {
        internal OpenACAIBepInExPluginCompatibilitySnapshot(
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
            IReadOnlyList<OpenACAIBepInExPluginMetadataHarnessSnapshot> parserHarnesses,
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
            IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> candidates,
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
            IReadOnlyList<OpenACAIBepInExPluginCompatibilityPlanSnapshot> loadPlans,
            IReadOnlyList<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot> loadAttempts,
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
            ServiceKey = serviceKey ?? OpenACAIBepInExPluginCompatibility.ServiceKey;
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
            ParserHarnesses = parserHarnesses ?? Array.Empty<OpenACAIBepInExPluginMetadataHarnessSnapshot>();
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
            Candidates = candidates ?? Array.Empty<OpenACAIBepInExPluginCompatibilityCandidateSnapshot>();
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
            LoadPlans = loadPlans ?? Array.Empty<OpenACAIBepInExPluginCompatibilityPlanSnapshot>();
            LoadPlanCount = LoadPlans.Count;
            LoadAttempts = loadAttempts ?? Array.Empty<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot>();
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
        public IReadOnlyList<OpenACAIBepInExPluginMetadataHarnessSnapshot> ParserHarnesses { get; }
        public int ParserHarnessCount { get; }
        public int ParserHarnessReadyCount { get; }
        public int ParserHarnessIssueCount { get; }
        public int ParserHarnessAttributeCount { get; }
        public int ParserHarnessUnsafeLocationCount { get; }
        public int ParserHarnessAssemblyReferenceCount { get; }
        public int ParserHarnessResolvedAssemblyReferenceCount { get; }
        public int ParserHarnessUnresolvedAssemblyReferenceCount { get; }
        public OpenACAIBepInExPluginAssemblyReferenceSnapshot FirstUnresolvedParserHarnessAssemblyReference { get; }
        public int ParserHarnessExecutionPlanCount { get; }
        public int ParserHarnessExecutionPlanReadyCount { get; }
        public int ParserHarnessExecutionPlanIssueCount { get; }
        public int ParserHarnessLifecycleMethodCount { get; }
        public int ParserHarnessLifecycleMethodReadyCount { get; }
        public int ParserHarnessLifecycleMethodIssueCount { get; }
        public OpenACAIBepInExPluginExecutionPlanSnapshot FirstParserHarnessExecutionPlanIssue { get; }
        public OpenACAIBepInExPluginLifecycleMethodSnapshot FirstParserHarnessLifecycleMethodIssue { get; }
        public bool PositiveMetadataParserVerified { get; }
        public int InventoryEntryCount { get; }
        public int LegacyBepInExPluginCount { get; }
        public int LibraryExcludedCount { get; }
        public int OpenAcaiNativeExcludedCount { get; }
        public int RedLoaderCompatibilityExcludedCount { get; }
        public IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> Candidates { get; }
        public int CandidateCount { get; }
        public int MetadataReadCount { get; }
        public int MetadataIssueCount { get; }
        public int PluginAttributeCount { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public OpenACAIBepInExPluginAssemblyReferenceSnapshot FirstUnresolvedCandidateAssemblyReference { get; }
        public int ExecutionPlanCount { get; }
        public int ExecutionPlanReadyCount { get; }
        public int ExecutionPlanIssueCount { get; }
        public int LifecycleMethodCount { get; }
        public int LifecycleMethodReadyCount { get; }
        public int LifecycleMethodIssueCount { get; }
        public OpenACAIBepInExPluginExecutionPlanSnapshot FirstExecutionPlanIssue { get; }
        public OpenACAIBepInExPluginLifecycleMethodSnapshot FirstLifecycleMethodIssue { get; }
        public int CompatibilityReadyCount { get; }
        public int CompatibilityIssueCount { get; }
        public int LoadEligibleCount { get; }
        public int LoadGateOpenCount { get; }
        public int LoadGateBlockedCount { get; }
        public int PluginLoadedByOpenAcaiCount { get; }
        public IReadOnlyList<OpenACAIBepInExPluginCompatibilityPlanSnapshot> LoadPlans { get; }
        public int LoadPlanCount { get; }
        public IReadOnlyList<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot> LoadAttempts { get; }
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

        internal static OpenACAIBepInExPluginCompatibilitySnapshot CreateUnknown()
        {
            return new OpenACAIBepInExPluginCompatibilitySnapshot(
                OpenACAIBepInExPluginCompatibility.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "OpenACAI BepInEx plugin compatibility gate has not refreshed yet.",
                true,
                OpenACAIBepInExPluginCompatibility.RuntimePluginLoadingEnabled,
                "",
                false,
                "",
                false,
                Array.Empty<OpenACAIBepInExPluginMetadataHarnessSnapshot>(),
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
                Array.Empty<OpenACAIBepInExPluginCompatibilityCandidateSnapshot>(),
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
                Array.Empty<OpenACAIBepInExPluginCompatibilityPlanSnapshot>(),
                Array.Empty<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot>(),
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

    public sealed class OpenACAIBepInExPluginMetadataHarnessSnapshot
    {
        internal OpenACAIBepInExPluginMetadataHarnessSnapshot(
            int sequenceNumber,
            string harnessId,
            string assemblyName,
            string assemblyPath,
            string assemblyVersion,
            long assemblySize,
            bool assemblyExists,
            bool assemblyMetadataReadable,
            IReadOnlyList<OpenACAIBepInExPluginAssemblyReferenceSnapshot> assemblyReferences,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            int bepInExPluginAttributeCount,
            bool bepInExPluginAttributeFound,
            string pluginGuid,
            string pluginName,
            string pluginVersion,
            string pluginTypeName,
            OpenACAIBepInExPluginExecutionPlanSnapshot executionPlan,
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
            AssemblyReferences = assemblyReferences ?? Array.Empty<OpenACAIBepInExPluginAssemblyReferenceSnapshot>();
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
            ExecutionPlan = executionPlan ?? OpenACAIBepInExPluginExecutionPlanSnapshot.CreateUnavailable(pluginTypeName, "execution-plan-unavailable", "BepInEx plugin execution plan metadata was not available.");
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
        public IReadOnlyList<OpenACAIBepInExPluginAssemblyReferenceSnapshot> AssemblyReferences { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public OpenACAIBepInExPluginAssemblyReferenceSnapshot FirstUnresolvedAssemblyReference { get; }
        public int BepInExPluginAttributeCount { get; }
        public bool BepInExPluginAttributeFound { get; }
        public string PluginGuid { get; }
        public string PluginName { get; }
        public string PluginVersion { get; }
        public string PluginTypeName { get; }
        public OpenACAIBepInExPluginExecutionPlanSnapshot ExecutionPlan { get; }
        public bool LocatedOutsidePluginRoot { get; }
        public bool PositiveMetadataParserVerified { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAIBepInExPluginCompatibilityCandidateSnapshot
    {
        internal OpenACAIBepInExPluginCompatibilityCandidateSnapshot(
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
            IReadOnlyList<OpenACAIBepInExPluginAssemblyReferenceSnapshot> assemblyReferences,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            int bepInExPluginAttributeCount,
            bool bepInExPluginAttributeFound,
            string pluginGuid,
            string pluginName,
            string pluginVersion,
            string pluginTypeName,
            OpenACAIBepInExPluginExecutionPlanSnapshot executionPlan,
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
            AssemblyReferences = assemblyReferences ?? Array.Empty<OpenACAIBepInExPluginAssemblyReferenceSnapshot>();
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
            ExecutionPlan = executionPlan ?? OpenACAIBepInExPluginExecutionPlanSnapshot.CreateUnavailable(pluginTypeName, "execution-plan-unavailable", "BepInEx plugin execution plan metadata was not available.");
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
        public IReadOnlyList<OpenACAIBepInExPluginAssemblyReferenceSnapshot> AssemblyReferences { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public OpenACAIBepInExPluginAssemblyReferenceSnapshot FirstUnresolvedAssemblyReference { get; }
        public int BepInExPluginAttributeCount { get; }
        public bool BepInExPluginAttributeFound { get; }
        public string PluginGuid { get; }
        public string PluginName { get; }
        public string PluginVersion { get; }
        public string PluginTypeName { get; }
        public OpenACAIBepInExPluginExecutionPlanSnapshot ExecutionPlan { get; }
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

    public sealed class OpenACAIBepInExPluginExecutionPlanSnapshot
    {
        internal OpenACAIBepInExPluginExecutionPlanSnapshot(
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
            IReadOnlyList<OpenACAIBepInExPluginLifecycleMethodSnapshot> lifecycleMethods,
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
            LifecycleMethods = lifecycleMethods ?? Array.Empty<OpenACAIBepInExPluginLifecycleMethodSnapshot>();
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
        public IReadOnlyList<OpenACAIBepInExPluginLifecycleMethodSnapshot> LifecycleMethods { get; }
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

        internal static OpenACAIBepInExPluginExecutionPlanSnapshot CreateUnavailable(
            string pluginTypeName,
            string issueCode,
            string detail)
        {
            return new OpenACAIBepInExPluginExecutionPlanSnapshot(
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
                Array.Empty<OpenACAIBepInExPluginLifecycleMethodSnapshot>(),
                false,
                false,
                false,
                false,
                "INFO",
                issueCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginLifecycleMethodSnapshot
    {
        internal OpenACAIBepInExPluginLifecycleMethodSnapshot(
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

    public sealed class OpenACAIBepInExPluginAssemblyReferenceSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyReferenceSnapshot(
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

    public sealed class OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot(
            int sequenceNumber,
            string assemblyName,
            string requiredVersion,
            string requestingDirectory,
            bool resolved,
            string resolvedSource,
            string resolvedVersion,
            string resolvedPath,
            string status,
            string reasonCode,
            string detail)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            AssemblyName = assemblyName ?? "";
            RequiredVersion = requiredVersion ?? "";
            RequestingDirectory = requestingDirectory ?? "";
            Resolved = resolved;
            ResolvedSource = resolvedSource ?? "";
            ResolvedVersion = resolvedVersion ?? "";
            ResolvedPath = resolvedPath ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string AssemblyName { get; }
        public string RequiredVersion { get; }
        public string RequestingDirectory { get; }
        public bool Resolved { get; }
        public string ResolvedSource { get; }
        public string ResolvedVersion { get; }
        public string ResolvedPath { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => !Resolved || string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAIBepInExPluginLoadInvocationSnapshot
    {
        internal OpenACAIBepInExPluginLoadInvocationSnapshot(
            bool available,
            bool loadInvocationRequested,
            bool loadInvocationAccepted,
            bool pluginLoadInvocationEnabled,
            bool pluginInstanceCreationEnabled,
            bool pluginTypeResolutionEnabled,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool pluginInstanceRequired,
            bool pluginInstanceAvailable,
            bool instanceResultAvailable,
            bool pluginInstanceCreated,
            bool instanceCreationCompleted,
            bool loadInvocationPlanned,
            bool loadInvocationStarted,
            bool loadInvocationCompleted,
            bool loadMethodInvoked,
            bool pluginLoadInvoked,
            bool failureIsolated,
            bool rollbackRequired,
            string loadInvocationStrategy,
            string methodName,
            string pluginTypeName,
            string resolvedTypeName,
            string instanceTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            LoadInvocationRequested = loadInvocationRequested;
            LoadInvocationAccepted = loadInvocationAccepted;
            PluginLoadInvocationEnabled = pluginLoadInvocationEnabled;
            PluginInstanceCreationEnabled = pluginInstanceCreationEnabled;
            PluginTypeResolutionEnabled = pluginTypeResolutionEnabled;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            PluginInstanceRequired = pluginInstanceRequired;
            PluginInstanceAvailable = pluginInstanceAvailable;
            InstanceResultAvailable = instanceResultAvailable;
            PluginInstanceCreated = pluginInstanceCreated;
            InstanceCreationCompleted = instanceCreationCompleted;
            LoadInvocationPlanned = loadInvocationPlanned;
            LoadInvocationStarted = loadInvocationStarted;
            LoadInvocationCompleted = loadInvocationCompleted;
            LoadMethodInvoked = loadMethodInvoked;
            PluginLoadInvoked = pluginLoadInvoked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            LoadInvocationStrategy = loadInvocationStrategy ?? "";
            MethodName = methodName ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ResolvedTypeName = resolvedTypeName ?? "";
            InstanceTypeName = instanceTypeName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool LoadInvocationRequested { get; }
        public bool LoadInvocationAccepted { get; }
        public bool PluginLoadInvocationEnabled { get; }
        public bool PluginInstanceCreationEnabled { get; }
        public bool PluginTypeResolutionEnabled { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool PluginInstanceRequired { get; }
        public bool PluginInstanceAvailable { get; }
        public bool InstanceResultAvailable { get; }
        public bool PluginInstanceCreated { get; }
        public bool InstanceCreationCompleted { get; }
        public bool LoadInvocationPlanned { get; }
        public bool LoadInvocationStarted { get; }
        public bool LoadInvocationCompleted { get; }
        public bool LoadMethodInvoked { get; }
        public bool PluginLoadInvoked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string LoadInvocationStrategy { get; }
        public string MethodName { get; }
        public string PluginTypeName { get; }
        public string ResolvedTypeName { get; }
        public string InstanceTypeName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginLoadInvocationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginLoadInvocationSnapshot(
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
                true,
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
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginInstanceCreationSnapshot
    {
        internal OpenACAIBepInExPluginInstanceCreationSnapshot(
            bool available,
            bool instanceCreationRequested,
            bool instanceCreationAccepted,
            bool pluginInstanceCreationEnabled,
            bool pluginTypeResolutionEnabled,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool typeResultRequired,
            bool typeResultAvailable,
            bool pluginTypeResolved,
            bool resolutionCompleted,
            bool instanceCreationPlanned,
            bool instanceCreationStarted,
            bool instanceCreationCompleted,
            bool pluginInstanceCreated,
            bool instanceResultAvailable,
            bool constructorInvoked,
            bool failureIsolated,
            bool rollbackRequired,
            string instanceCreationStrategy,
            string methodName,
            string pluginTypeName,
            string resolvedTypeName,
            string instanceTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            OpenACAIBepInExPluginLoadInvocationSnapshot pluginLoadInvocation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            InstanceCreationRequested = instanceCreationRequested;
            InstanceCreationAccepted = instanceCreationAccepted;
            PluginInstanceCreationEnabled = pluginInstanceCreationEnabled;
            PluginTypeResolutionEnabled = pluginTypeResolutionEnabled;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            TypeResultRequired = typeResultRequired;
            TypeResultAvailable = typeResultAvailable;
            PluginTypeResolved = pluginTypeResolved;
            ResolutionCompleted = resolutionCompleted;
            InstanceCreationPlanned = instanceCreationPlanned;
            InstanceCreationStarted = instanceCreationStarted;
            InstanceCreationCompleted = instanceCreationCompleted;
            PluginInstanceCreated = pluginInstanceCreated;
            InstanceResultAvailable = instanceResultAvailable;
            ConstructorInvoked = constructorInvoked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            InstanceCreationStrategy = instanceCreationStrategy ?? "";
            MethodName = methodName ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ResolvedTypeName = resolvedTypeName ?? "";
            InstanceTypeName = instanceTypeName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            PluginLoadInvocation = pluginLoadInvocation ?? OpenACAIBepInExPluginLoadInvocationSnapshot.CreateUnavailable(
                "plugin-load-invocation-not-requested",
                "No OpenACAI BasePlugin.Load invocation boundary was requested for this plugin instance creation.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool InstanceCreationRequested { get; }
        public bool InstanceCreationAccepted { get; }
        public bool PluginInstanceCreationEnabled { get; }
        public bool PluginTypeResolutionEnabled { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool TypeResultRequired { get; }
        public bool TypeResultAvailable { get; }
        public bool PluginTypeResolved { get; }
        public bool ResolutionCompleted { get; }
        public bool InstanceCreationPlanned { get; }
        public bool InstanceCreationStarted { get; }
        public bool InstanceCreationCompleted { get; }
        public bool PluginInstanceCreated { get; }
        public bool InstanceResultAvailable { get; }
        public bool ConstructorInvoked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string InstanceCreationStrategy { get; }
        public string MethodName { get; }
        public string PluginTypeName { get; }
        public string ResolvedTypeName { get; }
        public string InstanceTypeName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public OpenACAIBepInExPluginLoadInvocationSnapshot PluginLoadInvocation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginInstanceCreationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginInstanceCreationSnapshot(
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
                true,
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
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                OpenACAIBepInExPluginLoadInvocationSnapshot.CreateUnavailable(
                    "plugin-load-invocation-not-requested",
                    "No OpenACAI BasePlugin.Load invocation boundary was requested for this plugin instance creation."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginTypeResolutionSnapshot
    {
        internal OpenACAIBepInExPluginTypeResolutionSnapshot(
            bool available,
            bool resolutionRequested,
            bool resolutionAccepted,
            bool pluginTypeResolutionEnabled,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool assemblyResultRequired,
            bool assemblyResultAvailable,
            bool assemblyLoaded,
            bool loadResultAvailable,
            bool invocationCompleted,
            bool resolutionPlanned,
            bool resolutionStarted,
            bool resolutionCompleted,
            bool pluginTypeResolved,
            bool typeResultAvailable,
            bool failureIsolated,
            bool rollbackRequired,
            string resolutionStrategy,
            string methodName,
            string pluginTypeName,
            string resolvedTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            OpenACAIBepInExPluginInstanceCreationSnapshot pluginInstanceCreation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            ResolutionRequested = resolutionRequested;
            ResolutionAccepted = resolutionAccepted;
            PluginTypeResolutionEnabled = pluginTypeResolutionEnabled;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            AssemblyResultRequired = assemblyResultRequired;
            AssemblyResultAvailable = assemblyResultAvailable;
            AssemblyLoaded = assemblyLoaded;
            LoadResultAvailable = loadResultAvailable;
            InvocationCompleted = invocationCompleted;
            ResolutionPlanned = resolutionPlanned;
            ResolutionStarted = resolutionStarted;
            ResolutionCompleted = resolutionCompleted;
            PluginTypeResolved = pluginTypeResolved;
            TypeResultAvailable = typeResultAvailable;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            ResolutionStrategy = resolutionStrategy ?? "";
            MethodName = methodName ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ResolvedTypeName = resolvedTypeName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            PluginInstanceCreation = pluginInstanceCreation ?? OpenACAIBepInExPluginInstanceCreationSnapshot.CreateUnavailable(
                "plugin-instance-creation-not-requested",
                "No OpenACAI plugin instance creation boundary was requested for this plugin type resolution.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool ResolutionRequested { get; }
        public bool ResolutionAccepted { get; }
        public bool PluginTypeResolutionEnabled { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool AssemblyResultRequired { get; }
        public bool AssemblyResultAvailable { get; }
        public bool AssemblyLoaded { get; }
        public bool LoadResultAvailable { get; }
        public bool InvocationCompleted { get; }
        public bool ResolutionPlanned { get; }
        public bool ResolutionStarted { get; }
        public bool ResolutionCompleted { get; }
        public bool PluginTypeResolved { get; }
        public bool TypeResultAvailable { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string ResolutionStrategy { get; }
        public string MethodName { get; }
        public string PluginTypeName { get; }
        public string ResolvedTypeName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public OpenACAIBepInExPluginInstanceCreationSnapshot PluginInstanceCreation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginTypeResolutionSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginTypeResolutionSnapshot(
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
                true,
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
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                OpenACAIBepInExPluginInstanceCreationSnapshot.CreateUnavailable(
                    "plugin-instance-creation-not-requested",
                    "No OpenACAI plugin instance creation boundary was requested for this plugin type resolution."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot(
            bool available,
            bool invocationRequested,
            bool invocationAccepted,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool registeredContextRequired,
            bool registeredContextAvailable,
            bool contextTracked,
            bool invocationPlanned,
            bool invocationStarted,
            bool invocationCompleted,
            bool assemblyLoaded,
            bool loadResultAvailable,
            bool failureIsolated,
            bool rollbackRequired,
            string invocationStrategy,
            string methodName,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            OpenACAIBepInExPluginTypeResolutionSnapshot pluginTypeResolution,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            InvocationRequested = invocationRequested;
            InvocationAccepted = invocationAccepted;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            RegisteredContextRequired = registeredContextRequired;
            RegisteredContextAvailable = registeredContextAvailable;
            ContextTracked = contextTracked;
            InvocationPlanned = invocationPlanned;
            InvocationStarted = invocationStarted;
            InvocationCompleted = invocationCompleted;
            AssemblyLoaded = assemblyLoaded;
            LoadResultAvailable = loadResultAvailable;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            InvocationStrategy = invocationStrategy ?? "";
            MethodName = methodName ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            PluginTypeResolution = pluginTypeResolution ?? OpenACAIBepInExPluginTypeResolutionSnapshot.CreateUnavailable(
                "plugin-type-resolution-not-requested",
                "No OpenACAI plugin type resolution boundary was requested for this assembly-load invocation.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool InvocationRequested { get; }
        public bool InvocationAccepted { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool RegisteredContextRequired { get; }
        public bool RegisteredContextAvailable { get; }
        public bool ContextTracked { get; }
        public bool InvocationPlanned { get; }
        public bool InvocationStarted { get; }
        public bool InvocationCompleted { get; }
        public bool AssemblyLoaded { get; }
        public bool LoadResultAvailable { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string InvocationStrategy { get; }
        public string MethodName { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public OpenACAIBepInExPluginTypeResolutionSnapshot PluginTypeResolution { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot(
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
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                OpenACAIBepInExPluginTypeResolutionSnapshot.CreateUnavailable(
                    "plugin-type-resolution-not-requested",
                    "No OpenACAI plugin type resolution boundary was requested for this assembly-load invocation."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot(
            bool available,
            bool registrationRequested,
            bool registrationAccepted,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool constructorResultRequired,
            bool constructorResultAvailable,
            bool constructorCalled,
            bool contextCreated,
            bool contextTracked,
            bool unloadRegistered,
            bool weakReferencePlanned,
            bool weakReferenceCreated,
            bool ownershipRegistered,
            bool failureIsolated,
            bool rollbackRequired,
            string registrationStrategy,
            string trackingCollection,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot assemblyLoadInvocation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            RegistrationRequested = registrationRequested;
            RegistrationAccepted = registrationAccepted;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ConstructorResultRequired = constructorResultRequired;
            ConstructorResultAvailable = constructorResultAvailable;
            ConstructorCalled = constructorCalled;
            ContextCreated = contextCreated;
            ContextTracked = contextTracked;
            UnloadRegistered = unloadRegistered;
            WeakReferencePlanned = weakReferencePlanned;
            WeakReferenceCreated = weakReferenceCreated;
            OwnershipRegistered = ownershipRegistered;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            RegistrationStrategy = registrationStrategy ?? "";
            TrackingCollection = trackingCollection ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyLoadInvocation = assemblyLoadInvocation ?? OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot.CreateUnavailable(
                "assembly-load-invocation-unavailable",
                "No OpenACAI assembly-load invocation boundary was available for this context registration.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool RegistrationRequested { get; }
        public bool RegistrationAccepted { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ConstructorResultRequired { get; }
        public bool ConstructorResultAvailable { get; }
        public bool ConstructorCalled { get; }
        public bool ContextCreated { get; }
        public bool ContextTracked { get; }
        public bool UnloadRegistered { get; }
        public bool WeakReferencePlanned { get; }
        public bool WeakReferenceCreated { get; }
        public bool OwnershipRegistered { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string RegistrationStrategy { get; }
        public string TrackingCollection { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot AssemblyLoadInvocation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot(
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
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot.CreateUnavailable(
                    "assembly-load-invocation-not-requested",
                    "No OpenACAI assembly-load invocation boundary was requested."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot(
            bool available,
            bool allocationRequested,
            bool allocationAccepted,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool constructorPlanned,
            bool constructorCalled,
            bool contextCreated,
            bool contextTracked,
            bool unloadRegistered,
            bool collectibleContextPlanned,
            bool dedicatedContextPlanned,
            bool defaultContextBlocked,
            bool failureIsolated,
            bool rollbackRequired,
            string allocationStrategy,
            string constructorSignature,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot assemblyLoadContextRegistration,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            AllocationRequested = allocationRequested;
            AllocationAccepted = allocationAccepted;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ConstructorPlanned = constructorPlanned;
            ConstructorCalled = constructorCalled;
            ContextCreated = contextCreated;
            ContextTracked = contextTracked;
            UnloadRegistered = unloadRegistered;
            CollectibleContextPlanned = collectibleContextPlanned;
            DedicatedContextPlanned = dedicatedContextPlanned;
            DefaultContextBlocked = defaultContextBlocked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            AllocationStrategy = allocationStrategy ?? "";
            ConstructorSignature = constructorSignature ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyLoadContextRegistration = assemblyLoadContextRegistration ?? OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot.CreateUnavailable(
                "assembly-load-context-registration-unavailable",
                "No OpenACAI assembly-load context registration boundary was available for this allocation attempt.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool AllocationRequested { get; }
        public bool AllocationAccepted { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ConstructorPlanned { get; }
        public bool ConstructorCalled { get; }
        public bool ContextCreated { get; }
        public bool ContextTracked { get; }
        public bool UnloadRegistered { get; }
        public bool CollectibleContextPlanned { get; }
        public bool DedicatedContextPlanned { get; }
        public bool DefaultContextBlocked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string AllocationStrategy { get; }
        public string ConstructorSignature { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot AssemblyLoadContextRegistration { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot(
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
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot.CreateUnavailable(
                    "assembly-load-context-registration-not-requested",
                    "No OpenACAI assembly-load context registration boundary was requested."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot(
            bool available,
            bool planRequested,
            bool planAccepted,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool contextAllocationPlanned,
            bool contextCreated,
            bool collectibleContextPlanned,
            bool dedicatedContextPlanned,
            bool defaultContextBlocked,
            bool failureIsolated,
            bool rollbackRequired,
            string allocationStrategy,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot assemblyLoadContextAllocation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            PlanRequested = planRequested;
            PlanAccepted = planAccepted;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ContextAllocationPlanned = contextAllocationPlanned;
            ContextCreated = contextCreated;
            CollectibleContextPlanned = collectibleContextPlanned;
            DedicatedContextPlanned = dedicatedContextPlanned;
            DefaultContextBlocked = defaultContextBlocked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            AllocationStrategy = allocationStrategy ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyLoadContextAllocation = assemblyLoadContextAllocation ?? OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot.CreateUnavailable(
                "assembly-load-context-allocation-unavailable",
                "No OpenACAI assembly-load context allocation attempt was available for this plan.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool PlanRequested { get; }
        public bool PlanAccepted { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ContextAllocationPlanned { get; }
        public bool ContextCreated { get; }
        public bool CollectibleContextPlanned { get; }
        public bool DedicatedContextPlanned { get; }
        public bool DefaultContextBlocked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string AllocationStrategy { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot AssemblyLoadContextAllocation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot(
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
                true,
                false,
                false,
                false,
                false,
                true,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot.CreateUnavailable(
                    "assembly-load-context-allocation-not-requested",
                    "No OpenACAI assembly-load context allocation attempt was requested."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginAssemblyLoadOperationSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyLoadOperationSnapshot(
            bool available,
            bool operationRequested,
            bool operationAccepted,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool contextCreated,
            bool operationStarted,
            bool operationCompleted,
            bool assemblyLoaded,
            bool assemblyAlreadyLoaded,
            bool failureIsolated,
            bool rollbackRequired,
            string operationKind,
            string methodName,
            string contextName,
            string contextIdentity,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            bool assemblyExists,
            long assemblyFileSize,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot assemblyLoadContextPlan,
            string rollbackStrategy,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            OperationRequested = operationRequested;
            OperationAccepted = operationAccepted;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ContextCreated = contextCreated;
            OperationStarted = operationStarted;
            OperationCompleted = operationCompleted;
            AssemblyLoaded = assemblyLoaded;
            AssemblyAlreadyLoaded = assemblyAlreadyLoaded;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            OperationKind = operationKind ?? "";
            MethodName = methodName ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyExists = assemblyExists;
            AssemblyFileSize = Math.Max(0, assemblyFileSize);
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            AssemblyLoadContextPlan = assemblyLoadContextPlan ?? OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot.CreateUnavailable(
                "assembly-load-context-plan-unavailable",
                "No OpenACAI assembly-load context allocation plan was available for this operation.");
            RollbackStrategy = rollbackStrategy ?? "";
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool OperationRequested { get; }
        public bool OperationAccepted { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ContextCreated { get; }
        public bool OperationStarted { get; }
        public bool OperationCompleted { get; }
        public bool AssemblyLoaded { get; }
        public bool AssemblyAlreadyLoaded { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string OperationKind { get; }
        public string MethodName { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public bool AssemblyExists { get; }
        public long AssemblyFileSize { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot AssemblyLoadContextPlan { get; }
        public string RollbackStrategy { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginAssemblyLoadOperationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginAssemblyLoadOperationSnapshot(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                false,
                0,
                0,
                0,
                0,
                OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot.CreateUnavailable(
                    "assembly-load-context-plan-not-requested",
                    "No OpenACAI assembly-load context allocation plan was requested."),
                "none",
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot
    {
        internal OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot(
            bool available,
            bool planRequested,
            bool planAccepted,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool assemblyLoadStarted,
            bool assemblyLoaded,
            bool failureIsolated,
            bool rollbackRequired,
            string contextName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            bool assemblyExists,
            long assemblyFileSize,
            int resolverSearchDirectoryCount,
            IReadOnlyList<OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot> dependencyPlans,
            OpenACAIBepInExPluginAssemblyLoadOperationSnapshot assemblyLoadOperation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            PlanRequested = planRequested;
            PlanAccepted = planAccepted;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            AssemblyLoadStarted = assemblyLoadStarted;
            AssemblyLoaded = assemblyLoaded;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            ContextName = contextName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyExists = assemblyExists;
            AssemblyFileSize = Math.Max(0, assemblyFileSize);
            ResolverSearchDirectoryCount = Math.Max(0, resolverSearchDirectoryCount);
            DependencyPlans = dependencyPlans ?? Array.Empty<OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot>();
            AssemblyReferenceCount = DependencyPlans.Count;
            ResolvedAssemblyReferenceCount = DependencyPlans.Count(plan => plan != null && plan.Resolved);
            UnresolvedAssemblyReferenceCount = DependencyPlans.Count(plan => plan != null && !plan.Resolved);
            FirstUnresolvedAssemblyReference = DependencyPlans.FirstOrDefault(plan => plan != null && !plan.Resolved);
            DependenciesResolved = AssemblyReferenceCount == ResolvedAssemblyReferenceCount;
            AssemblyLoadOperation = assemblyLoadOperation ?? OpenACAIBepInExPluginAssemblyLoadOperationSnapshot.CreateUnavailable(
                "assembly-load-operation-unavailable",
                "No OpenACAI assembly-load operation plan was available for this resolver plan.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool PlanRequested { get; }
        public bool PlanAccepted { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool AssemblyLoadStarted { get; }
        public bool AssemblyLoaded { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string ContextName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public bool AssemblyExists { get; }
        public long AssemblyFileSize { get; }
        public int ResolverSearchDirectoryCount { get; }
        public IReadOnlyList<OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot> DependencyPlans { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public bool DependenciesResolved { get; }
        public OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot FirstUnresolvedAssemblyReference { get; }
        public OpenACAIBepInExPluginAssemblyLoadOperationSnapshot AssemblyLoadOperation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                false,
                true,
                false,
                "",
                "",
                "",
                "",
                "",
                false,
                0,
                0,
                Array.Empty<OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot>(),
                OpenACAIBepInExPluginAssemblyLoadOperationSnapshot.CreateUnavailable(
                    "assembly-load-operation-not-requested",
                    "No OpenACAI assembly-load operation plan was requested for this resolver plan."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginInvocationPipelineStageSnapshot
    {
        internal OpenACAIBepInExPluginInvocationPipelineStageSnapshot(
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

    public sealed class OpenACAIBepInExPluginInvocationStageAttemptSnapshot
    {
        internal OpenACAIBepInExPluginInvocationStageAttemptSnapshot(
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

    public sealed class OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot
    {
        internal OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot(
            int sequenceNumber,
            string stageName,
            string stageKind,
            string fromState,
            string toState,
            string action,
            bool gateOpen,
            bool gateBlocked,
            bool executionStarted,
            bool executionCompleted,
            bool failureIsolated,
            bool rollbackRequired,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            StageName = stageName ?? "";
            StageKind = stageKind ?? "";
            FromState = fromState ?? "";
            ToState = toState ?? "";
            Action = action ?? "";
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ExecutionStarted = executionStarted;
            ExecutionCompleted = executionCompleted;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string StageName { get; }
        public string StageKind { get; }
        public string FromState { get; }
        public string ToState { get; }
        public string Action { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ExecutionStarted { get; }
        public bool ExecutionCompleted { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot
    {
        internal OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot(
            string requestId,
            int sequenceNumber,
            string stageName,
            string stageKind,
            string requestedStageKind,
            string requestSource,
            string action,
            bool requested,
            bool accepted,
            bool stageMatched,
            bool stageAvailable,
            bool assemblyLoadTransitionRequestEnabled,
            bool executorEnabled,
            bool runtimePluginLoadingEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool executionStarted,
            bool executionCompleted,
            bool assemblyLoaded,
            OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot assemblyLoadResolverPlan,
            bool failureIsolated,
            bool rollbackRequired,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            RequestId = requestId ?? "";
            SequenceNumber = Math.Max(0, sequenceNumber);
            StageName = stageName ?? "";
            StageKind = stageKind ?? "";
            RequestedStageKind = requestedStageKind ?? "";
            RequestSource = requestSource ?? "";
            Action = action ?? "";
            Requested = requested;
            Accepted = accepted;
            StageMatched = stageMatched;
            StageAvailable = stageAvailable;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            ExecutorEnabled = executorEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ExecutionStarted = executionStarted;
            ExecutionCompleted = executionCompleted;
            AssemblyLoaded = assemblyLoaded;
            AssemblyLoadResolverPlan = assemblyLoadResolverPlan ?? OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot.CreateUnavailable(
                "assembly-load-resolver-plan-unavailable",
                "No OpenACAI assembly-load resolver plan was available for this transition request.");
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string RequestId { get; }
        public int SequenceNumber { get; }
        public string StageName { get; }
        public string StageKind { get; }
        public string RequestedStageKind { get; }
        public string RequestSource { get; }
        public string Action { get; }
        public bool Requested { get; }
        public bool Accepted { get; }
        public bool StageMatched { get; }
        public bool StageAvailable { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ExecutionStarted { get; }
        public bool ExecutionCompleted { get; }
        public bool AssemblyLoaded { get; }
        public OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot AssemblyLoadResolverPlan { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAIBepInExPluginInvocationExecutorSnapshot
    {
        internal OpenACAIBepInExPluginInvocationExecutorSnapshot(
            bool available,
            bool executorEnabled,
            bool runtimePluginLoadingEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool executionGateOpen,
            bool executionGateBlocked,
            string stateMachineVersion,
            string initialState,
            string finalState,
            bool rollbackRequired,
            string isolationBoundary,
            OpenACAIBepInExPluginActivationPolicySnapshot activationPolicy,
            IReadOnlyList<OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot> transitions,
            IReadOnlyList<OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot> transitionRequests,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            ExecutorEnabled = executorEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            ExecutionGateOpen = executionGateOpen;
            ExecutionGateBlocked = executionGateBlocked;
            StateMachineVersion = stateMachineVersion ?? "";
            InitialState = initialState ?? "";
            FinalState = finalState ?? "";
            RollbackRequired = rollbackRequired;
            IsolationBoundary = isolationBoundary ?? "";
            ActivationPolicy = activationPolicy ?? OpenACAIBepInExPluginActivationPolicySnapshot.CreateUnavailable("activation-policy-unavailable", "BepInEx plugin activation policy was not available for this executor snapshot.");
            Transitions = transitions ?? Array.Empty<OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot>();
            TransitionCount = Transitions.Count;
            BlockedTransitionCount = Transitions.Count(transition => transition != null && transition.GateBlocked);
            OpenTransitionCount = Transitions.Count(transition => transition != null && transition.GateOpen);
            StartedTransitionCount = Transitions.Count(transition => transition != null && transition.ExecutionStarted);
            CompletedTransitionCount = Transitions.Count(transition => transition != null && transition.ExecutionCompleted);
            FailedIsolatedTransitionCount = Transitions.Count(transition => transition != null && transition.FailureIsolated && transition.Issue);
            FirstBlockedTransition = Transitions.FirstOrDefault(transition => transition != null && transition.GateBlocked);
            TransitionRequests = transitionRequests ?? Array.Empty<OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot>();
            TransitionRequestCount = TransitionRequests.Count;
            AcceptedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.Accepted);
            BlockedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.GateBlocked);
            OpenTransitionRequestCount = TransitionRequests.Count(request => request != null && request.GateOpen);
            StartedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.ExecutionStarted);
            CompletedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.ExecutionCompleted);
            AssemblyLoadedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.AssemblyLoaded);
            FirstBlockedTransitionRequest = TransitionRequests.FirstOrDefault(request => request != null && request.GateBlocked);
            AssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available);
            AcceptedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.PlanAccepted);
            BlockedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.GateBlocked);
            OpenAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.GateOpen);
            StartedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadStarted);
            LoadedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoaded);
            FirstBlockedAssemblyLoadResolverPlan = TransitionRequests
                .Select(request => request == null ? null : request.AssemblyLoadResolverPlan)
                .FirstOrDefault(plan => plan != null && plan.Available && plan.GateBlocked);
            AssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available);
            AcceptedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.OperationAccepted);
            BlockedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.GateBlocked);
            OpenAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.GateOpen);
            StartedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.OperationStarted);
            CompletedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.OperationCompleted);
            LoadedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoaded);
            FirstBlockedAssemblyLoadOperation = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation)
                .FirstOrDefault(operation => operation != null && operation.Available && operation.GateBlocked);
            AssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available);
            AcceptedAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.PlanAccepted);
            BlockedAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.GateBlocked);
            OpenAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.GateOpen);
            CreatedAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.ContextCreated);
            FirstBlockedAssemblyLoadContextPlan = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan)
                .FirstOrDefault(plan => plan != null && plan.Available && plan.GateBlocked);
            AssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available);
            AcceptedAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.AllocationAccepted);
            BlockedAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.GateBlocked);
            OpenAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.GateOpen);
            ConstructorCalledAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.ConstructorCalled);
            CreatedAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.ContextCreated);
            FirstBlockedAssemblyLoadContextAllocation = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation)
                .FirstOrDefault(allocation => allocation != null && allocation.Available && allocation.GateBlocked);
            var assemblyLoadContextRegistrations = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation)
                .Select(allocation => allocation == null ? null : allocation.AssemblyLoadContextRegistration)
                .Where(registration => registration != null && registration.Available)
                .ToArray();
            AssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Length;
            AcceptedAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.RegistrationAccepted);
            BlockedAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.GateBlocked);
            OpenAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.GateOpen);
            TrackedAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.ContextTracked);
            UnloadRegisteredAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.UnloadRegistered);
            FirstBlockedAssemblyLoadContextRegistration = assemblyLoadContextRegistrations.FirstOrDefault(registration => registration != null && registration.GateBlocked);
            var assemblyLoadInvocations = assemblyLoadContextRegistrations
                .Select(registration => registration == null ? null : registration.AssemblyLoadInvocation)
                .Where(invocation => invocation != null && invocation.Available)
                .ToArray();
            AssemblyLoadInvocationCount = assemblyLoadInvocations.Length;
            AcceptedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationAccepted);
            BlockedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.GateBlocked);
            OpenAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.GateOpen);
            StartedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationStarted);
            CompletedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationCompleted);
            LoadedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.AssemblyLoaded);
            FirstBlockedAssemblyLoadInvocation = assemblyLoadInvocations.FirstOrDefault(invocation => invocation != null && invocation.GateBlocked);
            var pluginTypeResolutions = assemblyLoadInvocations
                .Select(invocation => invocation == null ? null : invocation.PluginTypeResolution)
                .Where(resolution => resolution != null && resolution.Available)
                .ToArray();
            PluginTypeResolutionCount = pluginTypeResolutions.Length;
            AcceptedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionAccepted);
            BlockedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.GateBlocked);
            OpenPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.GateOpen);
            StartedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionStarted);
            CompletedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionCompleted);
            ResolvedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.PluginTypeResolved);
            FirstBlockedPluginTypeResolution = pluginTypeResolutions.FirstOrDefault(resolution => resolution != null && resolution.GateBlocked);
            var pluginInstanceCreations = pluginTypeResolutions
                .Select(resolution => resolution == null ? null : resolution.PluginInstanceCreation)
                .Where(creation => creation != null && creation.Available)
                .ToArray();
            PluginInstanceCreationCount = pluginInstanceCreations.Length;
            AcceptedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationAccepted);
            BlockedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.GateBlocked);
            OpenPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.GateOpen);
            StartedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationStarted);
            CompletedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationCompleted);
            CreatedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.PluginInstanceCreated);
            FirstBlockedPluginInstanceCreation = pluginInstanceCreations.FirstOrDefault(creation => creation != null && creation.GateBlocked);
            var pluginLoadInvocations = pluginInstanceCreations
                .Select(creation => creation == null ? null : creation.PluginLoadInvocation)
                .Where(invocation => invocation != null && invocation.Available)
                .ToArray();
            PluginLoadInvocationCount = pluginLoadInvocations.Length;
            AcceptedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationAccepted);
            BlockedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.GateBlocked);
            OpenPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.GateOpen);
            StartedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationStarted);
            CompletedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationCompleted);
            InvokedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.PluginLoadInvoked);
            FirstBlockedPluginLoadInvocation = pluginLoadInvocations.FirstOrDefault(invocation => invocation != null && invocation.GateBlocked);
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool ExecutorEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool ExecutionGateOpen { get; }
        public bool ExecutionGateBlocked { get; }
        public string StateMachineVersion { get; }
        public string InitialState { get; }
        public string FinalState { get; }
        public bool RollbackRequired { get; }
        public string IsolationBoundary { get; }
        public OpenACAIBepInExPluginActivationPolicySnapshot ActivationPolicy { get; }
        public IReadOnlyList<OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot> Transitions { get; }
        public int TransitionCount { get; }
        public int BlockedTransitionCount { get; }
        public int OpenTransitionCount { get; }
        public int StartedTransitionCount { get; }
        public int CompletedTransitionCount { get; }
        public int FailedIsolatedTransitionCount { get; }
        public OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot FirstBlockedTransition { get; }
        public IReadOnlyList<OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot> TransitionRequests { get; }
        public int TransitionRequestCount { get; }
        public int AcceptedTransitionRequestCount { get; }
        public int BlockedTransitionRequestCount { get; }
        public int OpenTransitionRequestCount { get; }
        public int StartedTransitionRequestCount { get; }
        public int CompletedTransitionRequestCount { get; }
        public int AssemblyLoadedTransitionRequestCount { get; }
        public OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot FirstBlockedTransitionRequest { get; }
        public int AssemblyLoadResolverPlanCount { get; }
        public int AcceptedAssemblyLoadResolverPlanCount { get; }
        public int BlockedAssemblyLoadResolverPlanCount { get; }
        public int OpenAssemblyLoadResolverPlanCount { get; }
        public int StartedAssemblyLoadResolverPlanCount { get; }
        public int LoadedAssemblyLoadResolverPlanCount { get; }
        public OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot FirstBlockedAssemblyLoadResolverPlan { get; }
        public int AssemblyLoadOperationCount { get; }
        public int AcceptedAssemblyLoadOperationCount { get; }
        public int BlockedAssemblyLoadOperationCount { get; }
        public int OpenAssemblyLoadOperationCount { get; }
        public int StartedAssemblyLoadOperationCount { get; }
        public int CompletedAssemblyLoadOperationCount { get; }
        public int LoadedAssemblyLoadOperationCount { get; }
        public OpenACAIBepInExPluginAssemblyLoadOperationSnapshot FirstBlockedAssemblyLoadOperation { get; }
        public int AssemblyLoadContextPlanCount { get; }
        public int AcceptedAssemblyLoadContextPlanCount { get; }
        public int BlockedAssemblyLoadContextPlanCount { get; }
        public int OpenAssemblyLoadContextPlanCount { get; }
        public int CreatedAssemblyLoadContextPlanCount { get; }
        public OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot FirstBlockedAssemblyLoadContextPlan { get; }
        public int AssemblyLoadContextAllocationCount { get; }
        public int AcceptedAssemblyLoadContextAllocationCount { get; }
        public int BlockedAssemblyLoadContextAllocationCount { get; }
        public int OpenAssemblyLoadContextAllocationCount { get; }
        public int ConstructorCalledAssemblyLoadContextAllocationCount { get; }
        public int CreatedAssemblyLoadContextAllocationCount { get; }
        public OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot FirstBlockedAssemblyLoadContextAllocation { get; }
        public int AssemblyLoadContextRegistrationCount { get; }
        public int AcceptedAssemblyLoadContextRegistrationCount { get; }
        public int BlockedAssemblyLoadContextRegistrationCount { get; }
        public int OpenAssemblyLoadContextRegistrationCount { get; }
        public int TrackedAssemblyLoadContextRegistrationCount { get; }
        public int UnloadRegisteredAssemblyLoadContextRegistrationCount { get; }
        public OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot FirstBlockedAssemblyLoadContextRegistration { get; }
        public int AssemblyLoadInvocationCount { get; }
        public int AcceptedAssemblyLoadInvocationCount { get; }
        public int BlockedAssemblyLoadInvocationCount { get; }
        public int OpenAssemblyLoadInvocationCount { get; }
        public int StartedAssemblyLoadInvocationCount { get; }
        public int CompletedAssemblyLoadInvocationCount { get; }
        public int LoadedAssemblyLoadInvocationCount { get; }
        public OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot FirstBlockedAssemblyLoadInvocation { get; }
        public int PluginTypeResolutionCount { get; }
        public int AcceptedPluginTypeResolutionCount { get; }
        public int BlockedPluginTypeResolutionCount { get; }
        public int OpenPluginTypeResolutionCount { get; }
        public int StartedPluginTypeResolutionCount { get; }
        public int CompletedPluginTypeResolutionCount { get; }
        public int ResolvedPluginTypeResolutionCount { get; }
        public OpenACAIBepInExPluginTypeResolutionSnapshot FirstBlockedPluginTypeResolution { get; }
        public int PluginInstanceCreationCount { get; }
        public int AcceptedPluginInstanceCreationCount { get; }
        public int BlockedPluginInstanceCreationCount { get; }
        public int OpenPluginInstanceCreationCount { get; }
        public int StartedPluginInstanceCreationCount { get; }
        public int CompletedPluginInstanceCreationCount { get; }
        public int CreatedPluginInstanceCreationCount { get; }
        public OpenACAIBepInExPluginInstanceCreationSnapshot FirstBlockedPluginInstanceCreation { get; }
        public int PluginLoadInvocationCount { get; }
        public int AcceptedPluginLoadInvocationCount { get; }
        public int BlockedPluginLoadInvocationCount { get; }
        public int OpenPluginLoadInvocationCount { get; }
        public int StartedPluginLoadInvocationCount { get; }
        public int CompletedPluginLoadInvocationCount { get; }
        public int InvokedPluginLoadInvocationCount { get; }
        public OpenACAIBepInExPluginLoadInvocationSnapshot FirstBlockedPluginLoadInvocation { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginInvocationExecutorSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginInvocationExecutorSnapshot(
                false,
                false,
                false,
                false,
                false,
                true,
                "openacai.bepinex-stage-executor.v1",
                "unavailable",
                "unavailable",
                false,
                "openacai.metadata-gate",
                OpenACAIBepInExPluginActivationPolicySnapshot.CreateUnavailable(reasonCode, detail),
                Array.Empty<OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot>(),
                Array.Empty<OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot>(),
                "INFO",
                reasonCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginActivationPolicySnapshot
    {
        internal OpenACAIBepInExPluginActivationPolicySnapshot(
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

        internal static OpenACAIBepInExPluginActivationPolicySnapshot CreateUnavailable(
            string issueCode,
            string detail)
        {
            return new OpenACAIBepInExPluginActivationPolicySnapshot(
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

    public sealed class OpenACAIBepInExPluginInvocationPreflightSnapshot
    {
        internal OpenACAIBepInExPluginInvocationPreflightSnapshot(
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
            IReadOnlyList<OpenACAIBepInExPluginInvocationPipelineStageSnapshot> invocationPipelineStages,
            bool runtimeExecutionEnabled,
            bool invocationGateOpen,
            bool invocationGateBlocked,
            string isolationBoundary,
            OpenACAIBepInExPluginActivationPolicySnapshot activationPolicy,
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
            InvocationPipelineStages = invocationPipelineStages ?? Array.Empty<OpenACAIBepInExPluginInvocationPipelineStageSnapshot>();
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
            ActivationPolicy = activationPolicy ?? OpenACAIBepInExPluginActivationPolicySnapshot.CreateUnavailable("activation-policy-unavailable", "BepInEx plugin activation policy was not available for this invocation preflight.");
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
        public IReadOnlyList<OpenACAIBepInExPluginInvocationPipelineStageSnapshot> InvocationPipelineStages { get; }
        public int InvocationPipelineStageCount { get; }
        public int RequiredInvocationPipelineStageCount { get; }
        public int OpenInvocationPipelineStageCount { get; }
        public int BlockedInvocationPipelineStageCount { get; }
        public int CompletedInvocationPipelineStageCount { get; }
        public OpenACAIBepInExPluginInvocationPipelineStageSnapshot FirstBlockedInvocationPipelineStage { get; }
        public bool RuntimeExecutionEnabled { get; }
        public bool InvocationGateOpen { get; }
        public bool InvocationGateBlocked { get; }
        public string IsolationBoundary { get; }
        public OpenACAIBepInExPluginActivationPolicySnapshot ActivationPolicy { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginInvocationPreflightSnapshot CreateUnavailable(
            string issueCode,
            string detail)
        {
            return new OpenACAIBepInExPluginInvocationPreflightSnapshot(
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
                Array.Empty<OpenACAIBepInExPluginInvocationPipelineStageSnapshot>(),
                false,
                false,
                true,
                "openacai.metadata-gate",
                OpenACAIBepInExPluginActivationPolicySnapshot.CreateUnavailable(issueCode, detail),
                "INFO",
                issueCode,
                detail);
        }
    }

    public sealed class OpenACAIBepInExPluginCompatibilityPlanSnapshot
    {
        internal OpenACAIBepInExPluginCompatibilityPlanSnapshot(
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
            OpenACAIBepInExPluginInvocationPreflightSnapshot invocationPreflight,
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate,
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
            InvocationPreflight = invocationPreflight ?? OpenACAIBepInExPluginInvocationPreflightSnapshot.CreateUnavailable("invocation-preflight-unavailable", "BepInEx plugin invocation preflight was not available for this plan.");
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
        public OpenACAIBepInExPluginInvocationPreflightSnapshot InvocationPreflight { get; }
        public OpenACAIBepInExPluginCompatibilityCandidateSnapshot Candidate { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot
    {
        internal OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot(
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
            OpenACAIBepInExPluginInvocationPreflightSnapshot invocationPreflight,
            IReadOnlyList<OpenACAIBepInExPluginInvocationStageAttemptSnapshot> invocationStageAttempts,
            OpenACAIBepInExPluginInvocationExecutorSnapshot invocationExecutor,
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan,
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate,
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
            InvocationPreflight = invocationPreflight ?? OpenACAIBepInExPluginInvocationPreflightSnapshot.CreateUnavailable("invocation-preflight-unavailable", "BepInEx plugin invocation preflight was not available for this attempt.");
            InvocationStageAttempts = invocationStageAttempts ?? Array.Empty<OpenACAIBepInExPluginInvocationStageAttemptSnapshot>();
            InvocationStageAttemptCount = InvocationStageAttempts.Count;
            RecordedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.AttemptRecorded);
            BlockedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.GateBlocked);
            OpenInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.GateOpen);
            StartedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.StageExecutionStarted);
            CompletedInvocationStageAttemptCount = InvocationStageAttempts.Count(stage => stage != null && stage.StageCompleted);
            FirstBlockedInvocationStageAttempt = InvocationStageAttempts.FirstOrDefault(stage => stage != null && stage.GateBlocked);
            InvocationExecutor = invocationExecutor ?? OpenACAIBepInExPluginInvocationExecutorSnapshot.CreateUnavailable(
                "invocation-executor-unavailable",
                "BepInEx plugin invocation executor state was not available for this attempt.");
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
        public OpenACAIBepInExPluginInvocationPreflightSnapshot InvocationPreflight { get; }
        public IReadOnlyList<OpenACAIBepInExPluginInvocationStageAttemptSnapshot> InvocationStageAttempts { get; }
        public int InvocationStageAttemptCount { get; }
        public int RecordedInvocationStageAttemptCount { get; }
        public int BlockedInvocationStageAttemptCount { get; }
        public int OpenInvocationStageAttemptCount { get; }
        public int StartedInvocationStageAttemptCount { get; }
        public int CompletedInvocationStageAttemptCount { get; }
        public OpenACAIBepInExPluginInvocationStageAttemptSnapshot FirstBlockedInvocationStageAttempt { get; }
        public OpenACAIBepInExPluginInvocationExecutorSnapshot InvocationExecutor { get; }
        public OpenACAIBepInExPluginCompatibilityPlanSnapshot Plan { get; }
        public OpenACAIBepInExPluginCompatibilityCandidateSnapshot Candidate { get; }
        public string GeneratedUtc { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }
}
