// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public OpenACAI server/protected-runtime policy registry. Private admin or
// anti-cheat providers can plug in here without defining the public loader UI.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Sons;

namespace OpenACAI.Loader
{
    public static class OpenACAIServerPolicy
    {
        public const string ServiceKey = "openacai.loader.serverPolicy";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, IOpenACAIServerPolicyProvider> Providers =
            new Dictionary<string, IOpenACAIServerPolicyProvider>(StringComparer.OrdinalIgnoreCase);

        public static event Action<OpenACAIServerPolicyProviderSnapshot> ProviderChanged;

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Providers.Clear();
            }

            PublishService();
        }

        public static bool TryRegisterProvider(IOpenACAIServerPolicyProvider provider)
        {
            var snapshot = OpenACAIServerPolicyProviderSnapshot.FromProvider(provider);
            if (snapshot == null)
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (Providers.ContainsKey(snapshot.Id))
                {
                    return false;
                }

                Providers[snapshot.Id] = provider;
            }

            PublishService();
            DispatchProviderChanged(snapshot);
            return true;
        }

        public static bool RegisterOrUpdateProvider(IOpenACAIServerPolicyProvider provider)
        {
            var snapshot = OpenACAIServerPolicyProviderSnapshot.FromProvider(provider);
            if (snapshot == null)
            {
                return false;
            }

            lock (SyncRoot)
            {
                Providers[snapshot.Id] = provider;
            }

            PublishService();
            DispatchProviderChanged(snapshot);
            return true;
        }

        public static bool TryUnregisterProvider(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            bool removed;
            lock (SyncRoot)
            {
                removed = Providers.Remove(id.Trim());
            }

            if (removed)
            {
                PublishService();
            }

            return removed;
        }

        public static IReadOnlyList<OpenACAIServerPolicyProviderSnapshot> ProvidersSnapshot()
        {
            IOpenACAIServerPolicyProvider[] providers;
            lock (SyncRoot)
            {
                providers = Providers.Values.ToArray();
            }

            return providers
                .Select(OpenACAIServerPolicyProviderSnapshot.FromProvider)
                .Where(snapshot => snapshot != null)
                .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static IReadOnlyList<OpenACAIServerPolicySnapshot> PoliciesSnapshot()
        {
            IOpenACAIServerPolicyProvider[] providers;
            lock (SyncRoot)
            {
                providers = Providers.Values.ToArray();
            }

            return providers
                .Select(CreatePolicySafe)
                .Where(policy => policy != null)
                .OrderBy(policy => policy.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static bool TryGetPolicy(string providerId, out OpenACAIServerPolicySnapshot policy)
        {
            policy = null;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            IOpenACAIServerPolicyProvider provider;
            lock (SyncRoot)
            {
                Providers.TryGetValue(providerId.Trim(), out provider);
            }

            policy = CreatePolicySafe(provider);
            return policy != null;
        }

        internal static object CreateState()
        {
            var providers = ProvidersSnapshot();
            var policies = PoliciesSnapshot();
            return new
            {
                schema = 1,
                serviceKey = ServiceKey,
                providerCount = providers.Count,
                policyCount = policies.Count,
                requiredFailureCount = policies.Sum(policy => policy.RequiredFailureCount),
                providers,
                policies
            };
        }

        internal static object Refresh()
        {
            var state = CreateState();
            OpenACAIModding.SetService(ServiceKey, state);
            return state;
        }

        private static void PublishService()
        {
            Refresh();
        }

        private static OpenACAIServerPolicySnapshot CreatePolicySafe(IOpenACAIServerPolicyProvider provider)
        {
            if (provider == null || !provider.Enabled)
            {
                return null;
            }

            try
            {
                return provider.CreatePolicy();
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning(
                    "OpenACAI server policy provider failed for '" + provider.Id + "': " + ex.Message);
                return OpenACAIServerPolicySnapshot.Create(
                    provider.Id,
                    provider.DisplayName,
                    "FAILED",
                    "Policy provider failed: " + ex.Message,
                    Array.Empty<OpenACAIServerPolicyRequirementSnapshot>());
            }
        }

        private static void DispatchProviderChanged(OpenACAIServerPolicyProviderSnapshot snapshot)
        {
            Action<OpenACAIServerPolicyProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                handlers = ProviderChanged;
            }

            try
            {
                handlers?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI server policy provider event subscriber failed: " + ex.Message);
            }
        }
    }

    public interface IOpenACAIServerPolicyProvider
    {
        string Id { get; }
        string DisplayName { get; }
        string Source { get; }
        string Description { get; }
        bool RequiresAuthorization { get; }
        bool Enabled { get; }
        OpenACAIServerPolicySnapshot CreatePolicy();
    }

    public sealed class OpenACAIServerPolicyProviderSnapshot
    {
        private OpenACAIServerPolicyProviderSnapshot(
            string id,
            string displayName,
            string source,
            string description,
            bool requiresAuthorization,
            bool enabled,
            string updatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            Source = source;
            Description = description;
            RequiresAuthorization = requiresAuthorization;
            Enabled = enabled;
            UpdatedUtc = updatedUtc;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Source { get; }
        public string Description { get; }
        public bool RequiresAuthorization { get; }
        public bool Enabled { get; }
        public string UpdatedUtc { get; }

        internal static OpenACAIServerPolicyProviderSnapshot FromProvider(IOpenACAIServerPolicyProvider provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
            {
                return null;
            }

            return new OpenACAIServerPolicyProviderSnapshot(
                provider.Id.Trim(),
                string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Id.Trim() : provider.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(provider.Source) ? "Unknown" : provider.Source.Trim(),
                provider.Description ?? "",
                provider.RequiresAuthorization,
                provider.Enabled,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class OpenACAIServerPolicySnapshot
    {
        private readonly IReadOnlyList<OpenACAIServerPolicyRequirementSnapshot> _requirements;

        private OpenACAIServerPolicySnapshot(
            string providerId,
            string title,
            string status,
            string detail,
            IEnumerable<OpenACAIServerPolicyRequirementSnapshot> requirements,
            string updatedUtc)
        {
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? "provider" : providerId.Trim();
            Title = string.IsNullOrWhiteSpace(title) ? ProviderId : title.Trim();
            Status = string.IsNullOrWhiteSpace(status) ? "INFO" : status.Trim();
            Detail = detail ?? "";
            _requirements = (requirements ?? Array.Empty<OpenACAIServerPolicyRequirementSnapshot>())
                .Where(requirement => requirement != null)
                .ToArray();
            RequirementCount = _requirements.Count;
            RequiredCount = _requirements.Count(requirement => requirement.Required);
            SatisfiedRequiredCount = _requirements.Count(requirement => requirement.Required && requirement.Satisfied);
            RequiredFailureCount = _requirements.Count(requirement => requirement.Required && !requirement.Satisfied);
            UpdatedUtc = updatedUtc;
        }

        public string ProviderId { get; }
        public string Title { get; }
        public string Status { get; }
        public string Detail { get; }
        public int RequirementCount { get; }
        public int RequiredCount { get; }
        public int SatisfiedRequiredCount { get; }
        public int RequiredFailureCount { get; }
        public string UpdatedUtc { get; }
        public IReadOnlyList<OpenACAIServerPolicyRequirementSnapshot> Requirements => _requirements;

        public static OpenACAIServerPolicySnapshot Create(
            string providerId,
            string title,
            string status,
            string detail,
            IEnumerable<OpenACAIServerPolicyRequirementSnapshot> requirements)
        {
            return new OpenACAIServerPolicySnapshot(
                providerId,
                title,
                status,
                detail,
                requirements,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class OpenACAIServerPolicyRequirementSnapshot
    {
        public OpenACAIServerPolicyRequirementSnapshot(
            string id,
            string displayName,
            bool required,
            bool satisfied,
            string status,
            string detail)
        {
            Id = string.IsNullOrWhiteSpace(id) ? "requirement" : id.Trim();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
            Required = required;
            Satisfied = satisfied;
            Status = string.IsNullOrWhiteSpace(status) ? satisfied ? "READY" : "CHECK" : status.Trim();
            Detail = detail ?? "";
        }

        public string Id { get; }
        public string DisplayName { get; }
        public bool Required { get; }
        public bool Satisfied { get; }
        public string Status { get; }
        public string Detail { get; }
    }
}

namespace OpenACAI.Endnight.Loader
{
    using OpenACAI.Loader;
    using OpenACAI.Sons;

    internal static class OpenACAIProtectedServerPolicy
    {
        internal static void Initialize()
        {
            if (!OpenACAIServerPolicy.RegisterOrUpdateProvider(new RuntimeBaselineProvider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke runtime baseline server policy provider could not be registered.");
            }
        }

        private sealed class RuntimeBaselineProvider : IOpenACAIServerPolicyProvider
        {
            public string Id => "openacai.serverPolicy.runtimeBaseline";
            public string DisplayName => "Modstoke Runtime Baseline";
            public string Source => "Modstoke Loader";
            public string Description => "Disclosed public baseline signals for protected-server compatibility.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIServerPolicySnapshot CreatePolicy()
            {
                var inventory = OpenACAIModding.ModInventory;
                var catalog = OpenACAIModding.ModInventoryCatalog;
                var nativeModHost = OpenACAINativeModHost.State;
                var nativeModRuntime = OpenACAINativeModRuntime.State;
                var nativeLifecycleRegistry = OpenACAINativeLifecycleRegistry.State;
                var nativeLifecycleDispatcher = OpenACAINativeLifecycleDispatcher.State;
                var nativeLifecycleInvocationBridge = OpenACAINativeLifecycleInvocationBridge.State;
                var nativeLifecycleInvocationAdapter = OpenACAINativeLifecycleInvocationAdapter.State;
                var nativeLifecycleInvocationTokenResolver = OpenACAINativeLifecycleInvocationTokenResolver.State;
                var nativeLifecycleInvocationJournal = OpenACAINativeLifecycleInvocationJournal.State;
                var nativeLifecycleInvocationScheduler = OpenACAINativeLifecycleInvocationScheduler.State;
                var nativeLifecycleInvocationExecutor = OpenACAINativeLifecycleInvocationExecutor.State;
                var fileIntegrity = OpenACAIFileIntegrity.Snapshot;
                var runtimeAttestation = OpenACAIRuntimeAttestation.Snapshot;
                var compatibilityDependencies = OpenACAICompatibilityDependencies.Snapshot;
                var bepinExPluginCompatibility = OpenACAIBepInExPluginCompatibility.State;
                var sonsState = OpenACAISons.State;
                var sonsEnvironment = OpenACAISons.Environment;
                var sonsAssets = OpenACAISonsAssets.State;
                var sonsBuilding = OpenACAISonsBuilding.State;
                var sonsCrafting = OpenACAISonsCrafting.State;
                var sonsItemRegistration = OpenACAISonsItemRegistration.State;
                var sonsItems = OpenACAISonsItems.Catalog;
                var sonsInventory = OpenACAISonsInventory.State;
                var sonsNetworking = OpenACAISonsNetworking.State;
                var sonsSaveData = OpenACAISonsSaveData.State;
                var sonsSaveLifecycle = OpenACAISonsSaveLifecycle.State;
                var sonsSaveRegistry = OpenACAISonsSaveRegistry.State;
                var compatibilityShims = OpenACAIRuntime.CompatibilityShimsSnapshot();
                var menuProviders = OpenACAIMenu.Snapshot();
                var loaderReady = OpenACAILoaderCapabilities.IsReady && !OpenACAILoaderCapabilities.StartupFailed;
                var runtimeLibrariesProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.RuntimeLibraries, StringComparison.OrdinalIgnoreCase));
                var bepinExPluginCompatibilityProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.BepInExPluginCompatibility, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleRegistry, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleDispatcherProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleDispatcher, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleInvocationBridgeProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleInvocationBridge, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleInvocationAdapterProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleInvocationAdapter, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleInvocationTokenResolverProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleInvocationTokenResolver, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleInvocationJournalProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleInvocationJournal, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleInvocationSchedulerProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleInvocationScheduler, StringComparison.OrdinalIgnoreCase));
                var nativeLifecycleInvocationExecutorProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, OpenACAIMenuProviderIds.NativeLifecycleInvocationExecutor, StringComparison.OrdinalIgnoreCase));
                OpenACAIModInventoryCatalogSection libraryCatalogSection;
                var libraryCatalogReady = catalog != null
                    && inventory != null
                    && catalog.TryGetSection("libraries", out libraryCatalogSection)
                    && libraryCatalogSection.Count == catalog.LibraryCount
                    && catalog.LibraryCount == inventory.Libraries.Count;
                var libraryBoundaryReady = inventory != null
                    && catalog != null
                    && libraryCatalogReady
                    && inventory.Mods.All(entry => !entry.IsLibrary)
                    && inventory.Libraries.All(entry => entry.IsLibrary);
                var nativeHostLibraryBoundaryReady = inventory != null
                    && nativeModHost != null
                    && nativeModHost.LibraryExcludedCount == inventory.Libraries.Count;
                var bepinExPluginCompatibilityLibraryBoundaryReady = inventory != null
                    && bepinExPluginCompatibility != null
                    && bepinExPluginCompatibility.LibraryExcludedCount == inventory.Libraries.Count;
                var bepinExPluginCompatibilityNativeBoundaryReady = inventory != null
                    && bepinExPluginCompatibility != null
                    && bepinExPluginCompatibility.OpenAcaiNativeExcludedCount == inventory.OpenAcaiMods.Count;
                var bepinExPluginCompatibilityRedLoaderBoundaryReady = inventory != null
                    && bepinExPluginCompatibility != null
                    && bepinExPluginCompatibility.RedLoaderCompatibilityExcludedCount == inventory.LegacyRedLoaderMods.Count;
                var bepinExPluginCompatibilityReady = bepinExPluginCompatibilityProviderReady
                    && bepinExPluginCompatibility != null
                    && bepinExPluginCompatibility.Ready
                    && bepinExPluginCompatibility.CompatibilityIssueCount == 0
                    && bepinExPluginCompatibility.UnresolvedAssemblyReferenceCount == 0
                    && bepinExPluginCompatibility.ExecutionPlanIssueCount == 0
                    && bepinExPluginCompatibility.LifecycleMethodIssueCount == 0
                    && bepinExPluginCompatibility.ParserHarnessIssueCount == 0
                    && bepinExPluginCompatibility.ParserHarnessUnsafeLocationCount == 0
                    && bepinExPluginCompatibility.ParserHarnessUnresolvedAssemblyReferenceCount == 0
                    && bepinExPluginCompatibility.ParserHarnessExecutionPlanIssueCount == 0
                    && bepinExPluginCompatibility.ParserHarnessLifecycleMethodIssueCount == 0
                    && bepinExPluginCompatibility.LoadGateOpenCount == 0
                    && bepinExPluginCompatibility.LoadEligibleCount == 0
                    && bepinExPluginCompatibility.PluginLoadedByOpenAcaiCount == 0
                    && !bepinExPluginCompatibility.RuntimePluginLoadingEnabled
                    && bepinExPluginCompatibilityLibraryBoundaryReady
                    && bepinExPluginCompatibilityNativeBoundaryReady
                    && bepinExPluginCompatibilityRedLoaderBoundaryReady;
                var nativeLifecycleLibraryBoundaryReady = inventory != null
                    && nativeLifecycleRegistry != null
                    && nativeLifecycleRegistry.LibraryExcludedCount == inventory.Libraries.Count;
                var nativeLifecycleDispatcherLibraryBoundaryReady = inventory != null
                    && nativeLifecycleDispatcher != null
                    && nativeLifecycleDispatcher.LibraryExcludedCount == inventory.Libraries.Count;
                var nativeLifecycleInvocationBridgeLibraryBoundaryReady = inventory != null
                    && nativeLifecycleInvocationBridge != null
                    && nativeLifecycleInvocationBridge.LibraryExcludedCount == inventory.Libraries.Count;
                var nativeLifecycleInvocationAdapterLibraryBoundaryReady = inventory != null
                    && nativeLifecycleInvocationAdapter != null
                    && nativeLifecycleInvocationAdapter.LibraryExcludedCount == inventory.Libraries.Count;
                var nativeLifecycleInvocationTokenResolverLibraryBoundaryReady = inventory != null
                    && nativeLifecycleInvocationTokenResolver != null
                    && nativeLifecycleInvocationTokenResolver.LibraryExcludedCount == inventory.Libraries.Count;
                var nativeLifecycleInvocationJournalLibraryBoundaryReady = inventory != null
                    && nativeLifecycleInvocationJournal != null
                    && nativeLifecycleInvocationJournal.LibraryExcludedCount == inventory.Libraries.Count;
                var nativeLifecycleInvocationSchedulerLibraryBoundaryReady = inventory != null
                    && nativeLifecycleInvocationScheduler != null
                    && nativeLifecycleInvocationScheduler.LibraryExcludedCount == inventory.Libraries.Count;
                var nativeLifecycleInvocationExecutorLibraryBoundaryReady = inventory != null
                    && nativeLifecycleInvocationExecutor != null
                    && nativeLifecycleInvocationExecutor.LibraryExcludedCount == inventory.Libraries.Count;
                var requirements = new[]
                {
                    Requirement(
                        "loader-ready",
                        "Loader ready",
                        true,
                        loaderReady,
                        loaderReady ? "READY" : OpenACAILoaderCapabilities.StartupFailed ? "FAILED" : "PENDING",
                        string.IsNullOrWhiteSpace(OpenACAILoaderCapabilities.StartupError) ? "OpenACAI runtime startup state is published." : OpenACAILoaderCapabilities.StartupError),
                    Requirement(
                        "public-sdk-services",
                        "Public SDK services",
                        true,
                        OpenACAIModding.Runtime != null && inventory != null && catalog != null && bepinExPluginCompatibility != null && nativeModHost != null && nativeLifecycleRegistry != null && nativeLifecycleDispatcher != null && nativeLifecycleInvocationBridge != null && nativeLifecycleInvocationAdapter != null && nativeLifecycleInvocationTokenResolver != null && nativeLifecycleInvocationJournal != null && nativeLifecycleInvocationScheduler != null && nativeLifecycleInvocationExecutor != null && fileIntegrity != null && runtimeAttestation != null && compatibilityDependencies != null && sonsState != null && sonsEnvironment != null && sonsAssets != null && sonsBuilding != null && sonsCrafting != null && sonsItemRegistration != null && sonsItems != null && sonsInventory != null && sonsNetworking != null && sonsSaveData != null && sonsSaveLifecycle != null && sonsSaveRegistry != null,
                        "READY",
                        "Runtime, inventory, catalog, BepInEx plugin compatibility, native host, native lifecycle registry, native lifecycle dispatcher, native lifecycle invocation bridge, native lifecycle invocation adapter, native lifecycle invocation token resolver, native lifecycle invocation journal, native lifecycle invocation scheduler, native lifecycle invocation executor, file integrity, compatibility dependencies, runtime attestation, menu, config, and Sons service/save/item/building/networking snapshots are public OpenACAI surfaces."),
                    Requirement(
                        "compatibility-shim-state",
                        "Compatibility shim state",
                        true,
                        compatibilityShims.Count > 0,
                        compatibilityShims.Count > 0 ? "READY" : "CHECK",
                        "BepInEx, RedLoader, IL2CPP, SonsSdk, and patch-host compatibility states are disclosed separately."),
                    Requirement(
                        "compatibility-dependencies",
                        "Compatibility dependencies",
                        true,
                        compatibilityDependencies != null && compatibilityDependencies.AllReady,
                        compatibilityDependencies == null ? "MISSING" : compatibilityDependencies.AllReady ? "READY" : compatibilityDependencies.AllPackaged ? "CHECK" : "FAILED",
                        CompatibilityDependencyDetail(compatibilityDependencies)),
                    Requirement(
                        "file-integrity",
                        "File integrity",
                        true,
                        fileIntegrity != null && fileIntegrity.Ready,
                        fileIntegrity == null ? "MISSING" : fileIntegrity.Status,
                        FileIntegrityDetail(fileIntegrity)),
                    Requirement(
                        "runtime-attestation",
                        "Runtime attestation",
                        true,
                        runtimeAttestation != null && runtimeAttestation.Ready,
                        runtimeAttestation == null ? "MISSING" : runtimeAttestation.Status,
                        RuntimeAttestationDetail(runtimeAttestation)),
                    Requirement(
                        "library-separation",
                        "Library separation",
                        true,
                        catalog != null && catalog.ModCount == inventory.Mods.Count && inventory.Mods.All(entry => !entry.IsLibrary),
                        "READY",
                        "SDK/runtime libraries are cataloged as libraries, not user-facing mods."),
                    Requirement(
                        "runtime-library-surface",
                        "Runtime library surface",
                        true,
                        runtimeLibrariesProviderReady && libraryBoundaryReady,
                        runtimeLibrariesProviderReady && libraryBoundaryReady ? "READY" : runtimeLibrariesProviderReady ? "CHECK" : "MISSING",
                        "Libraries are exposed through the Libraries tab, catalog section, and Runtime Libraries status provider."),
                    Requirement(
                        "openacai-bepinex-plugin-compatibility-gate",
                        "OpenACAI BepInEx plugin compatibility gate",
                        true,
                        bepinExPluginCompatibilityReady,
                        bepinExPluginCompatibility == null ? "MISSING" : bepinExPluginCompatibilityReady ? "READY" : "CHECK",
                        bepinExPluginCompatibility == null
                            ? "OpenACAI BepInEx plugin compatibility gate state has not been published."
                            : "Candidates "
                                + bepinExPluginCompatibility.CandidateCount
                                + ", metadata-ready "
                                + bepinExPluginCompatibility.CompatibilityReadyCount
                                + ", compatibility issues "
                                + bepinExPluginCompatibility.CompatibilityIssueCount
                                + ", metadata read "
                                + bepinExPluginCompatibility.MetadataReadCount
                                + ", metadata issues "
                                + bepinExPluginCompatibility.MetadataIssueCount
                                + ", BepInPlugin attributes "
                                + bepinExPluginCompatibility.PluginAttributeCount
                                + ", references "
                                + bepinExPluginCompatibility.ResolvedAssemblyReferenceCount
                                + "/"
                                + bepinExPluginCompatibility.AssemblyReferenceCount
                                + ", unresolved references "
                                + bepinExPluginCompatibility.UnresolvedAssemblyReferenceCount
                                + ", first unresolved reference "
                                + FirstAssemblyReferenceName(bepinExPluginCompatibility.FirstUnresolvedCandidateAssemblyReference)
                                + ", execution plans "
                                + bepinExPluginCompatibility.ExecutionPlanReadyCount
                                + "/"
                                + bepinExPluginCompatibility.ExecutionPlanCount
                                + ", execution plan issues "
                                + bepinExPluginCompatibility.ExecutionPlanIssueCount
                                + ", first execution plan issue "
                                + FirstExecutionPlanIssueName(bepinExPluginCompatibility.FirstExecutionPlanIssue)
                                + ", lifecycle/entrypoint methods "
                                + bepinExPluginCompatibility.LifecycleMethodReadyCount
                                + "/"
                                + bepinExPluginCompatibility.LifecycleMethodCount
                                + ", lifecycle/entrypoint method issues "
                                + bepinExPluginCompatibility.LifecycleMethodIssueCount
                                + ", first lifecycle/entrypoint method issue "
                                + FirstLifecycleMethodIssueName(bepinExPluginCompatibility.FirstLifecycleMethodIssue)
                                + ", parser harnesses "
                                + bepinExPluginCompatibility.ParserHarnessCount
                                + ", parser harness ready "
                                + bepinExPluginCompatibility.ParserHarnessReadyCount
                                + ", parser harness issues "
                                + bepinExPluginCompatibility.ParserHarnessIssueCount
                                + ", parser harness unsafe locations "
                                + bepinExPluginCompatibility.ParserHarnessUnsafeLocationCount
                                + ", parser harness references "
                                + bepinExPluginCompatibility.ParserHarnessResolvedAssemblyReferenceCount
                                + "/"
                                + bepinExPluginCompatibility.ParserHarnessAssemblyReferenceCount
                                + ", parser harness unresolved references "
                                + bepinExPluginCompatibility.ParserHarnessUnresolvedAssemblyReferenceCount
                                + ", parser harness first unresolved reference "
                                + FirstAssemblyReferenceName(bepinExPluginCompatibility.FirstUnresolvedParserHarnessAssemblyReference)
                                + ", parser harness execution plans "
                                + bepinExPluginCompatibility.ParserHarnessExecutionPlanReadyCount
                                + "/"
                                + bepinExPluginCompatibility.ParserHarnessExecutionPlanCount
                                + ", parser harness execution plan issues "
                                + bepinExPluginCompatibility.ParserHarnessExecutionPlanIssueCount
                                + ", parser harness first execution plan issue "
                                + FirstExecutionPlanIssueName(bepinExPluginCompatibility.FirstParserHarnessExecutionPlanIssue)
                                + ", parser harness lifecycle/entrypoint methods "
                                + bepinExPluginCompatibility.ParserHarnessLifecycleMethodReadyCount
                                + "/"
                                + bepinExPluginCompatibility.ParserHarnessLifecycleMethodCount
                                + ", parser harness lifecycle/entrypoint method issues "
                                + bepinExPluginCompatibility.ParserHarnessLifecycleMethodIssueCount
                                + ", parser harness first lifecycle/entrypoint method issue "
                                + FirstLifecycleMethodIssueName(bepinExPluginCompatibility.FirstParserHarnessLifecycleMethodIssue)
                                + ", positive parser verified "
                                + bepinExPluginCompatibility.PositiveMetadataParserVerified
                                + ", load eligible "
                                + bepinExPluginCompatibility.LoadEligibleCount
                                + ", load gate open "
                                + bepinExPluginCompatibility.LoadGateOpenCount
                                + ", load gate blocked "
                                + bepinExPluginCompatibility.LoadGateBlockedCount
                                + ", load attempts "
                                + bepinExPluginCompatibility.LoadAttemptRequestCount
                                + ", accepted load attempts "
                                + bepinExPluginCompatibility.AcceptedLoadAttemptCount
                                + ", blocked load attempts "
                                + bepinExPluginCompatibility.BlockedLoadAttemptCount
                                + ", rejected load attempts "
                                + bepinExPluginCompatibility.RejectedLoadAttemptCount
                                + ", last load attempt "
                                + (string.IsNullOrWhiteSpace(bepinExPluginCompatibility.LastLoadAttemptReason) ? "none" : bepinExPluginCompatibility.LastLoadAttemptReason)
                                + ", OpenACAI-loaded plugins "
                                + bepinExPluginCompatibility.PluginLoadedByOpenAcaiCount
                                + ", libraries excluded "
                                + bepinExPluginCompatibility.LibraryExcludedCount
                                + ", Modstoke-native excluded "
                                + bepinExPluginCompatibility.OpenAcaiNativeExcludedCount
                                + ", RedLoader excluded "
                                + bepinExPluginCompatibility.RedLoaderCompatibilityExcludedCount
                                + "; runtime plugin loading "
                                + (bepinExPluginCompatibility.RuntimePluginLoadingEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-runtime",
                        "Modstoke native runtime",
                        true,
                        nativeModRuntime.LoadAttempted && nativeModRuntime.FailedCount == 0 && nativeModRuntime.DeferredCount == 0,
                        !nativeModRuntime.LoadAttempted ? "MISSING" : nativeModRuntime.FailedCount == 0 && nativeModRuntime.DeferredCount == 0 ? "READY" : "CHECK",
                        "Startup results: " + nativeModRuntime.LoadedCount + " loaded, " + nativeModRuntime.FailedCount + " failed, " + nativeModRuntime.DeferredCount + " deferred. Diagnostic lifecycle plans do not execute these callbacks."),
                    Requirement(
                        "openacai-native-host-gate",
                        "Modstoke native host gate",
                        true,
                        nativeModHost != null && nativeModHost.Ready && nativeModHost.ValidationIssueCount == 0 && nativeModHost.UnresolvedAssemblyReferenceCount == 0 && nativeModHost.ExecutionPlanIssueCount == 0 && nativeModHost.LifecycleHookMethodIssueCount == 0 && nativeHostLibraryBoundaryReady,
                        nativeModHost == null ? "MISSING" : nativeModHost.Ready && nativeModHost.ValidationIssueCount == 0 && nativeModHost.UnresolvedAssemblyReferenceCount == 0 && nativeModHost.ExecutionPlanIssueCount == 0 && nativeModHost.LifecycleHookMethodIssueCount == 0 && nativeHostLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeModHost == null
                            ? "Modstoke native mod host state has not been published."
                            : "Candidates "
                                + nativeModHost.CandidateCount
                                + ", hostable "
                                + nativeModHost.HostableCandidateCount
                                + ", metadata-only "
                                + nativeModHost.MetadataOnlyCandidateCount
                                + ", libraries excluded "
                                + nativeModHost.LibraryExcludedCount
                                + ", metadata read "
                                + nativeModHost.AssemblyMetadataReadCount
                                + ", metadata issues "
                                + nativeModHost.AssemblyMetadataIssueCount
                                + ", references "
                                + nativeModHost.ResolvedAssemblyReferenceCount
                                + "/"
                                + nativeModHost.AssemblyReferenceCount
                                + ", unresolved references "
                                + nativeModHost.UnresolvedAssemblyReferenceCount
                                + ", execution plans "
                                + nativeModHost.ExecutionPlanReadyCount
                                + "/"
                                + nativeModHost.CandidateCount
                                + ", execution plan issues "
                                + nativeModHost.ExecutionPlanIssueCount
                                + ", lifecycle hooks "
                                + nativeModHost.LifecycleHookDeclarationCount
                                + ", hook methods "
                                + nativeModHost.LifecycleHookMethodReadyCount
                                + "/"
                                + nativeModHost.LifecycleHookDeclarationCount
                                + ", hook method issues "
                                + nativeModHost.LifecycleHookMethodIssueCount
                                + ", entrypoint types "
                                + nativeModHost.EntrypointTypeMatchCount
                                + ", entrypoint methods "
                                + nativeModHost.EntrypointMethodMatchCount
                                + ", validation issues "
                                + nativeModHost.ValidationIssueCount
                                + "; runtime execution "
                                + (nativeModHost.RuntimeExecutionEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-registry",
                        "Modstoke native lifecycle registry",
                        true,
                        nativeLifecycleProviderReady && nativeLifecycleRegistry != null && nativeLifecycleRegistry.Ready && nativeLifecycleRegistry.RegistrationIssueCount == 0 && nativeLifecycleRegistry.DryRunIssueCount == 0 && nativeLifecycleRegistry.HookMethodIssueCount == 0 && nativeLifecycleLibraryBoundaryReady,
                        nativeLifecycleRegistry == null ? "MISSING" : nativeLifecycleProviderReady && nativeLifecycleRegistry.Ready && nativeLifecycleRegistry.RegistrationIssueCount == 0 && nativeLifecycleRegistry.DryRunIssueCount == 0 && nativeLifecycleRegistry.HookMethodIssueCount == 0 && nativeLifecycleLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleRegistry == null
                            ? "Modstoke native lifecycle registry state has not been published."
                            : "Registrations "
                                + nativeLifecycleRegistry.RegistrationCount
                                + ", planned hooks "
                                + nativeLifecycleRegistry.PlannedHookCount
                                + "/"
                                + nativeLifecycleRegistry.LifecycleHookDeclarationCount
                                + ", deferred "
                                + nativeLifecycleRegistry.DeferredHookCount
                                + ", hook methods "
                                + nativeLifecycleRegistry.HookMethodReadyCount
                                + "/"
                                + nativeLifecycleRegistry.PlannedHookCount
                                + ", hook method issues "
                                + nativeLifecycleRegistry.HookMethodIssueCount
                                + ", registration issues "
                                + nativeLifecycleRegistry.RegistrationIssueCount
                                + ", invocation gate open "
                                + nativeLifecycleRegistry.InvocationGateOpenCount
                                + ", invocation gate blocked "
                                + nativeLifecycleRegistry.InvocationGateBlockedCount
                                + ", dry-run ready "
                                + nativeLifecycleRegistry.DryRunReadyCount
                                + "/"
                                + nativeLifecycleRegistry.DryRunEvaluationCount
                                + ", dry-run blocked "
                                + nativeLifecycleRegistry.DryRunBlockedCount
                                + ", dry-run issues "
                                + nativeLifecycleRegistry.DryRunIssueCount
                                + ", accepted plans "
                                + nativeLifecycleRegistry.AcceptedPlanCount
                                + ", rejected plans "
                                + nativeLifecycleRegistry.RejectedPlanCount
                                + ", libraries excluded "
                                + nativeLifecycleRegistry.LibraryExcludedCount
                                + "; runtime invocation "
                                + (nativeLifecycleRegistry.RuntimeInvocationEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-dispatcher",
                        "Modstoke native lifecycle dispatcher",
                        true,
                        nativeLifecycleDispatcherProviderReady && nativeLifecycleDispatcher != null && nativeLifecycleDispatcher.Ready && nativeLifecycleDispatcher.DispatchIssueCount == 0 && nativeLifecycleDispatcher.DispatchGateOpenCount == 0 && nativeLifecycleDispatcher.DispatchIntentIssueCount == 0 && nativeLifecycleDispatcher.DispatchIntentGateOpenCount == 0 && !nativeLifecycleDispatcher.RuntimeDispatchEnabled && nativeLifecycleDispatcherLibraryBoundaryReady,
                        nativeLifecycleDispatcher == null ? "MISSING" : nativeLifecycleDispatcherProviderReady && nativeLifecycleDispatcher.Ready && nativeLifecycleDispatcher.DispatchIssueCount == 0 && nativeLifecycleDispatcher.DispatchGateOpenCount == 0 && nativeLifecycleDispatcher.DispatchIntentIssueCount == 0 && nativeLifecycleDispatcher.DispatchIntentGateOpenCount == 0 && !nativeLifecycleDispatcher.RuntimeDispatchEnabled && nativeLifecycleDispatcherLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleDispatcher == null
                            ? "Modstoke native lifecycle dispatcher state has not been published."
                            : "Dispatch targets "
                                + nativeLifecycleDispatcher.DispatchTargetCount
                                + ", metadata-ready "
                                + nativeLifecycleDispatcher.DispatchMetadataReadyCount
                                + ", dispatch issues "
                                + nativeLifecycleDispatcher.DispatchIssueCount
                                + ", dispatch gate open "
                                + nativeLifecycleDispatcher.DispatchGateOpenCount
                                + ", dispatch gate blocked "
                                + nativeLifecycleDispatcher.DispatchGateBlockedCount
                                + ", dispatch intentions "
                                + nativeLifecycleDispatcher.DispatchIntentCount
                                + ", queued intentions "
                                + nativeLifecycleDispatcher.QueuedDispatchIntentCount
                                + ", intention issues "
                                + nativeLifecycleDispatcher.DispatchIntentIssueCount
                                + ", intention gate open "
                                + nativeLifecycleDispatcher.DispatchIntentGateOpenCount
                                + ", intention gate blocked "
                                + nativeLifecycleDispatcher.DispatchIntentGateBlockedCount
                                + ", hook methods "
                                + nativeLifecycleDispatcher.HookMethodReadyCount
                                + ", hook method issues "
                                + nativeLifecycleDispatcher.HookMethodIssueCount
                                + ", dry-run ready "
                                + nativeLifecycleDispatcher.DryRunReadyCount
                                + ", dry-run issues "
                                + nativeLifecycleDispatcher.DryRunIssueCount
                                + ", plan requests "
                                + nativeLifecycleDispatcher.DispatchPlanRequestCount
                                + ", accepted plans "
                                + nativeLifecycleDispatcher.AcceptedDispatchPlanCount
                                + ", rejected plans "
                                + nativeLifecycleDispatcher.RejectedDispatchPlanCount
                                + ", libraries excluded "
                                + nativeLifecycleDispatcher.LibraryExcludedCount
                                + "; runtime dispatch "
                                + (nativeLifecycleDispatcher.RuntimeDispatchEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-invocation-bridge",
                        "Modstoke native lifecycle invocation bridge",
                        true,
                        nativeLifecycleInvocationBridgeProviderReady && nativeLifecycleInvocationBridge != null && nativeLifecycleInvocationBridge.Ready && nativeLifecycleInvocationBridge.InvocationIssueCount == 0 && nativeLifecycleInvocationBridge.InvocationGateOpenCount == 0 && !nativeLifecycleInvocationBridge.RuntimeInvocationEnabled && nativeLifecycleInvocationBridgeLibraryBoundaryReady,
                        nativeLifecycleInvocationBridge == null ? "MISSING" : nativeLifecycleInvocationBridgeProviderReady && nativeLifecycleInvocationBridge.Ready && nativeLifecycleInvocationBridge.InvocationIssueCount == 0 && nativeLifecycleInvocationBridge.InvocationGateOpenCount == 0 && !nativeLifecycleInvocationBridge.RuntimeInvocationEnabled && nativeLifecycleInvocationBridgeLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleInvocationBridge == null
                            ? "Modstoke native lifecycle invocation bridge state has not been published."
                            : "Invocation requests "
                                + nativeLifecycleInvocationBridge.InvocationRequestCount
                                + ", adapter-ready "
                                + nativeLifecycleInvocationBridge.AdapterReadyCount
                                + ", invocation issues "
                                + nativeLifecycleInvocationBridge.InvocationIssueCount
                                + ", invocation gate open "
                                + nativeLifecycleInvocationBridge.InvocationGateOpenCount
                                + ", invocation gate blocked "
                                + nativeLifecycleInvocationBridge.InvocationGateBlockedCount
                                + ", dispatch targets "
                                + nativeLifecycleInvocationBridge.DispatchTargetCount
                                + ", dispatch intentions "
                                + nativeLifecycleInvocationBridge.DispatchIntentCount
                                + ", queued intentions "
                                + nativeLifecycleInvocationBridge.QueuedDispatchIntentCount
                                + ", plan requests "
                                + nativeLifecycleInvocationBridge.InvocationPlanRequestCount
                                + ", accepted plans "
                                + nativeLifecycleInvocationBridge.AcceptedInvocationPlanCount
                                + ", rejected plans "
                                + nativeLifecycleInvocationBridge.RejectedInvocationPlanCount
                                + ", libraries excluded "
                                + nativeLifecycleInvocationBridge.LibraryExcludedCount
                                + "; runtime invocation "
                                + (nativeLifecycleInvocationBridge.RuntimeInvocationEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-invocation-adapter",
                        "Modstoke native lifecycle invocation adapter",
                        true,
                        nativeLifecycleInvocationAdapterProviderReady && nativeLifecycleInvocationAdapter != null && nativeLifecycleInvocationAdapter.Ready && nativeLifecycleInvocationAdapter.BindingIssueCount == 0 && nativeLifecycleInvocationAdapter.InvocationGateOpenCount == 0 && nativeLifecycleInvocationAdapter.InvocationEligibleCount == 0 && !nativeLifecycleInvocationAdapter.RuntimeInvocationEnabled && nativeLifecycleInvocationAdapterLibraryBoundaryReady,
                        nativeLifecycleInvocationAdapter == null ? "MISSING" : nativeLifecycleInvocationAdapterProviderReady && nativeLifecycleInvocationAdapter.Ready && nativeLifecycleInvocationAdapter.BindingIssueCount == 0 && nativeLifecycleInvocationAdapter.InvocationGateOpenCount == 0 && nativeLifecycleInvocationAdapter.InvocationEligibleCount == 0 && !nativeLifecycleInvocationAdapter.RuntimeInvocationEnabled && nativeLifecycleInvocationAdapterLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleInvocationAdapter == null
                            ? "Modstoke native lifecycle invocation adapter state has not been published."
                            : "Bindings "
                                + nativeLifecycleInvocationAdapter.BindingCount
                                + ", binding-ready "
                                + nativeLifecycleInvocationAdapter.BindingReadyCount
                                + ", binding issues "
                                + nativeLifecycleInvocationAdapter.BindingIssueCount
                                + ", tokens issued "
                                + nativeLifecycleInvocationAdapter.BindingTokenIssuedCount
                                + ", invocation eligible "
                                + nativeLifecycleInvocationAdapter.InvocationEligibleCount
                                + ", invocation gate open "
                                + nativeLifecycleInvocationAdapter.InvocationGateOpenCount
                                + ", invocation gate blocked "
                                + nativeLifecycleInvocationAdapter.InvocationGateBlockedCount
                                + ", bridge requests "
                                + nativeLifecycleInvocationAdapter.InvocationRequestCount
                                + ", bridge adapter-ready "
                                + nativeLifecycleInvocationAdapter.AdapterReadyCount
                                + ", bridge issues "
                                + nativeLifecycleInvocationAdapter.InvocationIssueCount
                                + ", bridge plans "
                                + nativeLifecycleInvocationAdapter.BridgePlanRequestCount
                                + ", accepted bridge plans "
                                + nativeLifecycleInvocationAdapter.BridgeAcceptedPlanCount
                                + ", rejected bridge plans "
                                + nativeLifecycleInvocationAdapter.BridgeRejectedPlanCount
                                + ", libraries excluded "
                                + nativeLifecycleInvocationAdapter.LibraryExcludedCount
                                + "; runtime invocation "
                                + (nativeLifecycleInvocationAdapter.RuntimeInvocationEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-invocation-token-resolver",
                        "Modstoke native lifecycle invocation token resolver",
                        true,
                        nativeLifecycleInvocationTokenResolverProviderReady && nativeLifecycleInvocationTokenResolver != null && nativeLifecycleInvocationTokenResolver.Ready && nativeLifecycleInvocationTokenResolver.TokenIssueCount == 0 && nativeLifecycleInvocationTokenResolver.InvocationGateOpenCount == 0 && nativeLifecycleInvocationTokenResolver.InvocationEligibleCount == 0 && !nativeLifecycleInvocationTokenResolver.RuntimeInvocationEnabled && nativeLifecycleInvocationTokenResolverLibraryBoundaryReady,
                        nativeLifecycleInvocationTokenResolver == null ? "MISSING" : nativeLifecycleInvocationTokenResolverProviderReady && nativeLifecycleInvocationTokenResolver.Ready && nativeLifecycleInvocationTokenResolver.TokenIssueCount == 0 && nativeLifecycleInvocationTokenResolver.InvocationGateOpenCount == 0 && nativeLifecycleInvocationTokenResolver.InvocationEligibleCount == 0 && !nativeLifecycleInvocationTokenResolver.RuntimeInvocationEnabled && nativeLifecycleInvocationTokenResolverLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleInvocationTokenResolver == null
                            ? "Modstoke native lifecycle invocation token resolver state has not been published."
                            : "Token resolutions "
                                + nativeLifecycleInvocationTokenResolver.TokenResolutionCount
                                + ", resolved "
                                + nativeLifecycleInvocationTokenResolver.TokenResolvedCount
                                + ", token issues "
                                + nativeLifecycleInvocationTokenResolver.TokenIssueCount
                                + ", binding tokens issued "
                                + nativeLifecycleInvocationTokenResolver.BindingTokenIssuedCount
                                + ", invocation eligible "
                                + nativeLifecycleInvocationTokenResolver.InvocationEligibleCount
                                + ", invocation gate open "
                                + nativeLifecycleInvocationTokenResolver.InvocationGateOpenCount
                                + ", invocation gate blocked "
                                + nativeLifecycleInvocationTokenResolver.InvocationGateBlockedCount
                                + ", lookup requests "
                                + nativeLifecycleInvocationTokenResolver.LookupRequestCount
                                + ", accepted lookups "
                                + nativeLifecycleInvocationTokenResolver.AcceptedLookupCount
                                + ", rejected lookups "
                                + nativeLifecycleInvocationTokenResolver.RejectedLookupCount
                                + ", last lookup status "
                                + nativeLifecycleInvocationTokenResolver.LastLookupStatus
                                + ", last lookup reason "
                                + nativeLifecycleInvocationTokenResolver.LastLookupReason
                                + ", adapter bindings "
                                + nativeLifecycleInvocationTokenResolver.BindingCount
                                + ", adapter binding-ready "
                                + nativeLifecycleInvocationTokenResolver.BindingReadyCount
                                + ", adapter binding issues "
                                + nativeLifecycleInvocationTokenResolver.BindingIssueCount
                                + ", libraries excluded "
                                + nativeLifecycleInvocationTokenResolver.LibraryExcludedCount
                                + "; runtime invocation "
                                + (nativeLifecycleInvocationTokenResolver.RuntimeInvocationEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-invocation-journal",
                        "Modstoke native lifecycle invocation journal",
                        true,
                        nativeLifecycleInvocationJournalProviderReady && nativeLifecycleInvocationJournal != null && nativeLifecycleInvocationJournal.Ready && nativeLifecycleInvocationJournal.JournalIssueCount == 0 && nativeLifecycleInvocationJournal.ReplayGateOpenCount == 0 && nativeLifecycleInvocationJournal.ReplayEligibleCount == 0 && !nativeLifecycleInvocationJournal.RuntimeInvocationEnabled && nativeLifecycleInvocationJournalLibraryBoundaryReady,
                        nativeLifecycleInvocationJournal == null ? "MISSING" : nativeLifecycleInvocationJournalProviderReady && nativeLifecycleInvocationJournal.Ready && nativeLifecycleInvocationJournal.JournalIssueCount == 0 && nativeLifecycleInvocationJournal.ReplayGateOpenCount == 0 && nativeLifecycleInvocationJournal.ReplayEligibleCount == 0 && !nativeLifecycleInvocationJournal.RuntimeInvocationEnabled && nativeLifecycleInvocationJournalLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleInvocationJournal == null
                            ? "Modstoke native lifecycle invocation journal state has not been published."
                            : "Journal entries "
                                + nativeLifecycleInvocationJournal.JournalEntryCount
                                + ", journaled "
                                + nativeLifecycleInvocationJournal.JournaledEntryCount
                                + ", journal issues "
                                + nativeLifecycleInvocationJournal.JournalIssueCount
                                + ", replay eligible "
                                + nativeLifecycleInvocationJournal.ReplayEligibleCount
                                + ", replay gate open "
                                + nativeLifecycleInvocationJournal.ReplayGateOpenCount
                                + ", replay gate blocked "
                                + nativeLifecycleInvocationJournal.ReplayGateBlockedCount
                                + ", replay plan requests "
                                + nativeLifecycleInvocationJournal.ReplayPlanRequestCount
                                + ", accepted replay plans "
                                + nativeLifecycleInvocationJournal.AcceptedReplayPlanCount
                                + ", rejected replay plans "
                                + nativeLifecycleInvocationJournal.RejectedReplayPlanCount
                                + ", last replay status "
                                + nativeLifecycleInvocationJournal.LastReplayStatus
                                + ", last replay reason "
                                + nativeLifecycleInvocationJournal.LastReplayReason
                                + ", token resolutions "
                                + nativeLifecycleInvocationJournal.TokenResolutionCount
                                + ", resolved tokens "
                                + nativeLifecycleInvocationJournal.TokenResolvedCount
                                + ", token issues "
                                + nativeLifecycleInvocationJournal.TokenIssueCount
                                + ", lookup requests "
                                + nativeLifecycleInvocationJournal.LookupRequestCount
                                + ", accepted lookups "
                                + nativeLifecycleInvocationJournal.AcceptedLookupCount
                                + ", rejected lookups "
                                + nativeLifecycleInvocationJournal.RejectedLookupCount
                                + ", libraries excluded "
                                + nativeLifecycleInvocationJournal.LibraryExcludedCount
                                + "; runtime invocation "
                                + (nativeLifecycleInvocationJournal.RuntimeInvocationEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-invocation-scheduler",
                        "Modstoke native lifecycle invocation scheduler",
                        true,
                        nativeLifecycleInvocationSchedulerProviderReady && nativeLifecycleInvocationScheduler != null && nativeLifecycleInvocationScheduler.Ready && nativeLifecycleInvocationScheduler.ScheduleIssueCount == 0 && nativeLifecycleInvocationScheduler.ScheduleGateOpenCount == 0 && nativeLifecycleInvocationScheduler.ScheduleEligibleCount == 0 && !nativeLifecycleInvocationScheduler.RuntimeInvocationEnabled && nativeLifecycleInvocationSchedulerLibraryBoundaryReady,
                        nativeLifecycleInvocationScheduler == null ? "MISSING" : nativeLifecycleInvocationSchedulerProviderReady && nativeLifecycleInvocationScheduler.Ready && nativeLifecycleInvocationScheduler.ScheduleIssueCount == 0 && nativeLifecycleInvocationScheduler.ScheduleGateOpenCount == 0 && nativeLifecycleInvocationScheduler.ScheduleEligibleCount == 0 && !nativeLifecycleInvocationScheduler.RuntimeInvocationEnabled && nativeLifecycleInvocationSchedulerLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleInvocationScheduler == null
                            ? "Modstoke native lifecycle invocation scheduler state has not been published."
                            : "Schedule batches "
                                + nativeLifecycleInvocationScheduler.ScheduleBatchCount
                                + ", schedule entries "
                                + nativeLifecycleInvocationScheduler.ScheduleEntryCount
                                + ", scheduled entries "
                                + nativeLifecycleInvocationScheduler.ScheduledEntryCount
                                + ", schedule issues "
                                + nativeLifecycleInvocationScheduler.ScheduleIssueCount
                                + ", schedule eligible "
                                + nativeLifecycleInvocationScheduler.ScheduleEligibleCount
                                + ", schedule gate open "
                                + nativeLifecycleInvocationScheduler.ScheduleGateOpenCount
                                + ", schedule gate blocked "
                                + nativeLifecycleInvocationScheduler.ScheduleGateBlockedCount
                                + ", schedule plan requests "
                                + nativeLifecycleInvocationScheduler.SchedulePlanRequestCount
                                + ", accepted schedule plans "
                                + nativeLifecycleInvocationScheduler.AcceptedSchedulePlanCount
                                + ", rejected schedule plans "
                                + nativeLifecycleInvocationScheduler.RejectedSchedulePlanCount
                                + ", last schedule status "
                                + nativeLifecycleInvocationScheduler.LastScheduleStatus
                                + ", last schedule reason "
                                + nativeLifecycleInvocationScheduler.LastScheduleReason
                                + ", journal entries "
                                + nativeLifecycleInvocationScheduler.JournalEntryCount
                                + ", journaled "
                                + nativeLifecycleInvocationScheduler.JournaledEntryCount
                                + ", journal issues "
                                + nativeLifecycleInvocationScheduler.JournalIssueCount
                                + ", replay plan requests "
                                + nativeLifecycleInvocationScheduler.ReplayPlanRequestCount
                                + ", accepted replay plans "
                                + nativeLifecycleInvocationScheduler.AcceptedReplayPlanCount
                                + ", rejected replay plans "
                                + nativeLifecycleInvocationScheduler.RejectedReplayPlanCount
                                + ", libraries excluded "
                                + nativeLifecycleInvocationScheduler.LibraryExcludedCount
                                + "; runtime invocation "
                                + (nativeLifecycleInvocationScheduler.RuntimeInvocationEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "openacai-native-lifecycle-invocation-executor",
                        "Modstoke native lifecycle invocation executor",
                        true,
                        nativeLifecycleInvocationExecutorProviderReady && nativeLifecycleInvocationExecutor != null && nativeLifecycleInvocationExecutor.Ready && nativeLifecycleInvocationExecutor.ExecutionIssueCount == 0 && nativeLifecycleInvocationExecutor.ExecutionGateOpenCount == 0 && nativeLifecycleInvocationExecutor.ExecutionEligibleCount == 0 && nativeLifecycleInvocationExecutor.MethodInvocationCount == 0 && !nativeLifecycleInvocationExecutor.RuntimeInvocationEnabled && nativeLifecycleInvocationExecutorLibraryBoundaryReady,
                        nativeLifecycleInvocationExecutor == null ? "MISSING" : nativeLifecycleInvocationExecutorProviderReady && nativeLifecycleInvocationExecutor.Ready && nativeLifecycleInvocationExecutor.ExecutionIssueCount == 0 && nativeLifecycleInvocationExecutor.ExecutionGateOpenCount == 0 && nativeLifecycleInvocationExecutor.ExecutionEligibleCount == 0 && nativeLifecycleInvocationExecutor.MethodInvocationCount == 0 && !nativeLifecycleInvocationExecutor.RuntimeInvocationEnabled && nativeLifecycleInvocationExecutorLibraryBoundaryReady ? "READY" : "CHECK",
                        nativeLifecycleInvocationExecutor == null
                            ? "Modstoke native lifecycle invocation executor state has not been published."
                            : "Execution batches "
                                + nativeLifecycleInvocationExecutor.ExecutionBatchCount
                                + ", decisions "
                                + nativeLifecycleInvocationExecutor.ExecutionDecisionCount
                                + ", planned "
                                + nativeLifecycleInvocationExecutor.ExecutionPlannedCount
                                + ", issues "
                                + nativeLifecycleInvocationExecutor.ExecutionIssueCount
                                + ", eligible "
                                + nativeLifecycleInvocationExecutor.ExecutionEligibleCount
                                + ", gate open "
                                + nativeLifecycleInvocationExecutor.ExecutionGateOpenCount
                                + ", gate blocked "
                                + nativeLifecycleInvocationExecutor.ExecutionGateBlockedCount
                                + ", method invocations "
                                + nativeLifecycleInvocationExecutor.MethodInvocationCount
                                + ", execution plan requests "
                                + nativeLifecycleInvocationExecutor.ExecutionPlanRequestCount
                                + ", accepted execution plans "
                                + nativeLifecycleInvocationExecutor.AcceptedExecutionPlanCount
                                + ", rejected execution plans "
                                + nativeLifecycleInvocationExecutor.RejectedExecutionPlanCount
                                + ", last execution status "
                                + nativeLifecycleInvocationExecutor.LastExecutionStatus
                                + ", last execution reason "
                                + nativeLifecycleInvocationExecutor.LastExecutionReason
                                + ", schedule entries "
                                + nativeLifecycleInvocationExecutor.ScheduleEntryCount
                                + ", scheduled "
                                + nativeLifecycleInvocationExecutor.ScheduledEntryCount
                                + ", schedule issues "
                                + nativeLifecycleInvocationExecutor.ScheduleIssueCount
                                + ", schedule plan requests "
                                + nativeLifecycleInvocationExecutor.SchedulePlanRequestCount
                                + ", accepted schedule plans "
                                + nativeLifecycleInvocationExecutor.AcceptedSchedulePlanCount
                                + ", rejected schedule plans "
                                + nativeLifecycleInvocationExecutor.RejectedSchedulePlanCount
                                + ", libraries excluded "
                                + nativeLifecycleInvocationExecutor.LibraryExcludedCount
                                + "; runtime invocation "
                                + (nativeLifecycleInvocationExecutor.RuntimeInvocationEnabled ? "enabled" : "disabled")
                                + "."),
                    Requirement(
                        "sons-runtime-state",
                        "Sons runtime state",
                        false,
                        sonsState != null && sonsState.LifecycleEventCount > 0,
                        sonsState == null ? "MISSING" : sonsState.LifecycleEventCount > 0 ? "READY" : "PENDING",
                        sonsState == null
                            ? "Modstoke SDK runtime state has not been published."
                            : "Scene "
                                + (string.IsNullOrWhiteSpace(sonsState.SceneName) ? sonsState.SceneKind : sonsState.SceneName)
                                + "; lifecycle events "
                                + sonsState.LifecycleEventCount
                                + ", scene events "
                                + sonsState.SceneEventCount
                                + "."),
                    Requirement(
                        "sons-environment",
                        "Sons environment",
                        false,
                        sonsEnvironment != null && !string.IsNullOrWhiteSpace(sonsEnvironment.OpenAcaiRootDirectory) && !string.IsNullOrWhiteSpace(sonsEnvironment.GameExecutablePath),
                        sonsEnvironment == null ? "MISSING" : string.IsNullOrWhiteSpace(sonsEnvironment.OpenAcaiRootDirectory) ? "CHECK" : "READY",
                        sonsEnvironment == null
                            ? "Modstoke SDK environment state has not been published."
                            : "Game executable, OpenACAI root, runtime, mods, UserData, and legacy compatibility paths are published."),
                    Requirement(
                        "sons-save-data",
                        "Sons save data",
                        false,
                        sonsSaveData != null && sonsSaveData.OpenAcaiSaveDataDirectoryExists && sonsSaveData.OpenAcaiModSaveDataDirectoryExists,
                        sonsSaveData == null ? "MISSING" : sonsSaveData.OpenAcaiSaveDataDirectoryExists && sonsSaveData.OpenAcaiModSaveDataDirectoryExists ? "READY" : "CHECK",
                        sonsSaveData == null
                            ? "Modstoke SDK save-data state has not been published."
                            : sonsSaveData.IsolationModel
                                + "; OpenACAI saves root exists "
                                + sonsSaveData.OpenAcaiSaveDataDirectoryExists
                                + ", mod saves root exists "
                                + sonsSaveData.OpenAcaiModSaveDataDirectoryExists
                                + "."),
                    Requirement(
                        "sons-asset-paths",
                        "Sons asset paths",
                        false,
                        sonsAssets != null && sonsAssets.Ready,
                        sonsAssets == null ? "MISSING" : sonsAssets.Status,
                        sonsAssets == null ? "Modstoke SDK asset path state has not been published." : sonsAssets.Detail),
                    Requirement(
                        "sons-asset-loading",
                        "Sons asset loading",
                        false,
                        sonsAssets != null && sonsAssets.RuntimeAssetLoadingEnabled && sonsAssets.AssetBundleLoadFailureCount == 0,
                        sonsAssets == null ? "MISSING" : sonsAssets.AssetBundleLoadFailureCount == 0 ? "READY" : "CHECK",
                        sonsAssets == null
                            ? "Modstoke SDK asset loading state has not been published."
                            : sonsAssets.AssetBundleFileCount
                                + " AssetBundle candidate(s), "
                                + sonsAssets.LoadedAssetBundleCount
                                + " loaded, attempts "
                                + sonsAssets.AssetBundleLoadAttemptCount
                                + ", failures "
                                + sonsAssets.AssetBundleLoadFailureCount
                                + "; audio loading "
                                + (sonsAssets.RuntimeAudioLoadingEnabled ? "enabled" : "discovery-only")
                                + "."),
                    Requirement(
                        "sons-audio-loading",
                        "Sons audio loading",
                        false,
                        sonsAssets != null && sonsAssets.RuntimeAudioLoadingEnabled && sonsAssets.AudioClipLoadFailureCount == 0,
                        sonsAssets == null ? "MISSING" : sonsAssets.RuntimeAudioLoadingEnabled && sonsAssets.AudioClipLoadFailureCount == 0 ? "READY" : sonsAssets.AudioClipLoadFailureCount > 0 ? "CHECK" : "PENDING",
                        sonsAssets == null
                            ? "Modstoke SDK audio loading state has not been published."
                            : sonsAssets.AudioFileCount
                                + " audio file candidate(s), "
                                + sonsAssets.LoadedAudioClipCount
                                + " loaded, "
                                + sonsAssets.PendingAudioClipLoadCount
                                + " pending, attempts "
                                + sonsAssets.AudioClipLoadAttemptCount
                                + ", failures "
                                + sonsAssets.AudioClipLoadFailureCount
                                + "."),
                    Requirement(
                        "sons-item-catalog",
                        "Sons item catalog",
                        false,
                        sonsItems != null,
                        sonsItems == null ? "MISSING" : sonsItems.Ready ? "READY" : sonsItems.Status,
                        sonsItems == null ? "Modstoke SDK item metadata state has not been published." : sonsItems.Detail),
                    Requirement(
                        "sons-item-registration-gate",
                        "Sons item registration gate",
                        false,
                        sonsItemRegistration != null && sonsItemRegistration.PlanningEnabled && sonsItemRegistration.CatalogCollisionCount == 0,
                        sonsItemRegistration == null ? "MISSING" : sonsItemRegistration.PlanningEnabled && sonsItemRegistration.CatalogCollisionCount == 0 ? "READY" : "CHECK",
                        sonsItemRegistration == null
                            ? "Modstoke SDK item registration state has not been published."
                            : "Planning "
                                + (sonsItemRegistration.PlanningEnabled ? "enabled" : "disabled")
                                + "; runtime registration "
                                + (sonsItemRegistration.RuntimeItemRegistrationEnabled ? "enabled" : "disabled")
                                + "; registered "
                                + sonsItemRegistration.RegisteredDefinitionCount
                                + "; collisions "
                                + sonsItemRegistration.CatalogCollisionCount
                                + "; plans "
                                + sonsItemRegistration.PlanRequestCount
                                + "."),
                    Requirement(
                        "sons-inventory-readiness",
                        "Sons inventory readiness",
                        false,
                        sonsInventory != null,
                        sonsInventory == null ? "MISSING" : sonsInventory.Ready ? "READY" : sonsInventory.Status,
                        sonsInventory == null ? "Modstoke SDK inventory readiness state has not been published." : sonsInventory.Detail),
                    Requirement(
                        "sons-inventory-action-gate",
                        "Sons inventory action gate",
                        false,
                        sonsInventory != null && sonsInventory.ActionPlanningEnabled,
                        sonsInventory == null ? "MISSING" : sonsInventory.ActionPlanningEnabled ? "READY" : "CHECK",
                        sonsInventory == null
                            ? "Modstoke SDK inventory action gate state has not been published."
                            : "Planning "
                                + (sonsInventory.ActionPlanningEnabled ? "enabled" : "disabled")
                                + "; runtime mutation "
                                + (sonsInventory.RuntimeMutationEnabled ? "enabled" : "disabled")
                                + "; supported actions "
                                + sonsInventory.SupportedActionKinds.Count
                                + "; plans "
                                + sonsInventory.PlanRequestCount
                                + "."),
                    Requirement(
                        "sons-crafting-plan-gate",
                        "Sons crafting plan gate",
                        false,
                        sonsCrafting != null && sonsCrafting.PlanningEnabled,
                        sonsCrafting == null ? "MISSING" : sonsCrafting.PlanningEnabled ? "READY" : "CHECK",
                        sonsCrafting == null
                            ? "Modstoke SDK crafting plan gate state has not been published."
                            : "Planning "
                                + (sonsCrafting.PlanningEnabled ? "enabled" : "disabled")
                                + "; runtime execution "
                                + (sonsCrafting.RuntimeExecutionEnabled ? "enabled" : "disabled")
                                + "; supported plan kinds "
                                + sonsCrafting.SupportedPlanKinds.Count
                                + "; plans "
                                + sonsCrafting.PlanRequestCount
                                + "."),
                    Requirement(
                        "sons-building-plan-gate",
                        "Sons building plan gate",
                        false,
                        sonsBuilding != null && sonsBuilding.PlanningEnabled,
                        sonsBuilding == null ? "MISSING" : sonsBuilding.PlanningEnabled ? "READY" : "CHECK",
                        sonsBuilding == null
                            ? "Modstoke SDK building state has not been published."
                            : "Planning "
                                + (sonsBuilding.PlanningEnabled ? "enabled" : "disabled")
                                + "; runtime placement "
                                + (sonsBuilding.RuntimePlacementEnabled ? "enabled" : "disabled")
                                + "; blueprint files "
                                + sonsBuilding.BlueprintFileCount
                                + "; registered "
                                + sonsBuilding.RegisteredBlueprintCount
                                + "; plans "
                                + sonsBuilding.PlanRequestCount
                                + "."),
                    Requirement(
                        "sons-networking-plan-gate",
                        "Sons networking plan gate",
                        false,
                        sonsNetworking != null && sonsNetworking.PlanningEnabled,
                        sonsNetworking == null ? "MISSING" : sonsNetworking.PlanningEnabled ? "READY" : "CHECK",
                        sonsNetworking == null
                            ? "Modstoke SDK networking state has not been published."
                            : "Planning "
                                + (sonsNetworking.PlanningEnabled ? "enabled" : "disabled")
                                + "; runtime transport "
                                + (sonsNetworking.RuntimeTransportEnabled ? "enabled" : "disabled")
                                + "; session "
                                + sonsNetworking.SessionMode
                                + "; channels "
                                + sonsNetworking.RegisteredChannelCount
                                + "; plans "
                                + sonsNetworking.PlanRequestCount
                                + "."),
                    Requirement(
                        "sons-save-lifecycle",
                        "Sons save lifecycle",
                        false,
                        sonsSaveLifecycle != null && !string.Equals(sonsSaveLifecycle.PatchStatus, "failed", StringComparison.OrdinalIgnoreCase),
                        sonsSaveLifecycle == null ? "MISSING" : sonsSaveLifecycle.HarmonyPatchesInstalled ? "READY" : string.Equals(sonsSaveLifecycle.PatchStatus, "not-installed", StringComparison.OrdinalIgnoreCase) ? "PENDING" : "CHECK",
                        sonsSaveLifecycle == null ? "Modstoke SDK save lifecycle state has not been published." : sonsSaveLifecycle.PatchDetail),
                    Requirement(
                        "sons-save-registry",
                        "Sons save registry",
                        false,
                        sonsSaveRegistry != null && sonsSaveRegistry.ErrorCount == 0,
                        sonsSaveRegistry == null ? "MISSING" : sonsSaveRegistry.ErrorCount == 0 ? "READY" : "CHECK",
                        sonsSaveRegistry == null
                            ? "Modstoke SDK save registry state has not been published."
                            : sonsSaveRegistry.RegisteredCount
                                + " saveable(s), writes "
                                + sonsSaveRegistry.WriteCount
                                + ", reads "
                                + sonsSaveRegistry.ReadCount
                                + ", errors "
                                + sonsSaveRegistry.ErrorCount
                                + "."),
                    Requirement(
                        "sons-save-serializer-routing",
                        "Sons save serializer routing",
                        false,
                        sonsSaveRegistry != null
                            && sonsSaveRegistry.ErrorCount == 0
                            && (sonsSaveRegistry.EmbeddedRequiredCount == 0 || sonsSaveRegistry.EmbeddedStorageSupported),
                        sonsSaveRegistry == null ? "MISSING" : sonsSaveRegistry.ErrorCount > 0 ? "CHECK" : sonsSaveRegistry.EmbeddedRequiredCount > 0 && !sonsSaveRegistry.EmbeddedStorageSupported ? "CHECK" : "READY",
                        sonsSaveRegistry == null
                            ? "Modstoke SDK save registry state has not been published."
                            : sonsSaveRegistry.SidecarOnlyCount
                                + " sidecar, "
                                + sonsSaveRegistry.EmbeddedPreferredCount
                                + " embedded-preferred, "
                                + sonsSaveRegistry.EmbeddedRequiredCount
                                + " embedded-required; embedded storage status "
                                + sonsSaveRegistry.EmbeddedStorageStatus
                                + "."),
                    Requirement(
                        "private-payload-boundary",
                        "Private payload boundary",
                        true,
                        true,
                        "READY",
                        "Private admin or anti-cheat payloads are not packaged in the public loader."),
                    Requirement(
                        "redloader-hosting-gate",
                        "RedLoader hosting gate",
                        false,
                        true,
                        "INFO",
                        "Legacy RedLoader hosting remains explicitly gated; inventory and library discovery stay available without hosting mods.")
                };

                var requiredFailures = requirements.Count(requirement => requirement.Required && !requirement.Satisfied);
                var status = OpenACAILoaderCapabilities.StartupFailed
                    ? "FAILED"
                    : requiredFailures > 0 ? "CHECK" : OpenACAILoaderCapabilities.IsReady ? "READY" : "PENDING";
                return OpenACAIServerPolicySnapshot.Create(
                    Id,
                    DisplayName,
                    status,
                    "Public baseline policy for protected-server readiness and loader capability disclosure.",
                    requirements);
            }

            private static OpenACAIServerPolicyRequirementSnapshot Requirement(
                string id,
                string displayName,
                bool required,
                bool satisfied,
                string status,
                string detail)
            {
                return new OpenACAIServerPolicyRequirementSnapshot(id, displayName, required, satisfied, status, detail);
            }

            private static string FileIntegrityDetail(OpenACAIFileIntegritySnapshot snapshot)
            {
                if (snapshot == null)
                {
                    return "OpenACAI file integrity state has not been published.";
                }

                return snapshot.Detail
                    + " "
                    + snapshot.VerifiedFileCount
                    + "/"
                    + snapshot.TotalFileCount
                    + " files verified; critical issues "
                    + snapshot.CriticalIssueCount
                    + ".";
            }

            private static string RuntimeAttestationDetail(OpenACAIRuntimeAttestationSnapshot snapshot)
            {
                if (snapshot == null)
                {
                    return "OpenACAI runtime attestation state has not been published.";
                }

                return snapshot.Detail
                    + " Digest "
                    + ShortHash(snapshot.EvidenceDigestSha256)
                    + "; shims "
                    + snapshot.ReadyShimCount
                    + "/"
                    + snapshot.CompatibilityShimCount
                    + ", failed "
                    + snapshot.FailedShimCount
                    + "; dependencies "
                    + snapshot.ReadyDependencyCount
                    + "/"
                    + snapshot.DependencyCount
                    + ".";
            }

            private static string CompatibilityDependencyDetail(OpenACAICompatibilityDependencySnapshot snapshot)
            {
                if (snapshot == null)
                {
                    return "OpenACAI compatibility dependency state has not been published.";
                }

                var dependencyCount = snapshot.Dependencies.Count;
                var readyCount = snapshot.Dependencies.Count(dependency => dependency.Ready);
                var packagedCount = snapshot.Dependencies.Count(dependency => dependency.Packaged);
                var bindingConflictCount = snapshot.Dependencies.Count(dependency => dependency.BindingConflict);
                return readyCount
                    + "/"
                    + dependencyCount
                    + " dependencies ready; packaged "
                    + packagedCount
                    + "; warnings "
                    + snapshot.Warnings.Count
                    + "; binding conflicts "
                    + bindingConflictCount
                    + ".";
            }

            private static string FirstAssemblyReferenceName(OpenACAIBepInExPluginAssemblyReferenceSnapshot reference)
            {
                if (reference == null || reference.Resolved)
                {
                    return "none";
                }

                if (string.IsNullOrWhiteSpace(reference.AssemblyName))
                {
                    return "unknown";
                }

                return string.IsNullOrWhiteSpace(reference.RequiredVersion)
                    ? reference.AssemblyName
                    : reference.AssemblyName + " " + reference.RequiredVersion;
            }

            private static string FirstExecutionPlanIssueName(OpenACAIBepInExPluginExecutionPlanSnapshot plan)
            {
                if (plan == null || !plan.Issue)
                {
                    return "none";
                }

                var typeName = string.IsNullOrWhiteSpace(plan.PluginTypeName) ? "unknown" : plan.PluginTypeName;
                var issue = string.IsNullOrWhiteSpace(plan.IssueCode) ? "execution-plan-issue" : plan.IssueCode;
                return typeName + " " + issue;
            }

            private static string FirstLifecycleMethodIssueName(OpenACAIBepInExPluginLifecycleMethodSnapshot method)
            {
                if (method == null || !method.Issue)
                {
                    return "none";
                }

                var methodName = string.IsNullOrWhiteSpace(method.MethodName) ? "unknown" : method.MethodName;
                var issue = string.IsNullOrWhiteSpace(method.IssueCode) ? "lifecycle-method-issue" : method.IssueCode;
                return methodName + " " + issue;
            }

            private static string ShortHash(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return "unavailable";
                }

                return value.Length <= 12 ? value : value.Substring(0, 12);
            }
        }
    }
}
