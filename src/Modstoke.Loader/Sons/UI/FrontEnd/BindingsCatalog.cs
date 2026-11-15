// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;

namespace Modstoke.Loader
{
    /// <summary>One action on the bindings screen: its keyboard and controller bindings side by side.</summary>
    internal sealed class BindingRow : OptionRow
    {
        internal BindingRow(BindingAction action, string section) : base(action.Name)
        {
            Action = action;
            Section = section;
            Ready = true;
            When = () => GameBindings.Ready;
        }

        internal BindingAction Action { get; }
        /// <summary>The section it is listed under, whose text says what its actions are for.</summary>
        internal string Section { get; }
        internal BindingSlot this[BindingDevice device] => Action[device];
        internal override string Value => BindingNames.Pair(Action, true);
        internal override string DefaultValue => BindingNames.Pair(Action, false);
        internal override bool IsDefault => Action[BindingDevice.Keyboard].IsDefault && Action[BindingDevice.Pad].IsDefault;
        internal override bool Step(int direction, bool wrap) => false;
        // The screen puts one binding back at a time, not the row.
        internal override bool Reset() => false;
    }

    /// <summary>
    /// Where the game's bindings sit on the screen: tabs for the game's contexts, sections within them, rows by the
    /// definition's name. A row the game does not have is left out; one the game has and this does not place goes on
    /// an Other tab, so nothing is ever unreachable.
    /// </summary>
    internal static class BindingsCatalog
    {
        private static readonly (string Tab, string Section, string[] Rows)[] Placed =
        {
            ("foot", "move", new[] { "Forward", "Backward", "Left", "Right", "Run", "Crouch", "Jump" }),
            ("foot", "act", new[] { "PrimaryAction", "SecondaryAction", "TertiaryAction", "Take", "Reload", "Drop" }),
            ("foot", "gear", new[] { "Lighter", "Utility", "Book", "Inventory", "GpsTracker", "WalkieTalkie" }),
            ("foot", "rest", new[] { "Sleep", "Save" }),
            ("foot", "general", new[] { "PushToTalk", "Skip" }),
            ("items", "hotkeys", new[] { "HotKey1", "HotKey2", "HotKey3", "HotKey4", "HotKey5", "HotKey6", "HotKey7", "HotKey8", "HotKey9", "HotKey0" }),
            ("items", "inventory", new[] { "Interact", "AlternateInteract", "ToggleInventoryMode" }),
            ("items", "book", new[] { "ToggleBookMode", "BookFlipPagePrevious", "BookFlipPageNext" }),
            ("build", "blueprints", new[] { "RotateLeft", "RotateRight", "Place", "BatchPlace", "CancelStructure" }),
            ("build", "freeform", new[] { "PlaceElement", "TogglePlaceMode", "Dismantle" }),
            ("drive", "drive", new[] { "Throttle", "Reverse", "TurnLeft", "TurnRight", "Boost", "HandBrake" }),
            ("drive", "radio", new[] { "VehicleRadio", "VehicleNextTrack" })
        };

        internal static IReadOnlyList<string> TabIds()
        {
            var ids = new List<string> { "foot", "items", "build", "drive" };
            if (Unplaced().Any()) ids.Add("other");
            return ids;
        }

        internal static OptionTab Tab(string id)
        {
            if (id == "other")
                return new OptionTab(id, new[] { new OptionSection("other", Unplaced().Select(action => new BindingRow(action, "other")).ToArray()) });
            var actions = Listed().GroupBy(action => action.Name).ToDictionary(group => group.Key, group => group.First());
            return new OptionTab(id, Placed.Where(entry => entry.Tab == id)
                .Select(entry => new OptionSection(entry.Section, entry.Rows
                    .Where(actions.ContainsKey)
                    .Select(name => new BindingRow(actions[name], entry.Section))
                    .ToArray()))
                .ToArray());
        }

        // The menus' own definitions are never listed: the game's screen offers no way to change them either.
        private static IEnumerable<BindingAction> Listed() => GameBindings.Ready ? GameBindings.Actions.Where(action => !action.Fixed) : Enumerable.Empty<BindingAction>();

        private static IEnumerable<BindingAction> Unplaced()
        {
            var placed = new HashSet<string>(Placed.SelectMany(entry => entry.Rows));
            return Listed().Where(action => !placed.Contains(action.Name));
        }
    }

    /// <summary>How a binding looks in a cell: the game's icon (or none, for a keycap), its name, and a stick's direction (-1 none, 0 up, 1 right, 2 down, 3 left).</summary>
    internal readonly record struct BindingLook(string Glyph, string Text, int Direction);

    /// <summary>Bindings as the player reads them: keys by their names in the player's language and layout, controller buttons as the game's own icons.</summary>
    internal static class BindingNames
    {
        // Keys with a name of their own; the rest read as the keyboard's layout prints them.
        private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["space"] = "space", ["enter"] = "enter", ["tab"] = "tab", ["backspace"] = "backspace", ["capsLock"] = "capsLock",
            ["leftShift"] = "leftShift", ["rightShift"] = "rightShift", ["leftCtrl"] = "leftCtrl", ["rightCtrl"] = "rightCtrl",
            ["leftAlt"] = "leftAlt", ["rightAlt"] = "rightAlt", ["insert"] = "insert", ["delete"] = "delete", ["home"] = "home",
            ["end"] = "end", ["pageUp"] = "pageUp", ["pageDown"] = "pageDown", ["upArrow"] = "up", ["downArrow"] = "down",
            ["leftArrow"] = "left", ["rightArrow"] = "right", ["printScreen"] = "printScreen", ["scrollLock"] = "scrollLock",
            ["pause"] = "pause", ["numLock"] = "numLock", ["contextMenu"] = "contextMenu", ["numpadEnter"] = "numpadEnter"
        };
        private static readonly Dictionary<string, string> NumpadSigns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["numpadPlus"] = "+", ["numpadMinus"] = "-", ["numpadMultiply"] = "*", ["numpadDivide"] = "/", ["numpadPeriod"] = ".",
            ["numpadEquals"] = "="
        };
        private static readonly string[] MouseButtons = { "leftbutton", "rightbutton", "middlebutton", "backbutton", "forwardbutton" };
        private static readonly Dictionary<(string Path, bool PlayStation, string Language), (BindingLook Look, string Name)> Known = new();

        private static Strings Text => FrontEnd.Text;

        internal static BindingLook Look(string path) => Read(path).Look;

        /// <summary>A binding in words, for the details column and the conflict question.</summary>
        internal static string Name(string path) => Read(path).Name;

        /// <summary>Both bindings of an action, keyboard first ("E · A"); a side with nothing on it is left out.</summary>
        internal static string Pair(BindingAction action, bool current)
        {
            var parts = new List<string>(2);
            foreach (var device in GameBindings.Devices)
            {
                var slot = action[device];
                var path = current ? slot.Path : slot.DefaultPath;
                if (slot.Offered && path.Length > 0) parts.Add(Name(path));
            }
            return string.Join(" · ", parts);
        }

        /// <summary>Forgets every name worked out, for a keyboard layout that may have changed since.</summary>
        internal static void Forget() => Known.Clear();

        private static (BindingLook Look, string Name) Read(string path)
        {
            var playStation = !path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase) && Glyphs.PlayStationPad;
            var key = (path, playStation, Languages.Current);
            if (Known.TryGetValue(key, out var known)) return known;
            var read = path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase) ? Keyboard(path)
                : path.StartsWith("<Mouse>", StringComparison.OrdinalIgnoreCase) ? Mouse(path)
                : Pad(path, playStation);
            return Known[key] = read;
        }

        private static (BindingLook, string) Keyboard(string path)
        {
            var control = Segment(GameBindings.Canonical(path));
            string name;
            if (Named.TryGetValue(control, out var named)) name = Text["bindings.key." + named];
            else if (NumpadSigns.TryGetValue(control, out var sign)) name = Text.Get("bindings.key.numpad", ("key", sign));
            else if (control.Length == 7 && control.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && char.IsDigit(control[6]))
                name = Text.Get("bindings.key.numpad", ("key", control.Substring(6)));
            else name = DisplayName(path) ?? Segment(path);
            return (new BindingLook(null, name, -1), name);
        }

        // Left and right show the game's own mouse icons; the other buttons are keycaps.
        private static (BindingLook, string) Mouse(string path)
        {
            var number = Array.IndexOf(MouseButtons, Segment(path).ToLowerInvariant()) + 1;
            var name = number > 0 ? Text.Get("bindings.key.mouse", ("number", number)) : Segment(path);
            return (new BindingLook(number == 1 ? "LMB" : number == 2 ? "RMB" : null, name, -1), name);
        }

        // Glyph names are the ones Glyphs maps to the game's Xbox and PlayStation icons.
        private static (BindingLook, string) Pad(string path, bool playStation)
        {
            var tail = path.Substring(path.IndexOf('/') + 1).ToLowerInvariant();
            return tail switch
            {
                "buttonsouth" => Button("A", playStation ? Text["bindings.pad.cross"] : "A"),
                "buttoneast" => Button("B", playStation ? Text["bindings.pad.circle"] : "B"),
                "buttonwest" => Button("X", playStation ? Text["bindings.pad.square"] : "X"),
                "buttonnorth" => Button("Y", playStation ? Text["bindings.pad.triangle"] : "Y"),
                "leftshoulder" => Button("LB", playStation ? "L1" : "LB"),
                "rightshoulder" => Button("RB", playStation ? "R1" : "RB"),
                "lefttrigger" => Button("LT", playStation ? "L2" : "LT"),
                "righttrigger" => Button("RT", playStation ? "R2" : "RT"),
                "leftstickpress" => Button("LS", Text["bindings.pad.leftStickPress"]),
                "rightstickpress" => Button("RS", Text["bindings.pad.rightStickPress"]),
                // The game's PlayStation icon for select is the touch pad.
                "select" => Button("VIEW", Text[playStation ? "bindings.pad.touchpad" : "bindings.pad.view"]),
                "dpad/up" => Button("DUP", Text["bindings.pad.dpadUp"]),
                "dpad/down" => Button("DDOWN", Text["bindings.pad.dpadDown"]),
                "dpad/left" => Button("DLEFT", Text["bindings.pad.dpadLeft"]),
                "dpad/right" => Button("DRIGHT", Text["bindings.pad.dpadRight"]),
                "leftstick/up" => Button("LS", Text["bindings.pad.leftStickUp"], 0),
                "leftstick/right" => Button("LS", Text["bindings.pad.leftStickRight"], 1),
                "leftstick/down" => Button("LS", Text["bindings.pad.leftStickDown"], 2),
                "leftstick/left" => Button("LS", Text["bindings.pad.leftStickLeft"], 3),
                _ => Button(null, Segment(path))
            };
        }

        private static (BindingLook, string) Button(string glyph, string name, int direction = -1) => (new BindingLook(glyph, name, direction), name);

        private static string DisplayName(string path)
        {
            try
            {
                var control = UnityEngine.InputSystem.InputSystem.FindControl(path);
                return control != null && !string.IsNullOrWhiteSpace(control.displayName) ? control.displayName : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Segment(string path) => path.Substring(path.LastIndexOf('/') + 1).Replace("#(", "").TrimEnd(')');
    }
}
