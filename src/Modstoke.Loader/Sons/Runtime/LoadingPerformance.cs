// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using UnityEngine;
using FpsLimiter = global::Sons.FpsLimiter;
using FpsLimiterOverride = global::Sons.FpsLimiterOverride;
using LoadScene = global::Sons.Loading.LoadScene;

namespace Modstoke.Sons
{
    /// <summary>
    /// Loading-time corrections that need no game patch.
    /// Unity integrates asynchronously loaded scenes in a slice of each frame: 4 ms at the default BelowNormal
    /// priority, 50 ms at High, and uploads their textures and meshes in another slice. The game keeps the defaults and
    /// caps its loading screen at 30 fps, so most of every loading frame sat idle. From the loading screen until the
    /// player is in the world both slices are wide, then the game's own values are put back so streaming in the world
    /// keeps its small slices.
    /// Menus: the multiplayer scenes carry a 30 fps override, which made the front end's motion visibly choppy; while
    /// no world is loading or running, such overrides are raised to the game's own menu limit.
    /// During a performance capture each loading step is marked on the timeline.
    /// </summary>
    internal static class LoadingPerformance
    {
        private const int DefaultMenuFps = 60;
        private const int LoadingUploadSliceMs = 24;
        private const int LoadingUploadBufferMb = 256;
        private static (int Slice, int Buffer, bool Persistent)? _gameUpload;
        private static readonly Dictionary<IntPtr, (FpsLimiterOverride Override, int Fps)> RaisedOverrides = new();
        private static readonly List<(LoadScene Scene, int Steps)> Watched = new();
        private static ThreadPriority? _gamePriority;
        private static bool _loading;
        private static float _nextMenuCheck;
        private static int _loaderSteps = -1;
        private static string _scannedScene;
        private static global::LoadSave _startSequence;
        private static bool _failed;

        internal static void Tick()
        {
            if (_failed) return;
            try
            {
                var state = GameRuntime.State;
                var loadingScene = string.Equals(state.SceneKind, SceneKind.Loading, StringComparison.Ordinal);
                var gameScene = string.Equals(state.SceneKind, SceneKind.Game, StringComparison.Ordinal);
                // Loads that matter start outside the game scene; the game's flag alone is not trusted inside it.
                if (!_loading && !state.IsInWorld && (loadingScene || (LoadScene._isLoading && !gameScene)))
                    Begin(state.SceneName);
                // A menu scene load also sets the game's flag; once it clears outside the loading and game scenes, the
                // menus are showing again. Between the loading screen and the world the game scene keeps it open.
                else if (_loading && (state.IsInWorld || (!loadingScene && !gameScene && !LoadScene._isLoading)))
                    End(state.IsInWorld ? "in the world" : "in the menus");

                if (_loading)
                {
                    if (PerformanceCapture.Active)
                    {
                        FindLoaders(state.SceneName);
                        WatchSteps();
                    }
                }
                else if (!state.IsInWorld && !gameScene && !GameBuild.IsDedicatedServer)
                    LiftMenuCap();
            }
            catch (Exception error)
            {
                // A changed game build must not take frame pacing or loading with it.
                _failed = true;
                RestoreMenuCaps();
                RestorePriority();
                RuntimeEntryPoint.LogSource?.LogWarning("Loading performance corrections stopped: " + error.Message);
            }
        }

        private static void Begin(string scene)
        {
            _loading = true;
            RestoreMenuCaps();
            var current = Application.backgroundLoadingPriority;
            if (current != ThreadPriority.High)
            {
                _gamePriority = current;
                Application.backgroundLoadingPriority = ThreadPriority.High;
            }
            // Textures and meshes reach the GPU through Unity's async upload ring: a 2 ms slice a frame by default,
            // 60 ms a second under the loading screen's 30 fps cap while gigabytes stream in.
            var upload = (Slice: QualitySettings.asyncUploadTimeSlice, Buffer: QualitySettings.asyncUploadBufferSize,
                Persistent: QualitySettings.asyncUploadPersistentBuffer);
            _gameUpload ??= upload;
            QualitySettings.asyncUploadTimeSlice = Math.Max(upload.Slice, LoadingUploadSliceMs);
            QualitySettings.asyncUploadBufferSize = Math.Max(upload.Buffer, LoadingUploadBufferMb);
            QualitySettings.asyncUploadPersistentBuffer = true;
            PerformanceCapture.Mark("loading-started", new
            {
                scene,
                gamePriority = current.ToString(),
                gameUploadSliceMs = upload.Slice,
                gameUploadBufferMb = upload.Buffer,
                gameUploadPersistent = upload.Persistent
            });
            Watched.Clear();
            _loaderSteps = -1;
            _scannedScene = null;
            _startSequence = null;
        }

        // Loaders live in the scenes being loaded, so each newly current scene is searched once.
        private static void FindLoaders(string scene)
        {
            if (string.Equals(scene, _scannedScene, StringComparison.Ordinal)) return;
            _scannedScene = scene;
            foreach (var loader in UnityEngine.Object.FindObjectsOfType<LoadScene>())
            {
                if (!loader || Watched.Exists(entry => entry.Scene && entry.Scene.Pointer == loader.Pointer)) continue;
                Watched.Add((loader, -1));
                PerformanceCapture.Mark("load-scene", new
                {
                    loader = Watched.Count - 1,
                    scene = loader.gameObject.scene.name,
                    delay = loader._delay,
                    cleanupDelay = loader._cleanupDelay,
                    shaderWarmUps = loader._shaderCollectionWarmUps?.Count ?? 0,
                    ignoreShaderWarmUp = loader._ignoreShaderWarmUp,
                    sceneList = loader._activeSceneList?._id
                });
            }
            // The game's start sequence runs after the loading screen has gone, before the player is in the world.
            if (!_startSequence)
            {
                _startSequence = UnityEngine.Object.FindObjectOfType<global::LoadSave>();
                if (_startSequence)
                    PerformanceCapture.Mark("start-sequence-found", new { activateAfterLoading = _startSequence._activateAfterLoading?.Count ?? 0 });
            }
        }

        private static void End(string where)
        {
            _loading = false;
            RestorePriority();
            Watched.Clear();
            _startSequence = null;
            PerformanceCapture.Mark("loading-finished", where);
        }

        // The game's values come back once the player is in the world, so streaming there keeps its small slices.
        private static void RestorePriority()
        {
            if (_gamePriority is { } priority && Application.backgroundLoadingPriority == ThreadPriority.High)
                Application.backgroundLoadingPriority = priority;
            _gamePriority = null;
            if (_gameUpload is { } upload)
            {
                QualitySettings.asyncUploadTimeSlice = upload.Slice;
                QualitySettings.asyncUploadBufferSize = upload.Buffer;
                QualitySettings.asyncUploadPersistentBuffer = upload.Persistent;
            }
            _gameUpload = null;
        }

        // Each loader's progress flags as bits, so a change marks the step that just happened.
        private static void WatchSteps()
        {
            var global = (LoadScene._isLoading ? 1 : 0) | (LoadScene._isMainSceneLoadComplete ? 2 : 0) |
                (_startSequence && _startSequence.startedSequence ? 4 : 0) |
                (BoltNetwork.isRunning ? 8 : 0) | (BoltNetwork.isServer ? 16 : 0) |
                (TheForest.Utils.LocalPlayer.Entity && TheForest.Utils.LocalPlayer.Entity.isAttached ? 32 : 0);
            if (global != _loaderSteps)
            {
                PerformanceCapture.Mark("load-global", new
                {
                    isLoading = (global & 1) != 0,
                    mainSceneLoadComplete = (global & 2) != 0,
                    startSequence = (global & 4) != 0,
                    networkRunning = (global & 8) != 0,
                    host = (global & 16) != 0,
                    playerEntityAttached = (global & 32) != 0
                });
                _loaderSteps = global;
            }
            for (var index = 0; index < Watched.Count; index++)
            {
                var (loader, previous) = Watched[index];
                if (!loader) continue;
                var steps = (loader._initialized ? 1 : 0) | (loader._startLoadSequence ? 2 : 0) |
                    (loader._preloadCompleted > 0 ? 4 : 0) | (loader._singleLoadStarted ? 8 : 0) |
                    (loader._startedTetrahedralizeAsync ? 16 : 0) | (loader._finishedTetrahedralizeAsync ? 32 : 0) |
                    (loader._pauseLoading ? 64 : 0);
                if (steps == previous) continue;
                Watched[index] = (loader, steps);
                PerformanceCapture.Mark("load-step", new
                {
                    loader = index,
                    initialized = (steps & 1) != 0,
                    sequence = (steps & 2) != 0,
                    preloaded = (steps & 4) != 0,
                    singleLoad = (steps & 8) != 0,
                    probesStarted = (steps & 16) != 0,
                    probesFinished = (steps & 32) != 0,
                    paused = (steps & 64) != 0,
                    progress = loader._totalProgressPercent
                });
            }
        }

        private static void LiftMenuCap()
        {
            if (Time.unscaledTime < _nextMenuCheck) return;
            _nextMenuCheck = Time.unscaledTime + 0.5f;
            var overrides = FpsLimiter._overrides;
            if (overrides == null || overrides.Count == 0) return;
            var menuFps = FpsLimiter.TryGetInstance(out var limiter) && limiter && limiter._menuFpsMax > 0
                ? limiter._menuFpsMax : DefaultMenuFps;
            for (var index = 0; index < overrides.Count; index++)
            {
                var fpsOverride = overrides[index];
                if (!fpsOverride || fpsOverride._targetFps <= 0 || fpsOverride._targetFps >= menuFps) continue;
                if (!RaisedOverrides.ContainsKey(fpsOverride.Pointer))
                {
                    RaisedOverrides[fpsOverride.Pointer] = (fpsOverride, fpsOverride._targetFps);
                    RuntimeEntryPoint.LogSource?.LogInfo("Menu frame limit raised from " + fpsOverride._targetFps + " to " + menuFps +
                        " fps for " + fpsOverride.gameObject.scene.name + ".");
                }
                fpsOverride._targetFps = menuFps;
            }
        }

        private static void RestoreMenuCaps()
        {
            foreach (var (fpsOverride, fps) in RaisedOverrides.Values)
                if (fpsOverride) fpsOverride._targetFps = fps;
            RaisedOverrides.Clear();
        }
    }
}
