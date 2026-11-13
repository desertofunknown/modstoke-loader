// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>Something on a screen that can hold focus: a button, a row, a choice.</summary>
    internal sealed class Focusable
    {
        internal RectTransform Rect;
        /// <summary>Runs on a click, Enter or the controller's confirm button.</summary>
        internal Action Activate;
        /// <summary>Draws the focused or unfocused look.</summary>
        internal Action<bool> Paint;
        /// <summary>For a value that steps left and right; takes left and right presses while focused.</summary>
        internal Action<int> Adjust;
        internal Func<bool> Enabled;

        internal bool Usable => Rect != null && Rect && Rect.gameObject.activeInHierarchy && (Enabled == null || Enabled());
    }

    /// <summary>One prompt in the bar at the bottom of a screen: what a button or key does here.</summary>
    internal sealed record PromptSpec(string Label, string Pad, string Key, Action Click);

    /// <summary>
    /// One full screen of the front end. A screen builds its layout once, registers what can hold focus, and
    /// the base class moves focus between those by direction for keyboard and controller, and by pointer for the
    /// mouse, so every screen behaves the same on all three.
    /// </summary>
    internal abstract class FrontScreen
    {
        private const float AppearSeconds = 0.14f;
        private readonly List<Focusable> _items = new();
        private CanvasGroup _group;

        internal RectTransform Root { get; private set; }
        protected Focusable Focused { get; private set; }
        /// <summary>Whether the focus change or activation being handled came from the mouse.</summary>
        protected bool FromPointer { get; private set; }
        protected static Strings Text => FrontEnd.Text;
        protected static float U(float design) => FrontEnd.U(design);

        internal void Create(RectTransform canvas)
        {
            Root = MenuKit.Box(canvas, GetType().Name).Place(0, 0, 1, 1);
            _group = Root.gameObject.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;
            _items.Clear();
            Focused = null;
            Build();
            Retext();
        }

        /// <summary>Starts the short fade a screen arrives with.</summary>
        internal void Appear()
        {
            if (_group != null && _group) _group.alpha = 0;
        }

        internal void Animate()
        {
            if (_group != null && _group && _group.alpha < 1)
                _group.alpha = Mathf.MoveTowards(_group.alpha, 1, Time.unscaledDeltaTime / AppearSeconds);
        }

        /// <summary>Creates the layout under <see cref="Root"/>. Text is set in <see cref="Retext"/>.</summary>
        protected abstract void Build();

        /// <summary>Sets every text on the screen; runs after building and whenever the language changes.</summary>
        internal virtual void Retext() { }

        /// <summary>The screen came to the front, or was rebuilt for a new screen size; it sets its focus here.</summary>
        internal virtual void Opened() { }

        internal virtual void Closed() { }

        /// <summary>The player switched between controller and keyboard; button labels on the screen follow.</summary>
        internal virtual void DeviceChanged() { }

        internal virtual void Tick() { }

        /// <summary>Back was pressed; false lets the front end close this screen.</summary>
        internal virtual bool OnBack() => false;

        internal virtual void OnAlt1() { }

        internal virtual void OnAlt2() { }

        /// <summary>A step to the previous (-1) or next (1) section of the menu this screen belongs to.</summary>
        internal virtual void OnSection(int step) { }

        /// <summary>The prompts to show for this screen, left group first.</summary>
        internal virtual IReadOnlyList<PromptSpec> Prompts => Array.Empty<PromptSpec>();

        protected Focusable Add(RectTransform rect, Action activate, Action<bool> paint = null, Action<int> adjust = null,
            Func<bool> enabled = null)
        {
            var item = new Focusable { Rect = rect, Activate = activate, Paint = paint, Adjust = adjust, Enabled = enabled };
            _items.Add(item);
            paint?.Invoke(false);
            return item;
        }

        protected void Focus(Focusable item)
        {
            if (ReferenceEquals(item, Focused)) return;
            var previous = Focused;
            Focused = item;
            previous?.Paint?.Invoke(false);
            item?.Paint?.Invoke(true);
            FocusChanged(previous, item);
        }

        protected virtual void FocusChanged(Focusable previous, Focusable current) { }

        /// <summary>Moves focus one step; a screen overrides this to scroll a list at its edges.</summary>
        protected virtual void Navigate(int dx, int dy)
        {
            if (Focused == null || !Focused.Usable)
            {
                Focus(First());
                return;
            }
            if (dx != 0 && Focused.Adjust != null)
            {
                Focused.Adjust(dx);
                return;
            }
            var next = Nearest(Focused, dx, dy);
            if (next != null) Focus(next);
        }

        internal void HandleInput()
        {
            Focusable over = null;
            foreach (var item in _items)
                if (item.Usable && RectTransformUtility.RectangleContainsScreenPoint(item.Rect, FrontInput.Pointer, null))
                    over = item;
            // The pointer takes focus only when it moves, so it does not fight the keyboard or controller.
            FromPointer = true;
            try
            {
                if (over != null && FrontInput.PointerMoved) Focus(over);
                if (FrontInput.Click && over != null)
                {
                    Focus(over);
                    over.Activate?.Invoke();
                    return;
                }
            }
            finally
            {
                FromPointer = false;
            }
            var move = FrontInput.Move;
            if (move != Vector2Int.zero) Navigate(move.x, move.y);
            if (FrontInput.Confirm)
            {
                if (Focused == null || !Focused.Usable) Focus(First());
                else Focused.Activate?.Invoke();
            }
            else if (FrontInput.Back)
            {
                if (!OnBack()) FrontEnd.Pop();
            }
            else if (FrontInput.Alt1) OnAlt1();
            else if (FrontInput.Alt2) OnAlt2();
            else if (FrontInput.Section != 0) OnSection(FrontInput.Section);
        }

        protected Focusable First()
        {
            foreach (var item in _items)
                if (item.Usable) return item;
            return null;
        }

        // The usable item most directly in that direction: distance along it, with sideways drift counted double.
        private Focusable Nearest(Focusable from, int dx, int dy)
        {
            var origin = Center(from.Rect);
            // Screen space runs up, input runs down.
            var direction = new Vector2(dx, -dy);
            Focusable best = null;
            var bestScore = float.MaxValue;
            foreach (var item in _items)
            {
                if (ReferenceEquals(item, from) || !item.Usable) continue;
                var offset = Center(item.Rect) - origin;
                var along = Vector2.Dot(offset, direction);
                if (along < 4f) continue;
                var across = Mathf.Abs(offset.x * direction.y - offset.y * direction.x);
                var score = along + across * 2f;
                if (score >= bestScore) continue;
                bestScore = score;
                best = item;
            }
            return best;
        }

        private static Vector2 Center(RectTransform rect)
        {
            var local = rect.rect.center;
            var center = rect.TransformPoint(new Vector3(local.x, local.y, 0));
            return new Vector2(center.x, center.y);
        }
    }
}
