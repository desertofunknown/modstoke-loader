// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Unity lifecycle and scene event bridge.

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Modstoke.Loader;
using Modstoke.Sons;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Modstoke.Loader
{
    internal static class UnityLifecycle
    {
        private static readonly Queue<SceneInitEvent> SceneInitQueue = new Queue<SceneInitEvent>();
        private static readonly Dictionary<string, int> LifecycleErrorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static bool _sceneLoadObserved;
        private static bool _sceneLoadedHookInstalled;
        private static bool _sceneUnloadedHookInstalled;
        private static RuntimeDriver _driver;

        internal static void InstallSceneHooks(Action<string> addStartupWarning)
        {
            if (!_sceneLoadedHookInstalled)
            {
                try
                {
                    SceneManager.sceneLoaded = ReferenceEquals(SceneManager.sceneLoaded, null)
                        ? new Action<Scene, LoadSceneMode>(OnSceneLoaded)
                        : Il2CppSystem.Delegate.Combine(SceneManager.sceneLoaded, (UnityAction<Scene, LoadSceneMode>)new Action<Scene, LoadSceneMode>(OnSceneLoaded)).Cast<UnityAction<Scene, LoadSceneMode>>();
                    _sceneLoadedHookInstalled = true;
                }
                catch (Exception ex)
                {
                    InvokeLifecycle("SceneLoadedHook.Warning", () => addStartupWarning?.Invoke("Scene loaded hook failed: " + ex.Message));
                }
            }

            if (!_sceneUnloadedHookInstalled)
            {
                try
                {
                    SceneManager.sceneUnloaded = ReferenceEquals(SceneManager.sceneUnloaded, null)
                        ? new Action<Scene>(OnSceneUnloaded)
                        : Il2CppSystem.Delegate.Combine(SceneManager.sceneUnloaded, (UnityAction<Scene>)new Action<Scene>(OnSceneUnloaded)).Cast<UnityAction<Scene>>();
                    _sceneUnloadedHookInstalled = true;
                }
                catch (Exception ex)
                {
                    InvokeLifecycle("SceneUnloadedHook.Warning", () => addStartupWarning?.Invoke("Scene unloaded hook failed: " + ex.Message));
                }
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
                RuntimeEntryPoint.LogSource.LogWarning("Initial scene probe failed: " + ex.Message);
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

            InvokeLifecycle("SceneInitialized.RedLoader", () => LegacyLifecycleBridge.PublishSceneInitialized(scene.BuildIndex, scene.Name));
            InvokeLifecycle("SceneInitialized.Modding", () => Modding.PublishScene("initialized", scene.BuildIndex, scene.Name));
            InvokeLifecycle("SceneInitialized.Sons", () => GameRuntime.PublishScene("Initialized", scene.BuildIndex, scene.Name));
        }

        internal static void InvokeLifecycle(string name, Action action)
        {
            try
            {
                using var capture = PerformanceCapture.MeasureCallback(name);
                action();
            }
            catch (Exception ex)
            {
                LifecycleErrorCounts.TryGetValue(name, out var count);
                count++;
                LifecycleErrorCounts[name] = count;
                if (count == 1 || count % 300 == 0)
                {
                    try
                    {
                        // The first report carries the stack trace; repeats stay short.
                        RuntimeEntryPoint.LogSource?.LogWarning(name + " failed" + (count == 1
                            ? ": " + ex
                            : " " + count + " times: " + ex.GetType().Name + ": " + ex.Message));
                    }
                    catch (Exception loggingError)
                    {
                        RuntimeEntryPoint.TraceCompat("Lifecycle failure logging failed for " + name + ": " + loggingError.GetType().FullName);
                    }
                }
            }
        }

        internal static void CreateDriver()
        {
            if (_driver)
                return;

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<RuntimeDriver>();
            }
            catch (Exception ex)
            {
                RuntimeEntryPoint.LogSource.LogWarning("Driver type registration warning: " + ex.Message);
            }

            var host = new GameObject("OpenACAI Endnight Loader Core");
            try
            {
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
                _driver = host.AddComponent<RuntimeDriver>();
                if (!_driver)
                    throw new InvalidOperationException("The loader runtime driver could not be attached.");
            }
            catch
            {
                UnityEngine.Object.Destroy(host);
                throw;
            }
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
                InvokeLifecycle("SceneInitQueueOverflow", () => RuntimeEntryPoint.LogSource?.LogWarning("Scene initialization queue reached 64 entries; dropping the oldest scene event."));
            }

            SceneInitQueue.Enqueue(pending);
            InvokeLifecycle("SceneLoaded.MenuBridge", () => GameMenuBridge.OnSceneLoaded(scene));
            InvokeLifecycle("SceneLoaded.Modding", () => Modding.PublishScene("loaded", pending.BuildIndex, pending.Name));
            InvokeLifecycle("SceneLoaded.Sons", () => GameRuntime.PublishScene("Loaded", pending.BuildIndex, pending.Name));
            InvokeLifecycle("SceneLoaded.RedLoader", () => LegacyLifecycleBridge.PublishSceneLoaded(pending.BuildIndex, pending.Name));
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            InvokeLifecycle("SceneUnloaded", () => PublishSceneUnloaded(scene));
        }

        private static void PublishSceneUnloaded(Scene scene)
        {
            InvokeLifecycle("SceneUnloaded.MenuBridge", () => GameMenuBridge.OnSceneUnloaded(scene));
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
            InvokeLifecycle("SceneUnloaded.RedLoader", () => LegacyLifecycleBridge.PublishSceneUnloaded(buildIndex, name));
            InvokeLifecycle("SceneUnloaded.Modding", () => Modding.PublishScene("unloaded", buildIndex, name));
            InvokeLifecycle("SceneUnloaded.Sons", () => GameRuntime.PublishScene("Unloaded", buildIndex, name));
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
