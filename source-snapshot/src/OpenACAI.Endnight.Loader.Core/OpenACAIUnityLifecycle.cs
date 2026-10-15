// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Unity lifecycle and scene event bridge.

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using OpenACAI.Loader;
using OpenACAI.Sons;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIUnityLifecycle
    {
        private static readonly Queue<SceneInitEvent> SceneInitQueue = new Queue<SceneInitEvent>();
        private static readonly Dictionary<string, int> LifecycleErrorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static bool _sceneLoadObserved;
        private static bool _sceneHooksInstalled;

        internal static void InstallSceneHooks(Action<string> addStartupWarning)
        {
            if (_sceneHooksInstalled)
            {
                return;
            }

            _sceneHooksInstalled = true;

            try
            {
                SceneManager.sceneLoaded = ReferenceEquals(SceneManager.sceneLoaded, null)
                    ? new Action<Scene, LoadSceneMode>(OnSceneLoaded)
                    : Il2CppSystem.Delegate.Combine(SceneManager.sceneLoaded, (UnityAction<Scene, LoadSceneMode>)new Action<Scene, LoadSceneMode>(OnSceneLoaded)).Cast<UnityAction<Scene, LoadSceneMode>>();
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke("Scene loaded hook failed: " + ex.Message);
            }

            try
            {
                SceneManager.sceneUnloaded = ReferenceEquals(SceneManager.sceneUnloaded, null)
                    ? new Action<Scene>(OnSceneUnloaded)
                    : Il2CppSystem.Delegate.Combine(SceneManager.sceneUnloaded, (UnityAction<Scene>)new Action<Scene>(OnSceneUnloaded)).Cast<UnityAction<Scene>>();
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke("Scene unloaded hook failed: " + ex.Message);
            }
        }

        internal static void QueueInitialSceneIfAvailable()
        {
            if (_sceneLoadObserved)
            {
                return;
            }

            try
            {
                var scene = SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded && !string.IsNullOrEmpty(scene.name))
                {
                    OnSceneLoaded(scene, LoadSceneMode.Single);
                }
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.LogSource.LogWarning("Initial scene probe failed: " + ex.Message);
            }
        }

        internal static void PumpSceneInitialized()
        {
            if (SceneInitQueue.Count == 0)
            {
                return;
            }

            var frame = Time.frameCount;
            // Bound processing even when callbacks add or remove queued scenes.
            for (var remaining = SceneInitQueue.Count; remaining > 0 && SceneInitQueue.Count > 0; remaining--)
            {
                var scene = SceneInitQueue.Dequeue();
                InvokeLifecycle("SceneInitialized", () => InitializeScene(scene, frame));
            }
        }

        private static void InitializeScene(SceneInitEvent scene, int frame)
        {
            if (!scene.Scene.IsValid() || !scene.Scene.isLoaded)
            {
                return;
            }

            if (scene.LoadedFrame == frame)
            {
                SceneInitQueue.Enqueue(scene);
                return;
            }

            InvokeLifecycle("SceneInitialized.RedLoader", () => OpenACAILegacyLifecycleBridge.PublishSceneInitialized(scene.BuildIndex, scene.Name));
            InvokeLifecycle("SceneInitialized.Modding", () => OpenACAIModding.PublishScene("initialized", scene.BuildIndex, scene.Name));
            InvokeLifecycle("SceneInitialized.Sons", () => OpenACAISons.PublishScene("Initialized", scene.BuildIndex, scene.Name));
        }

        internal static void InvokeLifecycle(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                var count = 1;
                if (LifecycleErrorCounts.TryGetValue(name, out var existing))
                {
                    count = existing + 1;
                }

                LifecycleErrorCounts[name] = count;
                if (count == 1 || count % 300 == 0)
                {
                    try
                    {
                        OpenACAIEndnightRuntime.LogSource?.LogWarning(name + " failed" + (count == 1 ? "" : " " + count + " times") + ": " + ex.Message);
                    }
                    catch (Exception loggingError)
                    {
                        OpenACAIEndnightRuntime.TraceCompat("Lifecycle failure logging failed for " + name + ": " + loggingError.GetType().FullName);
                    }
                }
            }
        }

        internal static void CreateDriver()
        {
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<OpenACAIRuntimeDriver>();
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.LogSource.LogWarning("Driver type registration warning: " + ex.Message);
            }

            var host = new GameObject("OpenACAI Endnight Loader Core");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<OpenACAIRuntimeDriver>();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            InvokeLifecycle("SceneLoaded", () => PublishSceneLoaded(scene));
        }

        private static void PublishSceneLoaded(Scene scene)
        {
            if (!scene.IsValid())
            {
                return;
            }

            // The Start-time probe is only a fallback when no load event reached us.
            var pending = new SceneInitEvent(scene);
            _sceneLoadObserved = true;
            if (SceneInitQueue.Count >= 64)
            {
                SceneInitQueue.Dequeue();
                InvokeLifecycle("SceneInitQueueOverflow", () => OpenACAIEndnightRuntime.LogSource?.LogWarning("Scene initialization queue reached 64 entries; dropping the oldest scene event."));
            }

            SceneInitQueue.Enqueue(pending);
            InvokeLifecycle("SceneLoaded.MenuBridge", () => OpenAcaiSonsMenuBridge.OnSceneLoaded(pending.Name));
            InvokeLifecycle("SceneLoaded.Modding", () => OpenACAIModding.PublishScene("loaded", pending.BuildIndex, pending.Name));
            InvokeLifecycle("SceneLoaded.Sons", () => OpenACAISons.PublishScene("Loaded", pending.BuildIndex, pending.Name));
            InvokeLifecycle("SceneLoaded.RedLoader", () => OpenACAILegacyLifecycleBridge.PublishSceneLoaded(pending.BuildIndex, pending.Name));
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            InvokeLifecycle("SceneUnloaded", () => PublishSceneUnloaded(scene));
        }

        private static void PublishSceneUnloaded(Scene scene)
        {
            var handle = scene.handle;
            for (var remaining = SceneInitQueue.Count; remaining > 0; remaining--)
            {
                var pending = SceneInitQueue.Dequeue();
                if (pending.Handle != handle)
                {
                    SceneInitQueue.Enqueue(pending);
                }
            }

            if (!scene.IsValid())
            {
                return;
            }

            var buildIndex = scene.buildIndex;
            var name = scene.name;
            InvokeLifecycle("SceneUnloaded.RedLoader", () => OpenACAILegacyLifecycleBridge.PublishSceneUnloaded(buildIndex, name));
            InvokeLifecycle("SceneUnloaded.Modding", () => OpenACAIModding.PublishScene("unloaded", buildIndex, name));
            InvokeLifecycle("SceneUnloaded.Sons", () => OpenACAISons.PublishScene("Unloaded", buildIndex, name));
        }

        private sealed class SceneInitEvent
        {
            internal SceneInitEvent(Scene scene)
            {
                Scene = scene;
                Handle = scene.handle;
                BuildIndex = scene.buildIndex;
                Name = scene.name;
                LoadedFrame = Time.frameCount;
            }

            internal Scene Scene { get; }
            internal int Handle { get; }
            internal int BuildIndex { get; }
            internal string Name { get; }
            internal int LoadedFrame { get; }
        }
    }
}
