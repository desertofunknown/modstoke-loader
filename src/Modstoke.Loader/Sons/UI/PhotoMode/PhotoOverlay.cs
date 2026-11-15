// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// What photo mode draws over the world besides its panel: the frame's mask and composition guides, the focus
    /// reticle and the controls hint underneath the panel, and the shutter flash and the photo's card on top. The
    /// mask stays with the interface hidden, since it shows what the photo leaves out.
    /// </summary>
    internal sealed class PhotoOverlay : IDisposable
    {
        private const float FlashSeconds = 0.28f, ReticleSeconds = 1f, HintSeconds = 8f, HintFade = 0.5f;
        private const float CardHold = 3.5f, CardFade = 0.3f, SweepSeconds = 1.1f;
        private static readonly Color GuideColor = MenuKit.Faded(0.35f);
        private readonly float _opened;
        private readonly bool _online;
        private RectTransform _under, _over, _guides, _reticle, _hint, _card, _sweep, _sweepBand;
        private readonly Image[] _bars = new Image[4];
        private CanvasGroup _reticleGroup, _hintGroup, _cardGroup;
        private Image _flash;
        private RawImage _thumb;
        private TextMeshProUGUI _cardTitle, _cardName, _cardLine;
        private int _layout = -1;
        private bool _pad;
        private string _guide, _aspect;
        private float _flashAt = -1, _focusAt = -1, _cardAt = -1, _developingAt = -1;

        /// <param name="online">Online, the hint also says that the world keeps going.</param>
        internal PhotoOverlay(bool online)
        {
            _opened = Time.unscaledTime;
            _online = online;
        }

        /// <summary>The part of a <paramref name="width"/> by <paramref name="height"/> frame inside the chosen shape, centred.</summary>
        internal static RectInt Crop(int width, int height, float? aspect)
        {
            if (aspect is not { } shape || shape <= 0) return new RectInt(0, 0, width, height);
            if (shape < (float)width / height)
            {
                var inner = Mathf.Clamp(Mathf.RoundToInt(height * shape), 1, width);
                return new RectInt((width - inner) / 2, 0, inner, height);
            }
            var tall = Mathf.Clamp(Mathf.RoundToInt(width / shape), 1, height);
            return new RectInt(0, (height - tall) / 2, width, tall);
        }

        internal void Tick(PhotoSettings settings, bool hidden, bool follow)
        {
            if (_layout != FrontEnd.LayoutVersion || _pad != FrontEnd.PadActive || _under == null || !_under) Build();
            if (_guide != settings.Guide || _aspect != settings.Aspect) Frame(settings);
            var mask = settings.BlackMask ? Color.black : new Color(0, 0, 0, 0.72f);
            foreach (var bar in _bars) bar.color = mask;
            _guides.gameObject.SetActive(!hidden);
            var now = Time.unscaledTime;

            var reticle = _focusAt < 0 ? 0f : 1f - Mathf.Clamp01((now - _focusAt) / ReticleSeconds);
            _reticleGroup.alpha = hidden ? 0f : Mathf.Max(reticle, follow ? 0.25f : 0f);

            var hint = 1f - Mathf.Clamp01((now - _opened - HintSeconds) / HintFade);
            _hintGroup.alpha = hidden ? 0f : hint;

            if (_flashAt >= 0)
            {
                var t = Mathf.Clamp01((now - _flashAt) / FlashSeconds);
                _flash.color = new Color(1, 1, 1, 0.9f * (1f - Moving.EaseOut(t)));
                _flash.enabled = t < 1f;
                if (t >= 1f) _flashAt = -1;
            }
            PaintCard(now);
            // Screens pushed later are drawn after this layer; the flash and the card stay over them.
            if (_over.GetSiblingIndex() != _over.parent.childCount - 1) _over.SetAsLastSibling();
            if (_under.GetSiblingIndex() != 0) _under.SetAsFirstSibling();
        }

        /// <summary>
        /// The shutter's flash, drawn from this very frame: the photo is taken late in the frame, and the view's
        /// anti-aliasing starts afresh after it.
        /// </summary>
        internal void Flash()
        {
            _flashAt = Time.unscaledTime;
            if (_flash == null || !_flash) return;
            _flash.color = new Color(1, 1, 1, 0.9f);
            _flash.enabled = true;
        }

        /// <summary>Autofocus landed: the reticle shows at the centre and fades.</summary>
        internal void Focused() => _focusAt = Time.unscaledTime;

        internal void Developing()
        {
            _developingAt = Time.unscaledTime;
            _cardAt = -1;
            if (_card == null || !_card) return;
            _thumb.texture = null;
            _thumb.enabled = false;
            _cardTitle.text = Upper(FrontEnd.Text["photo.developing"]);
            _cardTitle.color = MenuKit.Ink;
            _cardName.text = _cardLine.text = "";
        }

        internal void Show(PhotoResult result)
        {
            _developingAt = -1;
            _cardAt = Time.unscaledTime;
            if (_card == null || !_card || result == null) return;
            var text = FrontEnd.Text;
            if (result.Saved)
            {
                _cardTitle.text = Upper(text["photo.saved"]);
                _cardTitle.color = MenuKit.Ink;
                _cardName.text = result.FileName ?? "";
                _cardLine.text = result.InSteam ? text["photo.steamAdded"] : "";
            }
            else
            {
                _cardTitle.text = text.Get("photo.failed", ("reason", result.Error ?? ""));
                _cardTitle.color = MenuKit.Red;
                _cardName.text = _cardLine.text = "";
            }
            var thumb = result.Thumbnail;
            _thumb.texture = thumb;
            _thumb.enabled = thumb != null && thumb;
            if (_thumb.enabled && thumb.width > 0)
                _thumb.rectTransform.sizeDelta = new Vector2(FrontEnd.U(128), FrontEnd.U(128) * thumb.height / thumb.width);
        }

        private void PaintCard(float now)
        {
            var developing = _developingAt >= 0;
            var alpha = developing ? 1f : _cardAt < 0 ? 0f : 1f - Mathf.Clamp01((now - _cardAt - CardHold) / CardFade);
            _cardGroup.alpha = alpha;
            if (_card.gameObject.activeSelf != alpha > 0) _card.gameObject.SetActive(alpha > 0);
            _sweep.gameObject.SetActive(developing);
            if (!developing) return;
            // A light runs along the card's foot while the photo develops, as the front end's loading line does.
            var width = _sweep.rect.width;
            var band = width * 0.3f;
            var t = (now - _developingAt) % SweepSeconds / SweepSeconds;
            _sweepBand.sizeDelta = new Vector2(band, 0);
            _sweepBand.anchoredPosition = new Vector2(Mathf.Lerp(-band, width, t * t * (3f - 2f * t)), 0);
        }

        private void Build()
        {
            Dispose();
            _layout = FrontEnd.LayoutVersion;
            _pad = FrontEnd.PadActive;
            _guide = _aspect = null;
            var canvas = FrontEnd.Canvas();
            _under = MenuKit.Box(canvas, "Photo Frame").Place(0, 0, 1, 1);
            _under.SetAsFirstSibling();
            for (var i = 0; i < _bars.Length; i++) _bars[i] = MenuKit.Fill(MenuKit.Box(_under, "Mask " + i), null, Color.clear);
            _guides = MenuKit.Box(_under, "Guides");
            BuildReticle();
            BuildHint();

            _over = MenuKit.Box(canvas, "Photo Flash").Place(0, 0, 1, 1);
            _flash = MenuKit.Fill(MenuKit.Box(_over, "Flash").Place(0, 0, 1, 1), null, Color.clear);
            _flash.enabled = false;
            BuildCard();
        }

        // Four corner brackets around the centre of the frame.
        private void BuildReticle()
        {
            _reticle = MenuKit.Box(_under, "Reticle");
            _reticle.anchorMin = _reticle.anchorMax = _reticle.pivot = new Vector2(0.5f, 0.5f);
            var side = FrontEnd.U(64);
            _reticle.sizeDelta = new Vector2(side, side);
            _reticleGroup = _reticle.gameObject.AddComponent<CanvasGroup>();
            _reticleGroup.alpha = 0;
            var arm = FrontEnd.U(14);
            var thick = Mathf.Max(1.5f, FrontEnd.U(2));
            foreach (var (x, y) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
            {
                var across = MenuKit.Box(_reticle, "Bracket");
                across.anchorMin = across.anchorMax = across.pivot = new Vector2(x, y);
                across.sizeDelta = new Vector2(arm, thick);
                MenuKit.Fill(across, null, MenuKit.Ink);
                var down = MenuKit.Box(_reticle, "Bracket");
                down.anchorMin = down.anchorMax = down.pivot = new Vector2(x, y);
                down.sizeDelta = new Vector2(thick, arm);
                MenuKit.Fill(down, null, MenuKit.Ink);
            }
        }

        // The camera controls for the device in use, along the foot of the world beside the panel, for a few seconds;
        // online, with a line over them saying the world does not wait.
        private void BuildHint()
        {
            _hint = MenuKit.Box(_under, "Hint").Place(0, 0, 1, 0, FrontEnd.U(628), FrontEnd.U(104), FrontEnd.U(60), -FrontEnd.U(140));
            _hintGroup = _hint.gameObject.AddComponent<CanvasGroup>();
            var text = FrontEnd.Text;
            if (_online)
            {
                var online = MenuKit.Text(_hint, "Online", text["photo.online"], FrontEnd.U(15), false, MenuKit.Soft, TextAlignmentOptions.MidlineLeft, true);
                online.rectTransform.Place(0, 1, 1, 1, 0, FrontEnd.U(4), 0, -FrontEnd.U(28));
            }
            var items = _pad
                ? new List<(string Key, string Label)> { ("LS", text["photo.hint.move"]), ("LT", ""), ("RT", text["photo.hint.upDown"]),
                    ("RS", text["photo.hint.look"]), ("LS", text["photo.hint.faster"]) }
                : new List<(string Key, string Label)> { ("W A S D", text["photo.hint.move"]), ("SPACE", ""), ("CTRL", text["photo.hint.upDown"]),
                    ("RMB", text["photo.hint.look"]), ("WHEEL", text["photo.hint.zoom"]), ("SHIFT", text["photo.hint.faster"]) };
            var height = FrontEnd.U(24);
            var x = 0f;
            foreach (var (key, label) in items)
            {
                var tag = new KeyTag(_hint, "Key", height).Set(key, _pad);
                tag.Rect.At(x, (FrontEnd.U(36) - tag.Size.y) / 2, tag.Size.x, tag.Size.y);
                x += tag.Size.x + FrontEnd.U(label.Length == 0 ? 4 : 8);
                if (label.Length == 0) continue;
                var words = MenuKit.Text(_hint, "Label", Upper(label), FrontEnd.U(15), true, MenuKit.Soft);
                words.characterSpacing = 12;
                var width = words.GetPreferredValues(words.text).x + FrontEnd.U(4);
                words.rectTransform.At(x, 0, width, FrontEnd.U(36));
                x += width + FrontEnd.U(26);
            }
        }

        private void BuildCard()
        {
            var u = (Func<float, float>)FrontEnd.U;
            _card = MenuKit.Box(_over, "Photo").Place(1, 0, 1, 0, -(u(60) + u(380)), u(150), u(60), -(u(150) + u(104)));
            _cardGroup = _card.gameObject.AddComponent<CanvasGroup>();
            MenuKit.Fill(_card, null, MenuKit.Hex(0x050606, 0.86f));
            MenuKit.Hairline(_card, "Top", 0);
            var frame = MenuKit.Box(_card, "Picture").At(u(14), u(16), u(128), u(72));
            MenuKit.Fill(frame, null, MenuKit.Cell);
            _thumb = MenuKit.Box(frame, "Image").gameObject.AddComponent<RawImage>();
            _thumb.raycastTarget = false;
            _thumb.rectTransform.anchorMin = _thumb.rectTransform.anchorMax = _thumb.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _thumb.rectTransform.sizeDelta = new Vector2(u(128), u(72));
            _thumb.enabled = false;
            _cardTitle = MenuKit.Text(_card, "Title", "", u(16), true, MenuKit.Ink, TextAlignmentOptions.TopLeft, true);
            _cardTitle.characterSpacing = 18;
            _cardTitle.enableWordWrapping = true;
            _cardTitle.rectTransform.Place(0, 0, 1, 1, u(158), u(40), u(14), u(18));
            _cardName = MenuKit.Text(_card, "Name", "", u(14), false, MenuKit.Soft, TextAlignmentOptions.MidlineLeft, true);
            _cardName.rectTransform.Place(0, 0, 1, 0, u(158), u(36), u(14), -u(58));
            _cardLine = MenuKit.Text(_card, "Line", "", u(14), false, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
            _cardLine.rectTransform.Place(0, 0, 1, 0, u(158), u(14), u(14), -u(36));
            _sweep = MenuKit.Box(_card, "Developing").Place(0, 0, 1, 0, 0, 0, 0, -Mathf.Max(2f, u(2)));
            _sweep.gameObject.AddComponent<RectMask2D>();
            _sweepBand = MenuKit.Box(_sweep, "Light");
            _sweepBand.anchorMin = Vector2.zero;
            _sweepBand.anchorMax = new Vector2(0, 1);
            _sweepBand.pivot = new Vector2(0, 0.5f);
            MenuKit.Fill(_sweepBand, MenuKit.Bump, MenuKit.Ink);
            _card.gameObject.SetActive(false);
            if (_developingAt >= 0) Developing();
        }

        // The mask bars outside the frame's shape, and the guide lines inside it.
        private void Frame(PhotoSettings settings)
        {
            _guide = settings.Guide;
            _aspect = settings.Aspect;
            float width = Screen.width, height = Screen.height;
            var crop = Crop(Screen.width, Screen.height, settings.AspectRatio);
            Place(_bars[0], 0, 0, crop.x, height);
            Place(_bars[1], crop.xMax, 0, width - crop.xMax, height);
            Place(_bars[2], crop.x, 0, crop.width, crop.y);
            Place(_bars[3], crop.x, crop.yMax, crop.width, height - crop.yMax);

            for (var i = _guides.childCount - 1; i >= 0; i--) Object.Destroy(_guides.GetChild(i).gameObject);
            _guides.anchorMin = _guides.anchorMax = _guides.pivot = Vector2.zero;
            _guides.anchoredPosition = new Vector2(crop.x, crop.y);
            _guides.sizeDelta = new Vector2(crop.width, crop.height);
            float w = crop.width, h = crop.height;
            switch (settings.Guide)
            {
                case "thirds":
                    Lines(w, h, 1f / 3, 2f / 3);
                    break;
                case "golden":
                    Lines(w, h, 0.382f, 0.618f);
                    break;
                case "centre":
                    var arm = FrontEnd.U(28);
                    Line(new Vector2(w / 2 - arm, h / 2), new Vector2(w / 2 + arm, h / 2));
                    Line(new Vector2(w / 2, h / 2 - arm), new Vector2(w / 2, h / 2 + arm));
                    break;
                case "diagonal":
                    Line(Vector2.zero, new Vector2(w, h));
                    Line(new Vector2(0, h), new Vector2(w, 0));
                    break;
            }
        }

        private void Lines(float width, float height, float first, float second)
        {
            foreach (var at in new[] { first, second })
            {
                Line(new Vector2(width * at, 0), new Vector2(width * at, height));
                Line(new Vector2(0, height * at), new Vector2(width, height * at));
            }
        }

        // A hairline from one point to another, in the guide box's own space.
        private void Line(Vector2 from, Vector2 to)
        {
            var rect = MenuKit.Box(_guides, "Line");
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0, 0.5f);
            rect.anchoredPosition = from;
            var span = to - from;
            rect.sizeDelta = new Vector2(span.magnitude, Mathf.Max(1f, FrontEnd.U(1)));
            rect.localEulerAngles = new Vector3(0, 0, Mathf.Atan2(span.y, span.x) * Mathf.Rad2Deg);
            MenuKit.Fill(rect, null, GuideColor);
        }

        private static void Place(Image bar, float x, float y, float width, float height)
        {
            var rect = bar.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(Mathf.Max(0, width), Mathf.Max(0, height));
        }

        private static string Upper(string text) => text.ToUpper(Languages.CultureOf(Languages.Current));

        public void Dispose()
        {
            if (_under != null && _under) Object.Destroy(_under.gameObject);
            if (_over != null && _over) Object.Destroy(_over.gameObject);
            _under = _over = null;
        }
    }
}
