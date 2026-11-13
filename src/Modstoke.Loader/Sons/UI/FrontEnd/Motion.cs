// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Modstoke.Loader
{
    /// <summary>Something on a Modstoke screen that moves over time; ticked once a frame until its owner is gone.</summary>
    internal abstract class Moving
    {
        private static readonly List<Moving> Active = new();

        protected abstract bool Alive { get; }

        protected abstract void Step(float dt);

        protected void Start()
        {
            if (!Active.Contains(this)) Active.Add(this);
        }

        protected void Stop() => Active.Remove(this);

        /// <summary>Runs every frame from the front end, for every screen on the layer.</summary>
        internal static void TickAll()
        {
            var dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var item = Active[i];
                try
                {
                    if (item.Alive) item.Step(dt);
                    else Active.RemoveAt(i);
                }
                catch (Exception)
                {
                    // One broken animation stops alone.
                    Active.RemoveAt(i);
                }
            }
        }

        internal static float EaseOut(float t) => 1f - (1f - t) * (1f - t) * (1f - t);
    }

    /// <summary>
    /// The focus mark, gliding to whatever holds focus. As a fill it is a bone plate across the item with a red edge
    /// on its left, the item's text turning dark on it; it opens out from that edge when it first appears and lands
    /// white before settling to bone. As an underline it runs under the item. Over rows that are still fading in,
    /// it fades in with them.
    /// </summary>
    internal sealed class Glide : Moving
    {
        private const float Speed = 22f, LandSeconds = 0.3f;
        private readonly RectTransform _container, _bar;
        private readonly CanvasGroup _group;
        private readonly Image _plate;
        private readonly bool _underline;
        private RectTransform _target;
        private CanvasGroup _targetGroup;
        private Vector2 _min, _max;
        private float _landed = -1;
        private bool _placed;

        /// <summary>The bar draws where it is created: after what it sits on, before what it sits under.</summary>
        internal Glide(RectTransform container, bool underline)
        {
            _container = container;
            _underline = underline;
            _bar = MenuKit.Box(container, underline ? "Underline" : "Focus");
            _bar.anchorMin = _bar.anchorMax = Vector2.zero;
            _bar.pivot = Vector2.zero;
            _group = _bar.gameObject.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;
            if (underline)
            {
                MenuKit.Fill(_bar, null, MenuKit.Ink);
            }
            else
            {
                _plate = MenuKit.Fill(_bar, null, MenuKit.Plate);
                MenuKit.Fill(MenuKit.Box(_bar, "Edge").Place(0, 0, 0, 1, 0, 0, -Mathf.Max(3f, FrontEnd.U(4)), 0), null, MenuKit.Accent);
            }
            _bar.gameObject.SetActive(false);
        }

        protected override bool Alive => _bar != null && _bar;

        /// <summary>Moves to an item, or hides with null; <paramref name="snap"/> opens there without gliding.</summary>
        internal void Target(RectTransform target, bool snap = false)
        {
            var next = target != null && target ? target : null;
            if (next == null)
            {
                _target = null;
                _bar.gameObject.SetActive(false);
                _placed = false;
                return;
            }
            if (!ReferenceEquals(next, _target))
            {
                _landed = Time.unscaledTime;
                _targetGroup = next.GetComponent<CanvasGroup>();
            }
            _target = next;
            if (snap) _placed = false;
            _bar.gameObject.SetActive(true);
            Start();
        }

        protected override void Step(float dt)
        {
            if (_target == null || !_target || !_target.gameObject.activeInHierarchy)
            {
                if (_bar.gameObject.activeSelf) _bar.gameObject.SetActive(false);
                return;
            }
            if (!_bar.gameObject.activeSelf) _bar.gameObject.SetActive(true);
            var corners = new Il2CppStructArray<Vector3>(4);
            _target.GetWorldCorners(corners);
            var origin = _container.rect.min;
            Vector2 low = (Vector2)_container.InverseTransformPoint(corners[0]) - origin;
            Vector2 high = (Vector2)_container.InverseTransformPoint(corners[2]) - origin;
            if (_underline)
            {
                var thickness = Mathf.Max(2f, FrontEnd.U(3));
                high.y = low.y + thickness;
            }
            if (!_placed)
            {
                // A fill that was not showing opens out from the item's left edge; an underline is simply there.
                _min = low;
                _max = _underline ? high : new Vector2(low.x, high.y);
                _placed = true;
            }
            var k = 1f - Mathf.Exp(-Speed * dt);
            _min = Vector2.Lerp(_min, low, k);
            _max = Vector2.Lerp(_max, high, k);
            _bar.anchoredPosition = _min;
            _bar.sizeDelta = _max - _min;
            var landing = Land();
            var alpha = _targetGroup != null && _targetGroup ? _targetGroup.alpha : 1f;
            _group.alpha = alpha;
            // At rest it needs no more frames.
            if (!landing && alpha >= 0.999f && (_min - low).sqrMagnitude < 0.01f && (_max - high).sqrMagnitude < 0.01f) Stop();
        }

        // The plate lands white and settles to bone; true while it is settling.
        private bool Land()
        {
            if (_plate == null || _landed < 0) return false;
            var t = (Time.unscaledTime - _landed) / LandSeconds;
            if (t >= 1f)
            {
                _plate.color = MenuKit.Plate;
                _landed = -1;
                return false;
            }
            _plate.color = Color.Lerp(Color.white, MenuKit.Plate, EaseOut(t));
            return true;
        }

        /// <summary>Follows its target again after the layout under it moved, as when a list scrolls.</summary>
        internal void Follow()
        {
            if (_target != null) Start();
        }
    }

    /// <summary>
    /// The game's split-color lettering, as a glitch on the text that takes focus. For a moment the text is cut into
    /// bands that jump sideways independently, in hard steps rather than smoothly, while green and magenta copies
    /// of it tear apart behind; the jolt dies down within half a second and the text settles clean. While the item
    /// keeps focus it glitches again briefly every few seconds. Moving focus away ends it at once.
    /// </summary>
    internal sealed class Glitch : Moving
    {
        private const int Bands = 4;
        // The whole jolt, its hardest opening part, the rate the cuts jump at, and a later flicker's length.
        private const float Burst = 0.42f, Hit = 0.08f, Frame = 1f / 30f, Flicker = 0.14f;
        // How far bands jump and the colors tear, in ems of the text; a flicker is a smaller jolt.
        private const float Shear = 0.24f, Split = 0.14f, FlickerAmount = 0.45f;
        private static readonly Color Green = new(0.42f, 0.78f, 0.29f, 1f);
        private static readonly Color Magenta = new(0.84f, 0.12f, 0.52f, 1f);
        private readonly TextMeshProUGUI _text;
        private readonly RectTransform[] _bands = new RectTransform[Bands];
        private readonly TextMeshProUGUI[] _cuts = new TextMeshProUGUI[Bands];
        private readonly float[] _shift = new float[Bands];
        private readonly float[] _edges = new float[Bands + 1];
        private RectTransform _rig;
        private TextMeshProUGUI _left, _right;
        private float _start, _flicker = -1, _nextFlicker, _nextFrame, _tear, _lift;
        private bool _hold, _running, _shown;

        internal Glitch(TextMeshProUGUI text) => _text = text;

        protected override bool Alive => _text != null && _text;

        /// <summary>
        /// Glitches the text as focus lands on it or its value changes. With <paramref name="hold"/> it keeps
        /// flickering now and then until <see cref="Release"/>; without, as for a heading that changed, it is one jolt.
        /// </summary>
        internal void Pulse(bool hold = true)
        {
            if (!Alive || !_text.isActiveAndEnabled || string.IsNullOrEmpty(_text.text)) return;
            if (_rig == null || !_rig) Build();
            _hold = hold;
            _start = Time.unscaledTime;
            _flicker = -1;
            _nextFrame = 0;
            _nextFlicker = _start + Burst + NextGap();
            _running = true;
            Start();
            Step(0);
        }

        /// <summary>Ends the glitch and shows the text plainly, as focus moves on.</summary>
        internal void Release()
        {
            if (!_running) return;
            _running = false;
            Settle();
            Stop();
        }

        protected override void Step(float dt)
        {
            // On a screen out of view the glitch waits as it is, for when the screen comes back.
            if (!_running || !_text.isActiveAndEnabled) return;
            try
            {
                var now = Time.unscaledTime;
                var age = now - _start;
                float amount;
                if (age < Burst) amount = age < Hit ? 1f : Mathf.Pow(1f - (age - Hit) / (Burst - Hit), 1.6f);
                else if (!_hold)
                {
                    Release();
                    return;
                }
                else
                {
                    if (_flicker < 0 && now >= _nextFlicker)
                    {
                        _flicker = now;
                        _nextFrame = 0;
                    }
                    if (_flicker < 0 || now - _flicker >= Flicker)
                    {
                        if (_flicker >= 0)
                        {
                            _flicker = -1;
                            _nextFlicker = now + NextGap();
                        }
                        Settle();
                        return;
                    }
                    amount = FlickerAmount;
                }
                if (now >= _nextFrame)
                {
                    _nextFrame = now + Frame;
                    Jolt(amount);
                }
            }
            catch (Exception)
            {
                // The text never stays hidden behind a glitch that failed.
                Settle();
                throw;
            }
        }

        // A new arrangement of cuts: band edges wander a little across the letters, every band jumps by its own amount
        // (one of them always hard), and the colored copies tear apart with a slight lift.
        private void Jolt(float amount)
        {
            Sync();
            var source = _text.rectTransform.rect;
            var bounds = _text.textBounds;
            var low = source.height > 0 ? Mathf.Clamp01((bounds.min.y - source.yMin) / source.height) : 0.2f;
            var high = source.height > 0 ? Mathf.Clamp01((bounds.max.y - source.yMin) / source.height) : 0.8f;
            if (high - low < 0.05f)
            {
                low = 0.2f;
                high = 0.8f;
            }
            _edges[0] = 0;
            _edges[Bands] = 1;
            for (var i = 1; i < Bands; i++)
                _edges[i] = Mathf.Lerp(low, high, (i + Random.Range(-0.35f, 0.35f)) / Bands);
            var hard = Random.Range(0, Bands);
            for (var i = 0; i < Bands; i++)
            {
                var size = i == hard ? 1f : Random.value * Random.value;
                _shift[i] = (Random.value < 0.5f ? -1f : 1f) * size * amount;
            }
            _tear = amount * Random.Range(0.6f, 1f);
            _lift = amount * Random.Range(-0.25f, 0.25f);
            Place();
        }

        private void Place()
        {
            var em = _text.fontSize;
            var height = _rig.rect.height;
            var margin = em * 2f;
            for (var i = 0; i < Bands; i++)
            {
                var band = _bands[i];
                band.anchorMin = new Vector2(0, _edges[i]);
                band.anchorMax = new Vector2(1, _edges[i + 1]);
                band.offsetMin = new Vector2(-margin, 0);
                band.offsetMax = new Vector2(margin, 0);
                // Each cut is the whole text laid out as the original, seen through its band and moved sideways.
                var dx = _shift[i] * Shear * em;
                var cut = _cuts[i].rectTransform;
                cut.offsetMin = new Vector2(margin + dx, -_edges[i] * height);
                cut.offsetMax = new Vector2(-margin + dx, (1 - _edges[i + 1]) * height);
            }
            var tear = Mathf.Max(2f, _tear * Split * em);
            var lift = _lift * em * 0.3f;
            _left.rectTransform.offsetMin = _left.rectTransform.offsetMax = new Vector2(-tear, lift);
            _right.rectTransform.offsetMin = _right.rectTransform.offsetMax = new Vector2(tear, -lift);
            if (_shown) return;
            _shown = true;
            _rig.gameObject.SetActive(true);
            Conceal(true);
        }

        // The original text is hidden while its cuts stand in for it, with the parts drawn from fallback fonts (as
        // Chinese, Japanese and Korean letters are), which render on meshes of their own.
        private void Conceal(bool hidden)
        {
            var alpha = hidden ? 0f : 1f;
            _text.canvasRenderer.SetAlpha(alpha);
            foreach (var part in _text.GetComponentsInChildren<TMP_SubMeshUI>(true))
                if (part != null && part) part.canvasRenderer.SetAlpha(alpha);
        }

        // The cuts and copies follow the text: where it sits, what it says, and how it looks right now.
        private void Sync()
        {
            var source = _text.rectTransform;
            _rig.anchorMin = source.anchorMin;
            _rig.anchorMax = source.anchorMax;
            _rig.pivot = source.pivot;
            _rig.anchoredPosition = source.anchoredPosition;
            _rig.sizeDelta = source.sizeDelta;
            var color = _text.color;
            foreach (var cut in _cuts)
            {
                Copy(cut);
                cut.color = color;
            }
            Copy(_left);
            Copy(_right);
            _left.color = new Color(Green.r, Green.g, Green.b, 0.9f * color.a);
            _right.color = new Color(Magenta.r, Magenta.g, Magenta.b, 0.9f * color.a);
        }

        private void Settle()
        {
            if (!_shown) return;
            _shown = false;
            if (_rig != null && _rig) _rig.gameObject.SetActive(false);
            if (_text != null && _text) Conceal(false);
        }

        private void Build()
        {
            _rig = MenuKit.Box(_text.rectTransform.parent, _text.name + " Glitch");
            // Over the text it stands in for, so the cuts cover where the letters were.
            _rig.SetSiblingIndex(_text.rectTransform.GetSiblingIndex() + 1);
            _left = Ghost("Green");
            _right = Ghost("Magenta");
            for (var i = 0; i < Bands; i++)
            {
                var band = _bands[i] = MenuKit.Box(_rig, "Band " + i);
                band.gameObject.AddComponent<RectMask2D>();
                _cuts[i] = MenuKit.Text(band, "Cut", "", _text.fontSize, true, _text.color, _text.alignment);
                _cuts[i].rectTransform.anchorMin = Vector2.zero;
                _cuts[i].rectTransform.anchorMax = Vector2.one;
            }
            _rig.gameObject.SetActive(false);
            _shown = false;
        }

        private TextMeshProUGUI Ghost(string name)
        {
            var ghost = MenuKit.Text(_rig, name, "", _text.fontSize, true, Color.clear, _text.alignment);
            ghost.rectTransform.anchorMin = Vector2.zero;
            ghost.rectTransform.anchorMax = Vector2.one;
            return ghost;
        }

        private void Copy(TextMeshProUGUI target)
        {
            if (target.text != _text.text) target.text = _text.text;
            target.font = _text.font;
            target.fontSize = _text.fontSize;
            target.fontStyle = _text.fontStyle;
            target.characterSpacing = _text.characterSpacing;
            target.alignment = _text.alignment;
            target.enableWordWrapping = _text.enableWordWrapping;
            target.overflowMode = _text.overflowMode;
            target.margin = _text.margin;
        }

        private static float NextGap() => Random.Range(3.2f, 6.5f);
    }

    /// <summary>A screen on its way out: it fades over a moment, then is put away or removed.</summary>
    internal sealed class Fading : Moving
    {
        /// <summary>How long a screen takes to leave; the one replacing it waits this long before it arrives.</summary>
        internal const float Seconds = 0.1f;
        private readonly RectTransform _rect;
        private readonly CanvasGroup _group;
        private readonly bool _destroy;
        private float _start;
        private bool _cancelled;

        internal Fading(RectTransform rect, CanvasGroup group, bool destroy)
        {
            _rect = rect;
            _group = group;
            _destroy = destroy;
        }

        protected override bool Alive => !_cancelled && _rect != null && _rect && _group != null && _group;

        internal Fading Begin()
        {
            _start = Time.unscaledTime;
            Start();
            return this;
        }

        /// <summary>The screen is wanted again before it finished leaving.</summary>
        internal void Cancel()
        {
            _cancelled = true;
            Stop();
        }

        protected override void Step(float dt)
        {
            var t = Mathf.Clamp01((Time.unscaledTime - _start) / Seconds);
            _group.alpha = (1f - t) * (1f - t);
            if (t < 1f) return;
            Stop();
            if (_destroy) Object.Destroy(_rect.gameObject);
            else
            {
                _rect.gameObject.SetActive(false);
                _group.alpha = 1f;
            }
        }
    }

    /// <summary>
    /// Rows that arrive one after another: each fades in a moment after the one above it, so a list reads from the
    /// top as it appears. Only the first rows wait; the rest come with the last of them.
    /// </summary>
    internal sealed class Cascade : Moving
    {
        private const float Seconds = 0.2f, Gap = 0.028f;
        private const int Staggered = 12;
        private readonly List<CanvasGroup> _groups = new();
        private float _start;

        internal Cascade(IEnumerable<RectTransform> rows)
        {
            foreach (var row in rows)
            {
                if (row == null || !row) continue;
                var group = row.GetComponent<CanvasGroup>();
                if (group == null || !group) group = row.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                _groups.Add(group);
            }
            _start = Time.unscaledTime;
            Start();
        }

        protected override bool Alive => _groups.Count > 0 && _groups[0] != null && _groups[0];

        protected override void Step(float dt)
        {
            var elapsed = Time.unscaledTime - _start;
            var done = true;
            for (var i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                if (group == null || !group) continue;
                var t = Mathf.Clamp01((elapsed - Math.Min(i, Staggered) * Gap) / Seconds);
                group.alpha = EaseOut(t);
                done &= t >= 1f;
            }
            if (done) Stop();
        }
    }

    /// <summary>A number that eases toward its target, for slider fills and scroll positions.</summary>
    internal sealed class Ease
    {
        private readonly float _speed;
        private bool _set;

        internal Ease(float speed = 18f) => _speed = speed;

        internal float Value { get; private set; }
        internal float Goal { get; private set; }

        internal void Set(float goal, bool snap = false)
        {
            Goal = goal;
            if (snap || !_set) Value = goal;
            _set = true;
        }

        /// <summary>Moves toward the goal; true while still moving.</summary>
        internal bool Step(float dt)
        {
            if (Mathf.Abs(Value - Goal) < 0.01f)
            {
                Value = Goal;
                return false;
            }
            Value = Mathf.Lerp(Value, Goal, 1f - Mathf.Exp(-_speed * dt));
            return true;
        }
    }
}
