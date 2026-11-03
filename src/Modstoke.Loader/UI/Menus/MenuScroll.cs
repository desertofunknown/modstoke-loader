// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Scroll regions for the Mods menu: a fixed eased wheel step, paging keys and per-view pixel offsets.

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    internal sealed class MenuScroll
    {
        internal const float PagePitch = 60f;
        internal const float NavPitch = 44f;
        private const float WheelRows = 3f;
        private const float MaxNotches = 3f;
        private const float SmoothTime = 0.08f;
        private const float PageFraction = 0.9f;
        private const float ScrollbarWidth = 6f;
        private const float ScrollbarSpacing = 6f;
        private const float ContentSpacing = 4f;
        private const float InputSystemNotch = 120f;
        internal static readonly Color TrackColor = new Color(1f, 1f, 1f, 0.04f);
        internal static readonly Color HandleColor = new Color(0.72f, 0.74f, 0.72f, 0.55f);
        private static readonly List<MenuScroll> Regions = new List<MenuScroll>();
        private static bool _inputSystemWheel = true;
        private float _target;
        private float _velocity;
        private float _written;
        private bool _animating;

        private MenuScroll(RectTransform root, ScrollRect scrollRect, RectTransform viewport, RectTransform content, Scrollbar scrollbar, float basePitch)
        {
            Root = root;
            ScrollRect = scrollRect;
            Viewport = viewport;
            Content = content;
            Scrollbar = scrollbar;
            BasePitch = basePitch;
        }

        internal RectTransform Root { get; }
        internal ScrollRect ScrollRect { get; }
        internal RectTransform Viewport { get; }
        internal RectTransform Content { get; }
        internal Scrollbar Scrollbar { get; }
        /// <summary>The height of one base row; a wheel notch moves three of them.</summary>
        internal float BasePitch { get; }
        /// <summary>Takes the paging keys when the pointer is over no region.</summary>
        internal bool Primary { get; set; }
        /// <summary>A child that scrolls itself, such as the raw config editor, so the wheel is left to it.</summary>
        internal RectTransform WheelExclusion { get; set; }
        internal static float LastWheelTime { get; private set; } = float.NegativeInfinity;

        /// <summary>Pixel offset of the content from the top.</summary>
        internal float Position => Content.anchoredPosition.y;
        internal float MaxPosition => Mathf.Max(0f, Content.rect.height - Viewport.rect.height);
        internal bool IsMoving => _animating || ScrollRect.velocity.sqrMagnitude > 1f;
        private bool Alive => Root != null && Content != null && Viewport != null;

        /// <summary>
        /// Builds ScrollRect root, Viewport (RectMask2D) with Content, and a Scrollbar that hides and gives its width
        /// back when the content fits. The caller places <see cref="Root"/>.
        /// </summary>
        internal static MenuScroll Create(Transform parent, string name, float basePitch, bool isolateContent = false)
        {
            var rootObject = new GameObject(name);
            rootObject.transform.SetParent(parent, false);
            var root = rootObject.AddComponent<RectTransform>();
            var scroll = rootObject.AddComponent<ScrollRect>();

            var viewport = MenuWidgets.CreateImage("Viewport", root, new Color(0f, 0f, 0f, 0.001f));
            // Catches drags over the gaps between rows.
            viewport.raycastTarget = true;
            MenuWidgets.Stretch(viewport.rectTransform, 0f, 0f, 0f, 0f);
            viewport.rectTransform.pivot = new Vector2(0f, 1f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var contentObject = new GameObject("Content");
            contentObject.transform.SetParent(viewport.transform, false);
            var content = contentObject.AddComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            var layout = contentObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = ContentSpacing;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            contentObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            if (isolateContent)
            {
                // Row text changes then re-batch only this canvas, not the status strip and footer.
                contentObject.AddComponent<Canvas>();
                contentObject.AddComponent<GraphicRaycaster>();
            }

            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            // The wheel is read once per frame in Tick, so the step does not depend on the input module.
            scroll.scrollSensitivity = 0f;
            scroll.viewport = viewport.rectTransform;
            scroll.content = content;
            var scrollbar = CreateScrollbar(root);
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scroll.verticalScrollbarSpacing = ScrollbarSpacing;

            var region = new MenuScroll(root, scroll, viewport.rectTransform, content, scrollbar, basePitch);
            Regions.Add(region);
            return region;
        }

        internal void Release()
        {
            Regions.Remove(this);
            if (Root != null)
            {
                Object.Destroy(Root.gameObject);
            }
        }

        /// <summary>Runs once per frame while the menu is visible. A modal owns the wheel and keys while it is open.</summary>
        internal static void Tick(bool blocked)
        {
            for (var i = Regions.Count - 1; i >= 0; i--)
            {
                if (!Regions[i].Alive)
                {
                    Regions.RemoveAt(i);
                }
            }

            if (Regions.Count == 0)
            {
                return;
            }

            if (!blocked)
            {
                var notches = ReadWheel();
                if (notches != 0f)
                {
                    LastWheelTime = Time.unscaledTime;
                    Vector2 pointer = Input.mousePosition;
                    var region = RegionAt(pointer);
                    if (region != null && !region.OverExclusion(pointer))
                    {
                        region.Wheel(notches);
                    }
                }

                if (Input.anyKeyDown)
                {
                    ReadKeys();
                }
            }

            var deltaTime = Time.unscaledDeltaTime;
            foreach (var region in Regions)
            {
                region.Animate(deltaTime);
            }
        }

        internal void ScrollBy(float pixels)
        {
            ScrollTo((_animating ? _target : Position) + pixels);
        }

        internal void Page(int direction)
        {
            ScrollBy(direction * Viewport.rect.height * PageFraction);
        }

        internal void ScrollToTop()
        {
            ScrollTo(0f);
        }

        internal void ScrollToBottom()
        {
            ScrollTo(MaxPosition);
        }

        /// <summary>Eases to a pixel offset from the top, on unscaled time because the pause menu may stop the clock.</summary>
        internal void ScrollTo(float position)
        {
            if (!_animating)
            {
                _written = Position;
                _velocity = 0f;
            }

            ScrollRect.velocity = Vector2.zero;
            _target = Mathf.Clamp(position, 0f, MaxPosition);
            _animating = true;
        }

        internal void JumpTo(float position)
        {
            _animating = false;
            _velocity = 0f;
            ScrollRect.StopMovement();
            SetPosition(Mathf.Clamp(position, 0f, MaxPosition));
        }

        internal void Save(string viewKey)
        {
            Save(MenuState.ViewState, viewKey);
        }

        /// <summary>Saves into a view state of its own, for regions outside the mods menu such as screen host panels.</summary>
        internal void Save(MenuViewState state, string viewKey)
        {
            if (Alive)
            {
                state.SetScrollOffset(viewKey, _animating ? _target : Position);
            }
        }

        /// <summary>Lands on the saved offset for a view that is being shown in place of another.</summary>
        internal void Restore(string viewKey)
        {
            Restore(MenuState.ViewState, viewKey);
        }

        internal void Restore(MenuViewState state, string viewKey)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(Content);
            JumpTo(state.GetScrollOffset(viewKey));
        }

        internal void ScrollIntoView(RectTransform target, float padding = 24f)
        {
            if (TryFit(target, padding, out var position))
            {
                ScrollTo(position);
            }
        }

        /// <summary>Lands on a child at once, for a region that has just been shown.</summary>
        internal void JumpIntoView(RectTransform target, float padding = 24f)
        {
            if (TryFit(target, padding, out var position))
            {
                JumpTo(position);
            }
        }

        // The nearest offset that shows the whole child with padding, or its top when it is taller than the view.
        private bool TryFit(RectTransform target, float padding, out float position)
        {
            position = 0f;
            if (target == null || !target.IsChildOf(Content))
            {
                return false;
            }

            var rect = target.rect;
            var top = Depth(target, rect.yMax);
            var bottom = Depth(target, rect.yMin);
            var current = _animating ? _target : Position;
            var view = Viewport.rect.height;
            if (top - padding < current)
            {
                position = top - padding;
                return true;
            }

            if (bottom + padding > current + view)
            {
                position = Mathf.Min(top - padding, bottom + padding - view);
                return true;
            }

            return false;
        }

        /// <summary>Eases a child's top edge to the top of the viewport, less <paramref name="padding"/>.</summary>
        internal void ScrollToChild(RectTransform target, float padding)
        {
            if (target != null && target.IsChildOf(Content))
            {
                ScrollTo(Depth(target, target.rect.yMax) - padding);
            }
        }

        // Content's pivot is its top edge, so local y runs negative downwards.
        private float Depth(RectTransform target, float localY)
        {
            return -Content.InverseTransformPoint(target.TransformPoint(new Vector3(0f, localY, 0f))).y;
        }

        private void Wheel(float notches)
        {
            var steps = Mathf.Sign(notches) * Mathf.Min(Mathf.Abs(notches), MaxNotches);
            ScrollBy(-steps * WheelRows * BasePitch);
        }

        private void Animate(float deltaTime)
        {
            if (!_animating)
            {
                return;
            }

            var position = Position;
            if (Mathf.Abs(position - _written) > 0.5f)
            {
                // A drag, the scrollbar or inertia moved the content, so the target gives way to it.
                _animating = false;
                _velocity = 0f;
                return;
            }

            var target = Mathf.Clamp(_target, 0f, MaxPosition);
            var next = Mathf.SmoothDamp(position, target, ref _velocity, SmoothTime, Mathf.Infinity, deltaTime);
            if (Mathf.Abs(next - target) < 0.5f)
            {
                next = target;
                _animating = false;
                _velocity = 0f;
            }

            SetPosition(next);
        }

        private void SetPosition(float position)
        {
            Content.anchoredPosition = new Vector2(0f, position);
            _written = position;
        }

        private bool OverExclusion(Vector2 pointer)
        {
            return WheelExclusion != null && WheelExclusion.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(WheelExclusion, pointer, null);
        }

        private static MenuScroll RegionAt(Vector2 pointer)
        {
            for (var i = Regions.Count - 1; i >= 0; i--)
            {
                var region = Regions[i];
                if (region.Root.gameObject.activeInHierarchy
                    && RectTransformUtility.RectangleContainsScreenPoint(region.Viewport, pointer, null))
                {
                    return region;
                }
            }

            return null;
        }

        // With the pointer elsewhere, the keys page the region the keyboard or gamepad is in.
        private static MenuScroll SelectedRegion()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            if (selected == null)
            {
                return null;
            }

            var transform = selected.transform;
            foreach (var region in Regions)
            {
                if (region.Root.gameObject.activeInHierarchy && transform.IsChildOf(region.Content))
                {
                    return region;
                }
            }

            return null;
        }

        private static MenuScroll PrimaryRegion()
        {
            foreach (var region in Regions)
            {
                if (region.Primary && region.Root.gameObject.activeInHierarchy)
                {
                    return region;
                }
            }

            return null;
        }

        /// <summary>This frame's wheel movement in notches; positive scrolls up.</summary>
        internal static float ReadWheel()
        {
            var delta = Input.mouseScrollDelta.y;
            if (delta != 0f || !_inputSystemWheel)
            {
                return delta;
            }

            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                return mouse != null ? mouse.scroll.ReadValue().y / InputSystemNotch : 0f;
            }
            catch (Exception ex)
            {
                // The legacy delta still drives the wheel, so stop retrying a path that fails every frame.
                _inputSystemWheel = false;
                RuntimeEntryPoint.LogSource?.LogWarning("Mods menu wheel: Input System scroll is unavailable: " + ex.Message);
                return 0f;
            }
        }

        private static void ReadKeys()
        {
            var direction = Input.GetKeyDown(KeyCode.PageDown) ? 1 : Input.GetKeyDown(KeyCode.PageUp) ? -1 : 0;
            var home = Input.GetKeyDown(KeyCode.Home);
            var end = Input.GetKeyDown(KeyCode.End);
            // Home and End move the caret while a text field has focus.
            if ((direction == 0 && !home && !end) || IsTyping())
            {
                return;
            }

            var region = RegionAt(Input.mousePosition) ?? SelectedRegion() ?? PrimaryRegion();
            if (region == null)
            {
                return;
            }

            if (home)
            {
                region.ScrollToTop();
            }
            else if (end)
            {
                region.ScrollToBottom();
            }
            else
            {
                region.Page(direction);
            }
        }

        private static bool IsTyping()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            var input = selected != null ? selected.GetComponent<TMP_InputField>() : null;
            return input != null && input.isFocused;
        }

        // Flat colours only: the game's scrollbar and brush sprites fringe red and green at this width.
        private static Scrollbar CreateScrollbar(RectTransform root)
        {
            var track = MenuWidgets.CreateImage("Scrollbar Vertical", root, TrackColor);
            // The track takes clicks so a click beside the handle pages.
            track.raycastTarget = true;
            AnchorRight(track.rectTransform, ScrollbarWidth);
            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            MenuWidgets.NoNavigation(scrollbar);
            var handle = MenuWidgets.CreateImage("Handle", track.transform, HandleColor);
            handle.raycastTarget = true;
            MenuWidgets.Stretch(handle.rectTransform, 0f, 0f, 0f, 0f);
            scrollbar.handleRect = handle.rectTransform;
            scrollbar.targetGraphic = handle;
            return scrollbar;
        }

        // The ScrollRect drives the bar's height and the viewport's width; only the right edge and width are ours.
        private static void AnchorRight(RectTransform rect, float width)
        {
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, 0f);
        }
    }
}
