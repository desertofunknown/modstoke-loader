// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Modstoke.Localization;
using TMPro;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// The Join | Host switch at the top right of the multiplayer screens: the open section on a brush stroke,
    /// the other one clickable, and the buttons that step between them for the device in use.
    /// </summary>
    internal sealed class SectionNav
    {
        private const float TagHeight = 24, Height = 48;
        private readonly RectTransform _rect;
        private readonly (RectTransform Rect, TextMeshProUGUI Label) _previous, _next, _join, _host;
        private readonly bool _hostOpen;

        internal SectionNav(RectTransform root, float right, float top, bool hostOpen)
        {
            _hostOpen = hostOpen;
            _rect = MenuKit.Box(root, "Sections");
            _rect.anchorMin = _rect.anchorMax = _rect.pivot = new Vector2(1, 1);
            _rect.anchoredPosition = new Vector2(-right, -top);
            _previous = MenuKit.Tag(_rect, "Previous", "LT", FrontEnd.U(TagHeight));
            _join = Item("Join", !hostOpen);
            _host = Item("Host", hostOpen);
            _next = MenuKit.Tag(_rect, "Next", "RT", FrontEnd.U(TagHeight));
        }

        /// <summary>The section that is not open; a click on it switches.</summary>
        internal RectTransform Other => (_hostOpen ? _join : _host).Rect;

        internal void Hover(bool on) => (_hostOpen ? _join : _host).Label.color = on ? MenuKit.Ink : MenuKit.Muted;

        /// <summary>Sets the text and the button labels, then lays the row out to them.</summary>
        internal void Paint(bool pad)
        {
            var culture = Languages.CultureOf(Languages.Current);
            Fit(_join, FrontEnd.Text["common.join"].ToUpper(culture), !_hostOpen);
            Fit(_host, FrontEnd.Text["common.host"].ToUpper(culture), _hostOpen);
            Label(_previous, pad ? "LT" : "");
            Label(_next, pad ? "RT" : "TAB");

            var x = 0f;
            var gap = FrontEnd.U(6);
            foreach (var item in new[] { _previous.Rect, _join.Rect, _host.Rect, _next.Rect })
            {
                if (!item.gameObject.activeSelf) continue;
                var size = item.sizeDelta;
                item.At(x, (FrontEnd.U(Height) - size.y) / 2, size.x, size.y);
                x += size.x + gap;
            }
            _rect.sizeDelta = new Vector2(Mathf.Max(0, x - gap), FrontEnd.U(Height));
        }

        // The open section is bright and underlined, as an open tab is; the other waits in a muted tone.
        private (RectTransform Rect, TextMeshProUGUI Label) Item(string name, bool open)
        {
            var rect = MenuKit.Box(_rect, name);
            if (open)
                MenuKit.Fill(MenuKit.Box(rect, "Under").Place(0, 0, 1, 0, FrontEnd.U(14), FrontEnd.U(6), FrontEnd.U(14),
                    -(FrontEnd.U(6) + Mathf.Max(2f, FrontEnd.U(3)))), null, MenuKit.Ink);
            var label = MenuKit.Text(rect, "Label", "", FrontEnd.U(21), true, open ? MenuKit.Ink : MenuKit.Muted, TextAlignmentOptions.Center);
            label.characterSpacing = 14;
            label.rectTransform.Place(0, 0, 1, 1);
            return (rect, label);
        }

        private static void Fit((RectTransform Rect, TextMeshProUGUI Label) item, string text, bool open)
        {
            item.Label.text = text;
            item.Rect.sizeDelta = new Vector2(item.Label.GetPreferredValues(text).x + FrontEnd.U(28), FrontEnd.U(Height));
        }

        private static void Label((RectTransform Rect, TextMeshProUGUI Label) tag, string text)
        {
            tag.Rect.gameObject.SetActive(text.Length > 0);
            if (text.Length == 0) return;
            tag.Label.text = text;
            tag.Rect.sizeDelta = new Vector2(MenuKit.TagWidth(tag.Label, text, FrontEnd.U(TagHeight)), FrontEnd.U(TagHeight));
        }
    }
}
