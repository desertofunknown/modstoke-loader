// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Modstoke Loader bootstrap entrypoint.
// Loader runtime modules live beside this file; BepInEx, RedLoader, and SonsSdk
// are compatibility/bootstrap surfaces under their upstream licenses; see the
// public loader repo notices.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Modstoke.Loader;
using Modstoke.Sons;
using global::Sons.Gui;

namespace Modstoke.Loader
{
    [BepInPlugin(RuntimeEntryPoint.PluginGuid, RuntimeEntryPoint.PluginName, RuntimeEntryPoint.Version)]
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
            RuntimeEntryPoint.LoadPlugin(this);
        }
    }

    internal static class RuntimeEntryPoint
    {
        public const string PluginGuid = "com.openacai.sotf.endnightloader";
        public const string PluginName = "Modstoke Loader Core";
        public const string Version = "0.4.3-net11-preview";
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
        private static HarmonySupportSnapshot _harmonySupportState;
        private static readonly object TraceSync = new object();
        private static StreamWriter _traceWriter;
        private static bool _traceUnavailable;

        private enum StartupState { NotStarted, Starting, Ready, Failed }

        internal static bool StartupCompleted
        {
            get { lock (StartupSync) return _startupState == StartupState.Ready; }
        }

        internal static bool PluginLoadingAvailable
        {
            get
            {
                lock (StartupSync)
                    return _startupState == StartupState.Starting || _startupState == StartupState.Ready;
            }
        }

        internal static string EnableRedLoaderModsEnvironmentVariableName => LegacyRedLoaderCompatibility.EnableRedLoaderModsEnvironmentVariableName;
        internal static string RedLoaderModAllowlistEnvironmentVariableName => LegacyRedLoaderCompatibility.RedLoaderModAllowlistEnvironmentVariableName;
        internal static string UseUpstreamRedLoaderBootstrapEnvironmentVariableName => LegacyRedLoaderCompatibility.UseUpstreamRedLoaderBootstrapEnvironmentVariableName;
        internal static string EnableRedLoaderModsButtonEnvironmentVariableName => LegacyRedLoaderCompatibility.EnableRedLoaderModsButtonEnvironmentVariableName;

        // Invoked by reflection from Modstoke.Bootstrap's direct core path (IL2CPPChainloaderAdapter).
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
                        throw new InvalidOperationException("Modstoke Loader Core startup is already in progress.");
                    case StartupState.Failed:
                        throw new InvalidOperationException("Modstoke Loader Core startup previously failed. Restart the game before trying again.", _startupFailure);
                }
                _startupState = StartupState.Starting;
                LogSource = log;
            }

            LoadedMods.Initialize();
            var loadStopwatch = Stopwatch.StartNew();
            BepInExRegistry registry = null;
            PluginInfo coreInfo = null;
            try
            {
                TraceCompat("RuntimeEntryPoint.LoadCore entered");
                ManagedEnumerator.InstallBepInEx();
                if (IL2CPPChainloader.Instance == null)
                {
                    registry = new BepInExRegistry(AddStartupWarning);
                    IL2CPPChainloader.Instance = registry;
                    coreInfo = registry.BeginCorePlugin();
                    plugin ??= new Plugin(log);
                    registry.BeforeLoad(coreInfo, typeof(Plugin).Assembly, plugin);
                }
                Timings.Initialize();
                Timings.Mark("loader", "load-entered", ".NET " + Environment.Version);
                InitializeRuntimeServices();
                WarningCapture.Initialize(AddStartupWarning);
                UnityLogSource.Install(AddStartupWarning);
                TraceCompat("AssemblyResolver.Install");
                using (Timings.Measure("loader.bootstrap", "assembly-resolver"))
                {
                    AssemblyResolver.Install(log);
                }

                InstallHooks("sons-settings-startup", SettingsStartup.InstallHarmonyPatches);

                TraceCompat("CompatibilityDependencies.Publish");
                using (Timings.Measure("loader.bootstrap", "compatibility-dependencies"))
                {
                    CompatibilityDependencies.Publish(AddStartupWarning);
                }

                TraceCompat("LegacyRedLoaderCompatibility.SetExecutablePath");
                using (Timings.Measure("loader.bootstrap", "environment-paths"))
                {
                    LegacyRedLoaderCompatibility.SetExecutablePath(Paths.ExecutablePath);
                }

                TraceCompat("LegacyRedLoaderCompatibility.InitializeCompatibilityDrivers");
                LegacyRedLoaderCompatibility.InitializeCompatibilityDrivers();

                TraceCompat("InstallRedLoaderCompatibilityPatches");
                LegacyRedLoaderCompatibilityPatches.InstallRedLoaderCompatibilityPatches(
                    AddStartupWarning,
                    ref _redLoaderCompatibilityPatchesInstalled);

                TraceCompat("LegacyRedLoaderCompatibility.TryInitializeCorePreferences");
                using (Timings.Measure("loader.bootstrap", "core-preferences"))
                {
                    LegacyRedLoaderCompatibility.TryInitializeCorePreferences(AddStartupWarning);
                }

                TraceCompat("ConfigSystem.Load");
                using (Timings.Measure("loader.bootstrap", "config-system"))
                {
                    LegacyRedLoaderCompatibility.TryInitializeConfigSystem(AddStartupWarning);
                }

                TraceCompat("InstallRedLoaderLogBridge");
                LegacyRedLoaderCompatibility.InstallLogBridge(ref _redLoaderLogBridgeInstalled, LogSource, AddStartupWarning);

                TraceCompat("TryInitializeRedLoaderUnitySupport");
                bool nativeUnitySupportActive;
                using (Timings.Measure("loader.unity", "native-support-probe"))
                {
                    nativeUnitySupportActive = LegacyRedLoaderCompatibility.TryInitializeUnitySupport(AddStartupWarning);
                }

                TraceCompat("TryInitializeRedLoaderUnitySupport returned " + nativeUnitySupportActive);
                TraceCompat("TryInitializeRedLoaderSupportModule");
                var supportModuleInitialized = LegacyRedLoaderCompatibility.TryInitializeSupportModule(AddStartupWarning);
                TraceCompat("TryInitializeRedLoaderSupportModule returned " + supportModuleInitialized);
                if (!nativeUnitySupportActive)
                {
                    TraceCompat("InstallSceneHooks");
                    using (Timings.Measure("loader.unity", "scene-hooks-install"))
                    {
                        UnityLifecycle.InstallSceneHooks(AddStartupWarning);
                    }

                    TraceCompat("InstallSceneHooks returned");
                }

                TraceCompat("Creating SdkEntryPoint");
                var processor = LegacyLifecycleBridge.CreateSdkEntryPoint();
                TraceCompat("LegacyLifecycleBridge.PublishPreModsLoaded");
                LegacyLifecycleBridge.PublishPreModsLoaded();
                TraceCompat("LoadRedLoaderMods");
                RedLoaderHostResult hostResult;
                using (Timings.Measure("loader.redloader", "mod-discovery-hosting"))
                {
                    hostResult = LegacyRedLoaderCompatibility.LoadMods(processor, AddStartupWarning);
                }

                LoaderState.SetRedLoaderHostState(hostResult.CreateState());
                LegacyRedLoaderCompatibility.PublishRedLoaderShimState(hostResult);
                LegacyRedLoaderCompatibilityPatches.InstallHostedModCompatibilityPatches(
                    hostResult,
                    AddStartupWarning);
                AxelCompatibility.Install(hostResult, AddStartupWarning);
                SaveLifecycle.RefreshLegacySonsSdkRelay();
                TraceCompat("LoadRedLoaderMods returned " + hostResult.HostedCount + " mod(s) in " + hostResult.Mode + " mode");

                TraceCompat("RegisterHostedModTypes");
                using (Timings.Measure("loader.redloader", "register-hosted-types"))
                {
                    LegacyRedLoaderCompatibility.RegisterHostedModTypes(hostResult, AddStartupWarning);
                }

                TraceCompat("LoadBepInExPlugins");
                var startupInventory = ModInventoryScanner.Create(Paths.GameRootPath, Paths.PluginPath, hostResult.HostedMods);
                using (Timings.Measure("loader.bepinex", "plugin-hosting"))
                {
                    var bepinExHostResult = BepInExPluginHost.LoadPlugins(startupInventory, AddStartupWarning);
                    LoaderState.SetBepInExPluginHostState(bepinExHostResult.CreateState());
                    TraceCompat("LoadBepInExPlugins returned " + bepinExHostResult.LoadedThisRunCount + " plugin(s)");
                }

                TraceCompat("LegacyLifecycleBridge.PublishApplicationStart");
                using (Timings.Measure("loader.lifecycle", "application-start"))
                {
                    LegacyLifecycleBridge.PublishApplicationStart();
                }

                LegacyLifecycleBridge.PreparePostUnityCompatibility();
                LegacyLifecycleBridge.InitializeSonsSdkAfterUnity(processor, hostResult, AddStartupWarning);
                TraceCompat("ApplyEntrypointlessModPorts");
                using (Timings.Measure("loader.compatibility", "entrypointless-mod-ports"))
                {
                    EntrypointlessModPorts.Apply(AddStartupWarning);
                }

                if (!nativeUnitySupportActive)
                {
                    TraceCompat("CreateDriver");
                    using (Timings.Measure("loader.unity", "driver-create"))
                    {
                        UnityLifecycle.CreateDriver();
                    }

                    TraceCompat("CreateDriver returned");
                }

                TraceCompat("InstallSonsMenuHarmony");
                using (Timings.Measure("loader.sons-menu", "harmony-install"))
                {
                    InstallSonsMenuHarmony();
                }

                TraceCompat("InstallSonsMenuHarmony returned");

                InstallHooks("sons-save", SaveLifecycle.InstallHarmonyPatches);

                if (GameBuild.IsDedicatedServer)
                    AddStartupWarning("Dedicated server build: only the server-core Sons SDK hooks reviewed for it are on; player-simulation hooks stay off.");
                else if (!GameBuild.IsRecognized)
                    AddStartupWarning("This game build is not the one the Sons SDK hooks were qualified on, so build-specific hooks stay off.");

                TraceCompat("InitializeSonsServerWorld");
                ServerWorld.Initialize();

                TraceCompat("InitializeSonsServerRoles");
                ServerRoles.Initialize();

                TraceCompat("InitializeSonsEnvironmentReads");
                EnvironmentReads.Initialize();

                TraceCompat("InitializeSonsWorldLocatorReads");
                WorldLocatorReads.Initialize();

                TraceCompat("InitializeSonsLocatorRecordReads");
                LocatorRecordReads.Initialize();

                TraceCompat("InitializeSonsServerSaves");
                ServerSaves.Initialize();

                TraceCompat("InitializeSonsServerTime");
                ServerTime.Initialize();

                TraceCompat("InitializeSonsServerWeather");
                ServerWeather.Initialize();

                TraceCompat("InitializeSonsServerInventory");
                ServerInventory.Initialize();

                TraceCompat("InitializeSonsServerAdmission");
                ServerAdmission.Initialize();

                TraceCompat("InitializeSonsControlTransport");
                ControlTransport.Initialize(_harmonySupportState, AddStartupWarning);
                ClientEnrollment.Initialize(_harmonySupportState);
                ModAdmission.Initialize();
                Datagrams.Initialize(_harmonySupportState);
                ModuleStorage.Initialize(_harmonySupportState, AddStartupWarning);
                global::Modstoke.Sons.Combat.StrictCombatLifeHooks.Initialize(_harmonySupportState, AddStartupWarning);
                global::Modstoke.Sons.Combat.StrictCombatWeaponHooks.Initialize(_harmonySupportState, AddStartupWarning);
                global::Modstoke.Sons.Combat.StrictCombatRebreatherHooks.Initialize(_harmonySupportState, AddStartupWarning);
                global::Modstoke.Sons.Combat.StrictCombatArmourHooks.Initialize(_harmonySupportState, AddStartupWarning);

                TraceCompat("InstallSonsServerSessionsHarmony");
                using (Timings.Measure("loader.sons-server-sessions", "harmony-install"))
                {
                    ServerSessions.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                    ServerWorld.InstallPlayerIdentityHooks(_harmonySupportState, AddStartupWarning);
                    PlayerRoles.Install(_harmonySupportState, AddStartupWarning);
                    using (Timings.Measure("loader.player-carrier", "harmony-install"))
                        PlayerCarrier.Install(_harmonySupportState, AddStartupWarning);
                    using (Timings.Measure("loader.player-lifecycle", "harmony-install"))
                        PlayerLifecycle.Install(_harmonySupportState, AddStartupWarning);
                    // Inventory byte qualification must precede the shared unequip presentation hook.
                    using (Timings.Measure("loader.inventory-enforcement", "harmony-install"))
                        PlayerInventoryEnforcement.InstallHarmonyPatches(_harmonySupportState, AddStartupWarning);
                    PlayerLifePresentation.Install(_harmonySupportState, AddStartupWarning);
                    PlayerArmourSource.Install(_harmonySupportState, AddStartupWarning);
                    // Qualify cart exit bodies before client movement installs its shared exit hooks.
                    using (Timings.Measure("loader.cart-lifecycle", "harmony-install"))
                        VehiclePreservation.InstallCartLifecycleHooks(_harmonySupportState, AddStartupWarning);
                    using (Timings.Measure("loader.player-movement", "harmony-install"))
                        PlayerClientMovement.Install(_harmonySupportState, AddStartupWarning);
                    LocalPlayerReads.InstallMovementDefaults(_harmonySupportState, AddStartupWarning);
                    PlayerInventoryEnforcement.InstallHeldOnlyHooks(_harmonySupportState, AddStartupWarning);
                    PlayerInventoryEnforcement.InstallPowerPatches(_harmonySupportState, AddStartupWarning);
                    PlayerInventoryEnforcement.InstallCraftingPatches(_harmonySupportState, AddStartupWarning);
                }

                InstallHooks("sons-server-authentication", ServerAuthentication.InstallHarmonyPatches);
                InstallHooks("sons-server-events", ServerEvents.InstallHarmonyPatches);
                InstallHooks("sons-bolt-coverage",
                    BoltPackets.InstallHarmonyPatches,
                    BoltStates.InstallHarmonyPatches,
                    BoltStateAdmission.InstallHarmonyPatches,
                    BoltProperties.InstallHarmonyPatches,
                    BoltEntities.InstallHarmonyPatches,
                    BoltCommands.InstallHarmonyPatches,
                    BoltStreams.InstallHarmonyPatches);
                InstallHooks("sons-server-chat", ServerChat.InstallHarmonyPatches, Commands.InstallHostHarmonyPatch);
                InstallHooks("sons-server-voice", ServerVoice.InstallHarmonyPatches);
                InstallHooks("sons-server-printers", ServerPrinters.InstallHarmonyPatches);
                InstallHooks("sons-server-named-states", ServerNamedStates.InstallHarmonyPatches);
                InstallHooks("sons-server-companions",
                    ServerCompanions.InstallHarmonyPatches,
                    ServerCompanions.InstallObservationPatches,
                    CompanionJobs.InstallHarmonyPatches);
                InstallHooks("sons-npc-search", ServerNpcSearch.InstallHarmonyPatches, ServerNpcParties.InstallHarmonyPatches);
                InstallHooks("sons-server-admin-requests", ServerAdminRequests.InstallHarmonyPatches);
                InstallHooks("sons-server-item-drops", ServerItemDrops.InstallHarmonyPatches);
                InstallHooks("sons-server-pickups", ServerPickups.InstallHarmonyPatches);
                InstallHooks("sons-server-loot-spawns", ServerLootSpawns.InstallHarmonyPatches);
                InstallHooks("sons-server-actor-items", ServerActorItems.InstallHarmonyPatches);
                InstallHooks("sons-server-world-placements", ServerWorldPlacements.InstallHarmonyPatches);
                InstallHooks("sons-server-transports", ServerTransports.InstallHarmonyPatches);
                InstallHooks("sons-server-interactions", ServerInteractions.InstallHarmonyPatches);
                InstallHooks("sons-server-tree-spawns", ServerTreeSpawns.InstallHarmonyPatches);
                InstallHooks("sons-server-storage", ServerStorage.InstallHarmonyPatches);
                InstallHooks("sons-server-world-objects", ServerWorldObjects.InstallHarmonyPatches);
                InstallHooks("sons-server-world-locators", ServerWorldLocators.InstallHarmonyPatches);
                InstallHooks("sons-server-structure-hits", ServerStructureHits.InstallHarmonyPatches);
                InstallHooks("sons-server-construction", ServerConstruction.InstallHarmonyPatches);
                InstallHooks("sons-server-vehicles", ServerVehicles.InstallHarmonyPatches);
                InstallHooks("sons-server-free-form", ServerFreeForm.InstallHarmonyPatches);
                InstallHooks("sons-blob-transport", BlobTransport.InstallHarmonyPatches);
                InstallHooks("sons-construction-sync", ConstructionSync.InstallHarmonyPatches);
                InstallHooks("sons-placement-collisions", PlacementCollisionPatch.InstallHarmonyPatch);
                InstallHooks("sons-impact-audio", ImpactAudioPatch.InstallHarmonyPatch);
                InstallHooks("sons-game-lifecycle", GameLifecycleFixes.InstallHarmonyPatches, GatheringLifecycleFix.InstallHarmonyPatches);
                InstallHooks("sons-construction-dirty-cache", ConstructionDirtyPatch.InstallHarmonyPatch);
                PerformanceCapture.Initialize(_harmonySupportState);
                InstallHooks("sons-server-player-spawns", ServerPlayerSpawns.InstallHarmonyPatches);
                InstallHooks("sons-server-player-hits", ServerPlayerHits.InstallHarmonyPatches);
                InstallHooks("sons-server-player-revives", ServerPlayerRevives.InstallHarmonyPatches);
                InstallHooks("sons-server-inventory", ServerInventory.InstallHarmonyPatches);
                InstallHooks("sons-server-sleep", ServerSleep.InstallHarmonyPatches);
                InstallHooks("sons-server-vail-hits", ServerVailHits.InstallHarmonyPatches);
                InstallHooks("sons-server-melee-impacts", ServerMeleeImpacts.InstallHarmonyPatches);
                InstallHooks("sons-server-dismember-hits", ServerDismemberHits.InstallHarmonyPatches);
                InstallHooks("sons-server-notices", ServerNotices.InstallHarmonyPatches);

                TraceCompat("Loading native Modstoke mods");
                using (Timings.Measure("loader.native", "mod-hosting"))
                {
                    Modding.PublishModInventory(startupInventory);
                    ModRuntime.Load(startupInventory, !nativeUnitySupportActive);
                }

                TraceCompat("LoaderCapabilities.MarkReady");
                LoaderCapabilities.MarkReady(nativeUnitySupportActive, hostResult.HostedCount);
                Modding.MarkReady(nativeUnitySupportActive, hostResult.HostedCount, LoaderCapabilities.PathsSnapshot, StartupWarnings);
                RuntimeAttestation.Refresh();
                ServerPolicy.Refresh();
                GameRuntime.PublishLifecycle("LoaderReady");
                LogStartupMessage(LogLevel.Info, LegacyRedLoaderCompatibility.CreateCompatibilityStartedMessage(hostResult));
                Timings.RecordDuration("loader", "load-total", loadStopwatch.Elapsed, "ready");
                TraceCompat("LoaderState.Write success path");
                LoaderState.Write(nativeUnitySupportActive, hostResult, null, StartupWarnings);
                TraceCompat("RuntimeEntryPoint.LoadCore completed");
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
                try { ModRuntime.Shutdown(); }
                catch { /* Preserve the original Core startup failure. */ }
                try { AudioBanks.Shutdown(); }
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
            LoaderRuntime.Initialize(Version, LoaderCapabilities.CompatibilityMode);
            InputBindings.InitializeDefaults();
            LoaderConfig.Initialize();
            UiState.InitializeDefaults();
            MenuRegistry.InitializeDefaults();
            InputBindings.RegisterMenuProvider();
            FileIntegrity.InitializeDefaults();
            RuntimeAttestation.InitializeDefaults();
            ServerPolicy.InitializeDefaults();
            RuntimePreferences.Initialize();
            RuntimeCommands.Initialize();
            ChainloaderStatus.Initialize();
            BepInExPluginCompatibility.Initialize();
            ModHost.Initialize();
            ProtectedServerPolicy.Initialize();
            ProtectedRuntimeStatus.Initialize();
            LibraryInventoryStatus.Initialize();
            RedLoader.GlobalEvents.OnApplicationLateStart.Subscribe(CaptureGraphicsCapabilities, int.MaxValue);
            Assets.Initialize();
            AudioBanks.Initialize();
            ItemCatalog.Initialize();
            ItemRegistration.Initialize();
            Inventory.Initialize();
            Crafting.Initialize();
            Building.Initialize();
            Networking.Initialize();
            // First, so a screen's Closed handler runs on world exit and scene unload while its stage is still alive.
            ScreenHost.Initialize();
            PreviewStage.Initialize();
            PreviewStageProbe.Initialize();
            LoaderRuntime.MarkShimReady(
                CompatibilityShimIds.BepInExPlugins,
                "BepInEx plugins",
                "BepInEx",
                "transitional-il2cpp-bootstrap",
                "BepInEx IL2CPP loaded the Modstoke runtime bridge. This remains a transitional compatibility shim.");
            var il2CppHookState = IL2CPPHookState.Publish(Paths.PluginPath, Paths.GameRootPath);
            _harmonySupportState = HarmonySupport.Publish(
                Path.Combine(Paths.BepInExRootPath, "core"),
                il2CppHookState,
                IsTruthy(Environment.GetEnvironmentVariable(EnableSonsMenuHarmonyEnvVar)),
                IsTruthy(Environment.GetEnvironmentVariable(DisableSonsMenuHarmonyEnvVar)));
            foreach (var warning in _harmonySupportState.Warnings)
                AddStartupWarning(warning);

            Modding.PublishLifecycle("LoaderLoadStarted");
            GameRuntime.Initialize();
            InventoryEvents.Initialize();
            Assets.Refresh();
            ItemCatalog.Refresh();
            ItemRegistration.Refresh();
            Inventory.Refresh();
            Crafting.Refresh();
            Building.Refresh();
            Networking.Refresh();
        }

        private static void CaptureGraphicsCapabilities()
        {
            GraphicsCapabilities.Initialize();
            if (!GraphicsCapabilities.TryCapture(out var snapshot) &&
                snapshot.Status == GraphicsCapabilityStatus.Failed)
                AddStartupWarning(snapshot.Detail);
            RequestLoaderStateRefresh("graphics-capabilities");
        }

        private static void ReportStartupFailure(Exception error, TimeSpan elapsed)
        {
            try
            {
                // Publish failure before invoking diagnostic callbacks that could themselves throw.
                LoaderCapabilities.MarkFailed(error.Message);
                TraceCompat("RuntimeEntryPoint.LoadCore managed exception: " + error);
                Modding.MarkFailed(error.Message, LoaderCapabilities.PathsSnapshot, StartupWarnings);
                LoaderRuntime.MarkShimFailed(
                    CompatibilityShimIds.RedLoaderMods,
                    "RedLoader mods",
                    "RedLoader",
                    "startup",
                    error.Message);
                LoaderRuntime.MarkShimFailed(
                    CompatibilityShimIds.SonsSdk,
                    "SonsSdk APIs",
                    "SonsSdk",
                    "startup",
                    error.Message);
                RuntimeAttestation.Refresh();
                ServerPolicy.Refresh();
                GameRuntime.PublishLifecycle("LoaderFailed");
                Timings.RecordDuration("loader", "load-total", elapsed, "failed");
                LogSource.LogError("Modstoke Loader Core failed to start: " + error);
            }
            finally
            {
                LoaderState.WriteFailure(error.Message, StartupWarnings);
            }
        }

        // File trace for messages that cannot rely on LogSource: it runs before the logger exists and when log
        // delivery itself fails. The file is rewritten on each launch so it never grows across sessions.
        internal static void TraceCompat(string message)
        {
            lock (TraceSync)
            {
                if (_traceUnavailable)
                    return;

                try
                {
                    if (_traceWriter == null)
                    {
                        var root = !string.IsNullOrWhiteSpace(Paths.GameRootPath)
                            ? Paths.GameRootPath
                            : Path.GetDirectoryName(Paths.ExecutablePath);
                        var path = Path.Combine(root ?? Environment.CurrentDirectory, "openacai-redloader-compat-net11-preview.log");
                        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        _traceWriter = new StreamWriter(stream) { AutoFlush = true };
                    }

                    _traceWriter.WriteLine(DateTimeOffset.UtcNow.ToString("O") + " " + message);
                }
                catch
                {
                    // Tracing is best-effort and must never fail the caller.
                    _traceUnavailable = true;
                }
            }
        }

        private static void InstallHooks(string area, params Action<HarmonySupportSnapshot, Action<string>>[] installers)
        {
            TraceCompat("Installing " + area + " hooks");
            using (Timings.Measure("loader." + area, "harmony-install"))
            {
                foreach (var install in installers)
                    install(_harmonySupportState, AddStartupWarning);
            }
        }

        internal static bool IsTruthy(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        internal static ModInventoryState RefreshModInventory()
        {
            return LoaderState.RefreshModInventory();
        }

        internal static void RequestLoaderStateRefresh(string reason, bool refreshFileIntegrity = false)
        {
            LoaderState.RequestRefresh(reason, refreshFileIntegrity);
        }

        internal static void PumpLoaderStateRefresh(bool force = false)
        {
            LoaderState.PumpRefresh(StartupWarnings, force);
        }

        internal static void AddStartupWarning(string message)
        {
            StartupWarnings.Add(message);
            Modding.PublishStartupWarning(message);
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

            // InitializeRuntimeServices has already published the probe; Publish never returns null.
            var harmonySupport = _harmonySupportState;
            if (!harmonySupport.SonsMenuHarmonyEnabled)
            {
                AddStartupWarning("Sons menu Harmony patches were not installed: " + harmonySupport.Detail + " Set " + EnableSonsMenuHarmonyEnvVar + "=1 to force diagnostics or " + DisableSonsMenuHarmonyEnvVar + "=1 to keep them off.");
                return;
            }

            if (!harmonySupport.SafeForHarmonyPatches)
            {
                AddStartupWarning("Sons menu Harmony patches are forced on before the runtime passed the full safety probe: " + harmonySupport.Detail);
            }

            _sonsMenuHarmonyInstalled = true;
            _sonsMenuHarmony = new HarmonyLib.Harmony(PluginGuid + ".sons-menu");

            TryPatchSonsMenu("PauseMenu.Start", AccessTools.Method(typeof(PauseMenu), "Start"), nameof(SonsMenuPatches.AfterPauseMenuAvailable));
            TryPatchSonsMenu("PauseMenu.Open", AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.Open)), nameof(SonsMenuPatches.AfterPauseMenuAvailable));
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

