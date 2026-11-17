// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Localization;
using Modstoke.Sons;
using Sons.Gui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The local microphone on the HUD, on a small dark plate in the front end's colors so it reads over a bright lake
    /// as well as at night. The microphone is dim while the always-on microphone is quiet, bright while push to talk is
    /// held, and has the voice bars moving with a green mark at its edge while the voice reaches another player; the word
    /// Radio follows it when a radio carries it. Struck through in red, with a red mark, when always-on is chosen but the
    /// game isn't recording. It keeps out of photo mode, the pause and mods menus and a HUD the player has switched off;
    /// its canvas sorts under the front end, so the menus' own sweeps put it away as well.
    /// </summary>
    internal static class MicIndicator
    {
        // With mods' HUDs, under the front end's 4500.
        private const int SortingOrder = 130;
        // Design units on the front end's 860-unit screen height. The microphone stands well over the cap height of
        // the prompt bar's text.
        private const float Left = 30, Height = 38, Pad = 12, Glyph = 24, GlyphWidth = Glyph * 12 / 22, Bars = 16, Gap = 10,
            FadeSeconds = 0.18f;
        private static readonly Color Plate = MenuKit.Hex(0x050606, 0.84f);
        private static readonly Color Dim = MenuKit.Faded(0.5f);
        private static readonly Color Resting = MenuKit.Faded(0.32f);
        private static readonly Color Ready = MenuKit.Faded(0.5f);
        private static GameObject _canvas;
        private static RectTransform _root;
        private static CanvasGroup _group;
        private static Image[] _mic;
        private static Image _edge;
        private static GameObject _slash;
        private static VoiceBars _bars;
        private static TextMeshProUGUI _radio;
        private static float _alpha, _drawnAlpha = -1f, _level, _unit, _nextMeasure;
        private static int _screenHeight;
        private static bool _shown, _atRest, _radioShown;
        private static Look _look;

        private enum Look { None, Quiet, Live, Sending, Down }

        internal static void Tick(float now, float dt)
        {
            var down = PlayerVoice.MicDown;
            var shown = (PlayerVoice.MicOpen || down) && !Covered();
            var sending = shown && PlayerVoice.Transmitting;
            if (!shown && _alpha <= 0f) return;
            Measure(now);
            _alpha = VoiceBars.Step(_alpha, shown ? 1f : 0f, dt / FadeSeconds);
            _level = VoiceBars.Step(_level, sending ? 1f : 0f, dt / (sending ? 0.12f : 0.3f));
            Show(_alpha > 0f);
            if (!_shown) return;
            // An always-on microphone sits here quiet for whole sessions, so nothing is set that hasn't changed.
            if (_alpha != _drawnAlpha) _group.alpha = _drawnAlpha = _alpha;
            if (shown) Apply(down ? Look.Down : sending ? Look.Sending : PlayerVoice.PushToTalk ? Look.Live : Look.Quiet);
            if (_level > 0f || !_atRest)
            {
                _bars.Paint(_level, now);
                _atRest = _level <= 0f;
            }
            var radio = PlayerVoice.Radio && !down;
            if (radio != _radioShown) ShowRadio(radio);
        }

        /// <summary>Takes the indicator off the screen for good, after a failure.</summary>
        internal static void Close()
        {
            if (_canvas != null && _canvas) Object.Destroy(_canvas);
            _canvas = null;
            _root = null;
            _alpha = 0f;
        }

        // Photo mode, the menus over the world, and the game's own switches for its HUD.
        private static bool Covered()
            => PhotoMode.Active || GameRuntime.State.PauseMenuOpen || MenuController.IsVisible || ScreenHost.IsOpen
                || UiManager._allOffUserOverride || UiManager._hudOffUserOverride;

        // Colors for each state, set as it changes. The plate itself never dims, so a quiet microphone on it still reads.
        private static void Apply(Look look)
        {
            if (look == _look) return;
            _look = look;
            var bright = look is Look.Live or Look.Sending;
            foreach (var part in _mic) part.color = bright ? MenuKit.Ink : Dim;
            _bars.Tint(look == Look.Sending ? MenuKit.Green : look == Look.Live ? Ready : Resting);
            _bars.Rect.gameObject.SetActive(look != Look.Down);
            _slash.SetActive(look == Look.Down);
            _edge.enabled = look is Look.Sending or Look.Down;
            _edge.color = look == Look.Down ? MenuKit.Accent : MenuKit.Green;
        }

        // Sized from the screen's height as the front end is, and laid out again when it changes. It sits on the left
        // edge at mid height: mods put their HUDs in bands along the top and bottom, the top right corner or the
        // middle, so this place is clear of any of them however tall their band.
        private static void Measure(float now)
        {
            if (_root is not null && now < _nextMeasure) return;
            _nextMeasure = now + 1f;
            var height = Screen.height;
            if (_root is not null && _root && height == _screenHeight) return;
            if (_canvas == null || !_canvas)
            {
                _canvas = new GameObject("Modstoke Voice");
                Object.DontDestroyOnLoad(_canvas);
                var canvas = _canvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
            }
            if (_root is not null && _root) Object.Destroy(_root.gameObject);
            _screenHeight = height;
            var u = _unit = Math.Max(0.25f, height / FrontEnd.DesignHeight);
            _root = MenuKit.Box(_canvas.transform, "Microphone");
            _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0, 0.5f);
            _root.anchoredPosition = new Vector2(Left * u, 0);
            _group = _root.gameObject.AddComponent<CanvasGroup>();
            MenuKit.GlowBehind(_root, 14 * u, 0.45f);
            MenuKit.Fill(MenuKit.Box(_root, "Plate").Place(0, 0, 1, 1), Glyphs.Keycap, Plate);
            _edge = MenuKit.Fill(MenuKit.Box(_root, "Edge").At(0, Height * u * 0.24f, Math.Max(2f, 3 * u), Height * u * 0.52f), null, MenuKit.Green);
            Microphone(_root, u);
            _bars = new VoiceBars(_root, "Bars", Bars * u);
            _bars.Rect.anchorMin = _bars.Rect.anchorMax = _bars.Rect.pivot = new Vector2(0, 0.5f);
            _bars.Rect.anchoredPosition = new Vector2((Pad + GlyphWidth + Gap) * u, 0);
            _radio = MenuKit.Text(_root, "Radio", "", 13 * u, true, MenuKit.Soft);
            _radio.characterSpacing = 14;
            _drawnAlpha = -1f;
            _atRest = false;
            _look = Look.None;
            _shown = true;
            ShowRadio(PlayerVoice.Radio);
        }

        // A studio microphone drawn from strokes, as the front end draws its chevrons: head, cradle, stand and foot, and
        // the red stroke across it for a microphone that is down. Laid out on a 12 by 22 grid scaled to its height.
        private static void Microphone(RectTransform root, float u)
        {
            var k = Glyph * u / 22f;
            var mic = MenuKit.Box(root, "Glyph");
            mic.anchorMin = mic.anchorMax = mic.pivot = new Vector2(0, 0.5f);
            mic.anchoredPosition = new Vector2(Pad * u, 0);
            mic.sizeDelta = new Vector2(12 * k, 22 * k);
            var stroke = Math.Max(1.5f, 1.8f * k);
            _mic = new[]
            {
                MenuKit.Fill(MenuKit.Box(mic, "Head").At(2.5f * k, 0, 7 * k, 13 * k), Glyphs.Keycap, MenuKit.Ink),
                MenuKit.Fill(MenuKit.Box(mic, "Left").At(0, 7 * k, stroke, 8 * k), null, MenuKit.Ink),
                MenuKit.Fill(MenuKit.Box(mic, "Right").At(12 * k - stroke, 7 * k, stroke, 8 * k), null, MenuKit.Ink),
                MenuKit.Fill(MenuKit.Box(mic, "Cradle").At(0, 15 * k - stroke, 12 * k, stroke), null, MenuKit.Ink),
                MenuKit.Fill(MenuKit.Box(mic, "Stand").At(6 * k - stroke / 2, 15 * k, stroke, 7 * k - stroke), null, MenuKit.Ink),
                MenuKit.Fill(MenuKit.Box(mic, "Foot").At(2.5f * k, 22 * k - stroke, 7 * k, stroke), null, MenuKit.Ink)
            };
            var slash = MenuKit.Box(mic, "Off");
            slash.anchorMin = slash.anchorMax = slash.pivot = new Vector2(0.5f, 0.5f);
            slash.sizeDelta = new Vector2(stroke * 1.5f, 26 * k);
            slash.localEulerAngles = new Vector3(0, 0, 38f);
            MenuKit.Fill(slash, null, MenuKit.Accent);
            _slash = slash.gameObject;
            _slash.SetActive(false);
        }

        private static void ShowRadio(bool radio)
        {
            _radioShown = radio;
            var u = _unit;
            var width = (Pad + GlyphWidth + Gap) * u + _bars.Width;
            if (radio)
            {
                _radio.text = LocalizationStartup.Text["voice.radio"].ToUpper(Languages.CultureOf(Languages.Current));
                var label = _radio.GetPreferredValues(_radio.text).x + 2 * u;
                _radio.rectTransform.At(width + Gap * u, 0, label, Height * u);
                width += Gap * u + label;
            }
            _radio.gameObject.SetActive(radio);
            _root.sizeDelta = new Vector2(width + Pad * u, Height * u);
        }

        private static void Show(bool shown)
        {
            if (shown == _shown) return;
            _shown = shown;
            _root.gameObject.SetActive(shown);
        }
    }
}
