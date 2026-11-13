// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modstoke.Loader
{
    /// <summary>
    /// What the player did this frame, the same for mouse, keyboard and controller. Read once per frame by the
    /// front end; screens look at the results.
    /// </summary>
    internal static class FrontInput
    {
        private const float FirstRepeat = 0.35f, NextRepeat = 0.08f;
        private static readonly bool[] PadDown = new bool[8];
        private static Vector2Int _held;
        private static float _repeatAt, _resumeAt;
        private static bool _padFailed;
        private static Vector2 _lastPointer;

        /// <summary>A step in a direction, repeating while held: x is right, y is down.</summary>
        internal static Vector2Int Move { get; private set; }
        internal static bool Confirm { get; private set; }
        internal static bool Back { get; private set; }
        /// <summary>The first extra action: X on a controller, F5 or Delete on a keyboard, as the screen labels it.</summary>
        internal static bool Alt1 { get; private set; }
        internal static bool Alt2 { get; private set; }
        internal static bool PreviousTab { get; private set; }
        internal static bool NextTab { get; private set; }
        /// <summary>A step between a menu's sections, such as Join and Host: the triggers, or Tab.</summary>
        internal static int Section { get; private set; }
        internal static Vector2 Pointer { get; private set; }
        internal static bool PointerMoved { get; private set; }
        internal static bool Click { get; private set; }
        /// <summary>The left mouse button is down, for dragging.</summary>
        internal static bool Held { get; private set; }
        /// <summary>The first extra action's button is held down: X on a controller, Delete on a keyboard.</summary>
        internal static bool Alt1Held { get; private set; }
        /// <summary>The confirm button is held down: A on a controller, Enter or Space on a keyboard.</summary>
        internal static bool ConfirmHeld { get; private set; }
        internal static float Scroll { get; private set; }

        /// <summary>Whether a controller is the device in use, by the game's own flag.</summary>
        internal static bool PadActive
        {
            get
            {
                try { return global::Sons.Input.InputSystem.IsGamePad; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Ignores presses for a moment, so the press that opened a screen does not also act on it.</summary>
        internal static void Pause(float seconds = 0.25f) => _resumeAt = Mathf.Max(_resumeAt, Time.unscaledTime + seconds);

        internal static void Read()
        {
            Move = Vector2Int.zero;
            Confirm = Back = Alt1 = Alt2 = PreviousTab = NextTab = Click = false;
            Section = 0;
            Scroll = 0;
            var acting = Time.unscaledTime >= _resumeAt;
            Held = acting && UnityEngine.Input.GetMouseButton(0);

            var position = UnityEngine.Input.mousePosition;
            Pointer = new Vector2(position.x, position.y);
            PointerMoved = (Pointer - _lastPointer).sqrMagnitude > 1f;
            _lastPointer = Pointer;

            var held = new Vector2Int(
                (UnityEngine.Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (UnityEngine.Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                (UnityEngine.Input.GetKey(KeyCode.DownArrow) ? 1 : 0) - (UnityEngine.Input.GetKey(KeyCode.UpArrow) ? 1 : 0));
            bool confirm = false, back = false, alt1 = false, alt2 = false, previous = false, next = false;
            var section = 0;
            ReadPad(ref held, ref confirm, ref back, ref alt1, ref alt2, ref previous, ref next, ref section);
            // Index 0 and 2 of the pad's tracked buttons are its confirm and X buttons, held or not.
            Alt1Held = acting && (UnityEngine.Input.GetKey(KeyCode.Delete) || PadDown[2]);
            ConfirmHeld = acting && (UnityEngine.Input.GetKey(KeyCode.Return) || UnityEngine.Input.GetKey(KeyCode.KeypadEnter)
                || UnityEngine.Input.GetKey(KeyCode.Space) || PadDown[0]);

            // One axis at a time, vertical first, so a diagonal stick does not skip sideways.
            if (held.y != 0) held.x = 0;
            if (held != _held)
            {
                _held = held;
                _repeatAt = Time.unscaledTime + FirstRepeat;
                if (acting) Move = held;
            }
            else if (held != Vector2Int.zero && Time.unscaledTime >= _repeatAt)
            {
                _repeatAt = Time.unscaledTime + NextRepeat;
                if (acting) Move = held;
            }
            if (!acting) return;

            Confirm = confirm || UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
                || UnityEngine.Input.GetKeyDown(KeyCode.Space);
            Back = back || UnityEngine.Input.GetKeyDown(KeyCode.Escape);
            Alt1 = alt1 || UnityEngine.Input.GetKeyDown(KeyCode.F5) || UnityEngine.Input.GetKeyDown(KeyCode.Delete);
            Alt2 = alt2 || UnityEngine.Input.GetKeyDown(KeyCode.F2);
            PreviousTab = previous || UnityEngine.Input.GetKeyDown(KeyCode.Q);
            NextTab = next || UnityEngine.Input.GetKeyDown(KeyCode.E);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
                section = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift) ? -1 : 1;
            Section = section;
            Click = UnityEngine.Input.GetMouseButtonDown(0);
            Scroll = UnityEngine.Input.mouseScrollDelta.y;
        }

        // Buttons are tracked every frame, so one held through a pause does not fire when it ends.
        private static void ReadPad(ref Vector2Int held, ref bool confirm, ref bool back, ref bool alt1, ref bool alt2,
            ref bool previous, ref bool next, ref int section)
        {
            if (_padFailed) return;
            try
            {
                var pad = Gamepad.current;
                if (pad == null) return;
                if (pad.dpad.right.isPressed || pad.leftStick.right.isPressed) held.x = 1;
                else if (pad.dpad.left.isPressed || pad.leftStick.left.isPressed) held.x = -1;
                if (pad.dpad.down.isPressed || pad.leftStick.down.isPressed) held.y = 1;
                else if (pad.dpad.up.isPressed || pad.leftStick.up.isPressed) held.y = -1;
                confirm = Pressed(0, pad.buttonSouth.isPressed);
                back = Pressed(1, pad.buttonEast.isPressed);
                alt1 = Pressed(2, pad.buttonWest.isPressed);
                alt2 = Pressed(3, pad.buttonNorth.isPressed);
                previous = Pressed(4, pad.leftShoulder.isPressed);
                next = Pressed(5, pad.rightShoulder.isPressed);
                if (Pressed(6, pad.leftTrigger.isPressed)) section = -1;
                if (Pressed(7, pad.rightTrigger.isPressed)) section = 1;
            }
            catch (Exception error)
            {
                _padFailed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("The menus stopped reading the controller: " + error.Message);
            }
        }

        private static bool Pressed(int index, bool down)
        {
            var pressed = down && !PadDown[index];
            PadDown[index] = down;
            return pressed;
        }
    }
}
