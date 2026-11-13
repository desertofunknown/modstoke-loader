// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// A screen of settings: tabs when there is more than one, a list of rows under section headings, and a
    /// column that says what the focused setting does and what it defaults to. Options, custom game rules and
    /// the host's setup are all this screen with different rows; a screen that leads somewhere has its main
    /// action at the foot of that column.
    /// </summary>
    internal abstract class SettingsScreen : FrontScreen
    {
        private const float StripHeight = 143, TabsTop = 126, RowHeight = 44, TallHeight = 62, SectionHeight = 44, ControlWidth = 380;
        private const float DetailsWidth = 400, TagHeight = 24, FactHeight = 32, RefreshSeconds = 0.5f, CaretBlink = 0.53f;

        private sealed class RowView
        {
            internal OptionRow Row;
            internal RectTransform Rect, Track, Thumb, Fill, Pips, Control, Caret;
            internal TextMeshProUGUI Label, Value, Detail, Hint;
            internal Image[] Left, Right;
            internal Image Changed, Underline, Field;
            internal RawImage Picture;
            internal Image[] PipMarks;
            internal Focusable Focus;
            internal float Height;
            internal readonly Ease Slide = new(22f);
        }

        private sealed class FactView
        {
            internal RectTransform Rect;
            internal TextMeshProUGUI Label, Value;
        }

        private sealed class TabView
        {
            internal string Id;
            internal RectTransform Rect, Under;
            internal TextMeshProUGUI Label;
            internal Focusable Focus;
            internal float Width;
        }

        private readonly List<TabView> _tabs = new();
        private readonly List<RowView> _rows = new();
        private readonly List<FactView> _facts = new();
        private readonly Ease _scroll = new(16f);
        private int _tab, _focusIndex;
        private RectTransform _viewport, _content, _details;
        private KeyTag _tabLeft, _tabRight;
        private TextMeshProUGUI _heading, _title, _name, _about, _default, _empty, _note, _primaryLabel;
        private Glitch _titleFlash;
        private Glide _rowMark, _tabMark;
        private Focusable _primary;
        private Image _primaryFill;
        private RowView _drag, _editing;
        private TMP_InputField _padField;
        private string _draft, _padText, _problem;
        private float _nextRefresh, _contentHeight, _editStart, _foot;
        private bool _built;

        protected SettingsScreen(int tab = 0) => _tab = tab;

        /// <summary>The tabs this screen has; with only one, no tab bar is drawn.</summary>
        protected abstract IReadOnlyList<string> TabIds { get; }

        protected abstract OptionTab BuildTab(string id);

        /// <summary>The small line above the title, naming where the player is.</summary>
        protected abstract string Heading { get; }

        protected virtual string Title => TabName(TabIds[_tab]);

        protected abstract string TabName(string id);

        protected abstract string SectionName(string id);

        protected abstract string ItemName(OptionRow row);

        protected abstract string ItemAbout(OptionRow row);

        /// <summary>
        /// Short facts about the focused row, as label and value, set out as a table under its name; null for none.
        /// A value may carry color tags.
        /// </summary>
        protected virtual IReadOnlyList<(string Label, string Value)> ItemFacts(OptionRow row) => null;

        /// <summary>The main action at the foot of the details column, or null for a screen without one.</summary>
        protected virtual string PrimaryLabel => null;

        protected virtual void Primary() { }

        /// <summary>A line over the main action, such as what is about to start.</summary>
        protected virtual string Summary => null;

        /// <summary>Stores what changed; runs shortly after a change and when the screen closes.</summary>
        protected virtual void Save() { }

        /// <summary>A setting just changed, for rules that follow one another.</summary>
        protected virtual void RowChanged(OptionRow row) { }

        /// <summary>Whether a change to this row has to be confirmed before it is kept, as a new resolution does.</summary>
        protected virtual bool NeedsConfirm(OptionRow row) => false;

        /// <summary>Asks to keep a change; <paramref name="before"/> is the value to put back.</summary>
        protected virtual void Confirm(OptionRow row, string before) { }

        protected virtual void TabChanged(int index) { }

        /// <summary>Whether rows go back to a default, with the Default prompt; not for a list of things rather than settings.</summary>
        protected virtual bool CanReset => true;

        /// <summary>What an empty tab says.</summary>
        protected virtual string EmptyText => Text["options.none"];

        protected int TabIndex => _tab;

        /// <summary>Builds the open tab's rows again, as when what they list changed; focus returns to the row it was on.</summary>
        protected void Rebuild(string focusId = null)
        {
            var keep = focusId ?? _rows.ElementAtOrDefault(_focusIndex)?.Row.Id;
            EndEdit(false);
            BuildRows();
            if (keep != null) FocusRow(keep);
            if (Focused == null || !Focused.Usable) FocusRow(0, 1);
            PaintDetails();
        }

        protected override void Build()
        {
            _built = false;
            _tabs.Clear();
            _rows.Clear();
            _primary = null;
            _primaryFill = null;
            _primaryLabel = _note = null;
            _editing = _drag = null;
            var pad = U(60);
            MenuKit.Backdrop(Root, U(StripHeight - 24), U(110));
            _heading = MenuKit.Text(Root, "Heading", "", U(14), true, MenuKit.Muted);
            _heading.characterSpacing = 32;
            _heading.rectTransform.At(pad, U(30), U(900), U(18));
            _title = MenuKit.Text(Root, "Title", "", U(50), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _title.characterSpacing = 5;
            _title.rectTransform.At(pad, U(39), U(1100), U(80));
            _titleFlash = new Glitch(_title);

            var tabbed = TabIds.Count > 1;
            if (tabbed) BuildTabs(pad);
            var listTop = tabbed ? TabsTop + 58 : TabsTop + 10;

            var listRight = pad + U(DetailsWidth + 56);
            var list = MenuKit.Box(Root, "List").Place(0, 0, 1, 1, pad, U(StripHeight), listRight, U(listTop));
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

            BuildDetails(pad, listTop);
            BuildRows();
            _built = true;
        }

        private void BuildDetails(float pad, float top)
        {
            var details = _details = MenuKit.Box(Root, "Details").Place(1, 0, 1, 1, -(pad + U(DetailsWidth)), U(StripHeight), pad, U(top));
            MenuKit.Hairline(details, "Top", 0);
            _facts.Clear();
            _name = MenuKit.Text(details, "Name", "", U(28), true, MenuKit.Ink, TextAlignmentOptions.TopLeft);
            _name.enableWordWrapping = true;
            _name.rectTransform.Strip(U(22), U(76), 0, 0);
            _about = MenuKit.Text(details, "About", "", U(17), false, MenuKit.Soft, TextAlignmentOptions.TopLeft);
            _about.enableWordWrapping = true;
            _about.lineSpacing = 6;
            _about.rectTransform.Strip(U(104), U(260), 0, 0);

            // From the foot up: the main action, a line about it, then the focused setting's default.
            var foot = 12f;
            if (PrimaryLabel != null)
            {
                var go = MenuKit.Box(details, "Primary").Place(0, 0, 1, 0, 0, U(20), 0, -U(78));
                _primaryFill = MenuKit.Fill(go, MenuKit.Brush, MenuKit.Soft);
                _primaryLabel = MenuKit.Text(go, "Label", "", U(21), true, MenuKit.Dark, TextAlignmentOptions.Center);
                _primaryLabel.characterSpacing = 16;
                _primaryLabel.rectTransform.Place(0, 0, 1, 1);
                _primary = Add(go, RunPrimary, focused => _primaryFill.color = focused ? MenuKit.Ink : MenuKit.Soft);
                _primary.Flash = Flashing(_primaryLabel);
                _note = MenuKit.Text(details, "Note", "", U(15), false, MenuKit.Muted, TextAlignmentOptions.BottomLeft);
                _note.enableWordWrapping = true;
                _note.rectTransform.Place(0, 0, 1, 0, 0, U(88), 0, -U(136));
                foot = 146;
            }
            _default = MenuKit.Text(details, "Default", "", U(15), false, MenuKit.Muted);
            _foot = U(foot + 24);
        }

        private void BuildTabs(float pad)
        {
            var bar = MenuKit.Box(Root, "Tabs").Strip(U(TabsTop), U(44), pad, pad);
            _tabLeft = new KeyTag(bar, "Previous", U(TagHeight));
            foreach (var id in TabIds)
            {
                var view = new TabView { Id = id, Rect = MenuKit.Box(bar, id) };
                view.Under = MenuKit.Box(view.Rect, "Under").Place(0, 0, 1, 0, U(12), U(4), U(12), -U(7));
                view.Label = MenuKit.Text(view.Rect, "Label", "", U(16), true, MenuKit.Muted, TextAlignmentOptions.Center);
                view.Label.characterSpacing = 14;
                view.Label.rectTransform.Place(0, 0, 1, 1);
                var index = _tabs.Count;
                _tabs.Add(view);
                view.Focus = Add(view.Rect, () => SetTab(index), focused => PaintTab(view, focused));
                view.Focus.Flash = Flashing(view.Label);
            }
            _tabRight = new KeyTag(bar, "Next", U(TagHeight));
            _tabMark = new Glide(bar, true);
        }

        // One row per setting, under its section's heading; the list scrolls inside its viewport.
        private void BuildRows()
        {
            Remove(item => _rows.Any(row => ReferenceEquals(row.Focus, item)));
            _rows.Clear();
            _editing = null;
            for (var i = _content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            _rowMark = new Glide(_content, false);
            var tab = BuildTab(TabIds[_tab]);
            var culture = Languages.CultureOf(Languages.Current);
            var y = 0f;
            var arriving = new List<RectTransform>();
            foreach (var section in tab.Sections)
            {
                if (section.Rows.Count == 0) continue;
                var heading = MenuKit.Text(_content, "Section " + section.Id, SectionName(section.Id).ToUpper(culture), U(13), true, MenuKit.Muted);
                heading.characterSpacing = 28;
                heading.rectTransform.Strip(y + U(16), U(20), U(16), 0);
                var headingWidth = heading.GetPreferredValues(heading.text).x;
                var rule = MenuKit.Hairline(_content, "Rule " + section.Id, y + U(26), U(16) + headingWidth + U(16), 0);
                arriving.Add(heading.rectTransform);
                arriving.Add(rule.rectTransform);
                y += y == 0 ? U(SectionHeight) - U(6) : U(SectionHeight);
                foreach (var row in section.Rows)
                {
                    var view = BuildRow(row, y, U(row.Detail != null ? TallHeight : RowHeight));
                    _rows.Add(view);
                    arriving.Add(view.Rect);
                    y += view.Height;
                }
            }
            // The list reads in from the top, as it does each time a tab opens.
            new Cascade(arriving);
            _contentHeight = y + U(12);
            _content.sizeDelta = new Vector2(0, _contentHeight);
            _empty.text = _rows.Count == 0 ? EmptyText : "";
            _scroll.Set(0, true);
            ApplyScroll();
            PaintRows();
        }

        private RowView BuildRow(OptionRow row, float y, float height)
        {
            var view = new RowView { Row = row, Height = height, Rect = MenuKit.Box(_content, "Row " + row.Id).Strip(y, height, 0, 0) };
            view.Changed = MenuKit.Fill(MenuKit.Box(view.Rect, "Changed").Place(0, 0.5f, 0, 0.5f, U(6), -U(3), -U(12), -U(3)), null, MenuKit.Muted);
            var wide = row is TextRow { Placeholder: not null };
            var labelLeft = U(20);
            // A picture or a lettered tile leads the row, a little larger on a row with a second line.
            var tile = U(row.Detail != null ? 40 : 32);
            if (row.HasPicture || row.Monogram != null)
            {
                var frame = MenuKit.Box(view.Rect, "Picture").At(labelLeft, (height - tile) / 2, tile, tile);
                MenuKit.Fill(frame, null, row.MonogramTint ?? MenuKit.Cell);
                if (row.HasPicture)
                {
                    view.Picture = MenuKit.Box(frame, "Image").Place(0, 0, 1, 1).gameObject.AddComponent<RawImage>();
                    view.Picture.raycastTarget = false;
                    view.Picture.uvRect = new Rect(0, 1, 1, -1);
                }
                else
                {
                    var letters = MenuKit.Text(frame, "Letters", row.Monogram, tile * 0.42f, true, MenuKit.Ink, TextAlignmentOptions.Center);
                    letters.characterSpacing = 4;
                    letters.rectTransform.Place(0, 0, 1, 1);
                }
                labelLeft += tile + U(14);
            }
            view.Label = MenuKit.Text(view.Rect, "Label", ItemName(row), U(18), false, MenuKit.Soft, TextAlignmentOptions.MidlineLeft, true);
            var labelRight = U(ControlWidth + 28);
            if (row.Detail != null)
            {
                // The name over its second line, the pair centered in the row.
                view.Label.rectTransform.Place(0, 0.5f, 1, 1, labelLeft, -U(1), labelRight, U(7));
                view.Detail = MenuKit.Text(view.Rect, "Detail", row.Detail, U(14), false, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
                view.Detail.rectTransform.Place(0, 0, 1, 0.5f, labelLeft, U(8), labelRight, U(1));
            }
            else view.Label.rectTransform.Place(0, 0, 1, 1, labelLeft, 0, labelRight, 0);
            view.Control = wide
                ? MenuKit.Box(view.Rect, "Control").Place(0, 0, 1, 1, U(12), 0, U(12), 0)
                : MenuKit.Box(view.Rect, "Control").Place(1, 0, 1, 1, -U(ControlWidth + 12), 0, U(12), 0);
            if (row is TextRow) BuildField(view, wide);
            else if (row.Fraction.HasValue) BuildSlider(view);
            else if (row.Open != null) BuildOpen(view);
            else if (row.ReadOnly) BuildInfo(view);
            else BuildChoice(view);
            // A search box's field is the whole row; its name lives in the details column only.
            if (wide) view.Label.gameObject.SetActive(false);
            view.Focus = Add(view.Rect, () => Activate(view), focused => PaintRow(view, focused), direction => Adjust(view, direction),
                () => view.Row.Available);
            view.Focus.Mark = _rowMark;
            view.Focus.Flash = new Glitch(wide ? view.Value : view.Label);
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

        // A value to read, with nothing to step through.
        private void BuildInfo(RowView view)
        {
            view.Value = MenuKit.Text(view.Control, "Value", "", U(16), false, MenuKit.Ink, TextAlignmentOptions.MidlineRight, true);
            view.Value.rectTransform.Place(0, 0, 1, 1, 0, 0, U(14), 0);
        }

        private void BuildOpen(RowView view)
        {
            view.Value = MenuKit.Text(view.Control, "Value", "", U(14), true, MenuKit.Soft, TextAlignmentOptions.MidlineRight);
            view.Value.characterSpacing = 18;
            view.Value.rectTransform.Place(0, 0, 1, 1, 0, 0, U(30), 0);
            view.Right = Arrow(view.Control, "Right", true);
        }

        // Typed text on a rule, with a caret while it is being edited. A search box sits in a faint field of its own
        // across the row, with its hint in it while it is empty.
        private void BuildField(RowView view, bool wide)
        {
            if (wide)
            {
                view.Field = MenuKit.Fill(MenuKit.Box(view.Control, "Field").Place(0, 0, 1, 1, 0, U(6), 0, U(6)), null, MenuKit.Field);
                view.Hint = MenuKit.Text(view.Control, "Hint", ((TextRow)view.Row).Placeholder, U(17), false, MenuKit.Faint,
                    TextAlignmentOptions.MidlineLeft, true);
                view.Hint.rectTransform.Place(0, 0, 1, 1, U(18), U(4), U(18), 0);
            }
            var inset = U(wide ? 18 : 10);
            view.Value = MenuKit.Text(view.Control, "Value", "", U(17), !wide, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            view.Value.rectTransform.Place(0, 0, 1, 1, inset, U(4), inset, 0);
            view.Underline = MenuKit.Fill(MenuKit.Box(view.Control, "Rule").Place(0, 0, 1, 0, U(wide ? 0 : 10), U(wide ? 6 : 7), U(wide ? 0 : 10),
                -(U(wide ? 6 : 7) + Mathf.Max(1f, U(1.5f)))), null, MenuKit.Rule);
            view.Caret = MenuKit.Box(view.Value.rectTransform, "Caret");
            view.Caret.anchorMin = view.Caret.anchorMax = new Vector2(0, 0.5f);
            view.Caret.pivot = new Vector2(0, 0.5f);
            view.Caret.sizeDelta = new Vector2(Mathf.Max(2f, U(2)), U(20));
            MenuKit.Fill(view.Caret, null, MenuKit.Ink);
            view.Caret.gameObject.SetActive(false);
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
            _heading.text = Heading.ToUpper(culture);
            _title.text = Title.ToUpper(culture);
            foreach (var view in _tabs)
            {
                view.Label.text = TabName(view.Id).ToUpper(culture);
                view.Width = view.Label.GetPreferredValues(view.Label.text).x + U(28);
            }
            foreach (var view in _rows) view.Label.text = ItemName(view.Row);
            if (_primaryLabel != null) _primaryLabel.text = PrimaryLabel.ToUpper(culture);
            LayoutTabs();
            PaintRows();
            PaintDetails();
        }

        internal override void DeviceChanged() => LayoutTabs();

        internal override void Opened()
        {
            FocusRow(Mathf.Clamp(_focusIndex, 0, Math.Max(0, _rows.Count - 1)), 1);
            if (Focused == null) Focus(First());
            Reveal(true);
        }

        internal override void Closed()
        {
            EndEdit(true);
            Save();
        }

        internal override IReadOnlyList<PromptSpec> Prompts
        {
            get
            {
                var prompts = new List<PromptSpec> { new(Text["common.back"], "B", "ESC", () => FrontEnd.Pop()) };
                if (CanReset) prompts.Add(new PromptSpec(Text["options.reset"], "X", "DEL", ResetFocused));
                if (PrimaryLabel != null) prompts.Add(new PromptSpec(PrimaryLabel, "Y", "F2", RunPrimary));
                return prompts;
            }
        }

        internal override void OnAlt1()
        {
            if (CanReset) ResetFocused();
        }

        internal override void OnAlt2()
        {
            if (PrimaryLabel != null) Sounding(MenuCue.Forward, RunPrimary);
        }

        // Text still being typed is kept first, so the action sees it.
        private void RunPrimary()
        {
            EndEdit(true);
            Primary();
        }

        internal override void OnTab(int step)
        {
            if (_tabs.Count > 1) SetTab((_tab + step + _tabs.Count) % _tabs.Count);
        }

        internal override void Tick()
        {
            var now = Time.unscaledTime;
            if (now >= _nextRefresh)
            {
                // The game's handlers can change other settings in turn, as a quality preset does.
                _nextRefresh = now + RefreshSeconds;
                PaintRows();
            }
            if (FrontInput.Scroll != 0 && RectTransformUtility.RectangleContainsScreenPoint(_viewport, FrontInput.Pointer, null))
                _scroll.Set(Mathf.Clamp(_scroll.Goal - Math.Sign(FrontInput.Scroll) * U(RowHeight) * 3, 0, MaxScroll));
            var dt = Time.unscaledDeltaTime;
            if (_scroll.Step(dt)) ApplyScroll();
            foreach (var view in _rows)
                if (view.Track != null && view.Slide.Step(dt)) PlaceThumb(view);
            if (_editing != null) PaintCaret(_editing);
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

        // Up and down follow the list in order, skipping settings that are switched off, and run on to the main
        // action below the last one and the tabs above the first.
        protected override void Navigate(int dx, int dy)
        {
            var index = _rows.FindIndex(row => ReferenceEquals(row.Focus, Focused));
            if (dy != 0 && index >= 0)
            {
                var next = index + dy;
                while (next >= 0 && next < _rows.Count && !_rows[next].Focus.Usable) next += dy;
                if (next >= 0 && next < _rows.Count) Focus(_rows[next].Focus);
                else if (dy > 0 && _primary != null) Focus(_primary);
                else if (dy < 0 && _tabs.Count > 0) Focus(_tabs[_tab].Focus);
                return;
            }
            if (dy != 0 && ReferenceEquals(Focused, _primary))
            {
                if (dy < 0) FocusRow(_focusIndex, -1);
                return;
            }
            if (dy > 0 && _tabs.Any(tab => ReferenceEquals(tab.Focus, Focused)))
            {
                FocusRow(0, 1);
                return;
            }
            base.Navigate(dx, dy);
        }

        // The usable row nearest <paramref name="from"/>, looking in <paramref name="direction"/> first.
        private void FocusRow(int from, int direction)
        {
            for (var step = 0; step < _rows.Count * 2; step++)
            {
                var index = from + (step % 2 == 0 ? step / 2 * direction : -(step / 2 + 1) * direction);
                if (index < 0 || index >= _rows.Count || !_rows[index].Focus.Usable) continue;
                Focus(_rows[index].Focus);
                return;
            }
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

        protected override bool CaptureInput() => _editing != null && Edit();

        private void SetTab(int index)
        {
            if (index == _tab || index < 0 || index >= _tabs.Count) return;
            EndEdit(true);
            MenuSound.Play(MenuCue.Forward);
            _tab = index;
            TabChanged(index);
            _focusIndex = 0;
            _drag = null;
            BuildRows();
            _title.text = Title.ToUpper(Languages.CultureOf(Languages.Current));
            _titleFlash.Pulse(false);
            LayoutTabs();
            FocusRow(0, 1);
            PaintDetails();
        }

        private void Activate(RowView view)
        {
            var row = view.Row;
            if (row is TextRow text)
            {
                BeginEdit(view, text);
                return;
            }
            if (row.Open != null)
            {
                Save();
                MenuSound.Play(MenuCue.Forward);
                try
                {
                    row.Open();
                }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("\"" + row.Id + "\" could not open: " + error.Message);
                }
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
            if (view.Row.Open != null || view.Row is TextRow) return;
            Change(view, () => view.Row.Step(direction, false));
        }

        private void Change(RowView view, Func<bool> change)
        {
            var row = view.Row;
            var before = NeedsConfirm(row) ? row.Snapshot() : null;
            var changed = change();
            Changed(view, changed);
            if (changed && before != null) Confirm(row, before);
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
            if (view != null && view.Row.Available) Change(view, view.Row.Reset);
        }

        private void Changed(RowView view, bool changed)
        {
            if (!changed) return;
            // Something switched on or off sounds like a switch; a step along a list or slider ticks.
            var row = view.Row;
            MenuSound.Play(row.Count == 2 ? (row.Index == 1 ? MenuCue.On : MenuCue.Off) : MenuCue.Move);
            view.Focus.Flash?.Pulse();
            Problem(null);
            RowChanged(row);
            // Other rows may follow this one, as the graphics options follow the preset.
            Refresh();
        }

        /// <summary>Paints every row again from the stored values.</summary>
        protected void Refresh()
        {
            PaintRows();
            PaintDetails();
        }

        /// <summary>Shows why the main action could not go ahead, over it; null clears it.</summary>
        protected void Problem(string text)
        {
            _problem = text;
            PaintNote();
        }

        /// <summary>Moves focus to a row by its id, as to a setting that needs the player's attention.</summary>
        protected void FocusRow(string id)
        {
            var view = _rows.FirstOrDefault(row => row.Row.Id == id);
            if (view != null) Focus(view.Focus);
        }

        // Editing text: the keyboard types into the row, Enter keeps it and Escape puts the old text back. On a
        // controller Steam's on-screen keyboard opens where Steam offers one.
        private void BeginEdit(RowView view, TextRow row)
        {
            if (!row.Available) return;
            _editing = view;
            _draft = row.Value;
            _editStart = Time.unscaledTime;
            _padField = null;
            if (FrontEnd.PadActive && row.PadKeyboard != null)
            {
                try
                {
                    _padField = row.PadKeyboard(_draft);
                    _padText = _padField != null ? _padField.text : null;
                }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Steam's on-screen keyboard could not open: " + error.Message);
                }
            }
            MenuSound.Play(MenuCue.Forward);
            PaintRow(view, true);
        }

        private bool Edit()
        {
            var view = _editing;
            var row = (TextRow)view.Row;
            if (FrontInput.Back)
            {
                EndEdit(false);
                MenuSound.Play(MenuCue.Back);
                return true;
            }
            // A controller's confirm keeps the text; on a keyboard Space is a character like any other.
            if (FrontInput.Confirm && !UnityEngine.Input.GetKeyDown(KeyCode.Space) && !UnityEngine.Input.GetKeyDown(KeyCode.Return)
                && !UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter) || FrontInput.Click && !view.Focus.Under(FrontInput.Pointer))
            {
                EndEdit(true);
                return true;
            }
            if (_padField != null && _padField && _padField.text != _padText)
            {
                _draft = Clip(_padField.text, row.MaxLength);
                EndEdit(true);
                return true;
            }
            var paste = (UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl))
                && UnityEngine.Input.GetKeyDown(KeyCode.V);
            if (paste) _draft = Clip(_draft + (GUIUtility.systemCopyBuffer ?? "").Replace('\n', ' ').Replace('\r', ' '), row.MaxLength);
            foreach (var c in UnityEngine.Input.inputString ?? "")
            {
                if (c == '\b')
                {
                    if (_draft.Length > 0) _draft = _draft.Substring(0, _draft.Length - 1);
                }
                else if (c is '\n' or '\r')
                {
                    EndEdit(true);
                    return true;
                }
                else if (!char.IsControl(c) && _draft.Length < row.MaxLength) _draft += c;
            }
            PaintRow(view, true);
            return true;
        }

        private static string Clip(string text, int length) => text.Length > length ? text.Substring(0, length) : text;

        private void EndEdit(bool keep)
        {
            var view = _editing;
            if (view == null) return;
            _editing = null;
            _padField = null;
            FrontInput.Pause(0.15f);
            var row = (TextRow)view.Row;
            var typed = (_draft ?? "").Trim();
            if (keep && typed != row.Value)
            {
                if (row.Write(typed)) Changed(view, true);
                else
                {
                    // Refused, as an empty name is: the old text stays.
                    MenuSound.Play(MenuCue.Back);
                    view.Focus.Flash?.Pulse();
                }
            }
            else if (keep) MenuSound.Play(MenuCue.Forward);
            if (view.Caret != null) view.Caret.gameObject.SetActive(false);
            PaintRow(view, ReferenceEquals(view.Focus, Focused));
        }

        // Keeps the focused row inside the visible part of the list, with a row of context where there is one.
        private void Reveal(bool snap)
        {
            if (_focusIndex < 0 || _focusIndex >= _rows.Count) return;
            var view = _rows[_focusIndex];
            var top = -view.Rect.offsetMax.y;
            var bottom = top + view.Height;
            var height = _viewport.rect.height;
            var goal = _scroll.Goal;
            if (top - view.Height < goal) goal = top - (_focusIndex == 0 ? U(SectionHeight) : view.Height);
            else if (bottom + view.Height > goal + height) goal = bottom + view.Height - height;
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
            for (var i = 0; i < _tabs.Count; i++) PaintTab(_tabs[i], ReferenceEquals(Focused, _tabs[i].Focus));
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
            PaintNote();
        }

        private void PaintRow(RowView view, bool focused)
        {
            if (view.Label == null) return;
            var row = view.Row;
            var available = row.Available;
            var editing = ReferenceEquals(view, _editing);
            string value;
            try
            {
                value = editing ? _draft : row.Value;
            }
            catch (Exception)
            {
                value = "—";
            }
            if (view.Picture != null)
            {
                var picture = row.Picture;
                view.Picture.texture = picture;
                view.Picture.enabled = picture != null;
            }
            // A setting that another has switched off stays in view, dimmed, and focus passes over it. On the focus
            // plate everything on the row turns dark.
            var plate = focused && available;
            view.Label.color = !available ? MenuKit.Faint : plate ? MenuKit.OnPlate : MenuKit.Soft;
            if (view.Detail != null)
            {
                view.Detail.text = row.Detail ?? "";
                view.Detail.color = !available ? MenuKit.Faint : plate ? MenuKit.OnPlateMuted : MenuKit.Muted;
            }
            view.Value.text = row.Open != null ? value.ToUpper(Languages.CultureOf(Languages.Current)) : value;
            view.Value.color = !available ? MenuKit.Faint : plate ? MenuKit.OnPlate : row.Tint ?? (row.Open == null ? MenuKit.Ink : MenuKit.Soft);
            // A dot beside a setting that is not at its default.
            view.Changed.enabled = row.Open == null && !SafeDefault(row);
            view.Changed.color = plate ? MenuKit.OnPlateMuted : MenuKit.Muted;
            if (view.Underline != null)
            {
                view.Underline.color = plate ? (editing ? MenuKit.OnPlate : MenuKit.OnPlateMuted) : editing ? MenuKit.Ink : MenuKit.Rule;
                if (view.Caret != null) view.Caret.GetComponent<Image>().color = plate ? MenuKit.OnPlate : MenuKit.Ink;
                if (view.Hint != null)
                {
                    view.Hint.enabled = !editing && string.IsNullOrEmpty(value);
                    view.Hint.color = plate ? MenuKit.OnPlateMuted : MenuKit.Faint;
                }
                if (view.Field != null) view.Field.color = plate ? MenuKit.Hex(0x111212, 0.08f) : MenuKit.Field;
                // While typing, the end of a long text stays in view.
                var wide = editing && view.Value.GetPreferredValues(value + " ").x > view.Value.rectTransform.rect.width;
                view.Value.alignment = wide ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft;
                view.Value.overflowMode = editing ? TextOverflowModes.Masking : TextOverflowModes.Ellipsis;
                if (editing) PaintCaret(view);
                return;
            }
            if (view.Track != null)
            {
                view.Track.GetComponent<Image>().color = plate ? MenuKit.OnPlateTrack : MenuKit.BarOff;
                view.Fill.GetComponent<Image>().color = plate ? MenuKit.OnPlate : MenuKit.Soft;
                view.Thumb.GetComponent<Image>().color = plate ? MenuKit.OnPlate : MenuKit.Ink;
                view.Slide.Set(row.Fraction ?? 0);
                PlaceThumb(view);
                return;
            }
            var index = row.Index;
            if (view.Left != null)
            {
                // An end of the list greys out its arrow, as there is nothing further that way.
                Tint(view.Left, !plate ? MenuKit.Faint : index <= 0 ? MenuKit.OnPlateTrack : MenuKit.OnPlate);
                Tint(view.Right, !plate ? MenuKit.Faint : index >= row.Count - 1 && row.Count > 0 ? MenuKit.OnPlateTrack : MenuKit.OnPlate);
            }
            else Tint(view.Right, plate ? MenuKit.OnPlate : MenuKit.Faint);
            if (view.PipMarks == null) return;
            for (var i = 0; i < view.PipMarks.Length; i++)
                view.PipMarks[i].color = i == index
                    ? (plate ? MenuKit.OnPlate : available ? MenuKit.Soft : MenuKit.Faint)
                    : plate ? MenuKit.OnPlateTrack : MenuKit.BarOff;
        }

        private void PaintCaret(RowView view)
        {
            if (view.Caret == null) return;
            var shown = (Time.unscaledTime - _editStart) % (CaretBlink * 2) < CaretBlink;
            if (view.Caret.gameObject.activeSelf != shown) view.Caret.gameObject.SetActive(shown);
            var box = view.Value.rectTransform.rect.width;
            var width = view.Value.GetPreferredValues(_draft ?? "").x;
            view.Caret.anchoredPosition = new Vector2(Mathf.Min(width, box) + U(2), 0);
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
                LayoutDetails(null);
                return;
            }
            _name.text = ItemName(view.Row);
            _about.text = ItemAbout(view.Row);
            IReadOnlyList<(string Label, string Value)> facts;
            try { facts = ItemFacts(view.Row); }
            catch (Exception) { facts = null; }
            LayoutDetails(facts);
            string defaultValue;
            try { defaultValue = view.Row.DefaultValue; }
            catch (Exception) { defaultValue = ""; }
            // A default whose own name is "Default" says nothing more than the label would.
            var plain = defaultValue.Length == 0 || view.Row is TextRow
                || string.Equals(defaultValue, Text["options.value.default"], StringComparison.CurrentCultureIgnoreCase);
            _default.text = plain ? "" : Text.Get("options.default", ("value", defaultValue));
        }

        // The name as tall as its lines, the facts in a ruled table under it, and what the item does below them.
        private void LayoutDetails(IReadOnlyList<(string Label, string Value)> facts)
        {
            var width = _details.rect.width;
            var nameHeight = string.IsNullOrEmpty(_name.text) ? 0 : _name.GetPreferredValues(_name.text, width, 0).y;
            var y = U(22) + Mathf.Max(U(34), nameHeight) + U(16);
            var count = facts?.Count ?? 0;
            while (_facts.Count < count) _facts.Add(BuildFact());
            for (var i = 0; i < _facts.Count; i++)
            {
                var fact = _facts[i];
                var shown = i < count;
                fact.Rect.gameObject.SetActive(shown);
                if (!shown) continue;
                fact.Rect.Strip(y, U(FactHeight), 0, 0);
                fact.Label.text = facts[i].Label.ToUpper(Languages.CultureOf(Languages.Current));
                fact.Value.text = facts[i].Value ?? "";
                y += U(FactHeight);
            }
            if (count > 0) y += U(18);
            _about.rectTransform.Strip(y, U(260), 0, 0);
            // The default sits just under what the setting does, clear of the main action at the foot.
            var aboutHeight = string.IsNullOrEmpty(_about.text) ? 0 : _about.GetPreferredValues(_about.text, width, 0).y;
            var below = y + Mathf.Min(aboutHeight, U(260)) + U(16);
            _default.rectTransform.Strip(Mathf.Max(0, Mathf.Min(below, _details.rect.height - _foot)), U(24), 0, 0);
        }

        private FactView BuildFact()
        {
            var view = new FactView { Rect = MenuKit.Box(_details, "Fact") };
            view.Label = MenuKit.Text(view.Rect, "Label", "", U(13), true, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
            view.Label.characterSpacing = 18;
            view.Label.rectTransform.Place(0, 0, 0.38f, 1);
            view.Value = MenuKit.Text(view.Rect, "Value", "", U(17), false, MenuKit.Soft, TextAlignmentOptions.MidlineLeft, true);
            view.Value.rectTransform.Place(0.38f, 0, 1, 1, U(8), 0, 0, 0);
            MenuKit.Fill(MenuKit.Box(view.Rect, "Rule").Place(0, 0, 1, 0, 0, 0, 0, -Mathf.Max(1f, U(1))), null, MenuKit.Line);
            return view;
        }

        private void PaintNote()
        {
            if (_note == null) return;
            _note.color = _problem != null ? MenuKit.Red : MenuKit.Muted;
            string summary;
            try { summary = Summary; }
            catch (Exception) { summary = null; }
            _note.text = _problem ?? summary ?? "";
        }
    }
}
