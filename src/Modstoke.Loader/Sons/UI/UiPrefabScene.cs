// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Loading;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Modstoke.Loader;

// The extraction scene supplies prefabs, not live input or global lighting state.
internal sealed class UiPrefabScene : IDisposable
{
    private static UiPrefabScene _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly UnityAction<Scene> _unloaded;
    private Scene _scene;
    private bool _hasScene, _loading, _disposed;

    internal UiPrefabScene()
    {
        if (_current != null)
            throw new InvalidOperationException("UI prefab scene extraction is already in progress.");
        _unloaded = new Action<Scene>(OnSceneUnloaded);
        SceneManager.add_sceneUnloaded(_unloaded);
        _current = this;
    }

    internal void Load()
    {
        _loading = true;
        try
        {
            var scene = SceneManager.LoadScene(SonsSceneManager.OptionsMenuSceneName,
                new LoadSceneParameters(LoadSceneMode.Additive));
            if (_hasScene && _scene.handle != scene.handle)
                throw new InvalidOperationException("UI prefab scene activation identity changed.");
            _scene = scene;
            _hasScene = scene.IsValid();
        }
        finally { _loading = false; }
    }

    internal static bool Owns(Component component)
    {
        var scope = _current;
        if (scope == null || scope._thread != Environment.CurrentManagedThreadId || !component)
            return false;
        var scene = component.gameObject.scene;
        if (!scene.IsValid())
            return false;
        if (!scope._hasScene && scope._loading && scene.name == SonsSceneManager.OptionsMenuSceneName)
        {
            // Activation may run inside LoadScene, before its Scene return value exists.
            scope._scene = scene;
            scope._hasScene = true;
        }
        return scope._hasScene && scene.handle == scope._scene.handle;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hasScene && _scene.IsValid() && _scene.isLoaded)
        {
            // Keep ownership until sceneUnloaded, not merely until this request returns.
            if (SceneManager.UnloadSceneAsync(_scene) == null)
                throw new InvalidOperationException("The UI prefab scene could not be unloaded.");
        }
        else
            Release();
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (_hasScene && scene.handle == _scene.handle)
            Release();
    }

    private void Release()
    {
        SceneManager.remove_sceneUnloaded(_unloaded);
        if (ReferenceEquals(_current, this))
            _current = null;
    }
}
