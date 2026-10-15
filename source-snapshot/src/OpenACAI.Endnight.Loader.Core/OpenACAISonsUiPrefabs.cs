// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using RedLoader;
using Sons.Gui;
using Sons.Gui.Input;
using Sons.Gui.Options;
using Sons.UiElements;
using SonsSdk;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.PropertyVariants;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Sui = SUI.SUI;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsUiPrefabs : ISdkUiPrefabDriver
    {
        internal static readonly OpenACAISonsUiPrefabs Instance = new();

        private OpenACAISonsUiPrefabs() { }

        public void Initialize()
        {
            FindPrefabs();
            CopyPrefabs();
            PreparePrefabs();
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

            foreach (var button in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (button.name == "SinglePlayerButton")
                {
                    Sui._menuButtonPrefab = button.gameObject;
                    Sui._titleMenuButtonsContainer = button.transform.parent;
                }
            }

            Sui._togglePrefab = Sui.CreateTogglePrefab();
            Sui._maskedImagePrefab = Sui.CreateMaskedImagePrefab();
            Sui._tabControllerPrefab = Sui.CreateTabControllerPrefab();
        }

        private static void CopyPrefabs()
        {
            CheckForNull(Sui._sonsBackgroundSprite, nameof(Sui._sonsBackgroundSprite));
            CheckForNull(Sui._roundBackgroundSprite, nameof(Sui._roundBackgroundSprite));
            CheckForNull(Sui._sliderPrefab, nameof(Sui._sliderPrefab));
            CheckForNull(Sui._optionsPrefab, nameof(Sui._optionsPrefab));
            CheckForNull(Sui._textPrefab, nameof(Sui._textPrefab));
            CheckForNull(Sui._labelDividerPrefab, nameof(Sui._labelDividerPrefab));
            CheckForNull(Sui._togglePrefab, nameof(Sui._togglePrefab));
            CheckForNull(Sui._inputPrefab, nameof(Sui._inputPrefab));
            CheckForNull(Sui._buttonPrefab, nameof(Sui._buttonPrefab));
            CheckForNull(Sui._maskedImagePrefab, nameof(Sui._maskedImagePrefab));
            CheckForNull(Sui._menuButtonPrefab, nameof(Sui._menuButtonPrefab));
            CheckForNull(Sui._scrollContainerPrefab, nameof(Sui._scrollContainerPrefab));
            CheckForNull(Sui._colorWheelPrefab, nameof(Sui._colorWheelPrefab));
            CheckForNull(Sui._keybindPrefab, nameof(Sui._keybindPrefab));

            Sui._sliderPrefab = CopyPrefab(Sui._sliderPrefab);
            Sui._optionsPrefab = CopyPrefab(Sui._optionsPrefab);
            Sui._textPrefab = CopyPrefab(Sui._textPrefab);
            Sui._labelDividerPrefab = CopyPrefab(Sui._labelDividerPrefab);
            Sui._inputPrefab = CopyPrefab(Sui._inputPrefab);
            Sui._buttonPrefab = CopyPrefab(Sui._buttonPrefab);
            Sui._bgButtonPrefab = new GameObject("BgButton").DontDestroyOnLoad().HideAndDontSave();
            Sui._menuButtonPrefab = CopyPrefab(Sui._menuButtonPrefab);
            Sui._scrollContainerPrefab = CopyPrefab(Sui._scrollContainerPrefab);
            Sui._keybindPrefab = CopyPrefab(Sui._keybindPrefab);
            Sui._colorWheelPrefab.DontDestroyOnLoad().HideAndDontSave();
        }

        private static void PreparePrefabs()
        {
            Sui._sliderPrefab.FindGet<TextMeshProUGUI>("LabelPanel/Label").gameObject.Destroy<LocalizeStringEvent>();
            Sui._sliderPrefab.SetActive(false);
            Sui._sliderPrefab.name = "Slider";

            var dropdown = Sui._optionsPrefab.FindGet<SonsDropdown>("DropdownPanel/Dropdown");
            var optionLabel = Sui._optionsPrefab.FindGet<TextMeshProUGUI>("LabelPanel/Label");
            optionLabel.gameObject.Destroy<LocalizeStringEvent>();
            dropdown.gameObject.Destroy<GameObjectLocalizer>();
            dropdown.ClearOptions();
            dropdown.m_Options.options.Clear();
            dropdown.options.Clear();
            Sui._optionsPrefab.SetActive(false);
            Sui._optionsPrefab.name = "Options";

            Sui._labelDividerPrefab.FindGet<TextMeshProUGUI>("ScreenLabel").gameObject.Destroy<LocalizeStringEvent>();
            Sui._labelDividerPrefab.SetActive(false);
            Sui._labelDividerPrefab.name = "Divider";

            var label = Sui._textPrefab.GetComponent<TextMeshProUGUI>();
            label.gameObject.Destroy<LocalizeStringEvent>();
            label.gameObject.Destroy<LayoutElement>();
            label.gameObject.Destroy<ContentSizeFitter>();
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
            inputLabel.gameObject.Destroy<LocalizeStringEvent>();
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
            Sui._buttonPrefab.Destroy<LocalizeStringEvent>();
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
                RLog.Error($"= {memberName} is null! =");
        }

        private static GameObject CopyPrefab(GameObject source)
        {
            return Object.Instantiate(source).DontDestroyOnLoad().HideAndDontSave();
        }
    }
}
