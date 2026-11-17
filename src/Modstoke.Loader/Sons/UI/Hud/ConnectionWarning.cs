// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Localization;
using Modstoke.Sons;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// A small warning on the HUD while this client's link to its server is poor: the server list's ping bars and a word
    /// on what is wrong, on the microphone's dark plate just above where the microphone shows. Nothing shows while the
    /// link is fine. <see cref="ConnectionQuality.Health"/> holds each verdict with hysteresis, so this never flickers.
    /// </summary>
    internal static class ConnectionWarning
    {
        // With the microphone, under the front end's 4500.
        private const int SortingOrder = 130;
        // Design units on the front end's 860-unit screen height; the microphone's plate is centred on the left edge.
        // The ping bars are the server list's: three, 4 wide with 2 between.
        private const float Left = 30, Above = 46, Height = 38, Pad = 12, Bars = 16, Gap = 10, FadeSeconds = 0.18f;
        private static readonly Color Plate = MenuKit.Hex(0x050606, 0.84f);
        private static GameObject _canvas;
        private static RectTransform _root;
        private static CanvasGroup _group;
        private static Image _edge;
        private static Image[] _bars;
        private static TextMeshProUGUI _text;
        private static float _alpha, _drawnAlpha = -1f, _unit, _last, _nextMeasure, _nextRead;
        private static int _screenHeight;
        private static string _shownText;
        private static ConnectionHealth _painted;
        private static bool _failed, _shown;

        internal static void Tick()
        {
            if (_failed || GameBuild.IsDedicatedServer) return;
            var now = Time.unscaledTime;
            var dt = Math.Min(now - _last, 0.1f);
            _last = now;
            var health = ConnectionQuality.Health;
            var wanted = health is ConnectionHealth.HighPing or ConnectionHealth.PacketLoss or ConnectionHealth.Interrupted
                && FrontEnd.Enabled && GameRuntime.IsWorldLoaded && !MicIndicator.Covered();
            if (!wanted && _alpha <= 0f) return;
            try
            {
                Measure(now);
                _alpha = VoiceBars.Step(_alpha, wanted ? 1f : 0f, dt / FadeSeconds);
                Show(_alpha > 0f);
                if (!_shown) return;
                if (_alpha != _drawnAlpha) _group.alpha = _drawnAlpha = _alpha;
                // A new verdict shows at once and its numbers follow the link once a second; fading out, the last words stay.
                if (wanted && (now >= _nextRead || health != _painted))
                {
                    _nextRead = now + 1f;
                    _painted = health;
                    Paint(health);
                }
            }
            catch (Exception error)
            {
                _failed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("The connection warning stopped: " + error);
                if (_canvas != null && _canvas) Object.Destroy(_canvas);
            }
        }

        private static void Paint(ConnectionHealth health)
        {
            ConnectionQuality.TryReadServer(out var link);
            var text = health switch
            {
                ConnectionHealth.Interrupted => LocalizationStartup.Text["net.hud.interrupted"],
                ConnectionHealth.PacketLoss => LocalizationStartup.Text.Get("net.hud.loss", ("percent", (int)Math.Round(link.Loss * 100))),
                _ => LocalizationStartup.Text.Get("net.hud.highPing", ("ms", link.RoundTripMs))
            };
            text = text.ToUpper(Languages.CultureOf(Languages.Current));
            // Nothing arriving reads as no bars at all; otherwise the bars step as the server list's do.
            var lit = health == ConnectionHealth.Interrupted || link.RoundTripMs <= 0 ? 0
                : link.RoundTripMs < 60 ? 3 : link.RoundTripMs < 110 ? 2 : 1;
            _edge.color = health == ConnectionHealth.Interrupted ? MenuKit.Accent : MenuKit.Amber;
            for (var b = 0; b < 3; b++) _bars[b].color = b < lit ? MenuKit.Amber : MenuKit.BarOff;
            if (text == _shownText) return;
            _shownText = text;
            _text.text = text;
            var u = _unit;
            var x = (Pad + Bars + Gap) * u;
            var width = _text.GetPreferredValues(text).x + 2 * u;
            _text.rectTransform.At(x, 0, width, Height * u);
            _root.sizeDelta = new Vector2(x + width + Pad * u, Height * u);
        }

        // Sized from the screen's height as the microphone is, and built again when that changes.
        private static void Measure(float now)
        {
            if (_root is not null && now < _nextMeasure) return;
            _nextMeasure = now + 1f;
            var height = Screen.height;
            if (_root is not null && _root && height == _screenHeight) return;
            if (_canvas == null || !_canvas)
            {
                _canvas = new GameObject("Modstoke Connection");
                Object.DontDestroyOnLoad(_canvas);
                var canvas = _canvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
            }
            if (_root is not null && _root) Object.Destroy(_root.gameObject);
            _screenHeight = height;
            var u = _unit = Math.Max(0.25f, height / FrontEnd.DesignHeight);
            _root = MenuKit.Box(_canvas.transform, "Warning");
            _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0, 0.5f);
            _root.anchoredPosition = new Vector2(Left * u, Above * u);
            _root.sizeDelta = new Vector2((Pad * 2 + Bars) * u, Height * u);
            _group = _root.gameObject.AddComponent<CanvasGroup>();
            MenuKit.GlowBehind(_root, 14 * u, 0.45f);
            MenuKit.Fill(MenuKit.Box(_root, "Plate").Place(0, 0, 1, 1), Glyphs.Keycap, Plate);
            _edge = MenuKit.Fill(MenuKit.Box(_root, "Edge").At(0, Height * u * 0.24f, Math.Max(2f, 3 * u), Height * u * 0.52f), null,
                MenuKit.Amber);
            _bars = new Image[3];
            for (var b = 0; b < 3; b++)
            {
                var bar = MenuKit.Box(_root, "Bar " + b);
                bar.anchorMin = bar.anchorMax = new Vector2(0, 0.5f);
                bar.pivot = Vector2.zero;
                bar.anchoredPosition = new Vector2((Pad + b * 6) * u, -7 * u);
                bar.sizeDelta = new Vector2(4 * u, (6 + b * 4) * u);
                _bars[b] = MenuKit.Fill(bar, null, MenuKit.BarOff);
            }
            _text = MenuKit.Text(_root, "Text", "", 13 * u, true, MenuKit.Ink);
            _text.characterSpacing = 14;
            _drawnAlpha = -1f;
            _shownText = null;
            _painted = ConnectionHealth.Offline;
            _shown = true;
        }

        private static void Show(bool shown)
        {
            if (shown == _shown) return;
            _shown = shown;
            _root.gameObject.SetActive(shown);
        }
    }
}
