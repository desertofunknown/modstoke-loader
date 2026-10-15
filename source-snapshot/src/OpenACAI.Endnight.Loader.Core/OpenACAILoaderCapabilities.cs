// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public loader capability snapshot for Modstoke Loader.

using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using OpenACAI.Loader;
using OpenACAI.Sons;
using RedLoader.Utils;

namespace OpenACAI.Endnight.Loader
{
    public static class OpenACAILoaderCapabilities
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
            lock (SyncRoot)
            {
                return new
                {
                    apiVersion = ApiVersion,
                    loaderName = LoaderName,
                    compatibilityMode = CompatibilityMode,
                    isReady = IsReady,
                    startupFailed = StartupFailed,
                    exposesStableReflectionApi = true,
                    antiCheatCompanionSafe = true,
                    nativeRedLoaderUnitySupportActive = NativeRedLoaderUnitySupportActive,
                    hostedRedLoaderModCount = HostedRedLoaderModCount,
                    startupError = StartupError,
                    compatibilityShims = OpenACAIRuntime.CompatibilityShimsSnapshot(),
                    compatibilityDependencies = OpenACAICompatibilityDependencies.CreateState(),
                    input = OpenACAIInput.CreateState(),
                    config = OpenACAIConfig.CreateState(),
                    ui = OpenACAIUi.CreateState(),
                    menu = OpenACAIMenu.CreateState(),
                    bepInExPluginCompatibility = OpenACAIBepInExPluginCompatibility.State,
                    nativeModHost = OpenACAINativeModHost.State,
                    nativeLifecycleRegistry = OpenACAINativeLifecycleRegistry.State,
                    nativeLifecycleDispatcher = OpenACAINativeLifecycleDispatcher.State,
                    nativeLifecycleInvocationBridge = OpenACAINativeLifecycleInvocationBridge.State,
                    nativeLifecycleInvocationAdapter = OpenACAINativeLifecycleInvocationAdapter.State,
                    nativeLifecycleInvocationTokenResolver = OpenACAINativeLifecycleInvocationTokenResolver.State,
                    nativeLifecycleInvocationJournal = OpenACAINativeLifecycleInvocationJournal.State,
                    nativeLifecycleInvocationScheduler = OpenACAINativeLifecycleInvocationScheduler.State,
                    nativeLifecycleInvocationExecutor = OpenACAINativeLifecycleInvocationExecutor.State,
                    fileIntegrity = OpenACAIFileIntegrity.CreateState(),
                    runtimeAttestation = OpenACAIRuntimeAttestation.CreateState(),
                    sonsEnvironment = OpenACAISons.Environment,
                    sonsAssets = OpenACAISonsAssets.State,
                    sonsBuilding = OpenACAISonsBuilding.State,
                    sonsCrafting = OpenACAISonsCrafting.State,
                    sonsItemRegistration = OpenACAISonsItemRegistration.State,
                    sonsItems = OpenACAISonsItems.Catalog,
                    sonsInventory = OpenACAISonsInventory.State,
                    sonsNetworking = OpenACAISonsNetworking.State,
                    sonsSaveData = OpenACAISonsSaveData.State,
                    sonsSaveLifecycle = OpenACAISonsSaveLifecycle.State,
                    sonsSaveRegistry = OpenACAISonsSaveRegistry.State,
                    paths = new Dictionary<string, string>(PathSnapshotValues, StringComparer.OrdinalIgnoreCase)
                };
            }
        }

        private static void CapturePaths()
        {
            PathSnapshotValues.Clear();
            SetPath("gameRoot", () => Paths.GameRootPath);
            SetPath("gameExecutable", () => Paths.ExecutablePath);
            SetPath("openAcaiRoot", () => OpenACAIPaths.RootDirectory);
            SetPath("openAcaiRuntimeDirectory", () => OpenACAIPaths.RuntimeDirectory);
            SetPath("openAcaiRuntimeCompatibilityDirectory", () => OpenACAIPaths.RuntimeCompatibilityDirectory);
            SetPath("openAcaiModsDirectory", () => OpenACAIPaths.ModsDirectory);
            SetPath("openAcaiUserDataDirectory", () => OpenACAIPaths.UserDataDirectory);
            SetPath("bootstrapCompatibilityMirror", () => OpenACAIPaths.BootstrapMirrorDirectory);
            SetPath("loaderStateFile", () => OpenACAIPaths.LoaderStatePath);
            SetPath("loaderManifestFile", () => OpenACAIPaths.LoaderManifestPath);
            SetPath("loaderFileManifest", () => OpenACAIPaths.FileManifestPath);
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
