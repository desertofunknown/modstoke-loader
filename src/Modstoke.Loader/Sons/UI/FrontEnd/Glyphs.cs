// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The game's own controller button icons, Xbox or PlayStation to match the controller in hand, looked up by
    /// the names Modstoke's prompts use ("A", "LB", "RT"...).
    /// </summary>
    internal static class Glyphs
    {
        private static readonly Dictionary<string, string> Xbox = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = "XboxAIcon", ["B"] = "XboxBIcon", ["X"] = "XboxXIcon", ["Y"] = "XboxYIcon",
            ["LB"] = "XboxLeftShoulderIcon", ["RB"] = "XboxRightShoulderIcon",
            ["LT"] = "XboxLeftTriggerIcon", ["RT"] = "XboxRightTriggerIcon",
            ["LS"] = "XboxLeftStickIcon", ["RS"] = "XboxRightStickIcon",
            ["VIEW"] = "XboxBackIcon", ["MENU"] = "XboxStartIcon"
        };
        private static readonly Dictionary<string, string> PlayStation = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = "PS4CrossIcon", ["B"] = "PS4CircleIcon", ["X"] = "PS4SquareIcon", ["Y"] = "PS4TriangleIcon",
            ["LB"] = "PS4L1Icon", ["RB"] = "PS4R1Icon", ["LT"] = "PS4L2Icon", ["RT"] = "PS4R2Icon",
            ["LS"] = "XboxLeftStickIcon", ["RS"] = "XboxRightStickIcon",
            ["VIEW"] = "PS4TouchPadIcon", ["MENU"] = "PS4OptionsIcon"
        };
        private static readonly Dictionary<string, Texture2D> Found = new(StringComparer.Ordinal);
        private static float _nextScan;
        private static Sprite _keycap;

        /// <summary>Whether the controller in use is a PlayStation one, by its input layout.</summary>
        internal static bool PlayStationPad
        {
            get
            {
                try
                {
                    var layout = Gamepad.current?.layout ?? "";
                    return layout.Contains("DualShock", StringComparison.OrdinalIgnoreCase)
                        || layout.Contains("DualSense", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        internal static bool TryGet(string button, out Texture2D texture)
        {
            texture = null;
            if (string.IsNullOrEmpty(button)) return false;
            var names = PlayStationPad ? PlayStation : Xbox;
            if (!names.TryGetValue(button, out var name)) return false;
            if (Found.TryGetValue(name, out texture) && texture != null && texture) return true;
            Scan();
            return Found.TryGetValue(name, out texture) && texture != null && texture;
        }

        // The icons are loaded with the game's own input prompts; they are found by name once that has happened.
        private static void Scan()
        {
            var now = Time.unscaledTime;
            if (now < _nextScan) return;
            _nextScan = now + 10f;
            try
            {
                foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>())
                {
                    if (texture == null) continue;
                    var name = texture.name;
                    if (name.Length > 4 && (name.StartsWith("Xbox", StringComparison.Ordinal) || name.StartsWith("PS4", StringComparison.Ordinal)))
                        Found[name] = texture;
                }
            }
            catch (Exception)
            {
                // Prompts then show their text tags.
            }
        }

        /// <summary>A light rounded keycap, drawn once, that stretches to any key name's width.</summary>
        internal static Sprite Keycap
        {
            get
            {
                if (_keycap != null && _keycap) return _keycap;
                const int size = 32, radius = 7;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                texture.hideFlags = HideFlags.HideAndDontSave;
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        // Distance outside the rounded rectangle, smoothed over a pixel for a clean edge.
                        var dx = Mathf.Max(radius - x - 0.5f, 0, x + 0.5f - (size - radius));
                        var dy = Mathf.Max(radius - y - 0.5f, 0, y + 0.5f - (size - radius));
                        var outside = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                        texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(0.5f - outside)));
                    }
                texture.Apply(false, true);
                _keycap = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                    SpriteMeshType.FullRect, new Vector4(radius + 1, radius + 1, radius + 1, radius + 1));
                _keycap.hideFlags = HideFlags.HideAndDontSave;
                return _keycap;
            }
        }
    }

    /// <summary>
    /// A button or key shown in a prompt: the game's icon for that button while a controller is in use, a light
    /// keycap with the key's name otherwise.
    /// </summary>
    internal sealed class KeyTag
    {
        private readonly Image _cap;
        private readonly RawImage _icon;
        private readonly float _height;

        internal KeyTag(Transform parent, string name, float height)
        {
            _height = height;
            Rect = MenuKit.Box(parent, name);
            _cap = MenuKit.Fill(MenuKit.Box(Rect, "Cap").Place(0, 0, 1, 1), Glyphs.Keycap, MenuKit.Ink);
            var icon = MenuKit.Box(Rect, "Icon").Place(0, 0, 1, 1);
            _icon = icon.gameObject.AddComponent<RawImage>();
            _icon.raycastTarget = false;
            _icon.enabled = false;
            Label = MenuKit.Text(Rect, "Label", "", height * 0.5f, true, MenuKit.Dark, TextAlignmentOptions.Center);
            Label.rectTransform.Place(0, 0, 1, 1);
            Rect.sizeDelta = new Vector2(height, height);
        }

        internal RectTransform Rect { get; }
        internal TextMeshProUGUI Label { get; }
        internal Vector2 Size => Rect.sizeDelta;

        /// <summary>Shows <paramref name="key"/>, as an icon when <paramref name="pad"/> and the game has one.</summary>
        internal KeyTag Set(string key, bool pad)
        {
            key ??= "";
            Rect.gameObject.SetActive(key.Length > 0);
            if (key.Length == 0) return this;
            if (pad && Glyphs.TryGet(key, out var texture))
            {
                // The icons have room around their artwork, so they are drawn a little larger than a keycap.
                _icon.texture = texture;
                _icon.enabled = true;
                _cap.enabled = false;
                Label.text = "";
                var side = _height * 1.45f;
                Rect.sizeDelta = new Vector2(side, side);
                return this;
            }
            _icon.enabled = false;
            _cap.enabled = true;
            Label.text = key;
            Rect.sizeDelta = new Vector2(MenuKit.TagWidth(Label, key, _height), _height);
            return this;
        }
    }
}
