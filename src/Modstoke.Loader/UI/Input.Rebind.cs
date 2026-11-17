// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using GameInput = Sons.Input.InputSystem;
using InputState = TheForest.Utils.InputState;

namespace Modstoke.Loader
{
    // A player's bindings are saved with the Loader's settings as "keybind.<id>.key" and "keybind.<id>.pad" lines, only
    // while they differ from what the keybind was registered with; an empty value means the default. Older Loaders
    // ignore the lines, and the enable switches already in the file keep their own keys.
    public static partial class InputBindings
    {
        private const string SavedPrefix = "keybind.", KeySuffix = ".key", PadSuffix = ".pad";
        private const string CaptureCommand = "bind:", ResetAllCommand = "reset-all";
        private const float CaptureTimeout = 5f, CaptureArmLimit = 1.5f, ClearHold = 0.8f, GameStateRelease = 0.15f, ReleaseLimit = 1f;
        private const int StartBit = 1 << 12;

        private static readonly KeyCode[] CaptureKeys = Enum.GetValues(typeof(KeyCode)).Cast<KeyCode>().Distinct()
            .Where(key => key != KeyCode.None && key != KeyCode.Escape && key != KeyCode.Mouse0 && key != KeyCode.Mouse1
                && key is not (>= KeyCode.RightCommand and <= KeyCode.RightWindows) && key < KeyCode.JoystickButton0)
            .ToArray();

        private static Dictionary<string, string> _saved;
        private static Capture _capture;
        private static string _captureResult = "";
        private static bool _awaitRelease, _gameStateOn;
        private static float _gameStateOffAt, _awaitReleaseUntil;
        private static KeyCode _releaseKey;
        private static EventSystem _navigation;
        private static bool _navigationWas;

        /// <summary>Reads one keybind without copying the whole registry, for prompts read every frame.</summary>
        public static bool TryGetKeybind(string id, out KeybindSnapshot keybind)
        {
            keybind = null;
            if (string.IsNullOrWhiteSpace(id))
                return false;
            lock (SyncRoot)
            {
                return Keybinds.TryGetValue(id, out keybind);
            }
        }

        private static bool Rebind(string id, KeyCode? key, PadBinding? pad)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            KeybindSnapshot updated;
            lock (SyncRoot)
            {
                if (!Keybinds.TryGetValue(id, out var existing))
                    return false;
                updated = existing.WithBinding(key ?? existing.Key, pad ?? existing.Pad);
                if (updated.Key == existing.Key && updated.Pad == existing.Pad)
                    return true;
                SetKeybindNoLock(updated);
                PublishServiceNoLock();
            }

            if (key != null)
                Save(updated.Id + KeySuffix, updated.Key == updated.DefaultKey ? "" : updated.Key.ToString());
            if (pad != null)
                Save(updated.Id + PadSuffix, updated.Pad == updated.DefaultPad ? "" : updated.Pad.ToString());
            return true;
        }

        private static KeybindSnapshot WithSavedNoLock(KeybindSnapshot keybind)
        {
            var saved = _saved ??= RuntimePreferences.ReadSettings(SavedPrefix);
            var key = keybind.Key;
            var pad = keybind.Pad;
            if (saved.TryGetValue(SavedPrefix + keybind.Id + KeySuffix, out var keyText) && TryParseKey(keyText, out var savedKey))
                key = savedKey;
            if (saved.TryGetValue(SavedPrefix + keybind.Id + PadSuffix, out var padText) && PadBinding.TryParse(padText, out var savedPad))
                pad = savedPad;
            return key == keybind.Key && pad == keybind.Pad ? keybind : keybind.WithBinding(key, pad);
        }

        private static void Save(string name, string value)
        {
            // The settings file is "key = value" lines; an ID that would break its line is kept for this session only.
            if (name.IndexOfAny(new[] { '=', '\r', '\n' }) >= 0)
                return;
            var key = SavedPrefix + name;
            lock (SyncRoot)
            {
                var saved = _saved ??= RuntimePreferences.ReadSettings(SavedPrefix);
                if (saved.TryGetValue(key, out var current) ? current == value : value.Length == 0)
                    return;
                saved[key] = value;
            }

            // Written outside the registry lock: the preferences hold their own while they call back into it.
            RuntimePreferences.WriteText(key, value, out _);
        }

        private static bool TryParseKey(string text, out KeyCode key)
            => Enum.TryParse(text, true, out key) && Enum.IsDefined(typeof(KeyCode), key);

        private static bool InvokeCommand(string commandId, out CommandResultSnapshot result)
        {
            if (string.Equals(commandId, ResetAllCommand, StringComparison.Ordinal))
            {
                CancelCapture(null, "");
                string[] ids;
                lock (SyncRoot)
                {
                    ids = Keybinds.Keys.ToArray();
                }

                foreach (var id in ids)
                    ResetToDefault(id);
                SetCaptureResult("Every binding is back to its default.");
                result = CommandResultSnapshot.Success("Every binding is back to its default.");
                return true;
            }

            if (commandId.StartsWith(CaptureCommand, StringComparison.Ordinal))
                return TryBeginCapture(commandId.Substring(CaptureCommand.Length), out result);

            result = CommandResultSnapshot.Failure("Unknown input command.");
            return false;
        }

        private static bool TryBeginCapture(string id, out CommandResultSnapshot result)
        {
            string name;
            lock (SyncRoot)
            {
                if (!Keybinds.TryGetValue(id, out var keybind))
                {
                    result = CommandResultSnapshot.Failure("That binding is no longer registered.");
                    return false;
                }

                name = keybind.DisplayName;
                // The menu's own submit can arrive again with the press being bound; the capture already has it.
                if (_capture != null && string.Equals(_capture.Id, keybind.Id, StringComparison.OrdinalIgnoreCase))
                {
                    result = CommandResultSnapshot.Success("Press a key or button for " + name + ".");
                    return true;
                }

                if (!InputPageOpen())
                {
                    result = CommandResultSnapshot.Failure("Open Input Bindings in the Mods menu to change a binding.");
                    return false;
                }

                _capture = new Capture(keybind.Id, name, Time.unscaledTime);
                _captureResult = "";
            }

            // The menus hear nothing while it waits: Modstoke's screens by their listening flag, the Mods menu's own
            // controls by the event system's navigation, and every keybind by the capture itself.
            FrontInput.Listening = true;
            SuspendNavigation();
            MenuRegistry.NotifyContentChanged(MenuProviderIds.InputBindings);
            result = CommandResultSnapshot.Success("Press a key or button for " + name + ".");
            return true;
        }

        private static void CancelCapture(string id, string reason)
        {
            lock (SyncRoot)
            {
                if (_capture == null || id != null && !string.Equals(_capture.Id, id, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            EndCapture(reason);
        }

        private static bool CaptureQuietNoLock() => _capture != null || _awaitRelease;

        private static bool ReleaseHeldNoLock()
        {
            try
            {
                return _padBits != 0 || UnityEngine.Input.GetKey(_releaseKey) || UnityEngine.Input.GetMouseButton(0)
                    || UnityEngine.Input.GetMouseButton(1);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool InputPageOpen()
            => string.Equals(MenuState.ProviderEntry?.Id, MenuProviderIds.InputBindings, StringComparison.OrdinalIgnoreCase);

        private static void StepCapture()
        {
            Capture capture;
            CaptureOutcome outcome;
            lock (SyncRoot)
            {
                if (_capture == null && !_awaitRelease && !_gameStateOn)
                    return;
                // Only the press that ended the capture is waited out, and not for long: a joystick or throttle that
                // always reports a button would otherwise keep every keybind and the menu's navigation off.
                if (_awaitRelease && _capture == null && (!ReleaseHeldNoLock() || Time.unscaledTime >= _awaitReleaseUntil))
                {
                    _awaitRelease = false;
                    ResumeNavigation();
                }

                ReleaseGameStateNoLock();
                capture = _capture;
                if (capture == null)
                    return;
                // The arm limit bounds this wait for anything held when the capture opened.
                var held = UnityEngine.Input.anyKey || _padBits != 0;
                outcome = InputPageOpen() ? capture.Step(Time.unscaledTime, held, _padBits, _padPrevious) : CaptureOutcome.Closed;
                if (outcome.Kind == CaptureKind.Listening)
                    HoldGameStateNoLock();
                // Escape ends every cancel and clear on the keyboard; a bound key is waited for itself.
                _releaseKey = outcome.Kind == CaptureKind.Key ? outcome.Key : KeyCode.Escape;
            }

            switch (outcome.Kind)
            {
                case CaptureKind.Waiting:
                case CaptureKind.Listening:
                    return;
                case CaptureKind.Key:
                    Rebind(capture.Id, outcome.Key, null);
                    EndCapture(capture.Name + ": key is now " + InputGlyphs.Name(outcome.Key) + "." + ConflictNote(capture.Id));
                    return;
                case CaptureKind.Pad:
                    Rebind(capture.Id, null, outcome.Pad);
                    EndCapture(capture.Name + ": controller is now " + InputGlyphs.Name(outcome.Pad) + "." + ConflictNote(capture.Id));
                    return;
                case CaptureKind.ClearKey:
                    // The Mods menu and its way back always keep a key, so the menu can be opened and left without a pad.
                    if (capture.Id is KeybindIds.ToggleModsMenu or KeybindIds.Back)
                    {
                        EndCapture(capture.Name + " keeps a key.");
                        return;
                    }

                    Rebind(capture.Id, KeyCode.None, null);
                    EndCapture(capture.Name + ": no key.");
                    return;
                case CaptureKind.ClearPad:
                    Rebind(capture.Id, null, PadBinding.None);
                    EndCapture(capture.Name + ": no controller button.");
                    return;
                case CaptureKind.TimedOut:
                    EndCapture("Nothing was pressed for " + capture.Name + ".");
                    return;
                case CaptureKind.Closed:
                    EndCapture("");
                    return;
                default:
                    EndCapture(capture.Name + " was left as it was.");
                    return;
            }
        }

        private static void EndCapture(string result)
        {
            lock (SyncRoot)
            {
                if (_capture == null)
                    return;
                _capture = null;
                _captureResult = result ?? "";
                // Keybinds stay quiet until the captured press is let go, so it never acts as the new binding.
                _awaitRelease = true;
                _awaitReleaseUntil = Time.unscaledTime + ReleaseLimit;
                if (_gameStateOn)
                    _gameStateOffAt = Time.unscaledTime + GameStateRelease;
            }

            FrontInput.Listening = false;
            FrontInput.Pause(GameStateRelease);
            FrontInput.AwaitRelease();
            MenuRegistry.NotifyContentChanged(MenuProviderIds.InputBindings);
        }

        private static void SetCaptureResult(string result)
        {
            lock (SyncRoot)
            {
                _captureResult = result;
            }

            MenuRegistry.NotifyContentChanged(MenuProviderIds.InputBindings);
        }

        private static string ConflictNote(string id)
        {
            var conflicts = ConflictNames(id);
            return conflicts.Length == 0 ? "" : " Also on " + conflicts + ".";
        }

        // A keybind that clashes on both devices is named once.
        private static string ConflictNames(string id) => string.Join(", ", GetConflicts(id).Select(conflict => conflict.ToString()).Distinct());

        // The game's own binding state, as its controls screen uses: every other action map is off while the press
        // is listened for, the pause menu's keys with them, and comes back a moment after.
        private static void HoldGameStateNoLock()
        {
            _gameStateOffAt = float.MaxValue;
            if (_gameStateOn)
                return;
            try
            {
                GameInput.SetState(InputState.InputBinding, true);
                _gameStateOn = true;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Input Bindings: the game's input could not be held for a capture: " + error.Message);
                _gameStateOffAt = 0;
            }
        }

        private static void ReleaseGameStateNoLock()
        {
            if (!_gameStateOn || _capture != null && _capture.Listening || Time.unscaledTime < _gameStateOffAt)
                return;
            _gameStateOn = false;
            try
            {
                GameInput.SetState(InputState.InputBinding, false);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Input Bindings: the game's input could not be given back: " + error.Message);
            }
        }

        private static void SuspendNavigation()
        {
            try
            {
                var system = EventSystem.current;
                if (_navigation != null || system == null || !system)
                    return;
                _navigation = system;
                _navigationWas = system.sendNavigationEvents;
                system.sendNavigationEvents = false;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Input Bindings: the menu's navigation could not be paused for a capture: " + error.Message);
            }
        }

        private static void ResumeNavigation()
        {
            var system = _navigation;
            _navigation = null;
            try
            {
                if (system != null && system)
                    system.sendNavigationEvents = _navigationWas;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Input Bindings: the menu's navigation could not be given back: " + error.Message);
            }
        }

        private enum CaptureKind { Waiting, Listening, Key, Pad, ClearKey, ClearPad, Cancelled, TimedOut, Closed }

        private readonly record struct CaptureOutcome(CaptureKind Kind, KeyCode Key = KeyCode.None, PadBinding Pad = default)
        {
            internal static readonly CaptureOutcome Closed = new(CaptureKind.Closed);
        }

        /// <summary>
        /// One wait for a key or button. It starts once the press that opened it is let go; the first key binds the
        /// keyboard side, a controller button binds on release, or a second button held with it makes a chord. Tapping
        /// Escape or Start cancels, holding either clears that device's side, and so does a click.
        /// </summary>
        private sealed class Capture
        {
            private readonly float _armedAt;
            private float _startedAt, _escapeSince = -1, _startSince = -1;
            private int _first = -1;

            internal Capture(string id, string name, float now)
            {
                Id = id;
                Name = name;
                _armedAt = now;
            }

            internal string Id { get; }
            internal string Name { get; }
            internal bool Listening { get; private set; }

            internal CaptureOutcome Step(float now, bool held, int padBits, int padPrevious)
            {
                if (!Listening)
                {
                    if (held && now - _armedAt < CaptureArmLimit)
                        return new CaptureOutcome(CaptureKind.Waiting);
                    Listening = true;
                    _startedAt = now;
                    return new CaptureOutcome(CaptureKind.Listening);
                }

                if (now - _startedAt >= CaptureTimeout)
                    return new CaptureOutcome(CaptureKind.TimedOut);

                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                    _escapeSince = now;
                if (_escapeSince >= 0)
                {
                    if (!UnityEngine.Input.GetKey(KeyCode.Escape))
                        return new CaptureOutcome(CaptureKind.Cancelled);
                    return now - _escapeSince >= ClearHold ? new CaptureOutcome(CaptureKind.ClearKey) : new CaptureOutcome(CaptureKind.Listening);
                }

                var pressed = padBits & ~padPrevious;
                if ((pressed & StartBit) != 0)
                    _startSince = now;
                if (_startSince >= 0)
                {
                    if ((padBits & StartBit) == 0)
                        return new CaptureOutcome(CaptureKind.Cancelled);
                    return now - _startSince >= ClearHold ? new CaptureOutcome(CaptureKind.ClearPad) : new CaptureOutcome(CaptureKind.Listening);
                }

                if (UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetMouseButtonDown(1))
                    return new CaptureOutcome(CaptureKind.Cancelled);

                if (UnityEngine.Input.anyKeyDown)
                {
                    foreach (var key in CaptureKeys)
                        if (UnityEngine.Input.GetKeyDown(key))
                            return new CaptureOutcome(CaptureKind.Key, key);
                }

                pressed &= ~StartBit;
                if (_first < 0)
                {
                    if (pressed == 0)
                        return new CaptureOutcome(CaptureKind.Listening);
                    _first = Lowest(pressed);
                    // Two buttons that land in the same frame are a chord already.
                    var rest = pressed & ~(1 << _first);
                    return rest == 0 ? new CaptureOutcome(CaptureKind.Listening)
                        : new CaptureOutcome(CaptureKind.Pad, Pad: PadBinding.FromMask((1 << _first) | (1 << Lowest(rest))));
                }

                var first = 1 << _first;
                var other = pressed & ~first;
                if (other != 0 && (padBits & first) != 0)
                    return new CaptureOutcome(CaptureKind.Pad, Pad: PadBinding.FromMask(first | (1 << Lowest(other))));
                return (padBits & first) == 0
                    ? new CaptureOutcome(CaptureKind.Pad, Pad: PadBinding.FromMask(first))
                    : new CaptureOutcome(CaptureKind.Listening);
            }

            private static int Lowest(int bits)
            {
                var index = 0;
                while ((bits & (1 << index)) == 0)
                    index++;
                return index;
            }
        }

        private static MenuContentSnapshot CreateContent()
        {
            KeybindSnapshot[] keybinds;
            string waiting, result;
            lock (SyncRoot)
            {
                keybinds = CreateSortedSnapshotNoLock();
                waiting = _capture?.Id;
                result = _captureResult;
            }

            var state = new List<MenuRowSnapshot>
            {
                new MenuRowSnapshot("Device in use", CurrentDevice == InputDeviceKind.Gamepad ? "Controller" : "Keyboard and mouse", "",
                    "Prompts follow the device you used last."),
                new MenuRowSnapshot("Context", ContextName(CurrentContext()), "", "Where keybinds answer right now."),
                waiting != null
                    ? new MenuRowSnapshot("Waiting", "Press a key or button", "PENDING",
                        "Hold two controller buttons together for a chord. Tap Esc or Start to cancel; hold either to clear that device.")
                    : new MenuRowSnapshot("Change a binding", "Select it", "",
                        "Then press the key or controller button for it; two buttons held together make a chord. Tap Esc or Start "
                        + "to cancel, or hold either to clear that device's binding.")
            };
            if (result.Length > 0)
                state.Add(new MenuRowSnapshot("Last change", result, "", ""));

            var sections = new List<MenuSectionSnapshot> { new MenuSectionSnapshot("Input", state) };
            foreach (var category in keybinds.GroupBy(keybind => keybind.Category, StringComparer.OrdinalIgnoreCase))
                sections.Add(new MenuSectionSnapshot(category.Key,
                    category.Select(keybind => BindingRow(keybind, string.Equals(keybind.Id, waiting, StringComparison.OrdinalIgnoreCase)))));
            return new MenuContentSnapshot("Input Bindings", "Keys and controller buttons for Modstoke and its mods.", sections);
        }

        private static MenuRowSnapshot BindingRow(KeybindSnapshot keybind, bool waiting)
        {
            var conflicts = ConflictNames(keybind.Id);
            var status = waiting ? "PENDING" : !keybind.Enabled ? "DISABLED" : conflicts.Length > 0 ? "CHECK" : "";
            var detail = new List<string> { ContextName(keybind.Context) };
            if (keybind.HoldSeconds > 0)
                detail.Add("hold " + keybind.HoldSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
            if (!keybind.IsDefault)
                detail.Add("default " + BindingName(keybind.DefaultKey, keybind.DefaultPad));
            if (conflicts.Length > 0)
                detail.Add("also on " + conflicts);
            var text = string.Join(" · ", detail);
            if (!string.IsNullOrWhiteSpace(keybind.Tooltip))
                text += ". " + keybind.Tooltip.Trim();
            return new MenuRowSnapshot(keybind.DisplayName, waiting ? "Press a key or button" : BindingName(keybind.Key, keybind.Pad),
                status, text, CaptureCommand + keybind.Id);
        }

        private static string BindingName(KeyCode key, PadBinding pad)
        {
            var names = new List<string>(2);
            if (key != KeyCode.None)
                names.Add(InputGlyphs.Name(key));
            if (!pad.IsNone)
                names.Add(InputGlyphs.Name(pad));
            return names.Count == 0 ? "Unbound" : string.Join(" · ", names);
        }

        private static string ContextName(string context) => NormalizeContext(context) switch
        {
            InputContexts.Any => "Anywhere",
            InputContexts.Title => "Title screen",
            InputContexts.Loading => "Loading",
            InputContexts.GameScene => "In a game",
            InputContexts.InWorld => "In the world",
            InputContexts.NotInWorld => "Outside the world",
            InputContexts.GameControlsOn => "On foot",
            InputContexts.GameControlsOff => "In a seat or device",
            var other => other
        };

        private sealed class Provider : IMenuContentProvider, ICommandProvider
        {
            public string Id => MenuProviderIds.InputBindings;
            public string DisplayName => "Input Bindings";
            public string Kind => MenuProviderKinds.Settings;
            public int Priority => 320;
            public string Source => "Modstoke Loader";
            public string Description => "Change the keys and controller buttons of Modstoke and its mods.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent() => InputBindings.CreateContent();

            public IReadOnlyList<CommandSnapshot> GetCommands() => new[]
            {
                new CommandSnapshot(ResetAllCommand, "Reset all bindings",
                    "Put every keybind's key and controller button back to its default.", "", true, false)
            };

            public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
                => InputBindings.InvokeCommand(commandId ?? "", out result);
        }
    }
}
