// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons Of The Forest SDK surface. SonsSdk remains an upstream
// compatibility reference, but this API is intentionally implemented by
// Modstoke Loader so it can evolve independently.

using System;
using System.IO;
using BepInEx;
using OpenACAI.Loader;
using OpenACAI.Endnight.Loader;
using Sons.Gui;
using TheForest.Utils;
using UnityEngine;

namespace OpenACAI.Sons
{
    public static class OpenACAISons
    {
        public const string ApiVersion = "0.1";
        public const string StateServiceKey = "openacai.sons.state";
        public const string EnvironmentServiceKey = "openacai.sons.environment";

        private const string TitleSceneName = "SonsTitleScene";
        private const string LoadingSceneName = "SonsMainLoading";
        private const string GameSceneName = "SonsMain";
        private static readonly object SyncRoot = new object();
        private static OpenACAISonsState _state = OpenACAISonsState.CreateUnknown();
        private static OpenACAISonsEnvironmentSnapshot _environment = OpenACAISonsEnvironmentSnapshot.CreateUnknown();
        private static bool _initialized;
        private static bool _lastObservedWorldState;
        private static bool _hasObservedWorldState;
        private static bool _lastObservedPauseState;
        private static bool _lastObservedPauseKnown;
        private static bool _hasObservedPauseState;

        public static event Action<OpenACAISonsLifecycleEvent> Lifecycle;
        public static event Action<OpenACAISonsSceneEvent> SceneChanged;
        public static event Action<OpenACAISonsWorldEvent> WorldChanged;
        public static event Action<OpenACAISonsPauseMenuEvent> PauseMenuChanged;

        public static OpenACAISonsState State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static OpenACAISonsEnvironmentSnapshot Environment
        {
            get
            {
                lock (SyncRoot)
                {
                    return _environment;
                }
            }
        }

        public static bool IsTitleScene => string.Equals(State.SceneKind, OpenACAISonsSceneKind.Title, StringComparison.Ordinal);
        public static bool IsLoadingScene => string.Equals(State.SceneKind, OpenACAISonsSceneKind.Loading, StringComparison.Ordinal);
        public static bool IsGameScene => string.Equals(State.SceneKind, OpenACAISonsSceneKind.Game, StringComparison.Ordinal);
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
            OpenACAISonsSaveData.RegisterMenuProvider();
            OpenACAISonsSaveLifecycle.Initialize();
            OpenACAISonsSaveRegistry.Initialize();
            SetState(OpenACAISonsState.CreateUnknown());
            OpenACAISonsMenuBridgeState.Initialize();
            PublishLifecycle("OpenACAISonsInitialized");
        }

        internal static void RefreshEnvironment()
        {
            var snapshot = OpenACAISonsEnvironmentSnapshot.Create();
            lock (SyncRoot)
            {
                _environment = snapshot;
                OpenACAIModding.SetService(EnvironmentServiceKey, _environment);
            }

            OpenACAISonsSaveData.Refresh();
            OpenACAISonsAssets.Refresh();
        }

        internal static void PublishLifecycle(string name)
        {
            Action<OpenACAISonsLifecycleEvent> handlers;
            var timestamp = DateTimeOffset.UtcNow;
            var evt = new OpenACAISonsLifecycleEvent(name, timestamp);
            lock (SyncRoot)
            {
                _state = _state.WithLifecycle(name, timestamp);
                OpenACAIModding.SetService(StateServiceKey, _state);
                handlers = Lifecycle;
            }

            OpenACAIModding.Dispatch("OpenACAISons.Lifecycle." + name, handlers, evt);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("sons-lifecycle-" + name);
        }

        internal static void PublishScene(string phase, int buildIndex, string sceneName)
        {
            var kind = ClassifyScene(sceneName);
            var timestamp = DateTimeOffset.UtcNow;
            OpenACAISonsSceneEvent evt;
            Action<OpenACAISonsSceneEvent> handlers;

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
                    OpenACAIModding.SetService(StateServiceKey, _state);
                }

                evt = new OpenACAISonsSceneEvent(phase, buildIndex, sceneName, kind, timestamp);
                handlers = SceneChanged;
            }

            OpenACAIModding.Dispatch("OpenACAISons.SceneChanged", handlers, evt);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("sons-scene-" + phase);
        }

        internal static void Tick()
        {
            // Scene transitions do not reliably describe whether the player is in the world.
            ObserveWorldState();
            ObservePauseMenu();
        }

        internal static void PublishPauseMenu(string phase, bool known, bool open)
        {
            var timestamp = DateTimeOffset.UtcNow;
            OpenACAISonsPauseMenuEvent evt;
            Action<OpenACAISonsPauseMenuEvent> handlers;

            lock (SyncRoot)
            {
                _state = _state.WithPauseMenu(known, open, phase, timestamp);
                OpenACAIModding.SetService(StateServiceKey, _state);
                evt = new OpenACAISonsPauseMenuEvent(phase, known, open, timestamp);
                handlers = PauseMenuChanged;
            }

            _hasObservedPauseState = true;
            _lastObservedPauseState = open;
            _lastObservedPauseKnown = known;
            OpenACAIModding.Dispatch("OpenACAISons.PauseMenuChanged", handlers, evt);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("sons-pause-" + phase);
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

        private static void ObservePauseMenu()
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
                    open = menu.gameObject != null && menu.gameObject.activeInHierarchy;
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
            OpenACAISonsWorldEvent evt;
            Action<OpenACAISonsWorldEvent> handlers;

            lock (SyncRoot)
            {
                _state = _state.WithWorld(isInWorld, phase, timestamp);
                OpenACAIModding.SetService(StateServiceKey, _state);
                evt = new OpenACAISonsWorldEvent(phase, isInWorld, _state.SceneName, _state.SceneKind, timestamp);
                handlers = WorldChanged;
            }

            OpenACAIModding.Dispatch("OpenACAISons.WorldChanged", handlers, evt);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("sons-world-" + phase);
        }

        private static void SetState(OpenACAISonsState state)
        {
            lock (SyncRoot)
            {
                _state = state ?? OpenACAISonsState.CreateUnknown();
                OpenACAIModding.SetService(StateServiceKey, _state);
            }
        }

        private static string ClassifyScene(string sceneName)
        {
            if (string.Equals(sceneName, TitleSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return OpenACAISonsSceneKind.Title;
            }

            if (string.Equals(sceneName, LoadingSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return OpenACAISonsSceneKind.Loading;
            }

            if (string.Equals(sceneName, GameSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return OpenACAISonsSceneKind.Game;
            }

            return OpenACAISonsSceneKind.Other;
        }

        private static bool IsUnloadPhase(string phase)
        {
            return string.Equals(phase, "Unloaded", StringComparison.OrdinalIgnoreCase);
        }

        private static void RegisterMenuProvider()
        {
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK environment provider could not be registered.");
            }

            OpenACAISonsRuntimeStateStatus.Initialize();
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            RefreshEnvironment();
            var snapshot = Environment;
            return new OpenACAIMenuContentSnapshot(
                "Sons Environment",
                "OpenACAI-owned Sons runtime, game, and path environment state.",
                new[]
                {
                    CreateRuntimeSection(snapshot),
                    CreatePathSection(snapshot),
                    CreateCompatibilityPathSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateRuntimeSection(OpenACAISonsEnvironmentSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Runtime",
                new[]
                {
                    Row("Service", EnvironmentServiceKey, "READY", "Public Modstoke SDK environment service."),
                    Row("Loader", snapshot.LoaderVersion, "INFO", "Modstoke Loader version."),
                    Row(".NET Runtime", snapshot.DotnetRuntimeVersion, "INFO", "CoreCLR runtime visible to OpenACAI."),
                    Row("Unity", snapshot.UnityVersion, "INFO", "Unity runtime version reported by the game."),
                    Row("Product", Display(snapshot.UnityProductName), "INFO", Display(snapshot.UnityProductVersion)),
                    Row("Game Version", Display(snapshot.SonsGameVersion), string.IsNullOrWhiteSpace(snapshot.SonsGameVersion) ? "PENDING" : "INFO", "Sons version string when exposed by the game."),
                    Row("Dedicated Server", snapshot.IsDedicatedServer ? "Yes" : "No", snapshot.IsDedicatedServer ? "INFO" : "READY", "Executable classification.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreatePathSection(OpenACAISonsEnvironmentSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "OpenACAI Paths",
                new[]
                {
                    PathRow("Game Root", snapshot.GameRootDirectory, false),
                    PathRow("Game Executable", snapshot.GameExecutablePath, true),
                    PathRow("Unity Data", snapshot.UnityDataDirectory, false),
                    PathRow("Persistent Data", snapshot.PersistentDataDirectory, false),
                    PathRow("Game Saves", snapshot.SaveDataDirectory, false),
                    PathRow("OpenACAI Root", snapshot.OpenAcaiRootDirectory, false),
                    PathRow("OpenACAI Runtime", snapshot.OpenAcaiRuntimeDirectory, false),
                    PathRow("Modstoke Mods", snapshot.OpenAcaiModsDirectory, false),
                    PathRow("OpenACAI UserData", snapshot.OpenAcaiUserDataDirectory, false)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateCompatibilityPathSection(OpenACAISonsEnvironmentSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Compatibility Paths",
                new[]
                {
                    PathRow("Legacy Mods", snapshot.LegacyModsDirectory, false),
                    PathRow("Legacy Libs", snapshot.LegacyLibsDirectory, false),
                    PathRow("Legacy UserData", snapshot.LegacyUserDataDirectory, false),
                    PathRow("Legacy Blueprints", snapshot.LegacyBlueprintsDirectory, false),
                    PathRow("Streaming Assets", snapshot.StreamingAssetsDirectory, false),
                    PathRow("Unity Game Data", snapshot.UnityGameDataDirectory, false),
                    PathRow("Runtime Compatibility", snapshot.OpenAcaiRuntimeCompatibilityDirectory, false)
                });
        }

        private static OpenACAIMenuRowSnapshot PathRow(string label, string path, bool file)
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

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsEnvironment;
            public string DisplayName => "Sons Environment";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 718;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons environment paths and runtime state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISons.CreateContent();
            }
        }
    }

    public static class OpenACAISonsSceneKind
    {
        public const string Unknown = "Unknown";
        public const string Title = "Title";
        public const string Loading = "Loading";
        public const string Game = "Game";
        public const string Other = "Other";
    }

    public sealed class OpenACAISonsState
    {
        private OpenACAISonsState(
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

        internal static OpenACAISonsState CreateUnknown()
        {
            return new OpenACAISonsState(
                -1,
                null,
                OpenACAISonsSceneKind.Unknown,
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

        internal OpenACAISonsState WithLifecycle(string name, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsState(
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

        internal OpenACAISonsState WithScene(int buildIndex, string sceneName, string sceneKind, string phase, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsState(
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

        internal OpenACAISonsState WithWorld(bool isInWorld, string phase, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsState(
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

        internal OpenACAISonsState WithPauseMenu(bool known, bool open, string phase, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsState(
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

    public sealed class OpenACAISonsLifecycleEvent
    {
        internal OpenACAISonsLifecycleEvent(string name, DateTimeOffset timestampUtc)
        {
            Name = name;
            TimestampUtc = timestampUtc;
        }

        public string Name { get; }
        public DateTimeOffset TimestampUtc { get; }
    }

    public sealed class OpenACAISonsSceneEvent
    {
        internal OpenACAISonsSceneEvent(string phase, int buildIndex, string sceneName, string sceneKind, DateTimeOffset timestampUtc)
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

    public sealed class OpenACAISonsWorldEvent
    {
        internal OpenACAISonsWorldEvent(string phase, bool isInWorld, string sceneName, string sceneKind, DateTimeOffset timestampUtc)
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

    public sealed class OpenACAISonsPauseMenuEvent
    {
        internal OpenACAISonsPauseMenuEvent(string phase, bool known, bool open, DateTimeOffset timestampUtc)
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

    public sealed class OpenACAISonsEnvironmentSnapshot
    {
        private OpenACAISonsEnvironmentSnapshot(
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
            string openAcaiRootDirectory,
            string openAcaiRuntimeDirectory,
            string openAcaiRuntimeCompatibilityDirectory,
            string openAcaiModsDirectory,
            string openAcaiUserDataDirectory,
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
            OpenAcaiRootDirectory = openAcaiRootDirectory;
            OpenAcaiRuntimeDirectory = openAcaiRuntimeDirectory;
            OpenAcaiRuntimeCompatibilityDirectory = openAcaiRuntimeCompatibilityDirectory;
            OpenAcaiModsDirectory = openAcaiModsDirectory;
            OpenAcaiUserDataDirectory = openAcaiUserDataDirectory;
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
        public string OpenAcaiRootDirectory { get; }
        public string OpenAcaiRuntimeDirectory { get; }
        public string OpenAcaiRuntimeCompatibilityDirectory { get; }
        public string OpenAcaiModsDirectory { get; }
        public string OpenAcaiUserDataDirectory { get; }
        public string LegacyModsDirectory { get; }
        public string LegacyLibsDirectory { get; }
        public string LegacyUserDataDirectory { get; }
        public string LegacyBlueprintsDirectory { get; }

        internal static OpenACAISonsEnvironmentSnapshot Create()
        {
            EnsureOpenACAIDirectories();

            var gameRoot = SafeRead(() => Paths.GameRootPath);
            var executablePath = SafeRead(() => Paths.ExecutablePath);
            var executableName = Path.GetFileNameWithoutExtension(executablePath);
            var persistentDataDirectory = SafeRead(() => Application.persistentDataPath);
            var legacyModsDirectory = Combine(gameRoot, "Mods");

            return new OpenACAISonsEnvironmentSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                OpenACAIEndnightRuntime.Version,
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
                SafeRead(() => OpenACAIPaths.RootDirectory),
                SafeRead(() => OpenACAIPaths.RuntimeDirectory),
                SafeRead(() => OpenACAIPaths.RuntimeCompatibilityDirectory),
                SafeRead(() => OpenACAIPaths.ModsDirectory),
                SafeRead(() => OpenACAIPaths.UserDataDirectory),
                legacyModsDirectory,
                Combine(gameRoot, "Libs"),
                Combine(gameRoot, "UserData"),
                Combine(legacyModsDirectory, "Blueprints"));
        }

        internal static OpenACAISonsEnvironmentSnapshot CreateUnknown()
        {
            return new OpenACAISonsEnvironmentSnapshot(
                null,
                OpenACAIEndnightRuntime.Version,
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

        private static void EnsureOpenACAIDirectories()
        {
            SafeCreateDirectory(() => OpenACAIPaths.RootDirectory);
            SafeCreateDirectory(() => OpenACAIPaths.RuntimeDirectory);
            SafeCreateDirectory(() => OpenACAIPaths.RuntimeCompatibilityDirectory);
            SafeCreateDirectory(() => OpenACAIPaths.ModsDirectory);
            SafeCreateDirectory(() => OpenACAIPaths.UserDataDirectory);
        }

        private static string GetSonsGameVersion()
        {
            var versionType = Type.GetType("Sons.GameApplication.Version, Assembly-CSharp");
            var method = versionType?.GetMethod("GetVersionString", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return method?.Invoke(null, null) as string;
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
                if (!string.IsNullOrWhiteSpace(path))
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

