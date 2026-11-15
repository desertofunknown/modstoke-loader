// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Modstoke.Localization;
using Modstoke.Sons;
using Sons.Save;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using LoadScene = global::Sons.Loading.LoadScene;
using MainSceneLoadingTrigger = global::Sons.Utils.MainSceneLoadingTrigger;

namespace Modstoke.Loader
{
    /// <summary>
    /// Modstoke's world loading screen, in place of the game's: raised by Modstoke's own start buttons, or when the
    /// loading scene starts arriving or arrives, and lowered as the game starts its opening. The game's loader is never
    /// touched, only read; its loading canvas is switched off and comes back if anything here fails, so a fault can
    /// cost the look of a load but never the load.
    /// </summary>
    internal static class LoadingScreen
    {
        private const string LoadingScene = "SonsMainLoading", TitleScene = "SonsTitleScene";
        // The front end's first screen settles and the game's fonts are in before the layer is built at the title.
        private const float SettleSeconds = 2f, PreferSeconds = 0.4f, IntentSeconds = 120f, ClickTimeout = 6f, HostTimeout = 30f;
        private const float SampleSeconds = 1f / 30f, PaintSeconds = 1f / 60f, WatchSeconds = 0.1f, ArrivingSeconds = 0.25f;
        private const float ControlsSeconds = 1f, IdleInput = 0.3f, ReleaseDelay = 1f, RecordDelay = 5f;
        private const int RecordLoads = 5;
        private static readonly string[] ControlActions = { "Inventory", "Book", "GpsTracker" };
        private static readonly JsonSerializerOptions RecordJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        private enum State { Off, Raising, Waiting, Loading, Lowering }

        // loading-screen.json: recent timings per kind of load, the tips seen and the picture shown last.
        private sealed class Record
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, List<float[]>> Loads { get; set; } = new();
            public List<string> Tips { get; set; } = new();
            public string Art { get; set; } = "";
        }

        private static readonly LoadingTrack Track = new();
        private static readonly bool[] PadDown = new bool[12];
        private static readonly ButtonControl[] PadButtons = new ButtonControl[12];
        private static readonly Dictionary<uint, int> Seasons = new();
        private static readonly Dictionary<uint, Task<int>> SeasonReads = new();
        private static readonly ControlKey[] PadControls = new ControlKey[3], KeyControls = new ControlKey[3];
        private static State _state;
        private static LoadingView _view;
        private static LoadingIntent _intent;
        private static Record _record;
        private static Task<Record> _recordRead;
        private static Task _recordWrite;
        private static Canvas _gameCanvas;
        private static IntPtr _padPointer;
        private static float _raisedAt, _timeout, _loweredAt, _titleSince = -1, _nextArriving, _nextSample, _nextWatch, _nextPaint;
        private static float _nextControls, _releaseAt = -1, _inWorldAt = -1, _lastInput, _preferAt = -1;
        private static int _sceneFrame = -1;
        // Of this visit's first three steps in Prepare, how many are done.
        private static int _prepared;
        private static bool _failed, _enabled = true, _settingsRead, _art, _scene, _raisedAtScene, _titleBack, _pad, _listening, _aborting, _hostStarted;
        private static bool _anyKeyDown, _nextKeyDown;
        private static (SaveGameType Type, uint Id, string GameId, int Hour) _preferred;
        private static (string Kind, float[] Marks) _pendingRecord;

        /// <summary>Whether the layer is up: raising, covering a load, or going.</summary>
        internal static bool Covering => _state != State.Off;

        /// <summary>
        /// The load that last reached the world, until the title comes back: photo mode files a photo under its save,
        /// as the game clears its own record of the slot once the world is in. Null when the load was not known.
        /// </summary>
        internal static LoadingIntent Entered { get; private set; }

        /// <summary>Whether the layer fully covers the screen, so the menu under it can go.</summary>
        internal static bool Opaque => _state is State.Waiting or State.Loading || _state == State.Raising && _view.Opaque(Time.unscaledTime);

        private static bool Usable
        {
            get
            {
                if (!_settingsRead) ReadSettings();
                return _enabled && FrontEnd.Enabled;
            }
        }

        /// <summary>The loading screen settings were changed in Options; they are read again now.</summary>
        internal static void SettingChanged() => ReadSettings();

        /// <summary>What the next load is for, from the menu that started it; used if a load follows within two minutes.</summary>
        internal static void Intend(LoadingIntent intent)
        {
            if (GameBuild.IsDedicatedServer || _failed || intent == null) return;
            try
            {
                _intent = intent;
                if (intent.SaveId != 0 && intent.Season < 0) ReadSeason(intent.Type, intent.SaveId);
                LoadingArt.Want(Wish(intent));
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        /// <summary>
        /// A save was selected on the saves screen. If it stays selected a moment its season is read and the picture for
        /// it prepared, so a continue usually finds it ready.
        /// </summary>
        internal static void Prefer(SaveGameType type, uint saveId, string gameId, int hour)
        {
            if (GameBuild.IsDedicatedServer || _failed || _preferred.Id == saveId && _preferred.Type == type) return;
            _preferred = (type, saveId, gameId ?? "", hour);
            _preferAt = Time.unscaledTime + PreferSeconds;
        }

        /// <summary>
        /// A start that loads now was pressed: the layer fades in over the menu. False when Modstoke's loading screen is
        /// off or a game dialog is up, so the caller steps its menu aside as it did before.
        /// </summary>
        internal static bool Raise(string why)
        {
            if (GameBuild.IsDedicatedServer || _failed || !Usable) return false;
            if (_state != State.Off) return true;
            // A dialog the start opened (a confirmation, a start that failed) stays readable rather than fading to black.
            if (FrontEnd.ModalShowing) return false;
            try
            {
                var now = Time.unscaledTime;
                if (!EnsureView()) return false;
                // A lobby knows its own game and players, however long it was open; a start from the title has its intent.
                var intent = LobbyHost.Open ? LobbyHost.IsHost ? LoadingIntent.FromHostedLobby() : LoadingIntent.FromLobby()
                    : _intent != null && now - _intent.Asked < IntentSeconds ? _intent : LoadingIntent.FromGameSetup();
                Begin(intent, why, now, false, LobbyHost.Open ? HostTimeout : ClickTimeout);
                return true;
            }
            catch (Exception error)
            {
                Fail(error);
                return false;
            }
        }

        /// <summary>
        /// A scene finished loading, before its first frame: the loading scene's loader and opening trigger are taken
        /// from its roots and the game's own loading canvas is switched off before it is ever drawn.
        /// </summary>
        internal static void SceneLoaded(Scene scene)
        {
            if (GameBuild.IsDedicatedServer || _failed) return;
            try
            {
                var name = scene.name;
                if (name == TitleScene)
                {
                    Entered = null;
                    // The title coming back mid-load means the load failed: a refused join, a kick, a lost host. With no
                    // load, a start asked for before (a join that never got as far as loading) is over.
                    if (_state is State.Raising or State.Waiting or State.Loading) _titleBack = true;
                    else if (_state == State.Off) _intent = null;
                    return;
                }
                if (name != LoadingScene || !Usable || _state == State.Lowering) return;
                var now = Time.unscaledTime;
                if (_state == State.Off)
                {
                    // Nothing raised the layer (the death screen, a console run, the game's own menus): it covers at once,
                    // as the game's own screen cuts to black.
                    if (!EnsureView()) return;
                    var intent = _intent != null && now - _intent.Asked < IntentSeconds ? _intent : LoadingIntent.FromGameSetup();
                    Begin(intent, "scene loaded", now, true, HostTimeout);
                }
                else _view.Snap(now);
                Attach(scene);
                _state = State.Loading;
                _scene = true;
                _sceneFrame = Time.frameCount;
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        internal static void Tick()
        {
            if (GameBuild.IsDedicatedServer || _failed) return;
            try
            {
                var now = Time.unscaledTime;
                if (_state == State.Off) TickIdle(now);
                else TickCovering(now);
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        // At the title and in the lobby: the layer is built and its picture prepared while the menu is idle, the host's
        // start and an arriving loading scene raise it, and the last load's timings are written once the player is in.
        private static void TickIdle(float now)
        {
            if (_releaseAt >= 0 && now >= _releaseAt)
            {
                _releaseAt = -1;
                LoadingArt.Release();
            }
            WriteRecordWhenIn(now);
            if (_recordWrite is { IsCompleted: true })
            {
                if (_recordWrite.IsFaulted)
                    RuntimeEntryPoint.LogSource?.LogWarning("The loading screen's record could not be written: " + _recordWrite.Exception?.GetBaseException().Message);
                _recordWrite = null;
            }
            if (!Usable) return;
            if (FrontInput.PointerMoved || FrontInput.Move != Vector2Int.zero || FrontInput.Confirm || FrontInput.Back || FrontInput.Click
                || FrontInput.Held || FrontInput.Scroll != 0)
                _lastInput = now;
            if (FrontEnd.AtTitle || LobbyHost.Open)
            {
                if (_titleSince < 0)
                {
                    _titleSince = now;
                    _prepared = 0;
                }
                if (now - _titleSince >= SettleSeconds && MenuKit.FontsReady) Prepare(now);
                TakeSeasons();
                if (_preferAt >= 0 && now >= _preferAt) PreferNow();
                // Once per start (the lobby's Start button raises it too), so a start that never loads does not raise
                // the layer again after it gives way.
                var starting = LobbyHost.Open && LobbyHost.IsHost && LobbyHost.Starting;
                if (starting && !_hostStarted && Raise("host start")) return;
                _hostStarted &= starting;
            }
            else _titleSince = -1;
            // A load started elsewhere (a lobby joiner, the server browser, an invite) shows as the scene starts arriving,
            // once the game's own connecting and password dialogs are done.
            if (now < _nextArriving || GameRuntime.IsInWorld || GameRuntime.IsGameScene) return;
            _nextArriving = now + ArrivingSeconds;
            var arriving = SceneManager.GetSceneByName(LoadingScene);
            if (arriving.IsValid() && !arriving.isLoaded) Raise("scene arriving");
        }

        private static void TickCovering(float now)
        {
            if (!Usable)
            {
                Close();
                return;
            }
            if (_state == State.Raising && _view.Opaque(now)) _state = State.Waiting;
            if (_state is State.Waiting or State.Loading)
            {
                if (now >= _nextWatch)
                {
                    _nextWatch = now + WatchSeconds;
                    var reason = AbortReason(now);
                    if (reason != null)
                    {
                        Abort(now, reason);
                        return;
                    }
                    TakeSeasons();
                }
                // A start from the title, or a raise at the scene load itself, waits out the scene switch's long frame,
                // so the picture's fade is not frozen halfway; from a lobby or the browser the old scene keeps drawing
                // underneath, so it starts at once.
                var afterSwitch = _intent.Session == LoadingSession.Single || _raisedAtScene;
                if (!_art && (!afterSwitch || _scene && Time.frameCount > _sceneFrame))
                {
                    _art = true;
                    _view.StartArt(now);
                }
                if (now >= _nextSample)
                {
                    _nextSample = now + SampleSeconds;
                    Track.Sample(now);
                    if (Track.Done) Lower(now);
                }
            }
            else if (_state == State.Lowering && now >= _loweredAt + (_aborting ? LoadingView.AbortSeconds : LoadingView.HideSeconds))
            {
                Finish(now);
                return;
            }
            if (now < _nextPaint) return;
            _nextPaint = now + PaintSeconds;
            ReadInput(now);
            _view.Paint(now, Track.Step(now), PhaseLabel(now), _pad, _listening);
        }

        // Only the raise at the scene load itself is opaque at once.
        private static void Begin(LoadingIntent intent, string why, float now, bool opaque, float timeout)
        {
            _intent = intent;
            _raisedAtScene = opaque;
            _raisedAt = now;
            _timeout = now + timeout;
            _state = opaque ? State.Waiting : State.Raising;
            _art = _scene = _titleBack = _aborting = false;
            _sceneFrame = -1;
            _titleSince = -1;
            _pendingRecord = default;
            _releaseAt = -1;
            _hostStarted = LobbyHost.Open && LobbyHost.IsHost;
            if (intent.SaveId != 0 && Seasons.TryGetValue(intent.SaveId, out var season)) _intent = intent = intent with { Season = season };
            _pad = FrontInput.PadActive;
            _listening = true;
            ReadPad(true);
            ReadKeys(true);
            var picture = LoadingArt.Take(Wish(intent));
            var tips = LoadingTips.Plan(new TipContext(intent.Session, intent.Fresh, intent.Mode, intent.Season, _pad,
                ControlGlyphs.Keybind(KeybindIds.PhotoMode).Bound, (LoaderState.CurrentInventory?.Entries?.Length ?? 0) > 0,
                LoadingArt.Setting != "modstoke"));
            _view.Begin(now, intent, picture, tips, _pad, opaque);
            Track.Begin(now, _record != null && _record.Loads.TryGetValue(intent.Kind, out var history) ? history : null,
                intent.SaveId != 0 && intent.Session != LoadingSession.Join);
            _nextSample = _nextWatch = _nextPaint = 0;
            var art = picture?.Source ?? "none";
            var tip = tips.Count > 0 ? tips[0].Id : "none";
            RuntimeEntryPoint.LogSource?.LogInfo("Loading screen shown (" + intent.Kind + ", art: " + art + ", tip: " + tip + ").");
            PerformanceCapture.Mark("loading-screen", new { state = "shown", path = intent.Kind, why, art, tip });
        }

        // The loading scene's own loader, its opening trigger under it and the game's loading canvas, all from its
        // roots, so nothing is searched for. Nothing of the game's is disabled or destroyed: those parts register the
        // listeners that load the save and start the opening.
        private static void Attach(Scene scene)
        {
            LoadScene loader = null;
            MainSceneLoadingTrigger trigger = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "SceneLoader")
                {
                    loader = root.GetComponent<LoadScene>();
                    var indicator = root.transform.Find("LoadingIndictor");
                    if (indicator != null) trigger = indicator.GetComponent<MainSceneLoadingTrigger>();
                }
                else if (root.name == "TitleGui")
                {
                    var canvas = root.GetComponent<Canvas>();
                    if (canvas == null) continue;
                    canvas.enabled = false;
                    _gameCanvas = canvas;
                }
            }
            Track.Attach(loader != null && loader._isMainSceneLoad ? loader : null, trigger);
            if (loader == null || trigger == null)
                RuntimeEntryPoint.LogSource?.LogWarning("The loading screen found no " + (loader == null ? "loader" : "opening trigger")
                    + " in the loading scene; it closes when the player is in the world.");
        }

        // The game's title pages are not a reason: the front end closes every one the frame it opens, so one seen here
        // has only just opened on the game's way through a start.
        private static string AbortReason(float now)
        {
            if (FrontEnd.ModalShowing) return "a game dialog opened";
            if (_titleBack) return "the title came back";
            if (!_scene && now >= _timeout) return "no world load started";
            return null;
        }

        private static void Lower(float now)
        {
            _state = State.Lowering;
            _loweredAt = now;
            _view.Lower(now, Track.Shown);
            Entered = _intent.Known ? _intent : null;
            var seconds = now - _raisedAt;
            if (_intent.Known) _pendingRecord = (_intent.Kind, CopyMarks());
            _inWorldAt = -1;
            RuntimeEntryPoint.LogSource?.LogInfo("Loading screen hidden after " + seconds.ToString("0.0", CultureInfo.InvariantCulture)
                + " s (" + Track.Ending + ").");
            PerformanceCapture.Mark("loading-screen", new { state = "lowered", path = _intent.Kind, ending = Track.Ending, seconds, shown = Track.Shown });
        }

        // The game's dialog or the title is readable at once; the menu comes back with its own arrival.
        private static void Abort(float now, string reason)
        {
            _state = State.Lowering;
            _loweredAt = now;
            _aborting = true;
            _view.Abort(now);
            RestoreGameCanvas();
            RuntimeEntryPoint.LogSource?.LogInfo("Loading screen gave way after " + (now - _raisedAt).ToString("0.0", CultureInfo.InvariantCulture)
                + " s: " + reason + ".");
            PerformanceCapture.Mark("loading-screen", new { state = "aborted", path = _intent.Kind, reason, phase = Track.Phase, shown = Track.Shown });
        }

        private static void Finish(float now)
        {
            _view.Hide();
            _state = State.Off;
            _intent = null;
            _gameCanvas = null;
            // A load that gave way at the title or in the lobby keeps its pictures, and the save they were prepared for,
            // for the same start again. After a world was played they go, and its season has likely moved on.
            if (_aborting && (FrontEnd.AtTitle || LobbyHost.Open)) return;
            _preferred = default;
            _releaseAt = now + ReleaseDelay;
            Seasons.Clear();
        }

        // At once, as when Modstoke's menus or this screen were switched off mid-load: the game's own screen shows.
        private static void Close()
        {
            RestoreGameCanvas();
            _view?.Hide();
            _state = State.Off;
            _intent = null;
            _gameCanvas = null;
            _releaseAt = Time.unscaledTime + ReleaseDelay;
        }

        private static void RestoreGameCanvas()
        {
            if (_gameCanvas != null && _gameCanvas) _gameCanvas.enabled = true;
        }

        private static void Fail(Exception error)
        {
            _failed = true;
            RuntimeEntryPoint.LogSource?.LogWarning("Modstoke's loading screen stopped and the game's own is back: " + error);
            // Each part on its own, so one that fails still leaves the game's canvas back and ours gone.
            Undo(RestoreGameCanvas);
            Undo(() => _view?.Destroy());
            Undo(LoadingArt.Release);
            _view = null;
            _state = State.Off;
        }

        private static void Undo(Action step)
        {
            try
            {
                step();
            }
            catch (Exception)
            {
                // Nothing further to undo.
            }
        }

        // Once per visit to the title or the lobby, a step a frame and each only while the menu is still, so none of it
        // shows as a hitch: settings, bindings and the record; the art index and the title picture; the game's button
        // icons (one search finds them all); the layer. After that the layer is kept built for the current screen size
        // and language (built again while still), its controls follow rebinding, and its picture is prepared.
        private static void Prepare(float now)
        {
            if (_recordRead is { IsCompleted: true })
            {
                _record = _recordRead.Status == TaskStatus.RanToCompletion ? _recordRead.Result : new Record();
                _recordRead = null;
                LoadingTips.History.Clear();
                LoadingTips.History.AddRange(_record.Tips);
                LoadingArt.Last = _record.Art ?? "";
            }
            var idle = Moving.Idle && now - _lastInput >= IdleInput;
            if (_prepared < 3 || !ViewCurrent)
            {
                if (!idle) return;
                switch (_prepared)
                {
                    case 0:
                        ReadSettings();
                        GameBindings.Load();
                        if (_record == null && _recordRead == null)
                        {
                            var path = RecordPath;
                            _recordRead = Task.Run(() => ReadRecord(path));
                        }
                        break;
                    case 1:
                        LoadingArt.Begin();
                        break;
                    case 2:
                        LoadingTips.FindGlyphs();
                        break;
                    default:
                        EnsureView();
                        break;
                }
                _prepared = Math.Min(_prepared + 1, 3);
                return;
            }
            if (now >= _nextControls)
            {
                _nextControls = now + ControlsSeconds;
                RefreshControls(false);
            }
            LoadingArt.Tick(idle);
        }

        // The layer as built still fits the screen size, the menu size and the language.
        private static bool ViewCurrent => _view is { Alive: true } && _view.Layout == FrontEnd.LayoutVersion && _view.Language == Languages.Current;

        // Built at the title so a start costs nothing; rebuilt there for a new screen size, menu size or language.
        private static bool EnsureView()
        {
            if (ViewCurrent || _view is { Alive: true } && _state != State.Off) return true;
            if (!MenuKit.FontsReady) return false;
            _view?.Destroy();
            // Not left on the old layer, should building the new one fail.
            _view = null;
            _view = new LoadingView();
            if (!GameBindings.Ready) GameBindings.Load();
            RefreshControls(true);
            return true;
        }

        // The reminder's keys, read again now and then at the title, so a rebind shows on the next load. The game's
        // icons for them and for every button a tip can show are looked for here, so a load never searches for one.
        private static void RefreshControls(bool force)
        {
            var changed = force;
            for (var i = 0; i < ControlActions.Length; i++)
            {
                var pad = ControlGlyphs.Game(ControlActions[i], BindingDevice.Pad);
                if (!pad.Bound && i == 2) pad = ControlGlyphs.Game("Utility", BindingDevice.Pad);
                var key = ControlGlyphs.Game(ControlActions[i], BindingDevice.Keyboard);
                changed |= pad != PadControls[i] || key != KeyControls[i];
                PadControls[i] = pad;
                KeyControls[i] = key;
            }
            if (!changed) return;
            LoadingTips.FindGlyphs();
            _view.SetControls(PadControls, KeyControls);
        }

        private static void PreferNow()
        {
            _preferAt = -1;
            var (type, id, gameId, hour) = _preferred;
            if (id == 0) return;
            ReadSeason(type, id);
            var season = Seasons.TryGetValue(id, out var known) ? known : -1;
            LoadingArt.Want(new ArtWish(LoadingIntent.WorldOf(gameId, type.ToString(), id), season, LoadingIntent.LightAt(hour)));
        }

        // The save folder comes from the game on this thread; the zip is read on a worker.
        private static void ReadSeason(SaveGameType type, uint id)
        {
            if (Seasons.ContainsKey(id) || SeasonReads.ContainsKey(id)) return;
            string folder;
            try
            {
                folder = SaveGameManager.GetSaveFolderForId(type, (int)id);
            }
            catch (Exception)
            {
                return;
            }
            if (string.IsNullOrEmpty(folder)) return;
            SeasonReads[id] = Task.Run(() => SaveSeason.Read(folder));
        }

        // Seasons read since the last look: the picture for a preferred save is prepared again with its season, and a
        // details line already up gains it.
        private static void TakeSeasons()
        {
            if (SeasonReads.Count == 0) return;
            List<uint> done = null;
            foreach (var (id, task) in SeasonReads)
                if (task.IsCompleted) (done ??= new List<uint>()).Add(id);
            if (done == null) return;
            foreach (var id in done)
            {
                var task = SeasonReads[id];
                SeasonReads.Remove(id);
                var season = task.Status == TaskStatus.RanToCompletion ? task.Result : -1;
                if (season < 0) continue;
                Seasons[id] = season;
                if (_state == State.Off)
                {
                    if (_preferred.Id == id && _preferAt < 0) PreferNow();
                    else if (_intent != null && _intent.SaveId == id) LoadingArt.Want(Wish(_intent with { Season = season }));
                }
                else if (_intent.SaveId == id && _intent.Season < 0)
                {
                    _intent = _intent with { Season = season };
                    _view.UpdateDetails(_intent.Details(), Time.unscaledTime);
                }
            }
        }

        // Next tip on RB or E, by press edges, so a button held through the raise does nothing. Reading stops once the
        // world's scenes are in, well before the game takes input back, so nothing reaches the cutscene or the world.
        // A press on either device switches the keys shown.
        private static void ReadInput(float now)
        {
            if (!_listening) return;
            if (Track.Complete || _state == State.Lowering)
            {
                _listening = false;
                return;
            }
            var padPressed = ReadPad(false);
            var keys = ReadKeys(false);
            var next = padPressed == 5;
            if (padPressed >= 0) _pad = true;
            // The old input also reports controller buttons as keys, so a key counts only with no button held.
            else if ((keys.Any || keys.Next) && Array.IndexOf(PadDown, true) < 0)
            {
                _pad = false;
                next = keys.Next;
            }
            if (next && !ConsoleOverlay.IsVisible) _view.NextTip(now);
        }

        // Keys by their held state, kept between reads like the pad's buttons: a press lasts several paints, but the
        // frame a key goes down is often one this screen does not paint. Whether any key, and E, went down since.
        private static (bool Any, bool Next) ReadKeys(bool baseline)
        {
            var any = UnityEngine.Input.anyKey;
            var next = UnityEngine.Input.GetKey(KeyCode.E);
            var pressed = (any && !_anyKeyDown && !baseline, next && !_nextKeyDown && !baseline);
            _anyKeyDown = any;
            _nextKeyDown = next;
            return pressed;
        }

        // The pad's buttons, kept between reads; returns the first newly pressed one (5 is RB), or -1.
        private static int ReadPad(bool baseline)
        {
            var pad = Gamepad.current;
            if (pad == null)
            {
                _padPointer = IntPtr.Zero;
                Array.Clear(PadDown);
                return -1;
            }
            if (pad.Pointer != _padPointer)
            {
                _padPointer = pad.Pointer;
                PadButtons[0] = pad.buttonSouth;
                PadButtons[1] = pad.buttonEast;
                PadButtons[2] = pad.buttonWest;
                PadButtons[3] = pad.buttonNorth;
                PadButtons[4] = pad.leftShoulder;
                PadButtons[5] = pad.rightShoulder;
                PadButtons[6] = pad.leftTrigger;
                PadButtons[7] = pad.rightTrigger;
                PadButtons[8] = pad.dpad.up;
                PadButtons[9] = pad.dpad.down;
                PadButtons[10] = pad.dpad.left;
                PadButtons[11] = pad.dpad.right;
                baseline = true;
            }
            var pressed = -1;
            for (var i = 0; i < PadButtons.Length; i++)
            {
                var down = PadButtons[i].isPressed;
                if (down && !PadDown[i] && !baseline && pressed < 0) pressed = i;
                PadDown[i] = down;
            }
            return pressed;
        }

        // The phase label to show, by its key under loading.phase.
        private static string PhaseLabel(float now)
        {
            var join = _intent.Session == LoadingSession.Join;
            return Track.Phase switch
            {
                0 => _intent.Session switch { LoadingSession.Host => "server", LoadingSession.Join => "connect", _ => "prepare" },
                1 => "stream",
                2 => "build",
                3 => join ? "joinWorld" : _intent.Fresh ? "place" : "restore",
                // A joiner's gap includes the network start, which waits on the host.
                _ => join && Track.PhaseSeconds(now) > 4f ? "waitHost" : "settle"
            };
        }

        private static ArtWish Wish(LoadingIntent intent) => new(intent.World, intent.Season, intent.Light);

        private static void ReadSettings()
        {
            _settingsRead = true;
            _enabled = RuntimePreferences.ReadBoolean("loadingScreen", true);
            var art = RuntimePreferences.ReadText("loadingArt", "all").ToLowerInvariant();
            LoadingArt.Setting = art is "modstoke" or "photos" ? art : "all";
        }

        private static float[] CopyMarks()
        {
            var marks = new float[Track.Marks.Count];
            for (var i = 0; i < marks.Length; i++) marks[i] = Track.Marks[i];
            return marks;
        }

        private static string RecordPath => Path.Combine(LoaderPaths.UserDataDirectory, "loading-screen.json");

        // The load's timings, the tips seen and the picture shown go to the record once the player has been in the
        // world a few seconds, when nothing is loading; the file is written on a worker.
        private static void WriteRecordWhenIn(float now)
        {
            if (_pendingRecord.Marks == null || _record == null) return;
            if (!GameRuntime.IsInWorld)
            {
                _inWorldAt = -1;
                return;
            }
            if (_inWorldAt < 0) _inWorldAt = now;
            if (now - _inWorldAt < RecordDelay) return;
            var (kind, marks) = _pendingRecord;
            _pendingRecord = default;
            if (!_record.Loads.TryGetValue(kind, out var loads)) _record.Loads[kind] = loads = new List<float[]>();
            loads.Add(marks);
            if (loads.Count > RecordLoads) loads.RemoveRange(0, loads.Count - RecordLoads);
            _record.Tips = new List<string>(LoadingTips.History);
            _record.Art = LoadingArt.Last;
            var json = JsonSerializer.Serialize(_record, RecordJson);
            var path = RecordPath;
            _recordWrite = Task.Run(() =>
            {
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, true);
            });
        }

        private static Record ReadRecord(string path)
        {
            try
            {
                if (!File.Exists(path)) return new Record();
                var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), RecordJson);
                if (record is not { Version: 1 }) return new Record();
                record.Loads ??= new Dictionary<string, List<float[]>>();
                record.Tips ??= new List<string>();
                return record;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return new Record();
            }
        }
    }
}
