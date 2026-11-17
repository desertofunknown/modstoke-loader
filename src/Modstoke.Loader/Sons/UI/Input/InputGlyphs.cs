// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;

namespace Modstoke.Loader
{
    /// <summary>
    /// Key and button names in the player's language and keyboard layout, and the game's own controller icons (Xbox
    /// or PlayStation to match the pad in hand), for prompts that follow the player's bindings. Main-thread only.
    /// </summary>
    public static class InputGlyphs
    {
        // Input System control paths and the Loader's icon names, in PadBinding.Buttons order.
        private static readonly string[] Paths =
        {
            "dpad/up", "dpad/down", "dpad/left", "dpad/right", "buttonNorth", "buttonEast", "buttonSouth", "buttonWest",
            "leftStickPress", "rightStickPress", "leftShoulder", "rightShoulder", "start", "select", "leftTrigger", "rightTrigger"
        };
        private static readonly string[] Icons =
        {
            "DUP", "DDOWN", "DLEFT", "DRIGHT", "Y", "B", "A", "X", "LS", "RS", "LB", "RB", "MENU", "VIEW", "LT", "RT"
        };

        /// <summary>Whether the controller in use is a PlayStation one, by its input layout.</summary>
        public static bool PlayStation => Glyphs.PlayStationPad;

        /// <summary>The game's icon for a controller button; false while the game has not loaded its prompt icons.</summary>
        /// <remarks>A miss searches the game's loaded textures at most every ten seconds; look icons up when a prompt is built, not every frame.</remarks>
        public static bool TryGetIcon(GamepadButton button, out Texture2D texture)
        {
            texture = null;
            var index = Array.IndexOf(PadBinding.Buttons, button);
            return index >= 0 && Glyphs.TryGet(Icons[index], out texture);
        }

        /// <summary>A controller button's name as the pad in hand prints it: "A" or "Cross", "LB" or "L1".</summary>
        public static string Name(GamepadButton button)
        {
            var path = PathOf(button);
            if (path == null)
                return "";
            // The game names no start button of its own; it is the pause menu's.
            if (button == GamepadButton.Start)
                return PlayStation ? "Options" : "Menu";
            return BindingNames.Name(path);
        }

        /// <summary>The button's Input System path, as the game's bindings hold it.</summary>
        internal static string PathOf(GamepadButton button)
        {
            var index = Array.IndexOf(PadBinding.Buttons, button);
            return index < 0 ? null : "<Gamepad>/" + Paths[index];
        }

        /// <summary>A controller binding's name: "RB", "LB + RB", or empty for none.</summary>
        public static string Name(PadBinding pad)
        {
            if (pad.IsNone)
                return "";
            var first = Name(pad.First.Value);
            return pad.IsChord ? first + " + " + Name(pad.Second.Value) : first;
        }

        /// <summary>A key's name as the player's keyboard layout prints it, for a keycap: "F", "Left Shift", "Mouse 3".</summary>
        public static string Name(KeyCode key)
        {
            if (key == KeyCode.None)
                return "";
            var path = GameBindings.PathOf(key);
            return path == null ? key.ToString() : BindingNames.Name(path);
        }

        /// <summary>A keybind's binding on one device as it is set now; empty when nothing is on that side.</summary>
        public static string Name(string keybindId, InputDeviceKind device)
        {
            if (!InputBindings.TryGetKeybind(keybindId, out var keybind))
                return "";
            return device == InputDeviceKind.Gamepad ? Name(keybind.Pad) : Name(keybind.Key);
        }
    }
}
