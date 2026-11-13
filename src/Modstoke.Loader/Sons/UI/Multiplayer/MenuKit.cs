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
    /// The pieces Modstoke's menus are built from: the game's Roboto Condensed fonts, its brush stroke for primary
    /// actions and its soft brush for glows, in a bone-on-black palette whose surfaces are all the text color at
    /// low opacity laid over a deep veil.
    /// </summary>
    internal static class MenuKit
    {
        // Colors are written as they should look. Modstoke's menus draw on their own screen layer, which shows
        // them exactly; the game's own menu canvas runs through its camera's tone curve and would not.
        internal static readonly Color Ink = Hex(0xECEBE6);
        internal static readonly Color Soft = Hex(0xCFCCC3);
        internal static readonly Color Muted = Hex(0xA9A69D);
        internal static readonly Color Faint = Hex(0x85837C);
        internal static readonly Color Dark = Hex(0x0B0C0C);
        internal static readonly Color Green = Hex(0xA7DCB8);
        internal static readonly Color Amber = Hex(0xF1C674);
        internal static readonly Color Red = Hex(0xF0A08A);
        // Surfaces are the text color made faint, never a grey of their own, so they sit on whatever is behind
        // the veil without turning flat or washed out.
        internal static readonly Color Cell = Faded(0.06f);
        internal static readonly Color Line = Faded(0.10f);
        internal static readonly Color Rule = Faded(0.20f);
        internal static readonly Color BarOff = Faded(0.22f);
        internal static readonly Color RowSelected = Faded(0.07f);
        internal static readonly Color RowHover = Faded(0.045f);
        internal static readonly Color FocusFill = Faded(0.11f);
        internal static readonly Color ChipOff = Faded(0.07f);
        internal static readonly Color Field = Faded(0.07f);
        internal static readonly Color Disabled = Faded(0.14f);
        internal static readonly Color Scrim = Hex(0x050606, 0.76f);
        private const int UiLayer = 5;
        private static Sprite _brush, _fade, _glow;
        private static TMP_FontAsset _bold, _regular;

        internal static Sprite Brush => Live(ref _brush, "Brush Wide");
        /// <summary>The game's soft round brush: a dark one behind a word keeps it readable over the bright world.</summary>
        internal static Sprite Glow => Live(ref _glow, "Brush Radial");
        internal static TMP_FontAsset Bold => LiveFont(ref _bold, MenuTheme.BoldFontName, MenuTheme.HeadingFont);
        internal static TMP_FontAsset Regular => LiveFont(ref _regular, MenuTheme.RegularFontName, MenuTheme.BodyFont) ?? Bold;

        /// <summary>White that fades from solid at its top edge to clear at its bottom, for soft edges.</summary>
        internal static Sprite Fade
        {
            get
            {
                if (_fade != null && _fade) return _fade;
                const int steps = 64;
                var texture = new Texture2D(1, steps, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                texture.hideFlags = HideFlags.HideAndDontSave;
                for (var y = 0; y < steps; y++)
                {
                    // Eased, so the edge has no visible start or end.
                    var t = y / (steps - 1f);
                    texture.SetPixel(0, y, new Color(1, 1, 1, t * t * (3f - 2f * t)));
                }
                texture.Apply(false, true);
                _fade = Sprite.Create(texture, new Rect(0, 0, 1, steps), new Vector2(0.5f, 0.5f));
                _fade.hideFlags = HideFlags.HideAndDontSave;
                return _fade;
            }
        }

        internal static Color Hex(int rgb, float alpha = 1f)
            => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);

        /// <summary>The text color at a low opacity: the surface every row, field and cell is drawn with.</summary>
        internal static Color Faded(float alpha) => Hex(0xECEBE6, alpha);

        /// <summary>An accent laid thinly over the veil, for notices and tinted boxes.</summary>
        internal static Color Toned(int accentRgb, float amount) => Hex(accentRgb, amount * 0.6f);

        /// <summary>A thin horizontal rule at y below the parent's top, inset left and right.</summary>
        internal static Image Hairline(Transform parent, string name, float y, float left = 0, float right = 0)
            => Fill(Box(parent, name).Strip(y, Mathf.Max(1f, FrontEnd.U(1)), left, right), null, Rule);

        /// <summary>
        /// A dark soft glow behind a box, spilling past it by <paramref name="spill"/> on every side. Without the
        /// game's soft brush there is no glow at all, since a plain dark box would look broken.
        /// </summary>
        internal static Image GlowBehind(RectTransform rect, float spill, float alpha = 0.6f)
        {
            var sprite = Glow;
            if (sprite == null) return null;
            var glow = Box(rect, "Glow").Place(0, 0, 1, 1, -spill, -spill, -spill, -spill);
            glow.SetAsFirstSibling();
            return Fill(glow, sprite, new Color(0, 0, 0, alpha));
        }

        /// <summary>
        /// Dims what is behind a full screen of panels, down to a soft edge above the game's bottom strip
        /// (<paramref name="bottom"/> from the screen's lower edge), which stays as the game draws it.
        /// </summary>
        internal static void Backdrop(RectTransform root, float bottom, float fade)
        {
            Fill(Box(root, "Backdrop").Place(0, 0, 1, 1, 0, bottom + fade, 0, 0), null, Scrim);
            Fill(Box(root, "Backdrop edge").Place(0, 0, 1, 0, 0, bottom, 0, -(bottom + fade)), Fade, Scrim);
        }

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

        /// <summary>The width of a key's tag: its name's width with some room, never narrower than it is tall.</summary>
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
