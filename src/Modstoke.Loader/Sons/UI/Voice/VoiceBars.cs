// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// The voice mark: three rounded bars, the middle one taller, that rise and fall while someone talks. The name tags,
    /// the Players list and the microphone on the HUD all draw it, so talking looks the same everywhere.
    /// </summary>
    internal sealed class VoiceBars
    {
        // Each bar's height at rest, and its wave's speed and offset, so the three never move in step.
        private static readonly float[] Rest = { 0.42f, 0.66f, 0.42f };
        private static readonly float[] Speed = { 11.3f, 8.2f, 13.6f };
        private static readonly float[] Phase = { 0f, 2.1f, 4.4f };
        private readonly RectTransform[] _bars = new RectTransform[3];
        private readonly Image[] _fills = new Image[3];
        private readonly float _height, _bar;

        internal VoiceBars(Transform parent, string name, float height)
        {
            _height = height;
            _bar = Math.Max(1f, height * 0.22f);
            var gap = Math.Max(1f, height * 0.17f);
            Width = _bar * 3 + gap * 2;
            Rect = MenuKit.Box(parent, name);
            Rect.sizeDelta = new Vector2(Width, height);
            for (var i = 0; i < 3; i++)
            {
                var bar = _bars[i] = MenuKit.Box(Rect, "Bar " + i);
                bar.anchorMin = bar.anchorMax = new Vector2(0, 0.5f);
                bar.pivot = new Vector2(0, 0.5f);
                bar.anchoredPosition = new Vector2(i * (_bar + gap), 0);
                _fills[i] = MenuKit.Fill(bar, Glyphs.Keycap, Color.white);
            }
            Paint(0, 0);
        }

        internal RectTransform Rect { get; }
        internal float Width { get; }

        /// <summary>Colors the bars; called when the color changes rather than every frame.</summary>
        internal void Tint(Color color)
        {
            foreach (var fill in _fills) fill.color = color;
        }

        /// <summary>Draws the bars at rest when <paramref name="level"/> is 0 and fully moving at 1.</summary>
        internal void Paint(float level, float now)
        {
            for (var i = 0; i < 3; i++)
            {
                var wave = 0.25f + 0.75f * (0.5f + 0.5f * MathF.Sin(now * Speed[i] + Phase[i]));
                _bars[i].sizeDelta = new Vector2(_bar, _height * (Rest[i] + (wave - Rest[i]) * level));
            }
        }

        // Unity's Mathf is a call into the game that boxes its result, which these per-frame paths stay clear of.
        internal static float Step(float value, float goal, float delta)
            => value < goal ? Math.Min(goal, value + delta) : Math.Max(goal, value - delta);

        internal static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
