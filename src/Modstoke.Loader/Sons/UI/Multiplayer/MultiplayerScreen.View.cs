// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal static partial class MultiplayerScreen
    {
        // Layout in design units: the screen is drawn as if 860 units tall and scaled to the real height.
        private const float PanelTop = 120, RowsTop = 150, RowHeight = 48, RowGap = 2, TagHeight = 24, FooterHeight = 44;

        private sealed class RowView
        {
            internal RectTransform Rect;
            internal Image Highlight, Marker;
            internal TextMeshProUGUI Name, Kind, Status, Players, Ping;
            internal Image[] Bars;
        }

        private sealed class ModView
        {
            internal RectTransform Rect;
            internal TextMeshProUGUI Name, Server, Yours;
        }

        private sealed class TabView
        {
            internal RectTransform Rect;
            internal Image Brush;
            internal TextMeshProUGUI Label;
            internal float Width;
            internal bool Hovered;
        }

        // A filter chip or a sortable column header.
        private sealed class ToolView
        {
            internal RectTransform Rect, Chevron;
            internal Image Fill, Focus;
            internal TextMeshProUGUI Label;
            internal bool Hovered;
        }

        private sealed class Prompt
        {
            internal RectTransform Rect;
            internal (RectTransform Rect, TextMeshProUGUI Label) Tag;
            internal TextMeshProUGUI Label;
            // The button or key shown for each device; null hides the prompt on that device.
            internal string Pad, Key;
            internal bool Right;
        }

        private static RowView[] _rows = Array.Empty<RowView>();
        private static ModView[] _mods = Array.Empty<ModView>();
        private static readonly TabView[] TabViews = new TabView[4];
        private static readonly ToolView[] ChipViews = new ToolView[4];
        private static readonly ToolView[] HeaderViews = new ToolView[4];
        private static readonly List<Prompt> Prompts = new();
        private static readonly TextMeshProUGUI[] StatValues = new TextMeshProUGUI[3];
        private static RectTransform _listRect, _nav, _modsHeader;
        private static (RectTransform Rect, TextMeshProUGUI Label) _tabLeft, _tabRight, _navLeft, _navRight, _navJoin, _navHost;
        private static TextMeshProUGUI _status, _empty, _kind, _name, _sub, _noticeTitle, _noticeText, _joinLabel, _searchText;
        private static Image _noticeBox, _noticeBar, _joinFill, _searchBox;
        private static float _bottom;
        private static int _modstokeCount;
        private static bool _caretOn;

        private static float U(float design) => design * _s;

        private static void Build(RectTransform parent)
        {
            var root = MenuKit.Box(parent, "ModstokeMultiplayer").Place(0, 0, 1, 1);
            root.SetAsLastSibling();
            _rootObject = root.gameObject;
            // Its own canvas, so repainting the list never rebuilds the game's menu canvas.
            _canvas = _rootObject.AddComponent<Canvas>();
            var height = parent.rect.height;
            _s = height > 1 ? height / DesignHeight : 1f;
            _bottom = BottomInset(parent);
            Hots.Clear();
            Prompts.Clear();
            var pad = U(60);
            BuildHeader(root, pad);
            BuildList(root, pad);
            BuildDetails(root, pad);
            BuildFooter(root, pad);
            PaintHints();
        }

        // The game's own panels end where its bottom strip's artwork begins; this screen ends on the same line.
        private static float BottomInset(RectTransform parent)
        {
            try
            {
                var layout = _gameList != null && _gameList ? _gameList.GetComponent<RectTransform>() : null;
                if (layout != null)
                {
                    var corners = new Il2CppStructArray<Vector3>(4);
                    layout.GetWorldCorners(corners);
                    var inset = parent.InverseTransformPoint(corners[0]).y - parent.rect.yMin;
                    if (inset > U(110) && inset < U(220)) return inset;
                }
            }
            catch (Exception)
            {
                // Falls back to the strip's usual height below.
            }
            return U(143);
        }

        private static void BuildHeader(RectTransform root, float pad)
        {
            var section = MenuKit.Text(root, "Section", "MULTIPLAYER", U(14), true, MenuKit.Muted);
            section.characterSpacing = 32;
            section.rectTransform.At(pad, U(30), U(600), U(18));
            var title = MenuKit.Text(root, "Title", "JOIN A GAME", U(50), true, MenuKit.Ink);
            title.characterSpacing = 5;
            title.rectTransform.At(pad, U(46), U(900), U(66));

            _nav = MenuKit.Box(root, "Nav");
            _nav.anchorMin = _nav.anchorMax = _nav.pivot = new Vector2(1, 1);
            _nav.anchoredPosition = new Vector2(-pad, -U(56));
            _navLeft = MenuKit.Tag(_nav, "Previous", "LT", U(TagHeight));
            _navJoin = NavItem("JOIN", true);
            _navHost = NavItem("HOST", false);
            _navRight = MenuKit.Tag(_nav, "Next", "RT", U(TagHeight));
            Add(_navHost.Rect, Host, on => _navHost.Label.color = on ? MenuKit.Ink : MenuKit.Muted);
        }

        private static (RectTransform Rect, TextMeshProUGUI Label) NavItem(string text, bool current)
        {
            var rect = MenuKit.Box(_nav, text);
            if (current) MenuKit.Fill(rect, MenuKit.Brush, MenuKit.Ink);
            var label = MenuKit.Text(rect, "Label", text, U(21), true, current ? MenuKit.Dark : MenuKit.Muted, TextAlignmentOptions.Center);
            label.characterSpacing = 14;
            label.rectTransform.Place(0, 0, 1, 1);
            rect.sizeDelta = new Vector2(label.GetPreferredValues(text).x + U(current ? 68 : 44), U(48));
            return (rect, label);
        }

        private static void BuildList(RectTransform root, float pad)
        {
            var panel = MenuKit.PanelBox(root, "ServerList").rectTransform.Place(0, 0, 1, 1, pad, _bottom, pad + U(464), U(PanelTop));
            _listRect = panel;
            var panelHeight = root.rect.height - _bottom - U(PanelTop);

            _tabLeft = MenuKit.Tag(panel, "PreviousTab", "LB", U(TagHeight));
            for (var i = 0; i < TabNames.Length; i++)
            {
                var tab = (Tab)i;
                var view = new TabView { Rect = MenuKit.Box(panel, "Tab " + TabNames[i]) };
                view.Brush = MenuKit.Fill(MenuKit.Box(view.Rect, "Brush").Place(0, 0, 1, 1), MenuKit.Brush, MenuKit.Ink);
                view.Label = MenuKit.Text(view.Rect, "Label", TabNames[i], U(15), true, MenuKit.Muted, TextAlignmentOptions.Center);
                view.Label.characterSpacing = 12;
                view.Label.rectTransform.Place(0, 0, 1, 1);
                view.Width = view.Label.GetPreferredValues(TabNames[i] + " 0000").x + U(24);
                TabViews[i] = view;
                Add(view.Rect, () => SetTab(tab), on =>
                {
                    view.Hovered = on;
                    PaintTabs();
                });
            }
            _tabRight = MenuKit.Tag(panel, "NextTab", "RB", U(TagHeight));

            var search = MenuKit.Box(panel, "Search").AtRight(U(28), U(20), U(300), U(40));
            _searchBox = MenuKit.Fill(search, null, MenuKit.Field);
            _searchText = MenuKit.Text(search, "Text", "", U(16), false, MenuKit.Faint, TextAlignmentOptions.MidlineLeft, true);
            _searchText.rectTransform.Place(0, 0, 1, 1, U(14), 0, U(14), 0);
            _searchHot = Add(search, () => FocusSearch(true));

            MenuKit.Fill(MenuKit.Box(panel, "Rule").Strip(U(70), U(2), U(28), U(28)), MenuKit.Brush, MenuKit.Rule);

            var x = U(28);
            for (var i = 0; i < ChipNames.Length; i++)
            {
                var index = i;
                var view = new ToolView { Rect = MenuKit.Box(panel, "Filter " + ChipNames[i]) };
                view.Fill = MenuKit.Fill(view.Rect, null, MenuKit.ChipOff);
                view.Label = MenuKit.Text(view.Rect, "Label", ChipNames[i], U(12.5f), true, MenuKit.Soft, TextAlignmentOptions.Center);
                view.Label.characterSpacing = 10;
                view.Label.rectTransform.Place(0, 0, 1, 1);
                view.Focus = MenuKit.Fill(MenuKit.Box(view.Rect, "Focus").Place(0, 0, 1, 0, 0, -U(5), 0, U(3)), null, MenuKit.Ink);
                var width = view.Label.GetPreferredValues(ChipNames[i]).x + U(26);
                view.Rect.At(x, U(80), width, U(30));
                x += width + U(6);
                ChipViews[i] = view;
                Add(view.Rect, () => ToggleChip(index), on =>
                {
                    view.Hovered = on;
                    PaintTools();
                });
            }

            var header = MenuKit.Box(panel, "Columns").Strip(U(118), U(26), U(28), U(28));
            HeaderViews[0] = Header(header, 0, "SERVER", false, U(24), U(382));
            HeaderViews[1] = Header(header, 1, "MODS", true, -U(370), U(170));
            HeaderViews[2] = Header(header, 2, "PLAYERS", true, -U(158), U(76));
            HeaderViews[3] = Header(header, 3, "PING", true, -U(70), 0);

            var visible = Mathf.Clamp(Mathf.FloorToInt((panelHeight - U(RowsTop) - U(40)) / U(RowHeight + RowGap)), 3, 14);
            _rows = new RowView[visible];
            for (var i = 0; i < visible; i++) _rows[i] = Row(panel, i);

            _empty = MenuKit.Text(panel, "Empty", "", U(16), false, MenuKit.Muted, TextAlignmentOptions.Center);
            _empty.rectTransform.Strip(U(RowsTop + 60), U(26), U(28), U(28));

            _status = MenuKit.Text(panel, "Status", "", U(14), false, MenuKit.Muted);
            _status.rectTransform.Place(0, 0, 1, 0, U(28), U(10), U(260), -U(32));
            var classic = MenuKit.Box(panel, "Classic").Place(1, 0, 1, 0, -U(250), U(8), U(28), -U(36));
            var classicLabel = MenuKit.Text(classic, "Label", "GAME'S OWN BROWSER", U(12), true, MenuKit.Muted, TextAlignmentOptions.MidlineRight);
            classicLabel.characterSpacing = 12;
            classicLabel.rectTransform.Place(0, 0, 1, 1);
            Add(classic, MultiplayerTakeover.UseClassicBrowser, on => classicLabel.color = on ? MenuKit.Ink : MenuKit.Muted);
        }

        private static ToolView Header(RectTransform header, int index, string text, bool fromRight, float left, float right)
        {
            var view = new ToolView { Rect = MenuKit.Box(header, text) };
            if (fromRight) view.Rect.Place(1, 0, 1, 1, left, 0, right, 0);
            else view.Rect.Place(0, 0, 1, 1, left, 0, right, 0);
            view.Label = MenuKit.Text(view.Rect, "Label", text, U(12), true, MenuKit.Muted);
            view.Label.characterSpacing = 16;
            view.Label.rectTransform.Place(0, 0, 1, 1);
            var width = view.Label.GetPreferredValues(text).x;
            view.Chevron = MenuKit.Chevron(view.Rect, "Sort", U(9), MenuKit.Ink);
            view.Chevron.anchorMin = view.Chevron.anchorMax = new Vector2(0, 0.5f);
            view.Chevron.pivot = new Vector2(0.5f, 0.5f);
            view.Chevron.anchoredPosition = new Vector2(width + U(10), 0);
            view.Focus = MenuKit.Fill(MenuKit.Box(view.Rect, "Focus").At(0, U(23), width, U(2)), null, MenuKit.Ink);
            Add(view.Rect, () => ToggleSort((SortKey)(index + 1)), on =>
            {
                view.Hovered = on;
                PaintTools();
            });
            return view;
        }

        private static RowView Row(RectTransform panel, int slot)
        {
            var rect = MenuKit.Box(panel, "Row " + slot).Strip(U(RowsTop + slot * (RowHeight + RowGap)), U(RowHeight), U(28), U(28));
            var view = new RowView { Rect = rect };
            view.Highlight = MenuKit.Fill(MenuKit.Box(rect, "Highlight").Place(0, 0, 1, 1), MenuKit.Brush, MenuKit.RowSelected);
            view.Marker = MenuKit.Fill(MenuKit.Box(rect, "Marker").Place(0, 0, 0, 1, U(10), U(11), -U(13), U(11)), null, MenuKit.Faint);
            view.Name = MenuKit.Text(rect, "Name", "", U(17), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            view.Name.rectTransform.Place(0, 0.5f, 1, 1, U(24), -U(2), U(382), U(3));
            view.Kind = MenuKit.Text(rect, "Kind", "", U(13), false, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
            view.Kind.rectTransform.Place(0, 0, 1, 0.5f, U(24), U(4), U(382), U(2));
            view.Status = MenuKit.Text(rect, "Status", "", U(15), true, MenuKit.Faint);
            view.Status.rectTransform.Place(1, 0, 1, 1, -U(370), 0, U(170), 0);
            view.Players = MenuKit.Text(rect, "Players", "", U(16), false, MenuKit.Ink);
            view.Players.rectTransform.Place(1, 0, 1, 1, -U(158), 0, U(76), 0);
            var ping = MenuKit.Box(rect, "Ping").Place(1, 0, 1, 1, -U(70), 0, 0, 0);
            view.Bars = new Image[3];
            for (var b = 0; b < 3; b++)
            {
                var barHeight = U(6 + b * 4);
                view.Bars[b] = MenuKit.Fill(MenuKit.Box(ping, "Bar " + b).At(U(b * 6), U(RowHeight / 2 + 7) - barHeight, U(4), barHeight), null, MenuKit.Ink);
            }
            view.Ping = MenuKit.Text(ping, "Value", "", U(14), false, MenuKit.Muted);
            view.Ping.rectTransform.At(U(24), U(RowHeight / 2 - 10), U(46), U(20));
            Add(rect, () => OnRowClicked(slot), on =>
            {
                if (on) _hoverRow = slot;
                else if (_hoverRow == slot) _hoverRow = -1;
                PaintHighlights();
            });
            return view;
        }

        private static void BuildDetails(RectTransform root, float pad)
        {
            var panel = MenuKit.PanelBox(root, "Details").rectTransform.Place(1, 0, 1, 1, -(pad + U(440)), _bottom, pad, U(PanelTop));
            var panelHeight = root.rect.height - _bottom - U(PanelTop);
            _kind = MenuKit.Text(panel, "Kind", "", U(13), true, MenuKit.Muted);
            _kind.characterSpacing = 18;
            _kind.rectTransform.Strip(U(22), U(18), U(28), U(28));
            _name = MenuKit.Text(panel, "Name", "", U(30), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _name.rectTransform.Strip(U(42), U(44), U(28), U(28));
            _sub = MenuKit.Text(panel, "Address", "", U(15), false, MenuKit.Soft);
            _sub.rectTransform.Strip(U(86), U(22), U(28), U(28));

            var stats = MenuKit.Box(panel, "Stats").Strip(U(118), U(58), U(28), U(28));
            var names = new[] { "PLAYERS", "PING", "MODSTOKE" };
            for (var i = 0; i < 3; i++)
            {
                var cell = MenuKit.Box(stats, names[i]).Place(i / 3f, 0, (i + 1) / 3f, 1, i == 0 ? 0 : U(1), 0, i == 2 ? 0 : U(1), 0);
                MenuKit.Fill(cell, null, MenuKit.Cell);
                var label = MenuKit.Text(cell, "Label", names[i], U(11), true, MenuKit.Muted);
                label.characterSpacing = 14;
                label.rectTransform.Strip(U(8), U(16), U(10), U(4));
                StatValues[i] = MenuKit.Text(cell, "Value", "", U(18), true, MenuKit.Ink);
                StatValues[i].rectTransform.Strip(U(26), U(26), U(10), U(4));
            }

            var notice = MenuKit.Box(panel, "Notice").Strip(U(188), U(92), U(28), U(28));
            _noticeBox = MenuKit.Fill(notice, null, MenuKit.Cell);
            _noticeBar = MenuKit.Fill(MenuKit.Box(notice, "Bar").Place(0, 0, 0, 1, 0, 0, -U(4), 0), null, MenuKit.Faint);
            _noticeTitle = MenuKit.Text(notice, "Title", "", U(17), true, MenuKit.Ink);
            _noticeTitle.rectTransform.Strip(U(10), U(24), U(18), U(12));
            _noticeText = MenuKit.Text(notice, "Text", "", U(14.5f), false, MenuKit.Soft, TextAlignmentOptions.TopLeft);
            _noticeText.enableWordWrapping = true;
            _noticeText.overflowMode = TextOverflowModes.Truncate;
            _noticeText.rectTransform.Strip(U(36), U(50), U(18), U(12));

            _modsHeader = MenuKit.Box(panel, "ModColumns").Strip(U(294), U(18), U(28), U(28));
            Column(_modsHeader, "MOD", false, 0, U(190));
            Column(_modsHeader, "SERVER", true, -U(186), U(102));
            Column(_modsHeader, "YOURS", true, -U(98), 0);
            // As many mod rows as fit between the column header and the Join button.
            var modRows = Mathf.Clamp(Mathf.FloorToInt((panelHeight - U(316) - U(92)) / U(38)), 1, 10);
            _mods = new ModView[modRows];
            for (var i = 0; i < modRows; i++)
            {
                var rect = MenuKit.Box(panel, "Mod " + i).Strip(U(316 + i * 38), U(36), U(28), U(28));
                MenuKit.Fill(MenuKit.Box(rect, "Line").Place(0, 0, 1, 0, 0, 0, 0, -U(1)), null, MenuKit.Line);
                var view = new ModView { Rect = rect };
                view.Name = MenuKit.Text(rect, "Name", "", U(16), false, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
                view.Name.rectTransform.Place(0, 0, 1, 1, 0, 0, U(190), 0);
                view.Server = MenuKit.Text(rect, "Server", "", U(15), false, MenuKit.Soft);
                view.Server.rectTransform.Place(1, 0, 1, 1, -U(186), 0, U(102), 0);
                view.Yours = MenuKit.Text(rect, "Yours", "", U(15), true, MenuKit.Ink);
                view.Yours.rectTransform.Place(1, 0, 1, 1, -U(98), 0, 0, 0);
                _mods[i] = view;
            }

            var join = MenuKit.Box(panel, "Join").Place(0, 0, 1, 0, U(28), U(20), U(28), -U(78));
            _joinFill = MenuKit.Fill(join, MenuKit.Brush, MenuKit.Ink);
            _joinLabel = MenuKit.Text(join, "Label", "JOIN", U(21), true, MenuKit.Dark, TextAlignmentOptions.Center);
            _joinLabel.characterSpacing = 16;
            _joinLabel.rectTransform.Place(0, 0, 1, 1);
            Add(join, Join);
        }

        private static void Column(RectTransform header, string text, bool fromRight, float left, float right)
        {
            var label = MenuKit.Text(header, text, text, U(12), true, MenuKit.Muted);
            label.characterSpacing = 16;
            if (fromRight) label.rectTransform.Place(1, 0, 1, 1, left, 0, right, 0);
            else label.rectTransform.Place(0, 0, 1, 1, left, 0, right, 0);
        }

        // The prompt bar over the game's bottom strip artwork: what each button or key does on this screen.
        private static void BuildFooter(RectTransform root, float pad)
        {
            var footer = MenuKit.Box(root, "Prompts").Place(0, 0, 1, 0, pad, U(30), pad, -U(30 + FooterHeight));
            AddPrompt(footer, "BACK", "B", "ESC", false, () => BackOut());
            AddPrompt(footer, "JOIN BY IP", "Y", "F2", false, JoinByIp);
            AddPrompt(footer, "SEARCH", null, "F3", false, () => FocusSearch(true));
            AddPrompt(footer, "JOIN", "A", "ENTER", true, Activate);
            AddPrompt(footer, "REFRESH", "X", "F5", true, Refresh);
        }

        private static void AddPrompt(RectTransform footer, string text, string pad, string key, bool right, Action click)
        {
            var prompt = new Prompt { Rect = MenuKit.Box(footer, text), Pad = pad, Key = key, Right = right };
            prompt.Tag = MenuKit.Tag(prompt.Rect, "Key", key ?? pad ?? "", U(TagHeight));
            prompt.Label = MenuKit.Text(prompt.Rect, "Label", text, U(19), true, MenuKit.Soft);
            prompt.Label.characterSpacing = 14;
            Prompts.Add(prompt);
            Add(prompt.Rect, click, on => prompt.Label.color = on ? MenuKit.Ink : MenuKit.Soft);
        }

        // Shows controller buttons while a controller is the device in use, keys otherwise.
        private static void PaintHints()
        {
            if (_rootObject == null || !_rootObject) return;
            SetTag(_tabLeft, _pad ? "LB" : "Q");
            SetTag(_tabRight, _pad ? "RB" : "E");
            SetTag(_navLeft, _pad ? "LT" : "");
            SetTag(_navRight, _pad ? "RT" : "TAB");
            foreach (var prompt in Prompts)
            {
                var text = _pad ? prompt.Pad : prompt.Key;
                prompt.Rect.gameObject.SetActive(text != null);
                if (text != null) SetTag(prompt.Tag, text);
            }

            var x = U(28);
            foreach (var item in new[] { _tabLeft.Rect, TabViews[0].Rect, TabViews[1].Rect, TabViews[2].Rect, TabViews[3].Rect, _tabRight.Rect })
            {
                var tab = Array.Find(TabViews, view => view.Rect == item);
                var width = tab != null ? tab.Width : item.sizeDelta.x;
                var height = tab != null ? U(44) : U(TagHeight);
                item.At(x, U(18) + (U(44) - height) / 2, width, height);
                x += width + U(tab != null ? 2 : 6);
            }

            x = 0;
            foreach (var item in new[] { _navLeft.Rect, _navJoin.Rect, _navHost.Rect, _navRight.Rect })
            {
                if (!item.gameObject.activeSelf) continue;
                var size = item.sizeDelta;
                item.At(x, (U(48) - size.y) / 2, size.x, size.y);
                x += size.x + U(6);
            }
            _nav.sizeDelta = new Vector2(Mathf.Max(0, x - U(6)), U(48));

            float left = 0, right = 0;
            foreach (var prompt in Prompts)
            {
                if (!prompt.Rect.gameObject.activeSelf) continue;
                var tagWidth = prompt.Tag.Rect.sizeDelta.x;
                var labelWidth = prompt.Label.GetPreferredValues(prompt.Label.text).x + U(4);
                var width = tagWidth + U(10) + labelWidth;
                prompt.Tag.Rect.At(0, (U(FooterHeight) - U(TagHeight)) / 2, tagWidth, U(TagHeight));
                prompt.Label.rectTransform.At(tagWidth + U(10), 0, labelWidth, U(FooterHeight));
                if (prompt.Right)
                {
                    prompt.Rect.AtRight(right, 0, width, U(FooterHeight));
                    right += width + U(38);
                }
                else
                {
                    prompt.Rect.At(left, 0, width, U(FooterHeight));
                    left += width + U(38);
                }
            }
        }

        private static void SetTag((RectTransform Rect, TextMeshProUGUI Label) tag, string text)
        {
            tag.Rect.gameObject.SetActive(text.Length > 0);
            if (text.Length == 0) return;
            tag.Label.text = text;
            tag.Rect.sizeDelta = new Vector2(MenuKit.TagWidth(tag.Label, text, U(TagHeight)), U(TagHeight));
        }

        private static void Paint()
        {
            if (_rootObject == null || !_rootObject) return;
            PaintTabs();
            PaintTools();
            PaintSearch();
            for (var i = 0; i < _rows.Length; i++) PaintRow(_rows[i], _top + i);
            PaintHighlights();
            PaintDetails();

            var refreshing = false;
            try { refreshing = _gui._dedicatedBrowser.RefreshInProgress || _gui._lobbyBrowser.RefreshInProgress; }
            catch (Exception) { /* The browsers report nothing between refreshes. */ }
            var filtered = Entries.Count != Listed.Count;
            _empty.text = Entries.Count > 0 ? ""
                : filtered ? "No servers match your search and filters."
                : refreshing ? "Looking for servers…"
                : _tab == Tab.Lan ? "No servers on your network." : "No servers found.";
            _status.text = (filtered ? Entries.Count.ToString("N0") + " of " + Listed.Count.ToString("N0") : Listed.Count.ToString("N0"))
                + (Listed.Count == 1 ? " server" : " servers")
                + (_modstokeCount > 0 ? " · " + _modstokeCount + " running Modstoke" : "")
                + (refreshing ? " · refreshing" : "");
        }

        private static void PaintTabs()
        {
            for (var i = 0; i < TabViews.Length; i++)
            {
                var view = TabViews[i];
                var on = i == (int)_tab;
                view.Brush.enabled = on;
                view.Label.color = on ? MenuKit.Dark : view.Hovered ? MenuKit.Ink : MenuKit.Muted;
                view.Label.text = on && Listed.Count > 0
                    ? TabNames[i] + " <color=" + MenuKit.Html(MenuKit.Faint) + ">" + Listed.Count + "</color>"
                    : TabNames[i];
            }
        }

        private static void PaintTools()
        {
            for (var i = 0; i < ChipViews.Length; i++)
            {
                var view = ChipViews[i];
                view.Fill.color = Chips[i] ? MenuKit.Ink : view.Hovered ? MenuKit.RowSelected : MenuKit.ChipOff;
                view.Label.color = Chips[i] ? MenuKit.Dark : MenuKit.Soft;
                view.Focus.enabled = _zone == Zone.Chips && _tool == i;
            }
            for (var i = 0; i < HeaderViews.Length; i++)
            {
                var view = HeaderViews[i];
                var sorted = (int)_sort == i + 1;
                view.Label.color = sorted ? MenuKit.Ink : view.Hovered ? MenuKit.Soft : MenuKit.Muted;
                view.Chevron.gameObject.SetActive(sorted);
                // Points up for ascending and down for descending.
                view.Chevron.localScale = new Vector3(1, _descending ? -1 : 1, 1);
                view.Focus.enabled = _zone == Zone.Headers && _tool == i;
            }
        }

        private static void PaintSearch()
        {
            if (_searchText == null || !_searchText) return;
            _searchBox.color = _searching ? MenuKit.RowSelected : MenuKit.Field;
            var empty = _search.Length == 0 && !_searching;
            _searchText.color = empty ? MenuKit.Faint : MenuKit.Ink;
            _searchText.text = empty ? "Search servers" : _search + (_searching && _caretOn ? "|" : "");
        }

        private static void PaintCaret()
        {
            if (!_searching) return;
            var on = (int)(Time.unscaledTime * 2f) % 2 == 0;
            if (on == _caretOn) return;
            _caretOn = on;
            PaintSearch();
        }

        private static void PaintHighlights()
        {
            for (var i = 0; i < _rows.Length; i++)
            {
                var index = _top + i;
                if (index >= Entries.Count) continue;
                var selected = index == _selected;
                var view = _rows[i];
                view.Highlight.enabled = selected || i == _hoverRow;
                // The selection dims while the controller is up in the filters or column headers.
                view.Highlight.color = selected && _zone == Zone.List ? MenuKit.RowSelected : MenuKit.RowHover;
            }
        }

        private static void PaintRow(RowView view, int index)
        {
            var shown = index < Entries.Count;
            if (view.Rect.gameObject.activeSelf != shown) view.Rect.gameObject.SetActive(shown);
            if (!shown) return;
            var entry = Entries[index];
            var state = StateOf(entry, out var details);
            var (status, color) = Describe(state, details);
            view.Name.text = entry.Name;
            view.Kind.text = Kind(entry, details);
            view.Status.text = status;
            view.Status.color = color;
            view.Marker.color = color;
            view.Players.text = entry.Limit > 0 ? entry.Players + " / " + entry.Limit : entry.Players.ToString();
            var bars = entry.Ping <= 0 ? 0 : entry.Ping < 60 ? 3 : entry.Ping < 110 ? 2 : 1;
            for (var b = 0; b < 3; b++) view.Bars[b].color = b < bars ? MenuKit.Ink : MenuKit.BarOff;
            view.Ping.text = entry.Ping > 0 ? entry.Ping.ToString() : "";
        }

        private static string Kind(Entry entry, ServerDetails details)
        {
            var parts = new List<string> { entry.Dedicated != null ? "Dedicated" : "Hosted" };
            if (details != null) parts.Add("Modstoke " + details.Loader);
            if (entry.Mode.Length > 0) parts.Add(entry.Mode);
            if (entry.Password) parts.Add("Password");
            return string.Join(" · ", parts);
        }

        private static (string Text, Color Color) Describe(ModState state, ServerDetails details)
        {
            switch (state)
            {
                case ModState.Match: return ("Mods match", MenuKit.Green);
                case ModState.Needs:
                    var missing = ServerBrowserBadges.Missing(details).Count();
                    return (missing == 1 ? "Needs 1 mod" : "Needs " + missing + " mods", MenuKit.Amber);
                case ModState.AddsItems: return ("Your mods add items", MenuKit.Amber);
                case ModState.Differs: return ("Mods differ", MenuKit.Red);
                case ModState.Modded: return ("Modded", MenuKit.Muted);
                case ModState.Checking: return ("Checking", MenuKit.Faint);
                case ModState.Vanilla: return ("Vanilla", MenuKit.Faint);
                case ModState.OtherVersion: return ("Other version", MenuKit.Red);
                default: return ("—", MenuKit.Faint);
            }
        }

        private static void PaintDetails()
        {
            var entry = _selected >= 0 && _selected < Entries.Count ? Entries[_selected] : null;
            if (entry == null)
            {
                _kind.text = "";
                _name.text = "No server selected";
                _sub.text = "";
                foreach (var value in StatValues) value.text = "—";
                ShowNotice(null, null, 0);
                ShowMods(null);
                SetJoin(false, "JOIN");
                return;
            }
            // The selected server is always asked, so a Modstoke server is found even without a marked name.
            Ask(entry);
            var state = StateOf(entry, out var details);
            _kind.text = (entry.Dedicated != null ? "DEDICATED" : "HOSTED") + (entry.Mode.Length > 0 ? " · " + entry.Mode.ToUpperInvariant() : "")
                + (entry.Password ? " · PASSWORD" : "");
            _name.text = entry.Name;
            _sub.text = entry.Dedicated != null ? entry.Dedicated.Ip + ":" + entry.Dedicated.Port : "Hosted by a player over Steam";
            StatValues[0].text = entry.Limit > 0 ? entry.Players + " / " + entry.Limit : entry.Players.ToString();
            StatValues[1].text = entry.Ping > 0 ? entry.Ping + " ms" : "—";
            StatValues[2].text = details != null ? (details.Loader.Length > 0 ? details.Loader : "Yes")
                : state switch
                {
                    ModState.Checking => "…",
                    ModState.Modded => "Unknown",
                    ModState.Hosted => "—",
                    _ => "No"
                };

            var (title, text, accent) = Notice(state, details);
            ShowNotice(title, text, accent);
            ShowMods(details);
            var full = entry.Limit > 0 && entry.Players >= entry.Limit;
            SetJoin(entry.VersionMatch && !full, !entry.VersionMatch ? "OTHER VERSION" : full ? "FULL" : "JOIN");
        }

        private const int GreyAccent = 0x74726C, GreenAccent = 0xA7DCB8, AmberAccent = 0xF1C674, RedAccent = 0xF0A08A;

        private static (string Title, string Text, int Accent) Notice(ModState state, ServerDetails details)
        {
            switch (state)
            {
                case ModState.OtherVersion:
                    return ("Different game version", "This server runs another version of Sons of the Forest.", RedAccent);
                case ModState.Hosted:
                    return ("Hosted game", "Hosted games don't list their mods yet; a Modstoke host checks them when you join.", GreyAccent);
                case ModState.Checking:
                    return ("Checking the server", "Asking it which mods it runs.", GreyAccent);
                case ModState.Modded:
                    return ("Modded server", "It doesn't list its mods. It may run another loader or an older Modstoke.", GreyAccent);
                case ModState.Vanilla:
                    return ("No Modstoke", "This server runs no Modstoke, so mods with their own items won't work there.", GreyAccent);
                case ModState.Match:
                    return ("Your mods match", "You can join with the mods you have.", GreenAccent);
                case ModState.AddsItems:
                    return ("Your mods add items", "This server has no mods with their own items, so yours won't exist there.", AmberAccent);
            }
            var missing = ServerBrowserBadges.Missing(details).Select(mod => mod.Id).ToArray();
            if (state == ModState.Needs)
                return (missing.Length == 1 ? "Needs 1 mod" : "Needs " + missing.Length + " mods", "Add " + string.Join(", ", missing) + " to join.", AmberAccent);
            var local = ServerDetails.LocalMods();
            var extra = local.Where(own => own.Client == "required"
                && !details.Mods.Any(mod => string.Equals(mod.Id, own.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(own => own.Id).ToArray();
            var steps = new List<string>();
            if (missing.Length > 0) steps.Add("Add " + string.Join(", ", missing) + ".");
            if (extra.Length > 0) steps.Add("Turn off " + string.Join(", ", extra) + ".");
            if (steps.Count == 0) steps.Add("Match the versions in the list below.");
            return ("You'd be refused", "Its mods with their own items differ from yours. " + string.Join(" ", steps), RedAccent);
        }

        private static void ShowNotice(string title, string text, int accent)
        {
            var shown = title != null;
            _noticeBox.gameObject.SetActive(shown);
            if (!shown) return;
            _noticeBox.color = MenuKit.Toned(accent, 0.2f);
            _noticeBar.color = MenuKit.Hex(accent);
            _noticeTitle.text = title;
            _noticeTitle.color = accent == GreyAccent ? MenuKit.Ink : MenuKit.Hex(accent);
            _noticeText.text = text;
        }

        private static void ShowMods(ServerDetails details)
        {
            var mods = details?.Mods ?? Array.Empty<ServerDetails.Mod>();
            _modsHeader.gameObject.SetActive(mods.Count > 0);
            var local = ServerDetails.LocalMods();
            for (var i = 0; i < _mods.Length; i++)
            {
                var view = _mods[i];
                var shown = i < mods.Count;
                view.Rect.gameObject.SetActive(shown);
                if (!shown) continue;
                var mod = mods[i];
                if (i == _mods.Length - 1 && details.ModCount > _mods.Length)
                {
                    view.Name.text = "and " + (details.ModCount - _mods.Length + 1) + " more";
                    view.Server.text = view.Yours.text = "";
                    continue;
                }
                var own = local.FirstOrDefault(entry => string.Equals(entry.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
                view.Name.text = mod.Id;
                view.Server.text = mod.Version.Length > 0 ? mod.Version : "?";
                if (own != null)
                {
                    view.Yours.text = own.Version.Length > 0 ? own.Version : "?";
                    view.Yours.color = string.Equals(own.Version, mod.Version, StringComparison.OrdinalIgnoreCase) ? MenuKit.Green : MenuKit.Amber;
                }
                else
                {
                    view.Yours.text = mod.Client == "required" ? "Missing" : mod.Client == "server" ? "Not needed" : "Not installed";
                    view.Yours.color = mod.Client == "required" ? MenuKit.Red : MenuKit.Faint;
                }
            }
        }

        private static void SetJoin(bool enabled, string text)
        {
            _joinFill.color = enabled ? MenuKit.Ink : MenuKit.Disabled;
            _joinLabel.color = enabled ? MenuKit.Dark : MenuKit.Faint;
            _joinLabel.text = text;
        }
    }
}
