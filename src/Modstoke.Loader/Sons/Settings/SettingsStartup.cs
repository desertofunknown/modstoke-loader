// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons;
using global::Sons.Loading;
using global::Sons.Settings;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using NativeCollections = Il2CppSystem.Collections.Generic;

namespace Modstoke.Loader
{
    internal static partial class SettingsStartup
    {
        private const string TargetFrameRateKey = "Display.TargetFrameRate";
        private const float TargetFrameRateDefault = 240f;
        private static readonly Dictionary<nint, FpsSubscription> FpsSubscriptions = new();
        private static int _installationAttempted;
        private static bool _enabled;
        private static GameSettingsManager _readyManager;

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            // A dedicated server has no frame limiter or settings menu to correct.
            if (GameBuild.IsDedicatedServer)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Settings startup correction is unavailable: Harmony support is not ready.");
                return;
            }

            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-settings-startup");
            var operation = "validating the native settings methods";
            try
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                    || !GameBuild.IsRecognized)
                    throw new NotSupportedException("The game build or platform is not recognized.");
                // Take ownership before native subscriptions or settings initialization have run.
                if (FpsLimiter.TryGetInstance(out _) || GameSettingsManager.TryGetInstance(out _))
                    throw new InvalidOperationException("Settings startup correction must be installed before the first scene awakens.");

                var enable = QualifyMethod(typeof(FpsLimiter), nameof(FpsLimiter.OnEnable), 0x2D2D320, 512,
                    "0430A62AF36B0219BAFA9F5FF04B68DF90C62219CED1A6886D875BE650504EB5");
                var disable = QualifyMethod(typeof(FpsLimiter), nameof(FpsLimiter.OnDisable), 0x2D2D900, 432,
                    "579A0B8F41463EACACF12AF0D67F9C3F08E2D39C39D9B51A15BEA2AFCC387003");
                var ready = QualifyMethod(typeof(GameSettingsManager), nameof(GameSettingsManager.PostAwake), 0x2EB84F0, 16,
                    "3203F47AD5D3A38261993E6885746F9CA293BC79659C49F8D56518DC133E9E69");
                var initialize = QualifyMethod(typeof(LoadScene), nameof(LoadScene.Initialize), 0x2E16590, 1232,
                    "10EFF604CB32225D3D8ACF6684902E1C8EAC251D9C5EC9DC0C6692A6B994B73C");

                operation = "patching FpsLimiter.OnDisable";
                harmony.Patch(disable, prefix: new HarmonyMethod(typeof(SettingsStartup), nameof(BeforeFpsDisable)));
                operation = "patching GameSettingsManager.PostAwake";
                harmony.Patch(ready, postfix: new HarmonyMethod(typeof(SettingsStartup), nameof(AfterSettingsAwake)));
                operation = "patching LoadScene.Initialize";
                harmony.Patch(initialize, prefix: new HarmonyMethod(typeof(SettingsStartup), nameof(BeforeLoadInitialize)));
                operation = "patching FpsLimiter.OnEnable";
                harmony.Patch(enable, prefix: new HarmonyMethod(typeof(SettingsStartup), nameof(BeforeFpsEnable)));
                _enabled = true;
                operation = "installing the scene, UI and audio startup corrections";
                InstallSceneManagerReuse(warning);
                InstallUiPrefabPresentation(warning);
                InstallAudioVoiceCountIdempotence(warning);
                RuntimeEntryPoint.LogSource?.LogInfo("Settings startup correction registered for the recognized game build.");
            }
            catch (Exception error)
            {
                _enabled = false;
                try
                {
                    warning?.Invoke("Settings startup correction failed while " + operation + ": " + error);
                }
                finally
                {
                    try
                    {
                        harmony.UnpatchSelf();
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException(
                            "Settings startup correction failed while " + operation +
                            " and its patches could not be fully removed.", error, rollbackError);
                    }
                }
            }
        }

        private static bool SettingsReady()
        {
            return _readyManager && GameSettingsManager.TryGetInstance(out var manager)
                && manager.Pointer == _readyManager.Pointer && manager._gameSettings;
        }

        private static void AfterSettingsAwake(GameSettingsManager __instance, bool __runOriginal)
        {
            if (!_enabled || !__runOriginal || !__instance || !__instance._gameSettings
                || !GameSettingsManager.TryGetInstance(out var manager) || manager.Pointer != __instance.Pointer)
                return;

            // PostAwake has returned from the game's synchronous persisted-settings load.
            _readyManager = manager;
            foreach (var subscription in FpsSubscriptions.Values)
                subscription.RefreshSettings();
        }

        private static bool BeforeFpsEnable(FpsLimiter __instance, bool __runOriginal)
        {
            if (!_enabled || !__runOriginal || !__instance)
                return true;
            if (FpsLimiter.TryGetInstance(out var active) && active && active.Pointer != __instance.Pointer)
                return false;
            if (FpsSubscriptions.ContainsKey(__instance.Pointer))
                return false;

            var subscription = new FpsSubscription(__instance);
            FpsSubscriptions.Add(__instance.Pointer, subscription);
            __instance._wasInitialized = true;
            SceneManager.add_sceneLoaded(subscription.SceneLoaded);
            SceneManager.add_sceneUnloaded(subscription.SceneUnloaded);
            subscription.RefreshSettings();
            if (FpsLimiter._unlimitedGlobalOverride)
                __instance.FpsUnlimited();
            else
                __instance.FpsLimited(__instance._limitedFps);
            return false;
        }

        private static bool BeforeFpsDisable(FpsLimiter __instance, bool __runOriginal)
        {
            if (_enabled && __runOriginal && __instance
                && FpsLimiter.TryGetInstance(out var active) && active && active.Pointer != __instance.Pointer
                && !FpsSubscriptions.ContainsKey(__instance.Pointer))
                return false;
            if (!_enabled || !__runOriginal || __instance is null
                || !FpsSubscriptions.Remove(__instance.Pointer, out var subscription))
                return true;

            SceneManager.remove_sceneLoaded(subscription.SceneLoaded);
            SceneManager.remove_sceneUnloaded(subscription.SceneUnloaded);
            if (subscription.SettingsRegistered)
                GameSettingsManager.UnregisterSettingChangeCallback(TargetFrameRateKey, subscription.TargetChanged);
            if (__instance)
            {
                __instance._wasInitialized = false;
                __instance.FpsDefault();
            }
            return false;
        }

        private sealed class FpsSubscription
        {
            internal readonly FpsLimiter Instance;
            internal readonly UnityAction<Scene, LoadSceneMode> SceneLoaded;
            internal readonly UnityAction<Scene> SceneUnloaded;
            internal readonly SettingCallbacks.FloatAction TargetChanged;
            internal bool SettingsRegistered;

            internal FpsSubscription(FpsLimiter instance)
            {
                Instance = instance;
                SceneLoaded = new Action<Scene, LoadSceneMode>(OnSceneLoaded);
                SceneUnloaded = new Action<Scene>(_ => FpsLimiter.RefreshMainSceneLoaded());
                TargetChanged = new Action<float>(OnTargetChanged);
            }

            private void OnTargetChanged(float value)
            {
                if (Instance)
                    Instance.UpdateTargetFpsValuesFromSettings(value);
            }

            private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                RefreshSettings();
                FpsLimiter.RefreshMainSceneLoaded();
            }

            internal void RefreshSettings()
            {
                if (!Instance || !Instance.isActiveAndEnabled || !SettingsReady())
                    return;
                if (SettingsRegistered)
                {
                    OnTargetChanged(GameSettingsManager.GetSetting(TargetFrameRateKey, TargetFrameRateDefault));
                    return;
                }

                GameSettingsManager.RegisterSettingChangeCallbackAndRun(TargetFrameRateKey, TargetChanged, TargetFrameRateDefault);
                SettingsRegistered = true;
            }
        }

        private static bool BeforeLoadInitialize(LoadScene __instance, bool __runOriginal)
        {
            if (!_enabled || !__runOriginal || !__instance || __instance._initialized
                || GameSettingsManager.TryGetInstance(out _) || __instance._isMainSceneLoad
                || __instance._sceneListSettingsKey != string.Empty || __instance._mode != LoadSceneMode.Single)
                return true;

            var scene = __instance.gameObject.scene;
            var sets = __instance._sceneSets;
            if (scene.path != "Assets/Sons/Scenes/Splash.unity" || sets == null || sets.Count != 1)
                return true;
            var selected = sets[0];
            if (selected == null || selected._id != "Default" || !selected._sceneList)
                return true;
            var names = selected._sceneList._sceneNames;
            if (names == null || names.Count != 1 || names[0] != "SonsTitleScene")
                return true;

            // This exact prerequisite scene owns the settings manager. It has no settings key;
            // settings-based selection/removal starts only after that scene has loaded.
            LoadScene._isLoading = true;
            LoadScene._isMainSceneLoadComplete = false;
            __instance.UpdateProgressInfo("Loading Scenes");
            __instance._activeSceneList = selected;
            var sceneNames = new NativeCollections.List<string>();
            sceneNames.Add(names[0]);
            __instance._sceneNamesCache = sceneNames;
            __instance.DisableObjects();
            __instance._startTime = __instance.CurrentTime;
            scene = __instance.gameObject.scene;
            __instance._thisScene = scene;
            var roots = new NativeCollections.List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                roots.Add(root);
            roots.Remove(__instance.gameObject);
            __instance._objectsToUnload = roots;
            UnityEngine.Object.DontDestroyOnLoad(__instance.gameObject);
            __instance._initialized = true;
            return false;
        }
    }
}
