// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public Modstoke SDK surface for Sons Of The Forest mods.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    public static class Modding
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
        private const string RetiredLifecycleMessage = global::Modstoke.Loader.ModRuntime.RetiredLifecycleMessage;

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, object> Services = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoaderServiceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RuntimeServiceKey,
            ModInventoryServiceKey,
            ModInventoryCatalogServiceKey,
            LoaderRuntime.CompatibilityShimServiceKey
        };
        private static readonly ConditionalWeakTable<Delegate, Dictionary<string, long>> EventErrorCounts = new();
        private static RuntimeSnapshot _runtime = RuntimeSnapshot.CreateUnknown();
        private static ModInventorySnapshot _modInventory = ModInventorySnapshot.Empty();
        private static ModInventoryCatalogSnapshot _modInventoryCatalog = ModInventoryCatalogSnapshot.Empty();

        public static event Action<global::Modstoke.Loader.LifecycleEvent> Lifecycle;
        public static event Action<global::Modstoke.Loader.SceneEvent> SceneChanged;
        public static event Action<ModInventorySnapshot> ModInventoryChanged;
        public static event Action<string> StartupWarning;

        public static RuntimeSnapshot Runtime
        {
            get
            {
                lock (SyncRoot)
                {
                    return _runtime;
                }
            }
        }

        public static ModInventorySnapshot ModInventory
        {
            get
            {
                lock (SyncRoot)
                {
                    return _modInventory;
                }
            }
        }

        public static ModInventoryCatalogSnapshot ModInventoryCatalog
        {
            get
            {
                lock (SyncRoot)
                {
                    return _modInventoryCatalog;
                }
            }
        }

        public static RuntimeAttestationSnapshot RuntimeAttestation => global::Modstoke.Loader.RuntimeAttestation.Snapshot;
        public static ChainloaderStateSnapshot Chainloader => ChainloaderStatus.State;
        public static CompatibilityDependencySnapshot CompatibilityDependencies => global::Modstoke.Loader.CompatibilityDependencies.Snapshot;
        public static BepInExPluginCompatibilitySnapshot BepInExPluginCompatibility => global::Modstoke.Loader.BepInExPluginCompatibility.State;
        public static ModHostSnapshot ModHost => global::Modstoke.Loader.ModHost.State;
        public static ModRuntimeSnapshot ModRuntime => global::Modstoke.Loader.ModRuntime.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleRegistrySnapshot LifecycleRegistry => global::Modstoke.Loader.LifecycleRegistry.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleDispatchPlannerSnapshot LifecycleDispatchPlanner => global::Modstoke.Loader.LifecycleDispatchPlanner.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleInvocationPlannerSnapshot LifecycleInvocationPlanner => global::Modstoke.Loader.LifecycleInvocationPlanner.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleBindingsSnapshot LifecycleBindings => global::Modstoke.Loader.LifecycleBindings.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleTokenResolverSnapshot LifecycleTokenResolver => global::Modstoke.Loader.LifecycleTokenResolver.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleJournalSnapshot LifecycleJournal => global::Modstoke.Loader.LifecycleJournal.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleSchedulePlannerSnapshot LifecycleSchedulePlanner => global::Modstoke.Loader.LifecycleSchedulePlanner.State;
        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleExecutionPlannerSnapshot LifecycleExecutionPlanner => global::Modstoke.Loader.LifecycleExecutionPlanner.State;

        public static IReadOnlyList<ModInfo> GetMods()
        {
            return ModInventory.Mods;
        }

        public static IReadOnlyList<ModInfo> GetLibraries()
        {
            return ModInventory.Libraries;
        }

        public static IReadOnlyList<ModInfo> GetCompatibilityEntries()
        {
            return ModInventory.CompatibilityEntries;
        }

        public static IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> GetBepInExPluginCompatibilityCandidates()
        {
            return global::Modstoke.Loader.BepInExPluginCompatibility.GetCandidates();
        }

        public static IReadOnlyList<BepInExPluginCompatibilityPlanSnapshot> GetBepInExPluginCompatibilityPlans()
        {
            return global::Modstoke.Loader.BepInExPluginCompatibility.GetLoadPlans();
        }

        public static IReadOnlyList<BepInExPluginCompatibilityLoadAttemptSnapshot> GetBepInExPluginCompatibilityLoadAttempts()
        {
            return global::Modstoke.Loader.BepInExPluginCompatibility.GetLoadAttempts();
        }

        public static BepInExPluginCompatibilityPlanSnapshot PlanBepInExPluginCompatibility(string idOrAssemblyName)
        {
            return global::Modstoke.Loader.BepInExPluginCompatibility.PlanLoad(idOrAssemblyName);
        }

        public static BepInExPluginCompatibilityLoadAttemptSnapshot AttemptBepInExPluginCompatibilityLoad(string idOrAssemblyName)
        {
            return global::Modstoke.Loader.BepInExPluginCompatibility.AttemptLoad(idOrAssemblyName);
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleDispatchTargetSnapshot> GetLifecycleDispatchTargets()
        {
            return global::Modstoke.Loader.LifecycleDispatchPlanner.GetDispatchTargets();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleDispatchIntentSnapshot> GetLifecycleDispatchIntentions()
        {
            return global::Modstoke.Loader.LifecycleDispatchPlanner.GetDispatchIntentions();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleDispatchPlan PlanLifecycleDispatch(string hookName)
        {
            return global::Modstoke.Loader.LifecycleDispatchPlanner.PlanDispatch(hookName);
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleInvocationRequestSnapshot> GetLifecycleInvocationRequests()
        {
            return global::Modstoke.Loader.LifecycleInvocationPlanner.GetInvocationRequests();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleInvocationPlan PlanLifecycleInvocation(string hookName)
        {
            return global::Modstoke.Loader.LifecycleInvocationPlanner.PlanInvocation(hookName);
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleBindingSnapshot> GetLifecycleBindings()
        {
            return global::Modstoke.Loader.LifecycleBindings.GetBindings();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<string> GetLifecycleBindingTokens()
        {
            return global::Modstoke.Loader.LifecycleBindings.GetBindingTokens();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleTokenResolutionSnapshot> GetLifecycleTokenResolutions()
        {
            return global::Modstoke.Loader.LifecycleTokenResolver.GetTokenResolutions();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleTokenLookupSnapshot> GetLifecycleTokenLookupResults()
        {
            return global::Modstoke.Loader.LifecycleTokenResolver.GetLookupResults();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleTokenLookupSnapshot LookupLifecycleToken(string bindingToken)
        {
            return global::Modstoke.Loader.LifecycleTokenResolver.LookupBindingToken(bindingToken);
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleJournalEntrySnapshot> GetLifecycleJournalEntries()
        {
            return global::Modstoke.Loader.LifecycleJournal.GetJournalEntries();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleReplayPlanSnapshot> GetLifecycleReplayPlans()
        {
            return global::Modstoke.Loader.LifecycleJournal.GetReplayPlans();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleReplayPlanSnapshot PlanLifecycleReplay(string bindingToken)
        {
            return global::Modstoke.Loader.LifecycleJournal.PlanReplay(bindingToken);
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleScheduleBatchSnapshot> GetLifecycleScheduleBatches()
        {
            return global::Modstoke.Loader.LifecycleSchedulePlanner.GetScheduleBatches();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleScheduleEntrySnapshot> GetLifecycleScheduleEntries()
        {
            return global::Modstoke.Loader.LifecycleSchedulePlanner.GetScheduleEntries();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleSchedulePlanSnapshot> GetLifecycleSchedulePlans()
        {
            return global::Modstoke.Loader.LifecycleSchedulePlanner.GetSchedulePlans();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleSchedulePlanSnapshot PlanLifecycleSchedule(string hookName)
        {
            return global::Modstoke.Loader.LifecycleSchedulePlanner.PlanSchedule(hookName);
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleExecutionBatchSnapshot> GetLifecycleExecutionBatches()
        {
            return global::Modstoke.Loader.LifecycleExecutionPlanner.GetExecutionBatches();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleExecutionDecisionSnapshot> GetLifecycleExecutionDecisions()
        {
            return global::Modstoke.Loader.LifecycleExecutionPlanner.GetExecutionDecisions();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static IReadOnlyList<LifecycleExecutionPlanSnapshot> GetLifecycleExecutionPlans()
        {
            return global::Modstoke.Loader.LifecycleExecutionPlanner.GetExecutionPlans();
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static LifecycleExecutionPlanSnapshot PlanLifecycleExecution(string hookName)
        {
            return global::Modstoke.Loader.LifecycleExecutionPlanner.PlanExecution(hookName);
        }

        [Obsolete(RetiredLifecycleMessage)]
        public static bool TryResolveLifecycleToken(string bindingToken, out LifecycleTokenResolutionSnapshot resolution)
        {
            return global::Modstoke.Loader.LifecycleTokenResolver.TryResolveBindingToken(bindingToken, out resolution);
        }

        public static bool TryFindInventoryEntry(string idOrName, out ModInfo entry)
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
                if (IsLoaderServiceKeyNoLock(key) || Services.ContainsKey(key))
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
                return !IsLoaderServiceKeyNoLock(key) && Services.Remove(key);
            }
        }

        // Loader namespaces are reserved up front: several loader services publish only after
        // mod entrypoints run, and a mod must not claim one of those keys first.
        private static bool IsLoaderServiceKeyNoLock(string key)
        {
            return LoaderServiceKeys.Contains(key)
                || key.StartsWith("openacai.loader.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("openacai.sons.", StringComparison.OrdinalIgnoreCase);
        }

        internal static void SetService(string key, object service)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (SyncRoot)
            {
                // Loader publications retain ownership while a service is unavailable.
                LoaderServiceKeys.Add(key);
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

        public static ModInventorySnapshot RefreshModInventory()
        {
            // CaptureModInventory already publishes the refreshed inventory and event.
            RuntimeEntryPoint.RefreshModInventory();
            return ModInventory;
        }

        internal static void MarkReady(
            bool nativeRedLoaderUnitySupportActive,
            int hostedRedLoaderModCount,
            IReadOnlyDictionary<string, string> paths,
            IEnumerable<string> warnings)
        {
            var snapshot = new RuntimeSnapshot(
                ApiVersion,
                LoaderName,
                RuntimeEntryPoint.Version,
                LoaderCapabilities.CompatibilityMode,
                true,
                false,
                nativeRedLoaderUnitySupportActive,
                hostedRedLoaderModCount,
                null,
                Environment.Version.ToString(),
                paths,
                warnings,
                LoaderRuntime.CompatibilityShimsSnapshot());

            SetRuntime(snapshot);
            PublishLifecycle("LoaderReady");
        }

        internal static void MarkFailed(
            string startupError,
            IReadOnlyDictionary<string, string> paths,
            IEnumerable<string> warnings)
        {
            var snapshot = new RuntimeSnapshot(
                ApiVersion,
                LoaderName,
                RuntimeEntryPoint.Version,
                LoaderCapabilities.CompatibilityMode,
                false,
                true,
                false,
                0,
                startupError,
                Environment.Version.ToString(),
                paths,
                warnings,
                LoaderRuntime.CompatibilityShimsSnapshot());

            SetRuntime(snapshot);
            PublishLifecycle("LoaderFailed");
        }

        internal static ModInventorySnapshot PublishModInventory(ModInventoryState inventory)
        {
            var snapshot = ModInventorySnapshot.FromInternal(inventory);
            var catalog = ModInventoryCatalogSnapshot.FromInventory(snapshot);
            Action<ModInventorySnapshot> handlers;
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
            Action<global::Modstoke.Loader.SceneEvent> handlers;
            lock (SyncRoot)
            {
                handlers = SceneChanged;
            }

            if (handlers != null)
                Dispatch("SceneChanged", handlers, new global::Modstoke.Loader.SceneEvent(phase, buildIndex, name, DateTimeOffset.UtcNow));
        }

        internal static void PublishLifecycle(string name)
        {
            Action<global::Modstoke.Loader.LifecycleEvent> handlers;
            lock (SyncRoot)
            {
                handlers = Lifecycle;
            }

            if (handlers != null)
                Dispatch("Lifecycle." + name, handlers, new global::Modstoke.Loader.LifecycleEvent(name, DateTimeOffset.UtcNow));
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

        private static void SetRuntime(RuntimeSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _runtime = snapshot ?? RuntimeSnapshot.CreateUnknown();
                Services[RuntimeServiceKey] = _runtime;
                Services[ModInventoryServiceKey] = _modInventory;
                Services[ModInventoryCatalogServiceKey] = _modInventoryCatalog;
                Services[LoaderRuntime.CompatibilityShimServiceKey] = _runtime.CompatibilityShims;
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
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(value);
                }
                catch (Exception ex)
                {
                    var counts = EventErrorCounts.GetValue(handler, static _ => new Dictionary<string, long>(StringComparer.Ordinal));
                    var errorChannel = channel ?? "unknown";
                    long count;
                    lock (counts)
                    {
                        counts.TryGetValue(errorChannel, out var existing);
                        count = existing == long.MaxValue ? long.MaxValue : existing + 1;
                        counts[errorChannel] = count;
                    }

                    if (count == 1 || count % 300 == 0)
                    {
                        try
                        {
                            RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK event '" + errorChannel
                                + "' subscriber " + handler.Method.DeclaringType?.FullName + "." + handler.Method.Name
                                + " failed" + (count == 1 ? "" : " " + count + " times") + ": " + ex);
                        }
                        catch (Exception loggingError)
                        {
                            RuntimeEntryPoint.TraceCompat("SDK event failure logging failed for " + channel + ": " + loggingError.GetType().FullName);
                        }
                    }
                }
            }
        }
    }

    public sealed class RuntimeSnapshot
    {
        private readonly IReadOnlyDictionary<string, string> _paths;
        private readonly IReadOnlyList<string> _warnings;
        private readonly IReadOnlyDictionary<string, CompatibilityShimSnapshot> _compatibilityShims;

        public RuntimeSnapshot(
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
            IReadOnlyDictionary<string, CompatibilityShimSnapshot> compatibilityShims)
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
            _paths = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(paths ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
            _warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
            _compatibilityShims = new ReadOnlyDictionary<string, CompatibilityShimSnapshot>(new Dictionary<string, CompatibilityShimSnapshot>(
                compatibilityShims ?? new Dictionary<string, CompatibilityShimSnapshot>(),
                StringComparer.OrdinalIgnoreCase));
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
        public IReadOnlyDictionary<string, CompatibilityShimSnapshot> CompatibilityShims => _compatibilityShims;

        internal static RuntimeSnapshot CreateUnknown()
        {
            return new RuntimeSnapshot(
                Modding.ApiVersion,
                Modding.LoaderName,
                RuntimeEntryPoint.Version,
                LoaderCapabilities.CompatibilityMode,
                false,
                false,
                false,
                0,
                null,
                Environment.Version.ToString(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<string>(),
                LoaderRuntime.CompatibilityShimsSnapshot());
        }
    }

    public sealed class ModInventorySnapshot
    {
        private readonly IReadOnlyList<ModInfo> _entries;
        private readonly IReadOnlyList<ModInfo> _mods;
        private readonly IReadOnlyList<ModInfo> _libraries;
        private readonly IReadOnlyList<ModInfo> _openAcaiMods;
        private readonly IReadOnlyList<ModInfo> _legacyRedLoaderMods;
        private readonly IReadOnlyList<ModInfo> _legacyBepInExPlugins;
        private readonly IReadOnlyList<ModInfo> _compatibilityEntries;
        private readonly IReadOnlyList<ModInfo> _hostableEntries;
        private readonly IReadOnlyList<ModInfo> _hostedEntries;
        private readonly IReadOnlyList<ModInfo> _issueEntries;

        private ModInventorySnapshot(
            string generatedUtc,
            bool vortexDeploymentDetected,
            string vortexStagingPath,
            ModInventoryCounts summary,
            ModInventoryIssueSummary issues,
            IEnumerable<ModInfo> entries)
        {
            GeneratedUtc = generatedUtc;
            VortexDeploymentDetected = vortexDeploymentDetected;
            VortexStagingPath = vortexStagingPath;
            Summary = summary ?? ModInventoryCounts.Empty();
            Issues = issues ?? ModInventoryIssueSummary.Empty();
            _entries = Array.AsReadOnly((entries ?? Array.Empty<ModInfo>()).ToArray());
            _libraries = Array.AsReadOnly(_entries.Where(entry => entry.IsLibrary).ToArray());
            _mods = Array.AsReadOnly(_entries.Where(entry => !entry.IsLibrary).ToArray());
            _openAcaiMods = Array.AsReadOnly(_entries.Where(entry => entry.IsOpenAcai && !entry.IsLibrary).ToArray());
            _legacyRedLoaderMods = Array.AsReadOnly(_entries.Where(entry => entry.IsLegacyRedLoader && !entry.IsLibrary).ToArray());
            _legacyBepInExPlugins = Array.AsReadOnly(_entries.Where(entry => entry.IsLegacyBepInEx && !entry.IsLibrary).ToArray());
            _compatibilityEntries = Array.AsReadOnly(_entries.Where(entry => entry.IsCompatibility).ToArray());
            _hostableEntries = Array.AsReadOnly(_entries.Where(entry => entry.Hostable).ToArray());
            _hostedEntries = Array.AsReadOnly(_entries.Where(entry => entry.HostedByOpenAcai).ToArray());
            _issueEntries = Array.AsReadOnly(_entries.Where(entry => entry.HasIssue).ToArray());
        }

        public string GeneratedUtc { get; }
        public bool VortexDeploymentDetected { get; }
        public string VortexStagingPath { get; }
        public ModInventoryCounts Summary { get; }
        public ModInventoryIssueSummary Issues { get; }
        public IReadOnlyList<ModInfo> Entries => _entries;
        public IReadOnlyList<ModInfo> Mods => _mods;
        public IReadOnlyList<ModInfo> Libraries => _libraries;
        public IReadOnlyList<ModInfo> OpenAcaiMods => _openAcaiMods;
        public IReadOnlyList<ModInfo> LegacyRedLoaderMods => _legacyRedLoaderMods;
        public IReadOnlyList<ModInfo> LegacyBepInExPlugins => _legacyBepInExPlugins;
        public IReadOnlyList<ModInfo> CompatibilityEntries => _compatibilityEntries;
        public IReadOnlyList<ModInfo> HostableEntries => _hostableEntries;
        public IReadOnlyList<ModInfo> HostedEntries => _hostedEntries;
        public IReadOnlyList<ModInfo> IssueEntries => _issueEntries;

        internal static ModInventorySnapshot Empty()
        {
            return new ModInventorySnapshot(
                null,
                false,
                null,
                ModInventoryCounts.Empty(),
                ModInventoryIssueSummary.Empty(),
                Array.Empty<ModInfo>());
        }

        internal static ModInventorySnapshot FromInternal(ModInventoryState inventory)
        {
            if (inventory == null)
            {
                return Empty();
            }

            return new ModInventorySnapshot(
                inventory.GeneratedUtc,
                inventory.VortexDeploymentDetected,
                inventory.VortexStagingPath,
                ModInventoryCounts.FromInternal(inventory.Summary),
                ModInventoryIssueSummary.FromInternal(inventory.Issues),
                (inventory.Entries ?? Array.Empty<ModInventoryEntry>()).Select(ModInfo.FromInternal));
        }
    }

    public sealed class ModInventoryCounts
    {
        private ModInventoryCounts(
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

        internal static ModInventoryCounts Empty()
        {
            return new ModInventoryCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        internal static ModInventoryCounts FromInternal(ModInventorySummary summary)
        {
            if (summary == null)
            {
                return Empty();
            }

            return new ModInventoryCounts(
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

    public sealed class ModInventoryIssueSummary
    {
        private ModInventoryIssueSummary(int total, int missingAssemblies, int duplicateIds, int disabled)
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

        internal static ModInventoryIssueSummary Empty()
        {
            return new ModInventoryIssueSummary(0, 0, 0, 0);
        }

        internal static ModInventoryIssueSummary FromInternal(ModInventoryIssues issues)
        {
            if (issues == null)
            {
                return Empty();
            }

            return new ModInventoryIssueSummary(issues.Total, issues.MissingAssemblies, issues.DuplicateIds, issues.Disabled);
        }
    }

    public sealed class ModInfo
    {
        private ModInfo(ModInventoryEntry entry)
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
            DataOnly = entry.DataOnly;
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
        public bool IsOpenAcai => ModLoaderTypes.IsOpenAcai(LoaderType);
        public bool IsLibrary => ModLoaderTypes.IsLibrary(LoaderType);
        public bool IsLegacyRedLoader => ModLoaderTypes.IsLegacyRedLoader(LoaderType);
        public bool IsLegacyBepInEx => ModLoaderTypes.IsLegacyBepInEx(LoaderType);
        public bool IsCompatibility => !IsLibrary && (IsLegacyRedLoader || IsLegacyBepInEx);
        public bool HasIssue => !string.IsNullOrWhiteSpace(IssueCode);
        internal bool DataOnly { get; }

        internal static ModInfo FromInternal(ModInventoryEntry entry)
        {
            return new ModInfo(entry);
        }
    }

    public sealed class ModInventoryCatalogSnapshot
    {
        private readonly IReadOnlyList<ModInventoryCatalogSection> _sections;

        private ModInventoryCatalogSnapshot(
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
            IEnumerable<ModInventoryCatalogSection> sections)
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
            _sections = Array.AsReadOnly((sections ?? Array.Empty<ModInventoryCatalogSection>()).Where(section => section != null).ToArray());
        }

        public string ServiceKey => Modding.ModInventoryCatalogServiceKey;
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
        public IReadOnlyList<ModInventoryCatalogSection> Sections => _sections;

        public bool TryGetSection(string id, out ModInventoryCatalogSection section)
        {
            section = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var sectionId = id.Trim();
            section = _sections.FirstOrDefault(candidate => string.Equals(candidate.Id, sectionId, StringComparison.OrdinalIgnoreCase));
            return section != null;
        }

        internal static ModInventoryCatalogSnapshot Empty()
        {
            return FromInventory(ModInventorySnapshot.Empty());
        }

        internal static ModInventoryCatalogSnapshot FromInventory(ModInventorySnapshot inventory)
        {
            inventory = inventory ?? ModInventorySnapshot.Empty();
            return new ModInventoryCatalogSnapshot(
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
                    new ModInventoryCatalogSection("mods", "Mods", "Loadable/user-facing mod entries. Libraries are excluded.", inventory.Mods),
                    new ModInventoryCatalogSection("libraries", "Libraries", "SDK/runtime helper libraries and support assemblies.", inventory.Libraries),
                    new ModInventoryCatalogSection("openacai", "Modstoke Mods", "Native Modstoke mod packages from the Modstoke mods root. Libraries are excluded.", inventory.OpenAcaiMods),
                    new ModInventoryCatalogSection("compatibility", "Compatibility Entries", "Legacy RedLoader and BepInEx compatibility mods/plugins. Libraries are excluded.", inventory.CompatibilityEntries),
                    new ModInventoryCatalogSection("legacy-redloader", "Legacy RedLoader Mods", "RedLoader-style mod packages excluding library shims.", inventory.LegacyRedLoaderMods),
                    new ModInventoryCatalogSection("legacy-bepinex", "Legacy BepInEx Plugins", "BepInEx-style plugin compatibility entries.", inventory.LegacyBepInExPlugins),
                    new ModInventoryCatalogSection("hostable", "Hostable", "Entries the Modstoke compatibility host may load when enabled.", inventory.HostableEntries),
                    new ModInventoryCatalogSection("hosted", "Hosted", "Entries already hosted by Modstoke in this run.", inventory.HostedEntries),
                    new ModInventoryCatalogSection("issues", "Issues", "Entries with warnings, missing assemblies, duplicates, or disabled state.", inventory.IssueEntries)
                });
        }
    }

    public sealed class ModInventoryCatalogSection
    {
        private readonly IReadOnlyList<string> _entryIds;

        internal ModInventoryCatalogSection(string id, string displayName, string description, IEnumerable<ModInfo> entries)
        {
            Id = string.IsNullOrWhiteSpace(id) ? "section" : id.Trim();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
            Description = description ?? "";
            _entryIds = Array.AsReadOnly((entries ?? Array.Empty<ModInfo>())
                .Where(entry => entry != null)
                .Select(CreateEntryReference)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
            Count = _entryIds.Count;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public int Count { get; }
        public IReadOnlyList<string> EntryIds => _entryIds;

        private static string CreateEntryReference(ModInfo entry)
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

    public sealed class SceneEvent
    {
        internal SceneEvent(string phase, int buildIndex, string name, DateTimeOffset timestampUtc)
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

    public sealed class LifecycleEvent
    {
        internal LifecycleEvent(string name, DateTimeOffset timestampUtc)
        {
            Name = name;
            TimestampUtc = timestampUtc;
        }

        public string Name { get; }
        public DateTimeOffset TimestampUtc { get; }
    }
}

