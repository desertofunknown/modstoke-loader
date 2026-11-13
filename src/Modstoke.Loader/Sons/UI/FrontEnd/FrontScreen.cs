// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using TMPro;
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
        /// <summary>The focus mark this item is shown with, or null for an item that draws its own.</summary>
        internal Glide Mark;
        /// <summary>The text that flashes the split colors when focus lands here.</summary>
        internal Glitch Flash;
        /// <summary>For an item in a scrolling list: the visible part of the list, outside which the mouse misses it.</summary>
        internal RectTransform Clip;

        internal bool Usable => Rect != null && Rect && Rect.gameObject.activeInHierarchy && (Enabled == null || Enabled());

        internal bool Under(Vector2 pointer)
            => RectTransformUtility.RectangleContainsScreenPoint(Rect, pointer, null)
                && (Clip == null || !Clip || RectTransformUtility.RectangleContainsScreenPoint(Clip, pointer, null));
    }

    /// <summary>One prompt in the bar at the bottom of a screen: what a button or key does here.</summary>
    internal sealed record PromptSpec(string Label, string Pad, string Key, Action Click);

    /// <summary>
    /// One full screen of the front end. A screen builds its layout once, registers what can hold focus, and
    /// the base class moves focus between those by direction for keyboard and controller, and by pointer for the
    /// mouse, so every screen behaves the same on all three. Focus is shown one way everywhere: a light mark
    /// glides to the item and its text brightens, with a short flash of the game's split colors as it lands.
    /// </summary>
    internal abstract class FrontScreen
    {
        private const float AppearSeconds = 0.18f, Rise = 12f;
        private readonly List<Focusable> _items = new();
        private CanvasGroup _group;
        private float _appearStart = -1;

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

        /// <summary>Starts the short fade and rise a screen arrives with.</summary>
        internal void Appear()
        {
            _leaving?.Cancel();
            _leaving = null;
            _appearStart = Time.unscaledTime;
            Animate();
        }

        private Fading _leaving;

        /// <summary>Fades the screen out, then hides it, or removes it with <paramref name="destroy"/>.</summary>
        internal void Leave(bool destroy)
        {
            if (Root == null || !Root) return;
            _appearStart = -1;
            if (!Root.gameObject.activeSelf)
            {
                if (destroy) UnityEngine.Object.Destroy(Root.gameObject);
                return;
            }
            // A screen already fading out of view keeps going, unless it now has to be removed for good.
            if (_leaving != null && !destroy) return;
            _leaving?.Cancel();
            _leaving = new Fading(Root, _group, destroy).Begin();
        }

        internal void Animate()
        {
            if (_appearStart < 0 || _group == null || !_group) return;
            var t = Mathf.Clamp01((Time.unscaledTime - _appearStart) / AppearSeconds);
            var eased = Moving.EaseOut(t);
            _group.alpha = eased;
            var offset = -U(Rise) * (1f - eased);
            Root.offsetMin = new Vector2(0, offset);
            Root.offsetMax = new Vector2(0, offset);
            if (t >= 1f) _appearStart = -1;
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

        /// <summary>A step to the previous (-1) or next (1) tab of this screen, on the bumpers or Q and E.</summary>
        internal virtual void OnTab(int step) { }

        /// <summary>A step to the previous (-1) or next (1) section of the menu this screen belongs to.</summary>
        internal virtual void OnSection(int step) { }

        /// <summary>Whether the live world behind this screen is blurred; full-screen menus are, the title row is not.</summary>
        internal virtual bool Blurs => true;

        /// <summary>A dialog over the screen below it, which stays in view, dimmed, while it is open.</summary>
        internal virtual bool Overlay => false;

        /// <summary>Whether the game's logo stays in view; it belongs to the title row and fades under the screens past it.</summary>
        internal virtual bool ShowsLogo => false;

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

        /// <summary>Drops items, for a screen that rebuilds part of itself, such as a tab's rows.</summary>
        protected void Remove(Predicate<Focusable> match)
        {
            if (Focused != null && match(Focused)) Focus(null);
            _items.RemoveAll(match);
        }

        protected void Focus(Focusable item)
        {
            if (ReferenceEquals(item, Focused)) return;
            var previous = Focused;
            Focused = item;
            // Only the player's own moves tick; focus set by the screen itself stays quiet.
            if (_steering && previous != null && item != null) MenuSound.Play(MenuCue.Move);
            previous?.Paint?.Invoke(false);
            if (previous?.Mark != null && !ReferenceEquals(previous.Mark, item?.Mark)) previous.Mark.Target(null);
            item?.Paint?.Invoke(true);
            // The mark glides from where it was; one that was hidden appears in place.
            item?.Mark?.Target(item.Rect, previous?.Mark == null || !ReferenceEquals(previous.Mark, item.Mark));
            item?.Flash?.Pulse();
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
                Sounding(MenuCue.Move, () => Focused.Adjust(dx));
                return;
            }
            var next = Nearest(Focused, dx, dy);
            if (next != null) Focus(next);
        }

        /// <summary>
        /// Takes the frame's input for something of the screen's own, such as text being typed; true when it did,
        /// and focus, Back and the other buttons are left alone this frame.
        /// </summary>
        protected virtual bool CaptureInput() => false;

        internal void HandleInput()
        {
            if (CaptureInput()) return;
            Focusable over = null;
            foreach (var item in _items)
                if (item.Usable && item.Under(FrontInput.Pointer))
                    over = item;
            // The pointer takes focus only when it moves, so it does not fight the keyboard or controller.
            FromPointer = _steering = true;
            try
            {
                if (over != null && FrontInput.PointerMoved) Focus(over);
                if (FrontInput.Click && over != null)
                {
                    Focus(over);
                    var clicked = over;
                    Sounding(MenuCue.Forward, () => clicked.Activate?.Invoke());
                    return;
                }
                Pointer(over);
            }
            finally
            {
                FromPointer = _steering = false;
            }
            var move = FrontInput.Move;
            _steering = true;
            try
            {
                if (move != Vector2Int.zero) Navigate(move.x, move.y);
            }
            finally
            {
                _steering = false;
            }
            if (FrontInput.Confirm)
            {
                if (Focused == null || !Focused.Usable) Focus(First());
                else
                {
                    var focused = Focused;
                    Sounding(MenuCue.Forward, () => focused.Activate?.Invoke());
                }
            }
            else if (FrontInput.Back)
            {
                if (!OnBack()) FrontEnd.Pop();
            }
            else if (FrontInput.Alt1) OnAlt1();
            else if (FrontInput.Alt2) OnAlt2();
            else if (FrontInput.PreviousTab) OnTab(-1);
            else if (FrontInput.NextTab) OnTab(1);
            else if (FrontInput.Section != 0) OnSection(FrontInput.Section);
        }

        private bool _steering;

        /// <summary>Runs an action and plays <paramref name="cue"/> unless the action played a sound of its own.</summary>
        protected static void Sounding(MenuCue cue, Action action)
        {
            var before = MenuSound.Played;
            action();
            if (MenuSound.Played == before) MenuSound.Play(cue);
        }

        /// <summary>Mouse handling beyond clicks and hover, such as dragging a slider.</summary>
        protected virtual void Pointer(Focusable over) { }

        protected Focusable First()
        {
            foreach (var item in _items)
                if (item.Usable) return item;
            return null;
        }

        /// <summary>A text that flashes the split colors when focus lands on its item.</summary>
        protected static Glitch Flashing(TextMeshProUGUI text) => new(text);

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
