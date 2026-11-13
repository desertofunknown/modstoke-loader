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
    /// The focus mark: a light bar that glides to whatever holds focus. As a fill it covers the item with a
    /// bright edge on its left; as an underline it runs under the item.
    /// </summary>
    internal sealed class Glide : Moving
    {
        private const float Speed = 20f;
        private readonly RectTransform _container, _bar;
        private readonly bool _underline;
        private RectTransform _target;
        private Vector2 _min, _max;
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
                MenuKit.Fill(_bar, null, MenuKit.FocusFill);
                MenuKit.Fill(MenuKit.Box(_bar, "Edge").Place(0, 0, 0, 1, 0, 0, -FrontEnd.U(3), 0), null, MenuKit.Ink);
            }
            _bar.gameObject.SetActive(false);
        }

        protected override bool Alive => _bar != null && _bar;

        /// <summary>Moves to an item, or hides with null; <paramref name="snap"/> jumps there without gliding.</summary>
        internal void Target(RectTransform target, bool snap = false)
        {
            _target = target != null && target ? target : null;
            if (_target == null)
            {
                _bar.gameObject.SetActive(false);
                _placed = false;
                return;
            }
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
                _min = low;
                _max = high;
                _placed = true;
            }
            else
            {
                var k = 1f - Mathf.Exp(-Speed * dt);
                _min = Vector2.Lerp(_min, low, k);
                _max = Vector2.Lerp(_max, high, k);
            }
            _bar.anchoredPosition = _min;
            _bar.sizeDelta = _max - _min;
            // At rest it needs no more frames.
            if ((_min - low).sqrMagnitude < 0.01f && (_max - high).sqrMagnitude < 0.01f) Stop();
        }

        /// <summary>Follows its target again after the layout under it moved, as when a list scrolls.</summary>
        internal void Follow()
        {
            if (_target != null) Start();
        }
    }

    /// <summary>
    /// The game's split-color lettering as a short flash: when focus lands on a text, green and magenta copies
    /// of it spring apart behind it and settle back in a fraction of a second.
    /// </summary>
    internal sealed class Glitch : Moving
    {
        private const float Seconds = 0.22f, Spread = 0.075f;
        private static readonly Color Green = new(0.42f, 0.78f, 0.29f, 0.95f);
        private static readonly Color Magenta = new(0.84f, 0.12f, 0.52f, 0.95f);
        private readonly TextMeshProUGUI _text, _left, _right;
        private float _start;

        internal Glitch(TextMeshProUGUI text)
        {
            _text = text;
            _left = Ghost("Green", Green);
            _right = Ghost("Magenta", Magenta);
        }

        protected override bool Alive => _text != null && _text && _left != null && _left;

        internal void Pulse()
        {
            if (!Alive || !_text.isActiveAndEnabled || string.IsNullOrEmpty(_text.text)) return;
            foreach (var ghost in new[] { _left, _right })
            {
                ghost.text = _text.text;
                ghost.font = _text.font;
                ghost.fontSize = _text.fontSize;
                ghost.characterSpacing = _text.characterSpacing;
                ghost.alignment = _text.alignment;
                ghost.enableWordWrapping = _text.enableWordWrapping;
                ghost.overflowMode = _text.overflowMode;
                var rect = ghost.rectTransform;
                var source = _text.rectTransform;
                rect.anchorMin = source.anchorMin;
                rect.anchorMax = source.anchorMax;
                rect.pivot = source.pivot;
                rect.sizeDelta = source.sizeDelta;
                rect.anchoredPosition = source.anchoredPosition;
                ghost.enabled = true;
            }
            _start = Time.unscaledTime;
            Start();
            Step(0);
        }

        protected override void Step(float dt)
        {
            var t = (Time.unscaledTime - _start) / Seconds;
            if (t >= 1f || !_text.isActiveAndEnabled)
            {
                _left.enabled = _right.enabled = false;
                Stop();
                return;
            }
            var source = _text.rectTransform.anchoredPosition;
            var em = _text.fontSize * Spread * (1f - EaseOut(t));
            // A brief stutter in the first frames, as the game's lettering does.
            var jitter = t < 0.35f ? ((Time.frameCount & 1) == 0 ? 1.25f : 0.7f) : 1f;
            _left.rectTransform.anchoredPosition = source + new Vector2(-em * jitter, em * 0.25f);
            _right.rectTransform.anchoredPosition = source + new Vector2(em * jitter, -em * 0.35f);
            var alpha = 1f - t * t;
            _left.color = new Color(Green.r, Green.g, Green.b, Green.a * alpha);
            _right.color = new Color(Magenta.r, Magenta.g, Magenta.b, Magenta.a * alpha);
        }

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
        private const float Seconds = 0.12f;
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
