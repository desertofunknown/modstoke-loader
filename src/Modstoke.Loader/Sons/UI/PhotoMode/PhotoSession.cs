// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modstoke.Localization;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using Sons.Gui.Options;
using Sons.Settings;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Loader
{
    /// <summary>
    /// One visit to photo mode, from its set-up to everything being put back. Each part it changes in the game
    /// records how to undo that as it is made; any exception in its frame work or a capture ends the session, runs
    /// the undo log and leaves the game as it was, so the front end itself never sees the fault. Online the world
    /// keeps going, and getting hurt ends the visit.
    /// </summary>
    internal sealed class PhotoSession
    {
        private const float VitalsSeconds = 0.25f, FocusRate = 6f, RumbleSeconds = 0.09f, JumpMetres = 1f, JumpDegrees = 30f;
        private const int SmallMemoryMb = 6000;
        private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        private static PhotoCapture _capture;
        private static bool _captureFaulted;
        private static string _rumbleKey;
        private static bool _rumbleDefault = true;

        private readonly PhotoUndo _undo = new();
        private Camera _main;
        private Transform _eye;
        private PhotoCamera _rig;
        private PhotoSubject _subject;
        private PhotoRenderHooks _hooks;
        private PhotoUiHider _hider;
        private PhotoOverlay _overlay;
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private float _focus = 10f, _nextVitals, _health = float.NaN, _rumbleUntil = -1;
        private bool _cursorHeld, _cursorWasVisible, _subjectStale, _lookLogged;
        private CursorLockMode _cursorWasLocked;
        private string _shotResolution;
        private PhotoShotRequest _queued;

        private PhotoSession(bool fromWorld)
        {
            FromWorld = fromWorld;
            Settings = PhotoSettings.Shared;
        }

        internal static PhotoSession Current { get; private set; }
        internal static PhotoResult LastResult { get; private set; }
        internal static string LastProblem { get; private set; }

        internal PhotoSettings Settings { get; }
        internal PhotoLook Look { get; private set; }
        internal PhotoWorld World { get; private set; }
        internal PhotoScreen Screen { get; set; }
        internal bool FromWorld { get; }
        internal bool Online { get; private set; }
        internal bool Ended { get; private set; }
        /// <summary>The focal length of the view photo mode opened on, the Focal length row's default.</summary>
        internal float EntryFocal { get; private set; } = 17f;
        /// <summary>What was at the centre when depth of field was first switched on, the Focus distance row's default.</summary>
        internal float FocusDefault { get; private set; } = 10f;
        internal IReadOnlyList<(string Id, int Width, int Height)> Resolutions { get; private set; } = Array.Empty<(string, int, int)>();
        internal string DefaultResolution { get; private set; } = "screen";
        /// <summary>A photo is waiting for the end of the frame, or still developing.</summary>
        internal bool Busy => _queued != null || _capture != null && _capture.Busy;

        /// <summary>The line over Take photo: what the photo will be, or that one is developing.</summary>
        internal string CaptureNote
        {
            get
            {
                var text = FrontEnd.Text;
                if (Busy) return text["photo.developing"];
                var (width, height) = ShotSize();
                var crop = PhotoOverlay.Crop(width, height, Settings.AspectRatio);
                var culture = Languages.CultureOf(Languages.Current);
                var note = text.Get("photo.unit.size", ("width", crop.width.ToString("0", culture)), ("height", crop.height.ToString("0", culture))) + "  ·  PNG";
                return Settings.Steam && SteamRunning() ? note + "  ·  Steam" : note;
            }
        }

        /// <summary>The capture shared by every visit, so a photo still developing finishes after photo mode closes.</summary>
        internal static PhotoCapture Capture
        {
            get
            {
                if (_capture != null) return _capture;
                _capture = new PhotoCapture();
                _capture.Completed += Completed;
                return _capture;
            }
        }

        /// <summary>Sets photo mode up over the pause menu; null, with the reason for the player, when it cannot.</summary>
        internal static PhotoSession Begin(bool fromWorld, out string reason)
        {
            reason = null;
            if (Current != null) return null;
            if (!PhotoGate.CanOpen(out reason, fresh: true)) return null;
            var session = new PhotoSession(fromWorld);
            try
            {
                session.Build();
                Current = session;
                return session;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogError("Photo mode could not open: " + error);
                session.Ended = true;
                session._undo.Run();
                PhotoGate.Failed();
                LastProblem = error.Message;
                reason = FrontEnd.Text["photo.closedError"];
                return null;
            }
        }

        /// <summary>Ends the open session, if any, putting everything back.</summary>
        internal static void EndCurrent(string why)
        {
            try { Current?.EndNow(why); }
            catch (Exception error) { RuntimeEntryPoint.LogSource?.LogError("Photo mode did not close cleanly: " + error); }
        }

        /// <summary>Ends the open session on a fault; never throws, as it runs from game hooks.</summary>
        internal static void Abort(Exception error)
        {
            try { Current?.Fail(error); }
            catch (Exception) { /* The undo log has run as far as it could. */ }
        }

        /// <summary>The front end's check each frame: the session lives only while its screen is on top of the pause menu.</summary>
        internal static void Watch(bool showing)
        {
            if (!showing && Current != null) EndCurrent("the pause screen no longer shows it");
        }

        /// <summary>
        /// Takes the photo asked for this frame, once the grass is queued for it: right after the grass draw
        /// (<paramref name="afterGrass"/>), or from the Loader's late update where the grass leaves the photo pose
        /// alone or has not drawn for a frame. Waits while the look or the world is still settling. Never throws, as
        /// both callers run inside the game's late update.
        /// </summary>
        internal static void DevelopQueued(bool afterGrass)
        {
            try
            {
                var session = Current;
                if (session == null || session._queued == null || session.Ended || session.Look == null || !session.Look.Settled
                    || !session.World.Settled) return;
                // The grass draws later this frame unless it has stopped drawing.
                if (!afterGrass && PhotoPatches.GrassFollows && PhotoPatches.GrassFrame >= Time.frameCount - 1) return;
                session.Develop();
            }
            catch (Exception error)
            {
                Abort(error);
            }
        }

        /// <summary>Drives a photo still developing after photo mode closed, and the files it hands back.</summary>
        internal static void TickCapture()
        {
            if (_capture == null || _captureFaulted) return;
            try
            {
                _capture.Tick();
            }
            catch (Exception error)
            {
                _captureFaulted = true;
                LastProblem = error.Message;
                RuntimeEntryPoint.LogSource?.LogError("Photo capture stopped: " + error);
                Abort(error);
            }
        }

        /// <summary>The front end is stopping: photo mode closes and the capture lets its buffers go.</summary>
        internal static void Shutdown()
        {
            EndCurrent("the menus stopped");
            try { _capture?.Dispose(); }
            catch (Exception error) { RuntimeEntryPoint.LogSource?.LogWarning("Photo capture did not release cleanly: " + error.Message); }
            _capture = null;
            _captureFaulted = false;
        }

        // Each part registers its undo as it is made, so a throw part way unwinds only what exists.
        private void Build()
        {
            _main = LocalPlayer.MainCam;
            _eye = LocalPlayer.MainCamTr;
            if (_main == null || !_main || _eye == null || !_eye) throw new InvalidOperationException("the player's camera is not there");
            Online = PauseHost.Multiplayer;
            _undo.Add("photo mode's state", () => PhotoMode.Set(false, false));

            _rig = new PhotoCamera(_eye, _main.fieldOfView);
            EntryFocal = _rig.Focal;
            Settings.Focal = EntryFocal;
            Settings.Roll = 0;
            _lastPosition = _rig.Position;
            _lastRotation = _rig.Rotation;
            _focus = float.IsNaN(Settings.FocusDistance) ? 10f : Settings.FocusDistance;

            _subject = new PhotoSubject();
            _subject.Collect();
            var main = _main;
            _hooks = new PhotoRenderHooks(_main, _rig, _subject);
            _undo.Add("the camera", () =>
            {
                _hooks.Dispose();
                if (main != null && main) HDCamera.GetOrCreate(main, 0).Reset();
            });
            Look = new PhotoLook(_main);
            _undo.Add("the photo look", Look.Dispose);
            // Reads only; the world changes, and records its undo, when the World tab is first used.
            Settings.ResetWorld();
            World = new PhotoWorld(_undo, _eye, Online);
            _hider = new PhotoUiHider(_undo);
            _hider.Begin(Online);
            _overlay = new PhotoOverlay(Online);
            _undo.Add("the photo overlay", _overlay.Dispose);

            FrontInput.StickNavigates = false;
            FrontInput.SpaceConfirms = false;
            _undo.Add("the menu controls", () => FrontInput.StickNavigates = FrontInput.SpaceConfirms = true);
            var visible = Cursor.visible;
            var lockState = Cursor.lockState;
            _undo.Add("the cursor", () =>
            {
                Cursor.lockState = lockState;
                Cursor.visible = visible;
            });
            _undo.Add("the controller's rumble", () => Rumble(0, 0));

            Resolutions = PhotoCapture.Resolutions(UnityEngine.Screen.width, UnityEngine.Screen.height) ?? Array.Empty<(string, int, int)>();
            DefaultResolution = Resolutions.Any(size => size.Id == "4k") && SystemInfo.graphicsMemorySize >= SmallMemoryMb ? "4k"
                : Resolutions.Count > 0 ? Resolutions[0].Id : "screen";
            Capture.Completed += Show;
            _undo.Add("the photo card", () => Capture.Completed -= Show);

            ReadHealth();
            Look.Apply(Settings);
            _hooks.Armed = true;
            PhotoInput.Pause();
            PhotoMode.Set(true, !Online);
            RuntimeEntryPoint.LogSource?.LogInfo("Photo mode opened" + (Online ? " online" : "") + (FromWorld ? " from the photo key" : "")
                + ": " + _subject.Describe() + (PhotoPatches.GrassFollows ? "" : "; grass follows the player's view") + ".");
        }

        /// <summary>One frame of photo mode, from the panel's tick.</summary>
        internal void Tick(bool hidden, bool overPanel, bool panelBusy)
        {
            if (Ended) return;
            try
            {
                if (_hooks.Fault != null) throw new InvalidOperationException("a render callback failed", _hooks.Fault);
                // The pose is only ever on the camera inside a render; this keeps it so if a frame was cut short.
                _hooks.RestoreIfApplied();
                if (_main == null || !_main || _eye == null || !_eye || Camera.main == null || Camera.main.Pointer != _main.Pointer)
                {
                    EndNow("the view moved to another camera");
                    return;
                }
                var dt = Time.unscaledDeltaTime;
                var input = PhotoInput.Read(hidden, overPanel, panelBusy, _rig.Fov);
                if (input.Hide) Screen?.ToggleHidden();
                if (input.Return) ReturnToPlayer();
                if (input.NextGuide)
                {
                    var index = Array.IndexOf(PhotoSettings.Guides, Settings.Guide);
                    Settings.Guide = PhotoSettings.Guides[(index + 1) % PhotoSettings.Guides.Length];
                    MenuSound.Play(MenuCue.Move);
                    Screen?.Repaint();
                }
                if (input.Autofocus) Autofocus();
                HoldCursor(input.Dragging);

                _rig.Tick(input, Settings, _eye, dt);
                if (Vector3.Distance(_rig.Position, _lastPosition) > JumpMetres || Quaternion.Angle(_rig.Rotation, _lastRotation) > JumpDegrees)
                    _hooks.ResetHistory();
                _lastPosition = _rig.Position;
                _lastRotation = _rig.Rotation;

                if (_subjectStale)
                {
                    _subjectStale = false;
                    _subject.Collect();
                }
                _subject.ShowPlayer = Settings.ShowPlayer;
                _subject.ShowHeld = Settings.ShowHeld;
                // The head is where the eye is now: online the player can be carried off while photo mode is open.
                _subject.HeadClear = PhotoSubject.IsClear(_rig.Position, _rig.Anchor);

                var follow = Settings.Dof && Settings.FollowCentre;
                if (follow) _focus = Mathf.Lerp(_focus, _rig.FocusDistance(Settings) ?? 300f, 1f - Mathf.Exp(-FocusRate * dt));
                else _focus = float.IsNaN(Settings.FocusDistance) ? 10f : Settings.FocusDistance;
                _hider.Tick(dt, !Settings.ScreenEffects);
                if (World.Apply(Settings)) _hooks.ResetHistory();
                if (Look.Settle(_hider.Clear, !Settings.ScreenEffects)) Settled();
                Look.Focal = _rig.Focal;
                Look.FocusDistance = _focus;
                Look.Apply(Settings);
                _overlay.Tick(Settings, hidden, follow);

                if (_rumbleUntil >= 0 && Time.unscaledTime >= _rumbleUntil)
                {
                    _rumbleUntil = -1;
                    Rumble(0, 0);
                }
                if (Online && Time.unscaledTime >= _nextVitals) CheckVitals();
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        /// <summary>
        /// Asks for a photo of the view as it stands; it is taken at the end of this frame, once the grass is queued
        /// for it. False with the reason for the player when one is already on its way.
        /// </summary>
        internal bool Shoot(out string reason)
        {
            reason = null;
            if (Ended) return false;
            try
            {
                if (Busy)
                {
                    reason = FrontEnd.Text["photo.developing"];
                    return false;
                }
                var (width, height) = ShotSize();
                _queued = new PhotoShotRequest(_main, width, height, Settings.Fine ? 12 : 4,
                    PhotoOverlay.Crop(width, height, Settings.AspectRatio), Describe(), Metadata(width, height), Settings.Pictures,
                    Settings.Steam && SteamRunning());
                MenuSound.Play(MenuCue.Activate);
                return true;
            }
            catch (Exception error)
            {
                Fail(error);
                return false;
            }
            finally
            {
                _shotResolution = null;
            }
        }

        // The capture renders inside this call, through the render hooks, with the look the preview shows.
        private void Develop()
        {
            var request = _queued;
            _queued = null;
            if (Ended || request == null) return;
            try
            {
                var started = Capture.TryShoot(request, out var reason);
                // The capture's reason is already a sentence for the player.
                if (!started)
                {
                    LastProblem = reason;
                    Screen?.Refused(reason);
                    return;
                }
                _overlay.Flash();
                _overlay.Developing();
                if (RumbleOn()) Rumble(0.15f, 0.35f);
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        /// <summary>Puts everything back. Safe to call more than once.</summary>
        internal void EndNow(string why)
        {
            if (Ended) return;
            Ended = true;
            if (ReferenceEquals(Current, this)) Current = null;
            _undo.Run();
            _capture?.ShrinkBuffers();
            // With everything back, the camera should stand where the game last left it.
            var drift = _eye != null && _eye && _rig != null ? Vector3.Distance(_eye.position, _rig.Anchor) : 0f;
            RuntimeEntryPoint.LogSource?.LogInfo("Photo mode closed: " + why + string.Format(CultureInfo.InvariantCulture,
                "; the camera is {0:0.###} m from where the game left it", drift)
                + (_rig != null && _rig.HitLayers.Length > 0 ? "; the camera met " + _rig.HitLayers : "") + ".");
        }

        /// <summary>Ends the session on a fault, puts the game back and tells the player.</summary>
        internal void Fail(Exception error)
        {
            if (Ended) return;
            LastProblem = error.Message;
            RuntimeEntryPoint.LogSource?.LogError("Photo mode closed after a problem: " + error);
            EndNow("a problem");
            PhotoGate.Failed();
            PauseScreen.Notice(FrontEnd.Text["photo.closedError"]);
        }

        internal void ReturnToPlayer()
        {
            if (Ended) return;
            _rig.Return(_eye);
            _hooks.ResetHistory();
            MenuSound.Play(MenuCue.Forward);
        }

        internal void ResetLook()
        {
            Settings.ResetLook();
            Screen?.Repaint();
        }

        internal void ResetWorld()
        {
            Settings.ResetWorld();
            Screen?.Repaint();
        }

        /// <summary>Depth of field on or off; the first time, it focuses on whatever is at the centre.</summary>
        internal void SetDepthOfField(bool on)
        {
            Settings.Dof = on;
            if (!on || !float.IsNaN(Settings.FocusDistance)) return;
            FocusDefault = _rig?.FocusDistance(Settings) ?? 10f;
            Settings.FocusDistance = FocusDefault;
        }

        internal void RowChanged(string id)
        {
            if (id is "player" or "held") _subjectStale = true;
            if (id is "resolution" or "detail" or "folder" or "steam") Settings.SaveCapture();
        }

        /// <summary>The console's "photo" command: open, close, shoot [size], world [setting value] and status.</summary>
        internal static string RunCommand(string arguments)
        {
            var words = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var verb = words.Length > 0 ? words[0].ToLowerInvariant() : "status";
            switch (verb)
            {
                case "world":
                    return Current == null ? "Photo mode is not open." : Current.WorldCommand(words);
                case "open":
                    if (Current != null) return "Photo mode is already open.";
                    if (PauseHost.Open) return PhotoScreen.Open(false) ? "Photo mode opened." : "Photo mode did not open: " + (LastProblem ?? "see the pause screen.");
                    var menu = PauseHost.Current;
                    if (menu == null) return "There is no pause menu here to open photo mode in.";
                    if (!FrontEnd.Enabled) return "Photo mode needs Modstoke's menus; classicMenus is on.";
                    FrontEnd.OverPause(() => PhotoScreen.Open(fromWorld: true));
                    menu.Open();
                    return "Opening the pause menu with photo mode over it.";
                case "close":
                    if (Current == null) return "Photo mode is not open.";
                    var fromWorld = Current.FromWorld;
                    EndCurrent("closed from the console");
                    if (fromWorld) PauseHost.Resume();
                    return "Photo mode closed.";
                case "shoot":
                    var session = Current;
                    if (session == null) return "Photo mode is not open.";
                    if (words.Length > 1)
                    {
                        var wanted = words[1].ToLowerInvariant();
                        if (session.Resolutions.All(size => size.Id != wanted))
                            return "No size \"" + wanted + "\" here; this machine offers " + string.Join(", ", session.Resolutions.Select(size => size.Id)) + ".";
                        session._shotResolution = wanted;
                    }
                    var note = session.CaptureNote;
                    return session.Shoot(out var reason) ? "Taking a photo: " + note + "." : "No photo: " + (reason ?? LastProblem ?? "unknown");
                default:
                    return Status();
            }
        }

        // "photo world time 18:30", "weather rain", "clouds 0.6", "fog 0.4" (or "asis" for any of them) and "reset";
        // the change shows from the next frame, as a row's would.
        private string WorldCommand(string[] words)
        {
            if (World.Unavailable != null) return "The World tab is off here: " + FrontEnd.Text[World.Unavailable];
            var setting = words.Length > 1 ? words[1].ToLowerInvariant() : "";
            var value = words.Length > 2 ? words[2].ToLowerInvariant() : "";
            var asIs = value == "asis";
            var number = float.NaN;
            switch (setting)
            {
                case "time":
                    if (!World.CanSetTime) return "The game holds the time of day here.";
                    if (asIs) Settings.Hour = float.NaN;
                    else if (TimeSpan.TryParseExact(value, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time) && time.TotalHours < 24)
                        Settings.Hour = (float)time.TotalHours;
                    else return "Give the time as 18:30.";
                    break;
                case "weather":
                    if (!World.CanSetWeather) return "There is no weather here to change.";
                    if (Array.IndexOf(World.Weathers, value) < 0)
                        return (Array.IndexOf(PhotoSettings.Weathers, value) < 0 ? "No weather \"" + value + "\"" : "The game keeps rain and snow out here")
                            + "; choose " + string.Join(", ", World.Weathers) + ".";
                    Settings.ChooseWeather(value);
                    break;
                case "clouds" or "fog":
                    if (!asIs && (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || number < 0 || number > 1))
                        return "Give " + setting + " as a number from 0 to 1.";
                    if (setting == "clouds") Settings.Clouds = number;
                    else Settings.Fog = number;
                    break;
                case "reset":
                    Settings.ResetWorld();
                    break;
                case "":
                    return "World: " + World.Describe() + ".";
                default:
                    return "Use photo world time 18:30, weather " + string.Join("|", World.Weathers) + ", clouds 0.6, fog 0.4 or reset.";
            }
            Screen?.Repaint();
            return "World set; it shows from the next frame.";
        }

        private static string Status()
        {
            var parts = new List<string>
            {
                Current == null ? "closed" : "open" + (Current.Online ? " online" : " in single player") + (PhotoMode.WorldFrozen ? ", world frozen" : ""),
                "grass " + (PhotoPatches.GrassFollows ? "follows the photo camera" : "follows the player's view"),
                "capture " + (_capture == null ? "not used yet" : string.IsNullOrEmpty(_capture.Status) ? "idle" : _capture.Status)
            };
            if (LastResult != null)
                parts.Add(LastResult.Saved ? "last photo " + LastResult.Path + (LastResult.InSteam ? " (in Steam)" : "") : "last photo failed: " + LastResult.Error);
            var timings = Timings.Snapshot();
            var times = new[] { "Burst", "Readback", "Encode" }
                .Where(phase => timings.ContainsKey("Photo." + phase))
                .Select(phase => string.Format(CultureInfo.InvariantCulture, "{0} {1:0} ms", phase.ToLowerInvariant(), timings["Photo." + phase].LastDurationMs))
                .ToArray();
            if (times.Length > 0) parts.Add("last " + string.Join(", ", times));
            if (!string.IsNullOrEmpty(LastProblem)) parts.Add("last problem: " + LastProblem);
            if (Current?._rig != null) parts.Add(string.Format(CultureInfo.InvariantCulture, "camera {0:0.0} m from the player at {1:0} mm",
                Vector3.Distance(Current._rig.Position, Current._rig.Anchor), Current._rig.Focal));
            if (Current?.World != null) parts.Add("world " + Current.World.Describe());
            return "Photo mode: " + string.Join("; ", parts) + ".";
        }

        private static void Completed(PhotoResult result)
        {
            LastResult = result;
            if (result == null) return;
            if (!result.Saved) LastProblem = result.Error;
            RuntimeEntryPoint.LogSource?.LogInfo(result.Saved
                ? "Photo saved to " + result.Path + (result.InSteam ? " and added to Steam's screenshots." : ".")
                : "Photo could not be saved: " + result.Error);
        }

        private void Show(PhotoResult result)
        {
            if (!Ended) _overlay?.Show(result);
        }

        private (int Width, int Height) ShotSize()
        {
            var id = _shotResolution ?? Settings.Resolution;
            foreach (var size in Resolutions)
                if (size.Id == id) return (size.Width, size.Height);
            foreach (var size in Resolutions)
                if (size.Id == DefaultResolution) return (size.Width, size.Height);
            return (UnityEngine.Screen.width, UnityEngine.Screen.height);
        }

        // The look's base values were read again; the rows that start from them show the new ones.
        private void Settled()
        {
            if (!_lookLogged)
            {
                _lookLogged = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Photo mode read the game's look: " + Look.DescribeBase() + ".");
            }
            Screen?.Repaint();
        }

        private void Autofocus()
        {
            var distance = _rig.FocusDistance(Settings);
            _overlay.Focused();
            if (distance == null)
            {
                MenuSound.Play(MenuCue.Back);
                return;
            }
            _focus = distance.Value;
            Settings.FocusDistance = distance.Value;
            if (float.IsNaN(FocusDefault)) FocusDefault = distance.Value;
            MenuSound.Play(MenuCue.Move);
            Screen?.Repaint();
        }

        // The cursor is held still and hidden while the mouse turns the camera, and given back as it was when that
        // stops: on a controller the game keeps it hidden and locked all along.
        private void HoldCursor(bool dragging)
        {
            if (dragging)
            {
                if (!_cursorHeld)
                {
                    _cursorWasVisible = Cursor.visible;
                    _cursorWasLocked = Cursor.lockState;
                    _cursorHeld = true;
                }
                if (Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
                if (Cursor.visible) Cursor.visible = false;
                return;
            }
            if (!_cursorHeld) return;
            _cursorHeld = false;
            Cursor.lockState = _cursorWasLocked;
            Cursor.visible = _cursorWasVisible;
        }

        // Online the world keeps going; a hit, being downed or dying ends the visit.
        private void CheckVitals()
        {
            _nextVitals = Time.unscaledTime + VitalsSeconds;
            if (!LocalPlayerReads.TryReadVitals(out var vitals, out _)) return;
            var hurt = vitals.IsDowned || vitals.HasDied || vitals.LifeState is { } life && life != PlayerLifeState.Alive
                || !float.IsNaN(_health) && vitals.Health.Current < _health - 0.01f;
            _health = float.IsNaN(_health) ? vitals.Health.Current : Mathf.Min(_health, vitals.Health.Current);
            if (!hurt) return;
            EndNow("the player was hurt");
            PauseScreen.Notice(FrontEnd.Text["photo.closedHurt"]);
        }

        private void ReadHealth()
        {
            if (Online && LocalPlayerReads.TryReadVitals(out var vitals, out _)) _health = vitals.Health.Current;
        }

        // "Day 12 · 18:40 · Spring", in the player's language, for the photo's description and Steam's location.
        private static string Describe()
        {
            try
            {
                if (!EnvironmentReads.TryReadClock(out var clock, out _)) return "";
                var culture = Languages.CultureOf(Languages.Current);
                var time = clock.EffectiveTime;
                var text = FrontEnd.Text.Get("photo.location", ("day", time.Days.ToString(culture)),
                    ("time", DateTime.Today.Add(new TimeSpan(time.Hours, time.Minutes, 0)).ToString("t", culture)));
                if (EnvironmentReads.TryReadSeason(out var season, out _) && season.ActiveSeason is { } active)
                    text += " · " + FrontEnd.Text["options.value." + active.ToString().ToLowerInvariant()];
                return text;
            }
            catch (Exception)
            {
                return "";
            }
        }

        // How the photo was made, so the shot can be set up again: camera, lens, look, capture and world. No player
        // names or Steam IDs, as photos are shared.
        private string Metadata(int width, int height)
        {
            var world = new Dictionary<string, object>();
            // The world as its save knows it, so its photos can be found again when it loads: the game id the save keeps
            // as GameId (multiplayer worlds only) and the save slot, which is all a single player world has.
            if (GameSetupManager.TryGetInstance(out var setup) && setup)
            {
                if (setup._gameId is { Length: > 0 } gameId) world["gameId"] = gameId;
                if (setup._saveId != 0)
                {
                    world["saveType"] = setup._saveGameType.ToString();
                    world["saveId"] = setup._saveId;
                }
            }
            // The game clears its own slot once the world is in; the loading screen kept the save it loaded.
            if (!world.ContainsKey("saveId") && LoadingScreen.Entered is { SaveId: not 0 } entered)
            {
                world["saveType"] = entered.Type.ToString();
                world["saveId"] = entered.SaveId;
                if (!world.ContainsKey("gameId") && entered.GameId.Length > 0) world["gameId"] = entered.GameId;
            }
            if (EnvironmentReads.TryReadClock(out var clock, out _)) world["time"] = clock.EffectiveTime.ToString("c", CultureInfo.InvariantCulture);
            if (EnvironmentReads.TryReadSeason(out var season, out _)) world["season"] = season.ActiveSeason?.ToString();
            if (EnvironmentReads.TryReadWeather(out var weather, out _))
            {
                world["precipitation"] = weather.Precipitation?.ToString();
                world["clouds"] = weather.CloudState?.ToString();
                world["snowing"] = weather.IsSnowing;
            }
            var position = _rig.Position;
            var record = new Dictionary<string, object>
            {
                ["camera"] = new Dictionary<string, object>
                {
                    ["position"] = new[] { position.x, position.y, position.z },
                    ["yaw"] = _rig.Yaw, ["pitch"] = _rig.Pitch, ["roll"] = _rig.Roll, ["focalLength"] = _rig.Focal, ["fov"] = _rig.Fov
                },
                ["settings"] = Settings.Describe(),
                ["capture"] = new Dictionary<string, object> { ["width"] = width, ["height"] = height, ["renders"] = Settings.Fine ? 12 : 4 },
                ["world"] = world,
                ["online"] = Online,
                ["gameAssembly"] = GameBuild.AssemblySha256,
                ["modstoke"] = RuntimeEntryPoint.Version
            };
            return JsonSerializer.Serialize(record, JsonOptions);
        }

        private static bool SteamRunning()
        {
            try { return Steamworks.SteamAPI.IsSteamRunning(); }
            catch (Exception) { return false; }
        }

        // The game's own rumble setting decides whether the shutter is felt.
        private static bool RumbleOn()
        {
            try
            {
                if (_rumbleKey == null)
                {
                    var option = new RumbleOptionGui();
                    _rumbleKey = option.SettingKey ?? "";
                    _rumbleDefault = option.GetDefaultBoolValue();
                }
                return _rumbleKey.Length > 0 && GameSettingsManager.GetSetting(_rumbleKey, _rumbleDefault);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Rumble(float low, float high)
        {
            try
            {
                var pad = Gamepad.current;
                if (pad == null) return;
                pad.SetMotorSpeeds(low, high);
                _rumbleUntil = low > 0 || high > 0 ? Time.unscaledTime + RumbleSeconds : -1;
            }
            catch (Exception)
            {
                // No rumble then.
            }
        }
    }
}
