// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Transitional IMGUI fallback for the Modstoke Mods screen.

using System;
using System.Collections.Generic;
using System.IO;
using OpenACAI.Loader;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenAcaiModMenu
    {
        private const float PanelMarginX = 58f;
        private const float PanelMarginY = 26f;
        private const float ContentMarginX = 92f;
        private const int MaxConfigEditorBytes = 262144;
        private static bool _visible;
        private static object _liveControlsSession;
        private static Action _closeLiveControls;
        private static bool _openingLiveControls;
        private static bool _resumeAfterLiveControls;
        private static bool _liveControlsFromNative;
        private static int _liveControlsSceneHandle;
        private static int _liveControlsClosedFrame;
        private static Rect _windowRect;
        private static Vector2 _scroll;
        private static Vector2 _configScroll;
        private static GUIStyle _windowStyle;
        private static GUIStyle _titleStyle;
        private static GUIStyle _tabStyle;
        private static GUIStyle _tabInactiveStyle;
        private static GUIStyle _tabRedStyle;
        private static GUIStyle _tabGreenStyle;
        private static GUIStyle _tabBlueStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _metaStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _valueStyle;
        private static GUIStyle _mutedStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _rowStyle;
        private static GUIStyle _framelessButtonStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _buttonActiveStyle;
        private static GUIStyle _statusReadyStyle;
        private static GUIStyle _statusHostedStyle;
        private static GUIStyle _statusWarningStyle;
        private static GUIStyle _statusMissingStyle;
        private static GUIStyle _statusDisabledStyle;
        private static Texture2D _windowTex;
        private static Texture2D _headerTex;
        private static Texture2D _panelTex;
        private static Texture2D _rowTex;
        private static Texture2D _lineTex;
        private static Texture2D _buttonTex;
        private static Texture2D _buttonHoverTex;
        private static Texture2D _buttonSelectedTex;
        private static readonly Dictionary<string, string> _providerDraftValues =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static void SetInventory(ModInventoryState inventory)
        {
            OpenAcaiModMenuState.SetInventory(inventory);
        }

        internal static void Show()
        {
            if (_openingLiveControls)
            {
                return;
            }

            if (_closeLiveControls != null)
            {
                _closeLiveControls();
                return;
            }

            var fallbackMode = OpenAcaiModMenuDispatcher.FallbackMode;
            try
            {
                OpenACAIEndnightRuntime.RefreshModInventory();
            }
            catch (Exception ex)
            {
                OpenAcaiModMenuState.SetConfigStatus("Refresh failed: " + ex.Message);
                fallbackMode = OpenAcaiModMenuDispatcher.RefreshFailedMode;
            }

            OpenAcaiModMenuDispatcher.Show(OpenAcaiModMenuState.Inventory, ShowFallback, HideFallbackForNative, fallbackMode);
        }

        internal static void Hide()
        {
            OpenAcaiModMenuDispatcher.Hide(_visible, HideFallback);
        }

        internal static void Toggle()
        {
            if (OpenAcaiModMenuDispatcher.IsVisible(_visible))
            {
                Hide();
            }
            else
            {
                Show();
            }
        }

        internal static void Update()
        {
            if (OpenAcaiModActions.Update())
            {
                if (_closeLiveControls != null && OpenACAIInput.IsPressed(OpenACAIKeybindIds.ToggleModsMenu))
                {
                    _closeLiveControls();
                }

                return;
            }

            if (_resumeAfterLiveControls)
            {
                if (Time.frameCount == _liveControlsClosedFrame)
                {
                    return;
                }

                _resumeAfterLiveControls = false;
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != _liveControlsSceneHandle)
                {
                    return;
                }

                if (_liveControlsFromNative)
                {
                    OpenAcaiNativeModMenu.ResumeConfig();
                }
                else
                {
                    ShowFallback();
                    OpenACAIUi.MarkFallback(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "config", "Mod config view is visible.");
                    OpenACAIUi.MarkFallback(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "config", "Mod config editor is visible.");
                }

                return;
            }

            if (_closeLiveControls != null)
            {
                if (OpenACAIInput.IsPressed(OpenACAIKeybindIds.ToggleModsMenu))
                {
                    _closeLiveControls();
                }

                return;
            }

            OpenAcaiModMenuDispatcher.UpdateNative();

            if (OpenACAIInput.IsPressed(OpenACAIKeybindIds.ToggleModsMenu))
            {
                Toggle();
            }

            if (_visible && OpenACAIInput.IsPressed(OpenACAIKeybindIds.Back))
            {
                if (OpenAcaiModMenuState.IsProviderOpen)
                {
                    OpenAcaiModMenuState.CloseProvider();
                    _scroll = Vector2.zero;
                }
                else if (OpenAcaiModMenuState.IsConfigOpen)
                {
                    CloseConfig();
                }
                else
                {
                    Hide();
                }
            }
        }

        internal static void OnGUI()
        {
            if (!OpenAcaiModMenuDispatcher.ShouldDrawFallback(_visible))
            {
                return;
            }

            EnsureStyles();
            _windowRect = new Rect(0f, 0f, Screen.width, Screen.height);
            DrawFullScreen();
        }

        internal static void OpenLiveSettings()
        {
            OpenLiveControls("Mod Settings", (entry, onClosed) => SonsGameManager.ModSettingsUi.TryOpen(entry.Id, onClosed),
                SonsGameManager.ModSettingsUi.Close);
        }

        internal static void OpenModActions()
        {
            OpenLiveControls("Mod Actions", OpenAcaiModActions.TryOpen, OpenAcaiModActions.Close);
        }

        private static void OpenLiveControls(string title, Func<ModInventoryEntry, Action<bool>, bool> tryOpen, Action close)
        {
            var entry = OpenAcaiModMenuState.ConfigEntry;
            if (entry == null || _liveControlsSession != null || _openingLiveControls)
            {
                return;
            }

            if (!string.Equals(OpenAcaiModMenuState.ConfigText, OpenAcaiModMenuState.ConfigOriginalText, StringComparison.Ordinal))
            {
                OpenAcaiModMenuState.SetConfigStatus("Save or revert unsaved file edits before opening live controls.");
                return;
            }

            var session = new object();
            var opened = false;
            _openingLiveControls = true;
            _liveControlsSession = session;
            _closeLiveControls = close;
            _resumeAfterLiveControls = false;
            try
            {
                var friendlyMode = OpenAcaiModMenuState.FriendlyMode;
                _liveControlsFromNative = OpenAcaiNativeModMenu.IsVisible;
                _liveControlsSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if (!tryOpen(entry, returnToOwner =>
                {
                    if (!ReferenceEquals(_liveControlsSession, session))
                    {
                        return;
                    }

                    _liveControlsSession = null;
                    _closeLiveControls = null;
                    _resumeAfterLiveControls = returnToOwner && opened;
                    _liveControlsClosedFrame = Time.frameCount;
                    OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModConfig, title, "sonssdk", "closed", title + " are closed.");
                    if (_resumeAfterLiveControls)
                    {
                        OpenAcaiModMenuState.OpenConfig(entry, MaxConfigEditorBytes);
                        OpenAcaiModMenuState.SetFriendlyMode(friendlyMode);
                    }
                }))
                {
                    _liveControlsSession = null;
                    _closeLiveControls = null;
                    OpenAcaiModMenuState.SetConfigStatus(title + " are not available for this mod.");
                    return;
                }

                if (!ReferenceEquals(_liveControlsSession, session))
                {
                    return;
                }

                Hide();
                OpenACAIUi.MarkVisible(OpenACAIUiSurfaceIds.ModConfig, title, "sonssdk", "live:" + entry.Id, title + " are visible.");
                opened = true;
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(_liveControlsSession, session))
                {
                    _liveControlsSession = null;
                    _closeLiveControls = null;
                    try
                    {
                        close();
                    }
                    catch
                    {
                        // Preserve the opening failure if panel cleanup also fails.
                    }
                }

                OpenAcaiModMenuState.SetConfigStatus("Could not open " + title + ": " + exception.Message);
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Could not open " + title + ": " + exception);
            }
            finally
            {
                _openingLiveControls = false;
            }
        }

        private static void ShowFallback()
        {
            _visible = true;
            OpenACAIUi.MarkFallback(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "list", "Fallback IMGUI Modstoke mods menu is visible.");
        }

        private static void HideFallbackForNative()
        {
            _visible = false;
            OpenAcaiModMenuState.CloseProvider();
        }

        private static void HideFallback()
        {
            _visible = false;
            OpenAcaiModMenuState.CloseProvider();
            OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "imgui-fallback", "closed", "No mod config is open.");
            OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "imgui-fallback", "closed", "Modstoke mods menu is not visible.");
        }

        private static void DrawFullScreen()
        {
            DrawSolid(_windowRect, _windowTex);
            var panel = new Rect(PanelMarginX, PanelMarginY, _windowRect.width - (PanelMarginX * 2f), _windowRect.height - (PanelMarginY * 2f));
            DrawSolid(panel, _panelTex);

            DrawTabs();

            if (OpenAcaiModMenuState.IsConfigOpen)
            {
                DrawConfigEditor();
            }
            else if (OpenAcaiModMenuState.IsProviderOpen)
            {
                DrawProviderContent();
            }
            else if (OpenAcaiModMenuState.IsExtensionsTab)
            {
                DrawExtensions();
            }
            else if (OpenAcaiModMenuState.IsCreditsTab)
            {
                DrawCredits();
            }
            else
            {
                DrawEntries();
            }

            DrawFooterActions();
        }

        private static void DrawTabs()
        {
            var tabY = PanelMarginY + 52f;
            var tabWidth = 190f;
            var tabCount = OpenAcaiModMenuState.TabCount;
            var startX = Math.Max(ContentMarginX, (_windowRect.width - (tabWidth * tabCount)) * 0.5f);

            for (var i = 0; i < tabCount; i++)
            {
                var rect = new Rect(startX + (i * tabWidth), tabY, tabWidth, 42f);
                if (GUI.Button(rect, GUIContent.none, _framelessButtonStyle))
                {
                    if (OpenAcaiModMenuState.SelectTab(i))
                    {
                        _scroll = Vector2.zero;
                    }
                }

                if (i == OpenAcaiModMenuState.ActiveTab)
                {
                    DrawChromaticLabel(rect, OpenAcaiModMenuState.TabLabel(i).ToUpperInvariant(), _tabStyle);
                }
                else
                {
                    GUI.Label(rect, OpenAcaiModMenuState.TabLabel(i).ToUpperInvariant(), _tabInactiveStyle);
                }
            }
        }

        private static void DrawEntries()
        {
            var entries = FilterEntries();
            var left = ContentMarginX;
            var top = PanelMarginY + 128f;
            var width = _windowRect.width - (ContentMarginX * 2f);
            var height = _windowRect.height - top - 120f;

            GUI.Label(new Rect(left, top, 220f, 32f), OpenAcaiModMenuState.IsLibrariesTab ? "LIBRARIES" : "MODS", _sectionStyle);
            DrawSolid(new Rect(left + 185f, top + 15f, width - 185f, 1f), _lineTex);
            GUILayout.BeginArea(new Rect(left, top + 54f, width, height));
            GUILayout.BeginHorizontal();
            GUILayout.Label("NAME", _headerStyle, GUILayout.Width(270f));
            GUILayout.Label("STATUS", _headerStyle, GUILayout.Width(92f));
            GUILayout.Label("TYPE", _headerStyle, GUILayout.Width(116f));
            GUILayout.Label("AUTHOR", _headerStyle, GUILayout.Width(132f));
            GUILayout.Label("VERSION", _headerStyle, GUILayout.Width(92f));
            GUILayout.Label("DETAIL", _headerStyle);
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.Height(height - 30f));
            if (entries.Length == 0)
            {
                GUILayout.Label("No matching entries found.", _mutedStyle, GUILayout.Height(28f));
            }

            foreach (var entry in entries)
            {
                DrawEntry(entry);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawExtensions()
        {
            var providers = OpenAcaiModMenuState.GetExtensionProviders();
            var left = ContentMarginX;
            var top = PanelMarginY + 128f;
            var width = _windowRect.width - (ContentMarginX * 2f);
            var height = _windowRect.height - top - 120f;

            GUI.Label(new Rect(left, top, 260f, 32f), "EXTENSIONS", _sectionStyle);
            DrawSolid(new Rect(left + 230f, top + 15f, width - 230f, 1f), _lineTex);
            GUILayout.BeginArea(new Rect(left, top + 54f, width, height));
            GUILayout.BeginHorizontal();
            GUILayout.Label("NAME", _headerStyle, GUILayout.Width(230f));
            GUILayout.Label("KIND", _headerStyle, GUILayout.Width(120f));
            GUILayout.Label("SOURCE", _headerStyle, GUILayout.Width(180f));
            GUILayout.Label("AUTH", _headerStyle, GUILayout.Width(86f));
            GUILayout.Label("STATUS", _headerStyle, GUILayout.Width(90f));
            GUILayout.Label("DETAIL", _headerStyle);
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.Height(height - 30f));
            if (providers.Length == 0)
            {
                GUILayout.Label("No extension providers are registered yet.", _mutedStyle, GUILayout.Height(28f));
                GUILayout.Label("Mods and Modstoke companion tools can register settings, status, action, or server panels through OpenACAIMenu.", _mutedStyle, GUILayout.Height(28f));
            }

            foreach (var provider in providers)
            {
                DrawProvider(provider);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawProvider(OpenACAIMenuProviderSnapshot provider)
        {
            GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(34f));
            GUILayout.Label(Shorten(provider.DisplayName, 26), _labelStyle, GUILayout.Width(230f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(provider.Kind, 14), _mutedStyle, GUILayout.Width(120f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(provider.Source, 22), _mutedStyle, GUILayout.Width(180f), GUILayout.Height(26f));
            GUILayout.Label(provider.RequiresAuthorization ? "YES" : "NO", _mutedStyle, GUILayout.Width(86f), GUILayout.Height(26f));
            GUILayout.Label(provider.Enabled ? "READY" : "OFF", provider.Enabled ? _statusReadyStyle : _statusDisabledStyle, GUILayout.Width(90f), GUILayout.Height(26f));
            if (GUILayout.Button(provider.HasContent || provider.HasCommands ? "OPEN  >" : "DETAIL  >", _framelessButtonStyle, GUILayout.Width(150f), GUILayout.Height(26f)))
            {
                OpenAcaiModMenuState.OpenProvider(provider);
                _scroll = Vector2.zero;
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawProviderContent()
        {
            var provider = OpenAcaiModMenuState.ProviderEntry;
            var content = OpenAcaiModMenuState.ProviderContent;
            var commands = OpenAcaiModMenuState.ProviderCommands;
            var title = content?.Title ?? commands?.Title ?? provider?.DisplayName ?? "EXTENSION";
            var subtitle = content?.Subtitle ?? commands?.Subtitle ?? provider?.Description ?? "";
            var left = ContentMarginX;
            var top = PanelMarginY + 128f;
            var width = _windowRect.width - (ContentMarginX * 2f);
            var height = _windowRect.height - top - 120f;

            GUI.Label(new Rect(left, top, 360f, 32f), Shorten(title.ToUpperInvariant(), 34), _sectionStyle);
            DrawSolid(new Rect(left + 310f, top + 15f, width - 310f, 1f), _lineTex);
            GUILayout.BeginArea(new Rect(left, top + 54f, width, height));

            if (provider != null)
            {
                GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(34f));
                GUILayout.Label(Shorten(provider.DisplayName, 26), _labelStyle, GUILayout.Width(230f), GUILayout.Height(26f));
                GUILayout.Label(Shorten(provider.Kind, 14), _mutedStyle, GUILayout.Width(120f), GUILayout.Height(26f));
                GUILayout.Label(Shorten(provider.Source, 22), _mutedStyle, GUILayout.Width(180f), GUILayout.Height(26f));
                GUILayout.Label(provider.RequiresAuthorization ? "AUTH" : "OPEN", provider.RequiresAuthorization ? _statusWarningStyle : _statusReadyStyle, GUILayout.Width(86f), GUILayout.Height(26f));
                GUILayout.Label(provider.Enabled ? "READY" : "OFF", provider.Enabled ? _statusReadyStyle : _statusDisabledStyle, GUILayout.Width(90f), GUILayout.Height(26f));
                GUILayout.Label(ProviderSurfaceDetail(provider).ToUpperInvariant(), _mutedStyle, GUILayout.Height(26f));
                GUILayout.EndHorizontal();
            }

            if (!string.IsNullOrWhiteSpace(OpenAcaiModMenuState.ProviderStatus))
            {
                GUILayout.Label(OpenAcaiModMenuState.ProviderStatus, _metaStyle, GUILayout.Height(24f));
            }

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                GUILayout.Label(subtitle, _mutedStyle, GUILayout.Height(26f));
            }

            _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.Height(height - 76f));
            OpenACAIConfigProviderSnapshot configSnapshot;
            if (OpenAcaiModMenuState.TryGetProviderConfigSnapshot(out configSnapshot))
            {
                DrawProviderConfigRows(configSnapshot);
            }
            else if (commands != null)
            {
                DrawProviderCommandRows(commands);
            }
            else if (content == null || content.Sections.Count == 0)
            {
                GUILayout.Label("No provider detail rows are available yet.", _mutedStyle, GUILayout.Height(28f));
            }
            else
            {
                foreach (var section in content.Sections)
                {
                    GUILayout.Space(10f);
                    GUILayout.Label(section.Title.ToUpperInvariant(), _sectionStyle, GUILayout.Height(26f));
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("ITEM", _headerStyle, GUILayout.Width(260f));
                    GUILayout.Label("VALUE", _headerStyle, GUILayout.Width(220f));
                    GUILayout.Label("STATUS", _headerStyle, GUILayout.Width(100f));
                    GUILayout.Label("DETAIL", _headerStyle);
                    GUILayout.EndHorizontal();

                    if (section.Rows.Count == 0)
                    {
                        GUILayout.Label("No rows in this section.", _mutedStyle, GUILayout.Height(28f));
                        continue;
                    }

                    foreach (var item in section.Rows)
                    {
                        DrawProviderContentRow(item);
                    }
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawProviderContentRow(OpenACAIMenuRowSnapshot item)
        {
            GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(34f));
            GUILayout.Label(Shorten(item.Label, 30), _labelStyle, GUILayout.Width(260f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(item.Value, 28), _mutedStyle, GUILayout.Width(220f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(item.Status, 14), ProviderStatusStyle(item.Status), GUILayout.Width(100f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(item.Detail, 86), _mutedStyle, GUILayout.Height(26f));
            GUILayout.EndHorizontal();
        }

        private static void DrawProviderConfigRows(OpenACAIConfigProviderSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Settings.Count == 0)
            {
                GUILayout.Label("No structured settings are available.", _mutedStyle, GUILayout.Height(28f));
                return;
            }

            string currentSection = null;
            foreach (var setting in snapshot.Settings)
            {
                var section = string.IsNullOrWhiteSpace(setting.Section) ? "Settings" : setting.Section;
                if (!string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = section;
                    GUILayout.Space(10f);
                    GUILayout.Label(currentSection.ToUpperInvariant(), _sectionStyle, GUILayout.Height(26f));
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("SETTING", _headerStyle, GUILayout.Width(260f));
                    GUILayout.Label("VALUE", _headerStyle, GUILayout.Width(270f));
                    GUILayout.Label("STATE", _headerStyle, GUILayout.Width(100f));
                    GUILayout.Label("DETAIL", _headerStyle);
                    GUILayout.EndHorizontal();
                }

                DrawProviderConfigRow(setting);
            }
        }

        private static void DrawProviderConfigRow(OpenACAIConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return;
            }

            GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(34f));
            GUILayout.Label(Shorten(setting.DisplayName, 30), _labelStyle, GUILayout.Width(260f), GUILayout.Height(26f));
            if (!setting.CanEdit)
            {
                GUILayout.Label(Shorten(setting.Value, 28), _mutedStyle, GUILayout.Width(270f), GUILayout.Height(26f));
            }
            else if (string.Equals(setting.Kind, OpenACAIConfigValueKinds.Boolean, StringComparison.OrdinalIgnoreCase)
                || TryParseBoolean(setting.Value, out _))
            {
                TryParseBoolean(setting.Value, out var current);
                if (GUILayout.Button(BoolDisplay(current), current ? _buttonActiveStyle : _buttonStyle, GUILayout.Width(270f), GUILayout.Height(28f)))
                {
                    ApplyProviderConfigValue(setting, current ? "false" : "true");
                }
            }
            else if (string.Equals(setting.Kind, OpenACAIConfigValueKinds.Choice, StringComparison.OrdinalIgnoreCase)
                && setting.Options != null
                && setting.Options.Count > 0)
            {
                var next = NextChoiceValue(setting);
                if (GUILayout.Button(Shorten(string.IsNullOrWhiteSpace(setting.Value) ? next : setting.Value, 28), _buttonStyle, GUILayout.Width(270f), GUILayout.Height(28f)))
                {
                    ApplyProviderConfigValue(setting, next);
                }
            }
            else
            {
                var draftKey = ProviderDraftKey(setting);
                string draft;
                if (!_providerDraftValues.TryGetValue(draftKey, out draft))
                {
                    draft = setting.Value ?? "";
                }

                draft = GUILayout.TextField(draft, GUILayout.Width(188f), GUILayout.Height(28f));
                _providerDraftValues[draftKey] = draft;
                if (GUILayout.Button("APPLY", _buttonStyle, GUILayout.Width(76f), GUILayout.Height(28f)))
                {
                    ApplyProviderConfigValue(setting, draft);
                }
            }

            GUILayout.Label(ProviderSettingStatus(setting), ProviderStatusStyle(ProviderSettingStatus(setting)), GUILayout.Width(100f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(ProviderSettingDetail(setting), 86), _mutedStyle, GUILayout.Height(26f));
            GUILayout.EndHorizontal();
        }

        private static void DrawProviderCommandRows(OpenACAICommandProviderSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Commands.Count == 0)
            {
                GUILayout.Label("No commands are available.", _mutedStyle, GUILayout.Height(28f));
                return;
            }

            GUILayout.Space(10f);
            GUILayout.Label("COMMANDS", _sectionStyle, GUILayout.Height(26f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("COMMAND", _headerStyle, GUILayout.Width(260f));
            GUILayout.Label("STATUS", _headerStyle, GUILayout.Width(100f));
            GUILayout.Label("ACCESS", _headerStyle, GUILayout.Width(100f));
            GUILayout.Label("DETAIL", _headerStyle);
            GUILayout.Label("ACTION", _headerStyle, GUILayout.Width(86f));
            GUILayout.EndHorizontal();

            foreach (var command in snapshot.Commands)
            {
                DrawProviderCommandRow(command);
            }
        }

        private static void DrawProviderCommandRow(OpenACAICommandSnapshot command)
        {
            if (command == null)
            {
                return;
            }

            GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(34f));
            GUILayout.Label(Shorten(command.DisplayName, 30), _labelStyle, GUILayout.Width(260f), GUILayout.Height(26f));
            GUILayout.Label(ProviderCommandStatus(command), ProviderStatusStyle(ProviderCommandStatus(command)), GUILayout.Width(100f), GUILayout.Height(26f));
            GUILayout.Label(ProviderCommandAccess(command), ProviderStatusStyle(ProviderCommandAccess(command)), GUILayout.Width(100f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(command.Description, 76), _mutedStyle, GUILayout.Height(26f));
            if (command.Enabled && !command.RequiresConfirmation)
            {
                if (GUILayout.Button("RUN", _buttonStyle, GUILayout.Width(86f), GUILayout.Height(28f)))
                {
                    InvokeProviderCommand(command);
                }
            }
            else
            {
                GUILayout.Label(ProviderCommandActionText(command), _mutedStyle, GUILayout.Width(86f), GUILayout.Height(26f));
            }

            GUILayout.EndHorizontal();
        }

        private static void DrawCredits()
        {
            var left = ContentMarginX;
            var top = PanelMarginY + 128f;
            var width = _windowRect.width - (ContentMarginX * 2f);
            var height = _windowRect.height - top - 120f;

            GUI.Label(new Rect(left, top, 220f, 32f), "CREDITS", _sectionStyle);
            DrawSolid(new Rect(left + 185f, top + 15f, width - 185f, 1f), _lineTex);
            GUILayout.BeginArea(new Rect(left, top + 54f, width, height));
            GUILayout.BeginHorizontal();
            GUILayout.Label("PROJECT", _headerStyle, GUILayout.Width(250f));
            GUILayout.Label("CREDIT", _headerStyle, GUILayout.Width(230f));
            GUILayout.Label("ROLE", _headerStyle, GUILayout.Width(180f));
            GUILayout.Label("NOTES", _headerStyle);
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.Height(height - 30f));
            DrawCreditRow("Modstoke Loader", "Alex Cooper / OpenACAI Inc", "Developer", "by OpenACAI. Independent mod loader for Sons of the Forest.");
            DrawCreditRow("Modstoke SDK", "by OpenACAI", "Mod API", "Public modding APIs; existing OpenACAI namespaces remain compatible.");
            DrawCreditRow("Modstoke", "by OpenACAI", "Website / catalog", "Intended address: modstoke.com. Services are not confirmed live.");
            DrawCreditRow("OpenACAI", "Nonprofit publisher", "Publisher", "Not affiliated with or endorsed by Endnight Games.");
            DrawCreditRow("BepInEx", "BepInEx contributors", "Upstream loader", "Modstoke Loader builds on BepInEx IL2CPP runtime foundations.");
            DrawCreditRow("RedLoader", "Toni Macaroni", "Upstream SOTF loader", "RedLoader and SonsSdk behavior informed compatibility and menu support.");
            DrawCreditRow("SonsSdk", "Toni Macaroni and contributors", "Mod API", "SonsSdk APIs remain credited for RedLoader mod compatibility.");
            DrawCreditRow("Community Mods", "Mod authors", "Content", "Author names shown in the mod inventory come from mod manifests and assemblies.");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawCreditRow(string project, string credit, string role, string notes)
        {
            GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(34f));
            GUILayout.Label(Shorten(project, 28), _labelStyle, GUILayout.Width(250f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(credit, 28), _mutedStyle, GUILayout.Width(230f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(role, 22), _mutedStyle, GUILayout.Width(180f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(notes, 74), _mutedStyle, GUILayout.Height(26f));
            GUILayout.EndHorizontal();
        }

        private static void DrawEntry(ModInventoryEntry entry)
        {
            GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(34f));
            GUILayout.Label(Shorten(entry.DisplayName, 32), _labelStyle, GUILayout.Width(270f), GUILayout.Height(26f));
            GUILayout.Label(StatusText(entry), StatusStyle(entry), GUILayout.Width(92f), GUILayout.Height(26f));
            GUILayout.Label(TypeText(entry), _mutedStyle, GUILayout.Width(116f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(SourceText(entry), 20), _mutedStyle, GUILayout.Width(132f), GUILayout.Height(26f));
            GUILayout.Label(Shorten(entry.Version ?? entry.AssemblyVersion ?? "-", 12), _valueStyle, GUILayout.Width(92f), GUILayout.Height(26f));
            var detail = entry.HasControls ? "CONFIG  >" : DetailText(entry);
            if (entry.HasControls && GUILayout.Button(detail, _framelessButtonStyle, GUILayout.Width(150f), GUILayout.Height(26f)))
            {
                OpenConfig(entry);
            }
            else if (!entry.HasControls)
            {
                GUILayout.Label(Shorten(detail, 64), _mutedStyle, GUILayout.Height(26f));
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawFooterActions()
        {
            var y = _windowRect.height - PanelMarginY - 58f;
            var backRect = new Rect(ContentMarginX, y, 150f, 42f);
            if (GUI.Button(backRect, GUIContent.none, _framelessButtonStyle))
            {
                if (OpenAcaiModMenuState.IsProviderOpen)
                {
                    OpenAcaiModMenuState.CloseProvider();
                    _scroll = Vector2.zero;
                }
                else if (OpenAcaiModMenuState.IsConfigOpen)
                {
                    CloseConfig();
                }
                else
                {
                    Hide();
                }
            }

            GUI.Label(backRect, "BACK", _tabInactiveStyle);

            var refreshRect = new Rect(_windowRect.width - ContentMarginX - 190f, y, 190f, 42f);
            if (GUI.Button(refreshRect, GUIContent.none, _framelessButtonStyle))
            {
                try
                {
                    OpenACAIEndnightRuntime.RefreshModInventory();
                }
                catch (Exception ex)
                {
                    OpenAcaiModMenuState.SetConfigStatus("Refresh failed: " + ex.Message);
                }
            }

            GUI.Label(refreshRect, "REFRESH", _tabInactiveStyle);
        }

        private static void DrawChromaticLabel(Rect rect, string text, GUIStyle style)
        {
            var phase = Time.unscaledTime;
            var redOffset = -3.2f + Mathf.Sin(phase * 13.1f) * 0.45f;
            var greenOffset = -1.2f + Mathf.Sin(phase * 17.3f) * 0.35f;
            var blueOffset = 3.1f + Mathf.Sin(phase * 15.7f) * 0.45f;
            GUI.Label(new Rect(rect.x + redOffset, rect.y - 1.4f, rect.width, rect.height), text, _tabRedStyle);
            GUI.Label(new Rect(rect.x + greenOffset, rect.y + 2.2f, rect.width, rect.height), text, _tabGreenStyle);
            GUI.Label(new Rect(rect.x + blueOffset, rect.y + 1.3f, rect.width, rect.height), text, _tabBlueStyle);
            GUI.Label(rect, text, style);
        }

        private static void DrawConfigEditor()
        {
            var left = ContentMarginX;
            var top = PanelMarginY + 128f;
            var width = _windowRect.width - (ContentMarginX * 2f);
            GUI.Label(new Rect(left, top, 320f, 32f), "MOD CONFIG", _sectionStyle);
            DrawSolid(new Rect(left + 260f, top + 15f, width - 260f, 1f), _lineTex);
            var entry = OpenAcaiModMenuState.ConfigEntry;

            GUILayout.BeginArea(new Rect(left, top + 54f, width, _windowRect.height - top - 138f));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("BACK", _buttonStyle, GUILayout.Width(96f), GUILayout.Height(32f)))
            {
                CloseConfig();
            }

            GUILayout.Label(Shorten(entry?.DisplayName, 54), _labelStyle, GUILayout.Height(32f));
            GUILayout.FlexibleSpace();
            if (entry?.HasConfig == true)
            {
                GUILayout.Label((entry.ConfigFormat ?? "CFG") + "  " + (entry.ConfigSource ?? ""), _mutedStyle, GUILayout.Height(32f));
            }

            GUILayout.EndHorizontal();

            if (entry?.LiveSettingsAvailable == true || entry?.ModActionsAvailable == true)
            {
                GUILayout.BeginHorizontal();
                if (entry.LiveSettingsAvailable && GUILayout.Button("MOD SETTINGS", _buttonStyle, GUILayout.Width(190f), GUILayout.Height(32f)))
                {
                    OpenLiveSettings();
                }

                if (entry.ModActionsAvailable && GUILayout.Button("MOD ACTIONS", _buttonStyle, GUILayout.Width(190f), GUILayout.Height(32f)))
                {
                    OpenModActions();
                }

                GUILayout.EndHorizontal();
            }

            if (!string.IsNullOrWhiteSpace(OpenAcaiModMenuState.ConfigStatus))
            {
                GUILayout.Label(OpenAcaiModMenuState.ConfigStatus, _metaStyle, GUILayout.Height(24f));
            }

            if (entry?.HasConfig != true)
            {
                GUILayout.Space(24f);
                GUILayout.Label("No config file was detected for this mod yet.", _mutedStyle, GUILayout.Height(28f));
                GUILayout.Label("If the mod creates settings after first run, refresh this panel after the game has loaded it.", _mutedStyle, GUILayout.Height(28f));
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label(Shorten(entry.ConfigPath, 120), _mutedStyle, GUILayout.Height(22f));
            var liveSettingsHeight = entry.LiveSettingsAvailable || entry.ModActionsAvailable ? 36f : 0f;
            _configScroll = GUILayout.BeginScrollView(_configScroll, false, true, GUILayout.Height(_windowRect.height - top - 210f - liveSettingsHeight));
            var nextText = GUILayout.TextArea(OpenAcaiModMenuState.ConfigText ?? "", GUILayout.ExpandHeight(true));
            if (!string.Equals(nextText, OpenAcaiModMenuState.ConfigText, StringComparison.Ordinal))
            {
                OpenAcaiModMenuState.SetConfigText(nextText);
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", _buttonActiveStyle, GUILayout.Width(96f), GUILayout.Height(32f)))
            {
                SaveConfig();
            }

            if (GUILayout.Button("Revert", _buttonStyle, GUILayout.Width(96f), GUILayout.Height(32f)))
            {
                OpenAcaiModMenuState.RevertConfig();
            }

            GUILayout.Label("Most RedLoader and BepInEx config changes require a restart or mod reload.", _mutedStyle, GUILayout.Height(32f));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static void OpenConfig(ModInventoryEntry entry)
        {
            _configScroll = Vector2.zero;
            OpenAcaiModMenuState.OpenConfig(entry, MaxConfigEditorBytes);
            OpenACAIUi.MarkFallback(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "config", "Fallback IMGUI Modstoke mods config view is visible.");
            OpenACAIUi.MarkFallback(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "config:" + (entry?.DisplayName ?? "none"), OpenAcaiModMenuState.ConfigStatus ?? "Config editor opened.");
        }

        private static void SaveConfig()
        {
            OpenAcaiModMenuState.SaveConfig(() => OpenACAIEndnightRuntime.RefreshModInventory(), out _);
        }

        private static void CloseConfig()
        {
            OpenAcaiModMenuState.CloseConfig();
            _configScroll = Vector2.zero;
            OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "imgui-fallback", "closed", "No mod config is open.");
            OpenACAIUi.MarkFallback(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "list", "Fallback IMGUI Modstoke mods list is visible.");
        }

        private static ModInventoryEntry[] FilterEntries()
        {
            return OpenAcaiModMenuState.GetFilteredEntries();
        }

        private static string BuildMetaLine()
        {
            return OpenAcaiModMenuState.BuildMetaLine(OpenACAIEndnightRuntime.Version);
        }

        private static void DrawStat(string label, int value)
        {
            GUILayout.BeginVertical(GUILayout.Width(118f));
            GUILayout.Label(label, _metaStyle, GUILayout.Height(18f));
            GUILayout.Label(value.ToString(), _labelStyle, GUILayout.Height(26f));
            GUILayout.EndVertical();
        }

        private static string StatusText(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return "-";
            }

            if (string.Equals(entry.Status, "hosted", StringComparison.OrdinalIgnoreCase))
            {
                return "HOSTED";
            }

            if (string.Equals(entry.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return "READY";
            }

            if (string.Equals(entry.Status, "missing", StringComparison.OrdinalIgnoreCase))
            {
                return "MISSING";
            }

            if (string.Equals(entry.Status, "warning", StringComparison.OrdinalIgnoreCase))
            {
                return "CHECK";
            }

            if (string.Equals(entry.Status, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                return "OFF";
            }

            return "INFO";
        }

        private static GUIStyle StatusStyle(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return _statusDisabledStyle;
            }

            if (string.Equals(entry.Status, "hosted", StringComparison.OrdinalIgnoreCase))
            {
                return _statusHostedStyle;
            }

            if (string.Equals(entry.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return _statusReadyStyle;
            }

            if (string.Equals(entry.Status, "missing", StringComparison.OrdinalIgnoreCase))
            {
                return _statusMissingStyle;
            }

            if (string.Equals(entry.Status, "warning", StringComparison.OrdinalIgnoreCase))
            {
                return _statusWarningStyle;
            }

            return _statusDisabledStyle;
        }

        private static GUIStyle ProviderStatusStyle(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return _mutedStyle;
            }

            var value = status.Trim();
            if (ContainsAny(value, "ready", "ok", "enabled", "running", "active", "loaded", "hosted", "connected", "online"))
            {
                return _statusReadyStyle;
            }

            if (ContainsAny(value, "missing", "error", "failed", "fail", "off", "disabled", "blocked"))
            {
                return _statusMissingStyle;
            }

            if (ContainsAny(value, "check", "warn", "pending", "auth", "required"))
            {
                return _statusWarningStyle;
            }

            return _mutedStyle;
        }

        private static void ApplyProviderConfigValue(OpenACAIConfigValueSnapshot setting, string value)
        {
            if (setting == null)
            {
                return;
            }

            string tooltip;
            OpenAcaiModMenuState.TryApplyProviderConfigValue(setting.Key, value, out tooltip);
            _providerDraftValues.Remove(ProviderDraftKey(setting));
        }

        private static void InvokeProviderCommand(OpenACAICommandSnapshot command)
        {
            if (command == null || !command.Enabled || command.RequiresConfirmation)
            {
                return;
            }

            string tooltip;
            OpenAcaiModMenuState.TryInvokeProviderCommand(command.Id, out tooltip);
        }

        private static string ProviderSurfaceDetail(OpenACAIMenuProviderSnapshot provider)
        {
            if (provider == null)
            {
                return "metadata only";
            }

            if (provider.HasContent && provider.HasCommands)
            {
                return "structured content and commands";
            }

            if (provider.HasCommands)
            {
                return "command provider";
            }

            return provider.HasContent ? "structured content" : "metadata only";
        }

        private static string ProviderCommandStatus(OpenACAICommandSnapshot command)
        {
            if (command == null)
            {
                return "";
            }

            if (!command.Enabled)
            {
                return "OFF";
            }

            return string.IsNullOrWhiteSpace(command.Status) ? "READY" : command.Status.Trim().ToUpperInvariant();
        }

        private static string ProviderCommandAccess(OpenACAICommandSnapshot command)
        {
            if (command == null)
            {
                return "";
            }

            if (!command.Enabled)
            {
                return "DISABLED";
            }

            return command.RequiresConfirmation ? "CONFIRM" : "OPEN";
        }

        private static string ProviderCommandActionText(OpenACAICommandSnapshot command)
        {
            if (command == null)
            {
                return "";
            }

            if (!command.Enabled)
            {
                return "DISABLED";
            }

            return command.RequiresConfirmation ? "CONFIRM" : "RUN";
        }

        private static string ProviderDraftKey(OpenACAIConfigValueSnapshot setting)
        {
            var provider = OpenAcaiModMenuState.ProviderEntry;
            return (provider?.Id ?? "provider") + "::" + (setting?.Key ?? "setting");
        }

        private static string ProviderSettingStatus(OpenACAIConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return "";
            }

            if (!setting.CanEdit)
            {
                return "READONLY";
            }

            return setting.RequiresRestart ? "RESTART" : "READY";
        }

        private static string ProviderSettingDetail(OpenACAIConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return "";
            }

            var kind = string.IsNullOrWhiteSpace(setting.Kind) ? "text" : setting.Kind;
            if (setting.RequiresRestart)
            {
                return kind + "; restart required";
            }

            return string.IsNullOrWhiteSpace(setting.Tooltip) ? kind : setting.Tooltip;
        }

        private static string NextChoiceValue(OpenACAIConfigValueSnapshot setting)
        {
            if (setting?.Options == null || setting.Options.Count == 0)
            {
                return setting?.Value ?? "";
            }

            var first = null as string;
            var returnNext = false;
            foreach (var option in setting.Options)
            {
                if (string.IsNullOrWhiteSpace(option))
                {
                    continue;
                }

                if (first == null)
                {
                    first = option;
                }

                if (returnNext)
                {
                    return option;
                }

                if (string.Equals(option, setting.Value, StringComparison.OrdinalIgnoreCase))
                {
                    returnNext = true;
                }
            }

            return first ?? setting.Value ?? "";
        }

        private static bool TryParseBoolean(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            if (string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "enabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "disabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            return false;
        }

        private static string BoolDisplay(bool value)
        {
            return value ? "ENABLED" : "DISABLED";
        }

        private static string TypeText(ModInventoryEntry entry)
        {
            if (OpenACAIModLoaderTypes.IsLibrary(entry?.LoaderType)
                && string.Equals(entry?.SourceFamily, OpenACAIModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
            {
                return "Modstoke library";
            }

            if (string.Equals(entry?.SourceFamily, OpenACAIModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
            {
                return "Modstoke";
            }

            if (OpenACAIModLoaderTypes.IsLegacyBepInEx(entry?.LoaderType))
            {
                return "BepInEx shim";
            }

            if (string.Equals(entry?.LoaderType, OpenACAIModLoaderTypes.RedLoaderLibrary, StringComparison.OrdinalIgnoreCase))
            {
                return "RL lib shim";
            }

            if (OpenACAIModLoaderTypes.IsLegacyRedLoader(entry?.LoaderType))
            {
                return "RL mod shim";
            }

            return "Unknown";
        }

        private static string SourceText(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return "-";
            }

            if (!string.IsNullOrWhiteSpace(entry.Author))
            {
                return entry.Author;
            }

            return "Unknown";
        }

        private static string DetailText(ModInventoryEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.Issue))
            {
                return entry.Issue;
            }

            if (entry.HostedByOpenAcai)
            {
                return "Running through Modstoke";
            }

            if (string.Equals(entry.SourceFamily, OpenACAIModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
            {
                return "Modstoke-native package detected";
            }

            if (entry.Hostable)
            {
                return "Detected and ready for controlled hosting";
            }

            if (!entry.HasAssembly && entry.ManifestPath != null)
            {
                return "Manifest only";
            }

            return entry.Store ?? entry.ExpectedLocation ?? "";
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }

            return value.Substring(0, Math.Max(0, max - 1)) + ".";
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            return tokens != null && Array.Exists(tokens, token => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void EnsureStyles()
        {
            if (_windowStyle != null)
            {
                return;
            }

            _windowTex = MakeTexture(new Color(0.035f, 0.04f, 0.042f, 0.98f));
            _headerTex = MakeTexture(new Color(0.03f, 0.16f, 0.24f, 1f));
            _panelTex = MakeTexture(new Color(0.015f, 0.014f, 0.012f, 0.93f));
            _rowTex = MakeTexture(new Color(1f, 1f, 1f, 0.075f));
            _lineTex = MakeTexture(new Color(0.3f, 0.62f, 0.72f, 0.55f));
            _buttonTex = MakeTexture(new Color(0.09f, 0.1f, 0.1f, 0.92f));
            _buttonHoverTex = MakeTexture(new Color(0.16f, 0.17f, 0.16f, 0.96f));
            _buttonSelectedTex = MakeTexture(new Color(0.32f, 0.29f, 0.18f, 0.98f));

            _windowStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(1, 1, 1, 1)
            };
            SetStyleBackground(_windowStyle, _windowTex);
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _tabStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _tabInactiveStyle = new GUIStyle(_tabStyle) { normal = { textColor = new Color(0.68f, 0.68f, 0.66f, 1f) } };
            _tabRedStyle = new GUIStyle(_tabStyle) { normal = { textColor = new Color(1f, 0.1f, 0.35f, 0.86f) } };
            _tabGreenStyle = new GUIStyle(_tabStyle) { normal = { textColor = new Color(0.25f, 1f, 0.42f, 0.72f) } };
            _tabBlueStyle = new GUIStyle(_tabStyle) { normal = { textColor = new Color(0.15f, 0.55f, 1f, 0.78f) } };
            _sectionStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
            _metaStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.72f, 0.8f, 0.78f, 1f) } };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = Color.white }, alignment = TextAnchor.MiddleLeft };
            _valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _mutedStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.74f, 0.78f, 0.77f, 1f) }, alignment = TextAnchor.MiddleLeft };
            _headerStyle = new GUIStyle(_mutedStyle) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.78f, 0.88f, 0.86f, 1f) } };
            _rowStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 6, 6), margin = new RectOffset(0, 0, 2, 2) };
            SetStyleBackground(_rowStyle, _rowTex);
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, hover = { textColor = Color.white }, active = { textColor = Color.white }, padding = new RectOffset(8, 8, 4, 4), border = new RectOffset(1, 1, 1, 1) };
            SetButtonBackgrounds(_buttonStyle, _buttonTex, _buttonHoverTex, _buttonHoverTex);
            _framelessButtonStyle = new GUIStyle(GUIStyle.none)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
                hover = { textColor = new Color(1f, 0.92f, 0.62f, 1f) },
                active = { textColor = new Color(1f, 0.92f, 0.62f, 1f) }
            };
            _buttonActiveStyle = new GUIStyle(_buttonStyle)
            {
                normal = { textColor = new Color(0.96f, 0.84f, 0.48f, 1f) },
                hover = { textColor = new Color(1f, 0.92f, 0.62f, 1f) },
                active = { textColor = new Color(1f, 0.92f, 0.62f, 1f) }
            };
            SetButtonBackgrounds(_buttonActiveStyle, _buttonSelectedTex, _buttonSelectedTex, _buttonSelectedTex);
            _statusReadyStyle = CreateStatusStyle(new Color(0.42f, 0.95f, 0.64f, 1f));
            _statusHostedStyle = CreateStatusStyle(new Color(0.35f, 0.82f, 1f, 1f));
            _statusWarningStyle = CreateStatusStyle(new Color(1f, 0.78f, 0.34f, 1f));
            _statusMissingStyle = CreateStatusStyle(new Color(1f, 0.36f, 0.32f, 1f));
            _statusDisabledStyle = CreateStatusStyle(new Color(0.68f, 0.72f, 0.72f, 1f));
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


