// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Localization;
using Modstoke.Sons;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal static partial class MultiplayerScreen
    {
        // Layout in the front end's design units, scaled to the real screen height.
        private const float PanelTop = 120, RowsTop = 150, RowHeight = 48, RowGap = 2, TagHeight = 24, FooterHeight = 44;
        private const float StripHeight = 143;

        private sealed class RowView
        {
            internal RectTransform Rect;
            internal Image Highlight, Marker;
            internal TextMeshProUGUI Name, Kind, Status, Players, Ping;
            internal Image[] Bars;
            internal Glitch Flash;
        }

        private sealed class ModView
        {
            internal RectTransform Rect;
            internal TextMeshProUGUI Name, Server, Yours;
        }

        private sealed class TabView
        {
            internal RectTransform Rect, Under;
            internal TextMeshProUGUI Label;
            internal Glitch Flash;
            internal string Text;
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
            internal KeyTag Tag;
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
        private static SectionNav _sections;
        private static Glide _tabMark, _rowMark;
        private static Tab _paintedTab = (Tab)(-1);
        private static IntPtr _paintedSelection;
        private static RectTransform _listRect, _modsHeader, _progress;
        private static KeyTag _tabLeft, _tabRight;
        private static TextMeshProUGUI _status, _empty, _kind, _name, _sub, _noticeTitle, _noticeText, _joinLabel, _searchText;
        private static Image _noticeBox, _noticeBar, _joinFill, _searchBox;
        private static float _bottom, _nextProgress;
        private static int _modstokeCount;
        private static bool _caretOn;

        private static float U(float design) => FrontEnd.U(design);

        private static string Upper(string text) => text.ToUpper(Languages.CultureOf(Languages.Current));

        private static void Build(RectTransform canvas)
        {
            var root = MenuKit.Box(canvas, "Join").Place(0, 0, 1, 1);
            _rootObject = root.gameObject;
            _layout = FrontEnd.LayoutVersion;
            _bottom = BottomInset();
            Hots.Clear();
            Prompts.Clear();
            _paintedTab = (Tab)(-1);
            _paintedSelection = IntPtr.Zero;
            MenuKit.Backdrop(root, _bottom - U(24), U(110));
            var pad = U(60);
            BuildHeader(root, pad);
            BuildList(root, pad);
            BuildDetails(root, pad);
            BuildFooter(root, pad);
            PaintHints();
        }

        // The game's own panels end where its bottom strip's artwork begins; this screen ends on the same line.
        private static float BottomInset()
        {
            try
            {
                var layout = _gameList != null && _gameList ? _gameList.GetComponent<RectTransform>() : null;
                var canvas = _canvasRoot != null && _canvasRoot ? _canvasRoot.GetComponent<Canvas>() : null;
                if (layout != null && canvas != null)
                {
                    var camera = canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                    var corners = new Il2CppStructArray<Vector3>(4);
                    layout.GetWorldCorners(corners);
                    var inset = RectTransformUtility.WorldToScreenPoint(camera, corners[0]).y;
                    if (inset > U(110) && inset < U(220)) return inset;
                }
            }
            catch (Exception)
            {
                // Falls back to the strip's usual height below.
            }
            return U(StripHeight);
        }

        private static void BuildHeader(RectTransform root, float pad)
        {
            var section = MenuKit.Text(root, "Section", Upper(T["multiplayer.section"]), U(14), true, MenuKit.Muted);
            section.characterSpacing = 32;
            section.rectTransform.At(pad, U(30), U(600), U(18));
            var title = MenuKit.Text(root, "Title", Upper(T["multiplayer.joinTitle"]), U(50), true, MenuKit.Ink);
            title.characterSpacing = 5;
            title.rectTransform.At(pad, U(39), U(900), U(80));
            _sections = new SectionNav(root, pad, U(56), false);
            Add(_sections.Other, Host, _sections.Hover);
        }

        private static void BuildList(RectTransform root, float pad)
        {
            // No panel: the list sits on the veil, its content lined up with the title above it.
            var panel = MenuKit.Box(root, "ServerList").Place(0, 0, 1, 1, pad - U(28), _bottom, pad + U(464) - U(28), U(PanelTop));
            _listRect = panel;
            MenuKit.Hairline(panel, "Top", 0, U(28), U(28));
            var panelHeight = root.rect.height - _bottom - U(PanelTop);

            _tabLeft = new KeyTag(panel, "PreviousTab", U(TagHeight));
            for (var i = 0; i < TabKeys.Length; i++)
            {
                var tab = (Tab)i;
                var view = new TabView { Rect = MenuKit.Box(panel, "Tab " + TabKeys[i]), Text = Upper(T["multiplayer.tab." + TabKeys[i]]) };
                view.Under = MenuKit.Box(view.Rect, "Under").Place(0, 0, 1, 0, U(12), U(6), U(12), -U(9));
                view.Label = MenuKit.Text(view.Rect, "Label", view.Text, U(15), true, MenuKit.Muted, TextAlignmentOptions.Center);
                view.Label.characterSpacing = 12;
                view.Label.rectTransform.Place(0, 0, 1, 1);
                // Room for the count shown beside the open tab's name.
                view.Width = view.Label.GetPreferredValues(view.Text + " 0000").x + U(24);
                view.Flash = new Glitch(view.Label);
                TabViews[i] = view;
                Add(view.Rect, () => SetTab(tab), on =>
                {
                    view.Hovered = on;
                    PaintTabs();
                });
            }
            _tabRight = new KeyTag(panel, "NextTab", U(TagHeight));
            _tabMark = new Glide(panel, true);

            var search = MenuKit.Box(panel, "Search").AtRight(U(28), U(20), U(300), U(40));
            _searchBox = MenuKit.Fill(search, null, MenuKit.Field);
            _searchText = MenuKit.Text(search, "Text", "", U(16), false, MenuKit.Faint, TextAlignmentOptions.MidlineLeft, true);
            _searchText.rectTransform.Place(0, 0, 1, 1, U(14), 0, U(14), 0);
            _searchHot = Add(search, () => FocusSearch(true));

            var rule = MenuKit.Box(panel, "Rule").Strip(U(70), U(2), U(28), U(28));
            MenuKit.Fill(rule, null, MenuKit.Rule);
            // The rule under the tabs fills from the left while the game is still hearing from servers.
            _progress = MenuKit.Box(rule, "Progress").Place(0, 0, 0, 1);
            MenuKit.Fill(_progress, null, MenuKit.Soft);

            var x = U(28);
            for (var i = 0; i < ChipKeys.Length; i++)
            {
                var index = i;
                var text = Upper(T["multiplayer.filter." + ChipKeys[i]]);
                var view = new ToolView { Rect = MenuKit.Box(panel, "Filter " + ChipKeys[i]) };
                view.Fill = MenuKit.Fill(view.Rect, null, MenuKit.ChipOff);
                view.Label = MenuKit.Text(view.Rect, "Label", text, U(12.5f), true, MenuKit.Soft, TextAlignmentOptions.Center);
                view.Label.characterSpacing = 10;
                view.Label.rectTransform.Place(0, 0, 1, 1);
                view.Focus = MenuKit.Fill(MenuKit.Box(view.Rect, "Focus").Place(0, 0, 1, 0, 0, -U(5), 0, U(3)), null, MenuKit.Ink);
                var width = view.Label.GetPreferredValues(text).x + U(26);
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
            HeaderViews[0] = Header(header, 0, "server", false, U(24), U(382));
            HeaderViews[1] = Header(header, 1, "mods", true, -U(370), U(170));
            HeaderViews[2] = Header(header, 2, "players", true, -U(158), U(76));
            HeaderViews[3] = Header(header, 3, "ping", true, -U(70), 0);

            var visible = Mathf.Clamp(Mathf.FloorToInt((panelHeight - U(RowsTop) - U(40)) / U(RowHeight + RowGap)), 3, 14);
            _rowMark = new Glide(panel, false);
            _rows = new RowView[visible];
            for (var i = 0; i < visible; i++) _rows[i] = Row(panel, i);

            _empty = MenuKit.Text(panel, "Empty", "", U(16), false, MenuKit.Muted, TextAlignmentOptions.Center);
            _empty.rectTransform.Strip(U(RowsTop + 60), U(26), U(28), U(28));

            _status = MenuKit.Text(panel, "Status", "", U(14), false, MenuKit.Muted);
            _status.rectTransform.Place(0, 0, 1, 0, U(28), U(10), U(290), -U(32));
            var classic = MenuKit.Box(panel, "Classic").Place(1, 0, 1, 0, -U(280), U(8), U(28), -U(36));
            var classicLabel = MenuKit.Text(classic, "Label", Upper(T["multiplayer.ownBrowser"]), U(12), true, MenuKit.Muted, TextAlignmentOptions.MidlineRight);
            classicLabel.characterSpacing = 12;
            classicLabel.rectTransform.Place(0, 0, 1, 1);
            Add(classic, MultiplayerTakeover.UseClassicBrowser, on => classicLabel.color = on ? MenuKit.Ink : MenuKit.Muted);
        }

        private static ToolView Header(RectTransform header, int index, string key, bool fromRight, float left, float right)
        {
            var text = Upper(T["multiplayer.column." + key]);
            var view = new ToolView { Rect = MenuKit.Box(header, key) };
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
            view.Highlight = MenuKit.Fill(MenuKit.Box(rect, "Hover").Place(0, 0, 1, 1), null, MenuKit.RowHover);
            view.Marker = MenuKit.Fill(MenuKit.Box(rect, "Marker").Place(0, 0, 0, 1, U(10), U(11), -U(13), U(11)), null, MenuKit.Faint);
            // Each line gets a box taller than its text: an ellipsis box shorter than the line hides it.
            view.Name = MenuKit.Text(rect, "Name", "", U(17), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            view.Name.rectTransform.Place(0, 0.5f, 1, 1, U(24), -U(5), U(382), U(2));
            view.Flash = new Glitch(view.Name);
            view.Kind = MenuKit.Text(rect, "Kind", "", U(13), false, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
            view.Kind.rectTransform.Place(0, 0, 1, 0.5f, U(24), U(1), U(382), -U(3));
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
            var panel = MenuKit.Box(root, "Details").Place(1, 0, 1, 1, -(pad + U(440) - U(28)), _bottom, pad - U(28), U(PanelTop));
            MenuKit.Hairline(panel, "Top", 0, U(28), U(28));
            var panelHeight = root.rect.height - _bottom - U(PanelTop);
            _kind = MenuKit.Text(panel, "Kind", "", U(13), true, MenuKit.Muted);
            _kind.characterSpacing = 18;
            _kind.rectTransform.Strip(U(22), U(18), U(28), U(28));
            _name = MenuKit.Text(panel, "Name", "", U(30), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _name.rectTransform.Strip(U(42), U(44), U(28), U(28));
            _sub = MenuKit.Text(panel, "Address", "", U(15), false, MenuKit.Soft);
            _sub.rectTransform.Strip(U(86), U(22), U(28), U(28));

            var stats = MenuKit.Box(panel, "Stats").Strip(U(118), U(58), U(28), U(28));
            var names = new[] { "players", "ping", "modstoke" };
            for (var i = 0; i < 3; i++)
            {
                var cell = MenuKit.Box(stats, names[i]).Place(i / 3f, 0, (i + 1) / 3f, 1, i == 0 ? 0 : U(1), 0, i == 2 ? 0 : U(1), 0);
                MenuKit.Fill(cell, null, MenuKit.Cell);
                var label = MenuKit.Text(cell, "Label", Upper(T["multiplayer.details." + names[i]]), U(11), true, MenuKit.Muted);
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
            Column(_modsHeader, "mod", false, 0, U(190));
            Column(_modsHeader, "server", true, -U(186), U(102));
            Column(_modsHeader, "yours", true, -U(98), 0);
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
            _joinLabel = MenuKit.Text(join, "Label", "", U(21), true, MenuKit.Dark, TextAlignmentOptions.Center);
            _joinLabel.characterSpacing = 16;
            _joinLabel.rectTransform.Place(0, 0, 1, 1);
            Add(join, Join);
        }

        private static void Column(RectTransform header, string key, bool fromRight, float left, float right)
        {
            var label = MenuKit.Text(header, key, Upper(T["multiplayer.column." + key]), U(12), true, MenuKit.Muted);
            label.characterSpacing = 16;
            if (fromRight) label.rectTransform.Place(1, 0, 1, 1, left, 0, right, 0);
            else label.rectTransform.Place(0, 0, 1, 1, left, 0, right, 0);
        }

        // The prompt bar over the game's bottom strip artwork: what each button or key does on this screen.
        private static void BuildFooter(RectTransform root, float pad)
        {
            var footer = MenuKit.Box(root, "Prompts").Place(0, 0, 1, 0, pad, U(30), pad, -U(30 + FooterHeight));
            AddPrompt(footer, "common.back", "B", "ESC", false, () =>
            {
                MenuSound.Play(MenuCue.Back);
                BackOut();
            });
            AddPrompt(footer, "multiplayer.joinByIp", "Y", "F2", false, JoinByIp);
            AddPrompt(footer, "common.search", null, "F3", false, () => FocusSearch(true));
            AddPrompt(footer, "common.join", "A", "ENTER", true, Activate);
            AddPrompt(footer, "common.refresh", "X", "F5", true, Refresh);
        }

        private static void AddPrompt(RectTransform footer, string key, string pad, string keyboard, bool right, Action click)
        {
            var prompt = new Prompt { Rect = MenuKit.Box(footer, key), Pad = pad, Key = keyboard, Right = right };
            prompt.Tag = new KeyTag(prompt.Rect, "Key", U(TagHeight));
            prompt.Label = MenuKit.Text(prompt.Rect, "Label", Upper(T[key]), U(19), true, MenuKit.Soft);
            prompt.Label.characterSpacing = 14;
            Prompts.Add(prompt);
            Add(prompt.Rect, click, on => prompt.Label.color = on ? MenuKit.Ink : MenuKit.Soft);
        }

        // Shows controller buttons while a controller is the device in use, keys otherwise.
        private static void PaintHints()
        {
            if (_rootObject == null || !_rootObject) return;
            _tabLeft.Set(_pad ? "LB" : "Q", _pad);
            _tabRight.Set(_pad ? "RB" : "E", _pad);
            _sections.Paint(_pad);
            foreach (var prompt in Prompts)
            {
                var text = _pad ? prompt.Pad : prompt.Key;
                prompt.Rect.gameObject.SetActive(text != null);
                if (text != null) prompt.Tag.Set(text, _pad);
            }

            var x = U(28);
            foreach (var item in new[] { _tabLeft.Rect, TabViews[0].Rect, TabViews[1].Rect, TabViews[2].Rect, TabViews[3].Rect, _tabRight.Rect })
            {
                var tab = Array.Find(TabViews, view => view.Rect == item);
                var width = tab != null ? tab.Width : item.sizeDelta.x;
                var height = tab != null ? U(44) : item.sizeDelta.y;
                item.At(x, U(18) + (U(44) - height) / 2, width, height);
                x += width + U(tab != null ? 2 : 6);
            }

            float left = 0, right = 0;
            foreach (var prompt in Prompts)
            {
                if (!prompt.Rect.gameObject.activeSelf) continue;
                var tag = prompt.Tag.Size;
                var tagWidth = tag.x;
                var labelWidth = prompt.Label.GetPreferredValues(prompt.Label.text).x + U(4);
                var width = tagWidth + U(10) + labelWidth;
                prompt.Tag.Rect.At(0, (U(FooterHeight) - tag.y) / 2, tagWidth, tag.y);
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

        private static void Paint()
        {
            if (_rootObject == null || !_rootObject) return;
            PaintTabs();
            PaintTools();
            PaintSearch();
            for (var i = 0; i < _rows.Length; i++) PaintRow(_rows[i], _top + i);
            PaintHighlights();
            PaintDetails();
            PaintStatus();
        }

        private static void PaintStatus()
        {
            var refreshing = Refreshing() || _backlog;
            var filtered = Entries.Count != Listed.Count;
            _empty.text = Entries.Count > 0 ? ""
                : filtered ? T["multiplayer.empty.filtered"]
                : refreshing ? T["multiplayer.empty.searching"]
                : T[_tab == Tab.Lan ? "multiplayer.empty.lan" : "multiplayer.empty.none"];
            var parts = new List<string>
            {
                filtered
                    ? T.Count("multiplayer.status.filtered", Listed.Count, ("shown", Entries.Count))
                    : T.Count("multiplayer.status.servers", Listed.Count)
            };
            if (_modstokeCount > 0) parts.Add(T.Get("multiplayer.status.runningModstoke", ("count", _modstokeCount)));
            if (refreshing) parts.Add(ReadingText());
            _status.text = string.Join(" · ", parts);
        }

        // How far the game is through hearing from the servers Steam listed, where it says.
        private static string ReadingText()
        {
            try
            {
                _gui._dedicatedBrowser.GetServerCount(out var done, out var total);
                if (total > 0 && done < total) return T.Get("multiplayer.status.reading", ("done", done), ("total", total));
            }
            catch (Exception)
            {
                // The plain word below says enough.
            }
            return T["multiplayer.status.refreshing"];
        }

        private static void PaintProgress()
        {
            var now = Time.unscaledTime;
            if (now < _nextProgress || _progress == null || !_progress) return;
            _nextProgress = now + 0.15f;
            var progress = 0f;
            try
            {
                var browser = _gui._dedicatedBrowser;
                if (_tab != Tab.Hosted && browser.RefreshInProgress) progress = Mathf.Clamp01(browser.RefreshProgress);
            }
            catch (Exception)
            {
                // No bar when the browser has nothing to report.
            }
            // The status line carries the counts, so it follows the bar while a refresh runs and once it ends.
            if (progress > 0 || _progress.anchorMax.x > 0) PaintStatus();
            _progress.anchorMax = new Vector2(progress, 1);
        }

        private static void PaintTabs()
        {
            for (var i = 0; i < TabViews.Length; i++)
            {
                var view = TabViews[i];
                var on = i == (int)_tab;
                view.Label.color = on ? MenuKit.Ink : view.Hovered ? MenuKit.Soft : MenuKit.Muted;
                view.Label.text = on && Listed.Count > 0
                    ? view.Text + " <color=" + MenuKit.Html(MenuKit.Muted) + ">" + Listed.Count + "</color>"
                    : view.Text;
            }
            if (_paintedTab == _tab) return;
            var first = (int)_paintedTab < 0;
            if (!first) TabViews[(int)_paintedTab].Flash.Release();
            _paintedTab = _tab;
            var open = TabViews[(int)_tab];
            _tabMark.Target(open.Under, first);
            if (!first) open.Flash.Pulse();
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
            _searchText.text = empty ? T["multiplayer.searchPlaceholder"] : _search + (_searching && _caretOn ? "|" : "");
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
            RowView selectedView = null;
            for (var i = 0; i < _rows.Length; i++)
            {
                var index = _top + i;
                if (index >= Entries.Count) continue;
                var view = _rows[i];
                if (index == _selected) selectedView = view;
                // The focus mark covers the selected row; others only take a faint hover.
                view.Highlight.enabled = i == _hoverRow && index != _selected;
                // The selection dims while the controller is up in the filters or column headers.
                view.Highlight.color = MenuKit.RowHover;
                PaintSelection(view, index);
            }
            var mark = selectedView != null && _zone == Zone.List ? selectedView.Rect : null;
            _rowMark.Target(mark);
            _rowMark.Follow();
            if (selectedView == null || _selectedId == _paintedSelection) return;
            // A newly selected server splits as it is reached, not when the list merely redraws. The rows are reused
            // as the list scrolls, so the split closes up by itself rather than staying on a row.
            var moved = _paintedSelection != IntPtr.Zero;
            _paintedSelection = _selectedId;
            if (moved) selectedView.Flash.Pulse(false);
        }

        private static void PaintRow(RowView view, int index)
        {
            var shown = index < Entries.Count;
            if (view.Rect.gameObject.activeSelf != shown) view.Rect.gameObject.SetActive(shown);
            if (!shown) return;
            var entry = Entries[index];
            var state = StateOf(entry, out var details);
            var (status, color) = Describe(state, details);
            view.Name.text = NameOf(entry);
            view.Kind.text = Kind(entry, details, " · ");
            view.Status.text = status;
            view.Status.color = color;
            view.Marker.color = color;
            view.Players.text = entry.Limit > 0 ? entry.Players + " / " + entry.Limit : entry.Players.ToString();
            view.Ping.text = entry.Ping > 0 ? entry.Ping.ToString() : "";
            PaintSelection(view, index);
        }

        // The selected row is under the focus plate, so its text and ping bars turn dark there.
        private static void PaintSelection(RowView view, int index)
        {
            if (index >= Entries.Count) return;
            var entry = Entries[index];
            var on = index == _selected && _zone == Zone.List;
            view.Name.color = view.Players.color = view.Ping.color = on ? MenuKit.OnPlate : MenuKit.Ink;
            view.Kind.color = on ? MenuKit.OnPlateMuted : MenuKit.Muted;
            if (on) view.Status.color = MenuKit.OnPlateSoft;
            else view.Status.color = Describe(StateOf(entry, out var details), details).Color;
            var bars = entry.Ping <= 0 ? 0 : entry.Ping < 60 ? 3 : entry.Ping < 110 ? 2 : 1;
            for (var b = 0; b < 3; b++)
                view.Bars[b].color = b < bars ? on ? MenuKit.OnPlate : MenuKit.Ink : on ? MenuKit.OnPlateTrack : MenuKit.BarOff;
        }

        private static string NameOf(Entry entry) => entry.Name.Length > 0 ? entry.Name : T["multiplayer.unnamed"];

        private static string Kind(Entry entry, ServerDetails details, string separator)
        {
            var parts = new List<string> { T[entry.Dedicated != null ? "multiplayer.kind.dedicated" : "multiplayer.kind.hosted"] };
            if (details != null && details.Loader.Length > 0) parts.Add(T.Get("multiplayer.kind.modstoke", ("version", ModNames.Version(details.Loader))));
            var mode = ModeName(entry.Mode);
            if (mode.Length > 0) parts.Add(mode);
            if (entry.Password) parts.Add(T["multiplayer.kind.password"]);
            return string.Join(separator, parts);
        }

        private static (string Text, Color Color) Describe(ModState state, ServerDetails details)
        {
            switch (state)
            {
                case ModState.Match: return (T["multiplayer.mods.match"], MenuKit.Green);
                case ModState.Needs: return (T.Count("multiplayer.mods.needs", ServerBrowserBadges.Missing(details).Count()), MenuKit.Amber);
                case ModState.AddsItems: return (T["multiplayer.mods.addsItems"], MenuKit.Amber);
                case ModState.Differs: return (T["multiplayer.mods.differ"], MenuKit.Red);
                case ModState.Modded: return (T["multiplayer.mods.modded"], MenuKit.Muted);
                case ModState.Checking: return (T["multiplayer.mods.checking"], MenuKit.Faint);
                case ModState.Vanilla: return (T["multiplayer.mods.vanilla"], MenuKit.Faint);
                case ModState.OtherVersion: return (T["multiplayer.mods.otherVersion"], MenuKit.Red);
                default: return ("—", MenuKit.Faint);
            }
        }

        private static void PaintDetails()
        {
            var entry = _selected >= 0 && _selected < Entries.Count ? Entries[_selected] : null;
            if (entry == null)
            {
                _kind.text = "";
                _name.text = T["multiplayer.details.none"];
                _sub.text = "";
                foreach (var value in StatValues) value.text = "—";
                ShowNotice(null, null, 0);
                ShowMods(null);
                SetJoin(false, T["common.join"]);
                return;
            }
            // The selected server is always asked, so a Modstoke server is found even without a marked name.
            Ask(entry);
            var state = StateOf(entry, out var details);
            _kind.text = Upper(Kind(entry, null, " · "));
            _name.text = NameOf(entry);
            _sub.text = entry.Dedicated != null ? entry.Dedicated.Ip + ":" + entry.Dedicated.Port : T["multiplayer.details.hostedBy"];
            StatValues[0].text = entry.Limit > 0 ? entry.Players + " / " + entry.Limit : entry.Players.ToString();
            StatValues[1].text = entry.Ping > 0 ? T.Get("multiplayer.details.pingValue", ("ping", entry.Ping)) : "—";
            StatValues[2].text = details != null ? (details.Loader.Length > 0 ? ModNames.Version(details.Loader) : T["common.yes"])
                : state switch
                {
                    ModState.Checking => "…",
                    ModState.Modded => T["common.unknown"],
                    ModState.Hosted => "—",
                    _ => T["common.no"]
                };

            var (title, text, accent) = Notice(state, details);
            ShowNotice(title, text, accent);
            ShowMods(details);
            var full = entry.Limit > 0 && entry.Players >= entry.Limit;
            SetJoin(entry.VersionMatch && !full,
                T[!entry.VersionMatch ? "multiplayer.mods.otherVersion" : full ? "multiplayer.details.full" : "common.join"]);
        }

        private const int GreyAccent = 0x85837C, GreenAccent = 0xA7DCB8, AmberAccent = 0xF1C674, RedAccent = 0xF0A08A;

        private static (string Title, string Text, int Accent) Notice(ModState state, ServerDetails details)
        {
            (string, string, int) Plain(string key, int accent)
                => (T["multiplayer.notice." + key + "Title"], T["multiplayer.notice." + key], accent);
            switch (state)
            {
                case ModState.OtherVersion: return Plain("otherVersion", RedAccent);
                case ModState.Hosted: return Plain("hosted", GreyAccent);
                case ModState.Checking: return Plain("checking", GreyAccent);
                case ModState.Modded: return Plain("modded", GreyAccent);
                case ModState.Vanilla: return Plain("vanilla", GreyAccent);
                case ModState.Match: return Plain("match", GreenAccent);
                case ModState.AddsItems: return Plain("addsItems", AmberAccent);
            }
            var missing = ServerBrowserBadges.Missing(details).Select(mod => ModNames.LabelForId(mod.Id, mod.Version)).ToArray();
            if (state == ModState.Needs)
                return (T.Count("multiplayer.notice.needsTitle", missing.Length),
                    T.Get("multiplayer.notice.needs", ("mods", string.Join(", ", missing))), AmberAccent);
            var local = ServerDetails.LocalMods();
            var extra = local.Where(own => own.Client == "required"
                && !details.Mods.Any(mod => string.Equals(mod.Id, own.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(own => ModNames.ForId(own.Id)).ToArray();
            var steps = new List<string> { T["multiplayer.notice.refused"] };
            if (missing.Length > 0) steps.Add(T.Get("multiplayer.notice.refusedAdd", ("mods", string.Join(", ", missing))));
            if (extra.Length > 0) steps.Add(T.Get("multiplayer.notice.refusedTurnOff", ("mods", string.Join(", ", extra))));
            if (steps.Count == 1) steps.Add(T["multiplayer.notice.refusedVersions"]);
            return (T["multiplayer.notice.refusedTitle"], string.Join(" ", steps), RedAccent);
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
                    view.Name.text = T.Get("multiplayer.mod.more", ("count", details.ModCount - _mods.Length + 1));
                    view.Server.text = view.Yours.text = "";
                    continue;
                }
                var own = local.FirstOrDefault(entry => string.Equals(entry.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
                view.Name.text = ModNames.ForId(mod.Id);
                view.Server.text = mod.Version.Length > 0 ? ModNames.Version(mod.Version) : "?";
                if (own != null)
                {
                    view.Yours.text = own.Version.Length > 0 ? ModNames.Version(own.Version) : "?";
                    view.Yours.color = string.Equals(own.Version, mod.Version, StringComparison.OrdinalIgnoreCase) ? MenuKit.Green : MenuKit.Amber;
                }
                else
                {
                    view.Yours.text = T[mod.Client == "required" ? "multiplayer.mod.missing"
                        : mod.Client == "server" ? "multiplayer.mod.notNeeded" : "multiplayer.mod.notInstalled"];
                    view.Yours.color = mod.Client == "required" ? MenuKit.Red : MenuKit.Faint;
                }
            }
        }

        private static void SetJoin(bool enabled, string text)
        {
            _joinFill.color = enabled ? MenuKit.Ink : MenuKit.Disabled;
            _joinLabel.color = enabled ? MenuKit.Dark : MenuKit.Faint;
            _joinLabel.text = Upper(text);
        }
    }
}
