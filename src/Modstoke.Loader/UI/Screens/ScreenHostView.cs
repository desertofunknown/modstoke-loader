// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Input;
using TheForest.Utils;
using Modstoke.Sons;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The screen host canvas, built once and kept across scenes like the mods menu. It sits between the mods menu and
    /// the console, uses the menu's panel geometry and theme, and shades only behind its pane so the stage stays clear.
    /// </summary>
    internal static class ScreenHostView
    {
        private const int SortingOrder = 4700;
        private const float ReferenceHeight = 1080f;
        // The front end's veil behind the panel side; the panel itself has no fill of its own.
        private static readonly Color ShadeColor = MenuKit.Hex(0x050606, 0.84f);
        // Clear enough to see through, but still a raycast target, so clicks never reach the pause menu underneath.
        private static readonly Color BlockerColor = new Color(0f, 0f, 0f, 0.001f);
        private static GameObject _root;
        private static Canvas _canvas;
        private static GameObject _capture;
        private static RectTransform _shade;
        private static RectTransform _frame;
        private static RectTransform _left;
        private static LayoutElement _leftElement;
        private static RectTransform _content;
        private static RawImage _stageImage;
        private static MenuLabel _title;
        private static MenuLabel _subtitle;
        private static MenuLabel _status;
        private static MenuLabel _hints;
        private static Button _back;
        private static MenuScroll _scroll;
        private static ScreenPanel _panel;
        private static ScreenHostSession _session;
        private static string _customHints;
        private static string _themeTier = "plain";
        private static float _inset = -1f;
        private static float _navWidth = -1f;
        private static int _screenWidth;
        private static int _screenHeight;

        internal static bool IsAlive => _root != null && _canvas != null;
        internal static string Tier => _themeTier;
        internal static ScreenPanel Panel => _panel;
        internal static RectTransform LeftPanel => _left;
        internal static RectTransform ContentRect => _content;

        /// <summary>Builds the canvas if needed and binds it to a new session, hidden until the session opens.</summary>
        internal static void Prepare(ScreenHostSession session)
        {
            EnsureCreated();
            _session = session;
            _customHints = null;
            _title.Set((session.Title ?? "").ToUpperInvariant());
            _subtitle.Show(session.Subtitle);
            _status.Set("");
            RefreshHints(session);
            _stageImage.texture = null;
            _stageImage.gameObject.SetActive(false);
            _capture.SetActive(false);
            _root.SetActive(false);
            _panel.Begin(session.OwnerId);
        }

        internal static void Show(ScreenHostSession session)
        {
            ApplyGeometry();
            _root.SetActive(true);
            _panel.Show();
        }

        internal static void Tick()
        {
            if (Screen.width != _screenWidth || Screen.height != _screenHeight)
                ApplyGeometry();
            MenuScroll.Tick(false);
            _panel.Tick(Time.unscaledTime);
        }

        internal static void Hide()
        {
            _session = null;
            _customHints = null;
            _panel?.End();
            if (_stageImage != null)
            {
                _stageImage.texture = null;
                _stageImage.gameObject.SetActive(false);
            }
            if (_capture != null)
                _capture.SetActive(false);
            if (_root != null)
                _root.SetActive(false);
        }

        internal static void SetCapture(bool active)
        {
            if (_capture != null)
                _capture.SetActive(active);
        }

        internal static void SetStatus(string text)
        {
            _status?.Set(text ?? "");
        }

        internal static void SetHints(ScreenHostSession session, string text)
        {
            _customHints = text;
            RefreshHints(session);
        }

        internal static void RefreshHints(ScreenHostSession session)
        {
            if (_hints == null)
                return;
            if (_customHints != null)
            {
                _hints.Set(_customHints);
                return;
            }

            var back = InputBindings.Snapshot().TryGetValue(KeybindIds.Back, out var keybind) ? keybind.KeyName : KeyCode.Escape.ToString();
            _hints.Set(session?.Stage != null
                ? back + " back     Drag rotate     Wheel zoom     Middle-drag raise     Double-click reset"
                : back + " back");
        }

        internal static ScreenStageView CreateStageView(PreviewStage stage, bool disposeOnClose)
        {
            return new ScreenStageView(stage, disposeOnClose, _stageImage, _content, _canvas);
        }

        internal static bool PointerOverContent()
        {
            return _content != null && _root != null && _root.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(_content, Input.mousePosition, null);
        }

        internal static bool Contains(GameObject target)
        {
            return _root != null && target != null && target.transform.IsChildOf(_root.transform);
        }

        internal static void Release()
        {
            _scroll?.Release();
            if (_root != null)
                Object.Destroy(_root);
            _root = null;
            _canvas = null;
            _capture = null;
            _shade = null;
            _frame = null;
            _left = null;
            _leftElement = null;
            _content = null;
            _stageImage = null;
            _title = null;
            _subtitle = null;
            _status = null;
            _hints = null;
            _back = null;
            _scroll = null;
            _panel = null;
            _session = null;
            _customHints = null;
            _inset = -1f;
            _navWidth = -1f;
        }

        private static void ApplyGeometry()
        {
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            MenuRenderer.PanelGeometry(out var inset, out var marginY, out var navWidth);
            if (inset == _inset && navWidth == _navWidth)
                return;

            _inset = inset;
            _navWidth = navWidth;
            MenuWidgets.Stretch(_frame, inset, marginY, inset, marginY);
            _leftElement.minWidth = navWidth;
            _leftElement.preferredWidth = navWidth;
            _shade.sizeDelta = new Vector2(inset + navWidth, 0f);
        }

        private static void EnsureCreated()
        {
            // A screen first built before the options scene was captured picks up the theme on its next open.
            if (IsAlive && _themeTier == MenuTheme.TierName)
                return;

            Release();
            using (Timings.Measure("ui.screen-host", "create", MenuTheme.TierName))
            {
                _themeTier = MenuTheme.TierName;
                _root = new GameObject("Modstoke Screen Host");
                Object.DontDestroyOnLoad(_root);
                _root.SetActive(false);

                _canvas = _root.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = SortingOrder;
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, ReferenceHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 1f;
                _root.AddComponent<GraphicRaycaster>();
                CreateCapture(_root.transform);

                var blocker = MenuWidgets.CreateImage("Blocker", _root.transform, BlockerColor);
                MenuWidgets.Stretch(blocker.rectTransform, 0f, 0f, 0f, 0f);
                blocker.raycastTarget = true;

                var shade = MenuWidgets.CreateImage("Shade", _root.transform, ShadeColor);
                _shade = shade.rectTransform;
                _shade.anchorMin = Vector2.zero;
                _shade.anchorMax = new Vector2(0f, 1f);
                _shade.pivot = new Vector2(0f, 0.5f);
                _shade.anchoredPosition = Vector2.zero;

                _frame = MenuWidgets.CreateRect("Frame", _root.transform);
                var columns = _frame.gameObject.AddComponent<HorizontalLayoutGroup>();
                columns.spacing = 16f;
                columns.childControlWidth = true;
                columns.childForceExpandWidth = false;
                columns.childControlHeight = true;
                columns.childForceExpandHeight = true;

                CreateLeftPanel(_frame);
                _content = MenuWidgets.CreateRect("Content", _frame);
                MenuWidgets.Flexible(_content.gameObject, 1f);
                var stage = new GameObject("Stage");
                stage.transform.SetParent(_content, false);
                _stageImage = stage.AddComponent<RawImage>();
                _stageImage.color = Color.white;
                _stageImage.raycastTarget = true;
                MenuWidgets.Stretch(_stageImage.rectTransform, 0f, 0f, 0f, 0f);
                stage.SetActive(false);

                _panel = new ScreenPanel(_scroll, _back);
                ApplyGeometry();
            }
        }

        private static void CreateLeftPanel(Transform parent)
        {
            var panel = MenuWidgets.CreateImage("Panel", parent, Color.clear);
            _left = panel.rectTransform;
            _leftElement = panel.gameObject.AddComponent<LayoutElement>();
            _leftElement.flexibleWidth = 0f;
            var stack = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(28, 28, 0, 0);
            stack.spacing = 4f;
            stack.childControlWidth = true;
            stack.childForceExpandWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandHeight = false;

            var header = MenuRenderer.Band(panel.transform, "Header", 76f, false);
            var titles = MenuWidgets.Column(header, "Titles", 1f);
            _title = MenuLabel.Create(titles, "Title", 28f, MenuWidgets.TextColor, true, false);
            _subtitle = MenuLabel.Create(titles, "Subtitle", 15f, MenuWidgets.MutedColor, false, false);
            _subtitle.Text.richText = false;

            _scroll = MenuScroll.Create(panel.transform, "PanelScroll", MenuScroll.PagePitch, true);
            _scroll.Primary = true;
            MenuRenderer.Grow(_scroll.Root);

            var status = MenuRenderer.Band(panel.transform, "StatusStrip", 52f, true);
            _status = MenuLabel.Create(status, "Status", 15f, MenuWidgets.MutedColor, false, true, 2);
            _status.Text.richText = false;
            MenuWidgets.Flexible(_status.Text.gameObject, 1f);

            var footer = MenuRenderer.Band(panel.transform, "Footer", 84f, false);
            _back = MenuWidgets.CreateButton(footer, "BACK", 160f, out _);
            _back.onClick.AddListener((UnityAction)new Action(OnBack));
            _hints = MenuLabel.Create(footer, "KeyHints", 14f, MenuWidgets.MutedColor, false, true, 2);
            _hints.Text.alignment = TextAlignmentOptions.MidlineLeft;
            MenuWidgets.Flexible(_hints.Text.gameObject, 1f);
        }

        // The mods menu's cursor and action map recipe, on a child so it can be re-applied by toggling. The event system
        // comes from ScreenHost's lease.
        private static void CreateCapture(Transform parent)
        {
            _capture = new GameObject("Input");
            _capture.transform.SetParent(parent, false);
            _capture.SetActive(false);
            try
            {
                var cursor = _capture.AddComponent<InputCursorState>();
                cursor._enabled = true;
                cursor._hardwareCursor = true;
                cursor._priority = 100;

                var actionMap = _capture.AddComponent<InputActionMapState>();
                actionMap._applyState = InputState.Console;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host cursor and input state are unavailable: " + error.Message);
            }
        }

        private static void OnBack()
        {
            var session = _session;
            if (session != null && ScreenHost.IsCurrent(session) && session.State == ScreenHostState.Open)
                ScreenHost.Back(session);
        }
    }
}
