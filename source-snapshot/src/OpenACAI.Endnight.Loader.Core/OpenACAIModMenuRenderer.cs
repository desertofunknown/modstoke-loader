// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Native Modstoke mods menu renderer for Sons Of The Forest.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenACAI.Loader;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenAcaiNativeModMenu
    {
        private const int MaxConfigEditorBytes = 262144;
        private static GameObject _root;
        private static RectTransform _content;
        private static RectTransform _viewport;
        private static ScrollRect _scrollRect;
        private static RectTransform _tooltipPanel;
        private static TextMeshProUGUI _tooltipKeyText;
        private static TextMeshProUGUI _tooltipBodyText;
        private static TextMeshProUGUI _headingText;
        private static TextMeshProUGUI _subheadingText;
        private static TextMeshProUGUI _modeText;
        private static readonly List<HologramLabel> _tabLabels = new List<HologramLabel>();
        private static readonly List<HologramLabel> _footerLabels = new List<HologramLabel>();
        private static readonly List<HologramLabel> _actionLabels = new List<HologramLabel>();
        private static TMP_FontAsset _font;
        private static Material _fontMaterial;
        private static Sprite _nativePanelSprite;
        private static Sprite _nativeRowSprite;
        private static Sprite _nativeButtonSprite;
        private static Sprite _nativeInputSprite;
        private static ConfigSetting[] _friendlyBindings = Array.Empty<ConfigSetting>();
        private static TMP_InputField _rawEditor;
        private static bool _created;
        private static bool _visible;

        internal static bool IsVisible => _visible;

        internal static bool Show(ModInventoryState inventory)
        {
            using (OpenACAITimings.Measure("ui.mods-menu.native", "show", inventory == null || inventory.Summary == null ? "no-inventory" : inventory.Summary.Total + " mods"))
            {
                try
                {
                    OpenAcaiModMenuState.SetInventory(inventory);
                    EnsureCreated();
                    if (!_created)
                    {
                        return false;
                    }

                    OpenAcaiModMenuState.CloseConfig();
                    OpenAcaiModMenuState.ResetView();
                    RebuildList();
                    _root.SetActive(true);
                    _visible = true;
                    OpenACAIUi.MarkVisible(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "list", "Native Modstoke mods menu is visible.");
                    OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "native-menu", "closed", "No mod config is open.");
                    SetTooltip("MODS - Select a mod to view details or edit its configuration.");
                    return true;
                }
                catch (Exception ex)
                {
                    OpenACAIEndnightRuntime.LogSource?.LogWarning("Native Modstoke Mods menu failed; falling back to IMGUI: " + ex.Message);
                    _visible = false;
                    if (_root != null)
                    {
                        _root.SetActive(false);
                    }

                    return false;
                }
            }
        }

        internal static void Hide()
        {
            using (OpenACAITimings.Measure("ui.mods-menu.native", "hide", _visible ? "visible" : "already-hidden"))
            {
                _visible = false;
                OpenAcaiModMenuState.CloseProvider();
                if (_root != null)
                {
                    _root.SetActive(false);
                }
                OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "native-menu", "closed", "No mod config is open.");
                OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "closed", "Modstoke mods menu is not visible.");
            }
        }

        internal static void Update()
        {
            if (!_visible)
            {
                return;
            }

            var phase = Time.unscaledTime;
            for (var i = 0; i < _tabLabels.Count; i++)
            {
                _tabLabels[i].SetSelected(!OpenAcaiModMenuState.IsConfigOpen && i == OpenAcaiModMenuState.ActiveTab);
                _tabLabels[i].Update(phase);
            }

            foreach (var label in _footerLabels)
            {
                label.Update(phase);
            }

            _actionLabels.RemoveAll(label => label == null || !label.IsAlive);
            foreach (var label in _actionLabels)
            {
                label.Update(phase);
            }

            ApplyManualScroll();

            if (OpenACAIInput.IsPressed(OpenACAIKeybindIds.Back))
            {
                if (OpenAcaiModMenuState.IsProviderOpen)
                {
                    OpenAcaiModMenuState.CloseProvider();
                    RebuildList();
                }
                else if (OpenAcaiModMenuState.IsConfigOpen)
                {
                    RebuildList();
                }
                else
                {
                    Hide();
                }
            }
        }

        internal static void ResumeConfig()
        {
            RebuildConfig();
            _root.SetActive(true);
            _visible = true;
            OpenACAIUi.MarkVisible(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "config", "Native mod config view is visible.");
            OpenACAIUi.MarkVisible(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "native-menu", "config", "Native mod config editor is visible.");
        }

        private static void EnsureCreated()
        {
            if (_created)
            {
                return;
            }

            using (OpenACAITimings.Measure("ui.mods-menu.native", "create", "initial canvas"))
            {
                _font = FindNativeFont();
                _fontMaterial = FindNativeFontMaterial();
                _nativePanelSprite = FindNativeOptionsSprite(NativeSpriteKind.Panel);
                _nativeRowSprite = FindNativeOptionsSprite(NativeSpriteKind.Row);
                _nativeButtonSprite = FindNativeOptionsSprite(NativeSpriteKind.Button);
                _nativeInputSprite = FindNativeOptionsSprite(NativeSpriteKind.Input);

                _root = new GameObject("OpenACAI Native Mods Menu");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _root.SetActive(false);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 4600;

                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 1f;

                _root.AddComponent<GraphicRaycaster>();
                EnsureEventSystem();

                var shade = CreateImage("Shade", _root.transform, new Color(0f, 0f, 0f, 0.70f));
                Stretch(shade.rectTransform, 0f, 0f, 0f, 0f);

                var panel = CreateImage("NativeOptionsPanelClone", _root.transform, new Color(0f, 0f, 0f, 0.82f));
                Stretch(panel.rectTransform, 58f, 28f, 58f, 28f);
                panel.raycastTarget = false;
                if (_nativePanelSprite != null)
                {
                    panel.sprite = _nativePanelSprite;
                    panel.type = _nativePanelSprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
                    panel.fillCenter = true;
                    panel.pixelsPerUnitMultiplier = 1.6f;
                }
                else
                {
                    panel.color = new Color(0f, 0f, 0f, 0.74f);
                    AddRoughOptionsEdges(panel.transform);
                }

                _headingText = CreateText(panel.transform, "Heading", "MODS", 30f, Color.white, TextAlignmentOptions.Center);
                SetRect(_headingText.rectTransform, 0f, -38f, 0f, 42f, AnchorPresets.TopStretch);

                _subheadingText = CreateText(panel.transform, "Subheading", "", 13f, new Color(0.70f, 0.76f, 0.74f, 0.9f), TextAlignmentOptions.Center);
                SetRect(_subheadingText.rectTransform, 0f, -72f, 0f, 26f, AnchorPresets.TopStretch);

                var tabCount = OpenAcaiModMenuState.TabCount;
                for (var i = 0; i < OpenAcaiModMenuState.TabCount; i++)
                {
                    var label = HologramLabel.Create(panel.transform, OpenAcaiModMenuState.TabLabel(i).ToUpperInvariant(), _font, _fontMaterial, 28f, TextAlignmentOptions.Center);
                    SetTabRect(label.RectTransform, i, tabCount);
                    var index = i;
                    AddButton(label.Root, () =>
                    {
                        OpenAcaiModMenuState.SelectTab(index);
                        RebuildList();
                    });
                    AddHologramHover(label.Root, label, () => !OpenAcaiModMenuState.IsConfigOpen && index == OpenAcaiModMenuState.ActiveTab, OpenAcaiModMenuState.TabTooltip(index));
                    _tabLabels.Add(label);
                }

                var rule = CreateImage("SectionRule", panel.transform, new Color(0.75f, 0.75f, 0.72f, 0.64f));
                SetRect(rule.rectTransform, 78f, -224f, -78f, 1.4f, AnchorPresets.TopStretch);

                var viewportRoot = new GameObject("Viewport");
                viewportRoot.transform.SetParent(panel.transform, false);
                var viewportRect = viewportRoot.AddComponent<RectTransform>();
                _viewport = viewportRect;
                Stretch(viewportRect, 80f, 238f, 80f, 118f);
                var viewportImage = viewportRoot.AddComponent<Image>();
                viewportImage.color = new Color(0f, 0f, 0f, 0.001f);
                viewportImage.raycastTarget = true;
                viewportRoot.AddComponent<RectMask2D>();

                var scrollRoot = new GameObject("Scroll");
                scrollRoot.transform.SetParent(viewportRoot.transform, false);
                var scrollRectTransform = scrollRoot.AddComponent<RectTransform>();
                Stretch(scrollRectTransform, 0f, 0f, 0f, 0f);
                var scroll = scrollRoot.AddComponent<ScrollRect>();
                _scrollRect = scroll;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 38f;
                scroll.viewport = viewportRect;

                var contentRoot = new GameObject("Content");
                contentRoot.transform.SetParent(scrollRoot.transform, false);
                _content = contentRoot.AddComponent<RectTransform>();
                _content.anchorMin = new Vector2(0f, 1f);
                _content.anchorMax = new Vector2(1f, 1f);
                _content.pivot = new Vector2(0.5f, 1f);
                _content.offsetMin = Vector2.zero;
                _content.offsetMax = Vector2.zero;
                var layout = contentRoot.AddComponent<VerticalLayoutGroup>();
                layout.spacing = 5f;
                layout.childControlWidth = true;
                layout.childForceExpandWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
                contentRoot.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                scroll.content = _content;

                CreateFooter(panel.transform);
                CreateTooltip(panel.transform);
                _created = true;
            }
        }

        private static void CreateFooter(Transform parent)
        {
            var back = HologramLabel.Create(parent, "BACK", _font, _fontMaterial, 30f, TextAlignmentOptions.MidlineLeft);
            SetRect(back.RectTransform, 80f, 28f, 190f, 52f, AnchorPresets.BottomLeft);
            AddButton(back.Root, () =>
            {
                if (OpenAcaiModMenuState.IsProviderOpen)
                {
                    OpenAcaiModMenuState.CloseProvider();
                    RebuildList();
                }
                else if (OpenAcaiModMenuState.IsConfigOpen)
                {
                    RebuildList();
                }
                else
                {
                    Hide();
                }
            });
            AddHologramHover(back.Root, back, () => false, "BACK - Return to the previous menu.");
            _footerLabels.Add(back);

            var refresh = HologramLabel.Create(parent, "REFRESH", _font, _fontMaterial, 30f, TextAlignmentOptions.MidlineRight);
            SetRect(refresh.RectTransform, -285f, 28f, 220f, 52f, AnchorPresets.BottomRight);
            AddButton(refresh.Root, () =>
            {
                try
                {
                    OpenAcaiModMenuState.SetInventory(OpenACAIEndnightRuntime.RefreshModInventory());
                    RebuildList();
                    SetTooltip("REFRESH - Mod inventory refreshed.");
                }
                catch (Exception ex)
                {
                    SetTooltip("REFRESH - Failed: " + ex.Message);
                }
            });
            AddHologramHover(refresh.Root, refresh, () => false, "REFRESH - Rescan RedLoader, BepInEx, Vortex, and config files.");
            _footerLabels.Add(refresh);
        }

        private static void CreateTooltip(Transform parent)
        {
            var panel = CreateImage("TooltipPanel", parent, new Color(0.065f, 0.065f, 0.058f, 0.86f));
            panel.raycastTarget = false;
            ApplyNativeSprite(panel, _nativeRowSprite, 1.2f);
            _tooltipPanel = panel.rectTransform;
            SetRect(_tooltipPanel, 150f, -180f, 150f, 36f, AnchorPresets.TopStretch);
            var topRule = CreateImage("TooltipRule", panel.transform, new Color(1f, 1f, 1f, 0.04f));
            topRule.raycastTarget = false;
            Stretch(topRule.rectTransform, 0f, 0f, 0f, 0f);

            _tooltipKeyText = CreateText(panel.transform, "TooltipKey", "", 14f, new Color(0.92f, 0.94f, 0.08f, 1f), TextAlignmentOptions.MidlineLeft);
            _tooltipKeyText.fontStyle = FontStyles.Bold;
            _tooltipKeyText.characterSpacing = 1.5f;
            _tooltipKeyText.raycastTarget = false;
            SetRect(_tooltipKeyText.rectTransform, 24f, 0f, 360f, 36f, AnchorPresets.TopLeft);

            _tooltipBodyText = CreateText(panel.transform, "TooltipBody", "", 14f, new Color(0.86f, 0.88f, 0.85f, 0.96f), TextAlignmentOptions.MidlineLeft);
            _tooltipBodyText.fontStyle = FontStyles.Bold;
            _tooltipBodyText.characterSpacing = 1.2f;
            _tooltipBodyText.raycastTarget = false;
            SetRect(_tooltipBodyText.rectTransform, 24f, 0f, -24f, 36f, AnchorPresets.TopLeftStretchWidth);
        }

        private static void RebuildList()
        {
            var activeTab = OpenAcaiModMenuState.ActiveTab;
            var activeTabLabel = OpenAcaiModMenuState.ActiveTabLabel.ToUpperInvariant();
            using (OpenACAITimings.Measure("ui.mods-menu.native", "rebuild-list", activeTabLabel))
            {
                try
                {
                    OpenAcaiModMenuState.CloseConfig();
                    _rawEditor = null;
                    OpenACAIUi.MarkVisible(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "list:" + activeTabLabel, "Native Modstoke mods list is visible.");
                    OpenACAIUi.MarkHidden(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "native-menu", "closed", "No mod config is open.");
                    ClearContent();
                    SetHeading("MODS", BuildMetaLine());

                    if (OpenAcaiModMenuState.IsProviderOpen)
                    {
                        AddProviderContentRows();
                        return;
                    }

                    if (OpenAcaiModMenuState.IsExtensionsTab)
                    {
                        AddProviderRows();
                        return;
                    }

                    if (OpenAcaiModMenuState.IsCreditsTab)
                    {
                        AddCreditsRows();
                        return;
                    }

                    var entries = FilterEntries();
                    AddSectionHeader(OpenAcaiModMenuState.IsLibrariesTab ? "LIBRARIES" : "MODS", "NAME", "STATUS", "TYPE", "AUTHOR", "VERSION", "DETAIL");
                    if (entries.Length == 0)
                    {
                        AddInfoRow("No matching entries were found.", "Try refreshing after the game has loaded or check the Mods, Libs, OpenACAI/Mods, and BepInEx plugin folders.");
                        return;
                    }

                    foreach (var entry in entries)
                    {
                        AddModRow(entry);
                    }
                }
                finally
                {
                    ResetScrollToTop();
                }
            }
        }

        private static void AddProviderRows()
        {
            var providers = OpenAcaiModMenuState.GetExtensionProviders();
            AddSectionHeader("EXTENSIONS", "NAME", "KIND", "SOURCE", "AUTH", "STATUS", "DETAIL");
            if (providers.Length == 0)
            {
                AddInfoRow("No extension providers are registered yet.", "EXTENSIONS - Mods and Modstoke companion tools can register settings, status, action, or server panels through OpenACAIMenu.");
                return;
            }

            foreach (var provider in providers)
            {
                AddProviderRow(provider);
            }
        }

        private static void AddProviderRow(OpenACAIMenuProviderSnapshot provider)
        {
            var row = CreateRow("Provider_" + SafeName(provider.Id), 48f, new Color(1f, 1f, 1f, 0.047f));
            AddColumnText(row.transform, Shorten(provider.DisplayName, 34), 18f, Color.white, 18f, 0f, 390f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(provider.Kind, 13), 14f, MutedColor(), 430f, 0f, 120f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(provider.Source, 18), 15f, MutedColor(), 560f, 0f, 160f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, provider.RequiresAuthorization ? "YES" : "NO", 14f, provider.RequiresAuthorization ? new Color(1f, 0.78f, 0.34f, 1f) : MutedColor(), 740f, 0f, 170f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, provider.Enabled ? "READY" : "OFF", 14f, provider.Enabled ? new Color(0.42f, 0.95f, 0.64f, 1f) : new Color(1f, 0.36f, 0.32f, 1f), 930f, 0f, 130f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, provider.HasContent || provider.HasCommands ? "OPEN  >" : "DETAIL  >", 15f, Color.white, 1100f, 0f, -22f, 48f, TextAlignmentOptions.MidlineLeft);
            var button = AddButton(row, () =>
            {
                OpenAcaiModMenuState.OpenProvider(provider);
                RebuildList();
                SetTooltip(provider.DisplayName + " - " + provider.Description);
            });
            button.transition = Selectable.Transition.ColorTint;
            AddTooltip(row, provider.DisplayName + " - " + provider.Description);
        }

        private static void AddProviderContentRows()
        {
            var provider = OpenAcaiModMenuState.ProviderEntry;
            var content = OpenAcaiModMenuState.ProviderContent;
            var commands = OpenAcaiModMenuState.ProviderCommands;
            var title = content?.Title ?? commands?.Title ?? provider?.DisplayName ?? "Extension";
            var subtitle = content?.Subtitle ?? commands?.Subtitle ?? provider?.Description ?? "";

            SetHeading(Shorten(title, 30).ToUpperInvariant(), BuildProviderMetaLine(provider));
            AddSectionHeader("EXTENSION", "NAME", "KIND", "SOURCE", "AUTH", "STATUS", "DETAIL");
            if (provider != null)
            {
                AddProviderSummaryRow(provider);
            }

            if (!string.IsNullOrWhiteSpace(OpenAcaiModMenuState.ProviderStatus))
            {
                AddInfoRow(OpenAcaiModMenuState.ProviderStatus, "EXTENSION - " + OpenAcaiModMenuState.ProviderStatus);
            }

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                AddInfoRow(subtitle, title + " - " + subtitle);
            }

            OpenACAIConfigProviderSnapshot configSnapshot;
            if (OpenAcaiModMenuState.TryGetProviderConfigSnapshot(out configSnapshot))
            {
                AddProviderConfigRows(configSnapshot);
                return;
            }

            if (commands != null)
            {
                AddProviderCommandRows(commands);
                return;
            }

            if (content == null || content.Sections.Count == 0)
            {
                AddInfoRow("No provider detail rows are available yet.", "EXTENSION - This provider has not published structured menu content.");
                return;
            }

            foreach (var section in content.Sections)
            {
                AddSectionHeader(Shorten(section.Title, 28).ToUpperInvariant(), "ITEM", "VALUE", "", "STATUS", "", "DETAIL");
                if (section.Rows.Count == 0)
                {
                    AddInfoRow("No rows in this section.", section.Title + " - No provider rows were supplied.");
                    continue;
                }

                foreach (var row in section.Rows)
                {
                    AddProviderContentRow(row);
                }
            }
        }

        private static void AddProviderSummaryRow(OpenACAIMenuProviderSnapshot provider)
        {
            var row = CreateRow("ProviderSummary_" + SafeName(provider.Id), 48f, new Color(1f, 1f, 1f, 0.047f));
            AddColumnText(row.transform, Shorten(provider.DisplayName, 34), 18f, Color.white, 18f, 0f, 390f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(provider.Kind, 13), 14f, MutedColor(), 430f, 0f, 120f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(provider.Source, 18), 15f, MutedColor(), 560f, 0f, 160f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, provider.RequiresAuthorization ? "YES" : "NO", 14f, provider.RequiresAuthorization ? new Color(1f, 0.78f, 0.34f, 1f) : MutedColor(), 740f, 0f, 170f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, provider.Enabled ? "READY" : "OFF", 14f, provider.Enabled ? new Color(0.42f, 0.95f, 0.64f, 1f) : new Color(1f, 0.36f, 0.32f, 1f), 930f, 0f, 130f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, ProviderSurfaceText(provider), 15f, MutedColor(), 1100f, 0f, -22f, 48f, TextAlignmentOptions.MidlineLeft);
            AddTooltip(row, provider.DisplayName + " - " + provider.Description);
        }

        private static void AddProviderContentRow(OpenACAIMenuRowSnapshot item)
        {
            var row = CreateRow("ProviderContent_" + SafeName(item.Label), 48f, new Color(1f, 1f, 1f, 0.047f));
            AddColumnText(row.transform, Shorten(item.Label, 34), 17f, Color.white, 18f, 0f, 390f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(item.Value, 32), 15f, MutedColor(), 430f, 0f, 290f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(item.Status, 16), 14f, ProviderStatusColor(item.Status), 740f, 0f, 170f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(item.Detail, 72), 15f, MutedColor(), 930f, 0f, -22f, 48f, TextAlignmentOptions.MidlineLeft);
            AddTooltip(row, item.Label + " - " + (string.IsNullOrWhiteSpace(item.Detail) ? item.Value : item.Detail));
        }

        private static void AddProviderConfigRows(OpenACAIConfigProviderSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Settings.Count == 0)
            {
                AddInfoRow("No structured settings are available.", "SETTINGS - This provider did not publish editable settings.");
                return;
            }

            foreach (var group in snapshot.Settings.GroupBy(setting => string.IsNullOrWhiteSpace(setting.Section) ? "Settings" : setting.Section))
            {
                AddSectionHeader(Shorten(group.Key, 28).ToUpperInvariant(), "SETTING", "VALUE", "", "STATE", "", "DETAIL");
                foreach (var setting in group)
                {
                    AddProviderConfigSettingRow(setting);
                }
            }
        }

        private static void AddProviderConfigSettingRow(OpenACAIConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return;
            }

            var row = CreateRow("ProviderSetting_" + SafeName(setting.Key), 54f, new Color(1f, 1f, 1f, 0.045f));
            AddColumnText(row.transform, Shorten(setting.DisplayName, 32), 17f, Color.white, 18f, 0f, 350f, 54f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, ProviderSettingStatus(setting), 14f, ProviderStatusColor(ProviderSettingStatus(setting)), 825f, 0f, 100f, 54f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(ProviderSettingDetail(setting), 64), 15f, MutedColor(), 930f, 0f, -22f, 54f, TextAlignmentOptions.MidlineLeft);

            if (!setting.CanEdit)
            {
                AddColumnText(row.transform, Shorten(setting.Value, 36), 15f, MutedColor(), 380f, 0f, 430f, 54f, TextAlignmentOptions.MidlineLeft);
                AddTooltip(row, ProviderSettingTooltip(setting));
                return;
            }

            if (string.Equals(setting.Kind, OpenACAIConfigValueKinds.Boolean, StringComparison.OrdinalIgnoreCase)
                || TryParseBoolean(setting.Value, out _))
            {
                AddProviderBooleanControl(row.transform, setting);
            }
            else if (string.Equals(setting.Kind, OpenACAIConfigValueKinds.Choice, StringComparison.OrdinalIgnoreCase)
                && setting.Options != null
                && setting.Options.Count > 0)
            {
                AddProviderChoiceControl(row.transform, setting);
            }
            else
            {
                AddProviderInputControl(row.transform, setting);
            }

            AddTooltip(row, ProviderSettingTooltip(setting));
        }

        private static void AddProviderBooleanControl(Transform row, OpenACAIConfigValueSnapshot setting)
        {
            TryParseBoolean(setting.Value, out var current);
            AddTextButton(row, BoolDisplay(current), 380f, 5f, 310f, 44f, current, () =>
            {
                ApplyProviderConfigValue(setting, current ? "false" : "true");
            }, ProviderSettingTooltip(setting));
        }

        private static void AddProviderChoiceControl(Transform row, OpenACAIConfigValueSnapshot setting)
        {
            var options = setting.Options
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .ToArray();
            if (options.Length == 0)
            {
                AddProviderInputControl(row, setting);
                return;
            }

            var index = Array.FindIndex(options, option => string.Equals(option, setting.Value, StringComparison.OrdinalIgnoreCase));
            var next = options[(index < 0 ? 0 : index + 1) % options.Length];
            AddTextButton(row, Shorten(string.IsNullOrWhiteSpace(setting.Value) ? options[0] : setting.Value, 24).ToUpperInvariant(), 380f, 5f, 310f, 44f, false, () =>
            {
                ApplyProviderConfigValue(setting, next);
            }, ProviderSettingTooltip(setting));
        }

        private static void AddProviderInputControl(Transform row, OpenACAIConfigValueSnapshot setting)
        {
            var input = CreateInput(row, setting.Value, false);
            if (string.Equals(setting.Kind, OpenACAIConfigValueKinds.Integer, StringComparison.OrdinalIgnoreCase))
            {
                input.contentType = TMP_InputField.ContentType.IntegerNumber;
            }
            else if (string.Equals(setting.Kind, OpenACAIConfigValueKinds.Number, StringComparison.OrdinalIgnoreCase))
            {
                input.contentType = TMP_InputField.ContentType.DecimalNumber;
            }

            SetRect(input.GetComponent<RectTransform>(), 380f, -7f, 310f, 40f, AnchorPresets.TopLeft);
            AddTextButton(row, "APPLY", 705f, 5f, 110f, 44f, false, () =>
            {
                ApplyProviderConfigValue(setting, input.text ?? "");
            }, "APPLY - Save this structured setting through the provider.");
        }

        private static void ApplyProviderConfigValue(OpenACAIConfigValueSnapshot setting, string value)
        {
            if (setting == null)
            {
                SetTooltip("APPLY - No setting is selected.");
                return;
            }

            string tooltip;
            OpenAcaiModMenuState.TryApplyProviderConfigValue(setting.Key, value, out tooltip);
            SetTooltip(tooltip);
            RebuildList();
        }

        private static void AddProviderCommandRows(OpenACAICommandProviderSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Commands.Count == 0)
            {
                AddInfoRow("No commands are available.", "COMMANDS - This provider did not publish command rows.");
                return;
            }

            AddSectionHeader("COMMANDS", "COMMAND", "STATUS", "ACCESS", "DETAIL", "", "ACTION");
            foreach (var command in snapshot.Commands)
            {
                AddProviderCommandRow(command);
            }
        }

        private static void AddProviderCommandRow(OpenACAICommandSnapshot command)
        {
            if (command == null)
            {
                return;
            }

            var row = CreateRow("ProviderCommand_" + SafeName(command.Id), 54f, new Color(1f, 1f, 1f, 0.045f));
            var status = ProviderCommandStatus(command);
            AddColumnText(row.transform, Shorten(command.DisplayName, 32), 17f, Color.white, 18f, 0f, 350f, 54f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, status, 14f, ProviderStatusColor(status), 430f, 0f, 120f, 54f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, ProviderCommandAccess(command), 14f, ProviderStatusColor(ProviderCommandAccess(command)), 560f, 0f, 160f, 54f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(command.Description, 54), 15f, MutedColor(), 740f, 0f, 340f, 54f, TextAlignmentOptions.MidlineLeft);

            if (command.Enabled && !command.RequiresConfirmation)
            {
                AddTextButton(row.transform, "RUN", 1110f, 5f, 110f, 44f, false, () =>
                {
                    InvokeProviderCommand(command);
                }, ProviderCommandTooltip(command));
            }
            else
            {
                AddColumnText(row.transform, ProviderCommandActionText(command), 15f, MutedColor(), 1100f, 0f, -22f, 54f, TextAlignmentOptions.MidlineLeft);
            }

            AddTooltip(row, ProviderCommandTooltip(command));
        }

        private static void InvokeProviderCommand(OpenACAICommandSnapshot command)
        {
            if (command == null)
            {
                SetTooltip("RUN - No command is selected.");
                return;
            }

            if (!command.Enabled)
            {
                SetTooltip(command.DisplayName + " - Command is disabled.");
                return;
            }

            if (command.RequiresConfirmation)
            {
                SetTooltip(command.DisplayName + " - Confirmation commands are visible but not runnable from this safe panel yet.");
                return;
            }

            string tooltip;
            OpenAcaiModMenuState.TryInvokeProviderCommand(command.Id, out tooltip);
            SetTooltip(tooltip);
            RebuildList();
        }

        private static void AddCreditsRows()
        {
            AddSectionHeader("CREDITS", "PROJECT", "CREDIT", "ROLE", "", "", "NOTES");
            AddCreditRow("Modstoke Loader", "Alex Cooper / OpenACAI Inc", "Developer", "by OpenACAI. Independent mod loader for Sons of the Forest.");
            AddCreditRow("Modstoke SDK", "by OpenACAI", "Mod API", "Public modding APIs; existing OpenACAI namespaces remain compatible.");
            AddCreditRow("Modstoke", "by OpenACAI", "Website / catalog", "Intended address: modstoke.com. Services are not confirmed live.");
            AddCreditRow("BepInEx", "BepInEx contributors", "Upstream loader", "Modstoke Loader builds on BepInEx IL2CPP runtime foundations.");
            AddCreditRow("RedLoader", "Toni Macaroni", "Upstream SOTF loader", "RedLoader and SonsSdk behavior informed the compatibility layer and mod menu support.");
            AddCreditRow("SonsSdk", "Toni Macaroni and contributors", "Mod API", "SonsSdk APIs remain credited for RedLoader mod compatibility.");
            AddCreditRow("Mod authors", "Community creators", "Mods", "The AUTHOR column shows mod manifest authors whenever available.");
            AddInfoRow("Licensing: Modstoke Loader code is GPL-3.0-or-later. Upstream projects retain their own licenses.", "CREDITS - BepInEx, RedLoader, and SonsSdk remain credited upstream work.");
            AddInfoRow("OpenACAI is the nonprofit publisher. Not affiliated with or endorsed by Endnight Games.", "Compatible with Sons of the Forest; an independent community project.");
        }

        private static void AddCreditRow(string project, string credit, string role, string notes)
        {
            var row = CreateRow("Credit_" + SafeName(project), 48f, new Color(1f, 1f, 1f, 0.047f));
            AddColumnText(row.transform, Shorten(project, 34), 17f, Color.white, 18f, 0f, 390f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(credit, 28), 15f, MutedColor(), 430f, 0f, 230f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(role, 22), 15f, MutedColor(), 690f, 0f, 210f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(notes, 64), 15f, MutedColor(), 930f, 0f, -22f, 48f, TextAlignmentOptions.MidlineLeft);
            AddTooltip(row, project + " - " + notes);
        }

        private static void RebuildConfig()
        {
            var entry = OpenAcaiModMenuState.ConfigEntry;
            using (OpenACAITimings.Measure("ui.mods-menu.native", "rebuild-config", entry?.DisplayName ?? "none"))
            {
                try
                {
                    OpenACAIUi.MarkVisible(OpenACAIUiSurfaceIds.ModsMenu, "Mods Menu", "native-menu", "config", "Native Modstoke mod config view is visible.");
                    OpenACAIUi.MarkVisible(OpenACAIUiSurfaceIds.ModConfig, "Mod Config", "native-menu", "config:" + (entry?.DisplayName ?? "none"), OpenAcaiModMenuState.ConfigStatus ?? "Native mod config editor is visible.");
                    ClearContent();
                    SetHeading("MOD CONFIG", (entry?.DisplayName ?? "No mod selected").ToUpperInvariant());
                    if (entry?.HasConfig == true)
                    {
                        AddModeButtons();
                    }

                    if (entry?.LiveSettingsAvailable == true || entry?.ModActionsAvailable == true)
                    {
                        var controlsRow = CreateRow("LiveControls", 52f, new Color(0f, 0f, 0f, 0f));
                        if (entry.LiveSettingsAvailable)
                        {
                            AddTextButton(controlsRow.transform, "MOD SETTINGS", 0f, 4f, 220f, 44f, false,
                                () => OpenLiveControls(OpenAcaiModMenu.OpenLiveSettings), "Registered mod settings");
                        }

                        if (entry.ModActionsAvailable)
                        {
                            AddTextButton(controlsRow.transform, "MOD ACTIONS", entry.LiveSettingsAvailable ? 240f : 0f, 4f, 220f, 44f, false,
                                () => OpenLiveControls(OpenAcaiModMenu.OpenModActions), "Registered mod actions");
                        }
                    }

                    if (entry == null)
                    {
                        AddInfoRow("No mod selected.", "Go back and select a mod with a detected config file.");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(OpenAcaiModMenuState.ConfigStatus))
                    {
                        AddInfoRow(OpenAcaiModMenuState.ConfigStatus, "Status for the selected config file.");
                    }

                    if (entry.HasConfig != true || string.IsNullOrWhiteSpace(entry.ConfigPath))
                    {
                        AddInfoRow("No file-backed config was detected for this mod.", "Some RedLoader mods register live settings only after they are hosted.");
                        return;
                    }

                    if (OpenAcaiModMenuState.FriendlyMode)
                    {
                        AddFriendlyConfigRows();
                    }
                    else
                    {
                        AddRawConfigEditor();
                    }
                }
                finally
                {
                    ResetScrollToTop();
                }
            }
        }

        private static void AddModeButtons()
        {
            var row = CreateRow("EditorMode", 52f, new Color(0f, 0f, 0f, 0f));
            _modeText = CreateText(row.transform, "ModeLabel", "EDIT MODE", 16f, new Color(0.82f, 0.82f, 0.78f, 0.86f), TextAlignmentOptions.MidlineLeft);
            SetRect(_modeText.rectTransform, 0f, 0f, 180f, 48f, AnchorPresets.TopLeft);

            AddTextButton(row.transform, "FRIENDLY", 210f, 4f, 180f, 44f, OpenAcaiModMenuState.FriendlyMode, () =>
            {
                CaptureRawEditorText();
                OpenAcaiModMenuState.SetFriendlyMode(true);
                RebuildConfig();
            }, "FRIENDLY - Use a guided key/value editor generated from the config file.");

            AddTextButton(row.transform, "TEXT", 405f, 4f, 140f, 44f, !OpenAcaiModMenuState.FriendlyMode, () =>
            {
                if (OpenAcaiModMenuState.FriendlyMode)
                {
                    ApplyFriendlySettingsToText();
                }

                OpenAcaiModMenuState.SetFriendlyMode(false);
                RebuildConfig();
            }, "TEXT - Show the raw config text for advanced edits.");

            AddTextButton(row.transform, "SAVE", -355f, 4f, 140f, 44f, false, SaveConfigFromNativeEditor, "SAVE - Write the current config edits to disk.");
            AddTextButton(row.transform, "REVERT", -200f, 4f, 160f, 44f, false, () =>
            {
                OpenAcaiModMenuState.RevertConfig();
                RebuildConfig();
                SetTooltip("REVERT - Reverted unsaved config edits.");
            }, "REVERT - Discard unsaved edits and reload the last opened text.");
        }

        private static void OpenLiveControls(Action open)
        {
            if (OpenAcaiModMenuState.FriendlyMode)
            {
                if (_friendlyBindings.Any(setting => !string.Equals(setting.CurrentValue(), setting.InitialValue, StringComparison.Ordinal)))
                {
                    ApplyFriendlySettingsToText();
                }
            }
            else
            {
                CaptureRawEditorText();
            }

            open();
            if (_visible)
            {
                SetTooltip(OpenAcaiModMenuState.ConfigStatus);
            }
        }

        private static void AddFriendlyConfigRows()
        {
            _friendlyBindings = OpenAcaiModMenuState.FriendlySettings
                .Select(setting => new ConfigSetting
                {
                    LineIndex = setting.LineIndex,
                    Section = setting.Section,
                    Key = setting.Key,
                    Value = setting.Value,
                    Tooltip = setting.Tooltip
                })
                .ToArray();
            if (_friendlyBindings.Length == 0)
            {
                AddInfoRow("No simple key/value settings were detected.", "Switch to TEXT mode for this config file.");
                return;
            }

            string currentSection = null;
            foreach (var setting in _friendlyBindings)
            {
                if (!string.Equals(currentSection, setting.Section, StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = setting.Section;
                    AddSectionHeader(string.IsNullOrWhiteSpace(currentSection) ? "GENERAL" : currentSection.ToUpperInvariant(), "SETTING", "", "", "", "", "VALUE");
                }

                var row = CreateRow("Setting_" + setting.Key, 54f, new Color(1f, 1f, 1f, 0.045f));
                var label = CreateText(row.transform, "Label", PrettyKey(setting.Key), 17f, Color.white, TextAlignmentOptions.MidlineLeft);
                SetRect(label.rectTransform, 18f, 0f, 620f, 54f, AnchorPresets.TopLeft);

                AddFriendlySettingControl(row.transform, setting);
                setting.InitialValue = setting.CurrentValue();
                AddTooltip(row, PrettyKey(setting.Key) + " - " + (string.IsNullOrWhiteSpace(setting.Tooltip) ? "Config setting from this mod." : setting.Tooltip));
            }
        }

        private static void AddRawConfigEditor()
        {
            var editor = CreateRow("RawTextEditor", 520f, new Color(1f, 1f, 1f, 0.045f));
            _rawEditor = CreateInput(editor.transform, OpenAcaiModMenuState.ConfigText ?? "", true);
            Stretch(_rawEditor.GetComponent<RectTransform>(), 14f, 12f, 14f, 12f);
            AddTooltip(editor, "TEXT - Raw config mode for advanced files or formats the friendly editor cannot parse.");
        }

        private static void AddModRow(ModInventoryEntry entry)
        {
            var row = CreateRow("Mod_" + SafeName(entry.DisplayName), 48f, new Color(1f, 1f, 1f, 0.047f));
            AddColumnText(row.transform, Shorten(entry.DisplayName, 34), 18f, Color.white, 18f, 0f, 390f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, StatusText(entry), 14f, StatusColor(entry), 430f, 0f, 120f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, TypeText(entry), 15f, MutedColor(), 560f, 0f, 160f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, SourceText(entry), 15f, MutedColor(), 740f, 0f, 170f, 48f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(row.transform, Shorten(entry.Version ?? entry.AssemblyVersion ?? "-", 14), 15f, MutedColor(), 930f, 0f, 130f, 48f, TextAlignmentOptions.MidlineLeft);

            var detail = entry.HasControls ? "CONFIG  >" : Shorten(DetailText(entry), 58);
            AddColumnText(row.transform, detail, 15f, entry.HasControls ? Color.white : MutedColor(), 1100f, 0f, -22f, 48f, TextAlignmentOptions.MidlineLeft);

            var button = AddButton(row, () =>
            {
                if (entry.HasControls)
                {
                    OpenConfig(entry);
                }
                else
                {
                    SetTooltip(DetailText(entry));
                }
            });
            button.transition = Selectable.Transition.ColorTint;
            AddTooltip(row, DetailText(entry));
        }

        private static void AddSectionHeader(string section, string c1, string c2, string c3, string c4, string c5, string c6)
        {
            var title = CreateRow("Section_" + SafeName(section), 44f, new Color(0f, 0f, 0f, 0f));
            AddColumnText(title.transform, section, 18f, Color.white, 0f, 0f, 240f, 42f, TextAlignmentOptions.MidlineLeft);
            var line = CreateImage("Rule", title.transform, new Color(0.72f, 0.72f, 0.68f, 0.52f));
            SetRect(line.rectTransform, 190f, 21f, -4f, 1.3f, AnchorPresets.TopStretch);

            var header = CreateRow("Header_" + SafeName(section), 30f, new Color(0f, 0f, 0f, 0f));
            AddColumnText(header.transform, c1, 13f, HeaderColor(), 18f, 0f, 390f, 30f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(header.transform, c2, 13f, HeaderColor(), 430f, 0f, 120f, 30f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(header.transform, c3, 13f, HeaderColor(), 560f, 0f, 160f, 30f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(header.transform, c4, 13f, HeaderColor(), 740f, 0f, 170f, 30f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(header.transform, c5, 13f, HeaderColor(), 930f, 0f, 130f, 30f, TextAlignmentOptions.MidlineLeft);
            AddColumnText(header.transform, c6, 13f, HeaderColor(), 1100f, 0f, -22f, 30f, TextAlignmentOptions.MidlineLeft);
        }

        private static void AddInfoRow(string text, string tooltip)
        {
            var row = CreateRow("Info", 52f, new Color(1f, 1f, 1f, 0.04f));
            AddColumnText(row.transform, text, 17f, MutedColor(), 18f, 0f, -18f, 52f, TextAlignmentOptions.MidlineLeft);
            AddTooltip(row, tooltip);
        }

        private static void OpenConfig(ModInventoryEntry entry)
        {
            OpenAcaiModMenuState.OpenConfig(entry, MaxConfigEditorBytes);
            RebuildConfig();
        }

        private static void SaveConfigFromNativeEditor()
        {
            var entry = OpenAcaiModMenuState.ConfigEntry;
            if (entry == null || entry.HasConfig != true || string.IsNullOrWhiteSpace(entry.ConfigPath))
            {
                SetTooltip("SAVE - No config file is selected.");
                return;
            }

            if (OpenAcaiModMenuState.FriendlyMode)
            {
                ApplyFriendlySettingsToText();
            }
            else
            {
                CaptureRawEditorText();
            }

            OpenAcaiModMenuState.SaveConfig(() => OpenACAIEndnightRuntime.RefreshModInventory(), out var tooltip);
            SetTooltip(tooltip);
        }

        private static void ApplyFriendlySettingsToText()
        {
            var edits = (_friendlyBindings ?? Array.Empty<ConfigSetting>())
                .Select(setting => new OpenACAIConfigSettingEdit(setting.LineIndex, setting.CurrentValue()));
            OpenAcaiModMenuState.ApplyFriendlyEdits(edits);
        }

        private static void CaptureRawEditorText()
        {
            if (_rawEditor != null)
            {
                OpenAcaiModMenuState.SetConfigText(_rawEditor.text ?? "");
            }
        }

        private static ModInventoryEntry[] FilterEntries()
        {
            return OpenAcaiModMenuState.GetFilteredEntries()
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void ClearContent()
        {
            _friendlyBindings = Array.Empty<ConfigSetting>();
            _rawEditor = null;
            if (_content == null)
            {
                return;
            }

            for (var i = _content.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            }
        }

        private static void ResetScrollToTop()
        {
            if (_scrollRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            if (_content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }

            _scrollRect.StopMovement();
            _scrollRect.verticalNormalizedPosition = 1f;
            if (_content != null)
            {
                _content.anchoredPosition = Vector2.zero;
            }
        }

        private static void ApplyManualScroll()
        {
            if (_scrollRect == null || _viewport == null || !_visible)
            {
                return;
            }

            var wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) <= 0.001f)
            {
                return;
            }

            if (!RectTransformUtility.RectangleContainsScreenPoint(_viewport, Input.mousePosition, null))
            {
                return;
            }

            var hiddenHeight = Mathf.Max(1f, (_content?.rect.height ?? 0f) - _viewport.rect.height);
            var delta = (wheel * 90f) / hiddenHeight;
            _scrollRect.verticalNormalizedPosition = Mathf.Clamp01(_scrollRect.verticalNormalizedPosition + delta);
        }

        private static void SetHeading(string heading, string subheading)
        {
            if (_headingText != null)
            {
                _headingText.text = heading;
            }

            if (_subheadingText != null)
            {
                _subheadingText.text = subheading ?? "";
            }
        }

        private static GameObject CreateRow(string name, float height, Color color)
        {
            var image = CreateImage(name, _content, color);
            if (color.a > 0.001f)
            {
                ApplyNativeSprite(image, _nativeRowSprite, 1.2f);
            }

            var row = image.gameObject;
            row.AddComponent<LayoutElement>().preferredHeight = height;
            return row;
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, string value, float size, Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = value ?? "";
            text.fontSize = size;
            text.fontStyle = FontStyles.Bold;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = alignment;
            text.color = color;
            text.margin = Vector4.zero;
            text.characterSpacing = 6f;
            if (_font != null)
            {
                text.font = _font;
            }

            if (_fontMaterial != null)
            {
                text.fontSharedMaterial = _fontMaterial;
            }

            return text;
        }

        private static void AddColumnText(Transform parent, string value, float size, Color color, float x, float y, float width, float height, TextAlignmentOptions alignment)
        {
            var text = CreateText(parent, "Text", value, size, color, alignment);
            SetRect(text.rectTransform, x, y, width, height, AnchorPresets.TopLeftStretchWidth);
        }

        private static TMP_InputField CreateInput(Transform parent, string value, bool multiline)
        {
            var image = CreateImage("Input", parent, new Color(0.05f, 0.05f, 0.045f, 0.9f));
            ApplyNativeSprite(image, _nativeInputSprite ?? _nativeRowSprite, 1.2f);
            var root = image.gameObject;
            var input = root.AddComponent<TMP_InputField>();
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.characterLimit = multiline ? 0 : 2048;
            input.customCaretColor = true;
            input.caretColor = new Color(0.92f, 0.94f, 0.08f, 1f);
            input.caretWidth = multiline ? 3 : 2;
            input.selectionColor = new Color(1f, 1f, 1f, 0.24f);
            input.onFocusSelectAll = false;
            input.restoreOriginalTextOnEscape = false;

            var viewport = new GameObject("TextViewport");
            viewport.transform.SetParent(root.transform, false);
            var viewportRect = viewport.AddComponent<RectTransform>();
            Stretch(viewportRect, 10f, 5f, 10f, 5f);
            viewport.AddComponent<RectMask2D>();
            input.textViewport = viewportRect;

            var text = CreateText(viewport.transform, "Text", "", multiline ? 15f : 17f, Color.white, multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft);
            text.fontStyle = FontStyles.Normal;
            text.characterSpacing = 0f;
            text.enableWordWrapping = multiline;
            text.overflowMode = multiline ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            Stretch(text.rectTransform, 0f, 2f, 0f, 2f);

            var placeholder = CreateText(viewport.transform, "Placeholder", multiline ? "Raw config text" : "Value", multiline ? 15f : 17f, new Color(1f, 1f, 1f, 0.22f), multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft);
            placeholder.fontStyle = FontStyles.Normal;
            placeholder.characterSpacing = 0f;
            Stretch(placeholder.rectTransform, 0f, 2f, 0f, 2f);

            input.textComponent = text;
            input.placeholder = placeholder;
            input.text = value ?? "";
            return input;
        }

        private static void AddFriendlySettingControl(Transform row, ConfigSetting setting)
        {
            if (TryParseBoolean(setting.Value, out var boolValue))
            {
                AddBooleanControl(row, setting, boolValue);
                return;
            }

            if (TryParseNumber(setting.Value, out var numberValue, out var isInteger))
            {
                AddNumberControl(row, setting, numberValue, isInteger);
                return;
            }

            var input = CreateInput(row, setting.Value, false);
            SetRect(input.GetComponent<RectTransform>(), -40f, -7f, 560f, 40f, AnchorPresets.TopRight);
            setting.Input = input;
        }

        private static void AddBooleanControl(Transform row, ConfigSetting setting, bool value)
        {
            setting.Value = value ? "true" : "false";
            setting.ControlKind = "bool";

            var image = CreateImage("BooleanToggle", row, value ? new Color(0.13f, 0.30f, 0.18f, 0.94f) : new Color(0.18f, 0.18f, 0.16f, 0.94f));
            ApplyNativeSprite(image, _nativeButtonSprite ?? _nativeRowSprite, 1.2f);
            SetRect(image.rectTransform, -40f, -7f, 300f, 40f, AnchorPresets.TopRight);

            var label = CreateText(image.transform, "Value", BoolDisplay(value), 17f, value ? new Color(0.42f, 0.95f, 0.64f, 1f) : new Color(0.86f, 0.88f, 0.85f, 0.96f), TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
            setting.Label = label;
            setting.Image = image;

            AddButton(image.gameObject, () =>
            {
                var next = !string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
                setting.Value = next ? "true" : "false";
                setting.Label.text = BoolDisplay(next);
                setting.Label.color = next ? new Color(0.42f, 0.95f, 0.64f, 1f) : new Color(0.86f, 0.88f, 0.85f, 0.96f);
                setting.Image.color = next ? new Color(0.13f, 0.30f, 0.18f, 0.94f) : new Color(0.18f, 0.18f, 0.16f, 0.94f);
                SetTooltip(PrettyKey(setting.Key) + " - " + (next ? "Enabled." : "Disabled."));
            });
        }

        private static void AddNumberControl(Transform row, ConfigSetting setting, double value, bool isInteger)
        {
            setting.ControlKind = isInteger ? "int" : "decimal";
            var minus = CreateSmallValueButton(row, "-", -40f, () => StepNumber(setting, isInteger ? -1d : -0.1d, isInteger));
            var plus = CreateSmallValueButton(row, "+", -92f, () => StepNumber(setting, isInteger ? 1d : 0.1d, isInteger));
            var input = CreateInput(row, FormatNumber(value, isInteger), false);
            input.contentType = isInteger ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.DecimalNumber;
            SetRect(input.GetComponent<RectTransform>(), -150f, -7f, 450f, 40f, AnchorPresets.TopRight);
            setting.Input = input;
            AddTooltip(minus, "DECREASE - Lower this numeric value.");
            AddTooltip(plus, "INCREASE - Raise this numeric value.");
        }

        private static GameObject CreateSmallValueButton(Transform row, string text, float x, Action action)
        {
            var image = CreateImage("ValueButton_" + text, row, new Color(0f, 0f, 0f, 0.34f));
            ApplyNativeSprite(image, _nativeButtonSprite ?? _nativeRowSprite, 1.2f);
            SetRect(image.rectTransform, x, -7f, 44f, 40f, AnchorPresets.TopRight);
            AddButton(image.gameObject, action);
            var label = CreateText(image.transform, "Label", text, 18f, Color.white, TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
            return image.gameObject;
        }

        private static void StepNumber(ConfigSetting setting, double delta, bool isInteger)
        {
            if (setting == null || setting.Input == null)
            {
                return;
            }

            if (!TryParseNumber(setting.Input.text, out var value, out _))
            {
                value = 0d;
            }

            setting.Input.text = FormatNumber(value + delta, isInteger);
            setting.Input.ActivateInputField();
            setting.Input.MoveTextEnd(false);
        }

        private static Button AddButton(GameObject target, Action action)
        {
            var button = target.GetComponent<Button>();
            if (button == null)
            {
                button = target.AddComponent<Button>();
            }

            EnsureRaycastGraphic(target);
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener((UnityAction)new Action(action));
            return button;
        }

        private static void AddTextButton(Transform parent, string text, float x, float y, float width, float height, bool active, Action action, string tooltip)
        {
            var image = CreateImage("Button_" + text, parent, active ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.24f));
            ApplyNativeSprite(image, _nativeButtonSprite ?? _nativeRowSprite, 1.2f);
            SetRect(image.rectTransform, x, y, width, height, x < 0 ? AnchorPresets.TopRight : AnchorPresets.TopLeft);
            AddButton(image.gameObject, action);
            var label = HologramLabel.Create(image.transform, text, _font, _fontMaterial, 19f, TextAlignmentOptions.Center);
            Stretch(label.RectTransform, 0f, 0f, 0f, 0f);
            label.SetSelected(active);
            AddHologramHover(image.gameObject, label, () => active, tooltip);
            _actionLabels.Add(label);
        }

        private static void AddHologramHover(GameObject target, HologramLabel label, Func<bool> isActive, string tooltip)
        {
            if (target == null || label == null)
            {
                return;
            }

            label.SetActiveStateReader(isActive);
            AddTooltip(target, tooltip);
            var trigger = GetOrCreateEventTrigger(target);

            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback = new EventTrigger.TriggerEvent();
            enter.callback.AddListener((UnityAction<BaseEventData>)new Action<BaseEventData>(_ =>
            {
                label.SetHovered(true);
                SetTooltip(tooltip);
            }));
            trigger.triggers.Add(enter);

            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback = new EventTrigger.TriggerEvent();
            exit.callback.AddListener((UnityAction<BaseEventData>)new Action<BaseEventData>(_ => label.SetHovered(false)));
            trigger.triggers.Add(exit);

            var select = new EventTrigger.Entry { eventID = EventTriggerType.Select };
            select.callback = new EventTrigger.TriggerEvent();
            select.callback.AddListener((UnityAction<BaseEventData>)new Action<BaseEventData>(_ =>
            {
                label.SetHovered(true);
                SetTooltip(tooltip);
            }));
            trigger.triggers.Add(select);
        }

        private static void AddTooltip(GameObject target, string tooltip)
        {
            if (target == null || string.IsNullOrWhiteSpace(tooltip))
            {
                return;
            }

            EnsureRaycastGraphic(target);
            var trigger = GetOrCreateEventTrigger(target);

            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback = new EventTrigger.TriggerEvent();
            enter.callback.AddListener((UnityAction<BaseEventData>)new Action<BaseEventData>(_ => SetTooltip(tooltip)));
            trigger.triggers.Add(enter);

            var select = new EventTrigger.Entry { eventID = EventTriggerType.Select };
            select.callback = new EventTrigger.TriggerEvent();
            select.callback.AddListener((UnityAction<BaseEventData>)new Action<BaseEventData>(_ => SetTooltip(tooltip)));
            trigger.triggers.Add(select);
        }

        private static EventTrigger GetOrCreateEventTrigger(GameObject target)
        {
            var trigger = target.GetComponent<EventTrigger>();
            if (trigger == null)
            {
                trigger = target.AddComponent<EventTrigger>();
            }

            return trigger;
        }

        private static void EnsureRaycastGraphic(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            var graphic = target.GetComponent<Graphic>();
            if (graphic != null)
            {
                graphic.raycastTarget = true;
                return;
            }

            var image = target.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.001f);
            image.raycastTarget = true;
        }

        private static void SetTooltip(string text)
        {
            OpenACAIUi.SetTooltip(text);
            if (_tooltipKeyText == null || _tooltipBodyText == null)
            {
                return;
            }

            SplitTooltip(text, out var key, out var body);
            var hasKey = !string.IsNullOrWhiteSpace(key);
            _tooltipKeyText.gameObject.SetActive(hasKey);
            _tooltipKeyText.text = hasKey ? key.ToUpperInvariant() + " -" : "";
            _tooltipBodyText.text = (body ?? "").ToUpperInvariant();
            SetRect(_tooltipBodyText.rectTransform, hasKey ? 390f : 24f, 0f, -24f, 36f, AnchorPresets.TopLeftStretchWidth);
        }

        private static void SplitTooltip(string text, out string key, out string body)
        {
            key = null;
            body = text ?? "";
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            foreach (var separator in new[] { " - ", ": " })
            {
                var index = body.IndexOf(separator, StringComparison.Ordinal);
                if (index <= 0 || index > 32 || index + separator.Length >= body.Length)
                {
                    continue;
                }

                key = body.Substring(0, index).Trim();
                body = body.Substring(index + separator.Length).Trim();
                return;
            }
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var go = new GameObject("OpenACAI EventSystem");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        private static TMP_FontAsset FindNativeFont()
        {
            return Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                .Where(text => text != null && text.font != null)
                .OrderByDescending(NativeTextScore)
                .Select(text => text.font)
                .FirstOrDefault();
        }

        private static Material FindNativeFontMaterial()
        {
            return Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                .Where(text => text != null && text.fontSharedMaterial != null)
                .OrderByDescending(NativeTextScore)
                .Select(text => text.fontSharedMaterial)
                .FirstOrDefault();
        }

        private static Sprite FindNativePanelSprite()
        {
            return FindNativeOptionsSprite(NativeSpriteKind.Panel);
        }

        private static int NativeTextScore(TextMeshProUGUI text)
        {
            var score = 0;
            var value = (text.text ?? "").Trim();
            var name = text.name ?? "";
            var path = BuildUiPath(text.transform);

            if (IsNativeOptionsLabel(value))
            {
                score += 160;
            }

            if (path.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 80;
            }

            if (name.IndexOf("TextBase", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 35;
            }

            if (text.fontStyle.HasFlag(FontStyles.Bold))
            {
                score += 10;
            }

            return score;
        }

        private static bool IsNativeOptionsLabel(string value)
        {
            return string.Equals(value, "DISPLAY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GRAPHICS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GAMEPLAY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "CONTROLS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "AUDIO", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "BACK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "RESET", StringComparison.OrdinalIgnoreCase);
        }

        private static Sprite FindNativeOptionsSprite(NativeSpriteKind kind)
        {
            return Resources.FindObjectsOfTypeAll<Image>()
                .Where(image => image != null
                    && image.sprite != null
                    && BuildUiPath(image.transform).IndexOf("OpenACAI", StringComparison.OrdinalIgnoreCase) < 0)
                .Select(image => new { image, score = NativeImageScore(image, kind) })
                .Where(result => result.score > 0)
                .OrderByDescending(result => result.score)
                .Select(result => result.image.sprite)
                .FirstOrDefault();
        }

        private static int NativeImageScore(Image image, NativeSpriteKind kind)
        {
            var name = image.name ?? "";
            var spriteName = image.sprite?.name ?? "";
            var path = BuildUiPath(image.transform);
            var haystack = name + " " + spriteName + " " + path;
            var rect = image.rectTransform != null ? image.rectTransform.rect : new Rect(0f, 0f, 0f, 0f);
            var score = 0;

            if (path.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 90;
            }

            if (path.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 25;
            }

            if (kind == NativeSpriteKind.Panel)
            {
                if (ContainsAny(haystack, "Background", "Panel", "Screen", "Frame")) score += 70;
                if (rect.width > 700f && rect.height > 350f) score += 40;
            }
            else if (kind == NativeSpriteKind.Row)
            {
                if (ContainsAny(haystack, "Option", "Setting", "Row", "Selector", "Value", "Background")) score += 55;
                if (rect.width > 250f && rect.height >= 18f && rect.height <= 70f) score += 35;
            }
            else if (kind == NativeSpriteKind.Button)
            {
                if (ContainsAny(haystack, "Button", "Tab", "Selector", "Option")) score += 65;
                if (rect.width > 80f && rect.height >= 24f && rect.height <= 80f) score += 25;
            }
            else if (kind == NativeSpriteKind.Input)
            {
                if (ContainsAny(haystack, "Input", "Value", "Slider", "Selector", "Option")) score += 65;
                if (rect.width > 180f && rect.height >= 18f && rect.height <= 70f) score += 25;
            }

            return score;
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            return tokens.Any(token => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void ApplyNativeSprite(Image image, Sprite sprite, float pixelsPerUnitMultiplier)
        {
            if (image == null || sprite == null)
            {
                return;
            }

            image.sprite = sprite;
            image.type = sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            image.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier;
        }

        private static string BuildUiPath(Transform transform)
        {
            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static void AddRoughOptionsEdges(Transform parent)
        {
            var go = new GameObject("RoughOptionsFrame");
            go.transform.SetParent(parent, false);
            var raw = go.AddComponent<RawImage>();
            raw.texture = CreateRoughFrameTexture(1024, 576);
            raw.color = Color.white;
            raw.raycastTarget = false;
            Stretch(raw.rectTransform, -18f, -16f, -18f, -18f);
        }

        private static Texture2D CreateRoughFrameTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var topDepth = 24f + Mathf.PerlinNoise(x * 0.018f, 3.7f) * 22f + Mathf.PerlinNoise(x * 0.073f, 9.1f) * 9f;
                    var bottomDepth = 30f + Mathf.PerlinNoise(x * 0.017f, 17.2f) * 28f + Mathf.PerlinNoise(x * 0.061f, 22.4f) * 11f;
                    var leftDepth = 24f + Mathf.PerlinNoise(y * 0.021f, 31.5f) * 24f;
                    var rightDepth = 24f + Mathf.PerlinNoise(y * 0.019f, 41.8f) * 24f;

                    var alpha = Mathf.Max(
                        EdgeAlpha(height - 1 - y, topDepth),
                        EdgeAlpha(y, bottomDepth),
                        EdgeAlpha(x, leftDepth),
                        EdgeAlpha(width - 1 - x, rightDepth));

                    var scratch = Mathf.PerlinNoise((x + 91f) * 0.12f, (y + 53f) * 0.10f);
                    alpha = Mathf.Clamp01(alpha * (0.68f + scratch * 0.24f));
                    if (alpha < 0.025f)
                    {
                        alpha = 0f;
                    }

                    texture.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
                }
            }

            texture.Apply(false, true);
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private static float EdgeAlpha(float distance, float depth)
        {
            if (distance > depth)
            {
                return 0f;
            }

            var t = 1f - (distance / Mathf.Max(1f, depth));
            return Mathf.SmoothStep(0f, 1f, t);
        }

        private static void AddRoughEdge(Transform parent, string name, int width, int height, RoughEdgeSide side, AnchorPresets preset, float x, float y, float rectWidth, float rectHeight)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var raw = go.AddComponent<RawImage>();
            raw.texture = CreateRoughEdgeTexture(width, height, side);
            raw.color = Color.white;
            raw.raycastTarget = false;
            SetRect(raw.rectTransform, x, y, rectWidth, rectHeight, preset);
        }

        private static Texture2D CreateRoughEdgeTexture(int width, int height, RoughEdgeSide side)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var axis = side == RoughEdgeSide.Top || side == RoughEdgeSide.Bottom ? x : y;
                    var distance = side == RoughEdgeSide.Top
                        ? height - 1 - y
                        : side == RoughEdgeSide.Bottom
                            ? y
                            : side == RoughEdgeSide.Left
                                ? width - 1 - x
                                : x;
                    var n1 = Mathf.PerlinNoise(axis * 0.045f, (int)side * 9.37f);
                    var n2 = Mathf.PerlinNoise(axis * 0.19f, (int)side * 5.13f + 17f);
                    var jaggedDepth = 14f + (n1 * 24f) + (n2 * 10f);
                    var fade = Mathf.Clamp01(1f - (distance / jaggedDepth));
                    var scratch = Mathf.PerlinNoise((x + 41f) * 0.13f, (y + 73f) * 0.11f);
                    var alpha = Mathf.Clamp01((fade * 0.92f) + (scratch > 0.64f ? 0.16f : 0f));
                    texture.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
                }
            }

            texture.Apply(false, true);
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private static void AddSoftBand(Transform parent, string name, AnchorPresets preset, Color color, float x, float y, float width, float height)
        {
            var image = CreateImage(name, parent, color);
            SetRect(image.rectTransform, x, y, width, height, preset);
        }

        private static void SetRect(RectTransform rect, float x, float y, float width, float height, AnchorPresets preset)
        {
            if (preset == AnchorPresets.TopLeft)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, y);
                rect.sizeDelta = new Vector2(width, height);
            }
            else if (preset == AnchorPresets.TopRight)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(1f, 1f);
                rect.anchoredPosition = new Vector2(x, y);
                rect.sizeDelta = new Vector2(width, height);
            }
            else if (preset == AnchorPresets.BottomLeft)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
                rect.pivot = new Vector2(0f, 0f);
                rect.anchoredPosition = new Vector2(x, y);
                rect.sizeDelta = new Vector2(width, height);
            }
            else if (preset == AnchorPresets.BottomRight)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(1f, 0f);
                rect.anchoredPosition = new Vector2(x, y);
                rect.sizeDelta = new Vector2(width, height);
            }
            else if (preset == AnchorPresets.TopStretch)
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.offsetMin = new Vector2(x, y - height);
                rect.offsetMax = new Vector2(-width, y);
            }
            else if (preset == AnchorPresets.BottomStretch)
            {
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.offsetMin = new Vector2(x, y);
                rect.offsetMax = new Vector2(-width, y + height);
            }
            else if (preset == AnchorPresets.TopLeftStretchWidth)
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(width < 0f ? 1f : 0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.offsetMin = new Vector2(x, y - height);
                rect.offsetMax = width < 0f ? new Vector2(width, y) : new Vector2(x + width, y);
            }
            else if (preset == AnchorPresets.LeftStretch)
            {
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.offsetMin = new Vector2(x, y);
                rect.offsetMax = new Vector2(x + width, -height);
            }
            else if (preset == AnchorPresets.RightStretch)
            {
                rect.anchorMin = new Vector2(1f, 0f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(1f, 0.5f);
                rect.offsetMin = new Vector2(x, y);
                rect.offsetMax = new Vector2(x + width, -height);
            }
        }

        private static void SetTabRect(RectTransform rect, int index, int count)
        {
            var safeCount = Math.Max(1, count);
            var span = 0.84f / safeCount;
            var left = 0.14f + (index * span);
            var right = left + span;

            rect.anchorMin = new Vector2(left, 1f);
            rect.anchorMax = new Vector2(right, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(8f, -160f);
            rect.offsetMax = new Vector2(-8f, -112f);
        }

        private static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static string BuildMetaLine()
        {
            return OpenAcaiModMenuState.BuildMetaLine(OpenACAIEndnightRuntime.Version);
        }

        private static string StatusText(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return "-";
            }

            if (string.Equals(entry.Status, "hosted", StringComparison.OrdinalIgnoreCase)) return "HOSTED";
            if (string.Equals(entry.Status, "ready", StringComparison.OrdinalIgnoreCase)) return "READY";
            if (string.Equals(entry.Status, "missing", StringComparison.OrdinalIgnoreCase)) return "MISSING";
            if (string.Equals(entry.Status, "warning", StringComparison.OrdinalIgnoreCase)) return "CHECK";
            if (string.Equals(entry.Status, "disabled", StringComparison.OrdinalIgnoreCase)) return "OFF";
            return "INFO";
        }

        private static string TypeText(ModInventoryEntry entry)
        {
            if (OpenACAIModLoaderTypes.IsLibrary(entry?.LoaderType)
                && string.Equals(entry?.SourceFamily, OpenACAIModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
            {
                return "Modstoke library";
            }

            if (string.Equals(entry?.SourceFamily, OpenACAIModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase)) return "Modstoke";
            if (OpenACAIModLoaderTypes.IsLegacyBepInEx(entry?.LoaderType)) return "BepInEx shim";
            if (string.Equals(entry?.LoaderType, OpenACAIModLoaderTypes.RedLoaderLibrary, StringComparison.OrdinalIgnoreCase)) return "RL lib shim";
            if (OpenACAIModLoaderTypes.IsLegacyRedLoader(entry?.LoaderType)) return "RL mod shim";
            return "Unknown";
        }

        private static string SourceText(ModInventoryEntry entry)
        {
            if (entry == null) return "-";
            if (!string.IsNullOrWhiteSpace(entry.Author)) return entry.Author;
            return "Unknown";
        }

        private static string DetailText(ModInventoryEntry entry)
        {
            if (entry == null) return "";
            if (!string.IsNullOrWhiteSpace(entry.Issue)) return entry.Issue;
            if (entry.HostedByOpenAcai) return "Running through Modstoke";
            if (string.Equals(entry.SourceFamily, OpenACAIModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase)) return "Modstoke-native package detected";
            if (entry.Hostable) return "Detected and ready for controlled hosting";
            if (!entry.HasAssembly && entry.ManifestPath != null) return "Manifest only";
            return entry.Store ?? entry.ExpectedLocation ?? "";
        }

        private static Color StatusColor(ModInventoryEntry entry)
        {
            var status = entry?.Status ?? "";
            if (string.Equals(status, "hosted", StringComparison.OrdinalIgnoreCase)) return new Color(0.35f, 0.82f, 1f, 1f);
            if (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)) return new Color(0.42f, 0.95f, 0.64f, 1f);
            if (string.Equals(status, "missing", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.36f, 0.32f, 1f);
            if (string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.78f, 0.34f, 1f);
            return MutedColor();
        }

        private static string BuildProviderMetaLine(OpenACAIMenuProviderSnapshot provider)
        {
            if (provider == null)
            {
                return BuildMetaLine();
            }

            return Shorten(provider.Source, 38)
                + "  |  " + provider.Kind
                + "  |  " + (provider.RequiresAuthorization ? "Authorization required" : "No authorization gate")
                + "  |  " + ProviderSurfaceDetail(provider);
        }

        private static string ProviderSurfaceText(OpenACAIMenuProviderSnapshot provider)
        {
            if (provider == null)
            {
                return "METADATA";
            }

            if (provider.HasContent && provider.HasCommands)
            {
                return "CONTENT+CMD";
            }

            if (provider.HasCommands)
            {
                return "COMMANDS";
            }

            return provider.HasContent ? "CONTENT" : "METADATA";
        }

        private static string ProviderSurfaceDetail(OpenACAIMenuProviderSnapshot provider)
        {
            if (provider == null)
            {
                return "Metadata only";
            }

            if (provider.HasContent && provider.HasCommands)
            {
                return "Structured content and commands";
            }

            if (provider.HasCommands)
            {
                return "Command provider";
            }

            return provider.HasContent ? "Structured content" : "Metadata only";
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

        private static string ProviderCommandTooltip(OpenACAICommandSnapshot command)
        {
            if (command == null)
            {
                return "COMMAND - No command selected.";
            }

            var detail = string.IsNullOrWhiteSpace(command.Description)
                ? ProviderCommandActionText(command)
                : command.Description;
            return command.DisplayName + " - " + detail;
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

        private static string ProviderSettingTooltip(OpenACAIConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return "SETTING - No setting selected.";
            }

            var detail = string.IsNullOrWhiteSpace(setting.Tooltip) ? ProviderSettingDetail(setting) : setting.Tooltip;
            return setting.DisplayName + " - " + detail;
        }

        private static Color ProviderStatusColor(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return MutedColor();
            }

            var value = status.Trim();
            if (ContainsAny(value, "hosted", "connected", "online"))
            {
                return new Color(0.35f, 0.82f, 1f, 1f);
            }

            if (ContainsAny(value, "ready", "ok", "enabled", "running", "active", "loaded"))
            {
                return new Color(0.42f, 0.95f, 0.64f, 1f);
            }

            if (ContainsAny(value, "missing", "error", "failed", "fail", "off", "disabled", "blocked"))
            {
                return new Color(1f, 0.36f, 0.32f, 1f);
            }

            if (ContainsAny(value, "check", "warn", "pending", "auth", "required"))
            {
                return new Color(1f, 0.78f, 0.34f, 1f);
            }

            return MutedColor();
        }

        private static Color MutedColor() => new Color(0.72f, 0.74f, 0.72f, 0.92f);

        private static Color HeaderColor() => new Color(0.80f, 0.84f, 0.82f, 0.88f);

        private static string PrettyKey(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? "" : key.Replace("_", " ").Replace("-", " ").ToUpperInvariant();
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
                || string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "disabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            return false;
        }

        private static bool TryParseNumber(string value, out double result, out bool isInteger)
        {
            result = 0d;
            isInteger = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            isInteger = normalized.IndexOf('.') < 0
                && normalized.IndexOf(',') < 0
                && normalized.IndexOf('e') < 0
                && normalized.IndexOf('E') < 0;
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static string FormatNumber(double value, bool isInteger)
        {
            return isInteger
                ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string BoolDisplay(bool value)
        {
            return value ? "ENABLED" : "DISABLED";
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }

            return value.Substring(0, Math.Max(0, max - 1)) + ".";
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Item";
            }

            return new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        }

        private enum AnchorPresets
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
            TopStretch,
            BottomStretch,
            TopLeftStretchWidth,
            LeftStretch,
            RightStretch
        }

        private enum RoughEdgeSide
        {
            Top,
            Bottom,
            Left,
            Right
        }

        private enum NativeSpriteKind
        {
            Panel,
            Row,
            Button,
            Input
        }

        private sealed class ConfigSetting
        {
            internal int LineIndex;
            internal string Section;
            internal string Key;
            internal string Value;
            internal string InitialValue;
            internal string Tooltip;
            internal string ControlKind;
            internal TMP_InputField Input;
            internal TextMeshProUGUI Label;
            internal Image Image;

            internal string CurrentValue()
            {
                if (string.Equals(ControlKind, "bool", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                }

                return Input?.text ?? Value ?? "";
            }
        }

        private sealed class HologramLabel
        {
            internal GameObject Root;
            internal RectTransform RectTransform;
            private TextMeshProUGUI _base;
            private TextMeshProUGUI _red;
            private TextMeshProUGUI _green;
            private TextMeshProUGUI _blue;
            private bool _selected;
            private bool _hovered;
            private Func<bool> _activeStateReader;

            internal bool IsAlive => Root != null;

            internal static HologramLabel Create(Transform parent, string text, TMP_FontAsset font, Material material, float size, TextAlignmentOptions alignment)
            {
                var root = new GameObject("Hologram_" + text);
                root.transform.SetParent(parent, false);
                var rect = root.AddComponent<RectTransform>();
                var label = new HologramLabel
                {
                    Root = root,
                    RectTransform = rect,
                    _red = CreateLayer(root.transform, "TextRNormal", text, font, material, size, alignment, new Color(1f, 0.05f, 0.32f, 0.84f)),
                    _green = CreateLayer(root.transform, "TextGNormal", text, font, material, size, alignment, new Color(0.16f, 1f, 0.36f, 0.72f)),
                    _blue = CreateLayer(root.transform, "TextBNormal", text, font, material, size, alignment, new Color(0.18f, 0.62f, 1f, 0.80f)),
                    _base = CreateLayer(root.transform, "TextBase", text, font, material, size, alignment, Color.white)
                };
                label.SetSelected(false);
                return label;
            }

            internal void SetSelected(bool selected)
            {
                _selected = selected;
                RefreshState();
            }

            internal void SetHovered(bool hovered)
            {
                _hovered = hovered;
                RefreshState();
            }

            internal void SetActiveStateReader(Func<bool> activeStateReader)
            {
                _activeStateReader = activeStateReader;
                RefreshState();
            }

            private void RefreshState()
            {
                var active = _selected || _hovered || (_activeStateReader?.Invoke() ?? false);
                _red.gameObject.SetActive(active);
                _green.gameObject.SetActive(active);
                _blue.gameObject.SetActive(active);
                _base.color = active ? Color.white : new Color(0.62f, 0.62f, 0.60f, 0.95f);
            }

            internal void Update(float phase)
            {
                RefreshState();
                if (!(_selected || _hovered || (_activeStateReader?.Invoke() ?? false)))
                {
                    return;
                }

                SetLayerOffset(_red.rectTransform, -3.2f + Mathf.Sin(phase * 13.1f) * 0.45f, -1.4f);
                SetLayerOffset(_green.rectTransform, -1.2f + Mathf.Sin(phase * 17.3f) * 0.35f, 2.2f);
                SetLayerOffset(_blue.rectTransform, 3.1f + Mathf.Sin(phase * 15.7f) * 0.45f, 1.3f);
            }

            private static TextMeshProUGUI CreateLayer(Transform parent, string name, string text, TMP_FontAsset font, Material material, float size, TextAlignmentOptions alignment, Color color)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.text = text;
                tmp.fontSize = size;
                tmp.fontStyle = FontStyles.Bold;
                tmp.characterSpacing = 8f;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.alignment = alignment;
                tmp.color = color;
                tmp.margin = Vector4.zero;
                if (font != null)
                {
                    tmp.font = font;
                }

                if (material != null)
                {
                    tmp.fontSharedMaterial = material;
                }

                StretchLayer(tmp.rectTransform);
                return tmp;
            }

            private static void StretchLayer(RectTransform rect)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            private static void SetLayerOffset(RectTransform rect, float x, float y)
            {
                rect.offsetMin = new Vector2(x, y);
                rect.offsetMax = new Vector2(x, y);
            }
        }
    }

}

