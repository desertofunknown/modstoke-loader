// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Native Modstoke mods menu renderer for Sons Of The Forest.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using global::Sons.Input;
using Modstoke.Loader;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal static class MenuRenderer
    {
        private const string RefreshTooltip = "REFRESH - Rescan RedLoader, BepInEx, Vortex, and config files.";
        private const string NavViewKey = "nav";
        private const string ConfigPagePrefix = "config:";
        private const string FriendlyRowPrefix = "friendly:";
        private const int MaxConfigEditorBytes = 262144;
        private const float ProviderRefreshInterval = 0.5f;
        private const float ReferenceHeight = 1080f;
        private const float PanelMarginX = 58f;
        private const float PanelMarginY = 28f;
        private const float MaxPanelWidth = 1880f;
        private const float NavShare = 0.3f;
        private const float MinNavWidth = 440f;
        private const float MaxNavWidth = 600f;
        private const int ChipsPerRow = 3;
        private const float ChipHeight = 34f;
        private const int MaxJumpButtons = 6;
        private const int JumpBarMinSections = 3;
        private const float JumpPadding = 8f;
        private static readonly Color ActiveButtonColor = MenuKit.Faded(0.42f);
        // The front end's veil, a little deeper: the menu sits on the world, which is blurred behind it.
        private static readonly Color ShadeColor = MenuKit.Hex(0x050606, 0.86f);
        private static readonly char[] NonPlainNumberMarks = { 'e', 'E', '+' };
        private static readonly UiEventSystem EventSystemLease = new UiEventSystem("Modstoke Mods Menu Event System");
        private static readonly List<Chip> _chips = new List<Chip>();
        private static readonly List<JumpButton> _jumpButtons = new List<JumpButton>();
        private static readonly List<MenuSection> _jumpSections = new List<MenuSection>();
        private static readonly Dictionary<Selectable, NavLinks> _links = new Dictionary<Selectable, NavLinks>(ReferenceEqualityComparer.Instance);
        private static readonly List<Selectable> _stops = new List<Selectable>();
        private static readonly List<RowStops> _rowStops = new List<RowStops>();
        private static readonly Dictionary<int, string> _chromeTooltips = new Dictionary<int, string>();
        private static readonly Dictionary<string, ConfigSettingSnapshot> _friendlyRows = new Dictionary<string, ConfigSettingSnapshot>(StringComparer.Ordinal);
        private static GameObject _root;
        private static RectTransform _panel;
        private static LayoutElement _navElement;
        private static float _panelInset = -1f;
        private static MenuLabel _breadcrumb;
        private static MenuLabel _statusLine;
        private static MenuLabel _hints;
        private static MenuLabel _paneStatus;
        private static RectTransform _paneFooter;
        private static bool? _paneFooterShown;
        private static MenuLabel _unsaved;
        private static GameObject _prompt;
        private static MenuLabel _promptMessage;
        private static Button _promptSave;
        private static Action _pendingLeave;
        private static GameObject _promptReturn;
        private static TMP_InputField _search;
        private static MenuScroll _navScroll;
        private static MenuScroll _pageScroll;
        private static NavView _nav;
        private static PageView _page;
        private static RectTransform _configButtons;
        private static Selectable[] _configStops = Array.Empty<Selectable>();
        private static Button _friendlyButton;
        private static Button _textButton;
        private static RectTransform _jumpBar;
        private static bool? _jumpShown;
        private static int _jumpCount;
        private static Button _statusRefresh;
        private static Button _back;
        private static TMP_InputField _rawEditor;
        private static LayoutElement _rawElement;
        private static bool _rawShown;
        private static bool? _configShown;
        private static bool? _friendlyShown;
        private static MenuPage _currentPage;
        private static string _renderedPageKey;
        private static string _result;
        private static string _themeTier = "plain";
        private static float _nextProviderRefresh;
        private static bool _inputWasFocused;
        private static bool _restorePending;
        private static GameObject _lastSelected;
        private static TMP_InputField _selectedInput;
        private static bool _created;
        private static bool _visible;
        private static GameObject _returnSelection;
        private static int _ownerSceneHandle;

        internal static bool IsVisible => _visible;

        internal static bool Show(ModInventoryState inventory)
        {
            using (Timings.Measure("ui.mods-menu.native", "show", inventory == null || inventory.Summary == null ? "no-inventory" : inventory.Summary.Total + " mods"))
            {
                try
                {
                    RequireEventSystem();
                    MenuState.SetInventory(inventory);
                    EnsureCreated();
                    if (!_created)
                    {
                        FailPresent();
                        return false;
                    }

                    CloseConfigEditor();
                    // The last page, filter and scroll position carry over from the previous open.
                    MenuState.Open(MenuState.ViewState.LastPage);
                    Present();
                    UiState.MarkVisible(UiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "list", ThemeDetail("Native Modstoke mods menu is visible."));
                    UiState.MarkHidden(UiSurfaceIds.ModConfig, "Mod Config", "native-menu", "closed", "No mod config is open.");
                    UiState.SetTooltip(MenuText.DefaultTooltip);
                    return true;
                }
                catch (Exception ex)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Native Modstoke Mods menu failed; falling back to IMGUI: " + ex.Message);
                    FailPresent();
                    return false;
                }
            }
        }

        private static void FailPresent()
        {
            _visible = false;
            if (_root != null)
            {
                _root.SetActive(false);
            }
            RestoreSelection();
            EventSystemLease.Release();
            if (!_created)
                ReleaseView();
        }

        internal static void Hide()
        {
            using (Timings.Measure("ui.mods-menu.native", "hide", _visible ? "visible" : "already-hidden"))
            {
                SaveScroll();
                _renderedPageKey = null;
                _visible = false;
                _restorePending = false;
                _lastSelected = null;
                _selectedInput = null;
                ClosePrompt();
                MenuState.CloseProvider();
                // Rows go back to the pool, which also drops a slider value that was never applied.
                _page?.Clear();
                if (_root != null)
                {
                    _root.SetActive(false);
                }
                RestoreSelection();
                EventSystemLease.Release();
                UiState.SetTooltip(MenuText.DefaultTooltip);
                UiState.MarkHidden(UiSurfaceIds.ModConfig, "Mod Config", "native-menu", "closed", "No mod config is open.");
                UiState.MarkHidden(UiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "closed", "Modstoke mods menu is not visible.");
            }
        }

        internal static void Update()
        {
            if (!_visible)
            {
                return;
            }

            var now = Time.unscaledTime;
            MenuScroll.Tick(_pendingLeave != null);
            _page.Tick(now);
            if (_restorePending)
            {
                // The first frame after showing has laid the panes out, so the saved offsets clamp correctly.
                _restorePending = false;
                _navScroll.Restore(NavViewKey);
                // Rows may have come or gone since the offset was saved, so the open page's row is brought back into view.
                var selected = _nav.FindKey(MenuState.ViewState.SelectedNavKey);
                if (selected != null)
                {
                    _navScroll.JumpIntoView(selected.Root);
                }
                else
                {
                    _navScroll.JumpTo(0f);
                }

                if (_renderedPageKey != null)
                {
                    _pageScroll.Restore(_renderedPageKey);
                }
            }

            PollSelection();
            var focusedInput = _selectedInput != null && _selectedInput.isFocused ? _selectedInput : null;
            var leavingInput = focusedInput != null || _inputWasFocused;
            _inputWasFocused = focusedInput != null;
            if (MenuState.IsProviderOpen && !MenuState.IsConfigOpen
                && (now >= _nextProviderRefresh || MenuState.ProviderContentNotified))
            {
                _nextProviderRefresh = now + ProviderRefreshInterval;
                // A rebind under the pointer would fight a scroll or click; bound rows keep focus and caret.
                if (!IsPointerBusy(now) && MenuState.RefreshProviderSnapshotsIfChanged())
                {
                    if (MenuState.IsProviderOpen)
                    {
                        Render(false);
                    }
                    else
                    {
                        OpenPage(MenuKeys.Home);
                    }
                }
            }

            if (InputBindings.IsPressed(KeybindIds.Back))
            {
                if (_pendingLeave != null)
                {
                    ResolvePrompt(PromptChoice.Cancel);
                }
                else if (leavingInput)
                {
                    // The first Esc only leaves the text field, even when the field saw the key first.
                    focusedInput?.DeactivateInputField();
                    _inputWasFocused = false;
                }
                else
                {
                    Guard(NavigateBack);
                }
            }
        }

        /// <summary>Runs <paramref name="close"/> now, or once the unsaved-edits prompt is answered with Save or Discard.</summary>
        internal static void GuardClose(Action close)
        {
            if (_visible)
            {
                Guard(close);
            }
            else
            {
                close();
            }
        }

        /// <summary>Shows the menu again at <paramref name="pageKey"/>, or at the open config entry's page.</summary>
        internal static void ResumeConfig(string pageKey = null)
        {
            try
            {
                RequireEventSystem();
                EnsureCreated();
                if (!_created)
                {
                    FailPresent();
                    return;
                }

                // Live controls open from a mod page, so closing them lands back on it.
                var entry = MenuState.ConfigEntry;
                CloseConfigEditor();
                MenuState.Open(pageKey ?? (entry != null ? MenuKeys.Mod(entry) : MenuState.ViewState.LastPage));
                Present();
            }
            catch (Exception ex)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Native Modstoke Mods menu could not reopen after live controls: " + ex.Message);
                FailPresent();
            }
        }

        internal static void OnSceneUnloaded(int handle)
        {
            if (handle != _ownerSceneHandle)
                return;
            _returnSelection = null;
            if (_visible)
                Hide();
            _ownerSceneHandle = 0;
        }

        private static void Present()
        {
            CaptureSelection();
            ApplyGeometry();
            _root.SetActive(true);
            _visible = true;
            _result = null;
            _hints.Set(KeyHints());
            Render(true);
            _restorePending = true;
            var row = _nav.FindKey(MenuState.ViewState.SelectedNavKey);
            Select(row != null ? row.Button : _back);
        }

        private static bool IsPointerBusy(float now)
        {
            return now - MenuScroll.LastWheelTime < 0.3f
                || Input.GetMouseButton(0)
                || _pageScroll.IsMoving;
        }

        /// <summary>Runs an action that would leave or reload the config editor, asking first while it has unsaved edits.</summary>
        private static void Guard(Action proceed)
        {
            if (_pendingLeave != null)
            {
                return;
            }

            if (!MenuState.IsConfigDirty)
            {
                proceed();
                return;
            }

            _pendingLeave = proceed;
            var system = EventSystem.current;
            _promptReturn = system != null ? system.currentSelectedGameObject : null;
            _promptMessage.Set((MenuState.ConfigEntry.DisplayName ?? "This mod") + " has config edits that are not saved yet. Save them to disk first, or discard them?");
            _prompt.SetActive(true);
            Select(_promptSave);
            UiState.SetTooltip("UNSAVED - Save or discard the config edits, or cancel to keep editing.");
        }

        private static void ResolvePrompt(PromptChoice choice)
        {
            var proceed = _pendingLeave;
            var previous = _promptReturn;
            ClosePrompt();
            if (proceed == null)
            {
                return;
            }

            if (choice == PromptChoice.Discard)
            {
                RevertConfig();
            }
            else if (choice == PromptChoice.Cancel || !SaveConfig())
            {
                // A failed save stays on the editor with its error in the footer.
                var selectable = previous != null && previous.activeInHierarchy ? previous.GetComponent<Selectable>() : null;
                if (selectable != null)
                {
                    Select(selectable);
                }
                else
                {
                    EnsureSelection();
                }

                return;
            }

            proceed();
        }

        private static void ClosePrompt()
        {
            _pendingLeave = null;
            _promptReturn = null;
            if (_prompt != null && _prompt.activeSelf)
            {
                _prompt.SetActive(false);
            }
        }

        private static void NavigateBack()
        {
            if (MenuState.IsConfigOpen)
            {
                CloseConfigEditor();
                _result = null;
                Render(false);
            }
            else if (MenuState.Back())
            {
                _result = null;
                Render(true);
            }
            else
            {
                Hide();
            }
        }

        private static void OpenPage(string pageKey)
        {
            CloseConfigEditor();
            MenuState.Open(pageKey);
            _result = null;
            Render(true);
        }

        private static void SetFilter(string filterProviderId)
        {
            MenuState.SetFilter(filterProviderId);
            Render(true);
        }

        private static void Refresh()
        {
            MenuState.Refresh(out var tooltip);
            Render(true);
            SetStatus(tooltip);
        }

        private static void Render(bool nav)
        {
            var config = MenuState.ConfigEntry;
            var chip = MenuViewBuilder.ChipViewName(MenuViewBuilder.ActiveChip());
            using (Timings.Measure("ui.mods-menu.native", config != null ? "rebuild-config" : "rebuild-list", config != null ? config.DisplayName ?? "none" : chip))
            {
                if (nav)
                {
                    RenderNav();
                    RenderChips();
                }

                RenderPage();
                RenderChrome();
                Relink();
                if (config != null)
                {
                    UiState.MarkVisible(UiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "config", ThemeDetail("Native Modstoke mod config view is visible."));
                    UiState.MarkVisible(UiSurfaceIds.ModConfig, "Mod Config", "native-menu", "config:" + (config.DisplayName ?? "none"), MenuState.ConfigStatus ?? "Native mod config editor is visible.");
                }
                else
                {
                    UiState.MarkVisible(UiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "list:" + chip, ThemeDetail("Native Modstoke mods list is visible."));
                    UiState.MarkHidden(UiSurfaceIds.ModConfig, "Mod Config", "native-menu", "closed", "No mod config is open.");
                }
            }
        }

        private static void RenderNav()
        {
            _nav.Bind(MenuViewBuilder.BuildNav(), MenuState.ViewState.SelectedNavKey);
        }

        private static void RenderChips()
        {
            var entries = MenuState.Inventory?.Entries;
            var active = MenuViewBuilder.ActiveChip();
            foreach (var chip in _chips)
            {
                var count = MenuState.FilterEntries(entries, chip.Id).Length.ToString(CultureInfo.InvariantCulture);
                chip.Label.Set(MenuState.TabLabel(MenuState.TabIndex(chip.Id)) + "  " + count);
                var on = string.Equals(chip.Id, active, StringComparison.OrdinalIgnoreCase);
                if (chip.Active == on)
                {
                    continue;
                }

                chip.Active = on;
                chip.Button.image.color = on ? ActiveButtonColor : MenuWidgets.ButtonColor;
                chip.Label.SetColor(on ? MenuWidgets.TextColor : MenuWidgets.MutedColor);
            }
        }

        private static void RenderPage()
        {
            var entry = MenuState.ConfigEntry;
            var page = entry != null ? BuildConfigPage(entry) : MenuViewBuilder.BuildPage();
            var switching = !string.Equals(page.Key, _renderedPageKey, StringComparison.Ordinal);
            if (switching && _renderedPageKey != null)
            {
                _pageScroll.Save(_renderedPageKey);
            }

            _currentPage = page;
            _page.Bind(page);
            _breadcrumb.Set(MenuViewBuilder.Breadcrumb(page, entry));
            UpdateRawEditor();
            UpdateJumpBar();
            UpdatePaneFooter();
            if (switching)
            {
                _renderedPageKey = page.Key;
                // The jump bar and footer set the viewport height that the restored offset is clamped to.
                LayoutRebuilder.ForceRebuildLayoutImmediate(_paneFooter.parent.Cast<RectTransform>());
                _pageScroll.Restore(page.Key);
            }

            EnsureSelection();
        }

        private static void RenderChrome()
        {
            _statusLine.Set(MenuText.StatusLine(MenuState.Inventory, RuntimeEntryPoint.Version));
            // Without a loaded file there is nothing to switch views of or to save.
            var config = MenuState.ConfigCanSave;
            if (_configShown != config)
            {
                _configShown = config;
                _configButtons.gameObject.SetActive(config);
            }

            var friendly = MenuState.FriendlyMode;
            if (config && _friendlyShown != friendly)
            {
                _friendlyShown = friendly;
                _friendlyButton.image.color = friendly ? ActiveButtonColor : MenuWidgets.ButtonColor;
                _textButton.image.color = friendly ? MenuWidgets.ButtonColor : ActiveButtonColor;
            }

            UpdateUnsaved();
        }

        // The footer folds away while it has neither a status nor the config buttons to show. Showing it shortens the
        // page viewport, so the pane is laid out at once and the selected row, often the one just edited, kept in view.
        private static void UpdatePaneFooter()
        {
            var status = _currentPage?.FooterStatus;
            var text = string.IsNullOrWhiteSpace(status) ? _result : status;
            _paneStatus.Set(text);
            var shown = !string.IsNullOrWhiteSpace(text) || MenuState.ConfigCanSave;
            if (_paneFooterShown == shown)
            {
                return;
            }

            _paneFooterShown = shown;
            _paneFooter.gameObject.SetActive(shown);
            // A wrapper from Transform.parent can be a plain Transform; C# casts fail on it.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_paneFooter.parent.Cast<RectTransform>());
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            // A slider still being dragged stays where it is under the pointer.
            var row = selected != null && !Input.GetMouseButton(0) ? _page.FindView(selected) : null;
            if (row != null)
            {
                _pageScroll.ScrollIntoView(row.Root);
            }
        }

        private static void SetStatus(string text)
        {
            _result = text;
            UiState.SetTooltip(text);
            UpdatePaneFooter();
        }

        private static void PublishTooltip(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                UiState.SetTooltip(text);
            }
        }

        /// <summary>Handles a row's control; false when the value it carried was refused, so the view can keep or reset it.</summary>
        private static bool OnRowIntent(MenuRow row, string value)
        {
            if (_friendlyRows.TryGetValue(row.Key, out var setting))
            {
                EditFriendly(row, setting, value);
                return true;
            }

            var intent = row.Intent;
            string tooltip;
            bool applied;
            switch (intent.Kind)
            {
                case MenuIntentKind.Open:
                    OpenPage(intent.Key);
                    PublishTooltip(row.Tooltip);
                    break;
                case MenuIntentKind.SetFilter:
                    SetFilter(intent.Key);
                    PublishTooltip(row.Tooltip);
                    break;
                case MenuIntentKind.Expand:
                case MenuIntentKind.Collapse:
                    MenuState.SetCollapsed(intent.Key, intent.Kind == MenuIntentKind.Collapse);
                    Render(false);
                    break;
                case MenuIntentKind.ApplySetting:
                    if (!IsOpenProvider(intent.ProviderId))
                    {
                        return false;
                    }

                    applied = MenuState.ApplySetting(intent.Scope, intent.Key, value ?? "", out tooltip);
                    Render(false);
                    SetStatus(tooltip);
                    return applied;
                case MenuIntentKind.ApplyConfigValue:
                    applied = MenuState.ApplyConfigValue(intent.ProviderId, intent.Key, value ?? "", out tooltip);
                    Render(false);
                    SetStatus(tooltip);
                    return applied;
                case MenuIntentKind.SetInput:
                    if (IsOpenProvider(intent.ProviderId) && !MenuState.SetInput(intent.ProviderId, intent.Scope, intent.FieldId, value ?? ""))
                    {
                        // The rebind gives the row the provider's own value for the field to go back to.
                        MenuState.RefreshProviderSnapshotsIfChanged();
                        Render(false);
                        return false;
                    }

                    break;
                case MenuIntentKind.RunCommand:
                    if (row.Control.Kind != MenuControlKind.Run || !IsOpenProvider(intent.ProviderId))
                    {
                        return false;
                    }

                    MenuState.RunCommand(intent.CommandId, out tooltip);
                    Render(false);
                    SetStatus(tooltip);
                    break;
                case MenuIntentKind.OpenConfigFile:
                    OpenConfig(MenuState.FindEntry(intent.EntryId));
                    break;
                case MenuIntentKind.OpenLiveSettings:
                    Guard(() => OpenLiveControls(intent.EntryId, MenuController.OpenLiveSettings));
                    break;
                case MenuIntentKind.OpenModActions:
                    Guard(() => OpenLiveControls(intent.EntryId, MenuController.OpenModActions));
                    break;
            }

            return true;
        }

        private static bool IsOpenProvider(string providerId)
        {
            return string.Equals(MenuState.ProviderEntry?.Id, providerId, StringComparison.OrdinalIgnoreCase);
        }

        private static void OnNavClick(NavRow row)
        {
            var item = row.Item;
            if (item.Kind == MenuNavKind.Group)
            {
                MenuState.SetCollapsed(item.Key, !item.Collapsed);
                RenderNav();
                Relink();
                return;
            }

            Guard(() => OpenNavItem(item));
        }

        private static void OpenNavItem(MenuNavItem item)
        {
            if (MenuState.FoldNavItem(item))
            {
                RenderNav();
                Relink();
                return;
            }

            OpenPage(item.Target);
        }

        private static void OpenConfig(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            CloseConfigEditor();
            MenuState.OpenConfig(entry, MaxConfigEditorBytes);
            Render(false);
        }

        private static void OpenLiveControls(string entryId, Action open)
        {
            CloseConfigEditor();
            if (MenuController.OpenLiveControlsFor(entryId, open, out var status))
            {
                Render(false);
                SetStatus(status);
            }
        }

        private static void CloseConfigEditor()
        {
            MenuState.CloseConfig();
            _friendlyRows.Clear();
        }

        private static MenuPage BuildConfigPage(ModInventoryEntry entry)
        {
            var pageKey = ConfigPagePrefix + MenuKeys.EntryId(entry);
            var sections = new List<MenuSection>();
            _friendlyRows.Clear();
            if (!entry.HasConfig || string.IsNullOrWhiteSpace(entry.ConfigPath))
            {
                sections.Add(NoticeSection(pageKey, MenuText.NoConfigFileNotice));
            }
            else if (!MenuState.ConfigCanSave)
            {
                sections.Add(NoticeSection(pageKey, MenuState.ConfigStatus));
            }
            else if (!MenuState.FriendlyMode)
            {
                sections.Add(NoticeSection(pageKey, "Raw config text. SAVE writes it to disk exactly as shown."));
            }
            else
            {
                AddFriendlySections(pageKey, sections);
            }

            return new MenuPage
            {
                Key = pageKey,
                Kind = MenuPageKind.Mod,
                Title = entry.DisplayName,
                Meta = MenuText.ConfigFileMeta(entry),
                Sections = MenuViewBuilder.Fold(MenuState.ViewState, sections),
                FooterStatus = MenuState.ConfigStatus
            };
        }

        private static void AddFriendlySections(string pageKey, List<MenuSection> sections)
        {
            var settings = MenuState.FriendlySettings;
            if (settings.Length == 0)
            {
                sections.Add(NoticeSection(pageKey, "No simple key/value settings were detected. Switch to TEXT for this config file."));
                return;
            }

            List<MenuRow> rows = null;
            string section = null;
            foreach (var setting in settings)
            {
                // Sections follow the file, so a name that comes back later starts a new one.
                if (rows == null || !string.Equals(section, setting.Section, StringComparison.OrdinalIgnoreCase))
                {
                    section = setting.Section;
                    rows = new List<MenuRow>();
                    sections.Add(new MenuSection
                    {
                        Key = pageKey + "/section:" + sections.Count.ToString(CultureInfo.InvariantCulture) + ":" + section,
                        Title = string.IsNullOrWhiteSpace(section) ? "General" : section,
                        Rows = rows
                    });
                }

                rows.Add(FriendlyRow(setting));
            }
        }

        // The file declares no kinds, so booleans and numbers are recognised by their text; 0 and 1 stay numbers.
        private static MenuRow FriendlyRow(ConfigSettingSnapshot setting)
        {
            var key = FriendlyRowPrefix + setting.LineIndex.ToString(CultureInfo.InvariantCulture) + "/" + setting.Key;
            var label = FriendlyLabel(setting.Key);
            var trimmed = setting.Value?.Trim();
            var toggle = trimmed != "0" && trimmed != "1" && LoaderConfig.TryParseBoolean(trimmed, out _);
            _friendlyRows[key] = setting;
            return new MenuRow
            {
                Key = key,
                Kind = toggle ? MenuRowKind.ConfigValue : MenuRowKind.Input,
                Label = label,
                Value = MenuState.TryGetFriendlyDraft(setting.LineIndex, out var draft) ? draft : setting.Value,
                Detail = setting.Tooltip,
                Tooltip = label + " - " + (string.IsNullOrWhiteSpace(setting.Tooltip) ? "Config setting from this mod." : setting.Tooltip),
                Control = toggle
                    ? new MenuControl { Kind = MenuControlKind.Toggle, Editable = true, ValueKind = ConfigValueKinds.Boolean }
                    : FriendlyInput(trimmed)
            };
        }

        // Plain numbers get - and + beside the field, stepping by 1, or by 0.1 for a value written with a fraction.
        // A leading + or an exponent keeps a plain text field, since the number fields would strip those characters.
        private static MenuControl FriendlyInput(string value)
        {
            if (!MenuText.TryParseNumber(value, out _) || value.IndexOfAny(NonPlainNumberMarks) >= 0)
            {
                return new MenuControl { Kind = MenuControlKind.InputLive, Editable = true, ValueKind = ConfigValueKinds.Text };
            }

            var integer = value.IndexOf('.') < 0;
            return new MenuControl
            {
                Kind = MenuControlKind.InputLive,
                Editable = true,
                ValueKind = integer ? ConfigValueKinds.Integer : ConfigValueKinds.Number,
                Step = integer ? 1d : 0.1d
            };
        }

        private static void EditFriendly(MenuRow row, ConfigSettingSnapshot setting, string value)
        {
            var toggle = row.Control.Kind == MenuControlKind.Toggle;
            var on = LoaderConfig.TryParseBoolean(value, out var parsed) && parsed;
            // A toggle writes true or false, which matches the file's own spelling only by meaning.
            MenuState.SetFriendlyDraft(setting, toggle && LoaderConfig.TryParseBoolean(setting.Value?.Trim(), out var was) && was == on
                ? setting.Value
                : value);
            if (!toggle)
            {
                UpdateUnsaved();
                return;
            }

            Render(false);
            PublishTooltip(FriendlyLabel(setting.Key) + " - " + (on ? "Enabled." : "Disabled."));
        }

        private static void SetFriendlyMode(bool friendly)
        {
            if (!MenuState.IsConfigOpen || MenuState.FriendlyMode == friendly)
            {
                return;
            }

            MenuState.SetFriendlyMode(friendly);
            Render(false);
        }

        private static bool SaveConfig()
        {
            var entry = MenuState.ConfigEntry;
            if (entry == null || !entry.HasConfig || string.IsNullOrWhiteSpace(entry.ConfigPath))
            {
                SetStatus("SAVE - No config file is selected.");
                return false;
            }

            var saved = MenuState.SaveConfig(() => RuntimeEntryPoint.RefreshModInventory(), out var tooltip);
            // The rescan can change the mod's status, so the nav is rebound with the page.
            Render(saved);
            SetStatus(tooltip);
            return saved;
        }

        private static void RevertConfig()
        {
            if (!MenuState.IsConfigOpen)
            {
                return;
            }

            MenuState.RevertConfig();
            if (_rawEditor != null && _rawShown)
            {
                _rawEditor.text = MenuState.ConfigText ?? "";
            }

            Render(false);
            SetStatus("REVERT - Reverted unsaved config edits.");
        }

        private static void UpdateUnsaved()
        {
            _unsaved?.SetActive(MenuState.IsConfigDirty);
        }

        private static void UpdateRawEditor()
        {
            var show = MenuState.ConfigCanSave && !MenuState.FriendlyMode
                && MenuState.ConfigEntry.HasConfig && !string.IsNullOrWhiteSpace(MenuState.ConfigEntry.ConfigPath);
            if (show && _rawEditor == null)
            {
                _rawEditor = CreateRawEditor(_pageScroll.Content);
                _rawElement = _rawEditor.GetComponent<LayoutElement>();
                // The state follows every keystroke, so the unsaved marker and the prompt see raw edits too.
                _rawEditor.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value =>
                {
                    if (_rawShown)
                    {
                        MenuState.SetConfigText(value);
                        UpdateUnsaved();
                    }
                }));
            }

            if (_rawEditor == null)
            {
                return;
            }

            if (_rawShown != show)
            {
                _rawShown = show;
                _rawEditor.gameObject.SetActive(show);
                _pageScroll.WheelExclusion = show ? _rawEditor.GetComponent<RectTransform>() : null;
                if (show)
                {
                    _rawEditor.text = MenuState.ConfigText ?? "";
                    var height = Mathf.Max(240f, _pageScroll.Viewport.rect.height - 24f);
                    _rawElement.minHeight = height;
                    _rawElement.preferredHeight = height;
                }
            }

            // The page rows are bound in front of the editor.
            var transform = _rawEditor.transform;
            if (show && transform.GetSiblingIndex() != transform.parent.childCount - 1)
            {
                transform.SetAsLastSibling();
            }
        }

        private static void UpdateJumpBar()
        {
            _jumpSections.Clear();
            foreach (var section in _currentPage?.Sections ?? Array.Empty<MenuSection>())
            {
                if (section != null && !section.NoJump && !string.IsNullOrWhiteSpace(section.Title))
                {
                    _jumpSections.Add(section);
                }
            }

            var show = _jumpSections.Count >= JumpBarMinSections;
            if (_jumpSections.Count > _jumpButtons.Count)
            {
                // Actions come last, so the last section keeps its button when the bar runs out of room.
                _jumpSections.RemoveRange(_jumpButtons.Count - 1, _jumpSections.Count - _jumpButtons.Count);
            }

            if (_jumpShown != show)
            {
                _jumpShown = show;
                _jumpBar.gameObject.SetActive(show);
            }

            _jumpCount = show ? _jumpSections.Count : 0;
            if (!show)
            {
                return;
            }

            for (var i = 0; i < _jumpButtons.Count; i++)
            {
                var jump = _jumpButtons[i];
                var active = i < _jumpCount;
                jump.SectionKey = active ? _jumpSections[i].Key : null;
                if (active)
                {
                    jump.Label.Set(_jumpSections[i].Title.ToUpperInvariant());
                }

                if (jump.Active != active)
                {
                    jump.Active = active;
                    jump.Button.gameObject.SetActive(active);
                }
            }
        }

        private static void JumpTo(JumpButton jump)
        {
            if (jump.SectionKey == null || !_page.TryGetView(PageView.HeaderKey(jump.SectionKey), out var header))
            {
                return;
            }

            _pageScroll.ScrollToChild(header.Root, JumpPadding);
            // The keyboard carries on from the section it jumped to.
            Select(header.Primary);
        }

        private static MenuSection NoticeSection(string pageKey, string text)
        {
            return new MenuSection
            {
                Key = pageKey + "/notice",
                Rows = new[] { new MenuRow { Key = pageKey + "/notice/text", Kind = MenuRowKind.Info, Label = text } }
            };
        }

        private static string FriendlyLabel(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? "" : key.Replace("_", " ").Replace("-", " ");
        }

        private static string KeyHints()
        {
            var keys = InputBindings.Snapshot();
            string Key(string id, string fallback) => keys.TryGetValue(id, out var keybind) ? keybind.KeyName : fallback;
            return Key(KeybindIds.Back, "Escape") + " back     PgUp/PgDn page     Home/End top or bottom     "
                + Key(KeybindIds.ToggleModsMenu, "F6") + " close";
        }

        private static string ThemeDetail(string detail)
        {
            return detail + " theme=" + _themeTier;
        }

        private static void PollSelection()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            if (selected == _lastSelected)
            {
                return;
            }

            _lastSelected = selected;
            _selectedInput = selected != null ? selected.GetComponent<TMP_InputField>() : null;
            if (selected == null || !selected.transform.IsChildOf(_root.transform))
            {
                return;
            }

            // A press selects on the way down, so scrolling then would move the control out from under the release.
            var follow = !Input.GetMouseButton(0);
            var row = _page.FindView(selected);
            if (row != null)
            {
                if (follow)
                {
                    _pageScroll.ScrollIntoView(row.Root);
                }

                PublishTooltip(row.Row.Tooltip);
                return;
            }

            var nav = _nav.Find(selected);
            if (nav != null)
            {
                if (follow)
                {
                    _navScroll.ScrollIntoView(nav.Root);
                }

                PublishTooltip(nav.Item.Label);
                return;
            }

            if (_chromeTooltips.TryGetValue(selected.GetInstanceID(), out var tooltip))
            {
                PublishTooltip(tooltip);
            }
        }

        // A row that went back to the pool leaves the selection on a hidden object, which strands the keyboard.
        private static void EnsureSelection()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            if (selected == null || selected.activeInHierarchy || !selected.transform.IsChildOf(_root.transform))
            {
                return;
            }

            var row = _nav.FindKey(MenuState.ViewState.SelectedNavKey);
            Select(row != null ? row.Button : _back);
        }

        // Selected without publishing its tooltip, so an action's result stays the last tooltip.
        private static void Select(Selectable selectable)
        {
            if (selectable == null)
            {
                return;
            }

            selectable.Select();
            _lastSelected = selectable.gameObject;
            // A field selected here is still the one Esc leaves first once the user types in it.
            _selectedInput = selectable.GetComponent<TMP_InputField>();
        }

        // Enter in the search box moves on to the first match.
        private static void SelectFirstMatch()
        {
            foreach (var row in _nav.Rows)
            {
                if (row.Item.Kind != MenuNavKind.Group && row.Item.Kind != MenuNavKind.Home)
                {
                    Select(row.Button);
                    return;
                }
            }
        }

        /// <summary>
        /// Explicit navigation for every control, so arrow keys and the gamepad never reach the pause menu underneath.
        /// Nav rows chain vertically, Right enters the page, Left returns to the current nav row, and the last rows
        /// lead down to the footer.
        /// </summary>
        private static void Relink()
        {
            if (_nav == null || _page == null)
            {
                return;
            }

            _stops.Clear();
            _rowStops.Clear();
            foreach (var view in _page.Views)
            {
                var start = _stops.Count;
                view.CollectSelectables(_stops);
                if (_stops.Count == start)
                {
                    continue;
                }

                var landing = start;
                for (var i = start; i < _stops.Count; i++)
                {
                    if (ReferenceEquals(_stops[i], view.Primary))
                    {
                        landing = i;
                        break;
                    }
                }

                _rowStops.Add(new RowStops(start, _stops.Count - start, landing, view.UsesHorizontal));
            }

            var navRows = _nav.Rows;
            Selectable firstNav = navRows.Count > 0 ? navRows[0].Button : null;
            Selectable lastNav = navRows.Count > 0 ? navRows[navRows.Count - 1].Button : null;
            var currentNav = _nav.FindKey(MenuState.ViewState.SelectedNavKey)?.Button ?? firstNav;
            Selectable chipsBottom = _chips.Count > 0 ? _chips[(_chips.Count - 1) / ChipsPerRow * ChipsPerRow].Button : _search;
            Selectable firstJump = _jumpCount > 0 ? _jumpButtons[0].Button : null;
            var firstRow = _rowStops.Count > 0 ? _stops[_rowStops[0].Landing] : null;
            var lastRow = _rowStops.Count > 0 ? _stops[_rowStops[_rowStops.Count - 1].Landing] : null;
            Selectable raw = _rawShown ? _rawEditor : null;
            var firstConfig = _configShown == true && _configStops.Length > 0 ? _configStops[0] : null;
            var pageTop = firstJump ?? _statusRefresh;
            var pageEntry = firstRow ?? firstJump ?? raw ?? firstConfig;
            var aboveConfig = raw ?? lastRow ?? pageTop;

            Link(_statusRefresh, null, firstJump ?? firstRow ?? _search, _search, null);
            Link(_search, _statusRefresh, _chips.Count > 0 ? _chips[0].Button : firstNav, null, pageEntry);
            for (var i = 0; i < _chips.Count; i++)
            {
                var column = i % ChipsPerRow;
                Link(_chips[i].Button,
                    i < ChipsPerRow ? _search : _chips[i - ChipsPerRow].Button,
                    i + ChipsPerRow < _chips.Count ? _chips[i + ChipsPerRow].Button : firstNav ?? _back,
                    column == 0 ? null : _chips[i - 1].Button,
                    column == ChipsPerRow - 1 || i == _chips.Count - 1 ? pageEntry : _chips[i + 1].Button);
            }

            for (var i = 0; i < navRows.Count; i++)
            {
                Link(navRows[i].Button,
                    i == 0 ? chipsBottom : navRows[i - 1].Button,
                    i == navRows.Count - 1 ? _back : navRows[i + 1].Button,
                    null,
                    pageEntry);
            }

            for (var i = 0; i < _jumpCount; i++)
            {
                Link(_jumpButtons[i].Button,
                    _statusRefresh,
                    firstRow ?? raw ?? firstConfig ?? _back,
                    i == 0 ? currentNav : _jumpButtons[i - 1].Button,
                    i + 1 < _jumpCount ? _jumpButtons[i + 1].Button : null);
            }

            for (var r = 0; r < _rowStops.Count; r++)
            {
                var row = _rowStops[r];
                var up = r == 0 ? pageTop : _stops[_rowStops[r - 1].Landing];
                var down = r == _rowStops.Count - 1 ? raw ?? firstConfig ?? _back : _stops[_rowStops[r + 1].Landing];
                for (var k = 0; k < row.Count; k++)
                {
                    var index = row.Start + k;
                    // A slider steps its value with Left and Right, which it only does while they lead nowhere.
                    Link(_stops[index], up, down,
                        row.Horizontal ? null : k == 0 ? currentNav : _stops[index - 1],
                        row.Horizontal || k == row.Count - 1 ? null : _stops[index + 1]);
                }
            }

            if (raw != null)
            {
                Link(raw, lastRow ?? pageTop, firstConfig ?? _back, currentNav, null);
            }

            if (firstConfig != null)
            {
                for (var k = 0; k < _configStops.Length; k++)
                {
                    Link(_configStops[k], aboveConfig, _back,
                        k == 0 ? currentNav : _configStops[k - 1],
                        k == _configStops.Length - 1 ? null : _configStops[k + 1]);
                }
            }

            // Right from BACK climbs into the bottom of the page.
            Link(_back, lastNav ?? _search, null, null, firstConfig ?? raw ?? lastRow);
        }

        // Writes a control's navigation only when its targets changed, so an unchanged rebind costs no interop.
        private static void Link(Selectable selectable, Selectable up, Selectable down, Selectable left, Selectable right)
        {
            if (selectable == null)
            {
                return;
            }

            var links = new NavLinks(up, down, left, right);
            if (_links.TryGetValue(selectable, out var current) && current.Same(links))
            {
                return;
            }

            _links[selectable] = links;
            var navigation = selectable.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnUp = up;
            navigation.selectOnDown = down;
            navigation.selectOnLeft = left;
            navigation.selectOnRight = right;
            selectable.navigation = navigation;
        }

        private static void SaveScroll()
        {
            if (_renderedPageKey == null)
            {
                return;
            }

            _pageScroll?.Save(_renderedPageKey);
            _navScroll?.Save(NavViewKey);
        }

        /// <summary>The panel's side inset and nav pane width in canvas units, shared with screen hosts.</summary>
        internal static void PanelGeometry(out float inset, out float marginY, out float navWidth)
        {
            // Match-height scaling keeps the canvas 1080 high, so only its width follows the aspect ratio.
            var canvasWidth = Screen.height > 0 ? ReferenceHeight * Screen.width / Screen.height : 1920f;
            inset = Mathf.Max(PanelMarginX, Mathf.Round((canvasWidth - MaxPanelWidth) * 0.5f));
            marginY = PanelMarginY;
            navWidth = Mathf.Round(Mathf.Clamp((canvasWidth - 2f * inset) * NavShare, MinNavWidth, MaxNavWidth));
        }

        private static void ApplyGeometry()
        {
            PanelGeometry(out var inset, out var marginY, out var navWidth);
            if (inset != _panelInset)
            {
                _panelInset = inset;
                MenuWidgets.Stretch(_panel, inset, marginY, inset, marginY);
            }

            if (navWidth != _navElement.preferredWidth)
            {
                _navElement.minWidth = navWidth;
                _navElement.preferredWidth = navWidth;
            }
        }

        private static void EnsureCreated()
        {
            // A menu first built before the options scene was captured picks up the theme on its next open.
            if (_created && _root != null && (_visible || _themeTier == MenuTheme.TierName))
            {
                return;
            }

            using (Timings.Measure("ui.mods-menu.native", "create", "initial canvas"))
            {
                ReleaseView();
                _themeTier = MenuTheme.TierName;

                _root = new GameObject("OpenACAI Native Mods Menu");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _root.SetActive(false);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 4600;

                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, ReferenceHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 1f;

                _root.AddComponent<GraphicRaycaster>();
                AddInputState(_root);

                var shade = MenuWidgets.CreateImage("Shade", _root.transform, ShadeColor);
                MenuWidgets.Stretch(shade.rectTransform, 0f, 0f, 0f, 0f);
                // Keeps clicks from reaching the menus underneath.
                shade.raycastTarget = true;

                // Content on the veil, with no panel of its own: grouped by space and hairlines.
                var panel = MenuWidgets.CreateImage("Content", _root.transform, Color.clear);
                _panel = panel.rectTransform;
                var stack = panel.gameObject.AddComponent<VerticalLayoutGroup>();
                stack.padding = new RectOffset(28, 28, 0, 0);
                stack.spacing = 4f;
                stack.childControlWidth = true;
                stack.childForceExpandWidth = true;
                stack.childControlHeight = true;
                stack.childForceExpandHeight = false;

                CreateHeader(panel.transform);
                CreateStatusStrip(panel.transform);
                CreateBody(panel.transform);
                CreateFooter(panel.transform);
                CreatePrompt(_root.transform);
                _created = true;
            }
        }

        // The cursor and action map recipe of SUI panels that take input, active while the root is. The event system
        // comes from RequireEventSystem, which borrows SUI's only while the game has none active.
        private static void AddInputState(GameObject root)
        {
            try
            {
                var cursor = root.AddComponent<InputCursorState>();
                cursor._enabled = true;
                cursor._hardwareCursor = true;
                cursor._priority = 100;

                var actionMap = root.AddComponent<InputActionMapState>();
                actionMap._applyState = InputState.Console;
            }
            catch (Exception ex)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Mods menu cursor and input state are unavailable: " + ex.Message);
            }
        }

        private static void CreateHeader(Transform parent)
        {
            var header = Band(parent, "Header", 96f, false);
            var title = MenuLabel.Create(header, "Heading", 62f, MenuWidgets.TextColor, true, false);
            title.Text.characterSpacing = 5f;
            title.Set("MODS");
            MenuWidgets.Fixed(title.Text.gameObject, 320f, 0f);
            _breadcrumb = MenuLabel.Create(header, "Breadcrumb", 16f, MenuWidgets.MutedColor, false, false);
            _breadcrumb.Text.richText = false;
            _breadcrumb.Text.alignment = TextAlignmentOptions.MidlineRight;
            MenuWidgets.Flexible(_breadcrumb.Text.gameObject, 1f);
        }

        private static void CreateStatusStrip(Transform parent)
        {
            var strip = Band(parent, "StatusStrip", MenuWidgets.ControlHeight, true);
            _statusLine = MenuLabel.Create(strip, "Status", 14f, MenuWidgets.MutedColor, false, false);
            MenuWidgets.Flexible(_statusLine.Text.gameObject, 1f);
            _statusRefresh = MenuWidgets.CreateButton(strip, "REFRESH", 140f, out _);
            Wire(_statusRefresh, Refresh, RefreshTooltip);
        }

        private static void CreateBody(Transform parent)
        {
            var body = MenuWidgets.CreateRect("Body", parent);
            var layout = body.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 16f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = true;
            body.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;

            var nav = Pane(body, "NavPane", true);
            _navElement = nav.gameObject.AddComponent<LayoutElement>();
            _navElement.flexibleWidth = 0f;
            _search = MenuWidgets.CreateInput(nav, false, "Search mods and pages");
            _search.text = MenuState.ViewState.SearchText ?? "";
            _search.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value =>
            {
                MenuState.SetSearch(value);
                if (_visible)
                {
                    RenderNav();
                    Relink();
                }
            }));
            _search.onSubmit.AddListener((UnityAction<string>)new Action<string>(_ => SelectFirstMatch()));
            _chromeTooltips[_search.gameObject.GetInstanceID()] = "SEARCH - Filter mods and pages by name, ID, author, source, or setting.";
            CreateChips(nav);
            _navScroll = MenuScroll.Create(nav, "NavScroll", MenuScroll.NavPitch, true);
            Grow(_navScroll.Root);
            _nav = new NavView(_navScroll.Content, OnNavClick);

            var page = Pane(body, "PagePane", false);
            MenuWidgets.Flexible(page.gameObject, 1f);
            CreateJumpBar(page);
            _pageScroll = MenuScroll.Create(page, "PageScroll", MenuScroll.PagePitch, true);
            _pageScroll.Primary = true;
            Grow(_pageScroll.Root);
            _page = new PageView(_pageScroll.Content, OnRowIntent);

            var footer = Band(page, "PaneFooter", 52f, true);
            _paneFooter = footer;
            _paneStatus = MenuLabel.Create(footer, "Status", 15f, MenuWidgets.MutedColor, false, false);
            _paneStatus.Text.richText = false;
            MenuWidgets.Flexible(_paneStatus.Text.gameObject, 1f);
            _configButtons = MenuWidgets.CreateRect("ConfigButtons", footer);
            var buttons = _configButtons.gameObject.AddComponent<HorizontalLayoutGroup>();
            buttons.spacing = 8f;
            buttons.childAlignment = TextAnchor.MiddleRight;
            buttons.childControlWidth = true;
            buttons.childForceExpandWidth = false;
            buttons.childControlHeight = true;
            buttons.childForceExpandHeight = false;
            _friendlyButton = MenuWidgets.CreateButton(_configButtons, "FRIENDLY", 150f, out _);
            Wire(_friendlyButton, () => SetFriendlyMode(true), "FRIENDLY - Use a guided key/value editor generated from the config file.");
            _textButton = MenuWidgets.CreateButton(_configButtons, "TEXT", 110f, out _);
            Wire(_textButton, () => SetFriendlyMode(false), "TEXT - Show the raw config text for advanced edits.");
            _unsaved = MenuLabel.Create(_configButtons, "Unsaved", 14f, MenuWidgets.ToneColor(MenuTone.Warn), true, false);
            _unsaved.Set("UNSAVED");
            _unsaved.Text.alignment = TextAlignmentOptions.Center;
            MenuWidgets.Fixed(_unsaved.Text.gameObject, 100f, 0f);
            _unsaved.SetActive(false);
            var save = MenuWidgets.CreateButton(_configButtons, "SAVE", 110f, out _);
            Wire(save, () => SaveConfig(), "SAVE - Write the current config edits to disk.");
            var revert = MenuWidgets.CreateButton(_configButtons, "REVERT", 130f, out _);
            Wire(revert, RevertConfig, "REVERT - Discard unsaved edits and reload the last opened text.");
            _configStops = new Selectable[] { _friendlyButton, _textButton, save, revert };
            _configButtons.gameObject.SetActive(false);
            _configShown = false;
        }

        /// <summary>Section shortcuts above the page, shown while it has enough sections to need them.</summary>
        private static void CreateJumpBar(Transform parent)
        {
            _jumpBar = Band(parent, "JumpBar", ChipHeight, false);
            _jumpBar.GetComponent<HorizontalLayoutGroup>().spacing = 6f;
            for (var i = 0; i < MaxJumpButtons; i++)
            {
                var button = MenuWidgets.CreateButton(_jumpBar, "", 0f, out var label);
                var element = MenuWidgets.Flexible(button.gameObject, 1f);
                element.minHeight = ChipHeight;
                element.preferredHeight = ChipHeight;
                label.Text.fontSize = 14f;
                var jump = new JumpButton(button, label);
                Wire(button, () => JumpTo(jump), "JUMP - Scroll the page to this section.");
                _jumpButtons.Add(jump);
            }

            _jumpBar.gameObject.SetActive(false);
            _jumpShown = false;
        }

        private static void CreateChips(Transform parent)
        {
            var grid = MenuWidgets.CreateRect("FilterChips", parent);
            var layout = grid.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            RectTransform row = null;
            for (var i = 0; i < MenuViewBuilder.FilterChips.Length; i++)
            {
                if (i % ChipsPerRow == 0)
                {
                    row = MenuWidgets.CreateRect("ChipRow", grid);
                    var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                    rowLayout.spacing = 6f;
                    rowLayout.childControlWidth = true;
                    rowLayout.childForceExpandWidth = false;
                    rowLayout.childControlHeight = true;
                    rowLayout.childForceExpandHeight = false;
                }

                var id = MenuViewBuilder.FilterChips[i];
                var button = MenuWidgets.CreateButton(row, "", 0f, out var label);
                var element = MenuWidgets.Flexible(button.gameObject, 1f);
                element.minHeight = ChipHeight;
                element.preferredHeight = ChipHeight;
                label.Text.fontSize = 14f;
                Wire(button, () => SetFilter(id), MenuState.TabTooltip(MenuState.TabIndex(id)));
                _chips.Add(new Chip(id, button, label));
            }
        }

        // REFRESH lives in the status strip beside the counts it updates, so the footer has BACK and the key hints only.
        private static void CreateFooter(Transform parent)
        {
            var footer = Band(parent, "Footer", 84f, false);
            _back = MenuWidgets.CreateButton(footer, "BACK", 160f, out _);
            Wire(_back, () => Guard(NavigateBack), "BACK - Return to the previous menu.");
            _hints = MenuLabel.Create(footer, "KeyHints", 14f, MenuWidgets.MutedColor, false, false);
            _hints.Text.alignment = TextAlignmentOptions.MidlineRight;
            MenuWidgets.Flexible(_hints.Text.gameObject, 1f);
        }

        private static void CreatePrompt(Transform parent)
        {
            var blocker = MenuWidgets.CreateImage("UnsavedPrompt", parent, new Color(0f, 0f, 0f, 0.55f));
            MenuWidgets.Stretch(blocker.rectTransform, 0f, 0f, 0f, 0f);
            // Holds clicks off the menu underneath until the prompt is answered.
            blocker.raycastTarget = true;
            _prompt = blocker.gameObject;

            // A dark band across the screen, as the front end's dialogs have.
            var band = MenuWidgets.CreateImage("Band", blocker.transform, MenuKit.Hex(0x050606, 0.94f));
            band.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            band.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            band.rectTransform.sizeDelta = new Vector2(0f, 260f);
            var dialog = MenuWidgets.CreateImage("Dialog", blocker.transform, Color.clear);
            var rect = dialog.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(680f, 230f);
            var stack = dialog.gameObject.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(0, 0, 24, 24);
            stack.spacing = 12f;
            stack.childControlWidth = true;
            stack.childForceExpandWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandHeight = false;

            var title = MenuLabel.Create(dialog.transform, "Title", 24f, MenuWidgets.TextColor, true, false);
            title.Set("UNSAVED CHANGES");
            _promptMessage = MenuLabel.Create(dialog.transform, "Message", 16f, MenuWidgets.MutedColor, false, true, 3);
            _promptMessage.Text.richText = false;
            _promptMessage.Text.alignment = TextAlignmentOptions.TopLeft;
            Grow(_promptMessage.Text.rectTransform);

            var buttons = Band(dialog.transform, "Buttons", MenuWidgets.ControlHeight, false);
            buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
            _promptSave = MenuWidgets.CreateButton(buttons, "SAVE", 150f, out _);
            Wire(_promptSave, () => ResolvePrompt(PromptChoice.Save), "SAVE - Write the config edits to disk, then continue.");
            var discard = MenuWidgets.CreateButton(buttons, "DISCARD", 150f, out _);
            Wire(discard, () => ResolvePrompt(PromptChoice.Discard), "DISCARD - Drop the config edits, then continue.");
            var cancel = MenuWidgets.CreateButton(buttons, "CANCEL", 150f, out _);
            Wire(cancel, () => ResolvePrompt(PromptChoice.Cancel), "CANCEL - Stay in the config editor and keep the edits.");

            // Arrow keys stay on the three answers instead of reaching the rows behind the prompt.
            Link(_promptSave, null, null, null, discard);
            Link(discard, null, null, _promptSave, cancel);
            Link(cancel, null, null, discard, null);
            _prompt.SetActive(false);
        }

        private static TMP_InputField CreateRawEditor(Transform parent)
        {
            var input = MenuWidgets.CreateInput(parent, true, "Raw config text");
            input.gameObject.name = "RawTextEditor";
            input.richText = false;
            input.textComponent.richText = false;
            MenuWidgets.Stretch(input.textViewport, 10f, 8f, 26f, 8f);

            var track = MenuWidgets.CreateImage("Scrollbar", input.transform, MenuScroll.TrackColor);
            track.raycastTarget = true;
            var rect = track.rectTransform;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(-14f, 8f);
            rect.offsetMax = new Vector2(-8f, -8f);
            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            MenuWidgets.NoNavigation(scrollbar);
            var handle = MenuWidgets.CreateImage("Handle", track.transform, MenuScroll.HandleColor);
            handle.raycastTarget = true;
            MenuWidgets.Stretch(handle.rectTransform, 0f, 0f, 0f, 0f);
            scrollbar.handleRect = handle.rectTransform;
            scrollbar.targetGraphic = handle;
            input.verticalScrollbar = scrollbar;
            input.gameObject.SetActive(false);
            return input;
        }

        /// <summary>A fixed-height horizontal strip in the panel or a pane; a backed one is set off by a hairline.</summary>
        internal static RectTransform Band(Transform parent, string name, float height, bool backed)
        {
            var rect = MenuWidgets.CreateRect(name, parent);
            if (backed) MenuWidgets.Underline(rect);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = backed ? new RectOffset(4, 0, 0, 0) : new RectOffset();
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            return rect;
        }

        /// <summary>A column of the body; a backed one is set off from the next by a hairline down its right side.</summary>
        internal static RectTransform Pane(Transform parent, string name, bool backed)
        {
            var rect = MenuWidgets.CreateRect(name, parent);
            if (backed)
            {
                var rule = MenuWidgets.CreateImage("Rule", rect, MenuWidgets.RuleColor);
                rule.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                rule.rectTransform.anchorMin = new Vector2(1f, 0f);
                rule.rectTransform.anchorMax = Vector2.one;
                rule.rectTransform.pivot = new Vector2(1f, 0.5f);
                rule.rectTransform.sizeDelta = new Vector2(1f, 0f);
                rule.rectTransform.anchoredPosition = Vector2.zero;
            }
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = backed ? new RectOffset(0, 16, 12, 12) : new RectOffset();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            return rect;
        }

        internal static void Grow(RectTransform rect)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = 0f;
            element.flexibleHeight = 1f;
        }

        private static void Wire(Button button, Action action, string tooltip)
        {
            button.onClick.AddListener((UnityAction)new Action(action));
            _chromeTooltips[button.gameObject.GetInstanceID()] = tooltip;
        }

        private static void RequireEventSystem()
        {
            if (EventSystemLease.Acquire() == null)
                throw new InvalidOperationException("The game's UI input system is not ready.");
        }

        private static void CaptureSelection()
        {
            if (_visible)
                return;
            _ownerSceneHandle = SceneManager.GetActiveScene().handle;
            _returnSelection = EventSystem.current.currentSelectedGameObject;
        }

        private static void RestoreSelection()
        {
            var system = EventSystem.current;
            var previous = _returnSelection;
            _returnSelection = null;
            if (system == null || previous == null || !previous.activeInHierarchy)
                return;
            var selected = system.currentSelectedGameObject;
            if (selected == null || (_root != null && selected.transform.IsChildOf(_root.transform)))
                system.SetSelectedGameObject(previous);
        }

        private static void ReleaseView()
        {
            _created = false;
            _navScroll?.Release();
            _pageScroll?.Release();
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _panel = null;
            _navElement = null;
            _panelInset = -1f;
            _breadcrumb = null;
            _statusLine = null;
            _hints = null;
            _paneStatus = null;
            _paneFooter = null;
            _paneFooterShown = null;
            _unsaved = null;
            _prompt = null;
            _promptMessage = null;
            _promptSave = null;
            _pendingLeave = null;
            _promptReturn = null;
            _search = null;
            _navScroll = null;
            _pageScroll = null;
            _nav = null;
            _page = null;
            _configButtons = null;
            _configStops = Array.Empty<Selectable>();
            _friendlyButton = null;
            _textButton = null;
            _jumpBar = null;
            _jumpShown = null;
            _jumpCount = 0;
            _statusRefresh = null;
            _back = null;
            _rawEditor = null;
            _rawElement = null;
            _rawShown = false;
            _configShown = null;
            _friendlyShown = null;
            _currentPage = null;
            _renderedPageKey = null;
            _lastSelected = null;
            _selectedInput = null;
            _chips.Clear();
            _jumpButtons.Clear();
            _jumpSections.Clear();
            _links.Clear();
            _stops.Clear();
            _rowStops.Clear();
            _chromeTooltips.Clear();
            _friendlyRows.Clear();
        }

        private enum PromptChoice
        {
            Save,
            Discard,
            Cancel
        }

        private sealed class Chip
        {
            internal Chip(string id, Button button, MenuLabel label)
            {
                Id = id;
                Button = button;
                Label = label;
            }

            internal string Id { get; }
            internal Button Button { get; }
            internal MenuLabel Label { get; }
            internal bool? Active { get; set; }
        }

        private sealed class JumpButton
        {
            internal JumpButton(Button button, MenuLabel label)
            {
                Button = button;
                Label = label;
            }

            internal Button Button { get; }
            internal MenuLabel Label { get; }
            internal string SectionKey { get; set; }
            internal bool Active { get; set; } = true;
        }

        /// <summary>A control's navigation targets, compared by reference so the check needs no interop.</summary>
        private readonly struct NavLinks
        {
            private readonly Selectable _up;
            private readonly Selectable _down;
            private readonly Selectable _left;
            private readonly Selectable _right;

            internal NavLinks(Selectable up, Selectable down, Selectable left, Selectable right)
            {
                _up = up;
                _down = down;
                _left = left;
                _right = right;
            }

            internal bool Same(NavLinks other)
            {
                return ReferenceEquals(_up, other._up) && ReferenceEquals(_down, other._down)
                    && ReferenceEquals(_left, other._left) && ReferenceEquals(_right, other._right);
            }
        }

        /// <summary>A page row's run of controls in the flat stop list, and the one vertical moves land on.</summary>
        private readonly struct RowStops
        {
            internal RowStops(int start, int count, int landing, bool horizontal)
            {
                Start = start;
                Count = count;
                Landing = landing;
                Horizontal = horizontal;
            }

            internal int Start { get; }
            internal int Count { get; }
            internal int Landing { get; }
            internal bool Horizontal { get; }
        }

        /// <summary>Pooled nav rows keyed by nav item, so a filter or search change rewrites only the text that changed.</summary>
        private sealed class NavView
        {
            private readonly RectTransform _content;
            private readonly Transform _pool;
            private readonly Action<NavRow> _click;
            private readonly Stack<NavRow> _freeGroups = new Stack<NavRow>();
            private readonly Stack<NavRow> _freeItems = new Stack<NavRow>();
            private readonly Dictionary<int, NavRow> _byRoot = new Dictionary<int, NavRow>();
            private readonly List<MenuNavItem> _flat = new List<MenuNavItem>();
            private readonly List<NavRow> _rows = new List<NavRow>();
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
            private Dictionary<string, NavRow> _bound = new Dictionary<string, NavRow>(StringComparer.Ordinal);
            private Dictionary<string, NavRow> _next = new Dictionary<string, NavRow>(StringComparer.Ordinal);

            internal NavView(RectTransform content, Action<NavRow> click)
            {
                _content = content;
                _click = click;
                var pool = new GameObject("Nav Pool");
                pool.SetActive(false);
                pool.transform.SetParent(content.parent, false);
                _pool = pool.transform;
            }

            internal void Bind(IReadOnlyList<MenuNavItem> items, string selectedKey)
            {
                _flat.Clear();
                _seen.Clear();
                Flatten(items);

                _next.Clear();
                foreach (var item in _flat)
                {
                    if (_bound.Remove(item.Key, out var kept))
                    {
                        _next[item.Key] = kept;
                    }
                }

                var changed = _bound.Count > 0;
                foreach (var row in _bound.Values)
                {
                    Release(row);
                }

                _bound.Clear();
                (_bound, _next) = (_next, _bound);
                _rows.Clear();
                for (var i = 0; i < _flat.Count; i++)
                {
                    var item = _flat[i];
                    if (!_bound.TryGetValue(item.Key, out var row))
                    {
                        row = Acquire(item.Kind == MenuNavKind.Group);
                        _bound[item.Key] = row;
                        changed = true;
                    }

                    _rows.Add(row);

                    if (row.Root.GetSiblingIndex() != i)
                    {
                        row.Root.SetSiblingIndex(i);
                        changed = true;
                    }

                    changed |= row.Bind(item, string.Equals(item.Target, selectedKey, StringComparison.OrdinalIgnoreCase));
                }

                if (changed)
                {
                    LayoutRebuilder.MarkLayoutForRebuild(_content);
                }
            }

            /// <summary>The bound rows, top to bottom.</summary>
            internal IReadOnlyList<NavRow> Rows => _rows;

            internal NavRow FindKey(string key)
            {
                return key != null && _bound.TryGetValue(key, out var row) ? row : null;
            }

            internal NavRow Find(GameObject target)
            {
                for (var transform = target != null ? target.transform : null; transform != null && transform != _content; transform = transform.parent)
                {
                    if (_byRoot.TryGetValue(transform.gameObject.GetInstanceID(), out var row) && row.Item != null)
                    {
                        return row;
                    }
                }

                return null;
            }

            private void Flatten(IReadOnlyList<MenuNavItem> items)
            {
                foreach (var item in items)
                {
                    if (item == null || item.Key == null || !_seen.Add(item.Key))
                    {
                        continue;
                    }

                    _flat.Add(item);
                    if (!item.Collapsed)
                    {
                        Flatten(item.Children);
                    }
                }
            }

            private NavRow Acquire(bool group)
            {
                var free = group ? _freeGroups : _freeItems;
                if (free.Count > 0)
                {
                    var row = free.Pop();
                    row.Root.SetParent(_content, false);
                    row.Root.gameObject.SetActive(true);
                    return row;
                }

                var created = new NavRow(_content, group, _click);
                _byRoot[created.Root.gameObject.GetInstanceID()] = created;
                return created;
            }

            private void Release(NavRow row)
            {
                row.Item = null;
                row.Root.gameObject.SetActive(false);
                row.Root.SetParent(_pool, false);
                (row.Group ? _freeGroups : _freeItems).Push(row);
            }
        }

        /// <summary>One nav entry: status dot, name, version or count, and a fold marker for entries with children.</summary>
        private sealed class NavRow
        {
            private const float Height = 40f;
            private const int Indent = 20;
            private readonly Image _backing, _edge;
            private readonly HorizontalLayoutGroup _layout;
            private readonly MenuDot _dot;
            private readonly MenuLabel _label;
            private readonly MenuLabel _detail;
            private readonly MenuLabel _chevron;
            private int _depth = -1;
            private bool? _selected;

            internal NavRow(Transform parent, bool group, Action<NavRow> click)
            {
                Group = group;
                // The focus fill shows faintly at rest; the open page is marked by a bright edge, as focus is elsewhere.
                _backing = MenuWidgets.CreateImage(group ? "NavGroup" : "NavRow", parent, MenuWidgets.RowColor);
                Root = _backing.rectTransform;
                _edge = MenuWidgets.CreateImage("Edge", Root, MenuWidgets.TextColor);
                _edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                _edge.rectTransform.anchorMin = Vector2.zero;
                _edge.rectTransform.anchorMax = new Vector2(0f, 1f);
                _edge.rectTransform.pivot = new Vector2(0f, 0.5f);
                _edge.rectTransform.sizeDelta = new Vector2(3f, 0f);
                _edge.rectTransform.anchoredPosition = Vector2.zero;
                _edge.enabled = false;
                _layout = Root.gameObject.AddComponent<HorizontalLayoutGroup>();
                _layout.spacing = 8f;
                _layout.childAlignment = TextAnchor.MiddleLeft;
                _layout.childControlWidth = true;
                _layout.childForceExpandWidth = false;
                _layout.childControlHeight = true;
                _layout.childForceExpandHeight = false;
                var element = Root.gameObject.AddComponent<LayoutElement>();
                element.minHeight = Height;
                element.preferredHeight = Height;
                _dot = MenuDot.Create(Root);
                _label = MenuLabel.Create(Root, "Label", group ? 14f : 16f, group ? MenuWidgets.HeaderColor : MenuWidgets.TextColor, group, false);
                _label.Text.richText = false;
                MenuWidgets.Flexible(_label.Text.gameObject, 1f);
                _detail = MenuLabel.Create(Root, "Detail", 13f, MenuWidgets.MutedColor, false, false);
                _detail.Text.richText = false;
                _detail.Text.alignment = TextAlignmentOptions.MidlineRight;
                MenuWidgets.Fixed(_detail.Text.gameObject, 90f, 0f);
                _chevron = MenuLabel.Create(Root, "Chevron", 15f, MenuWidgets.HeaderColor, true, false);
                _chevron.Text.alignment = TextAlignmentOptions.Center;
                MenuWidgets.Fixed(_chevron.Text.gameObject, 24f, 0f);
                Button = MenuWidgets.MakeButton(_backing, group ? 0f : 0.2f);
                Button.onClick.AddListener((UnityAction)new Action(() =>
                {
                    if (Item != null)
                    {
                        click(this);
                    }
                }));
            }

            internal RectTransform Root { get; }
            internal Button Button { get; }
            internal bool Group { get; }
            internal MenuNavItem Item { get; set; }

            internal bool Bind(MenuNavItem item, bool selected)
            {
                Item = item;
                var changed = _label.Set(Group ? (item.Label ?? "").ToUpperInvariant() : item.Label);
                changed |= _detail.Set(item.Detail);
                changed |= _dot.Set(item.Tone, item.Kind == MenuNavKind.Mod || item.Tone == MenuTone.Warn || item.Tone == MenuTone.Bad);
                changed |= _chevron.Set(item.Children.Count == 0 ? "" : item.Collapsed ? "+" : "-");
                if (_depth != item.Depth)
                {
                    _depth = item.Depth;
                    _layout.padding = new RectOffset(12 + item.Depth * Indent, 10, 0, 0);
                    changed = true;
                }

                if (_selected != selected)
                {
                    _selected = selected;
                    _edge.enabled = selected;
                    _label.SetColor(selected ? MenuWidgets.TextColor : Group ? MenuWidgets.MutedColor : MenuWidgets.HeaderColor);
                    changed = true;
                }

                return changed;
            }
        }
    }
}
