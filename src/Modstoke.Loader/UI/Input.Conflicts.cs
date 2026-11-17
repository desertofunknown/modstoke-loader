// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;

namespace Modstoke.Loader
{
    /// <summary>Another control on the same key or controller button that can answer at the same moment.</summary>
    public sealed class InputBindingConflict
    {
        internal InputBindingConflict(InputDeviceKind device, string name, string owner, string otherId)
        {
            Device = device;
            Name = name;
            Owner = owner;
            OtherId = otherId;
        }

        public InputDeviceKind Device { get; }
        /// <summary>The other control: one of the game's actions, such as "Jump", or a keybind's display name.</summary>
        public string Name { get; }
        /// <summary><see cref="InputBindings.GameOwner"/> for the game's own controls, else the other keybind's category.</summary>
        public string Owner { get; }
        /// <summary>The other keybind's ID; null for one of the game's own controls.</summary>
        public string OtherId { get; }

        public override string ToString() => Name + " (" + Owner + ")";
    }

    public static partial class InputBindings
    {
        /// <summary>The owner named on a conflict with one of the game's own controls.</summary>
        public const string GameOwner = "Sons of the Forest";

        // The game's default controller map in the world, from its SonsInputMapping asset, with the input actions on
        // each button, which a keybind can share. While aiming only the bumpers and Select are idle, so the aiming map
        // adds nothing a binding could use.
        private static readonly (GamepadButton Button, string Action, string[] GameActions)[] WorldPad =
        {
            (GamepadButton.RightTrigger, "Primary action", new[] { "PrimaryAction" }),
            (GamepadButton.LeftTrigger, "Aim", new[] { "SecondaryAction" }),
            (GamepadButton.Select, "Tertiary action", new[] { "TertiaryAction" }),
            (GamepadButton.West, "Reload, place and build", new[] { "Reload", "Place", "GoToBuilding" }),
            (GamepadButton.South, "Take, interact and use", new[] { "Take", "Interact", "Use" }),
            (GamepadButton.East, "Back, drop and save", new[] { "Back", "Drop", "Save" }),
            (GamepadButton.North, "Jump", new[] { "Jump", "Skip" }),
            (GamepadButton.LeftStick, "Run", new[] { "Run" }),
            (GamepadButton.RightStick, "Crouch", new[] { "Crouch" }),
            (GamepadButton.DpadUp, "Survival book", new[] { "Book" }),
            (GamepadButton.DpadDown, "Inventory and quick select", new[] { "OpenInventory", "OpenQuickSelect" }),
            (GamepadButton.DpadLeft, "Lighter", new[] { "Lighter", "IgniteObject" }),
            (GamepadButton.DpadRight, "Utility", new[] { "Utility" }),
            (GamepadButton.LeftShoulder, "Rotate", new[] { "Rotate" }),
            (GamepadButton.RightShoulder, "Rotate", new[] { "Rotate" }),
            (GamepadButton.Start, "Pause", new[] { "TogglePauseMenu" })
        };

        // The menus' buttons, as Modstoke's screens and the game's read them; not something a keybind stands in for.
        private static readonly (GamepadButton Button, string Action, string[] GameActions)[] MenuPad =
        {
            (GamepadButton.South, "Confirm", Array.Empty<string>()), (GamepadButton.East, "Back", Array.Empty<string>()),
            (GamepadButton.West, "Menu action", Array.Empty<string>()), (GamepadButton.North, "Menu action", Array.Empty<string>()),
            (GamepadButton.LeftShoulder, "Previous tab", Array.Empty<string>()), (GamepadButton.RightShoulder, "Next tab", Array.Empty<string>()),
            (GamepadButton.LeftTrigger, "Previous section", Array.Empty<string>()),
            (GamepadButton.RightTrigger, "Next section", Array.Empty<string>()), (GamepadButton.DpadUp, "Move", Array.Empty<string>()),
            (GamepadButton.DpadDown, "Move", Array.Empty<string>()), (GamepadButton.DpadLeft, "Move", Array.Empty<string>()),
            (GamepadButton.DpadRight, "Move", Array.Empty<string>()), (GamepadButton.Start, "Pause", Array.Empty<string>())
        };

        // The maps the game turns on in the world on foot (Sons.Input.InputSystem.Apply); its others (menus, inventory,
        // grab bag) are on only while IsInWorld is false.
        private static readonly string[] WorldMaps = { "default", "Construction", "ItemHotkey", "Vehicle" };

        // Held together the two bumpers turn the game's rotate axis both ways at once, which cancels.
        private static readonly int BothBumpers = new PadBinding(GamepadButton.LeftShoulder, GamepadButton.RightShoulder).Mask;

        [Flags]
        private enum Moments
        {
            Title = 1,
            Loading = 2,
            GameOutsideWorld = 4,
            OnFoot = 8,
            // The game's input held by a seat or a full-screen device: only its holder's keybinds answer then.
            Held = 16,
            OtherScene = 32,
            All = Title | Loading | GameOutsideWorld | OnFoot | Held | OtherScene,
            Menus = Title | GameOutsideWorld | OtherScene
        }

        /// <summary>
        /// The game's controls and other keybinds that share this keybind's key or controller side in a moment both can
        /// answer: the game's default controller map for its context, the game's current keyboard bindings once the
        /// title has read them, and every other registered keybind.
        /// </summary>
        /// <remarks>
        /// A chord clashes with a keybind on one of its buttons, since holding the chord presses it too, but not with a
        /// chord that only shares a button. Two keybinds for the held game input clash only within one category,
        /// since one seat or device holds it at a time.
        /// </remarks>
        public static IReadOnlyList<InputBindingConflict> GetConflicts(string id)
        {
            KeybindSnapshot keybind;
            KeybindSnapshot[] others;
            lock (SyncRoot)
            {
                if (string.IsNullOrWhiteSpace(id) || !Keybinds.TryGetValue(id, out keybind) || !keybind.Enabled)
                    return Array.Empty<InputBindingConflict>();
                others = CreateSortedSnapshotNoLock();
            }

            var conflicts = new List<InputBindingConflict>();
            var moments = MomentsOf(keybind.Context);
            var mask = keybind.Pad.Mask;
            if (mask != 0)
            {
                // Both bumpers together leave the game's rotate alone, so a chord of them is checked without them.
                var gameMask = (mask & BothBumpers) == BothBumpers ? mask & ~BothBumpers : mask;
                if ((moments & Moments.OnFoot) != 0)
                    AddGamePad(conflicts, WorldPad, gameMask, keybind);
                if ((moments & Moments.Menus) != 0)
                    AddGamePad(conflicts, MenuPad, gameMask, keybind);
            }

            if (keybind.Key != KeyCode.None)
                AddGameKeys(conflicts, keybind, moments);

            foreach (var other in others)
            {
                if (ReferenceEquals(other, keybind) || !other.Enabled || !Overlap(keybind, other))
                    continue;
                if (keybind.Key != KeyCode.None && other.Key == keybind.Key)
                    conflicts.Add(new InputBindingConflict(InputDeviceKind.Keyboard, other.DisplayName, other.Category, other.Id));
                var both = mask & other.Pad.Mask;
                if (mask != 0 && other.Pad.Mask != 0 && (both == mask || both == other.Pad.Mask))
                    conflicts.Add(new InputBindingConflict(InputDeviceKind.Gamepad, other.DisplayName, other.Category, other.Id));
            }

            return conflicts;
        }

        private static void AddGamePad(List<InputBindingConflict> conflicts, (GamepadButton Button, string Action, string[] GameActions)[] map,
            int mask, KeybindSnapshot keybind)
        {
            foreach (var (button, action, gameActions) in map)
            {
                if ((mask & (1 << PadBinding.IndexOf(button))) == 0 || Shares(keybind, gameActions) || conflicts.Exists(conflict
                    => conflict.OtherId == null && conflict.Device == InputDeviceKind.Gamepad && conflict.Name == action))
                    continue;
                conflicts.Add(new InputBindingConflict(InputDeviceKind.Gamepad, action, GameOwner, null));
            }
        }

        // The game's keyboard bindings are only known once the title has read them; until then nothing is claimed.
        private static void AddGameKeys(List<InputBindingConflict> conflicts, KeybindSnapshot keybind, Moments moments)
        {
            if (!GameBindings.Ready)
                return;
            var path = GameBindings.PathOf(keybind.Key);
            if (path == null)
                return;
            var control = GameBindings.Canonical(path);
            foreach (var action in GameBindings.Actions)
            {
                var slot = action[BindingDevice.Keyboard];
                if (action.Fixed || slot.Targets.Count == 0 || slot.Control != control || (MomentsOf(action.Maps) & moments) == 0
                    || SharesGameAction(keybind, action))
                    continue;
                var label = action.Label;
                if (!conflicts.Exists(conflict => conflict.OtherId == null && conflict.Name == label))
                    conflicts.Add(new InputBindingConflict(InputDeviceKind.Keyboard, label, GameOwner, null));
            }
        }

        /// <summary>
        /// Whether a keybind can answer while any of the game's action maps <paramref name="maps"/> is on, so the game's
        /// Keys and buttons screen and <see cref="GetConflicts"/> agree on what clashes.
        /// </summary>
        internal static bool LiveWithGameMaps(KeybindSnapshot keybind, IEnumerable<string> maps)
            => (MomentsOf(keybind.Context) & MomentsOf(maps)) != 0;

        /// <summary>
        /// Whether the keybind stands in for one of the game's binding definitions on purpose: one it names, by the
        /// definition's own name or an input action it covers.
        /// </summary>
        internal static bool SharesGameAction(KeybindSnapshot keybind, BindingAction action)
        {
            foreach (var shared in keybind.SharedGameActions)
            {
                if (string.Equals(shared, action.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
                foreach (var path in action.Paths)
                    if (path.EndsWith("/" + shared, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        private static bool Shares(KeybindSnapshot keybind, string[] gameActions)
        {
            foreach (var shared in keybind.SharedGameActions)
                if (Array.Exists(gameActions, action => string.Equals(action, shared, StringComparison.OrdinalIgnoreCase)))
                    return true;
            return false;
        }

        private static Moments MomentsOf(IEnumerable<string> maps)
        {
            var moments = (Moments)0;
            foreach (var map in maps)
                // The game turns the item hotkeys on with the inventory too, out of the world (GameBindings.Together).
                moments |= string.Equals(map, "ItemHotkey", StringComparison.OrdinalIgnoreCase)
                    ? Moments.OnFoot | Moments.GameOutsideWorld
                    : Array.Exists(WorldMaps, world => string.Equals(world, map, StringComparison.OrdinalIgnoreCase))
                        ? Moments.OnFoot : Moments.Menus;
            return moments;
        }

        private static bool Overlap(KeybindSnapshot a, KeybindSnapshot b)
        {
            var shared = MomentsOf(a.Context) & MomentsOf(b.Context);
            return shared != 0 && (shared != Moments.Held || string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase));
        }

        private static Moments MomentsOf(string context) => NormalizeContext(context) switch
        {
            InputContexts.Title => Moments.Title,
            InputContexts.Loading => Moments.Loading,
            InputContexts.GameScene => Moments.GameOutsideWorld | Moments.OnFoot | Moments.Held,
            InputContexts.InWorld => Moments.OnFoot | Moments.Held,
            InputContexts.NotInWorld => Moments.Title | Moments.Loading | Moments.GameOutsideWorld | Moments.OtherScene,
            InputContexts.GameControlsOn => Moments.OnFoot,
            InputContexts.GameControlsOff => Moments.Held,
            _ => Moments.All
        };
    }
}
