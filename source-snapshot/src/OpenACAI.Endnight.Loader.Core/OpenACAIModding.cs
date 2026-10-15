// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public Modstoke SDK surface for Sons Of The Forest mods.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAIModding
    {
        public const string ApiVersion = "0.1";
        public const string LoaderName = "Modstoke Loader";
        public const string RuntimeServiceKey = "openacai.loader.runtime";
        public const string ModInventoryServiceKey = "openacai.loader.modInventory";
        public const string ModInventoryCatalogServiceKey = "openacai.loader.modInventoryCatalog";
        public const string FileIntegrityServiceKey = "openacai.loader.fileIntegrity";
        public const string RuntimeAttestationServiceKey = "openacai.loader.runtimeAttestation";
        public const string CompatibilityDependenciesServiceKey = "openacai.loader.compatibilityDependencies";
        public const string BepInExPluginCompatibilityServiceKey = "openacai.loader.bepInExPluginCompatibility";
        public const string ChainloaderServiceKey = "openacai.loader.chainloader";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, object> Services = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> EventErrorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static OpenACAIRuntimeSnapshot _runtime = OpenACAIRuntimeSnapshot.CreateUnknown();
        private static OpenACAIModInventorySnapshot _modInventory = OpenACAIModInventorySnapshot.Empty();
        private static OpenACAIModInventoryCatalogSnapshot _modInventoryCatalog = OpenACAIModInventoryCatalogSnapshot.Empty();

        public static event Action<OpenACAILifecycleEvent> Lifecycle;
        public static event Action<OpenACAISceneEvent> SceneChanged;
        public static event Action<OpenACAIModInventorySnapshot> ModInventoryChanged;
        public static event Action<string> StartupWarning;

        public static OpenACAIRuntimeSnapshot Runtime
        {
            get
            {
                lock (SyncRoot)
                {
                    return _runtime;
                }
            }
        }

        public static OpenACAIModInventorySnapshot ModInventory
        {
            get
            {
                lock (SyncRoot)
                {
                    return _modInventory;
                }
            }
        }

        public static OpenACAIModInventoryCatalogSnapshot ModInventoryCatalog
        {
            get
            {
                lock (SyncRoot)
                {
                    return _modInventoryCatalog;
                }
            }
        }

        public static OpenACAIRuntimeAttestationSnapshot RuntimeAttestation => OpenACAIRuntimeAttestation.Snapshot;
        public static OpenACAIChainloaderStateSnapshot Chainloader => OpenACAIChainloaderStatus.State;
        public static OpenACAICompatibilityDependencySnapshot CompatibilityDependencies => OpenACAICompatibilityDependencies.Snapshot;
        public static OpenACAIBepInExPluginCompatibilitySnapshot BepInExPluginCompatibility => OpenACAIBepInExPluginCompatibility.State;
        public static OpenACAINativeModHostSnapshot NativeModHost => OpenACAINativeModHost.State;
        public static OpenACAINativeModRuntimeSnapshot NativeModRuntime => OpenACAINativeModRuntime.State;
        public static OpenACAINativeLifecycleRegistrySnapshot NativeLifecycleRegistry => OpenACAINativeLifecycleRegistry.State;
        public static OpenACAINativeLifecycleDispatcherSnapshot NativeLifecycleDispatcher => OpenACAINativeLifecycleDispatcher.State;
        public static OpenACAINativeLifecycleInvocationBridgeSnapshot NativeLifecycleInvocationBridge => OpenACAINativeLifecycleInvocationBridge.State;
        public static OpenACAINativeLifecycleInvocationAdapterSnapshot NativeLifecycleInvocationAdapter => OpenACAINativeLifecycleInvocationAdapter.State;
        public static OpenACAINativeLifecycleInvocationTokenResolverSnapshot NativeLifecycleInvocationTokenResolver => OpenACAINativeLifecycleInvocationTokenResolver.State;
        public static OpenACAINativeLifecycleInvocationJournalSnapshot NativeLifecycleInvocationJournal => OpenACAINativeLifecycleInvocationJournal.State;
        public static OpenACAINativeLifecycleInvocationSchedulerSnapshot NativeLifecycleInvocationScheduler => OpenACAINativeLifecycleInvocationScheduler.State;
        public static OpenACAINativeLifecycleInvocationExecutorSnapshot NativeLifecycleInvocationExecutor => OpenACAINativeLifecycleInvocationExecutor.State;

        public static IReadOnlyList<OpenACAIModInfo> GetMods()
        {
            return ModInventory.Mods;
        }

        public static IReadOnlyList<OpenACAIModInfo> GetLibraries()
        {
            return ModInventory.Libraries;
        }

        public static IReadOnlyList<OpenACAIModInfo> GetCompatibilityEntries()
        {
            return ModInventory.CompatibilityEntries;
        }

        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> GetBepInExPluginCompatibilityCandidates()
        {
            return OpenACAIBepInExPluginCompatibility.GetCandidates();
        }

        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityPlanSnapshot> GetBepInExPluginCompatibilityPlans()
        {
            return OpenACAIBepInExPluginCompatibility.GetLoadPlans();
        }

        public static IReadOnlyList<OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot> GetBepInExPluginCompatibilityLoadAttempts()
        {
            return OpenACAIBepInExPluginCompatibility.GetLoadAttempts();
        }

        public static OpenACAIBepInExPluginCompatibilityPlanSnapshot PlanBepInExPluginCompatibility(string idOrAssemblyName)
        {
            return OpenACAIBepInExPluginCompatibility.PlanLoad(idOrAssemblyName);
        }

        public static OpenACAIBepInExPluginCompatibilityLoadAttemptSnapshot AttemptBepInExPluginCompatibilityLoad(string idOrAssemblyName)
        {
            return OpenACAIBepInExPluginCompatibility.AttemptLoad(idOrAssemblyName);
        }

        public static IReadOnlyList<OpenACAINativeLifecycleDispatchTargetSnapshot> GetNativeLifecycleDispatchTargets()
        {
            return OpenACAINativeLifecycleDispatcher.GetDispatchTargets();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleDispatchIntentSnapshot> GetNativeLifecycleDispatchIntentions()
        {
            return OpenACAINativeLifecycleDispatcher.GetDispatchIntentions();
        }

        public static OpenACAINativeLifecycleDispatchPlan PlanNativeLifecycleDispatch(string hookName)
        {
            return OpenACAINativeLifecycleDispatcher.PlanDispatch(hookName);
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationRequestSnapshot> GetNativeLifecycleInvocationRequests()
        {
            return OpenACAINativeLifecycleInvocationBridge.GetInvocationRequests();
        }

        public static OpenACAINativeLifecycleInvocationPlan PlanNativeLifecycleInvocation(string hookName)
        {
            return OpenACAINativeLifecycleInvocationBridge.PlanInvocation(hookName);
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationBindingSnapshot> GetNativeLifecycleInvocationBindings()
        {
            return OpenACAINativeLifecycleInvocationAdapter.GetBindings();
        }

        public static IReadOnlyList<string> GetNativeLifecycleInvocationBindingTokens()
        {
            return OpenACAINativeLifecycleInvocationAdapter.GetBindingTokens();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationTokenResolutionSnapshot> GetNativeLifecycleInvocationTokenResolutions()
        {
            return OpenACAINativeLifecycleInvocationTokenResolver.GetTokenResolutions();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationTokenLookupSnapshot> GetNativeLifecycleInvocationTokenLookupResults()
        {
            return OpenACAINativeLifecycleInvocationTokenResolver.GetLookupResults();
        }

        public static OpenACAINativeLifecycleInvocationTokenLookupSnapshot LookupNativeLifecycleInvocationToken(string bindingToken)
        {
            return OpenACAINativeLifecycleInvocationTokenResolver.LookupBindingToken(bindingToken);
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationJournalEntrySnapshot> GetNativeLifecycleInvocationJournalEntries()
        {
            return OpenACAINativeLifecycleInvocationJournal.GetJournalEntries();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationReplayPlanSnapshot> GetNativeLifecycleInvocationReplayPlans()
        {
            return OpenACAINativeLifecycleInvocationJournal.GetReplayPlans();
        }

        public static OpenACAINativeLifecycleInvocationReplayPlanSnapshot PlanNativeLifecycleInvocationReplay(string bindingToken)
        {
            return OpenACAINativeLifecycleInvocationJournal.PlanReplay(bindingToken);
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleBatchSnapshot> GetNativeLifecycleInvocationScheduleBatches()
        {
            return OpenACAINativeLifecycleInvocationScheduler.GetScheduleBatches();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationScheduleEntrySnapshot> GetNativeLifecycleInvocationScheduleEntries()
        {
            return OpenACAINativeLifecycleInvocationScheduler.GetScheduleEntries();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationSchedulePlanSnapshot> GetNativeLifecycleInvocationSchedulePlans()
        {
            return OpenACAINativeLifecycleInvocationScheduler.GetSchedulePlans();
        }

        public static OpenACAINativeLifecycleInvocationSchedulePlanSnapshot PlanNativeLifecycleInvocationSchedule(string hookName)
        {
            return OpenACAINativeLifecycleInvocationScheduler.PlanSchedule(hookName);
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionBatchSnapshot> GetNativeLifecycleInvocationExecutionBatches()
        {
            return OpenACAINativeLifecycleInvocationExecutor.GetExecutionBatches();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionDecisionSnapshot> GetNativeLifecycleInvocationExecutionDecisions()
        {
            return OpenACAINativeLifecycleInvocationExecutor.GetExecutionDecisions();
        }

        public static IReadOnlyList<OpenACAINativeLifecycleInvocationExecutionPlanSnapshot> GetNativeLifecycleInvocationExecutionPlans()
        {
            return OpenACAINativeLifecycleInvocationExecutor.GetExecutionPlans();
        }

        public static OpenACAINativeLifecycleInvocationExecutionPlanSnapshot PlanNativeLifecycleInvocationExecution(string hookName)
        {
            return OpenACAINativeLifecycleInvocationExecutor.PlanExecution(hookName);
        }

        public static bool TryResolveNativeLifecycleInvocationToken(string bindingToken, out OpenACAINativeLifecycleInvocationTokenResolutionSnapshot resolution)
        {
            return OpenACAINativeLifecycleInvocationTokenResolver.TryResolveBindingToken(bindingToken, out resolution);
        }

        public static bool TryFindInventoryEntry(string idOrName, out OpenACAIModInfo entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(idOrName))
            {
                return false;
            }

            var needle = idOrName.Trim();
            var inventory = ModInventory;
            entry = inventory.Entries.FirstOrDefault(candidate => string.Equals(candidate.Id, needle, StringComparison.OrdinalIgnoreCase))
                ?? inventory.Entries.FirstOrDefault(candidate => string.Equals(candidate.Name, needle, StringComparison.OrdinalIgnoreCase))
                ?? inventory.Entries.FirstOrDefault(candidate => string.Equals(candidate.DisplayName, needle, StringComparison.OrdinalIgnoreCase))
                ?? inventory.Entries.FirstOrDefault(candidate => string.Equals(PathFileName(candidate.AssemblyPath), needle, StringComparison.OrdinalIgnoreCase));
            return entry != null;
        }

        public static bool TryGetPath(string key, out string path)
        {
            path = null;
            var runtime = Runtime;
            if (runtime.Paths == null || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return runtime.Paths.TryGetValue(key, out path) && !string.IsNullOrWhiteSpace(path);
        }

        public static bool TryRegisterService(string key, object service)
        {
            if (string.IsNullOrWhiteSpace(key) || service == null)
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (Services.ContainsKey(key))
                {
                    return false;
                }

                Services[key] = service;
                return true;
            }
        }

        public static bool TryGetService<T>(string key, out T service)
        {
            service = default(T);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (SyncRoot)
            {
                object candidate;
                if (!Services.TryGetValue(key, out candidate) || !(candidate is T))
                {
                    return false;
                }

                service = (T)candidate;
                return true;
            }
        }

        public static bool TryUnregisterService(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (SyncRoot)
            {
                return Services.Remove(key);
            }
        }

        internal static void SetService(string key, object service)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (SyncRoot)
            {
                if (service == null)
                {
                    Services.Remove(key);
                }
                else
                {
                    Services[key] = service;
                }
            }
        }

        public static OpenACAIModInventorySnapshot RefreshModInventory()
        {
            var inventory = OpenACAIEndnightRuntime.RefreshModInventory();
            return PublishModInventory(inventory);
        }

        internal static void MarkReady(
            bool nativeRedLoaderUnitySupportActive,
            int hostedRedLoaderModCount,
            IReadOnlyDictionary<string, string> paths,
            IEnumerable<string> warnings)
        {
            var snapshot = new OpenACAIRuntimeSnapshot(
                ApiVersion,
                LoaderName,
                OpenACAIEndnightRuntime.Version,
                OpenACAILoaderCapabilities.CompatibilityMode,
                true,
                false,
                nativeRedLoaderUnitySupportActive,
                hostedRedLoaderModCount,
                null,
                Environment.Version.ToString(),
                paths,
                warnings,
                OpenACAIRuntime.CompatibilityShimsSnapshot());

            SetRuntime(snapshot);
            PublishLifecycle("LoaderReady");
        }

        internal static void MarkFailed(
            string startupError,
            IReadOnlyDictionary<string, string> paths,
            IEnumerable<string> warnings)
        {
            var snapshot = new OpenACAIRuntimeSnapshot(
                ApiVersion,
                LoaderName,
                OpenACAIEndnightRuntime.Version,
                OpenACAILoaderCapabilities.CompatibilityMode,
                false,
                true,
                false,
                0,
                startupError,
                Environment.Version.ToString(),
                paths,
                warnings,
                OpenACAIRuntime.CompatibilityShimsSnapshot());

            SetRuntime(snapshot);
            PublishLifecycle("LoaderFailed");
        }

        internal static OpenACAIModInventorySnapshot PublishModInventory(ModInventoryState inventory)
        {
            var snapshot = OpenACAIModInventorySnapshot.FromInternal(inventory);
            var catalog = OpenACAIModInventoryCatalogSnapshot.FromInventory(snapshot);
            Action<OpenACAIModInventorySnapshot> handlers;
            lock (SyncRoot)
            {
                _modInventory = snapshot;
                _modInventoryCatalog = catalog;
                Services[ModInventoryServiceKey] = snapshot;
                Services[ModInventoryCatalogServiceKey] = catalog;
                handlers = ModInventoryChanged;
            }

            Dispatch("ModInventoryChanged", handlers, snapshot);
            return snapshot;
        }

        internal static void PublishScene(string phase, int buildIndex, string name)
        {
            Action<OpenACAISceneEvent> handlers;
            var evt = new OpenACAISceneEvent(phase, buildIndex, name, DateTimeOffset.UtcNow);
            lock (SyncRoot)
            {
                handlers = SceneChanged;
            }

            Dispatch("SceneChanged", handlers, evt);
        }

        internal static void PublishLifecycle(string name)
        {
            Action<OpenACAILifecycleEvent> handlers;
            var evt = new OpenACAILifecycleEvent(name, DateTimeOffset.UtcNow);
            lock (SyncRoot)
            {
                handlers = Lifecycle;
            }

            Dispatch("Lifecycle." + name, handlers, evt);
        }

        internal static void PublishStartupWarning(string warning)
        {
            Action<string> handlers;
            lock (SyncRoot)
            {
                handlers = StartupWarning;
            }

            Dispatch("StartupWarning", handlers, warning);
        }

        private static void SetRuntime(OpenACAIRuntimeSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _runtime = snapshot ?? OpenACAIRuntimeSnapshot.CreateUnknown();
                Services[RuntimeServiceKey] = _runtime;
                Services[ModInventoryServiceKey] = _modInventory;
                Services[ModInventoryCatalogServiceKey] = _modInventoryCatalog;
                Services[OpenACAIRuntime.CompatibilityShimServiceKey] = _runtime.CompatibilityShims;
            }
        }

        private static string PathFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return System.IO.Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return null;
            }
        }

        internal static void Dispatch<T>(string channel, Action<T> handlers, T value)
        {
            if (handlers == null)
            {
                return;
            }

            // Invoke the captured subscriber list outside service locks. A failing mod
            // must not prevent later subscribers from receiving the same event.
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(value);
                }
                catch (Exception ex)
                {
                    var count = 1;
                    lock (SyncRoot)
                    {
                        if (EventErrorCounts.TryGetValue(channel, out var existing))
                        {
                            count = existing + 1;
                        }

                        EventErrorCounts[channel] = count;
                    }

                    if (count == 1 || count % 300 == 0)
                    {
                        try
                        {
                            OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK event '" + channel + "' subscriber failed" + (count == 1 ? "" : " " + count + " times") + ": " + ex.Message);
                        }
                        catch (Exception loggingError)
                        {
                            OpenACAIEndnightRuntime.TraceCompat("SDK event failure logging failed for " + channel + ": " + loggingError.GetType().FullName);
                        }
                    }
                }
            }
        }
    }

    public sealed class OpenACAIRuntimeSnapshot
    {
        private readonly IReadOnlyDictionary<string, string> _paths;
        private readonly IReadOnlyList<string> _warnings;
        private readonly IReadOnlyDictionary<string, OpenACAICompatibilityShimSnapshot> _compatibilityShims;

        public OpenACAIRuntimeSnapshot(
            string apiVersion,
            string loaderName,
            string loaderVersion,
            string compatibilityMode,
            bool isReady,
            bool startupFailed,
            bool nativeRedLoaderUnitySupportActive,
            int hostedRedLoaderModCount,
            string startupError,
            string dotnetRuntimeVersion,
            IReadOnlyDictionary<string, string> paths,
            IEnumerable<string> warnings,
            IReadOnlyDictionary<string, OpenACAICompatibilityShimSnapshot> compatibilityShims)
        {
            ApiVersion = apiVersion;
            LoaderName = loaderName;
            LoaderVersion = loaderVersion;
            CompatibilityMode = compatibilityMode;
            IsReady = isReady;
            StartupFailed = startupFailed;
            NativeRedLoaderUnitySupportActive = nativeRedLoaderUnitySupportActive;
            HostedRedLoaderModCount = hostedRedLoaderModCount;
            StartupError = startupError;
            DotnetRuntimeVersion = dotnetRuntimeVersion;
            _paths = new Dictionary<string, string>(paths ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            _warnings = (warnings ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            _compatibilityShims = new Dictionary<string, OpenACAICompatibilityShimSnapshot>(
                compatibilityShims ?? new Dictionary<string, OpenACAICompatibilityShimSnapshot>(),
                StringComparer.OrdinalIgnoreCase);
        }

        public string ApiVersion { get; }
        public string LoaderName { get; }
        public string LoaderVersion { get; }
        public string CompatibilityMode { get; }
        public bool IsReady { get; }
        public bool StartupFailed { get; }
        public bool NativeRedLoaderUnitySupportActive { get; }
        public int HostedRedLoaderModCount { get; }
        public string StartupError { get; }
        public string DotnetRuntimeVersion { get; }
        public IReadOnlyDictionary<string, string> Paths => _paths;
        public IReadOnlyList<string> Warnings => _warnings;
        public IReadOnlyDictionary<string, OpenACAICompatibilityShimSnapshot> CompatibilityShims => _compatibilityShims;

        internal static OpenACAIRuntimeSnapshot CreateUnknown()
        {
            return new OpenACAIRuntimeSnapshot(
                OpenACAIModding.ApiVersion,
                OpenACAIModding.LoaderName,
                OpenACAIEndnightRuntime.Version,
                OpenACAILoaderCapabilities.CompatibilityMode,
                false,
                false,
                false,
                0,
                null,
                Environment.Version.ToString(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<string>(),
                OpenACAIRuntime.CompatibilityShimsSnapshot());
        }
    }

    public sealed class OpenACAIModInventorySnapshot
    {
        private readonly IReadOnlyList<OpenACAIModInfo> _entries;
        private readonly IReadOnlyList<OpenACAIModInfo> _mods;
        private readonly IReadOnlyList<OpenACAIModInfo> _libraries;
        private readonly IReadOnlyList<OpenACAIModInfo> _openAcaiMods;
        private readonly IReadOnlyList<OpenACAIModInfo> _legacyRedLoaderMods;
        private readonly IReadOnlyList<OpenACAIModInfo> _legacyBepInExPlugins;
        private readonly IReadOnlyList<OpenACAIModInfo> _compatibilityEntries;
        private readonly IReadOnlyList<OpenACAIModInfo> _hostableEntries;
        private readonly IReadOnlyList<OpenACAIModInfo> _hostedEntries;
        private readonly IReadOnlyList<OpenACAIModInfo> _issueEntries;

        private OpenACAIModInventorySnapshot(
            string generatedUtc,
            bool vortexDeploymentDetected,
            string vortexStagingPath,
            OpenACAIModInventorySummary summary,
            OpenACAIModInventoryIssues issues,
            IEnumerable<OpenACAIModInfo> entries)
        {
            GeneratedUtc = generatedUtc;
            VortexDeploymentDetected = vortexDeploymentDetected;
            VortexStagingPath = vortexStagingPath;
            Summary = summary ?? OpenACAIModInventorySummary.Empty();
            Issues = issues ?? OpenACAIModInventoryIssues.Empty();
            _entries = (entries ?? Array.Empty<OpenACAIModInfo>()).ToArray();
            _libraries = _entries.Where(entry => entry.IsLibrary).ToArray();
            _mods = _entries.Where(entry => !entry.IsLibrary).ToArray();
            _openAcaiMods = _entries.Where(entry => entry.IsOpenAcai && !entry.IsLibrary).ToArray();
            _legacyRedLoaderMods = _entries.Where(entry => entry.IsLegacyRedLoader && !entry.IsLibrary).ToArray();
            _legacyBepInExPlugins = _entries.Where(entry => entry.IsLegacyBepInEx && !entry.IsLibrary).ToArray();
            _compatibilityEntries = _entries.Where(entry => entry.IsCompatibility).ToArray();
            _hostableEntries = _entries.Where(entry => entry.Hostable).ToArray();
            _hostedEntries = _entries.Where(entry => entry.HostedByOpenAcai).ToArray();
            _issueEntries = _entries.Where(entry => entry.HasIssue).ToArray();
        }

        public string GeneratedUtc { get; }
        public bool VortexDeploymentDetected { get; }
        public string VortexStagingPath { get; }
        public OpenACAIModInventorySummary Summary { get; }
        public OpenACAIModInventoryIssues Issues { get; }
        public IReadOnlyList<OpenACAIModInfo> Entries => _entries;
        public IReadOnlyList<OpenACAIModInfo> Mods => _mods;
        public IReadOnlyList<OpenACAIModInfo> Libraries => _libraries;
        public IReadOnlyList<OpenACAIModInfo> OpenAcaiMods => _openAcaiMods;
        public IReadOnlyList<OpenACAIModInfo> LegacyRedLoaderMods => _legacyRedLoaderMods;
        public IReadOnlyList<OpenACAIModInfo> LegacyBepInExPlugins => _legacyBepInExPlugins;
        public IReadOnlyList<OpenACAIModInfo> CompatibilityEntries => _compatibilityEntries;
        public IReadOnlyList<OpenACAIModInfo> HostableEntries => _hostableEntries;
        public IReadOnlyList<OpenACAIModInfo> HostedEntries => _hostedEntries;
        public IReadOnlyList<OpenACAIModInfo> IssueEntries => _issueEntries;

        internal static OpenACAIModInventorySnapshot Empty()
        {
            return new OpenACAIModInventorySnapshot(
                null,
                false,
                null,
                OpenACAIModInventorySummary.Empty(),
                OpenACAIModInventoryIssues.Empty(),
                Array.Empty<OpenACAIModInfo>());
        }

        internal static OpenACAIModInventorySnapshot FromInternal(ModInventoryState inventory)
        {
            if (inventory == null)
            {
                return Empty();
            }

            return new OpenACAIModInventorySnapshot(
                inventory.GeneratedUtc,
                inventory.VortexDeploymentDetected,
                inventory.VortexStagingPath,
                OpenACAIModInventorySummary.FromInternal(inventory.Summary),
                OpenACAIModInventoryIssues.FromInternal(inventory.Issues),
                (inventory.Entries ?? Array.Empty<ModInventoryEntry>()).Select(OpenACAIModInfo.FromInternal));
        }
    }

    public sealed class OpenACAIModInventorySummary
    {
        private OpenACAIModInventorySummary(
            int total,
            int openAcai,
            int redLoader,
            int bepInEx,
            int libraries,
            int legacyRedLoader,
            int legacyBepInEx,
            int vortex,
            int nativeOrManual,
            int hostable,
            int hosted,
            int configurable,
            int liveSettings)
        {
            Total = total;
            OpenAcai = openAcai;
            RedLoader = redLoader;
            BepInEx = bepInEx;
            Libraries = libraries;
            LegacyRedLoader = legacyRedLoader;
            LegacyBepInEx = legacyBepInEx;
            Vortex = vortex;
            NativeOrManual = nativeOrManual;
            Hostable = hostable;
            Hosted = hosted;
            Configurable = configurable;
            LiveSettings = liveSettings;
        }

        public int Total { get; }
        public int OpenAcai { get; }
        public int RedLoader { get; }
        public int BepInEx { get; }
        public int Libraries { get; }
        public int LegacyRedLoader { get; }
        public int LegacyBepInEx { get; }
        public int Vortex { get; }
        public int NativeOrManual { get; }
        public int Hostable { get; }
        public int Hosted { get; }
        public int Configurable { get; }
        public int LiveSettings { get; }

        internal static OpenACAIModInventorySummary Empty()
        {
            return new OpenACAIModInventorySummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        internal static OpenACAIModInventorySummary FromInternal(ModInventorySummary summary)
        {
            if (summary == null)
            {
                return Empty();
            }

            return new OpenACAIModInventorySummary(
                summary.Total,
                summary.OpenAcai,
                summary.RedLoader,
                summary.BepInEx,
                summary.Libraries,
                summary.LegacyRedLoader,
                summary.LegacyBepInEx,
                summary.Vortex,
                summary.NativeOrManual,
                summary.Hostable,
                summary.Hosted,
                summary.Configurable,
                summary.LiveSettings);
        }
    }

    public sealed class OpenACAIModInventoryIssues
    {
        private OpenACAIModInventoryIssues(int total, int missingAssemblies, int duplicateIds, int disabled)
        {
            Total = total;
            MissingAssemblies = missingAssemblies;
            DuplicateIds = duplicateIds;
            Disabled = disabled;
        }

        public int Total { get; }
        public int MissingAssemblies { get; }
        public int DuplicateIds { get; }
        public int Disabled { get; }

        internal static OpenACAIModInventoryIssues Empty()
        {
            return new OpenACAIModInventoryIssues(0, 0, 0, 0);
        }

        internal static OpenACAIModInventoryIssues FromInternal(ModInventoryIssues issues)
        {
            if (issues == null)
            {
                return Empty();
            }

            return new OpenACAIModInventoryIssues(issues.Total, issues.MissingAssemblies, issues.DuplicateIds, issues.Disabled);
        }
    }

    public sealed class OpenACAIModInfo
    {
        private OpenACAIModInfo(ModInventoryEntry entry)
        {
            Id = entry.Id;
            Name = entry.Name;
            Version = entry.Version;
            Author = entry.Author;
            SourceFamily = entry.SourceFamily;
            LoaderType = entry.LoaderType;
            ManifestType = entry.ManifestType;
            ExpectedLocation = entry.ExpectedLocation;
            InstallSource = entry.InstallSource;
            Store = entry.Store;
            VortexPackage = entry.VortexPackage;
            Enabled = entry.Enabled;
            HostedByOpenAcai = entry.HostedByOpenAcai;
            Hostable = entry.Hostable;
            HasAssembly = entry.HasAssembly;
            HasConfig = entry.HasConfig;
            LiveSettingsAvailable = entry.LiveSettingsAvailable;
            ModActionsAvailable = entry.ModActionsAvailable;
            DuplicateId = entry.DuplicateId;
            Status = entry.Status;
            IssueCode = entry.IssueCode;
            Issue = entry.Issue;
            ConfigPath = entry.ConfigPath;
            ConfigFormat = entry.ConfigFormat;
            ConfigSource = entry.ConfigSource;
            AssemblySize = entry.AssemblySize;
            AssemblyVersion = entry.AssemblyVersion;
            ManifestPath = entry.ManifestPath;
            PackagePath = entry.PackagePath;
            AssemblyPath = entry.AssemblyPath;
        }

        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string Author { get; }
        public string SourceFamily { get; }
        public string LoaderType { get; }
        public string ManifestType { get; }
        public string ExpectedLocation { get; }
        public string InstallSource { get; }
        public string Store { get; }
        public string VortexPackage { get; }
        public bool Enabled { get; }
        public bool HostedByOpenAcai { get; }
        public bool Hostable { get; }
        public bool HasAssembly { get; }
        public bool HasConfig { get; }
        public bool LiveSettingsAvailable { get; }
        public bool ModActionsAvailable { get; }
        public bool DuplicateId { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Issue { get; }
        public string ConfigPath { get; }
        public string ConfigFormat { get; }
        public string ConfigSource { get; }
        public long? AssemblySize { get; }
        public string AssemblyVersion { get; }
        public string ManifestPath { get; }
        public string PackagePath { get; }
        public string AssemblyPath { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? string.IsNullOrWhiteSpace(Id) ? "Unknown mod" : Id : Name;
        public bool IsOpenAcai => OpenACAIModLoaderTypes.IsOpenAcai(LoaderType);
        public bool IsLibrary => OpenACAIModLoaderTypes.IsLibrary(LoaderType);
        public bool IsLegacyRedLoader => OpenACAIModLoaderTypes.IsLegacyRedLoader(LoaderType);
        public bool IsLegacyBepInEx => OpenACAIModLoaderTypes.IsLegacyBepInEx(LoaderType);
        public bool IsCompatibility => !IsLibrary && (IsLegacyRedLoader || IsLegacyBepInEx);
        public bool HasIssue => !string.IsNullOrWhiteSpace(IssueCode);

        internal static OpenACAIModInfo FromInternal(ModInventoryEntry entry)
        {
            return new OpenACAIModInfo(entry);
        }
    }

    public sealed class OpenACAIModInventoryCatalogSnapshot
    {
        private readonly IReadOnlyList<OpenACAIModInventoryCatalogSection> _sections;

        private OpenACAIModInventoryCatalogSnapshot(
            string generatedUtc,
            int totalEntries,
            int modCount,
            int libraryCount,
            int openAcaiModCount,
            int compatibilityEntryCount,
            int legacyRedLoaderModCount,
            int legacyBepInExPluginCount,
            int hostableCount,
            int hostedCount,
            int issueCount,
            IEnumerable<OpenACAIModInventoryCatalogSection> sections)
        {
            GeneratedUtc = generatedUtc;
            TotalEntries = totalEntries;
            ModCount = modCount;
            LibraryCount = libraryCount;
            OpenAcaiModCount = openAcaiModCount;
            CompatibilityEntryCount = compatibilityEntryCount;
            LegacyRedLoaderModCount = legacyRedLoaderModCount;
            LegacyBepInExPluginCount = legacyBepInExPluginCount;
            HostableCount = hostableCount;
            HostedCount = hostedCount;
            IssueCount = issueCount;
            _sections = (sections ?? Array.Empty<OpenACAIModInventoryCatalogSection>()).Where(section => section != null).ToArray();
        }

        public string ServiceKey => OpenACAIModding.ModInventoryCatalogServiceKey;
        public string GeneratedUtc { get; }
        public int TotalEntries { get; }
        public int ModCount { get; }
        public int LibraryCount { get; }
        public int OpenAcaiModCount { get; }
        public int CompatibilityEntryCount { get; }
        public int LegacyRedLoaderModCount { get; }
        public int LegacyBepInExPluginCount { get; }
        public int HostableCount { get; }
        public int HostedCount { get; }
        public int IssueCount { get; }
        public IReadOnlyList<OpenACAIModInventoryCatalogSection> Sections => _sections;

        public bool TryGetSection(string id, out OpenACAIModInventoryCatalogSection section)
        {
            section = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            section = _sections.FirstOrDefault(candidate => string.Equals(candidate.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            return section != null;
        }

        internal static OpenACAIModInventoryCatalogSnapshot Empty()
        {
            return FromInventory(OpenACAIModInventorySnapshot.Empty());
        }

        internal static OpenACAIModInventoryCatalogSnapshot FromInventory(OpenACAIModInventorySnapshot inventory)
        {
            inventory = inventory ?? OpenACAIModInventorySnapshot.Empty();
            return new OpenACAIModInventoryCatalogSnapshot(
                inventory.GeneratedUtc,
                inventory.Entries.Count,
                inventory.Mods.Count,
                inventory.Libraries.Count,
                inventory.OpenAcaiMods.Count,
                inventory.CompatibilityEntries.Count,
                inventory.LegacyRedLoaderMods.Count,
                inventory.LegacyBepInExPlugins.Count,
                inventory.HostableEntries.Count,
                inventory.HostedEntries.Count,
                inventory.IssueEntries.Count,
                new[]
                {
                    CreateSection("mods", "Mods", "Loadable/user-facing mod entries. Libraries are excluded.", inventory.Mods),
                    CreateSection("libraries", "Libraries", "SDK/runtime helper libraries and support assemblies.", inventory.Libraries),
                    CreateSection("openacai", "Modstoke Mods", "Native Modstoke mod packages from the Modstoke mods root. Libraries are excluded.", inventory.OpenAcaiMods),
                    CreateSection("compatibility", "Compatibility Entries", "Legacy RedLoader and BepInEx compatibility mods/plugins. Libraries are excluded.", inventory.CompatibilityEntries),
                    CreateSection("legacy-redloader", "Legacy RedLoader Mods", "RedLoader-style mod packages excluding library shims.", inventory.LegacyRedLoaderMods),
                    CreateSection("legacy-bepinex", "Legacy BepInEx Plugins", "BepInEx-style plugin compatibility entries.", inventory.LegacyBepInExPlugins),
                    CreateSection("hostable", "Hostable", "Entries the OpenACAI compatibility host may load when enabled.", inventory.HostableEntries),
                    CreateSection("hosted", "Hosted", "Entries already hosted by OpenACAI in this run.", inventory.HostedEntries),
                    CreateSection("issues", "Issues", "Entries with warnings, missing assemblies, duplicates, or disabled state.", inventory.IssueEntries)
                });
        }

        private static OpenACAIModInventoryCatalogSection CreateSection(string id, string displayName, string description, IEnumerable<OpenACAIModInfo> entries)
        {
            return new OpenACAIModInventoryCatalogSection(id, displayName, description, entries);
        }
    }

    public sealed class OpenACAIModInventoryCatalogSection
    {
        private readonly IReadOnlyList<string> _entryIds;

        internal OpenACAIModInventoryCatalogSection(string id, string displayName, string description, IEnumerable<OpenACAIModInfo> entries)
        {
            Id = string.IsNullOrWhiteSpace(id) ? "section" : id.Trim();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
            Description = description ?? "";
            _entryIds = (entries ?? Array.Empty<OpenACAIModInfo>())
                .Where(entry => entry != null)
                .Select(CreateEntryReference)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Count = _entryIds.Count;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public int Count { get; }
        public IReadOnlyList<string> EntryIds => _entryIds;

        private static string CreateEntryReference(OpenACAIModInfo entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.Id))
            {
                return entry.Id.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                return entry.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.AssemblyPath))
            {
                try
                {
                    return System.IO.Path.GetFileNameWithoutExtension(entry.AssemblyPath);
                }
                catch
                {
                    return entry.AssemblyPath;
                }
            }

            return null;
        }
    }

    public sealed class OpenACAISceneEvent
    {
        internal OpenACAISceneEvent(string phase, int buildIndex, string name, DateTimeOffset timestampUtc)
        {
            Phase = phase;
            BuildIndex = buildIndex;
            Name = name;
            TimestampUtc = timestampUtc;
        }

        public string Phase { get; }
        public int BuildIndex { get; }
        public string Name { get; }
        public DateTimeOffset TimestampUtc { get; }
    }

    public sealed class OpenACAILifecycleEvent
    {
        internal OpenACAILifecycleEvent(string name, DateTimeOffset timestampUtc)
        {
            Name = name;
            TimestampUtc = timestampUtc;
        }

        public string Name { get; }
        public DateTimeOffset TimestampUtc { get; }
    }
}

