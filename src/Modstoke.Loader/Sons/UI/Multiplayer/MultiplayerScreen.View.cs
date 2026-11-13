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
        private const int ModRows = 6;

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

        private static RowView[] _rows = Array.Empty<RowView>();
        private static RectTransform _listRect;
        private static readonly List<(TextMeshProUGUI Label, Image Brush)> TabViews = new();
        private static TextMeshProUGUI _status, _kind, _name, _sub, _noticeTitle, _noticeText, _modsHeader, _joinLabel;
        private static readonly TextMeshProUGUI[] StatValues = new TextMeshProUGUI[3];
        private static Image _noticeBox, _noticeBar, _joinFill;
        private static ModView[] _mods = Array.Empty<ModView>();

        private static float U(float design) => design * _s;

        private static void Build(RectTransform parent, RectTransform strip)
        {
            var root = MenuKit.Box(parent, "ModstokeMultiplayer").Place(0, 0, 1, 1);
            root.SetAsLastSibling();
            _rootObject = root.gameObject;
            _canvas = _rootObject.AddComponent<Canvas>();
            _rootObject.AddComponent<GraphicRaycaster>();
            var height = parent.rect.height;
            _s = height > 1 ? height / DesignHeight : 1f;

            // The game's own bottom strip stays below this screen with its Back and Join by IP buttons.
            var bottom = U(96);
            if (strip != null && strip)
            {
                var corners = new Il2CppStructArray<Vector3>(4);
                strip.GetWorldCorners(corners);
                bottom = Mathf.Clamp(parent.InverseTransformPoint(corners[1]).y - parent.rect.yMin + U(12), U(30), U(220));
            }
            var pad = U(60);
            BuildHeader(root, pad);
            BuildList(root, pad, bottom);
            BuildDetails(root, pad, bottom);
        }

        private static void BuildHeader(RectTransform root, float pad)
        {
            var section = MenuKit.Text(root, "Section", "MULTIPLAYER", U(14), true, MenuKit.Muted);
            section.characterSpacing = 32;
            section.rectTransform.At(pad, U(40), U(600), U(18));
            var title = MenuKit.Text(root, "Title", "JOIN A GAME", U(54), true, MenuKit.Ink);
            title.characterSpacing = 5;
            title.rectTransform.At(pad, U(56), U(900), U(62));

            var nav = MenuKit.Box(root, "Nav");
            nav.anchorMin = nav.anchorMax = nav.pivot = new Vector2(1, 1);
            nav.anchoredPosition = new Vector2(-pad, -U(66));
            var x = 0f;
            var lt = MenuKit.Glyph(nav, "LT", "LT", U(26));
            var join = NavItem(nav, "JOIN", true, null);
            var host = NavItem(nav, "HOST", false, Host);
            var rt = MenuKit.Glyph(nav, "RT", "RT", U(26));
            foreach (var item in new[] { lt, join, host, rt })
            {
                var size = item.sizeDelta;
                item.At(x, (U(48) - size.y) / 2, size.x, size.y);
                x += size.x + U(4);
            }
            nav.sizeDelta = new Vector2(x, U(48));
        }

        private static RectTransform NavItem(RectTransform parent, string text, bool current, Action onClick)
        {
            var rect = MenuKit.Box(parent, text);
            if (current) MenuKit.Fill(rect, MenuKit.Brush, MenuKit.Ink);
            var label = MenuKit.Text(rect, "Label", text, U(21), true, current ? MenuKit.Dark : MenuKit.Muted, TextAlignmentOptions.Center);
            label.characterSpacing = 14;
            label.rectTransform.Place(0, 0, 1, 1);
            rect.sizeDelta = new Vector2(label.GetPreferredValues(text).x + U(current ? 68 : 52), U(48));
            if (onClick != null) MenuKit.Clickable(rect, onClick);
            return rect;
        }

        private static void BuildList(RectTransform root, float pad, float bottom)
        {
            var panel = MenuKit.PanelBox(root, "ServerList").rectTransform.Place(0, 0, 1, 1, pad, bottom, pad + U(464), U(130));
            _listRect = panel;
            var panelHeight = root.rect.height - bottom - U(130);

            var x = U(28);
            var lb = MenuKit.Glyph(panel, "LB", "LB", U(26));
            lb.At(x, U(31), lb.sizeDelta.x, lb.sizeDelta.y);
            x += lb.sizeDelta.x + U(4);
            TabViews.Clear();
            for (var i = 0; i < TabNames.Length; i++)
            {
                var tab = (Tab)i;
                var rect = MenuKit.Box(panel, "Tab " + TabNames[i]);
                var brush = MenuKit.Fill(MenuKit.Box(rect, "Brush").Place(0, 0, 1, 1), MenuKit.Brush, MenuKit.Ink);
                var label = MenuKit.Text(rect, "Label", TabNames[i], U(15), true, MenuKit.Muted, TextAlignmentOptions.Center);
                label.characterSpacing = 12;
                label.rectTransform.Place(0, 0, 1, 1);
                var width = label.GetPreferredValues(TabNames[i] + " 000").x + U(22);
                rect.At(x, U(22), width, U(44));
                MenuKit.Clickable(rect, () => SetTab(tab));
                TabViews.Add((label, brush));
                x += width + U(2);
            }
            var rb = MenuKit.Glyph(panel, "RB", "RB", U(26));
            rb.At(x + U(2), U(31), rb.sizeDelta.x, rb.sizeDelta.y);

            var refresh = MenuKit.Box(panel, "Refresh");
            refresh.anchorMin = refresh.anchorMax = refresh.pivot = new Vector2(1, 1);
            refresh.anchoredPosition = new Vector2(-U(28), -U(22));
            refresh.sizeDelta = new Vector2(U(150), U(44));
            var refreshLabel = MenuKit.Text(refresh, "Label", "REFRESH", U(15), true, MenuKit.Soft, TextAlignmentOptions.MidlineRight);
            refreshLabel.characterSpacing = 12;
            refreshLabel.rectTransform.Place(0, 0, 1, 1);
            MenuKit.Clickable(refresh, Refresh);

            MenuKit.Fill(MenuKit.Box(panel, "Rule").Strip(U(76), U(2), U(28), U(28)), MenuKit.Brush, new Color(MenuKit.Ink.r, MenuKit.Ink.g, MenuKit.Ink.b, 0.22f));

            var header = MenuKit.Box(panel, "Columns").Strip(U(88), U(16), U(28), U(28));
            Column(header, "SERVER", U(24), U(370), false);
            Column(header, "MODS", -U(370), U(170), true);
            Column(header, "PLAYERS", -U(158), U(82), true);
            Column(header, "PING", -U(70), 0, true);

            const float top = 110, rowHeight = 58, gap = 2;
            var visible = Mathf.Clamp(Mathf.FloorToInt((panelHeight - U(top) - U(44)) / U(rowHeight + gap)), 3, 14);
            _rows = new RowView[visible];
            for (var i = 0; i < visible; i++)
            {
                var slot = i;
                var rect = MenuKit.Box(panel, "Row " + i).Strip(U(top + i * (rowHeight + gap)), U(rowHeight), U(28), U(28));
                var view = new RowView { Rect = rect };
                view.Highlight = MenuKit.Fill(MenuKit.Box(rect, "Highlight").Place(0, 0, 1, 1), MenuKit.Brush, new Color(MenuKit.Ink.r, MenuKit.Ink.g, MenuKit.Ink.b, 0.14f));
                view.Marker = MenuKit.Fill(MenuKit.Box(rect, "Marker").Place(0, 0, 0, 1, U(10), U(15), -U(14), U(15)), null, MenuKit.Faint);
                view.Name = MenuKit.Text(rect, "Name", "", U(18), true, MenuKit.Ink);
                view.Name.rectTransform.Place(0, 0.5f, 1, 1, U(24), 0, U(382), U(6));
                view.Kind = MenuKit.Text(rect, "Kind", "", U(14), false, MenuKit.Muted);
                view.Kind.rectTransform.Place(0, 0, 1, 0.5f, U(24), U(6), U(382), 0);
                view.Status = MenuKit.Text(rect, "Status", "", U(15), true, MenuKit.Faint);
                view.Status.rectTransform.Place(1, 0, 1, 1, -U(370), 0, U(170), 0);
                view.Players = MenuKit.Text(rect, "Players", "", U(16), false, MenuKit.Ink);
                view.Players.rectTransform.Place(1, 0, 1, 1, -U(158), 0, U(82), 0);
                var ping = MenuKit.Box(rect, "Ping").Place(1, 0, 1, 1, -U(70), 0, 0, 0);
                view.Bars = new Image[3];
                for (var b = 0; b < 3; b++)
                {
                    var barHeight = U(6 + b * 4);
                    view.Bars[b] = MenuKit.Fill(MenuKit.Box(ping, "Bar " + b).At(U(b * 6), U(rowHeight / 2 + 7) - barHeight, U(4), barHeight), null, MenuKit.Ink);
                }
                view.Ping = MenuKit.Text(ping, "Value", "", U(14), false, MenuKit.Muted);
                view.Ping.rectTransform.At(U(22), U(rowHeight / 2 - 9), U(48), U(18));
                MenuKit.Clickable(rect, () => OnRowClicked(slot));
                _rows[i] = view;
            }

            _status = MenuKit.Text(panel, "Status", "", U(14), false, MenuKit.Muted);
            _status.rectTransform.Place(0, 0, 1, 0, U(28), U(14), U(240), -U(34));
            var classic = MenuKit.Box(panel, "Classic").Place(1, 0, 1, 0, -U(220), U(8), U(28), -U(40));
            var classicLabel = MenuKit.Text(classic, "Label", "GAME'S OWN BROWSER", U(13), true, MenuKit.Soft, TextAlignmentOptions.MidlineRight);
            classicLabel.characterSpacing = 10;
            classicLabel.rectTransform.Place(0, 0, 1, 1);
            MenuKit.Clickable(classic, MultiplayerTakeover.UseClassicBrowser);
        }

        private static void Column(RectTransform header, string text, float left, float right, bool fromRight)
        {
            var label = MenuKit.Text(header, text, text, U(12), true, MenuKit.Muted);
            label.characterSpacing = 16;
            if (fromRight) label.rectTransform.Place(1, 0, 1, 1, left, 0, right, 0);
            else label.rectTransform.Place(0, 0, 1, 1, left, 0, right, 0);
        }

        private static void BuildDetails(RectTransform root, float pad, float bottom)
        {
            var panel = MenuKit.PanelBox(root, "Details").rectTransform.Place(1, 0, 1, 1, -(pad + U(440)), bottom, pad, U(130));
            _kind = MenuKit.Text(panel, "Kind", "", U(13), true, MenuKit.Muted);
            _kind.characterSpacing = 18;
            _kind.rectTransform.Strip(U(24), U(16), U(28), U(28));
            _name = MenuKit.Text(panel, "Name", "", U(32), true, MenuKit.Ink);
            _name.rectTransform.Strip(U(42), U(40), U(28), U(28));
            _sub = MenuKit.Text(panel, "Address", "", U(15), false, MenuKit.Soft);
            _sub.rectTransform.Strip(U(84), U(20), U(28), U(28));

            var stats = MenuKit.Box(panel, "Stats").Strip(U(116), U(56), U(28), U(28));
            var names = new[] { "PLAYERS", "PING", "MODSTOKE" };
            for (var i = 0; i < 3; i++)
            {
                var cell = MenuKit.Box(stats, names[i]).Place(i / 3f, 0, (i + 1) / 3f, 1, i == 0 ? 0 : U(1), 0, i == 2 ? 0 : U(1), 0);
                MenuKit.Fill(cell, null, new Color(MenuKit.Ink.r, MenuKit.Ink.g, MenuKit.Ink.b, 0.05f));
                var label = MenuKit.Text(cell, "Label", names[i], U(12), true, MenuKit.Muted);
                label.characterSpacing = 14;
                label.rectTransform.Strip(U(8), U(14), U(10), U(4));
                StatValues[i] = MenuKit.Text(cell, "Value", "", U(18), true, MenuKit.Ink);
                StatValues[i].rectTransform.Strip(U(24), U(24), U(10), U(4));
            }

            var notice = MenuKit.Box(panel, "Notice").Strip(U(186), U(88), U(28), U(28));
            _noticeBox = MenuKit.Fill(notice, null, MenuKit.Amber);
            _noticeBar = MenuKit.Fill(MenuKit.Box(notice, "Bar").Place(0, 0, 0, 1, 0, 0, -U(4), 0), null, MenuKit.Amber);
            _noticeTitle = MenuKit.Text(notice, "Title", "", U(17), true, MenuKit.Ink);
            _noticeTitle.rectTransform.Strip(U(10), U(22), U(18), U(12));
            _noticeText = MenuKit.Text(notice, "Text", "", U(15), false, MenuKit.Soft, TextAlignmentOptions.TopLeft);
            _noticeText.enableWordWrapping = true;
            _noticeText.overflowMode = TextOverflowModes.Truncate;
            _noticeText.rectTransform.Strip(U(34), U(48), U(18), U(12));

            var header = MenuKit.Box(panel, "ModColumns").Strip(U(290), U(16), U(28), U(28));
            _modsHeader = MenuKit.Text(header, "Mod", "MOD", U(12), true, MenuKit.Muted);
            _modsHeader.characterSpacing = 16;
            _modsHeader.rectTransform.Place(0, 0, 1, 1, 0, 0, U(190), 0);
            Column(header, "SERVER", -U(186), U(102), true);
            Column(header, "YOURS", -U(98), 0, true);
            _mods = new ModView[ModRows];
            for (var i = 0; i < ModRows; i++)
            {
                var rect = MenuKit.Box(panel, "Mod " + i).Strip(U(310 + i * 38), U(36), U(28), U(28));
                MenuKit.Fill(MenuKit.Box(rect, "Line").Place(0, 0, 1, 0, 0, 0, 0, -U(1)), null, new Color(MenuKit.Ink.r, MenuKit.Ink.g, MenuKit.Ink.b, 0.08f));
                var view = new ModView { Rect = rect };
                view.Name = MenuKit.Text(rect, "Name", "", U(16), false, MenuKit.Ink);
                view.Name.rectTransform.Place(0, 0, 1, 1, 0, 0, U(190), 0);
                view.Server = MenuKit.Text(rect, "Server", "", U(15), false, MenuKit.Soft);
                view.Server.rectTransform.Place(1, 0, 1, 1, -U(186), 0, U(102), 0);
                view.Yours = MenuKit.Text(rect, "Yours", "", U(15), true, MenuKit.Ink);
                view.Yours.rectTransform.Place(1, 0, 1, 1, -U(98), 0, 0, 0);
                _mods[i] = view;
            }

            var join = MenuKit.Box(panel, "Join").Place(0, 0, 1, 0, U(28), U(20), U(28), -U(78));
            _joinFill = MenuKit.Fill(join, MenuKit.Brush, MenuKit.Ink, true);
            _joinLabel = MenuKit.Text(join, "Label", "JOIN", U(21), true, MenuKit.Dark, TextAlignmentOptions.Center);
            _joinLabel.characterSpacing = 16;
            _joinLabel.rectTransform.Place(0, 0, 1, 1);
            MenuKit.Clickable(join, Join);
        }

        private static void Paint()
        {
            if (_rootObject == null || !_rootObject) return;
            for (var i = 0; i < TabViews.Count; i++)
            {
                var on = i == (int)_tab;
                TabViews[i].Brush.enabled = on;
                TabViews[i].Label.color = on ? MenuKit.Dark : MenuKit.Muted;
                TabViews[i].Label.text = on && Entries.Count > 0 ? TabNames[i] + " <alpha=#99>" + Entries.Count : TabNames[i];
            }
            for (var i = 0; i < _rows.Length; i++) PaintRow(_rows[i], _top + i);
            PaintDetails();
            var refreshing = false;
            try { refreshing = _gui.ActiveBrowser != null && _gui.ActiveBrowser.RefreshInProgress; }
            catch (Exception) { /* The browser reports nothing between refreshes. */ }
            var modstoke = Entries.Count(entry => Read(entry, out _, out var details) && details != null);
            _status.text = (Entries.Count == 1 ? "1 server" : Entries.Count + " servers")
                + (modstoke > 0 ? " · " + modstoke + " running Modstoke" : "")
                + (refreshing ? " · refreshing" : "");
        }

        private static void PaintRow(RowView view, int index)
        {
            var shown = index < Entries.Count;
            if (view.Rect.gameObject.activeSelf != shown) view.Rect.gameObject.SetActive(shown);
            if (!shown) return;
            var entry = Entries[index];
            Ask(entry);
            var (status, color) = Status(entry);
            view.Highlight.enabled = index == _selected;
            view.Name.text = entry.Name;
            view.Kind.text = Kind(entry);
            view.Status.text = status;
            view.Status.color = color;
            view.Marker.color = color;
            view.Players.text = entry.Limit > 0 ? entry.Players + " / " + entry.Limit : entry.Players.ToString();
            var bars = entry.Ping <= 0 ? 0 : entry.Ping < 60 ? 3 : entry.Ping < 110 ? 2 : 1;
            for (var b = 0; b < 3; b++) view.Bars[b].color = b < bars ? MenuKit.Ink : new Color(MenuKit.Ink.r, MenuKit.Ink.g, MenuKit.Ink.b, 0.2f);
            view.Ping.text = entry.Ping > 0 ? entry.Ping.ToString() : "—";
        }

        private static void PaintDetails()
        {
            var has = _selected >= 0 && _selected < Entries.Count;
            var entry = has ? Entries[_selected] : null;
            if (entry == null)
            {
                _kind.text = "";
                _name.text = "No servers yet";
                _sub.text = "Servers appear here as the list refreshes.";
                foreach (var value in StatValues) value.text = "—";
                ShowNotice(null, null, MenuKit.Faint);
                ShowMods(null);
                SetJoin(false, "JOIN");
                return;
            }
            Ask(entry);
            Read(entry, out var answered, out var details);
            _kind.text = (entry.Dedicated != null ? "DEDICATED" : "HOSTED") + (entry.Mode.Length > 0 ? " · " + entry.Mode.ToUpperInvariant() : "")
                + (entry.Password ? " · PASSWORD" : "");
            _name.text = entry.Name;
            _sub.text = entry.Dedicated != null ? entry.Dedicated.Ip + ":" + entry.Dedicated.Port : "Hosted by a player over Steam";
            StatValues[0].text = entry.Limit > 0 ? entry.Players + " / " + entry.Limit : entry.Players.ToString();
            StatValues[1].text = entry.Ping > 0 ? entry.Ping + " ms" : "—";
            StatValues[2].text = details != null ? (details.Loader.Length > 0 ? details.Loader : "Yes")
                : entry.Dedicated == null || answered || entry.LegacyModded ? (entry.LegacyModded ? "Older" : "No") : "…";

            var (title, text, color) = Notice(entry, answered, details);
            ShowNotice(title, text, color);
            ShowMods(details);
            var full = entry.Limit > 0 && entry.Players >= entry.Limit;
            SetJoin(entry.VersionMatch && !full, !entry.VersionMatch ? "OTHER VERSION" : full ? "FULL" : "JOIN");
        }

        private static (string Title, string Text, Color Color) Notice(Entry entry, bool answered, ServerDetails details)
        {
            if (!entry.VersionMatch)
                return ("Different game version", "This server runs another version of Sons of the Forest.", MenuKit.Red);
            if (entry.Dedicated == null)
                return ("Hosted game", "Hosted games don't list their mods yet; a Modstoke host checks them when you join.", MenuKit.Faint);
            if (!Read(entry, out _, out _))
                return ("Checking the server", "Asking it which mods it runs.", MenuKit.Faint);
            if (details == null)
                return answered
                    ? entry.LegacyModded
                        ? ("Older Modstoke", "This server runs a Modstoke version that doesn't list its mods.", MenuKit.Faint)
                        : ("No Modstoke", "This server runs no Modstoke, so mods with their own items won't work there.", MenuKit.Faint)
                    : ("No details", "The server didn't answer the details query.", MenuKit.Faint);

            var mine = ContentAgreement.Fingerprint;
            var local = ServerDetails.LocalMods();
            var missing = ServerBrowserBadges.Missing(details).Select(mod => mod.Id).ToArray();
            if (details.Content.Length > 0 && details.Content != mine)
            {
                var extra = local.Where(own => own.Client == "required"
                    && !details.Mods.Any(mod => string.Equals(mod.Id, own.Id, StringComparison.OrdinalIgnoreCase)))
                    .Select(own => own.Id).ToArray();
                var steps = new List<string>();
                if (missing.Length > 0) steps.Add("Add " + string.Join(", ", missing) + ".");
                if (extra.Length > 0) steps.Add("Turn off " + string.Join(", ", extra) + ".");
                if (steps.Count == 0) steps.Add("Match the versions in the list below.");
                return ("You'd be refused", "Its mods with their own items differ from yours. " + string.Join(" ", steps), MenuKit.Red);
            }
            if (missing.Length > 0)
                return (missing.Length == 1 ? "Needs 1 mod" : "Needs " + missing.Length + " mods", "Add " + string.Join(", ", missing) + " to join.", MenuKit.Amber);
            if (details.Content.Length == 0 && mine.Length > 0)
                return ("Your mods add items", "This server has no mods with their own items, so yours won't exist there.", MenuKit.Amber);
            return ("Your mods match", "You can join with the mods you have.", MenuKit.Green);
        }

        private static void ShowNotice(string title, string text, Color color)
        {
            var shown = title != null;
            _noticeBox.gameObject.SetActive(shown);
            if (!shown) return;
            _noticeBox.color = new Color(color.r, color.g, color.b, 0.13f);
            _noticeBar.color = color;
            _noticeTitle.text = title;
            _noticeTitle.color = Color.Lerp(color, MenuKit.Ink, 0.45f);
            _noticeText.text = text;
        }

        private static void ShowMods(ServerDetails details)
        {
            var mods = details?.Mods ?? Array.Empty<ServerDetails.Mod>();
            _modsHeader.transform.parent.gameObject.SetActive(mods.Count > 0);
            var local = ServerDetails.LocalMods();
            for (var i = 0; i < _mods.Length; i++)
            {
                var view = _mods[i];
                var shown = i < mods.Count;
                view.Rect.gameObject.SetActive(shown);
                if (!shown) continue;
                var mod = mods[i];
                var own = local.FirstOrDefault(entry => string.Equals(entry.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
                view.Name.text = i == _mods.Length - 1 && details.ModCount > _mods.Length
                    ? "and " + (details.ModCount - _mods.Length + 1) + " more"
                    : mod.Id;
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
            _joinFill.color = enabled ? MenuKit.Ink : new Color(MenuKit.Ink.r, MenuKit.Ink.g, MenuKit.Ink.b, 0.1f);
            _joinLabel.color = enabled ? MenuKit.Dark : MenuKit.Faint;
            _joinLabel.text = text;
        }

        private static string Kind(Entry entry)
        {
            var parts = new List<string> { entry.Dedicated != null ? "Dedicated" : "Hosted" };
            if (Read(entry, out _, out var details) && details != null) parts.Add("Modstoke " + details.Loader);
            if (entry.Mode.Length > 0) parts.Add(entry.Mode);
            if (entry.Password) parts.Add("password");
            return string.Join(" · ", parts);
        }

        private static (string Text, Color Color) Status(Entry entry)
        {
            if (!entry.VersionMatch) return ("Other version", MenuKit.Red);
            if (entry.Dedicated == null) return ("Not listed yet", MenuKit.Faint);
            if (!Read(entry, out var answered, out var details)) return ("Checking", MenuKit.Faint);
            if (details == null)
                return answered ? (entry.LegacyModded ? "Older Modstoke" : "Vanilla", MenuKit.Muted) : ("No details", MenuKit.Faint);
            var (hex, verdict) = ServerBrowserBadges.Judge(details);
            var color = ColorUtility.TryParseHtmlString(hex, out var parsed) ? parsed : MenuKit.Ink;
            return (char.ToUpperInvariant(verdict[0]) + verdict.Substring(1), color);
        }

        private static bool Read(Entry entry, out bool answered, out ServerDetails details)
        {
            answered = false;
            details = null;
            return entry?.Key != null && ServerBrowserBadges.TryRead(entry.Key, out answered, out details);
        }
    }
}
