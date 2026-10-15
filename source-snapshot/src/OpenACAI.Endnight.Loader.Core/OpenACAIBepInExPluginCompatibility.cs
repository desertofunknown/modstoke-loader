// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned BepInEx plugin compatibility gate. This inventories legacy
// BepInEx plugin assemblies and opens the runtime load path for metadata-valid
// IL2CPP BasePlugin candidates through OpenACAIBepInExPluginHost.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BepInEx;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAIBepInExPluginCompatibility
    {
        public const string ServiceKey = OpenACAIModding.BepInExPluginCompatibilityServiceKey;
        public const int MaxCandidateCount = 256;
        public const int MaxLoadPlanCount = 128;
        public const int MaxLoadAttemptCount = 128;
        public const int MaxParserHarnessCount = 32;
        public const int MaxAssemblyReferenceCount = 96;
        public const int MaxLifecycleMethodCount = 24;
        public const int MaxExternalBaseChainDepth = 12;
        public const string ParserHarnessDirectoryName = "BepInExPluginMetadataHarness";
        public const string CandidateHarnessDirectoryName = "BepInExPluginCandidateHarness";
        private const string ValidateMissingLoadAttemptCommandId = "validateMissingLoadAttempt";
        private const string ValidationMissingLoadAttemptTargetPrefix = "openacai.validation.missing-bepinex-plugin.";
        private const string ValidateDeferredLoadAttemptCommandId = "validateDeferredLoadAttempt";
        private const string ValidationDeferredLoadAttemptTargetPrefix = "openacai.validation.deferred-bepinex-plugin.";
        private const string ValidateMetadataLoadAttemptCommandId = "validateMetadataLoadAttempt";
        private const string ValidateCandidateLoadAttemptCommandId = "validateCandidateLoadAttempt";
        private const string ValidateAssemblyLoadTransitionRequestCommandId = "validateAssemblyLoadTransitionRequest";
        private const string ValidateAssemblyLoadResolverPlanCommandId = "validateAssemblyLoadResolverPlan";
        private const string ValidateAssemblyLoadOperationCommandId = "validateAssemblyLoadOperation";
        private const string ValidateAssemblyLoadContextPlanCommandId = "validateAssemblyLoadContextPlan";
        private const string ValidateAssemblyLoadContextAllocationCommandId = "validateAssemblyLoadContextAllocation";
        private const string ValidateAssemblyLoadContextRegistrationCommandId = "validateAssemblyLoadContextRegistration";
        private const string ValidateAssemblyLoadInvocationCommandId = "validateAssemblyLoadInvocation";
        private const string ValidatePluginTypeResolutionCommandId = "validatePluginTypeResolution";
        private const string ValidatePluginInstanceCreationCommandId = "validatePluginInstanceCreation";
        private const string ValidatePluginLoadInvocationCommandId = "validatePluginLoadInvocation";
        private const string ValidateProbeAssemblyLoadCommandId = "validateProbeAssemblyLoad";
        private const string ValidateProbeTypeActivationCommandId = "validateProbeTypeActivation";
        private const string ValidateProbeInstanceCreationCommandId = "validateProbeInstanceCreation";
        private const string ValidateProbeLoadInvocationCommandId = "validateProbeLoadInvocation";
        private const string CandidateSourceInventory = "inventory";
        private const string CandidateSourceValidationProbe = "validation-probe";
        private const string LoadPlanSourceCandidate = "candidate";
        private const string LoadPlanSourceLookup = "lookup";
        private const string LoadPlanSourceParserHarness = "parser-harness";
        private const string LoadPlanSourceValidationCommand = "validation-command";
        private const string LoadPlanSourceUnknown = "unknown";
        private const string InvocationBoundaryDisabledRuntimeGate = "openacai.disabled-runtime-plugin-gate";
        private const string InvocationBoundaryMetadataGate = "openacai.metadata-only-bepinex-gate";
        private const string ActivationPolicyNoSideEffectRisk = "no-metadata-side-effect-risk";
        private const string ActivationPolicyModuleInitializerRisk = "module-initializer-before-assembly-load";
        private const string ActivationPolicyStaticConstructorRisk = "static-constructor-before-instance-creation";
        private const string ActivationPolicyRuntimeGateRisk = "runtime-plugin-loading-disabled";
        private const string InvocationStageExecutorStateMachineVersion = "openacai.bepinex-stage-executor.v1";
        private const string InvocationAssemblyLoadTransitionRequestStageKind = "assembly-load";
        private const string InvocationAssemblyLoadResolverPlanContextName = "openacai.bepinex.assembly-load.metadata-context";
        private const string InvocationAssemblyLoadOperationKind = "assembly-load-from-path";
        private const string InvocationAssemblyLoadOperationMethodName = "System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath";
        private const string InvocationAssemblyLoadOperationRollbackStrategy = "no-runtime-mutation";
        private const string InvocationAssemblyLoadContextAllocationStrategy = "dedicated-collectible-context";
        private const string InvocationAssemblyLoadContextConstructorSignature = "System.Runtime.Loader.AssemblyLoadContext..ctor(string name, bool isCollectible)";
        private const string InvocationAssemblyLoadContextRegistrationStrategy = "track-collectible-context-before-load";
        private const string InvocationAssemblyLoadContextTrackingCollection = "openacai.bepinex.assembly-load.context-registry";
        private const string InvocationAssemblyLoadInvocationStrategy = "load-from-path-after-context-registration";
        private const string InvocationPluginTypeResolutionStrategy = "resolve-plugin-type-from-loaded-assembly";
        private const string InvocationPluginTypeResolutionMethodName = "System.Reflection.Assembly.GetType(string, bool, bool)";
        private const string InvocationPluginInstanceCreationStrategy = "create-base-plugin-instance-from-resolved-type";
        private const string InvocationPluginInstanceCreationMethodName = "System.Activator.CreateInstance(System.Type)";
        private const string InvocationPluginLoadInvocationStrategy = "invoke-base-plugin-load-after-instance-creation";
        private const string InvocationPluginLoadInvocationMethodName = "BepInEx.Unity.IL2CPP.BasePlugin.Load()";

        private static readonly string[] UnityLifecycleMethodNames =
        {
            "Awake",
            "Start",
            "OnEnable",
            "OnDisable",
            "OnDestroy",
            "Update",
            "LateUpdate",
            "FixedUpdate",
            "OnGUI",
            "OnApplicationQuit"
        };

        private static readonly HashSet<string> UnityLifecycleMethodNameLookup =
            new HashSet<string>(UnityLifecycleMethodNames, StringComparer.Ordinal);

        private static readonly bool RuntimePluginLoadingEnabledValue = true;
        private static readonly bool InvocationStageExecutorEnabledValue = true;
        private static readonly bool InvocationAssemblyLoadTransitionRequestEnabledValue = true;
        private static readonly bool InvocationAssemblyLoadResolverPlanEnabledValue = true;
        private static readonly bool InvocationAssemblyLoadOperationEnabledValue = true;
        private static readonly bool InvocationAssemblyLoadContextPlanEnabledValue = true;
        private static readonly bool InvocationAssemblyLoadContextAllocationEnabledValue = true;
        private static readonly bool InvocationAssemblyLoadContextRegistrationEnabledValue = true;
        private static readonly bool InvocationAssemblyLoadInvocationEnabledValue = true;
        private static readonly bool InvocationPluginTypeResolutionEnabledValue = true;
        private static readonly bool InvocationPluginInstanceCreationEnabledValue = true;
        private static readonly bool InvocationPluginLoadInvocationEnabledValue = true;
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> AutoPlannedLoadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadedCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<OpenACAIBepInExPluginCompatibilityPlanSnapshot> RecordedLoadPlans =
            new List<OpenACAIBepInExPluginCompatibilityPlanSnapshot>();
        private static readonly List<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot> RecordedLoadAttempts =
            new List<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot>();
        private static OpenACAIBepInExPluginCompatibilitySnapshot _state = OpenACAIBepInExPluginCompatibilitySnapshot.CreateUnknown();
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

        public static OpenACAIBepInExPluginCompatibilitySnapshot State
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
        public static bool RuntimePluginLoadingEnabled => RuntimePluginLoadingEnabledValue;
        public static bool InvocationStageExecutorEnabled => InvocationStageExecutorEnabledValue;
        public static bool InvocationAssemblyLoadTransitionRequestEnabled => InvocationAssemblyLoadTransitionRequestEnabledValue;
        public static bool InvocationAssemblyLoadResolverPlanEnabled => InvocationAssemblyLoadResolverPlanEnabledValue;
        public static bool InvocationAssemblyLoadOperationEnabled => InvocationAssemblyLoadOperationEnabledValue;
        public static bool InvocationAssemblyLoadContextPlanEnabled => InvocationAssemblyLoadContextPlanEnabledValue;
        public static bool InvocationAssemblyLoadContextAllocationEnabled => InvocationAssemblyLoadContextAllocationEnabledValue;
        public static bool InvocationAssemblyLoadContextRegistrationEnabled => InvocationAssemblyLoadContextRegistrationEnabledValue;
        public static bool InvocationAssemblyLoadInvocationEnabled => InvocationAssemblyLoadInvocationEnabledValue;
        public static bool InvocationPluginTypeResolutionEnabled => InvocationPluginTypeResolutionEnabledValue;
        public static bool InvocationPluginInstanceCreationEnabled => InvocationPluginInstanceCreationEnabledValue;
        public static bool InvocationPluginLoadInvocationEnabled => InvocationPluginLoadInvocationEnabledValue;
        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> Candidates => State.Candidates;
        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityPlanSnapshot> LoadPlans => State.LoadPlans;
        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot> LoadAttempts => State.LoadAttempts;

        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> GetCandidates()
        {
            return Candidates;
        }

        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityPlanSnapshot> GetLoadPlans()
        {
            return LoadPlans;
        }

        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot> GetLoadAttempts()
        {
            return LoadAttempts;
        }

        internal static IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> DiscoverRuntimeCandidates(IEnumerable<string> assemblyPaths)
        {
            var generatedUtc = DateTimeOffset.UtcNow.ToString("O");
            var entries = assemblyPaths.Select(assemblyPath => new ModInventoryEntry
            {
                Id = Path.GetFileNameWithoutExtension(assemblyPath),
                Name = Path.GetFileNameWithoutExtension(assemblyPath),
                SourceFamily = OpenACAIModSourceFamilies.LegacyBepInEx,
                LoaderType = OpenACAIModLoaderTypes.BepInExPlugin,
                ManifestType = "BepInExPlugin",
                ExpectedLocation = "BepInEx/plugins",
                Enabled = true,
                PackagePath = Path.GetDirectoryName(assemblyPath),
                AssemblyPath = assemblyPath,
                HasAssembly = File.Exists(assemblyPath)
            }).ToArray();
            var inventory = OpenACAIModInventorySnapshot.FromInternal(new ModInventoryState
            {
                GeneratedUtc = generatedUtc,
                Entries = entries
            });
            return CreateInventoryCandidates(inventory, generatedUtc).ToArray();
        }

        internal static void MarkLoadedByOpenAcai(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
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
            var candidateId = OpenACAIBepInExPluginCompatibilityCandidateSnapshot.CreateCandidateId(
                assemblyPath, metadata.PluginTypeName, metadata.PluginGuid, metadata.PluginVersion);
            lock (SyncRoot)
            {
                return LoadedCandidateIds.Contains(candidateId);
            }
        }

        public static OpenACAIBepInExPluginCompatibilityPlanSnapshot PlanLoad(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            Refresh();
            return plan;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptLoad(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var attempt = CreateLoadAttempt(plan);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadTransitionRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-assembly-load-request-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadResolverPlanRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-assembly-resolver-plan-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadOperationRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-assembly-load-operation-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadContextPlanRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-assembly-load-context-plan-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadContextAllocationRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-assembly-load-context-allocation-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadContextRegistrationRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-assembly-load-context-registration-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true,
                true,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAssemblyLoadInvocationRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-assembly-load-invocation-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true,
                true,
                true,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptPluginTypeResolutionRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-plugin-type-resolution-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true,
                true,
                true,
                true,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptPluginInstanceCreationRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-plugin-instance-creation-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptPluginLoadInvocationRequest(string idOrAssemblyName)
        {
            var plan = CreateLoadPlan(State, idOrAssemblyName);
            RecordLoadPlan(plan);
            var requestId = "bepinex-plugin-load-invocation-" + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = CreateLoadAttempt(
                plan,
                InvocationAssemblyLoadTransitionRequestStageKind,
                LoadPlanSourceValidationCommand,
                requestId,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        public static OpenACAIBepInExPluginCompatibilitySnapshot Refresh()
        {
            return Refresh(OpenACAIModding.ModInventory);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAIBepInExPluginCompatibilitySnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI BepInEx plugin compatibility provider could not be registered.");
            }
        }

        internal static OpenACAIBepInExPluginCompatibilitySnapshot Refresh(ModInventoryState inventory)
        {
            return Refresh(OpenACAIModInventorySnapshot.FromInternal(inventory));
        }

        internal static OpenACAIBepInExPluginCompatibilitySnapshot Refresh(OpenACAIModInventorySnapshot inventory)
        {
            var snapshot = CreateSnapshot(inventory);
            if (RecordAutomaticLoadPlans(snapshot))
            {
                snapshot = CreateSnapshot(inventory);
            }

            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAIBepInExPluginCompatibilitySnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
                OpenACAIRuntime.MarkShim(
                    OpenACAICompatibilityShimIds.BepInExPlugins,
                    "BepInEx plugins",
                    "BepInEx",
                    _state.CompatibilityIssueCount == 0 && _state.ParserHarnessIssueCount == 0
                        ? OpenACAICompatibilityShimStatus.Ready
                        : OpenACAICompatibilityShimStatus.Warning,
                    "openacai-bepinex-plugin-host",
                    true,
                    _state.CompatibilityIssueCount == 0 && _state.ParserHarnessIssueCount == 0,
                    false,
                    _state.Detail);
            }
        }

        private static OpenACAIBepInExPluginCompatibilitySnapshot CreateSnapshot(OpenACAIModInventorySnapshot inventory)
        {
            inventory = inventory ?? OpenACAIModInventorySnapshot.Empty();
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
            OpenACAIBepInExPluginCompatibilityPlanSnapshot[] loadPlans;
            int loadAttemptRequestCount;
            int acceptedLoadAttemptCount;
            int blockedLoadAttemptCount;
            int rejectedLoadAttemptCount;
            string lastLoadAttemptPluginId;
            string lastLoadAttemptStatus;
            string lastLoadAttemptDetail;
            string lastLoadAttemptReason;
            OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot[] loadAttempts;
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

            var assemblies = candidates.GroupBy(candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
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
            var positiveMetadataParserVerified = parserHarnesses.Any(harness => harness.PositiveMetadataParserVerified);
            var gateIssueCount = compatibilityIssueCount + parserHarnessIssueCount;
            var status = gateIssueCount == 0 ? "READY" : "CHECK";
            var detail = gateIssueCount == 0
                ? "OpenACAI BepInEx plugin compatibility runtime loading is enabled for metadata-valid IL2CPP BasePlugin candidates."
                : gateIssueCount + " BepInEx plugin compatibility gate issue(s) were found.";

            return new OpenACAIBepInExPluginCompatibilitySnapshot(
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

        private static IEnumerable<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> CreateInventoryCandidates(
            OpenACAIModInventorySnapshot inventory, string generatedUtc)
        {
            var sequenceNumber = 1;
            foreach (var entry in inventory.LegacyBepInExPlugins
                .Where(entry => entry != null && !entry.IsLibrary && !entry.IsOpenAcai && !entry.IsLegacyRedLoader)
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssemblyPath ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var pluginTypes = new List<BepInExPluginAssemblyMetadata>();
                var assembly = InspectAssemblyMetadata(entry.AssemblyPath, pluginTypes, allowModuleInitialization: true);
                if (pluginTypes.Count == 0)
                    pluginTypes.Add(assembly);

                foreach (var metadata in pluginTypes)
                    yield return CreateCandidate(sequenceNumber++, entry, metadata, generatedUtc);
            }
        }

        private static OpenACAIBepInExPluginCompatibilityCandidateSnapshot CreateCandidate(
            int sequenceNumber,
            OpenACAIModInfo entry,
            BepInExPluginAssemblyMetadata metadata,
            string generatedUtc)
        {
            var assemblyPath = entry == null ? "" : entry.AssemblyPath;
            var assemblyExists = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath);
            var status = "CHECK";
            var issueCode = "not-validated";
            var detail = "BepInEx plugin compatibility metadata has not been validated.";
            var compatibilityReady = false;

            if (entry == null)
            {
                issueCode = "missing-inventory-entry";
                detail = "BepInEx plugin compatibility candidate did not include an inventory entry.";
            }
            else if (!entry.Enabled)
            {
                status = "INFO";
                issueCode = "disabled-plugin";
                detail = "The BepInEx plugin candidate is disabled and will not be planned by OpenACAI.";
            }
            else if (!entry.HasAssembly || !assemblyExists)
            {
                issueCode = "missing-assembly";
                detail = "The BepInEx plugin candidate does not have a loadable assembly file.";
            }
            else if (!metadata.Readable)
            {
                issueCode = metadata.IssueCode;
                detail = metadata.Detail;
            }
            else if (metadata.PluginAttributeCount == 0)
            {
                issueCode = "bepinex-plugin-attribute-missing";
                detail = "Assembly metadata does not contain a BepInEx.BepInPlugin attribute on a plugin type.";
            }
            else if (string.IsNullOrWhiteSpace(metadata.PluginGuid)
                || string.IsNullOrWhiteSpace(metadata.PluginName)
                || string.IsNullOrWhiteSpace(metadata.PluginVersion))
            {
                issueCode = "bepinex-plugin-metadata-incomplete";
                detail = "BepInEx plugin metadata was found, but guid, name, or version was missing.";
            }
            else if (metadata.UnresolvedAssemblyReferenceCount > 0)
            {
                issueCode = "unresolved-assembly-references";
                detail = metadata.UnresolvedAssemblyReferenceCount + " assembly reference(s) could not be resolved by the OpenACAI BepInEx compatibility dependency pass.";
            }
            else if (metadata.ExecutionPlan != null && metadata.ExecutionPlan.Issue)
            {
                issueCode = string.IsNullOrWhiteSpace(metadata.ExecutionPlan.IssueCode)
                    ? "bepinex-plugin-execution-plan-not-ready"
                    : metadata.ExecutionPlan.IssueCode;
                detail = metadata.ExecutionPlan.Detail;
            }
            else
            {
                compatibilityReady = true;
                status = RuntimePluginLoadingEnabled ? "READY" : "DEFERRED";
                issueCode = RuntimePluginLoadingEnabled ? "" : "runtime-plugin-loading-disabled";
                detail = RuntimePluginLoadingEnabled
                    ? "OpenACAI BepInEx plugin compatibility metadata is ready for the runtime load path."
                    : "OpenACAI accepted this BepInEx plugin compatibility candidate as metadata-only; runtime plugin loading remains disabled.";
            }

            var loadGateOpen = compatibilityReady && RuntimePluginLoadingEnabled;
            var loadGateBlocked = compatibilityReady && !RuntimePluginLoadingEnabled;
            var loadEligible = compatibilityReady && RuntimePluginLoadingEnabled;

            return new OpenACAIBepInExPluginCompatibilityCandidateSnapshot(
                sequenceNumber,
                entry == null ? "" : NormalizeId(entry.Id),
                string.IsNullOrWhiteSpace(metadata.PluginName) ? entry?.DisplayName ?? "" : metadata.PluginName,
                string.IsNullOrWhiteSpace(metadata.AssemblyName) ? Path.GetFileNameWithoutExtension(assemblyPath) : metadata.AssemblyName,
                assemblyPath,
                entry == null ? "" : entry.PackagePath,
                CandidateSourceInventory,
                false,
                string.IsNullOrWhiteSpace(metadata.AssemblyVersion) && entry != null ? entry.AssemblyVersion : metadata.AssemblyVersion,
                entry == null ? 0 : entry.AssemblySize ?? 0,
                entry != null && entry.Enabled,
                entry == null ? "" : entry.Status,
                entry == null ? "" : entry.IssueCode,
                assemblyExists,
                metadata.Readable,
                metadata.AssemblyReferences,
                metadata.AssemblyReferenceCount,
                metadata.ResolvedAssemblyReferenceCount,
                metadata.UnresolvedAssemblyReferenceCount,
                metadata.PluginAttributeCount,
                metadata.PluginAttributeCount > 0,
                metadata.PluginGuid,
                metadata.PluginName,
                metadata.PluginVersion,
                metadata.PluginTypeName,
                metadata.ExecutionPlan,
                compatibilityReady,
                RuntimePluginLoadingEnabled,
                loadEligible,
                loadGateOpen,
                loadGateBlocked,
                IsLoadedByOpenAcai(metadata, assemblyPath),
                status,
                issueCode,
                detail,
                generatedUtc);
        }

        private static IEnumerable<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> CreateCandidateHarnesses(
            int firstSequenceNumber,
            string candidateHarnessRoot,
            string pluginRoot,
            string generatedUtc)
        {
            if (string.IsNullOrWhiteSpace(candidateHarnessRoot) || !Directory.Exists(candidateHarnessRoot))
            {
                yield break;
            }

            var remaining = Math.Max(0, MaxCandidateCount - Math.Max(0, firstSequenceNumber - 1));
            var sequenceNumber = Math.Max(1, firstSequenceNumber);
            foreach (var assemblyPath in Directory.EnumerateFiles(candidateHarnessRoot, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .Take(remaining))
            {
                yield return CreateCandidateHarness(sequenceNumber, assemblyPath, pluginRoot, generatedUtc);
                sequenceNumber++;
            }
        }

        private static OpenACAIBepInExPluginCompatibilityCandidateSnapshot CreateCandidateHarness(
            int sequenceNumber,
            string assemblyPath,
            string pluginRoot,
            string generatedUtc)
        {
            var metadata = InspectAssemblyMetadata(assemblyPath);
            var assemblyExists = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath);
            var locatedOutsidePluginRoot = !IsPathUnderRoot(assemblyPath, pluginRoot);
            var status = "CHECK";
            var issueCode = "not-validated";
            var detail = "BepInEx candidate validation probe has not been validated.";
            var compatibilityReady = false;

            if (!assemblyExists)
            {
                issueCode = "missing-assembly";
                detail = "Candidate validation probe assembly file was not found.";
            }
            else if (!locatedOutsidePluginRoot)
            {
                issueCode = "unsafe-candidate-harness-location";
                detail = "Candidate validation probe assembly is inside BepInEx/plugins and could be loaded by the transitional bootstrap.";
            }
            else if (!metadata.Readable)
            {
                issueCode = metadata.IssueCode;
                detail = metadata.Detail;
            }
            else if (metadata.PluginAttributeCount == 0)
            {
                issueCode = "bepinex-plugin-attribute-missing";
                detail = "Candidate validation probe metadata does not contain a BepInEx.BepInPlugin attribute on a plugin type.";
            }
            else if (string.IsNullOrWhiteSpace(metadata.PluginGuid)
                || string.IsNullOrWhiteSpace(metadata.PluginName)
                || string.IsNullOrWhiteSpace(metadata.PluginVersion))
            {
                issueCode = "bepinex-plugin-metadata-incomplete";
                detail = "Candidate validation probe BepInPlugin metadata was found, but guid, name, or version was missing.";
            }
            else if (metadata.UnresolvedAssemblyReferenceCount > 0)
            {
                issueCode = "unresolved-assembly-references";
                detail = metadata.UnresolvedAssemblyReferenceCount + " candidate validation probe assembly reference(s) could not be resolved by the OpenACAI BepInEx compatibility dependency pass.";
            }
            else if (metadata.ExecutionPlan != null && metadata.ExecutionPlan.Issue)
            {
                issueCode = string.IsNullOrWhiteSpace(metadata.ExecutionPlan.IssueCode)
                    ? "candidate-harness-execution-plan-not-ready"
                    : metadata.ExecutionPlan.IssueCode;
                detail = "Candidate validation probe BepInEx execution plan is not ready: " + metadata.ExecutionPlan.Detail;
            }
            else
            {
                compatibilityReady = true;
                status = RuntimePluginLoadingEnabled ? "READY" : "DEFERRED";
                issueCode = RuntimePluginLoadingEnabled ? "" : "runtime-plugin-loading-disabled";
                detail = RuntimePluginLoadingEnabled
                    ? "OpenACAI accepted this BepInEx validation candidate for the runtime load path."
                    : "OpenACAI accepted this BepInEx validation candidate as metadata-only; runtime plugin loading remains disabled and the assembly is outside BepInEx/plugins.";
            }

            var loadGateOpen = compatibilityReady && RuntimePluginLoadingEnabled;
            var loadGateBlocked = compatibilityReady && !RuntimePluginLoadingEnabled;
            var loadEligible = compatibilityReady && RuntimePluginLoadingEnabled;
            var assemblyName = string.IsNullOrWhiteSpace(metadata.AssemblyName)
                ? Path.GetFileNameWithoutExtension(assemblyPath)
                : metadata.AssemblyName;
            var pluginId = string.IsNullOrWhiteSpace(metadata.PluginGuid)
                ? "openacai.validation.bepinex.candidate." + NormalizeId(Path.GetFileNameWithoutExtension(assemblyPath))
                : metadata.PluginGuid;
            var displayName = string.IsNullOrWhiteSpace(metadata.PluginName)
                ? assemblyName
                : metadata.PluginName;

            return new OpenACAIBepInExPluginCompatibilityCandidateSnapshot(
                sequenceNumber,
                pluginId,
                displayName,
                assemblyName,
                assemblyPath,
                Path.GetDirectoryName(assemblyPath) ?? "",
                CandidateSourceValidationProbe,
                true,
                metadata.AssemblyVersion,
                GetFileSize(assemblyPath),
                true,
                CandidateSourceValidationProbe,
                "",
                assemblyExists,
                metadata.Readable,
                metadata.AssemblyReferences,
                metadata.AssemblyReferenceCount,
                metadata.ResolvedAssemblyReferenceCount,
                metadata.UnresolvedAssemblyReferenceCount,
                metadata.PluginAttributeCount,
                metadata.PluginAttributeCount > 0,
                metadata.PluginGuid,
                metadata.PluginName,
                metadata.PluginVersion,
                metadata.PluginTypeName,
                metadata.ExecutionPlan,
                compatibilityReady,
                RuntimePluginLoadingEnabled,
                loadEligible,
                loadGateOpen,
                loadGateBlocked,
                IsLoadedByOpenAcai(metadata, assemblyPath),
                status,
                issueCode,
                detail,
                generatedUtc);
        }

        private static IReadOnlyList<OpenACAIBepInExPluginMetadataHarnessSnapshot> CreateParserHarnesses(
            string parserHarnessRoot,
            string pluginRoot,
            string generatedUtc)
        {
            if (string.IsNullOrWhiteSpace(parserHarnessRoot) || !Directory.Exists(parserHarnessRoot))
            {
                return Array.Empty<OpenACAIBepInExPluginMetadataHarnessSnapshot>();
            }

            return Directory.EnumerateFiles(parserHarnessRoot, "*.dll", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxParserHarnessCount)
                .Select((assemblyPath, index) => CreateParserHarness(index + 1, assemblyPath, pluginRoot, generatedUtc))
                .ToArray();
        }

        private static OpenACAIBepInExPluginMetadataHarnessSnapshot CreateParserHarness(
            int sequenceNumber,
            string assemblyPath,
            string pluginRoot,
            string generatedUtc)
        {
            var metadata = InspectAssemblyMetadata(assemblyPath);
            var assemblyExists = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath);
            var locatedOutsidePluginRoot = !IsPathUnderRoot(assemblyPath, pluginRoot);
            var status = "CHECK";
            var issueCode = "not-validated";
            var detail = "BepInEx plugin metadata parser harness has not been validated.";
            var positiveMetadataParserVerified = false;

            if (!assemblyExists)
            {
                issueCode = "missing-assembly";
                detail = "Parser harness assembly file was not found.";
            }
            else if (!locatedOutsidePluginRoot)
            {
                issueCode = "unsafe-harness-location";
                detail = "Parser harness assembly is inside BepInEx/plugins and could be loaded by the transitional bootstrap.";
            }
            else if (!metadata.Readable)
            {
                issueCode = metadata.IssueCode;
                detail = metadata.Detail;
            }
            else if (metadata.UnresolvedAssemblyReferenceCount > 0)
            {
                issueCode = "unresolved-assembly-references";
                detail = metadata.UnresolvedAssemblyReferenceCount + " parser harness assembly reference(s) could not be resolved by the OpenACAI BepInEx compatibility dependency pass.";
            }
            else if (metadata.PluginAttributeCount == 0)
            {
                issueCode = "bepinex-plugin-attribute-missing";
                detail = "Parser harness assembly metadata does not contain a BepInEx.BepInPlugin attribute.";
            }
            else if (string.IsNullOrWhiteSpace(metadata.PluginGuid)
                || string.IsNullOrWhiteSpace(metadata.PluginName)
                || string.IsNullOrWhiteSpace(metadata.PluginVersion))
            {
                issueCode = "bepinex-plugin-metadata-incomplete";
                detail = "Parser harness BepInPlugin metadata was found, but guid, name, or version was missing.";
            }
            else if (metadata.ExecutionPlan != null && metadata.ExecutionPlan.Issue)
            {
                issueCode = string.IsNullOrWhiteSpace(metadata.ExecutionPlan.IssueCode)
                    ? "parser-harness-execution-plan-not-ready"
                    : metadata.ExecutionPlan.IssueCode;
                detail = "Parser harness BepInEx execution plan is not ready: " + metadata.ExecutionPlan.Detail;
            }
            else
            {
                status = "READY";
                issueCode = "";
                positiveMetadataParserVerified = true;
                detail = "OpenACAI parsed valid BepInPlugin metadata from a harness assembly outside BepInEx/plugins without loading it.";
            }

            return new OpenACAIBepInExPluginMetadataHarnessSnapshot(
                sequenceNumber,
                Path.GetFileNameWithoutExtension(assemblyPath),
                string.IsNullOrWhiteSpace(metadata.AssemblyName) ? Path.GetFileNameWithoutExtension(assemblyPath) : metadata.AssemblyName,
                assemblyPath,
                metadata.AssemblyVersion,
                GetFileSize(assemblyPath),
                assemblyExists,
                metadata.Readable,
                metadata.AssemblyReferences,
                metadata.AssemblyReferenceCount,
                metadata.ResolvedAssemblyReferenceCount,
                metadata.UnresolvedAssemblyReferenceCount,
                metadata.PluginAttributeCount,
                metadata.PluginAttributeCount > 0,
                metadata.PluginGuid,
                metadata.PluginName,
                metadata.PluginVersion,
                metadata.PluginTypeName,
                metadata.ExecutionPlan,
                locatedOutsidePluginRoot,
                positiveMetadataParserVerified,
                status,
                issueCode,
                detail,
                generatedUtc);
        }

        private static bool RecordAutomaticLoadPlans(OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
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

        private static OpenACAIBepInExPluginCompatibilityPlanSnapshot CreateLoadPlan(
            OpenACAIBepInExPluginCompatibilitySnapshot snapshot,
            string idOrAssemblyName)
        {
            snapshot = snapshot ?? OpenACAIBepInExPluginCompatibilitySnapshot.CreateUnknown();
            var normalizedId = NormalizeId(idOrAssemblyName);
            var status = "CHECK";
            var reason = "invalid";
            var detail = "BepInEx plugin compatibility load plan is invalid.";
            var accepted = false;
            var canLoadNow = false;
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate = null;

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
                    if (!RuntimePluginLoadingEnabled)
                    {
                        status = "DEFERRED";
                        reason = "runtime-plugin-loading-disabled";
                        detail = "OpenACAI accepted the BepInEx plugin compatibility plan; runtime plugin loading remains disabled.";
                    }
                    else
                    {
                        canLoadNow = true;
                        status = "READY";
                        reason = "ready";
                        detail = "OpenACAI BepInEx plugin compatibility plan is ready for runtime loading.";
                    }
                }
            }

            return new OpenACAIBepInExPluginCompatibilityPlanSnapshot(
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

        private static OpenACAIBepInExPluginCompatibilityPlanSnapshot CreateAcceptedValidationLoadPlan(string idOrAssemblyName)
        {
            var normalizedId = NormalizeId(idOrAssemblyName);
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                normalizedId = ValidationDeferredLoadAttemptTargetPrefix + DateTimeOffset.UtcNow.UtcTicks;
            }

            return new OpenACAIBepInExPluginCompatibilityPlanSnapshot(
                normalizedId,
                0,
                true,
                false,
                RuntimePluginLoadingEnabled,
                false,
                true,
                false,
                LoadPlanSourceValidationCommand,
                true,
                "DEFERRED",
                "runtime-plugin-loading-disabled",
                "OpenACAI accepted a validation-only BepInEx compatibility plan; runtime plugin loading remains disabled and no plugin assembly was loaded.",
                OpenACAIBepInExPluginInvocationPreflightSnapshot.CreateUnavailable(
                    "validation-only-plan",
                    "Generated validation-only plans do not target a concrete BepInEx plugin invocation preflight."),
                null,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptAcceptedValidationLoad(string idOrAssemblyName)
        {
            var plan = CreateAcceptedValidationLoadPlan(idOrAssemblyName);
            RecordLoadPlan(plan);
            var attempt = CreateLoadAttempt(plan);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        private static OpenACAIBepInExPluginCompatibilityPlanSnapshot CreateParserHarnessLoadPlan(
            OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            snapshot = snapshot ?? OpenACAIBepInExPluginCompatibilitySnapshot.CreateUnknown();
            var harness = SelectPositiveParserHarness(snapshot);
            if (harness == null)
            {
                return new OpenACAIBepInExPluginCompatibilityPlanSnapshot(
                    "openacai.parser-harness.unavailable",
                    0,
                    false,
                    false,
                    RuntimePluginLoadingEnabled,
                    false,
                    true,
                    false,
                    LoadPlanSourceParserHarness,
                    false,
                    "CHECK",
                    "parser-harness-unavailable",
                    "No positive BepInEx metadata parser harness was available under OpenACAI validation probes.",
                    OpenACAIBepInExPluginInvocationPreflightSnapshot.CreateUnavailable(
                        "parser-harness-unavailable",
                        "Parser harness invocation preflight was not available because no positive parser harness was present."),
                    null,
                    DateTimeOffset.UtcNow.ToString("O"));
            }

            var pluginId = string.IsNullOrWhiteSpace(harness.PluginGuid)
                ? "openacai.parser-harness." + NormalizeId(harness.HarnessId)
                : harness.PluginGuid;

            return new OpenACAIBepInExPluginCompatibilityPlanSnapshot(
                pluginId,
                harness.SequenceNumber,
                true,
                false,
                RuntimePluginLoadingEnabled,
                false,
                true,
                false,
                LoadPlanSourceParserHarness,
                false,
                "DEFERRED",
                "runtime-plugin-loading-disabled",
                "OpenACAI accepted a BepInEx metadata parser harness plan for " + harness.AssemblyName + "; runtime plugin loading remains disabled and no harness or plugin code was loaded.",
                OpenACAIBepInExPluginInvocationPreflightSnapshot.CreateUnavailable(
                    "parser-harness-plan",
                    "Parser harness plans prove metadata parsing but do not target a concrete candidate invocation preflight."),
                null,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptParserHarnessMetadataLoad()
        {
            var snapshot = Refresh();
            var plan = CreateParserHarnessLoadPlan(snapshot);
            RecordLoadPlan(plan);
            var attempt = CreateLoadAttempt(plan);
            RecordLoadAttempt(attempt);
            Refresh();
            return attempt;
        }

        private static OpenACAIBepInExPluginInvocationPreflightSnapshot CreateInvocationPreflight(
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            if (candidate == null)
            {
                return OpenACAIBepInExPluginInvocationPreflightSnapshot.CreateUnavailable(
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
                status = RuntimePluginLoadingEnabled ? "READY" : "DEFERRED";
                issueCode = RuntimePluginLoadingEnabled ? "" : "runtime-plugin-loading-disabled";
                detail = RuntimePluginLoadingEnabled
                    ? "OpenACAI BepInEx invocation preflight is ready for failure-isolated runtime execution."
                    : "OpenACAI resolved BepInEx invocation preflight for " + pluginTypeName
                        + "; assembly load, instance creation, and "
                        + (string.IsNullOrWhiteSpace(entrypointMethodName) ? invocationKind : entrypointMethodName)
                        + " invocation remain blocked by the disabled runtime plugin gate.";
            }

            var invocationGateOpen = preflightReady && RuntimePluginLoadingEnabled;
            var invocationGateBlocked = preflightReady && !RuntimePluginLoadingEnabled;
            var isolationBoundary = invocationGateBlocked
                ? InvocationBoundaryDisabledRuntimeGate
                : InvocationBoundaryMetadataGate;
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
                RuntimePluginLoadingEnabled,
                isolationBoundary);

            return new OpenACAIBepInExPluginInvocationPreflightSnapshot(
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
                invocationGateOpen,
                invocationGateBlocked,
                isolationBoundary,
                activationPolicy,
                status,
                issueCode,
                detail);
        }

        private static OpenACAIBepInExPluginActivationPolicySnapshot CreateActivationPolicy(
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate,
            OpenACAIBepInExPluginExecutionPlanSnapshot executionPlan,
            bool assemblyLoadRequired,
            bool constructorInvocationRequired,
            bool loadInvocationRequired)
        {
            if (candidate == null)
            {
                return OpenACAIBepInExPluginActivationPolicySnapshot.CreateUnavailable(
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
            var detail = "OpenACAI BepInEx activation policy is not ready.";

            if (!candidateReady)
            {
                issueCode = string.IsNullOrWhiteSpace(candidate.IssueCode) ? "compatibility-not-ready" : candidate.IssueCode;
                detail = "OpenACAI BepInEx activation policy is blocked by candidate compatibility state: " + candidate.Detail;
                assemblyLoadBlockReason = issueCode;
            }
            else if (executionPlan == null)
            {
                issueCode = "execution-plan-unavailable";
                detail = "OpenACAI BepInEx activation policy requires execution-plan metadata before any runtime activation boundary can open.";
                assemblyLoadBlockReason = issueCode;
            }
            else if (!executionPlanReady)
            {
                issueCode = string.IsNullOrWhiteSpace(executionPlan.IssueCode)
                    ? "execution-plan-not-ready"
                    : executionPlan.IssueCode;
                detail = "OpenACAI BepInEx activation policy is blocked by execution-plan metadata: " + executionPlan.Detail;
                assemblyLoadBlockReason = issueCode;
            }
            else if (!assemblyLoadRequired)
            {
                issueCode = "assembly-load-target-missing";
                detail = "OpenACAI BepInEx activation policy requires a candidate assembly path before any activation boundary can open.";
                assemblyLoadBlockReason = issueCode;
            }
            else if (moduleInitializerFound)
            {
                issueCode = "bepinex-plugin-module-initializer-present";
                detail = "OpenACAI BepInEx activation policy blocks assembly load because module initializers may run before constructor or Load() gates.";
                assemblyLoadBlockReason = issueCode;
            }
            else if (!RuntimePluginLoadingEnabled)
            {
                status = "DEFERRED";
                issueCode = "runtime-plugin-loading-disabled";
                detail = "OpenACAI BepInEx activation policy resolved this candidate, but real plugin execution remains blocked by the disabled runtime plugin gate.";
                assemblyLoadBlockReason = issueCode;
            }
            else if (!InvocationAssemblyLoadInvocationEnabled)
            {
                status = "DEFERRED";
                issueCode = "assembly-load-invocation-disabled";
                detail = "OpenACAI BepInEx activation policy blocks before LoadFromAssemblyPath until the isolated assembly-load boundary is enabled.";
                assemblyLoadBlockReason = issueCode;
            }

            var assemblyLoadAllowed = policyEvaluated
                && executionPlanReady
                && assemblyLoadRequired
                && !moduleInitializerFound
                && RuntimePluginLoadingEnabled
                && InvocationAssemblyLoadInvocationEnabled
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
                else if (!InvocationPluginTypeResolutionEnabled)
                {
                    constructorInvocationBlockReason = "plugin-type-resolution-disabled";
                }
                else if (!InvocationPluginInstanceCreationEnabled)
                {
                    constructorInvocationBlockReason = "plugin-instance-creation-disabled";
                }
            }
            else
            {
                constructorInvocationBlockReason = "not-required";
            }

            var constructorInvocationAllowed = constructorInvocationRequired
                && assemblyLoadAllowed
                && !staticConstructorFound
                && InvocationPluginTypeResolutionEnabled
                && InvocationPluginInstanceCreationEnabled;
            var constructorInvocationBlocked = constructorInvocationRequired && !constructorInvocationAllowed;

            if (loadInvocationRequired)
            {
                if (!constructorInvocationAllowed)
                {
                    loadInvocationBlockReason = string.IsNullOrWhiteSpace(constructorInvocationBlockReason)
                        ? "plugin-instance-creation-not-allowed"
                        : constructorInvocationBlockReason;
                }
                else if (!InvocationPluginLoadInvocationEnabled)
                {
                    loadInvocationBlockReason = "plugin-load-invocation-disabled";
                }
            }
            else
            {
                loadInvocationBlockReason = "not-required";
            }

            var loadInvocationAllowed = loadInvocationRequired
                && constructorInvocationAllowed
                && InvocationPluginLoadInvocationEnabled;
            var loadInvocationBlocked = loadInvocationRequired && !loadInvocationAllowed;
            var realPluginExecutionAllowed = RuntimePluginLoadingEnabled
                && (!assemblyLoadRequired || assemblyLoadAllowed)
                && (!constructorInvocationRequired || constructorInvocationAllowed)
                && (!loadInvocationRequired || loadInvocationAllowed);

            if (realPluginExecutionAllowed)
            {
                status = "READY";
                issueCode = "";
                detail = "OpenACAI BepInEx activation policy allows the required real-plugin runtime boundaries for this candidate.";
            }
            else if (string.Equals(status, "CHECK", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(assemblyLoadBlockReason)
                && (constructorInvocationBlocked || loadInvocationBlocked))
            {
                issueCode = constructorInvocationBlocked
                    ? constructorInvocationBlockReason
                    : loadInvocationBlockReason;
                detail = "OpenACAI BepInEx activation policy blocks before "
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
            else if (!RuntimePluginLoadingEnabled)
            {
                sideEffectRisk = ActivationPolicyRuntimeGateRisk;
            }

            if (string.IsNullOrWhiteSpace(constructorInvocationBlockReason) && constructorInvocationBlocked)
            {
                constructorInvocationBlockReason = "plugin-instance-creation-not-allowed";
            }

            if (string.IsNullOrWhiteSpace(loadInvocationBlockReason) && loadInvocationBlocked)
            {
                loadInvocationBlockReason = "plugin-load-invocation-not-allowed";
            }

            return new OpenACAIBepInExPluginActivationPolicySnapshot(
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

        private static string DetermineInvocationKind(OpenACAIBepInExPluginExecutionPlanSnapshot executionPlan)
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

        private static string DetermineInvocationEntrypoint(OpenACAIBepInExPluginExecutionPlanSnapshot executionPlan)
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

        private static IReadOnlyList<OpenACAIBepInExPluginInvocationPipelineStageSnapshot> CreateInvocationPipelineStages(
            string pluginTypeName,
            string invocationKind,
            string entrypointMethodName,
            bool preflightReady,
            bool assemblyLoadRequired,
            bool instanceCreationRequired,
            bool methodInvocationRequired,
            bool runtimeExecutionEnabled,
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
                    runtimeExecutionEnabled,
                    isolationBoundary,
                    "Load the candidate plugin assembly into an OpenACAI-controlled compatibility context."),
                CreateInvocationPipelineStage(
                    2,
                    "Instance Create",
                    "instance-create",
                    instanceCreationRequired,
                    preflightReady,
                    runtimeExecutionEnabled,
                    isolationBoundary,
                    "Create the BepInEx plugin instance for " + (string.IsNullOrWhiteSpace(pluginTypeName) ? "the resolved plugin type" : pluginTypeName) + "."),
                CreateInvocationPipelineStage(
                    3,
                    "Entrypoint Invoke",
                    string.IsNullOrWhiteSpace(invocationKind) ? "entrypoint-invoke" : invocationKind,
                    methodInvocationRequired,
                    preflightReady,
                    runtimeExecutionEnabled,
                    isolationBoundary,
                    "Invoke " + (string.IsNullOrWhiteSpace(entrypointMethodName) ? "the resolved plugin entrypoint" : entrypointMethodName) + " through the OpenACAI BepInEx compatibility invoker.")
            };
        }

        private static OpenACAIBepInExPluginInvocationPipelineStageSnapshot CreateInvocationPipelineStage(
            int sequenceNumber,
            string stageName,
            string stageKind,
            bool required,
            bool preflightReady,
            bool runtimeExecutionEnabled,
            string isolationBoundary,
            string actionDetail)
        {
            var gateOpen = required && preflightReady && runtimeExecutionEnabled;
            var gateBlocked = required && preflightReady && !runtimeExecutionEnabled;
            var status = "CHECK";
            var issueCode = "invocation-stage-preflight-not-ready";
            var detail = (actionDetail ?? "").Trim();

            if (!required)
            {
                status = "INFO";
                issueCode = "not-required";
                detail = stageName + " is not required for this BepInEx compatibility candidate.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                issueCode = "runtime-plugin-loading-disabled";
                detail = stageName + " is required but remains blocked by the disabled OpenACAI runtime plugin gate. " + detail;
            }
            else if (gateOpen)
            {
                status = "READY";
                issueCode = "";
                detail = stageName + " is ready for failure-isolated OpenACAI runtime execution. " + detail;
            }
            else if (string.IsNullOrWhiteSpace(detail))
            {
                detail = stageName + " is waiting on invocation preflight readiness.";
            }
            else
            {
                detail = stageName + " is waiting on invocation preflight readiness. " + detail;
            }

            return new OpenACAIBepInExPluginInvocationPipelineStageSnapshot(
                sequenceNumber,
                stageName,
                stageKind,
                required,
                gateOpen,
                gateBlocked,
                false,
                isolationBoundary,
                status,
                issueCode,
                detail);
        }

        private static void RecordLoadPlan(OpenACAIBepInExPluginCompatibilityPlanSnapshot plan)
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

        private static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot CreateLoadAttempt(
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan,
            string requestedExecutorStageKind = null,
            string executorRequestSource = null,
            string executorRequestId = null,
            bool includeAssemblyLoadResolverPlan = false,
            bool includeAssemblyLoadOperation = false,
            bool includeAssemblyLoadContextPlan = false,
            bool includeAssemblyLoadContextAllocation = false,
            bool includeAssemblyLoadContextRegistration = false,
            bool includeAssemblyLoadInvocation = false,
            bool includePluginTypeResolution = false,
            bool includePluginInstanceCreation = false,
            bool includePluginLoadInvocation = false)
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
                loadGateOpen = plan.LoadGateOpen && RuntimePluginLoadingEnabled;
                loadGateBlocked = !loadGateOpen;
                if (!RuntimePluginLoadingEnabled)
                {
                    status = "DEFERRED";
                    reason = "runtime-plugin-loading-disabled";
                    detail = "OpenACAI recorded a failure-isolated BepInEx load attempt; runtime plugin loading remains disabled and no plugin code was invoked.";
                }
                else if (!plan.CanLoadNow || !plan.LoadGateOpen)
                {
                    status = "DEFERRED";
                    reason = "load-gate-blocked";
                    detail = "OpenACAI recorded a failure-isolated BepInEx load attempt, but the load gate is blocked and no plugin code was invoked.";
                }
                else
                {
                    status = "DEFERRED";
                    reason = "runtime-invocation-path-not-implemented";
                    detail = "OpenACAI recorded a failure-isolated BepInEx load attempt; the runtime invocation path is not enabled for plugin code yet.";
                    loadGateBlocked = true;
                }
            }

            var invocationStageAttempts = CreateInvocationStageAttempts(plan);
            var invocationExecutor = CreateInvocationExecutor(
                plan,
                invocationStageAttempts,
                requestedExecutorStageKind,
                executorRequestSource,
                executorRequestId,
                includeAssemblyLoadResolverPlan,
                includeAssemblyLoadOperation,
                includeAssemblyLoadContextPlan,
                includeAssemblyLoadContextAllocation,
                includeAssemblyLoadContextRegistration,
                includeAssemblyLoadInvocation,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation);

            return new OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot(
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
                invocationExecutor,
                plan,
                plan == null ? null : plan.Candidate,
                generatedUtc);
        }

        private static IReadOnlyList<OpenACAIBepInExPluginInvocationStageAttemptSnapshot> CreateInvocationStageAttempts(
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan)
        {
            if (plan == null || plan.InvocationPreflight == null || plan.InvocationPreflight.InvocationPipelineStageCount == 0)
            {
                return Array.Empty<OpenACAIBepInExPluginInvocationStageAttemptSnapshot>();
            }

            var stages = new List<OpenACAIBepInExPluginInvocationStageAttemptSnapshot>();
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

        private static OpenACAIBepInExPluginInvocationStageAttemptSnapshot CreateInvocationStageAttempt(
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan,
            OpenACAIBepInExPluginInvocationPipelineStageSnapshot stage)
        {
            var attemptRecorded = plan != null && plan.Accepted && stage.Required;
            var gateOpen = attemptRecorded && stage.GateOpen && RuntimePluginLoadingEnabled;
            var gateBlocked = attemptRecorded && (stage.GateBlocked || !RuntimePluginLoadingEnabled);
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
            else if (gateBlocked)
            {
                status = "DEFERRED";
                reasonCode = "runtime-plugin-loading-disabled";
                detail = stage.StageName + " stage attempt was recorded but blocked before execution by the disabled OpenACAI runtime plugin gate.";
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = stage.StageName + " stage attempt is ready for a future failure-isolated executor.";
            }

            return new OpenACAIBepInExPluginInvocationStageAttemptSnapshot(
                stage.SequenceNumber,
                stage.StageName,
                stage.StageKind,
                stage.Required,
                attemptRecorded,
                false,
                false,
                gateOpen,
                gateBlocked,
                true,
                stage.IsolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginInvocationExecutorSnapshot CreateInvocationExecutor(
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan,
            IReadOnlyList<OpenACAIBepInExPluginInvocationStageAttemptSnapshot> stageAttempts,
            string requestedExecutorStageKind,
            string executorRequestSource,
            string executorRequestId,
            bool includeAssemblyLoadResolverPlan,
            bool includeAssemblyLoadOperation,
            bool includeAssemblyLoadContextPlan,
            bool includeAssemblyLoadContextAllocation,
            bool includeAssemblyLoadContextRegistration,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation)
        {
            if (plan == null || stageAttempts == null || stageAttempts.Count == 0)
            {
                return OpenACAIBepInExPluginInvocationExecutorSnapshot.CreateUnavailable(
                    "invocation-stage-attempts-unavailable",
                    "OpenACAI BepInEx stage executor state requires stage-attempt records.");
            }

            var transitions = new List<OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot>();
            foreach (var stageAttempt in stageAttempts)
            {
                if (stageAttempt == null)
                {
                    continue;
                }

                transitions.Add(CreateInvocationExecutorTransition(stageAttempt));
            }

            var transitionRequests = CreateInvocationExecutorTransitionRequests(
                plan,
                stageAttempts,
                requestedExecutorStageKind,
                executorRequestSource,
                executorRequestId,
                includeAssemblyLoadResolverPlan,
                includeAssemblyLoadOperation,
                includeAssemblyLoadContextPlan,
                includeAssemblyLoadContextAllocation,
                includeAssemblyLoadContextRegistration,
                includeAssemblyLoadInvocation,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation);
            var hasTransitions = transitions.Count > 0;
            var executionGateOpen = hasTransitions
                && InvocationStageExecutorEnabled
                && RuntimePluginLoadingEnabled
                && transitions.All(transition => transition != null && transition.GateOpen);
            var executionGateBlocked = hasTransitions && !executionGateOpen;
            var isolationBoundary = transitions
                .Where(transition => transition != null && !string.IsNullOrWhiteSpace(transition.IsolationBoundary))
                .Select(transition => transition.IsolationBoundary)
                .FirstOrDefault() ?? InvocationBoundaryDisabledRuntimeGate;
            var status = "CHECK";
            var reasonCode = "invocation-stage-executor-not-ready";
            var detail = "OpenACAI BepInEx invocation stage executor state is not ready.";
            var finalState = "unavailable";

            if (!hasTransitions)
            {
                status = "INFO";
                reasonCode = "no-stage-transitions";
                detail = "No OpenACAI BepInEx invocation executor transitions were recorded for this attempt.";
                finalState = "unavailable";
            }
            else if (!RuntimePluginLoadingEnabled)
            {
                status = "DEFERRED";
                reasonCode = "runtime-plugin-loading-disabled";
                detail = "OpenACAI BepInEx invocation stage executor recorded blocked transitions; runtime plugin loading remains disabled and no stage was started.";
                finalState = "blocked";
            }
            else if (!InvocationStageExecutorEnabled)
            {
                status = "DEFERRED";
                reasonCode = "invocation-stage-executor-disabled";
                detail = "OpenACAI BepInEx invocation stage executor is modeled but disabled, so no stage was started.";
                finalState = "blocked";
            }
            else if (executionGateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI BepInEx invocation stage executor is ready for a future failure-isolated stage run.";
                finalState = "pending";
            }

            var activationPolicy = plan == null || plan.InvocationPreflight == null
                ? null
                : plan.InvocationPreflight.ActivationPolicy;

            return new OpenACAIBepInExPluginInvocationExecutorSnapshot(
                hasTransitions,
                InvocationStageExecutorEnabled,
                RuntimePluginLoadingEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                executionGateOpen,
                executionGateBlocked,
                InvocationStageExecutorStateMachineVersion,
                hasTransitions ? "pending" : "unavailable",
                finalState,
                false,
                isolationBoundary,
                activationPolicy,
                transitions,
                transitionRequests,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot CreateInvocationExecutorTransition(
            OpenACAIBepInExPluginInvocationStageAttemptSnapshot stageAttempt)
        {
            var gateOpen = stageAttempt.AttemptRecorded
                && stageAttempt.GateOpen
                && InvocationStageExecutorEnabled
                && RuntimePluginLoadingEnabled;
            var gateBlocked = stageAttempt.AttemptRecorded && !gateOpen;
            var status = "CHECK";
            var reasonCode = "invocation-stage-transition-not-ready";
            var action = "wait";
            var toState = "pending";
            var detail = stageAttempt.StageName + " executor transition is waiting.";

            if (!stageAttempt.Required)
            {
                status = "INFO";
                reasonCode = "not-required";
                action = "skip";
                toState = "skipped";
                detail = stageAttempt.StageName + " executor transition skipped because the stage is not required.";
            }
            else if (!stageAttempt.AttemptRecorded)
            {
                status = "CHECK";
                reasonCode = "stage-attempt-not-recorded";
                action = "reject";
                toState = "rejected";
                detail = stageAttempt.StageName + " executor transition rejected because no stage attempt was recorded.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                reasonCode = RuntimePluginLoadingEnabled
                    ? "invocation-stage-executor-disabled"
                    : "runtime-plugin-loading-disabled";
                action = "block-before-stage-start";
                toState = "blocked";
                detail = stageAttempt.StageName + " executor transition was blocked before stage start.";
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                action = "ready";
                toState = "pending";
                detail = stageAttempt.StageName + " executor transition is ready for a future stage start.";
            }

            return new OpenACAIBepInExPluginInvocationExecutorTransitionSnapshot(
                stageAttempt.SequenceNumber,
                stageAttempt.StageName,
                stageAttempt.StageKind,
                "pending",
                toState,
                action,
                gateOpen,
                gateBlocked,
                false,
                false,
                true,
                false,
                stageAttempt.IsolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static IReadOnlyList<OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot> CreateInvocationExecutorTransitionRequests(
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan,
            IReadOnlyList<OpenACAIBepInExPluginInvocationStageAttemptSnapshot> stageAttempts,
            string requestedStageKind,
            string requestSource,
            string requestId,
            bool includeAssemblyLoadResolverPlan,
            bool includeAssemblyLoadOperation,
            bool includeAssemblyLoadContextPlan,
            bool includeAssemblyLoadContextAllocation,
            bool includeAssemblyLoadContextRegistration,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation)
        {
            var normalizedStageKind = NormalizeId(requestedStageKind);
            if (string.IsNullOrWhiteSpace(normalizedStageKind))
            {
                return Array.Empty<OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot>();
            }

            var stageAttempt = stageAttempts == null
                ? null
                : stageAttempts.FirstOrDefault(stage => stage != null && string.Equals(stage.StageKind, normalizedStageKind, StringComparison.OrdinalIgnoreCase));
            return new[]
            {
                CreateInvocationExecutorTransitionRequest(
                    plan,
                    stageAttempt,
                    normalizedStageKind,
                    string.IsNullOrWhiteSpace(requestSource) ? LoadPlanSourceUnknown : requestSource,
                    string.IsNullOrWhiteSpace(requestId) ? "bepinex-transition-request-" + DateTimeOffset.UtcNow.UtcTicks : requestId,
                    includeAssemblyLoadResolverPlan,
                    includeAssemblyLoadOperation,
                    includeAssemblyLoadContextPlan,
                    includeAssemblyLoadContextAllocation,
                    includeAssemblyLoadContextRegistration,
                    includeAssemblyLoadInvocation,
                    includePluginTypeResolution,
                    includePluginInstanceCreation,
                    includePluginLoadInvocation)
            };
        }

        private static OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot CreateInvocationExecutorTransitionRequest(
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan,
            OpenACAIBepInExPluginInvocationStageAttemptSnapshot stageAttempt,
            string requestedStageKind,
            string requestSource,
            string requestId,
            bool includeAssemblyLoadResolverPlan,
            bool includeAssemblyLoadOperation,
            bool includeAssemblyLoadContextPlan,
            bool includeAssemblyLoadContextAllocation,
            bool includeAssemblyLoadContextRegistration,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation)
        {
            var normalizedStageKind = NormalizeId(requestedStageKind);
            var requested = !string.IsNullOrWhiteSpace(normalizedStageKind);
            var stageName = stageAttempt == null
                ? string.Equals(normalizedStageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase) ? "Assembly Load" : normalizedStageKind
                : stageAttempt.StageName;
            var stageKind = stageAttempt == null ? normalizedStageKind : stageAttempt.StageKind;
            var stageMatched = stageAttempt != null
                && string.Equals(stageAttempt.StageKind, normalizedStageKind, StringComparison.OrdinalIgnoreCase);
            var stageAvailable = stageAttempt != null
                && stageAttempt.Required
                && stageAttempt.AttemptRecorded;
            var assemblyLoadRequest = string.Equals(normalizedStageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase);
            var accepted = requested && stageMatched && stageAvailable && assemblyLoadRequest;
            var activationPolicy = ActivationPolicyForPlan(plan);
            var activationPolicyDetail = AssemblyActivationPolicyDetail(activationPolicy);
            var gateOpen = accepted
                && RuntimePluginLoadingEnabled
                && InvocationStageExecutorEnabled
                && InvocationAssemblyLoadTransitionRequestEnabled
                && stageAttempt.GateOpen;
            var gateBlocked = accepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "transition-request-not-ready";
            var action = "reject";
            var detail = "OpenACAI BepInEx executor transition request is not ready.";

            if (!requested)
            {
                reasonCode = "transition-request-stage-missing";
                detail = "OpenACAI BepInEx executor transition request did not name a stage.";
            }
            else if (stageAttempt == null || !stageMatched)
            {
                reasonCode = "transition-request-stage-not-found";
                detail = "OpenACAI BepInEx executor transition request could not find stage " + normalizedStageKind + ".";
            }
            else if (!stageAvailable)
            {
                reasonCode = "transition-request-stage-unavailable";
                detail = stageName + " transition request was rejected because no required stage attempt was available.";
            }
            else if (!assemblyLoadRequest)
            {
                reasonCode = "transition-request-stage-unsupported";
                detail = stageName + " transition request was rejected because only assembly-load requests are modeled in this slice.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                action = "block-before-assembly-load";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else
                {
                    reasonCode = "assembly-load-stage-gate-blocked";
                }

                detail = "OpenACAI accepted an assembly-load executor transition request, then blocked it before assembly load."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                action = "ready-for-assembly-load";
                detail = "OpenACAI accepted an assembly-load executor transition request and it is ready for a future isolated assembly load."
                    + activationPolicyDetail;
            }

            var assemblyLoadResolverPlan = CreateAssemblyLoadResolverPlan(
                plan,
                stageAttempt,
                requested,
                accepted,
                includeAssemblyLoadResolverPlan,
                includeAssemblyLoadOperation,
                includeAssemblyLoadContextPlan,
                includeAssemblyLoadContextAllocation,
                includeAssemblyLoadContextRegistration,
                includeAssemblyLoadInvocation,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation);

            return new OpenACAIBepInExPluginInvocationExecutorTransitionRequestSnapshot(
                requestId,
                stageAttempt == null ? 0 : stageAttempt.SequenceNumber,
                stageName,
                stageKind,
                normalizedStageKind,
                requestSource,
                action,
                requested,
                accepted,
                stageMatched,
                stageAvailable,
                InvocationAssemblyLoadTransitionRequestEnabled,
                InvocationStageExecutorEnabled,
                RuntimePluginLoadingEnabled,
                gateOpen,
                gateBlocked,
                false,
                false,
                false,
                assemblyLoadResolverPlan,
                true,
                false,
                stageAttempt == null ? InvocationBoundaryMetadataGate : stageAttempt.IsolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot CreateAssemblyLoadResolverPlan(
            OpenACAIBepInExPluginCompatibilityPlanSnapshot plan,
            OpenACAIBepInExPluginInvocationStageAttemptSnapshot stageAttempt,
            bool requested,
            bool transitionAccepted,
            bool includeAssemblyLoadResolverPlan,
            bool includeAssemblyLoadOperation,
            bool includeAssemblyLoadContextPlan,
            bool includeAssemblyLoadContextAllocation,
            bool includeAssemblyLoadContextRegistration,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation)
        {
            if (!includeAssemblyLoadResolverPlan)
            {
                return OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot.CreateUnavailable(
                    "assembly-load-resolver-plan-not-requested",
                    "No OpenACAI assembly-load resolver plan was requested for this transition request.");
            }

            var candidate = plan == null ? null : plan.Candidate;
            var pluginId = plan == null ? "" : plan.PluginId;
            var assemblyPath = candidate == null ? "" : candidate.AssemblyPath;
            if (string.IsNullOrWhiteSpace(assemblyPath) && plan != null && plan.InvocationPreflight != null)
            {
                assemblyPath = plan.InvocationPreflight.AssemblyPath;
            }

            var assemblyName = candidate == null ? "" : candidate.AssemblyName;
            var pluginTypeName = candidate == null ? "" : candidate.PluginTypeName;
            if (string.IsNullOrWhiteSpace(pluginTypeName) && plan != null && plan.InvocationPreflight != null)
            {
                pluginTypeName = plan.InvocationPreflight.PluginTypeName;
            }

            var assemblyDirectory = "";
            var assemblyExists = false;
            var assemblyFileSize = 0L;
            if (!string.IsNullOrWhiteSpace(assemblyPath))
            {
                try
                {
                    assemblyPath = Path.GetFullPath(assemblyPath);
                    assemblyDirectory = Path.GetDirectoryName(assemblyPath) ?? "";
                    assemblyExists = File.Exists(assemblyPath);
                    if (assemblyExists)
                    {
                        assemblyFileSize = new FileInfo(assemblyPath).Length;
                    }
                }
                catch
                {
                    assemblyPath = assemblyPath ?? "";
                    assemblyDirectory = "";
                    assemblyExists = false;
                    assemblyFileSize = 0;
                }
            }

            if (string.IsNullOrWhiteSpace(assemblyName) && !string.IsNullOrWhiteSpace(assemblyPath))
            {
                assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            }

            var dependencyPlans = new List<OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot>();
            if (candidate != null && candidate.AssemblyReferences != null)
            {
                foreach (var reference in candidate.AssemblyReferences)
                {
                    if (dependencyPlans.Count >= MaxAssemblyReferenceCount)
                    {
                        break;
                    }

                    dependencyPlans.Add(CreateAssemblyLoadResolverDependencyPlan(
                        dependencyPlans.Count + 1,
                        reference,
                        assemblyDirectory));
                }
            }

            var dependenciesResolved = dependencyPlans.All(dependency => dependency != null && dependency.Resolved);
            var activationPolicy = ActivationPolicyForPlan(plan);
            var activationPolicyDetail = AssemblyActivationPolicyDetail(activationPolicy);
            var planAccepted = requested
                && transitionAccepted
                && plan != null
                && plan.Accepted
                && assemblyExists
                && dependenciesResolved;
            var gateOpen = planAccepted
                && RuntimePluginLoadingEnabled
                && InvocationStageExecutorEnabled
                && InvocationAssemblyLoadTransitionRequestEnabled
                && InvocationAssemblyLoadResolverPlanEnabled;
            var gateBlocked = planAccepted && !gateOpen;
            var assemblyAlreadyLoaded = !string.IsNullOrWhiteSpace(assemblyName) && FindLoadedAssembly(assemblyName) != null;
            var status = "CHECK";
            var reasonCode = "assembly-load-resolver-plan-not-ready";
            var detail = "OpenACAI assembly-load resolver plan is not ready.";

            if (!requested)
            {
                reasonCode = "assembly-load-resolver-plan-not-requested";
                detail = "OpenACAI assembly-load resolver plan was not requested.";
            }
            else if (!transitionAccepted)
            {
                reasonCode = "assembly-load-transition-request-not-accepted";
                detail = "OpenACAI assembly-load resolver plan was rejected because the assembly-load transition request was not accepted.";
            }
            else if (stageAttempt == null)
            {
                reasonCode = "assembly-load-stage-unavailable";
                detail = "OpenACAI assembly-load resolver plan could not find a recorded assembly-load stage attempt.";
            }
            else if (plan == null || !plan.Accepted)
            {
                reasonCode = "load-plan-not-accepted";
                detail = "OpenACAI assembly-load resolver plan requires an accepted BepInEx compatibility load plan.";
            }
            else if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                reasonCode = "assembly-load-path-missing";
                detail = "OpenACAI assembly-load resolver plan requires a candidate assembly path.";
            }
            else if (!assemblyExists)
            {
                reasonCode = "assembly-load-assembly-missing";
                detail = "OpenACAI assembly-load resolver plan could not find the candidate assembly on disk.";
            }
            else if (!dependenciesResolved)
            {
                reasonCode = "assembly-load-dependencies-unresolved";
                detail = "OpenACAI assembly-load resolver plan found unresolved candidate assembly references.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else
                {
                    reasonCode = "assembly-load-resolver-gate-blocked";
                }

                detail = "OpenACAI planned the assembly path, metadata context, and dependencies, then blocked before any assembly load."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI assembly-load resolver plan is ready for a future isolated assembly load."
                    + activationPolicyDetail;
            }

            var assemblyLoadOperation = CreateAssemblyLoadOperation(
                includeAssemblyLoadOperation,
                requested,
                planAccepted,
                assemblyAlreadyLoaded,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                assemblyExists,
                assemblyFileSize,
                dependencyPlans.Count,
                dependencyPlans.Count(dependency => dependency != null && dependency.Resolved),
                dependencyPlans.Count(dependency => dependency != null && !dependency.Resolved),
                stageAttempt == null ? InvocationBoundaryMetadataGate : stageAttempt.IsolationBoundary,
                includeAssemblyLoadContextPlan,
                includeAssemblyLoadContextAllocation,
                includeAssemblyLoadContextRegistration,
                includeAssemblyLoadInvocation,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation,
                pluginTypeName,
                activationPolicyDetail);

            return new OpenACAIBepInExPluginAssemblyLoadResolverPlanSnapshot(
                true,
                requested,
                planAccepted,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                false,
                false,
                true,
                false,
                InvocationAssemblyLoadResolverPlanContextName,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                assemblyExists,
                assemblyFileSize,
                OpenACAIAssemblyResolver.SearchDirectoriesSnapshot().Length,
                dependencyPlans,
                assemblyLoadOperation,
                stageAttempt == null ? InvocationBoundaryMetadataGate : stageAttempt.IsolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginAssemblyLoadOperationSnapshot CreateAssemblyLoadOperation(
            bool includeAssemblyLoadOperation,
            bool requested,
            bool resolverPlanAccepted,
            bool assemblyAlreadyLoaded,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            bool assemblyExists,
            long assemblyFileSize,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            string isolationBoundary,
            bool includeAssemblyLoadContextPlan,
            bool includeAssemblyLoadContextAllocation,
            bool includeAssemblyLoadContextRegistration,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation,
            string pluginTypeName,
            string activationPolicyDetail)
        {
            if (!includeAssemblyLoadOperation)
            {
                return OpenACAIBepInExPluginAssemblyLoadOperationSnapshot.CreateUnavailable(
                    "assembly-load-operation-not-requested",
                    "No OpenACAI assembly-load operation was requested for this resolver plan.");
            }

            var operationAccepted = requested
                && resolverPlanAccepted
                && assemblyExists
                && !assemblyAlreadyLoaded
                && unresolvedAssemblyReferenceCount == 0;
            var gateOpen = operationAccepted
                && RuntimePluginLoadingEnabled
                && InvocationStageExecutorEnabled
                && InvocationAssemblyLoadTransitionRequestEnabled
                && InvocationAssemblyLoadResolverPlanEnabled
                && InvocationAssemblyLoadOperationEnabled;
            var gateBlocked = operationAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "assembly-load-operation-not-ready";
            var detail = "OpenACAI assembly-load operation is not ready.";
            var contextIdentity = InvocationAssemblyLoadResolverPlanContextName
                + ":"
                + (string.IsNullOrWhiteSpace(pluginId) ? NormalizeId(assemblyName) : NormalizeId(pluginId));
            var contextPlan = CreateAssemblyLoadContextPlan(
                includeAssemblyLoadContextPlan,
                requested,
                operationAccepted,
                gateOpen,
                contextIdentity,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                isolationBoundary,
                includeAssemblyLoadContextAllocation,
                includeAssemblyLoadContextRegistration,
                includeAssemblyLoadInvocation,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation,
                pluginTypeName,
                activationPolicyDetail);

            if (!requested)
            {
                reasonCode = "assembly-load-operation-not-requested";
                detail = "OpenACAI assembly-load operation was not requested.";
            }
            else if (!resolverPlanAccepted)
            {
                reasonCode = "assembly-load-resolver-plan-not-accepted";
                detail = "OpenACAI assembly-load operation requires an accepted resolver plan.";
            }
            else if (!assemblyExists)
            {
                reasonCode = "assembly-load-assembly-missing";
                detail = "OpenACAI assembly-load operation could not find the candidate assembly on disk.";
            }
            else if (assemblyAlreadyLoaded)
            {
                reasonCode = "assembly-already-loaded";
                detail = "OpenACAI assembly-load operation was rejected because the candidate assembly is already loaded.";
            }
            else if (unresolvedAssemblyReferenceCount > 0)
            {
                reasonCode = "assembly-load-dependencies-unresolved";
                detail = "OpenACAI assembly-load operation requires all resolver-plan dependencies to resolve first.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else
                {
                    reasonCode = "assembly-load-operation-gate-blocked";
                }

                detail = "OpenACAI recorded the would-call assembly-load operation, then blocked before context creation or LoadFromAssemblyPath."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI assembly-load operation is ready for a future isolated LoadFromAssemblyPath call."
                    + activationPolicyDetail;
            }

            return new OpenACAIBepInExPluginAssemblyLoadOperationSnapshot(
                true,
                requested,
                operationAccepted,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                false,
                false,
                false,
                false,
                assemblyAlreadyLoaded,
                true,
                false,
                InvocationAssemblyLoadOperationKind,
                InvocationAssemblyLoadOperationMethodName,
                InvocationAssemblyLoadResolverPlanContextName,
                contextIdentity,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                assemblyExists,
                assemblyFileSize,
                assemblyReferenceCount,
                resolvedAssemblyReferenceCount,
                unresolvedAssemblyReferenceCount,
                contextPlan,
                InvocationAssemblyLoadOperationRollbackStrategy,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot CreateAssemblyLoadContextPlan(
            bool includeAssemblyLoadContextPlan,
            bool requested,
            bool operationAccepted,
            bool operationGateOpen,
            string contextIdentity,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string isolationBoundary,
            bool includeAssemblyLoadContextAllocation,
            bool includeAssemblyLoadContextRegistration,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation,
            string pluginTypeName,
            string activationPolicyDetail)
        {
            if (!includeAssemblyLoadContextPlan)
            {
                return OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot.CreateUnavailable(
                    "assembly-load-context-plan-not-requested",
                    "No OpenACAI assembly-load context allocation plan was requested for this operation.");
            }

            var planAccepted = requested && operationAccepted;
            var gateOpen = planAccepted && operationGateOpen && InvocationAssemblyLoadContextPlanEnabled;
            var gateBlocked = planAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "assembly-load-context-plan-not-ready";
            var detail = "OpenACAI assembly-load context allocation plan is not ready.";

            if (!requested)
            {
                reasonCode = "assembly-load-context-plan-not-requested";
                detail = "OpenACAI assembly-load context allocation plan was not requested.";
            }
            else if (!operationAccepted)
            {
                reasonCode = "assembly-load-operation-not-accepted";
                detail = "OpenACAI assembly-load context allocation plan requires an accepted assembly-load operation.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else if (!InvocationAssemblyLoadContextPlanEnabled)
                {
                    reasonCode = "assembly-load-context-plan-disabled";
                }
                else
                {
                    reasonCode = "assembly-load-context-plan-gate-blocked";
                }

                detail = "OpenACAI planned a dedicated collectible AssemblyLoadContext, then blocked before context allocation."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI assembly-load context allocation plan is ready for a future isolated AssemblyLoadContext allocation."
                    + activationPolicyDetail;
            }

            var contextAllocation = CreateAssemblyLoadContextAllocation(
                includeAssemblyLoadContextAllocation,
                requested,
                planAccepted,
                gateOpen,
                contextIdentity,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                isolationBoundary,
                includeAssemblyLoadContextRegistration,
                includeAssemblyLoadInvocation,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation,
                pluginTypeName,
                activationPolicyDetail);

            return new OpenACAIBepInExPluginAssemblyLoadContextPlanSnapshot(
                true,
                requested,
                planAccepted,
                InvocationAssemblyLoadContextPlanEnabled,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                planAccepted,
                false,
                true,
                true,
                true,
                true,
                false,
                InvocationAssemblyLoadContextAllocationStrategy,
                InvocationAssemblyLoadResolverPlanContextName,
                contextIdentity,
                InvocationAssemblyLoadOperationKind,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                contextAllocation,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot CreateAssemblyLoadContextAllocation(
            bool includeAssemblyLoadContextAllocation,
            bool requested,
            bool contextPlanAccepted,
            bool contextPlanGateOpen,
            string contextIdentity,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string isolationBoundary,
            bool includeAssemblyLoadContextRegistration,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation,
            string pluginTypeName,
            string activationPolicyDetail)
        {
            if (!includeAssemblyLoadContextAllocation)
            {
                return OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot.CreateUnavailable(
                    "assembly-load-context-allocation-not-requested",
                    "No OpenACAI assembly-load context allocation attempt was requested for this context plan.");
            }

            var allocationAccepted = requested && contextPlanAccepted;
            var gateOpen = allocationAccepted
                && contextPlanGateOpen
                && InvocationAssemblyLoadContextAllocationEnabled;
            var gateBlocked = allocationAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "assembly-load-context-allocation-not-ready";
            var detail = "OpenACAI assembly-load context allocation attempt is not ready.";

            if (!requested)
            {
                reasonCode = "assembly-load-context-allocation-not-requested";
                detail = "OpenACAI assembly-load context allocation attempt was not requested.";
            }
            else if (!contextPlanAccepted)
            {
                reasonCode = "assembly-load-context-plan-not-accepted";
                detail = "OpenACAI assembly-load context allocation attempt requires an accepted context allocation plan.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else if (!InvocationAssemblyLoadContextPlanEnabled)
                {
                    reasonCode = "assembly-load-context-plan-disabled";
                }
                else if (!InvocationAssemblyLoadContextAllocationEnabled)
                {
                    reasonCode = "assembly-load-context-allocation-disabled";
                }
                else
                {
                    reasonCode = "assembly-load-context-allocation-gate-blocked";
                }

                detail = "OpenACAI recorded the would-call AssemblyLoadContext constructor boundary, then blocked before constructor invocation."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI assembly-load context allocation attempt is ready for a future isolated AssemblyLoadContext constructor call."
                    + activationPolicyDetail;
            }

            var contextRegistration = CreateAssemblyLoadContextRegistration(
                includeAssemblyLoadContextRegistration,
                requested,
                allocationAccepted,
                gateOpen,
                contextIdentity,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                isolationBoundary,
                includeAssemblyLoadInvocation,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation,
                pluginTypeName,
                activationPolicyDetail);

            return new OpenACAIBepInExPluginAssemblyLoadContextAllocationSnapshot(
                true,
                requested,
                allocationAccepted,
                InvocationAssemblyLoadContextAllocationEnabled,
                InvocationAssemblyLoadContextPlanEnabled,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                allocationAccepted,
                false,
                false,
                false,
                false,
                true,
                true,
                true,
                true,
                false,
                InvocationAssemblyLoadContextAllocationStrategy,
                InvocationAssemblyLoadContextConstructorSignature,
                InvocationAssemblyLoadResolverPlanContextName,
                contextIdentity,
                InvocationAssemblyLoadOperationKind,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                contextRegistration,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot CreateAssemblyLoadContextRegistration(
            bool includeAssemblyLoadContextRegistration,
            bool requested,
            bool allocationAccepted,
            bool allocationGateOpen,
            string contextIdentity,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string isolationBoundary,
            bool includeAssemblyLoadInvocation,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation,
            string pluginTypeName,
            string activationPolicyDetail)
        {
            if (!includeAssemblyLoadContextRegistration)
            {
                return OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot.CreateUnavailable(
                    "assembly-load-context-registration-not-requested",
                    "No OpenACAI assembly-load context registration boundary was requested for this allocation attempt.");
            }

            var registrationAccepted = requested && allocationAccepted;
            var gateOpen = registrationAccepted
                && allocationGateOpen
                && InvocationAssemblyLoadContextRegistrationEnabled;
            var gateBlocked = registrationAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "assembly-load-context-registration-not-ready";
            var detail = "OpenACAI assembly-load context registration boundary is not ready.";

            if (!requested)
            {
                reasonCode = "assembly-load-context-registration-not-requested";
                detail = "OpenACAI assembly-load context registration boundary was not requested.";
            }
            else if (!allocationAccepted)
            {
                reasonCode = "assembly-load-context-allocation-not-accepted";
                detail = "OpenACAI assembly-load context registration boundary requires an accepted context allocation attempt.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else if (!InvocationAssemblyLoadContextPlanEnabled)
                {
                    reasonCode = "assembly-load-context-plan-disabled";
                }
                else if (!InvocationAssemblyLoadContextAllocationEnabled)
                {
                    reasonCode = "assembly-load-context-allocation-disabled";
                }
                else if (!InvocationAssemblyLoadContextRegistrationEnabled)
                {
                    reasonCode = "assembly-load-context-registration-disabled";
                }
                else
                {
                    reasonCode = "assembly-load-context-registration-gate-blocked";
                }

                detail = "OpenACAI recorded the would-register AssemblyLoadContext tracking boundary, then blocked before constructor result tracking or unload registration."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI assembly-load context registration boundary is ready for future isolated context tracking."
                    + activationPolicyDetail;
            }

            var assemblyLoadInvocation = CreateAssemblyLoadInvocation(
                includeAssemblyLoadInvocation,
                requested,
                registrationAccepted,
                gateOpen,
                contextIdentity,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                isolationBoundary,
                includePluginTypeResolution,
                includePluginInstanceCreation,
                includePluginLoadInvocation,
                pluginTypeName,
                activationPolicyDetail);

            return new OpenACAIBepInExPluginAssemblyLoadContextRegistrationSnapshot(
                true,
                requested,
                registrationAccepted,
                InvocationAssemblyLoadContextRegistrationEnabled,
                InvocationAssemblyLoadContextAllocationEnabled,
                InvocationAssemblyLoadContextPlanEnabled,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                registrationAccepted,
                false,
                false,
                false,
                false,
                false,
                registrationAccepted,
                false,
                false,
                true,
                false,
                InvocationAssemblyLoadContextRegistrationStrategy,
                InvocationAssemblyLoadContextTrackingCollection,
                InvocationAssemblyLoadResolverPlanContextName,
                contextIdentity,
                InvocationAssemblyLoadOperationKind,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                assemblyLoadInvocation,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot CreateAssemblyLoadInvocation(
            bool includeAssemblyLoadInvocation,
            bool requested,
            bool registrationAccepted,
            bool registrationGateOpen,
            string contextIdentity,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string isolationBoundary,
            bool includePluginTypeResolution,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation,
            string pluginTypeName,
            string activationPolicyDetail)
        {
            if (!includeAssemblyLoadInvocation)
            {
                return OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot.CreateUnavailable(
                    "assembly-load-invocation-not-requested",
                    "No OpenACAI assembly-load invocation boundary was requested for this context registration.");
            }

            var invocationAccepted = requested && registrationAccepted;
            var gateOpen = invocationAccepted
                && registrationGateOpen
                && InvocationAssemblyLoadInvocationEnabled;
            var gateBlocked = invocationAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "assembly-load-invocation-not-ready";
            var detail = "OpenACAI assembly-load invocation boundary is not ready.";

            if (!requested)
            {
                reasonCode = "assembly-load-invocation-not-requested";
                detail = "OpenACAI assembly-load invocation boundary was not requested.";
            }
            else if (!registrationAccepted)
            {
                reasonCode = "assembly-load-context-registration-not-accepted";
                detail = "OpenACAI assembly-load invocation boundary requires an accepted context registration boundary.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else if (!InvocationAssemblyLoadContextPlanEnabled)
                {
                    reasonCode = "assembly-load-context-plan-disabled";
                }
                else if (!InvocationAssemblyLoadContextAllocationEnabled)
                {
                    reasonCode = "assembly-load-context-allocation-disabled";
                }
                else if (!InvocationAssemblyLoadContextRegistrationEnabled)
                {
                    reasonCode = "assembly-load-context-registration-disabled";
                }
                else if (!InvocationAssemblyLoadInvocationEnabled)
                {
                    reasonCode = "assembly-load-invocation-disabled";
                }
                else
                {
                    reasonCode = "assembly-load-invocation-gate-blocked";
                }

                detail = "OpenACAI recorded the would-call LoadFromAssemblyPath invocation boundary, then blocked before invoking the assembly-load method or capturing an Assembly result."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI assembly-load invocation boundary is ready for a future isolated LoadFromAssemblyPath call."
                    + activationPolicyDetail;
            }

            var pluginTypeResolution = CreatePluginTypeResolution(
                includePluginTypeResolution,
                requested,
                invocationAccepted,
                gateOpen,
                false,
                false,
                false,
                pluginTypeName,
                pluginId,
                assemblyName,
                assemblyPath,
                "",
                "",
                includePluginInstanceCreation,
                includePluginLoadInvocation,
                isolationBoundary,
                activationPolicyDetail);

            return new OpenACAIBepInExPluginAssemblyLoadInvocationSnapshot(
                true,
                requested,
                invocationAccepted,
                InvocationAssemblyLoadInvocationEnabled,
                InvocationAssemblyLoadContextRegistrationEnabled,
                InvocationAssemblyLoadContextAllocationEnabled,
                InvocationAssemblyLoadContextPlanEnabled,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                invocationAccepted,
                false,
                false,
                invocationAccepted,
                false,
                false,
                false,
                false,
                true,
                false,
                InvocationAssemblyLoadInvocationStrategy,
                InvocationAssemblyLoadOperationMethodName,
                InvocationAssemblyLoadResolverPlanContextName,
                contextIdentity,
                InvocationAssemblyLoadOperationKind,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                "",
                "",
                pluginTypeResolution,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginTypeResolutionSnapshot CreatePluginTypeResolution(
            bool includePluginTypeResolution,
            bool requested,
            bool invocationAccepted,
            bool invocationGateOpen,
            bool invocationCompleted,
            bool assemblyLoaded,
            bool loadResultAvailable,
            string pluginTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            bool includePluginInstanceCreation,
            bool includePluginLoadInvocation,
            string isolationBoundary,
            string activationPolicyDetail)
        {
            if (!includePluginTypeResolution)
            {
                return OpenACAIBepInExPluginTypeResolutionSnapshot.CreateUnavailable(
                    "plugin-type-resolution-not-requested",
                    "No OpenACAI plugin type resolution boundary was requested for this assembly-load invocation.");
            }

            var resolutionAccepted = requested && invocationAccepted;
            var assemblyResultAvailable = resolutionAccepted && invocationCompleted && assemblyLoaded && loadResultAvailable;
            var gateOpen = resolutionAccepted
                && invocationGateOpen
                && assemblyResultAvailable
                && InvocationPluginTypeResolutionEnabled;
            var gateBlocked = resolutionAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "plugin-type-resolution-not-ready";
            var detail = "OpenACAI plugin type resolution boundary is not ready.";

            if (!requested)
            {
                reasonCode = "plugin-type-resolution-not-requested";
                detail = "OpenACAI plugin type resolution boundary was not requested.";
            }
            else if (!invocationAccepted)
            {
                reasonCode = "assembly-load-invocation-not-accepted";
                detail = "OpenACAI plugin type resolution boundary requires an accepted assembly-load invocation boundary.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else if (!InvocationAssemblyLoadContextPlanEnabled)
                {
                    reasonCode = "assembly-load-context-plan-disabled";
                }
                else if (!InvocationAssemblyLoadContextAllocationEnabled)
                {
                    reasonCode = "assembly-load-context-allocation-disabled";
                }
                else if (!InvocationAssemblyLoadContextRegistrationEnabled)
                {
                    reasonCode = "assembly-load-context-registration-disabled";
                }
                else if (!InvocationAssemblyLoadInvocationEnabled)
                {
                    reasonCode = "assembly-load-invocation-disabled";
                }
                else if (!assemblyResultAvailable)
                {
                    reasonCode = "assembly-load-result-unavailable";
                }
                else if (!InvocationPluginTypeResolutionEnabled)
                {
                    reasonCode = "plugin-type-resolution-disabled";
                }
                else
                {
                    reasonCode = "plugin-type-resolution-gate-blocked";
                }

                detail = "OpenACAI recorded the would-resolve plugin Type boundary, then blocked before Assembly.GetType or Type result capture."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI plugin type resolution boundary is ready for a future isolated Assembly.GetType call."
                    + activationPolicyDetail;
            }

            var pluginInstanceCreation = CreatePluginInstanceCreation(
                includePluginInstanceCreation,
                requested,
                resolutionAccepted,
                gateOpen,
                false,
                false,
                false,
                pluginTypeName,
                "",
                pluginId,
                assemblyName,
                assemblyPath,
                loadedAssemblyName,
                loadedAssemblyPath,
                includePluginLoadInvocation,
                isolationBoundary,
                activationPolicyDetail);

            return new OpenACAIBepInExPluginTypeResolutionSnapshot(
                true,
                requested,
                resolutionAccepted,
                InvocationPluginTypeResolutionEnabled,
                InvocationAssemblyLoadInvocationEnabled,
                InvocationAssemblyLoadContextRegistrationEnabled,
                InvocationAssemblyLoadContextAllocationEnabled,
                InvocationAssemblyLoadContextPlanEnabled,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                resolutionAccepted,
                assemblyResultAvailable,
                assemblyLoaded,
                loadResultAvailable,
                invocationCompleted,
                resolutionAccepted,
                false,
                false,
                false,
                false,
                true,
                false,
                InvocationPluginTypeResolutionStrategy,
                InvocationPluginTypeResolutionMethodName,
                pluginTypeName,
                "",
                pluginId,
                assemblyName,
                assemblyPath,
                loadedAssemblyName,
                loadedAssemblyPath,
                pluginInstanceCreation,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginInstanceCreationSnapshot CreatePluginInstanceCreation(
            bool includePluginInstanceCreation,
            bool requested,
            bool typeResolutionAccepted,
            bool typeResolutionGateOpen,
            bool resolutionCompleted,
            bool pluginTypeResolved,
            bool typeResultAvailable,
            string pluginTypeName,
            string resolvedTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            bool includePluginLoadInvocation,
            string isolationBoundary,
            string activationPolicyDetail)
        {
            if (!includePluginInstanceCreation)
            {
                return OpenACAIBepInExPluginInstanceCreationSnapshot.CreateUnavailable(
                    "plugin-instance-creation-not-requested",
                    "No OpenACAI plugin instance creation boundary was requested for this plugin type resolution.");
            }

            var creationAccepted = requested && typeResolutionAccepted;
            var typeResultReady = creationAccepted && resolutionCompleted && pluginTypeResolved && typeResultAvailable;
            var gateOpen = creationAccepted
                && typeResolutionGateOpen
                && typeResultReady
                && InvocationPluginInstanceCreationEnabled;
            var gateBlocked = creationAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "plugin-instance-creation-not-ready";
            var detail = "OpenACAI plugin instance creation boundary is not ready.";

            if (!requested)
            {
                reasonCode = "plugin-instance-creation-not-requested";
                detail = "OpenACAI plugin instance creation boundary was not requested.";
            }
            else if (!typeResolutionAccepted)
            {
                reasonCode = "plugin-type-resolution-not-accepted";
                detail = "OpenACAI plugin instance creation boundary requires an accepted plugin type resolution boundary.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else if (!InvocationAssemblyLoadContextPlanEnabled)
                {
                    reasonCode = "assembly-load-context-plan-disabled";
                }
                else if (!InvocationAssemblyLoadContextAllocationEnabled)
                {
                    reasonCode = "assembly-load-context-allocation-disabled";
                }
                else if (!InvocationAssemblyLoadContextRegistrationEnabled)
                {
                    reasonCode = "assembly-load-context-registration-disabled";
                }
                else if (!InvocationAssemblyLoadInvocationEnabled)
                {
                    reasonCode = "assembly-load-invocation-disabled";
                }
                else if (!InvocationPluginTypeResolutionEnabled)
                {
                    reasonCode = "plugin-type-resolution-disabled";
                }
                else if (!typeResultReady)
                {
                    reasonCode = "plugin-type-result-unavailable";
                }
                else if (!InvocationPluginInstanceCreationEnabled)
                {
                    reasonCode = "plugin-instance-creation-disabled";
                }
                else
                {
                    reasonCode = "plugin-instance-creation-gate-blocked";
                }

                detail = "OpenACAI recorded the would-create plugin instance boundary, then blocked before Activator.CreateInstance or plugin instance capture."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI plugin instance creation boundary is ready for a future isolated Activator.CreateInstance call."
                    + activationPolicyDetail;
            }

            var pluginLoadInvocation = CreatePluginLoadInvocation(
                includePluginLoadInvocation,
                requested,
                creationAccepted,
                gateOpen,
                false,
                false,
                false,
                pluginTypeName,
                resolvedTypeName,
                "",
                pluginId,
                assemblyName,
                assemblyPath,
                loadedAssemblyName,
                loadedAssemblyPath,
                isolationBoundary,
                activationPolicyDetail);

            return new OpenACAIBepInExPluginInstanceCreationSnapshot(
                true,
                requested,
                creationAccepted,
                InvocationPluginInstanceCreationEnabled,
                InvocationPluginTypeResolutionEnabled,
                InvocationAssemblyLoadInvocationEnabled,
                InvocationAssemblyLoadContextRegistrationEnabled,
                InvocationAssemblyLoadContextAllocationEnabled,
                InvocationAssemblyLoadContextPlanEnabled,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                creationAccepted,
                typeResultReady,
                pluginTypeResolved,
                resolutionCompleted,
                creationAccepted,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                InvocationPluginInstanceCreationStrategy,
                InvocationPluginInstanceCreationMethodName,
                pluginTypeName,
                resolvedTypeName,
                "",
                pluginId,
                assemblyName,
                assemblyPath,
                loadedAssemblyName,
                loadedAssemblyPath,
                pluginLoadInvocation,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginLoadInvocationSnapshot CreatePluginLoadInvocation(
            bool includePluginLoadInvocation,
            bool requested,
            bool instanceCreationAccepted,
            bool instanceCreationGateOpen,
            bool instanceCreationCompleted,
            bool pluginInstanceCreated,
            bool instanceResultAvailable,
            string pluginTypeName,
            string resolvedTypeName,
            string instanceTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            string isolationBoundary,
            string activationPolicyDetail)
        {
            if (!includePluginLoadInvocation)
            {
                return OpenACAIBepInExPluginLoadInvocationSnapshot.CreateUnavailable(
                    "plugin-load-invocation-not-requested",
                    "No OpenACAI BasePlugin.Load invocation boundary was requested for this plugin instance creation.");
            }

            var invocationAccepted = requested && instanceCreationAccepted;
            var pluginInstanceReady = invocationAccepted
                && instanceCreationCompleted
                && pluginInstanceCreated
                && instanceResultAvailable;
            var gateOpen = invocationAccepted
                && instanceCreationGateOpen
                && pluginInstanceReady
                && InvocationPluginLoadInvocationEnabled;
            var gateBlocked = invocationAccepted && !gateOpen;
            var status = "CHECK";
            var reasonCode = "plugin-load-invocation-not-ready";
            var detail = "OpenACAI BasePlugin.Load invocation boundary is not ready.";

            if (!requested)
            {
                reasonCode = "plugin-load-invocation-not-requested";
                detail = "OpenACAI BasePlugin.Load invocation boundary was not requested.";
            }
            else if (!instanceCreationAccepted)
            {
                reasonCode = "plugin-instance-creation-not-accepted";
                detail = "OpenACAI BasePlugin.Load invocation boundary requires an accepted plugin instance creation boundary.";
            }
            else if (gateBlocked)
            {
                status = "DEFERRED";
                if (!RuntimePluginLoadingEnabled)
                {
                    reasonCode = "runtime-plugin-loading-disabled";
                }
                else if (!InvocationStageExecutorEnabled)
                {
                    reasonCode = "invocation-stage-executor-disabled";
                }
                else if (!InvocationAssemblyLoadTransitionRequestEnabled)
                {
                    reasonCode = "assembly-load-transition-request-disabled";
                }
                else if (!InvocationAssemblyLoadResolverPlanEnabled)
                {
                    reasonCode = "assembly-load-resolver-plan-disabled";
                }
                else if (!InvocationAssemblyLoadOperationEnabled)
                {
                    reasonCode = "assembly-load-operation-disabled";
                }
                else if (!InvocationAssemblyLoadContextPlanEnabled)
                {
                    reasonCode = "assembly-load-context-plan-disabled";
                }
                else if (!InvocationAssemblyLoadContextAllocationEnabled)
                {
                    reasonCode = "assembly-load-context-allocation-disabled";
                }
                else if (!InvocationAssemblyLoadContextRegistrationEnabled)
                {
                    reasonCode = "assembly-load-context-registration-disabled";
                }
                else if (!InvocationAssemblyLoadInvocationEnabled)
                {
                    reasonCode = "assembly-load-invocation-disabled";
                }
                else if (!InvocationPluginTypeResolutionEnabled)
                {
                    reasonCode = "plugin-type-resolution-disabled";
                }
                else if (!InvocationPluginInstanceCreationEnabled)
                {
                    reasonCode = "plugin-instance-creation-disabled";
                }
                else if (!pluginInstanceReady)
                {
                    reasonCode = "plugin-instance-result-unavailable";
                }
                else if (!InvocationPluginLoadInvocationEnabled)
                {
                    reasonCode = "plugin-load-invocation-disabled";
                }
                else
                {
                    reasonCode = "plugin-load-invocation-gate-blocked";
                }

                detail = "OpenACAI recorded the would-invoke BasePlugin.Load boundary, then blocked before Load() or plugin lifecycle dispatch."
                    + activationPolicyDetail;
            }
            else if (gateOpen)
            {
                status = "READY";
                reasonCode = "";
                detail = "OpenACAI BasePlugin.Load invocation boundary is ready for a future isolated plugin Load() call."
                    + activationPolicyDetail;
            }

            return new OpenACAIBepInExPluginLoadInvocationSnapshot(
                true,
                requested,
                invocationAccepted,
                InvocationPluginLoadInvocationEnabled,
                InvocationPluginInstanceCreationEnabled,
                InvocationPluginTypeResolutionEnabled,
                InvocationAssemblyLoadInvocationEnabled,
                InvocationAssemblyLoadContextRegistrationEnabled,
                InvocationAssemblyLoadContextAllocationEnabled,
                InvocationAssemblyLoadContextPlanEnabled,
                InvocationAssemblyLoadOperationEnabled,
                InvocationAssemblyLoadResolverPlanEnabled,
                RuntimePluginLoadingEnabled,
                InvocationStageExecutorEnabled,
                InvocationAssemblyLoadTransitionRequestEnabled,
                gateOpen,
                gateBlocked,
                invocationAccepted,
                pluginInstanceReady,
                instanceResultAvailable,
                pluginInstanceCreated,
                instanceCreationCompleted,
                invocationAccepted,
                false,
                false,
                false,
                false,
                true,
                false,
                InvocationPluginLoadInvocationStrategy,
                InvocationPluginLoadInvocationMethodName,
                pluginTypeName,
                resolvedTypeName,
                instanceTypeName,
                pluginId,
                assemblyName,
                assemblyPath,
                loadedAssemblyName,
                loadedAssemblyPath,
                isolationBoundary,
                status,
                reasonCode,
                detail);
        }

        private static OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot CreateAssemblyLoadResolverDependencyPlan(
            int sequenceNumber,
            OpenACAIBepInExPluginAssemblyReferenceSnapshot reference,
            string requestingDirectory)
        {
            var assemblyName = reference == null ? "" : reference.AssemblyName;
            var requiredVersion = reference == null ? "" : reference.RequiredVersion;
            var resolved = false;
            var resolvedSource = "";
            var resolvedVersion = "";
            var resolvedPath = "";
            var status = "CHECK";
            var reasonCode = "assembly-reference-unresolved";
            var detail = "OpenACAI assembly-load resolver plan could not resolve this dependency.";

            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                reasonCode = "assembly-reference-name-missing";
                detail = "Assembly reference name was empty.";
            }
            else
            {
                string resolverPath;
                string resolverSource;
                if (OpenACAIAssemblyResolver.TryFindAssemblyPath(assemblyName, requestingDirectory, out resolverPath, out resolverSource))
                {
                    resolved = true;
                    resolvedSource = string.IsNullOrWhiteSpace(resolverSource) ? "resolver" : resolverSource;
                    resolvedPath = resolverPath ?? "";
                    resolvedVersion = SafeAssemblyVersion(resolvedPath);
                    status = "READY";
                    reasonCode = "";
                    detail = "Assembly reference would resolve through OpenACAI resolver roots.";
                }
                else if (IsFrameworkReference(assemblyName))
                {
                    resolved = true;
                    resolvedSource = "framework-reference";
                    resolvedVersion = "";
                    resolvedPath = "";
                    status = "READY";
                    reasonCode = "";
                    detail = "Assembly reference is supplied by the current .NET runtime.";
                }
                else if (reference != null && reference.Resolved)
                {
                    resolved = true;
                    resolvedSource = string.IsNullOrWhiteSpace(reference.Source) ? "metadata-preflight" : reference.Source;
                    resolvedVersion = reference.ResolvedVersion ?? "";
                    resolvedPath = reference.ResolvedPath ?? "";
                    status = "READY";
                    reasonCode = "";
                    detail = "Assembly reference was already resolved by OpenACAI metadata preflight.";
                }
                else
                {
                    resolvedSource = reference == null ? "unresolved" : reference.Source;
                    detail = reference == null || string.IsNullOrWhiteSpace(reference.Detail)
                        ? detail
                        : reference.Detail;
                }
            }

            return new OpenACAIBepInExPluginAssemblyLoadResolverDependencyPlanSnapshot(
                sequenceNumber,
                assemblyName,
                requiredVersion,
                requestingDirectory,
                resolved,
                resolvedSource,
                resolvedVersion,
                resolvedPath,
                status,
                reasonCode,
                detail);
        }

        private static void RecordLoadAttempt(OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot attempt)
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

        private static OpenACAIBepInExPluginCompatibilityCandidateSnapshot FindCandidate(
            OpenACAIBepInExPluginCompatibilitySnapshot snapshot,
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
                    || (string.Equals(normalized, "OpenACAI Endnight Loader Core", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.PluginGuid, OpenACAIEndnightRuntime.PluginGuid, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(candidate.AssemblyPath, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.PluginTypeName, normalized, StringComparison.Ordinal)))
                .Take(2).ToArray();
            ambiguous = matches.Length > 1;
            return matches.Length == 1 ? matches[0] : null;
        }

        private static OpenACAIBepInExPluginMetadataHarnessSnapshot SelectPositiveParserHarness(
            OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.ParserHarnesses == null)
            {
                return null;
            }

            return snapshot.ParserHarnesses
                .Where(harness => harness != null
                    && harness.PositiveMetadataParserVerified
                    && harness.LocatedOutsidePluginRoot
                    && harness.AssemblyExists
                    && harness.AssemblyMetadataReadable
                    && harness.ExecutionPlan != null
                    && harness.ExecutionPlan.Ready)
                .OrderBy(harness => harness.SequenceNumber)
                .FirstOrDefault();
        }

        private static OpenACAIBepInExPluginCompatibilityCandidateSnapshot SelectLoadValidationCandidate(
            OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Candidates == null)
            {
                return null;
            }

            return snapshot.Candidates
                .Where(candidate => candidate != null
                    && candidate.Enabled
                    && candidate.AssemblyExists
                    && candidate.AssemblyMetadataReadable
                    && candidate.CompatibilityReady
                    && candidate.ExecutionPlan != null
                    && candidate.ExecutionPlan.Ready)
                .OrderBy(candidate => candidate.ValidationProbe ? 1 : 0)
                .ThenBy(candidate => candidate.SequenceNumber)
                .FirstOrDefault();
        }

        private static OpenACAIBepInExPluginCompatibilityCandidateSnapshot SelectActivationValidationProbeCandidate(
            OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Candidates == null)
            {
                return null;
            }

            return snapshot.Candidates
                .Where(candidate => candidate != null
                    && candidate.ValidationProbe
                    && candidate.Enabled
                    && candidate.AssemblyExists
                    && candidate.AssemblyMetadataReadable
                    && candidate.CompatibilityReady
                    && candidate.ExecutionPlan != null
                    && candidate.ExecutionPlan.Ready)
                .OrderBy(candidate => candidate.SequenceNumber)
                .FirstOrDefault();
        }

        private static BepInExPluginAssemblyMetadata InspectAssemblyMetadata(
            string assemblyPath, ICollection<BepInExPluginAssemblyMetadata> pluginTypes = null,
            bool allowModuleInitialization = false)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return BepInExPluginAssemblyMetadata.Missing("missing-assembly", "No assembly path is available for BepInEx plugin metadata validation.");
            }

            if (!File.Exists(assemblyPath))
            {
                return BepInExPluginAssemblyMetadata.Missing("missing-assembly", "Assembly file was not found for BepInEx plugin metadata validation.");
            }

            try
            {
                using (var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var peReader = new PEReader(stream))
                {
                    if (!peReader.HasMetadata)
                    {
                        return BepInExPluginAssemblyMetadata.Missing("invalid-assembly-metadata", "Assembly file does not contain managed metadata.");
                    }

                    var reader = peReader.GetMetadataReader();
                    var assemblyName = "";
                    var assemblyVersion = "";
                    var assemblyReferences = ReadAssemblyReferences(reader, Path.GetDirectoryName(assemblyPath));
                    if (reader.IsAssembly)
                    {
                        var definition = reader.GetAssemblyDefinition();
                        assemblyName = reader.GetString(definition.Name);
                        assemblyVersion = definition.Version == null ? "" : definition.Version.ToString();
                    }

                    var plugins = new List<BepInExPluginAssemblyMetadata>();
                    var moduleInitializerCount = CountModuleInitializerMethods(reader);

                    foreach (var typeHandle in reader.TypeDefinitions)
                    {
                        var typeDefinition = reader.GetTypeDefinition(typeHandle);
                        var typeName = ReadTypeDefinitionFullName(reader, typeDefinition);
                        var pluginAttributeCount = 0;
                        var pluginGuid = "";
                        var pluginName = "";
                        var pluginVersion = "";
                        var selectedPluginMetadataComplete = false;
                        foreach (var attributeHandle in typeDefinition.GetCustomAttributes())
                        {
                            var attribute = reader.GetCustomAttribute(attributeHandle);
                            var attributeTypeName = ReadAttributeTypeName(reader, attribute.Constructor);
                            if (!IsBepInExPluginAttribute(attributeTypeName))
                            {
                                continue;
                            }

                            pluginAttributeCount++;
                            var attributeReadable = TryReadBepInExPluginAttribute(reader, attribute, out var guid, out var name, out var version);
                            var metadataComplete = attributeReadable
                                && !string.IsNullOrWhiteSpace(guid)
                                && !string.IsNullOrWhiteSpace(name)
                                && !string.IsNullOrWhiteSpace(version);
                            if (pluginAttributeCount == 1 || (!selectedPluginMetadataComplete && metadataComplete))
                            {
                                pluginGuid = attributeReadable ? guid : "";
                                pluginName = attributeReadable ? name : "";
                                pluginVersion = attributeReadable ? version : "";
                                selectedPluginMetadataComplete = metadataComplete;
                            }
                        }

                        if (pluginAttributeCount == 0)
                            continue;

                        OpenACAIBepInExPluginExecutionPlanSnapshot executionPlan;
                        try
                        {
                            executionPlan = CreateExecutionPlan(reader, typeHandle, typeName,
                                Path.GetDirectoryName(assemblyPath), moduleInitializerCount, allowModuleInitialization);
                        }
                        catch (Exception ex)
                        {
                            executionPlan = OpenACAIBepInExPluginExecutionPlanSnapshot.CreateUnavailable(
                                typeName, "bepinex-plugin-execution-plan-error", "Plugin metadata could not be inspected: " + ex.Message);
                        }

                        plugins.Add(new BepInExPluginAssemblyMetadata(
                            true, "", "Plugin metadata was read successfully.", assemblyName, assemblyVersion,
                            assemblyReferences, pluginAttributeCount, pluginGuid, pluginName, pluginVersion, typeName, executionPlan));
                    }

                    // Keep the assembly summary for the existing metadata-only probe surfaces.
                    var selected = plugins.FirstOrDefault(plugin => !string.IsNullOrWhiteSpace(plugin.PluginGuid)
                        && !string.IsNullOrWhiteSpace(plugin.PluginName) && !string.IsNullOrWhiteSpace(plugin.PluginVersion))
                        ?? plugins.FirstOrDefault();
                    var summary = new BepInExPluginAssemblyMetadata(
                        true,
                        "",
                        "Assembly metadata was read successfully.",
                        assemblyName,
                        assemblyVersion,
                        assemblyReferences,
                        plugins.Sum(plugin => plugin.PluginAttributeCount),
                        selected?.PluginGuid,
                        selected?.PluginName,
                        selected?.PluginVersion,
                        selected?.PluginTypeName,
                        selected?.ExecutionPlan);
                    if (pluginTypes != null)
                    {
                        foreach (var plugin in plugins)
                            pluginTypes.Add(plugin);
                    }
                    return summary;
                }
            }
            catch (BadImageFormatException ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("invalid-assembly-image", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (IOException ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("assembly-metadata-io", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("assembly-metadata-access", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (Exception ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("assembly-metadata-error", "Assembly metadata could not be read: " + ex.Message);
            }
        }

        private static OpenACAIBepInExPluginExecutionPlanSnapshot CreateExecutionPlan(
            MetadataReader reader,
            TypeDefinitionHandle pluginTypeHandle,
            string pluginTypeName,
            string requestingDirectory,
            int moduleInitializerCount,
            bool allowModuleInitialization)
        {
            if (reader == null || pluginTypeHandle.IsNil)
            {
                return OpenACAIBepInExPluginExecutionPlanSnapshot.CreateUnavailable(
                    pluginTypeName,
                    "bepinex-plugin-type-unavailable",
                    "No BepInEx plugin type was available for execution-plan validation.");
            }

            var typeDefinition = reader.GetTypeDefinition(pluginTypeHandle);
            var normalizedTypeName = string.IsNullOrWhiteSpace(pluginTypeName)
                ? ReadTypeDefinitionFullName(reader, typeDefinition)
                : pluginTypeName;
            var typePublic = IsPublicType(typeDefinition.Attributes);
            var typeAbstract = (typeDefinition.Attributes & TypeAttributes.Abstract) == TypeAttributes.Abstract;
            var typeOpenGeneric = typeDefinition.GetGenericParameters().Count > 0;
            var baseTypeName = ReadTypeName(reader, typeDefinition.BaseType);
            var basePluginChain = FindBaseTypeInDefinitionChain(reader, typeDefinition, requestingDirectory, IsBepInExBasePluginTypeName);
            var baseUnityPluginChain = FindBaseTypeInDefinitionChain(reader, typeDefinition, requestingDirectory, IsBepInExBaseUnityPluginTypeName);
            var basePluginReferenceFound = basePluginChain.Found;
            var baseUnityPluginReferenceFound = baseUnityPluginChain.Found;
            var pluginBaseKind = basePluginReferenceFound
                ? "base-plugin"
                : baseUnityPluginReferenceFound ? "base-unity-plugin" : "unknown";
            var selectedBaseChain = basePluginReferenceFound
                ? basePluginChain
                : baseUnityPluginReferenceFound ? baseUnityPluginChain : BepInExBaseChainMetadata.Missing("");
            var constructorFound = false;
            var parameterlessConstructorFound = false;
            var publicParameterlessConstructorFound = false;
            var staticConstructorFound = false;
            var moduleInitializerFound = moduleInitializerCount > 0;
            var methods = new List<BepInExPluginMethodMetadata>();

            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var methodDefinition = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(methodDefinition.Name);
                var parameterCount = CountMethodParameters(reader, methodDefinition);
                var isPublic = (methodDefinition.Attributes & MethodAttributes.Public) == MethodAttributes.Public;
                var isStatic = (methodDefinition.Attributes & MethodAttributes.Static) == MethodAttributes.Static;
                if (string.Equals(methodName, ".ctor", StringComparison.Ordinal))
                {
                    constructorFound = true;
                    if (parameterCount == 0)
                    {
                        parameterlessConstructorFound = true;
                        if (isPublic)
                        {
                            publicParameterlessConstructorFound = true;
                        }
                    }

                    continue;
                }

                if (string.Equals(methodName, ".cctor", StringComparison.Ordinal))
                {
                    staticConstructorFound = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(methodName) || methodName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                methods.Add(new BepInExPluginMethodMetadata(methodName, isStatic, isPublic, parameterCount));
            }

            var lifecycleMethods = CreateLifecycleMethodPlans(methods, pluginBaseKind);
            var requiredEntrypointMethodName = string.Equals(pluginBaseKind, "base-plugin", StringComparison.Ordinal)
                ? "Load"
                : "";
            var requiredEntrypointMethodFound = string.IsNullOrWhiteSpace(requiredEntrypointMethodName)
                || lifecycleMethods.Any(method => method != null
                    && string.Equals(method.MethodName, requiredEntrypointMethodName, StringComparison.Ordinal)
                    && method.MethodFound);
            var lifecycleIssueCount = lifecycleMethods.Count(method => method != null && method.Issue);
            var ready = false;
            var status = "CHECK";
            var issueCode = "bepinex-plugin-execution-plan-not-ready";
            var detail = "BepInEx plugin execution-plan metadata is not ready.";

            if (string.IsNullOrWhiteSpace(normalizedTypeName))
            {
                issueCode = "bepinex-plugin-type-missing";
                detail = "BepInEx plugin execution plan could not identify the plugin type.";
            }
            else if (typeAbstract)
            {
                issueCode = "bepinex-plugin-type-abstract";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' is abstract and cannot be instantiated by the compatibility load path.";
            }
            else if (typeOpenGeneric)
            {
                issueCode = "bepinex-plugin-type-open-generic";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' has unbound generic parameters and cannot be instantiated by the compatibility load path.";
            }
            else if (!basePluginReferenceFound && !baseUnityPluginReferenceFound)
            {
                issueCode = "bepinex-plugin-base-type-unrecognized";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' does not expose a metadata-visible BasePlugin or BaseUnityPlugin inheritance path.";
            }
            else if (!parameterlessConstructorFound)
            {
                issueCode = "bepinex-plugin-parameterless-constructor-missing";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' does not expose a parameterless constructor in metadata.";
            }
            else if (basePluginReferenceFound && !publicParameterlessConstructorFound)
            {
                issueCode = "bepinex-plugin-parameterless-constructor-not-public";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' does not expose the public parameterless constructor used by the compatibility host.";
            }
            else if (moduleInitializerFound && (!allowModuleInitialization || !basePluginReferenceFound))
            {
                issueCode = "bepinex-plugin-module-initializer-present";
                detail = "BepInEx plugin assembly declares module initialization; this metadata/validation path does not permit its execution.";
            }
            else if (lifecycleIssueCount > 0)
            {
                issueCode = "bepinex-plugin-lifecycle-method-issue";
                detail = lifecycleIssueCount + " BepInEx plugin lifecycle/entrypoint method issue(s) were found on '" + normalizedTypeName + "'.";
            }
            else
            {
                ready = true;
                status = "READY";
                issueCode = "";
                detail = RuntimePluginLoadingEnabled
                    ? "BepInEx plugin execution-plan metadata is ready for the runtime load path."
                    : "BepInEx plugin execution-plan metadata is ready; runtime plugin loading remains disabled.";
            }

            return new OpenACAIBepInExPluginExecutionPlanSnapshot(
                normalizedTypeName,
                true,
                typePublic,
                typeAbstract,
                baseTypeName,
                pluginBaseKind,
                basePluginReferenceFound,
                baseUnityPluginReferenceFound,
                selectedBaseChain.MatchTypeName,
                selectedBaseChain.MatchDepth,
                selectedBaseChain.ExternalAssemblyResolved,
                selectedBaseChain.ExternalAssemblyName,
                selectedBaseChain.ExternalAssemblyPath,
                selectedBaseChain.ExternalAssemblySource,
                selectedBaseChain.Detail,
                constructorFound,
                parameterlessConstructorFound,
                publicParameterlessConstructorFound,
                staticConstructorFound,
                moduleInitializerFound,
                moduleInitializerCount,
                requiredEntrypointMethodName,
                requiredEntrypointMethodFound,
                lifecycleMethods,
                ready,
                RuntimePluginLoadingEnabled,
                ready && RuntimePluginLoadingEnabled,
                ready && !RuntimePluginLoadingEnabled,
                status,
                issueCode,
                detail);
        }

        private static int CountModuleInitializerMethods(MetadataReader reader)
        {
            if (reader == null)
            {
                return 0;
            }

            var count = 0;
            var moduleConstructorFound = false;
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeDefinition = reader.GetTypeDefinition(typeHandle);
                var isModule = reader.GetString(typeDefinition.Name) == "<Module>";
                foreach (var methodHandle in typeDefinition.GetMethods())
                {
                    var methodDefinition = reader.GetMethodDefinition(methodHandle);
                    if (isModule && reader.GetString(methodDefinition.Name) == ".cctor"
                        && (methodDefinition.Attributes & MethodAttributes.Static) != 0)
                        moduleConstructorFound = true;
                    foreach (var attributeHandle in methodDefinition.GetCustomAttributes())
                    {
                        var attribute = reader.GetCustomAttribute(attributeHandle);
                        if (IsModuleInitializerAttribute(ReadAttributeTypeName(reader, attribute.Constructor)))
                        {
                            count++;
                            break;
                        }
                    }
                }
            }

            // Attribute counts describe source methods; a module constructor can exist without them.
            return Math.Max(count, moduleConstructorFound ? 1 : 0);
        }

        private static IReadOnlyList<OpenACAIBepInExPluginLifecycleMethodSnapshot> CreateLifecycleMethodPlans(
            IReadOnlyList<BepInExPluginMethodMetadata> methods,
            string pluginBaseKind)
        {
            var plans = new List<OpenACAIBepInExPluginLifecycleMethodSnapshot>();
            if (string.Equals(pluginBaseKind, "base-plugin", StringComparison.Ordinal))
            {
                // The host calls the BasePlugin virtual contract, not a method declared by name.
                plans.Add(new OpenACAIBepInExPluginLifecycleMethodSnapshot(
                    "Load", "entrypoint", true, false, true, 0, true, "READY", "",
                    "BasePlugin.Load() is the virtual entrypoint, including inherited overrides. "
                    + "The runtime validates the concrete implementation when loading the plugin type."));
                return plans;
            }

            foreach (var methodName in UnityLifecycleMethodNames)
            {
                if (plans.Count >= MaxLifecycleMethodCount)
                {
                    break;
                }

                var method = SelectBestMethod(methods, methodName);
                if (method == null)
                {
                    continue;
                }

                plans.Add(CreateLifecycleMethodPlan(method, methodName, "unity-lifecycle", false));
            }

            return plans;
        }

        private static OpenACAIBepInExPluginLifecycleMethodSnapshot CreateLifecycleMethodPlan(
            BepInExPluginMethodMetadata method,
            string methodName,
            string methodKind,
            bool requirePublic)
        {
            var status = "CHECK";
            var issueCode = "bepinex-plugin-lifecycle-method-not-ready";
            var detail = "BepInEx plugin lifecycle/entrypoint method metadata is not ready.";
            var ready = false;

            if (method == null)
            {
                issueCode = "bepinex-plugin-required-entrypoint-missing";
                detail = "Required BepInEx plugin entrypoint method '" + methodName + "' was not found.";
                return new OpenACAIBepInExPluginLifecycleMethodSnapshot(methodName, methodKind, false, false, false, 0, false, status, issueCode, detail);
            }

            if (method.IsStatic)
            {
                issueCode = "bepinex-plugin-lifecycle-method-static";
                detail = "BepInEx plugin lifecycle/entrypoint method '" + methodName + "' must be an instance method.";
            }
            else if (method.ParameterCount > 0)
            {
                issueCode = "bepinex-plugin-lifecycle-method-parameters";
                detail = "BepInEx plugin lifecycle/entrypoint method '" + methodName + "' must not require parameters in the current metadata gate.";
            }
            else if (requirePublic && !method.IsPublic)
            {
                issueCode = "bepinex-plugin-entrypoint-method-not-public";
                detail = "BepInEx BasePlugin entrypoint method '" + methodName + "' must be public.";
            }
            else
            {
                ready = true;
                status = "READY";
                issueCode = "";
                detail = "BepInEx plugin lifecycle/entrypoint method '" + methodName + "' metadata is ready.";
            }

            return new OpenACAIBepInExPluginLifecycleMethodSnapshot(
                method.MethodName,
                methodKind,
                true,
                method.IsStatic,
                method.IsPublic,
                method.ParameterCount,
                ready,
                status,
                issueCode,
                detail);
        }

        private static BepInExPluginMethodMetadata SelectBestMethod(
            IReadOnlyList<BepInExPluginMethodMetadata> methods,
            string methodName)
        {
            if (methods == null || string.IsNullOrWhiteSpace(methodName))
            {
                return null;
            }

            return methods
                .Where(method => method != null && string.Equals(method.MethodName, methodName, StringComparison.Ordinal))
                .OrderByDescending(method => !method.IsStatic && method.ParameterCount == 0)
                .ThenBy(method => method.ParameterCount)
                .FirstOrDefault();
        }

        private static BepInExBaseChainMetadata FindBaseTypeInDefinitionChain(
            MetadataReader reader,
            TypeDefinition typeDefinition,
            string requestingDirectory,
            Func<string, bool> match)
        {
            return FindBaseTypeInDefinitionChain(
                reader,
                typeDefinition,
                requestingDirectory,
                match,
                new HashSet<string>(StringComparer.Ordinal),
                0);
        }

        private static BepInExBaseChainMetadata FindBaseTypeInDefinitionChain(
            MetadataReader reader,
            TypeDefinition typeDefinition,
            string requestingDirectory,
            Func<string, bool> match,
            ISet<string> visited,
            int depth)
        {
            if (reader == null || match == null)
            {
                return BepInExBaseChainMetadata.Missing("Base-chain metadata was unavailable.");
            }

            if (depth >= MaxExternalBaseChainDepth)
            {
                return BepInExBaseChainMetadata.Missing("Base-chain traversal reached the OpenACAI depth guard.");
            }

            var baseType = ReadBaseTypeHandle(reader, typeDefinition.BaseType);
            if (baseType.IsNil)
            {
                return BepInExBaseChainMetadata.Missing("Plugin type does not expose a metadata-visible base type.");
            }

            var baseTypeName = ReadTypeName(reader, baseType);
            if (match(baseTypeName))
            {
                return BepInExBaseChainMetadata.CreateFound(
                    baseTypeName,
                    depth + 1,
                    "Matched metadata-visible BepInEx base type '" + baseTypeName + "'.");
            }

            if (baseType.Kind != HandleKind.TypeDefinition)
            {
                return baseType.Kind == HandleKind.TypeReference
                    ? FindExternalBaseTypeInDefinitionChain(
                        reader,
                        (TypeReferenceHandle)baseType,
                        requestingDirectory,
                        match,
                        visited,
                        depth + 1)
                    : BepInExBaseChainMetadata.Missing("Base-chain metadata handle kind '" + baseType.Kind + "' is not supported.");
            }

            var baseTypeHandle = (TypeDefinitionHandle)baseType;
            var visitedKey = "local:" + reader.GetGuid(reader.GetModuleDefinition().Mvid) + ":" + baseTypeName;
            if (!visited.Add(visitedKey))
            {
                return BepInExBaseChainMetadata.Missing("Base-chain traversal encountered a repeated local base type '" + baseTypeName + "'.");
            }

            return FindBaseTypeInDefinitionChain(
                reader,
                reader.GetTypeDefinition(baseTypeHandle),
                requestingDirectory,
                match,
                visited,
                depth + 1);
        }

        private static BepInExBaseChainMetadata FindExternalBaseTypeInDefinitionChain(
            MetadataReader reader,
            TypeReferenceHandle typeReferenceHandle,
            string requestingDirectory,
            Func<string, bool> match,
            ISet<string> visited,
            int depth)
        {
            if (reader == null || typeReferenceHandle.IsNil || match == null || depth >= MaxExternalBaseChainDepth)
            {
                return BepInExBaseChainMetadata.Missing("External base-chain metadata was unavailable or exceeded the depth guard.");
            }

            var typeReference = reader.GetTypeReference(typeReferenceHandle);
            var typeName = ReadTypeName(reader, typeReferenceHandle);
            if (match(typeName))
            {
                return BepInExBaseChainMetadata.CreateFound(
                    typeName,
                    depth,
                    "Matched metadata-visible BepInEx external base type '" + typeName + "'.");
            }

            OpenACAIBepInExPluginAssemblyReferenceSnapshot resolvedReference;
            if (!TryResolveTypeReferenceAssembly(reader, typeReference, requestingDirectory, out resolvedReference)
                || resolvedReference == null
                || string.IsNullOrWhiteSpace(resolvedReference.ResolvedPath)
                || !File.Exists(resolvedReference.ResolvedPath))
            {
                return BepInExBaseChainMetadata.Missing("External base type '" + typeName + "' could not be resolved through OpenACAI resolver roots.");
            }

            string fullAssemblyPath;
            try
            {
                fullAssemblyPath = Path.GetFullPath(resolvedReference.ResolvedPath);
            }
            catch
            {
                return BepInExBaseChainMetadata.Missing("External base type '" + typeName + "' resolved to an invalid assembly path.");
            }

            var visitedKey = "external:" + fullAssemblyPath.ToUpperInvariant() + ":" + typeName;
            if (!visited.Add(visitedKey))
            {
                return BepInExBaseChainMetadata.Missing("Base-chain traversal encountered a repeated external base type '" + typeName + "'.");
            }

            try
            {
                using (var stream = new FileStream(fullAssemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var peReader = new PEReader(stream))
                {
                    if (!peReader.HasMetadata)
                    {
                        return BepInExBaseChainMetadata.Missing("External base assembly '" + fullAssemblyPath + "' did not expose metadata.");
                    }

                    var externalReader = peReader.GetMetadataReader();
                    var externalTypeHandle = FindTypeDefinition(externalReader, typeName);
                    if (externalTypeHandle.IsNil)
                    {
                        return BepInExBaseChainMetadata.Missing("External base type '" + typeName + "' was not found in resolved assembly metadata.");
                    }

                    var result = FindBaseTypeInDefinitionChain(
                        externalReader,
                        externalReader.GetTypeDefinition(externalTypeHandle),
                        Path.GetDirectoryName(fullAssemblyPath),
                        match,
                        visited,
                        depth);
                    return result.Found
                        ? result.WithExternalAssembly(
                            resolvedReference.AssemblyName,
                            fullAssemblyPath,
                            resolvedReference.Source,
                            "Resolved external base type '" + typeName + "' through " + resolvedReference.Source + ". " + result.Detail)
                        : result;
                }
            }
            catch (Exception ex)
            {
                return BepInExBaseChainMetadata.Missing("External base assembly metadata could not be read: " + ex.Message);
            }
        }

        private static bool TryResolveTypeReferenceAssembly(
            MetadataReader reader,
            TypeReference typeReference,
            string requestingDirectory,
            out OpenACAIBepInExPluginAssemblyReferenceSnapshot resolvedReference)
        {
            resolvedReference = null;
            if (reader == null)
            {
                return false;
            }

            var scope = typeReference.ResolutionScope;
            while (!scope.IsNil)
            {
                if (scope.Kind == HandleKind.AssemblyReference)
                {
                    var reference = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                    var name = reader.GetString(reference.Name);
                    var version = reference.Version == null ? "" : reference.Version.ToString();
                    var resolved = ResolveAssemblyReference(name, version, requestingDirectory);
                    if (resolved != null
                        && resolved.Resolved
                        && !string.IsNullOrWhiteSpace(resolved.ResolvedPath)
                        && File.Exists(resolved.ResolvedPath))
                    {
                        resolvedReference = resolved;
                        return true;
                    }

                    return false;
                }

                if (scope.Kind == HandleKind.TypeReference)
                {
                    scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
                    continue;
                }

                return false;
            }

            return false;
        }

        private static TypeDefinitionHandle FindTypeDefinition(MetadataReader reader, string fullName)
        {
            if (reader == null || string.IsNullOrWhiteSpace(fullName))
            {
                return default(TypeDefinitionHandle);
            }

            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeDefinition = reader.GetTypeDefinition(typeHandle);
                var typeName = ReadTypeDefinitionFullName(reader, typeDefinition);
                if (string.Equals(typeName, fullName, StringComparison.Ordinal))
                {
                    return typeHandle;
                }
            }

            return default(TypeDefinitionHandle);
        }

        private static bool IsBepInExBasePluginTypeName(string fullName)
        {
            return string.Equals(fullName, "BepInEx.Unity.IL2CPP.BasePlugin", StringComparison.Ordinal)
                || string.Equals(fullName, "BepInEx.BasePlugin", StringComparison.Ordinal)
                || string.Equals(ShortTypeName(fullName), "BasePlugin", StringComparison.Ordinal);
        }

        private static bool IsBepInExBaseUnityPluginTypeName(string fullName)
        {
            return string.Equals(fullName, "BepInEx.BaseUnityPlugin", StringComparison.Ordinal)
                || string.Equals(ShortTypeName(fullName), "BaseUnityPlugin", StringComparison.Ordinal);
        }

        private static bool IsPublicType(TypeAttributes attributes)
        {
            var visibility = attributes & TypeAttributes.VisibilityMask;
            return visibility == TypeAttributes.Public || visibility == TypeAttributes.NestedPublic;
        }

        private static int CountMethodParameters(MetadataReader reader, MethodDefinition methodDefinition)
        {
            // Parameter table rows carry names/attributes and may be omitted; the signature is authoritative.
            return methodDefinition.DecodeSignature(BepInExBaseTypeSignatureProvider.Instance, 0).ParameterTypes.Length;
        }

        private static IReadOnlyList<OpenACAIBepInExPluginAssemblyReferenceSnapshot> ReadAssemblyReferences(MetadataReader reader, string requestingDirectory)
        {
            if (reader == null)
            {
                return Array.Empty<OpenACAIBepInExPluginAssemblyReferenceSnapshot>();
            }

            var references = new List<OpenACAIBepInExPluginAssemblyReferenceSnapshot>();
            foreach (var referenceHandle in reader.AssemblyReferences)
            {
                if (references.Count >= MaxAssemblyReferenceCount)
                {
                    break;
                }

                var reference = reader.GetAssemblyReference(referenceHandle);
                var name = reader.GetString(reference.Name);
                var version = reference.Version == null ? "" : reference.Version.ToString();
                references.Add(ResolveAssemblyReference(name, version, requestingDirectory));
            }

            return references;
        }

        private static OpenACAIBepInExPluginAssemblyReferenceSnapshot ResolveAssemblyReference(
            string assemblyName,
            string requiredVersion,
            string requestingDirectory)
        {
            var normalizedName = string.IsNullOrWhiteSpace(assemblyName) ? "" : assemblyName.Trim();
            var normalizedVersion = string.IsNullOrWhiteSpace(requiredVersion) ? "" : requiredVersion.Trim();
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return new OpenACAIBepInExPluginAssemblyReferenceSnapshot(
                    "",
                    normalizedVersion,
                    false,
                    "invalid",
                    "",
                    "",
                    "Assembly reference name was empty.");
            }

            var loaded = FindLoadedAssembly(normalizedName);
            if (loaded != null)
            {
                return new OpenACAIBepInExPluginAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    "loaded",
                    SafeAssemblyVersion(loaded),
                    SafeAssemblyLocation(loaded),
                    "Assembly reference is already loaded in the runtime domain.");
            }

            string resolvedPath;
            string resolvedSource;
            if (OpenACAIAssemblyResolver.TryFindAssemblyPath(normalizedName, requestingDirectory, out resolvedPath, out resolvedSource))
            {
                return new OpenACAIBepInExPluginAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    string.IsNullOrWhiteSpace(resolvedSource) ? "resolver" : resolvedSource,
                    SafeAssemblyVersion(resolvedPath),
                    resolvedPath,
                    "Assembly reference was found through OpenACAI resolver roots.");
            }

            if (IsFrameworkReference(normalizedName))
            {
                return new OpenACAIBepInExPluginAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    "framework-reference",
                    "",
                    "",
                    "Assembly reference is supplied by the current .NET runtime.");
            }

            return new OpenACAIBepInExPluginAssemblyReferenceSnapshot(
                normalizedName,
                normalizedVersion,
                false,
                "unresolved",
                "",
                "",
                "No loaded assembly or OpenACAI resolver path matched this assembly reference.");
        }

        private static Assembly FindLoadedAssembly(string assemblyName)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AssemblyName name;
                    try
                    {
                        name = assembly.GetName();
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.Equals(name.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool IsFrameworkReference(string assemblyName)
        {
            return string.Equals(assemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "netstandard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "System", StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "Microsoft.VisualBasic.Core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "Microsoft.Win32.Registry", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeAssemblyVersion(Assembly assembly)
        {
            try
            {
                return assembly == null || assembly.GetName().Version == null ? "" : assembly.GetName().Version.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string SafeAssemblyVersion(string assemblyPath)
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(assemblyPath);
                return name.Version == null ? "" : name.Version.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string SafeAssemblyLocation(Assembly assembly)
        {
            try
            {
                return assembly == null ? "" : assembly.Location ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool TryReadBepInExPluginAttribute(
            MetadataReader reader,
            CustomAttribute attribute,
            out string guid,
            out string name,
            out string version)
        {
            guid = "";
            name = "";
            version = "";

            try
            {
                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.Length < 2 || blob.ReadUInt16() != 1)
                {
                    return false;
                }

                guid = blob.ReadSerializedString() ?? "";
                name = blob.ReadSerializedString() ?? "";
                version = blob.ReadSerializedString() ?? "";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadAttributeTypeName(MetadataReader reader, EntityHandle constructor)
        {
            if (reader == null || constructor.IsNil)
            {
                return "";
            }

            switch (constructor.Kind)
            {
                case HandleKind.MemberReference:
                    var memberReference = reader.GetMemberReference((MemberReferenceHandle)constructor);
                    return ReadTypeName(reader, memberReference.Parent);
                case HandleKind.MethodDefinition:
                    var methodDefinition = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
                    return ReadTypeName(reader, methodDefinition.GetDeclaringType());
                default:
                    return "";
            }
        }

        private static EntityHandle ReadBaseTypeHandle(MetadataReader reader, EntityHandle handle)
        {
            return handle.Kind == HandleKind.TypeSpecification
                ? BepInExBaseTypeSignatureProvider.Instance.GetTypeFromSpecification(reader, 0, (TypeSpecificationHandle)handle, 0)
                : handle;
        }

        private static string ReadTypeName(MetadataReader reader, EntityHandle handle)
        {
            if (reader == null || handle.IsNil)
            {
                return "";
            }

            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return ReadTypeDefinitionFullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle));
                case HandleKind.TypeReference:
                    var typeReference = reader.GetTypeReference((TypeReferenceHandle)handle);
                    if (typeReference.ResolutionScope.Kind == HandleKind.TypeReference)
                        return ReadTypeName(reader, typeReference.ResolutionScope) + "+" + reader.GetString(typeReference.Name);
                    return CombineTypeName(reader.GetString(typeReference.Namespace), reader.GetString(typeReference.Name));
                case HandleKind.TypeSpecification:
                    return ReadTypeName(reader, ReadBaseTypeHandle(reader, handle));
                default:
                    return "";
            }
        }

        private static string ReadTypeDefinitionFullName(MetadataReader reader, TypeDefinition typeDefinition)
        {
            var declaringType = typeDefinition.GetDeclaringType();
            if (!declaringType.IsNil)
                return ReadTypeDefinitionFullName(reader, reader.GetTypeDefinition(declaringType)) + "+" + reader.GetString(typeDefinition.Name);
            return CombineTypeName(reader.GetString(typeDefinition.Namespace), reader.GetString(typeDefinition.Name));
        }

        private static string CombineTypeName(string typeNamespace, string name)
        {
            return string.IsNullOrWhiteSpace(typeNamespace) ? name ?? "" : typeNamespace + "." + name;
        }

        private static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return "";
            }

            var index = fullName.LastIndexOf('.');
            return index < 0 || index >= fullName.Length - 1 ? fullName : fullName.Substring(index + 1);
        }

        private static bool IsBepInExPluginAttribute(string fullName)
        {
            return string.Equals(fullName, "BepInEx.BepInPlugin", StringComparison.Ordinal)
                || string.Equals(fullName, "BepInEx.BepInPluginAttribute", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(fullName), "BepInPlugin", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(fullName), "BepInPluginAttribute", StringComparison.Ordinal);
        }

        private static bool IsModuleInitializerAttribute(string fullName)
        {
            var shortName = ShortTypeName(fullName);
            return string.Equals(fullName, "System.Runtime.CompilerServices.ModuleInitializerAttribute", StringComparison.Ordinal)
                || string.Equals(shortName, "ModuleInitializerAttribute", StringComparison.Ordinal)
                || string.Equals(shortName, "ModuleInitializer", StringComparison.Ordinal);
        }

        private static string CandidatePlanId(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            return candidate?.CandidateId ?? "";
        }

        private static string PlanKey(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            return CandidatePlanId(candidate);
        }

        private static string NormalizeId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static string ResolvePluginRoot()
        {
            try
            {
                return string.IsNullOrWhiteSpace(Paths.PluginPath) ? "" : Paths.PluginPath;
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveParserHarnessRoot()
        {
            try
            {
                return Path.Combine(OpenACAIPaths.ValidationProbesDirectory, ParserHarnessDirectoryName);
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveCandidateHarnessRoot()
        {
            try
            {
                return Path.Combine(OpenACAIPaths.ValidationProbesDirectory, CandidateHarnessDirectoryName);
            }
            catch
            {
                return "";
            }
        }

        private static bool IsPathUnderRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static long GetFileSize(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? 0 : new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static IReadOnlyList<OpenACAICommandSnapshot> CreateCommands()
        {
            var snapshot = State;
            return new[]
            {
                new OpenACAICommandSnapshot(
                    ValidateMissingLoadAttemptCommandId,
                    "Validate Missing Attempt",
                    "Record a generated missing-plugin BepInEx load attempt without loading or invoking plugin code.",
                    "READY",
                    true,
                    false),
                new OpenACAICommandSnapshot(
                    ValidateDeferredLoadAttemptCommandId,
                    "Validate Deferred Attempt",
                    "Record a generated accepted BepInEx load attempt while proving the runtime gate still blocks plugin invocation.",
                    "READY",
                    true,
                    false),
                new OpenACAICommandSnapshot(
                    ValidateMetadataLoadAttemptCommandId,
                    "Validate Metadata Attempt",
                    "Record a parser-harness-backed BepInEx load attempt while proving the runtime gate still blocks plugin invocation.",
                    snapshot.PositiveMetadataParserVerified ? "READY" : "CHECK",
                    snapshot.PositiveMetadataParserVerified,
                    false),
                new OpenACAICommandSnapshot(
                    ValidateCandidateLoadAttemptCommandId,
                    "Validate Candidate Attempt",
                    "Record a candidate-backed BepInEx load attempt while proving the runtime gate still blocks plugin invocation.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateAssemblyLoadTransitionRequestCommandId,
                    "Validate Assembly Load Request",
                    "Record a candidate-backed assembly-load executor request while proving the request gate blocks before assembly load.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateAssemblyLoadResolverPlanCommandId,
                    "Validate Assembly Resolver Plan",
                    "Record a candidate-backed assembly-load resolver plan while proving assembly loading remains blocked.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateAssemblyLoadOperationCommandId,
                    "Validate Assembly Load Operation",
                    "Record a candidate-backed would-call assembly-load operation while proving LoadFromAssemblyPath remains blocked.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateAssemblyLoadContextPlanCommandId,
                    "Validate Assembly Context Plan",
                    "Record a candidate-backed assembly-load context allocation plan while proving no AssemblyLoadContext is created.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateAssemblyLoadContextAllocationCommandId,
                    "Validate Assembly Context Allocation",
                    "Record a candidate-backed AssemblyLoadContext allocation attempt while proving the constructor is not called.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateAssemblyLoadContextRegistrationCommandId,
                    "Validate Assembly Context Registration",
                    "Record a candidate-backed AssemblyLoadContext registration boundary while proving no context is tracked or unload-registered.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateAssemblyLoadInvocationCommandId,
                    "Validate Assembly Load Invocation",
                    "Record a candidate-backed LoadFromAssemblyPath invocation boundary while proving no assembly is loaded.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidatePluginTypeResolutionCommandId,
                    "Validate Plugin Type Resolution",
                    "Record a candidate-backed plugin Type resolution boundary while proving Assembly.GetType is not called.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidatePluginInstanceCreationCommandId,
                    "Validate Plugin Instance Creation",
                    "Record a candidate-backed plugin instance creation boundary while proving Activator.CreateInstance is not called.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidatePluginLoadInvocationCommandId,
                    "Validate Plugin Load Invocation",
                    "Record a candidate-backed BasePlugin.Load invocation boundary while proving Load() is not called.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateProbeAssemblyLoadCommandId,
                    "Validate Probe Assembly Load",
                    "Load only the generated validation probe assembly in an OpenACAI collectible context, then unload it before type resolution.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateProbeTypeActivationCommandId,
                    "Validate Probe Type Activation",
                    "Load the generated validation probe in an OpenACAI context, resolve its BasePlugin type, and still block constructor and Load().",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateProbeInstanceCreationCommandId,
                    "Validate Probe Instance Creation",
                    "Create only the generated empty validation probe instance in an OpenACAI context, and still block Load().",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    ValidateProbeLoadInvocationCommandId,
                    "Validate Probe Load Invocation",
                    "Invoke Load() only on the generated empty validation probe in an OpenACAI context.",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady) ? "READY" : "CHECK",
                    snapshot.Candidates.Any(candidate => candidate != null && candidate.ValidationProbe && candidate.CompatibilityReady),
                    false),
                new OpenACAICommandSnapshot(
                    "lastLoadAttempt",
                    "Last Load Attempt",
                    snapshot.LastLoadAttemptDetail,
                    snapshot.LastLoadAttemptStatus,
                    false,
                    false)
            };
        }

        private static bool TryInvokeCommand(string commandId, out OpenACAICommandResultSnapshot result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(commandId))
            {
                result = OpenACAICommandResultSnapshot.Failure("Command id is required.");
                return false;
            }

            if (string.Equals(commandId, ValidateMissingLoadAttemptCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateMissingLoadAttempt(out result);
            }

            if (string.Equals(commandId, ValidateDeferredLoadAttemptCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateDeferredLoadAttempt(out result);
            }

            if (string.Equals(commandId, ValidateMetadataLoadAttemptCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateMetadataLoadAttempt(out result);
            }

            if (string.Equals(commandId, ValidateCandidateLoadAttemptCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateCandidateLoadAttempt(out result);
            }

            if (string.Equals(commandId, ValidateAssemblyLoadTransitionRequestCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateAssemblyLoadTransitionRequest(out result);
            }

            if (string.Equals(commandId, ValidateAssemblyLoadResolverPlanCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateAssemblyLoadResolverPlan(out result);
            }

            if (string.Equals(commandId, ValidateAssemblyLoadOperationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateAssemblyLoadOperation(out result);
            }

            if (string.Equals(commandId, ValidateAssemblyLoadContextPlanCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateAssemblyLoadContextPlan(out result);
            }

            if (string.Equals(commandId, ValidateAssemblyLoadContextAllocationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateAssemblyLoadContextAllocation(out result);
            }

            if (string.Equals(commandId, ValidateAssemblyLoadContextRegistrationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateAssemblyLoadContextRegistration(out result);
            }

            if (string.Equals(commandId, ValidateAssemblyLoadInvocationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateAssemblyLoadInvocation(out result);
            }

            if (string.Equals(commandId, ValidatePluginTypeResolutionCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidatePluginTypeResolution(out result);
            }

            if (string.Equals(commandId, ValidatePluginInstanceCreationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidatePluginInstanceCreation(out result);
            }

            if (string.Equals(commandId, ValidatePluginLoadInvocationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidatePluginLoadInvocation(out result);
            }

            if (string.Equals(commandId, ValidateProbeAssemblyLoadCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateProbeAssemblyLoad(out result);
            }

            if (string.Equals(commandId, ValidateProbeTypeActivationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateProbeTypeActivation(out result);
            }

            if (string.Equals(commandId, ValidateProbeInstanceCreationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateProbeInstanceCreation(out result);
            }

            if (string.Equals(commandId, ValidateProbeLoadInvocationCommandId, StringComparison.OrdinalIgnoreCase))
            {
                return TryValidateProbeLoadInvocation(out result);
            }

            result = OpenACAICommandResultSnapshot.Failure("Unknown BepInEx compatibility command: " + commandId + ".");
            return false;
        }

        private static bool TryValidateMissingLoadAttempt(out OpenACAICommandResultSnapshot result)
        {
            var target = ValidationMissingLoadAttemptTargetPrefix + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = AttemptLoad(target);
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var rejectedAsMissing = attempt != null
                && !attempt.AttemptAccepted
                && string.Equals(attempt.ReasonCode, "unknown-bepinex-plugin", StringComparison.OrdinalIgnoreCase);
            var detail = attempt == null
                ? "BepInEx missing-plugin validation attempt did not return an attempt snapshot."
                : "Recorded BepInEx missing-plugin validation attempt for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-missing-load-attempt-command");
            if (noInvocation && rejectedAsMissing)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateDeferredLoadAttempt(out OpenACAICommandResultSnapshot result)
        {
            var target = ValidationDeferredLoadAttemptTargetPrefix + DateTimeOffset.UtcNow.UtcTicks;
            var attempt = AttemptAcceptedValidationLoad(target);
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var detail = attempt == null
                ? "BepInEx deferred validation attempt did not return an attempt snapshot."
                : "Recorded BepInEx deferred validation attempt for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", plan accepted " + attempt.PlanAccepted
                    + ", attempt accepted " + attempt.AttemptAccepted
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-deferred-load-attempt-command");
            if (noInvocation && acceptedButDeferred)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateMetadataLoadAttempt(out OpenACAICommandResultSnapshot result)
        {
            var attempt = AttemptParserHarnessMetadataLoad();
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var metadataBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceParserHarness, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceParserHarness, StringComparison.OrdinalIgnoreCase);
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var target = attempt == null ? "unknown" : attempt.PluginId;
            var detail = attempt == null
                ? "BepInEx metadata validation attempt did not return an attempt snapshot."
                : "Recorded BepInEx metadata-backed validation attempt for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", validation only " + attempt.ValidationOnly
                    + ", plan accepted " + attempt.PlanAccepted
                    + ", attempt accepted " + attempt.AttemptAccepted
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-metadata-load-attempt-command");
            if (noInvocation && metadataBacked && acceptedButDeferred)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateCandidateLoadAttempt(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-candidate-load-attempt-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for a candidate-backed load attempt.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptLoad(target);
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var invocationPreflighted = attempt != null
                && attempt.InvocationPreflight != null
                && attempt.InvocationPreflight.Available
                && attempt.InvocationPreflight.PreflightReady
                && !attempt.InvocationPreflight.RuntimeExecutionEnabled
                && attempt.InvocationPreflight.InvocationGateBlocked
                && !attempt.InvocationPreflight.InvocationGateOpen
                && attempt.InvocationPreflight.AssemblyLoadRequired
                && attempt.InvocationPreflight.InstanceCreationRequired
                && attempt.InvocationPreflight.MethodInvocationRequired
                && attempt.InvocationPreflight.InvocationPipelineStageCount == 3
                && attempt.InvocationPreflight.RequiredInvocationPipelineStageCount == 3
                && attempt.InvocationPreflight.BlockedInvocationPipelineStageCount == 3
                && attempt.InvocationPreflight.OpenInvocationPipelineStageCount == 0
                && attempt.InvocationPreflight.CompletedInvocationPipelineStageCount == 0
                && string.Equals(attempt.InvocationPreflight.IsolationBoundary, InvocationBoundaryDisabledRuntimeGate, StringComparison.OrdinalIgnoreCase);
            var activationPolicyBlocked = attempt != null
                && attempt.InvocationPreflight != null
                && attempt.InvocationPreflight.ActivationPolicy != null
                && attempt.InvocationPreflight.ActivationPolicy.Available
                && attempt.InvocationPreflight.ActivationPolicy.PolicyEvaluated
                && attempt.InvocationPreflight.ActivationPolicy.CandidateReady
                && attempt.InvocationPreflight.ActivationPolicy.ExecutionPlanReady
                && !attempt.InvocationPreflight.ActivationPolicy.RuntimePluginLoadingEnabled
                && !attempt.InvocationPreflight.ActivationPolicy.RealPluginExecutionAllowed
                && !attempt.InvocationPreflight.ActivationPolicy.AssemblyLoadAllowed
                && !attempt.InvocationPreflight.ActivationPolicy.ConstructorInvocationAllowed
                && !attempt.InvocationPreflight.ActivationPolicy.LoadInvocationAllowed
                && attempt.InvocationPreflight.ActivationPolicy.AssemblyLoadBlocked
                && attempt.InvocationPreflight.ActivationPolicy.ConstructorInvocationBlocked
                && attempt.InvocationPreflight.ActivationPolicy.LoadInvocationBlocked
                && string.Equals(attempt.InvocationPreflight.ActivationPolicy.AssemblyLoadBlockReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.InvocationPreflight.ActivationPolicy.ConstructorInvocationBlockReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.InvocationPreflight.ActivationPolicy.LoadInvocationBlockReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var executorPolicyPropagated = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.ActivationPolicy != null
                && attempt.InvocationExecutor.ActivationPolicy.Available
                && attempt.InvocationExecutor.ActivationPolicy.PolicyEvaluated
                && !attempt.InvocationExecutor.ActivationPolicy.RealPluginExecutionAllowed
                && attempt.InvocationExecutor.ActivationPolicy.AssemblyLoadBlocked
                && attempt.InvocationExecutor.ActivationPolicy.ConstructorInvocationBlocked
                && attempt.InvocationExecutor.ActivationPolicy.LoadInvocationBlocked
                && string.Equals(attempt.InvocationExecutor.ActivationPolicy.AssemblyLoadBlockReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.InvocationExecutor.ActivationPolicy.ConstructorInvocationBlockReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.InvocationExecutor.ActivationPolicy.LoadInvocationBlockReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var stageAttemptsBlocked = attempt != null
                && attempt.InvocationStageAttemptCount == 3
                && attempt.RecordedInvocationStageAttemptCount == 3
                && attempt.BlockedInvocationStageAttemptCount == 3
                && attempt.OpenInvocationStageAttemptCount == 0
                && attempt.StartedInvocationStageAttemptCount == 0
                && attempt.CompletedInvocationStageAttemptCount == 0;
            var executorBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.Available
                && !attempt.InvocationExecutor.ExecutorEnabled
                && !attempt.InvocationExecutor.RuntimePluginLoadingEnabled
                && attempt.InvocationExecutor.ExecutionGateBlocked
                && !attempt.InvocationExecutor.ExecutionGateOpen
                && string.Equals(attempt.InvocationExecutor.StateMachineVersion, InvocationStageExecutorStateMachineVersion, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.InvocationExecutor.InitialState, "pending", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.InvocationExecutor.FinalState, "blocked", StringComparison.OrdinalIgnoreCase)
                && attempt.InvocationExecutor.TransitionCount == 3
                && attempt.InvocationExecutor.BlockedTransitionCount == 3
                && attempt.InvocationExecutor.OpenTransitionCount == 0
                && attempt.InvocationExecutor.StartedTransitionCount == 0
                && attempt.InvocationExecutor.CompletedTransitionCount == 0
                && attempt.InvocationExecutor.FailedIsolatedTransitionCount == 0
                && !attempt.InvocationExecutor.RollbackRequired
                && attempt.InvocationExecutor.FirstBlockedTransition != null
                && string.Equals(attempt.InvocationExecutor.FirstBlockedTransition.StageKind, "assembly-load", StringComparison.OrdinalIgnoreCase);
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var detail = attempt == null
                ? "BepInEx candidate-backed validation attempt did not return an attempt snapshot."
                : "Recorded BepInEx candidate-backed validation attempt for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", validation probe " + (attempt.Candidate != null && attempt.Candidate.ValidationProbe)
                    + ", invocation " + (attempt.InvocationPreflight == null ? "none" : attempt.InvocationPreflight.InvocationKind)
                    + ", invocation gate blocked " + (attempt.InvocationPreflight != null && attempt.InvocationPreflight.InvocationGateBlocked)
                    + ", activation policy " + (attempt.InvocationPreflight == null || attempt.InvocationPreflight.ActivationPolicy == null ? "none" : attempt.InvocationPreflight.ActivationPolicy.Status)
                    + ", executor policy " + (attempt.InvocationExecutor == null || attempt.InvocationExecutor.ActivationPolicy == null ? "none" : attempt.InvocationExecutor.ActivationPolicy.Status)
                    + ", assembly policy reason " + (attempt.InvocationPreflight == null || attempt.InvocationPreflight.ActivationPolicy == null ? "none" : attempt.InvocationPreflight.ActivationPolicy.AssemblyLoadBlockReasonCode)
                    + ", constructor policy reason " + (attempt.InvocationPreflight == null || attempt.InvocationPreflight.ActivationPolicy == null ? "none" : attempt.InvocationPreflight.ActivationPolicy.ConstructorInvocationBlockReasonCode)
                    + ", Load policy reason " + (attempt.InvocationPreflight == null || attempt.InvocationPreflight.ActivationPolicy == null ? "none" : attempt.InvocationPreflight.ActivationPolicy.LoadInvocationBlockReasonCode)
                    + ", pipeline blocked stages " + (attempt.InvocationPreflight == null ? 0 : attempt.InvocationPreflight.BlockedInvocationPipelineStageCount)
                        + "/" + (attempt.InvocationPreflight == null ? 0 : attempt.InvocationPreflight.RequiredInvocationPipelineStageCount)
                    + ", stage attempts blocked " + attempt.BlockedInvocationStageAttemptCount + "/" + attempt.RecordedInvocationStageAttemptCount
                    + ", executor transitions blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedTransitionCount)
                        + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.TransitionCount)
                    + ", validation only " + attempt.ValidationOnly
                    + ", plan accepted " + attempt.PlanAccepted
                    + ", attempt accepted " + attempt.AttemptAccepted
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-candidate-load-attempt-command");
            if (noInvocation && candidateBacked && invocationPreflighted && activationPolicyBlocked && executorPolicyPropagated && stageAttemptsBlocked && executorBlocked && acceptedButDeferred)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateAssemblyLoadTransitionRequest(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-transition-request-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for an assembly-load transition request.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptAssemblyLoadTransitionRequest(target);
            var request = attempt == null || attempt.InvocationExecutor == null
                ? null
                : attempt.InvocationExecutor.FirstBlockedTransitionRequest;
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var executorStillBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.Available
                && !attempt.InvocationExecutor.ExecutorEnabled
                && !attempt.InvocationExecutor.RuntimePluginLoadingEnabled
                && !attempt.InvocationExecutor.AssemblyLoadTransitionRequestEnabled
                && attempt.InvocationExecutor.ExecutionGateBlocked
                && !attempt.InvocationExecutor.ExecutionGateOpen
                && attempt.InvocationExecutor.TransitionCount == 3
                && attempt.InvocationExecutor.BlockedTransitionCount == 3
                && attempt.InvocationExecutor.OpenTransitionCount == 0
                && attempt.InvocationExecutor.StartedTransitionCount == 0
                && attempt.InvocationExecutor.CompletedTransitionCount == 0
                && attempt.InvocationExecutor.FailedIsolatedTransitionCount == 0
                && !attempt.InvocationExecutor.RollbackRequired;
            var assemblyLoadRequestBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.TransitionRequestCount == 1
                && attempt.InvocationExecutor.AcceptedTransitionRequestCount == 1
                && attempt.InvocationExecutor.BlockedTransitionRequestCount == 1
                && attempt.InvocationExecutor.OpenTransitionRequestCount == 0
                && attempt.InvocationExecutor.StartedTransitionRequestCount == 0
                && attempt.InvocationExecutor.CompletedTransitionRequestCount == 0
                && attempt.InvocationExecutor.AssemblyLoadedTransitionRequestCount == 0
                && request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && string.Equals(request.RequestedStageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.StageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Action, "block-before-assembly-load", StringComparison.OrdinalIgnoreCase)
                && !request.AssemblyLoadTransitionRequestEnabled
                && !request.ExecutorEnabled
                && !request.RuntimePluginLoadingEnabled
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && request.FailureIsolated
                && !request.RollbackRequired
                && string.Equals(request.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var transitionRequestCarriesPolicyReason = request != null
                && !string.IsNullOrWhiteSpace(request.Detail)
                && request.Detail.Contains("Activation policy", StringComparison.Ordinal)
                && request.Detail.Contains("runtime-plugin-loading-disabled", StringComparison.Ordinal);
            var detail = attempt == null
                ? "BepInEx assembly-load transition request did not return an attempt snapshot."
                : "Recorded BepInEx assembly-load transition request for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", executor transitions blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedTransitionCount)
                        + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.TransitionCount)
                    + ", assembly-load requests blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedTransitionRequestCount)
                        + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.TransitionRequestCount)
                    + ", assembly loaded " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadedTransitionRequestCount)
                    + ", request policy reason visible " + transitionRequestCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-transition-request-command");
            if (noInvocation && candidateBacked && acceptedButDeferred && executorStillBlocked && assemblyLoadRequestBlocked && transitionRequestCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateAssemblyLoadResolverPlan(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-resolver-plan-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for an assembly-load resolver plan.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptAssemblyLoadResolverPlanRequest(target);
            var request = attempt == null || attempt.InvocationExecutor == null
                ? null
                : attempt.InvocationExecutor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var executorStillBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.Available
                && !attempt.InvocationExecutor.ExecutorEnabled
                && !attempt.InvocationExecutor.RuntimePluginLoadingEnabled
                && !attempt.InvocationExecutor.AssemblyLoadTransitionRequestEnabled
                && attempt.InvocationExecutor.ExecutionGateBlocked
                && !attempt.InvocationExecutor.ExecutionGateOpen
                && attempt.InvocationExecutor.TransitionCount == 3
                && attempt.InvocationExecutor.BlockedTransitionCount == 3
                && attempt.InvocationExecutor.OpenTransitionCount == 0
                && attempt.InvocationExecutor.StartedTransitionCount == 0
                && attempt.InvocationExecutor.CompletedTransitionCount == 0
                && attempt.InvocationExecutor.FailedIsolatedTransitionCount == 0
                && !attempt.InvocationExecutor.RollbackRequired;
            var assemblyLoadRequestBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.TransitionRequestCount == 1
                && attempt.InvocationExecutor.AcceptedTransitionRequestCount == 1
                && attempt.InvocationExecutor.BlockedTransitionRequestCount == 1
                && attempt.InvocationExecutor.OpenTransitionRequestCount == 0
                && attempt.InvocationExecutor.StartedTransitionRequestCount == 0
                && attempt.InvocationExecutor.CompletedTransitionRequestCount == 0
                && attempt.InvocationExecutor.AssemblyLoadedTransitionRequestCount == 0
                && request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && string.Equals(request.RequestedStageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.StageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Action, "block-before-assembly-load", StringComparison.OrdinalIgnoreCase)
                && !request.AssemblyLoadTransitionRequestEnabled
                && !request.ExecutorEnabled
                && !request.RuntimePluginLoadingEnabled
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && request.FailureIsolated
                && !request.RollbackRequired
                && string.Equals(request.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var resolverPlanBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.AssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.AcceptedAssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.BlockedAssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.OpenAssemblyLoadResolverPlanCount == 0
                && attempt.InvocationExecutor.StartedAssemblyLoadResolverPlanCount == 0
                && attempt.InvocationExecutor.LoadedAssemblyLoadResolverPlanCount == 0
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.AssemblyLoadResolverPlanEnabled
                && !resolverPlan.RuntimePluginLoadingEnabled
                && !resolverPlan.ExecutorEnabled
                && !resolverPlan.AssemblyLoadTransitionRequestEnabled
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.FailureIsolated
                && !resolverPlan.RollbackRequired
                && string.Equals(resolverPlan.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(resolverPlan.AssemblyPath)
                && resolverPlan.AssemblyExists
                && resolverPlan.AssemblyFileSize > 0
                && resolverPlan.ResolverSearchDirectoryCount > 0
                && resolverPlan.DependenciesResolved
                && resolverPlan.AssemblyReferenceCount == resolverPlan.ResolvedAssemblyReferenceCount
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && resolverPlan.FirstUnresolvedAssemblyReference == null
                && string.Equals(resolverPlan.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var transitionRequestCarriesPolicyReason = request != null
                && !string.IsNullOrWhiteSpace(request.Detail)
                && request.Detail.Contains("Activation policy", StringComparison.Ordinal)
                && request.Detail.Contains("runtime-plugin-loading-disabled", StringComparison.Ordinal);
            var resolverPlanCarriesPolicyReason = resolverPlan != null
                && !string.IsNullOrWhiteSpace(resolverPlan.Detail)
                && resolverPlan.Detail.Contains("Activation policy", StringComparison.Ordinal)
                && resolverPlan.Detail.Contains("runtime-plugin-loading-disabled", StringComparison.Ordinal);
            var detail = attempt == null
                ? "BepInEx assembly-load resolver plan did not return an attempt snapshot."
                : "Recorded BepInEx assembly-load resolver plan for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", resolver context " + (resolverPlan == null ? "none" : resolverPlan.ContextName)
                    + ", resolver plans blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadResolverPlanCount)
                        + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadResolverPlanCount)
                    + ", references resolved " + (resolverPlan == null ? 0 : resolverPlan.ResolvedAssemblyReferenceCount)
                        + "/" + (resolverPlan == null ? 0 : resolverPlan.AssemblyReferenceCount)
                    + ", request policy reason visible " + transitionRequestCarriesPolicyReason
                    + ", resolver policy reason visible " + resolverPlanCarriesPolicyReason
                    + ", assembly load started " + (resolverPlan != null && resolverPlan.AssemblyLoadStarted)
                    + ", assembly loaded " + (resolverPlan != null && resolverPlan.AssemblyLoaded)
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-resolver-plan-command");
            if (noInvocation && candidateBacked && acceptedButDeferred && executorStillBlocked && assemblyLoadRequestBlocked && resolverPlanBlocked && transitionRequestCarriesPolicyReason && resolverPlanCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateAssemblyLoadOperation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-operation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for an assembly-load operation.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptAssemblyLoadOperationRequest(target);
            var request = attempt == null || attempt.InvocationExecutor == null
                ? null
                : attempt.InvocationExecutor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var executorStillBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.Available
                && !attempt.InvocationExecutor.ExecutorEnabled
                && !attempt.InvocationExecutor.RuntimePluginLoadingEnabled
                && !attempt.InvocationExecutor.AssemblyLoadTransitionRequestEnabled
                && attempt.InvocationExecutor.ExecutionGateBlocked
                && !attempt.InvocationExecutor.ExecutionGateOpen
                && attempt.InvocationExecutor.TransitionCount == 3
                && attempt.InvocationExecutor.BlockedTransitionCount == 3
                && attempt.InvocationExecutor.OpenTransitionCount == 0
                && attempt.InvocationExecutor.StartedTransitionCount == 0
                && attempt.InvocationExecutor.CompletedTransitionCount == 0
                && attempt.InvocationExecutor.FailedIsolatedTransitionCount == 0
                && !attempt.InvocationExecutor.RollbackRequired;
            var assemblyLoadRequestBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.TransitionRequestCount == 1
                && attempt.InvocationExecutor.AcceptedTransitionRequestCount == 1
                && attempt.InvocationExecutor.BlockedTransitionRequestCount == 1
                && attempt.InvocationExecutor.OpenTransitionRequestCount == 0
                && attempt.InvocationExecutor.StartedTransitionRequestCount == 0
                && attempt.InvocationExecutor.CompletedTransitionRequestCount == 0
                && attempt.InvocationExecutor.AssemblyLoadedTransitionRequestCount == 0
                && request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && string.Equals(request.RequestedStageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.StageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Action, "block-before-assembly-load", StringComparison.OrdinalIgnoreCase)
                && !request.AssemblyLoadTransitionRequestEnabled
                && !request.ExecutorEnabled
                && !request.RuntimePluginLoadingEnabled
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && request.FailureIsolated
                && !request.RollbackRequired
                && string.Equals(request.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var resolverPlanBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.AssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.AcceptedAssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.BlockedAssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.OpenAssemblyLoadResolverPlanCount == 0
                && attempt.InvocationExecutor.StartedAssemblyLoadResolverPlanCount == 0
                && attempt.InvocationExecutor.LoadedAssemblyLoadResolverPlanCount == 0
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.AssemblyLoadResolverPlanEnabled
                && !resolverPlan.RuntimePluginLoadingEnabled
                && !resolverPlan.ExecutorEnabled
                && !resolverPlan.AssemblyLoadTransitionRequestEnabled
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.FailureIsolated
                && !resolverPlan.RollbackRequired
                && resolverPlan.DependenciesResolved
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && string.Equals(resolverPlan.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var operationBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.AssemblyLoadOperationCount == 1
                && attempt.InvocationExecutor.AcceptedAssemblyLoadOperationCount == 1
                && attempt.InvocationExecutor.BlockedAssemblyLoadOperationCount == 1
                && attempt.InvocationExecutor.OpenAssemblyLoadOperationCount == 0
                && attempt.InvocationExecutor.StartedAssemblyLoadOperationCount == 0
                && attempt.InvocationExecutor.CompletedAssemblyLoadOperationCount == 0
                && attempt.InvocationExecutor.LoadedAssemblyLoadOperationCount == 0
                && operation != null
                && operation.Available
                && operation.OperationRequested
                && operation.OperationAccepted
                && !operation.AssemblyLoadOperationEnabled
                && !operation.AssemblyLoadResolverPlanEnabled
                && !operation.RuntimePluginLoadingEnabled
                && !operation.ExecutorEnabled
                && !operation.AssemblyLoadTransitionRequestEnabled
                && !operation.GateOpen
                && operation.GateBlocked
                && !operation.ContextCreated
                && !operation.OperationStarted
                && !operation.OperationCompleted
                && !operation.AssemblyLoaded
                && !operation.AssemblyAlreadyLoaded
                && operation.FailureIsolated
                && !operation.RollbackRequired
                && string.Equals(operation.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.MethodName, InvocationAssemblyLoadOperationMethodName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(operation.ContextIdentity)
                && !string.IsNullOrWhiteSpace(operation.AssemblyPath)
                && operation.AssemblyExists
                && operation.AssemblyFileSize > 0
                && operation.AssemblyReferenceCount == operation.ResolvedAssemblyReferenceCount
                && operation.UnresolvedAssemblyReferenceCount == 0
                && string.Equals(operation.RollbackStrategy, InvocationAssemblyLoadOperationRollbackStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var operationCarriesPolicyReason = DetailCarriesActivationPolicyReason(operation == null ? null : operation.Detail);
            var detail = attempt == null
                ? "BepInEx assembly-load operation did not return an attempt snapshot."
                : "Recorded BepInEx assembly-load operation for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", operation " + (operation == null ? "none" : operation.OperationKind)
                    + ", operations blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadOperationCount)
                        + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadOperationCount)
                    + ", context created " + (operation != null && operation.ContextCreated)
                    + ", operation started " + (operation != null && operation.OperationStarted)
                    + ", operation completed " + (operation != null && operation.OperationCompleted)
                    + ", assembly loaded " + (operation != null && operation.AssemblyLoaded)
                    + ", operation policy reason visible " + operationCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-operation-command");
            if (noInvocation && candidateBacked && acceptedButDeferred && executorStillBlocked && assemblyLoadRequestBlocked && resolverPlanBlocked && operationBlocked && operationCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateAssemblyLoadContextPlan(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-context-plan-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for an assembly-load context allocation plan.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptAssemblyLoadContextPlanRequest(target);
            var request = attempt == null || attempt.InvocationExecutor == null
                ? null
                : attempt.InvocationExecutor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var contextPlan = operation == null ? null : operation.AssemblyLoadContextPlan;
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var executorStillBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.Available
                && !attempt.InvocationExecutor.ExecutorEnabled
                && !attempt.InvocationExecutor.RuntimePluginLoadingEnabled
                && !attempt.InvocationExecutor.AssemblyLoadTransitionRequestEnabled
                && attempt.InvocationExecutor.ExecutionGateBlocked
                && !attempt.InvocationExecutor.ExecutionGateOpen
                && attempt.InvocationExecutor.TransitionCount == 3
                && attempt.InvocationExecutor.BlockedTransitionCount == 3
                && attempt.InvocationExecutor.OpenTransitionCount == 0
                && attempt.InvocationExecutor.StartedTransitionCount == 0
                && attempt.InvocationExecutor.CompletedTransitionCount == 0
                && attempt.InvocationExecutor.FailedIsolatedTransitionCount == 0
                && !attempt.InvocationExecutor.RollbackRequired;
            var assemblyLoadRequestBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.TransitionRequestCount == 1
                && attempt.InvocationExecutor.AcceptedTransitionRequestCount == 1
                && attempt.InvocationExecutor.BlockedTransitionRequestCount == 1
                && attempt.InvocationExecutor.OpenTransitionRequestCount == 0
                && attempt.InvocationExecutor.StartedTransitionRequestCount == 0
                && attempt.InvocationExecutor.CompletedTransitionRequestCount == 0
                && attempt.InvocationExecutor.AssemblyLoadedTransitionRequestCount == 0
                && request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && string.Equals(request.RequestedStageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.StageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Action, "block-before-assembly-load", StringComparison.OrdinalIgnoreCase)
                && !request.AssemblyLoadTransitionRequestEnabled
                && !request.ExecutorEnabled
                && !request.RuntimePluginLoadingEnabled
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && request.FailureIsolated
                && !request.RollbackRequired
                && string.Equals(request.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var resolverPlanBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.AssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.AcceptedAssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.BlockedAssemblyLoadResolverPlanCount == 1
                && attempt.InvocationExecutor.OpenAssemblyLoadResolverPlanCount == 0
                && attempt.InvocationExecutor.StartedAssemblyLoadResolverPlanCount == 0
                && attempt.InvocationExecutor.LoadedAssemblyLoadResolverPlanCount == 0
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.AssemblyLoadResolverPlanEnabled
                && !resolverPlan.RuntimePluginLoadingEnabled
                && !resolverPlan.ExecutorEnabled
                && !resolverPlan.AssemblyLoadTransitionRequestEnabled
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.FailureIsolated
                && !resolverPlan.RollbackRequired
                && resolverPlan.DependenciesResolved
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && string.Equals(resolverPlan.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var operationBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.AssemblyLoadOperationCount == 1
                && attempt.InvocationExecutor.AcceptedAssemblyLoadOperationCount == 1
                && attempt.InvocationExecutor.BlockedAssemblyLoadOperationCount == 1
                && attempt.InvocationExecutor.OpenAssemblyLoadOperationCount == 0
                && attempt.InvocationExecutor.StartedAssemblyLoadOperationCount == 0
                && attempt.InvocationExecutor.CompletedAssemblyLoadOperationCount == 0
                && attempt.InvocationExecutor.LoadedAssemblyLoadOperationCount == 0
                && operation != null
                && operation.Available
                && operation.OperationRequested
                && operation.OperationAccepted
                && !operation.AssemblyLoadOperationEnabled
                && !operation.AssemblyLoadResolverPlanEnabled
                && !operation.RuntimePluginLoadingEnabled
                && !operation.ExecutorEnabled
                && !operation.AssemblyLoadTransitionRequestEnabled
                && !operation.GateOpen
                && operation.GateBlocked
                && !operation.ContextCreated
                && !operation.OperationStarted
                && !operation.OperationCompleted
                && !operation.AssemblyLoaded
                && !operation.AssemblyAlreadyLoaded
                && operation.FailureIsolated
                && !operation.RollbackRequired
                && string.Equals(operation.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.MethodName, InvocationAssemblyLoadOperationMethodName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(operation.ContextIdentity)
                && !string.IsNullOrWhiteSpace(operation.AssemblyPath)
                && operation.AssemblyExists
                && operation.AssemblyFileSize > 0
                && operation.AssemblyReferenceCount == operation.ResolvedAssemblyReferenceCount
                && operation.UnresolvedAssemblyReferenceCount == 0
                && string.Equals(operation.RollbackStrategy, InvocationAssemblyLoadOperationRollbackStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var contextPlanBlocked = attempt != null
                && attempt.InvocationExecutor != null
                && attempt.InvocationExecutor.AssemblyLoadContextPlanCount == 1
                && attempt.InvocationExecutor.AcceptedAssemblyLoadContextPlanCount == 1
                && attempt.InvocationExecutor.BlockedAssemblyLoadContextPlanCount == 1
                && attempt.InvocationExecutor.OpenAssemblyLoadContextPlanCount == 0
                && attempt.InvocationExecutor.CreatedAssemblyLoadContextPlanCount == 0
                && contextPlan != null
                && contextPlan.Available
                && contextPlan.PlanRequested
                && contextPlan.PlanAccepted
                && !contextPlan.AssemblyLoadContextPlanEnabled
                && !contextPlan.AssemblyLoadOperationEnabled
                && !contextPlan.AssemblyLoadResolverPlanEnabled
                && !contextPlan.RuntimePluginLoadingEnabled
                && !contextPlan.ExecutorEnabled
                && !contextPlan.AssemblyLoadTransitionRequestEnabled
                && !contextPlan.GateOpen
                && contextPlan.GateBlocked
                && contextPlan.ContextAllocationPlanned
                && !contextPlan.ContextCreated
                && contextPlan.CollectibleContextPlanned
                && contextPlan.DedicatedContextPlanned
                && contextPlan.DefaultContextBlocked
                && contextPlan.FailureIsolated
                && !contextPlan.RollbackRequired
                && string.Equals(contextPlan.AllocationStrategy, InvocationAssemblyLoadContextAllocationStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ContextIdentity, operation == null ? "" : operation.ContextIdentity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(contextPlan.AssemblyPath)
                && string.Equals(contextPlan.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var contextPlanCarriesPolicyReason = DetailCarriesActivationPolicyReason(contextPlan == null ? null : contextPlan.Detail);
            var detail = attempt == null
                ? "BepInEx assembly-load context allocation plan did not return an attempt snapshot."
                : "Recorded BepInEx assembly-load context plan for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", context plans blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadContextPlanCount)
                        + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadContextPlanCount)
                    + ", allocation strategy " + (contextPlan == null ? "none" : contextPlan.AllocationStrategy)
                    + ", context created " + (contextPlan != null && contextPlan.ContextCreated)
                    + ", operation started " + (operation != null && operation.OperationStarted)
                    + ", assembly loaded " + (operation != null && operation.AssemblyLoaded)
                    + ", context plan policy reason visible " + contextPlanCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-context-plan-command");
            if (noInvocation && candidateBacked && acceptedButDeferred && executorStillBlocked && assemblyLoadRequestBlocked && resolverPlanBlocked && operationBlocked && contextPlanBlocked && contextPlanCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateAssemblyLoadContextAllocation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-context-allocation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for an AssemblyLoadContext allocation attempt.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptAssemblyLoadContextAllocationRequest(target);
            var executor = attempt == null ? null : attempt.InvocationExecutor;
            var request = executor == null ? null : executor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var contextPlan = operation == null ? null : operation.AssemblyLoadContextPlan;
            var allocation = contextPlan == null ? null : contextPlan.AssemblyLoadContextAllocation;
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var executorBlocked = executor != null
                && executor.Available
                && !executor.ExecutorEnabled
                && !executor.RuntimePluginLoadingEnabled
                && !executor.AssemblyLoadTransitionRequestEnabled
                && executor.ExecutionGateBlocked
                && !executor.ExecutionGateOpen
                && executor.TransitionCount == 3
                && executor.BlockedTransitionCount == 3
                && executor.OpenTransitionCount == 0
                && executor.StartedTransitionCount == 0
                && executor.CompletedTransitionCount == 0
                && !executor.RollbackRequired;
            var requestBlocked = executor != null
                && executor.TransitionRequestCount == 1
                && executor.AcceptedTransitionRequestCount == 1
                && executor.BlockedTransitionRequestCount == 1
                && executor.OpenTransitionRequestCount == 0
                && executor.StartedTransitionRequestCount == 0
                && executor.CompletedTransitionRequestCount == 0
                && executor.AssemblyLoadedTransitionRequestCount == 0
                && request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && string.Equals(request.RequestedStageKind, InvocationAssemblyLoadTransitionRequestStageKind, StringComparison.OrdinalIgnoreCase)
                && !request.AssemblyLoadTransitionRequestEnabled
                && !request.ExecutorEnabled
                && !request.RuntimePluginLoadingEnabled
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && request.FailureIsolated
                && !request.RollbackRequired
                && string.Equals(request.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var resolverPlanBlocked = executor != null
                && executor.AssemblyLoadResolverPlanCount == 1
                && executor.AcceptedAssemblyLoadResolverPlanCount == 1
                && executor.BlockedAssemblyLoadResolverPlanCount == 1
                && executor.OpenAssemblyLoadResolverPlanCount == 0
                && executor.StartedAssemblyLoadResolverPlanCount == 0
                && executor.LoadedAssemblyLoadResolverPlanCount == 0
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.AssemblyLoadResolverPlanEnabled
                && !resolverPlan.RuntimePluginLoadingEnabled
                && !resolverPlan.ExecutorEnabled
                && !resolverPlan.AssemblyLoadTransitionRequestEnabled
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.FailureIsolated
                && !resolverPlan.RollbackRequired
                && resolverPlan.DependenciesResolved
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && string.Equals(resolverPlan.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var operationBlocked = executor != null
                && executor.AssemblyLoadOperationCount == 1
                && executor.AcceptedAssemblyLoadOperationCount == 1
                && executor.BlockedAssemblyLoadOperationCount == 1
                && executor.OpenAssemblyLoadOperationCount == 0
                && executor.StartedAssemblyLoadOperationCount == 0
                && executor.CompletedAssemblyLoadOperationCount == 0
                && executor.LoadedAssemblyLoadOperationCount == 0
                && operation != null
                && operation.Available
                && operation.OperationRequested
                && operation.OperationAccepted
                && !operation.AssemblyLoadOperationEnabled
                && !operation.AssemblyLoadResolverPlanEnabled
                && !operation.RuntimePluginLoadingEnabled
                && !operation.ExecutorEnabled
                && !operation.AssemblyLoadTransitionRequestEnabled
                && !operation.GateOpen
                && operation.GateBlocked
                && !operation.ContextCreated
                && !operation.OperationStarted
                && !operation.OperationCompleted
                && !operation.AssemblyLoaded
                && !operation.AssemblyAlreadyLoaded
                && operation.FailureIsolated
                && !operation.RollbackRequired
                && string.Equals(operation.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.MethodName, InvocationAssemblyLoadOperationMethodName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(operation.ContextIdentity)
                && !string.IsNullOrWhiteSpace(operation.AssemblyPath)
                && operation.AssemblyExists
                && operation.AssemblyFileSize > 0
                && operation.AssemblyReferenceCount == operation.ResolvedAssemblyReferenceCount
                && operation.UnresolvedAssemblyReferenceCount == 0
                && string.Equals(operation.RollbackStrategy, InvocationAssemblyLoadOperationRollbackStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var contextPlanBlocked = executor != null
                && executor.AssemblyLoadContextPlanCount == 1
                && executor.AcceptedAssemblyLoadContextPlanCount == 1
                && executor.BlockedAssemblyLoadContextPlanCount == 1
                && executor.OpenAssemblyLoadContextPlanCount == 0
                && executor.CreatedAssemblyLoadContextPlanCount == 0
                && contextPlan != null
                && contextPlan.Available
                && contextPlan.PlanRequested
                && contextPlan.PlanAccepted
                && !contextPlan.AssemblyLoadContextPlanEnabled
                && !contextPlan.AssemblyLoadOperationEnabled
                && !contextPlan.AssemblyLoadResolverPlanEnabled
                && !contextPlan.RuntimePluginLoadingEnabled
                && !contextPlan.ExecutorEnabled
                && !contextPlan.AssemblyLoadTransitionRequestEnabled
                && !contextPlan.GateOpen
                && contextPlan.GateBlocked
                && contextPlan.ContextAllocationPlanned
                && !contextPlan.ContextCreated
                && contextPlan.CollectibleContextPlanned
                && contextPlan.DedicatedContextPlanned
                && contextPlan.DefaultContextBlocked
                && contextPlan.FailureIsolated
                && !contextPlan.RollbackRequired
                && string.Equals(contextPlan.AllocationStrategy, InvocationAssemblyLoadContextAllocationStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ContextIdentity, operation == null ? "" : operation.ContextIdentity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(contextPlan.AssemblyPath)
                && string.Equals(contextPlan.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var allocationBlocked = executor != null
                && executor.AssemblyLoadContextAllocationCount == 1
                && executor.AcceptedAssemblyLoadContextAllocationCount == 1
                && executor.BlockedAssemblyLoadContextAllocationCount == 1
                && executor.OpenAssemblyLoadContextAllocationCount == 0
                && executor.ConstructorCalledAssemblyLoadContextAllocationCount == 0
                && executor.CreatedAssemblyLoadContextAllocationCount == 0
                && allocation != null
                && allocation.Available
                && allocation.AllocationRequested
                && allocation.AllocationAccepted
                && !allocation.AssemblyLoadContextAllocationEnabled
                && !allocation.AssemblyLoadContextPlanEnabled
                && !allocation.AssemblyLoadOperationEnabled
                && !allocation.AssemblyLoadResolverPlanEnabled
                && !allocation.RuntimePluginLoadingEnabled
                && !allocation.ExecutorEnabled
                && !allocation.AssemblyLoadTransitionRequestEnabled
                && !allocation.GateOpen
                && allocation.GateBlocked
                && allocation.ConstructorPlanned
                && !allocation.ConstructorCalled
                && !allocation.ContextCreated
                && !allocation.ContextTracked
                && !allocation.UnloadRegistered
                && allocation.CollectibleContextPlanned
                && allocation.DedicatedContextPlanned
                && allocation.DefaultContextBlocked
                && allocation.FailureIsolated
                && !allocation.RollbackRequired
                && string.Equals(allocation.AllocationStrategy, InvocationAssemblyLoadContextAllocationStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ConstructorSignature, InvocationAssemblyLoadContextConstructorSignature, StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ContextIdentity, contextPlan == null ? "" : contextPlan.ContextIdentity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(allocation.AssemblyPath)
                && string.Equals(allocation.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var allocationCarriesPolicyReason = DetailCarriesActivationPolicyReason(allocation == null ? null : allocation.Detail);
            var detail = attempt == null
                ? "BepInEx AssemblyLoadContext allocation attempt did not return an attempt snapshot."
                : "Recorded BepInEx AssemblyLoadContext allocation attempt for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", allocations blocked " + (executor == null ? 0 : executor.BlockedAssemblyLoadContextAllocationCount)
                        + "/" + (executor == null ? 0 : executor.AssemblyLoadContextAllocationCount)
                    + ", constructor planned " + (allocation != null && allocation.ConstructorPlanned)
                    + ", constructor called " + (allocation != null && allocation.ConstructorCalled)
                    + ", context created " + (allocation != null && allocation.ContextCreated)
                    + ", context tracked " + (allocation != null && allocation.ContextTracked)
                    + ", unload registered " + (allocation != null && allocation.UnloadRegistered)
                    + ", assembly loaded " + (operation != null && operation.AssemblyLoaded)
                    + ", allocation policy reason visible " + allocationCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-context-allocation-command");
            if (noInvocation && candidateBacked && acceptedButDeferred && executorBlocked && requestBlocked && resolverPlanBlocked && operationBlocked && contextPlanBlocked && allocationBlocked && allocationCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateAssemblyLoadContextRegistration(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-context-registration-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for an AssemblyLoadContext registration boundary.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptAssemblyLoadContextRegistrationRequest(target);
            var executor = attempt == null ? null : attempt.InvocationExecutor;
            var request = executor == null ? null : executor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var contextPlan = operation == null ? null : operation.AssemblyLoadContextPlan;
            var allocation = contextPlan == null ? null : contextPlan.AssemblyLoadContextAllocation;
            var registration = allocation == null ? null : allocation.AssemblyLoadContextRegistration;
            var noInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var executorBlocked = executor != null
                && executor.Available
                && !executor.ExecutorEnabled
                && !executor.RuntimePluginLoadingEnabled
                && !executor.AssemblyLoadTransitionRequestEnabled
                && executor.ExecutionGateBlocked
                && !executor.ExecutionGateOpen
                && executor.TransitionCount == 3
                && executor.BlockedTransitionCount == 3
                && executor.OpenTransitionCount == 0
                && executor.StartedTransitionCount == 0
                && executor.CompletedTransitionCount == 0
                && executor.TransitionRequestCount == 1
                && executor.AcceptedTransitionRequestCount == 1
                && executor.BlockedTransitionRequestCount == 1
                && executor.OpenTransitionRequestCount == 0
                && executor.StartedTransitionRequestCount == 0
                && executor.CompletedTransitionRequestCount == 0
                && executor.AssemblyLoadedTransitionRequestCount == 0
                && !executor.RollbackRequired;
            var resolverAndOperationBlocked = executor != null
                && executor.AssemblyLoadResolverPlanCount == 1
                && executor.AcceptedAssemblyLoadResolverPlanCount == 1
                && executor.BlockedAssemblyLoadResolverPlanCount == 1
                && executor.OpenAssemblyLoadResolverPlanCount == 0
                && executor.StartedAssemblyLoadResolverPlanCount == 0
                && executor.LoadedAssemblyLoadResolverPlanCount == 0
                && executor.AssemblyLoadOperationCount == 1
                && executor.AcceptedAssemblyLoadOperationCount == 1
                && executor.BlockedAssemblyLoadOperationCount == 1
                && executor.OpenAssemblyLoadOperationCount == 0
                && executor.StartedAssemblyLoadOperationCount == 0
                && executor.CompletedAssemblyLoadOperationCount == 0
                && executor.LoadedAssemblyLoadOperationCount == 0
                && request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.DependenciesResolved
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && operation != null
                && operation.Available
                && operation.OperationRequested
                && operation.OperationAccepted
                && !operation.GateOpen
                && operation.GateBlocked
                && !operation.ContextCreated
                && !operation.OperationStarted
                && !operation.OperationCompleted
                && !operation.AssemblyLoaded
                && !operation.AssemblyAlreadyLoaded
                && operation.AssemblyExists
                && operation.AssemblyFileSize > 0
                && operation.AssemblyReferenceCount == operation.ResolvedAssemblyReferenceCount
                && operation.UnresolvedAssemblyReferenceCount == 0
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var contextAndAllocationBlocked = executor != null
                && executor.AssemblyLoadContextPlanCount == 1
                && executor.AcceptedAssemblyLoadContextPlanCount == 1
                && executor.BlockedAssemblyLoadContextPlanCount == 1
                && executor.OpenAssemblyLoadContextPlanCount == 0
                && executor.CreatedAssemblyLoadContextPlanCount == 0
                && executor.AssemblyLoadContextAllocationCount == 1
                && executor.AcceptedAssemblyLoadContextAllocationCount == 1
                && executor.BlockedAssemblyLoadContextAllocationCount == 1
                && executor.OpenAssemblyLoadContextAllocationCount == 0
                && executor.ConstructorCalledAssemblyLoadContextAllocationCount == 0
                && executor.CreatedAssemblyLoadContextAllocationCount == 0
                && contextPlan != null
                && contextPlan.Available
                && contextPlan.PlanRequested
                && contextPlan.PlanAccepted
                && !contextPlan.AssemblyLoadContextPlanEnabled
                && !contextPlan.AssemblyLoadOperationEnabled
                && !contextPlan.AssemblyLoadResolverPlanEnabled
                && !contextPlan.RuntimePluginLoadingEnabled
                && !contextPlan.ExecutorEnabled
                && !contextPlan.AssemblyLoadTransitionRequestEnabled
                && !contextPlan.GateOpen
                && contextPlan.GateBlocked
                && contextPlan.ContextAllocationPlanned
                && !contextPlan.ContextCreated
                && contextPlan.CollectibleContextPlanned
                && contextPlan.DedicatedContextPlanned
                && contextPlan.DefaultContextBlocked
                && contextPlan.FailureIsolated
                && !contextPlan.RollbackRequired
                && allocation != null
                && allocation.Available
                && allocation.AllocationRequested
                && allocation.AllocationAccepted
                && !allocation.AssemblyLoadContextAllocationEnabled
                && !allocation.AssemblyLoadContextPlanEnabled
                && !allocation.AssemblyLoadOperationEnabled
                && !allocation.AssemblyLoadResolverPlanEnabled
                && !allocation.RuntimePluginLoadingEnabled
                && !allocation.ExecutorEnabled
                && !allocation.AssemblyLoadTransitionRequestEnabled
                && !allocation.GateOpen
                && allocation.GateBlocked
                && allocation.ConstructorPlanned
                && !allocation.ConstructorCalled
                && !allocation.ContextCreated
                && !allocation.ContextTracked
                && !allocation.UnloadRegistered
                && allocation.CollectibleContextPlanned
                && allocation.DedicatedContextPlanned
                && allocation.DefaultContextBlocked
                && allocation.FailureIsolated
                && !allocation.RollbackRequired
                && string.Equals(contextPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var registrationBlocked = executor != null
                && executor.AssemblyLoadContextRegistrationCount == 1
                && executor.AcceptedAssemblyLoadContextRegistrationCount == 1
                && executor.BlockedAssemblyLoadContextRegistrationCount == 1
                && executor.OpenAssemblyLoadContextRegistrationCount == 0
                && executor.TrackedAssemblyLoadContextRegistrationCount == 0
                && executor.UnloadRegisteredAssemblyLoadContextRegistrationCount == 0
                && registration != null
                && registration.Available
                && registration.RegistrationRequested
                && registration.RegistrationAccepted
                && !registration.AssemblyLoadContextRegistrationEnabled
                && !registration.AssemblyLoadContextAllocationEnabled
                && !registration.AssemblyLoadContextPlanEnabled
                && !registration.AssemblyLoadOperationEnabled
                && !registration.AssemblyLoadResolverPlanEnabled
                && !registration.RuntimePluginLoadingEnabled
                && !registration.ExecutorEnabled
                && !registration.AssemblyLoadTransitionRequestEnabled
                && !registration.GateOpen
                && registration.GateBlocked
                && registration.ConstructorResultRequired
                && !registration.ConstructorResultAvailable
                && !registration.ConstructorCalled
                && !registration.ContextCreated
                && !registration.ContextTracked
                && !registration.UnloadRegistered
                && registration.WeakReferencePlanned
                && !registration.WeakReferenceCreated
                && !registration.OwnershipRegistered
                && registration.FailureIsolated
                && !registration.RollbackRequired
                && string.Equals(registration.RegistrationStrategy, InvocationAssemblyLoadContextRegistrationStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.TrackingCollection, InvocationAssemblyLoadContextTrackingCollection, StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.ContextIdentity, allocation == null ? "" : allocation.ContextIdentity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(registration.AssemblyPath)
                && string.Equals(registration.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var registrationCarriesPolicyReason = DetailCarriesActivationPolicyReason(registration == null ? null : registration.Detail);
            var detail = attempt == null
                ? "BepInEx AssemblyLoadContext registration boundary did not return an attempt snapshot."
                : "Recorded BepInEx AssemblyLoadContext registration boundary for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", registrations blocked " + (executor == null ? 0 : executor.BlockedAssemblyLoadContextRegistrationCount)
                        + "/" + (executor == null ? 0 : executor.AssemblyLoadContextRegistrationCount)
                    + ", constructor result available " + (registration != null && registration.ConstructorResultAvailable)
                    + ", weak reference created " + (registration != null && registration.WeakReferenceCreated)
                    + ", context tracked " + (registration != null && registration.ContextTracked)
                    + ", unload registered " + (registration != null && registration.UnloadRegistered)
                    + ", assembly loaded " + (operation != null && operation.AssemblyLoaded)
                    + ", registration policy reason visible " + registrationCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-context-registration-command");
            if (noInvocation && candidateBacked && acceptedButDeferred && executorBlocked && resolverAndOperationBlocked && contextAndAllocationBlocked && registrationBlocked && registrationCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateAssemblyLoadInvocation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-invocation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for an assembly-load invocation boundary.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptAssemblyLoadInvocationRequest(target);
            var executor = attempt == null ? null : attempt.InvocationExecutor;
            var request = executor == null ? null : executor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var contextPlan = operation == null ? null : operation.AssemblyLoadContextPlan;
            var allocation = contextPlan == null ? null : contextPlan.AssemblyLoadContextAllocation;
            var registration = allocation == null ? null : allocation.AssemblyLoadContextRegistration;
            var invocation = registration == null ? null : registration.AssemblyLoadInvocation;
            var noPluginInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var upstreamBlocked = executor != null
                && executor.Available
                && !executor.ExecutorEnabled
                && !executor.RuntimePluginLoadingEnabled
                && !executor.AssemblyLoadTransitionRequestEnabled
                && executor.ExecutionGateBlocked
                && !executor.ExecutionGateOpen
                && executor.TransitionCount == 3
                && executor.BlockedTransitionCount == 3
                && executor.OpenTransitionCount == 0
                && executor.StartedTransitionCount == 0
                && executor.CompletedTransitionCount == 0
                && executor.TransitionRequestCount == 1
                && executor.AcceptedTransitionRequestCount == 1
                && executor.BlockedTransitionRequestCount == 1
                && executor.OpenTransitionRequestCount == 0
                && executor.StartedTransitionRequestCount == 0
                && executor.CompletedTransitionRequestCount == 0
                && executor.AssemblyLoadedTransitionRequestCount == 0
                && executor.AssemblyLoadResolverPlanCount == 1
                && executor.AcceptedAssemblyLoadResolverPlanCount == 1
                && executor.BlockedAssemblyLoadResolverPlanCount == 1
                && executor.OpenAssemblyLoadResolverPlanCount == 0
                && executor.StartedAssemblyLoadResolverPlanCount == 0
                && executor.LoadedAssemblyLoadResolverPlanCount == 0
                && executor.AssemblyLoadOperationCount == 1
                && executor.AcceptedAssemblyLoadOperationCount == 1
                && executor.BlockedAssemblyLoadOperationCount == 1
                && executor.OpenAssemblyLoadOperationCount == 0
                && executor.StartedAssemblyLoadOperationCount == 0
                && executor.CompletedAssemblyLoadOperationCount == 0
                && executor.LoadedAssemblyLoadOperationCount == 0
                && executor.AssemblyLoadContextPlanCount == 1
                && executor.AcceptedAssemblyLoadContextPlanCount == 1
                && executor.BlockedAssemblyLoadContextPlanCount == 1
                && executor.OpenAssemblyLoadContextPlanCount == 0
                && executor.CreatedAssemblyLoadContextPlanCount == 0
                && executor.AssemblyLoadContextAllocationCount == 1
                && executor.AcceptedAssemblyLoadContextAllocationCount == 1
                && executor.BlockedAssemblyLoadContextAllocationCount == 1
                && executor.OpenAssemblyLoadContextAllocationCount == 0
                && executor.ConstructorCalledAssemblyLoadContextAllocationCount == 0
                && executor.CreatedAssemblyLoadContextAllocationCount == 0
                && executor.AssemblyLoadContextRegistrationCount == 1
                && executor.AcceptedAssemblyLoadContextRegistrationCount == 1
                && executor.BlockedAssemblyLoadContextRegistrationCount == 1
                && executor.OpenAssemblyLoadContextRegistrationCount == 0
                && executor.TrackedAssemblyLoadContextRegistrationCount == 0
                && executor.UnloadRegisteredAssemblyLoadContextRegistrationCount == 0
                && !executor.RollbackRequired;
            var chainSnapshotsBlocked = request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.DependenciesResolved
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && operation != null
                && operation.Available
                && operation.OperationRequested
                && operation.OperationAccepted
                && !operation.GateOpen
                && operation.GateBlocked
                && !operation.ContextCreated
                && !operation.OperationStarted
                && !operation.OperationCompleted
                && !operation.AssemblyLoaded
                && !operation.AssemblyAlreadyLoaded
                && operation.AssemblyExists
                && operation.AssemblyFileSize > 0
                && operation.AssemblyReferenceCount == operation.ResolvedAssemblyReferenceCount
                && operation.UnresolvedAssemblyReferenceCount == 0
                && contextPlan != null
                && contextPlan.Available
                && contextPlan.PlanRequested
                && contextPlan.PlanAccepted
                && !contextPlan.GateOpen
                && contextPlan.GateBlocked
                && !contextPlan.ContextCreated
                && allocation != null
                && allocation.Available
                && allocation.AllocationRequested
                && allocation.AllocationAccepted
                && !allocation.GateOpen
                && allocation.GateBlocked
                && allocation.ConstructorPlanned
                && !allocation.ConstructorCalled
                && !allocation.ContextCreated
                && !allocation.ContextTracked
                && !allocation.UnloadRegistered
                && registration != null
                && registration.Available
                && registration.RegistrationRequested
                && registration.RegistrationAccepted
                && !registration.GateOpen
                && registration.GateBlocked
                && registration.ConstructorResultRequired
                && !registration.ConstructorResultAvailable
                && !registration.ConstructorCalled
                && !registration.ContextCreated
                && !registration.ContextTracked
                && !registration.UnloadRegistered
                && registration.WeakReferencePlanned
                && !registration.WeakReferenceCreated
                && !registration.OwnershipRegistered
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var invocationBlocked = executor != null
                && executor.AssemblyLoadInvocationCount == 1
                && executor.AcceptedAssemblyLoadInvocationCount == 1
                && executor.BlockedAssemblyLoadInvocationCount == 1
                && executor.OpenAssemblyLoadInvocationCount == 0
                && executor.StartedAssemblyLoadInvocationCount == 0
                && executor.CompletedAssemblyLoadInvocationCount == 0
                && executor.LoadedAssemblyLoadInvocationCount == 0
                && invocation != null
                && invocation.Available
                && invocation.InvocationRequested
                && invocation.InvocationAccepted
                && !invocation.AssemblyLoadInvocationEnabled
                && !invocation.AssemblyLoadContextRegistrationEnabled
                && !invocation.AssemblyLoadContextAllocationEnabled
                && !invocation.AssemblyLoadContextPlanEnabled
                && !invocation.AssemblyLoadOperationEnabled
                && !invocation.AssemblyLoadResolverPlanEnabled
                && !invocation.RuntimePluginLoadingEnabled
                && !invocation.ExecutorEnabled
                && !invocation.AssemblyLoadTransitionRequestEnabled
                && !invocation.GateOpen
                && invocation.GateBlocked
                && invocation.RegisteredContextRequired
                && !invocation.RegisteredContextAvailable
                && !invocation.ContextTracked
                && invocation.InvocationPlanned
                && !invocation.InvocationStarted
                && !invocation.InvocationCompleted
                && !invocation.AssemblyLoaded
                && !invocation.LoadResultAvailable
                && invocation.FailureIsolated
                && !invocation.RollbackRequired
                && string.Equals(invocation.InvocationStrategy, InvocationAssemblyLoadInvocationStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(invocation.MethodName, InvocationAssemblyLoadOperationMethodName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(invocation.ContextName, InvocationAssemblyLoadResolverPlanContextName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(invocation.ContextIdentity, registration == null ? "" : registration.ContextIdentity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(invocation.OperationKind, InvocationAssemblyLoadOperationKind, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(invocation.AssemblyPath)
                && string.IsNullOrWhiteSpace(invocation.LoadedAssemblyName)
                && string.IsNullOrWhiteSpace(invocation.LoadedAssemblyPath)
                && string.Equals(invocation.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(invocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var invocationCarriesPolicyReason = DetailCarriesActivationPolicyReason(invocation == null ? null : invocation.Detail);
            var detail = attempt == null
                ? "BepInEx assembly-load invocation boundary did not return an attempt snapshot."
                : "Recorded BepInEx assembly-load invocation boundary for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", load invocations blocked " + (executor == null ? 0 : executor.BlockedAssemblyLoadInvocationCount)
                        + "/" + (executor == null ? 0 : executor.AssemblyLoadInvocationCount)
                    + ", invocation planned " + (invocation != null && invocation.InvocationPlanned)
                    + ", invocation started " + (invocation != null && invocation.InvocationStarted)
                    + ", invocation completed " + (invocation != null && invocation.InvocationCompleted)
                    + ", load result available " + (invocation != null && invocation.LoadResultAvailable)
                    + ", assembly loaded " + (invocation != null && invocation.AssemblyLoaded)
                    + ", invocation policy reason visible " + invocationCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-assembly-load-invocation-command");
            if (noPluginInvocation && candidateBacked && acceptedButDeferred && upstreamBlocked && chainSnapshotsBlocked && invocationBlocked && invocationCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidatePluginTypeResolution(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-plugin-type-resolution-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for a plugin type resolution boundary.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptPluginTypeResolutionRequest(target);
            var executor = attempt == null ? null : attempt.InvocationExecutor;
            var request = executor == null ? null : executor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var contextPlan = operation == null ? null : operation.AssemblyLoadContextPlan;
            var allocation = contextPlan == null ? null : contextPlan.AssemblyLoadContextAllocation;
            var registration = allocation == null ? null : allocation.AssemblyLoadContextRegistration;
            var invocation = registration == null ? null : registration.AssemblyLoadInvocation;
            var typeResolution = invocation == null ? null : invocation.PluginTypeResolution;
            var noPluginInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var upstreamBlocked = executor != null
                && executor.Available
                && !executor.ExecutorEnabled
                && !executor.RuntimePluginLoadingEnabled
                && !executor.AssemblyLoadTransitionRequestEnabled
                && executor.ExecutionGateBlocked
                && !executor.ExecutionGateOpen
                && executor.TransitionCount == 3
                && executor.BlockedTransitionCount == 3
                && executor.OpenTransitionCount == 0
                && executor.StartedTransitionCount == 0
                && executor.CompletedTransitionCount == 0
                && executor.TransitionRequestCount == 1
                && executor.AcceptedTransitionRequestCount == 1
                && executor.BlockedTransitionRequestCount == 1
                && executor.OpenTransitionRequestCount == 0
                && executor.StartedTransitionRequestCount == 0
                && executor.CompletedTransitionRequestCount == 0
                && executor.AssemblyLoadedTransitionRequestCount == 0
                && executor.AssemblyLoadResolverPlanCount == 1
                && executor.AcceptedAssemblyLoadResolverPlanCount == 1
                && executor.BlockedAssemblyLoadResolverPlanCount == 1
                && executor.OpenAssemblyLoadResolverPlanCount == 0
                && executor.StartedAssemblyLoadResolverPlanCount == 0
                && executor.LoadedAssemblyLoadResolverPlanCount == 0
                && executor.AssemblyLoadOperationCount == 1
                && executor.AcceptedAssemblyLoadOperationCount == 1
                && executor.BlockedAssemblyLoadOperationCount == 1
                && executor.OpenAssemblyLoadOperationCount == 0
                && executor.StartedAssemblyLoadOperationCount == 0
                && executor.CompletedAssemblyLoadOperationCount == 0
                && executor.LoadedAssemblyLoadOperationCount == 0
                && executor.AssemblyLoadContextPlanCount == 1
                && executor.AcceptedAssemblyLoadContextPlanCount == 1
                && executor.BlockedAssemblyLoadContextPlanCount == 1
                && executor.OpenAssemblyLoadContextPlanCount == 0
                && executor.CreatedAssemblyLoadContextPlanCount == 0
                && executor.AssemblyLoadContextAllocationCount == 1
                && executor.AcceptedAssemblyLoadContextAllocationCount == 1
                && executor.BlockedAssemblyLoadContextAllocationCount == 1
                && executor.OpenAssemblyLoadContextAllocationCount == 0
                && executor.ConstructorCalledAssemblyLoadContextAllocationCount == 0
                && executor.CreatedAssemblyLoadContextAllocationCount == 0
                && executor.AssemblyLoadContextRegistrationCount == 1
                && executor.AcceptedAssemblyLoadContextRegistrationCount == 1
                && executor.BlockedAssemblyLoadContextRegistrationCount == 1
                && executor.OpenAssemblyLoadContextRegistrationCount == 0
                && executor.TrackedAssemblyLoadContextRegistrationCount == 0
                && executor.UnloadRegisteredAssemblyLoadContextRegistrationCount == 0
                && executor.AssemblyLoadInvocationCount == 1
                && executor.AcceptedAssemblyLoadInvocationCount == 1
                && executor.BlockedAssemblyLoadInvocationCount == 1
                && executor.OpenAssemblyLoadInvocationCount == 0
                && executor.StartedAssemblyLoadInvocationCount == 0
                && executor.CompletedAssemblyLoadInvocationCount == 0
                && executor.LoadedAssemblyLoadInvocationCount == 0
                && executor.PluginTypeResolutionCount == 1
                && executor.AcceptedPluginTypeResolutionCount == 1
                && executor.BlockedPluginTypeResolutionCount == 1
                && executor.OpenPluginTypeResolutionCount == 0
                && executor.StartedPluginTypeResolutionCount == 0
                && executor.CompletedPluginTypeResolutionCount == 0
                && executor.ResolvedPluginTypeResolutionCount == 0
                && !executor.RollbackRequired;
            var chainSnapshotsBlocked = request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.DependenciesResolved
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && operation != null
                && operation.Available
                && operation.OperationRequested
                && operation.OperationAccepted
                && !operation.GateOpen
                && operation.GateBlocked
                && !operation.ContextCreated
                && !operation.OperationStarted
                && !operation.OperationCompleted
                && !operation.AssemblyLoaded
                && !operation.AssemblyAlreadyLoaded
                && operation.AssemblyExists
                && operation.AssemblyFileSize > 0
                && operation.AssemblyReferenceCount == operation.ResolvedAssemblyReferenceCount
                && operation.UnresolvedAssemblyReferenceCount == 0
                && contextPlan != null
                && contextPlan.Available
                && contextPlan.PlanRequested
                && contextPlan.PlanAccepted
                && !contextPlan.GateOpen
                && contextPlan.GateBlocked
                && !contextPlan.ContextCreated
                && allocation != null
                && allocation.Available
                && allocation.AllocationRequested
                && allocation.AllocationAccepted
                && !allocation.GateOpen
                && allocation.GateBlocked
                && allocation.ConstructorPlanned
                && !allocation.ConstructorCalled
                && !allocation.ContextCreated
                && !allocation.ContextTracked
                && !allocation.UnloadRegistered
                && registration != null
                && registration.Available
                && registration.RegistrationRequested
                && registration.RegistrationAccepted
                && !registration.GateOpen
                && registration.GateBlocked
                && registration.ConstructorResultRequired
                && !registration.ConstructorResultAvailable
                && !registration.ConstructorCalled
                && !registration.ContextCreated
                && !registration.ContextTracked
                && !registration.UnloadRegistered
                && invocation != null
                && invocation.Available
                && invocation.InvocationRequested
                && invocation.InvocationAccepted
                && !invocation.GateOpen
                && invocation.GateBlocked
                && invocation.RegisteredContextRequired
                && !invocation.RegisteredContextAvailable
                && !invocation.ContextTracked
                && invocation.InvocationPlanned
                && !invocation.InvocationStarted
                && !invocation.InvocationCompleted
                && !invocation.AssemblyLoaded
                && !invocation.LoadResultAvailable
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(invocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var typeResolutionBlocked = typeResolution != null
                && typeResolution.Available
                && typeResolution.ResolutionRequested
                && typeResolution.ResolutionAccepted
                && !typeResolution.PluginTypeResolutionEnabled
                && !typeResolution.AssemblyLoadInvocationEnabled
                && !typeResolution.AssemblyLoadContextRegistrationEnabled
                && !typeResolution.AssemblyLoadContextAllocationEnabled
                && !typeResolution.AssemblyLoadContextPlanEnabled
                && !typeResolution.AssemblyLoadOperationEnabled
                && !typeResolution.AssemblyLoadResolverPlanEnabled
                && !typeResolution.RuntimePluginLoadingEnabled
                && !typeResolution.ExecutorEnabled
                && !typeResolution.AssemblyLoadTransitionRequestEnabled
                && !typeResolution.GateOpen
                && typeResolution.GateBlocked
                && typeResolution.AssemblyResultRequired
                && !typeResolution.AssemblyResultAvailable
                && !typeResolution.AssemblyLoaded
                && !typeResolution.LoadResultAvailable
                && !typeResolution.InvocationCompleted
                && typeResolution.ResolutionPlanned
                && !typeResolution.ResolutionStarted
                && !typeResolution.ResolutionCompleted
                && !typeResolution.PluginTypeResolved
                && !typeResolution.TypeResultAvailable
                && typeResolution.FailureIsolated
                && !typeResolution.RollbackRequired
                && string.Equals(typeResolution.ResolutionStrategy, InvocationPluginTypeResolutionStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeResolution.MethodName, InvocationPluginTypeResolutionMethodName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeResolution.PluginTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(typeResolution.ResolvedTypeName)
                && string.IsNullOrWhiteSpace(typeResolution.LoadedAssemblyName)
                && string.IsNullOrWhiteSpace(typeResolution.LoadedAssemblyPath)
                && string.Equals(typeResolution.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeResolution.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var typeResolutionCarriesPolicyReason = DetailCarriesActivationPolicyReason(typeResolution == null ? null : typeResolution.Detail);
            var detail = attempt == null
                ? "BepInEx plugin type resolution boundary did not return an attempt snapshot."
                : "Recorded BepInEx plugin type resolution boundary for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", type resolutions blocked " + (executor == null ? 0 : executor.BlockedPluginTypeResolutionCount)
                        + "/" + (executor == null ? 0 : executor.PluginTypeResolutionCount)
                    + ", plugin type " + (typeResolution == null ? "" : typeResolution.PluginTypeName)
                    + ", resolution started " + (typeResolution != null && typeResolution.ResolutionStarted)
                    + ", resolution completed " + (typeResolution != null && typeResolution.ResolutionCompleted)
                    + ", type result available " + (typeResolution != null && typeResolution.TypeResultAvailable)
                    + ", assembly loaded " + (typeResolution != null && typeResolution.AssemblyLoaded)
                    + ", type policy reason visible " + typeResolutionCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-plugin-type-resolution-command");
            if (noPluginInvocation && candidateBacked && acceptedButDeferred && upstreamBlocked && chainSnapshotsBlocked && typeResolutionBlocked && typeResolutionCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidatePluginInstanceCreation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-plugin-instance-creation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for a plugin instance creation boundary.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptPluginInstanceCreationRequest(target);
            var executor = attempt == null ? null : attempt.InvocationExecutor;
            var request = executor == null ? null : executor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var contextPlan = operation == null ? null : operation.AssemblyLoadContextPlan;
            var allocation = contextPlan == null ? null : contextPlan.AssemblyLoadContextAllocation;
            var registration = allocation == null ? null : allocation.AssemblyLoadContextRegistration;
            var invocation = registration == null ? null : registration.AssemblyLoadInvocation;
            var typeResolution = invocation == null ? null : invocation.PluginTypeResolution;
            var instanceCreation = typeResolution == null ? null : typeResolution.PluginInstanceCreation;
            var noPluginInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var upstreamBlocked = executor != null
                && executor.Available
                && !executor.ExecutorEnabled
                && !executor.RuntimePluginLoadingEnabled
                && !executor.AssemblyLoadTransitionRequestEnabled
                && executor.ExecutionGateBlocked
                && !executor.ExecutionGateOpen
                && executor.TransitionCount == 3
                && executor.BlockedTransitionCount == 3
                && executor.OpenTransitionCount == 0
                && executor.StartedTransitionCount == 0
                && executor.CompletedTransitionCount == 0
                && executor.TransitionRequestCount == 1
                && executor.AcceptedTransitionRequestCount == 1
                && executor.BlockedTransitionRequestCount == 1
                && executor.OpenTransitionRequestCount == 0
                && executor.StartedTransitionRequestCount == 0
                && executor.CompletedTransitionRequestCount == 0
                && executor.AssemblyLoadedTransitionRequestCount == 0
                && executor.AssemblyLoadResolverPlanCount == 1
                && executor.AcceptedAssemblyLoadResolverPlanCount == 1
                && executor.BlockedAssemblyLoadResolverPlanCount == 1
                && executor.OpenAssemblyLoadResolverPlanCount == 0
                && executor.StartedAssemblyLoadResolverPlanCount == 0
                && executor.LoadedAssemblyLoadResolverPlanCount == 0
                && executor.AssemblyLoadOperationCount == 1
                && executor.AcceptedAssemblyLoadOperationCount == 1
                && executor.BlockedAssemblyLoadOperationCount == 1
                && executor.OpenAssemblyLoadOperationCount == 0
                && executor.StartedAssemblyLoadOperationCount == 0
                && executor.CompletedAssemblyLoadOperationCount == 0
                && executor.LoadedAssemblyLoadOperationCount == 0
                && executor.AssemblyLoadContextPlanCount == 1
                && executor.AcceptedAssemblyLoadContextPlanCount == 1
                && executor.BlockedAssemblyLoadContextPlanCount == 1
                && executor.OpenAssemblyLoadContextPlanCount == 0
                && executor.CreatedAssemblyLoadContextPlanCount == 0
                && executor.AssemblyLoadContextAllocationCount == 1
                && executor.AcceptedAssemblyLoadContextAllocationCount == 1
                && executor.BlockedAssemblyLoadContextAllocationCount == 1
                && executor.OpenAssemblyLoadContextAllocationCount == 0
                && executor.ConstructorCalledAssemblyLoadContextAllocationCount == 0
                && executor.CreatedAssemblyLoadContextAllocationCount == 0
                && executor.AssemblyLoadContextRegistrationCount == 1
                && executor.AcceptedAssemblyLoadContextRegistrationCount == 1
                && executor.BlockedAssemblyLoadContextRegistrationCount == 1
                && executor.OpenAssemblyLoadContextRegistrationCount == 0
                && executor.TrackedAssemblyLoadContextRegistrationCount == 0
                && executor.UnloadRegisteredAssemblyLoadContextRegistrationCount == 0
                && executor.AssemblyLoadInvocationCount == 1
                && executor.AcceptedAssemblyLoadInvocationCount == 1
                && executor.BlockedAssemblyLoadInvocationCount == 1
                && executor.OpenAssemblyLoadInvocationCount == 0
                && executor.StartedAssemblyLoadInvocationCount == 0
                && executor.CompletedAssemblyLoadInvocationCount == 0
                && executor.LoadedAssemblyLoadInvocationCount == 0
                && executor.PluginTypeResolutionCount == 1
                && executor.AcceptedPluginTypeResolutionCount == 1
                && executor.BlockedPluginTypeResolutionCount == 1
                && executor.OpenPluginTypeResolutionCount == 0
                && executor.StartedPluginTypeResolutionCount == 0
                && executor.CompletedPluginTypeResolutionCount == 0
                && executor.ResolvedPluginTypeResolutionCount == 0
                && executor.PluginInstanceCreationCount == 1
                && executor.AcceptedPluginInstanceCreationCount == 1
                && executor.BlockedPluginInstanceCreationCount == 1
                && executor.OpenPluginInstanceCreationCount == 0
                && executor.StartedPluginInstanceCreationCount == 0
                && executor.CompletedPluginInstanceCreationCount == 0
                && executor.CreatedPluginInstanceCreationCount == 0
                && !executor.RollbackRequired;
            var chainSnapshotsBlocked = request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanRequested
                && resolverPlan.PlanAccepted
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && !resolverPlan.AssemblyLoadStarted
                && !resolverPlan.AssemblyLoaded
                && resolverPlan.DependenciesResolved
                && resolverPlan.UnresolvedAssemblyReferenceCount == 0
                && operation != null
                && operation.Available
                && operation.OperationRequested
                && operation.OperationAccepted
                && !operation.GateOpen
                && operation.GateBlocked
                && !operation.ContextCreated
                && !operation.OperationStarted
                && !operation.OperationCompleted
                && !operation.AssemblyLoaded
                && !operation.AssemblyAlreadyLoaded
                && operation.AssemblyExists
                && operation.AssemblyFileSize > 0
                && operation.AssemblyReferenceCount == operation.ResolvedAssemblyReferenceCount
                && operation.UnresolvedAssemblyReferenceCount == 0
                && contextPlan != null
                && contextPlan.Available
                && contextPlan.PlanRequested
                && contextPlan.PlanAccepted
                && !contextPlan.GateOpen
                && contextPlan.GateBlocked
                && !contextPlan.ContextCreated
                && allocation != null
                && allocation.Available
                && allocation.AllocationRequested
                && allocation.AllocationAccepted
                && !allocation.GateOpen
                && allocation.GateBlocked
                && allocation.ConstructorPlanned
                && !allocation.ConstructorCalled
                && !allocation.ContextCreated
                && !allocation.ContextTracked
                && !allocation.UnloadRegistered
                && registration != null
                && registration.Available
                && registration.RegistrationRequested
                && registration.RegistrationAccepted
                && !registration.GateOpen
                && registration.GateBlocked
                && registration.ConstructorResultRequired
                && !registration.ConstructorResultAvailable
                && !registration.ConstructorCalled
                && !registration.ContextCreated
                && !registration.ContextTracked
                && !registration.UnloadRegistered
                && invocation != null
                && invocation.Available
                && invocation.InvocationRequested
                && invocation.InvocationAccepted
                && !invocation.GateOpen
                && invocation.GateBlocked
                && invocation.RegisteredContextRequired
                && !invocation.RegisteredContextAvailable
                && !invocation.ContextTracked
                && invocation.InvocationPlanned
                && !invocation.InvocationStarted
                && !invocation.InvocationCompleted
                && !invocation.AssemblyLoaded
                && !invocation.LoadResultAvailable
                && typeResolution != null
                && typeResolution.Available
                && typeResolution.ResolutionRequested
                && typeResolution.ResolutionAccepted
                && !typeResolution.GateOpen
                && typeResolution.GateBlocked
                && typeResolution.AssemblyResultRequired
                && !typeResolution.AssemblyResultAvailable
                && !typeResolution.AssemblyLoaded
                && !typeResolution.LoadResultAvailable
                && !typeResolution.InvocationCompleted
                && typeResolution.ResolutionPlanned
                && !typeResolution.ResolutionStarted
                && !typeResolution.ResolutionCompleted
                && !typeResolution.PluginTypeResolved
                && !typeResolution.TypeResultAvailable
                && string.Equals(request.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolverPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(contextPlan.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(allocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(registration.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(invocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeResolution.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var instanceCreationBlocked = instanceCreation != null
                && instanceCreation.Available
                && instanceCreation.InstanceCreationRequested
                && instanceCreation.InstanceCreationAccepted
                && !instanceCreation.PluginInstanceCreationEnabled
                && !instanceCreation.PluginTypeResolutionEnabled
                && !instanceCreation.AssemblyLoadInvocationEnabled
                && !instanceCreation.AssemblyLoadContextRegistrationEnabled
                && !instanceCreation.AssemblyLoadContextAllocationEnabled
                && !instanceCreation.AssemblyLoadContextPlanEnabled
                && !instanceCreation.AssemblyLoadOperationEnabled
                && !instanceCreation.AssemblyLoadResolverPlanEnabled
                && !instanceCreation.RuntimePluginLoadingEnabled
                && !instanceCreation.ExecutorEnabled
                && !instanceCreation.AssemblyLoadTransitionRequestEnabled
                && !instanceCreation.GateOpen
                && instanceCreation.GateBlocked
                && instanceCreation.TypeResultRequired
                && !instanceCreation.TypeResultAvailable
                && !instanceCreation.PluginTypeResolved
                && !instanceCreation.ResolutionCompleted
                && instanceCreation.InstanceCreationPlanned
                && !instanceCreation.InstanceCreationStarted
                && !instanceCreation.InstanceCreationCompleted
                && !instanceCreation.PluginInstanceCreated
                && !instanceCreation.InstanceResultAvailable
                && !instanceCreation.ConstructorInvoked
                && instanceCreation.FailureIsolated
                && !instanceCreation.RollbackRequired
                && string.Equals(instanceCreation.InstanceCreationStrategy, InvocationPluginInstanceCreationStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(instanceCreation.MethodName, InvocationPluginInstanceCreationMethodName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(instanceCreation.PluginTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(instanceCreation.ResolvedTypeName)
                && string.IsNullOrWhiteSpace(instanceCreation.InstanceTypeName)
                && string.IsNullOrWhiteSpace(instanceCreation.LoadedAssemblyName)
                && string.IsNullOrWhiteSpace(instanceCreation.LoadedAssemblyPath)
                && string.Equals(instanceCreation.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(instanceCreation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var instanceCreationCarriesPolicyReason = DetailCarriesActivationPolicyReason(instanceCreation == null ? null : instanceCreation.Detail);
            var detail = attempt == null
                ? "BepInEx plugin instance creation boundary did not return an attempt snapshot."
                : "Recorded BepInEx plugin instance creation boundary for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", instance creations blocked " + (executor == null ? 0 : executor.BlockedPluginInstanceCreationCount)
                        + "/" + (executor == null ? 0 : executor.PluginInstanceCreationCount)
                    + ", plugin type " + (instanceCreation == null ? "" : instanceCreation.PluginTypeName)
                    + ", instance creation started " + (instanceCreation != null && instanceCreation.InstanceCreationStarted)
                    + ", instance creation completed " + (instanceCreation != null && instanceCreation.InstanceCreationCompleted)
                    + ", instance result available " + (instanceCreation != null && instanceCreation.InstanceResultAvailable)
                    + ", instance created " + (instanceCreation != null && instanceCreation.PluginInstanceCreated)
                    + ", constructor invoked " + (instanceCreation != null && instanceCreation.ConstructorInvoked)
                    + ", instance policy reason visible " + instanceCreationCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-plugin-instance-creation-command");
            if (noPluginInvocation && candidateBacked && acceptedButDeferred && upstreamBlocked && chainSnapshotsBlocked && instanceCreationBlocked && instanceCreationCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidatePluginLoadInvocation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectLoadValidationCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-plugin-load-invocation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No compatibility-ready BepInEx candidate was available for a BasePlugin.Load invocation boundary.");
                return false;
            }

            var target = CandidatePlanId(candidate);
            var attempt = AttemptPluginLoadInvocationRequest(target);
            var executor = attempt == null ? null : attempt.InvocationExecutor;
            var request = executor == null ? null : executor.FirstBlockedTransitionRequest;
            var resolverPlan = request == null ? null : request.AssemblyLoadResolverPlan;
            var operation = resolverPlan == null ? null : resolverPlan.AssemblyLoadOperation;
            var contextPlan = operation == null ? null : operation.AssemblyLoadContextPlan;
            var allocation = contextPlan == null ? null : contextPlan.AssemblyLoadContextAllocation;
            var registration = allocation == null ? null : allocation.AssemblyLoadContextRegistration;
            var invocation = registration == null ? null : registration.AssemblyLoadInvocation;
            var typeResolution = invocation == null ? null : invocation.PluginTypeResolution;
            var instanceCreation = typeResolution == null ? null : typeResolution.PluginInstanceCreation;
            var loadInvocation = instanceCreation == null ? null : instanceCreation.PluginLoadInvocation;
            var noPluginInvocation = attempt != null
                && !attempt.ExecutionStarted
                && !attempt.PluginInvoked
                && attempt.FailureIsolated;
            var candidateBacked = attempt != null
                && !attempt.ValidationOnly
                && string.Equals(attempt.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Candidate != null
                && attempt.Plan != null
                && !attempt.Plan.ValidationOnly
                && string.Equals(attempt.Plan.PlanSource, LoadPlanSourceCandidate, StringComparison.OrdinalIgnoreCase)
                && attempt.Plan.Candidate != null;
            var acceptedButDeferred = attempt != null
                && attempt.PlanAccepted
                && attempt.AttemptAccepted
                && string.Equals(attempt.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase)
                && !attempt.RuntimePluginLoadingEnabled
                && attempt.LoadGateBlocked
                && !attempt.LoadGateOpen;
            var executorBlocked = executor != null
                && executor.Available
                && !executor.ExecutorEnabled
                && !executor.RuntimePluginLoadingEnabled
                && !executor.AssemblyLoadTransitionRequestEnabled
                && executor.ExecutionGateBlocked
                && !executor.ExecutionGateOpen
                && executor.PluginTypeResolutionCount == 1
                && executor.AcceptedPluginTypeResolutionCount == 1
                && executor.BlockedPluginTypeResolutionCount == 1
                && executor.OpenPluginTypeResolutionCount == 0
                && executor.StartedPluginTypeResolutionCount == 0
                && executor.CompletedPluginTypeResolutionCount == 0
                && executor.ResolvedPluginTypeResolutionCount == 0
                && executor.PluginInstanceCreationCount == 1
                && executor.AcceptedPluginInstanceCreationCount == 1
                && executor.BlockedPluginInstanceCreationCount == 1
                && executor.OpenPluginInstanceCreationCount == 0
                && executor.StartedPluginInstanceCreationCount == 0
                && executor.CompletedPluginInstanceCreationCount == 0
                && executor.CreatedPluginInstanceCreationCount == 0
                && executor.PluginLoadInvocationCount == 1
                && executor.AcceptedPluginLoadInvocationCount == 1
                && executor.BlockedPluginLoadInvocationCount == 1
                && executor.OpenPluginLoadInvocationCount == 0
                && executor.StartedPluginLoadInvocationCount == 0
                && executor.CompletedPluginLoadInvocationCount == 0
                && executor.InvokedPluginLoadInvocationCount == 0
                && !executor.RollbackRequired;
            var upstreamBlocked = request != null
                && request.Requested
                && request.Accepted
                && request.StageMatched
                && request.StageAvailable
                && !request.GateOpen
                && request.GateBlocked
                && !request.ExecutionStarted
                && !request.ExecutionCompleted
                && !request.AssemblyLoaded
                && resolverPlan != null
                && resolverPlan.Available
                && resolverPlan.PlanAccepted
                && !resolverPlan.GateOpen
                && resolverPlan.GateBlocked
                && operation != null
                && operation.Available
                && operation.OperationAccepted
                && !operation.GateOpen
                && operation.GateBlocked
                && contextPlan != null
                && contextPlan.Available
                && contextPlan.PlanAccepted
                && !contextPlan.GateOpen
                && contextPlan.GateBlocked
                && allocation != null
                && allocation.Available
                && allocation.AllocationAccepted
                && !allocation.GateOpen
                && allocation.GateBlocked
                && !allocation.ConstructorCalled
                && !allocation.ContextCreated
                && registration != null
                && registration.Available
                && registration.RegistrationAccepted
                && !registration.GateOpen
                && registration.GateBlocked
                && !registration.ContextTracked
                && !registration.UnloadRegistered
                && invocation != null
                && invocation.Available
                && invocation.InvocationAccepted
                && !invocation.GateOpen
                && invocation.GateBlocked
                && !invocation.InvocationStarted
                && !invocation.InvocationCompleted
                && !invocation.AssemblyLoaded
                && !invocation.LoadResultAvailable
                && typeResolution != null
                && typeResolution.Available
                && typeResolution.ResolutionAccepted
                && !typeResolution.GateOpen
                && typeResolution.GateBlocked
                && !typeResolution.ResolutionStarted
                && !typeResolution.ResolutionCompleted
                && !typeResolution.PluginTypeResolved
                && !typeResolution.TypeResultAvailable
                && instanceCreation != null
                && instanceCreation.Available
                && instanceCreation.InstanceCreationAccepted
                && !instanceCreation.GateOpen
                && instanceCreation.GateBlocked
                && !instanceCreation.InstanceCreationStarted
                && !instanceCreation.InstanceCreationCompleted
                && !instanceCreation.PluginInstanceCreated
                && !instanceCreation.InstanceResultAvailable
                && !instanceCreation.ConstructorInvoked;
            var loadInvocationBlocked = loadInvocation != null
                && loadInvocation.Available
                && loadInvocation.LoadInvocationRequested
                && loadInvocation.LoadInvocationAccepted
                && !loadInvocation.PluginLoadInvocationEnabled
                && !loadInvocation.PluginInstanceCreationEnabled
                && !loadInvocation.PluginTypeResolutionEnabled
                && !loadInvocation.AssemblyLoadInvocationEnabled
                && !loadInvocation.AssemblyLoadContextRegistrationEnabled
                && !loadInvocation.AssemblyLoadContextAllocationEnabled
                && !loadInvocation.AssemblyLoadContextPlanEnabled
                && !loadInvocation.AssemblyLoadOperationEnabled
                && !loadInvocation.AssemblyLoadResolverPlanEnabled
                && !loadInvocation.RuntimePluginLoadingEnabled
                && !loadInvocation.ExecutorEnabled
                && !loadInvocation.AssemblyLoadTransitionRequestEnabled
                && !loadInvocation.GateOpen
                && loadInvocation.GateBlocked
                && loadInvocation.PluginInstanceRequired
                && !loadInvocation.PluginInstanceAvailable
                && !loadInvocation.InstanceResultAvailable
                && !loadInvocation.PluginInstanceCreated
                && !loadInvocation.InstanceCreationCompleted
                && loadInvocation.LoadInvocationPlanned
                && !loadInvocation.LoadInvocationStarted
                && !loadInvocation.LoadInvocationCompleted
                && !loadInvocation.LoadMethodInvoked
                && !loadInvocation.PluginLoadInvoked
                && loadInvocation.FailureIsolated
                && !loadInvocation.RollbackRequired
                && string.Equals(loadInvocation.LoadInvocationStrategy, InvocationPluginLoadInvocationStrategy, StringComparison.OrdinalIgnoreCase)
                && string.Equals(loadInvocation.MethodName, InvocationPluginLoadInvocationMethodName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(loadInvocation.PluginTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(loadInvocation.ResolvedTypeName)
                && string.IsNullOrWhiteSpace(loadInvocation.InstanceTypeName)
                && string.IsNullOrWhiteSpace(loadInvocation.LoadedAssemblyName)
                && string.IsNullOrWhiteSpace(loadInvocation.LoadedAssemblyPath)
                && string.Equals(loadInvocation.Status, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(loadInvocation.ReasonCode, "runtime-plugin-loading-disabled", StringComparison.OrdinalIgnoreCase);
            var loadInvocationCarriesPolicyReason = DetailCarriesActivationPolicyReason(loadInvocation == null ? null : loadInvocation.Detail);
            var detail = attempt == null
                ? "BepInEx BasePlugin.Load invocation boundary did not return an attempt snapshot."
                : "Recorded BepInEx BasePlugin.Load invocation boundary for " + target
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", source " + attempt.PlanSource
                    + ", candidate source " + (attempt.Candidate == null ? "unknown" : attempt.Candidate.CandidateSource)
                    + ", load invocations blocked " + (executor == null ? 0 : executor.BlockedPluginLoadInvocationCount)
                        + "/" + (executor == null ? 0 : executor.PluginLoadInvocationCount)
                    + ", plugin type " + (loadInvocation == null ? "" : loadInvocation.PluginTypeName)
                    + ", load invocation started " + (loadInvocation != null && loadInvocation.LoadInvocationStarted)
                    + ", load invocation completed " + (loadInvocation != null && loadInvocation.LoadInvocationCompleted)
                    + ", Load() invoked " + (loadInvocation != null && loadInvocation.LoadMethodInvoked)
                    + ", plugin load invoked " + (loadInvocation != null && loadInvocation.PluginLoadInvoked)
                    + ", Load policy reason visible " + loadInvocationCarriesPolicyReason
                    + ", plugin invoked " + attempt.PluginInvoked
                    + ", execution started " + attempt.ExecutionStarted
                    + ", failure isolated " + attempt.FailureIsolated + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-plugin-load-invocation-command");
            if (noPluginInvocation && candidateBacked && acceptedButDeferred && executorBlocked && upstreamBlocked && loadInvocationBlocked && loadInvocationCarriesPolicyReason)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateProbeAssemblyLoad(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectActivationValidationProbeCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-assembly-load-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No generated BepInEx validation probe candidate was available for OpenACAI-owned assembly load.");
                return false;
            }

            var attempt = OpenACAIBepInExPluginActivationProbe.ValidateAssemblyLoad(candidate);
            var assemblyLoadSucceeded = attempt != null
                && attempt.Success
                && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindAssemblyLoad, StringComparison.OrdinalIgnoreCase)
                && attempt.ValidationProbe
                && attempt.ActivationAllowed
                && attempt.ContextCreated
                && attempt.CollectibleContext
                && attempt.ResolverAttached
                && attempt.AssemblyLoadStarted
                && attempt.AssemblyLoadCompleted
                && attempt.AssemblyLoaded
                && !attempt.TypeResolutionStarted
                && !attempt.TypeResolutionCompleted
                && !attempt.PluginTypeResolved
                && attempt.PluginInstanceCreationBlocked
                && attempt.PluginLoadInvocationBlocked
                && !attempt.ConstructorInvoked
                && !attempt.PluginInstanceCreated
                && !attempt.LoadMethodInvoked
                && !attempt.PluginLoadInvoked
                && attempt.WeakReferenceCreated
                && attempt.UnloadRequested
                && !attempt.ContextAliveAfterUnload
                && string.Equals(attempt.PluginId, candidate.ModId, StringComparison.Ordinal)
                && string.Equals(attempt.Status, "READY", StringComparison.OrdinalIgnoreCase);
            var detail = attempt == null
                ? "OpenACAI validation-probe assembly load did not return an attempt snapshot."
                : "OpenACAI validation-probe assembly load for " + candidate.ModId
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", context created " + attempt.ContextCreated
                    + ", resolver attached " + attempt.ResolverAttached
                    + ", assembly loaded " + attempt.AssemblyLoaded
                    + ", loaded assembly " + attempt.LoadedAssemblyName
                    + ", type resolution started " + attempt.TypeResolutionStarted
                    + ", constructor invoked " + attempt.ConstructorInvoked
                    + ", Load() invoked " + attempt.LoadMethodInvoked
                    + ", unload requested " + attempt.UnloadRequested
                    + ", context alive after unload " + attempt.ContextAliveAfterUnload + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-assembly-load-command");
            if (assemblyLoadSucceeded)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateProbeTypeActivation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectActivationValidationProbeCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-type-activation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No generated BepInEx validation probe candidate was available for OpenACAI-owned type activation.");
                return false;
            }

            var attempt = OpenACAIBepInExPluginActivationProbe.ValidateTypeResolution(candidate);
            var activationSucceeded = attempt != null
                && attempt.Success
                && attempt.ValidationProbe
                && attempt.ActivationAllowed
                && attempt.ContextCreated
                && attempt.CollectibleContext
                && attempt.ResolverAttached
                && attempt.AssemblyLoadStarted
                && attempt.AssemblyLoadCompleted
                && attempt.AssemblyLoaded
                && attempt.TypeResolutionStarted
                && attempt.TypeResolutionCompleted
                && attempt.PluginTypeResolved
                && attempt.AssignableToBasePlugin
                && attempt.PluginInstanceCreationBlocked
                && attempt.PluginLoadInvocationBlocked
                && !attempt.ConstructorInvoked
                && !attempt.PluginInstanceCreated
                && !attempt.LoadMethodInvoked
                && !attempt.PluginLoadInvoked
                && attempt.WeakReferenceCreated
                && attempt.UnloadRequested
                && string.Equals(attempt.PluginId, candidate.ModId, StringComparison.Ordinal)
                && string.Equals(attempt.PluginTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.ResolvedTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.Status, "READY", StringComparison.OrdinalIgnoreCase);
            var detail = attempt == null
                ? "OpenACAI validation-probe type activation did not return an attempt snapshot."
                : "OpenACAI validation-probe type activation for " + candidate.ModId
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", assembly loaded " + attempt.AssemblyLoaded
                    + ", loaded assembly " + attempt.LoadedAssemblyName
                    + ", type resolved " + attempt.PluginTypeResolved
                    + ", resolved type " + attempt.ResolvedTypeName
                    + ", BasePlugin assignable " + attempt.AssignableToBasePlugin
                    + ", constructor invoked " + attempt.ConstructorInvoked
                    + ", Load() invoked " + attempt.LoadMethodInvoked
                    + ", plugin load invoked " + attempt.PluginLoadInvoked
                    + ", unload requested " + attempt.UnloadRequested
                    + ", context alive after unload " + attempt.ContextAliveAfterUnload + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-type-activation-command");
            if (activationSucceeded)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateProbeInstanceCreation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectActivationValidationProbeCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-instance-creation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No generated BepInEx validation probe candidate was available for OpenACAI-owned instance creation.");
                return false;
            }

            var attempt = OpenACAIBepInExPluginActivationProbe.ValidateInstanceCreation(candidate);
            var instanceCreationSucceeded = attempt != null
                && attempt.Success
                && string.Equals(attempt.ActivationKind, "validation-instance-creation", StringComparison.OrdinalIgnoreCase)
                && attempt.ValidationProbe
                && attempt.ActivationAllowed
                && attempt.ContextCreated
                && attempt.CollectibleContext
                && attempt.ResolverAttached
                && attempt.AssemblyLoadStarted
                && attempt.AssemblyLoadCompleted
                && attempt.AssemblyLoaded
                && attempt.TypeResolutionStarted
                && attempt.TypeResolutionCompleted
                && attempt.PluginTypeResolved
                && attempt.AssignableToBasePlugin
                && attempt.PluginInstanceCreationRequested
                && !attempt.PluginInstanceCreationBlocked
                && attempt.PluginInstanceCreationStarted
                && attempt.PluginInstanceCreationCompleted
                && attempt.ConstructorInvoked
                && attempt.PluginInstanceCreated
                && attempt.PluginInstanceAssignableToBasePlugin
                && attempt.PluginLoadInvocationBlocked
                && !attempt.LoadMethodInvoked
                && !attempt.PluginLoadInvoked
                && attempt.WeakReferenceCreated
                && attempt.UnloadRequested
                && string.Equals(attempt.PluginId, candidate.ModId, StringComparison.Ordinal)
                && string.Equals(attempt.PluginTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.ResolvedTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.PluginInstanceTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.Status, "READY", StringComparison.OrdinalIgnoreCase);
            var detail = attempt == null
                ? "OpenACAI validation-probe instance creation did not return an attempt snapshot."
                : "OpenACAI validation-probe instance creation for " + candidate.ModId
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", assembly loaded " + attempt.AssemblyLoaded
                    + ", type resolved " + attempt.PluginTypeResolved
                    + ", BasePlugin assignable " + attempt.AssignableToBasePlugin
                    + ", instance requested " + attempt.PluginInstanceCreationRequested
                    + ", instance started " + attempt.PluginInstanceCreationStarted
                    + ", constructor invoked " + attempt.ConstructorInvoked
                    + ", instance created " + attempt.PluginInstanceCreated
                    + ", instance type " + attempt.PluginInstanceTypeName
                    + ", instance BasePlugin assignable " + attempt.PluginInstanceAssignableToBasePlugin
                    + ", Load() invoked " + attempt.LoadMethodInvoked
                    + ", plugin load invoked " + attempt.PluginLoadInvoked
                    + ", unload requested " + attempt.UnloadRequested
                    + ", context alive after unload " + attempt.ContextAliveAfterUnload + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-instance-creation-command");
            if (instanceCreationSucceeded)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static bool TryValidateProbeLoadInvocation(out OpenACAICommandResultSnapshot result)
        {
            var snapshot = Refresh();
            var candidate = SelectActivationValidationProbeCandidate(snapshot);
            if (candidate == null)
            {
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-load-invocation-command-empty");
                result = OpenACAICommandResultSnapshot.Failure("No generated BepInEx validation probe candidate was available for OpenACAI-owned Load() invocation.");
                return false;
            }

            var attempt = OpenACAIBepInExPluginActivationProbe.ValidateLoadInvocation(candidate);
            var loadInvocationSucceeded = attempt != null
                && attempt.Success
                && string.Equals(attempt.ActivationKind, "validation-load-invocation", StringComparison.OrdinalIgnoreCase)
                && attempt.ValidationProbe
                && attempt.ActivationAllowed
                && attempt.ContextCreated
                && attempt.CollectibleContext
                && attempt.ResolverAttached
                && attempt.AssemblyLoadStarted
                && attempt.AssemblyLoadCompleted
                && attempt.AssemblyLoaded
                && attempt.TypeResolutionStarted
                && attempt.TypeResolutionCompleted
                && attempt.PluginTypeResolved
                && attempt.AssignableToBasePlugin
                && attempt.PluginInstanceCreationRequested
                && !attempt.PluginInstanceCreationBlocked
                && attempt.PluginInstanceCreationStarted
                && attempt.PluginInstanceCreationCompleted
                && attempt.ConstructorInvoked
                && attempt.PluginInstanceCreated
                && attempt.PluginInstanceAssignableToBasePlugin
                && attempt.PluginLoadInvocationRequested
                && !attempt.PluginLoadInvocationBlocked
                && attempt.PluginLoadInvocationStarted
                && attempt.PluginLoadInvocationCompleted
                && attempt.PluginLoadInvocationFailureIsolated
                && !attempt.PluginLoadInvocationRollbackRequired
                && string.IsNullOrWhiteSpace(attempt.PluginLoadInvocationExceptionType)
                && attempt.LoadMethodInvoked
                && attempt.PluginLoadInvoked
                && attempt.WeakReferenceCreated
                && attempt.UnloadRequested
                && string.Equals(attempt.PluginId, candidate.ModId, StringComparison.Ordinal)
                && string.Equals(attempt.PluginTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.ResolvedTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.PluginInstanceTypeName, candidate.PluginTypeName, StringComparison.Ordinal)
                && string.Equals(attempt.Status, "READY", StringComparison.OrdinalIgnoreCase);
            var detail = attempt == null
                ? "OpenACAI validation-probe Load() invocation did not return an attempt snapshot."
                : "OpenACAI validation-probe Load() invocation for " + candidate.ModId
                    + ": status " + attempt.Status
                    + ", reason " + attempt.ReasonCode
                    + ", assembly loaded " + attempt.AssemblyLoaded
                    + ", type resolved " + attempt.PluginTypeResolved
                    + ", instance created " + attempt.PluginInstanceCreated
                    + ", Load() requested " + attempt.PluginLoadInvocationRequested
                    + ", Load() started " + attempt.PluginLoadInvocationStarted
                    + ", Load() completed " + attempt.PluginLoadInvocationCompleted
                    + ", Load() invoked " + attempt.LoadMethodInvoked
                    + ", plugin load invoked " + attempt.PluginLoadInvoked
                    + ", failure isolated " + attempt.PluginLoadInvocationFailureIsolated
                    + ", rollback required " + attempt.PluginLoadInvocationRollbackRequired
                    + ", exception " + attempt.PluginLoadInvocationExceptionType
                    + ", unload requested " + attempt.UnloadRequested
                    + ", context alive after unload " + attempt.ContextAliveAfterUnload + ".";

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-compatibility-probe-load-invocation-command");
            if (loadInvocationSucceeded)
            {
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure(detail);
            return false;
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke BepInEx Gate",
                "OpenACAI-owned metadata gate for legacy BepInEx plugin compatibility.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateParserHarnessSection(snapshot),
                    CreateCandidateSection(snapshot),
                    CreatePlanSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            var validationCandidateCount = snapshot.Candidates.Count(candidate => candidate != null && candidate.ValidationProbe);
            var invocationPreflightReadyCount = snapshot.LoadPlans.Count(plan => plan != null && plan.InvocationPreflight != null && plan.InvocationPreflight.PreflightReady);
            var invocationPreflightBlockedCount = snapshot.LoadPlans.Count(plan => plan != null && plan.InvocationPreflight != null && plan.InvocationPreflight.InvocationGateBlocked);
            var invocationPipelineStageCount = snapshot.LoadPlans.Sum(plan => plan == null || plan.InvocationPreflight == null ? 0 : plan.InvocationPreflight.InvocationPipelineStageCount);
            var invocationPipelineBlockedStageCount = snapshot.LoadPlans.Sum(plan => plan == null || plan.InvocationPreflight == null ? 0 : plan.InvocationPreflight.BlockedInvocationPipelineStageCount);
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
            var invocationStageAttemptBlockedCount = snapshot.LoadAttempts.Sum(attempt => attempt == null ? 0 : attempt.BlockedInvocationStageAttemptCount);
            var invocationExecutorTransitionCount = snapshot.LoadAttempts.Sum(attempt => attempt == null || attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.TransitionCount);
            var invocationExecutorBlockedTransitionCount = snapshot.LoadAttempts.Sum(attempt => attempt == null || attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedTransitionCount);
            var invocationExecutorTransitionRequestCount = snapshot.LoadAttempts.Sum(attempt => attempt == null || attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.TransitionRequestCount);
            var invocationExecutorBlockedTransitionRequestCount = snapshot.LoadAttempts.Sum(attempt => attempt == null || attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedTransitionRequestCount);
            var invocationExecutorAssemblyLoadedRequestCount = snapshot.LoadAttempts.Sum(attempt => attempt == null || attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadedTransitionRequestCount);
            var firstBlockedTransitionRequest = snapshot.LoadAttempts
                .Where(attempt => attempt != null && attempt.InvocationExecutor != null)
                .SelectMany(attempt => attempt.InvocationExecutor.TransitionRequests)
                .FirstOrDefault(request => request != null && request.GateBlocked);
            var assemblyLoadResolverPlans = snapshot.LoadAttempts
                .Where(attempt => attempt != null && attempt.InvocationExecutor != null)
                .SelectMany(attempt => attempt.InvocationExecutor.TransitionRequests)
                .Where(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available)
                .Select(request => request.AssemblyLoadResolverPlan)
                .ToArray();
            var assemblyLoadResolverPlanCount = assemblyLoadResolverPlans.Length;
            var assemblyLoadResolverBlockedPlanCount = assemblyLoadResolverPlans.Count(plan => plan != null && plan.GateBlocked);
            var assemblyLoadResolverLoadedPlanCount = assemblyLoadResolverPlans.Count(plan => plan != null && plan.AssemblyLoaded);
            var firstBlockedAssemblyLoadResolverPlan = assemblyLoadResolverPlans.FirstOrDefault(plan => plan != null && plan.GateBlocked);
            var assemblyLoadResolverReferenceCount = assemblyLoadResolverPlans.Sum(plan => plan == null ? 0 : plan.AssemblyReferenceCount);
            var assemblyLoadResolverResolvedReferenceCount = assemblyLoadResolverPlans.Sum(plan => plan == null ? 0 : plan.ResolvedAssemblyReferenceCount);
            var assemblyLoadResolverUnresolvedReferenceCount = assemblyLoadResolverPlans.Sum(plan => plan == null ? 0 : plan.UnresolvedAssemblyReferenceCount);
            var assemblyLoadOperations = assemblyLoadResolverPlans
                .Where(plan => plan != null && plan.AssemblyLoadOperation != null && plan.AssemblyLoadOperation.Available)
                .Select(plan => plan.AssemblyLoadOperation)
                .ToArray();
            var assemblyLoadOperationCount = assemblyLoadOperations.Length;
            var assemblyLoadBlockedOperationCount = assemblyLoadOperations.Count(operation => operation != null && operation.GateBlocked);
            var assemblyLoadStartedOperationCount = assemblyLoadOperations.Count(operation => operation != null && operation.OperationStarted);
            var assemblyLoadCompletedOperationCount = assemblyLoadOperations.Count(operation => operation != null && operation.OperationCompleted);
            var assemblyLoadLoadedOperationCount = assemblyLoadOperations.Count(operation => operation != null && operation.AssemblyLoaded);
            var assemblyLoadContextPlans = assemblyLoadOperations
                .Where(operation => operation != null && operation.AssemblyLoadContextPlan != null && operation.AssemblyLoadContextPlan.Available)
                .Select(operation => operation.AssemblyLoadContextPlan)
                .ToArray();
            var assemblyLoadContextPlanCount = assemblyLoadContextPlans.Length;
            var assemblyLoadBlockedContextPlanCount = assemblyLoadContextPlans.Count(plan => plan != null && plan.GateBlocked);
            var assemblyLoadCreatedContextPlanCount = assemblyLoadContextPlans.Count(plan => plan != null && plan.ContextCreated);
            var assemblyLoadContextAllocations = assemblyLoadContextPlans
                .Where(plan => plan != null && plan.AssemblyLoadContextAllocation != null && plan.AssemblyLoadContextAllocation.Available)
                .Select(plan => plan.AssemblyLoadContextAllocation)
                .ToArray();
            var assemblyLoadContextAllocationCount = assemblyLoadContextAllocations.Length;
            var assemblyLoadBlockedContextAllocationCount = assemblyLoadContextAllocations.Count(allocation => allocation != null && allocation.GateBlocked);
            var assemblyLoadConstructorCalledAllocationCount = assemblyLoadContextAllocations.Count(allocation => allocation != null && allocation.ConstructorCalled);
            var assemblyLoadCreatedContextAllocationCount = assemblyLoadContextAllocations.Count(allocation => allocation != null && allocation.ContextCreated);
            var assemblyLoadContextRegistrations = assemblyLoadContextAllocations
                .Where(allocation => allocation != null && allocation.AssemblyLoadContextRegistration != null && allocation.AssemblyLoadContextRegistration.Available)
                .Select(allocation => allocation.AssemblyLoadContextRegistration)
                .ToArray();
            var assemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Length;
            var assemblyLoadBlockedContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.GateBlocked);
            var assemblyLoadTrackedContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.ContextTracked);
            var assemblyLoadUnloadRegisteredContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.UnloadRegistered);
            var assemblyLoadInvocations = assemblyLoadContextRegistrations
                .Where(registration => registration != null && registration.AssemblyLoadInvocation != null && registration.AssemblyLoadInvocation.Available)
                .Select(registration => registration.AssemblyLoadInvocation)
                .ToArray();
            var assemblyLoadInvocationCount = assemblyLoadInvocations.Length;
            var assemblyLoadBlockedInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.GateBlocked);
            var assemblyLoadStartedInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationStarted);
            var assemblyLoadCompletedInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationCompleted);
            var assemblyLoadLoadedInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.AssemblyLoaded);
            var pluginTypeResolutions = assemblyLoadInvocations
                .Where(invocation => invocation != null && invocation.PluginTypeResolution != null && invocation.PluginTypeResolution.Available)
                .Select(invocation => invocation.PluginTypeResolution)
                .ToArray();
            var pluginTypeResolutionCount = pluginTypeResolutions.Length;
            var pluginTypeBlockedResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.GateBlocked);
            var pluginTypeStartedResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionStarted);
            var pluginTypeCompletedResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionCompleted);
            var pluginTypeResolvedResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.PluginTypeResolved);
            var pluginInstanceCreations = pluginTypeResolutions
                .Where(resolution => resolution != null && resolution.PluginInstanceCreation != null && resolution.PluginInstanceCreation.Available)
                .Select(resolution => resolution.PluginInstanceCreation)
                .ToArray();
            var pluginInstanceCreationCount = pluginInstanceCreations.Length;
            var pluginInstanceBlockedCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.GateBlocked);
            var pluginInstanceStartedCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationStarted);
            var pluginInstanceCompletedCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationCompleted);
            var pluginInstanceCreatedCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.PluginInstanceCreated);
            var pluginLoadInvocations = pluginInstanceCreations
                .Where(creation => creation != null && creation.PluginLoadInvocation != null && creation.PluginLoadInvocation.Available)
                .Select(creation => creation.PluginLoadInvocation)
                .ToArray();
            var pluginLoadInvocationCount = pluginLoadInvocations.Length;
            var pluginLoadBlockedInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.GateBlocked);
            var pluginLoadStartedInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationStarted);
            var pluginLoadCompletedInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationCompleted);
            var pluginLoadInvokedInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.PluginLoadInvoked);
            var candidateStaticConstructorCount = snapshot.Candidates.Count(candidate => candidate != null
                && candidate.ExecutionPlan != null
                && candidate.ExecutionPlan.StaticConstructorFound);
            var candidateModuleInitializerCount = snapshot.Candidates.Where(candidate => candidate != null)
                .GroupBy(candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase)
                .Sum(group => group.Max(candidate => candidate.ExecutionPlan?.ModuleInitializerCount ?? 0));
            var parserHarnessModuleInitializerCount = snapshot.ParserHarnesses.Sum(harness => harness == null || harness.ExecutionPlan == null
                ? 0
                : harness.ExecutionPlan.ModuleInitializerCount);
            var activationProbeState = OpenACAIBepInExPluginActivationProbe.State;
            var activationProbeLatestType = activationProbeState == null
                ? null
                : activationProbeState.Attempts.LastOrDefault(attempt => attempt != null && !attempt.PluginInstanceCreationRequested && !attempt.PluginLoadInvocationRequested);
            var activationProbeLatestInstance = activationProbeState == null
                ? null
                : activationProbeState.Attempts.LastOrDefault(attempt => attempt != null && attempt.PluginInstanceCreationRequested && !attempt.PluginLoadInvocationRequested);
            var activationProbeLatestLoad = activationProbeState == null
                ? null
                : activationProbeState.Attempts.LastOrDefault(attempt => attempt != null && attempt.PluginLoadInvocationRequested);
            return new OpenACAIMenuSectionSnapshot(
                "BepInEx Compatibility Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned BepInEx plugin compatibility snapshot."),
                    Row("Plugin Root", ShortPath(snapshot.PluginRoot), snapshot.PluginRootExists ? "READY" : "INFO", snapshot.PluginRoot),
                    Row("Scanning", snapshot.ScanningEnabled ? "Enabled" : "Disabled", snapshot.ScanningEnabled ? "READY" : "CHECK", "OpenACAI scans BepInEx plugin assemblies as metadata only."),
                    Row("Runtime Plugin Loading", snapshot.RuntimePluginLoadingEnabled ? "Enabled" : "Disabled", snapshot.RuntimePluginLoadingEnabled ? "READY" : "INFO", "BepInEx plugin runtime loading remains disabled in the OpenACAI compatibility gate."),
                    Row("Candidates", snapshot.CandidateCount.ToString(), snapshot.CompatibilityIssueCount == 0 ? "READY" : "CHECK", snapshot.CompatibilityReadyCount + " metadata-ready; " + snapshot.CompatibilityIssueCount + " compatibility issue(s)."),
                    Row("BepInPlugin Metadata", snapshot.PluginAttributeCount.ToString(), snapshot.CompatibilityIssueCount == 0 ? "READY" : "CHECK", snapshot.MetadataReadCount + " assembly metadata read, " + snapshot.MetadataIssueCount + " metadata issue(s)."),
                    Row("Candidate References", snapshot.ResolvedAssemblyReferenceCount + "/" + snapshot.AssemblyReferenceCount, snapshot.UnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.UnresolvedAssemblyReferenceCount + " unresolved BepInEx plugin candidate assembly reference(s)." + FirstUnresolvedDetail(snapshot.FirstUnresolvedCandidateAssemblyReference)),
                    Row("Candidate Execution Plans", snapshot.ExecutionPlanReadyCount + "/" + snapshot.ExecutionPlanCount, snapshot.ExecutionPlanIssueCount == 0 && snapshot.LifecycleMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.ExecutionPlanIssueCount + " execution plan issue(s); lifecycle/entrypoint methods " + snapshot.LifecycleMethodReadyCount + "/" + snapshot.LifecycleMethodCount + "." + FirstExecutionPlanIssueDetail(snapshot.FirstExecutionPlanIssue, snapshot.FirstLifecycleMethodIssue)),
                    Row("Activation Side Effects", candidateStaticConstructorCount + " cctor / " + candidateModuleInitializerCount + " init", candidateModuleInitializerCount == 0 && parserHarnessModuleInitializerCount == 0 ? "READY" : "CHECK", "Metadata-only side-effect preflight found " + candidateStaticConstructorCount + " candidate static constructor(s), " + candidateModuleInitializerCount + " candidate module initializer(s), and " + parserHarnessModuleInitializerCount + " parser-harness module initializer(s)."),
                    Row("Parser Harness", snapshot.ParserHarnessCount.ToString(), snapshot.ParserHarnessIssueCount == 0 ? snapshot.PositiveMetadataParserVerified ? "READY" : "INFO" : "CHECK", snapshot.ParserHarnessReadyCount + " positive parser proof(s), " + snapshot.ParserHarnessIssueCount + " issue(s), " + snapshot.ParserHarnessUnsafeLocationCount + " unsafe location(s)."),
                    Row("Harness References", snapshot.ParserHarnessResolvedAssemblyReferenceCount + "/" + snapshot.ParserHarnessAssemblyReferenceCount, snapshot.ParserHarnessUnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.ParserHarnessUnresolvedAssemblyReferenceCount + " unresolved parser harness assembly reference(s)." + FirstUnresolvedDetail(snapshot.FirstUnresolvedParserHarnessAssemblyReference)),
                    Row("Harness Execution Plans", snapshot.ParserHarnessExecutionPlanReadyCount + "/" + snapshot.ParserHarnessExecutionPlanCount, snapshot.ParserHarnessExecutionPlanIssueCount == 0 && snapshot.ParserHarnessLifecycleMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.ParserHarnessExecutionPlanIssueCount + " parser harness execution plan issue(s); lifecycle/entrypoint methods " + snapshot.ParserHarnessLifecycleMethodReadyCount + "/" + snapshot.ParserHarnessLifecycleMethodCount + "." + FirstExecutionPlanIssueDetail(snapshot.FirstParserHarnessExecutionPlanIssue, snapshot.FirstParserHarnessLifecycleMethodIssue)),
                    Row("Validation Candidates", validationCandidateCount.ToString(), "INFO", "OpenACAI candidate probes are read only from OpenACAI validation roots and are not mod inventory entries."),
                    Row("Invocation Preflight", invocationPreflightReadyCount + "/" + snapshot.LoadPlanCount, invocationPreflightBlockedCount == 0 ? "INFO" : "DEFERRED", invocationPreflightBlockedCount + " BepInEx invocation preflight(s) are blocked by the disabled OpenACAI runtime plugin gate."),
                    Row("Activation Policy", activationPolicyBlockedCount + "/" + activationPolicyCount, activationPolicyStatus, "OpenACAI activation policy blocks assembly load " + activationPolicyAssemblyBlockedCount + ", constructors " + activationPolicyConstructorBlockedCount + ", and Load() calls " + activationPolicyLoadBlockedCount + "; first reason " + firstBlockedActivationReason + "."),
                    Row("Invocation Pipeline", invocationPipelineBlockedStageCount + "/" + invocationPipelineStageCount, invocationPipelineBlockedStageCount == 0 ? "INFO" : "DEFERRED", invocationPipelineBlockedStageCount + " OpenACAI-owned BepInEx invocation stage(s) are blocked before assembly load, instance creation, or entrypoint invocation."),
                    Row("Stage Attempts", invocationStageAttemptBlockedCount + "/" + invocationStageAttemptCount, invocationStageAttemptBlockedCount == 0 ? "INFO" : "DEFERRED", invocationStageAttemptBlockedCount + " OpenACAI BepInEx stage attempt(s) were recorded but blocked before execution."),
                    Row("Stage Executor", invocationExecutorBlockedTransitionCount + "/" + invocationExecutorTransitionCount, invocationExecutorBlockedTransitionCount == 0 ? "INFO" : "DEFERRED", invocationExecutorBlockedTransitionCount + " OpenACAI BepInEx executor transition(s) are blocked before stage start."),
                    Row("Stage Requests", invocationExecutorBlockedTransitionRequestCount + "/" + invocationExecutorTransitionRequestCount, invocationExecutorTransitionRequestCount == 0 ? "INFO" : "DEFERRED", invocationExecutorBlockedTransitionRequestCount + " OpenACAI BepInEx executor transition request(s) are blocked before assembly load; assembly loads completed " + invocationExecutorAssemblyLoadedRequestCount + "." + FirstBoundaryDetail(firstBlockedTransitionRequest == null ? null : firstBlockedTransitionRequest.Detail)),
                    Row("Assembly Request Gate", InvocationAssemblyLoadTransitionRequestEnabled ? "Enabled" : "Disabled", InvocationAssemblyLoadTransitionRequestEnabled ? "READY" : "INFO", "Assembly-load executor transition requests remain disabled by default until the isolated assembly-loader slice is implemented."),
                    Row("Assembly Resolver Plans", assemblyLoadResolverBlockedPlanCount + "/" + assemblyLoadResolverPlanCount, assemblyLoadResolverPlanCount == 0 ? "INFO" : "DEFERRED", assemblyLoadResolverBlockedPlanCount + " OpenACAI assembly-load resolver plan(s) are blocked before LoadFromAssemblyPath; assembly loads completed " + assemblyLoadResolverLoadedPlanCount + "." + FirstBoundaryDetail(firstBlockedAssemblyLoadResolverPlan == null ? null : firstBlockedAssemblyLoadResolverPlan.Detail)),
                    Row("Assembly Resolver Refs", assemblyLoadResolverResolvedReferenceCount + "/" + assemblyLoadResolverReferenceCount, assemblyLoadResolverUnresolvedReferenceCount == 0 ? "READY" : "CHECK", assemblyLoadResolverUnresolvedReferenceCount + " unresolved resolver-plan reference(s)."),
                    Row("Assembly Resolver Gate", InvocationAssemblyLoadResolverPlanEnabled ? "Enabled" : "Disabled", InvocationAssemblyLoadResolverPlanEnabled ? "READY" : "INFO", "Assembly-load resolver plans remain disabled by default and only record path/context/dependency metadata."),
                    Row("Assembly Load Ops", assemblyLoadBlockedOperationCount + "/" + assemblyLoadOperationCount, assemblyLoadOperationCount == 0 ? "INFO" : "DEFERRED", assemblyLoadBlockedOperationCount + " OpenACAI assembly-load operation(s) are blocked before context creation; started " + assemblyLoadStartedOperationCount + ", completed " + assemblyLoadCompletedOperationCount + ", loaded " + assemblyLoadLoadedOperationCount + "."),
                    Row("Assembly Operation Gate", InvocationAssemblyLoadOperationEnabled ? "Enabled" : "Disabled", InvocationAssemblyLoadOperationEnabled ? "READY" : "INFO", "Assembly-load operations remain disabled by default and do not call LoadFromAssemblyPath."),
                    Row("Assembly Context Plans", assemblyLoadBlockedContextPlanCount + "/" + assemblyLoadContextPlanCount, assemblyLoadContextPlanCount == 0 ? "INFO" : "DEFERRED", assemblyLoadBlockedContextPlanCount + " OpenACAI assembly-load context allocation plan(s) are blocked before AssemblyLoadContext allocation; contexts created " + assemblyLoadCreatedContextPlanCount + "."),
                    Row("Assembly Context Gate", InvocationAssemblyLoadContextPlanEnabled ? "Enabled" : "Disabled", InvocationAssemblyLoadContextPlanEnabled ? "READY" : "INFO", "Assembly-load context allocation plans remain disabled by default and do not create AssemblyLoadContext instances."),
                    Row("Assembly Context Allocs", assemblyLoadBlockedContextAllocationCount + "/" + assemblyLoadContextAllocationCount, assemblyLoadContextAllocationCount == 0 ? "INFO" : "DEFERRED", assemblyLoadBlockedContextAllocationCount + " OpenACAI AssemblyLoadContext allocation attempt(s) are blocked before constructor invocation; constructors called " + assemblyLoadConstructorCalledAllocationCount + ", contexts created " + assemblyLoadCreatedContextAllocationCount + "."),
                    Row("Assembly Allocation Gate", InvocationAssemblyLoadContextAllocationEnabled ? "Enabled" : "Disabled", InvocationAssemblyLoadContextAllocationEnabled ? "READY" : "INFO", "AssemblyLoadContext allocation attempts remain disabled by default and do not call the context constructor."),
                    Row("Assembly Context Tracks", assemblyLoadBlockedContextRegistrationCount + "/" + assemblyLoadContextRegistrationCount, assemblyLoadContextRegistrationCount == 0 ? "INFO" : "DEFERRED", assemblyLoadBlockedContextRegistrationCount + " OpenACAI AssemblyLoadContext registration boundary/boundaries are blocked before context tracking; tracked " + assemblyLoadTrackedContextRegistrationCount + ", unload registered " + assemblyLoadUnloadRegisteredContextRegistrationCount + "."),
                    Row("Assembly Registration Gate", InvocationAssemblyLoadContextRegistrationEnabled ? "Enabled" : "Disabled", InvocationAssemblyLoadContextRegistrationEnabled ? "READY" : "INFO", "AssemblyLoadContext registration remains disabled by default and does not track contexts or register unload cleanup."),
                    Row("Assembly Load Calls", assemblyLoadBlockedInvocationCount + "/" + assemblyLoadInvocationCount, assemblyLoadInvocationCount == 0 ? "INFO" : "DEFERRED", assemblyLoadBlockedInvocationCount + " Modstoke LoadFromAssemblyPath invocation boundary/boundaries are blocked before assembly load; started " + assemblyLoadStartedInvocationCount + ", completed " + assemblyLoadCompletedInvocationCount + ", loaded " + assemblyLoadLoadedInvocationCount + "."),
                    Row("Assembly Load Call Gate", InvocationAssemblyLoadInvocationEnabled ? "Enabled" : "Disabled", InvocationAssemblyLoadInvocationEnabled ? "READY" : "INFO", "Assembly-load invocation remains disabled by default and does not call LoadFromAssemblyPath."),
                    Row("Plugin Type Resolution", pluginTypeBlockedResolutionCount + "/" + pluginTypeResolutionCount, pluginTypeResolutionCount == 0 ? "INFO" : "DEFERRED", pluginTypeBlockedResolutionCount + " OpenACAI plugin Type resolution boundary/boundaries are blocked before Assembly.GetType; started " + pluginTypeStartedResolutionCount + ", completed " + pluginTypeCompletedResolutionCount + ", resolved " + pluginTypeResolvedResolutionCount + "."),
                    Row("Plugin Type Gate", InvocationPluginTypeResolutionEnabled ? "Enabled" : "Disabled", InvocationPluginTypeResolutionEnabled ? "READY" : "INFO", "Plugin type resolution remains disabled by default and does not call Assembly.GetType."),
                    Row("Plugin Instance Creation", pluginInstanceBlockedCreationCount + "/" + pluginInstanceCreationCount, pluginInstanceCreationCount == 0 ? "INFO" : "DEFERRED", pluginInstanceBlockedCreationCount + " OpenACAI plugin instance creation boundary/boundaries are blocked before Activator.CreateInstance; started " + pluginInstanceStartedCreationCount + ", completed " + pluginInstanceCompletedCreationCount + ", created " + pluginInstanceCreatedCreationCount + "."),
                    Row("Plugin Instance Gate", InvocationPluginInstanceCreationEnabled ? "Enabled" : "Disabled", InvocationPluginInstanceCreationEnabled ? "READY" : "INFO", "Plugin instance creation remains disabled by default and does not call Activator.CreateInstance."),
                    Row("Plugin Load Calls", pluginLoadBlockedInvocationCount + "/" + pluginLoadInvocationCount, pluginLoadInvocationCount == 0 ? "INFO" : "DEFERRED", pluginLoadBlockedInvocationCount + " OpenACAI BasePlugin.Load invocation boundary/boundaries are blocked before Load(); started " + pluginLoadStartedInvocationCount + ", completed " + pluginLoadCompletedInvocationCount + ", invoked " + pluginLoadInvokedInvocationCount + "."),
                    Row("Plugin Load Gate", InvocationPluginLoadInvocationEnabled ? "Enabled" : "Disabled", InvocationPluginLoadInvocationEnabled ? "READY" : "INFO", "BasePlugin.Load invocation remains disabled by default and does not call plugin entrypoints."),
                    Row("Probe Type Activation", activationProbeState == null ? "0" : activationProbeState.SuccessfulTypeActivationAttemptCount + "/" + activationProbeState.TypeActivationAttemptCount, activationProbeLatestType == null ? "INFO" : activationProbeLatestType.Status, activationProbeLatestType == null ? "No OpenACAI validation-probe type activation attempt has run." : "Validation probe assembly loaded " + activationProbeLatestType.AssemblyLoaded + ", type resolved " + activationProbeLatestType.PluginTypeResolved + ", constructor invoked " + activationProbeLatestType.ConstructorInvoked + ", Load() invoked " + activationProbeLatestType.LoadMethodInvoked + "."),
                    Row("Probe Instance Creation", activationProbeState == null ? "0" : activationProbeState.SuccessfulInstanceCreationAttemptCount + "/" + activationProbeState.InstanceCreationAttemptCount, activationProbeLatestInstance == null ? "INFO" : activationProbeLatestInstance.Status, activationProbeLatestInstance == null ? "No OpenACAI validation-probe instance creation attempt has run." : "Validation probe instance requested " + activationProbeLatestInstance.PluginInstanceCreationRequested + ", created " + activationProbeLatestInstance.PluginInstanceCreated + ", constructor invoked " + activationProbeLatestInstance.ConstructorInvoked + ", Load() invoked " + activationProbeLatestInstance.LoadMethodInvoked + "."),
                    Row("Probe Load Invocation", activationProbeState == null ? "0" : activationProbeState.SuccessfulLoadInvocationAttemptCount + "/" + activationProbeState.LoadInvocationAttemptCount, activationProbeLatestLoad == null ? "INFO" : activationProbeLatestLoad.Status, activationProbeLatestLoad == null ? "No OpenACAI validation-probe Load() invocation attempt has run." : "Validation probe Load() requested " + activationProbeLatestLoad.PluginLoadInvocationRequested + ", started " + activationProbeLatestLoad.PluginLoadInvocationStarted + ", completed " + activationProbeLatestLoad.PluginLoadInvocationCompleted + ", invoked " + activationProbeLatestLoad.LoadMethodInvoked + "."),
                    Row("Load Gate", snapshot.LoadGateOpenCount + " open", snapshot.LoadGateOpenCount == 0 ? "READY" : "CHECK", snapshot.LoadGateBlockedCount + " accepted compatibility candidate(s) blocked by the disabled runtime plugin load gate."),
                    Row("Load Attempts", snapshot.LoadAttemptRequestCount + " requested", snapshot.BlockedLoadAttemptCount == 0 ? "READY" : "INFO", snapshot.AcceptedLoadAttemptCount + " accepted, " + snapshot.RejectedLoadAttemptCount + " rejected, " + snapshot.BlockedLoadAttemptCount + " blocked before plugin invocation."),
                    Row("Modstoke Loads", snapshot.PluginLoadedByOpenAcaiCount.ToString(), snapshot.PluginLoadedByOpenAcaiCount == 0 ? "READY" : "CHECK", "OpenACAI does not load BepInEx plugin assemblies in this metadata gate."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Libraries stay in their own section and never appear as BepInEx plugin compatibility candidates."),
                    Row("Modstoke Native Excluded", snapshot.OpenAcaiNativeExcludedCount.ToString(), "READY", "Modstoke-native mods remain in the native mod host path."),
                    Row("RedLoader Excluded", snapshot.RedLoaderCompatibilityExcludedCount.ToString(), "READY", "Legacy RedLoader mods remain in the RedLoader compatibility path.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateParserHarnessSection(OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
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
                rows.Add(Row("No Parser Harness", "0", "INFO", "No BepInPlugin parser harness DLLs were discovered under OpenACAI/ValidationProbes/BepInExPluginMetadataHarness."));
            }

            return new OpenACAIMenuSectionSnapshot("Parser Harness", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateCandidateSection(OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            var validationCandidateCount = snapshot.Candidates.Count(candidate => candidate != null && candidate.ValidationProbe);
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Candidates", snapshot.CandidateCount.ToString(), snapshot.CompatibilityIssueCount == 0 ? "READY" : "CHECK", snapshot.CompatibilityIssueCount + " compatibility issue(s)."),
                Row("Validation Probes", validationCandidateCount.ToString(), "INFO", "Validation-probe candidates stay under OpenACAI/ValidationProbes/BepInExPluginCandidateHarness and do not appear in the public mod inventory."),
                Row("Candidate References", snapshot.ResolvedAssemblyReferenceCount + "/" + snapshot.AssemblyReferenceCount, snapshot.UnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.UnresolvedAssemblyReferenceCount + " unresolved BepInEx plugin candidate assembly reference(s)." + FirstUnresolvedDetail(snapshot.FirstUnresolvedCandidateAssemblyReference)),
                Row("Candidate Execution Plans", snapshot.ExecutionPlanReadyCount + "/" + snapshot.ExecutionPlanCount, snapshot.ExecutionPlanIssueCount == 0 && snapshot.LifecycleMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.LifecycleMethodReadyCount + "/" + snapshot.LifecycleMethodCount + " lifecycle/entrypoint method(s) ready." + FirstExecutionPlanIssueDetail(snapshot.FirstExecutionPlanIssue, snapshot.FirstLifecycleMethodIssue))
            };

            foreach (var candidate in snapshot.Candidates.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(candidate.PluginName) ? candidate.DisplayName : candidate.PluginName,
                    string.IsNullOrWhiteSpace(candidate.PluginGuid) ? candidate.AssemblyName : candidate.PluginGuid,
                    candidate.Status,
                    candidate.Detail + " Candidate " + candidate.CandidateId + "; type " + candidate.PluginTypeName + "; assembly " + candidate.AssemblyPath + "; loaded by OpenACAI " + candidate.LoadedByOpenAcai + "; source " + candidate.CandidateSource + "; validation probe " + candidate.ValidationProbe + "; BepInPlugin attributes " + candidate.BepInExPluginAttributeCount + "; assembly metadata " + (candidate.AssemblyMetadataReadable ? "read" : "not read") + "; references " + candidate.ResolvedAssemblyReferenceCount + "/" + candidate.AssemblyReferenceCount + ";" + ExecutionPlanDetail(candidate.ExecutionPlan) + " load gate " + (candidate.LoadGateOpen ? "open" : candidate.LoadGateBlocked ? "blocked" : "closed") + "." + FirstUnresolvedDetail(candidate.FirstUnresolvedAssemblyReference)));
            }

            if (snapshot.CandidateCount == 0)
            {
                rows.Add(Row("No BepInEx Plugins", "0", "INFO", "No legacy BepInEx plugin candidates were discovered outside OpenACAI support folders."));
            }

            return new OpenACAIMenuSectionSnapshot("BepInEx Candidates", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreatePlanSection(OpenACAIBepInExPluginCompatibilitySnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
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
                    attempt.Detail + " Source " + (string.IsNullOrWhiteSpace(attempt.PlanSource) ? LoadPlanSourceUnknown : attempt.PlanSource) + "; candidate source " + (attempt.Candidate == null ? "none" : attempt.Candidate.CandidateSource) + "; validation only " + attempt.ValidationOnly + "; failure isolated " + attempt.FailureIsolated + "; execution started " + attempt.ExecutionStarted + "; stage attempts blocked " + attempt.BlockedInvocationStageAttemptCount + "/" + attempt.RecordedInvocationStageAttemptCount + "; executor transitions blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedTransitionCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.TransitionCount) + "; executor requests blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedTransitionRequestCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.TransitionRequestCount) + "; resolver plans blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadResolverPlanCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadResolverPlanCount) + "; load operations blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadOperationCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadOperationCount) + "; context plans blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadContextPlanCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadContextPlanCount) + "; context allocations blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadContextAllocationCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadContextAllocationCount) + "; context registrations blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadContextRegistrationCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadContextRegistrationCount) + "; load invocations blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedAssemblyLoadInvocationCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadInvocationCount) + "; type resolutions blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedPluginTypeResolutionCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.PluginTypeResolutionCount) + "; instance creations blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedPluginInstanceCreationCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.PluginInstanceCreationCount) + "; plugin load invocations blocked " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.BlockedPluginLoadInvocationCount) + "/" + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.PluginLoadInvocationCount) + "; assembly loads " + (attempt.InvocationExecutor == null ? 0 : attempt.InvocationExecutor.AssemblyLoadedTransitionRequestCount) + "." + InvocationPreflightDetail(attempt.InvocationPreflight)));
            }

            return new OpenACAIMenuSectionSnapshot("Load Plans", rows);
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

        private static string FirstUnresolvedDetail(OpenACAIBepInExPluginAssemblyReferenceSnapshot reference)
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
            OpenACAIBepInExPluginExecutionPlanSnapshot plan,
            OpenACAIBepInExPluginLifecycleMethodSnapshot method)
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

        private static string ExecutionPlanDetail(OpenACAIBepInExPluginExecutionPlanSnapshot plan)
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

        private static string InvocationPreflightDetail(OpenACAIBepInExPluginInvocationPreflightSnapshot preflight)
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

        private static string ActivationPolicyDetail(OpenACAIBepInExPluginActivationPolicySnapshot policy)
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

        private static OpenACAIBepInExPluginActivationPolicySnapshot ActivationPolicyForPlan(OpenACAIBepInExPluginCompatibilityPlanSnapshot plan)
        {
            return plan == null || plan.InvocationPreflight == null
                ? null
                : plan.InvocationPreflight.ActivationPolicy;
        }

        private static string AssemblyActivationPolicyBlockReason(OpenACAIBepInExPluginActivationPolicySnapshot policy)
        {
            if (policy == null || !policy.Available)
            {
                return "activation-policy-unavailable";
            }

            if (policy.AssemblyLoadBlocked && !string.IsNullOrWhiteSpace(policy.AssemblyLoadBlockReasonCode))
            {
                return policy.AssemblyLoadBlockReasonCode;
            }

            if (policy.ConstructorInvocationBlocked && !string.IsNullOrWhiteSpace(policy.ConstructorInvocationBlockReasonCode))
            {
                return policy.ConstructorInvocationBlockReasonCode;
            }

            if (policy.LoadInvocationBlocked && !string.IsNullOrWhiteSpace(policy.LoadInvocationBlockReasonCode))
            {
                return policy.LoadInvocationBlockReasonCode;
            }

            if (policy.RealPluginExecutionAllowed)
            {
                return "real-plugin-execution-allowed";
            }

            return string.IsNullOrWhiteSpace(policy.IssueCode)
                ? "activation-policy-blocked"
                : policy.IssueCode;
        }

        private static string AssemblyActivationPolicyDetail(OpenACAIBepInExPluginActivationPolicySnapshot policy)
        {
            if (policy == null || !policy.Available)
            {
                return " Activation policy unavailable.";
            }

            return " Activation policy " + policy.Status
                + " blocks assembly load " + policy.AssemblyLoadBlocked
                + ", constructor " + policy.ConstructorInvocationBlocked
                + ", Load " + policy.LoadInvocationBlocked
                + "; reason " + AssemblyActivationPolicyBlockReason(policy) + ".";
        }

        private static string FirstBoundaryDetail(string detail)
        {
            return string.IsNullOrWhiteSpace(detail)
                ? ""
                : " First blocked detail: " + detail;
        }

        private static bool DetailCarriesActivationPolicyReason(string detail)
        {
            return !string.IsNullOrWhiteSpace(detail)
                && detail.Contains("Activation policy", StringComparison.Ordinal)
                && detail.Contains("runtime-plugin-loading-disabled", StringComparison.Ordinal);
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider, IOpenACAICommandProvider
        {
            public string Id => OpenACAIMenuProviderIds.BepInExPluginCompatibility;
            public string DisplayName => "Modstoke BepInEx Gate";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 705;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned BepInEx plugin compatibility metadata gate state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAIBepInExPluginCompatibility.CreateContent();
            }

            public IReadOnlyList<OpenACAICommandSnapshot> GetCommands()
            {
                return OpenACAIBepInExPluginCompatibility.CreateCommands();
            }

            public bool TryInvokeCommand(string commandId, out OpenACAICommandResultSnapshot result)
            {
                return OpenACAIBepInExPluginCompatibility.TryInvokeCommand(commandId, out result);
            }
        }

        // Ancestry needs the generic definition, not substituted argument types or runtime Type objects.
        private sealed class BepInExBaseTypeSignatureProvider : ISignatureTypeProvider<EntityHandle, int>
        {
            internal static readonly BepInExBaseTypeSignatureProvider Instance = new BepInExBaseTypeSignatureProvider();

            public EntityHandle GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => handle;
            public EntityHandle GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;
            public EntityHandle GetGenericInstantiation(EntityHandle genericType, ImmutableArray<EntityHandle> typeArguments) => genericType;
            public EntityHandle GetModifiedType(EntityHandle modifier, EntityHandle unmodifiedType, bool isRequired) => unmodifiedType;

            public EntityHandle GetTypeFromSpecification(MetadataReader reader, int genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                if (genericContext >= MaxExternalBaseChainDepth)
                    throw new BadImageFormatException("Base-type signature traversal exceeded the OpenACAI depth guard.");
                return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext + 1);
            }

            public EntityHandle GetArrayType(EntityHandle elementType, ArrayShape shape) => default;
            public EntityHandle GetSZArrayType(EntityHandle elementType) => default;
            public EntityHandle GetByReferenceType(EntityHandle elementType) => default;
            public EntityHandle GetPointerType(EntityHandle elementType) => default;
            public EntityHandle GetPinnedType(EntityHandle elementType) => default;
            public EntityHandle GetFunctionPointerType(MethodSignature<EntityHandle> signature) => default;
            public EntityHandle GetGenericMethodParameter(int genericContext, int index) => default;
            public EntityHandle GetGenericTypeParameter(int genericContext, int index) => default;
            public EntityHandle GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
        }

        private sealed class BepInExPluginMethodMetadata
        {
            internal BepInExPluginMethodMetadata(string methodName, bool isStatic, bool isPublic, int parameterCount)
            {
                MethodName = methodName ?? "";
                IsStatic = isStatic;
                IsPublic = isPublic;
                ParameterCount = Math.Max(0, parameterCount);
            }

            internal string MethodName { get; }
            internal bool IsStatic { get; }
            internal bool IsPublic { get; }
            internal int ParameterCount { get; }
        }

        private sealed class BepInExBaseChainMetadata
        {
            private BepInExBaseChainMetadata(
                bool found,
                string matchTypeName,
                int matchDepth,
                bool externalAssemblyResolved,
                string externalAssemblyName,
                string externalAssemblyPath,
                string externalAssemblySource,
                string detail)
            {
                Found = found;
                MatchTypeName = matchTypeName ?? "";
                MatchDepth = Math.Max(0, matchDepth);
                ExternalAssemblyResolved = externalAssemblyResolved;
                ExternalAssemblyName = externalAssemblyName ?? "";
                ExternalAssemblyPath = externalAssemblyPath ?? "";
                ExternalAssemblySource = externalAssemblySource ?? "";
                Detail = detail ?? "";
            }

            internal bool Found { get; }
            internal string MatchTypeName { get; }
            internal int MatchDepth { get; }
            internal bool ExternalAssemblyResolved { get; }
            internal string ExternalAssemblyName { get; }
            internal string ExternalAssemblyPath { get; }
            internal string ExternalAssemblySource { get; }
            internal string Detail { get; }

            internal BepInExBaseChainMetadata WithExternalAssembly(
                string assemblyName,
                string assemblyPath,
                string assemblySource,
                string detail)
            {
                return new BepInExBaseChainMetadata(
                    Found,
                    MatchTypeName,
                    MatchDepth,
                    true,
                    assemblyName,
                    assemblyPath,
                    assemblySource,
                    detail);
            }

            internal static BepInExBaseChainMetadata CreateFound(string matchTypeName, int matchDepth, string detail)
            {
                return new BepInExBaseChainMetadata(
                    true,
                    matchTypeName,
                    matchDepth,
                    false,
                    "",
                    "",
                    "",
                    detail);
            }

            internal static BepInExBaseChainMetadata Missing(string detail)
            {
                return new BepInExBaseChainMetadata(
                    false,
                    "",
                    0,
                    false,
                    "",
                    "",
                    "",
                    detail);
            }
        }

        private sealed class BepInExPluginAssemblyMetadata
        {
            internal BepInExPluginAssemblyMetadata(
                bool readable,
                string issueCode,
                string detail,
                string assemblyName,
                string assemblyVersion,
                IReadOnlyList<OpenACAIBepInExPluginAssemblyReferenceSnapshot> assemblyReferences,
                int pluginAttributeCount,
                string pluginGuid,
                string pluginName,
                string pluginVersion,
                string pluginTypeName,
                OpenACAIBepInExPluginExecutionPlanSnapshot executionPlan)
            {
                Readable = readable;
                IssueCode = issueCode ?? "";
                Detail = detail ?? "";
                AssemblyName = assemblyName ?? "";
                AssemblyVersion = assemblyVersion ?? "";
                AssemblyReferences = assemblyReferences ?? Array.Empty<OpenACAIBepInExPluginAssemblyReferenceSnapshot>();
                AssemblyReferenceCount = AssemblyReferences.Count;
                ResolvedAssemblyReferenceCount = AssemblyReferences.Count(reference => reference.Resolved);
                UnresolvedAssemblyReferenceCount = AssemblyReferences.Count(reference => !reference.Resolved);
                PluginAttributeCount = Math.Max(0, pluginAttributeCount);
                PluginGuid = pluginGuid ?? "";
                PluginName = pluginName ?? "";
                PluginVersion = pluginVersion ?? "";
                PluginTypeName = pluginTypeName ?? "";
                ExecutionPlan = executionPlan ?? OpenACAIBepInExPluginExecutionPlanSnapshot.CreateUnavailable(pluginTypeName, "execution-plan-unavailable", "BepInEx plugin execution plan metadata was not available.");
            }

            internal bool Readable { get; }
            internal string IssueCode { get; }
            internal string Detail { get; }
            internal string AssemblyName { get; }
            internal string AssemblyVersion { get; }
            internal IReadOnlyList<OpenACAIBepInExPluginAssemblyReferenceSnapshot> AssemblyReferences { get; }
            internal int AssemblyReferenceCount { get; }
            internal int ResolvedAssemblyReferenceCount { get; }
            internal int UnresolvedAssemblyReferenceCount { get; }
            internal int PluginAttributeCount { get; }
            internal string PluginGuid { get; }
            internal string PluginName { get; }
            internal string PluginVersion { get; }
            internal string PluginTypeName { get; }
            internal OpenACAIBepInExPluginExecutionPlanSnapshot ExecutionPlan { get; }

            internal static BepInExPluginAssemblyMetadata Missing(string issueCode, string detail)
            {
                return new BepInExPluginAssemblyMetadata(
                    false,
                    issueCode,
                    detail,
                    "",
                    "",
                    Array.Empty<OpenACAIBepInExPluginAssemblyReferenceSnapshot>(),
                    0,
                    "",
                    "",
                    "",
                    "",
                    OpenACAIBepInExPluginExecutionPlanSnapshot.CreateUnavailable("", issueCode, detail));
            }
        }
    }
}
