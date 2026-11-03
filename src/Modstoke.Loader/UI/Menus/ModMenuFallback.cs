// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Transitional IMGUI fallback for the Modstoke Mods screen, drawn from the shared view model.

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Loader;
using UnityEngine;
using static Modstoke.Loader.MenuText;

namespace Modstoke.Loader
{
    internal static partial class MenuController
    {
        private const float ReferenceHeight = 1080f;
        private const float PanelMarginX = 58f;
        private const float PanelMarginY = 26f;
        private const float MaxPanelWidth = 1880f;
        private const float ContentInset = 34f;
        private const float LabelWidth = 300f;
        private const float ControlWidth = 340f;
        private const float StatusWidth = 110f;
        private const float NavWidth = 560f;
        private const int DefaultMaxLength = 2048;
        private const float ProviderRefreshInterval = 0.5f;
        // Offsets are kept apart from the native panes, whose page offsets do not include the nav list.
        private const string ScrollPrefix = "imgui:";
        private static readonly List<MenuNavItem> _navRows = new List<MenuNavItem>();
        private static readonly List<string> _chipLabels = new List<string>();
        // Unapplied text per row key on the open page, so the cached view does not undo typing.
        private static readonly Dictionary<string, string> _drafts = new Dictionary<string, string>(StringComparer.Ordinal);
        private static MenuPage _page;
        private static string _pageKey;
        private static string _scrollKey;
        private static string _breadcrumb;
        private static string _statusLine;
        private static string _publishedView;
        private static string _result;
        private static bool _viewDirty = true;
        private static bool _jumpPending;
        private static bool _jumpToPage;
        private static bool _configDirty;
        private static float _nextProviderRefresh;
        private static float _lastWheelTime;
        private static Rect _windowRect;
        private static GUIStyle _titleStyle;
        private static GUIStyle _pageTitleStyle;
        private static GUIStyle _breadcrumbStyle;
        private static GUIStyle _statusLineStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _sectionButtonStyle;
        private static GUIStyle _metaStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _valueStyle;
        private static GUIStyle _mutedStyle;
        private static GUIStyle _wrapStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _rowStyle;
        private static GUIStyle _navStyle;
        private static GUIStyle _navActiveStyle;
        private static GUIStyle _navGroupStyle;
        private static GUIStyle _framelessButtonStyle;
        private static GUIStyle _footerButtonStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _buttonActiveStyle;
        private static GUIStyle _inputStyle;
        private static GUIStyle _statusReadyStyle;
        private static GUIStyle _statusHostedStyle;
        private static GUIStyle _statusWarningStyle;
        private static GUIStyle _statusMissingStyle;
        private static Texture2D _windowTex;
        private static Texture2D _panelTex;
        private static Texture2D _rowTex;
        private static Texture2D _lineTex;
        private static Texture2D _buttonTex;
        private static Texture2D _buttonHoverTex;
        private static Texture2D _buttonSelectedTex;

        internal static void OnGUI()
        {
            if (!MenuDispatcher.ShouldDrawFallback(_visible))
            {
                return;
            }

            var current = Event.current;
            if (current.type == EventType.ScrollWheel)
            {
                _lastWheelTime = Time.unscaledTime;
            }

            // Layout comes first in every pass, so the rebuilt view is the one both layout and drawing see.
            if (current.type == EventType.Layout && (_viewDirty || (_page == null) != MenuState.IsConfigOpen))
            {
                _viewDirty = false;
                BuildView();
            }

            EnsureStyles();
            var scale = Screen.height > 0 ? Screen.height / ReferenceHeight : 1f;
            var matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                _windowRect = new Rect(0f, 0f, Screen.width / scale, ReferenceHeight);
                DrawFullScreen();
            }
            finally
            {
                GUI.matrix = matrix;
            }
        }

        private static void ShowFallback()
        {
            // The last page and filter carry over from the previous open, as in the native menu.
            MenuState.CloseConfig();
            MenuState.Open(MenuState.ViewState.LastPage);
            PresentFallback();
        }

        private static void ResumeFallback(string pageKey = null)
        {
            // Live controls open from a mod page, so closing them lands back on it.
            var entry = MenuState.ConfigEntry;
            MenuState.CloseConfig();
            MenuState.Open(pageKey ?? (entry != null ? MenuKeys.Mod(entry) : MenuState.ViewState.LastPage));
            PresentFallback();
        }

        private static void PresentFallback()
        {
            _visible = true;
            _result = null;
            _publishedView = null;
            _viewDirty = true;
            UiState.MarkFallback(UiSurfaceIds.ModsMenu, "Mods Menu", "list", "Fallback IMGUI Modstoke mods menu is visible.");
            UiState.SetTooltip(DefaultTooltip);
        }

        // The native menu now owns the shared page state, so only the fallback's own view is dropped.
        private static void HideFallbackForNative()
        {
            _visible = false;
            ClearFallbackView();
        }

        private static void HideFallback()
        {
            _visible = false;
            MenuState.CloseProvider();
            ClearFallbackView();
            UiState.SetTooltip(DefaultTooltip);
            UiState.MarkHidden(UiSurfaceIds.ModConfig, "Mod Config", "imgui-fallback", "closed", "No mod config is open.");
            UiState.MarkHidden(UiSurfaceIds.ModsMenu, "Mods Menu", "imgui-fallback", "closed", "Modstoke mods menu is not visible.");
        }

        private static void ClearFallbackView()
        {
            _drafts.Clear();
            _page = null;
            _pageKey = null;
            _publishedView = null;
            _jumpPending = false;
            _jumpToPage = false;
            _viewDirty = true;
        }

        private static void UpdateFallback()
        {
            if (!MenuDispatcher.ShouldDrawFallback(_visible))
            {
                return;
            }

            var now = Time.unscaledTime;
            if (MenuState.IsProviderOpen && !MenuState.IsConfigOpen
                && (now >= _nextProviderRefresh || MenuState.ProviderContentNotified))
            {
                _nextProviderRefresh = now + ProviderRefreshInterval;
                // A rebuild under a drag or the wheel would move the rows the user is working with.
                if (!IsPointerBusy(now) && MenuState.RefreshProviderSnapshotsIfChanged())
                {
                    if (!MenuState.IsProviderOpen)
                    {
                        MenuState.Open(MenuKeys.Home);
                    }

                    _viewDirty = true;
                }
            }

            _configDirty = MenuState.IsConfigDirty;
        }

        private static bool IsPointerBusy(float now)
        {
            return now - _lastWheelTime < 0.3f || Input.GetMouseButton(0);
        }

        private static void BuildView()
        {
            var config = MenuState.ConfigEntry;
            var chip = MenuViewBuilder.ChipViewName(MenuViewBuilder.ActiveChip());
            using (Timings.Measure("ui.mods-menu", config != null ? "rebuild-config" : "rebuild-list", config != null ? config.DisplayName ?? "none" : chip))
            {
                _navRows.Clear();
                AddNavRows(MenuViewBuilder.BuildNav());
                var entries = MenuState.Inventory?.Entries;
                _chipLabels.Clear();
                foreach (var id in MenuViewBuilder.FilterChips)
                {
                    _chipLabels.Add(MenuViewBuilder.ChipViewName(id) + "  " + MenuState.FilterEntries(entries, id).Length.ToString(CultureInfo.InvariantCulture));
                }

                _page = config != null ? null : MenuViewBuilder.BuildPage();
                var pageKey = config != null ? "config:" + MenuKeys.EntryId(config) : _page.Key;
                if (!string.Equals(pageKey, _pageKey, StringComparison.Ordinal))
                {
                    // Unapplied text belongs to the page it was typed on.
                    _drafts.Clear();
                    _pageKey = pageKey;
                }
                else
                {
                    DropSettledDrafts();
                }

                _scrollKey = ScrollPrefix + pageKey;
                _jumpToPage = _jumpPending;
                _jumpPending = false;
                _breadcrumb = MenuViewBuilder.Breadcrumb(_page, config);
                _statusLine = StatusLine(MenuState.Inventory, RuntimeEntryPoint.Version);
                PublishView(config, chip);
            }
        }

        private static void AddNavRows(IReadOnlyList<MenuNavItem> items)
        {
            foreach (var item in items)
            {
                _navRows.Add(item);
                if (!item.Collapsed)
                {
                    AddNavRows(item.Children);
                }
            }
        }

        // A form field the provider now holds as typed no longer needs its draft.
        private static void DropSettledDrafts()
        {
            foreach (var section in _page?.Sections ?? Array.Empty<MenuSection>())
            {
                foreach (var row in section.Rows)
                {
                    if (row.Control.Kind == MenuControlKind.InputLive && _drafts.TryGetValue(row.Key, out var draft)
                        && string.Equals(draft, row.Value ?? "", StringComparison.Ordinal))
                    {
                        _drafts.Remove(row.Key);
                    }
                }
            }
        }

        private static void PublishView(ModInventoryEntry config, string chip)
        {
            var view = config != null ? "config:" + config.DisplayName + "\n" + MenuState.ConfigStatus : "list:" + chip;
            if (string.Equals(view, _publishedView, StringComparison.Ordinal))
            {
                return;
            }

            _publishedView = view;
            if (config != null)
            {
                UiState.MarkFallback(UiSurfaceIds.ModsMenu, "Mods Menu", "config", "Fallback IMGUI Modstoke mods config view is visible.");
                UiState.MarkFallback(UiSurfaceIds.ModConfig, "Mod Config", "config:" + (config.DisplayName ?? "none"), MenuState.ConfigStatus ?? "Config editor opened.");
            }
            else
            {
                UiState.MarkFallback(UiSurfaceIds.ModsMenu, "Mods Menu", "list:" + chip, "Fallback IMGUI Modstoke mods list is visible.");
                UiState.MarkHidden(UiSurfaceIds.ModConfig, "Mod Config", "imgui-fallback", "closed", "No mod config is open.");
            }
        }

        private static void DrawFullScreen()
        {
            DrawSolid(_windowRect, _windowTex);
            var width = Mathf.Min(_windowRect.width - (PanelMarginX * 2f), MaxPanelWidth);
            var panel = new Rect((_windowRect.width - width) * 0.5f, PanelMarginY, width, _windowRect.height - (PanelMarginY * 2f));
            DrawSolid(panel, _panelTex);

            var content = new Rect(panel.x + ContentInset, panel.y + 18f, panel.width - (ContentInset * 2f), panel.height - 36f);
            DrawHeader(content);
            var body = new Rect(content.x, content.y + 92f, content.width, content.height - 92f - 62f);
            if (MenuState.IsConfigOpen)
            {
                DrawConfigEditor(body);
            }
            else
            {
                DrawColumn(body);
            }

            DrawFooter(new Rect(content.x, content.yMax - 46f, content.width, 46f));
        }

        private static void DrawHeader(Rect rect)
        {
            GUI.Label(new Rect(rect.x, rect.y, 130f, 40f), "MODS", _titleStyle);
            GUI.Label(new Rect(rect.x + 130f, rect.y + 4f, rect.width - 130f, 34f), _breadcrumb ?? "", _breadcrumbStyle);
            GUI.Label(new Rect(rect.x, rect.y + 46f, rect.width, 26f), _statusLine ?? "", _statusLineStyle);
            DrawSolid(new Rect(rect.x, rect.y + 80f, rect.width, 1f), _lineTex);
        }

        // Filters and search stay put above one scrolled column: the nav list, then the open page.
        private static void DrawColumn(Rect rect)
        {
            var chips = MenuViewBuilder.FilterChips;
            var active = MenuViewBuilder.ActiveChip();
            var chipWidth = Mathf.Min(210f, rect.width / chips.Length);
            for (var i = 0; i < chips.Length && i < _chipLabels.Count; i++)
            {
                var id = chips[i];
                var on = string.Equals(id, active, StringComparison.OrdinalIgnoreCase);
                if (GUI.Button(new Rect(rect.x + (i * chipWidth), rect.y, chipWidth - 6f, 34f), _chipLabels[i], on ? _buttonActiveStyle : _buttonStyle))
                {
                    MenuState.SetFilter(id);
                    _viewDirty = true;
                    PublishTooltip(MenuState.TabTooltip(MenuState.TabIndex(id)));
                }
            }

            GUI.Label(new Rect(rect.x, rect.y + 44f, 90f, 30f), "SEARCH", _headerStyle);
            var search = MenuState.ViewState.SearchText ?? "";
            var next = GUI.TextField(new Rect(rect.x + 90f, rect.y + 44f, 460f, 30f), search, 128, _inputStyle);
            if (!string.Equals(next, search, StringComparison.Ordinal))
            {
                MenuState.SetSearch(next);
                _viewDirty = true;
            }

            var area = new Rect(rect.x, rect.y + 86f, rect.width, rect.height - 86f);
            var view = MenuState.ViewState;
            var offset = view.GetScrollOffset(_scrollKey);
            GUILayout.BeginArea(area);
            var scroll = GUILayout.BeginScrollView(new Vector2(0f, offset), false, true, GUILayout.Height(area.height));
            DrawNav();
            var pageTop = GUILayoutUtility.GetRect(0f, 0f);
            DrawPage();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            if (scroll.y != offset)
            {
                view.SetScrollOffset(_scrollKey, scroll.y);
            }

            // A page opened from the nav starts at its own header rather than back at the top of the list.
            if (_jumpToPage && Event.current.type == EventType.Repaint)
            {
                _jumpToPage = false;
                view.SetScrollOffset(_scrollKey, pageTop.y);
            }
        }

        private static void DrawNav()
        {
            GUILayout.Label("BROWSE", _sectionStyle, GUILayout.Height(28f));
            var selected = MenuState.ViewState.SelectedNavKey;
            foreach (var item in _navRows)
            {
                var indent = item.Depth * 24f;
                var folds = item.Kind == MenuNavKind.Group || item.Children.Count > 0;
                var style = item.Kind == MenuNavKind.Group ? _navGroupStyle
                    : string.Equals(item.Target, selected, StringComparison.OrdinalIgnoreCase) ? _navActiveStyle
                    : _navStyle;
                GUILayout.BeginHorizontal(GUILayout.Height(28f));
                GUILayout.Space(indent);
                if (GUILayout.Button((folds ? (item.Collapsed ? "+  " : "-  ") : "    ") + Shorten(item.Label, 56), style,
                    GUILayout.Width(NavWidth - indent), GUILayout.Height(28f)))
                {
                    OnNavClick(item);
                }

                GUILayout.Label(item.Detail ?? "", ToneStyle(item.Tone, _mutedStyle), GUILayout.Width(220f), GUILayout.Height(28f));
                GUILayout.EndHorizontal();
            }
        }

        private static void DrawPage()
        {
            var page = _page;
            if (page == null)
            {
                return;
            }

            GUILayout.Space(22f);
            if (page.Kind != MenuPageKind.Home)
            {
                GUILayout.Label((page.Title ?? "").ToUpperInvariant(), _pageTitleStyle, GUILayout.Height(34f));
            }

            if (!string.IsNullOrWhiteSpace(page.Meta))
            {
                GUILayout.Label(page.Meta, _metaStyle);
            }

            if (!string.IsNullOrWhiteSpace(page.Description))
            {
                GUILayout.Label(page.Description, _wrapStyle);
            }

            foreach (var section in page.Sections)
            {
                DrawSection(section);
            }
        }

        private static void DrawSection(MenuSection section)
        {
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                GUILayout.Space(10f);
                var title = section.Title.ToUpperInvariant();
                if (!section.Collapsible)
                {
                    GUILayout.Label(title, _sectionStyle, GUILayout.Height(28f));
                }
                else if (GUILayout.Button((section.Collapsed ? "+  " : "-  ") + title, _sectionButtonStyle, GUILayout.Height(28f)))
                {
                    MenuState.SetCollapsed(section.Key, !section.Collapsed);
                    _viewDirty = true;
                }

                if (section.Collapsed)
                {
                    return;
                }
            }

            foreach (var row in section.Rows)
            {
                DrawRow(row);
            }
        }

        private static void DrawRow(MenuRow row)
        {
            GUILayout.BeginHorizontal(_rowStyle, GUILayout.MinHeight(34f));
            if (row.Stats.Count > 0)
            {
                foreach (var stat in row.Stats)
                {
                    GUILayout.Label(stat.Value + "  " + stat.Label, ToneStyle(stat.Tone, _labelStyle), GUILayout.Height(26f), GUILayout.ExpandWidth(true));
                }

                GUILayout.EndHorizontal();
                return;
            }

            if (row.Control.Kind == MenuControlKind.Open)
            {
                if (GUILayout.Button(Shorten(row.Label, 44) + "  >", _framelessButtonStyle, GUILayout.Width(LabelWidth), GUILayout.Height(26f)))
                {
                    Invoke(row, null);
                }
            }
            else
            {
                GUILayout.Label(row.Label ?? "", _labelStyle, GUILayout.Width(LabelWidth), GUILayout.MinHeight(26f));
            }

            GUILayout.BeginHorizontal(GUILayout.Width(ControlWidth));
            DrawControl(row);
            GUILayout.EndHorizontal();
            GUILayout.Label(row.Status ?? "", ToneStyle(row.Tone, _mutedStyle), GUILayout.Width(StatusWidth), GUILayout.Height(26f));
            GUILayout.Label(row.Detail ?? "", _wrapStyle, GUILayout.MinHeight(26f), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private static void DrawControl(MenuRow row)
        {
            var control = row.Control;
            switch (control.Kind)
            {
                case MenuControlKind.Toggle:
                    var on = LoaderConfig.TryParseBoolean(row.Value, out var parsed) && parsed;
                    if (GUILayout.Button(BoolDisplay(on), on ? _buttonActiveStyle : _buttonStyle, GUILayout.Width(ControlWidth), GUILayout.Height(28f)))
                    {
                        Invoke(row, on ? "false" : "true");
                    }

                    break;
                case MenuControlKind.Choice:
                    if (GUILayout.Button("<", _buttonStyle, GUILayout.Width(32f), GUILayout.Height(28f)))
                    {
                        Invoke(row, AdjacentOption(control.Options, row.Value, -1));
                    }

                    GUILayout.Label(row.Value ?? "", _valueStyle, GUILayout.Width(ControlWidth - 76f), GUILayout.Height(28f));
                    if (GUILayout.Button(">", _buttonStyle, GUILayout.Width(32f), GUILayout.Height(28f)))
                    {
                        Invoke(row, AdjacentOption(control.Options, row.Value, 1));
                    }

                    break;
                case MenuControlKind.Slider:
                case MenuControlKind.TextApply:
                    DrawTextApply(row);
                    break;
                case MenuControlKind.InputLive:
                    DrawInput(row);
                    break;
                case MenuControlKind.Run:
                    if (GUILayout.Button("RUN", _buttonStyle, GUILayout.Width(96f), GUILayout.Height(28f)))
                    {
                        Invoke(row, null);
                    }

                    GUILayout.FlexibleSpace();
                    break;
                case MenuControlKind.Open:
                    GUILayout.FlexibleSpace();
                    break;
                default:
                    GUILayout.Label(row.Value ?? "", _wrapStyle, GUILayout.Width(ControlWidth), GUILayout.MinHeight(26f));
                    break;
            }
        }

        // IMGUI sliders would apply on every drag frame, so bounded numbers take text with - and + like stepped ones.
        private static void DrawTextApply(MenuRow row)
        {
            var control = row.Control;
            var value = row.Value ?? "";
            var draft = _drafts.TryGetValue(row.Key, out var text) ? text : value;
            var stepped = IsStepped(control);
            string apply = null;
            if (stepped && GUILayout.Button("-", _buttonStyle, GUILayout.Width(30f), GUILayout.Height(28f)))
            {
                apply = StepNumber(control, draft, value, -1);
            }

            var edited = GUILayout.TextField(draft, control.MaxLength > 0 ? control.MaxLength : DefaultMaxLength, _inputStyle,
                GUILayout.Height(28f), GUILayout.ExpandWidth(true));
            if (stepped && GUILayout.Button("+", _buttonStyle, GUILayout.Width(30f), GUILayout.Height(28f)))
            {
                apply = StepNumber(control, edited, value, 1);
            }

            if (GUILayout.Button("APPLY", _buttonStyle, GUILayout.Width(76f), GUILayout.Height(28f)))
            {
                apply = edited;
            }

            if (string.Equals(edited, value, StringComparison.Ordinal))
            {
                _drafts.Remove(row.Key);
            }
            else
            {
                _drafts[row.Key] = edited;
            }

            if (apply != null)
            {
                Invoke(row, apply);
            }
        }

        private static void DrawInput(MenuRow row)
        {
            var control = row.Control;
            var value = _drafts.TryGetValue(row.Key, out var draft) ? draft : row.Value ?? "";
            var enabled = GUI.enabled;
            GUI.enabled = enabled && control.Editable;
            var edited = GUILayout.TextField(value, control.MaxLength > 0 ? control.MaxLength : DefaultMaxLength, _inputStyle,
                GUILayout.Height(28f), GUILayout.ExpandWidth(true));
            GUI.enabled = enabled;
            if (enabled && control.Editable && !string.Equals(edited, value, StringComparison.Ordinal))
            {
                _drafts[row.Key] = edited;
                Invoke(row, edited);
            }
        }

        private static void DrawConfigEditor(Rect rect)
        {
            var entry = MenuState.ConfigEntry;
            GUILayout.BeginArea(rect);
            GUILayout.Label((entry.DisplayName ?? "").ToUpperInvariant(), _pageTitleStyle, GUILayout.Height(34f));
            GUILayout.Label(ConfigFileMeta(entry), _metaStyle);
            if (!entry.HasConfig || string.IsNullOrWhiteSpace(entry.ConfigPath))
            {
                GUILayout.Space(16f);
                GUILayout.Label(NoConfigFileNotice, _wrapStyle);
                GUILayout.EndArea();
                return;
            }

            // A file that could not be read gets its reason instead of an empty editor that SAVE would write back.
            if (!MenuState.ConfigCanSave)
            {
                GUILayout.Space(16f);
                GUILayout.Label(MenuState.ConfigStatus ?? "", _wrapStyle);
                GUILayout.EndArea();
                return;
            }

            var view = MenuState.ViewState;
            var offset = view.GetScrollOffset(_scrollKey);
            var scroll = GUILayout.BeginScrollView(new Vector2(0f, offset), false, true, GUILayout.Height(rect.height - 100f));
            var text = MenuState.ConfigText ?? "";
            var next = GUILayout.TextArea(text, GUILayout.ExpandHeight(true));
            if (!string.Equals(next, text, StringComparison.Ordinal))
            {
                MenuState.SetConfigText(next);
            }

            GUILayout.EndScrollView();
            if (scroll.y != offset)
            {
                view.SetScrollOffset(_scrollKey, scroll.y);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("SAVE", _buttonActiveStyle, GUILayout.Width(110f), GUILayout.Height(32f)))
            {
                SaveConfig();
            }

            if (GUILayout.Button("REVERT", _buttonStyle, GUILayout.Width(110f), GUILayout.Height(32f)))
            {
                RevertConfig();
            }

            GUILayout.Label(_configDirty ? "UNSAVED" : "", _statusWarningStyle, GUILayout.Width(90f), GUILayout.Height(32f));
            GUILayout.Label("Most RedLoader and BepInEx config changes require a restart or mod reload.", _mutedStyle, GUILayout.Height(32f));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static void DrawFooter(Rect rect)
        {
            if (GUI.Button(new Rect(rect.x, rect.y, 150f, rect.height), "BACK", _footerButtonStyle))
            {
                GoBack();
            }

            var status = MenuState.IsConfigOpen ? MenuState.ConfigStatus : _page?.FooterStatus;
            GUI.Label(new Rect(rect.x + 170f, rect.y, rect.width - 380f, rect.height),
                string.IsNullOrWhiteSpace(status) ? _result ?? "" : status, _metaStyle);
            if (GUI.Button(new Rect(rect.xMax - 190f, rect.y, 190f, rect.height), "REFRESH", _footerButtonStyle))
            {
                MenuState.Refresh(out var tooltip);
                SetStatus(tooltip);
            }
        }

        private static void Invoke(MenuRow row, string value)
        {
            var intent = row.Intent;
            string tooltip;
            switch (intent.Kind)
            {
                case MenuIntentKind.Open:
                    OpenPage(intent.Key);
                    PublishTooltip(row.Tooltip);
                    break;
                case MenuIntentKind.SetFilter:
                    MenuState.SetFilter(intent.Key);
                    _viewDirty = true;
                    PublishTooltip(row.Tooltip);
                    break;
                case MenuIntentKind.Expand:
                case MenuIntentKind.Collapse:
                    MenuState.SetCollapsed(intent.Key, intent.Kind == MenuIntentKind.Collapse);
                    _viewDirty = true;
                    break;
                case MenuIntentKind.ApplySetting:
                    if (!IsOpenProvider(intent.ProviderId))
                    {
                        return;
                    }

                    // A failed apply keeps the draft so it can be corrected.
                    if (MenuState.ApplySetting(intent.Scope, intent.Key, value ?? "", out tooltip))
                    {
                        _drafts.Remove(row.Key);
                    }

                    SetStatus(tooltip);
                    break;
                case MenuIntentKind.ApplyConfigValue:
                    if (MenuState.ApplyConfigValue(intent.ProviderId, intent.Key, value ?? "", out tooltip))
                    {
                        _drafts.Remove(row.Key);
                    }

                    SetStatus(tooltip);
                    break;
                case MenuIntentKind.SetInput:
                    if (IsOpenProvider(intent.ProviderId) && !MenuState.SetInput(intent.ProviderId, intent.Scope, intent.FieldId, value ?? ""))
                    {
                        // The provider refused the text, so the field goes back to what it holds.
                        _drafts.Remove(row.Key);
                    }

                    break;
                case MenuIntentKind.RunCommand:
                    if (row.Control.Kind != MenuControlKind.Run || !IsOpenProvider(intent.ProviderId))
                    {
                        return;
                    }

                    MenuState.RunCommand(intent.CommandId, out tooltip);
                    SetStatus(tooltip);
                    break;
                case MenuIntentKind.OpenConfigFile:
                    OpenConfig(MenuState.FindEntry(intent.EntryId));
                    PublishTooltip(row.Tooltip);
                    break;
                case MenuIntentKind.OpenLiveSettings:
                    OpenFallbackLiveControls(intent.EntryId, OpenLiveSettings);
                    break;
                case MenuIntentKind.OpenModActions:
                    OpenFallbackLiveControls(intent.EntryId, OpenModActions);
                    break;
            }
        }

        private static bool IsOpenProvider(string providerId)
        {
            return string.Equals(MenuState.ProviderEntry?.Id, providerId, StringComparison.OrdinalIgnoreCase);
        }

        private static void OnNavClick(MenuNavItem item)
        {
            if (item.Kind == MenuNavKind.Group)
            {
                MenuState.SetCollapsed(item.Key, !item.Collapsed);
                _viewDirty = true;
                return;
            }

            if (MenuState.FoldNavItem(item))
            {
                _viewDirty = true;
                return;
            }

            OpenPage(item.Target);
            PublishTooltip(item.Label);
        }

        private static void OpenPage(string pageKey)
        {
            MenuState.CloseConfig();
            MenuState.Open(pageKey);
            _result = null;
            _viewDirty = true;
            _jumpPending = true;
        }

        // The first Esc only leaves a text field, as in the native menu.
        private static void NavigateBack()
        {
            if (GUIUtility.keyboardControl != 0)
            {
                GUIUtility.keyboardControl = 0;
                return;
            }

            GoBack();
        }

        private static void GoBack()
        {
            if (MenuState.IsConfigOpen)
            {
                MenuState.CloseConfig();
            }
            else if (!MenuState.Back())
            {
                Hide();
                return;
            }

            _result = null;
            _viewDirty = true;
        }

        private static void OpenConfig(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            MenuState.CloseConfig();
            MenuState.OpenConfig(entry, MaxConfigEditorBytes);
            // The fallback edits raw text only, so it skips parsing friendly settings on every keystroke.
            MenuState.SetFriendlyMode(false);
            _viewDirty = true;
        }

        private static void OpenFallbackLiveControls(string entryId, Action open)
        {
            if (OpenLiveControlsFor(entryId, open, out var status))
            {
                SetStatus(status);
            }
        }

        private static void SaveConfig()
        {
            MenuState.SaveConfig(() => RuntimeEntryPoint.RefreshModInventory(), out var tooltip);
            SetStatus(tooltip);
        }

        private static void RevertConfig()
        {
            // A focused text area keeps its own copy of the text, so it lets go before the revert lands.
            GUIUtility.keyboardControl = 0;
            MenuState.RevertConfig();
            SetStatus("REVERT - Reverted unsaved config edits.");
        }

        private static void SetStatus(string text)
        {
            _result = text;
            _viewDirty = true;
            PublishTooltip(text);
        }

        private static void PublishTooltip(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                UiState.SetTooltip(text);
            }
        }

        private static GUIStyle ToneStyle(MenuTone tone, GUIStyle neutral)
        {
            switch (tone)
            {
                case MenuTone.Info: return _statusHostedStyle;
                case MenuTone.Good: return _statusReadyStyle;
                case MenuTone.Bad: return _statusMissingStyle;
                case MenuTone.Warn: return _statusWarningStyle;
                default: return neutral;
            }
        }

        private static void EnsureStyles()
        {
            if (_rowStyle != null)
            {
                return;
            }

            // Opaque, so the pause menu never shows through.
            _windowTex = MakeTexture(new Color(0.035f, 0.04f, 0.042f, 1f));
            _panelTex = MakeTexture(new Color(0.015f, 0.014f, 0.012f, 0.93f));
            _rowTex = MakeTexture(new Color(1f, 1f, 1f, 0.075f));
            _lineTex = MakeTexture(new Color(0.3f, 0.62f, 0.72f, 0.55f));
            _buttonTex = MakeTexture(new Color(0.09f, 0.1f, 0.1f, 0.92f));
            _buttonHoverTex = MakeTexture(new Color(0.16f, 0.17f, 0.16f, 0.96f));
            _buttonSelectedTex = MakeTexture(new Color(0.32f, 0.29f, 0.18f, 0.98f));

            var hoverText = new Color(1f, 0.92f, 0.62f, 1f);
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _pageTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _breadcrumbStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.72f, 0.74f, 0.72f, 1f) } };
            // The status line is built by the loader and highlights its issue count with a colour tag.
            _statusLineStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.72f, 0.8f, 0.78f, 1f) } };
            _sectionStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _metaStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, normal = { textColor = new Color(0.72f, 0.8f, 0.78f, 1f) } };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _mutedStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.74f, 0.78f, 0.77f, 1f) } };
            _wrapStyle = new GUIStyle(_mutedStyle) { wordWrap = true, alignment = TextAnchor.UpperLeft };
            _headerStyle = new GUIStyle(_mutedStyle) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.78f, 0.88f, 0.86f, 1f) } };
            _rowStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 6, 6), margin = new RectOffset(0, 0, 2, 2) };
            SetStyleBackground(_rowStyle, _rowTex);
            _framelessButtonStyle = new GUIStyle(GUIStyle.none)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
                hover = { textColor = hoverText },
                active = { textColor = hoverText }
            };
            _sectionButtonStyle = new GUIStyle(_framelessButtonStyle) { fontSize = 17 };
            _navStyle = new GUIStyle(_framelessButtonStyle) { fontSize = 15, fontStyle = FontStyle.Normal };
            _navActiveStyle = new GUIStyle(_navStyle) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.96f, 0.84f, 0.48f, 1f) } };
            _navGroupStyle = new GUIStyle(_framelessButtonStyle) { fontSize = 13, normal = { textColor = new Color(0.78f, 0.88f, 0.86f, 1f) } };
            _footerButtonStyle = new GUIStyle(_framelessButtonStyle)
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.68f, 0.68f, 0.66f, 1f) }
            };
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, hover = { textColor = Color.white }, active = { textColor = Color.white }, padding = new RectOffset(8, 8, 4, 4), border = new RectOffset(1, 1, 1, 1) };
            SetButtonBackgrounds(_buttonStyle, _buttonTex, _buttonHoverTex, _buttonHoverTex);
            _buttonActiveStyle = new GUIStyle(_buttonStyle)
            {
                normal = { textColor = new Color(0.96f, 0.84f, 0.48f, 1f) },
                hover = { textColor = hoverText },
                active = { textColor = hoverText }
            };
            SetButtonBackgrounds(_buttonActiveStyle, _buttonSelectedTex, _buttonSelectedTex, _buttonSelectedTex);
            _statusReadyStyle = CreateStatusStyle(new Color(0.42f, 0.95f, 0.64f, 1f));
            _statusHostedStyle = CreateStatusStyle(new Color(0.35f, 0.82f, 1f, 1f));
            _statusWarningStyle = CreateStatusStyle(new Color(1f, 0.78f, 0.34f, 1f));
            _statusMissingStyle = CreateStatusStyle(new Color(1f, 0.36f, 0.32f, 1f));
            _inputStyle = new GUIStyle(GUI.skin.textField) { richText = false, fontSize = 14, alignment = TextAnchor.MiddleLeft };
            foreach (var style in new[] { _titleStyle, _pageTitleStyle, _breadcrumbStyle, _sectionStyle, _metaStyle, _labelStyle, _valueStyle, _mutedStyle, _wrapStyle, _headerStyle, _framelessButtonStyle, _sectionButtonStyle, _navStyle, _navActiveStyle, _navGroupStyle, _footerButtonStyle, _buttonStyle, _buttonActiveStyle })
            {
                style.richText = false;
            }
        }

        private static void SetStyleBackground(GUIStyle style, Texture2D texture)
        {
            style.normal.background = texture;
            style.hover.background = texture;
            style.active.background = texture;
            style.focused.background = texture;
            style.onNormal.background = texture;
            style.onHover.background = texture;
            style.onActive.background = texture;
            style.onFocused.background = texture;
        }

        private static void SetButtonBackgrounds(GUIStyle style, Texture2D normal, Texture2D hover, Texture2D active)
        {
            style.normal.background = normal;
            style.hover.background = hover;
            style.active.background = active;
            style.focused.background = hover;
            style.onNormal.background = active;
            style.onHover.background = active;
            style.onActive.background = active;
            style.onFocused.background = active;
        }

        private static GUIStyle CreateStatusStyle(Color color)
        {
            return new GUIStyle(GUI.skin.label)
            {
                richText = false,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = color }
            };
        }

        private static Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static void DrawSolid(Rect rect, Texture texture)
        {
            if (texture != null)
            {
                GUI.DrawTexture(rect, texture);
            }
        }
    }
}
