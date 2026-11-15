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
    /// The loading screen's layer: the picture full-bleed under a deep veil, the place being loaded on the left, one
    /// tip at a time on the right and a quiet progress line along the foot. It is built inactive at the title with
    /// every label it can show already laid out, so a load sets its own texts once and then only switches parts on
    /// and off. Everything is painted from timestamps, so a frame the engine holds for seconds leaves a composed still
    /// rather than a half-played animation.
    /// </summary>
    internal sealed class LoadingView
    {
        internal const float RaiseSeconds = 0.14f, AbortSeconds = 0.2f, HideSeconds = 0.75f;
        // Over the front end (4500), under the mods menu (4600), mod screens (4700) and the console (4800).
        private const int SortingOrder = 4550;
        // Design units, as the front end's layouts at its 860-unit height.
        private const float Side = 60, BlockBottom = 110, BarBottom = 72, RowMiddle = 40, CreditInset = 40;
        private const float TipMin = 380, TipMax = 540, Clearance = 80, EyebrowRule = 22, TagHeight = 22, TipTag = 24;
        private const float ArtFade = 0.6f, PushSeconds = 24f, Push = 0.06f;
        private const float CascadeDelay = 0.25f, CascadeGap = 0.06f, ArriveSeconds = 0.22f, Rise = 14, DetailsFade = 0.2f;
        private const float LiveDelay = 0.5f, LiveFade = 0.3f, PhaseFade = 0.2f, PromptFade = 0.25f, SheenSeconds = 2.6f;
        private const float TipDelay = 0.9f, TipArrive = 0.45f, TipRise = 8, TipStay = 7.5f, TipLeave = 0.3f, TipGap = 0.1f;
        private const float LowerFill = 0.2f, LowerText = 0.25f, LowerDelay = 0.15f, LowerFade = 0.6f;
        // How dark the bottom veil is at the foot and the left wash at the edge, as they should look.
        private const float VeilStrength = 0.92f, WashStrength = 0.28f;

        /// <summary>The phase labels, by their keys under loading.phase.</summary>
        internal static readonly string[] Phases = { "prepare", "server", "connect", "stream", "build", "restore", "place", "joinWorld", "settle", "waitHost" };
        private static readonly string[] ControlLabels = { "inventory", "book", "gps", "next" };
        private static readonly Color Black = MenuKit.Hex(0x050606);
        private static Texture2D _veil, _wash;

        // A fade that writes to its group only when the value moves, so a quiet frame changes nothing on the canvas.
        private sealed class Alpha
        {
            private readonly CanvasGroup _group;
            private float _value = -1;

            internal Alpha(GameObject owner) => _group = owner.AddComponent<CanvasGroup>();

            internal void Set(float value)
            {
                if (Mathf.Abs(value - _value) < 0.002f) return;
                _value = value;
                _group.alpha = value;
            }
        }

        private sealed class ControlView
        {
            internal RectTransform Rect;
            internal KeyTag Pad, Key;
            internal TextMeshProUGUI Label;
            internal float LabelWidth;
            internal ControlKey PadKey, KeyboardKey;
        }

        private sealed class TipView
        {
            internal RectTransform Rect, Header;
            internal Alpha Alpha;
            internal KeyTag Pad, Key;
            internal TextMeshProUGUI Category, Control, Body;
            internal PlannedTip Tip;
        }

        private readonly GameObject _root;
        private readonly RectTransform _block, _fill, _sheen, _row;
        private readonly Alpha _alpha, _artAlpha, _creditAlpha, _liveAlpha, _promptAlpha;
        private readonly RawImage _art;
        private readonly TextMeshProUGUI _eyebrow, _title, _details, _saved, _credit;
        private readonly RectTransform[] _parts = new RectTransform[4];
        private readonly Alpha[] _partAlphas = new Alpha[4];
        private readonly float[] _partBottoms = new float[4];
        private readonly RectTransform[] _phaseLabels = new RectTransform[Phases.Length];
        private readonly Alpha[] _phaseAlphas = new Alpha[Phases.Length];
        private readonly ControlView[] _controls = new ControlView[ControlLabels.Length];
        private readonly TipView[] _tips = new TipView[4];
        private readonly Glitch _glitch;
        private readonly float _tipWidth, _threeLines, _trackWidth;
        private LoadingPicture _picture;
        private Vector2 _fit = Vector2.one;
        private float _raisedAt, _artAt = -1, _lowerAt = -1, _abortAt = -1, _promptOffAt = -1, _detailsAt = -1, _phaseAt, _tipStart, _tipLeave = -1;
        private float _shownAtLower, _lastPush = -1, _lastFill = -1, _nextShift, _phaseRoom = -1;
        private int _phase = -1, _phaseFrom = -1, _tipIndex, _tipCount;
        private bool _pad, _pulsed, _prompting = true, _promptGone;

        internal LoadingView()
        {
            Layout = FrontEnd.LayoutVersion;
            Language = Languages.Current;
            _root = new GameObject("Modstoke Loading");
            // The root is opaque and over the menu until the last line puts it away, so a build that fails halfway
            // must not leave it there with nothing owning it.
            try
            {
                Object.DontDestroyOnLoad(_root);
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
                var rect = _root.GetComponent<RectTransform>();
                _alpha = new Alpha(_root);
                MenuKit.Fill(MenuKit.Box(rect, "Base").Place(0, 0, 1, 1), null, Black);

                // Three canvases, so the push-in redraws one quad and the bar and tips never rebuild the title block.
                var art = Layer(rect, "Art");
                _art = MenuKit.Box(art, "Picture").Place(0, 0, 1, 1).gameObject.AddComponent<RawImage>();
                _art.raycastTarget = false;
                _artAlpha = new Alpha(_art.gameObject);

                var still = Layer(rect, "Still");
                Veil(MenuKit.Box(still, "Bottom veil").Place(0, 0, 1, 0.52f), Ramp(ref _veil, true, VeilStrength));
                Veil(MenuKit.Box(still, "Left wash").Place(0, 0, 0.5f, 1), Ramp(ref _wash, false, WashStrength));
                _block = MenuKit.Box(still, "Place");
                Bottom(_block, U(Side), 0, 0, 0);
                var eyebrowRow = Part(0, "Eyebrow");
                var hairline = Mathf.Max(1f, U(1)) / 2;
                MenuKit.Fill(MenuKit.Box(eyebrowRow, "Rule").Place(0, 0.5f, 0, 0.5f, 0, -hairline, -U(EyebrowRule), -hairline), null, MenuKit.Rule);
                _eyebrow = MenuKit.Text(eyebrowRow, "Text", "", U(14), true, MenuKit.Muted);
                _eyebrow.characterSpacing = 14;
                _eyebrow.rectTransform.Place(0, 0, 1, 1, U(EyebrowRule + 12));
                _title = MenuKit.Text(Part(1, "Title"), "Text", "", U(52), true, MenuKit.Ink, TextAlignmentOptions.BottomLeft, true);
                _title.characterSpacing = 2;
                _title.rectTransform.Place(0, 0, 1, 1);
                _glitch = new Glitch(_title);
                _details = MenuKit.Text(Part(2, "Details"), "Text", "", U(19), false, MenuKit.Soft, TextAlignmentOptions.MidlineLeft, true);
                _details.rectTransform.Place(0, 0, 1, 1);
                _saved = MenuKit.Text(Part(3, "Saved"), "Text", "", U(16), false, MenuKit.Faint, TextAlignmentOptions.MidlineLeft, true);
                _saved.rectTransform.Place(0, 0, 1, 1);
                var credit = MenuKit.Faint;
                credit.a = 0.8f;
                _credit = MenuKit.Text(still, "Credit", "", U(13), false, credit, TextAlignmentOptions.TopRight);
                _credit.rectTransform.AtRight(U(CreditInset), U(CreditInset), U(800), U(20));
                _creditAlpha = new Alpha(_credit.gameObject);

                var live = Layer(rect, "Live");
                var progress = MenuKit.Box(live, "Progress").Place(0, 0, 1, 1);
                _liveAlpha = new Alpha(progress.gameObject);
                var thickness = Mathf.Max(2f, U(2));
                _trackWidth = Screen.width - 2 * U(Side);
                var track = MenuKit.Box(progress, "Track").Place(0, 0, 1, 0, U(Side), U(BarBottom), U(Side), -(U(BarBottom) + thickness));
                MenuKit.Fill(track, null, MenuKit.Line);
                _fill = MenuKit.Box(track, "Fill");
                _fill.anchorMin = Vector2.zero;
                _fill.anchorMax = new Vector2(0, 1);
                _fill.offsetMin = _fill.offsetMax = Vector2.zero;
                MenuKit.Fill(_fill, null, MenuKit.Faded(0.9f));
                _fill.gameObject.AddComponent<RectMask2D>();
                _sheen = MenuKit.Box(_fill, "Sheen");
                _sheen.anchorMin = Vector2.zero;
                _sheen.anchorMax = new Vector2(0, 1);
                _sheen.pivot = new Vector2(0, 0.5f);
                // White rather than the fill's own bone, or it would not show over a fill nearly as bright.
                MenuKit.Fill(_sheen, MenuKit.Bump, new Color(1, 1, 1, 0.5f));
                for (var i = 0; i < Phases.Length; i++)
                {
                    var label = MenuKit.Text(progress, Phases[i], Upper(FrontEnd.Text["loading.phase." + Phases[i]]), U(14), true, MenuKit.Muted,
                        TextAlignmentOptions.MidlineLeft, true);
                    label.characterSpacing = 14;
                    Bottom(label.rectTransform, U(Side), U(RowMiddle) - U(10), U(700), U(20));
                    _phaseLabels[i] = label.rectTransform;
                    _phaseAlphas[i] = new Alpha(label.gameObject);
                }
                _row = MenuKit.Box(progress, "Controls");
                _row.anchorMin = _row.anchorMax = _row.pivot = new Vector2(1, 0);
                _row.sizeDelta = new Vector2(0, U(TagHeight) * 1.5f);
                PlaceRow(0);
                for (var i = 0; i < _controls.Length; i++)
                {
                    var view = _controls[i] = new ControlView { Rect = MenuKit.Box(_row, ControlLabels[i]) };
                    view.Rect.anchorMin = view.Rect.anchorMax = view.Rect.pivot = new Vector2(1, 0);
                    view.Pad = new KeyTag(view.Rect, "Pad", U(TagHeight));
                    view.Key = new KeyTag(view.Rect, "Key", U(TagHeight));
                    view.Label = MenuKit.Text(view.Rect, "Label", Upper(FrontEnd.Text["loading.control." + ControlLabels[i]]), U(16), true, MenuKit.Soft);
                    view.Label.characterSpacing = 14;
                    // Measured once here: a device switch mid-load only moves what is already laid out.
                    view.LabelWidth = view.Label.GetPreferredValues(view.Label.text).x + U(4);
                }
                var next = _controls[_controls.Length - 1];
                _promptAlpha = new Alpha(next.Rect.gameObject);
                // Next tip is on the front end's next-tab button and key.
                next.PadKey = new ControlKey("RB", "RB");
                next.KeyboardKey = new ControlKey(null, "E");

                _tipWidth = U(Mathf.Clamp(Screen.width / FrontEnd.S * 0.3f, TipMin, TipMax));
                for (var i = 0; i < _tips.Length; i++) _tips[i] = Tip(live, i);
                _threeLines = _tips[0].Body.GetPreferredValues("A\nA\nA", _tipWidth, 0).y;
                // The title's glitch copies are made here at the title, not at the first raise.
                _glitch.Prepare();
                _root.SetActive(false);
            }
            catch
            {
                Object.Destroy(_root);
                throw;
            }
        }

        /// <summary>The front end's layout version it was built at; another one means building again.</summary>
        internal int Layout { get; }
        internal string Language { get; }
        internal bool Alive => _root != null && _root;

        /// <summary>Whether the layer covers the whole screen, so what is under it can go.</summary>
        internal bool Opaque(float now) => _lowerAt < 0 && _abortAt < 0 && now - _raisedAt >= RaiseSeconds;

        private static float U(float design) => FrontEnd.U(design);

        private static string Upper(string text) => text.ToUpper(Languages.CultureOf(Languages.Current));

        /// <summary>The controls reminder: Inventory, the guide book and the GPS as bound on each device right now.</summary>
        internal void SetControls(ControlKey[] pad, ControlKey[] keyboard)
        {
            var culture = Languages.CultureOf(Languages.Current);
            for (var i = 0; i < _controls.Length - 1; i++)
            {
                _controls[i].PadKey = pad[i];
                _controls[i].KeyboardKey = keyboard[i];
            }
            foreach (var view in _controls)
            {
                ControlGlyphs.Find(view.PadKey);
                ControlGlyphs.Find(view.KeyboardKey);
                if (view.PadKey.Bound) ControlGlyphs.Show(view.Pad, view.PadKey, culture);
                if (view.KeyboardKey.Bound) ControlGlyphs.Show(view.Key, view.KeyboardKey, culture);
            }
            ApplyDevice();
        }

        /// <summary>
        /// Sets this load's texts and starts the raise: the base fades in over the menu, or is there at once with
        /// <paramref name="opaque"/>. Nothing on the layer is created after this.
        /// </summary>
        internal void Begin(float now, LoadingIntent intent, LoadingPicture picture, List<PlannedTip> tips, bool pad, bool opaque)
        {
            _raisedAt = opaque ? now - RaiseSeconds : now;
            _artAt = _lowerAt = _abortAt = _promptOffAt = _detailsAt = _lastPush = _lastFill = _tipLeave = -1;
            _tipIndex = 0;
            _phase = _phaseFrom = -1;
            _pulsed = _promptGone = false;
            _prompting = true;
            _eyebrow.text = Upper(intent.Eyebrow());
            _title.text = Upper(intent.Title());
            _details.text = intent.Details();
            _saved.text = intent.SavedLine();
            LayOut();
            SetPicture(picture);
            _tipCount = tips.Count;
            for (var i = 0; i < _tips.Length; i++) SetTip(_tips[i], i < tips.Count ? tips[i] : null);
            _pad = pad;
            ApplyDevice();
            _alpha.Set(opaque ? 1 : 0);
            _artAlpha.Set(0);
            _creditAlpha.Set(0);
            _liveAlpha.Set(0);
            _promptAlpha.Set(1);
            foreach (var alpha in _partAlphas) alpha.Set(0);
            foreach (var alpha in _phaseAlphas) alpha.Set(0);
            _fill.anchorMax = new Vector2(0, 1);
            _root.SetActive(true);
            // The title's glitch copies take this load's title now, rather than when it lands.
            _glitch.Prepare();
        }

        /// <summary>The loading scene arrived while the overlay was still fading in; it covers the screen at once.</summary>
        internal void Snap(float now) => _raisedAt = Mathf.Min(_raisedAt, now - RaiseSeconds);

        /// <summary>The picture fades up, the texts follow, then the tips; the push-in starts.</summary>
        internal void StartArt(float now)
        {
            if (_artAt >= 0) return;
            _artAt = now;
            _tipStart = now + TipDelay;
        }

        /// <summary>A details line known only after the raise, as a season read late; it fades in again.</summary>
        internal void UpdateDetails(string details, float now)
        {
            var placed = _details.text.Length > 0;
            _details.text = details;
            if (!placed) LayOut();
            if (_artAt >= 0) _detailsAt = now;
        }

        internal void Lower(float now, float shown)
        {
            if (_lowerAt >= 0) return;
            _lowerAt = now;
            _shownAtLower = shown;
        }

        internal void Abort(float now)
        {
            if (_abortAt < 0) _abortAt = now;
        }

        /// <summary>Moves on to the next tip once the current one has arrived.</summary>
        internal void NextTip(float now)
        {
            if (_artAt < 0 || _tipLeave >= 0 || _tipCount < 2 || now - _tipStart < TipArrive) return;
            _tipLeave = now;
        }

        /// <summary>Puts the layer away and lets go of its picture, which may be destroyed after this.</summary>
        internal void Hide()
        {
            if (Alive) _root.SetActive(false);
            SetPicture(null);
        }

        internal void Destroy()
        {
            if (Alive) Object.Destroy(_root);
        }

        /// <summary>
        /// Draws the moment <paramref name="now"/>: the root's fade, the picture's push-in, the arrivals, the bar at
        /// <paramref name="shown"/>, the phase label named <paramref name="phase"/> (one of <see cref="Phases"/>) and the
        /// keys of the device in use.
        /// </summary>
        internal void Paint(float now, float shown, string phase, bool pad, bool prompting)
        {
            float root;
            if (_abortAt >= 0) root = 1f - Mathf.Clamp01((now - _abortAt) / AbortSeconds);
            else if (_lowerAt >= 0) root = 1f - Smooth((now - _lowerAt - LowerDelay) / LowerFade);
            else root = Mathf.Clamp01((now - _raisedAt) / RaiseSeconds);
            _alpha.Set(root);
            if (pad != _pad)
            {
                _pad = pad;
                ApplyDevice();
            }
            if (_artAt < 0) return;
            var t = now - _artAt;
            // Text and the strip leave quickly once the world is ready; the picture stays to the last.
            var leaving = _lowerAt >= 0 ? 1f - Mathf.Clamp01((now - _lowerAt) / LowerText) : 1f;

            var art = Motion(t / ArtFade);
            _artAlpha.Set(art);
            _creditAlpha.Set(art * leaving);
            PushIn(t);
            for (var i = 0; i < _parts.Length; i++)
            {
                var arrive = Motion((t - CascadeDelay - i * CascadeGap) / ArriveSeconds);
                var again = i == 2 && _detailsAt >= 0 ? Mathf.Clamp01((now - _detailsAt) / DetailsFade) : 1f;
                _partAlphas[i].Set(arrive * again * leaving);
                Move(_parts[i], _partBottoms[i] - U(Rise) * (1f - arrive));
            }
            if (!_pulsed && t >= CascadeDelay + CascadeGap + ArriveSeconds)
            {
                _pulsed = true;
                _glitch.Pulse(false);
            }

            _liveAlpha.Set(Mathf.Clamp01((t - LiveDelay) / LiveFade) * leaving);
            PaintBar(now, _lowerAt >= 0 ? Mathf.Lerp(_shownAtLower, 1f, Mathf.Clamp01((now - _lowerAt) / LowerFill)) : shown);
            PaintPhase(now, Array.IndexOf(Phases, phase));
            if (_prompting && !prompting)
            {
                _prompting = false;
                _promptOffAt = now;
            }
            if (_promptOffAt >= 0 && !_promptGone)
            {
                // The rest of the row slides over to the right edge as Next tip fades, then is laid out without it.
                var gone = Motion((now - _promptOffAt) / PromptFade);
                _promptAlpha.Set(1f - gone);
                PlaceRow(_nextShift * gone);
                if (gone >= 1f)
                {
                    _promptGone = true;
                    ApplyDevice();
                }
            }
            PaintTips(now, leaving);
        }

        private static RectTransform Layer(RectTransform root, string name)
        {
            var layer = MenuKit.Box(root, name).Place(0, 0, 1, 1);
            layer.gameObject.AddComponent<Canvas>();
            return layer;
        }

        private RectTransform Part(int index, string name)
        {
            var part = _parts[index] = MenuKit.Box(_block, name);
            _partAlphas[index] = new Alpha(part.gameObject);
            return part;
        }

        private TipView Tip(RectTransform parent, int index)
        {
            var rect = MenuKit.Box(parent, "Tip " + index);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-U(Side), U(BlockBottom));
            var view = new TipView { Rect = rect, Alpha = new Alpha(rect.gameObject), Header = MenuKit.Box(rect, "Header") };
            view.Category = MenuKit.Text(view.Header, "Category", "", U(14), true, MenuKit.Muted);
            view.Category.characterSpacing = 14;
            view.Category.rectTransform.Place(0, 0, 1, 1);
            view.Pad = new KeyTag(view.Header, "Pad", U(TipTag));
            view.Key = new KeyTag(view.Header, "Key", U(TipTag));
            view.Control = MenuKit.Text(view.Header, "Control", "", U(16), true, MenuKit.Soft);
            view.Control.characterSpacing = 14;
            view.Body = MenuKit.Text(rect, "Body", "", U(20), false, MenuKit.Soft, TextAlignmentOptions.TopLeft, true);
            view.Body.enableWordWrapping = true;
            view.Body.lineSpacing = 4;
            rect.gameObject.SetActive(false);
            return view;
        }

        // Bottom up from 110 units above the foot: the saved line and the details when there are any, the title, the
        // eyebrow. The title gives way to the tip column on screens narrower than 16:9.
        private void LayOut()
        {
            var tipLeft = Screen.width - U(Side) - _tipWidth;
            var width = Mathf.Min(Screen.width * 0.55f, tipLeft - U(Clearance) - U(Side));
            var saved = _saved.text.Length > 0;
            var details = _details.text.Length > 0;
            _parts[3].gameObject.SetActive(saved);
            _parts[2].gameObject.SetActive(details);
            // The two small lines get boxes taller than their type needs: an ellipsis also cuts a line that overflows its
            // box upwards, and a 19-unit line in a 26-unit box was cut to its first word.
            var y = U(BlockBottom);
            Size(3, width, U(24), y);
            if (saved) y += U(24);
            Size(2, width, U(30), y);
            if (details) y += U(30);
            Size(1, width, U(62), y);
            y += U(62) + U(6);
            Size(0, width, U(18), y);
        }

        private void Size(int index, float width, float height, float bottom)
        {
            _partBottoms[index] = bottom;
            Bottom(_parts[index], 0, bottom, width, height);
        }

        private void SetPicture(LoadingPicture picture)
        {
            _picture = picture;
            var texture = picture?.Texture;
            _art.texture = texture;
            _art.enabled = texture != null;
            var photo = picture != null && picture.Photo && !string.IsNullOrEmpty(picture.Where);
            _credit.gameObject.SetActive(photo);
            if (photo) _credit.text = FrontEnd.Text.Get("loading.yourPhoto", ("where", picture.Where));
            if (texture == null) return;
            // Aspect-fill: the share of the picture's width and height the screen shows before the push-in.
            var screen = Screen.width / (float)Mathf.Max(1, Screen.height);
            var image = texture.width / (float)Mathf.Max(1, texture.height);
            _fit = screen >= image ? new Vector2(1f, image / screen) : new Vector2(screen / image, 1f);
        }

        private void SetTip(TipView view, PlannedTip tip)
        {
            view.Tip = tip;
            view.Rect.gameObject.SetActive(false);
            if (tip == null) return;
            var culture = Languages.CultureOf(Languages.Current);
            view.Body.text = tip.Text;
            // At most three lines; the third ends in "…" when the text runs longer.
            var body = Mathf.Min(view.Body.GetPreferredValues(tip.Text, _tipWidth, 0).y, _threeLines);
            Bottom(view.Body.rectTransform, 0, 0, _tipWidth, body);
            view.Category.text = tip.Category.ToUpper(culture);
            view.Control.text = tip.Control?.ToUpper(culture) ?? "";
            if (tip.Pad.Bound) ControlGlyphs.Show(view.Pad, tip.Pad, culture);
            if (tip.Key.Bound) ControlGlyphs.Show(view.Key, tip.Key, culture);
            var header = U(TipTag) * 1.45f;
            Bottom(view.Header, 0, body + U(10), _tipWidth, header);
            view.Rect.sizeDelta = new Vector2(_tipWidth, body + U(10) + header);
        }

        // A control tip's header shows its key on this device, or the tip's category where the device has nothing bound;
        // the reminder leaves out what this device has no binding for, and Next tip once it has gone, and closes up
        // toward the right.
        private void ApplyDevice()
        {
            foreach (var view in _tips)
            {
                if (view.Tip == null) continue;
                var key = _pad ? view.Tip.Pad : view.Tip.Key;
                var control = view.Tip.Control != null && key.Bound;
                view.Category.gameObject.SetActive(!control);
                view.Control.gameObject.SetActive(control);
                view.Pad.Rect.gameObject.SetActive(control && _pad);
                view.Key.Rect.gameObject.SetActive(control && !_pad);
                if (!control) continue;
                var size = (_pad ? view.Pad : view.Key).Size;
                var height = U(TipTag) * 1.45f;
                Bottom((_pad ? view.Pad : view.Key).Rect, 0, (height - size.y) / 2, size.x, size.y);
                Bottom(view.Control.rectTransform, size.x + U(10), 0, _tipWidth - size.x - U(10), height);
            }
            var right = 0f;
            var last = _controls.Length - 1;
            _nextShift = 0;
            for (var i = last; i >= 0; i--)
            {
                var view = _controls[i];
                var key = _pad ? view.PadKey : view.KeyboardKey;
                var shown = key.Bound && (i < last || !_promptGone);
                view.Rect.gameObject.SetActive(shown);
                if (!shown) continue;
                view.Pad.Rect.gameObject.SetActive(_pad);
                view.Key.Rect.gameObject.SetActive(!_pad);
                var tag = _pad ? view.Pad : view.Key;
                var size = tag.Size;
                var height = U(TagHeight) * 1.5f;
                Bottom(tag.Rect, 0, (height - size.y) / 2, size.x, size.y);
                Bottom(view.Label.rectTransform, size.x + U(10), 0, view.LabelWidth, height);
                var width = size.x + U(10) + view.LabelWidth;
                view.Rect.anchoredPosition = new Vector2(-right, 0);
                view.Rect.sizeDelta = new Vector2(width, height);
                right += width + U(30);
                if (i == last) _nextShift = right;
            }
            PlaceRow(0);
            // The phase label stops short of the row, ending in "…" where a long one on a narrow screen would reach it.
            var room = Screen.width - 2 * U(Side) - right;
            if (Mathf.Abs(room - _phaseRoom) < 0.5f) return;
            _phaseRoom = room;
            foreach (var label in _phaseLabels) label.sizeDelta = new Vector2(room, U(20));
        }

        // The controls row at the right margin, or moved right by shift.
        private void PlaceRow(float shift) => _row.anchoredPosition = new Vector2(-U(Side) + shift, U(RowMiddle) - U(TagHeight) * 0.75f);

        // The picture grows 6 % over 24 s around its focal point, kept inside the picture at every step.
        private void PushIn(float t)
        {
            if (_picture?.Texture == null) return;
            var scale = 1f + Push * Mathf.Clamp01(t / PushSeconds);
            if (Mathf.Abs(scale - _lastPush) < 0.00002f) return;
            _lastPush = scale;
            var w = _fit.x / scale;
            var h = _fit.y / scale;
            var x = Mathf.Clamp(_picture.Focus.x - w / 2, 0, 1 - w);
            var y = Mathf.Clamp(_picture.Focus.y - h / 2, 0, 1 - h);
            // y runs from the top here; a picture stored top row first is sampled upside down.
            _art.uvRect = _picture.TopDown ? new Rect(x, y + h, w, -h) : new Rect(x, 1 - y - h, w, h);
        }

        // The fill follows the bar; a soft sheen crosses the filled part every 2.6 s, clipped to it.
        private void PaintBar(float now, float shown)
        {
            if (Mathf.Abs(shown - _lastFill) >= 0.0005f)
            {
                _lastFill = shown;
                _fill.anchorMax = new Vector2(shown, 1);
            }
            var filled = _trackWidth * shown;
            var band = Mathf.Max(filled * 0.16f, U(60));
            var p = now % SheenSeconds / SheenSeconds;
            _sheen.sizeDelta = new Vector2(band, 0);
            _sheen.anchoredPosition = new Vector2(Mathf.Lerp(-band, filled, p * p * (3f - 2f * p)), 0);
        }

        private void PaintPhase(float now, int phase)
        {
            if (phase != _phase)
            {
                if (_phaseFrom >= 0) _phaseAlphas[_phaseFrom].Set(0);
                _phaseFrom = _phase;
                _phase = phase;
                _phaseAt = now;
            }
            if (_phase < 0) return;
            var t = Mathf.Clamp01((now - _phaseAt) / PhaseFade);
            _phaseAlphas[_phase].Set(t);
            if (_phaseFrom >= 0) _phaseAlphas[_phaseFrom].Set(1f - t);
        }

        // One tip at a time: it rises in, stays 7.5 s, leaves, and the next follows; the planned tips cycle.
        private void PaintTips(float now, float leaving)
        {
            if (_tipCount == 0 || now < _tipStart) return;
            var view = _tips[_tipIndex];
            if (!view.Rect.gameObject.activeSelf)
            {
                view.Rect.gameObject.SetActive(true);
                LoadingTips.Shown(view.Tip.Id);
            }
            if (_tipLeave < 0 && _tipCount > 1 && now - _tipStart >= TipArrive + TipStay) _tipLeave = now;
            if (_tipLeave >= 0 && now - _tipLeave >= TipLeave + TipGap)
            {
                view.Rect.gameObject.SetActive(false);
                _tipIndex = (_tipIndex + 1) % _tipCount;
                _tipStart = now;
                _tipLeave = -1;
                return;
            }
            var arrive = Motion((now - _tipStart) / TipArrive);
            var gone = _tipLeave >= 0 ? Mathf.Clamp01((now - _tipLeave) / TipLeave) : 0f;
            view.Alpha.Set(arrive * (1f - gone) * leaving);
            Move(view.Rect, U(BlockBottom) - U(TipRise) * (1f - arrive));
        }

        private static void Move(RectTransform rect, float bottom)
        {
            var position = rect.anchoredPosition;
            if (Mathf.Abs(position.y - bottom) < 0.05f) return;
            rect.anchoredPosition = new Vector2(position.x, bottom);
        }

        private static void Bottom(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static float Motion(float t) => Moving.EaseOut(Mathf.Clamp01(t));

        private static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static void Veil(RectTransform rect, Texture2D ramp)
        {
            var image = rect.gameObject.AddComponent<RawImage>();
            image.texture = ramp;
            image.raycastTarget = false;
        }

        // A strip solid at its bottom or left edge, easing to clear: the flipped MenuKit.Fade. Darkness is written as it
        // should look, but this layer blends in linear light, where a black veil darkens far less than its alpha says
        // (MenuKit.Band's note), so each step's alpha is 1 - (1 - look)^2.2.
        private static Texture2D Ramp(ref Texture2D cached, bool vertical, float strength)
        {
            if (cached != null && cached) return cached;
            const int steps = 64;
            var texture = new Texture2D(vertical ? 1 : steps, vertical ? steps : 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            texture.hideFlags = HideFlags.HideAndDontSave;
            for (var i = 0; i < steps; i++)
            {
                var t = i / (steps - 1f);
                var look = strength * (1f - t * t * (3f - 2f * t));
                var color = new Color(Black.r, Black.g, Black.b, 1f - Mathf.Pow(1f - look, 2.2f));
                if (vertical) texture.SetPixel(0, i, color);
                else texture.SetPixel(i, 0, color);
            }
            texture.Apply(false, true);
            return cached = texture;
        }
    }
}
