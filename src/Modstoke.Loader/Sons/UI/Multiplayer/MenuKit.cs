// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Builds menu screens from the game's own art: the brush-edged panel, the brush stroke highlight and the
    /// Roboto Condensed fonts, in the colors of the game's menus.
    /// </summary>
    internal static class MenuKit
    {
        internal static readonly Color Ink = Hex(0xECEBE6);
        internal static readonly Color Soft = Hex(0xD9D6CC);
        internal static readonly Color Muted = Hex(0xA9A69D);
        internal static readonly Color Faint = Hex(0x8F8C84);
        internal static readonly Color Dark = Hex(0x0B0C0C);
        internal static readonly Color PanelTint = new(0.035f, 0.039f, 0.039f, 0.9f);
        internal static readonly Color Green = Hex(0xA7DCB8);
        internal static readonly Color Amber = Hex(0xF1C674);
        internal static readonly Color Red = Hex(0xF0A08A);

        private const int UiLayer = 5;
        private static Sprite _brush, _panel;
        private static TMP_FontAsset _bold, _regular;

        internal static Sprite Brush => Live(ref _brush, "Brush Wide");
        internal static Sprite Panel => Live(ref _panel, "Background (400ppu)");
        internal static TMP_FontAsset Bold => LiveFont(ref _bold, MenuTheme.BoldFontName, MenuTheme.HeadingFont);
        internal static TMP_FontAsset Regular => LiveFont(ref _regular, MenuTheme.RegularFontName, MenuTheme.BodyFont) ?? Bold;

        internal static Color Hex(int rgb, float alpha = 1f)
            => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);

        internal static string Html(Color color) => "#" + ColorUtility.ToHtmlStringRGB(color);

        internal static RectTransform Box(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.layer = UiLayer;
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            return rect;
        }

        /// <summary>Anchors a box inside its parent: anchor fractions, then insets from those anchors in canvas units.</summary>
        internal static RectTransform Place(this RectTransform rect, float xMin, float yMin, float xMax, float yMax,
            float left = 0, float bottom = 0, float right = 0, float top = 0)
        {
            rect.anchorMin = new Vector2(xMin, yMin);
            rect.anchorMax = new Vector2(xMax, yMax);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
            return rect;
        }

        /// <summary>A box of fixed size whose top-left corner sits at (x, y) below its parent's top-left corner.</summary>
        internal static RectTransform At(this RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        /// <summary>A box of fixed height spanning its parent's width, its top at y, inset left and right.</summary>
        internal static RectTransform Strip(this RectTransform rect, float y, float height, float left = 0, float right = 0)
        {
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.offsetMin = new Vector2(left, -y - height);
            rect.offsetMax = new Vector2(-right, -y);
            return rect;
        }

        internal static Image Fill(RectTransform rect, Sprite sprite, Color color, bool raycast = false)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        internal static Image PanelBox(Transform parent, string name)
            => Fill(Box(parent, name), Panel, PanelTint);

        internal static TextMeshProUGUI Text(Transform parent, string name, string text, float size, bool bold, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var label = Box(parent, name).gameObject.AddComponent<TextMeshProUGUI>();
            var font = bold ? Bold : Regular;
            if (font != null) label.font = font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = FontStyles.Normal;
            label.color = color;
            label.alignment = align;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = true;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>A click target over a box; the screen draws focus itself, so the button has no transition.</summary>
        internal static Button Clickable(RectTransform rect, Action onClick)
        {
            var hit = rect.gameObject.GetComponent<Image>();
            if (hit == null || !hit)
            {
                hit = rect.gameObject.AddComponent<Image>();
                hit.color = new Color(0, 0, 0, 0);
            }
            hit.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hit;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener((UnityAction)(() => onClick()));
            return button;
        }

        /// <summary>A controller button label drawn as a light pill, like the game's prompt glyphs.</summary>
        internal static RectTransform Glyph(Transform parent, string name, string label, float height)
        {
            var rect = Box(parent, name);
            Fill(rect, Brush, Ink);
            var text = Text(rect, "Label", label, height * 0.5f, true, Dark, TextAlignmentOptions.Center);
            text.rectTransform.Place(0, 0, 1, 1);
            rect.sizeDelta = new Vector2(Mathf.Max(height, text.preferredWidth + height * 0.6f), height);
            return rect;
        }

        private static Sprite Live(ref Sprite cached, string name)
        {
            if (cached != null && cached) return cached;
            try { cached = SUI.SUI.GetSprite(name); }
            catch (Exception) { cached = null; }
            if (cached != null && cached) return cached;
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sprite != null && sprite.name == name) return cached = sprite;
            return null;
        }

        private static TMP_FontAsset LiveFont(ref TMP_FontAsset cached, string name, TMP_FontAsset themed)
        {
            if (cached != null && cached) return cached;
            if (themed != null && themed && themed.name == name) return cached = themed;
            foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (font != null && font.name == name) return cached = font;
            return themed != null && themed ? cached = themed : null;
        }
    }
}
