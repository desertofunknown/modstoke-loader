// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.DualShock;

namespace Modstoke.Loader
{
    /// <summary>One frame of photo mode's camera controls, from keyboard, mouse and controller together.</summary>
    internal readonly struct PhotoInputFrame
    {
        /// <summary>Right, up (along the world's up) and forward (along the view), each -1 to 1.</summary>
        internal Vector3 Move { get; init; }
        /// <summary>Degrees to turn this frame: x to the right, y upwards.</summary>
        internal Vector2 Look { get; init; }
        /// <summary>Whether the look came from a stick, which is smoothed a little longer than the mouse.</summary>
        internal bool PadLook { get; init; }
        /// <summary>Mouse wheel steps this frame; up zooms in.</summary>
        internal float Zoom { get; init; }
        /// <summary>-1 rolls left, 1 rolls right.</summary>
        internal float Roll { get; init; }
        /// <summary>How much faster or slower than the set speed the camera moves.</summary>
        internal float Pace { get; init; }
        internal bool Autofocus { get; init; }
        internal bool Return { get; init; }
        internal bool NextGuide { get; init; }
        internal bool Hide { get; init; }
        /// <summary>The mouse is turning the camera, so the cursor is held still and out of sight.</summary>
        internal bool Dragging { get; init; }
    }

    /// <summary>
    /// Reads photo mode's camera controls straight from the devices, as the front end does: the game's own
    /// action maps are off inside its pause menu. Mouse look runs while the right button is held anywhere or the
    /// left one off the panel, and all the time while the interface is hidden.
    /// </summary>
    internal static class PhotoInput
    {
        private const float MouseDegrees = 0.1f, PadDegrees = 110f, Deadzone = 0.15f;
        private static readonly bool[] PadDown = new bool[3];
        private static float _resumeAt;
        private static bool _leftLook, _padFailed;
        private static IntPtr _touchpadOf;
        private static ButtonControl _touchpad;

        /// <summary>Ignores the controls for a moment, as when photo mode has just opened.</summary>
        internal static void Pause(float seconds = 0.25f)
        {
            _resumeAt = Time.unscaledTime + seconds;
            _leftLook = false;
        }

        /// <param name="hidden">The interface is hidden, so the mouse always looks.</param>
        /// <param name="overPanel">The pointer is over the panel, where the wheel and left button belong to it.</param>
        /// <param name="panelBusy">A slider on the panel is being dragged.</param>
        /// <param name="fov">The view's field of view; looking slows as the lens zooms in.</param>
        internal static PhotoInputFrame Read(bool hidden, bool overPanel, bool panelBusy, float fov)
        {
            var dt = Time.unscaledDeltaTime;
            var zoomScale = Mathf.Clamp(fov / 70f, 0.05f, 2f);
            var left = UnityEngine.Input.GetMouseButton(0);
            if (UnityEngine.Input.GetMouseButtonDown(0) && !overPanel && !panelBusy) _leftLook = true;
            if (!left) _leftLook = false;
            var padLook = Vector2.zero;
            var padMove = Vector3.zero;
            bool padFast = false, padFocus = false, padHide = false;
            ReadPad(ref padMove, ref padLook, ref padFast, ref padFocus, ref padHide);
            if (Time.unscaledTime < _resumeAt) return new PhotoInputFrame { Pace = 1f };

            var move = new Vector3(Axis(KeyCode.D, KeyCode.A), Key(KeyCode.Space) ? 1 : 0, Axis(KeyCode.W, KeyCode.S));
            if (Key(KeyCode.LeftControl) || Key(KeyCode.C)) move.y -= 1;
            move += padMove;
            move = new Vector3(Mathf.Clamp(move.x, -1, 1), Mathf.Clamp(move.y, -1, 1), Mathf.Clamp(move.z, -1, 1));
            var pace = Key(KeyCode.LeftShift) || Key(KeyCode.RightShift) || padFast ? 3f : Key(KeyCode.LeftAlt) || Key(KeyCode.RightAlt) ? 0.25f : 1f;

            var dragging = hidden || UnityEngine.Input.GetMouseButton(1) || _leftLook;
            var look = Vector2.zero;
            var invert = Setting(() => global::Sons.Input.InputSystem.InvertLookY, false);
            if (dragging)
            {
                var delta = MouseDelta();
                look.x = delta.x * MouseDegrees * Setting(() => global::Sons.Input.InputSystem.MouseXSensitivity, 1f) * zoomScale;
                look.y = delta.y * MouseDegrees * Setting(() => global::Sons.Input.InputSystem.MouseYSensitivity, 1f) * zoomScale;
            }
            var stick = padLook.sqrMagnitude > 0;
            if (stick)
            {
                look.x += padLook.x * PadDegrees * Setting(() => global::Sons.Input.InputSystem.GamePadXSensitivity, 1f) * zoomScale * dt;
                look.y += padLook.y * PadDegrees * Setting(() => global::Sons.Input.InputSystem.GamePadYSensitivity, 1f) * zoomScale * dt;
            }
            if (invert) look.y = -look.y;

            return new PhotoInputFrame
            {
                Move = move,
                Look = look,
                PadLook = stick,
                Zoom = overPanel && !hidden ? 0 : UnityEngine.Input.mouseScrollDelta.y,
                Roll = Axis(KeyCode.X, KeyCode.Z),
                Pace = pace,
                Autofocus = UnityEngine.Input.GetKeyDown(KeyCode.F) || padFocus,
                Return = UnityEngine.Input.GetKeyDown(KeyCode.R),
                NextGuide = UnityEngine.Input.GetKeyDown(KeyCode.G),
                Hide = UnityEngine.Input.GetKeyDown(KeyCode.H) || padHide,
                Dragging = dragging
            };
        }

        private static bool Key(KeyCode key) => UnityEngine.Input.GetKey(key);

        private static float Axis(KeyCode positive, KeyCode negative) => (Key(positive) ? 1 : 0) - (Key(negative) ? 1 : 0);

        private static T Setting<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }

        private static Vector2 MouseDelta()
        {
            try
            {
                var mouse = Mouse.current;
                return mouse != null ? mouse.delta.ReadValue() : Vector2.zero;
            }
            catch (Exception)
            {
                return Vector2.zero;
            }
        }

        // Sticks with a round dead zone and a squared response, for fine aim near the centre; the triggers raise and
        // lower the camera by how far they are pulled.
        private static void ReadPad(ref Vector3 move, ref Vector2 look, ref bool fast, ref bool focus, ref bool hide)
        {
            if (_padFailed) return;
            try
            {
                var pad = Gamepad.current;
                if (pad == null) return;
                var walk = Shape(pad.leftStick.ReadValue(), false);
                move = new Vector3(walk.x, pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue(), walk.y);
                look = Shape(pad.rightStick.ReadValue(), true);
                fast = pad.leftStickButton.isPressed;
                focus = Pressed(1, pad.rightStickButton.isPressed);
                var touchpad = Touchpad(pad);
                hide = Pressed(2, pad.selectButton.isPressed || touchpad != null && touchpad.isPressed);
            }
            catch (Exception error)
            {
                _padFailed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Photo mode stopped reading the controller: " + error.Message);
            }
        }

        // On a PlayStation pad the prompt shows the touchpad, whose click is a button of its own; select is Share there.
        private static ButtonControl Touchpad(Gamepad pad)
        {
            if (pad.Pointer == _touchpadOf) return _touchpad;
            _touchpadOf = pad.Pointer;
            _touchpad = pad.TryCast<DualShockGamepad>()?.touchpadButton;
            return _touchpad;
        }

        private static Vector2 Shape(Vector2 stick, bool squared)
        {
            var length = stick.magnitude;
            if (length <= Deadzone) return Vector2.zero;
            var amount = Mathf.Clamp01((length - Deadzone) / (1f - Deadzone));
            return stick / length * (squared ? amount * amount : amount);
        }

        private static bool Pressed(int index, bool down)
        {
            var pressed = down && !PadDown[index];
            PadDown[index] = down;
            return pressed;
        }
    }
}
