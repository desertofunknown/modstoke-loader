// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using Endnight.Types;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using global::Sons;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class SettingsStartup
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SingletonAwake(nint receiver, nint methodInfo);

        private static readonly Dictionary<nint, Func<MonoBehaviour>> SceneManagers = new();
        private static readonly HashSet<nint> HostMenuSceneManagers = new();
        private static INativeDetour _singletonAwakeDetour;
        private static SingletonAwake _singletonAwakeCallback, _singletonAwakeOriginal;
        private static int _sceneManagerThread;
        private static bool _sceneManagerReuseEnabled;
        private static bool _sceneManagerFailureReported;

        private static void InstallSceneManagerReuse(Action<string> warning)
        {
            try
            {
                var entry = GetModuleHandle("GameAssembly.dll") + 0x152C9F0;
                AddSceneManager<FpsLimiter>(entry);
                AddSceneManager<global::Sons.Settings.GameSettingsManager>(entry, hostMenu: true);
                AddSceneManager<global::Sons.Quality.QualityManager>(entry);
                AddSceneManager<global::Sons.Input.InputEventsManager>(entry, hostMenu: true);
                AddSceneManager<global::Sons.Input.InputManager>(entry, hostMenu: true);
                AddSceneManager<global::Sons.Gui.ModalDialogManager>(entry);
                AddSceneManager<global::Sons.Gameplay.GameSetup.PlayerProfileManager>(entry);
                AddSceneManager<global::Sons.Save.SaveGameManager>(entry, hostMenu: true);
                AddSceneManager<global::Sons.Music.MusicManager>(entry);
                AddSceneManager<global::Sons.Multiplayer.MultiplayerCallbacks>(entry);
                AddSceneManager<global::Sons.Gameplay.GameSetup.GameSetupManager>(entry, hostMenu: true);
                AddSceneManager<global::Sons.Tests.TestsManager>(entry);
                AddSceneManager<global::Sons.Quality.DynamicResolutionScaler>(entry);
                AddSceneManager<global::Endnight.Physics.PhysicsSettingsUpdate>(entry);
                AddSceneManager<global::Sons.SteamTimelineExtended.SonsSteamTimelineManager>(entry);
                AddSceneManager<global::Sons.Save.GameState>(entry);
                if (Convert.ToHexString(SHA256.HashData(ReadMemory(entry, 1584))) !=
                    "F074C29C713AEF37495424DC2FEAF2831A9125F32B7A5D9762A49C91C2CE1E96")
                    throw new InvalidOperationException("Scene manager Awake code changed or is already hooked.");

                try
                {
                    AddSceneManager<global::Sons.Multiplayer.ProfanityFilteringManager>(entry, hostMenu: true);
                }
                catch (Exception error)
                {
                    warning?.Invoke("Profanity manager reuse yielded to native Awake: " + error.Message);
                }

                // All supported closed singleton types share this native body. Keep its
                // original receiver and generic MethodInfo intact for every other caller.
                _sceneManagerThread = Environment.CurrentManagedThreadId;
                _singletonAwakeCallback = OnSingletonAwake;
                _singletonAwakeDetour = INativeDetour.Create(entry, _singletonAwakeCallback);
                Volatile.Write(ref _singletonAwakeOriginal, _singletonAwakeDetour.GenerateTrampoline<SingletonAwake>());
                _singletonAwakeDetour.Apply();
                _sceneManagerReuseEnabled = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Scene manager reuse registered for the recognized game build.");
            }
            catch (Exception error)
            {
                _sceneManagerReuseEnabled = false;
                // Retain a possible applied detour and its trampoline; disabled callbacks forward.
                warning?.Invoke("Scene manager reuse was not installed: " + error.Message);
            }
        }

        private static void AddSceneManager<T>(nint entry, bool hostMenu = false) where T : MonoBehaviour
        {
            if (!typeof(SingletonBehaviour<T>).IsAssignableFrom(typeof(T)))
                throw new InvalidOperationException("Unsupported scene manager base: " + typeof(T).FullName);
            var method = AccessTools.DeclaredMethod(typeof(SingletonBehaviour<T>), nameof(SingletonBehaviour<T>.Awake), Type.EmptyTypes);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0
                || (nint)BitConverter.ToInt64(ReadMemory(info, IntPtr.Size), 0) != entry)
                throw new InvalidOperationException("Scene manager Awake entry changed: " + typeof(T).FullName);
            var nativeClass = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (nativeClass == 0)
                throw new InvalidOperationException("Scene manager class is unavailable: " + typeof(T).FullName);
            SceneManagers.Add(nativeClass, () => SingletonBehaviour<T>._instance);
            if (hostMenu)
                HostMenuSceneManagers.Add(nativeClass);
        }

        private static void OnSingletonAwake(nint receiver, nint methodInfo)
        {
            try
            {
                if (_sceneManagerReuseEnabled && receiver != 0
                    && _sceneManagerThread == Environment.CurrentManagedThreadId
                    && SceneManagers.TryGetValue(IL2CPP.il2cpp_object_get_class(receiver), out var getExisting))
                {
                    var existing = getExisting();
                    if (existing && existing.Pointer != receiver && existing.isActiveAndEnabled)
                    {
                        var duplicate = new MonoBehaviour(receiver);
                        var duplicateObject = duplicate.gameObject;
                        var scene = duplicateObject.scene;
                        // Options, SDK prefab extraction, and title reloads reuse persistent managers.
                        bool titleScene = scene.name == GameMenuBridge.TitleSceneName;
                        bool optionsScene = scene.name == global::Sons.Loading.SonsSceneManager.OptionsMenuSceneName;
                        bool hostScene = scene.name == "MultiplayerHostMenu"
                            && HostMenuSceneManagers.Contains(IL2CPP.il2cpp_object_get_class(receiver));
                        if ((titleScene || optionsScene || hostScene) && existing.gameObject.scene.handle != scene.handle)
                        {
                            // Native Awake already destroys this redundant object, but only at
                            // end of frame. Disable it first so OnEnable cannot register again.
                            duplicateObject.SetActive(false);
                            UnityEngine.Object.Destroy(duplicateObject);
                            return;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                if (!_sceneManagerFailureReported)
                {
                    _sceneManagerFailureReported = true;
                    try { RuntimeEntryPoint.LogSource?.LogWarning("Scene manager reuse yielded to native Awake: " + error.Message); }
                    catch { }
                }
            }
            Volatile.Read(ref _singletonAwakeOriginal)(receiver, methodInfo);
        }
    }
}
