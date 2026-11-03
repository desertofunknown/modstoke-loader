// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using RedLoader;
using global::Sons.Gui;
using global::Sons.Gui.Input;
using global::Sons.Gui.Options;
using global::Sons.UiElements;
using SonsSdk;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.PropertyVariants;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Sui = SUI.SUI;

namespace Modstoke.Loader
{
    internal sealed class UiPrefabs : ISdkUiPrefabDriver
    {
        internal static readonly UiPrefabs Instance = new();
        private bool _prepared;

        private UiPrefabs() { }

        public void Initialize()
        {
            if (_prepared)
                return;

            var previous = (Sui._sonsBackgroundSprite, Sui._roundBackgroundSprite,
                Sui._sliderPrefab, Sui._optionsPrefab, Sui._textPrefab, Sui._labelDividerPrefab,
                Sui._inputPrefab, Sui._buttonPrefab, Sui._bgButtonPrefab, Sui._menuButtonPrefab,
                Sui._scrollContainerPrefab, Sui._keybindPrefab, Sui._togglePrefab,
                Sui._maskedImagePrefab, Sui._tabControllerPrefab);
            var createdObjects = new List<GameObject>();
            GameObject staging = null;
            try
            {
                FindPrefabs();
                staging = new GameObject("SUI Prefab Staging");
                staging.SetActive(false);
                CopyPrefabs(staging.transform, createdObjects);
                PreparePrefabs();

                Sui._togglePrefab = Sui.CreateTogglePrefab();
                createdObjects.Add(Sui._togglePrefab);
                Sui._maskedImagePrefab = Sui.CreateMaskedImagePrefab();
                createdObjects.Add(Sui._maskedImagePrefab);
                Sui._tabControllerPrefab = Sui.CreateTabControllerPrefab();
                createdObjects.Add(Sui._tabControllerPrefab);
                _prepared = true;
            }
            catch
            {
                (Sui._sonsBackgroundSprite, Sui._roundBackgroundSprite,
                    Sui._sliderPrefab, Sui._optionsPrefab, Sui._textPrefab, Sui._labelDividerPrefab,
                    Sui._inputPrefab, Sui._buttonPrefab, Sui._bgButtonPrefab, Sui._menuButtonPrefab,
                    Sui._scrollContainerPrefab, Sui._keybindPrefab, Sui._togglePrefab,
                    Sui._maskedImagePrefab, Sui._tabControllerPrefab) = previous;
                for (var i = createdObjects.Count - 1; i >= 0; i--)
                    Discard(createdObjects[i]);
                throw;
            }
            finally
            {
                Discard(staging);
            }

            // The menu theme is optional and must never roll back the shared SUI prefabs.
            try
            {
                MenuTheme.Capture();
            }
            catch (Exception exception)
            {
                RLog.Warning("Failed to capture the Modstoke menu theme: " + exception.Message);
            }
        }

        private static void FindPrefabs()
        {
            var optionsPanel = Resources.FindObjectsOfTypeAll<OptionsGuiManager>().FirstWithName("OptionsPanel");
            var displayOptions = optionsPanel._optionGroups._items.FirstWithName("DisplayPanel");
            var gameplayOptions = optionsPanel._optionGroups._items.FirstWithName("GameplayPanel");
            var controlsOptions = optionsPanel._optionGroups._items.FirstWithName("ControlsPanel");
            var dialog = ModalDialogManager._instance.transform.Find("DynamicModalDialogGui/Panel");

            Sui._sonsBackgroundSprite = Sui.SpriteBackground400ppu;
            Sui._roundBackgroundSprite = Sui.SpriteBackground;

            Sui._sliderPrefab = displayOptions.Get<TargetFrameRateOptionGui>()._optionGuiRoot;
            Sui._optionsPrefab = displayOptions.Get<FullscreenOptionGui>()._optionGuiRoot;
            Sui._labelDividerPrefab = gameplayOptions.Get<FovOffsetOptionGui>()._optionGuiRoot.transform.parent.parent.Find("LabelPanel").gameObject;
            Sui._textPrefab = dialog.Find("Content").gameObject;
            Sui._inputPrefab = dialog.Find("InputField").gameObject;
            Sui._buttonPrefab = dialog.Find("ButtonsLayout/BackButton").gameObject;
            Sui._scrollContainerPrefab = Sui._labelDividerPrefab.transform.parent.parent.parent.parent.parent.gameObject;
            Sui._keybindPrefab = controlsOptions.GetComponentInChildren<RebindingInputOptionGui>(true).gameObject;

            var menuButton = GameMenuBridge.FindTitleTemplate();
            Sui._menuButtonPrefab = menuButton != null ? menuButton.gameObject : null;
        }

        private static void CopyPrefabs(Transform staging, List<GameObject> createdObjects)
        {
            CheckForNull(Sui._sonsBackgroundSprite, nameof(Sui._sonsBackgroundSprite));
            CheckForNull(Sui._roundBackgroundSprite, nameof(Sui._roundBackgroundSprite));
            CheckForNull(Sui._sliderPrefab, nameof(Sui._sliderPrefab));
            CheckForNull(Sui._optionsPrefab, nameof(Sui._optionsPrefab));
            CheckForNull(Sui._textPrefab, nameof(Sui._textPrefab));
            CheckForNull(Sui._labelDividerPrefab, nameof(Sui._labelDividerPrefab));
            CheckForNull(Sui._inputPrefab, nameof(Sui._inputPrefab));
            CheckForNull(Sui._buttonPrefab, nameof(Sui._buttonPrefab));
            CheckForNull(Sui._menuButtonPrefab, nameof(Sui._menuButtonPrefab));
            CheckForNull(Sui._scrollContainerPrefab, nameof(Sui._scrollContainerPrefab));
            CheckForNull(Sui._colorWheelPrefab, nameof(Sui._colorWheelPrefab));
            CheckForNull(Sui._keybindPrefab, nameof(Sui._keybindPrefab));

            Sui._sliderPrefab = CopyPrefab(Sui._sliderPrefab, staging, createdObjects);
            Sui._optionsPrefab = CopyPrefab(Sui._optionsPrefab, staging, createdObjects);
            Sui._textPrefab = CopyPrefab(Sui._textPrefab, staging, createdObjects);
            Sui._labelDividerPrefab = CopyPrefab(Sui._labelDividerPrefab, staging, createdObjects);
            Sui._inputPrefab = CopyPrefab(Sui._inputPrefab, staging, createdObjects);
            Sui._buttonPrefab = CopyPrefab(Sui._buttonPrefab, staging, createdObjects);
            Sui._bgButtonPrefab = new GameObject("BgButton");
            createdObjects.Add(Sui._bgButtonPrefab);
            Sui._bgButtonPrefab.DontDestroyOnLoad().HideAndDontSave();
            Sui._menuButtonPrefab = GameMenuBridge.CloneInactiveTemplate(Sui._menuButtonPrefab, true);
            createdObjects.Add(Sui._menuButtonPrefab);
            Sui._menuButtonPrefab.DontDestroyOnLoad().HideAndDontSave();
            Sui._scrollContainerPrefab = CopyPrefab(Sui._scrollContainerPrefab, staging, createdObjects);
            Sui._keybindPrefab = CopyPrefab(Sui._keybindPrefab, staging, createdObjects);
            Sui._colorWheelPrefab.DontDestroyOnLoad().HideAndDontSave();
        }

        private static void PreparePrefabs()
        {
            RemoveComponent<LocalizeStringEvent>(Sui._sliderPrefab.FindGet<TextMeshProUGUI>("LabelPanel/Label").gameObject);
            Sui._sliderPrefab.SetActive(false);
            Sui._sliderPrefab.name = "Slider";

            var dropdown = Sui._optionsPrefab.FindGet<SonsDropdown>("DropdownPanel/Dropdown");
            var optionLabel = Sui._optionsPrefab.FindGet<TextMeshProUGUI>("LabelPanel/Label");
            RemoveComponent<LocalizeStringEvent>(optionLabel.gameObject);
            RemoveComponent<GameObjectLocalizer>(dropdown.gameObject);
            dropdown.ClearOptions();
            dropdown.m_Options.options.Clear();
            dropdown.options.Clear();
            Sui._optionsPrefab.SetActive(false);
            Sui._optionsPrefab.name = "Options";

            RemoveComponent<LocalizeStringEvent>(Sui._labelDividerPrefab.FindGet<TextMeshProUGUI>("ScreenLabel").gameObject);
            Sui._labelDividerPrefab.SetActive(false);
            Sui._labelDividerPrefab.name = "Divider";

            var label = Sui._textPrefab.GetComponent<TextMeshProUGUI>();
            RemoveComponent<LocalizeStringEvent>(label.gameObject);
            RemoveComponent<LayoutElement>(label.gameObject);
            RemoveComponent<ContentSizeFitter>(label.gameObject);
            label.margin = new Vector4(0, 0, 0, 0);
            label.enableWordWrapping = false;
            label.fontSizeMin = 0;
            label.fontSizeMax = 60;
            label.enableAutoSizing = false;
            label.alignment = TextAlignmentOptions.Center;
            Sui._textPrefab.SetActive(false);
            Sui._textPrefab.name = "Label";

            var input = Sui._inputPrefab.FindGet<TMP_InputField>("InputPanel/InputField");
            var placeholder = input.placeholder.GetComponent<TextMeshProUGUI>();
            input.gameObject.SetActive(true);
            var inputLabel = Sui._inputPrefab.FindGet<TextMeshProUGUI>("Label");
            RemoveComponent<LocalizeStringEvent>(inputLabel.gameObject);
            inputLabel.gameObject.SetActive(true);

            var layout = Sui._inputPrefab.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0;
            layout.childForceExpandWidth = true;
            placeholder.color = new Color(1, 1, 1, 0.2f);
            inputLabel.enableAutoSizing = false;
            inputLabel.fontSize = 20;
            Sui._inputPrefab.SetActive(false);
            Sui._inputPrefab.name = "TextBox";

            var buttonLabel = Sui._buttonPrefab.FindGet<TextMeshProUGUI>("ContentPanel/TextBase");
            buttonLabel.fontSize = 30;
            buttonLabel.text = "Button";
            buttonLabel.margin = new Vector4(0, 0, 0, 0);
            RemoveComponent<LocalizeStringEvent>(Sui._buttonPrefab);
            Sui._buttonPrefab.GetComponent<Button>().onClick = new Button.ButtonClickedEvent();
            Sui._buttonPrefab.SetActive(false);
            Sui._buttonPrefab.name = "Button";

            var keybindButton = Sui._keybindPrefab.FindGet<Button>("InputSelectionPanel/Panel/InputButton01");
            keybindButton.onClick = new Button.ButtonClickedEvent();
            Sui._keybindPrefab.SetActive(false);

            var scrollContent = Sui._scrollContainerPrefab.transform.Find("Viewport/Content");
            for (var i = scrollContent.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(scrollContent.GetChild(i).gameObject);

            Sui._scrollContainerPrefab.SetActive(false);
            Sui._scrollContainerPrefab.name = "SUI_ScrollContainer";
        }

        private static void CheckForNull(Object value, string memberName)
        {
            if (!value)
                throw new InvalidOperationException("Required SUI prefab source is unavailable: " + memberName);
        }

        private static GameObject CopyPrefab(GameObject source, Transform staging, List<GameObject> createdObjects)
        {
            var clone = Object.Instantiate(source, staging, false);
            createdObjects.Add(clone);
            clone.SetActive(false);
            clone.transform.SetParent(null, false);
            return clone.DontDestroyOnLoad().HideAndDontSave();
        }

        private static void RemoveComponent<T>(GameObject gameObject) where T : Component
        {
            var component = gameObject.GetComponent<T>();
            if (component)
                Object.DestroyImmediate(component);
        }

        private static void Discard(GameObject gameObject)
        {
            if (!gameObject)
                return;
            try { gameObject.SetActive(false); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
            try { Object.Destroy(gameObject); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Warning("Failed to clean up an incomplete SUI prefab: " + exception); }
            catch { /* Preserve the initialization failure. */ }
        }
    }
}
