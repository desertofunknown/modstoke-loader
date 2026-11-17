// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Modstoke.Loader
{
    /// <summary>The device a binding or prompt is for; the mouse counts as keyboard.</summary>
    public enum InputDeviceKind
    {
        Keyboard,
        Gamepad
    }

    /// <summary>A keybind's controller side: one button, two buttons held together, or nothing.</summary>
    /// <remarks>
    /// Saved and parsed by the button names in <see cref="ToString"/>, such as "South" or "LeftShoulder+RightShoulder";
    /// <see cref="TryParse"/> also takes the Xbox names ("A", "LB", "View").
    /// </remarks>
    public readonly struct PadBinding : IEquatable<PadBinding>
    {
        internal static readonly GamepadButton[] Buttons =
        {
            GamepadButton.DpadUp, GamepadButton.DpadDown, GamepadButton.DpadLeft, GamepadButton.DpadRight,
            GamepadButton.North, GamepadButton.East, GamepadButton.South, GamepadButton.West,
            GamepadButton.LeftStick, GamepadButton.RightStick, GamepadButton.LeftShoulder, GamepadButton.RightShoulder,
            GamepadButton.Start, GamepadButton.Select, GamepadButton.LeftTrigger, GamepadButton.RightTrigger
        };

        // GamepadButton has aliases (A is South, Y is North), so its enum names are not used for saving.
        internal static readonly string[] Names =
        {
            "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "North", "East", "South", "West",
            "LeftStick", "RightStick", "LeftShoulder", "RightShoulder", "Start", "Select", "LeftTrigger", "RightTrigger"
        };

        private static readonly string[] XboxNames =
        {
            "DUp", "DDown", "DLeft", "DRight", "Y", "B", "A", "X", "LS", "RS", "LB", "RB", "Menu", "View", "LT", "RT"
        };

        // Each button stored as its index plus one, so default(PadBinding) is None; a chord keeps the lower index first.
        private readonly byte _first, _second;

        public PadBinding(GamepadButton button)
        {
            _first = (byte)(IndexOf(button) + 1);
            _second = 0;
        }

        /// <summary>Two buttons held together; the same button twice is that button alone.</summary>
        public PadBinding(GamepadButton first, GamepadButton second)
            : this(IndexOf(first), IndexOf(second))
        {
        }

        private PadBinding(int first, int second)
        {
            if (first == second) second = -1;
            var low = second < 0 ? first : Math.Min(first, second);
            _first = (byte)(low + 1);
            _second = (byte)(second < 0 ? 0 : Math.Max(first, second) + 1);
        }

        public static PadBinding None => default;
        public bool IsNone => _first == 0;
        public bool IsChord => _second != 0;
        public GamepadButton? First => _first == 0 ? null : Buttons[_first - 1];
        public GamepadButton? Second => _second == 0 ? null : Buttons[_second - 1];

        /// <summary>One bit per button, by its index in <see cref="Buttons"/>.</summary>
        internal int Mask => (_first == 0 ? 0 : 1 << (_first - 1)) | (_second == 0 ? 0 : 1 << (_second - 1));

        public bool Uses(GamepadButton button)
        {
            var index = Array.IndexOf(Buttons, button);
            return index >= 0 && (Mask & (1 << index)) != 0;
        }

        public override string ToString()
            => _first == 0 ? "None" : _second == 0 ? Names[_first - 1] : Names[_first - 1] + "+" + Names[_second - 1];

        public static bool TryParse(string text, out PadBinding binding)
        {
            binding = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var trimmed = text.Trim();
            if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase)) return true;
            var parts = trimmed.Split('+');
            if (parts.Length > 2) return false;
            var first = Find(parts[0]);
            var second = parts.Length == 2 ? Find(parts[1]) : -1;
            if (first < 0 || parts.Length == 2 && second < 0) return false;
            binding = new PadBinding(first, second);
            return true;
        }

        internal static PadBinding FromMask(int mask)
        {
            int first = -1, second = -1;
            for (var i = 0; i < Buttons.Length; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                if (first < 0) first = i;
                else if (second < 0) second = i;
            }
            return first < 0 ? default : new PadBinding(first, second);
        }

        internal static int IndexOf(GamepadButton button)
        {
            var index = Array.IndexOf(Buttons, button);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(button), button, "Not a controller button a keybind can use.");
            return index;
        }

        private static int Find(string name)
        {
            name = name.Trim();
            for (var i = 0; i < Buttons.Length; i++)
                if (string.Equals(name, Names[i], StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, XboxNames[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        public bool Equals(PadBinding other) => _first == other._first && _second == other._second;
        public override bool Equals(object obj) => obj is PadBinding other && Equals(other);
        public override int GetHashCode() => _first | (_second << 8);
        public static bool operator ==(PadBinding left, PadBinding right) => left.Equals(right);
        public static bool operator !=(PadBinding left, PadBinding right) => !left.Equals(right);
    }

    /// <summary>A keybind as both devices report it this frame.</summary>
    /// <remarks>
    /// <see cref="Pressed"/> is the frame the binding went down or, for a hold binding, the frame its hold completed.
    /// The device edges (<see cref="KeyPressed"/>, <see cref="PadPressed"/>) ignore the hold.
    /// </remarks>
    public readonly struct InputBindingState
    {
        internal InputBindingState(bool keyHeld, bool keyPressed, bool keyReleased, bool padHeld, bool padPressed,
            bool padReleased, bool pressed, bool released, float holdProgress, InputDeviceKind device)
        {
            KeyHeld = keyHeld;
            KeyPressed = keyPressed;
            KeyReleased = keyReleased;
            PadHeld = padHeld;
            PadPressed = padPressed;
            PadReleased = padReleased;
            Pressed = pressed;
            Released = released;
            HoldProgress = holdProgress;
            Device = device;
        }

        public bool Held => KeyHeld || PadHeld;
        public bool Pressed { get; }
        public bool Released { get; }
        /// <summary>0 to 1 through a hold binding's hold, 1 once it completed; 0 when not held or not a hold binding.</summary>
        public float HoldProgress { get; }
        /// <summary>The device holding the binding, or the one that last pressed or let go of it.</summary>
        public InputDeviceKind Device { get; }
        public bool KeyHeld { get; }
        public bool KeyPressed { get; }
        public bool KeyReleased { get; }
        public bool PadHeld { get; }
        public bool PadPressed { get; }
        public bool PadReleased { get; }
    }

    public static partial class InputBindings
    {
        private static readonly Dictionary<string, Track> Tracks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ButtonControl[] PadControls = new ButtonControl[PadBinding.Buttons.Length];
        private static int _advancedFrame = -1, _padBits, _padPrevious;
        private static IntPtr _pad;
        private static bool _padFailed;

        /// <summary>The device the player last used, by the game's own flag; keyboard when it can't be read.</summary>
        public static InputDeviceKind CurrentDevice
        {
            get
            {
                try { return global::Sons.Input.InputSystem.IsGamePad ? InputDeviceKind.Gamepad : InputDeviceKind.Keyboard; }
                catch (Exception) { return InputDeviceKind.Keyboard; }
            }
        }

        /// <summary>The keybind's key and controller side this frame, with no focus, context or UI gate. Main-thread only.</summary>
        /// <remarks>
        /// For a focus owner reading its own keybinds (a seat or a full-screen device holding the game's input) and for
        /// safety actions that must work under any focus. A disabled or unknown keybind reads as nothing, and so does
        /// every keybind while the Mods menu waits for a key or button to bind.
        /// </remarks>
        public static InputBindingState ReadState(string id) => TryRead(id, out _, out var state) ? state : default;

        /// <summary>How far through its hold a hold keybind is, 0 to 1, after the same gates as <see cref="IsHeld"/>.</summary>
        public static float HoldProgress(string id)
            => TryRead(id, out var keybind, out var state) && state.Held && IsContextAllowed(keybind, out _) ? state.HoldProgress : 0;

        public static bool IsPressed(string id) => TryRead(id, out var keybind, out var state) && state.Pressed && IsContextAllowed(keybind, out _);

        /// <summary>Whether the key or controller side is down; a hold keybind counts from the first moment, so a prompt can count down.</summary>
        public static bool IsHeld(string id) => TryRead(id, out var keybind, out var state) && state.Held && IsContextAllowed(keybind, out _);

        public static bool IsReleased(string id) => TryRead(id, out var keybind, out var state) && state.Released && IsContextAllowed(keybind, out _);

        // Context checks can copy UI state, so callers check the state first; idle keybinds need no such work.
        private static bool TryRead(string id, out KeybindSnapshot keybind, out InputBindingState state)
        {
            state = default;
            if (string.IsNullOrWhiteSpace(id))
            {
                keybind = null;
                return false;
            }

            lock (SyncRoot)
            {
                AdvanceNoLock();
                if (!Keybinds.TryGetValue(id, out keybind) || !keybind.Enabled || CaptureQuietNoLock()
                    || !Tracks.TryGetValue(id, out var track))
                    return false;
                state = track.State;
                return true;
            }
        }

        /// <summary>Every frame from the runtime driver, so each keybind's edges are seen even when nobody polls it.</summary>
        internal static void Tick()
        {
            lock (SyncRoot)
            {
                AdvanceNoLock();
            }

            StepCapture();
        }

        // The first read in a frame moves every keybind on, whoever reads first.
        private static void AdvanceNoLock()
        {
            var frame = Time.frameCount;
            if (_advancedFrame == frame) return;
            _advancedFrame = frame;
            var padWanted = _capture != null || _awaitRelease;
            foreach (var keybind in Keybinds.Values)
                padWanted |= !keybind.Pad.IsNone;
            ReadPadNoLock(padWanted);
            var now = Time.unscaledTime;
            foreach (var keybind in Keybinds.Values)
            {
                if (!Tracks.TryGetValue(keybind.Id, out var track))
                    Tracks[keybind.Id] = track = new Track();
                track.Advance(keybind, frame, now, _padBits);
            }
        }

        private static void ReadPadNoLock(bool wanted)
        {
            _padPrevious = _padBits;
            _padBits = 0;
            if (!wanted || _padFailed) return;
            try
            {
                var pad = Gamepad.current;
                if (pad == null)
                {
                    _pad = IntPtr.Zero;
                    return;
                }

                // Each control asked of the pad comes back as a new wrapper, so they are kept per pad.
                if (pad.Pointer != _pad)
                {
                    _pad = pad.Pointer;
                    for (var i = 0; i < PadControls.Length; i++)
                        PadControls[i] = pad[PadBinding.Buttons[i]];
                }

                for (var i = 0; i < PadControls.Length; i++)
                    if (PadControls[i].isPressed) _padBits |= 1 << i;
            }
            catch (Exception error)
            {
                _padFailed = true;
                _padBits = 0;
                RuntimeEntryPoint.LogSource?.LogWarning("Keybinds stopped reading the controller until the game restarts: " + error.Message);
            }
        }

        private sealed class Track
        {
            private int _frame = -1;
            private float _since;
            private bool _keyHeld, _padHeld, _fired;

            internal InputBindingState State { get; private set; }

            // A keybind first seen this frame, or one that missed frames, starts from what is held now: a key held
            // before it was registered does nothing until it is let go.
            internal void Advance(KeybindSnapshot keybind, int frame, float now, int padBits)
            {
                var fresh = _frame != frame - 1;
                _frame = frame;
                var keyHeld = keybind.Enabled && keybind.Key != KeyCode.None && KeyHeld(keybind.Key);
                var mask = keybind.Pad.Mask;
                var padHeld = keybind.Enabled && mask != 0 && (padBits & mask) == mask;
                var wasKey = fresh ? keyHeld : _keyHeld;
                var wasPad = fresh ? padHeld : _padHeld;
                _keyHeld = keyHeld;
                _padHeld = padHeld;
                // A tap made and let go between two frames shows only in the key's own latch, as it always did.
                var keyDown = keyHeld && !wasKey || !fresh && keybind.Enabled && keybind.Key != KeyCode.None && KeyDown(keybind.Key);

                var held = keyHeld || padHeld;
                var was = wasKey || wasPad;
                var down = held && !was || keyDown && !wasPad && !padHeld;
                if (down)
                {
                    _since = now;
                    _fired = false;
                }
                else if (fresh && held)
                {
                    // Already complete, so a hold already under way when the keybind appeared never fires.
                    _fired = true;
                }

                var hold = keybind.HoldSeconds;
                var pressed = down && hold <= 0;
                if (hold > 0 && held && !_fired && now - _since >= hold)
                {
                    pressed = true;
                    _fired = true;
                }

                var progress = hold <= 0 || !held ? 0f : _fired ? 1f : Mathf.Clamp01((now - _since) / hold);
                var device = padHeld ? InputDeviceKind.Gamepad
                    : keyHeld || keyDown ? InputDeviceKind.Keyboard
                    : wasPad ? InputDeviceKind.Gamepad
                    : wasKey ? InputDeviceKind.Keyboard
                    : State.Device;
                State = new InputBindingState(keyHeld, keyDown, !keyHeld && (wasKey || keyDown), padHeld, padHeld && !wasPad,
                    !padHeld && wasPad, pressed, !held && (was || keyDown), progress, device);
            }

            // The legacy keyboard can be unavailable, as on a dedicated server; a key there is never held.
            private static bool KeyHeld(KeyCode key)
            {
                try { return UnityEngine.Input.GetKey(key); }
                catch (Exception) { return false; }
            }

            private static bool KeyDown(KeyCode key)
            {
                try { return UnityEngine.Input.GetKeyDown(key); }
                catch (Exception) { return false; }
            }
        }
    }
}
