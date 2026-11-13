// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

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
    /// The focus mark: a light plate that glides to whatever holds focus. As a fill it covers the item, brightest
    /// at a solid edge on its left and fading toward the right, and a soft sheen crosses it each time it lands; as
    /// an underline it runs under the item.
    /// </summary>
    internal sealed class Glide : Moving
    {
        private const float Speed = 20f, SheenSeconds = 0.7f, SheenWidth = 0.32f;
        private readonly RectTransform _container, _bar, _sheen;
        private readonly bool _underline;
        private RectTransform _target;
        private Vector2 _min, _max;
        private float _sheenStart = -1;
        private bool _placed;

        /// <summary>The bar draws where it is created: after what it sits on, before what it sits under.</summary>
        internal Glide(RectTransform container, bool underline)
        {
            _container = container;
            _underline = underline;
            _bar = MenuKit.Box(container, underline ? "Underline" : "Focus");
            _bar.anchorMin = _bar.anchorMax = Vector2.zero;
            _bar.pivot = Vector2.zero;
            if (underline)
            {
                MenuKit.Fill(_bar, null, MenuKit.Ink);
            }
            else
            {
                // A faint even wash with a brighter one that falls away from the edge, so the item reads as lit
                // from its left rather than boxed in grey.
                MenuKit.Fill(_bar, null, MenuKit.Faded(0.045f));
                MenuKit.Fill(MenuKit.Box(_bar, "Light").Place(0, 0, 1, 1), MenuKit.Across, MenuKit.Faded(0.17f));
                var clip = MenuKit.Box(_bar, "Sheen").Place(0, 0, 1, 1);
                clip.gameObject.AddComponent<RectMask2D>();
                _sheen = MenuKit.Box(clip, "Band");
                _sheen.anchorMin = Vector2.zero;
                _sheen.anchorMax = new Vector2(0, 1);
                _sheen.pivot = new Vector2(0, 0.5f);
                MenuKit.Fill(_sheen, MenuKit.Bump, MenuKit.Faded(0.11f));
                _sheen.gameObject.SetActive(false);
                MenuKit.Fill(MenuKit.Box(_bar, "Edge").Place(0, 0, 0, 1, 0, 0, -Mathf.Max(2f, FrontEnd.U(3)), 0), null, MenuKit.Ink);
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
            if (!ReferenceEquals(next, _target)) _sheenStart = Time.unscaledTime;
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
            var sweeping = Sweep();
            // At rest it needs no more frames.
            if (!sweeping && (_min - low).sqrMagnitude < 0.01f && (_max - high).sqrMagnitude < 0.01f) Stop();
        }

        // The sheen runs once across the plate, left to right, easing in and out; true while it is still crossing.
        private bool Sweep()
        {
            if (_sheen == null || _sheenStart < 0) return false;
            var t = (Time.unscaledTime - _sheenStart) / SheenSeconds;
            if (t >= 1f)
            {
                _sheenStart = -1;
                _sheen.gameObject.SetActive(false);
                return false;
            }
            var width = _bar.sizeDelta.x;
            var band = Mathf.Max(FrontEnd.U(60), width * SheenWidth);
            var eased = t * t * (3f - 2f * t);
            if (!_sheen.gameObject.activeSelf) _sheen.gameObject.SetActive(true);
            _sheen.sizeDelta = new Vector2(band, 0);
            _sheen.anchoredPosition = new Vector2(Mathf.Lerp(-band, width, eased), 0);
            return true;
        }

        /// <summary>Follows its target again after the layout under it moved, as when a list scrolls.</summary>
        internal void Follow()
        {
            if (_target != null) Start();
        }
    }

    /// <summary>
    /// The game's split-color lettering on the text that holds focus. As focus lands, green and magenta copies of
    /// the text jolt apart behind it in a few hard steps, then ease back to a slight fringe that stays for as long
    /// as the item keeps focus, flickering apart again now and then as the game's own lettering does. It closes
    /// up and fades when focus moves on.
    /// </summary>
    internal sealed class Glitch : Moving
    {
        private const float Burst = 0.14f, Settle = 0.5f, Fade = 0.18f, Flicker = 0.1f;
        private const float Spread = 0.13f, Rest = 0.035f, RestAlpha = 0.6f;
        private static readonly Color Green = new(0.42f, 0.78f, 0.29f, 0.95f);
        private static readonly Color Magenta = new(0.84f, 0.12f, 0.52f, 0.95f);
        // The jolt: where each copy stands, as a share of the full spread, through the burst's few hard steps.
        private static readonly float[] Jolt = { 1f, -0.45f, 0.8f, 0.15f, 0.65f, 0.4f };
        private readonly TextMeshProUGUI _text, _left, _right;
        private float _start, _released = -1, _flicker = -1, _nextFlicker, _releasedAlpha;
        private bool _hold;

        internal Glitch(TextMeshProUGUI text)
        {
            _text = text;
            _left = Ghost("Green", Green);
            _right = Ghost("Magenta", Magenta);
        }

        protected override bool Alive => _text != null && _text && _left != null && _left;

        /// <summary>
        /// Splits the text as focus lands on it or its value changes. With <paramref name="hold"/> the fringe stays
        /// until <see cref="Release"/>; without, as for a heading that just changed, it closes up by itself.
        /// </summary>
        internal void Pulse(bool hold = true)
        {
            if (!Alive || !_text.isActiveAndEnabled || string.IsNullOrEmpty(_text.text)) return;
            foreach (var ghost in new[] { _left, _right })
            {
                Copy(ghost);
                var rect = ghost.rectTransform;
                var source = _text.rectTransform;
                rect.anchorMin = source.anchorMin;
                rect.anchorMax = source.anchorMax;
                rect.pivot = source.pivot;
                rect.sizeDelta = source.sizeDelta;
                rect.anchoredPosition = source.anchoredPosition;
                ghost.enabled = true;
            }
            _hold = hold;
            _start = Time.unscaledTime;
            _released = _flicker = -1;
            _nextFlicker = _start + Burst + Settle + NextGap();
            Start();
            Step(0);
        }

        /// <summary>Closes the split up, as focus moves on.</summary>
        internal void Release()
        {
            if (!Alive || _released >= 0 || !_left.enabled) return;
            _released = Time.unscaledTime;
            _releasedAlpha = _left.color.a / Green.a;
            Start();
        }

        protected override void Step(float dt)
        {
            var now = Time.unscaledTime;
            // On a screen out of view the split waits as it is, for when the screen comes back.
            if (!_text.isActiveAndEnabled) return;
            if (_left.text != _text.text)
            {
                Copy(_left);
                Copy(_right);
            }
            float spread, alpha;
            if (_released >= 0)
            {
                var t = (now - _released) / Fade;
                if (t >= 1f)
                {
                    Hide();
                    return;
                }
                spread = Rest * (1f - t);
                alpha = _releasedAlpha * (1f - t) * (1f - t);
            }
            else
            {
                var age = now - _start;
                if (age < Burst)
                {
                    spread = Spread * Jolt[Mathf.Min(Jolt.Length - 1, (int)(age / Burst * Jolt.Length))];
                    alpha = 1f;
                }
                else if (age < Burst + Settle)
                {
                    var t = EaseOut((age - Burst) / Settle);
                    spread = Mathf.Lerp(Spread * Jolt[Jolt.Length - 1], Rest, t);
                    alpha = Mathf.Lerp(1f, RestAlpha, t);
                }
                else if (!_hold)
                {
                    Release();
                    return;
                }
                else
                {
                    spread = Rest;
                    alpha = RestAlpha;
                    // Now and then the fringe jumps apart for an instant and snaps back.
                    if (_flicker < 0 && now >= _nextFlicker) _flicker = now;
                    if (_flicker >= 0)
                    {
                        var t = (now - _flicker) / Flicker;
                        if (t >= 1f)
                        {
                            _flicker = -1;
                            _nextFlicker = now + NextGap();
                        }
                        else
                        {
                            spread = Spread * (t < 0.5f ? 0.55f : -0.3f);
                            alpha = 0.9f;
                        }
                    }
                }
            }
            Place(spread, alpha);
        }

        // Each copy stands off to its own side by the spread, in ems of the text, never closer than a pixel and a half
        // so the fringe shows on small text too.
        private void Place(float spread, float alpha)
        {
            var source = _text.rectTransform.anchoredPosition;
            var em = _text.fontSize * spread;
            if (Mathf.Abs(em) < 1.5f) em = em < 0 ? -1.5f : 1.5f;
            _left.rectTransform.anchoredPosition = source + new Vector2(-em, em * 0.25f);
            _right.rectTransform.anchoredPosition = source + new Vector2(em, -em * 0.3f);
            var fade = alpha * _text.color.a;
            _left.color = new Color(Green.r, Green.g, Green.b, Green.a * fade);
            _right.color = new Color(Magenta.r, Magenta.g, Magenta.b, Magenta.a * fade);
        }

        private void Hide()
        {
            _left.enabled = _right.enabled = false;
            _released = -1;
            Stop();
        }

        private void Copy(TextMeshProUGUI ghost)
        {
            ghost.text = _text.text;
            ghost.font = _text.font;
            ghost.fontSize = _text.fontSize;
            ghost.characterSpacing = _text.characterSpacing;
            ghost.alignment = _text.alignment;
            ghost.enableWordWrapping = _text.enableWordWrapping;
            ghost.overflowMode = _text.overflowMode;
        }

        private static float NextGap() => UnityEngine.Random.Range(2.6f, 5.2f);

        private TextMeshProUGUI Ghost(string name, Color color)
        {
            var parent = _text.rectTransform.parent;
            var ghost = MenuKit.Text(parent, _text.name + " " + name, "", _text.fontSize, true, color, _text.alignment);
            ghost.font = _text.font;
            // Behind the text it splits from.
            ghost.rectTransform.SetSiblingIndex(_text.rectTransform.GetSiblingIndex());
            ghost.enabled = false;
            return ghost;
        }
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
