// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// Binds screen panel sections onto the mods menu's pooled row views. Scroll offsets live in a view state of their
    /// own, keyed by owner, so they never mix with the mods menu's pages.
    /// </summary>
    internal sealed class ScreenPanel
    {
        private const string ViewPrefix = "screen:";
        private readonly MenuScroll _scroll;
        private readonly Button _back;
        private readonly PageView _page;
        private readonly MenuViewState _views = new MenuViewState();
        private readonly List<ScreenPanelSection> _sections = new List<ScreenPanelSection>();
        private readonly Dictionary<string, ScreenPanelRow> _rows = new Dictionary<string, ScreenPanelRow>(StringComparer.Ordinal);
        private readonly Dictionary<Selectable, NavLinks> _links = new Dictionary<Selectable, NavLinks>(ReferenceEqualityComparer.Instance);
        private readonly List<Selectable> _stops = new List<Selectable>();
        private readonly List<(int Start, int Count, int Landing, bool Horizontal)> _rowStops = new List<(int, int, int, bool)>();
        private string _viewKey;
        private bool _restorePending;
        private bool _shown;
        private GameObject _lastSelected;

        internal ScreenPanel(MenuScroll scroll, Button back)
        {
            _scroll = scroll;
            _back = back;
            _page = new PageView(scroll.Content, OnIntent, OnLive, false);
        }

        internal void Begin(string ownerId)
        {
            _viewKey = ViewPrefix + ownerId;
            _sections.Clear();
            _rows.Clear();
            Render();
        }

        internal void Show()
        {
            // The first frame after showing has laid the pane out, so the saved offset clamps correctly.
            _restorePending = true;
            _shown = true;
            _lastSelected = null;
            SelectFirst();
        }

        internal void FlushPending()
        {
            _page.FlushPending();
        }

        internal void End()
        {
            // A screen that never opened, or never laid out, would overwrite the owner's last offset with the top.
            if (_viewKey != null && _shown && !_restorePending)
            {
                _scroll.Save(_views, _viewKey);
            }

            _viewKey = null;
            _shown = false;
            _restorePending = false;
            _lastSelected = null;
            _sections.Clear();
            _rows.Clear();
            // Rows go back to the pool; the host has already flushed a waiting slider.
            _page.Clear();
        }

        internal void Bind(IEnumerable<ScreenPanelSection> sections)
        {
            _sections.Clear();
            _rows.Clear();
            if (sections != null)
            {
                foreach (var section in sections)
                {
                    if (section == null)
                    {
                        continue;
                    }

                    _sections.Add(section);
                    foreach (var row in section.Rows)
                    {
                        if (row.Id != null && !_rows.ContainsKey(row.Id))
                        {
                            _rows[row.Id] = row;
                        }
                    }
                }
            }

            Render();
        }

        internal bool TrySetValue(string rowId, string value)
        {
            if (string.IsNullOrWhiteSpace(rowId) || !_rows.TryGetValue(rowId.Trim(), out var row) || !Apply(row, value))
            {
                return false;
            }

            Render();
            return true;
        }

        internal void Tick(float now)
        {
            _page.Tick(now);
            if (_restorePending)
            {
                _restorePending = false;
                if (_viewKey != null)
                {
                    _scroll.Restore(_views, _viewKey);
                }
            }

            PollSelection();
        }

        private void Render()
        {
            if (_viewKey == null)
            {
                return;
            }

            _page.Bind(BuildPage());
            Relink();
            EnsureSelection();
        }

        // Header rows split a section, so each heading gets the menu's own section header view.
        private MenuPage BuildPage()
        {
            var sections = new List<MenuSection>();
            var rows = new List<MenuRow>();
            for (var s = 0; s < _sections.Count; s++)
            {
                var title = _sections[s].Title;
                var key = "section:" + s.ToString(CultureInfo.InvariantCulture);
                var part = 0;
                foreach (var row in _sections[s].Rows)
                {
                    if (row.Kind != ScreenPanelRowKind.Header)
                    {
                        rows.Add(ToMenuRow(row));
                        continue;
                    }

                    Flush(sections, rows, key, title);
                    title = row.Label;
                    key = "section:" + s.ToString(CultureInfo.InvariantCulture) + "." + (++part).ToString(CultureInfo.InvariantCulture);
                }

                Flush(sections, rows, key, title);
            }

            return new MenuPage { Key = _viewKey, Sections = sections };
        }

        private static void Flush(List<MenuSection> sections, List<MenuRow> rows, string key, string title)
        {
            if (rows.Count > 0 || !string.IsNullOrWhiteSpace(title))
            {
                sections.Add(new MenuSection { Key = key, Title = title, Rows = rows.ToArray() });
            }

            rows.Clear();
        }

        private static MenuRow ToMenuRow(ScreenPanelRow row)
        {
            string value;
            switch (row.Kind)
            {
                case ScreenPanelRowKind.Slider:
                    value = MenuText.FormatNumber(row.Control, row.Number);
                    break;
                case ScreenPanelRowKind.Choice:
                    value = row.Options[row.Index];
                    break;
                case ScreenPanelRowKind.Toggle:
                    value = row.On ? "true" : "false";
                    break;
                case ScreenPanelRowKind.Swatches:
                    value = row.Index.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    return new MenuRow { Key = row.Id, Kind = MenuRowKind.Info, Label = row.Label, Tooltip = row.Label, Control = row.Control };
            }

            return new MenuRow { Key = row.Id, Kind = MenuRowKind.Setting, Label = row.Label, Value = value, Tooltip = row.Label, Control = row.Control };
        }

        private static bool Apply(ScreenPanelRow row, string value)
        {
            switch (row.Kind)
            {
                case ScreenPanelRowKind.Slider:
                    if (!MenuText.TryParseNumber(value, out var number))
                    {
                        return false;
                    }

                    row.Number = Clamp(row, number);
                    return true;
                case ScreenPanelRowKind.Choice:
                    var choice = IndexOf(row, value);
                    if (choice < 0)
                    {
                        return false;
                    }

                    row.Index = choice;
                    return true;
                case ScreenPanelRowKind.Toggle:
                    if (!LoaderConfig.TryParseBoolean(value, out var on))
                    {
                        return false;
                    }

                    row.On = on;
                    return true;
                case ScreenPanelRowKind.Swatches:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var swatch) || swatch < -1 || swatch >= row.Options.Count)
                    {
                        return false;
                    }

                    row.Index = swatch;
                    return true;
                case ScreenPanelRowKind.Button:
                    row.Label = value ?? "";
                    return true;
                default:
                    return false;
            }
        }

        private bool OnIntent(MenuRow menuRow, string value)
        {
            if (menuRow?.Key == null || !_rows.TryGetValue(menuRow.Key, out var row))
            {
                return false;
            }

            switch (row.Kind)
            {
                case ScreenPanelRowKind.Slider:
                    if (!MenuText.TryParseNumber(value, out var number))
                    {
                        return false;
                    }

                    row.Number = Clamp(row, number);
                    Run(row, () => row.Commit?.Invoke(row.Number));
                    break;
                case ScreenPanelRowKind.Choice:
                    var choice = IndexOf(row, value);
                    if (choice < 0)
                    {
                        return false;
                    }

                    row.Index = choice;
                    Run(row, () => row.IndexChanged?.Invoke(choice));
                    break;
                case ScreenPanelRowKind.Toggle:
                    if (!LoaderConfig.TryParseBoolean(value, out var on))
                    {
                        return false;
                    }

                    row.On = on;
                    Run(row, () => row.ToggleChanged?.Invoke(on));
                    break;
                case ScreenPanelRowKind.Swatches:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var swatch) || swatch < 0 || swatch >= row.Options.Count)
                    {
                        return false;
                    }

                    row.Index = swatch;
                    Run(row, () => row.IndexChanged?.Invoke(swatch));
                    break;
                case ScreenPanelRowKind.Button:
                    Run(row, () => row.Clicked?.Invoke());
                    break;
            }

            // The handler may have closed the screen or replaced the rows.
            Render();
            return true;
        }

        private void OnLive(MenuRow menuRow, string value)
        {
            if (menuRow?.Key == null || !_rows.TryGetValue(menuRow.Key, out var row) || row.Kind != ScreenPanelRowKind.Slider
                || !MenuText.TryParseNumber(value, out var number))
            {
                return;
            }

            row.Number = Clamp(row, number);
            if (row.Live != null)
            {
                Run(row, () => row.Live(row.Number));
            }
        }

        private static void Run(ScreenPanelRow row, Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen panel row " + row.Id + " failed: " + error);
                ScreenHostView.SetStatus((string.IsNullOrWhiteSpace(row.Label) ? row.Id : row.Label) + " failed: " + error.Message);
            }
        }

        private static float Clamp(ScreenPanelRow row, double number)
        {
            return (float)Math.Clamp(number, row.Control.Min ?? 0d, row.Control.Max ?? 1d);
        }

        private static int IndexOf(ScreenPanelRow row, string value)
        {
            var options = row.Options;
            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < options.Count ? index : -1;
        }

        /// <summary>Explicit navigation, so arrow keys and the gamepad never reach the pause menu underneath.</summary>
        private void Relink()
        {
            _stops.Clear();
            _rowStops.Clear();
            foreach (var view in _page.Views)
            {
                var start = _stops.Count;
                view.CollectSelectables(_stops);
                if (_stops.Count == start)
                {
                    continue;
                }

                var landing = start;
                for (var i = start; i < _stops.Count; i++)
                {
                    if (ReferenceEquals(_stops[i], view.Primary))
                    {
                        landing = i;
                        break;
                    }
                }

                _rowStops.Add((start, _stops.Count - start, landing, view.UsesHorizontal));
            }

            for (var r = 0; r < _rowStops.Count; r++)
            {
                var row = _rowStops[r];
                var up = r == 0 ? null : _stops[_rowStops[r - 1].Landing];
                var down = r == _rowStops.Count - 1 ? _back : _stops[_rowStops[r + 1].Landing];
                for (var k = 0; k < row.Count; k++)
                {
                    var index = row.Start + k;
                    // A slider steps its value with Left and Right, which it only does while they lead nowhere.
                    Link(_stops[index], up, down,
                        row.Horizontal || k == 0 ? null : _stops[index - 1],
                        row.Horizontal || k == row.Count - 1 ? null : _stops[index + 1]);
                }
            }

            Link(_back, _rowStops.Count > 0 ? _stops[_rowStops[_rowStops.Count - 1].Landing] : null, null, null, null);
        }

        private void Link(Selectable selectable, Selectable up, Selectable down, Selectable left, Selectable right)
        {
            if (selectable == null)
            {
                return;
            }

            var links = new NavLinks(up, down, left, right);
            if (_links.TryGetValue(selectable, out var current) && current.Same(links))
            {
                return;
            }

            _links[selectable] = links;
            var navigation = selectable.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnUp = up;
            navigation.selectOnDown = down;
            navigation.selectOnLeft = left;
            navigation.selectOnRight = right;
            selectable.navigation = navigation;
        }

        private void SelectFirst()
        {
            var target = _rowStops.Count > 0 ? _stops[_rowStops[0].Landing] : _back;
            if (target != null && target.gameObject.activeInHierarchy)
            {
                target.Select();
                _lastSelected = target.gameObject;
            }
        }

        // A row that went back to the pool leaves the selection on a hidden object, which strands the keyboard.
        private void EnsureSelection()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            if (selected != null && !selected.activeInHierarchy && ScreenHostView.Contains(selected))
            {
                SelectFirst();
            }
        }

        private void PollSelection()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            if (selected == _lastSelected)
            {
                return;
            }

            _lastSelected = selected;
            // A press selects on the way down, so scrolling then would move the control out from under the release.
            var view = selected != null && !Input.GetMouseButton(0) ? _page.FindView(selected) : null;
            if (view != null)
            {
                _scroll.ScrollIntoView(view.Root);
            }
        }

        private readonly struct NavLinks
        {
            private readonly Selectable _up;
            private readonly Selectable _down;
            private readonly Selectable _left;
            private readonly Selectable _right;

            internal NavLinks(Selectable up, Selectable down, Selectable left, Selectable right)
            {
                _up = up;
                _down = down;
                _left = left;
                _right = right;
            }

            internal bool Same(NavLinks other)
            {
                return ReferenceEquals(_up, other._up) && ReferenceEquals(_down, other._down)
                    && ReferenceEquals(_left, other._left) && ReferenceEquals(_right, other._right);
            }
        }
    }
}
