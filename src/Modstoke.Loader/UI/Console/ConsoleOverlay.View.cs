// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal static partial class ConsoleOverlay
    {
        private static readonly Color Background = new(0.035f, 0.047f, 0.065f, 0.99f);
        private static readonly Color Surface = new(0.065f, 0.085f, 0.115f, 1f);
        private static readonly Color Foreground = new(0.88f, 0.92f, 0.97f, 1f);
        private static readonly Color Muted = new(0.57f, 0.66f, 0.77f, 1f);
        private static readonly Color Accent = new(0.34f, 0.80f, 0.85f, 1f);
        private static readonly List<LogRow> Rows = new();
        private static TMP_FontAsset _font;
        private static Canvas _canvas;
        private static CanvasScaler _scaler;
        private static RectTransform _panel;
        private static CanvasGroup _panelInteraction;
        private static RectTransform _logArea;
        private static RectTransform _resizeHandle;
        private static RectTransform _catalogContent;
        private static TextMeshProUGUI _status;
        private static TextMeshProUGUI _counter;
        private static TextMeshProUGUI _notice;
        private static TextMeshProUGUI _pauseLabel;
        private static TextMeshProUGUI _followLabel;
        private static TextMeshProUGUI _levelLabel;
        private static TMP_InputField _source;
        private static TMP_InputField _search;
        private static TMP_InputField _detail;
        private static TMP_InputField _command;
        private static GameObject _confirmation;
        private static TMP_InputField _confirmationText;
        private static Button _cancelButton;
        private static bool _resizing;
        private static Vector2 _resizeStart;
        private static Vector2 _resizeSize;
        private static Vector2 _size = new(1280f, 820f);
        private static float _scale = 1f;
        private static int _pageSize = 16;
        private static int _screenWidth;
        private static int _screenHeight;
        private static float _canvasScale;
        private static bool _viewReady;

        private sealed class LogRow
        {
            internal Image Image;
            internal TextMeshProUGUI Text;
            internal long EntryId;
        }

        private static void EnsureView()
        {
            if (_viewReady && _root != null) return;
            _font = TMP_Settings.defaultFontAsset;
            if (_font == null)
            {
                foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                {
                    if (text == null || text.font == null) continue;
                    _font = text.font;
                    break;
                }
            }
            if (_font == null) throw new InvalidOperationException("The game's UI font is not ready.");
            _root = new GameObject("Modstoke Console");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 4800;
            _scaler = _root.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(1600f, 1000f);
            _scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();
            var shade = Image("Shade", _root.transform, new Color(0f, 0f, 0f, 0.55f));
            Fill(shade.rectTransform);
            _panel = Image("Panel", _root.transform, Background).rectTransform;
            _panelInteraction = _panel.gameObject.AddComponent<CanvasGroup>();
            _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.sizeDelta = _size;

            var title = Text(_panel, "MODSTOKE  /  CONSOLE", 22f, Foreground);
            Top(title.rectTransform, 18f, 12f, 410f, 34f);
            var close = Button(_panel, "Close", Hide, out _);
            RightTop(close.GetComponent<RectTransform>(), 16f, 12f, 78f, 34f);
            _status = Text(_panel, "Waiting for loader status", 14f, Accent);
            TopStretch(_status.rectTransform, 18f, 52f, 18f, 26f);

            var level = Button(_panel, "Level: All", CycleLevel, out _levelLabel);
            Top(level.GetComponent<RectTransform>(), 18f, 90f, 132f, 34f);
            _source = InputField(_panel, "Filter source", false);
            Top(_source.GetComponent<RectTransform>(), 160f, 90f, 210f, 34f);
            _search = InputField(_panel, "Search messages", false);
            TopStretch(_search.GetComponent<RectTransform>(), 380f, 90f, 18f, 34f);
            _source.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => ApplyFilters()));
            _search.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => ApplyFilters()));

            var pause = Button(_panel, "Pause", TogglePause, out _pauseLabel);
            Top(pause.GetComponent<RectTransform>(), 18f, 134f, 100f, 32f);
            var follow = Button(_panel, "Follow: On", ToggleFollow, out _followLabel);
            Top(follow.GetComponent<RectTransform>(), 128f, 134f, 116f, 32f);
            var clear = Button(_panel, "Clear view", ClearView, out _);
            Top(clear.GetComponent<RectTransform>(), 254f, 134f, 110f, 32f);
            var smaller = Button(_panel, "A-", () => ChangeScale(-0.1f), out _);
            RightTop(smaller.GetComponent<RectTransform>(), 64f, 134f, 42f, 32f);
            var larger = Button(_panel, "A+", () => ChangeScale(0.1f), out _);
            RightTop(larger.GetComponent<RectTransform>(), 18f, 134f, 42f, 32f);

            _logArea = Image("Log entries", _panel, Surface).rectTransform;
            Region(_logArea, new Vector2(0f, 0f), new Vector2(0.61f, 1f), new Vector2(18f, 120f), new Vector2(-6f, -180f));
            _logArea.gameObject.AddComponent<RectMask2D>();
            Rows.Clear();
            for (var i = 0; i < 32; i++)
            {
                var row = new LogRow();
                var button = Button(_logArea, "", () => SelectEntry(row.EntryId), out row.Text);
                row.Image = button.GetComponent<Image>();
                row.Text.alignment = TextAlignmentOptions.MidlineLeft;
                row.Text.fontSize = 14f;
                Fill(row.Text.rectTransform, 9f, 0f, 9f, 0f);
                TopStretch(button.GetComponent<RectTransform>(), 4f, 4f + i * 28f, 4f, 27f);
                Rows.Add(row);
            }
            var pages = Image("Log navigation", _logArea, Background).rectTransform;
            Region(pages, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 38f));
            var previous = Button(pages, "Previous", () => ChangePage(-1), out _);
            Top(previous.GetComponent<RectTransform>(), 4f, 4f, 84f, 30f);
            var next = Button(pages, "Next", () => ChangePage(1), out _);
            RightTop(next.GetComponent<RectTransform>(), 4f, 4f, 72f, 30f);
            _counter = Text(pages, "", 12f, Muted);
            Fill(_counter.rectTransform, 94f, 0f, 82f, 0f);
            _counter.alignment = TextAlignmentOptions.Center;

            var right = Image("Details and commands", _panel, Background).rectTransform;
            Region(right, new Vector2(0.61f, 0f), Vector2.one, new Vector2(6f, 120f), new Vector2(-18f, -180f));
            var detailTitle = Text(right, "SELECTED MESSAGE", 13f, Muted);
            TopStretch(detailTitle.rectTransform, 0f, 0f, 0f, 24f);
            _detail = InputField(right, "Select a log entry to read its full message.", true);
            _detail.readOnly = true;
            _detail.characterLimit = 0;
            Region(_detail.GetComponent<RectTransform>(), new Vector2(0f, 0.42f), Vector2.one,
                new Vector2(0f, 12f), new Vector2(0f, -30f));
            var catalog = Image("Command catalog", right, Surface).rectTransform;
            Region(catalog, Vector2.zero, new Vector2(1f, 0.42f), Vector2.zero, Vector2.zero);
            var catalogTitle = Text(catalog, "COMMANDS  /  click to prepare", 13f, Muted);
            TopStretch(catalogTitle.rectTransform, 10f, 4f, 10f, 28f);
            var viewport = Image("Command viewport", catalog, Surface).rectTransform;
            Fill(viewport, 4f, 36f, 18f, 4f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            scroll.verticalScrollbar = Scrollbar(catalog, 36f);
            _catalogContent = new GameObject("Commands").AddComponent<RectTransform>();
            _catalogContent.SetParent(viewport, false);
            _catalogContent.anchorMin = new Vector2(0f, 1f);
            _catalogContent.anchorMax = Vector2.one;
            _catalogContent.pivot = new Vector2(0.5f, 1f);
            _catalogContent.sizeDelta = Vector2.zero;
            scroll.content = _catalogContent;

            _command = InputField(_panel, "Enter a command and its arguments  /  Up and Down for history", false);
            // Room for a full name and the hub's argument limit, so an overlong line is refused rather than cut.
            _command.characterLimit = 256 + 1 + Modstoke.Diagnostics.ConsoleHub.ArgumentLimit;
            Region(_command.GetComponent<RectTransform>(), Vector2.zero, new Vector2(1f, 0f),
                new Vector2(18f, 67f), new Vector2(-114f, 103f));
            _command.onSubmit.AddListener((UnityAction<string>)new Action<string>(_ => PrepareCommand()));
            var run = Button(_panel, "Run", PrepareCommand, out _);
            var runRect = run.GetComponent<RectTransform>();
            runRect.anchorMin = runRect.anchorMax = new Vector2(1f, 0f);
            runRect.pivot = new Vector2(1f, 0f);
            runRect.anchoredPosition = new Vector2(-18f, 67f);
            runRect.sizeDelta = new Vector2(86f, 36f);
            _notice = Text(_panel, "Only commands listed in the catalog are available.", 13f, Muted);
            Region(_notice.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(18f, 19f), new Vector2(-36f, 58f));
            _notice.enableWordWrapping = true;

            _resizeHandle = Image("Drag to resize", _panel, Surface).rectTransform;
            _resizeHandle.anchorMin = _resizeHandle.anchorMax = new Vector2(1f, 0f);
            _resizeHandle.pivot = new Vector2(1f, 0f);
            _resizeHandle.sizeDelta = new Vector2(26f, 26f);
            var grip = Text(_resizeHandle, "//", 16f, Muted);
            Fill(grip.rectTransform);
            grip.alignment = TextAlignmentOptions.Center;
            CreateConfirmation();
            _catalogSignature = null;
            _screenWidth = 0;
            _viewReady = true;
        }

        private static void CreateConfirmation()
        {
            _confirmation = Image("Confirm command", _panel, new Color(0.015f, 0.025f, 0.04f, 0.97f)).gameObject;
            _confirmation.AddComponent<CanvasGroup>().ignoreParentGroups = true;
            Fill(_confirmation.GetComponent<RectTransform>());
            var box = Image("Confirmation", _confirmation.transform, Surface).rectTransform;
            box.anchorMin = new Vector2(0.17f, 0.28f);
            box.anchorMax = new Vector2(0.83f, 0.72f);
            box.offsetMin = box.offsetMax = Vector2.zero;
            var title = Text(box, "CONFIRM COMMAND", 20f, Accent);
            TopStretch(title.rectTransform, 22f, 18f, 22f, 34f);
            _confirmationText = InputField(box, "", true);
            _confirmationText.readOnly = true;
            Fill(_confirmationText.GetComponent<RectTransform>(), 22f, 64f, 22f, 74f);
            _cancelButton = Button(box, "Cancel", CancelConfirmation, out _);
            var confirm = Button(box, "Confirm", ConfirmCommand, out _);
            Region(_cancelButton.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0.5f, 0f),
                new Vector2(22f, 20f), new Vector2(-6f, 58f));
            Region(confirm.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                new Vector2(6f, 20f), new Vector2(-22f, 58f));
            _confirmation.SetActive(false);
        }

        private static void ChangeScale(float delta)
        {
            _scale = Mathf.Clamp(_scale + delta, 0.8f, 1.3f);
            _scaler.referenceResolution = new Vector2(1600f / _scale, 1000f / _scale);
            _screenWidth = 0;
        }

        private static void UpdateSize()
        {
            if (_pendingCommand == null && Input.GetMouseButtonDown(0)
                && RectTransformUtility.RectangleContainsScreenPoint(_resizeHandle, Input.mousePosition, null))
            {
                _resizing = true;
                _resizeStart = Input.mousePosition;
                _resizeSize = _size;
            }
            if (!Input.GetMouseButton(0)) _resizing = false;
            if (!_resizing && _screenWidth == Screen.width && _screenHeight == Screen.height
                && Mathf.Approximately(_canvasScale, _canvas.scaleFactor)) return;
            if (_resizing)
            {
                Vector2 delta = ((Vector2)Input.mousePosition - _resizeStart) / _canvas.scaleFactor;
                _size = _resizeSize + new Vector2(delta.x * 2f, -delta.y * 2f);
            }
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            _canvasScale = _canvas.scaleFactor;
            var available = new Vector2(Screen.width, Screen.height) / _canvas.scaleFactor - new Vector2(32f, 32f);
            _size.x = Mathf.Clamp(_size.x, Mathf.Min(960f, available.x), available.x);
            _size.y = Mathf.Clamp(_size.y, Mathf.Min(640f, available.y), available.y);
            _panel.sizeDelta = _size;
            _pageSize = Mathf.Clamp(Mathf.FloorToInt((_size.y - 346f) / 28f), 4, Rows.Count);
            RenderRows();
        }

        private static Image Image(string name, Transform parent, Color color)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            var image = root.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static TextMeshProUGUI Text(Transform parent, string value, float size, Color color)
        {
            var root = new GameObject("Label");
            root.transform.SetParent(parent, false);
            var text = root.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.text = value;
            text.richText = false;
            text.fontSize = size;
            text.color = color;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            return text;
        }

        private static Button Button(Transform parent, string caption, Action action, out TextMeshProUGUI label)
        {
            var image = Image(caption.Length == 0 ? "Log row" : caption, parent, Surface);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.35f, 1.45f, 1.55f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = new Color(0.65f, 0.85f, 0.95f, 1f);
            button.colors = colors;
            button.onClick.AddListener((UnityAction)new Action(() =>
            {
                try { action(); }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("A Modstoke console action failed: " + error);
                    Hide();
                }
            }));
            label = Text(image.transform, caption, 14f, Foreground);
            label.alignment = TextAlignmentOptions.Center;
            Fill(label.rectTransform, 6f, 0f, 6f, 0f);
            return button;
        }

        private static TMP_InputField InputField(Transform parent, string hint, bool multiline)
        {
            var image = Image(hint, parent, Surface);
            var input = image.gameObject.AddComponent<TMP_InputField>();
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.characterLimit = multiline ? 0 : 256;
            input.customCaretColor = true;
            input.caretColor = Accent;
            input.caretWidth = 2;
            input.selectionColor = new Color(0.2f, 0.65f, 0.75f, 0.3f);
            input.onFocusSelectAll = false;
            input.restoreOriginalTextOnEscape = false;
            input.richText = false;
            var viewport = new GameObject("Text viewport").AddComponent<RectTransform>();
            viewport.SetParent(image.transform, false);
            Fill(viewport, 10f, 5f, multiline ? 22f : 10f, 5f);
            viewport.gameObject.AddComponent<RectMask2D>();
            input.textViewport = viewport;
            var text = Text(viewport, "", 15f, Foreground);
            text.enableWordWrapping = multiline;
            text.overflowMode = multiline ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            text.alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            Fill(text.rectTransform);
            var placeholder = Text(viewport, hint, 14f, Muted);
            placeholder.enableWordWrapping = multiline;
            placeholder.alignment = text.alignment;
            Fill(placeholder.rectTransform);
            input.textComponent = text;
            input.placeholder = placeholder;
            if (multiline) input.verticalScrollbar = Scrollbar(image.transform, 5f);
            return input;
        }

        private static Scrollbar Scrollbar(Transform parent, float top)
        {
            var track = Image("Scrollbar", parent, Background);
            Region(track.rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-14f, 5f), new Vector2(-4f, -top));
            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            var handle = Image("Handle", track.transform, Muted);
            Fill(handle.rectTransform);
            scrollbar.handleRect = handle.rectTransform;
            scrollbar.targetGraphic = handle;
            return scrollbar;
        }

        private static void Fill(RectTransform rect, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
            => Region(rect, Vector2.zero, Vector2.one, new Vector2(left, bottom), new Vector2(-right, -top));

        private static void Region(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void Top(RectTransform rect, float left, float top, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(left, -top);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void TopStretch(RectTransform rect, float left, float top, float right, float height)
            => Region(rect, new Vector2(0f, 1f), Vector2.one, new Vector2(left, -top - height), new Vector2(-right, -top));

        private static void RightTop(RectTransform rect, float right, float top, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.anchoredPosition = new Vector2(-right, -top);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
