// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using global::Sons.Gui.Options;
using SonsSdk;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Sui = SUI.SUI;

namespace Modstoke.Loader
{
    internal enum MenuThemeTier
    {
        Plain,
        Mixed,
        Native
    }

    // The SUI templates are shared with other mods, so the menu instantiates them and never edits them.
    internal static class MenuTheme
    {
        internal const string BoldFontName = "RobotoCondensed-Bold SDF";
        internal const string RegularFontName = "RobotoCondensed-Regular SDF";
        private const string OptionsPanelName = "OptionsPanel";
        private static readonly Color PlainPanelColor = new Color(0.078f, 0.078f, 0.078f, 0.92f);
        private static GameObject _scrollContainer;
        private static GameObject _options;
        private static GameObject _slider;
        private static GameObject _labelDivider;
        private static GameObject _input;
        private static GameObject _button;
        private static GameObject _menuButton;
        private static TMP_FontAsset _headingFont;
        private static Material _headingMaterial;
        private static TMP_FontAsset _bodyFont;
        private static Material _bodyMaterial;
        private static Sprite _rowSprite;
        private static Sprite _buttonSprite;
        private static Sprite _inputSprite;
        private static bool _hasOptionColors;
        private static Color _activeColor;
        private static Color _inactiveColor;

        internal static GameObject ScrollContainerTemplate => Live(_scrollContainer);
        internal static GameObject OptionsTemplate => Live(_options);
        internal static GameObject SliderTemplate => Live(_slider);
        internal static GameObject LabelDividerTemplate => Live(_labelDivider);
        internal static GameObject InputTemplate => Live(_input);
        internal static GameObject ButtonTemplate => Live(_button);
        internal static GameObject MenuButtonTemplate => Live(_menuButton);

        internal static TMP_FontAsset HeadingFont => Live(_headingFont) ?? Live(GameResources.GetFont(BoldFontName));
        // A template material only matches its own font atlas.
        internal static Material HeadingMaterial => Live(_headingFont) != null ? Live(_headingMaterial) : null;
        internal static TMP_FontAsset BodyFont => Live(_bodyFont) ?? Live(GameResources.GetFont(RegularFontName));
        internal static Material BodyMaterial => Live(_bodyFont) != null ? Live(_bodyMaterial) : null;

        internal static Sprite RowSprite => Live(_rowSprite);
        internal static Sprite ButtonSprite => Live(_buttonSprite) ?? RowSprite;
        internal static Sprite InputSprite => Live(_inputSprite) ?? RowSprite;
        internal static Sprite PanelSprite => Live(Sui.GetBackgroundSprite(EBackground.Sons));
        internal static Material PanelMaterial => Live(GameResources.GetMaterial(MaterialAssetMap.DarkGreyBackground));

        internal static bool HasOptionColors => _hasOptionColors;
        internal static Color ActiveColor => _activeColor;
        internal static Color InactiveColor => _inactiveColor;

        internal static MenuThemeTier Tier
        {
            get
            {
                var templates = Count(_scrollContainer) + Count(_options) + Count(_slider) + Count(_labelDivider)
                    + Count(_input) + Count(_button) + Count(_menuButton);
                if (templates == 0)
                    return MenuThemeTier.Plain;

                return templates == 7 && Live(_headingFont) != null && Live(_bodyFont) != null
                    && PanelSprite != null && PanelMaterial != null
                    ? MenuThemeTier.Native
                    : MenuThemeTier.Mixed;
            }
        }

        internal static string TierName => Tier switch
        {
            MenuThemeTier.Native => "native",
            MenuThemeTier.Mixed => "mixed",
            _ => "plain"
        };

        internal static void Capture()
        {
            var optionsPanel = FindOptionsPanel();
            var heading = FindGroupLabel(optionsPanel) ?? Find<TMP_Text>(Sui._labelDividerPrefab, "ScreenLabel");
            var body = Find<TMP_Text>(Sui._optionsPrefab, "LabelPanel/Label");
            var input = Find<TMP_InputField>(Sui._inputPrefab, "InputPanel/InputField");
            var button = Sui._buttonPrefab ? Sui._buttonPrefab.GetComponent<Button>() : null;
            var rowSprite = SpriteOf(Find<Image>(Sui._optionsPrefab, "DropdownPanel/DropdownBacking"));
            var buttonSprite = button != null ? SpriteOf(button.image) : null;
            var inputSprite = input != null ? SpriteOf(input.image) : null;
            var activeColor = optionsPanel != null ? optionsPanel._activeColor : default;
            var inactiveColor = optionsPanel != null ? optionsPanel._inactiveColor : default;

            _scrollContainer = Sui._scrollContainerPrefab;
            _options = Sui._optionsPrefab;
            _slider = Sui._sliderPrefab;
            _labelDivider = Sui._labelDividerPrefab;
            _input = Sui._inputPrefab;
            _button = Sui._buttonPrefab;
            _menuButton = Sui._menuButtonPrefab;
            _headingFont = heading != null ? heading.font : null;
            _headingMaterial = heading != null ? heading.fontSharedMaterial : null;
            _bodyFont = body != null ? body.font : null;
            _bodyMaterial = body != null ? body.fontSharedMaterial : null;
            _rowSprite = rowSprite;
            _buttonSprite = buttonSprite;
            _inputSprite = inputSprite;
            _hasOptionColors = optionsPanel != null;
            _activeColor = activeColor;
            _inactiveColor = inactiveColor;
        }

        internal static void ApplyPanelBackground(Image image)
        {
            // Same recipe as the Mod Settings panel the menu opens.
            var sprite = PanelSprite;
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Simple;
                image.color = Color.white;
            }
            else
            {
                image.color = PlainPanelColor;
            }

            var material = PanelMaterial;
            if (material != null)
                image.material = material;
        }

        private static OptionsGuiManager FindOptionsPanel()
        {
            foreach (var manager in Resources.FindObjectsOfTypeAll<OptionsGuiManager>())
            {
                if (manager != null && manager.name == OptionsPanelName)
                    return manager;
            }
            return null;
        }

        private static TMP_Text FindGroupLabel(OptionsGuiManager optionsPanel)
        {
            var groups = optionsPanel != null ? optionsPanel._optionGroups : null;
            if (groups == null)
                return null;

            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var label = group != null ? group._groupLabel : null;
                if (label != null && label.font != null)
                    return label;
            }
            return null;
        }

        private static T Find<T>(GameObject root, string path) where T : Component
        {
            if (!root)
                return null;
            var child = root.transform.Find(path);
            return child != null ? Live(child.GetComponent<T>()) : null;
        }

        private static Sprite SpriteOf(Image image)
        {
            return image != null ? Live(image.sprite) : null;
        }

        private static int Count(Object value)
        {
            return value ? 1 : 0;
        }

        private static T Live<T>(T value) where T : Object
        {
            return value ? value : null;
        }
    }
}
