// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Sons;
using UnityEngine;
using LoadScene = global::Sons.Loading.LoadScene;
using LocalPlayer = TheForest.Utils.LocalPlayer;
using MainSceneLoadingTrigger = global::Sons.Utils.MainSceneLoadingTrigger;
using PlayerActivationDependencies = TheForest.Save.PlayerActivationDependencies;
using PlayerViews = TheForest.Items.Inventory.PlayerInventory.PlayerViews;
using SaveGameManager = global::Sons.Save.SaveGameManager;
using SonsSceneManager = global::Sons.Loading.SonsSceneManager;

namespace Modstoke.Loader
{
    /// <summary>
    /// How far a world load has got, read from the game's own loader with no patches: five phases, each a band of the
    /// bar, and inside each a target from real progress, or a creep where the engine says nothing for a while. The
    /// bar eases toward the target and never goes back, and a frame the engine holds for seconds moves it no more than
    /// a tenth of a second would.
    /// </summary>
    internal sealed class LoadingTrack
    {
        /// <summary>The marks a load's timings are kept by, in seconds after the raise; the last entry is the scene count.</summary>
        internal const int Raised = 0, Initialized = 1, Activating = 2, MainComplete = 3, SaveLoaded = 4, Triggered = 5, SceneCount = 6;

        // Phase edges from captures of continued and new games (prepare, stream, activate, finish, the black gap).
        private static readonly float[] DefaultEdges = { 0f, 0.02f, 0.50f, 0.84f, 0.90f, 0.99f };
        private const int MaxScenes = 64, DefaultScenes = 23;
        private const float Smoothing = 0.3f, PrepareSeconds = 0.6f, PollSeconds = 0.1f, MainActivation = 2f;
        private const float DefaultPerScene = 0.3f, DefaultGap = 1.6f, MinBand = 0.02f, LastEdge = 0.99f;

        private readonly float[] _edges = new float[6];
        private readonly float[] _marks = new float[7];
        private readonly AsyncOperation[] _operations = new AsyncOperation[MaxScenes];
        private readonly float[] _progress = new float[MaxScenes];
        private readonly bool[] _done = new bool[MaxScenes];
        private LoadScene _loader;
        private MainSceneLoadingTrigger _trigger;
        private Il2CppSystem.Collections.Generic.List<AsyncOperation> _list;
        private int _known, _count = DefaultScenes, _main = 1, _blank, _saveLoads;
        private float _raised, _phaseStart, _lastDone, _nextPoll, _lastStep, _goal, _perScene, _gap, _streamed, _activated;
        private bool _initialized, _activating, _finished, _mainComplete, _saveLoaded, _expectSave, _allDone, _usedSeen, _viewSeen, _loadSeen, _outSeen;

        internal int Phase { get; private set; }
        internal float Shown { get; private set; }
        internal bool Done { get; private set; }
        /// <summary>What ended the load: the opening starting, the loading screen let go, the player's view or the world.</summary>
        internal string Ending { get; private set; } = "";
        /// <summary>Whether the world's scenes are all in, after which nothing the player presses may reach this screen.</summary>
        internal bool Complete => _mainComplete;
        internal float PhaseSeconds(float now) => now - _phaseStart;
        internal IReadOnlyList<float> Marks => _marks;

        /// <summary>Starts a load raised at <paramref name="now"/>, with bands from this kind of load's last timings.</summary>
        internal void Begin(float now, IReadOnlyList<float[]> history, bool expectSave)
        {
            _loader = null;
            _trigger = null;
            _list = null;
            Array.Clear(_operations);
            Array.Clear(_progress);
            Array.Clear(_done);
            Array.Fill(_marks, -1f);
            _marks[Raised] = 0;
            _known = _blank = 0;
            _count = DefaultScenes;
            _main = 1;
            _raised = _phaseStart = _lastDone = _lastStep = now;
            _nextPoll = 0;
            _goal = _streamed = _activated = 0;
            _initialized = _activating = _finished = _mainComplete = _saveLoaded = _allDone = _usedSeen = _viewSeen = _loadSeen = _outSeen = false;
            _expectSave = expectSave;
            _saveLoads = SaveLifecycle.State.AfterLoadCount;
            Phase = 0;
            Shown = 0;
            Done = false;
            Ending = "";
            Calibrate(history);
        }

        /// <summary>The loading scene's loader and opening trigger, taken from its roots when it arrives.</summary>
        internal void Attach(LoadScene loader, MainSceneLoadingTrigger trigger)
        {
            _loader = loader;
            _trigger = trigger;
        }

        /// <summary>Reads the game's state; at most 30 times a second, so nothing here is per frame.</summary>
        internal void Sample(float now)
        {
            if (Done) return;
            if (_loader != null && _loader && !_mainComplete)
            {
                if (!_initialized && _loader._initialized)
                {
                    _initialized = true;
                    Mark(Initialized, now);
                    ReadScenes();
                }
                if (_initialized && !_activating && _loader._preloadCompleted > 0)
                {
                    _activating = true;
                    _lastDone = now;
                    Mark(Activating, now);
                }
                if (_initialized) ReadOperations(now);
                if (_initialized && _loader._totalSceneLoadProgressPercent >= 99.9f) _finished = true;
            }
            // The game's flag stays set at the title until a load starts again, so it counts only after this one began.
            if (_initialized && !_mainComplete && LoadScene._isMainSceneLoadComplete)
            {
                _mainComplete = _finished = true;
                Mark(MainComplete, now);
            }
            if (_mainComplete && !_saveLoaded) WatchSave(now);
            if (_mainComplete && now >= _nextPoll)
            {
                _nextPoll = now + PollSeconds;
                _allDone = PlayerActivationDependencies.IsAllDone();
                WatchView(now);
            }
            WatchTrigger(now);
            if (Done) return;
            var phase = !_initialized ? 0 : _finished ? (_mainComplete && _saveLoaded ? 4 : 3) : _activating ? 2 : 1;
            if (phase != Phase)
            {
                Phase = phase;
                _phaseStart = now;
            }
            _goal = _edges[phase] + (_edges[phase + 1] - _edges[phase]) * Mathf.Clamp01(Inside(phase, now));
        }

        /// <summary>Eases the bar toward the target; called when the screen paints.</summary>
        internal float Step(float now)
        {
            // A long frame moves the bar like a tenth of a second, then it eases on.
            var dt = Mathf.Min(now - _lastStep, 0.1f);
            _lastStep = now;
            if (dt > 0) Shown = Mathf.Max(Shown, Shown + (_goal - Shown) * (1f - Mathf.Exp(-dt / Smoothing)));
            return Shown;
        }

        /// <summary>
        /// Band edges in proportion to the mean phase times of the last loads of this kind (the first and fourth bands
        /// at least 2 %, the last ending at 99 %), the expected time a scene takes to activate and the black gap's length.
        /// </summary>
        private void Calibrate(IReadOnlyList<float[]> history)
        {
            Array.Copy(DefaultEdges, _edges, _edges.Length);
            _perScene = DefaultPerScene;
            _gap = DefaultGap;
            Span<float> means = stackalloc float[5];
            var loads = 0;
            float scenes = 0;
            if (history != null)
                foreach (var marks in history)
                {
                    if (marks == null || marks.Length < 7) continue;
                    var complete = true;
                    for (var i = 1; i <= Triggered; i++) complete &= marks[i] >= marks[i - 1];
                    if (!complete) continue;
                    for (var i = 0; i < 5; i++) means[i] += marks[i + 1] - marks[i];
                    scenes += marks[SceneCount];
                    loads++;
                }
            if (loads == 0) return;
            var total = 0f;
            for (var i = 0; i < 5; i++) total += means[i] /= loads;
            if (total <= 0.01f) return;
            Span<float> widths = stackalloc float[5];
            for (var i = 0; i < 5; i++) widths[i] = means[i] / total * LastEdge;
            // The short bands keep enough room to show their step; what they need comes from the others in proportion.
            var taken = 0f;
            var rest = 0f;
            for (var i = 0; i < 5; i++)
                if ((i == 0 || i == 3) && widths[i] < MinBand) taken += MinBand - widths[i];
                else if (i != 0 && i != 3) rest += widths[i];
            for (var i = 0; i < 5; i++)
                widths[i] = (i == 0 || i == 3) ? Mathf.Max(widths[i], MinBand) : rest > 0 ? widths[i] * (1f - taken / rest) : widths[i];
            _edges[0] = 0;
            for (var i = 0; i < 5; i++) _edges[i + 1] = _edges[i] + widths[i];
            _edges[5] = LastEdge;
            _perScene = Mathf.Max(0.05f, means[2] / Mathf.Max(1f, scenes / loads));
            _gap = Mathf.Max(0.2f, means[4]);
        }

        private float Inside(int phase, float now)
        {
            switch (phase)
            {
                case 0:
                    return 1f - Mathf.Exp(-(now - _raised) / PrepareSeconds);
                case 1:
                    return _streamed;
                case 2:
                    return _activated;
                case 3:
                    return !_mainComplete ? 0.33f : _saveLoaded ? 1f : 0.66f;
                default:
                    var gap = 1f - Mathf.Exp(-(now - _phaseStart) / (_gap / 2f));
                    return _allDone ? Mathf.Max(gap, 0.67f) : gap;
            }
        }

        // The world's scene list, read once: which entries are the game scene and the blank one it hands over to.
        private void ReadScenes()
        {
            var names = _loader._sceneNamesCache;
            if (names == null || names.Count < 3) return;
            _count = Math.Min(names.Count, MaxScenes);
            for (var i = 0; i < _count; i++)
            {
                var name = names[i];
                if (name == "SonsMain") _main = i;
                else if (name == "BlankScene") _blank = i;
            }
        }

        // Streaming: the game scene is half the band and the blank scene almost none; operations the loader has not
        // started yet count as nothing. A scene's progress can fall back, so the best seen is kept. Activation: the game
        // scene is 40 % and the rest share 60 %, plus a creep through the scene being activated, so the bar keeps moving
        // through the seconds the game scene takes.
        private void ReadOperations(float now)
        {
            if (_list == null || _list.Count < _known)
            {
                _list = _loader._loadSceneAsync;
                _known = 0;
            }
            if (_list == null) return;
            var count = Math.Min(_list.Count, MaxScenes);
            for (var i = _known; i < count; i++) _operations[i] = _list[i];
            _known = count;
            _count = Math.Max(_count, count);
            var others = Math.Max(1, _count - 2);
            float streamed = 0, activated = 0, creep = 0;
            var waiting = true;
            for (var i = 0; i < count; i++)
            {
                var operation = _operations[i];
                if (operation == null) continue;
                if (!_done[i] && operation.isDone)
                {
                    _done[i] = true;
                    if (_activating) _lastDone = now;
                }
                _progress[i] = _done[i] ? 1f : Mathf.Max(_progress[i], operation.progress);
                var weight = i == _main ? 0.5f : i == _blank ? 0.01f : 0.49f / others;
                streamed += weight * Mathf.Clamp01(_progress[i] / 0.9f);
                if (i == _blank) continue;
                var share = i == _main ? 0.4f : 0.6f / others;
                if (_done[i]) activated += share;
                else if (waiting && _activating)
                {
                    // Scenes activate in the order they were asked for; the first not done is the one in progress.
                    waiting = false;
                    var expected = i == _main ? Mathf.Max(_perScene, MainActivation) : _perScene;
                    creep = share * Mathf.Min(0.8f, (now - _lastDone) / expected);
                }
            }
            _streamed = streamed;
            _activated = activated + creep;
        }

        // The save loads once the scenes are in; Modstoke's save hooks count it, and the game's flag stands in without them.
        private void WatchSave(float now)
        {
            var loaded = !_expectSave || SaveLifecycle.State.AfterLoadCount > _saveLoads;
            if (!loaded && now >= _nextPoll)
            {
                var busy = SaveGameManager.LoadInProgress();
                if (busy) _loadSeen = true;
                else loaded = _loadSeen;
            }
            if (!loaded) return;
            _saveLoaded = true;
            Mark(SaveLoaded, now);
        }

        private void WatchView(float now)
        {
            try
            {
                var loading = LocalPlayer.CurrentView == PlayerViews.Loading;
                if (loading) _viewSeen = true;
                else if (_viewSeen) End(now, "the player's view");
            }
            catch (Exception)
            {
                // The player is not there yet.
            }
        }

        // The opening trigger fires once the game is ready and destroys itself the next frame; either is the end.
        private void WatchTrigger(float now)
        {
            if (_trigger != null && (_trigger ? _trigger._triggered : _mainComplete)) End(now, "opening started");
            if (Done) return;
            if (SonsSceneManager._usedLoadingScreen) _usedSeen = true;
            else if (_usedSeen) End(now, "loading screen released");
            if (Done) return;
            // Loading from inside a game (the death screen's new game) leaves the world flag set for a frame or two, so
            // it only counts once it has been seen clear during this load.
            if (!GameRuntime.IsInWorld) _outSeen = true;
            else if (_outSeen) End(now, "in the world");
        }

        private void End(float now, string ending)
        {
            if (Done) return;
            Done = true;
            Ending = ending;
            Mark(Triggered, now);
            _marks[SceneCount] = _count;
        }

        private void Mark(int mark, float now)
        {
            if (_marks[mark] < 0) _marks[mark] = now - _raised;
        }
    }
}
