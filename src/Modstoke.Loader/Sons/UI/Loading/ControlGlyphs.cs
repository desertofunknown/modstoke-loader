// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>A key or button as a prompt draws it: the game's icon when <see cref="Glyph"/> is set, else a keycap reading <see cref="Name"/>.</summary>
    internal readonly record struct ControlKey(string Glyph, string Name)
    {
        internal bool Bound => !string.IsNullOrEmpty(Name);
    }

    /// <summary>
    /// What the game's actions and Modstoke's keybinds are bound to right now, from the game's own bindings model, so
    /// a rebind on the Keys and buttons screen shows on the loading screen too. Read at the title: the game's binding
    /// data is found there, and keys are named in the player's language and keyboard layout.
    /// </summary>
    internal static class ControlGlyphs
    {
        /// <summary>
        /// The binding of the game action <paramref name="action"/> on a device: a binding definition by its name
        /// ("Inventory"), or the one covering an action path that ends in it ("DismantleElement"). Unbound when the
        /// device has nothing on it or the bindings can't be read.
        /// </summary>
        internal static ControlKey Game(string action, BindingDevice device)
        {
            if (string.IsNullOrEmpty(action) || !GameBindings.Ready) return default;
            BindingAction found = null;
            var tail = "/" + action;
            foreach (var candidate in GameBindings.Actions)
            {
                if (candidate.Fixed) continue;
                if (string.Equals(candidate.Name, action, StringComparison.OrdinalIgnoreCase))
                {
                    found = candidate;
                    break;
                }
                if (found == null && Covers(candidate, tail)) found = candidate;
            }
            var slot = found?[device];
            if (slot == null || !slot.Offered || slot.Path.Length == 0) return default;
            var look = BindingNames.Look(slot.Path);
            return new ControlKey(look.Glyph, look.Text);
        }

        /// <summary>A Modstoke keybind's key, or its controller side; a chord reads as a keycap naming both buttons.</summary>
        internal static ControlKey Keybind(string id, BindingDevice device = BindingDevice.Keyboard)
        {
            if (!InputBindings.TryGetKeybind(id, out var keybind) || !keybind.Enabled) return default;
            if (device == BindingDevice.Pad)
            {
                if (keybind.Pad.IsNone) return default;
                if (keybind.Pad.IsChord) return new ControlKey(null, InputGlyphs.Name(keybind.Pad));
                var button = BindingNames.Look(InputGlyphs.PathOf(keybind.Pad.First.Value));
                return new ControlKey(button.Glyph, button.Text);
            }
            if (keybind.Key == KeyCode.None) return default;
            var path = GameBindings.PathOf(keybind.Key);
            if (path == null) return new ControlKey(null, keybind.Key.ToString());
            var look = BindingNames.Look(path);
            return new ControlKey(look.Glyph, look.Text);
        }

        /// <summary>
        /// Draws a key on a tag as the bindings screen does: the game's icon for a button, a keycap with its name otherwise.
        /// Only icons <see cref="Find"/> has found are drawn, so this never searches the game's textures during a load.
        /// </summary>
        internal static void Show(KeyTag tag, ControlKey key, CultureInfo culture)
        {
            var name = key.Name.ToUpper(culture);
            var icon = key.Glyph != null && Glyphs.Has(key.Glyph);
            tag.Set(icon ? key.Glyph : name, icon, name);
        }

        /// <summary>
        /// Looks for the game's icon for a key, which searches its textures the first time; only at the title or in the
        /// lobby, so a layer built at a raise in a game draws keycaps for icons not found yet rather than search.
        /// </summary>
        internal static void Find(ControlKey key)
        {
            if (key.Glyph != null && (FrontEnd.AtTitle || LobbyHost.Open)) Glyphs.TryGet(key.Glyph, out _);
        }

        private static bool Covers(BindingAction candidate, string tail)
        {
            foreach (var path in candidate.Paths)
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
