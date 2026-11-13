// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TMPro;
using UnityEngine;
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
        // The game's UI draws color values as linear light, so every color here is written as it should look
        // (sRGB) and converted once. Overlays are solid colors: a see-through white would render far too bright.
        internal static readonly Color Ink = Hex(0xECEBE6);
        internal static readonly Color Soft = Hex(0xCFCCC3);
        internal static readonly Color Muted = Hex(0x9D9A92);
        internal static readonly Color Faint = Hex(0x74726C);
        internal static readonly Color Dark = Hex(0x0B0C0C);
        internal static readonly Color Green = Hex(0xA7DCB8);
        internal static readonly Color Amber = Hex(0xF1C674);
        internal static readonly Color Red = Hex(0xF0A08A);
        internal static readonly Color Cell = Hex(0x222322);
        internal static readonly Color Line = Hex(0x2A2B29);
        internal static readonly Color Rule = Hex(0x4A4B47);
        internal static readonly Color BarOff = Hex(0x3C3D3A);
        internal static readonly Color RowSelected = Hex(0x3A3B36);
        internal static readonly Color RowHover = Hex(0x262725);
        internal static readonly Color ChipOff = Hex(0x242524);
        internal static readonly Color Field = Hex(0x1E1F1E);
        internal static readonly Color Disabled = Hex(0x2B2C2A);
        private static readonly Color PanelPlain = Hex(0x141414, 0.94f);
        private const int PanelBase = 0x1B1C1B;

        private const int UiLayer = 5;
        private static Sprite _brush, _panel;
        private static TMP_FontAsset _bold, _regular;

        internal static Sprite Brush => Live(ref _brush, "Brush Wide");
        internal static Sprite Panel => Live(ref _panel, "Background (400ppu)");
        internal static TMP_FontAsset Bold => LiveFont(ref _bold, MenuTheme.BoldFontName, MenuTheme.HeadingFont);
        internal static TMP_FontAsset Regular => LiveFont(ref _regular, MenuTheme.RegularFontName, MenuTheme.BodyFont) ?? Bold;

        internal static Color Hex(int rgb, float alpha = 1f)
            => new(Linear(((rgb >> 16) & 255) / 255f), Linear(((rgb >> 8) & 255) / 255f), Linear((rgb & 255) / 255f), alpha);

        /// <summary>A solid panel-toned color leaning toward an accent, for notices and tinted boxes.</summary>
        internal static Color Toned(int accentRgb, float amount)
        {
            float Mix(int shift) => Linear(Mathf.Lerp((PanelBase >> shift) & 255, (accentRgb >> shift) & 255, amount) / 255f);
            return new Color(Mix(16), Mix(8), Mix(0), 1f);
        }

        // sRGB to linear light.
        private static float Linear(float value)
            => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

        /// <summary>A color as a rich-text tag value, for text that changes color mid-line.</summary>
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

        /// <summary>A box of fixed size whose top-right corner sits at (x, y) inside its parent's top-right corner.</summary>
        internal static RectTransform AtRight(this RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(-x, -y);
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

        internal static Image Fill(RectTransform rect, Sprite sprite, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A brush-edged panel drawn with the same sprite and material as the mods menu's panels.</summary>
        internal static Image PanelBox(Transform parent, string name)
        {
            var image = Box(parent, name).gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            try
            {
                MenuTheme.ApplyPanelBackground(image);
            }
            catch (Exception)
            {
                image.sprite = Panel;
                image.color = PanelPlain;
            }
            return image;
        }

        /// <summary>One line of text. It runs past its box unless <paramref name="ellipsis"/> cuts it with "…".</summary>
        internal static TextMeshProUGUI Text(Transform parent, string name, string text, float size, bool bold, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool ellipsis = false)
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
            // An ellipsis box shorter than a line of text hides the whole line, so it is opt-in.
            label.overflowMode = ellipsis ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;
            label.richText = true;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>A small light tag naming a button or key, with its width fitted to the label.</summary>
        internal static (RectTransform Rect, TextMeshProUGUI Label) Tag(Transform parent, string name, string label, float height)
        {
            var rect = Box(parent, name);
            Fill(rect, null, Ink);
            var text = Text(rect, "Label", label, height * 0.5f, true, Dark, TextAlignmentOptions.Center);
            text.rectTransform.Place(0, 0, 1, 1);
            rect.sizeDelta = new Vector2(TagWidth(text, label, height), height);
            return (rect, text);
        }

        internal static float TagWidth(TextMeshProUGUI text, string label, float height)
            => Mathf.Max(height, text.GetPreferredValues(label).x + height * 0.55f);

        /// <summary>A small "^" drawn from two strokes; flip it with a negative y scale to point down.</summary>
        internal static RectTransform Chevron(Transform parent, string name, float size, Color color)
        {
            var rect = Box(parent, name);
            rect.sizeDelta = new Vector2(size, size);
            foreach (var side in new[] { -1f, 1f })
            {
                var stroke = Box(rect, side < 0 ? "Left" : "Right");
                stroke.anchorMin = stroke.anchorMax = stroke.pivot = new Vector2(0.5f, 0.5f);
                stroke.sizeDelta = new Vector2(size * 0.22f, size * 0.72f);
                stroke.anchoredPosition = new Vector2(side * size * 0.2f, 0);
                stroke.localEulerAngles = new Vector3(0, 0, side * 40f);
                Fill(stroke, null, color);
            }
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
