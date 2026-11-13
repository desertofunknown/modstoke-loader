// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// One row of a mod's page, standing for a row the Mods menu built: a setting or config value to change, a link to
    /// another page, a command to run, or a line to read.
    /// </summary>
    internal sealed class PageRow : OptionRow
    {
        private readonly ModPageScreen.Slot _slot;
        private readonly ModPageScreen _screen;

        internal PageRow(ModPageScreen.Slot slot, ModPageScreen screen, string id) : base(id)
        {
            _slot = slot;
            _screen = screen;
            Ready = true;
        }

        private MenuRow Source => _slot.Row;
        private MenuControl Control => Source.Control ?? MenuControl.None;
        private MenuControlKind Kind => Control.Kind;
        private string Current => _slot.Pending ?? Source.Value ?? "";
        private IReadOnlyList<string> Options => Control.Options ?? Array.Empty<string>();

        internal override bool ReadOnly => Kind == MenuControlKind.None || !Control.Editable;

        internal override Action Open => !ReadOnly && Kind is MenuControlKind.Open or MenuControlKind.Run
            ? () => _screen.Activate(_slot)
            : null;

        internal override string Value => Kind switch
        {
            MenuControlKind.Toggle => Text[IsOn ? "options.value.on" : "options.value.off"],
            MenuControlKind.Slider => MenuText.SliderLabel(Control, Current),
            MenuControlKind.Swatches => Index >= 0 ? (Index + 1) + " / " + Options.Count : "—",
            _ => Current.Length > 0 ? Current : Source.Status ?? ""
        };

        internal override string DefaultValue => "";
        internal override bool IsDefault => true;
        internal override bool Reset() => false;

        internal override int Count => Kind switch
        {
            MenuControlKind.Toggle => 2,
            MenuControlKind.Choice or MenuControlKind.Swatches when !ReadOnly => Options.Count,
            _ => 0
        };

        internal override int Index => Kind switch
        {
            MenuControlKind.Toggle => IsOn ? 1 : 0,
            MenuControlKind.Choice => IndexOf(Current),
            MenuControlKind.Swatches => int.TryParse(Current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < Options.Count ? index : -1,
            _ => -1
        };

        internal override float? Fraction
        {
            get
            {
                if (Kind != MenuControlKind.Slider || ReadOnly) return null;
                var (min, max) = Range;
                var value = MenuText.TryParseNumber(Current, out var number) ? number : min;
                return max > min ? (float)Math.Clamp((value - min) / (max - min), 0, 1) : 0f;
            }
        }

        internal override bool Step(int direction, bool wrap)
        {
            if (ReadOnly) return false;
            switch (Kind)
            {
                case MenuControlKind.Toggle:
                {
                    var next = wrap ? !IsOn : direction > 0;
                    return next != IsOn && _screen.Apply(_slot, next ? "true" : "false");
                }
                case MenuControlKind.Choice:
                case MenuControlKind.Swatches:
                {
                    var count = Options.Count;
                    if (count == 0) return false;
                    var index = Index;
                    var next = index < 0 ? (direction > 0 ? 0 : count - 1) : index + direction;
                    next = wrap ? (next % count + count) % count : Mathf.Clamp(next, 0, count - 1);
                    if (next == index) return false;
                    return _screen.Apply(_slot, Kind == MenuControlKind.Choice ? Options[next] : next.ToString(CultureInfo.InvariantCulture));
                }
                case MenuControlKind.Slider:
                    return Hold(MenuText.StepNumber(Control, Current, Current, direction));
                default:
                    return false;
            }
        }

        internal override bool SetFraction(float fraction)
        {
            if (Kind != MenuControlKind.Slider || ReadOnly) return false;
            var (min, max) = Range;
            var value = min + Mathf.Clamp01(fraction) * (max - min);
            var step = Control.Step > 0d ? Control.Step.Value : Control.ValueKind == ConfigValueKinds.Integer ? 1d : 0d;
            if (step > 0d) value = min + Math.Round((value - min) / step) * step;
            return Hold(MenuText.FormatNumber(Control, Math.Clamp(value, min, max)));
        }

        internal override Color? Tint => Source.Tone switch
        {
            MenuTone.Good => MenuKit.Green,
            MenuTone.Warn => MenuKit.Amber,
            MenuTone.Bad => MenuKit.Red,
            _ => null
        };

        internal override string Detail => ShowsDetail(Source) ? Source.Detail : null;

        /// <summary>
        /// A link, a command or a line with a short note of its own shows it under its name; a setting's note is in the
        /// details column, which keeps a long list of settings short.
        /// </summary>
        internal static bool ShowsDetail(MenuRow row)
        {
            var detail = row.Detail;
            if (string.IsNullOrWhiteSpace(detail) || detail.Length > 90) return false;
            return (row.Control ?? MenuControl.None).Kind is MenuControlKind.Open or MenuControlKind.Run or MenuControlKind.None;
        }

        // A colour choice leads with a tile in the chosen colour.
        internal override string Monogram => Kind == MenuControlKind.Swatches ? "" : null;

        internal override Color? MonogramTint => Kind == MenuControlKind.Swatches && Index >= 0 ? Decode(Options[Index]) : null;

        private bool IsOn => LoaderConfig.TryParseBoolean(Current, out var on) && on;

        private (double Min, double Max) Range => (Control.Min ?? 0d, Control.Max ?? 1d);

        private int IndexOf(string value)
        {
            for (var i = 0; i < Options.Count; i++)
                if (string.Equals(Options[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // A slider's value waits until the player lets go, so a drag does not write the setting on every frame.
        private bool Hold(string value)
        {
            if (string.Equals(value, Current, StringComparison.Ordinal)) return false;
            _slot.Pending = value;
            _slot.Held = Time.unscaledTime;
            return true;
        }

        // Swatch options are "r,g,b,a" in 0..1, as the Mods menu encodes them.
        private static Color Decode(string value)
        {
            var parts = (value ?? "").Split(',');
            var channels = new[] { 0f, 0f, 0f, 1f };
            for (var i = 0; i < parts.Length && i < channels.Length; i++)
                if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var channel) && float.IsFinite(channel))
                    channels[i] = Mathf.Clamp01(channel);
            return new Color(channels[0], channels[1], channels[2], channels[3]);
        }
    }

    /// <summary>
    /// A mod's own page, or one of the pages it registers, in place of the Mods menu's: its settings, config values,
    /// forms, commands and links laid out like every other Modstoke screen. The page is the one the Mods menu builds for
    /// the same key, and every change goes through the Mods menu's own handlers. A config file editor and a mod's live
    /// settings and actions still open in the Mods menu, which has the editors for them.
    /// </summary>
    internal sealed class ModPageScreen : SettingsScreen
    {
        private const float RefreshSeconds = 1f, SliderSeconds = 0.35f;
        private static readonly string[] Tabs = { "page" };
        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);
        private readonly string _trail;
        private string _key, _shape;
        private MenuPage _page;
        private float _nextRefresh;
        private bool _reload;

        /// <summary>The Mods menu row a screen row stands for, with a value that is on its way to being applied.</summary>
        internal sealed class Slot
        {
            internal MenuRow Row;
            internal string Pending;
            internal float Held = -1;
        }

        /// <summary>Opens the page with <paramref name="pageKey"/>; throws when the Mods menu cannot build it.</summary>
        internal ModPageScreen(string pageKey, string trail = null)
        {
            _key = pageKey;
            _trail = trail;
            if (!Bind()) throw new InvalidOperationException("The page " + pageKey + " is not available.");
            _page = MenuViewBuilder.BuildPage() ?? throw new InvalidOperationException("The page " + pageKey + " could not be built.");
            _shape = Shape(_page);
        }

        protected override IReadOnlyList<string> TabIds => Tabs;
        protected override string Heading => !string.IsNullOrWhiteSpace(_page.Meta) ? _page.Meta : _trail ?? Text["title.mods"];
        protected override string Title => _page.Title ?? "";
        protected override string TabName(string id) => Title;
        protected override string SectionName(string id) => _sections.TryGetValue(id, out var title) ? title : "";
        protected override bool CanReset => false;

        protected override string ItemName(OptionRow row)
        {
            var source = SourceOf(row);
            if (source == null) return "";
            return !string.IsNullOrWhiteSpace(source.Label) ? source.Label : source.Value ?? "";
        }

        protected override string ItemAbout(OptionRow row)
        {
            var source = SourceOf(row);
            var lines = new List<string>();
            if (source != null)
            {
                if (!string.IsNullOrWhiteSpace(source.Detail)) lines.Add(source.Detail.Trim());
                // A line to read can be longer than its row, so the whole of it is here.
                if (row.ReadOnly && !string.IsNullOrWhiteSpace(source.Value) && source.Value.Length > 24) lines.Add(source.Value.Trim());
                if (!string.IsNullOrWhiteSpace(source.Status) && source.Tone is MenuTone.Warn or MenuTone.Bad)
                    lines.Add("<color=" + MenuKit.Html(source.Tone == MenuTone.Bad ? MenuKit.Red : MenuKit.Amber) + ">" + source.Status + "</color>");
                switch (source.Intent?.Kind)
                {
                    case MenuIntentKind.OpenConfigFile or MenuIntentKind.OpenLiveSettings or MenuIntentKind.OpenModActions:
                        lines.Add(Text["modpage.opensMenu"]);
                        break;
                    case MenuIntentKind.RunCommand when !row.ReadOnly:
                        lines.Add(Text["modpage.run"]);
                        break;
                }
            }
            if (lines.Count == 0 && !string.IsNullOrWhiteSpace(_page.Description)) lines.Add(_page.Description.Trim());
            if (!string.IsNullOrWhiteSpace(_page.FooterStatus))
                lines.Add("<color=" + MenuKit.Html(MenuKit.Muted) + ">" + Text.Get("modpage.last", ("status", _page.FooterStatus.Trim())) + "</color>");
            return string.Join("\n\n", lines);
        }

        // A strip of figures, such as a page's counts, reads as a table.
        protected override IReadOnlyList<(string Label, string Value)> ItemFacts(OptionRow row)
        {
            var stats = SourceOf(row)?.Stats;
            if (stats == null || stats.Count == 0) return null;
            return stats.Select(stat => (stat.Label ?? "", stat.Tone is MenuTone.Warn or MenuTone.Bad
                ? "<color=" + MenuKit.Html(stat.Tone == MenuTone.Bad ? MenuKit.Red : MenuKit.Amber) + ">" + stat.Value + "</color>"
                : stat.Value ?? "")).ToArray();
        }

        protected override OptionTab BuildTab(string id)
        {
            _slots.Clear();
            _sections.Clear();
            var sections = new List<OptionSection>();
            for (var s = 0; s < _page.Sections.Count; s++)
            {
                var section = _page.Sections[s];
                if (section?.Rows == null || section.Rows.Count == 0) continue;
                var sectionId = Unique(section.Key ?? "section:" + s, _sections);
                _sections[sectionId] = section.Title ?? "";
                var rows = new List<OptionRow>();
                for (var r = 0; r < section.Rows.Count; r++)
                {
                    var source = section.Rows[r];
                    if (source == null) continue;
                    var rowId = Unique(source.Key ?? sectionId + "/" + r, _slots);
                    var slot = _slots[rowId] = new Slot { Row = source };
                    rows.Add(Make(slot, rowId));
                }
                sections.Add(new OptionSection(sectionId, rows));
            }
            return new OptionTab(id, sections);
        }

        // Typed values are a text row, everything else a page row.
        private OptionRow Make(Slot slot, string id)
        {
            var control = slot.Row.Control ?? MenuControl.None;
            if (control.Editable && control.Kind is MenuControlKind.TextApply or MenuControlKind.InputLive)
                return new TextRow(id, () => slot.Pending ?? slot.Row.Value ?? "", text => Apply(slot, text ?? ""), null,
                    control.MaxLength > 0 ? control.MaxLength : 512);
            return new PageRow(slot, this, id);
        }

        internal override void Tick()
        {
            base.Tick();
            var now = Time.unscaledTime;
            // A slider is applied once it has been still for a moment and the mouse is no longer holding it.
            foreach (var slot in _slots.Values)
            {
                if (slot.Held < 0 || slot.Pending == null || now - slot.Held < SliderSeconds || FrontInput.Held) continue;
                slot.Held = -1;
                Apply(slot, slot.Pending);
            }
            if (!_reload && now < _nextRefresh && !MenuState.ProviderContentNotified) return;
            _reload = false;
            _nextRefresh = now + RefreshSeconds;
            Reload();
        }

        /// <summary>Changes a setting, config value or form field through the Mods menu; false when it was refused.</summary>
        internal bool Apply(Slot slot, string value)
        {
            if (!Bind()) return false;
            var intent = slot.Row.Intent ?? MenuIntent.None;
            bool applied;
            try
            {
                switch (intent.Kind)
                {
                    case MenuIntentKind.ApplySetting:
                        applied = MenuState.ApplySetting(intent.Scope, intent.Key, value, out _);
                        break;
                    case MenuIntentKind.ApplyConfigValue:
                        applied = MenuState.ApplyConfigValue(intent.ProviderId, intent.Key, value, out _);
                        break;
                    case MenuIntentKind.SetInput:
                        applied = MenuState.SetInput(intent.ProviderId, intent.Scope, intent.FieldId, value);
                        break;
                    default:
                        return false;
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("\"" + slot.Row.Label + "\" could not be changed: " + error.Message);
                applied = false;
            }
            // The new value shows at once; the page is read again on the next frame, after this change has settled.
            slot.Pending = applied ? value : null;
            _reload = true;
            return applied;
        }

        /// <summary>Follows a link, runs a command, or hands a dedicated editor over to the Mods menu.</summary>
        internal void Activate(Slot slot)
        {
            var row = slot.Row;
            var intent = row.Intent ?? MenuIntent.None;
            try
            {
                switch (intent.Kind)
                {
                    case MenuIntentKind.Open:
                        FrontEnd.Push(new ModPageScreen(intent.Key, Title));
                        break;
                    case MenuIntentKind.Expand:
                    case MenuIntentKind.Collapse:
                        MenuState.SetCollapsed(intent.Key, intent.Kind == MenuIntentKind.Collapse);
                        _reload = true;
                        break;
                    case MenuIntentKind.RunCommand:
                        if (!Bind()) return;
                        MenuState.RunCommand(intent.CommandId, out _);
                        _reload = true;
                        break;
                    case MenuIntentKind.OpenConfigFile:
                    case MenuIntentKind.OpenLiveSettings:
                    case MenuIntentKind.OpenModActions:
                        MenuController.ShowHandedOver(_key);
                        break;
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("\"" + row.Label + "\" could not open: " + error.Message);
            }
        }

        // The Mods menu keeps one page open at a time, which another page or the Mods menu itself may have changed since,
        // so this screen's page is put back before it is read or acted on.
        private bool Bind()
        {
            var view = MenuState.ViewState;
            var open = MenuState.ProviderEntry;
            var provider = _key.StartsWith(MenuKeys.ProviderPrefix, StringComparison.OrdinalIgnoreCase);
            var bound = string.Equals(view.LastPage, _key, StringComparison.OrdinalIgnoreCase)
                && (provider ? open != null && string.Equals(MenuKeys.Provider(open.Id), _key, StringComparison.OrdinalIgnoreCase) : open == null);
            if (bound) return true;
            if (!MenuState.Open(_key)) return false;
            _key = view.LastPage;
            return true;
        }

        // The page is read again; a page laid out as before only takes its new values, so focus and scroll stay put.
        private void Reload()
        {
            MenuPage page;
            try
            {
                if (!Bind())
                {
                    // The page went away, as a mod that unregistered it does.
                    FrontEnd.Pop(true);
                    return;
                }
                if (MenuState.IsProviderOpen) MenuState.RefreshProviderSnapshotsIfChanged();
                page = MenuViewBuilder.BuildPage();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The page " + _key + " could not be read again: " + error.Message);
                return;
            }
            if (page == null) return;
            _page = page;
            var shape = Shape(page);
            if (shape != _shape)
            {
                _shape = shape;
                Rebuild();
                return;
            }
            var index = 0;
            var slots = _slots.Values.ToArray();
            foreach (var row in page.Sections.Where(section => section?.Rows != null && section.Rows.Count > 0)
                         .SelectMany(section => section.Rows).Where(row => row != null))
            {
                if (index >= slots.Length) break;
                var slot = slots[index++];
                slot.Row = row;
                // A slider still being moved keeps its place; anything else shows what the mod now holds.
                if (slot.Held < 0) slot.Pending = null;
            }
            Refresh();
        }

        private MenuRow SourceOf(OptionRow row) => row != null && _slots.TryGetValue(row.Id, out var slot) ? slot.Row : null;

        // What decides the layout: the sections, the rows in them, the kind of control each has and whether it has a
        // second line. A chosen swatch is part of it, since its tile is drawn with the row.
        private static string Shape(MenuPage page)
        {
            var shape = new StringBuilder();
            foreach (var section in page.Sections)
            {
                if (section?.Rows == null || section.Rows.Count == 0) continue;
                shape.Append(section.Key).Append('|').Append(section.Title).Append('\n');
                foreach (var row in section.Rows)
                {
                    if (row == null) continue;
                    var control = row.Control ?? MenuControl.None;
                    shape.Append(row.Key).Append('|').Append(control.Kind).Append('|').Append(control.Editable)
                        .Append('|').Append(control.Options?.Count ?? 0).Append('|').Append(PageRow.ShowsDetail(row));
                    if (control.Kind == MenuControlKind.Swatches) shape.Append('|').Append(row.Value);
                    shape.Append('\n');
                }
            }
            return shape.ToString();
        }

        private static string Unique<T>(string id, Dictionary<string, T> taken)
        {
            if (!taken.ContainsKey(id)) return id;
            for (var n = 2; ; n++)
                if (!taken.ContainsKey(id + "#" + n)) return id + "#" + n;
        }
    }
}
