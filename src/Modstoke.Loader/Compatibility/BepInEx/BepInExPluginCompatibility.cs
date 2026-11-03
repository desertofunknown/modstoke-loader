// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Modstoke-owned BepInEx plugin compatibility gate. This inventories legacy
// BepInEx plugin assemblies from metadata and decides which IL2CPP BasePlugin
// candidates BepInExPluginHost may load. The host loads them with
// Assembly.LoadFrom into the default load context; the plans and attempts
// recorded here are diagnostics and never load plugin code.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    public static partial class BepInExPluginCompatibility
    {
        public const string ServiceKey = Modding.BepInExPluginCompatibilityServiceKey;
        public const int MaxCandidateCount = 256;
        public const int MaxLoadPlanCount = 128;
        public const int MaxLoadAttemptCount = 128;
        public const int MaxParserHarnessCount = 32;
        public const int MaxAssemblyReferenceCount = 96;
        public const int MaxLifecycleMethodCount = 24;
        public const int MaxExternalBaseChainDepth = 12;
        public const string ParserHarnessDirectoryName = "BepInExPluginMetadataHarness";
        public const string CandidateHarnessDirectoryName = "BepInExPluginCandidateHarness";
        internal const string RetiredStageMessage = "BepInExPluginHost loads plugins with Assembly.LoadFrom; this stage model is no longer recorded.";
        private const string RetiredGateMessage = "Always true. BepInExPluginHost loads eligible plugins with Assembly.LoadFrom.";
        private const string RetiredRequestMessage = "Use AttemptLoad. Stage requests are no longer recorded.";
        private const string CandidateSourceInventory = "inventory";
        private const string CandidateSourceValidationProbe = "validation-probe";
        private const string LoadPlanSourceCandidate = "candidate";
        private const string LoadPlanSourceLookup = "lookup";
        private const string LoadPlanSourceUnknown = "unknown";
        private const string InvocationBoundaryMetadataGate = "openacai.metadata-only-bepinex-gate";
        private const string ActivationPolicyNoSideEffectRisk = "no-metadata-side-effect-risk";
        private const string ActivationPolicyModuleInitializerRisk = "module-initializer-before-assembly-load";
        private const string ActivationPolicyStaticConstructorRisk = "static-constructor-before-instance-creation";

        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoPlannedLoadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadedCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<BepInExPluginCompatibilityPlanSnapshot> RecordedLoadPlans =
            new List<BepInExPluginCompatibilityPlanSnapshot>();
        private static readonly List<BepInExPluginCompatibilityLoadAttemptSnapshot> RecordedLoadAttempts =
            new List<BepInExPluginCompatibilityLoadAttemptSnapshot>();
        private static BepInExPluginCompatibilitySnapshot _state = BepInExPluginCompatibilitySnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _loadPlanRequestCount;
        private static int _acceptedLoadPlanCount;
        private static int _rejectedLoadPlanCount;
        private static string _lastLoadPlanPluginId = "";
        private static string _lastLoadPlanStatus = "INFO";
        private static string _lastLoadPlanDetail = "No BepInEx plugin compatibility load plan has been requested.";
        private static string _lastLoadPlanReason = "none";
        private static int _loadAttemptRequestCount;
        private static int _acceptedLoadAttemptCount;
        private static int _blockedLoadAttemptCount;
        private static int _rejectedLoadAttemptCount;
        private static string _lastLoadAttemptPluginId = "";
        private static string _lastLoadAttemptStatus = "INFO";
        private static string _lastLoadAttemptDetail = "No BepInEx plugin compatibility load attempt has been requested.";
        private static string _lastLoadAttemptReason = "none";

        public static BepInExPluginCompatibilitySnapshot State
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
        public static bool ScanningEnabled => true;
        public static bool RuntimePluginLoadingEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationStageExecutorEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationAssemblyLoadTransitionRequestEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationAssemblyLoadResolverPlanEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationAssemblyLoadOperationEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationAssemblyLoadContextPlanEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationAssemblyLoadContextAllocationEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationAssemblyLoadContextRegistrationEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationAssemblyLoadInvocationEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationPluginTypeResolutionEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationPluginInstanceCreationEnabled => true;
        [Obsolete(RetiredGateMessage)]
        public static bool InvocationPluginLoadInvocationEnabled => true;
        public static IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> Candidates => State.Candidates;
        public static IReadOnlyList<BepInExPluginCompatibilityPlanSnapshot> LoadPlans => State.LoadPlans;
        public static IReadOnlyList<BepInExPluginCompatibilityLoadAttemptSnapshot> LoadAttempts => State.LoadAttempts;

        public static IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> GetCandidates()
        {
            return Candidates;
        }

        public static IReadOnlyList<BepInExPluginCompatibilityPlanSnapshot> GetLoadPlans()
        {
            return LoadPlans;
        }

        public static IReadOnlyList<BepInExPluginCompatibilityLoadAttemptSnapshot> GetLoadAttempts()
        {
            return LoadAttempts;
        }

        internal static IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> DiscoverRuntimeCandidates(IEnumerable<string> assemblyPaths)
        {
            var generatedUtc = DateTimeOffset.UtcNow.ToString("O");
            var entries = assemblyPaths.Select(assemblyPath =>
            {
                var name = Path.GetFileNameWithoutExtension(assemblyPath);
                return new ModInventoryEntry
                {
                    Id = name,
                    Name = name,
                    SourceFamily = ModSourceFamilies.LegacyBepInEx,
                    LoaderType = ModLoaderTypes.BepInExPlugin,
                    ManifestType = "BepInExPlugin",
                    ExpectedLocation = "BepInEx/plugins",
                    Enabled = true,
                    PackagePath = Path.GetDirectoryName(assemblyPath),
                    AssemblyPath = assemblyPath,
                    HasAssembly = File.Exists(assemblyPath)
                };
            }).ToArray();
            var inventory = ModInventorySnapshot.FromInternal(new ModInventoryState
            {
                GeneratedUtc = generatedUtc,
                Entries = entries
            });
            return CreateInventoryCandidates(inventory, generatedUtc).ToArray();
        }

        internal static void MarkLoadedByOpenAcai(BepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            if (candidate == null)
            {
                return;
            }

            lock (SyncRoot)
            {
                LoadedCandidateIds.Add(candidate.CandidateId);
            }
        }

        private static bool IsLoadedByOpenAcai(BepInExPluginAssemblyMetadata metadata, string assemblyPath)
        {
            var candidateId = BepInExPluginCompatibilityCandidateSnapshot.CreateCandidateId(
                assemblyPath, metadata.PluginTypeName, metadata.PluginGuid, metadata.PluginVersion);
            lock (SyncRoot)
            {
                return LoadedCandidateIds.Contains(candidateId);
            }
        }

        public static BepInExPluginCompatibilityPlanSnapshot PlanLoad(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            Refresh();
            return plan;
        }

        // Records a diagnostic attempt only; BepInExPluginHost owns loading.
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptLoad(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var attempt = CreateLoadAttempt(plan);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadTransitionRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadResolverPlanRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadOperationRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadContextPlanRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadContextAllocationRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadContextRegistrationRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadInvocationRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptPluginTypeResolutionRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptPluginInstanceCreationRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        [Obsolete(RetiredRequestMessage)]
        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptPluginLoadInvocationRequest(string idOrAssemblyName) => AttemptLoad(idOrAssemblyName);

        public static BepInExPluginCompatibilitySnapshot Refresh()
        {
            return Refresh(Modding.ModInventory);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(BepInExPluginCompatibilitySnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke BepInEx plugin compatibility provider could not be registered.");
            }
        }

        internal static BepInExPluginCompatibilitySnapshot Refresh(ModInventoryState inventory)
        {
            return Refresh(ModInventorySnapshot.FromInternal(inventory));
        }

        internal static BepInExPluginCompatibilitySnapshot Refresh(ModInventorySnapshot inventory)
        {
            var snapshot = CreateSnapshot(inventory);
            if (RecordAutomaticLoadPlans(snapshot))
            {
                snapshot = CreateSnapshot(inventory);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(BepInExPluginCompatibilitySnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? BepInExPluginCompatibilitySnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _state);
                LoaderRuntime.MarkShim(
                    CompatibilityShimIds.BepInExPlugins,
                    "BepInEx plugins",
                    "BepInEx",
                    _state.CompatibilityIssueCount == 0 && _state.ParserHarnessIssueCount == 0
                        ? CompatibilityShimStatus.Ready
                        : CompatibilityShimStatus.Warning,
                    "openacai-bepinex-plugin-host",
                    true,
                    _state.CompatibilityIssueCount == 0 && _state.ParserHarnessIssueCount == 0,
                    false,
                    _state.Detail);
            }
        }

        private static BepInExPluginCompatibilitySnapshot CreateSnapshot(ModInventorySnapshot inventory)
        {
            inventory = inventory ?? ModInventorySnapshot.Empty();
            var generatedUtc = DateTimeOffset.UtcNow.ToString("O");
            var pluginRoot = ResolvePluginRoot();
            var parserHarnessRoot = ResolveParserHarnessRoot();
            var candidateHarnessRoot = ResolveCandidateHarnessRoot();
            var parserHarnesses = CreateParserHarnesses(parserHarnessRoot, pluginRoot, generatedUtc);
            var inventoryCandidates = CreateInventoryCandidates(inventory, generatedUtc)
                .Take(MaxCandidateCount)
                .ToArray();
            var candidateHarnesses = CreateCandidateHarnesses(
                    inventoryCandidates.Length + 1,
                    candidateHarnessRoot,
                    pluginRoot,
                    generatedUtc)
                .ToArray();
            var candidates = inventoryCandidates
                .Concat(candidateHarnesses)
                .Take(MaxCandidateCount)
                .ToArray();

            int loadPlanRequestCount;
            int acceptedLoadPlanCount;
            int rejectedLoadPlanCount;
            string lastLoadPlanPluginId;
            string lastLoadPlanStatus;
            string lastLoadPlanDetail;
            string lastLoadPlanReason;
            BepInExPluginCompatibilityPlanSnapshot[] loadPlans;
            int loadAttemptRequestCount;
            int acceptedLoadAttemptCount;
            int blockedLoadAttemptCount;
            int rejectedLoadAttemptCount;
            string lastLoadAttemptPluginId;
            string lastLoadAttemptStatus;
            string lastLoadAttemptDetail;
            string lastLoadAttemptReason;
            BepInExPluginCompatibilityLoadAttemptSnapshot[] loadAttempts;
            lock (SyncRoot)
            {
                loadPlanRequestCount = _loadPlanRequestCount;
                acceptedLoadPlanCount = _acceptedLoadPlanCount;
                rejectedLoadPlanCount = _rejectedLoadPlanCount;
                lastLoadPlanPluginId = _lastLoadPlanPluginId ?? "";
                lastLoadPlanStatus = _lastLoadPlanStatus ?? "";
                lastLoadPlanDetail = _lastLoadPlanDetail ?? "";
                lastLoadPlanReason = _lastLoadPlanReason ?? "";
                loadPlans = RecordedLoadPlans.ToArray();
                loadAttemptRequestCount = _loadAttemptRequestCount;
                acceptedLoadAttemptCount = _acceptedLoadAttemptCount;
                blockedLoadAttemptCount = _blockedLoadAttemptCount;
                rejectedLoadAttemptCount = _rejectedLoadAttemptCount;
                lastLoadAttemptPluginId = _lastLoadAttemptPluginId ?? "";
                lastLoadAttemptStatus = _lastLoadAttemptStatus ?? "";
                lastLoadAttemptDetail = _lastLoadAttemptDetail ?? "";
                lastLoadAttemptReason = _lastLoadAttemptReason ?? "";
                loadAttempts = RecordedLoadAttempts.ToArray();
            }

            var assemblies = candidates.DistinctBy(candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var metadataIssueCount = assemblies.Count(candidate => candidate.AssemblyExists && !candidate.AssemblyMetadataReadable);
            var assemblyReferenceCount = assemblies.Sum(candidate => candidate.AssemblyReferenceCount);
            var resolvedAssemblyReferenceCount = assemblies.Sum(candidate => candidate.ResolvedAssemblyReferenceCount);
            var unresolvedAssemblyReferenceCount = assemblies.Sum(candidate => candidate.UnresolvedAssemblyReferenceCount);
            var compatibilityIssueCount = candidates.Count(candidate => candidate.Issue);
            var compatibilityReadyCount = candidates.Count(candidate => candidate.CompatibilityReady);
            var parserHarnessAssemblyReferenceCount = parserHarnesses.Sum(harness => harness.AssemblyReferenceCount);
            var parserHarnessResolvedAssemblyReferenceCount = parserHarnesses.Sum(harness => harness.ResolvedAssemblyReferenceCount);
            var parserHarnessUnresolvedAssemblyReferenceCount = parserHarnesses.Sum(harness => harness.UnresolvedAssemblyReferenceCount);
            var parserHarnessIssueCount = parserHarnesses.Count(harness => harness.Issue);
            var parserHarnessReadyCount = parserHarnesses.Count(harness => harness.PositiveMetadataParserVerified);
            var parserHarnessUnsafeLocationCount = parserHarnesses.Count(harness => !harness.LocatedOutsidePluginRoot);
            var positiveMetadataParserVerified = parserHarnessReadyCount > 0;
            var gateIssueCount = compatibilityIssueCount + parserHarnessIssueCount;
            var status = gateIssueCount == 0 ? "READY" : "CHECK";
            var detail = gateIssueCount == 0
                ? "The Modstoke BepInEx host loads metadata-valid IL2CPP BasePlugin candidates with Assembly.LoadFrom into the default load context."
                : gateIssueCount + " BepInEx plugin compatibility gate issue(s) were found.";

            return new BepInExPluginCompatibilitySnapshot(
                ServiceKey,
                generatedUtc,
                true,
                status,
                detail,
                ScanningEnabled,
                RuntimePluginLoadingEnabled,
                pluginRoot,
                Directory.Exists(pluginRoot),
                parserHarnessRoot,
                Directory.Exists(parserHarnessRoot),
                parserHarnesses,
                parserHarnessReadyCount,
                parserHarnessIssueCount,
                parserHarnesses.Sum(harness => harness.BepInExPluginAttributeCount),
                parserHarnessUnsafeLocationCount,
                parserHarnessAssemblyReferenceCount,
                parserHarnessResolvedAssemblyReferenceCount,
                parserHarnessUnresolvedAssemblyReferenceCount,
                positiveMetadataParserVerified,
                inventory.Entries.Count,
                inventory.LegacyBepInExPlugins.Count,
                inventory.Libraries.Count,
                inventory.OpenAcaiMods.Count,
                inventory.LegacyRedLoaderMods.Count,
                candidates,
                assemblies.Count(candidate => candidate.AssemblyMetadataReadable),
                metadataIssueCount,
                candidates.Sum(candidate => candidate.BepInExPluginAttributeCount),
                assemblyReferenceCount,
                resolvedAssemblyReferenceCount,
                unresolvedAssemblyReferenceCount,
                compatibilityReadyCount,
                compatibilityIssueCount,
                candidates.Count(candidate => candidate.LoadEligible),
                candidates.Count(candidate => candidate.LoadGateOpen),
                candidates.Count(candidate => candidate.LoadGateBlocked),
                candidates.Count(candidate => candidate.LoadedByOpenAcai),
                loadPlans,
                loadAttempts,
                loadPlanRequestCount,
                acceptedLoadPlanCount,
                rejectedLoadPlanCount,
                lastLoadPlanPluginId,
                lastLoadPlanStatus,
                lastLoadPlanDetail,
                lastLoadPlanReason,
                loadAttemptRequestCount,
                acceptedLoadAttemptCount,
                blockedLoadAttemptCount,
                rejectedLoadAttemptCount,
                lastLoadAttemptPluginId,
                lastLoadAttemptStatus,
                lastLoadAttemptDetail,
                lastLoadAttemptReason);
        }
    }
}
