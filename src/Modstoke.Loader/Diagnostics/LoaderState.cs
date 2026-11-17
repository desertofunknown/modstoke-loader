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
        // The inventory the published plugin compatibility and native mod host snapshots were last built from.
        private static ModInventoryState _modsInventory;
        private static long _environmentRevision;
        private static long _capturedEnvironmentRevision = -1;
        private static long _nextStateRefreshTick;
        private const long TitleSettleMs = 2000;
        private static long _titleSince = -1;
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
                    // Named parts for performance captures; the rest of a refresh is the snapshot below.
                    IL2CPPHookStateSnapshot il2CppNativeHooks;
                    using (Timings.MeasureCapture("LoaderState.Hooks"))
                        il2CppNativeHooks = IL2CPPHookState.Load(Paths.PluginPath, Paths.GameRootPath);
                    ModInventoryState modInventory;
                    using (Timings.MeasureCapture("LoaderState.Inventory"))
                        modInventory = CaptureModInventory(refreshDiscovery);
                    ItemCatalogSnapshot sonsItems;
                    using (Timings.MeasureCapture("LoaderState.Environment"))
                    {
                        RefreshEnvironment(refreshDiscovery);
                        // Scene callbacks already refresh the catalog. Routine state exports
                        // reuse it; startup and explicit refreshes still capture live metadata.
                        sonsItems = ItemCatalog.Catalog;
                        if (refreshItemCatalog || !sonsItems.Ready)
                            sonsItems = ItemCatalog.Refresh();
                    }
                    var sonsAssets = Assets.State;
                    ItemRegistrationSnapshot sonsItemRegistration;
                    InventorySnapshot sonsInventory;
                    CraftingSnapshot sonsCrafting;
                    BuildingSnapshot sonsBuilding;
                    NetworkingSnapshot sonsNetworking;
                    using (Timings.MeasureCapture("LoaderState.Sons"))
                    {
                        sonsItemRegistration = ItemRegistration.Refresh();
                        sonsInventory = Inventory.Refresh();
                        sonsCrafting = Crafting.Refresh();
                        sonsBuilding = Building.Refresh();
                        sonsNetworking = Networking.Refresh();
                    }
                    BepInExPluginCompatibilitySnapshot bepinExPluginCompatibility;
                    ModHostSnapshot nativeModHost;
                    FileIntegritySnapshot fileIntegrity;
                    RuntimeAttestationSnapshot runtimeAttestation;
                    using (Timings.MeasureCapture("LoaderState.Mods"))
                    {
                        // Startup and requested refreshes read every mod again; a routine export keeps the snapshots
                        // already published for the inventory it still has.
                        bool modsCurrent;
                        lock (RefreshSync)
                            modsCurrent = ReferenceEquals(modInventory, _modsInventory);
                        if (refreshFileIntegrity || refreshDiscovery || !modsCurrent)
                            (bepinExPluginCompatibility, nativeModHost) = RefreshMods(modInventory);
                        else
                        {
                            bepinExPluginCompatibility = BepInExPluginCompatibility.State;
                            nativeModHost = ModHost.State;
                        }
                        fileIntegrity = refreshFileIntegrity ? FileIntegrity.Refresh() : FileIntegrity.Snapshot;
                        runtimeAttestation = RuntimeAttestation.Refresh();
                    }
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
                RefreshMods(inventory);
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
                string reason = null;
                bool refreshFileIntegrity = false, publishOnly = false;
                lock (RefreshSync)
                {
                    if (_pendingRefreshReason == null || _stateRefreshInProgress
                        || (!force && Environment.TickCount64 < _nextStateRefreshTick))
                        return;
                    // A full export reads the game's and the loader's state on the game thread, and a multiplayer world
                    // keeps running behind the pause menu. So the file is pulled: a refresh command (the Mods menu, the
                    // console or the Manager's pipe) writes it at once, and every other change only marks it stale
                    // until the title screen, an empty dedicated server or quit.
                    if (!force && !_pendingFileIntegrityRefresh && !Idle())
                    {
                        // The open Mods menu lists the mod inventory, so a change to it still reaches the menu. Reading
                        // the mods again is the slowest part of an export; a load behind the loading screen, where a
                        // long frame only pauses the bar, does it ahead of the title's export.
                        if (_capturedInventoryRevision == _inventoryRevision
                            || !MenuController.IsVisible && !FrontEnd.ModsOpen && !Covered())
                            return;
                        publishOnly = true;
                    }
                    else
                    {
                        reason = _pendingRefreshReason;
                        refreshFileIntegrity = _pendingFileIntegrityRefresh;
                        _pendingRefreshReason = null;
                        _pendingFileIntegrityRefresh = false;
                    }
                    _stateRefreshInProgress = true;
                }

                try
                {
                    if (publishOnly)
                        RefreshMods(CaptureModInventory(false));
                    else
                    {
                        Timings.Mark("loader.state", "refresh-requested", reason);
                        Write(_lastNativeUnitySupportActive, _hostedRedLoaderMods ?? Array.Empty<ModBase>(), _lastStartupError,
                            startupWarnings, refreshFileIntegrity, refreshItemCatalog: refreshFileIntegrity,
                            refreshDiscovery: false, flush: false);
                    }
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

        private static bool Covered()
        {
            try
            {
                return global::Sons.Loading.LoadScene._isLoading || global::Modstoke.Sons.WorldReveal.Covered;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The title screen with no load under way, or a dedicated server nobody is connected to. At the title the export
        // waits out the title's first seconds, while its menus come up, and then for the menus to stand still, so its
        // long frame never lands in an animation.
        private static bool Idle()
        {
            try
            {
                if (LoaderEnvironment.IsDedicatedServer)
                    return global::Modstoke.Sons.ServerSessions.HooksInstalled
                        && global::Modstoke.Sons.ServerSessions.Sessions.Count == 0;
                if (!global::Modstoke.Sons.GameRuntime.IsTitleScene || global::Sons.Loading.LoadScene._isLoading)
                {
                    _titleSince = -1;
                    return false;
                }
                var now = Environment.TickCount64;
                if (_titleSince < 0) _titleSince = now;
                return now - _titleSince >= TitleSettleMs && Moving.Idle;
            }
            catch (Exception)
            {
                return false;
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

        // Plugin compatibility and the native mod host, which read every mod's manifest and assembly metadata.
        private static (BepInExPluginCompatibilitySnapshot, ModHostSnapshot) RefreshMods(ModInventoryState inventory)
        {
            var compatibility = BepInExPluginCompatibility.Refresh(inventory);
            var host = ModHost.Refresh(inventory);
            lock (RefreshSync)
                _modsInventory = inventory;
            return (compatibility, host);
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
