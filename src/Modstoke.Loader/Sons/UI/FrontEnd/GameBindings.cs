// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Localization;
using Sons.Gui.Input;
using SonsSdk;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInput = Sons.Input.InputSystem;

namespace Modstoke.Loader
{
    internal enum BindingDevice { Keyboard, Pad }

    /// <summary>One action's binding on one device, as the game holds it.</summary>
    internal sealed class BindingSlot
    {
        private readonly List<(InputAction Action, int Index)> _targets = new();

        internal BindingSlot(BindingAction action, BindingDevice device, bool offered, InputActionAsset asset)
        {
            Action = action;
            Device = device;
            Offered = offered;
            if (!offered) return;
            var group = device == BindingDevice.Pad ? GameBindings.PadGroup : GameBindings.KeyboardGroup;
            var complete = action.Paths.Count > 0;
            foreach (var path in action.Paths)
            {
                var input = asset.FindAction(path, false);
                var index = input == null ? -1 : FirstInGroup(input, group);
                if (index >= 0) index += action.Part;
                if (index >= 0 && index < input.bindings.Count) _targets.Add((input, index));
                else complete = false;
            }
            Available = complete;
            if (!complete && !action.Fixed)
                GameBindings.Report("\"" + action.Name + "\" has no " + (device == BindingDevice.Pad ? "controller" : "keyboard")
                    + " binding on every action it covers, so it can't be changed here.");
        }

        internal BindingAction Action { get; }
        internal BindingDevice Device { get; }
        /// <summary>Whether the game offers this device for the action at all; hotkeys are keyboard only, page turns controller only.</summary>
        internal bool Offered { get; }
        /// <summary>Offered, and every path the action covers has a binding for the device, so it can be changed.</summary>
        internal bool Available { get; }
        /// <summary>The control it is on, as the game stores it; empty when nothing is set.</summary>
        internal string Path { get; private set; } = "";
        internal string DefaultPath { get; private set; } = "";
        /// <summary><see cref="Path"/> in the form bindings are compared in.</summary>
        internal string Control { get; private set; } = "";
        internal string DefaultControl { get; private set; } = "";
        internal bool IsDefault => Control == DefaultControl;
        /// <summary>The binding waits for a hold, as Sleep and Save do.</summary>
        internal bool Held { get; private set; }
        /// <summary>Every binding this slot stands for: one per action path, at the group's first binding plus the composite part.</summary>
        internal IReadOnlyList<(InputAction Action, int Index)> Targets => _targets;

        // The first path's binding speaks for all of them; the game copies it over the others when it loads.
        internal void Read()
        {
            if (_targets.Count == 0)
            {
                Path = DefaultPath = Control = DefaultControl = "";
                Held = false;
                return;
            }
            var (input, index) = _targets[0];
            var binding = input.bindings[index];
            Path = binding.effectivePath ?? "";
            DefaultPath = binding.path ?? "";
            Control = GameBindings.Canonical(Path);
            DefaultControl = GameBindings.Canonical(DefaultPath);
            Held = (binding.interactions ?? "").Contains("Hold") || (input.interactions ?? "").Contains("Hold");
        }

        // The game's LookupBindingIndexForGroup, without the error it logs on a miss.
        private static int FirstInGroup(InputAction action, string group)
        {
            var bindings = action.bindings;
            for (var i = 0; i < bindings.Count; i++)
                if (string.Equals(bindings[i].groups, group, StringComparison.Ordinal)) return i;
            return -1;
        }
    }

    /// <summary>One of the game's binding definitions: a row on the bindings screen.</summary>
    internal sealed class BindingAction
    {
        private readonly BindingSlot _keyboard, _pad;
        private string _label, _labelLanguage;

        internal BindingAction(InputBindingDefinition definition, InputActionAsset asset)
        {
            Definition = definition;
            Name = definition._name ?? "";
            var mode = (int)definition._mode;
            Fixed = mode == 0;
            Part = definition._isComposite ? Math.Max(0, definition._compositeIndex) : 0;
            Paths = Strings(definition._actionPaths);
            Ids = Strings(definition._actionIds);
            Maps = new HashSet<string>(Paths.Select(path => path.Split('/')[0]), StringComparer.OrdinalIgnoreCase);
            // The menus' own definitions read both devices, only so that a binding can be warned off them.
            _keyboard = new BindingSlot(this, BindingDevice.Keyboard, Fixed || (mode & 1) != 0, asset);
            _pad = new BindingSlot(this, BindingDevice.Pad, Fixed || (mode & 2) != 0, asset);
        }

        internal InputBindingDefinition Definition { get; }
        /// <summary>The definition's own name, such as "Jump": the row's id and the catalog's key.</summary>
        internal string Name { get; }
        /// <summary>"Map/Action" for every action the definition covers.</summary>
        internal IReadOnlyList<string> Paths { get; }
        internal IReadOnlyList<string> Ids { get; }
        internal IReadOnlyCollection<string> Maps { get; }
        /// <summary>The composite part it binds (negative or positive of a movement axis), or 0.</summary>
        internal int Part { get; }
        /// <summary>One of the menus' own bindings, which no screen changes.</summary>
        internal bool Fixed { get; }
        internal BindingSlot this[BindingDevice device] => device == BindingDevice.Pad ? _pad : _keyboard;

        /// <summary>The game's own name for the action, in the game's language; read the first time it is shown.</summary>
        internal string Label
        {
            get
            {
                if (_label != null && _labelLanguage == Languages.Current) return _label;
                _labelLanguage = Languages.Current;
                return _label = ReadLabel();
            }
        }

        internal void Read()
        {
            _keyboard.Read();
            _pad.Read();
        }

        // The call the game's own controls screen makes for its rows; it loads the string table if it has to.
        private string ReadLabel()
        {
            try
            {
                var text = Definition._localizedName?.GetLocalizedString();
                if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("No translation found", StringComparison.Ordinal)) return text;
                GameBindings.Report("\"" + Name + "\" has no name in the game's language, so it reads as words.");
            }
            catch (Exception error)
            {
                GameBindings.Report("\"" + Name + "\"'s name could not be read: " + error.Message);
            }
            return OptionText.Words(Name);
        }

        private static IReadOnlyList<string> Strings(Il2CppSystem.Collections.Generic.List<string> list)
        {
            var strings = new List<string>();
            if (list == null) return strings;
            for (var i = 0; i < list.Count; i++)
                if (!string.IsNullOrEmpty(list[i])) strings.Add(list[i]);
            return strings;
        }
    }

    /// <summary>Another binding on the same control; <see cref="Slot"/> is null for one that can't be changed here.</summary>
    internal readonly record struct Clash(string Name, BindingSlot Slot);

    /// <summary>
    /// The game's key bindings, read and changed through its own model: the binding definitions its controls screen
    /// lists, the overrides on its input actions and its saved setting. A change reaches every action path the
    /// definition covers and is saved at once, so the game's screen, its prompts and its next start all agree.
    /// </summary>
    internal static class GameBindings
    {
        internal const string KeyboardGroup = "Mouse and Keyboard", PadGroup = "Gamepad";
        private const float RetrySeconds = 5f;

        // The maps the game turns on together (Sons.Input.InputSystem.Apply). Bindings clash only where both can be
        // live at once: Jump and an inventory action may share a key, Jump and a hotkey may not.
        private static readonly string[][] Together =
        {
            new[] { "default", "Construction", "ItemHotkey" },
            new[] { "default", "ItemHotkey", "Vehicle" },
            new[] { "Inventory", "ItemHotkey" },
            new[] { "GrabBag", "Menu" }
        };
        internal static readonly BindingDevice[] Devices = { BindingDevice.Keyboard, BindingDevice.Pad };
        private static readonly string[] PadLayouts = { "<xinputcontroller>", "<dualshockgamepad>", "<dualsensegamepadhid>" };
        private static readonly string[] MouseButtons = { "leftButton", "rightButton", "middleButton", "backButton", "forwardButton" };
        private static readonly List<BindingAction> All = new();
        private static readonly Dictionary<string, string> Canonicals = new();
        private static readonly Dictionary<BindingSlot, IReadOnlyList<Clash>> Shared = new();
        private static readonly HashSet<string> Reported = new();
        private static InputBindingData _data;
        private static InputActionAsset _asset;
        private static float _retryAt;

        internal static bool Ready { get; private set; }
        /// <summary>Every definition in the game's order, the menus' own included.</summary>
        internal static IReadOnlyList<BindingAction> Actions => All;
        internal static int ChangedCount { get; private set; }
        /// <summary>Why the bindings could not be read, for the log.</summary>
        internal static string Problem { get; private set; } = "";

        /// <summary>
        /// Reads the game's bindings; false when this install's can't be read, with the reason in <see cref="Problem"/>.
        /// <paramref name="fresh"/> reads the definitions again, as bindings may have changed elsewhere since.
        /// </summary>
        internal static bool Load(bool fresh = false)
        {
            if (!fresh && !Ready && Time.unscaledTime < _retryAt) return false;
            try
            {
                // Without the mapping the binding data would hand back the asset's unused default copy.
                if (GameInput.InputMapping == null) GameInput.InitializeUnityMapping();
                var data = InputIcons.IsLive(_data) ? _data : FindData();
                if (data == null) return Fail("its binding data is not loaded");
                // Read once, so the data caches the runtime asset the game plays with.
                var runtime = data.CurrentInputActionAsset;
                var asset = GameInput.InputMapping?.asset;
                if (asset == null) return Fail("its input actions are not set up");
                if (runtime == null || runtime.Pointer != asset.Pointer)
                    Report("The binding data's actions are not the ones the game reads; the game's are used.");
                if (!fresh && Ready && data.Pointer == _data.Pointer && asset.Pointer == _asset.Pointer)
                {
                    Reread();
                    return true;
                }
                var definitions = data._bindingDefinitions;
                if (definitions == null || definitions.Count == 0) return Fail("it lists no bindings");
                _data = data;
                _asset = asset;
                Canonicals.Clear();
                All.Clear();
                for (var i = 0; i < definitions.Count; i++)
                    if (definitions[i] != null) All.Add(new BindingAction(definitions[i], asset));
                Ready = true;
                Problem = "";
                Reread();
                return true;
            }
            catch (Exception error)
            {
                return Fail(error.Message);
            }
        }

        /// <summary>Binds a slot to a control on every path it covers; an empty path clears it.</summary>
        internal static bool Set(BindingSlot slot, string path) => Change("set " + slot.Action.Name, () =>
        {
            Override(slot, path);
            Changed(slot.Action);
            Save();
        });

        /// <summary>Binds a slot to a control and gives the slots already on it the slot's old binding.</summary>
        internal static bool Swap(BindingSlot slot, string path, IReadOnlyCollection<BindingSlot> others) => Change("swap " + slot.Action.Name, () =>
        {
            var old = slot.Path;
            foreach (var other in others) Override(other, old);
            Override(slot, path);
            Changed(others.Select(other => other.Action).Append(slot.Action).ToArray());
            Save();
        });

        /// <summary>Puts one slot back to the game's default.</summary>
        internal static bool Reset(BindingSlot slot) => Change("reset " + slot.Action.Name, () =>
        {
            Remove(slot);
            Changed(slot.Action);
            Save();
        });

        /// <summary>Puts both of an action's bindings back to the game's defaults.</summary>
        internal static bool Reset(BindingAction action) => Change("reset " + action.Name, () =>
        {
            Remove(action[BindingDevice.Keyboard]);
            Remove(action[BindingDevice.Pad]);
            Changed(action);
            Save();
        });

        /// <summary>Puts one device's bindings back to the game's defaults, or every binding with null; the game saves them.</summary>
        internal static bool Reset(BindingDevice? device) => Change("reset " + (device?.ToString() ?? "everything"), () =>
        {
            if (device == null) GameInput.ClearInputBindingsSettings();
            else GameInput.ClearInputBindingsSettings(new Il2CppStringArray(new[] { device == BindingDevice.Pad ? PadGroup : KeyboardGroup }));
            _data.ForceRefreshAllControlPaths(_asset);
            _data.RaiseOnActionBindingChanged(null);
            RefreshIcons(null);
            Reread();
        });

        /// <summary>
        /// What else is on <paramref name="path"/> for the slot's device and can be live at the same moment: the game's
        /// actions, then Modstoke's and mods' keybinds. Pairs that share a control in the game's defaults are left out.
        /// </summary>
        internal static IReadOnlyList<Clash> Clashes(BindingSlot slot, string path)
        {
            var control = Canonical(path);
            if (control.Length == 0 || !Ready) return Array.Empty<Clash>();
            var clashes = new List<Clash>();
            foreach (var action in All)
            {
                if (ReferenceEquals(action, slot.Action)) continue;
                var other = action[slot.Device];
                if (other.Targets.Count == 0 || other.Control != control || !Live(slot.Action, action)) continue;
                if (slot.DefaultControl.Length > 0 && slot.DefaultControl == other.DefaultControl) continue;
                clashes.Add(new Clash(action.Label, action.Fixed ? null : other));
            }
            AddKeybinds(clashes, control, slot.Action);
            return clashes;
        }

        /// <summary>The clashes of a slot's own binding, kept until the next change or load.</summary>
        internal static IReadOnlyList<Clash> Clashes(BindingSlot slot)
        {
            if (!slot.Offered) return Array.Empty<Clash>();
            if (Shared.TryGetValue(slot, out var clashes)) return clashes;
            return Shared[slot] = Clashes(slot, slot.Path);
        }

        /// <summary>
        /// A path in the form bindings are compared in. Keyboard paths resolve through the layout, so the game's
        /// "#(M)" paths and Modstoke's keys meet the key they mean; controller paths drop the model, so a binding on one
        /// controller is the same binding on another.
        /// </summary>
        internal static string Canonical(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (Canonicals.TryGetValue(path, out var known)) return known;
            var canonical = path.ToLowerInvariant();
            if (canonical.StartsWith("<keyboard>", StringComparison.Ordinal))
            {
                var control = UnityEngine.InputSystem.InputSystem.FindControl(path);
                if (control != null) canonical = control.path.ToLowerInvariant();
            }
            else
            {
                foreach (var layout in PadLayouts)
                    if (canonical.StartsWith(layout, StringComparison.Ordinal))
                        canonical = "<gamepad>" + canonical.Substring(layout.Length);
            }
            return Canonicals[path] = canonical;
        }

        internal static void Report(string message)
        {
            if (Reported.Add(message)) RuntimeEntryPoint.LogSource?.LogWarning("Bindings: " + message);
        }

        private static InputBindingData FindData()
        {
            InputBindingData first = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<InputBindingData>())
            {
                if (!InputIcons.IsLive(candidate)) continue;
                if (candidate.name == "SonsInputBindingMap") return candidate;
                first ??= candidate;
            }
            return first;
        }

        private static bool Fail(string why)
        {
            Ready = false;
            Problem = why;
            _retryAt = Time.unscaledTime + RetrySeconds;
            Report("the game's key bindings can't be read: " + why + ".");
            return false;
        }

        // Changes go through native code; one that fails is logged and the slots are read again as they now stand.
        private static bool Change(string what, Action change)
        {
            if (!Ready) return false;
            try
            {
                change();
                return true;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Bindings: could not " + what + ": " + error.Message);
                try { Reread(); }
                catch (Exception) { Ready = false; }
                return false;
            }
        }

        // Only the override path changes, so a hold or tap on the binding stays as the game made it.
        private static void Override(BindingSlot slot, string path)
        {
            foreach (var (action, index) in slot.Targets) InputActionRebindingExtensions.ApplyBindingOverride(action, index, path);
        }

        private static void Remove(BindingSlot slot)
        {
            foreach (var (action, index) in slot.Targets) InputActionRebindingExtensions.RemoveBindingOverride(action, index);
        }

        // What the game's own rebinding does once a binding is taken: the paths its prompts read are refreshed and its
        // listeners told. Its prompts already on screen are refreshed too, which the game itself leaves to the next
        // device switch.
        private static void Changed(params BindingAction[] actions)
        {
            var touched = actions.Distinct().ToList();
            foreach (var action in touched)
            {
                action.Definition.AutoRefreshControlPath(_asset, true);
                foreach (var id in action.Ids) _data.RaiseOnActionBindingChanged(id);
            }
            RefreshIcons(touched);
            Reread();
        }

        // Writes the overrides to the game's settings file; it clears the entry when nothing is overridden.
        private static void Save() => GameInput.SaveInputBindingsSettings();

        private static void Reread()
        {
            Shared.Clear();
            var changed = 0;
            foreach (var action in All)
            {
                action.Read();
                if (action.Fixed) continue;
                foreach (var device in Devices)
                    if (action[device].Available && !action[device].IsDefault) changed++;
            }
            ChangedCount = changed;
        }

        private static void RefreshIcons(IReadOnlyCollection<BindingAction> actions)
        {
            foreach (var icon in Resources.FindObjectsOfTypeAll<DynamicInputIcon>())
            {
                // Prefabs never ran Awake; only icons in a scene are refreshed.
                if (!InputIcons.IsLive(icon) || !icon._initalized) continue;
                if (actions != null)
                {
                    var id = icon._actionId;
                    if (string.IsNullOrEmpty(id) || !actions.Any(action => action.Definition.MatchesAction(id))) continue;
                }
                try
                {
                    icon.InvalidateInstancesAndRefresh();
                }
                catch (Exception error)
                {
                    Report("a prompt on screen could not follow a new binding: " + error.Message);
                }
            }
        }

        private static bool Live(BindingAction a, BindingAction b)
        {
            foreach (var left in a.Maps)
                foreach (var right in b.Maps)
                    if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                        || Together.Any(group => group.Contains(left, StringComparer.OrdinalIgnoreCase) && group.Contains(right, StringComparer.OrdinalIgnoreCase)))
                        return true;
            return false;
        }

        // Modstoke's keybinds and SDK mods' are kept out of the screen, but a binding is still warned off them; off a
        // Modstoke keybind only where it can answer while the action's maps are on, as a seat's or device's never does,
        // and not where it stands in for this very action on purpose, as a mod's own Interact does.
        private static void AddKeybinds(List<Clash> clashes, string control, BindingAction action)
        {
            foreach (var keybind in InputBindings.Snapshot().Values)
            {
                if (!keybind.Enabled || !InputBindings.LiveWithGameMaps(keybind, action.Maps)
                    || InputBindings.SharesGameAction(keybind, action)) continue;
                var path = keybind.Key == KeyCode.None ? null : PathOf(keybind.Key);
                // A chord is left out: the game's button pressed alone never sets it off.
                var pad = keybind.Pad.IsChord || keybind.Pad.IsNone ? null : InputGlyphs.PathOf(keybind.Pad.First.Value);
                if (path != null && Canonical(path) == control || pad != null && Canonical(pad) == control)
                    clashes.Add(new Clash(OwnerOf(keybind), null));
            }
            foreach (var keybind in ModInputCache.Keybinds.Values)
            {
                try
                {
                    var bindings = keybind.Action.bindings;
                    if (bindings.Count == 0 || Canonical(bindings[0].effectivePath) != control) continue;
                    var entry = keybind.ConfigEntry;
                    var mod = entry.Category?.DisplayName;
                    clashes.Add(new Clash(string.IsNullOrEmpty(mod) ? entry.DisplayName : entry.DisplayName + " (" + mod + ")", null));
                }
                catch (Exception)
                {
                    // A keybind whose action is gone has nothing to clash with.
                }
            }
        }

        private static string OwnerOf(KeybindSnapshot keybind) => keybind.Id switch
        {
            KeybindIds.ToggleModsMenu => FrontEnd.Text["bindings.owner.mods"],
            KeybindIds.PhotoMode => FrontEnd.Text["bindings.owner.photo"],
            ConsoleOverlay.KeybindId => FrontEnd.Text["bindings.owner.console"],
            _ => keybind.DisplayName + " (" + keybind.Category + ")"
        };

        // The legacy key a Modstoke keybind polls, as an Input System path. Letters go by what the layout prints, as the
        // legacy keys do; the rest by position.
        internal static string PathOf(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z) return "<Keyboard>/#(" + (char)('A' + (key - KeyCode.A)) + ")";
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return "<Keyboard>/digit" + (key - KeyCode.Alpha0);
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return "<Keyboard>/numpad" + (key - KeyCode.Keypad0);
            if (key >= KeyCode.F1 && key <= KeyCode.F15) return "<Keyboard>/f" + (key - KeyCode.F1 + 1);
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse4) return "<Mouse>/" + MouseButtons[key - KeyCode.Mouse0];
            var name = key switch
            {
                KeyCode.Space => "space", KeyCode.Return => "enter", KeyCode.Tab => "tab", KeyCode.Backspace => "backspace",
                KeyCode.Delete => "delete", KeyCode.Insert => "insert", KeyCode.Home => "home", KeyCode.End => "end",
                KeyCode.PageUp => "pageUp", KeyCode.PageDown => "pageDown", KeyCode.UpArrow => "upArrow",
                KeyCode.DownArrow => "downArrow", KeyCode.LeftArrow => "leftArrow", KeyCode.RightArrow => "rightArrow",
                KeyCode.LeftShift => "leftShift", KeyCode.RightShift => "rightShift", KeyCode.LeftControl => "leftCtrl",
                KeyCode.RightControl => "rightCtrl", KeyCode.LeftAlt => "leftAlt", KeyCode.RightAlt => "rightAlt",
                KeyCode.BackQuote => "backquote",
                _ => null
            };
            return name == null ? null : "<Keyboard>/" + name;
        }
    }
}
