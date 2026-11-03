// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public loader capability snapshot for Modstoke Loader.

using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Modstoke.Loader;
using Modstoke.Sons;
using RedLoader.Utils;

namespace Modstoke.Loader
{
    public static class LoaderCapabilities
    {
        public const string ApiVersion = "1.0";
        public const string LoaderName = "Modstoke Loader";
        public const string CompatibilityMode = "openacai-endnight-runtime";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, string> PathSnapshotValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static bool IsReady { get; private set; }
        public static bool StartupFailed { get; private set; }
        public static bool NativeRedLoaderUnitySupportActive { get; private set; }
        public static int HostedRedLoaderModCount { get; private set; }
        public static string StartupError { get; private set; }

        public static IReadOnlyDictionary<string, string> PathsSnapshot
        {
            get
            {
                lock (SyncRoot)
                {
                    return new Dictionary<string, string>(PathSnapshotValues, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        internal static void MarkReady(bool nativeUnitySupportActive, int hostedModCount)
        {
            lock (SyncRoot)
            {
                IsReady = true;
                StartupFailed = false;
                NativeRedLoaderUnitySupportActive = nativeUnitySupportActive;
                HostedRedLoaderModCount = hostedModCount;
                StartupError = null;
                CapturePaths();
            }
        }

        internal static void MarkFailed(string startupError)
        {
            lock (SyncRoot)
            {
                IsReady = false;
                StartupFailed = true;
                NativeRedLoaderUnitySupportActive = false;
                HostedRedLoaderModCount = 0;
                StartupError = startupError;
                CapturePaths();
            }
        }

        internal static object CreateState()
        {
            bool isReady, startupFailed, nativeRedLoaderUnitySupportActive;
            int hostedRedLoaderModCount;
            string startupError;
            Dictionary<string, string> paths;
            // Only the owned fields are read under the lock; config and menu snapshots call mod providers.
            lock (SyncRoot)
            {
                isReady = IsReady;
                startupFailed = StartupFailed;
                nativeRedLoaderUnitySupportActive = NativeRedLoaderUnitySupportActive;
                hostedRedLoaderModCount = HostedRedLoaderModCount;
                startupError = StartupError;
                paths = new Dictionary<string, string>(PathSnapshotValues, StringComparer.OrdinalIgnoreCase);
            }

            return new
            {
                apiVersion = ApiVersion,
                loaderName = LoaderName,
                compatibilityMode = CompatibilityMode,
                isReady,
                startupFailed,
                exposesStableReflectionApi = true,
                antiCheatCompanionSafe = true,
                nativeRedLoaderUnitySupportActive,
                hostedRedLoaderModCount,
                startupError,
                compatibilityShims = LoaderRuntime.CompatibilityShimsSnapshot(),
                compatibilityDependencies = CompatibilityDependencies.CreateState(),
                input = InputBindings.CreateState(),
                config = LoaderConfig.CreateState(),
                ui = UiState.CreateState(),
                graphics = GraphicsCapabilities.Snapshot(),
                menu = MenuRegistry.CreateState(),
                bepInExPluginCompatibility = BepInExPluginCompatibility.State,
                nativeModHost = ModHost.State,
                nativeModRuntime = ModRuntime.State,
                fileIntegrity = FileIntegrity.CreateState(),
                runtimeAttestation = RuntimeAttestation.CreateState(),
                sonsEnvironment = GameRuntime.Environment,
                sonsAssets = Assets.State,
                sonsBuilding = Building.State,
                sonsCrafting = Crafting.State,
                sonsItemRegistration = ItemRegistration.State,
                sonsItems = ItemCatalog.Catalog,
                sonsInventory = Inventory.State,
                sonsNetworking = Networking.State,
                sonsSaveData = SaveData.State,
                sonsSaveLifecycle = SaveLifecycle.State,
                sonsSaveRegistry = SaveRegistry.State,
                paths
            };
        }

        private static void CapturePaths()
        {
            PathSnapshotValues.Clear();
            SetPath("gameRoot", () => Paths.GameRootPath);
            SetPath("gameExecutable", () => Paths.ExecutablePath);
            SetPath("modstokeRoot", () => LoaderPaths.RootDirectory);
            SetPath("modstokeRuntimeDirectory", () => LoaderPaths.RuntimeDirectory);
            SetPath("modstokeRuntimeCompatibilityDirectory", () => LoaderPaths.RuntimeCompatibilityDirectory);
            SetPath("modstokeModsDirectory", () => LoaderPaths.ModsDirectory);
            SetPath("modstokeUserDataDirectory", () => LoaderPaths.UserDataDirectory);
            // Pre-rename keys, kept for mods that look paths up by name.
            SetPath("openAcaiRoot", () => LoaderPaths.RootDirectory);
            SetPath("openAcaiRuntimeDirectory", () => LoaderPaths.RuntimeDirectory);
            SetPath("openAcaiRuntimeCompatibilityDirectory", () => LoaderPaths.RuntimeCompatibilityDirectory);
            SetPath("openAcaiModsDirectory", () => LoaderPaths.ModsDirectory);
            SetPath("openAcaiUserDataDirectory", () => LoaderPaths.UserDataDirectory);
            SetPath("bootstrapCompatibilityMirror", () => LoaderPaths.BootstrapMirrorDirectory);
            SetPath("loaderStateFile", () => LoaderPaths.LoaderStatePath);
            SetPath("loaderManifestFile", () => LoaderPaths.LoaderManifestPath);
            SetPath("loaderFileManifest", () => LoaderPaths.FileManifestPath);
            SetPath("bepinexRoot", () => Paths.BepInExRootPath);
            SetPath("bepinexPluginRoot", () => Paths.PluginPath);
            SetPath("bepinexInterop", () => Path.Combine(Paths.BepInExRootPath, "interop"));
            SetPath("bepinexUnityLibs", () => Path.Combine(Paths.BepInExRootPath, "unity-libs"));
            SetPath("redLoaderDataDirectory", () => LoaderEnvironment.LoaderDirectory);
            SetPath("redLoaderAssemblyDirectory", () => LoaderEnvironment.LoaderAssemblyDirectory);
            SetPath("modsDirectory", () => LoaderEnvironment.ModsDirectory);
            SetPath("libsDirectory", () => LoaderEnvironment.LibsDirectory);
            SetPath("userDataDirectory", () => LoaderEnvironment.UserDataDirectory);
            SetPath("blueprintsDirectory", () => LoaderEnvironment.BlueprintDirectory);
        }

        private static void SetPath(string key, Func<string> read)
        {
            try
            {
                var value = read();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    PathSnapshotValues[key] = value;
                }
            }
            catch
            {
            }
        }
    }
}
