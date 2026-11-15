// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Modstoke.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// Keys and buttons: the game's bindings on Modstoke's settings screen, every action with its keyboard and
    /// controller binding side by side. One column holds focus down the whole list, so up and down stay on the device
    /// being set. A binding is changed by pressing the new key or button, tapped back to its default or held to clear;
    /// taking a control another action can use at the same moment is asked about first. Every change goes through the
    /// game's own binding model and is saved at once.
    /// </summary>
    internal sealed class BindingsScreen : SettingsScreen
    {
        private const float HeaderHeight = 30, TagHeight = 22, TapSeconds = 0.3f, ClearSeconds = 0.8f, RefusedSeconds = 1.5f, Breath = 1.2f;
        // A stick direction's chevron, by direction: up, right, down, left.
        private static readonly float[] Angles = { 0f, -90f, 180f, 90f };
        private static readonly Color SelectedFill = MenuKit.Hex(0x111212, 0.10f);
        private static int _lastTab;

        private enum CellState { Idle, Listening, Holding, Refused }

        // Everything a cell's look depends on; it is drawn again only when this changes. The icon's availability counts,
        // as the game loads its icons with its own prompts and they can arrive after the cell was first drawn.
        private readonly record struct CellLook(string Text, BindingLook Key, bool Icon, bool PlayStation, bool Plate, bool Selected,
            bool Available, CellState State, bool Clash, bool Changed);

        private sealed class CellView
        {
            internal RectTransform Rect, Direction;
            internal Image Fill, Track, Bar, Pip, Mark;
            internal Image[] Strokes;
            internal KeyTag Tag;
            internal TextMeshProUGUI Text;
            internal CellLook? Painted;
        }

        private sealed class RowCells
        {
            internal CellView Keyboard, Pad;
            internal CellView this[BindingDevice device] => device == BindingDevice.Pad ? Pad : Keyboard;
        }

        private readonly IReadOnlyList<string> _tabIds;
        private readonly Dictionary<BindingRow, RowCells> _cells = new();
        private TextMeshProUGUI _keyboardCaption, _padCaption;
        private BindingListener _listener;
        private BindingRow _listenRow, _holdRow;
        private BindingSlot _refused;
        private BindingDevice _column;
        private float _controlWidth, _holdStart, _refusedUntil;
        private int _shownSeconds;
        private bool _columnSet, _lastPad, _awaitRelease, _mouseHold;

        private BindingsScreen(IReadOnlyList<string> tabIds) : base(Math.Min(_lastTab, tabIds.Count - 1)) => _tabIds = tabIds;

        /// <summary>Opens the screen, or the game's own controls screen on an install whose bindings can't be read.</summary>
        internal static void Open()
        {
            BindingNames.Forget();
            if (GameBindings.Load(fresh: true))
            {
                FrontEnd.Push(new BindingsScreen(BindingsCatalog.TabIds()));
                return;
            }
            RuntimeEntryPoint.LogSource?.LogWarning("Bindings: the game's own controls screen opens instead, as " + GameBindings.Problem + ".");
            OptionsCatalog.OpenGameOptions();
        }

        /// <summary>What the Options row says: how many bindings are changed, or null for "Open".</summary>
        internal static string Changed()
        {
            if (!GameBindings.Ready && !GameBindings.Load()) return null;
            var count = GameBindings.ChangedCount;
            return count > 0 ? Text.Count("bindings.changed", count) : null;
        }

        protected override IReadOnlyList<string> TabIds => _tabIds;
        protected override OptionTab BuildTab(string id) => BindingsCatalog.Tab(id);
        protected override string Heading => Text["bindings.title"];
        protected override string TabName(string id) => Text["bindings.tab." + id];
        protected override string SectionName(string id) => Text["bindings.section." + id];
        protected override string ItemName(OptionRow row) => row is BindingRow binding ? binding.Action.Label : row.Id;
        protected override string EmptyText => GameBindings.Ready ? base.EmptyText : Text["bindings.unavailable"];
        protected override float ListHeaderHeight => HeaderHeight;

        // The cells take a share of what the list has beside the details column: their widest at 16:9, narrower at
        // 4:3, where the names keep the rest and end in "…" as on Options.
        protected override SettingsLayout Layout
        {
            get
            {
                var list = Screen.width / FrontEnd.S - 60 - (60 + 400 + 56);
                _controlWidth = Mathf.Clamp(list * 0.46f, 300, 440);
                return new SettingsLayout { ControlWidth = _controlWidth };
            }
        }

        protected override string ItemAbout(OptionRow row)
        {
            if (row is not BindingRow binding) return "";
            if (_listener != null && ReferenceEquals(binding, _listenRow))
                return Text.Get(_listener.Slot.Device == BindingDevice.Pad ? "bindings.listen.aboutButton" : "bindings.listen.aboutKey",
                        ("action", binding.Action.Label))
                    + "\n\n" + Text.Count("bindings.listen.seconds", Mathf.CeilToInt(_listener.Left));
            var about = Text["bindings.about." + binding.Section];
            var slot = binding[_column];
            if (!slot.Offered) about += "\n\n" + Text[_column == BindingDevice.Pad ? "bindings.notOnPad" : "bindings.notOnKeyboard"];
            else if (slot.Held) about += "\n\n" + Text["bindings.hold"];
            return about;
        }

        protected override IReadOnlyList<(string Label, string Value)> ItemFacts(OptionRow row)
        {
            if (row is not BindingRow binding) return null;
            var facts = new List<(string Label, string Value)>
            {
                (Text["bindings.fact.keyboard"], Describe(binding[BindingDevice.Keyboard])),
                (Text["bindings.fact.pad"], Describe(binding[BindingDevice.Pad]))
            };
            var clashes = GameBindings.Clashes(binding[_column]);
            if (clashes.Count > 0)
                facts.Add((Text["bindings.shared"], "<color=" + MenuKit.Html(MenuKit.Red) + ">" + string.Join(", ", clashes.Select(clash => clash.Name)) + "</color>"));
            return facts;
        }

        private string Describe(BindingSlot slot)
            => !slot.Offered ? Text[slot.Device == BindingDevice.Pad ? "bindings.keyboardOnly" : "bindings.padOnly"]
                : slot.Path.Length == 0 ? Text["bindings.unset"]
                : BindingNames.Name(slot.Path);

        protected override void Build()
        {
            // A new screen size or font builds everything again; a capture under way ends first.
            StopListening();
            _cells.Clear();
            _keyboardCaption = _padCaption = null;
            base.Build();
        }

        protected override void TabChanged(int index)
        {
            _lastTab = index;
            _cells.Clear();
        }

        // The two columns' names, each centred over its cells; the device in use reads brighter.
        protected override void BuildListHeader(RectTransform header)
        {
            var half = _controlWidth / 2;
            _keyboardCaption = Caption(header, "Keyboard", 12 + _controlWidth, 12 + half + 6);
            _padCaption = Caption(header, "Pad", 12 + half - 6, 12);
        }

        private TextMeshProUGUI Caption(RectTransform header, string name, float left, float right)
        {
            var caption = MenuKit.Text(header, name, "", U(13), true, MenuKit.Muted, TextAlignmentOptions.Center);
            caption.characterSpacing = 28;
            caption.rectTransform.Place(1, 0, 1, 1, -U(left), 0, U(right), 0);
            return caption;
        }

        protected override bool BuildControl(OptionRow row, RectTransform control)
        {
            if (row is not BindingRow binding) return false;
            _cells[binding] = new RowCells { Keyboard = BuildCell(control, "Keyboard", false), Pad = BuildCell(control, "Pad", true) };
            return true;
        }

        // A faint cell holding the key's cap or the controller's icon, a dot in one top corner for a binding changed from
        // the game's and a pip in the other for a clash, and a bar along its foot for the focus underline, a hold or the
        // time left to press something.
        private CellView BuildCell(RectTransform control, string name, bool right)
        {
            var rect = MenuKit.Box(control, name).Place(right ? 0.5f : 0, 0, right ? 1 : 0.5f, 1, right ? U(6) : 0, U(5), right ? 0 : U(6), U(5));
            var bar = Mathf.Max(2f, U(2));
            var cell = new CellView { Rect = rect, Fill = MenuKit.Fill(rect, null, MenuKit.Cell) };
            cell.Track = MenuKit.Fill(MenuKit.Box(rect, "Track").Place(0, 0, 1, 0, 0, 0, 0, -bar), null, MenuKit.OnPlateTrack);
            cell.Bar = MenuKit.Fill(MenuKit.Box(rect, "Bar").Place(0, 0, 1, 0, 0, 0, 0, -bar), null, MenuKit.OnPlate);
            cell.Tag = new KeyTag(rect, "Key", U(TagHeight));
            cell.Direction = MenuKit.Chevron(rect, "Direction", U(10), MenuKit.Ink);
            cell.Strokes = cell.Direction.GetComponentsInChildren<Image>(true).ToArray();
            cell.Text = MenuKit.Text(rect, "Text", "", U(14), true, MenuKit.Faint, TextAlignmentOptions.Center, true);
            cell.Text.characterSpacing = 18;
            cell.Text.rectTransform.Place(0, 0, 1, 1, U(8), 0, U(8), 0);
            var pip = MenuKit.Box(rect, "Clash");
            pip.anchorMin = pip.anchorMax = pip.pivot = Vector2.one;
            pip.sizeDelta = new Vector2(U(6), U(6));
            pip.anchoredPosition = new Vector2(-U(4), -U(4));
            // The logo's red: the paler error red reads as white on a cell, like the changed dot.
            cell.Pip = MenuKit.Fill(pip, null, MenuKit.Accent);
            var mark = MenuKit.Box(rect, "Changed");
            mark.anchorMin = mark.anchorMax = mark.pivot = Vector2.up;
            mark.sizeDelta = new Vector2(U(6), U(6));
            mark.anchoredPosition = new Vector2(U(4), -U(4));
            cell.Mark = MenuKit.Fill(mark, null, MenuKit.Muted);
            return cell;
        }

        internal override void Retext()
        {
            base.Retext();
            if (_keyboardCaption == null) return;
            var culture = Languages.CultureOf(Languages.Current);
            Fit(_keyboardCaption, Text["bindings.column.keyboard"].ToUpper(culture), 13, 9);
            Fit(_padCaption, Text["bindings.column.pad"].ToUpper(culture), 13, 9);
            PaintCaptions();
        }

        // Words longer than their box step down in size, to a floor, rather than running into the next column; a 4:3
        // screen gives a cell little more than a hundred units.
        private static void Fit(TextMeshProUGUI label, string text, float size, float smallest)
        {
            label.text = text;
            label.fontSize = U(size);
            var room = label.rectTransform.rect.width;
            var wanted = label.GetPreferredValues(text).x;
            if (room > 0 && wanted > room) label.fontSize = Mathf.Max(U(smallest), U(size) * room / wanted);
        }

        private void PaintCaptions()
        {
            if (_keyboardCaption == null) return;
            var pad = FrontEnd.PadActive;
            _keyboardCaption.color = pad ? MenuKit.Muted : MenuKit.Soft;
            _padCaption.color = pad ? MenuKit.Soft : MenuKit.Muted;
        }

        internal override void Opened()
        {
            // The column starts on the device in use, the first time only; a rebuild keeps the one chosen.
            if (!_columnSet)
            {
                _columnSet = true;
                _lastPad = FrontEnd.PadActive;
                _column = _lastPad ? BindingDevice.Pad : BindingDevice.Keyboard;
            }
            base.Opened();
            PaintCaptions();
        }

        internal override void Closed()
        {
            StopListening();
            base.Closed();
        }

        internal override void DeviceChanged()
        {
            base.DeviceChanged();
            // Only a real change of device moves the column, so a keyboard player setting the controller column stays on
            // it through the dialogs that close over this screen.
            if (FrontEnd.PadActive != _lastPad && _listener == null)
            {
                _lastPad = FrontEnd.PadActive;
                _column = _lastPad ? BindingDevice.Pad : BindingDevice.Keyboard;
            }
            PaintCaptions();
            Refresh();
        }

        internal override IReadOnlyList<PromptSpec> Prompts
        {
            get
            {
                // While listening the controller's B is a button to bind, so Start cancels the controller column. A click
                // on Cancel is caught by the listener itself, before the capture can take it as the left button.
                if (_listener != null)
                    return new[] { new PromptSpec(Text["common.cancel"], _listener.Slot.Device == BindingDevice.Pad ? "MENU" : "B", "ESC", null) };
                return new[]
                {
                    new PromptSpec(Text["common.back"], "B", "ESC", () => FrontEnd.Pop()),
                    new PromptSpec(Text["bindings.prompt.change"], "A", "ENTER", null),
                    new PromptSpec(Text["bindings.prompt.default"], "X", "DEL", HoldFromPrompt),
                    new PromptSpec(Text["bindings.prompt.reset"], "Y", "F2", OpenReset)
                };
            }
        }

        // F5 has no held state, so it taps at once; Delete and X wait for their release in Tick.
        internal override void OnAlt1()
        {
            if (!FrontInput.Alt1Held) DefaultFocused();
        }

        // The mouse button held on the Default prompt is Delete held: let go quickly for the default, or keep it down
        // to clear.
        private void HoldFromPrompt()
        {
            if (_listener != null || FocusedRow is not BindingRow)
            {
                MenuSound.Play(MenuCue.Back);
                return;
            }
            _mouseHold = true;
            MenuSound.Play(MenuCue.Move);
        }

        internal override void OnAlt2() => OpenReset();

        internal override void Tick()
        {
            base.Tick();
            var now = Time.unscaledTime;
            TickHold(now);
            TickListening(now);
            if (_refused == null || now < _refusedUntil) return;
            _refused = null;
            Refresh();
        }

        // Hovering a cell takes its column, as hovering a row takes focus.
        protected override void Pointer(Focusable over)
        {
            base.Pointer(over);
            if (!FrontInput.PointerMoved || !TryRow(over, out var row) || row is not BindingRow binding) return;
            if (CellUnder(binding) is not { } device || device == _column) return;
            _column = device;
            MenuSound.Play(MenuCue.Move);
            Refresh();
        }

        protected override bool ActivateControl(OptionRow row)
        {
            if (row is not BindingRow binding) return false;
            // A click takes the cell it lands on; one on the name, Enter or A take the column in use.
            if (FromPointer && CellUnder(binding) is { } device) _column = device;
            Listen(binding);
            return true;
        }

        protected override bool AdjustControl(OptionRow row, int direction)
        {
            if (row is not BindingRow) return false;
            var column = direction < 0 ? BindingDevice.Keyboard : BindingDevice.Pad;
            if (column == _column) return true;
            _column = column;
            Refresh();
            return true;
        }

        protected override bool CaptureInput()
        {
            if (_listener == null) return base.CaptureInput();
            var listener = _listener;
            var state = listener.Step();
            if (state is BindingListener.Phase.Arming or BindingListener.Phase.Listening && !listener.Ended) return true;
            var slot = listener.Slot;
            var path = listener.Path;
            StopListening();
            if (state == BindingListener.Phase.Captured) Commit(slot, path);
            else MenuSound.Play(MenuCue.Back);
            Refresh();
            return true;
        }

        private void Listen(BindingRow row)
        {
            var slot = row[_column];
            // Not on this device, or not one the game lets change: the cell already says which.
            if (!slot.Available)
            {
                MenuSound.Play(MenuCue.Back);
                return;
            }
            var listener = BindingListener.Begin(slot);
            if (listener == null)
            {
                // Something else holds Modstoke's input, as a mod's own window can.
                MenuSound.Play(MenuCue.Back);
                return;
            }
            _listener = listener;
            _listenRow = row;
            _refused = null;
            _shownSeconds = 0;
            MenuSound.Play(MenuCue.Forward);
            PulseRow(row);
            FrontEnd.RefreshPrompts();
            Refresh();
        }

        // Ends a capture without painting, as the screen may be on its way out.
        private void StopListening()
        {
            if (_listener == null) return;
            _listener.End();
            _listener = null;
            _listenRow = null;
            // A Delete or X that was just bound, still held, must not go on to clear it.
            _awaitRelease = true;
            FrontEnd.RefreshPrompts();
        }

        private void Commit(BindingSlot slot, string path)
        {
            // Only the column's own devices: a control one controller alone has (a touch pad press) can't be shown or kept.
            if (!Accepts(slot.Device, path))
            {
                _refused = slot;
                _refusedUntil = Time.unscaledTime + RefusedSeconds;
                MenuSound.Play(MenuCue.Back);
                return;
            }
            if (GameBindings.Canonical(path) == slot.Control)
            {
                MenuSound.Play(MenuCue.Activate);
                return;
            }
            var clashes = GameBindings.Clashes(slot, path);
            if (clashes.Count == 0) Apply(slot, () => GameBindings.Set(slot, path));
            else AskAboutClash(slot, path, clashes);
        }

        private static bool Accepts(BindingDevice device, string path)
            => device == BindingDevice.Pad
                ? path.StartsWith("<Gamepad>/", StringComparison.OrdinalIgnoreCase)
                : path.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("<Mouse>/", StringComparison.OrdinalIgnoreCase);

        // Swap is offered first where every other user can take the old binding instead; the menus' own bindings and
        // Modstoke's and mods' keybinds can't, so with any of those it is keep both or leave it, and the line names
        // which ones are fixed.
        private void AskAboutClash(BindingSlot slot, string path, IReadOnlyList<Clash> clashes)
        {
            var others = clashes.Where(clash => clash.Slot != null).Select(clash => clash.Slot).ToList();
            var names = string.Join(", ", clashes.Where(clash => clash.Slot != null).Select(clash => clash.Name));
            var fixedNames = string.Join(", ", clashes.Where(clash => clash.Slot == null).Select(clash => clash.Name));
            var swappable = others.Count == clashes.Count;
            var line = !swappable
                ? others.Count == 0 ? Text.Get("bindings.conflict.fixedLine", ("actions", fixedNames))
                    : Text.Get("bindings.conflict.mixedLine", ("actions", names), ("fixed", fixedNames))
                : slot.Path.Length == 0 ? Text.Get("bindings.conflict.clearLine", ("actions", names))
                : Text.Get("bindings.conflict.swapLine", ("actions", names), ("old", BindingNames.Name(slot.Path)));
            var actions = new List<MenuAction>();
            if (swappable) actions.Add(new MenuAction(Text["bindings.conflict.swap"], () => Apply(slot, () => GameBindings.Swap(slot, path, others), others)));
            actions.Add(new MenuAction(Text["bindings.conflict.both"], () => Apply(slot, () => GameBindings.Set(slot, path))));
            actions.Add(new MenuAction(Text["common.cancel"], () => { }));
            FrontEnd.Push(new ActionsScreen(Text.Get("bindings.conflict.title", ("key", BindingNames.Name(path))), line, actions));
        }

        // A binding taken: its row jolts, and so do rows that took the old binding in a swap.
        private void Apply(BindingSlot slot, Func<bool> change, IEnumerable<BindingSlot> swapped = null)
        {
            if (!change())
            {
                MenuSound.Play(MenuCue.Back);
                Refresh();
                return;
            }
            MenuSound.Play(MenuCue.Activate);
            foreach (var action in (swapped ?? Enumerable.Empty<BindingSlot>()).Append(slot).Select(other => other.Action))
            {
                var row = _cells.Keys.FirstOrDefault(candidate => ReferenceEquals(candidate.Action, action));
                if (row != null) PulseRow(row);
            }
            Refresh();
        }

        private void DefaultFocused()
        {
            if (_listener != null || FocusedRow is not BindingRow row) return;
            var slot = row[_column];
            // Nothing to put back is answered too; the cells' dots show which of the row's bindings is the changed one.
            if (!slot.Available || slot.IsDefault || !GameBindings.Reset(slot))
            {
                MenuSound.Play(MenuCue.Back);
                return;
            }
            MenuSound.Play(MenuCue.On);
            PulseRow(row);
            Refresh();
        }

        private void ClearFocused()
        {
            if (_listener != null || FocusedRow is not BindingRow row) return;
            var slot = row[_column];
            if (!slot.Available || slot.Path.Length == 0 || !GameBindings.Set(slot, ""))
            {
                MenuSound.Play(MenuCue.Back);
                Refresh();
                return;
            }
            MenuSound.Play(MenuCue.Off);
            PulseRow(row);
            Refresh();
        }

        // One action resets at once; a device or everything is asked about first.
        private void OpenReset()
        {
            if (_listener != null || !GameBindings.Ready) return;
            var actions = new List<MenuAction>();
            if (FocusedRow is BindingRow row)
                actions.Add(new MenuAction(Text.Get("bindings.reset.action", ("action", row.Action.Label)), () => ResetAction(row)));
            actions.Add(new MenuAction(Text["bindings.column.keyboard"], () => AskReset(BindingDevice.Keyboard, "bindings.reset.keyboardTitle")));
            actions.Add(new MenuAction(Text["bindings.column.pad"], () => AskReset(BindingDevice.Pad, "bindings.reset.padTitle")));
            actions.Add(new MenuAction(Text["bindings.reset.everything"], () => AskReset(null, "bindings.reset.allTitle")));
            MenuSound.Play(MenuCue.Forward);
            FrontEnd.Push(new ActionsScreen(Text["bindings.reset.title"], Text["bindings.reset.line"], actions));
        }

        private void ResetAction(BindingRow row)
        {
            if (!GameBindings.Reset(row.Action))
            {
                MenuSound.Play(MenuCue.Back);
                return;
            }
            MenuSound.Play(MenuCue.On);
            PulseRow(row);
            Refresh();
        }

        // A whole device or everything can't be undone, so focus starts on Cancel.
        private void AskReset(BindingDevice? device, string titleKey)
            => FrontEnd.Push(new ConfirmScreen(titleKey, "bindings.reset.body", "bindings.reset.yes", "common.cancel", () =>
            {
                if (!GameBindings.Reset(device)) MenuSound.Play(MenuCue.Back);
                Refresh();
            }, cautious: true));

        // Delete and X, or the mouse on the Default prompt, tap for the default and hold to clear, as a saved game is
        // held to delete. The bar shows once the hold is past a tap.
        private void TickHold(float now)
        {
            if (_awaitRelease && !Alt1Down()) _awaitRelease = false;
            if (_mouseHold && !FrontInput.Held) _mouseHold = false;
            var row = FocusedRow as BindingRow;
            var held = (FrontInput.Alt1Held || _mouseHold) && !_awaitRelease && _listener == null && row != null;
            if (held && !ReferenceEquals(row, _holdRow))
            {
                // A new press, or focus moved under it: the hold starts over on this row.
                _holdRow = row;
                _holdStart = now;
            }
            if (held)
            {
                var holding = now - _holdStart;
                if (holding >= TapSeconds + ClearSeconds)
                {
                    _holdRow = null;
                    _awaitRelease = true;
                    _mouseHold = false;
                    ClearFocused();
                }
                else if (holding >= TapSeconds && _cells.TryGetValue(row, out var cells))
                {
                    var cell = cells[_column];
                    if (cell.Painted?.State != CellState.Holding) Refresh();
                    SetBar(cell, (holding - TapSeconds) / ClearSeconds);
                }
                return;
            }
            if (_holdRow == null) return;
            // Let go: a quick press puts the binding back; one held past a tap but short of clearing does nothing.
            var tapped = now - _holdStart < TapSeconds;
            _holdRow = null;
            if (tapped) DefaultFocused();
            Refresh();
        }

        // The key or button itself, whether or not the menus are listening, so a press held through a capture is waited out.
        private static bool Alt1Down()
        {
            if (UnityEngine.Input.GetKey(KeyCode.Delete)) return true;
            try { return Gamepad.current?.buttonWest.isPressed == true; }
            catch (Exception) { return false; }
        }

        // "Press a key" breathes, and the bar along the cell's foot drains over the time left.
        private void TickListening(float now)
        {
            if (_listener == null || _listenRow == null || !_cells.TryGetValue(_listenRow, out var cells)) return;
            var cell = cells[_listener.Slot.Device];
            var left = _listener.Left;
            SetBar(cell, left / BindingListener.Timeout);
            var color = MenuKit.Plate;
            color.a = Mathf.Lerp(0.55f, 1f, 0.5f + 0.5f * Mathf.Cos(now * Breath * 2f * Mathf.PI));
            cell.Text.color = color;
            var seconds = Mathf.CeilToInt(left);
            if (seconds == _shownSeconds) return;
            _shownSeconds = seconds;
            Refresh();
        }

        private static void SetBar(CellView cell, float fraction) => cell.Bar.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(fraction), 0);

        private BindingDevice? CellUnder(BindingRow row)
        {
            if (!_cells.TryGetValue(row, out var cells)) return null;
            if (RectTransformUtility.RectangleContainsScreenPoint(cells.Keyboard.Rect, FrontInput.Pointer, null)) return BindingDevice.Keyboard;
            if (RectTransformUtility.RectangleContainsScreenPoint(cells.Pad.Rect, FrontInput.Pointer, null)) return BindingDevice.Pad;
            return null;
        }

        protected override void PaintControl(OptionRow row, bool plate, bool available)
        {
            if (row is not BindingRow binding || !_cells.TryGetValue(binding, out var cells)) return;
            var playStation = Glyphs.PlayStationPad;
            PaintCell(binding, BindingDevice.Keyboard, cells.Keyboard, plate, available, false);
            PaintCell(binding, BindingDevice.Pad, cells.Pad, plate, available, playStation);
        }

        // With the game's bindings unreadable the row dims like a setting switched off: no cell, faint keys.
        private void PaintCell(BindingRow row, BindingDevice device, CellView cell, bool plate, bool available, bool playStation)
        {
            var slot = row[device];
            var selected = plate && device == _column;
            var state = _listener != null && ReferenceEquals(_listener.Slot, slot) ? CellState.Listening
                : ReferenceEquals(_refused, slot) ? CellState.Refused
                : selected && slot.Available && ReferenceEquals(row, _holdRow) && Time.unscaledTime - _holdStart >= TapSeconds ? CellState.Holding
                : CellState.Idle;
            string text = null;
            var key = default(BindingLook);
            if (state == CellState.Listening) text = Text[device == BindingDevice.Pad ? "bindings.listen.button" : "bindings.listen.key"];
            else if (state == CellState.Refused) text = Text["bindings.refused"];
            else if (!slot.Offered) text = Text[device == BindingDevice.Pad ? "bindings.keyboardOnly" : "bindings.padOnly"];
            else if (slot.Path.Length == 0) text = Text["bindings.unset"];
            else key = BindingNames.Look(slot.Path);
            var icon = key.Glyph != null && Glyphs.TryGet(key.Glyph, out _);
            var settled = available && state is CellState.Idle or CellState.Holding;
            var look = new CellLook(text, key, icon, playStation, plate, selected, available, state,
                settled && GameBindings.Clashes(slot).Count > 0, settled && slot.Offered && !slot.IsDefault);
            if (cell.Painted == look) return;
            cell.Painted = look;

            // A device the action does not use shows its words alone; listening is a solid plate of its own.
            var listening = state == CellState.Listening;
            cell.Fill.enabled = listening || available && slot.Offered && (!plate || selected);
            cell.Fill.color = listening ? MenuKit.OnPlate : selected ? SelectedFill : MenuKit.Cell;
            cell.Track.enabled = state == CellState.Holding;
            cell.Bar.enabled = selected || listening;
            cell.Bar.color = listening ? MenuKit.Accent : MenuKit.OnPlate;
            if (state is CellState.Idle or CellState.Refused) SetBar(cell, 1);
            cell.Pip.enabled = look.Clash;
            cell.Mark.enabled = look.Changed;
            cell.Mark.color = plate ? MenuKit.OnPlateMuted : MenuKit.Muted;
            var culture = Languages.CultureOf(Languages.Current);
            cell.Text.enabled = text != null;
            if (text == null)
            {
                PaintKey(cell, key, plate, available, culture);
                return;
            }
            cell.Tag.Set("", false);
            cell.Direction.gameObject.SetActive(false);
            Fit(cell.Text, text.ToUpper(culture), 14, 10);
            cell.Text.color = listening ? MenuKit.Plate : state == CellState.Refused ? MenuKit.Accent : plate ? MenuKit.OnPlateMuted : MenuKit.Faint;
        }

        // A keycap, or the game's icon for a controller button and the two main mouse buttons; a stick direction adds a
        // chevron pointing its way. Until the game has loaded its icons, a button's keycap reads its name.
        private void PaintKey(CellView cell, BindingLook key, bool plate, bool available, CultureInfo culture)
        {
            var name = key.Text.ToUpper(culture);
            cell.Tag.Label.fontSize = U(TagHeight) * 0.5f;
            cell.Tag.Set(key.Glyph ?? name, key.Glyph != null, name);
            var ink = !available ? MenuKit.Faint : plate ? MenuKit.OnPlate : MenuKit.Ink;
            cell.Tag.Tint(ink, plate ? MenuKit.Plate : MenuKit.Dark, available ? Color.white : MenuKit.Faint);
            var pointing = key.Direction >= 0;
            var room = cell.Rect.rect.width - U(pointing ? 36 : 12);
            var size = cell.Tag.Size;
            if (!cell.Tag.Icon && room > 0 && size.x > room)
            {
                cell.Tag.Label.fontSize *= room / size.x;
                cell.Tag.Rect.sizeDelta = new Vector2(room, size.y);
            }
            cell.Direction.gameObject.SetActive(pointing);
            cell.Tag.Rect.anchoredPosition = new Vector2(pointing ? -U(8) : 0, 0);
            if (!pointing) return;
            cell.Direction.anchoredPosition = new Vector2(cell.Tag.Size.x / 2 - U(8) + U(12), 0);
            cell.Direction.localEulerAngles = new Vector3(0, 0, Angles[key.Direction]);
            foreach (var stroke in cell.Strokes) stroke.color = ink;
        }
    }
}
