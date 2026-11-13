// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Pooled row views for the Mods menu page pane, bound from the shared view model.

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal enum MenuRowTemplate
    {
        Card,
        Header,
        Notice,
        Value,
        Link,
        Toggle,
        Choice,
        Slider,
        TextApply,
        Input,
        Command,
        Swatches,
        Stats
    }

    /// <summary>
    /// Binds a <see cref="MenuPage"/> onto pooled row views. Views with an unchanged key and template survive a
    /// rebind, so drafts, focus and caret survive with them.
    /// </summary>
    internal sealed class PageView
    {
        private const string CardKey = "page/header";
        private const float SliderIdleApply = 0.25f;
        private readonly RectTransform _content;
        private readonly Transform _pool;
        private readonly Func<MenuRow, string, bool> _intent;
        private readonly Action<MenuRow, string> _live;
        private readonly bool _card;
        private readonly Dictionary<MenuRowTemplate, Stack<RowView>> _free = new Dictionary<MenuRowTemplate, Stack<RowView>>();
        private readonly Dictionary<int, RowView> _byRoot = new Dictionary<int, RowView>();
        private readonly List<(string Key, MenuRowTemplate Template, MenuRow Row)> _wanted = new List<(string, MenuRowTemplate, MenuRow)>();
        private readonly HashSet<(string, MenuRowTemplate)> _seen = new HashSet<(string, MenuRowTemplate)>();
        private readonly List<RowView> _order = new List<RowView>();
        private Dictionary<(string, MenuRowTemplate), RowView> _bound = new Dictionary<(string, MenuRowTemplate), RowView>();
        private Dictionary<(string, MenuRowTemplate), RowView> _next = new Dictionary<(string, MenuRowTemplate), RowView>();
        private SliderView _pendingSlider;
        private string _pendingKey;
        private string _pendingValue;
        private float _pendingAt;
        private bool _pendingPointer;

        /// <param name="intent">
        /// Receives the row whose control was used and the value it carries, if any, and returns false when that value was refused.
        /// </param>
        /// <param name="live">Receives every slider move as it happens; the idle apply still goes to <paramref name="intent"/>.</param>
        /// <param name="card">Whether the page starts with its title card.</param>
        internal PageView(RectTransform content, Func<MenuRow, string, bool> intent, Action<MenuRow, string> live = null, bool card = true)
        {
            _content = content;
            _intent = intent;
            _live = live;
            _card = card;
            var pool = new GameObject("Row Pool");
            pool.SetActive(false);
            pool.transform.SetParent(content.parent, false);
            _pool = pool.transform;
        }

        internal MenuPage Page { get; private set; }
        internal IReadOnlyList<RowView> Views => _order;

        internal void Bind(MenuPage page)
        {
            Page = page;
            _wanted.Clear();
            _seen.Clear();
            if (page != null)
            {
                // Home's title would only repeat the menu heading, so its summary strip leads instead.
                if (_card && page.Kind != MenuPageKind.Home)
                {
                    Want(CardKey, MenuRowTemplate.Card, CardRow(page));
                }

                foreach (var section in page.Sections)
                {
                    if (section == null)
                    {
                        continue;
                    }

                    var titled = !string.IsNullOrWhiteSpace(section.Title);
                    if (titled)
                    {
                        Want(HeaderKey(section.Key), MenuRowTemplate.Header, HeaderRow(section));
                    }

                    // An untitled section has no header to unfold it from.
                    if (titled && section.Collapsed)
                    {
                        continue;
                    }

                    foreach (var row in section.Rows)
                    {
                        if (row != null)
                        {
                            Want(row.Key, TemplateFor(row), row);
                        }
                    }
                }
            }

            _next.Clear();
            foreach (var wanted in _wanted)
            {
                var id = (wanted.Key, wanted.Template);
                if (_bound.Remove(id, out var kept))
                {
                    _next[id] = kept;
                }
            }

            // Leftovers go back first so the new rows below can reuse them.
            var changed = _bound.Count > 0;
            foreach (var view in _bound.Values)
            {
                Release(view);
            }

            _bound.Clear();
            (_bound, _next) = (_next, _bound);
            _order.Clear();
            for (var i = 0; i < _wanted.Count; i++)
            {
                var wanted = _wanted[i];
                var id = (wanted.Key, wanted.Template);
                if (!_bound.TryGetValue(id, out var view))
                {
                    view = Acquire(wanted.Template);
                    _bound[id] = view;
                    changed = true;
                }

                if (view.Root.GetSiblingIndex() != i)
                {
                    view.Root.SetSiblingIndex(i);
                    changed = true;
                }

                changed |= view.Bind(wanted.Row);
                _order.Add(view);
            }

            if (changed)
            {
                LayoutRebuilder.MarkLayoutForRebuild(_content);
            }
        }

        internal void Clear()
        {
            Bind(null);
        }

        /// <summary>Applies a slider once it has been still for a moment or the pointer lets go, and caps relaid labels.</summary>
        internal void Tick(float now)
        {
            MenuLabel.CapLines();
            if (_pendingSlider != null && (now - _pendingAt >= SliderIdleApply || (_pendingPointer && !Input.GetMouseButton(0))))
            {
                FlushSlider();
            }
        }

        internal bool TryGetView(string key, out RowView view)
        {
            foreach (var candidate in _order)
            {
                if (string.Equals(candidate.Key, key, StringComparison.Ordinal))
                {
                    view = candidate;
                    return true;
                }
            }

            view = null;
            return false;
        }

        /// <summary>The bound row that holds a selected object, for selection-driven scrolling and tooltips.</summary>
        internal RowView FindView(GameObject target)
        {
            for (var transform = target != null ? target.transform : null; transform != null && transform != _content; transform = transform.parent)
            {
                if (_byRoot.TryGetValue(transform.gameObject.GetInstanceID(), out var view) && view.Row != null)
                {
                    return view;
                }
            }

            return null;
        }

        /// <summary>The row key of a section's header.</summary>
        internal static string HeaderKey(string sectionKey)
        {
            return "section:" + sectionKey;
        }

        internal static MenuRowTemplate TemplateFor(MenuRow row)
        {
            if (row.Stats.Count > 0)
            {
                return MenuRowTemplate.Stats;
            }

            switch (row.Kind)
            {
                case MenuRowKind.Setting:
                case MenuRowKind.ConfigValue:
                    switch (row.Control.Kind)
                    {
                        case MenuControlKind.Toggle: return MenuRowTemplate.Toggle;
                        case MenuControlKind.Choice: return MenuRowTemplate.Choice;
                        case MenuControlKind.Slider: return MenuRowTemplate.Slider;
                        case MenuControlKind.TextApply: return MenuRowTemplate.TextApply;
                        case MenuControlKind.Swatches: return MenuRowTemplate.Swatches;
                        default: return MenuRowTemplate.Value;
                    }
                case MenuRowKind.Input:
                    return MenuRowTemplate.Input;
                case MenuRowKind.Command:
                    return MenuRowTemplate.Command;
                case MenuRowKind.Content:
                    return MenuRowTemplate.Value;
            }

            if (row.Control.Kind == MenuControlKind.Open)
            {
                return MenuRowTemplate.Link;
            }

            return string.IsNullOrEmpty(row.Value) ? MenuRowTemplate.Notice : MenuRowTemplate.Value;
        }

        internal bool Raise(MenuRow row, string value)
        {
            return _intent == null || _intent(row, value);
        }

        internal void RaiseLive(MenuRow row, string value)
        {
            _live?.Invoke(row, value);
        }

        internal void QueueSlider(SliderView view, string value)
        {
            if (_pendingSlider != null && _pendingSlider != view)
            {
                FlushSlider();
            }

            if (_pendingSlider == null)
            {
                _pendingPointer = false;
            }

            _pendingSlider = view;
            _pendingKey = view.Key;
            _pendingValue = value;
            _pendingAt = Time.unscaledTime;
            _pendingPointer |= Input.GetMouseButton(0);
        }

        internal bool IsPending(SliderView view)
        {
            return _pendingSlider == view;
        }

        /// <summary>Applies a waiting slider value now, for a view that closes after its live handler has run.</summary>
        internal void FlushPending()
        {
            if (_pendingSlider != null)
            {
                FlushSlider();
            }
        }

        private void FlushSlider()
        {
            var view = _pendingSlider;
            _pendingSlider = null;
            if (view.Row != null && string.Equals(view.Key, _pendingKey, StringComparison.Ordinal))
            {
                Raise(view.Row, _pendingValue);
            }
        }

        private void Want(string key, MenuRowTemplate template, MenuRow row)
        {
            key ??= "";
            var unique = key;
            for (var occurrence = 1; !_seen.Add((unique, template)); occurrence++)
            {
                unique = key + "#dup" + occurrence;
            }

            _wanted.Add((unique, template, row));
        }

        private RowView Acquire(MenuRowTemplate template)
        {
            if (_free.TryGetValue(template, out var free) && free.Count > 0)
            {
                var view = free.Pop();
                view.Root.SetParent(_content, false);
                view.Root.gameObject.SetActive(true);
                return view;
            }

            var created = RowView.Create(template, this, _content);
            _byRoot[created.Root.gameObject.GetInstanceID()] = created;
            return created;
        }

        private void Release(RowView view)
        {
            if (_pendingSlider == view)
            {
                _pendingSlider = null;
            }

            view.Unbind();
            view.Root.gameObject.SetActive(false);
            view.Root.SetParent(_pool, false);
            if (!_free.TryGetValue(view.Template, out var free))
            {
                _free[view.Template] = free = new Stack<RowView>();
            }

            free.Push(view);
        }

        private static MenuRow CardRow(MenuPage page)
        {
            return new MenuRow { Key = CardKey, Label = page.Title, Value = page.Meta, Detail = page.Description, Tone = page.Tone };
        }

        private static MenuRow HeaderRow(MenuSection section)
        {
            var tone = MenuTone.Neutral;
            foreach (var row in section.Rows)
            {
                if (row != null)
                {
                    tone = MenuText.WorseTone(tone, row.Tone);
                }
            }

            return new MenuRow
            {
                Key = HeaderKey(section.Key),
                Label = section.Title,
                Tone = tone,
                Control = section.Collapsible ? new MenuControl { Kind = MenuControlKind.Open, Editable = true } : MenuControl.None,
                Intent = section.Collapsible
                    ? new MenuIntent { Kind = section.Collapsed ? MenuIntentKind.Expand : MenuIntentKind.Collapse, Key = section.Key }
                    : MenuIntent.None
            };
        }
    }

    /// <summary>
    /// One pooled row. Listeners are wired once when the view is built and read <see cref="Row"/> when they fire,
    /// so a rebind only swaps the binding.
    /// </summary>
    internal abstract class RowView
    {
        internal const float HeaderHeight = 40f;
        internal const float OptionHeight = 56f;
        /// <summary>The height of a row that also shows a description line.</summary>
        internal const float DescribedHeight = 76f;
        private readonly float _baseHeight;

        protected RowView(PageView owner, MenuRowTemplate template, Transform parent, float minHeight, Color backing, bool vertical = false)
        {
            Owner = owner;
            Template = template;
            _baseHeight = minHeight;
            // Rows sit on the veil, set apart by a hairline; the fill shows faintly at rest and fully when focused.
            Backing = MenuWidgets.CreateImage(template + "Row", parent, backing.a > 0.001f ? MenuKit.Faded(0.035f) : backing);
            Root = Backing.rectTransform;
            if (backing.a > 0.001f) MenuWidgets.Underline(Root);

            HorizontalOrVerticalLayoutGroup layout = vertical
                ? Root.gameObject.AddComponent<VerticalLayoutGroup>()
                : Root.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = vertical ? new RectOffset(24, 24, 16, 16) : new RectOffset(18, 18, 8, 8);
            layout.spacing = vertical ? 4f : 12f;
            layout.childAlignment = vertical ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = vertical;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            Layout = Root.gameObject.AddComponent<LayoutElement>();
            Layout.minHeight = minHeight;
        }

        internal RectTransform Root { get; }
        internal LayoutElement Layout { get; }
        internal MenuRowTemplate Template { get; }
        internal MenuRow Row { get; private set; }
        internal string Key => Row?.Key;
        /// <summary>The control that takes selection for this row, or null when it has none.</summary>
        internal Selectable Primary { get; private protected set; }
        /// <summary>Set while the view writes its own controls, so their listeners do not send the write back.</summary>
        internal bool Updating { get; private set; }
        protected PageView Owner { get; }
        protected Image Backing { get; }

        internal static RowView Create(MenuRowTemplate template, PageView owner, Transform parent)
        {
            switch (template)
            {
                case MenuRowTemplate.Card: return new CardView(owner, parent);
                case MenuRowTemplate.Header: return new HeaderView(owner, parent);
                case MenuRowTemplate.Toggle: return new ToggleView(owner, parent);
                case MenuRowTemplate.Choice: return new ChoiceView(owner, parent);
                case MenuRowTemplate.Slider: return new SliderView(owner, parent);
                case MenuRowTemplate.TextApply: return new TextApplyView(owner, parent);
                case MenuRowTemplate.Input: return new InputView(owner, parent);
                case MenuRowTemplate.Command: return new CommandView(owner, parent);
                case MenuRowTemplate.Swatches: return new SwatchView(owner, parent);
                case MenuRowTemplate.Stats: return new StatsView(owner, parent);
                default: return new TextRowView(owner, template, parent);
            }
        }

        /// <summary>Writes the row into the view and reports whether anything visible changed.</summary>
        internal bool Bind(MenuRow row)
        {
            var rebound = !string.Equals(Row?.Key, row.Key, StringComparison.Ordinal);
            Row = row;
            Updating = true;
            try
            {
                return Apply(row, rebound);
            }
            finally
            {
                Updating = false;
            }
        }

        /// <summary>Whether the control takes Left and Right itself, as a slider does.</summary>
        internal virtual bool UsesHorizontal => false;

        internal virtual void Unbind()
        {
            Row = null;
        }

        /// <summary>Adds the row's selectable controls, left to right.</summary>
        internal virtual void CollectSelectables(List<Selectable> into)
        {
            if (Primary != null)
            {
                into.Add(Primary);
            }
        }

        protected abstract bool Apply(MenuRow row, bool rebound);

        /// <summary>Raises the row's intent; false when nothing was raised or the value was refused.</summary>
        protected bool Invoke(string value)
        {
            return !Updating && Row != null && Owner.Raise(Row, value);
        }

        protected void OnClick(Button button, Action action)
        {
            button.onClick.AddListener((UnityAction)new Action(() =>
            {
                if (!Updating && Row != null)
                {
                    action();
                }
            }));
        }

        /// <summary>Gives a row that shows a description line its taller minimum height.</summary>
        protected bool FitHeight(bool described)
        {
            var height = described ? Mathf.Max(_baseHeight, DescribedHeight) : _baseHeight;
            if (Layout.minHeight == height)
            {
                return false;
            }

            Layout.minHeight = height;
            return true;
        }
    }

    internal sealed class CardView : RowView
    {
        private readonly MenuLabel _title;
        private readonly MenuDot _dot;
        private readonly MenuLabel _meta;
        private readonly MenuLabel _description;

        internal CardView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Card, parent, 0f, MenuWidgets.RowColor, true)
        {
            var titleRow = MenuWidgets.Strip(Root, "TitleRow", 1f);
            _title = MenuLabel.Create(titleRow, "Title", 28f, MenuWidgets.TextColor, true, false);
            MenuWidgets.Flexible(_title.Text.gameObject, 1f);
            _dot = MenuDot.Create(titleRow);
            _meta = MenuLabel.Create(Root, "Meta", 14f, MenuWidgets.MutedColor, false, true, 2);
            _description = MenuLabel.Create(Root, "Description", 15f, MenuWidgets.TextColor, false, true, 3);
        }

        protected override bool Apply(MenuRow row, bool rebound)
        {
            var changed = _title.Set(row.Label);
            changed |= _dot.Set(row.Tone, row.Tone != MenuTone.Neutral);
            changed |= _meta.Show(row.Value);
            changed |= _description.Show(row.Detail);
            return changed;
        }
    }

    internal sealed class HeaderView : RowView
    {
        private readonly MenuLabel _title;
        private readonly MenuDot _dot;
        private readonly MenuLabel _chevron;
        private readonly Button _button;
        private bool? _collapsible;

        internal HeaderView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Header, parent, HeaderHeight, new Color(1f, 1f, 1f, 0.02f))
        {
            _title = MenuLabel.Create(Root, "Title", 15f, MenuWidgets.HeaderColor, true, false);
            MenuWidgets.Flexible(_title.Text.gameObject, 1f);
            _dot = MenuDot.Create(Root);
            _chevron = MenuLabel.Create(Root, "Chevron", 15f, MenuWidgets.HeaderColor, true, false);
            _chevron.Text.alignment = TextAlignmentOptions.Center;
            MenuWidgets.Fixed(_chevron.Text.gameObject, 24f, 0f);
            _button = MenuWidgets.MakeButton(Backing);
            OnClick(_button, () => Invoke(null));
        }

        protected override bool Apply(MenuRow row, bool rebound)
        {
            var collapsible = row.Intent.Kind == MenuIntentKind.Expand || row.Intent.Kind == MenuIntentKind.Collapse;
            var changed = _title.Set((row.Label ?? "").ToUpperInvariant());
            changed |= _dot.Set(row.Tone, row.Tone == MenuTone.Warn || row.Tone == MenuTone.Bad);
            changed |= _chevron.Set(!collapsible ? "" : row.Intent.Kind == MenuIntentKind.Expand ? "+" : "-");
            if (_collapsible != collapsible)
            {
                _collapsible = collapsible;
                _button.interactable = collapsible;
                Primary = collapsible ? _button : null;
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>Notice, Value and Link rows: label and detail, value, a status pill, and for links a chevron and a row button.</summary>
    internal sealed class TextRowView : RowView
    {
        private readonly MenuLabel _label;
        private readonly MenuLabel _detail;
        private readonly MenuLabel _value;
        private readonly MenuPill _status;

        internal TextRowView(PageView owner, MenuRowTemplate template, Transform parent)
            : base(owner, template, parent, template == MenuRowTemplate.Notice ? 36f : template == MenuRowTemplate.Link ? OptionHeight : 48f,
                template == MenuRowTemplate.Notice ? Color.clear : MenuWidgets.RowColor)
        {
            if (template == MenuRowTemplate.Notice)
            {
                _label = MenuLabel.Create(Root, "Text", 15f, MenuWidgets.MutedColor, false, true);
                MenuWidgets.Flexible(_label.Text.gameObject, 1f);
                return;
            }

            var column = MenuWidgets.Column(Root, "LabelColumn", 38f);
            _label = MenuLabel.Create(column, "Label", 17f, MenuWidgets.TextColor, false, true, 2);
            _detail = MenuLabel.Create(column, "Detail", 14f, MenuWidgets.MutedColor, false, true, 2);
            var values = MenuWidgets.Strip(Root, "Values", 62f);
            _value = MenuLabel.Create(values, "Value", 16f, template == MenuRowTemplate.Link ? MenuWidgets.MutedColor : MenuWidgets.TextColor, false, true, 3);
            // Sized to its text so the pill follows a short value; a long one shrinks to the space left and wraps.
            var element = _value.Text.gameObject.AddComponent<LayoutElement>();
            element.minWidth = 0f;
            element.flexibleWidth = 0f;
            _status = MenuPill.Create(values);
            if (template != MenuRowTemplate.Link)
            {
                return;
            }

            var chevron = MenuLabel.Create(Root, "Chevron", 17f, MenuWidgets.MutedColor, true, false);
            chevron.Text.alignment = TextAlignmentOptions.Center;
            chevron.Set(">");
            MenuWidgets.Fixed(chevron.Text.gameObject, 24f, 0f);
            // A row that opens something takes the full focus fill when hovered or selected.
            Backing.color = MenuWidgets.RowColor;
            var button = MenuWidgets.MakeButton(Backing, 0.3f);
            OnClick(button, () => Invoke(null));
            Primary = button;
        }

        protected override bool Apply(MenuRow row, bool rebound)
        {
            var changed = _label.Set(row.Label);
            if (_detail == null)
            {
                return changed;
            }

            changed |= _detail.Show(row.Detail);
            changed |= FitHeight(!string.IsNullOrEmpty(row.Detail));
            changed |= _value.Set(row.Value);
            changed |= _status.Set(row.Status, row.Tone);
            return changed;
        }
    }

    /// <summary>
    /// Settings and config values: label and detail at 40%, then the control with its status pill beside it. Controls
    /// are added after the pill exists, swatches even during a bind, so binding keeps the pill last.
    /// </summary>
    internal abstract class OptionView : RowView
    {
        private readonly MenuLabel _label;
        private readonly MenuLabel _detail;
        private readonly MenuPill _status;

        protected OptionView(PageView owner, MenuRowTemplate template, Transform parent)
            : base(owner, template, parent, OptionHeight, MenuWidgets.RowColor)
        {
            var column = MenuWidgets.Column(Root, "LabelColumn", 40f);
            _label = MenuLabel.Create(column, "Label", 17f, MenuWidgets.TextColor, false, true, 2);
            _detail = MenuLabel.Create(column, "Detail", 14f, MenuWidgets.MutedColor, false, true, 2);
            Controls = MenuWidgets.Strip(Root, "Controls", 60f);
            _status = MenuPill.Create(Controls);
        }

        protected RectTransform Controls { get; }

        protected sealed override bool Apply(MenuRow row, bool rebound)
        {
            var changed = _label.Set(row.Label);
            changed |= _detail.Show(row.Detail);
            changed |= FitHeight(!string.IsNullOrEmpty(row.Detail));
            changed |= ApplyControl(row, rebound);
            changed |= _status.Set(row.Status, row.Tone);
            var pill = _status.Root;
            if (pill.GetSiblingIndex() != Controls.childCount - 1)
            {
                pill.SetAsLastSibling();
                changed = true;
            }

            return changed;
        }

        protected abstract bool ApplyControl(MenuRow row, bool rebound);
    }

    internal sealed class ToggleView : OptionView
    {
        private static readonly Color OnBacking = MenuKit.Faded(0.30f);
        private static readonly Color OffBacking = MenuWidgets.ButtonColor;
        private readonly Button _button;
        private readonly MenuLabel _value;
        private bool? _on;

        internal ToggleView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Toggle, parent)
        {
            _button = MenuWidgets.CreateButton(Controls, "", 220f, out _value);
            OnClick(_button, () => Invoke(IsOn(Row.Value) ? "false" : "true"));
            Primary = _button;
        }

        protected override bool ApplyControl(MenuRow row, bool rebound)
        {
            var on = IsOn(row.Value);
            if (_on == on)
            {
                return false;
            }

            _on = on;
            _value.Set(MenuText.BoolDisplay(on));
            _value.SetColor(on ? MenuWidgets.TextColor : MenuWidgets.MutedColor);
            _button.image.color = on ? OnBacking : OffBacking;
            return true;
        }

        private static bool IsOn(string value)
        {
            return LoaderConfig.TryParseBoolean(value, out var on) && on;
        }
    }

    internal sealed class ChoiceView : OptionView
    {
        private readonly Button _previous;
        private readonly MenuLabel _value;

        internal ChoiceView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Choice, parent)
        {
            _previous = MenuWidgets.CreateButton(Controls, "<", 44f, out _);
            _value = MenuLabel.Create(Controls, "Value", 16f, MenuWidgets.TextColor, false, false);
            _value.Text.alignment = TextAlignmentOptions.Center;
            MenuWidgets.Flexible(_value.Text.gameObject, 1f);
            var next = MenuWidgets.CreateButton(Controls, ">", 44f, out _);
            OnClick(_previous, () => Invoke(MenuText.AdjacentOption(Row.Control.Options, Row.Value, -1)));
            OnClick(next, () => Invoke(MenuText.AdjacentOption(Row.Control.Options, Row.Value, 1)));
            Primary = next;
        }

        internal override void CollectSelectables(List<Selectable> into)
        {
            into.Add(_previous);
            into.Add(Primary);
        }

        protected override bool ApplyControl(MenuRow row, bool rebound)
        {
            return _value.Set(row.Value);
        }
    }

    /// <summary>Colour swatches. Each option is an encoded colour and the value is the selected index.</summary>
    internal sealed class SwatchView : OptionView
    {
        private const float SwatchSize = 28f;
        private static readonly Color SelectedFrame = new Color(1f, 1f, 1f, 0.9f);
        private readonly List<(Button Button, Image Fill)> _swatches = new List<(Button, Image)>();
        private IReadOnlyList<string> _options;
        private int _count;
        private int _selected = -2;

        internal SwatchView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Swatches, parent)
        {
        }

        internal static string Encode(Color color)
        {
            return string.Join(",", color.r.ToString("R", CultureInfo.InvariantCulture), color.g.ToString("R", CultureInfo.InvariantCulture),
                color.b.ToString("R", CultureInfo.InvariantCulture), color.a.ToString("R", CultureInfo.InvariantCulture));
        }

        internal override void CollectSelectables(List<Selectable> into)
        {
            for (var i = 0; i < _count; i++)
            {
                into.Add(_swatches[i].Button);
            }
        }

        protected override bool ApplyControl(MenuRow row, bool rebound)
        {
            var changed = false;
            var options = row.Control.Options;
            if (!ReferenceEquals(_options, options))
            {
                _options = options;
                for (var i = 0; i < options.Count; i++)
                {
                    if (i == _swatches.Count)
                    {
                        _swatches.Add(CreateSwatch(i));
                    }

                    _swatches[i].Fill.color = Decode(options[i]);
                    _swatches[i].Button.gameObject.SetActive(true);
                }

                for (var i = options.Count; i < _swatches.Count; i++)
                {
                    _swatches[i].Button.gameObject.SetActive(false);
                }

                _count = options.Count;
                _selected = -2;
                changed = true;
            }

            var selected = int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < _count ? index : -1;
            if (_selected != selected)
            {
                _selected = selected;
                for (var i = 0; i < _count; i++)
                {
                    _swatches[i].Button.image.color = i == selected ? SelectedFrame : MenuWidgets.ButtonColor;
                }

                changed = true;
            }

            Primary = _count > 0 ? _swatches[Math.Max(selected, 0)].Button : null;
            return changed;
        }

        private (Button, Image) CreateSwatch(int index)
        {
            var frame = MenuWidgets.CreateImage("Swatch", Controls, MenuWidgets.ButtonColor);
            // Swatches shrink towards half size before a long palette overflows the row.
            MenuWidgets.Fixed(frame.gameObject, SwatchSize, SwatchSize).minWidth = SwatchSize * 0.5f;
            var fill = MenuWidgets.CreateImage("Fill", frame.transform, Color.white);
            MenuWidgets.Stretch(fill.rectTransform, 3f, 3f, 3f, 3f);
            var button = MenuWidgets.MakeButton(frame);
            OnClick(button, () => Invoke(index.ToString(CultureInfo.InvariantCulture)));
            return (button, fill);
        }

        private static Color Decode(string value)
        {
            var parts = (value ?? "").Split(',');
            var channels = new float[] { 0f, 0f, 0f, 1f };
            for (var i = 0; i < parts.Length && i < channels.Length; i++)
            {
                if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var channel) && float.IsFinite(channel))
                {
                    channels[i] = Mathf.Clamp01(channel);
                }
            }

            return new Color(channels[0], channels[1], channels[2], channels[3]);
        }
    }

    internal sealed class SliderView : OptionView
    {
        private readonly Slider _slider;
        private readonly MenuLabel _value;
        private float _min = float.NaN;
        private float _max = float.NaN;
        private float _current = float.NaN;

        internal SliderView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Slider, parent)
        {
            _slider = MenuWidgets.CreateSlider(Controls);
            _value = MenuLabel.Create(Controls, "Value", 16f, MenuWidgets.TextColor, false, false);
            _value.Text.alignment = TextAlignmentOptions.MidlineRight;
            MenuWidgets.Fixed(_value.Text.gameObject, 90f, 0f);
            _slider.onValueChanged.AddListener((UnityAction<float>)new Action<float>(OnChanged));
            Primary = _slider;
        }

        internal override bool UsesHorizontal => true;

        protected override bool ApplyControl(MenuRow row, bool rebound)
        {
            var changed = false;
            var min = (float)(row.Control.Min ?? 0d);
            var max = (float)(row.Control.Max ?? 1d);
            if (min != _min || max != _max)
            {
                _min = min;
                _max = max;
                _slider.minValue = min;
                _slider.maxValue = max;
                _current = float.NaN;
                changed = true;
            }

            // Leave a drag that has not been applied yet where the user has it.
            if (!rebound && Owner.IsPending(this))
            {
                return changed;
            }

            var value = MenuText.TryParseNumber(row.Value, out var parsed) ? (float)parsed : min;
            if (value != _current)
            {
                _current = value;
                _slider.value = value;
                changed = true;
            }

            changed |= _value.Set(MenuText.SliderLabel(row.Control, row.Value));
            return changed;
        }

        private void OnChanged(float value)
        {
            if (Updating || Row == null)
            {
                return;
            }

            // The slider now holds a value the binding has not seen.
            _current = float.NaN;
            var text = MenuText.FormatNumber(Row.Control, Snap(Row.Control, value));
            _value.Set(MenuText.SliderLabel(Row.Control, text));
            Owner.QueueSlider(this, text);
            // Queued first, so a rebind from the live handler leaves the drag where the user has it.
            Owner.RaiseLive(Row, text);
        }

        private static double Snap(MenuControl control, double value)
        {
            var min = control.Min ?? 0d;
            var max = control.Max ?? 1d;
            var step = control.Step > 0d ? control.Step.Value : control.ValueKind == ConfigValueKinds.Integer ? 1d : 0d;
            if (step > 0d)
            {
                value = min + Math.Round((value - min) / step) * step;
            }

            return Math.Clamp(value, Math.Min(min, max), Math.Max(min, max));
        }
    }

    internal sealed class TextApplyView : OptionView
    {
        private readonly TMP_InputField _input;
        private readonly Button _minus;
        private readonly Button _plus;
        private readonly Button _apply;
        private string _text = "";
        private string _valueKind;
        private int _limit = -1;
        private bool _edited;
        private bool? _stepped;

        internal TextApplyView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.TextApply, parent)
        {
            _input = MenuWidgets.CreateInput(Controls, false, "Value");
            MenuWidgets.Flexible(_input.gameObject, 1f);
            _minus = MenuWidgets.CreateButton(Controls, "-", 44f, out _);
            _plus = MenuWidgets.CreateButton(Controls, "+", 44f, out _);
            _apply = MenuWidgets.CreateButton(Controls, "APPLY", 110f, out _);
            _input.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value =>
            {
                if (Updating)
                {
                    return;
                }

                _text = value ?? "";
                _edited = true;
            }));
            OnClick(_minus, () => Submit(MenuText.StepNumber(Row.Control, _text, Row.Value, -1)));
            OnClick(_plus, () => Submit(MenuText.StepNumber(Row.Control, _text, Row.Value, 1)));
            OnClick(_apply, () => Submit(_text));
            Primary = _input;
        }

        internal override void Unbind()
        {
            _edited = false;
            base.Unbind();
        }

        internal override void CollectSelectables(List<Selectable> into)
        {
            into.Add(_input);
            if (_stepped == true)
            {
                into.Add(_minus);
                into.Add(_plus);
            }

            into.Add(_apply);
        }

        protected override bool ApplyControl(MenuRow row, bool rebound)
        {
            var changed = false;
            var control = row.Control;
            var stepped = MenuText.IsStepped(control);
            if (_stepped != stepped)
            {
                _stepped = stepped;
                _minus.gameObject.SetActive(stepped);
                _plus.gameObject.SetActive(stepped);
                changed = true;
            }

            if (!string.Equals(_valueKind, control.ValueKind, StringComparison.Ordinal))
            {
                _valueKind = control.ValueKind;
                _input.contentType = control.ValueKind == ConfigValueKinds.Integer ? TMP_InputField.ContentType.IntegerNumber
                    : control.ValueKind == ConfigValueKinds.Number ? TMP_InputField.ContentType.DecimalNumber
                    : TMP_InputField.ContentType.Standard;
            }

            // Settings and config values share the pool but not a limit, so it follows the row rather than the kind.
            var limit = control.MaxLength > 0 ? control.MaxLength : 2048;
            if (_limit != limit)
            {
                _limit = limit;
                _input.characterLimit = limit;
            }

            // An unapplied edit, or a field the user is in, keeps its text until the row is applied.
            if (rebound || (!_edited && !_input.isFocused))
            {
                _edited = false;
                var value = row.Value ?? "";
                if (!string.Equals(_text, value, StringComparison.Ordinal))
                {
                    _text = value;
                    _input.text = value;
                    changed = true;
                }
            }

            return changed;
        }

        // The apply rebinds the row before it returns, so a refused edit is put back afterwards to be corrected.
        private void Submit(string value)
        {
            var key = Key;
            var typed = _text;
            var edited = _edited;
            _edited = false;
            if (Invoke(value) || Row == null || !string.Equals(Key, key, StringComparison.Ordinal))
            {
                return;
            }

            if (edited && !string.Equals(_input.text, typed, StringComparison.Ordinal))
            {
                _input.text = typed;
            }

            _edited = edited;
        }
    }

    /// <summary>
    /// Provider form fields and friendly config values, pushed on every keystroke. A number value with a step gets
    /// - and + buttons that write the field the same way typing does.
    /// </summary>
    internal sealed class InputView : RowView
    {
        private readonly MenuLabel _label;
        private readonly MenuLabel _detail;
        private readonly TMP_InputField _input;
        private readonly Button _minus;
        private readonly Button _plus;
        private string _text = "";
        private string _valueKind;
        private bool? _editable;
        private bool? _stepped;
        private bool _reverting;
        private int _limit = -1;

        internal InputView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Input, parent, OptionHeight, MenuWidgets.RowColor)
        {
            var column = MenuWidgets.Column(Root, "LabelColumn", 38f);
            _label = MenuLabel.Create(column, "Label", 17f, MenuWidgets.TextColor, false, true, 2);
            _detail = MenuLabel.Create(column, "Detail", 14f, MenuWidgets.MutedColor, false, true, 2);
            var controls = MenuWidgets.Strip(Root, "Controls", 62f);
            _input = MenuWidgets.CreateInput(controls, false, "Value");
            _input.richText = false;
            _input.textComponent.richText = false;
            MenuWidgets.Flexible(_input.gameObject, 1f);
            _minus = MenuWidgets.CreateButton(controls, "-", 44f, out _);
            _plus = MenuWidgets.CreateButton(controls, "+", 44f, out _);
            _input.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value =>
            {
                if (Updating || _reverting || Row == null)
                {
                    return;
                }

                _text = value ?? "";
                if (!Invoke(_text) && Row != null)
                {
                    // The refusal rebound the row with the provider's own value, which the focused field skipped.
                    Revert(Row.Value);
                }
            }));
            OnClick(_minus, () => Step(-1));
            OnClick(_plus, () => Step(1));
            Primary = _input;
        }

        internal override void CollectSelectables(List<Selectable> into)
        {
            if (_editable != true)
            {
                return;
            }

            into.Add(_input);
            if (_stepped == true)
            {
                into.Add(_minus);
                into.Add(_plus);
            }
        }

        protected override bool Apply(MenuRow row, bool rebound)
        {
            var changed = _label.Set(row.Label);
            changed |= _detail.Show(row.Detail);
            changed |= FitHeight(!string.IsNullOrEmpty(row.Detail));
            if (_editable != row.Control.Editable)
            {
                _editable = row.Control.Editable;
                _input.interactable = row.Control.Editable;
                changed = true;
            }

            var stepped = row.Control.Editable && MenuText.IsStepped(row.Control);
            if (_stepped != stepped)
            {
                _stepped = stepped;
                _minus.gameObject.SetActive(stepped);
                _plus.gameObject.SetActive(stepped);
                changed = true;
            }

            if (!string.Equals(_valueKind, row.Control.ValueKind, StringComparison.Ordinal))
            {
                _valueKind = row.Control.ValueKind;
                _input.contentType = _valueKind == ConfigValueKinds.Integer ? TMP_InputField.ContentType.IntegerNumber
                    : _valueKind == ConfigValueKinds.Number ? TMP_InputField.ContentType.DecimalNumber
                    : TMP_InputField.ContentType.Standard;
            }

            if (_limit != row.Control.MaxLength)
            {
                _limit = row.Control.MaxLength;
                _input.characterLimit = row.Control.MaxLength;
            }

            var value = row.Value ?? "";
            if (!string.Equals(_text, value, StringComparison.Ordinal) && (rebound || !_input.isFocused))
            {
                _text = value;
                _input.text = value;
                changed = true;
            }

            return changed;
        }

        private void Step(int direction)
        {
            // The field's own change listener pushes the stepped text on, as if it had been typed.
            _input.text = MenuText.StepNumber(Row.Control, _text, Row.Value, direction);
        }

        private void Revert(string value)
        {
            _text = value ?? "";
            _reverting = true;
            try
            {
                _input.text = _text;
            }
            finally
            {
                _reverting = false;
            }
        }
    }

    /// <summary>A command with RUN and its status pill; one that cannot run and gives no reason reads as a muted info line.</summary>
    internal sealed class CommandView : RowView
    {
        private readonly MenuLabel _label;
        private readonly MenuLabel _detail;
        private readonly Button _run;
        private readonly MenuPill _status;
        private readonly MenuLabel _reason;
        private bool? _runnable;

        internal CommandView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Command, parent, OptionHeight, MenuWidgets.RowColor)
        {
            var column = MenuWidgets.Column(Root, "LabelColumn", 38f);
            _label = MenuLabel.Create(column, "Label", 17f, MenuWidgets.TextColor, false, true, 2);
            _detail = MenuLabel.Create(column, "Detail", 14f, MenuWidgets.MutedColor, false, true, 2);
            var controls = MenuWidgets.Strip(Root, "Controls", 62f);
            _run = MenuWidgets.CreateButton(controls, "RUN", 110f, out _);
            _status = MenuPill.Create(controls);
            _reason = MenuLabel.Create(controls, "Reason", 14f, MenuWidgets.MutedColor, false, true, 2);
            MenuWidgets.Flexible(_reason.Text.gameObject, 1f);
            OnClick(_run, () => Invoke(null));
        }

        protected override bool Apply(MenuRow row, bool rebound)
        {
            var runnable = row.Control.Kind == MenuControlKind.Run;
            var changed = _label.Set(row.Label);
            changed |= _label.SetColor(runnable || !string.IsNullOrEmpty(row.Value) ? MenuWidgets.TextColor : MenuWidgets.MutedColor);
            changed |= _detail.Show(row.Detail);
            changed |= FitHeight(!string.IsNullOrEmpty(row.Detail));
            changed |= _reason.Show(row.Value);
            changed |= _status.Set(row.Status, row.Tone);
            if (_runnable != runnable)
            {
                _runnable = runnable;
                _run.gameObject.SetActive(runnable);
                Primary = runnable ? _run : null;
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>A compact strip of figures, each a value over its label, such as the Home summary.</summary>
    internal sealed class StatsView : RowView
    {
        private readonly List<(RectTransform Cell, MenuLabel Value, MenuLabel Label)> _cells = new List<(RectTransform, MenuLabel, MenuLabel)>();
        private int _count;

        internal StatsView(PageView owner, Transform parent)
            : base(owner, MenuRowTemplate.Stats, parent, OptionHeight, MenuWidgets.RowColor)
        {
        }

        protected override bool Apply(MenuRow row, bool rebound)
        {
            var changed = false;
            var stats = row.Stats;
            for (var i = 0; i < stats.Count; i++)
            {
                if (i == _cells.Count)
                {
                    var cell = MenuWidgets.Column(Root, "Stat", 1f);
                    _cells.Add((cell,
                        MenuLabel.Create(cell, "Value", 22f, MenuWidgets.TextColor, true, false),
                        MenuLabel.Create(cell, "Label", 13f, MenuWidgets.MutedColor, false, false)));
                }

                var stat = stats[i];
                changed |= _cells[i].Value.Set(stat.Value);
                changed |= _cells[i].Value.SetColor(stat.Tone == MenuTone.Neutral ? MenuWidgets.TextColor : MenuWidgets.ToneColor(stat.Tone));
                changed |= _cells[i].Label.Set(stat.Label);
            }

            if (_count != stats.Count)
            {
                _count = stats.Count;
                for (var i = 0; i < _cells.Count; i++)
                {
                    _cells[i].Cell.gameObject.SetActive(i < _count);
                }

                changed = true;
            }

            return changed;
        }
    }

    /// <summary>A small tinted status badge that sits beside its row's control and hides while there is no status.</summary>
    internal sealed class MenuPill
    {
        private const float Width = 104f;
        private const float Height = 24f;
        private const float FillAlpha = 0.16f;
        private readonly Image _image;
        private readonly MenuLabel _label;
        private MenuTone? _tone;
        private bool _active = true;

        private MenuPill(Image image, MenuLabel label)
        {
            _image = image;
            _label = label;
        }

        internal RectTransform Root => _image.rectTransform;

        internal static MenuPill Create(Transform parent)
        {
            var image = MenuWidgets.CreateImage("Status", parent, Color.clear);
            MenuWidgets.Fixed(image.gameObject, Width, Height);
            var label = MenuLabel.Create(image.transform, "Label", 12f, MenuWidgets.MutedColor, true, false);
            label.Text.alignment = TextAlignmentOptions.Center;
            MenuWidgets.Stretch(label.Text.rectTransform, 4f, 0f, 4f, 0f);
            return new MenuPill(image, label);
        }

        internal bool Set(string status, MenuTone tone)
        {
            var visible = !string.IsNullOrWhiteSpace(status);
            var changed = false;
            if (_active != visible)
            {
                _active = visible;
                _image.gameObject.SetActive(visible);
                changed = true;
            }

            if (!visible)
            {
                return changed;
            }

            changed |= _label.Set(status);
            if (_tone != tone)
            {
                _tone = tone;
                var color = MenuWidgets.ToneColor(tone);
                _label.SetColor(color);
                _image.color = new Color(color.r, color.g, color.b, FillAlpha);
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>A TMP text written only when its managed value differs, so an unchanged rebind costs no re-mesh.</summary>
    internal sealed class MenuLabel
    {
        private static readonly List<MenuLabel> Unmeasured = new List<MenuLabel>();
        private readonly int _maxLines;
        private string _value = "";
        private Color _color;
        private bool _active = true;
        private LayoutElement _cap;
        private bool _capped;
        private bool _queued;
        private bool _missed;
        private int _queuedFrame;

        private MenuLabel(TextMeshProUGUI text, Color color, int maxLines)
        {
            Text = text;
            _color = color;
            _maxLines = maxLines;
        }

        internal TextMeshProUGUI Text { get; }

        internal static MenuLabel Create(Transform parent, string name, float size, Color color, bool heading, bool wrap, int maxLines = 0)
        {
            var text = MenuWidgets.CreateText(parent, name, size, color, heading, wrap);
            if (maxLines > 0)
            {
                text.maxVisibleLines = maxLines;
            }

            return new MenuLabel(text, color, maxLines);
        }

        /// <summary>
        /// Caps line-limited labels changed in an earlier frame, now that they have been laid out. TMP sizes a label for
        /// every wrapped line and maxVisibleLines only hides the extra ones, which would leave their space blank and the
        /// ellipsis unshown. Does nothing while no label is waiting.
        /// </summary>
        internal static void CapLines()
        {
            var frame = Time.frameCount;
            for (var i = Unmeasured.Count - 1; i >= 0; i--)
            {
                var label = Unmeasured[i];
                if (label._queuedFrame >= frame)
                {
                    continue;
                }

                Unmeasured.RemoveAt(i);
                label._queued = false;
                label.Cap();
            }
        }

        internal bool Set(string value)
        {
            value ??= "";
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                if (_missed)
                {
                    Measure();
                }

                return false;
            }

            _value = value;
            Text.text = value;
            if (_maxLines > 0)
            {
                Measure();
            }

            return true;
        }

        private void Measure()
        {
            _missed = false;
            if (_capped)
            {
                _capped = false;
                _cap.preferredHeight = -1f;
            }

            _queuedFrame = Time.frameCount;
            if (!_queued)
            {
                _queued = true;
                Unmeasured.Add(this);
            }
        }

        private void Cap()
        {
            if (Text == null)
            {
                return;
            }

            // A label hidden before its layout ran is measured on its next bind instead.
            if (!Text.isActiveAndEnabled)
            {
                _missed = true;
                return;
            }

            var info = Text.textInfo;
            if (info == null || info.lineCount <= _maxLines)
            {
                return;
            }

            var lines = info.lineInfo;
            if (_cap == null)
            {
                var element = Text.GetComponent<LayoutElement>();
                _cap = element != null ? element : Text.gameObject.AddComponent<LayoutElement>();
            }

            _cap.preferredHeight = Mathf.Ceil(lines[0].ascender - lines[_maxLines - 1].descender) + 1f;
            _capped = true;
        }

        /// <summary>Sets the text and hides the label while it is empty.</summary>
        internal bool Show(string value)
        {
            var visible = !string.IsNullOrEmpty(value);
            var changed = SetActive(visible);
            if (visible)
            {
                changed |= Set(value);
            }

            return changed;
        }

        internal bool SetColor(Color color)
        {
            if (MenuWidgets.SameColor(_color, color))
            {
                return false;
            }

            _color = color;
            Text.color = color;
            return true;
        }

        internal bool SetActive(bool active)
        {
            if (_active == active)
            {
                return false;
            }

            _active = active;
            Text.gameObject.SetActive(active);
            return true;
        }
    }

    /// <summary>A small status dot, shown only for tones worth a glance.</summary>
    internal sealed class MenuDot
    {
        private readonly Image _image;
        private MenuTone? _tone;
        private bool _active = true;

        private MenuDot(Image image)
        {
            _image = image;
        }

        internal static MenuDot Create(Transform parent)
        {
            var image = MenuWidgets.CreateImage("Dot", parent, MenuWidgets.MutedColor);
            MenuWidgets.Fixed(image.gameObject, 12f, 12f);
            return new MenuDot(image);
        }

        internal bool Set(MenuTone tone, bool visible)
        {
            var changed = false;
            if (_active != visible)
            {
                _active = visible;
                _image.gameObject.SetActive(visible);
                changed = true;
            }

            if (visible && _tone != tone)
            {
                _tone = tone;
                _image.color = MenuWidgets.ToneColor(tone);
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>Plain uGUI builders for the menu's rows and scroll regions, themed from <see cref="MenuTheme"/>.</summary>
    internal static class MenuWidgets
    {
        internal const float ControlHeight = 40f;
        // The front end's language: text in its ink, every surface that ink at low opacity, never a grey of its own.
        internal static readonly Color TextColor = MenuKit.Ink;
        internal static readonly Color MutedColor = MenuKit.Muted;
        internal static readonly Color HeaderColor = MenuKit.Soft;
        /// <summary>A row's fill when focused; at rest it shows only faintly, through its button tint.</summary>
        internal static readonly Color RowColor = MenuKit.FocusFill;
        internal static readonly Color ButtonColor = MenuKit.Faded(0.16f);
        internal static readonly Color InputColor = MenuKit.Field;
        internal static readonly Color CaretColor = MenuKit.Ink;
        internal static readonly Color RuleColor = MenuKit.Rule;

        // Status colors only for status; plain information stays in the ink.
        internal static Color ToneColor(MenuTone tone)
        {
            switch (tone)
            {
                case MenuTone.Info: return MenuKit.Soft;
                case MenuTone.Good: return MenuKit.Green;
                case MenuTone.Bad: return MenuKit.Red;
                case MenuTone.Warn: return MenuKit.Amber;
                default: return MutedColor;
            }
        }

        internal static bool SameColor(Color a, Color b)
        {
            return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        }

        internal static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<RectTransform>();
        }

        internal static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            // Only buttons, inputs, sliders, scrollbars and viewports take pointer events.
            image.raycastTarget = false;
            return image;
        }

        internal static void ApplySprite(Image image, Sprite sprite)
        {
            if (image == null || sprite == null)
            {
                return;
            }

            image.sprite = sprite;
            image.type = sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            image.pixelsPerUnitMultiplier = 1.2f;
        }

        internal static TextMeshProUGUI CreateText(Transform parent, string name, float size, Color color, bool heading, bool wrap)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = "";
            text.fontSize = size;
            text.fontStyle = heading ? FontStyles.Bold : FontStyles.Normal;
            text.characterSpacing = heading ? 2f : 0f;
            text.enableWordWrapping = wrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.color = color;
            text.margin = Vector4.zero;
            text.raycastTarget = false;
            var font = heading ? MenuTheme.HeadingFont : MenuTheme.BodyFont;
            if (font != null)
            {
                text.font = font;
            }

            var material = heading ? MenuTheme.HeadingMaterial : MenuTheme.BodyMaterial;
            if (material != null)
            {
                text.fontSharedMaterial = material;
            }

            return text;
        }

        /// <summary>A vertical stack that takes a share of its row's width.</summary>
        internal static RectTransform Column(Transform parent, string name, float flexibleWidth)
        {
            var rect = CreateRect(name, parent);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 2f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            Flexible(rect.gameObject, flexibleWidth);
            return rect;
        }

        /// <summary>A horizontal run of controls that takes a share of its row's width.</summary>
        internal static RectTransform Strip(Transform parent, string name, float flexibleWidth)
        {
            var rect = CreateRect(name, parent);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            Flexible(rect.gameObject, flexibleWidth);
            return rect;
        }

        // A zero preferred width makes rows split purely by flexible weight, whatever the text length.
        internal static LayoutElement Flexible(GameObject target, float flexibleWidth)
        {
            var element = Element(target);
            element.minWidth = 0f;
            element.preferredWidth = 0f;
            element.flexibleWidth = flexibleWidth;
            return element;
        }

        internal static LayoutElement Fixed(GameObject target, float width, float height)
        {
            var element = Element(target);
            element.minWidth = width;
            element.preferredWidth = width;
            element.flexibleWidth = 0f;
            if (height > 0f)
            {
                element.minHeight = height;
                element.preferredHeight = height;
            }

            return element;
        }

        private static LayoutElement Element(GameObject target)
        {
            var element = target.GetComponent<LayoutElement>();
            return element != null ? element : target.AddComponent<LayoutElement>();
        }

        internal static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>For selectables that only the pointer should reach, such as scrollbars.</summary>
        internal static void NoNavigation(Selectable selectable)
        {
            var navigation = selectable.navigation;
            navigation.mode = Navigation.Mode.None;
            selectable.navigation = navigation;
        }

        /// <summary>
        /// Hover and selection come from the ColorBlock tint, so rows need no pointer triggers. The fill shows at
        /// <paramref name="rest"/> of its strength until the control is hovered or selected.
        /// </summary>
        internal static Button MakeButton(Image target, float rest = 0.45f)
        {
            target.raycastTarget = true;
            var button = target.gameObject.AddComponent<Button>();
            button.targetGraphic = target;
            var colors = button.colors;
            colors.normalColor = new Color(1f, 1f, 1f, rest);
            colors.highlightedColor = Color.white;
            colors.pressedColor = Color.white;
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, rest * 0.5f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            return button;
        }

        /// <summary>A hairline along a control's foot, in place of a fill, to set it apart.</summary>
        internal static Image Underline(RectTransform parent)
        {
            var line = CreateImage("Rule", parent, RuleColor);
            line.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            var rect = line.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(0f, 1f);
            return line;
        }

        internal static Button CreateButton(Transform parent, string text, float width, out MenuLabel label)
        {
            var image = CreateImage("Button_" + text, parent, ButtonColor);
            Fixed(image.gameObject, width, ControlHeight);
            label = MenuLabel.Create(image.transform, "Label", 16f, TextColor, true, false);
            label.Text.alignment = TextAlignmentOptions.Center;
            Stretch(label.Text.rectTransform, 4f, 0f, 4f, 0f);
            label.Set(text);
            return MakeButton(image);
        }

        internal static TMP_InputField CreateInput(Transform parent, bool multiline, string placeholderText)
        {
            var image = CreateImage("Input", parent, InputColor);
            Underline(image.rectTransform);
            image.raycastTarget = true;
            var element = image.gameObject.AddComponent<LayoutElement>();
            // TMP_InputField reports its whole text as its preferred size at priority 1, so a long value or file would
            // size the control; this element outranks it and leaves the size to the layout.
            element.layoutPriority = 2;
            element.minWidth = 0f;
            element.preferredWidth = 0f;
            element.minHeight = ControlHeight;
            element.preferredHeight = ControlHeight;
            var input = image.gameObject.AddComponent<TMP_InputField>();
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.characterLimit = multiline ? 0 : 2048;
            input.customCaretColor = true;
            input.caretColor = CaretColor;
            input.caretWidth = multiline ? 3 : 2;
            input.selectionColor = new Color(1f, 1f, 1f, 0.24f);
            input.onFocusSelectAll = false;
            input.restoreOriginalTextOnEscape = false;
            // Arrow keys and the gamepad pass over fields; Submit or a click starts typing.
            input.shouldActivateOnSelect = false;

            var viewport = CreateRect("TextViewport", image.transform);
            Stretch(viewport, 10f, 5f, 10f, 5f);
            viewport.gameObject.AddComponent<RectMask2D>();
            input.textViewport = viewport;

            var alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            var text = CreateText(viewport, "Text", multiline ? 15f : 17f, TextColor, false, multiline);
            text.alignment = alignment;
            text.overflowMode = multiline ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            Stretch(text.rectTransform, 0f, 2f, 0f, 2f);

            var placeholder = CreateText(viewport, "Placeholder", multiline ? 15f : 17f, MenuKit.Faint, false, multiline);
            placeholder.alignment = alignment;
            placeholder.text = placeholderText ?? "";
            Stretch(placeholder.rectTransform, 0f, 2f, 0f, 2f);

            input.textComponent = text;
            input.placeholder = placeholder;
            return input;
        }

        internal static Slider CreateSlider(Transform parent)
        {
            var root = CreateRect("Slider", parent);
            var element = Flexible(root.gameObject, 1f);
            element.minWidth = 80f;
            element.minHeight = 24f;
            element.preferredHeight = 24f;

            // A thin track, its fill and an upright thumb, as on the Options screen.
            var track = CreateImage("Background", root, MenuKit.BarOff);
            // The track takes clicks so a click jumps the handle there.
            track.raycastTarget = true;
            Stretch(track.rectTransform, 0f, 10f, 0f, 10f);

            var fillArea = CreateRect("Fill Area", root);
            Stretch(fillArea, 0f, 10f, 8f, 10f);
            var fill = CreateImage("Fill", fillArea, MenuKit.Soft);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = new Vector2(8f, 0f);

            var handleArea = CreateRect("Handle Slide Area", root);
            Stretch(handleArea, 8f, 0f, 8f, 0f);
            var handle = CreateImage("Handle", handleArea, MenuKit.Ink);
            handle.raycastTarget = true;
            handle.rectTransform.anchorMin = Vector2.zero;
            handle.rectTransform.anchorMax = new Vector2(0f, 1f);
            handle.rectTransform.sizeDelta = new Vector2(4f, 0f);

            var slider = root.gameObject.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            return slider;
        }
    }
}
