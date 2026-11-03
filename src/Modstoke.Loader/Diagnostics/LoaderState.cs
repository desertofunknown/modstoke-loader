// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned loader state and inventory refresh service.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Modstoke.Loader;
using Modstoke.Sons;
using RedLoader;
using RedLoader.Utils;
using SonsSdk;

namespace Modstoke.Loader
{
    internal static class LoaderState
    {
        private static object _redLoaderHostState;
        private static object _bepInExPluginHostState;
        private static IReadOnlyCollection<ModBase> _hostedRedLoaderMods = Array.Empty<ModBase>();
        internal static IReadOnlyCollection<ModBase> HostedRedLoaderMods => _hostedRedLoaderMods;
        private static readonly object RefreshSync = new object();
        private static ModInventoryState _modInventory;
        internal static ModInventoryState CurrentInventory
        {
            get { lock (RefreshSync) return _modInventory; }
        }
        private static long _inventoryRevision;
        private static long _capturedInventoryRevision = -1;
        private static long _environmentRevision;
        private static long _capturedEnvironmentRevision = -1;
        private static long _nextStateRefreshTick;
        private static string _pendingRefreshReason;
        private static bool _pendingFileIntegrityRefresh;
        private static bool _lastNativeUnitySupportActive;
        private static bool _stateRefreshInProgress;
        private static string _lastStartupError;

        internal static void SetRedLoaderHostState(object redLoaderHostState)
        {
            lock (RefreshSync)
            {
                _redLoaderHostState = redLoaderHostState;
                _inventoryRevision++;
            }
        }

        internal static void SetBepInExPluginHostState(object bepinExPluginHostState)
        {
            lock (RefreshSync)
            {
                _bepInExPluginHostState = bepinExPluginHostState;
                _inventoryRevision++;
            }
        }

        internal static void Write(bool nativeUnitySupportActive, RedLoaderHostResult hostResult, string startupError, IReadOnlyCollection<string> startupWarnings)
        {
            Write(nativeUnitySupportActive, hostResult?.HostedMods ?? Array.Empty<ModBase>(), startupError, startupWarnings);
        }

        internal static void WriteFailure(string startupError, IReadOnlyCollection<string> startupWarnings)
        {
            Write(false, Array.Empty<ModBase>(), startupError, startupWarnings);
        }

        internal static void Write(bool nativeUnitySupportActive, IReadOnlyCollection<ModBase> hostedMods, string startupError, IReadOnlyCollection<string> startupWarnings, bool refreshFileIntegrity = true, bool refreshItemCatalog = true, bool refreshDiscovery = true, bool flush = true)
        {
            _lastNativeUnitySupportActive = nativeUnitySupportActive;
            _lastStartupError = startupError;

            using (Timings.Measure("loader.state", "capture", startupError == null ? "ready" : "failed"))
            {
                try
                {
                    var effectiveHostedMods = hostedMods ?? Array.Empty<ModBase>();
                    _hostedRedLoaderMods = effectiveHostedMods;
                    var il2CppNativeHooks = IL2CPPHookState.Load(Paths.PluginPath, Paths.GameRootPath);
                    var modInventory = CaptureModInventory(refreshDiscovery);
                    RefreshEnvironment(refreshDiscovery);
                    // Scene callbacks already refresh the catalog. Routine state exports
                    // reuse it; startup and explicit refreshes still capture live metadata.
                    var sonsItems = ItemCatalog.Catalog;
                    if (refreshItemCatalog || !sonsItems.Ready)
                        sonsItems = ItemCatalog.Refresh();
                    var sonsAssets = Assets.State;
                    var sonsItemRegistration = ItemRegistration.Refresh();
                    var sonsInventory = Inventory.Refresh();
                    var sonsCrafting = Crafting.Refresh();
                    var sonsBuilding = Building.Refresh();
                    var sonsNetworking = Networking.Refresh();
                    var bepinExPluginCompatibility = BepInExPluginCompatibility.Refresh(modInventory);
                    var nativeModHost = ModHost.Refresh(modInventory);
                    var fileIntegrity = refreshFileIntegrity ? FileIntegrity.Refresh() : FileIntegrity.Snapshot;
                    var runtimeAttestation = RuntimeAttestation.Refresh();
                    var serverPolicy = ServerPolicy.Refresh();
                    var loadedModsStatus = LoadedMods.TryCapture(out var loadedMods);
                    var sonsSdkUiReady = !LoaderEnvironment.IsDedicatedServer && startupError == null
                        && LoaderRuntime.CompatibilityShims.TryGetValue(CompatibilityShimIds.SonsSdk, out var sonsSdkShim)
                        && sonsSdkShim.Enabled && sonsSdkShim.Ready && !sonsSdkShim.Failed;

                    var state = new
                    {
                        schema = 1,
                        name = "Modstoke Loader",
                        version = RuntimeEntryPoint.Version,
                        mode = "openacai-endnight-runtime",
                        generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                        loadedMods = new { status = loadedModsStatus.ToString(), snapshot = loadedMods },
                        gameRoot = Paths.GameRootPath,
                        gameExecutable = Paths.ExecutablePath,
                        openAcaiRoot = LoaderPaths.RootDirectory,
                        openAcaiRuntimeDirectory = LoaderPaths.RuntimeDirectory,
                        openAcaiRuntimeCompatibilityDirectory = LoaderPaths.RuntimeCompatibilityDirectory,
                        openAcaiModsDirectory = LoaderPaths.ModsDirectory,
                        openAcaiUserDataDirectory = LoaderPaths.UserDataDirectory,
                        bootstrapCompatibilityMirror = LoaderPaths.BootstrapMirrorDirectory,
                        stateFiles = new
                        {
                            primary = LoaderPaths.LoaderStatePath,
                            chainloader = LoaderPaths.ChainloaderStatePath,
                            il2CppPreloader = LoaderPaths.Il2CppPreloaderStatePath,
                            il2CppChainloader = LoaderPaths.Il2CppChainloaderStatePath,
                            legacyBootstrapMirror = LoaderPaths.LegacyBootstrapStatePath
                        },
                        sonsGameVersion = GameRuntime.Environment.SonsGameVersion,
                        bepinexRoot = Paths.BepInExRootPath,
                        modsDirectory = SafeRead(() => LoaderEnvironment.ModsDirectory),
                        redLoaderDataDirectory = SafeRead(() => LoaderEnvironment.LoaderDirectory),
                        resolverSearchDirectories = AssemblyResolver.SearchDirectoriesSnapshot(),
                        resolver = AssemblyResolver.CreateState(),
                        compatibilityDependencies = CompatibilityDependencies.CreateState(),
                        warnings = (startupWarnings ?? Array.Empty<string>()).ToArray(),
                        supports = new
                        {
                            bepinexIl2CppPlugins = true,
                            redLoaderModInventory = true,
                            redLoaderModHosting = startupError == null && effectiveHostedMods.Count > 0,
                            redLoaderSonsSdkUi = sonsSdkUiReady,
                            antiCheatCompanionSurface = true,
                            redLoaderDoorstopBootstrap = false
                        },
                        capabilities = LoaderCapabilities.CreateState(),
                        openAcaiRuntime = LoaderRuntime.CreateState(),
                        il2CppNativeHooks,
                        harmony = HarmonySupport.CreateState(),
                        unityLogging = UnityLogSource.CreateState(),
                        openAcaiCompatibilityDependencies = CompatibilityDependencies.CreateState(),
                        openAcaiInput = InputBindings.CreateState(),
                        openAcaiConfig = LoaderConfig.CreateState(),
                        openAcaiUi = UiState.CreateState(),
                        openAcaiChainloader = ChainloaderStatus.CreateState(),
                        openAcaiIl2CppPreloader = ChainloaderStatus.CreateIl2CppPreloaderState(),
                        openAcaiIl2CppChainloaderAdapter = ChainloaderStatus.CreateIl2CppChainloaderState(),
                        openAcaiMenu = MenuRegistry.CreateState(),
                        openAcaiBepInExPluginCompatibility = bepinExPluginCompatibility,
                        openAcaiBepInExPluginHost = _bepInExPluginHostState,
                        openAcaiBepInExPluginActivationProbe = BepInExPluginActivationProbe.CreateState(),
                        openAcaiNativeModHost = nativeModHost,
                        openAcaiNativeModRuntime = ModRuntime.State,
                        openAcaiFileIntegrity = fileIntegrity,
                        openAcaiRuntimeAttestation = runtimeAttestation,
                        openAcaiServerPolicy = serverPolicy,
                        openAcaiTimings = Timings.CreateState(),
                        openAcaiSons = GameRuntime.State,
                        openAcaiSonsEnvironment = GameRuntime.Environment,
                        openAcaiSonsAssets = sonsAssets,
                        openAcaiSonsAudioBanks = AudioBanks.State,
                        openAcaiSonsBuilding = sonsBuilding,
                        openAcaiSonsCrafting = sonsCrafting,
                        openAcaiSonsItemRegistration = sonsItemRegistration,
                        openAcaiSonsItems = sonsItems,
                        openAcaiSonsInventory = sonsInventory,
                        openAcaiSonsNetworking = sonsNetworking,
                        openAcaiSonsSaveData = SaveData.State,
                        openAcaiSonsSaveLifecycle = SaveLifecycle.State,
                        openAcaiSonsSaveRegistry = SaveRegistry.State,
                        openAcaiSonsMenuBridge = MenuBridgeState.CreateState(),
                        modInventory,
                        redLoaderCompatibility = new
                        {
                            pluginGuid = RuntimeEntryPoint.PluginGuid,
                            pluginName = RuntimeEntryPoint.PluginName,
                            pluginVersion = RuntimeEntryPoint.Version,
                            nativeUnitySupportActive,
                            hostedModCount = effectiveHostedMods.Count,
                            hostedMods = effectiveHostedMods.Select(CreateHostedModState).ToArray(),
                            runtimeIssues = LegacyRedLoaderCompatibility.CreateRuntimeIssueState(),
                            openAcaiHost = _redLoaderHostState
                        },
                        openAcaiModInventoryCatalog = Modding.ModInventoryCatalog,
                        startupError
                    };

                    LoaderStatePersistence.Queue(state, LoaderPaths.LoaderStatePath, LoaderPaths.LegacyBootstrapStatePath);
                }
                catch (Exception ex)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Modstoke Loader state capture failed: " + ex.Message);
                }
                finally
                {
                    if (flush)
                    {
                        LoaderStatePersistence.Flush();
                        ReportWriteCompletion();
                    }
                }
            }
        }

        internal static ModInventoryState RefreshModInventory()
        {
            using (Timings.Measure("mods.inventory", "refresh"))
            {
                var inventory = CaptureModInventory(force: true);
                RequestRefresh("mod-inventory-refreshed");
                BepInExPluginCompatibility.Refresh(inventory);
                ModHost.Refresh(inventory);
                return inventory;
            }
        }

        internal static void RequestRefresh(string reason, bool refreshFileIntegrity = false)
        {
            // Logging callbacks can arrive off-thread; Unity state is collected by the driver.
            lock (RefreshSync)
            {
                _pendingRefreshReason = reason ?? "unspecified";
                // An ordinary update must not erase an explicitly requested verification.
                _pendingFileIntegrityRefresh |= refreshFileIntegrity;

                // refreshInventory has already scanned synchronously for its command result.
                // refreshState still requests a fresh inventory, environment and integrity check.
                if (refreshFileIntegrity && reason != "runtime-command-refresh-inventory")
                {
                    _inventoryRevision++;
                    _environmentRevision++;
                }
                if (reason == "native-mod-runtime" || reason == "bepinex-dynamic-load"
                    || reason == "config-provider-value-applied" || reason == "sonssdk-initialization-completed")
                    _inventoryRevision++;
                if (reason == "mod-inventory-refreshed" || reason == "sonssdk-initialization-completed"
                    || reason == "audio-banks-changed"
                    || reason == "sons-lifecycle-DriverStarted"
                    || reason?.StartsWith("sons-scene-", StringComparison.Ordinal) == true
                    || reason?.StartsWith("sons-world-", StringComparison.Ordinal) == true)
                    _environmentRevision++;
            }
        }

        internal static void PumpRefresh(IReadOnlyCollection<string> startupWarnings, bool force = false)
        {
            try
            {
                ReportWriteCompletion();
                string reason;
                bool refreshFileIntegrity;
                lock (RefreshSync)
                {
                    if (_pendingRefreshReason == null || _stateRefreshInProgress
                        || (!force && Environment.TickCount64 < _nextStateRefreshTick))
                        return;

                    reason = _pendingRefreshReason;
                    refreshFileIntegrity = _pendingFileIntegrityRefresh;
                    _pendingRefreshReason = null;
                    _pendingFileIntegrityRefresh = false;
                    _stateRefreshInProgress = true;
                }

                try
                {
                    Timings.Mark("loader.state", "refresh-requested", reason);
                    Write(_lastNativeUnitySupportActive, _hostedRedLoaderMods ?? Array.Empty<ModBase>(), _lastStartupError,
                        startupWarnings, refreshFileIntegrity, refreshItemCatalog: refreshFileIntegrity,
                        refreshDiscovery: false, flush: false);
                }
                finally
                {
                    lock (RefreshSync)
                    {
                        _nextStateRefreshTick = Environment.TickCount64 + 2000;
                        _stateRefreshInProgress = false;
                    }
                }
            }
            finally
            {
                // Quit also drains an already queued write when there is no pending refresh.
                if (force)
                {
                    LoaderStatePersistence.Flush();
                    ReportWriteCompletion();
                }
            }
        }

        private static ModInventoryState CaptureModInventory(bool force)
        {
            long revision;
            lock (RefreshSync)
            {
                revision = _inventoryRevision;
                if (!force && _modInventory != null && _capturedInventoryRevision == revision)
                    return _modInventory;
            }

            var inventory = ModInventoryScanner.Create(Paths.GameRootPath, Paths.PluginPath, _hostedRedLoaderMods);
            MenuController.SetInventory(inventory);
            Modding.PublishModInventory(inventory);
            lock (RefreshSync)
            {
                _modInventory = inventory;
                // An invalidation raised during discovery must survive this publication.
                _capturedInventoryRevision = revision;
            }
            return inventory;
        }

        private static void RefreshEnvironment(bool force)
        {
            long revision;
            lock (RefreshSync)
            {
                revision = _environmentRevision;
                if (!force && _capturedEnvironmentRevision == revision)
                    return;
            }
            GameRuntime.RefreshEnvironment();
            lock (RefreshSync)
                _capturedEnvironmentRevision = revision;
        }

        private static void ReportWriteCompletion()
        {
            var completion = LoaderStatePersistence.TakeCompletion();
            if (completion.Failed > 0)
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke Loader state write failed ("
                    + completion.Failed + " attempt(s)): " + completion.Error);
            if (completion.Completed > 0)
                RuntimeEntryPoint.LogSource?.LogDebug("Modstoke Loader state written to both mirrors.");
            if (completion.Completed > 0 || completion.Failed > 0)
                Timings.RecordDuration("loader.state", "persist-batch", completion.Duration,
                    completion.Completed + " written; " + completion.Failed + " failed");
        }

        private static object CreateHostedModState(ModBase mod)
        {
            var sonsMod = mod as SonsMod;
            var manifest = sonsMod?.Manifest;
            return new
            {
                id = manifest?.Id ?? mod.ID,
                name = manifest?.Name,
                version = manifest?.Version,
                author = manifest?.Author,
                assembly = mod.ModAssembly != null ? Path.GetFileName(mod.ModAssembly.Location) : null,
                path = mod.ModAssembly?.Location
            };
        }

        private static string SafeRead(Func<string> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }
}
