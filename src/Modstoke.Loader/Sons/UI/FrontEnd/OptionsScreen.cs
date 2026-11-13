// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;
using Sons.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// Options: the game's settings and Modstoke's own on tabs, one list of rows each, with a column that says
    /// what the focused setting does and what it defaults to. Every change applies at once and is saved
    /// shortly after, as well as when the screen closes.
    /// </summary>
    internal sealed class OptionsScreen : FrontScreen
    {
        private const float StripHeight = 143, ListTop = 184, RowHeight = 44, SectionHeight = 44, ControlWidth = 380;
        private const float DetailsWidth = 400, TagHeight = 24, SaveDelay = 1.5f, RefreshSeconds = 0.5f;

        private sealed class RowView
        {
            internal OptionRow Row;
            internal RectTransform Rect, Track, Thumb, Fill, Pips, Control;
            internal TextMeshProUGUI Label, Value;
            internal Image[] Left, Right;
            internal Image Changed;
            internal Image[] PipMarks;
            internal Focusable Focus;
            internal readonly Ease Slide = new(22f);
        }

        private sealed class TabView
        {
            internal string Id;
            internal RectTransform Rect, Under;
            internal TextMeshProUGUI Label;
            internal Glitch Flash;
            internal float Width;
        }

        private static int _lastTab;
        private readonly List<TabView> _tabs = new();
        private readonly List<RowView> _rows = new();
        private readonly Ease _scroll = new(16f);
        private int _tab, _focusIndex;
        private RectTransform _viewport, _content;
        private KeyTag _tabLeft, _tabRight;
        private TextMeshProUGUI _section, _title, _name, _about, _default, _empty;
        private Glitch _titleFlash;
        private Glide _rowMark, _tabMark;
        private RowView _drag;
        private float _saveAt, _nextRefresh, _contentHeight;
        private bool _built;

        internal OptionsScreen() => _tab = _lastTab;

        protected override void Build()
        {
            _built = false;
            _tabs.Clear();
            _rows.Clear();
            var pad = U(60);
            MenuKit.Backdrop(Root, U(StripHeight - 24), U(110));
            _section = MenuKit.Text(Root, "Section", "", U(14), true, MenuKit.Muted);
            _section.characterSpacing = 32;
            _section.rectTransform.At(pad, U(30), U(700), U(18));
            _title = MenuKit.Text(Root, "Title", "", U(50), true, MenuKit.Ink);
            _title.characterSpacing = 5;
            _title.rectTransform.At(pad, U(46), U(1000), U(66));
            _titleFlash = new Glitch(_title);

            BuildTabs(pad);

            var listRight = pad + U(DetailsWidth + 56);
            var list = MenuKit.Box(Root, "List").Place(0, 0, 1, 1, pad, U(StripHeight), listRight, U(ListTop));
            MenuKit.Hairline(list, "Top", 0);
            _viewport = MenuKit.Box(list, "Viewport").Place(0, 0, 1, 1, 0, U(8), 0, U(10));
            _viewport.gameObject.AddComponent<RectMask2D>();
            _content = MenuKit.Box(_viewport, "Rows");
            _content.anchorMin = new Vector2(0, 1);
            _content.anchorMax = new Vector2(1, 1);
            _content.pivot = new Vector2(0.5f, 1);
            _content.offsetMin = _content.offsetMax = Vector2.zero;
            _empty = MenuKit.Text(list, "Empty", "", U(16), false, MenuKit.Muted);
            _empty.rectTransform.Strip(U(24), U(26), U(16), 0);

            var details = MenuKit.Box(Root, "Details").Place(1, 0, 1, 1, -(pad + U(DetailsWidth)), U(StripHeight), pad, U(ListTop));
            MenuKit.Hairline(details, "Top", 0);
            _name = MenuKit.Text(details, "Name", "", U(28), true, MenuKit.Ink);
            _name.enableWordWrapping = true;
            _name.rectTransform.Strip(U(22), U(76), 0, 0);
            _name.alignment = TextAlignmentOptions.TopLeft;
            _about = MenuKit.Text(details, "About", "", U(17), false, MenuKit.Soft, TextAlignmentOptions.TopLeft);
            _about.enableWordWrapping = true;
            _about.lineSpacing = 6;
            _about.rectTransform.Strip(U(104), U(220), 0, 0);
            _default = MenuKit.Text(details, "Default", "", U(15), false, MenuKit.Muted);
            _default.rectTransform.Place(0, 0, 1, 0, 0, U(12), 0, -U(36));

            BuildRows();
            _built = true;
        }

        private void BuildTabs(float pad)
        {
            var bar = MenuKit.Box(Root, "Tabs").Strip(U(126), U(44), pad, pad);
            _tabLeft = new KeyTag(bar, "Previous", U(TagHeight));
            foreach (var id in OptionsCatalog.TabIds)
            {
                var view = new TabView { Id = id, Rect = MenuKit.Box(bar, id) };
                view.Under = MenuKit.Box(view.Rect, "Under").Place(0, 0, 1, 0, U(12), U(4), U(12), -U(7));
                view.Label = MenuKit.Text(view.Rect, "Label", "", U(16), true, MenuKit.Muted, TextAlignmentOptions.Center);
                view.Label.characterSpacing = 14;
                view.Label.rectTransform.Place(0, 0, 1, 1);
                view.Flash = new Glitch(view.Label);
                var index = _tabs.Count;
                _tabs.Add(view);
                Add(view.Rect, () => SetTab(index), focused => PaintTab(view, focused)).Flash = view.Flash;
            }
            _tabRight = new KeyTag(bar, "Next", U(TagHeight));
            _tabMark = new Glide(bar, true);
        }

        // One row per setting, under its section's heading; the list scrolls inside its viewport.
        private void BuildRows()
        {
            Remove(item => _rows.Any(row => ReferenceEquals(row.Focus, item)));
            _rows.Clear();
            for (var i = _content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            _rowMark = new Glide(_content, false);
            var tab = OptionsCatalog.Tab(OptionsCatalog.TabIds[_tab]);
            var culture = Languages.CultureOf(Languages.Current);
            var y = 0f;
            foreach (var section in tab.Sections)
            {
                if (section.Rows.Count == 0) continue;
                var heading = MenuKit.Text(_content, "Section " + section.Id, Text["options.section." + section.Id].ToUpper(culture), U(13), true, MenuKit.Muted);
                heading.characterSpacing = 28;
                heading.rectTransform.Strip(y + U(16), U(20), U(16), 0);
                var headingWidth = heading.GetPreferredValues(heading.text).x;
                MenuKit.Hairline(_content, "Rule " + section.Id, y + U(26), U(16) + headingWidth + U(16), 0);
                y += SectionHeightFor(y);
                foreach (var row in section.Rows)
                {
                    _rows.Add(BuildRow(row, y));
                    y += U(RowHeight);
                }
            }
            _contentHeight = y + U(12);
            _content.sizeDelta = new Vector2(0, _contentHeight);
            _empty.text = _rows.Count == 0 ? Text["options.none"] : "";
            _scroll.Set(0, true);
            ApplyScroll();
            PaintRows();
        }

        private float SectionHeightFor(float y) => y == 0 ? U(SectionHeight) - U(6) : U(SectionHeight);

        private RowView BuildRow(OptionRow row, float y)
        {
            var view = new RowView { Row = row, Rect = MenuKit.Box(_content, "Row " + row.Id).Strip(y, U(RowHeight), 0, 0) };
            view.Changed = MenuKit.Fill(MenuKit.Box(view.Rect, "Changed").Place(0, 0.5f, 0, 0.5f, U(6), -U(3), -U(12), -U(3)), null, MenuKit.Muted);
            view.Label = MenuKit.Text(view.Rect, "Label", Text["options.item." + row.Id + ".name"], U(18), false, MenuKit.Soft, TextAlignmentOptions.MidlineLeft, true);
            view.Label.rectTransform.Place(0, 0, 1, 1, U(20), 0, U(ControlWidth + 28), 0);
            view.Control = MenuKit.Box(view.Rect, "Control").Place(1, 0, 1, 1, -U(ControlWidth + 12), 0, U(12), 0);
            if (row.Fraction.HasValue) BuildSlider(view);
            else if (row.Open != null) BuildOpen(view);
            else BuildChoice(view);
            view.Focus = Add(view.Rect, () => Activate(view), focused => PaintRow(view, focused), direction => Adjust(view, direction));
            view.Focus.Mark = _rowMark;
            view.Focus.Flash = new Glitch(view.Label);
            view.Focus.Clip = _viewport;
            return view;
        }

        // < value > with a mark for each choice beneath, the chosen one lit.
        private void BuildChoice(RowView view)
        {
            view.Left = Arrow(view.Control, "Left", false);
            view.Right = Arrow(view.Control, "Right", true);
            view.Value = MenuKit.Text(view.Control, "Value", "", U(17), true, MenuKit.Ink, TextAlignmentOptions.Center, true);
            view.Value.rectTransform.Place(0, 0, 1, 1, U(34), U(5), U(34), 0);
            var count = view.Row.Count;
            if (count < 2 || count > 12) return;
            view.Pips = MenuKit.Box(view.Control, "Pips");
            view.Pips.anchorMin = view.Pips.anchorMax = new Vector2(0.5f, 0);
            view.Pips.pivot = new Vector2(0.5f, 0);
            var width = U(12);
            var gap = U(4);
            view.Pips.sizeDelta = new Vector2(count * width + (count - 1) * gap, Mathf.Max(2f, U(2)));
            view.Pips.anchoredPosition = new Vector2(0, U(5));
            view.PipMarks = new Image[count];
            for (var i = 0; i < count; i++)
                view.PipMarks[i] = MenuKit.Fill(MenuKit.Box(view.Pips, "Pip " + i).At(i * (width + gap), 0, width, view.Pips.sizeDelta.y), null, MenuKit.BarOff);
        }

        // A thin track with its fill and a thumb, and the value to its right.
        private void BuildSlider(RowView view)
        {
            view.Value = MenuKit.Text(view.Control, "Value", "", U(17), true, MenuKit.Ink, TextAlignmentOptions.MidlineRight);
            view.Value.rectTransform.Place(1, 0, 1, 1, -U(70), 0, 0, 0);
            view.Track = MenuKit.Box(view.Control, "Track").Place(0, 0.5f, 1, 0.5f, U(8), -U(2), U(90), -U(2));
            MenuKit.Fill(view.Track, null, MenuKit.BarOff);
            view.Fill = MenuKit.Box(view.Track, "Fill").Place(0, 0, 0, 1);
            MenuKit.Fill(view.Fill, null, MenuKit.Soft);
            view.Thumb = MenuKit.Box(view.Track, "Thumb");
            view.Thumb.anchorMin = view.Thumb.anchorMax = new Vector2(0, 0.5f);
            view.Thumb.pivot = new Vector2(0.5f, 0.5f);
            view.Thumb.sizeDelta = new Vector2(Mathf.Max(2f, U(3)), U(18));
            MenuKit.Fill(view.Thumb, null, MenuKit.Ink);
            view.Slide.Set(view.Row.Fraction ?? 0, true);
        }

        private void BuildOpen(RowView view)
        {
            view.Value = MenuKit.Text(view.Control, "Value", "", U(16), true, MenuKit.Soft, TextAlignmentOptions.MidlineRight);
            view.Value.characterSpacing = 12;
            view.Value.rectTransform.Place(0, 0, 1, 1, 0, 0, U(30), 0);
            view.Right = Arrow(view.Control, "Right", true);
        }

        // Drawn from two strokes rather than a font character, so it looks the same in every language.
        private Image[] Arrow(RectTransform control, string name, bool right)
        {
            var arrow = MenuKit.Chevron(control, name, U(12), MenuKit.Muted);
            arrow.anchorMin = arrow.anchorMax = new Vector2(right ? 1 : 0, 0.5f);
            arrow.pivot = new Vector2(0.5f, 0.5f);
            arrow.anchoredPosition = new Vector2(right ? -U(14) : U(14), U(2));
            arrow.localEulerAngles = new Vector3(0, 0, right ? -90f : 90f);
            return arrow.GetComponentsInChildren<Image>(true).ToArray();
        }

        private static void Tint(Image[] strokes, Color color)
        {
            if (strokes == null) return;
            foreach (var stroke in strokes) stroke.color = color;
        }

        internal override void Retext()
        {
            var culture = Languages.CultureOf(Languages.Current);
            _section.text = Text["options.title"].ToUpper(culture);
            _title.text = Text["options.tab." + OptionsCatalog.TabIds[_tab]].ToUpper(culture);
            foreach (var view in _tabs)
            {
                view.Label.text = Text["options.tab." + view.Id].ToUpper(culture);
                view.Width = view.Label.GetPreferredValues(view.Label.text).x + U(28);
            }
            foreach (var view in _rows) view.Label.text = Text["options.item." + view.Row.Id + ".name"];
            LayoutTabs();
            PaintRows();
            PaintDetails();
        }

        internal override void DeviceChanged() => LayoutTabs();

        internal override void Opened()
        {
            var index = Mathf.Clamp(_focusIndex, 0, Math.Max(0, _rows.Count - 1));
            Focus(_rows.Count > 0 ? _rows[index].Focus : First());
            Reveal(true);
        }

        internal override void Closed() => SaveNow();

        internal override IReadOnlyList<PromptSpec> Prompts => new[]
        {
            new PromptSpec(Text["common.back"], "B", "ESC", () => FrontEnd.Pop()),
            new PromptSpec(Text["options.reset"], "X", "DEL", ResetFocused)
        };

        internal override void OnAlt1() => ResetFocused();

        internal override void OnTab(int step) => SetTab((_tab + step + _tabs.Count) % _tabs.Count);

        internal override void Tick()
        {
            var now = Time.unscaledTime;
            if (_saveAt > 0 && now >= _saveAt) SaveNow();
            if (now >= _nextRefresh)
            {
                // The game's handlers can change other settings in turn, as a quality preset does.
                _nextRefresh = now + RefreshSeconds;
                PaintRows();
            }
            if (FrontInput.Scroll != 0 && RectTransformUtility.RectangleContainsScreenPoint(_viewport, FrontInput.Pointer, null))
            {
                _scroll.Set(Mathf.Clamp(_scroll.Goal - Math.Sign(FrontInput.Scroll) * U(RowHeight) * 3, 0, MaxScroll));
            }
            var dt = Time.unscaledDeltaTime;
            if (_scroll.Step(dt)) ApplyScroll();
            foreach (var view in _rows)
                if (view.Track != null && view.Slide.Step(dt)) PlaceThumb(view);
        }

        protected override void FocusChanged(Focusable previous, Focusable current)
        {
            var index = _rows.FindIndex(row => ReferenceEquals(row.Focus, current));
            if (index >= 0)
            {
                _focusIndex = index;
                Reveal(false);
            }
            PaintDetails();
        }

        // Dragging a slider follows the mouse while the button is held.
        protected override void Pointer(Focusable over)
        {
            if (_drag == null) return;
            if (!FrontInput.Held)
            {
                _drag = null;
                return;
            }
            SetFromPointer(_drag);
        }

        private void SetTab(int index)
        {
            if (index == _tab || index < 0 || index >= _tabs.Count) return;
            MenuSound.Play(MenuCue.Forward);
            _tab = _lastTab = index;
            _focusIndex = 0;
            _drag = null;
            BuildRows();
            var culture = Languages.CultureOf(Languages.Current);
            _title.text = Text["options.tab." + OptionsCatalog.TabIds[_tab]].ToUpper(culture);
            _titleFlash.Pulse();
            LayoutTabs();
            Focus(_rows.Count > 0 ? _rows[0].Focus : null);
            PaintDetails();
        }

        private void Activate(RowView view)
        {
            var row = view.Row;
            if (row.Open != null)
            {
                SaveNow();
                MenuSound.Play(MenuCue.Forward);
                row.Open();
                return;
            }
            if (view.Track != null)
            {
                if (!FromPointer || !RectTransformUtility.RectangleContainsScreenPoint(view.Control, FrontInput.Pointer, null)) return;
                _drag = view;
                SetFromPointer(view);
                return;
            }
            // A click on the left half steps back and on the right half forward; Enter steps forward and wraps.
            var direction = 1;
            if (FromPointer && RectTransformUtility.ScreenPointToLocalPointInRectangle(view.Control, FrontInput.Pointer, null, out var local))
                direction = local.x < view.Control.rect.center.x ? -1 : 1;
            var wrap = !FromPointer;
            Change(view, () => row.Step(direction, wrap));
        }

        private void Adjust(RowView view, int direction)
        {
            if (view.Row.Open != null) return;
            Change(view, () => view.Row.Step(direction, false));
        }

        // A change of resolution or window mode can leave the screen unreadable; it is kept only when confirmed.
        private static readonly HashSet<string> DisplayRows = new() { "resolution", "windowMode" };

        private void Change(RowView view, Func<bool> change)
        {
            var row = view.Row;
            var before = DisplayRows.Contains(row.Id) ? row.Snapshot() : null;
            var changed = change();
            Changed(view, changed);
            if (!changed || before == null) return;
            FrontEnd.Push(new ConfirmScreen("confirm.keepDisplayTitle", "confirm.keepDisplayBody", "confirm.keep", "confirm.revert",
                SaveNow, () =>
                {
                    row.Restore(before);
                    SaveNow();
                    PaintRows();
                }, seconds: 15f));
        }

        private void SetFromPointer(RowView view)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(view.Track, FrontInput.Pointer, null, out var local)) return;
            var rect = view.Track.rect;
            Changed(view, view.Row.SetFraction(Mathf.InverseLerp(rect.xMin, rect.xMax, local.x)));
        }

        private void ResetFocused()
        {
            var view = _rows.FirstOrDefault(row => ReferenceEquals(row.Focus, Focused));
            if (view != null) Change(view, view.Row.Reset);
        }

        private void Changed(RowView view, bool changed)
        {
            if (!changed) return;
            _saveAt = Time.unscaledTime + SaveDelay;
            // Something switched on or off sounds like a switch; a step along a list or slider ticks.
            var row = view.Row;
            MenuSound.Play(row.Count == 2 ? (row.Index == 1 ? MenuCue.On : MenuCue.Off) : MenuCue.Move);
            view.Focus.Flash?.Pulse();
            // Other rows may follow this one, as the graphics options follow the preset.
            PaintRows();
            PaintDetails();
        }

        private void SaveNow()
        {
            _saveAt = 0;
            try
            {
                GameSettingsManager.Save();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game's settings could not be saved: " + error.Message);
            }
        }

        // Keeps the focused row inside the visible part of the list, with a row of context where there is one.
        private void Reveal(bool snap)
        {
            if (_focusIndex < 0 || _focusIndex >= _rows.Count) return;
            var rect = _rows[_focusIndex].Rect;
            var top = -rect.offsetMax.y;
            var bottom = top + U(RowHeight);
            var height = _viewport.rect.height;
            var goal = _scroll.Goal;
            if (top - U(RowHeight) < goal) goal = top - U(_focusIndex == 0 ? SectionHeight : RowHeight);
            else if (bottom + U(RowHeight) > goal + height) goal = bottom + U(RowHeight) - height;
            _scroll.Set(Mathf.Clamp(goal, 0, MaxScroll), snap);
            ApplyScroll();
        }

        private float MaxScroll => Mathf.Max(0, _contentHeight - _viewport.rect.height);

        private void ApplyScroll()
        {
            _content.anchoredPosition = new Vector2(0, _scroll.Value);
            _rowMark?.Follow();
        }

        private void LayoutTabs()
        {
            if (_tabs.Count == 0) return;
            var pad = FrontEnd.PadActive;
            _tabLeft.Set(pad ? "LB" : "Q", pad);
            _tabRight.Set(pad ? "RB" : "E", pad);
            var x = 0f;
            var height = U(44);
            var items = new List<(RectTransform Rect, float Width, float Height)> { (_tabLeft.Rect, _tabLeft.Size.x, _tabLeft.Size.y) };
            items.AddRange(_tabs.Select(tab => (tab.Rect, tab.Width, height)));
            items.Add((_tabRight.Rect, _tabRight.Size.x, _tabRight.Size.y));
            foreach (var (rect, width, itemHeight) in items)
            {
                rect.At(x, (height - itemHeight) / 2, width, itemHeight);
                x += width + U(itemHeight < height ? 10 : 4);
            }
            for (var i = 0; i < _tabs.Count; i++) PaintTab(_tabs[i], ReferenceEquals(Focused?.Rect, _tabs[i].Rect));
            _tabMark.Target(_tabs[_tab].Under, false);
            _tabMark.Follow();
        }

        private void PaintTab(TabView view, bool focused)
        {
            var open = ReferenceEquals(view, _tabs.ElementAtOrDefault(_tab));
            view.Label.color = open || focused ? MenuKit.Ink : MenuKit.Muted;
        }

        private void PaintRows()
        {
            if (!_built && _rows.Count == 0) return;
            foreach (var view in _rows) PaintRow(view, ReferenceEquals(view.Focus, Focused));
        }

        private void PaintRow(RowView view, bool focused)
        {
            if (view.Label == null) return;
            var row = view.Row;
            string value;
            try
            {
                value = row.Value;
            }
            catch (Exception)
            {
                value = "—";
            }
            view.Label.color = focused ? MenuKit.Ink : MenuKit.Soft;
            view.Value.text = row.Open != null ? value.ToUpper(Languages.CultureOf(Languages.Current)) : value;
            view.Value.color = focused ? MenuKit.Ink : row.Open != null ? MenuKit.Soft : MenuKit.Ink;
            // A dot beside a setting that is not at its default.
            view.Changed.enabled = row.Open == null && !SafeDefault(row);
            if (view.Track != null)
            {
                view.Fill.GetComponent<Image>().color = focused ? MenuKit.Ink : MenuKit.Soft;
                view.Slide.Set(row.Fraction ?? 0);
                PlaceThumb(view);
                return;
            }
            var index = row.Index;
            if (view.Left != null)
            {
                // An end of the list greys out its arrow, as there is nothing further that way.
                Tint(view.Left, !focused ? MenuKit.Faint : index <= 0 ? MenuKit.Disabled : MenuKit.Ink);
                Tint(view.Right, !focused ? MenuKit.Faint : index >= row.Count - 1 && row.Count > 0 ? MenuKit.Disabled : MenuKit.Ink);
            }
            else Tint(view.Right, focused ? MenuKit.Ink : MenuKit.Faint);
            if (view.PipMarks == null) return;
            for (var i = 0; i < view.PipMarks.Length; i++)
                view.PipMarks[i].color = i == index ? (focused ? MenuKit.Ink : MenuKit.Soft) : MenuKit.BarOff;
        }

        private static bool SafeDefault(OptionRow row)
        {
            try { return row.IsDefault; }
            catch (Exception) { return true; }
        }

        private static void PlaceThumb(RowView view)
        {
            var fraction = Mathf.Clamp01(view.Slide.Value);
            view.Fill.anchorMax = new Vector2(fraction, 1);
            view.Thumb.anchorMin = view.Thumb.anchorMax = new Vector2(fraction, 0.5f);
            view.Thumb.anchoredPosition = Vector2.zero;
        }

        private void PaintDetails()
        {
            if (!_built) return;
            var view = _rows.FirstOrDefault(row => ReferenceEquals(row.Focus, Focused));
            if (view == null)
            {
                _name.text = _about.text = _default.text = "";
                return;
            }
            _name.text = Text["options.item." + view.Row.Id + ".name"];
            _about.text = Text["options.item." + view.Row.Id + ".about"];
            string defaultValue;
            try { defaultValue = view.Row.DefaultValue; }
            catch (Exception) { defaultValue = ""; }
            _default.text = defaultValue.Length > 0 ? Text.Get("options.default", ("value", defaultValue)) : "";
        }
    }
}
