// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Modstoke.Localization;
using Sons.Gui;
using Sons.Gui.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// One entry of the game's message list as a toast: the game's icon, its text and the key or button its prompt names,
    /// on a dark plate edged in the front end's red. It comes in when the game shows the entry and goes when the game starts
    /// sliding it out, struck through first where the game strikes the entry out. Main thread only.
    /// </summary>
    internal sealed class Toast
    {
        private enum Phase { In, Shown, Struck, Out }

        // Design units, scaled as the front end's are. The keycap stands a little taller than a line of text and its
        // letter close to the text's own size, so the key reads at a glance.
        private const float Edge = 2f, Pad = 12f, IconSide = 22f, BadgeSide = 11f, Gap = 10f, TextSize = 16.5f, KeyHeight = 25f,
            KeyLetter = 14f, MaxText = 360f, MinHeight = 40f, Rise = 9f, Spacing = 8f, Arrive = 18f, Depart = 12f, Spill = 16f,
            Stroke = 1.5f;
        // A strike draws quickly and then holds, long enough to register before the toast leaves.
        private const float InSeconds = 0.32f, OutSeconds = 0.24f, StrikeSeconds = 0.2f, StrikeHold = 0.4f, PunchSeconds = 0.25f;
        private static readonly Color PlateColor = MenuKit.Hex(0x050606, 0.78f);
        private static int _textOffset;

        private readonly GameObject _entry;
        private readonly AnimateSidebarElement _motion;
        private readonly TMP_Text _source;
        private readonly RawImage _sourceIcon, _sourceBadge;
        private readonly DynamicInputIcon[] _prompts;
        private readonly RectTransform _parent;
        private readonly Ease _slot = new(14f);
        private RectTransform _rect, _edge, _strike;
        private GameObject _rectObject;
        private Image _edgeImage;
        private CanvasGroup _group, _content;
        private RawImage _icon, _badge;
        private TextMeshProUGUI _text;
        private KeyTag _key;
        private ControlKey _control;
        private IntPtr _shownText;
        private Phase _phase;
        private float _u, _since, _from, _alpha = -1f, _x, _placedX = float.NaN, _placedY = float.NaN, _height, _textWidth, _punch = -1f;
        private bool _pad, _empty, _visible = true, _pulsing, _stale;

        private Toast(RectTransform parent, Transform entry, AnimateSidebarElement motion, TMP_Text source, RawImage icon,
            RawImage badge, DynamicInputIcon[] prompts, float u, bool pad, float now)
        {
            Entry = entry;
            _entry = entry.gameObject;
            _motion = motion;
            _source = source;
            _sourceIcon = icon;
            _sourceBadge = badge;
            _prompts = prompts;
            _parent = parent;
            _pad = pad;
            _control = Prompt(pad);
            _u = u;
            Build();
            _shownText = TextOf(source);
            Retext();
            // From no room at all, so the toasts above make way for it rather than jump.
            _slot.Set(0f, true);
            Begin(Phase.In, now);
        }

        /// <summary>The game's entry: a child of its message list.</summary>
        internal Transform Entry { get; }

        /// <summary>Where the entry stands in the game's list, the first at the top.</summary>
        internal int Sibling { get; private set; }

        /// <summary>A toast for one of the list's entries, animated by <paramref name="motion"/>; null for an entry not laid out as the game's are.</summary>
        internal static Toast Create(RectTransform parent, Transform entry, AnimateSidebarElement motion, float u, bool pad, float now)
        {
            var root = motion.transform;
            var description = root.Find("Description");
            var source = description != null ? description.GetComponent<TextMeshProUGUI>() : null;
            if (source == null) return null;
            var found = root.GetComponents<DynamicInputIcon>();
            var prompts = new DynamicInputIcon[found.Length];
            for (var i = 0; i < prompts.Length; i++) prompts[i] = found[i];
            return new Toast(parent, entry, motion, source, Part(root, "Icon"), Part(root, "Icon (2)"), prompts, u, pad, now)
                { Sibling = entry.GetSiblingIndex() };
        }

        /// <summary>
        /// Follows the game's entry for a frame: in while the game has it in, out once the game slides it out, the text
        /// following the game's. False once the toast has gone and the game has put its entry away.
        /// </summary>
        internal bool Step(float now, float dt, bool pad)
        {
            var showing = Alive(_entry) && Alive(_motion) && _entry.activeSelf;
            if (!showing)
            {
                // Put away or destroyed without a slide of its own, as a world closing does; the toast leaves by itself.
                // The game puts a struck entry away before a strike this long has been seen, so one begun plays out.
                if (_phase < Phase.Struck) Begin(Phase.Out, now);
            }
            else
            {
                Sibling = Entry.GetSiblingIndex();
                // The game hides its whole list with the HUD or tutorials switched off, and in cutscenes.
                Show(_entry.activeInHierarchy);
                if (_motion._isActive)
                {
                    if (_phase >= Phase.Struck) Begin(Phase.In, now);
                    var text = TextOf(_source);
                    if (text != _shownText)
                    {
                        _shownText = text;
                        Retext();
                        // A count going up on a pickup the toast already shows.
                        if (_phase == Phase.Shown) _punch = now;
                    }
                    if (pad != _pad) Rekey(pad);
                }
                else if (_phase < Phase.Struck) Begin(_motion.UseStrikeout ? Phase.Struck : Phase.Out, now);
                if (_phase != Phase.Out) Pulse(now, _motion.ShouldPulse);
            }
            if (_stale && _rectObject.activeInHierarchy) Layout();
            // A faded toast stays parked, taking no room, while the game still has its entry out: the game can slide
            // that entry back in (another pickup of the same item) without switching it on again.
            return Animate(now, dt) || showing;
        }

        /// <summary>The game showed the entry again, possibly reused for another message: its icon is read again.</summary>
        internal void Refresh(float now)
        {
            ReadIcons();
            Layout();
            if (_phase >= Phase.Struck && Alive(_motion) && _motion._isActive) Begin(Phase.In, now);
        }

        /// <summary>Reads the key or button again, as after a menu where bindings may have changed.</summary>
        internal void Rekey(bool pad)
        {
            _pad = pad;
            if (_prompts.Length == 0) return;
            _control = Prompt(pad);
            Layout();
        }

        /// <summary>Lays the toast out again at a new screen size.</summary>
        internal void Resize(float u)
        {
            Object.Destroy(_rect.gameObject);
            _u = u;
            _alpha = -1f;
            _placedX = _placedY = float.NaN;
            _visible = true;
            Build();
            Retext();
        }

        /// <summary>Puts the toast's bottom at <paramref name="y"/> and returns the height it takes in the stack.</summary>
        internal float Place(float y)
        {
            if (y != _placedY || _x != _placedX)
            {
                _placedY = y;
                _placedX = _x;
                _rect.anchoredPosition = new Vector2(_x, y + _height / 2f);
            }
            return _slot.Value;
        }

        internal void Destroy()
        {
            if (_rect != null && _rect) Object.Destroy(_rect.gameObject);
        }

        // Unity's own test for a destroyed object, without a call into the engine: destroying clears the native pointer.
        internal static bool Alive(Object value) => value is not null && !value.WasCollected && value.m_CachedPtr != IntPtr.Zero;

        // TMP_Text.m_text, read in place. The game hands its label a new string whenever it changes the text, so a new
        // pointer is new text, seen without copying the string every frame.
        private static unsafe IntPtr TextOf(TMP_Text text)
        {
            if (_textOffset == 0)
            {
                var field = IL2CPP.il2cpp_class_get_field_from_name(Il2CppClassPointerStore<TMP_Text>.NativeClassPtr, "m_text");
                if (field == IntPtr.Zero) throw new MissingFieldException(nameof(TMP_Text), "m_text");
                _textOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
            }
            return *(IntPtr*)((byte*)text.Pointer + _textOffset);
        }

        private static RawImage Part(Transform root, string name)
        {
            var part = root.Find(name);
            return part != null ? part.GetComponent<RawImage>() : null;
        }

        private void Build()
        {
            var u = _u;
            _rect = MenuKit.Box(_parent, "Toast");
            _rectObject = _rect.gameObject;
            _rect.anchorMin = _rect.anchorMax = Vector2.zero;
            _rect.pivot = new Vector2(0f, 0.5f);
            _group = _rect.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            MenuKit.GlowBehind(_rect, Spill * u, 0.45f);
            MenuKit.Fill(MenuKit.Box(_rect, "Plate").Place(0, 0, 1, 1), null, PlateColor);
            _edge = MenuKit.Box(_rect, "Edge").Place(0, 0, 0, 1, 0, 0, -Math.Max(1f, Edge * u), 0);
            _edgeImage = MenuKit.Fill(_edge, null, MenuKit.Accent);
            var content = MenuKit.Box(_rect, "Content").Place(0, 0, 1, 1);
            _content = content.gameObject.AddComponent<CanvasGroup>();
            _icon = IconImage(content, "Icon");
            _badge = IconImage(content, "Badge");
            _text = MenuKit.Text(content, "Text", "", TextSize * u, true, MenuKit.Ink);
            _key = new KeyTag(content, "Key", KeyHeight * u);
            _key.Label.fontSize = KeyLetter * u;
            _strike = MenuKit.Box(_rect, "Strike");
            MenuKit.Fill(_strike, null, MenuKit.Ink);
            ReadIcons();
        }

        private static RawImage IconImage(RectTransform parent, string name)
        {
            var image = MenuKit.Box(parent, name).gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.color = MenuKit.Ink;
            return image;
        }

        // The game's own icon for the entry (its white cut-out, not the black outline drawn under it) and the small one
        // some entries add in its corner, when the game shows them.
        private void ReadIcons()
        {
            var icon = Copy(_icon, _sourceIcon);
            _icon.gameObject.SetActive(icon);
            _badge.gameObject.SetActive(icon && Copy(_badge, _sourceBadge));
        }

        private static bool Copy(RawImage target, RawImage source)
        {
            target.texture = source != null && source.gameObject.activeSelf ? source.texture : null;
            return target.texture != null;
        }

        // The binding behind the game's prompt on this entry: the one the game is showing, else the first bound on this
        // device. The entries name game actions, as "GpsTracker", read through the bindings model so rebinds show.
        private ControlKey Prompt(bool pad)
        {
            if (_prompts.Length == 0 || !GameBindings.Ready && !GameBindings.Load()) return default;
            var device = pad ? BindingDevice.Pad : BindingDevice.Keyboard;
            ControlKey first = default;
            foreach (var prompt in _prompts)
            {
                var key = ControlGlyphs.Game(prompt._actionId, device);
                if (!key.Bound) continue;
                var shown = prompt._activeInstance;
                if (shown != null && shown.activeSelf) return key;
                if (!first.Bound) first = key;
            }
            return first;
        }

        private void Retext()
        {
            var text = _source.text?.Trim() ?? "";
            _text.text = text;
            _empty = text.Length == 0;
            Layout();
        }

        private void Layout()
        {
            // Text is measured wrong while its object is inactive, as when a toast is made under a menu that hides the
            // toasts; that width would leave the plate a sliver behind the text, so the layout is done again once shown.
            _stale = !_rectObject.activeInHierarchy;
            var u = _u;
            var edge = Math.Max(1f, Edge * u);
            var x = edge + Pad * u;
            var max = MaxText * u;
            _text.enableWordWrapping = false;
            var size = _text.GetPreferredValues(_text.text);
            var wrap = size.x > max;
            if (wrap)
            {
                _text.enableWordWrapping = true;
                size = new Vector2(max, _text.GetPreferredValues(_text.text, max, 0f).y);
            }
            var height = Math.Max(MinHeight * u, size.y + 2f * Rise * u);
            if (_icon.gameObject.activeSelf)
            {
                var side = IconSide * u;
                var top = (height - side) / 2f;
                _icon.rectTransform.At(x, top, side, side);
                var badge = BadgeSide * u;
                _badge.rectTransform.At(x + side - badge * 0.6f, top + side - badge * 0.8f, badge, badge);
                x += side + Gap * u;
            }
            _textWidth = size.x;
            // A line gets a little slack against rounding; wrapped text keeps the width it was measured at.
            _text.rectTransform.At(x, (height - size.y) / 2f, wrap ? max : size.x + u, size.y);
            var stroke = Math.Max(1f, Stroke * u);
            _strike.At(x - 2f * u, (height - stroke) / 2f, _phase == Phase.Struck ? _strike.sizeDelta.x : 0f, stroke);
            x += size.x;
            if (_control.Bound)
            {
                ControlGlyphs.Show(_key, _control, Languages.CultureOf(Languages.Current));
                var key = _key.Size;
                x += Gap * u;
                _key.Rect.At(x, (height - key.y) / 2f, key.x, key.y);
                x += key.x;
            }
            else _key.Set(null, false);
            _height = height;
            _rect.sizeDelta = new Vector2(x + Pad * u, height);
            _placedY = float.NaN;
        }

        private void Begin(Phase phase, float now)
        {
            if (phase == Phase.In || phase == Phase.Out) _from = Math.Max(_alpha, 0f);
            if (phase == Phase.In)
            {
                _content.alpha = 1f;
                _strike.sizeDelta = new Vector2(0f, _strike.sizeDelta.y);
            }
            _phase = phase;
            _since = now;
        }

        private bool Animate(float now, float dt)
        {
            var t = now - _since;
            var alpha = 1f;
            var x = 0f;
            switch (_phase)
            {
                case Phase.In:
                {
                    var e = Moving.EaseOut(Mathf.Clamp01(t / InSeconds));
                    alpha = Mathf.Lerp(_from, 1f, e);
                    x = -Arrive * _u * (1f - e);
                    // On arrival the red edge draws out from its middle a little ahead of the plate.
                    if (_from <= 0f) _edge.localScale = new Vector3(1f, Moving.EaseOut(Mathf.Clamp01(t / (InSeconds * 0.7f))), 1f);
                    if (t >= InSeconds) Begin(Phase.Shown, now);
                    break;
                }
                case Phase.Struck:
                {
                    var s = Moving.EaseOut(Mathf.Clamp01(t / StrikeSeconds));
                    _strike.sizeDelta = new Vector2((_textWidth + 4f * _u) * s, _strike.sizeDelta.y);
                    _content.alpha = 1f - 0.45f * s;
                    if (t >= StrikeSeconds + StrikeHold) Begin(Phase.Out, now);
                    break;
                }
                case Phase.Out:
                {
                    var o = Mathf.Clamp01(t / OutSeconds);
                    alpha = _from * (1f - o * o);
                    x = -Depart * _u * o * o;
                    break;
                }
            }
            if (_empty) alpha = 0f;
            if (alpha != _alpha) _group.alpha = _alpha = alpha;
            _x = x;
            // A phase may have just begun above.
            t = now - _since;
            if (_punch >= 0f)
            {
                var p = Mathf.Clamp01((now - _punch) / PunchSeconds);
                var scale = 1f + 0.045f * (1f - p) * (1f - p);
                _rect.localScale = new Vector3(scale, scale, 1f);
                if (p >= 1f) _punch = -1f;
            }
            // A leaving toast gives up its room once mostly faded, so the ones above settle into it as it goes.
            var leaving = _phase == Phase.Out && t >= OutSeconds * 0.5f;
            _slot.Set(leaving || _empty ? 0f : _height + Spacing * _u);
            var settling = _slot.Step(dt);
            return _phase != Phase.Out || t < OutSeconds || settling;
        }

        // The game pulses some entries for attention; the red edge breathes with them.
        private void Pulse(float now, bool pulsing)
        {
            if (!pulsing && !_pulsing) return;
            _pulsing = pulsing;
            var color = MenuKit.Accent;
            if (pulsing) color.a = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Cos(now * 5.5f));
            _edgeImage.color = color;
        }

        private void Show(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _rect.gameObject.SetActive(visible);
        }
    }
}
