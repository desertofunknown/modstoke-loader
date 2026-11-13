// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal static partial class ConsoleOverlay
    {
        // Sizes are in the console canvas's own units: it scales with the screen, and A- and A+ scale it further.
        private const float Pad = 28f, HeaderHeight = 108f, FooterHeight = 86f, Gutter = 28f, BodyTop = 14f;
        private const float ColumnsHeight = 26f, CounterHeight = 26f, RowHeight = 26f, ShadowHeight = 28f;
        private const float RunWidth = 132f, TimeWidth = 70f, BadgeWidth = 54f, ScrimAlpha = 0.5f;
        private static readonly Color Veil = MenuKit.Hex(0x0B0C0C, 0.97f);
        private static readonly Color Raised = MenuKit.Hex(0x141616, 0.99f);
        private static readonly Color Clear = new(0f, 0f, 0f, 0f);

        /// <summary>Anything on the panel that lights up under the pointer.</summary>
        private sealed class Control
        {
            internal RectTransform Rect;
            internal Image Back, Under;
            internal TextMeshProUGUI Label, Count;
            internal Button Button;
            internal Action<Control> Paint;
            internal Func<bool> Lit;
            internal bool Hover, Modal;

            internal bool On => Lit != null && Lit();
        }

        private static readonly List<Control> Controls = new();
        private static readonly List<(TMP_InputField Field, Image Rule)> Fields = new();
        private static TMP_FontAsset _bold, _regular;
        private static bool _fallbackFont, _viewReady, _layoutDirty, _dragging;
        private static Canvas _canvas;
        private static CanvasScaler _scaler;
        private static CanvasGroup _rootGroup, _panelInteraction;
        private static Image _scrim, _statusDot, _grip;
        private static RectTransform _panel, _logColumn, _logViewport, _right, _messagePane, _commandsPane, _gripArea;
        private static RectTransform _detailBadge, _columnTime, _columnLevel, _columnSource, _columnMessage;
        private static TextMeshProUGUI _title, _status, _counter, _notice, _empty, _detailSource, _detailMeta, _detailBadgeText;
        private static TextMeshProUGUI _commandCount, _confirmationTitle, _confirmationLine, _confirmationAbout;
        private static TMP_InputField _source, _search, _detail, _command;
        private static Control _close, _smaller, _larger, _pause, _followToggle, _clear, _latest, _copy, _run, _cancel, _confirm;
        private static Control _messageTab, _commandsTab;
        private static readonly Control[] Chips = new Control[5];
        private static GameObject _confirmation;
        private static Button _cancelButton;
        private static float _scale = 1f, _heightFraction = 0.58f, _panelHeight, _logWidth, _viewportHeight;
        private static int _screenWidth, _screenHeight;

        private static Strings Text => LocalizationStartup.Text;

        private static string Upper(string text) => text.ToUpper(Languages.CultureOf(Languages.Current));

        private static void EnsureView()
        {
            if (_viewReady && _root != null) return;
            _bold = MenuKit.Bold;
            _regular = MenuKit.Regular;
            _fallbackFont = _bold == null;
            if (_fallbackFont) _bold = _regular = FallbackFont() ?? throw new InvalidOperationException("The game's UI font is not ready.");
            Controls.Clear();
            Fields.Clear();
            Rows.Clear();
            CommandViews.Clear();
            SuggestionViews.Clear();

            _root = new GameObject("Modstoke Console");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 4800;
            _scaler = _root.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(1600f / _scale, 1000f / _scale);
            _scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();
            _rootGroup = _root.AddComponent<CanvasGroup>();
            _scrim = Image("Scrim", _root.transform, Clear);
            Fill(_scrim.rectTransform);

            _panel = Box("Panel", _root.transform);
            _panel.anchorMin = new Vector2(0f, 1f);
            _panel.anchorMax = Vector2.one;
            _panel.pivot = new Vector2(0.5f, 1f);
            _panel.gameObject.AddComponent<Image>().color = Veil;
            _panelInteraction = _panel.gameObject.AddComponent<CanvasGroup>();
            var shadow = Image("Shadow", _panel, new Color(0f, 0f, 0f, 0.55f));
            shadow.sprite = MenuKit.Fade;
            shadow.raycastTarget = false;
            Region(shadow.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(0f, -ShadowHeight), Vector2.zero);
            Rule(_panel, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f));

            BuildHeader();
            BuildLog();
            BuildDetails();
            BuildFooter();
            BuildSuggestions();
            BuildConfirmation();
            ApplyText();
            _catalogSignature = null;
            _screenWidth = 0;
            _viewReady = true;
        }

        private static void DestroyView()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _panel = null;
            _confirmation = null;
            _suggestions = null;
            _command = null;
            _viewReady = _closing = false;
            Controls.Clear();
            Fields.Clear();
            Rows.Clear();
            CommandViews.Clear();
            SuggestionViews.Clear();
        }

        // The TMP default font, or any font the game's own text uses, when its Roboto Condensed is not loaded yet.
        private static TMP_FontAsset FallbackFont()
        {
            var font = TMP_Settings.defaultFontAsset;
            if (font != null) return font;
            foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                if (text != null && text.font != null) return text.font;
            return null;
        }

        private static void BuildHeader()
        {
            var header = Box("Header", _panel);
            Region(header, new Vector2(0f, 1f), Vector2.one, new Vector2(Pad, -HeaderHeight), new Vector2(-Pad, 0f));
            _title = Label(header, "Title", 26f, true, MenuKit.Ink);
            _title.characterSpacing = 8f;
            _statusDot = Image("Status light", header, MenuKit.Amber);
            _statusDot.raycastTarget = false;
            _status = Label(header, "Status", 14f, false, MenuKit.Muted);

            _close = Clickable(header, "Close", Hide, PaintPlain);
            _close.Label = Label(_close.Rect, "Label", 12f, true, MenuKit.Soft);
            _close.Label.characterSpacing = 14f;
            var tag = Image("Key", _close.Rect, MenuKit.Soft);
            tag.raycastTarget = false;
            Region(tag.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, -10f), new Vector2(46f, 10f));
            var escape = Label(tag.rectTransform, "Name", 10f, true, MenuKit.Dark, TextAlignmentOptions.Center);
            escape.text = "ESC";
            Fill(escape.rectTransform);
            Region(_close.Label.rectTransform, Vector2.zero, Vector2.one, new Vector2(54f, 0f), new Vector2(-8f, 0f));
            _larger = Clickable(header, "Larger", () => ChangeScale(0.1f), PaintButton);
            _larger.Label = Centered(_larger.Rect, "A+", 13f);
            _smaller = Clickable(header, "Smaller", () => ChangeScale(-0.1f), PaintButton);
            _smaller.Label = Centered(_smaller.Rect, "A-", 13f);

            for (var i = 0; i < Chips.Length; i++)
            {
                var level = i;
                var chip = Chips[i] = Clickable(header, "Level " + i, () => SetLevel(level), PaintChip);
                chip.Lit = () => _level == level;
                chip.Label = Label(chip.Rect, "Label", 13f, true, MenuKit.Muted);
                chip.Label.characterSpacing = 12f;
                chip.Count = Label(chip.Rect, "Count", 13f, false, MenuKit.Faint);
                chip.Under = Image("Under", chip.Rect, MenuKit.Ink);
                chip.Under.raycastTarget = false;
                Region(chip.Under.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(8f, 0f), new Vector2(-8f, 2f));
            }
            _source = Field(header, "Source", 14f);
            _search = Field(header, "Search", 14f);
            _source.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => ApplyFilters()));
            _search.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => ApplyFilters()));
            _pause = Clickable(header, "Pause", TogglePause, PaintButton);
            _pause.Lit = () => _paused;
            _pause.Label = Centered(_pause.Rect, "", 12f);
            _followToggle = Clickable(header, "Follow", ToggleFollow, PaintButton);
            _followToggle.Lit = () => _follow;
            _followToggle.Label = Centered(_followToggle.Rect, "", 12f);
            _clear = Clickable(header, "Clear", ClearView, PaintButton);
            _clear.Label = Centered(_clear.Rect, "", 12f);
            Rule(header, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f));
        }

        private static void BuildLog()
        {
            _logColumn = Box("Log", _panel);
            var head = Box("Columns", _logColumn);
            TopStretch(head, 0f, 0f, 0f, ColumnsHeight);
            _columnTime = Label(head, "Time", 11f, true, MenuKit.Faint).rectTransform;
            _columnLevel = Label(head, "Level", 11f, true, MenuKit.Faint).rectTransform;
            _columnSource = Label(head, "Source", 11f, true, MenuKit.Faint).rectTransform;
            _columnMessage = Label(head, "Message", 11f, true, MenuKit.Faint).rectTransform;
            foreach (var column in new[] { _columnTime, _columnLevel, _columnSource, _columnMessage })
                column.GetComponent<TextMeshProUGUI>().characterSpacing = 16f;
            var rule = Rule(_logColumn, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -ColumnsHeight - 1f), new Vector2(0f, -ColumnsHeight));
            rule.color = MenuKit.Line;

            _logViewport = Box("Viewport", _logColumn);
            Region(_logViewport, Vector2.zero, Vector2.one, new Vector2(0f, CounterHeight), new Vector2(0f, -(ColumnsHeight + 2f)));
            _logViewport.gameObject.AddComponent<RectMask2D>();
            _empty = Label(_logViewport, "Empty", 15f, false, MenuKit.Faint, TextAlignmentOptions.Center);
            Fill(_empty.rectTransform);
            _counter = Label(_logColumn, "Counter", 12f, false, MenuKit.Muted);
            Region(_counter.rectTransform, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, CounterHeight));

            _latest = Clickable(_logColumn, "Latest", JumpToLatest, PaintPrimary);
            _latest.Rect.anchorMin = _latest.Rect.anchorMax = _latest.Rect.pivot = new Vector2(1f, 0f);
            _latest.Rect.anchoredPosition = new Vector2(-14f, CounterHeight + 10f);
            _latest.Label = Label(_latest.Rect, "Label", 12f, true, MenuKit.Dark);
            _latest.Label.characterSpacing = 12f;
            Region(_latest.Label.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-30f, 0f));
            var arrow = MenuKit.Chevron(_latest.Rect, "Arrow", 10f, MenuKit.Dark);
            arrow.anchorMin = arrow.anchorMax = new Vector2(1f, 0.5f);
            arrow.anchoredPosition = new Vector2(-17f, -1f);
            arrow.localScale = new Vector3(1f, -1f, 1f);
            _latest.Rect.gameObject.SetActive(false);
        }

        private static void BuildDetails()
        {
            _right = Box("Details", _panel);
            _messageTab = Tab("Message tab", 0);
            _commandsTab = Tab("Commands tab", 1);
            var rule = Rule(_right, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -29f), new Vector2(0f, -28f));
            rule.color = MenuKit.Line;

            _messagePane = Box("Message", _right);
            Region(_messagePane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -42f));
            _detailBadge = Image("Badge", _messagePane, Clear).rectTransform;
            _detailBadge.GetComponent<Image>().raycastTarget = false;
            Top(_detailBadge, 0f, 3f, BadgeWidth, 20f);
            _detailBadgeText = Label(_detailBadge, "Level", 10f, true, MenuKit.Soft, TextAlignmentOptions.Center);
            _detailBadgeText.characterSpacing = 8f;
            Fill(_detailBadgeText.rectTransform);
            _detailSource = Label(_messagePane, "Source", 17f, true, MenuKit.Ink);
            TopStretch(_detailSource.rectTransform, BadgeWidth + 12f, 0f, 96f, 26f);
            _copy = Clickable(_messagePane, "Copy", CopyEntry, PaintButton);
            RightTop(_copy.Rect, 0f, 1f, 84f, 26f);
            _copy.Label = Centered(_copy.Rect, "", 12f);
            _detailMeta = Label(_messagePane, "Meta", 13f, false, MenuKit.Muted);
            TopStretch(_detailMeta.rectTransform, 0f, 30f, 0f, 18f);
            _detail = Field(_messagePane, "Text", 14f, true);
            _detail.readOnly = true;
            _detail.characterLimit = 0;
            Region(_detail.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -58f));

            _commandsPane = Box("Commands", _right);
            Region(_commandsPane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -42f));
            _commandCount = Label(_commandsPane, "Count", 13f, false, MenuKit.Muted);
            TopStretch(_commandCount.rectTransform, 0f, 0f, 0f, 20f);
            var viewport = Image("Viewport", _commandsPane, Clear).rectTransform;
            Region(viewport, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-12f, -28f));
            viewport.gameObject.AddComponent<RectMask2D>();
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = RowHeight * 2f;
            scroll.verticalScrollbar = Scrollbar(_commandsPane, 28f);
            _catalogContent = Box("Content", viewport);
            _catalogContent.anchorMin = new Vector2(0f, 1f);
            _catalogContent.anchorMax = Vector2.one;
            _catalogContent.pivot = new Vector2(0.5f, 1f);
            _catalogContent.sizeDelta = Vector2.zero;
            scroll.content = _catalogContent;
            ShowTab(0);
        }

        private static Control Tab(string name, int index)
        {
            var tab = Clickable(_right, name, () => ShowTab(index), PaintChip);
            tab.Lit = () => _tab == index;
            tab.Label = Label(tab.Rect, "Label", 12f, true, MenuKit.Muted, TextAlignmentOptions.Center);
            tab.Label.characterSpacing = 16f;
            Fill(tab.Label.rectTransform);
            tab.Under = Image("Under", tab.Rect, MenuKit.Ink);
            tab.Under.raycastTarget = false;
            Region(tab.Under.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(6f, 0f), new Vector2(-6f, 2f));
            return tab;
        }

        private static void BuildFooter()
        {
            var footer = Box("Footer", _panel);
            Region(footer, Vector2.zero, new Vector2(1f, 0f), new Vector2(Pad, 0f), new Vector2(-Pad, FooterHeight));
            Rule(footer, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -1f), Vector2.zero);
            _notice = Label(footer, "Notice", 13f, false, MenuKit.Muted);
            Region(_notice.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(0f, 54f), new Vector2(0f, 74f));
            var prompt = MenuKit.Chevron(footer, "Prompt", 14f, MenuKit.Ink);
            prompt.anchorMin = prompt.anchorMax = Vector2.zero;
            prompt.anchoredPosition = new Vector2(10f, 31f);
            prompt.localEulerAngles = new Vector3(0f, 0f, -90f);
            _command = Field(footer, "Command", 16f);
            // Room for a full name and the hub's argument limit, so an overlong line is refused rather than cut.
            _command.characterLimit = 256 + 1 + Modstoke.Diagnostics.ConsoleHub.ArgumentLimit;
            Region(_command.GetComponent<RectTransform>(), Vector2.zero, new Vector2(1f, 0f), new Vector2(30f, 12f),
                new Vector2(-(RunWidth + 12f), 50f));
            _command.onSubmit.AddListener((UnityAction<string>)new Action<string>(_ => PrepareCommand()));
            _command.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => CommandTyped()));
            _run = Clickable(footer, "Run", PrepareCommand, PaintPrimary);
            Region(_run.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-RunWidth, 12f), new Vector2(0f, 50f));
            var tag = Image("Key", _run.Rect, new Color(0f, 0f, 0f, 0.16f));
            tag.raycastTarget = false;
            Region(tag.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, -10f), new Vector2(60f, 10f));
            var enter = Label(tag.rectTransform, "Name", 10f, true, MenuKit.Dark, TextAlignmentOptions.Center);
            enter.text = "ENTER";
            Fill(enter.rectTransform);
            _run.Label = Label(_run.Rect, "Label", 14f, true, MenuKit.Dark, TextAlignmentOptions.Center);
            _run.Label.characterSpacing = 14f;
            Region(_run.Label.rectTransform, Vector2.zero, Vector2.one, new Vector2(60f, 0f), new Vector2(-8f, 0f));

            // The bottom edge drags to make the panel taller or shorter.
            _gripArea = Box("Resize", _panel);
            Region(_gripArea, Vector2.zero, new Vector2(1f, 0f), new Vector2(0f, -8f), new Vector2(0f, 10f));
            _grip = Image("Grip", _gripArea, MenuKit.Faint);
            _grip.raycastTarget = false;
            Region(_grip.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-36f, 0f), new Vector2(36f, 3f));
        }

        private static void BuildConfirmation()
        {
            _confirmation = Image("Confirm command", _panel, MenuKit.Hex(0x050606, 0.86f)).gameObject;
            _confirmation.AddComponent<CanvasGroup>().ignoreParentGroups = true;
            Fill(_confirmation.GetComponent<RectTransform>());
            var box = Image("Dialog", _confirmation.transform, Raised).rectTransform;
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
            box.sizeDelta = new Vector2(640f, 280f);
            Rule(box, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -1f), Vector2.zero);
            Rule(box, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f));
            _confirmationTitle = Label(box, "Title", 13f, true, MenuKit.Muted);
            _confirmationTitle.characterSpacing = 20f;
            TopStretch(_confirmationTitle.rectTransform, 32f, 28f, 32f, 20f);
            _confirmationLine = Label(box, "Command", 21f, true, MenuKit.Ink, TextAlignmentOptions.TopLeft);
            _confirmationLine.enableWordWrapping = true;
            TopStretch(_confirmationLine.rectTransform, 32f, 60f, 32f, 56f);
            _confirmationAbout = Label(box, "About", 15f, false, MenuKit.Soft, TextAlignmentOptions.TopLeft);
            _confirmationAbout.enableWordWrapping = true;
            TopStretch(_confirmationAbout.rectTransform, 32f, 122f, 32f, 72f);
            _confirm = Clickable(box, "Run", ConfirmCommand, PaintPrimary, true);
            Region(_confirm.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-182f, 28f), new Vector2(-32f, 70f));
            _confirm.Label = Centered(_confirm.Rect, "", 14f);
            _confirm.Label.color = MenuKit.Dark;
            _cancel = Clickable(box, "Cancel", CancelConfirmation, PaintButton, true);
            Region(_cancel.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-344f, 28f), new Vector2(-194f, 70f));
            _cancel.Label = Centered(_cancel.Rect, "", 14f);
            _cancelButton = _cancel.Button;
            // Left and right move between the two; Cancel holds focus first, so Enter alone never runs anything.
            Pair(_cancel.Button, _confirm.Button);
            _confirmation.SetActive(false);
        }

        // Every visible word, again whenever the language changes.
        private static void ApplyText()
        {
            if (_root == null) return;
            _title.text = Upper(Text["console.title"]);
            _close.Label.text = Upper(Text["console.close"]);
            string[] levels = { "all", "errors", "warnings", "info", "debug" };
            for (var i = 0; i < Chips.Length; i++) Chips[i].Label.text = Upper(Text["console.level." + levels[i]]);
            Hint(_source, Text["console.sourceHint"]);
            Hint(_search, Text["console.searchHint"]);
            Hint(_detail, Text["console.messageHint"]);
            Hint(_command, Text["console.inputHint"]);
            _clear.Label.text = Upper(Text["console.clear"]);
            _followToggle.Label.text = Upper(Text["console.follow"]);
            _columnTime.GetComponent<TextMeshProUGUI>().text = Upper(Text["console.column.time"]);
            _columnLevel.GetComponent<TextMeshProUGUI>().text = Upper(Text["console.column.level"]);
            _columnSource.GetComponent<TextMeshProUGUI>().text = Upper(Text["console.column.source"]);
            _columnMessage.GetComponent<TextMeshProUGUI>().text = Upper(Text["console.column.message"]);
            _latest.Label.text = Upper(Text["console.latest"]);
            _latest.Rect.sizeDelta = new Vector2(Preferred(_latest.Label) + 44f, 28f);
            _messageTab.Label.text = Upper(Text["console.tab.message"]);
            _commandsTab.Label.text = Upper(Text["console.tab.commands"]);
            _copy.Label.text = Upper(Text["console.copy"]);
            _run.Label.text = Upper(Text["console.run"]);
            _confirmationTitle.text = Upper(Text["console.confirmTitle"]);
            _confirm.Label.text = Upper(Text["console.run"]);
            _cancel.Label.text = Upper(Text["console.cancel"]);
            if (string.IsNullOrEmpty(_notice.text)) _notice.text = Text["console.intro"];
            PaintToggles();
            ShowEntry();
            _catalogSignature = null;
            _layoutDirty = true;
        }

        private static void Hint(TMP_InputField field, string text)
        {
            if (field.placeholder != null) field.placeholder.GetComponent<TextMeshProUGUI>().text = text;
        }

        private static void PaintToggles()
        {
            _pause.Label.text = Upper(Text[_paused ? "console.resume" : "console.pause"]);
            foreach (var control in Controls) control.Paint(control);
        }

        private static void ChangeScale(float delta)
        {
            var scale = Mathf.Clamp(_scale + delta, 0.8f, 1.3f);
            if (Mathf.Approximately(scale, _scale)) return;
            _scale = scale;
            _scaler.referenceResolution = new Vector2(1600f / _scale, 1000f / _scale);
            _layoutDirty = true;
        }

        // The canvas size the scaler arrives at, worked out here so the layout never waits a frame for it.
        private static Vector2 CanvasSize()
        {
            var screen = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            var reference = new Vector2(1600f, 1000f) / _scale;
            var log = Mathf.Lerp(Mathf.Log(screen.x / reference.x, 2f), Mathf.Log(screen.y / reference.y, 2f), 0.5f);
            return screen / Mathf.Pow(2f, log);
        }

        private static void UpdateLayout()
        {
            if (!_layoutDirty && _screenWidth == Screen.width && _screenHeight == Screen.height) return;
            _layoutDirty = false;
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            var canvas = CanvasSize();
            _panelHeight = Mathf.Round(canvas.y * Mathf.Clamp(_heightFraction, 0.32f, 0.92f));
            _panel.sizeDelta = new Vector2(0f, _panelHeight);
            var width = canvas.x - 2f * Pad;
            var right = Mathf.Clamp(width * 0.32f, 340f, 560f);
            _logWidth = width - right - Gutter;
            _viewportHeight = _panelHeight - HeaderHeight - FooterHeight - BodyTop - ColumnsHeight - 2f - CounterHeight;
            Region(_logColumn, Vector2.zero, Vector2.one, new Vector2(Pad, FooterHeight), new Vector2(-(Pad + right + Gutter), -(HeaderHeight + BodyTop)));
            Region(_right, new Vector2(1f, 0f), Vector2.one, new Vector2(-(Pad + right), FooterHeight), new Vector2(-Pad, -(HeaderHeight + BodyTop)));
            LayoutTabs();
            LayoutHeader(width);
            LayoutColumns();
            EnsureRows(Mathf.CeilToInt(Mathf.Max(0f, _viewportHeight) / RowHeight) + 1);
            _suggestionWidth = Mathf.Clamp(width * 0.5f, 420f, 760f);
            _rowsDirty = true;
        }

        private static void LayoutTabs()
        {
            var x = 0f;
            foreach (var tab in new[] { _messageTab, _commandsTab })
            {
                var tabWidth = Preferred(tab.Label) + 28f;
                Top(tab.Rect, x, 0f, tabWidth, 28f);
                x += tabWidth + 6f;
            }
        }

        // The title row, then the filter row: level chips on the left, the toggles on the right, the fields between.
        private static void LayoutHeader(float width)
        {
            var titleWidth = Preferred(_title) + 4f;
            Top(_title.rectTransform, 0f, 14f, titleWidth, 34f);
            Top(_statusDot.rectTransform, titleWidth + 16f, 27f, 8f, 8f);
            var x = width;
            var closeWidth = 54f + Preferred(_close.Label) + 10f;
            Top(_close.Rect, x -= closeWidth, 15f, closeWidth, 32f);
            Top(_larger.Rect, x -= 40f, 16f, 34f, 30f);
            Top(_smaller.Rect, x -= 40f, 16f, 34f, 30f);
            Top(_status.rectTransform, titleWidth + 34f, 14f, Mathf.Max(0f, x - 24f - (titleWidth + 34f)), 34f);

            x = 0f;
            foreach (var chip in Chips)
            {
                var label = Preferred(chip.Label);
                var count = Preferred(chip.Count);
                Region(chip.Label.rectTransform, Vector2.zero, new Vector2(0f, 1f), new Vector2(8f, 0f), new Vector2(8f + label, 0f));
                Region(chip.Count.rectTransform, Vector2.zero, new Vector2(0f, 1f), new Vector2(14f + label, 0f), new Vector2(14f + label + count, 0f));
                var chipWidth = label + count + 22f;
                Top(chip.Rect, x, 60f, chipWidth, 30f);
                x += chipWidth + 4f;
            }
            var end = width;
            foreach (var button in new[] { _clear, _followToggle, _pause })
            {
                var buttonWidth = Mathf.Max(64f, Preferred(button.Label) + 28f);
                Top(button.Rect, end -= buttonWidth, 60f, buttonWidth, 30f);
                end -= 8f;
            }
            var start = x + 20f;
            var room = Mathf.Max(120f, end - 12f - start);
            var sourceWidth = Mathf.Clamp(room * 0.36f, 110f, 220f);
            Top(_source.GetComponent<RectTransform>(), start, 60f, sourceWidth, 30f);
            Top(_search.GetComponent<RectTransform>(), start + sourceWidth + 10f, 60f, Mathf.Max(80f, room - sourceWidth - 10f), 30f);
        }

        // Time, level and source keep their widths; the message takes the rest.
        private static void LayoutColumns()
        {
            var source = Mathf.Clamp(_logWidth * 0.15f, 110f, 210f);
            var sourceLeft = 12f + TimeWidth + BadgeWidth + 16f;
            var messageLeft = sourceLeft + source + 14f;
            Region(_columnTime, Vector2.zero, new Vector2(0f, 1f), new Vector2(12f, 0f), new Vector2(12f + TimeWidth, 0f));
            Region(_columnLevel, Vector2.zero, new Vector2(0f, 1f), new Vector2(12f + TimeWidth, 0f), new Vector2(sourceLeft, 0f));
            Region(_columnSource, Vector2.zero, new Vector2(0f, 1f), new Vector2(sourceLeft, 0f), new Vector2(sourceLeft + source, 0f));
            Region(_columnMessage, Vector2.zero, Vector2.one, new Vector2(messageLeft, 0f), new Vector2(-8f, 0f));
            foreach (var row in Rows) PlaceColumns(row, sourceLeft, source, messageLeft);
            _sourceLeft = sourceLeft;
            _sourceWidth = source;
            _messageLeft = messageLeft;
        }

        private static float _sourceLeft, _sourceWidth, _messageLeft, _suggestionWidth;

        private static void PlaceColumns(LogRow row, float sourceLeft, float source, float messageLeft)
        {
            Region(row.Time.rectTransform, Vector2.zero, new Vector2(0f, 1f), new Vector2(12f, 0f), new Vector2(12f + TimeWidth, 0f));
            Region(row.BadgeBack.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f + TimeWidth, -9f),
                new Vector2(12f + TimeWidth + BadgeWidth, 9f));
            Region(row.Source.rectTransform, Vector2.zero, new Vector2(0f, 1f), new Vector2(sourceLeft, 0f), new Vector2(sourceLeft + source, 0f));
            Region(row.Message.rectTransform, Vector2.zero, Vector2.one, new Vector2(messageLeft, 0f), new Vector2(-10f, 0f));
        }

        // Hover follows the pointer; the wheel scrolls the log, or with Ctrl changes the text size; the bottom edge drags.
        private static void HandlePointer()
        {
            var pointer = (Vector2)Input.mousePosition;
            var modal = _pendingCommand != null;
            foreach (var control in Controls)
            {
                var over = control.Modal == modal && control.Rect.gameObject.activeInHierarchy
                    && RectTransformUtility.RectangleContainsScreenPoint(control.Rect, pointer, null);
                if (over == control.Hover) continue;
                control.Hover = over;
                control.Paint(control);
            }
            foreach (var (field, rule) in Fields)
                if (rule != null) rule.color = field.isFocused ? MenuKit.Ink : MenuKit.Rule;
            if (modal)
            {
                _cancel.Paint(_cancel);
                _confirm.Paint(_confirm);
                return;
            }

            var hover = -1;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_logViewport, pointer, null, out var local)
                && _logViewport.rect.Contains(local))
            {
                hover = Mathf.FloorToInt((_logViewport.rect.yMax - local.y + _scroll) / RowHeight);
                if (hover >= Filtered.Count) hover = -1;
                var wheel = Input.mouseScrollDelta.y;
                if (wheel != 0f && !Ctrl) ScrollBy(-wheel * RowHeight * 3f);
            }
            if (hover != _hoverIndex)
            {
                _hoverIndex = hover;
                _paintDirty = true;
            }
            if (Ctrl && Input.mouseScrollDelta.y != 0f) ChangeScale(Mathf.Sign(Input.mouseScrollDelta.y) * 0.1f);

            var overGrip = RectTransformUtility.RectangleContainsScreenPoint(_gripArea, pointer, null);
            if (overGrip && Input.GetMouseButtonDown(0)) _dragging = true;
            if (!Input.GetMouseButton(0)) _dragging = false;
            if (_dragging)
            {
                var height = 1f - pointer.y / Mathf.Max(1f, Screen.height);
                if (!Mathf.Approximately(height, _heightFraction))
                {
                    _heightFraction = Mathf.Clamp(height, 0.32f, 0.92f);
                    _layoutDirty = true;
                }
            }
            _grip.color = _dragging || overGrip ? MenuKit.Ink : MenuKit.Faint;
        }

        private static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        private static void PaintButton(Control control)
        {
            var lit = control.On || control.Hover || IsSelected(control);
            control.Back.color = control.On ? MenuKit.Faded(0.16f) : control.Hover || IsSelected(control) ? MenuKit.Faded(0.1f) : MenuKit.Faded(0.045f);
            if (control.Label != null) control.Label.color = lit ? MenuKit.Ink : MenuKit.Soft;
        }

        private static void PaintPrimary(Control control)
        {
            control.Back.color = control.Hover || IsSelected(control) ? MenuKit.Ink : MenuKit.Soft;
        }

        private static void PaintPlain(Control control)
        {
            control.Back.color = control.Hover ? MenuKit.Faded(0.06f) : Clear;
            if (control.Label != null) control.Label.color = control.Hover ? MenuKit.Ink : MenuKit.Soft;
        }

        private static void PaintChip(Control control)
        {
            control.Back.color = control.Hover && !control.On ? MenuKit.Faded(0.05f) : Clear;
            control.Label.color = control.On || control.Hover ? MenuKit.Ink : MenuKit.Muted;
            if (control.Under != null)
            {
                control.Under.enabled = control.On || control.Hover;
                control.Under.color = control.On ? MenuKit.Ink : MenuKit.Faded(0.3f);
            }
        }

        private static bool IsSelected(Control control)
            => control.Button != null && _eventSystem != null && _eventSystem.currentSelectedGameObject == control.Button.gameObject;

        private static RectTransform Box(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.layer = 5;
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static Image Image(string name, Transform parent, Color color)
        {
            var image = Box(name, parent).gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static Image Rule(Transform parent, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            var rule = Image("Rule", parent, MenuKit.Rule);
            rule.raycastTarget = false;
            Region(rule.rectTransform, min, max, offsetMin, offsetMax);
            return rule;
        }

        private static TextMeshProUGUI Label(Transform parent, string name, float size, bool bold, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var text = Box(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = bold ? _bold : _regular;
            text.richText = false;
            text.fontSize = size;
            text.color = color;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = align;
            text.raycastTarget = false;
            return text;
        }

        private static TextMeshProUGUI Centered(RectTransform parent, string text, float size)
        {
            var label = Label(parent, "Label", size, true, MenuKit.Soft, TextAlignmentOptions.Center);
            label.characterSpacing = 12f;
            label.text = text;
            Fill(label.rectTransform);
            return label;
        }

        private static float Preferred(TextMeshProUGUI text) => text.GetPreferredValues(text.text).x;

        private static Control Clickable(Transform parent, string name, Action action, Action<Control> paint, bool modal = false)
        {
            var rect = Box(name, parent);
            var back = rect.gameObject.AddComponent<Image>();
            back.color = Clear;
            var button = rect.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = back;
            NoNavigation(button);
            button.onClick.AddListener((UnityAction)new Action(() =>
            {
                try { action(); }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("A Modstoke console action failed: " + error);
                    Hide();
                }
            }));
            var control = new Control { Rect = rect, Back = back, Button = button, Paint = paint, Modal = modal };
            Controls.Add(control);
            return control;
        }

        // Arrow keys belong to the command line and the log, so the panel's buttons never take them.
        private static void NoNavigation(Selectable selectable)
        {
            var navigation = selectable.navigation;
            navigation.mode = Navigation.Mode.None;
            selectable.navigation = navigation;
        }

        private static void Pair(Selectable left, Selectable right)
        {
            var navigation = left.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnRight = right;
            left.navigation = navigation;
            navigation = right.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnLeft = left;
            right.navigation = navigation;
        }

        private static TMP_InputField Field(Transform parent, string name, float size, bool multiline = false)
        {
            var image = Image(name, parent, MenuKit.Faded(multiline ? 0.03f : 0.05f));
            var input = image.gameObject.AddComponent<TMP_InputField>();
            NoNavigation(input);
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.characterLimit = multiline ? 0 : 256;
            input.customCaretColor = true;
            input.caretColor = MenuKit.Ink;
            input.caretWidth = 2;
            input.selectionColor = MenuKit.Faded(0.25f);
            input.onFocusSelectAll = false;
            input.restoreOriginalTextOnEscape = false;
            input.richText = false;
            var viewport = Box("Text viewport", image.transform);
            Fill(viewport, 10f, multiline ? 8f : 2f, multiline ? 18f : 10f, multiline ? 8f : 2f);
            viewport.gameObject.AddComponent<RectMask2D>();
            input.textViewport = viewport;
            var text = Label(viewport, "Text", size, false, MenuKit.Ink);
            text.enableWordWrapping = multiline;
            text.overflowMode = multiline ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            text.alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            if (multiline) text.lineSpacing = 4f;
            Fill(text.rectTransform);
            var placeholder = Label(viewport, "Hint", size, false, MenuKit.Faint);
            placeholder.enableWordWrapping = multiline;
            placeholder.alignment = text.alignment;
            Fill(placeholder.rectTransform);
            input.textComponent = text;
            input.placeholder = placeholder;
            Image rule = null;
            if (multiline) input.verticalScrollbar = Scrollbar(image.transform, 0f);
            else rule = Rule(image.transform, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f));
            Fields.Add((input, rule));
            return input;
        }

        private static Scrollbar Scrollbar(Transform parent, float top)
        {
            var track = Image("Scrollbar", parent, MenuKit.Faded(0.04f));
            Region(track.rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-4f, 0f), new Vector2(0f, -top));
            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            NoNavigation(scrollbar);
            scrollbar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            var handle = Image("Handle", track.transform, MenuKit.Faded(0.35f));
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
