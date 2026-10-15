// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Modstoke Loader bootstrap entrypoint.
// OpenACAI-owned runtime modules live beside this file; BepInEx, RedLoader,
// and SonsSdk are compatibility/bootstrap surfaces under their upstream
// licenses; see the public loader repo notices.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using OpenACAI.Loader;
using OpenACAI.Sons;
using Sons.Gui;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OpenACAI.Endnight.Loader
{
    [BepInPlugin(OpenACAIEndnightRuntime.PluginGuid, OpenACAIEndnightRuntime.PluginName, OpenACAIEndnightRuntime.Version)]
    public sealed class Plugin : BasePlugin
    {
        public Plugin()
        {
        }

        internal Plugin(ManualLogSource log) : base(log)
        {
        }

        public override void Load()
        {
            OpenACAIEndnightRuntime.LoadPlugin(this);
        }
    }

    internal static class OpenACAIEndnightRuntime
    {
        public const string PluginGuid = "com.openacai.sotf.endnightloader";
        public const string PluginName = "Modstoke Loader Core";
        public const string Version = "0.4.0-net11-preview";
        private const string EnableSonsMenuHarmonyEnvVar = "OPENACAI_ENABLE_SONS_MENU_HARMONY";
        private const string DisableSonsMenuHarmonyEnvVar = "OPENACAI_DISABLE_SONS_MENU_HARMONY";

        internal static ManualLogSource LogSource;
        private static readonly List<string> StartupWarnings = new List<string>();
        private static bool _redLoaderLogBridgeInstalled;
        private static bool _redLoaderCompatibilityPatchesInstalled;
        private static bool _sonsMenuHarmonyInstalled;
        private static readonly object StartupSync = new object();
        private static StartupState _startupState;
        private static Exception _startupFailure;
        private static HarmonyLib.Harmony _sonsMenuHarmony;
        private static OpenACAIHarmonySupportSnapshot _harmonySupportState;

        private enum StartupState { NotStarted, Starting, Ready, Failed }

        internal static bool PluginLoadingAvailable
        {
            get
            {
                lock (StartupSync)
                    return _startupState == StartupState.Starting || _startupState == StartupState.Ready;
            }
        }

        internal static string EnableRedLoaderModsEnvironmentVariableName => OpenACAILegacyRedLoaderCompatibility.EnableRedLoaderModsEnvironmentVariableName;
        internal static string RedLoaderModAllowlistEnvironmentVariableName => OpenACAILegacyRedLoaderCompatibility.RedLoaderModAllowlistEnvironmentVariableName;
        internal static string UseUpstreamRedLoaderBootstrapEnvironmentVariableName => OpenACAILegacyRedLoaderCompatibility.UseUpstreamRedLoaderBootstrapEnvironmentVariableName;
        internal static string EnableRedLoaderModsButtonEnvironmentVariableName => OpenACAILegacyRedLoaderCompatibility.EnableRedLoaderModsButtonEnvironmentVariableName;

        internal static void Load(ManualLogSource log) => LoadCore(log, null);

        internal static void LoadPlugin(Plugin plugin) => LoadCore(plugin.Log, plugin);

        private static void LoadCore(ManualLogSource log, Plugin plugin)
        {
            ArgumentNullException.ThrowIfNull(log);
            lock (StartupSync)
            {
                switch (_startupState)
                {
                    case StartupState.Ready:
                        return;
                    case StartupState.Starting:
                        throw new InvalidOperationException("OpenACAI Core startup is already in progress.");
                    case StartupState.Failed:
                        throw new InvalidOperationException("OpenACAI Core startup previously failed. Restart the game before trying again.", _startupFailure);
                }
                _startupState = StartupState.Starting;
                LogSource = log;
            }

            var loadStopwatch = Stopwatch.StartNew();
            OpenACAIBepInExRegistry registry = null;
            PluginInfo coreInfo = null;
            try
            {
                TraceCompat("OpenACAIEndnightRuntime.Load entered");
                OpenACAIManagedEnumerator.InstallBepInEx();
                if (IL2CPPChainloader.Instance == null)
                {
                    registry = new OpenACAIBepInExRegistry(AddStartupWarning);
                    IL2CPPChainloader.Instance = registry;
                    coreInfo = registry.BeginCorePlugin();
                    plugin ??= new Plugin(log);
                    registry.BeforeLoad(coreInfo, typeof(Plugin).Assembly, plugin);
                }
                OpenACAITimings.Initialize();
                OpenACAITimings.Mark("loader", "load-entered", ".NET " + Environment.Version);
                InitializeRuntimeServices();
                OpenACAIUnityLogSource.Install(AddStartupWarning);
                TraceCompat("OpenACAIAssemblyResolver.Install");
                using (OpenACAITimings.Measure("loader.bootstrap", "assembly-resolver"))
                {
                    OpenACAIAssemblyResolver.Install(log);
                }

                TraceCompat("OpenACAICompatibilityDependencies.Publish");
                using (OpenACAITimings.Measure("loader.bootstrap", "compatibility-dependencies"))
                {
                    OpenACAICompatibilityDependencies.Publish(AddStartupWarning);
                }

                TraceCompat("OpenACAILegacyRedLoaderCompatibility.SetExecutablePath");
                using (OpenACAITimings.Measure("loader.bootstrap", "environment-paths"))
                {
                    OpenACAILegacyRedLoaderCompatibility.SetExecutablePath(Paths.ExecutablePath);
                }

                TraceCompat("OpenACAILegacyRedLoaderCompatibility.InitializeCompatibilityDrivers");
                OpenACAILegacyRedLoaderCompatibility.InitializeCompatibilityDrivers();

                TraceCompat("InstallRedLoaderCompatibilityPatches");
                OpenACAILegacyRedLoaderCompatibilityPatches.InstallRedLoaderCompatibilityPatches(
                    AddStartupWarning,
                    ref _redLoaderCompatibilityPatchesInstalled);

                TraceCompat("OpenACAILegacyRedLoaderCompatibility.TryInitializeCorePreferences");
                using (OpenACAITimings.Measure("loader.bootstrap", "core-preferences"))
                {
                    OpenACAILegacyRedLoaderCompatibility.TryInitializeCorePreferences(AddStartupWarning);
                }

                TraceCompat("ConfigSystem.Load");
                using (OpenACAITimings.Measure("loader.bootstrap", "config-system"))
                {
                    OpenACAILegacyRedLoaderCompatibility.TryInitializeConfigSystem(AddStartupWarning);
                }

                TraceCompat("InstallRedLoaderLogBridge");
                OpenACAILegacyRedLoaderCompatibility.InstallLogBridge(ref _redLoaderLogBridgeInstalled, LogSource, AddStartupWarning);

                TraceCompat("TryInitializeRedLoaderUnitySupport");
                bool nativeUnitySupportActive;
                using (OpenACAITimings.Measure("loader.unity", "native-support-probe"))
                {
                    nativeUnitySupportActive = OpenACAILegacyRedLoaderCompatibility.TryInitializeUnitySupport(AddStartupWarning);
                }

                TraceCompat("TryInitializeRedLoaderUnitySupport returned " + nativeUnitySupportActive);
                TraceCompat("TryInitializeRedLoaderSupportModule");
                var supportModuleInitialized = OpenACAILegacyRedLoaderCompatibility.TryInitializeSupportModule(AddStartupWarning);
                TraceCompat("TryInitializeRedLoaderSupportModule returned " + supportModuleInitialized);
                if (!nativeUnitySupportActive)
                {
                    TraceCompat("InstallSceneHooks");
                    using (OpenACAITimings.Measure("loader.unity", "scene-hooks-install"))
                    {
                        OpenACAIUnityLifecycle.InstallSceneHooks(AddStartupWarning);
                    }

                    TraceCompat("InstallSceneHooks returned");
                }

                TraceCompat("Creating SdkEntryPoint");
                var processor = OpenACAILegacyLifecycleBridge.CreateSdkEntryPoint();
                TraceCompat("OpenACAILegacyLifecycleBridge.PublishPreModsLoaded");
                OpenACAILegacyLifecycleBridge.PublishPreModsLoaded();
                TraceCompat("LoadRedLoaderMods");
                RedLoaderHostResult hostResult;
                using (OpenACAITimings.Measure("loader.redloader", "mod-discovery-hosting"))
                {
                    hostResult = OpenACAILegacyRedLoaderCompatibility.LoadMods(processor, AddStartupWarning);
                }

                OpenACAILoaderState.SetRedLoaderHostState(hostResult.CreateState());
                OpenACAILegacyRedLoaderCompatibility.PublishRedLoaderShimState(hostResult);
                OpenACAILegacyRedLoaderCompatibilityPatches.InstallHostedModCompatibilityPatches(
                    hostResult,
                    AddStartupWarning);
                OpenACAISonsSaveLifecycle.RefreshLegacySonsSdkRelay();
                TraceCompat("LoadRedLoaderMods returned " + hostResult.HostedCount + " mod(s) in " + hostResult.Mode + " mode");

                TraceCompat("RegisterHostedModTypes");
                using (OpenACAITimings.Measure("loader.redloader", "register-hosted-types"))
                {
                    OpenACAILegacyRedLoaderCompatibility.RegisterHostedModTypes(hostResult, AddStartupWarning);
                }

                TraceCompat("LoadBepInExPlugins");
                var startupInventory = ModInventoryScanner.Create(Paths.GameRootPath, Paths.PluginPath, hostResult.HostedMods);
                using (OpenACAITimings.Measure("loader.bepinex", "plugin-hosting"))
                {
                    var bepinExHostResult = OpenACAIBepInExPluginHost.LoadPlugins(startupInventory, AddStartupWarning);
                    OpenACAILoaderState.SetBepInExPluginHostState(bepinExHostResult.CreateState());
                    TraceCompat("LoadBepInExPlugins returned " + bepinExHostResult.LoadedThisRunCount + " plugin(s)");
                }

                TraceCompat("OpenACAILegacyLifecycleBridge.PublishApplicationStart");
                using (OpenACAITimings.Measure("loader.lifecycle", "application-start"))
                {
                    OpenACAILegacyLifecycleBridge.PublishApplicationStart();
                }

                OpenACAILegacyLifecycleBridge.PreparePostUnityCompatibility();
                OpenACAILegacyLifecycleBridge.InitializeSonsSdkAfterUnity(processor, hostResult, AddStartupWarning);
                TraceCompat("ApplyEntrypointlessModPorts");
                using (OpenACAITimings.Measure("loader.compatibility", "entrypointless-mod-ports"))
                {
                    OpenACAIEntrypointlessModPorts.Apply(AddStartupWarning);
                }

                if (!nativeUnitySupportActive)
                {
                    TraceCompat("CreateDriver");
                    using (OpenACAITimings.Measure("loader.unity", "driver-create"))
                    {
                        OpenACAIUnityLifecycle.CreateDriver();
                    }

                    TraceCompat("CreateDriver returned");
                }

                TraceCompat("InstallSonsMenuHarmony");
                using (OpenACAITimings.Measure("loader.sons-menu", "harmony-install"))
                {
                    InstallSonsMenuHarmony();
                }

                TraceCompat("InstallSonsMenuHarmony returned");

                TraceCompat("InstallSonsSaveLifecycleHarmony");
                using (OpenACAITimings.Measure("loader.sons-save", "harmony-install"))
                {
                    OpenACAISonsSaveLifecycle.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsSaveLifecycleHarmony returned");

                TraceCompat("InitializeSonsServerWorld");
                OpenACAISonsServerWorld.Initialize();

                TraceCompat("InitializeSonsServerRoles");
                OpenACAISonsServerRoles.Initialize();

                TraceCompat("InitializeSonsEnvironmentReads");
                OpenACAISonsEnvironmentReads.Initialize();

                TraceCompat("InitializeSonsWorldLocatorReads");
                OpenACAISonsWorldLocatorReads.Initialize();

                TraceCompat("InitializeSonsServerSaves");
                OpenACAISonsServerSaves.Initialize();

                TraceCompat("InitializeSonsServerTime");
                OpenACAISonsServerTime.Initialize();

                TraceCompat("InitializeSonsServerWeather");
                OpenACAISonsServerWeather.Initialize();

                TraceCompat("InitializeSonsServerInventory");
                OpenACAISonsServerInventory.Initialize();

                TraceCompat("InitializeSonsServerAdmission");
                OpenACAISonsServerAdmission.Initialize();

                TraceCompat("InstallSonsServerSessionsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-sessions", "harmony-install"))
                {
                    OpenACAISonsServerSessions.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerAuthenticationHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-authentication", "harmony-install"))
                {
                    OpenACAISonsServerAuthentication.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerChatHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-chat", "harmony-install"))
                {
                    OpenACAISonsServerChat.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerChatHarmony returned");

                TraceCompat("InstallSonsServerAdminRequestsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-admin-requests", "harmony-install"))
                {
                    OpenACAISonsServerAdminRequests.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerItemDropsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-item-drops", "harmony-install"))
                {
                    OpenACAISonsServerItemDrops.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerStorageHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-storage", "harmony-install"))
                {
                    OpenACAISonsServerStorage.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerWorldObjectsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-world-objects", "harmony-install"))
                {
                    OpenACAISonsServerWorldObjects.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerWorldLocatorsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-world-locators", "harmony-install"))
                {
                    OpenACAISonsServerWorldLocators.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerStructureHitsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-structure-hits", "harmony-install"))
                {
                    OpenACAISonsServerStructureHits.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerConstructionHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-construction", "harmony-install"))
                {
                    OpenACAISonsServerConstruction.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerVehiclesHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-vehicles", "harmony-install"))
                {
                    OpenACAISonsServerVehicles.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerFreeFormHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-free-form", "harmony-install"))
                {
                    OpenACAISonsServerFreeForm.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsBlobTransportHarmony");
                using (OpenACAITimings.Measure("loader.sons-blob-transport", "harmony-install"))
                {
                    OpenACAISonsBlobTransport.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsConstructionSyncHarmony");
                using (OpenACAITimings.Measure("loader.sons-construction-sync", "harmony-install"))
                {
                    OpenACAISonsConstructionSync.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerPlayerSpawnsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-player-spawns", "harmony-install"))
                {
                    OpenACAISonsServerPlayerSpawns.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerPlayerHitsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-player-hits", "harmony-install"))
                {
                    OpenACAISonsServerPlayerHits.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerPlayerRevivesHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-player-revives", "harmony-install"))
                {
                    OpenACAISonsServerPlayerRevives.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerInventoryHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-inventory", "harmony-install"))
                {
                    OpenACAISonsServerInventory.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerSleepHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-sleep", "harmony-install"))
                {
                    OpenACAISonsServerSleep.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerVailHitsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-vail-hits", "harmony-install"))
                {
                    OpenACAISonsServerVailHits.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerDismemberHitsHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-dismember-hits", "harmony-install"))
                {
                    OpenACAISonsServerDismemberHits.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("InstallSonsServerNoticesHarmony");
                using (OpenACAITimings.Measure("loader.sons-server-notices", "harmony-install"))
                {
                    OpenACAISonsServerNotices.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                }

                TraceCompat("LoadNativeOpenACAIMods");
                using (OpenACAITimings.Measure("loader.native", "mod-hosting"))
                {
                    OpenACAIModding.PublishModInventory(startupInventory);
                    OpenACAINativeModRuntime.Load(startupInventory, !nativeUnitySupportActive);
                }

                TraceCompat("OpenACAILoaderCapabilities.MarkReady");
                OpenACAILoaderCapabilities.MarkReady(nativeUnitySupportActive, hostResult.HostedCount);
                OpenACAIModding.MarkReady(nativeUnitySupportActive, hostResult.HostedCount, OpenACAILoaderCapabilities.PathsSnapshot, StartupWarnings);
                OpenACAIRuntimeAttestation.Refresh();
                OpenACAIServerPolicy.Refresh();
                OpenACAISons.PublishLifecycle("LoaderReady");
                LogStartupMessage(LogLevel.Info, OpenACAILegacyRedLoaderCompatibility.CreateCompatibilityStartedMessage(hostResult));
                OpenACAITimings.RecordDuration("loader", "load-total", loadStopwatch.Elapsed, "ready");
                TraceCompat("OpenACAILoaderState.Write success path");
                OpenACAILoaderState.Write(nativeUnitySupportActive, hostResult, null, StartupWarnings);
                TraceCompat("OpenACAIEndnightRuntime.Load completed");
                lock (StartupSync)
                    _startupState = StartupState.Ready;
                if (registry != null)
                {
                    coreInfo.Instance = plugin;
                    registry.NotifyPluginLoaded(coreInfo);
                    registry.NotifyFinished();
                }
            }
            catch (Exception ex)
            {
                lock (StartupSync)
                {
                    _startupFailure = ex;
                    _startupState = StartupState.Failed;
                }
                registry?.WithdrawPlugin(coreInfo);
                try { OpenACAINativeModRuntime.Shutdown(); }
                catch { /* Preserve the original Core startup failure. */ }
                try { OpenACAISonsAudioBanks.Shutdown(); }
                catch { /* Preserve the original Core startup failure. */ }
                try
                {
                    ReportStartupFailure(ex, loadStopwatch.Elapsed);
                }
                catch (Exception diagnosticError)
                {
                    TraceCompat("Core startup failure reporting also failed with " + diagnosticError.GetType().FullName
                        + "; preserving the original startup exception.");
                }
                // Both the owned adapter and the compatibility chainloader need the original failure.
                throw;
            }
        }

        private static void InitializeRuntimeServices()
        {
            OpenACAIRuntime.Initialize(Version, OpenACAILoaderCapabilities.CompatibilityMode);
            OpenACAIInput.InitializeDefaults();
            OpenACAIConfig.Initialize();
            OpenACAIUi.InitializeDefaults();
            OpenACAIMenu.InitializeDefaults();
            OpenACAIInput.RegisterMenuProvider();
            OpenACAIFileIntegrity.InitializeDefaults();
            OpenACAIRuntimeAttestation.InitializeDefaults();
            OpenACAIServerPolicy.InitializeDefaults();
            OpenACAIRuntimePreferences.Initialize();
            OpenACAIRuntimeCommands.Initialize();
            OpenACAIChainloaderStatus.Initialize();
            OpenACAIBepInExPluginCompatibility.Initialize();
            OpenACAINativeModHost.Initialize();
            OpenACAINativeLifecycleRegistry.Initialize();
            OpenACAINativeLifecycleDispatcher.Initialize();
            OpenACAINativeLifecycleInvocationBridge.Initialize();
            OpenACAINativeLifecycleInvocationAdapter.Initialize();
            OpenACAINativeLifecycleInvocationTokenResolver.Initialize();
            OpenACAINativeLifecycleInvocationJournal.Initialize();
            OpenACAINativeLifecycleInvocationScheduler.Initialize();
            OpenACAINativeLifecycleInvocationExecutor.Initialize();
            OpenACAIProtectedServerPolicy.Initialize();
            OpenACAIProtectedRuntimeStatus.Initialize();
            OpenACAILibraryInventoryStatus.Initialize();
            OpenACAISonsAssets.Initialize();
            OpenACAISonsAudioBanks.Initialize();
            OpenACAISonsItems.Initialize();
            OpenACAISonsItemRegistration.Initialize();
            OpenACAISonsInventory.Initialize();
            OpenACAISonsCrafting.Initialize();
            OpenACAISonsBuilding.Initialize();
            OpenACAISonsNetworking.Initialize();
            OpenACAIRuntime.MarkShimReady(
                OpenACAICompatibilityShimIds.BepInExPlugins,
                "BepInEx plugins",
                "BepInEx",
                "transitional-il2cpp-bootstrap",
                "BepInEx IL2CPP loaded the OpenACAI runtime bridge. This remains a transitional compatibility shim.");
            var il2CppHookState = OpenACAIIL2CPPHookState.Publish(Paths.PluginPath, Paths.GameRootPath);
            _harmonySupportState = OpenACAIHarmonySupport.Publish(
                Path.Combine(Paths.BepInExRootPath, "core"),
                il2CppHookState,
                IsTruthy(Environment.GetEnvironmentVariable(EnableSonsMenuHarmonyEnvVar)),
                IsTruthy(Environment.GetEnvironmentVariable(DisableSonsMenuHarmonyEnvVar)));
            if (_harmonySupportState != null)
            {
                foreach (var warning in _harmonySupportState.Warnings)
                    AddStartupWarning(warning);
            }

            OpenACAIModding.PublishLifecycle("LoaderLoadStarted");
            OpenACAISons.Initialize();
            OpenACAISonsInventoryEvents.Initialize();
            OpenACAISonsAssets.Refresh();
            OpenACAISonsItems.Refresh();
            OpenACAISonsItemRegistration.Refresh();
            OpenACAISonsInventory.Refresh();
            OpenACAISonsCrafting.Refresh();
            OpenACAISonsBuilding.Refresh();
            OpenACAISonsNetworking.Refresh();
        }

        private static void ReportStartupFailure(Exception error, TimeSpan elapsed)
        {
            try
            {
                // Publish failure before invoking diagnostic callbacks that could themselves throw.
                OpenACAILoaderCapabilities.MarkFailed(error.Message);
                TraceCompat("OpenACAIEndnightRuntime.Load managed exception: " + error);
                OpenACAIModding.MarkFailed(error.Message, OpenACAILoaderCapabilities.PathsSnapshot, StartupWarnings);
                OpenACAIRuntime.MarkShimFailed(
                    OpenACAICompatibilityShimIds.RedLoaderMods,
                    "RedLoader mods",
                    "RedLoader",
                    "startup",
                    error.Message);
                OpenACAIRuntime.MarkShimFailed(
                    OpenACAICompatibilityShimIds.SonsSdk,
                    "SonsSdk APIs",
                    "SonsSdk",
                    "startup",
                    error.Message);
                OpenACAIRuntimeAttestation.Refresh();
                OpenACAIServerPolicy.Refresh();
                OpenACAISons.PublishLifecycle("LoaderFailed");
                OpenACAITimings.RecordDuration("loader", "load-total", elapsed, "failed");
                LogSource.LogError("OpenACAI Core failed to start: " + error);
            }
            finally
            {
                OpenACAILoaderState.WriteFailure(error.Message, StartupWarnings);
            }
        }

        internal static void TraceCompat(string message)
        {
            try
            {
                var root = !string.IsNullOrWhiteSpace(Paths.GameRootPath)
                    ? Paths.GameRootPath
                    : Path.GetDirectoryName(Paths.ExecutablePath);
                var path = Path.Combine(root ?? Environment.CurrentDirectory, "openacai-redloader-compat-net11-preview.log");
                File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static bool IsTruthy(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        internal static ModInventoryState RefreshModInventory()
        {
            return OpenACAILoaderState.RefreshModInventory();
        }

        internal static void RequestLoaderStateRefresh(string reason)
        {
            OpenACAILoaderState.RequestRefresh(reason);
        }

        internal static void PumpLoaderStateRefresh(bool force = false)
        {
            OpenACAILoaderState.PumpRefresh(StartupWarnings, force);
        }

        internal static void AddStartupWarning(string message)
        {
            StartupWarnings.Add(message);
            OpenACAIModding.PublishStartupWarning(message);
            LogStartupMessage(LogLevel.Warning, message);
        }

        private static void LogStartupMessage(LogLevel level, string message)
        {
            try
            {
                LogSource?.Log(level, message);
            }
            catch (Exception error)
            {
                // Diagnostic listeners do not determine whether startup can continue.
                TraceCompat("Startup log delivery failed with " + error.GetType().FullName);
            }
        }

        private static void InstallSonsMenuHarmony()
        {
            if (_sonsMenuHarmonyInstalled)
            {
                return;
            }

            var harmonySupport = _harmonySupportState;
            if (harmonySupport == null)
            {
                harmonySupport = OpenACAIHarmonySupport.Publish(
                    Path.Combine(Paths.BepInExRootPath, "core"),
                    OpenACAIIL2CPPHookState.Load(Paths.PluginPath, Paths.GameRootPath),
                    IsTruthy(Environment.GetEnvironmentVariable(EnableSonsMenuHarmonyEnvVar)),
                    IsTruthy(Environment.GetEnvironmentVariable(DisableSonsMenuHarmonyEnvVar)));
                _harmonySupportState = harmonySupport;
            }

            if (harmonySupport == null || !harmonySupport.SonsMenuHarmonyEnabled)
            {
                var detail = harmonySupport == null
                    ? "Harmony support probe was unavailable."
                    : harmonySupport.Detail;
                AddStartupWarning("Sons menu Harmony patches were not installed: " + detail + " Set " + EnableSonsMenuHarmonyEnvVar + "=1 to force diagnostics or " + DisableSonsMenuHarmonyEnvVar + "=1 to keep them off.");
                return;
            }

            if (!harmonySupport.SafeForHarmonyPatches)
            {
                AddStartupWarning("Sons menu Harmony patches are forced on before the runtime passed the full safety probe: " + harmonySupport.Detail);
            }

            _sonsMenuHarmonyInstalled = true;
            _sonsMenuHarmony = new HarmonyLib.Harmony(PluginGuid + ".sons-menu");

            TryPatchSonsMenu("PauseMenu.Start", AccessTools.Method(typeof(PauseMenu), "Start"), nameof(SonsMenuPatches.AfterPauseMenuAvailable));
            TryPatchSonsMenu("PauseMenu.Open", AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.Open)), nameof(SonsMenuPatches.AfterPauseMenuOpen));
            TryPatchSonsMenu("PauseMenu.SetActiveInternal", AccessTools.Method(typeof(PauseMenu), "SetActiveInternal"), nameof(SonsMenuPatches.AfterPauseMenuAvailable));
        }

        private static void TryPatchSonsMenu(string label, MethodInfo target, string patchMethodName)
        {
            try
            {
                if (target == null)
                {
                    AddStartupWarning(label + " patch target was not found.");
                    return;
                }

                var patch = AccessTools.Method(typeof(SonsMenuPatches), patchMethodName);
                _sonsMenuHarmony.Patch(target, postfix: new HarmonyMethod(patch));
            }
            catch (Exception ex)
            {
                AddStartupWarning(label + " patch failed: " + DescribePatchException(ex));
            }
        }

        private static string DescribePatchException(Exception exception)
        {
            if (exception == null)
            {
                return "unknown exception";
            }

            var parts = new List<string>();
            var current = exception;
            var depth = 0;
            while (current != null && depth < 4)
            {
                parts.Add(current.GetType().Name + ": " + current.Message);
                current = current.InnerException;
                depth++;
            }

            return string.Join(" -> ", parts);
        }

    }

}

