// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public Modstoke server/protected-runtime policy registry. Private admin or
// anti-cheat providers can plug in here without defining the public loader UI.

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Sons;

namespace Modstoke.Loader
{
    public static class ServerPolicy
    {
        public const string ServiceKey = "openacai.loader.serverPolicy";
        internal const string RuntimeBaselineProviderId = "openacai.serverPolicy.runtimeBaseline";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, IServerPolicyProvider> Providers =
            new Dictionary<string, IServerPolicyProvider>(StringComparer.OrdinalIgnoreCase);

        public static event Action<ServerPolicyProviderSnapshot> ProviderChanged;

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Providers.Clear();
            }

            PublishService();
        }

        public static bool TryRegisterProvider(IServerPolicyProvider provider)
        {
            var snapshot = ServerPolicyProviderSnapshot.FromProvider(provider);
            if (snapshot == null || IsLoaderProviderId(snapshot.Id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!Providers.TryAdd(snapshot.Id, provider))
                {
                    return false;
                }
            }

            PublishService();
            DispatchProviderChanged(snapshot);
            return true;
        }

        public static bool RegisterOrUpdateProvider(IServerPolicyProvider provider)
        {
            var snapshot = ServerPolicyProviderSnapshot.FromProvider(provider);
            return snapshot != null && !IsLoaderProviderId(snapshot.Id) && SetProvider(snapshot, provider);
        }

        internal static bool RegisterLoaderProvider(IServerPolicyProvider provider)
        {
            var snapshot = ServerPolicyProviderSnapshot.FromProvider(provider);
            return snapshot != null && SetProvider(snapshot, provider);
        }

        public static bool TryUnregisterProvider(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || IsLoaderProviderId(id.Trim()))
            {
                return false;
            }

            IServerPolicyProvider removed;
            lock (SyncRoot)
            {
                if (!Providers.Remove(id.Trim(), out removed))
                {
                    return false;
                }
            }

            PublishService();
            DispatchProviderChanged(ServerPolicyProviderSnapshot.FromProvider(removed));
            return true;
        }

        private static bool SetProvider(ServerPolicyProviderSnapshot snapshot, IServerPolicyProvider provider)
        {
            lock (SyncRoot)
            {
                Providers[snapshot.Id] = provider;
            }

            PublishService();
            DispatchProviderChanged(snapshot);
            return true;
        }

        // Mods may add their own policies but never replace or remove the loader baseline.
        private static bool IsLoaderProviderId(string id)
        {
            return string.Equals(id, RuntimeBaselineProviderId, StringComparison.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<ServerPolicyProviderSnapshot> ProvidersSnapshot()
        {
            IServerPolicyProvider[] providers;
            lock (SyncRoot)
            {
                providers = Providers.Values.ToArray();
            }

            return providers
                .Select(ServerPolicyProviderSnapshot.FromProvider)
                .Where(snapshot => snapshot != null)
                .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static IReadOnlyList<ServerPolicySnapshot> PoliciesSnapshot()
        {
            IServerPolicyProvider[] providers;
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

        public static bool TryGetPolicy(string providerId, out ServerPolicySnapshot policy)
        {
            policy = null;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            IServerPolicyProvider provider;
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
            Modding.SetService(ServiceKey, state);
            return state;
        }

        private static void PublishService()
        {
            Refresh();
        }

        private static ServerPolicySnapshot CreatePolicySafe(IServerPolicyProvider provider)
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
                global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                    "Modstoke server policy provider failed for '" + provider.Id + "': " + ex.Message);
                return ServerPolicySnapshot.Create(
                    provider.Id,
                    provider.DisplayName,
                    "FAILED",
                    "Policy provider failed: " + ex.Message,
                    Array.Empty<ServerPolicyRequirementSnapshot>());
            }
        }

        private static void DispatchProviderChanged(ServerPolicyProviderSnapshot snapshot)
        {
            Action<ServerPolicyProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                handlers = ProviderChanged;
            }

            if (snapshot != null)
                Modding.Dispatch("ServerPolicy.ProviderChanged", handlers, snapshot);
        }
    }

    public interface IServerPolicyProvider
    {
        string Id { get; }
        string DisplayName { get; }
        string Source { get; }
        string Description { get; }
        bool RequiresAuthorization { get; }
        bool Enabled { get; }
        ServerPolicySnapshot CreatePolicy();
    }

    public sealed class ServerPolicyProviderSnapshot
    {
        private ServerPolicyProviderSnapshot(
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

        internal static ServerPolicyProviderSnapshot FromProvider(IServerPolicyProvider provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
            {
                return null;
            }

            return new ServerPolicyProviderSnapshot(
                provider.Id.Trim(),
                string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Id.Trim() : provider.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(provider.Source) ? "Unknown" : provider.Source.Trim(),
                provider.Description ?? "",
                provider.RequiresAuthorization,
                provider.Enabled,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class ServerPolicySnapshot
    {
        private readonly IReadOnlyList<ServerPolicyRequirementSnapshot> _requirements;

        private ServerPolicySnapshot(
            string providerId,
            string title,
            string status,
            string detail,
            IEnumerable<ServerPolicyRequirementSnapshot> requirements,
            string updatedUtc)
        {
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? "provider" : providerId.Trim();
            Title = string.IsNullOrWhiteSpace(title) ? ProviderId : title.Trim();
            Status = string.IsNullOrWhiteSpace(status) ? "INFO" : status.Trim();
            Detail = detail ?? "";
            _requirements = (requirements ?? Array.Empty<ServerPolicyRequirementSnapshot>())
                .Where(requirement => requirement != null)
                .ToArray();
            RequirementCount = _requirements.Count;
            RequiredCount = _requirements.Count(requirement => requirement.Required);
            SatisfiedRequiredCount = _requirements.Count(requirement => requirement.Required && requirement.Satisfied);
            RequiredFailureCount = RequiredCount - SatisfiedRequiredCount;
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
        public IReadOnlyList<ServerPolicyRequirementSnapshot> Requirements => _requirements;

        public static ServerPolicySnapshot Create(
            string providerId,
            string title,
            string status,
            string detail,
            IEnumerable<ServerPolicyRequirementSnapshot> requirements)
        {
            return new ServerPolicySnapshot(
                providerId,
                title,
                status,
                detail,
                requirements,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class ServerPolicyRequirementSnapshot
    {
        public ServerPolicyRequirementSnapshot(
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

namespace Modstoke.Loader
{
    using Modstoke.Loader;
    using Modstoke.Sons;

    internal static class ProtectedServerPolicy
    {
        internal static void Initialize()
        {
            if (!ServerPolicy.RegisterLoaderProvider(new RuntimeBaselineProvider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke runtime baseline server policy provider could not be registered.");
            }
        }

        private sealed class RuntimeBaselineProvider : IServerPolicyProvider
        {
            public string Id => ServerPolicy.RuntimeBaselineProviderId;
            public string DisplayName => "Modstoke Runtime Baseline";
            public string Source => "Modstoke Loader";
            public string Description => "Disclosed public baseline signals for protected-server compatibility.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public ServerPolicySnapshot CreatePolicy()
            {
                var inventory = Modding.ModInventory;
                var catalog = Modding.ModInventoryCatalog;
                var nativeModHost = ModHost.State;
                var nativeModRuntime = ModRuntime.State;
                var fileIntegrity = FileIntegrity.Snapshot;
                var runtimeAttestation = RuntimeAttestation.Snapshot;
                var compatibilityDependencies = CompatibilityDependencies.Snapshot;
                var bepinExPluginCompatibility = BepInExPluginCompatibility.State;
                var sonsState = GameRuntime.State;
                var sonsEnvironment = GameRuntime.Environment;
                var sonsAssets = Assets.State;
                var sonsBuilding = Building.State;
                var sonsCrafting = Crafting.State;
                var sonsItemRegistration = ItemRegistration.State;
                var sonsItems = ItemCatalog.Catalog;
                var sonsInventory = Inventory.State;
                var sonsNetworking = Networking.State;
                var sonsSaveData = SaveData.State;
                var sonsSaveLifecycle = SaveLifecycle.State;
                var sonsSaveRegistry = SaveRegistry.State;
                var compatibilityShims = LoaderRuntime.CompatibilityShimsSnapshot();
                var menuProviders = MenuRegistry.Snapshot();
                var loaderReady = LoaderCapabilities.IsReady && !LoaderCapabilities.StartupFailed;
                var runtimeLibrariesProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, MenuProviderIds.RuntimeLibraries, StringComparison.OrdinalIgnoreCase));
                var bepinExPluginCompatibilityProviderReady = menuProviders.Any(provider => string.Equals(provider.Id, MenuProviderIds.BepInExPluginCompatibility, StringComparison.OrdinalIgnoreCase));
                ModInventoryCatalogSection libraryCatalogSection;
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
                    // The BepInEx host loads plugins, but only candidates that passed the gate.
                    && bepinExPluginCompatibility.PluginLoadedByOpenAcaiCount <= bepinExPluginCompatibility.LoadEligibleCount
                    && bepinExPluginCompatibilityLibraryBoundaryReady
                    && bepinExPluginCompatibilityNativeBoundaryReady
                    && bepinExPluginCompatibilityRedLoaderBoundaryReady;
                var publicServicesReady = Modding.Runtime != null && inventory != null && catalog != null && bepinExPluginCompatibility != null && nativeModHost != null && fileIntegrity != null && runtimeAttestation != null && compatibilityDependencies != null && sonsState != null && sonsEnvironment != null && sonsAssets != null && sonsBuilding != null && sonsCrafting != null && sonsItemRegistration != null && sonsItems != null && sonsInventory != null && sonsNetworking != null && sonsSaveData != null && sonsSaveLifecycle != null && sonsSaveRegistry != null;
                var librarySeparationReady = catalog != null && inventory != null && catalog.ModCount == inventory.Mods.Count && inventory.Mods.All(entry => !entry.IsLibrary);
                var harmonySupport = HarmonySupport.CreateState();
                var harmonyPatchesReady = harmonySupport != null && harmonySupport.SafeForHarmonyPatches;
                var requirements = new[]
                {
                    Requirement(
                        "loader-ready",
                        "Loader ready",
                        true,
                        loaderReady,
                        loaderReady ? "READY" : LoaderCapabilities.StartupFailed ? "FAILED" : "PENDING",
                        string.IsNullOrWhiteSpace(LoaderCapabilities.StartupError) ? "Modstoke runtime startup state is published." : LoaderCapabilities.StartupError),
                    Requirement(
                        "public-sdk-services",
                        "Public SDK services",
                        true,
                        publicServicesReady,
                        publicServicesReady ? "READY" : "CHECK",
                        "Runtime, inventory, catalog, BepInEx plugin compatibility, native host, native runtime, file integrity, compatibility dependencies, runtime attestation, menu, config, and Sons service/save/item/building/networking snapshots are public Modstoke surfaces."),
                    Requirement(
                        "compatibility-shim-state",
                        "Compatibility shim state",
                        true,
                        compatibilityShims.Count > 0,
                        compatibilityShims.Count > 0 ? "READY" : "CHECK",
                        "BepInEx, RedLoader, IL2CPP, SonsSdk, and patch-host compatibility states are disclosed separately."),
                    // Without safe Harmony patches every Sons server-authority hook stays off.
                    Requirement(
                        "harmony-patch-support",
                        "Harmony patch support",
                        true,
                        harmonyPatchesReady,
                        harmonySupport == null ? "PENDING" : harmonyPatchesReady ? "READY" : "CHECK",
                        harmonySupport == null ? "Harmony patch support has not been probed yet." : harmonySupport.Detail),
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
                        librarySeparationReady,
                        librarySeparationReady ? "READY" : "CHECK",
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
                        "Modstoke BepInEx plugin compatibility gate",
                        true,
                        bepinExPluginCompatibilityReady,
                        bepinExPluginCompatibility == null ? "MISSING" : bepinExPluginCompatibilityReady ? "READY" : "CHECK",
                        bepinExPluginCompatibility == null
                            ? "Modstoke BepInEx plugin compatibility gate state has not been published."
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
                                + ", Modstoke-loaded plugins "
                                + bepinExPluginCompatibility.PluginLoadedByOpenAcaiCount
                                + ", libraries excluded "
                                + bepinExPluginCompatibility.LibraryExcludedCount
                                + ", Modstoke-native excluded "
                                + bepinExPluginCompatibility.OpenAcaiNativeExcludedCount
                                + ", RedLoader excluded "
                                + bepinExPluginCompatibility.RedLoaderCompatibilityExcludedCount
                                + "; runtime plugin loading "
                                + (bepinExPluginCompatibility.RuntimePluginLoadingEnabled ? "enabled" : "disabled")
                                + ". Ready needs no compatibility or parser issues, intact boundaries and no"
                                + " Modstoke-loaded plugin beyond the load-eligible candidates."),
                    Requirement(
                        "openacai-native-runtime",
                        "Modstoke native runtime",
                        true,
                        nativeModRuntime.LoadAttempted && nativeModRuntime.FailedCount == 0 && nativeModRuntime.DeferredCount == 0,
                        !nativeModRuntime.LoadAttempted ? "MISSING" : nativeModRuntime.FailedCount == 0 && nativeModRuntime.DeferredCount == 0 ? "READY" : "CHECK",
                        "Startup results: " + nativeModRuntime.LoadedCount + " loaded, " + nativeModRuntime.FailedCount + " failed, " + nativeModRuntime.DeferredCount + " deferred; " + nativeModRuntime.HookCount + " lifecycle hook(s) bound."),
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
                        sonsEnvironment != null && !string.IsNullOrWhiteSpace(sonsEnvironment.ModstokeRootDirectory) && !string.IsNullOrWhiteSpace(sonsEnvironment.GameExecutablePath),
                        sonsEnvironment == null ? "MISSING" : string.IsNullOrWhiteSpace(sonsEnvironment.ModstokeRootDirectory) ? "CHECK" : "READY",
                        sonsEnvironment == null
                            ? "Modstoke SDK environment state has not been published."
                            : "Game executable, loader root, runtime, mods, UserData, and legacy compatibility paths are published."),
                    Requirement(
                        "sons-save-data",
                        "Sons save data",
                        false,
                        sonsSaveData != null && sonsSaveData.ModstokeSaveDataDirectoryExists && sonsSaveData.ModstokeModSaveDataDirectoryExists,
                        sonsSaveData == null ? "MISSING" : sonsSaveData.ModstokeSaveDataDirectoryExists && sonsSaveData.ModstokeModSaveDataDirectoryExists ? "READY" : "CHECK",
                        sonsSaveData == null
                            ? "Modstoke SDK save-data state has not been published."
                            : sonsSaveData.IsolationModel
                                + "; Modstoke saves root exists "
                                + sonsSaveData.ModstokeSaveDataDirectoryExists
                                + ", mod saves root exists "
                                + sonsSaveData.ModstokeModSaveDataDirectoryExists
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

                var hasRequiredFailures = requirements.Any(requirement => requirement.Required && !requirement.Satisfied);
                var status = LoaderCapabilities.StartupFailed
                    ? "FAILED"
                    : hasRequiredFailures ? "CHECK" : LoaderCapabilities.IsReady ? "READY" : "PENDING";
                return ServerPolicySnapshot.Create(
                    Id,
                    DisplayName,
                    status,
                    "Public baseline policy for protected-server readiness and loader capability disclosure.",
                    requirements);
            }

            private static ServerPolicyRequirementSnapshot Requirement(
                string id,
                string displayName,
                bool required,
                bool satisfied,
                string status,
                string detail)
            {
                return new ServerPolicyRequirementSnapshot(id, displayName, required, satisfied, status, detail);
            }

            private static string FileIntegrityDetail(FileIntegritySnapshot snapshot)
            {
                if (snapshot == null)
                {
                    return "Modstoke file integrity state has not been published.";
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

            private static string RuntimeAttestationDetail(RuntimeAttestationSnapshot snapshot)
            {
                if (snapshot == null)
                {
                    return "Modstoke runtime attestation state has not been published.";
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

            private static string CompatibilityDependencyDetail(CompatibilityDependencySnapshot snapshot)
            {
                if (snapshot == null)
                {
                    return "Modstoke compatibility dependency state has not been published.";
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

            private static string FirstAssemblyReferenceName(BepInExPluginAssemblyReferenceSnapshot reference)
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

            private static string FirstExecutionPlanIssueName(BepInExPluginExecutionPlanSnapshot plan)
            {
                if (plan == null || !plan.Issue)
                {
                    return "none";
                }

                var typeName = string.IsNullOrWhiteSpace(plan.PluginTypeName) ? "unknown" : plan.PluginTypeName;
                var issue = string.IsNullOrWhiteSpace(plan.IssueCode) ? "execution-plan-issue" : plan.IssueCode;
                return typeName + " " + issue;
            }

            private static string FirstLifecycleMethodIssueName(BepInExPluginLifecycleMethodSnapshot method)
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
