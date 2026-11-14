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
    /// Frame caps: the game's frame limiter holds the menus and the world's loading screen at 30 fps. The menus get
    /// 60 fps; while a world loads, 240 fps, because Unity's uploads and scene integration advance once per frame.
    /// The new world is revealed as soon as frames settle instead of after the game's fixed black-screen wait.
    /// During a performance capture each loading step is marked on the timeline.
    /// </summary>
    internal static class LoadingPerformance
    {
        private const int DefaultMenuFps = 60;
        private const int LoadingFps = 240;
        private const string WorldLoaderScene = "SonsMainLoading";
        private const int LoadingUploadSliceMs = 24;
        private const int LoadingUploadBufferMb = 256;
        private const int WarmUpVariantsPerFrame = 64;
        private const float SettledFrameSeconds = 0.05f;
        private const int SettledFrames = 10;
        private const float SettledSeconds = 0.15f;
        private static float _smoothSeconds;
        private static global::Sons.Utils.MainSceneLoadingTrigger _reveal;
        private static float _nextRevealSearch;
        private static int _smoothFrames;
        private static bool _revealHandled;
        private static (int Slice, int Buffer, bool Persistent)? _gameUpload;
        private static readonly Dictionary<IntPtr, (FpsLimiterOverride Override, int Fps)> RaisedOverrides = new();
        private static FpsLimiter _limiter;
        private static int _limiterMenuFps;
        private static bool _loggedMenuRaise, _loggedLoadingRaise;
        private static bool _collectorPaused;
        private static long _heapAtPause;

        // What UnityEngine.Scripting.GarbageCollector.GCMode switches underneath; that type is stripped from this build.
        [System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void il2cpp_gc_disable();

        [System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void il2cpp_gc_enable();

        [System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
        private static extern bool il2cpp_gc_is_disabled();
        private static readonly List<(LoadScene Scene, int Steps)> Watched = new();
        private static ThreadPriority? _gamePriority;
        private static bool _loading;
        private static float _nextMenuCheck;
        private static int _loaderSteps = -1;
        private static string _scannedScene;
        private static global::LoadSave _startSequence;
        private static string _playerView;
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
                    FindLoaders(state.SceneName);
                    if (!GameBuild.IsDedicatedServer)
                    {
                        if (loadingScene) PauseCollector();
                        LiftFrameCaps(LoadingFps);
                        if (gameScene) RevealWhenSettled();
                    }
                    if (PerformanceCapture.Active) WatchSteps();
                }
                else if (!state.IsInWorld && !gameScene && !GameBuild.IsDedicatedServer)
                    LiftFrameCaps(0);
            }
            catch (Exception error)
            {
                // A changed game build must not take frame pacing or loading with it.
                _failed = true;
                RestoreMenuCaps();
                RestorePriority();
                ResumeCollector();
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
            SceneOperationStates.Clear();
            _loaderSteps = -1;
            _scannedScene = null;
            _startSequence = null;
            _playerView = null;
            _reveal = null;
            _nextRevealSearch = 0;
            _revealHandled = false;
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
                TrimFixedWaits(loader);
                PaceShaderWarmUp(loader);
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

        // Capture-only: marks when the game starts its opening cutscene and steps through its start sequence, to find
        // what holds the camera back after loading. Nothing is patched outside an armed capture.
        internal static void InstallCaptureHooks(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (!PerformanceCapture.Active || GameBuild.IsDedicatedServer || support == null || !support.SafeForHarmonyPatches) return;
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".loading-capture");
            var mark = new HarmonyLib.HarmonyMethod(typeof(LoadingPerformance), nameof(MarkGameCall));
            var patched = 0;
            foreach (var (type, name) in new[]
            {
                (typeof(global::Sons.Cutscenes.CutsceneManager), "Start"),
                (typeof(global::Sons.Cutscenes.CutsceneManager), "PlayOpeningCutscene"),
                (typeof(global::Sons.Cutscenes.CutsceneManager), "TriggerOpeningCutsceneInternal"),
                (typeof(global::Sons.Cutscenes.CutsceneManager), "VerifyFadeFromBlackCutscene"),
                (typeof(global::Sons.Cutscenes.CutsceneManager), "OnGameLoadedFromSave"),
                (typeof(global::Sons.Cutscenes.FadeFromBlackCutscene), "Play"),
                (typeof(global::Sons.Cutscenes.FadeFromBlackCutscene), "PostStartHook"),
                (typeof(global::Sons.Utils.MainSceneLoadingTrigger), "Completed"),
                (typeof(global::LoadSave), "OnSaveSlotSelected")
            })
            {
                try
                {
                    var method = HarmonyLib.AccessTools.DeclaredMethod(type, name);
                    if (method == null) continue;
                    harmony.Patch(method, prefix: mark);
                    patched++;
                }
                catch (Exception error)
                {
                    warning?.Invoke("Loading capture could not mark " + type.Name + "." + name + ": " + error.Message);
                }
            }
            try
            {
                var step = HarmonyLib.AccessTools.DeclaredMethod(typeof(global::LoadSave), "SetStartSequenceStep");
                if (step != null)
                {
                    harmony.Patch(step, prefix: new HarmonyLib.HarmonyMethod(typeof(LoadingPerformance), nameof(MarkStartStep)));
                    patched++;
                }
            }
            catch (Exception error)
            {
                warning?.Invoke("Loading capture could not mark LoadSave.SetStartSequenceStep: " + error.Message);
            }
            RuntimeEntryPoint.LogSource?.LogInfo("Loading capture marks " + patched + " game calls.");
        }

        private static void MarkGameCall(System.Reflection.MethodBase __originalMethod) =>
            PerformanceCapture.Mark("game-call", __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name);

        private static int _startStep = -1;

        // The sequence reports progress inside a step every frame; only a new step is marked.
        private static void MarkStartStep(int step)
        {
            if (step == _startStep) return;
            _startStep = step;
            PerformanceCapture.Mark("start-step", step);
        }

        // The world's loader waits fixed times before it starts and before it closes (LoadScene.DelayPassed and
        // WaitForCleanupDelay): plain timers with no condition behind them, 0.2 s and 1 s in this build. The screen
        // stays black for seconds after the loader closes anyway, so the world's loader skips both.
        private static void TrimFixedWaits(LoadScene loader)
        {
            if (!string.Equals(loader.gameObject.scene.name, WorldLoaderScene, StringComparison.Ordinal)) return;
            var delay = loader._delay;
            var cleanup = loader._cleanupDelay;
            if (delay <= 0 && cleanup <= 0) return;
            loader._delay = 0;
            loader._cleanupDelay = 0;
            RuntimeEntryPoint.LogSource?.LogInfo("World loader fixed waits skipped: " + delay + " s before loading, " + cleanup + " s before closing.");
        }

        // The world's loader does not request the game scene until its shader warm-up (ShaderCollectionWarmUp, 514
        // variants in this build) has finished. The warm-up compiles a few variants, waits _delayFrameCount frames
        // and repeats, so at the loading screen's 30 fps it held the load for over 4 s while every loading thread sat
        // idle; the compiling itself took 0.3 s. Larger batches with no frame gaps finish it in a few frames.
        private static void PaceShaderWarmUp(LoadScene loader)
        {
            if (!string.Equals(loader.gameObject.scene.name, WorldLoaderScene, StringComparison.Ordinal)) return;
            var warmUps = loader._shaderCollectionWarmUps;
            if (warmUps == null) return;
            for (var index = 0; index < warmUps.Count; index++)
            {
                var warmUp = warmUps[index];
                if (!warmUp) continue;
                var batch = warmUp._variantsPerFrame;
                var delay = warmUp._delayFrameCount;
                if (batch >= WarmUpVariantsPerFrame && delay <= 0) continue;
                warmUp._variantsPerFrame = Math.Max(batch, WarmUpVariantsPerFrame);
                warmUp._delayFrameCount = 0;
                RuntimeEntryPoint.LogSource?.LogInfo("Shader warm-up before the world paced up: " + batch + " variants every " +
                    (Math.Max(delay, 0) + 1) + " frames became " + warmUp._variantsPerFrame + " every frame.");
                PerformanceCapture.Mark("shader-warmup", new { batch, delay, newBatch = warmUp._variantsPerFrame });
            }
        }

        // MainSceneLoadingTrigger.LateUpdate starts the fade in once the loader has reported completion, a fixed
        // _completeWaitTrigger has passed (3 s of game time in this build, stretched further by the long frames right
        // after loading) and PlayerActivationDependencies are all done. Captures show frames running steadily for the
        // last 2.5 s of that wait with nothing left to do. Once frames have settled the fixed part is dropped, and the
        // game's own dependency check still decides when the fade starts.
        private static void RevealWhenSettled()
        {
            if (_revealHandled || !LoadScene._isMainSceneLoadComplete) return;
            if (!_reveal)
            {
                if (Time.unscaledTime < _nextRevealSearch) return;
                _nextRevealSearch = Time.unscaledTime + 0.25f;
                _reveal = UnityEngine.Object.FindObjectOfType<global::Sons.Utils.MainSceneLoadingTrigger>();
                _smoothFrames = 0;
                _smoothSeconds = 0;
                if (!_reveal) return;
            }
            // A negative completion time means the loader has not reported completion yet.
            if (_reveal._completedTime < 0) return;
            var wait = _reveal._completeWaitTrigger;
            var waited = Time.time - _reveal._completedTime;
            if (_reveal._triggered || waited >= wait)
            {
                _revealHandled = true;
                return;
            }
            var delta = Time.unscaledDeltaTime;
            _smoothFrames = delta < SettledFrameSeconds ? _smoothFrames + 1 : 0;
            _smoothSeconds = _smoothFrames == 0 ? 0 : _smoothSeconds + delta;
            if (_smoothFrames < SettledFrames || _smoothSeconds < SettledSeconds) return;
            _revealHandled = true;
            _reveal._completeWaitTrigger = 0;
            RuntimeEntryPoint.LogSource?.LogInfo($"World fade in no longer waits: frames settled {waited:0.0} s into the game's fixed {wait:0.#} s wait.");
            PerformanceCapture.Mark("world-reveal", new { fixedWait = wait, waited });
        }

        // The game's managed heap uses Unity's incremental collector, which does marking work on whichever thread
        // allocates once enough has been allocated. A world load allocates heavily: a CPU profile put 0.43 s of the
        // main thread's activation phase and 0.28 s of the streaming phase in the collector. Collection is paused from
        // the world's loading screen until the player is in the world (or back in the menus) and the heap simply grows
        // meanwhile; the incremental collector then catches up in its usual per-frame slices.
        private static void PauseCollector()
        {
            if (_collectorPaused || il2cpp_gc_is_disabled()) return;
            il2cpp_gc_disable();
            _collectorPaused = true;
            _heapAtPause = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            RuntimeEntryPoint.LogSource?.LogInfo("Managed collection paused while the world loads (" + _heapAtPause / 1048576 + " MB in use).");
        }

        private static void ResumeCollector()
        {
            if (!_collectorPaused) return;
            _collectorPaused = false;
            il2cpp_gc_enable();
            var used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            RuntimeEntryPoint.LogSource?.LogInfo("Managed collection resumed (" + used / 1048576 + " MB in use, " +
                (used - _heapAtPause) / 1048576 + " MB more than when paused; heap " + UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576 + " MB).");
            PerformanceCapture.Mark("collector-resumed", new { usedMb = used / 1048576, grownMb = (used - _heapAtPause) / 1048576 });
        }

        private static void End(string where)
        {
            _loading = false;
            ResumeCollector();
            RestoreMenuCaps();
            RestorePriority();
            Watched.Clear();
            _startSequence = null;
            _reveal = null;
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
            // The player is in the world once the game moves its view there; these show what holds it after loading.
            var cutscene = global::Sons.Cutscenes.CutsceneManager.GetActiveCutScene();
            var fade = cutscene ? cutscene.TryCast<global::Sons.Cutscenes.FadeFromBlackCutscene>() : null;
            var cameras = new List<string>();
            foreach (var camera in Camera.allCameras)
                if (camera && camera.enabled) cameras.Add(camera.name);
            var view = TheForest.Utils.LocalPlayer.CurrentView.ToString() + (Time.timeScale == 0 ? " (time stopped)" : "")
                + " | cutscene " + (cutscene ? cutscene.name + (fade != null ? " (duration " + fade._cutsceneDuration + " s, HUD at "
                    + fade._timeToHudReveal + " s, started " + fade._cutsceneHasStarted + ")" : "") : "none")
                + " | cameras " + (cameras.Count == 0 ? "none" : string.Join(", ", cameras));
            if (!string.Equals(view, _playerView, StringComparison.Ordinal))
            {
                PerformanceCapture.Mark("player-view", view);
                _playerView = view;
            }
            for (var index = 0; index < Watched.Count; index++)
            {
                var (loader, previous) = Watched[index];
                if (!loader) continue;
                MarkSceneOperations(index, loader);
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

        private static readonly Dictionary<int, string> SceneOperationStates = new();

        // The loader allows its scenes to activate once their combined progress reaches 90%, where Unity's async scene
        // loads stop until activation. Each operation's progress (d = done, a = activation allowed) shows which one
        // holds the world back when the loading threads are idle.
        private static void MarkSceneOperations(int index, LoadScene loader)
        {
            var operations = loader._loadSceneAsync;
            if (operations == null || operations.Count == 0) return;
            var state = new System.Text.StringBuilder();
            state.Append(Mathf.RoundToInt(loader._totalSceneLoadProgressPercent)).Append("% of");
            for (var operation = 0; operation < operations.Count; operation++)
            {
                var item = operations[operation];
                if (item == null) { state.Append(" -"); continue; }
                state.Append(' ').Append(Mathf.RoundToInt(item.progress * 100));
                if (item.isDone) state.Append('d');
                if (item.allowSceneActivation) state.Append('a');
            }
            var text = state.ToString();
            if (SceneOperationStates.TryGetValue(index, out var last) && string.Equals(last, text, StringComparison.Ordinal)) return;
            SceneOperationStates[index] = text;
            PerformanceCapture.Mark("scene-ops", new { loader = index, state = text });
        }

        // The game's frame limiter caps the menus and the world's loading screen at its menu limit, 30 fps, unless a
        // scene override says otherwise. In the menus that made motion choppy. While a world loads it held the load back:
        // Unity's async texture and mesh uploads and its scene integration each advance once per frame, and a scene
        // cannot activate until its uploads finish, so captures showed seconds of idle 30 fps frames with every thread
        // waiting. The limiter's menu limit and any lower overrides are raised to the target (0 = the menus' 60 fps);
        // when no limiter runs on the loading screen, the menus' last rate would stay in force, so it is raised directly.
        private static void LiftFrameCaps(int fps)
        {
            if (Time.unscaledTime < _nextMenuCheck) return;
            _nextMenuCheck = Time.unscaledTime + 0.25f;
            if (fps <= 0) fps = DefaultMenuFps;
            if (FpsLimiter.TryGetInstance(out var limiter) && limiter)
            {
                if (limiter._menuFpsMax < fps)
                {
                    if (_limiter == null || _limiter.Pointer != limiter.Pointer)
                    {
                        _limiter = limiter;
                        _limiterMenuFps = limiter._menuFpsMax;
                    }
                    // The game resets its menu limit on every menu scene load, so each case is reported once.
                    if (_loading ? !_loggedLoadingRaise : !_loggedMenuRaise)
                        RuntimeEntryPoint.LogSource?.LogInfo("Frame limit raised from " + limiter._menuFpsMax + " to " + fps + " fps"
                            + (_loading ? " while a world loads." : " in the menus."));
                    if (_loading) _loggedLoadingRaise = true;
                    else _loggedMenuRaise = true;
                    limiter._menuFpsMax = fps;
                }
            }
            else if (_loading && Application.targetFrameRate > 0 && Application.targetFrameRate < fps)
            {
                RuntimeEntryPoint.LogSource?.LogInfo("Frame rate raised from " + Application.targetFrameRate + " to " + fps
                    + " fps while the world loads; no frame limiter is running.");
                Application.targetFrameRate = fps;
            }
            var overrides = FpsLimiter._overrides;
            if (overrides == null || overrides.Count == 0) return;
            for (var index = 0; index < overrides.Count; index++)
            {
                var fpsOverride = overrides[index];
                if (!fpsOverride || fpsOverride._targetFps <= 0 || fpsOverride._targetFps >= fps) continue;
                if (!RaisedOverrides.ContainsKey(fpsOverride.Pointer))
                {
                    RaisedOverrides[fpsOverride.Pointer] = (fpsOverride, fpsOverride._targetFps);
                    RuntimeEntryPoint.LogSource?.LogInfo("Frame limit raised from " + fpsOverride._targetFps + " to " + fps +
                        " fps for " + fpsOverride.gameObject.scene.name + (_loading ? " while the world loads." : "."));
                }
                fpsOverride._targetFps = fps;
            }
        }

        private static void RestoreMenuCaps()
        {
            foreach (var (fpsOverride, fps) in RaisedOverrides.Values)
                if (fpsOverride) fpsOverride._targetFps = fps;
            RaisedOverrides.Clear();
            if (_limiter != null && _limiter) _limiter._menuFpsMax = _limiterMenuFps;
            _limiter = null;
        }
    }
}
