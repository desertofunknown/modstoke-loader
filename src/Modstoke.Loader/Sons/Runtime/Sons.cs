// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// SonsSdk remains an upstream compatibility reference, but this API is
// implemented by Modstoke Loader so it can evolve independently.

using System;
using System.IO;
using BepInEx;
using Modstoke.Loader;
using global::Sons.Gui;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static class GameRuntime
    {
        public const string ApiVersion = "0.1";
        public const string StateServiceKey = "openacai.sons.state";
        public const string EnvironmentServiceKey = "openacai.sons.environment";

        private const string TitleSceneName = "SonsTitleScene";
        private const string LoadingSceneName = "SonsMainLoading";
        private const string GameSceneName = "SonsMain";
        private static readonly object SyncRoot = new object();
        private static GameState _state = GameState.CreateUnknown();
        private static EnvironmentSnapshot _environment = EnvironmentSnapshot.CreateUnknown();
        private static MenuContentSnapshot _content;
        private static long _contentExpiresAt;
        private static bool _initialized;
        private static bool _lastObservedWorldState;
        private static bool _hasObservedWorldState;
        private static bool _lastObservedPauseState;
        private static bool _lastObservedPauseKnown;
        private static bool _hasObservedPauseState;

        public static event Action<global::Modstoke.Sons.LifecycleEvent> Lifecycle;
        public static event Action<global::Modstoke.Sons.SceneEvent> SceneChanged;
        public static event Action<WorldEvent> WorldChanged;
        public static event Action<PauseMenuEvent> PauseMenuChanged;

        public static GameState State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static EnvironmentSnapshot Environment
        {
            get
            {
                lock (SyncRoot)
                {
                    return _environment;
                }
            }
        }

        public static bool IsTitleScene => string.Equals(State.SceneKind, SceneKind.Title, StringComparison.Ordinal);
        public static bool IsLoadingScene => string.Equals(State.SceneKind, SceneKind.Loading, StringComparison.Ordinal);
        public static bool IsGameScene => string.Equals(State.SceneKind, SceneKind.Game, StringComparison.Ordinal);
        public static bool IsInWorld => State.IsInWorld;

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            RefreshEnvironment();
            RegisterMenuProvider();
            SaveData.RegisterMenuProvider();
            SaveLifecycle.Initialize();
            SaveRegistry.Initialize();
            MapLayers.Initialize();
            SetState(GameState.CreateUnknown());
            MenuBridgeState.Initialize();
            PublishLifecycle("OpenACAISonsInitialized");
        }

        internal static void RefreshEnvironment()
        {
            var snapshot = EnvironmentSnapshot.Create();
            lock (SyncRoot)
            {
                _environment = snapshot;
                Modding.SetService(EnvironmentServiceKey, _environment);
            }

            SaveData.Refresh();
            Assets.Refresh();
            _content = null;
        }

        internal static void PublishLifecycle(string name)
        {
            Action<global::Modstoke.Sons.LifecycleEvent> handlers;
            var timestamp = DateTimeOffset.UtcNow;
            var evt = new global::Modstoke.Sons.LifecycleEvent(name, timestamp);
            lock (SyncRoot)
            {
                _state = _state.WithLifecycle(name, timestamp);
                Modding.SetService(StateServiceKey, _state);
                handlers = Lifecycle;
            }

            Modding.Dispatch("OpenACAISons.Lifecycle." + name, handlers, evt);
            RuntimeEntryPoint.RequestLoaderStateRefresh("sons-lifecycle-" + name);
        }

        internal static void PublishScene(string phase, int buildIndex, string sceneName)
        {
            var kind = ClassifyScene(sceneName);
            var timestamp = DateTimeOffset.UtcNow;
            global::Modstoke.Sons.SceneEvent evt;
            Action<global::Modstoke.Sons.SceneEvent> handlers;

            lock (SyncRoot)
            {
                var sameScene = _state.BuildIndex == buildIndex
                    && string.Equals(_state.SceneName, sceneName, StringComparison.OrdinalIgnoreCase);
                // Delayed initialization of an older scene must not replace a newer load.
                var updateCurrentScene = string.Equals(phase, "Loaded", StringComparison.OrdinalIgnoreCase)
                    || (sameScene && !IsUnloadPhase(_state.ScenePhase));

                if (updateCurrentScene)
                {
                    _state = _state.WithScene(buildIndex, sceneName, kind, phase, timestamp);
                    Modding.SetService(StateServiceKey, _state);
                }

                evt = new global::Modstoke.Sons.SceneEvent(phase, buildIndex, sceneName, kind, timestamp);
                handlers = SceneChanged;
            }

            Modding.Dispatch("OpenACAISons.SceneChanged", handlers, evt);
            RuntimeEntryPoint.RequestLoaderStateRefresh("sons-scene-" + phase);
        }

        internal static void Tick()
        {
            try
            {
                Combat.StrictCombatEquipmentCleanupPump.Tick();
                Combat.StrictCombatImpactParticles.Tick();
            }
            finally
            {
                // A failing combat pump must not stall world and pause events. Scene
                // transitions do not reliably describe whether the player is in the world.
                ObserveWorldState();
                ObservePauseMenu();
            }
        }

        internal static void PublishPauseMenu(string phase, bool known, bool open)
        {
            var timestamp = DateTimeOffset.UtcNow;
            PauseMenuEvent evt;
            Action<PauseMenuEvent> handlers;

            lock (SyncRoot)
            {
                _state = _state.WithPauseMenu(known, open, phase, timestamp);
                Modding.SetService(StateServiceKey, _state);
                evt = new PauseMenuEvent(phase, known, open, timestamp);
                handlers = PauseMenuChanged;
            }

            _hasObservedPauseState = true;
            _lastObservedPauseState = open;
            _lastObservedPauseKnown = known;
            Modding.Dispatch("OpenACAISons.PauseMenuChanged", handlers, evt);
            RuntimeEntryPoint.RequestLoaderStateRefresh("sons-pause-" + phase);
        }

        private static void ObserveWorldState()
        {
            bool isInWorld;
            try
            {
                isInWorld = LocalPlayer.IsInWorld;
            }
            catch
            {
                return;
            }

            if (_hasObservedWorldState && _lastObservedWorldState == isInWorld)
            {
                return;
            }

            _hasObservedWorldState = true;
            _lastObservedWorldState = isInWorld;
            PublishWorldState(isInWorld ? "Entered" : "Exited", isInWorld);
        }

        internal static void ObservePauseMenu()
        {
            PauseMenu menu;
            try
            {
                menu = PauseMenu._instance;
            }
            catch
            {
                return;
            }

            var known = menu != null;
            var open = false;
            if (known)
            {
                try
                {
                    // The controller remains active while its menu panels are closed.
                    open = PauseMenu.IsActive;
                }
                catch
                {
                    open = false;
                }
            }

            if (_hasObservedPauseState && _lastObservedPauseState == open && _lastObservedPauseKnown == known)
            {
                return;
            }

            PublishPauseMenu(open ? "Opened" : "Closed", known, open);
        }

        private static void PublishWorldState(string phase, bool isInWorld)
        {
            var timestamp = DateTimeOffset.UtcNow;
            WorldEvent evt;
            Action<WorldEvent> handlers;

            lock (SyncRoot)
            {
                _state = _state.WithWorld(isInWorld, phase, timestamp);
                Modding.SetService(StateServiceKey, _state);
                evt = new WorldEvent(phase, isInWorld, _state.SceneName, _state.SceneKind, timestamp);
                handlers = WorldChanged;
            }

            Modding.Dispatch("OpenACAISons.WorldChanged", handlers, evt);
            RuntimeEntryPoint.RequestLoaderStateRefresh("sons-world-" + phase);
        }

        private static void SetState(GameState state)
        {
            lock (SyncRoot)
            {
                _state = state ?? GameState.CreateUnknown();
                Modding.SetService(StateServiceKey, _state);
            }
        }

        private static string ClassifyScene(string sceneName)
        {
            if (string.Equals(sceneName, TitleSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return SceneKind.Title;
            }

            if (string.Equals(sceneName, LoadingSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return SceneKind.Loading;
            }

            if (string.Equals(sceneName, GameSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return SceneKind.Game;
            }

            return SceneKind.Other;
        }

        private static bool IsUnloadPhase(string phase)
        {
            return string.Equals(phase, "Unloaded", StringComparison.OrdinalIgnoreCase);
        }

        private static void RegisterMenuProvider()
        {
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK environment provider could not be registered.");
            }

            RuntimeStateStatus.Initialize();
        }

        private static MenuContentSnapshot CreateContent()
        {
            // Menu pages poll this and building it touches the disk, so a page is reused for two seconds unless the environment is refreshed.
            var content = _content;
            if (content != null && System.Environment.TickCount64 < _contentExpiresAt)
            {
                return content;
            }

            RefreshEnvironment();
            var snapshot = Environment;
            content = new MenuContentSnapshot(
                "Sons Environment",
                "Sons runtime, game, and path environment state.",
                new[]
                {
                    CreateRuntimeSection(snapshot),
                    CreatePathSection(snapshot),
                    CreateCompatibilityPathSection(snapshot)
                });
            _content = content;
            _contentExpiresAt = System.Environment.TickCount64 + 2000;
            return content;
        }

        private static MenuSectionSnapshot CreateRuntimeSection(EnvironmentSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Runtime",
                new[]
                {
                    Row("Service", EnvironmentServiceKey, "READY", "Public Modstoke SDK environment service."),
                    Row("Loader", snapshot.LoaderVersion, "INFO", "Modstoke Loader version."),
                    Row(".NET Runtime", snapshot.DotnetRuntimeVersion, "INFO", "CoreCLR runtime visible to Modstoke."),
                    Row("Unity", snapshot.UnityVersion, "INFO", "Unity runtime version reported by the game."),
                    Row("Product", Display(snapshot.UnityProductName), "INFO", Display(snapshot.UnityProductVersion)),
                    Row("Game Version", Display(snapshot.SonsGameVersion), string.IsNullOrWhiteSpace(snapshot.SonsGameVersion) ? "PENDING" : "INFO", "Sons version string when exposed by the game."),
                    Row("Dedicated Server", snapshot.IsDedicatedServer ? "Yes" : "No", snapshot.IsDedicatedServer ? "INFO" : "READY", "Executable classification.")
                });
        }

        private static MenuSectionSnapshot CreatePathSection(EnvironmentSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Modstoke Paths",
                new[]
                {
                    PathRow("Game Root", snapshot.GameRootDirectory, false),
                    PathRow("Game Executable", snapshot.GameExecutablePath, true),
                    PathRow("Unity Data", snapshot.UnityDataDirectory, false),
                    PathRow("Persistent Data", snapshot.PersistentDataDirectory, false),
                    PathRow("Game Saves", snapshot.SaveDataDirectory, false),
                    PathRow("Modstoke Root", snapshot.ModstokeRootDirectory, false),
                    PathRow("Modstoke Runtime", snapshot.ModstokeRuntimeDirectory, false),
                    PathRow("Modstoke Mods", snapshot.ModstokeModsDirectory, false),
                    PathRow("Modstoke UserData", snapshot.ModstokeUserDataDirectory, false)
                });
        }

        private static MenuSectionSnapshot CreateCompatibilityPathSection(EnvironmentSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Compatibility Paths",
                new[]
                {
                    PathRow("Legacy Mods", snapshot.LegacyModsDirectory, false),
                    PathRow("Legacy Libs", snapshot.LegacyLibsDirectory, false),
                    PathRow("Legacy UserData", snapshot.LegacyUserDataDirectory, false),
                    PathRow("Legacy Blueprints", snapshot.LegacyBlueprintsDirectory, false),
                    PathRow("Streaming Assets", snapshot.StreamingAssetsDirectory, false),
                    PathRow("Unity Game Data", snapshot.UnityGameDataDirectory, false),
                    PathRow("Runtime Compatibility", snapshot.ModstokeRuntimeCompatibilityDirectory, false)
                });
        }

        private static MenuRowSnapshot PathRow(string label, string path, bool file)
        {
            var exists = false;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    exists = file ? File.Exists(path) : Directory.Exists(path);
                }
                catch
                {
                    exists = false;
                }
            }

            return Row(label, ShortPath(path), string.IsNullOrWhiteSpace(path) ? "PENDING" : exists ? "READY" : "CHECK", path);
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unavailable";
            }

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsEnvironment;
            public string DisplayName => "Sons Environment";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 718;
            public string Source => "Modstoke Loader";
            public string Description => "Show Sons environment paths and runtime state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return GameRuntime.CreateContent();
            }
        }
    }

    public static class SceneKind
    {
        public const string Unknown = "Unknown";
        public const string Title = "Title";
        public const string Loading = "Loading";
        public const string Game = "Game";
        public const string Other = "Other";
    }

    public sealed class GameState
    {
        private GameState(
            int buildIndex,
            string sceneName,
            string sceneKind,
            string scenePhase,
            bool isInWorld,
            string worldPhase,
            bool pauseMenuKnown,
            bool pauseMenuOpen,
            string pauseMenuPhase,
            string lastLifecycleName,
            int lifecycleEventCount,
            int sceneEventCount,
            int worldEventCount,
            int pauseMenuEventCount,
            DateTimeOffset timestampUtc)
        {
            BuildIndex = buildIndex;
            SceneName = sceneName;
            SceneKind = sceneKind;
            ScenePhase = scenePhase;
            IsInWorld = isInWorld;
            WorldPhase = worldPhase;
            PauseMenuKnown = pauseMenuKnown;
            PauseMenuOpen = pauseMenuOpen;
            PauseMenuPhase = pauseMenuPhase;
            LastLifecycleName = lastLifecycleName;
            LifecycleEventCount = lifecycleEventCount;
            SceneEventCount = sceneEventCount;
            WorldEventCount = worldEventCount;
            PauseMenuEventCount = pauseMenuEventCount;
            TimestampUtc = timestampUtc;
        }

        public int BuildIndex { get; }
        public string SceneName { get; }
        public string SceneKind { get; }
        public string ScenePhase { get; }
        public bool IsInWorld { get; }
        public string WorldPhase { get; }
        public bool PauseMenuKnown { get; }
        public bool PauseMenuOpen { get; }
        public string PauseMenuPhase { get; }
        public string LastLifecycleName { get; }
        public int LifecycleEventCount { get; }
        public int SceneEventCount { get; }
        public int WorldEventCount { get; }
        public int PauseMenuEventCount { get; }
        public DateTimeOffset TimestampUtc { get; }

        internal static GameState CreateUnknown()
        {
            return new GameState(
                -1,
                null,
                global::Modstoke.Sons.SceneKind.Unknown,
                "Unknown",
                false,
                "Unknown",
                false,
                false,
                "Unknown",
                null,
                0,
                0,
                0,
                0,
                DateTimeOffset.UtcNow);
        }

        internal GameState WithLifecycle(string name, DateTimeOffset timestampUtc)
        {
            return new GameState(
                BuildIndex,
                SceneName,
                SceneKind,
                ScenePhase,
                IsInWorld,
                WorldPhase,
                PauseMenuKnown,
                PauseMenuOpen,
                PauseMenuPhase,
                name,
                LifecycleEventCount + 1,
                SceneEventCount,
                WorldEventCount,
                PauseMenuEventCount,
                timestampUtc);
        }

        internal GameState WithScene(int buildIndex, string sceneName, string sceneKind, string phase, DateTimeOffset timestampUtc)
        {
            return new GameState(
                buildIndex,
                sceneName,
                sceneKind,
                phase,
                IsInWorld,
                WorldPhase,
                PauseMenuKnown,
                PauseMenuOpen,
                PauseMenuPhase,
                LastLifecycleName,
                LifecycleEventCount,
                SceneEventCount + 1,
                WorldEventCount,
                PauseMenuEventCount,
                timestampUtc);
        }

        internal GameState WithWorld(bool isInWorld, string phase, DateTimeOffset timestampUtc)
        {
            return new GameState(
                BuildIndex,
                SceneName,
                SceneKind,
                ScenePhase,
                isInWorld,
                phase,
                PauseMenuKnown,
                PauseMenuOpen,
                PauseMenuPhase,
                LastLifecycleName,
                LifecycleEventCount,
                SceneEventCount,
                WorldEventCount + 1,
                PauseMenuEventCount,
                timestampUtc);
        }

        internal GameState WithPauseMenu(bool known, bool open, string phase, DateTimeOffset timestampUtc)
        {
            return new GameState(
                BuildIndex,
                SceneName,
                SceneKind,
                ScenePhase,
                IsInWorld,
                WorldPhase,
                known,
                open,
                phase,
                LastLifecycleName,
                LifecycleEventCount,
                SceneEventCount,
                WorldEventCount,
                PauseMenuEventCount + 1,
                timestampUtc);
        }
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

    public sealed class SceneEvent
    {
        internal SceneEvent(string phase, int buildIndex, string sceneName, string sceneKind, DateTimeOffset timestampUtc)
        {
            Phase = phase;
            BuildIndex = buildIndex;
            SceneName = sceneName;
            SceneKind = sceneKind;
            TimestampUtc = timestampUtc;
        }

        public string Phase { get; }
        public int BuildIndex { get; }
        public string SceneName { get; }
        public string SceneKind { get; }
        public DateTimeOffset TimestampUtc { get; }
    }

    public sealed class WorldEvent
    {
        internal WorldEvent(string phase, bool isInWorld, string sceneName, string sceneKind, DateTimeOffset timestampUtc)
        {
            Phase = phase;
            IsInWorld = isInWorld;
            SceneName = sceneName;
            SceneKind = sceneKind;
            TimestampUtc = timestampUtc;
        }

        public string Phase { get; }
        public bool IsInWorld { get; }
        public string SceneName { get; }
        public string SceneKind { get; }
        public DateTimeOffset TimestampUtc { get; }
    }

    public sealed class PauseMenuEvent
    {
        internal PauseMenuEvent(string phase, bool known, bool open, DateTimeOffset timestampUtc)
        {
            Phase = phase;
            Known = known;
            Open = open;
            TimestampUtc = timestampUtc;
        }

        public string Phase { get; }
        public bool Known { get; }
        public bool Open { get; }
        public DateTimeOffset TimestampUtc { get; }
    }

    public sealed class EnvironmentSnapshot
    {
        private static bool _loaderDirectoriesCreated;
        private static string _sonsGameVersion;

        private EnvironmentSnapshot(
            string generatedUtc,
            string loaderVersion,
            string dotnetRuntimeVersion,
            string unityVersion,
            string unityProductName,
            string unityProductVersion,
            string sonsGameVersion,
            string gameRootDirectory,
            string gameExecutablePath,
            string gameExecutableName,
            bool isDedicatedServer,
            string unityGameDataDirectory,
            string unityDataDirectory,
            string streamingAssetsDirectory,
            string persistentDataDirectory,
            string saveDataDirectory,
            string modstokeRootDirectory,
            string modstokeRuntimeDirectory,
            string modstokeRuntimeCompatibilityDirectory,
            string modstokeModsDirectory,
            string modstokeUserDataDirectory,
            string legacyModsDirectory,
            string legacyLibsDirectory,
            string legacyUserDataDirectory,
            string legacyBlueprintsDirectory)
        {
            GeneratedUtc = generatedUtc;
            LoaderVersion = loaderVersion;
            DotnetRuntimeVersion = dotnetRuntimeVersion;
            UnityVersion = unityVersion;
            UnityProductName = unityProductName;
            UnityProductVersion = unityProductVersion;
            SonsGameVersion = sonsGameVersion;
            GameRootDirectory = gameRootDirectory;
            GameExecutablePath = gameExecutablePath;
            GameExecutableName = gameExecutableName;
            IsDedicatedServer = isDedicatedServer;
            UnityGameDataDirectory = unityGameDataDirectory;
            UnityDataDirectory = unityDataDirectory;
            StreamingAssetsDirectory = streamingAssetsDirectory;
            PersistentDataDirectory = persistentDataDirectory;
            SaveDataDirectory = saveDataDirectory;
            ModstokeRootDirectory = modstokeRootDirectory;
            ModstokeRuntimeDirectory = modstokeRuntimeDirectory;
            ModstokeRuntimeCompatibilityDirectory = modstokeRuntimeCompatibilityDirectory;
            ModstokeModsDirectory = modstokeModsDirectory;
            ModstokeUserDataDirectory = modstokeUserDataDirectory;
            LegacyModsDirectory = legacyModsDirectory;
            LegacyLibsDirectory = legacyLibsDirectory;
            LegacyUserDataDirectory = legacyUserDataDirectory;
            LegacyBlueprintsDirectory = legacyBlueprintsDirectory;
        }

        public string GeneratedUtc { get; }
        public string LoaderVersion { get; }
        public string DotnetRuntimeVersion { get; }
        public string UnityVersion { get; }
        public string UnityProductName { get; }
        public string UnityProductVersion { get; }
        public string SonsGameVersion { get; }
        public string GameRootDirectory { get; }
        public string GameExecutablePath { get; }
        public string GameExecutableName { get; }
        public bool IsDedicatedServer { get; }
        public string UnityGameDataDirectory { get; }
        public string UnityDataDirectory { get; }
        public string StreamingAssetsDirectory { get; }
        public string PersistentDataDirectory { get; }
        public string SaveDataDirectory { get; }
        public string ModstokeRootDirectory { get; }
        public string ModstokeRuntimeDirectory { get; }
        public string ModstokeRuntimeCompatibilityDirectory { get; }
        public string ModstokeModsDirectory { get; }
        public string ModstokeUserDataDirectory { get; }
        public string LegacyModsDirectory { get; }
        public string LegacyLibsDirectory { get; }
        public string LegacyUserDataDirectory { get; }
        public string LegacyBlueprintsDirectory { get; }

        [Obsolete("Use ModstokeRootDirectory.")]
        public string OpenAcaiRootDirectory => ModstokeRootDirectory;
        [Obsolete("Use ModstokeRuntimeDirectory.")]
        public string OpenAcaiRuntimeDirectory => ModstokeRuntimeDirectory;
        [Obsolete("Use ModstokeRuntimeCompatibilityDirectory.")]
        public string OpenAcaiRuntimeCompatibilityDirectory => ModstokeRuntimeCompatibilityDirectory;
        [Obsolete("Use ModstokeModsDirectory.")]
        public string OpenAcaiModsDirectory => ModstokeModsDirectory;
        [Obsolete("Use ModstokeUserDataDirectory.")]
        public string OpenAcaiUserDataDirectory => ModstokeUserDataDirectory;

        internal static EnvironmentSnapshot Create()
        {
            EnsureLoaderDirectories();

            var gameRoot = SafeRead(() => Paths.GameRootPath);
            var executablePath = SafeRead(() => Paths.ExecutablePath);
            var executableName = Path.GetFileNameWithoutExtension(executablePath);
            var persistentDataDirectory = SafeRead(() => Application.persistentDataPath);
            var legacyModsDirectory = Combine(gameRoot, "Mods");

            return new EnvironmentSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                RuntimeEntryPoint.Version,
                global::System.Environment.Version.ToString(),
                SafeRead(() => Application.unityVersion),
                SafeRead(() => Application.productName),
                SafeRead(() => Application.version),
                SafeRead(GetSonsGameVersion),
                gameRoot,
                executablePath,
                executableName,
                IsDedicatedServerExecutable(executablePath),
                Combine(gameRoot, executableName + "_Data"),
                SafeRead(() => Application.dataPath),
                SafeRead(() => Application.streamingAssetsPath),
                persistentDataDirectory,
                Combine(persistentDataDirectory, "Saves"),
                SafeRead(() => LoaderPaths.RootDirectory),
                SafeRead(() => LoaderPaths.RuntimeDirectory),
                SafeRead(() => LoaderPaths.RuntimeCompatibilityDirectory),
                SafeRead(() => LoaderPaths.ModsDirectory),
                SafeRead(() => LoaderPaths.UserDataDirectory),
                legacyModsDirectory,
                Combine(gameRoot, "Libs"),
                Combine(gameRoot, "UserData"),
                Combine(legacyModsDirectory, "Blueprints"));
        }

        internal static EnvironmentSnapshot CreateUnknown()
        {
            return new EnvironmentSnapshot(
                null,
                RuntimeEntryPoint.Version,
                global::System.Environment.Version.ToString(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        // Environment refreshes are frequent; the loader roots and game version are fixed per process.
        private static void EnsureLoaderDirectories()
        {
            if (_loaderDirectoriesCreated)
            {
                return;
            }

            _loaderDirectoriesCreated = true;
            SafeCreateDirectory(() => LoaderPaths.RootDirectory);
            SafeCreateDirectory(() => LoaderPaths.RuntimeDirectory);
            SafeCreateDirectory(() => LoaderPaths.RuntimeCompatibilityDirectory);
            SafeCreateDirectory(() => LoaderPaths.ModsDirectory);
            SafeCreateDirectory(() => LoaderPaths.UserDataDirectory);
        }

        private static string GetSonsGameVersion()
        {
            if (_sonsGameVersion != null)
            {
                return _sonsGameVersion;
            }

            var versionType = Type.GetType("Sons.GameApplication.Version, Assembly-CSharp");
            var method = versionType?.GetMethod("GetVersionString", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return _sonsGameVersion = method?.Invoke(null, null) as string;
        }

        private static bool IsDedicatedServerExecutable(string executablePath)
        {
            var name = Path.GetFileNameWithoutExtension(executablePath);
            return !string.IsNullOrWhiteSpace(name)
                && (name.EndsWith("DS", StringComparison.OrdinalIgnoreCase)
                    || name.IndexOf("DedicatedServer", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Combine(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
                ? null
                : Path.Combine(left, right);
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

        private static void SafeCreateDirectory(Func<string> readPath)
        {
            try
            {
                var path = readPath();
                // A legacy OpenACAI folder is still read from, but never extended.
                if (!string.IsNullOrWhiteSpace(path) && !LoaderPaths.IsUnderLegacyRoot(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch
            {
            }
        }
    }
}

