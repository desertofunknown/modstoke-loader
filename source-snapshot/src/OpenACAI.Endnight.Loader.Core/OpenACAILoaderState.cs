// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned loader state and inventory refresh service.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using OpenACAI.Loader;
using OpenACAI.Sons;
using RedLoader;
using RedLoader.Utils;
using SonsSdk;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAILoaderState
    {
        private static object _redLoaderHostState;
        private static object _bepInExPluginHostState;
        private static IReadOnlyCollection<ModBase> _hostedRedLoaderMods = Array.Empty<ModBase>();
        private static readonly object RefreshSync = new object();
        private static long _nextStateRefreshTick;
        private static string _pendingRefreshReason;
        private static bool _lastNativeUnitySupportActive;
        private static bool _stateRefreshInProgress;
        private static string _lastStartupError;

        internal static void SetRedLoaderHostState(object redLoaderHostState)
        {
            _redLoaderHostState = redLoaderHostState;
        }

        internal static void SetBepInExPluginHostState(object bepinExPluginHostState)
        {
            _bepInExPluginHostState = bepinExPluginHostState;
        }

        internal static void Write(bool nativeUnitySupportActive, RedLoaderHostResult hostResult, string startupError, IReadOnlyCollection<string> startupWarnings)
        {
            Write(nativeUnitySupportActive, hostResult?.HostedMods ?? Array.Empty<ModBase>(), startupError, startupWarnings);
        }

        internal static void WriteFailure(string startupError, IReadOnlyCollection<string> startupWarnings)
        {
            Write(false, Array.Empty<ModBase>(), startupError, startupWarnings);
        }

        internal static void Write(bool nativeUnitySupportActive, IReadOnlyCollection<ModBase> hostedMods, string startupError, IReadOnlyCollection<string> startupWarnings)
        {
            _lastNativeUnitySupportActive = nativeUnitySupportActive;
            _lastStartupError = startupError;

            using (OpenACAITimings.Measure("loader.state", "write", startupError == null ? "ready" : "failed"))
            {
                try
                {
                    var effectiveHostedMods = hostedMods ?? Array.Empty<ModBase>();
                    _hostedRedLoaderMods = effectiveHostedMods;
                    Directory.CreateDirectory(OpenACAIPaths.RuntimeDirectory);
                    Directory.CreateDirectory(OpenACAIPaths.BootstrapMirrorDirectory);

                    var il2CppNativeHooks = OpenACAIIL2CPPHookState.Load(Paths.PluginPath, Paths.GameRootPath);
                    var modInventory = ModInventoryScanner.Create(Paths.GameRootPath, Paths.PluginPath, effectiveHostedMods);
                    OpenACAISons.RefreshEnvironment();
                    var sonsItems = OpenACAISonsItems.Refresh();
                    var sonsAssets = OpenACAISonsAssets.Refresh();
                    var sonsItemRegistration = OpenACAISonsItemRegistration.Refresh();
                    var sonsInventory = OpenACAISonsInventory.Refresh();
                    var sonsCrafting = OpenACAISonsCrafting.Refresh();
                    var sonsBuilding = OpenACAISonsBuilding.Refresh();
                    var sonsNetworking = OpenACAISonsNetworking.Refresh();
                    OpenAcaiModMenu.SetInventory(modInventory);
                    OpenACAIModding.PublishModInventory(modInventory);
                    var bepinExPluginCompatibility = OpenACAIBepInExPluginCompatibility.Refresh(modInventory);
                    var nativeModHost = OpenACAINativeModHost.Refresh(modInventory);
                    var nativeLifecycleRegistry = OpenACAINativeLifecycleRegistry.Refresh(nativeModHost);
                    var nativeLifecycleDispatcher = OpenACAINativeLifecycleDispatcher.Refresh(nativeLifecycleRegistry);
                    var nativeLifecycleInvocationBridge = OpenACAINativeLifecycleInvocationBridge.Refresh(nativeLifecycleDispatcher);
                    var nativeLifecycleInvocationAdapter = OpenACAINativeLifecycleInvocationAdapter.Refresh(nativeLifecycleInvocationBridge);
                    var nativeLifecycleInvocationTokenResolver = OpenACAINativeLifecycleInvocationTokenResolver.Refresh(nativeLifecycleInvocationAdapter);
                    var nativeLifecycleInvocationJournal = OpenACAINativeLifecycleInvocationJournal.Refresh(nativeLifecycleInvocationTokenResolver);
                    var nativeLifecycleInvocationScheduler = OpenACAINativeLifecycleInvocationScheduler.Refresh(nativeLifecycleInvocationJournal);
                    var nativeLifecycleInvocationExecutor = OpenACAINativeLifecycleInvocationExecutor.Refresh(nativeLifecycleInvocationScheduler);
                    var fileIntegrity = OpenACAIFileIntegrity.Refresh();
                    var runtimeAttestation = OpenACAIRuntimeAttestation.Refresh();
                    var serverPolicy = OpenACAIServerPolicy.Refresh();

                    var state = new
                    {
                        schema = 1,
                        name = "Modstoke Loader",
                        version = OpenACAIEndnightRuntime.Version,
                        mode = "openacai-endnight-runtime",
                        generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                        gameRoot = Paths.GameRootPath,
                        gameExecutable = Paths.ExecutablePath,
                        openAcaiRoot = OpenACAIPaths.RootDirectory,
                        openAcaiRuntimeDirectory = OpenACAIPaths.RuntimeDirectory,
                        openAcaiRuntimeCompatibilityDirectory = OpenACAIPaths.RuntimeCompatibilityDirectory,
                        openAcaiModsDirectory = OpenACAIPaths.ModsDirectory,
                        openAcaiUserDataDirectory = OpenACAIPaths.UserDataDirectory,
                        bootstrapCompatibilityMirror = OpenACAIPaths.BootstrapMirrorDirectory,
                        stateFiles = new
                        {
                            primary = OpenACAIPaths.LoaderStatePath,
                            chainloader = OpenACAIPaths.ChainloaderStatePath,
                            il2CppPreloader = OpenACAIPaths.Il2CppPreloaderStatePath,
                            il2CppChainloader = OpenACAIPaths.Il2CppChainloaderStatePath,
                            legacyBootstrapMirror = OpenACAIPaths.LegacyBootstrapStatePath
                        },
                        sonsGameVersion = SafeRead(GetSonsGameVersion),
                        bepinexRoot = Paths.BepInExRootPath,
                        modsDirectory = SafeRead(() => LoaderEnvironment.ModsDirectory),
                        redLoaderDataDirectory = SafeRead(() => LoaderEnvironment.LoaderDirectory),
                        resolverSearchDirectories = OpenACAIAssemblyResolver.SearchDirectoriesSnapshot(),
                        resolver = OpenACAIAssemblyResolver.CreateState(),
                        compatibilityDependencies = OpenACAICompatibilityDependencies.CreateState(),
                        warnings = (startupWarnings ?? Array.Empty<string>()).ToArray(),
                        supports = new
                        {
                            bepinexIl2CppPlugins = true,
                            redLoaderModInventory = true,
                            redLoaderModHosting = startupError == null && effectiveHostedMods.Count > 0,
                            redLoaderSonsSdkUi = startupError == null && effectiveHostedMods.Count > 0,
                            antiCheatCompanionSurface = true,
                            redLoaderDoorstopBootstrap = false
                        },
                        capabilities = OpenACAILoaderCapabilities.CreateState(),
                        openAcaiRuntime = OpenACAIRuntime.CreateState(),
                        il2CppNativeHooks,
                        harmony = OpenACAIHarmonySupport.CreateState(),
                        unityLogging = OpenACAIUnityLogSource.CreateState(),
                        openAcaiCompatibilityDependencies = OpenACAICompatibilityDependencies.CreateState(),
                        openAcaiInput = OpenACAIInput.CreateState(),
                        openAcaiConfig = OpenACAIConfig.CreateState(),
                        openAcaiUi = OpenACAIUi.CreateState(),
                        openAcaiChainloader = OpenACAIChainloaderStatus.CreateState(),
                        openAcaiIl2CppPreloader = OpenACAIChainloaderStatus.CreateIl2CppPreloaderState(),
                        openAcaiIl2CppChainloaderAdapter = OpenACAIChainloaderStatus.CreateIl2CppChainloaderState(),
                        openAcaiMenu = OpenACAIMenu.CreateState(),
                        openAcaiBepInExPluginCompatibility = bepinExPluginCompatibility,
                        openAcaiBepInExPluginHost = _bepInExPluginHostState,
                        openAcaiBepInExPluginActivationProbe = OpenACAIBepInExPluginActivationProbe.CreateState(),
                        openAcaiNativeModHost = nativeModHost,
                        openAcaiNativeModRuntime = OpenACAINativeModRuntime.State,
                        openAcaiNativeLifecycleRegistry = nativeLifecycleRegistry,
                        openAcaiNativeLifecycleDispatcher = nativeLifecycleDispatcher,
                        openAcaiNativeLifecycleInvocationBridge = nativeLifecycleInvocationBridge,
                        openAcaiNativeLifecycleInvocationAdapter = nativeLifecycleInvocationAdapter,
                        openAcaiNativeLifecycleInvocationTokenResolver = nativeLifecycleInvocationTokenResolver,
                        openAcaiNativeLifecycleInvocationJournal = nativeLifecycleInvocationJournal,
                        openAcaiNativeLifecycleInvocationScheduler = nativeLifecycleInvocationScheduler,
                        openAcaiNativeLifecycleInvocationExecutor = nativeLifecycleInvocationExecutor,
                        openAcaiFileIntegrity = fileIntegrity,
                        openAcaiRuntimeAttestation = runtimeAttestation,
                        openAcaiServerPolicy = serverPolicy,
                        openAcaiTimings = OpenACAITimings.CreateState(),
                        openAcaiSons = OpenACAISons.State,
                        openAcaiSonsEnvironment = OpenACAISons.Environment,
                        openAcaiSonsAssets = sonsAssets,
                        openAcaiSonsAudioBanks = OpenACAISonsAudioBanks.State,
                        openAcaiSonsBuilding = sonsBuilding,
                        openAcaiSonsCrafting = sonsCrafting,
                        openAcaiSonsItemRegistration = sonsItemRegistration,
                        openAcaiSonsItems = sonsItems,
                        openAcaiSonsInventory = sonsInventory,
                        openAcaiSonsNetworking = sonsNetworking,
                        openAcaiSonsSaveData = OpenACAISonsSaveData.State,
                        openAcaiSonsSaveLifecycle = OpenACAISonsSaveLifecycle.State,
                        openAcaiSonsSaveRegistry = OpenACAISonsSaveRegistry.State,
                        openAcaiSonsMenuBridge = OpenACAISonsMenuBridgeState.CreateState(),
                        modInventory,
                        redLoaderCompatibility = new
                        {
                            pluginGuid = OpenACAIEndnightRuntime.PluginGuid,
                            pluginName = OpenACAIEndnightRuntime.PluginName,
                            pluginVersion = OpenACAIEndnightRuntime.Version,
                            nativeUnitySupportActive,
                            hostedModCount = effectiveHostedMods.Count,
                            hostedMods = effectiveHostedMods.Select(CreateHostedModState).ToArray(),
                            runtimeIssues = OpenACAILegacyRedLoaderCompatibility.CreateRuntimeIssueState(),
                            openAcaiHost = _redLoaderHostState
                        },
                        openAcaiModInventoryCatalog = OpenACAIModding.ModInventoryCatalog,
                        startupError
                    };

                    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    File.WriteAllText(OpenACAIPaths.LoaderStatePath, json);
                    File.WriteAllText(OpenACAIPaths.LegacyBootstrapStatePath, json);
                    OpenACAIEndnightRuntime.LogSource?.LogDebug("Modstoke Loader state written to " + OpenACAIPaths.LoaderStatePath + ".");
                }
                catch (Exception ex)
                {
                    OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke Loader state write failed: " + ex.Message);
                }
            }
        }

        internal static ModInventoryState RefreshModInventory()
        {
            using (OpenACAITimings.Measure("mods.inventory", "refresh"))
            {
                var inventory = ModInventoryScanner.Create(Paths.GameRootPath, Paths.PluginPath, _hostedRedLoaderMods ?? Array.Empty<ModBase>());
                OpenAcaiModMenu.SetInventory(inventory);
                OpenACAIModding.PublishModInventory(inventory);
                OpenACAIBepInExPluginCompatibility.Refresh(inventory);
                var nativeModHost = OpenACAINativeModHost.Refresh(inventory);
                var nativeLifecycleRegistry = OpenACAINativeLifecycleRegistry.Refresh(nativeModHost);
                var nativeLifecycleDispatcher = OpenACAINativeLifecycleDispatcher.Refresh(nativeLifecycleRegistry);
                var nativeLifecycleInvocationBridge = OpenACAINativeLifecycleInvocationBridge.Refresh(nativeLifecycleDispatcher);
                var nativeLifecycleInvocationAdapter = OpenACAINativeLifecycleInvocationAdapter.Refresh(nativeLifecycleInvocationBridge);
                var nativeLifecycleInvocationTokenResolver = OpenACAINativeLifecycleInvocationTokenResolver.Refresh(nativeLifecycleInvocationAdapter);
                var nativeLifecycleInvocationJournal = OpenACAINativeLifecycleInvocationJournal.Refresh(nativeLifecycleInvocationTokenResolver);
                var nativeLifecycleInvocationScheduler = OpenACAINativeLifecycleInvocationScheduler.Refresh(nativeLifecycleInvocationJournal);
                OpenACAINativeLifecycleInvocationExecutor.Refresh(nativeLifecycleInvocationScheduler);
                return inventory;
            }
        }

        internal static void RequestRefresh(string reason)
        {
            // Logging callbacks can arrive off-thread; Unity state is collected by the driver.
            lock (RefreshSync)
            {
                _pendingRefreshReason = reason ?? "unspecified";
            }
        }

        internal static void PumpRefresh(IReadOnlyCollection<string> startupWarnings, bool force = false)
        {
            string reason;
            lock (RefreshSync)
            {
                if (_pendingRefreshReason == null || _stateRefreshInProgress
                    || (!force && Environment.TickCount64 < _nextStateRefreshTick))
                {
                    return;
                }

                reason = _pendingRefreshReason;
                _pendingRefreshReason = null;
                _stateRefreshInProgress = true;
            }

            try
            {
                OpenACAITimings.Mark("loader.state", "refresh-requested", reason);
                Write(_lastNativeUnitySupportActive, _hostedRedLoaderMods ?? Array.Empty<ModBase>(), _lastStartupError, startupWarnings);
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

        private static string GetSonsGameVersion()
        {
            var versionType = Type.GetType("Sons.GameApplication.Version, Assembly-CSharp");
            var method = versionType?.GetMethod("GetVersionString", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return method?.Invoke(null, null) as string;
        }
    }
}
