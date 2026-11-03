// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility scene dispatch adapted from RedLoader.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using RedLoader;
using RedLoader.Support;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Modstoke.Loader;

internal sealed class RedSceneFallback : ISceneHandlerFallbackDriver
{
    private static readonly RedSceneFallback Driver = new();
    private bool _loadedHookInstalled;
    private bool _unloadedHookInstalled;
    private bool _updating;
    private int _loadingScenes;

    internal static ISceneHandlerFallbackDriver Create() => Driver;

    public void Initialize()
    {
        if (!_loadedHookInstalled)
        {
            try
            {
                SceneManager.sceneLoaded = (
                                               (ReferenceEquals(SceneManager.sceneLoaded, null))
                                                   ? new Action<Scene, LoadSceneMode>(SceneHandler.OnSceneLoad)
                                                   : Il2CppSystem.Delegate.Combine(SceneManager.sceneLoaded, (UnityAction<Scene, LoadSceneMode>)new Action<Scene, LoadSceneMode>(SceneHandler.OnSceneLoad)).Cast<UnityAction<Scene, LoadSceneMode>>()
                                           );
                _loadedHookInstalled = true;
            }
            catch (Exception ex) { ReportError("SceneManager.sceneLoaded override failed", ex); }
        }

        if (!_unloadedHookInstalled)
        {
            try
            {
                SceneManager.sceneUnloaded = (
                                                 (ReferenceEquals(SceneManager.sceneUnloaded, null))
                                                     ? new Action<Scene>(SceneHandler.OnSceneUnload)
                                                     : Il2CppSystem.Delegate.Combine(SceneManager.sceneUnloaded, (UnityAction<Scene>)new Action<Scene>(SceneHandler.OnSceneUnload)).Cast<UnityAction<Scene>>()
                                             );
                _unloadedHookInstalled = true;
            }
            catch (Exception ex) { ReportError("SceneManager.sceneUnloaded override failed", ex); }
        }
    }

    public void OnSceneLoad(Scene scene, LoadSceneMode mode)
    {
        var pending = new PendingScene(scene);
        SceneHandler.scenesLoaded.Enqueue(pending);
        _loadingScenes++;
        try
        {
            GlobalEvents.OnSceneWasLoaded.Invoke(pending.buildIndex, pending.name);
        }
        finally
        {
            _loadingScenes--;
        }
    }

    public void OnSceneUnload(Scene scene)
    {
        var handle = scene.handle;
        for (var remaining = SceneHandler.scenesLoaded.Count; remaining > 0; remaining--)
        {
            var pending = SceneHandler.scenesLoaded.Dequeue();
            if (pending is not PendingScene loaded || loaded.Handle != handle)
                SceneHandler.scenesLoaded.Enqueue(pending);
        }

        GlobalEvents.OnSceneWasUnloaded.Invoke(scene.buildIndex, scene.name);
    }

    public void OnUpdate()
    {
        if (_updating || _loadingScenes != 0 || SceneHandler.scenesLoaded.Count == 0)
            return;

        _updating = true;
        try
        {
            // Only the entries present at entry belong to this update. Callbacks may load
            // or unload other scenes, but cannot advance those scenes in this same pass.
            var pending = SceneHandler.scenesLoaded.ToArray();
            foreach (var evt in pending)
            {
                // An unload callback can remove a later entry from the live queue.
                if (SceneHandler.scenesLoaded.Count == 0 || !ReferenceEquals(SceneHandler.scenesLoaded.Peek(), evt))
                    continue;
                SceneHandler.scenesLoaded.Dequeue();
                if (evt == null)
                    continue;

                try
                {
                    if (evt is PendingScene loaded && (!loaded.Scene.IsValid() || !loaded.Scene.isLoaded))
                        continue;
                    if (evt.wasLoadedThisTick)
                        GlobalEvents.OnSceneWasInitialized.Invoke(evt.buildIndex, evt.name);
                    else
                    {
                        evt.wasLoadedThisTick = true;
                        SceneHandler.scenesLoaded.Enqueue(evt);
                    }
                }
                catch (Exception ex)
                {
                    ReportError("Scene initialization dispatch failed", ex);
                }
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private sealed class PendingScene : SceneHandler.SceneInitEvent
    {
        internal readonly Scene Scene;
        internal readonly int Handle;

        internal PendingScene(Scene scene)
        {
            Scene = scene;
            Handle = scene.handle;
            buildIndex = scene.buildIndex;
            name = scene.name;
        }
    }

    private static void ReportError(string message, Exception error)
    {
        try { RLog.Error(message + ": " + error); }
        catch { /* Diagnostics must not interrupt other scene lifetimes. */ }
    }
}
