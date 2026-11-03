using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Endnight.Utilities;
using Il2CppInterop.Runtime.Injection;
using JetBrains.Annotations;
using RedLoader;
using RedLoader.Utils;
using Sons.Gui;
using Sons.Gui.Input;
using Sons.Gui.Options;
using Sons.Input;
using Sons.Loading;
using Sons.UiElements;
using SonsSdk;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.PropertyVariants;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SUI;

internal interface ISdkUiPrefabDriver
{
    void Initialize();
}

internal interface ISdkUiViewportDriver
{
    void Create();
    void SetEventSystemActive(bool active);
}

internal interface ISdkUiPanelDriver
{
    SPanelOptions Register(string id, Transform parent, bool enableInput, KeyCode? toggleKey);
    bool Remove(string id);
}

internal interface ISdkMenuButtonDriver
{
    void Register(Func<SUiElement> generator, string id, ESonsScene scene, int index);
    void OnSceneInitialized(ESonsScene scene);
    void OnPauseMenuCreated(PauseMenu menu);
}

internal interface ISdkUiFallbackDriver
{
    void InitPrefabs(ISdkLifecycleDriver driver);
    void OnSceneInitialized(ESonsScene scene);
    SPanelOptions RegisterPanel(string id, Transform parent, bool enableInput, KeyCode? toggleKey);
    SPanelOptions GetPanel(string id);
    bool TogglePanel(string id);
    SPanelOptions TogglePanel(string id, bool show);
    bool RemovePanel(string id);
    void ReleasePanel(SPanelOptions panel);
    void OnPauseMenuCreated(PauseMenu menu);
    void RegisterMenuButton(Func<SUiElement> generator, string id, ESonsScene scene, int index);
    IEnumerable<GameObject> GetHoveredUiObjects(GraphicRaycaster raycaster);
    void CreateViewport();
    void SetEventSystemActive(bool active);
    void OnEventSystemEnabled(ref int activeEnablers);
    void OnEventSystemDisabled(ref int activeEnablers);
}

public partial class SUI
{
    public static readonly Color BG_CYAN = new(0, 0.5f, 0.5f, 0.2f);

    public static Canvas SUIViewport { get; private set; }
    public static GraphicRaycaster SUIRaycaster { get; private set; }
    public static bool IsInitialized { get; private set; }
    
    public static Dictionary<string, SPanelOptions> _panels = new();

    internal static GameObject _sliderPrefab;
    internal static GameObject _optionsPrefab;
    internal static GameObject _textPrefab;
    internal static GameObject _labelDividerPrefab;
    internal static GameObject _togglePrefab;
    internal static GameObject _inputPrefab;
    internal static GameObject _buttonPrefab;
    internal static GameObject _bgButtonPrefab;
    internal static GameObject _maskedImagePrefab;
    internal static GameObject _menuButtonPrefab;
    internal static GameObject _scrollContainerPrefab;
    internal static GameObject _tabControllerPrefab;
    internal static GameObject _keybindPrefab;

    internal static Transform _titleMenuButtonsContainer;

    internal static Sprite _sonsBackgroundSprite;
    internal static Sprite _roundBackgroundSprite;

    internal static GameObject _eventSystemObject;
    
    internal static readonly List<MenuButtonRegistration> _registeredMenuButtons = new();
    internal static PauseMenu _pauseMenu;

    private static readonly Lazy<ISdkUiFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.UiFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkUiFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkUiFallbackDriver>)))();
    });
    internal static ISdkUiFallbackDriver Fallback => FallbackDriver.Value;

    public static SSliderOptions SSlider => new(Object.Instantiate(_sliderPrefab));

    public static SOptionsOptions SOptions => new(Object.Instantiate(_optionsPrefab));

    public static SLabelOptions SLabel => new(Object.Instantiate(_textPrefab));

    public static SLabelDividerOptions SLabelDivider => new(Object.Instantiate(_labelDividerPrefab));

    public static SToggleOptions SToggle => new(Object.Instantiate(_togglePrefab));

    public static SColorWheelOptions SColorWheel => new(Object.Instantiate(_colorWheelPrefab));

    public static SContainerOptions SBackground
    {
        get
        {
            var obj = new SContainerOptions(new GameObject("Background"));
            obj.Background(Color.black);
            return obj;
        }
    }
    
    public static SContainerOptions SHorizontal
    {
        get
        {
            var obj = new SContainerOptions(new GameObject("Horizontal"));
            obj.Horizontal();
            return obj;
        }
    }
    
    public static SContainerOptions SVertical
    {
        get
        {
            var obj = new SContainerOptions(new GameObject("Vertical"));
            obj.Vertical();
            return obj;
        }
    }

    public static STextboxOptions STextbox => new(Object.Instantiate(_inputPrefab));
    
    public static SButtonOptions SButton => new(Object.Instantiate(_buttonPrefab));
    
    public static SBgButtonOptions SBgButton => new(Object.Instantiate(_bgButtonPrefab));
    
    public static SMenuButtonOptions SMenuButton => new(Object.Instantiate(_menuButtonPrefab));
    
    public static SMaskedImageOptions SMaskedImage => new(Object.Instantiate(_maskedImagePrefab));
    
    public static SSpriteOptions SSprite => new(new GameObject("Sprite"));
    
    public static SImageOptions SImage => new(new GameObject("Image"));
    
    public static SContainerOptions SContainer => new(new GameObject("Container"));

    public static SContainerOptions SDiv => new SContainerOptions(new GameObject("Container"))
        .Pivot(0,1)
        .Anchor(AnchorType.TopLeft);
    
    public static STabControllerOptions STabController => new(Object.Instantiate(_tabControllerPrefab));
    
    public static SScrollContainerOptions SScrollContainer => new(Object.Instantiate(_scrollContainerPrefab));

    public static SIconButtonOptions SIconButton => new(Object.Instantiate(_bgButtonPrefab));
    
    public static SKeybindOptions SKeybind => new(Object.Instantiate(_keybindPrefab));
    
    // ======= Sprite strong type =======
    public static Sprite SpriteBackground400ppu => GetSprite("Background (400ppu)");
    public static Sprite SpriteBackground => GetSprite("Background");
    public static Sprite BlurBackground => GetSprite("Blurred Title Screen");
    
    internal static void InitPrefabs()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        Fallback.InitPrefabs(driver);
    }

    internal static void InitBundleContentForFallback()
    {
        InitBundleContent();
    }

    internal static void PrintSpritesForFallback()
    {
        PrintSprites();
    }

    internal static void LogAddedTitleButton()
    {
        RLog.Debug("Added button to title menu");
    }

    internal static void PublishInitialized()
    {
        IsInitialized = true;
    }

    [Conditional("DEBUG")]
    private static void PrintSprites()
    {
        RLog.WriteLine(10);
        RLog.Debug("Sprites:");
        foreach (var (key, value) in GameResources.Sprites)
        {
            RLog.Debug(key);
        }
        RLog.WriteLine(10);
    }

    internal static void OnSonsScene(ESonsScene scene)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.MenuButtons.OnSceneInitialized(scene);
            return;
        }

        Fallback.OnSceneInitialized(scene);
    }

    /// <inheritdoc cref="RegisterNewPanel(string,bool,System.Nullable{UnityEngine.KeyCode})"/>
    public static SPanelOptions RegisterNewPanel(string id, bool enableInput = false, KeyCode? toggleKey = null)
    {
        return RegisterNewPanel(id, null, enableInput, toggleKey);
    }

    /// <summary>
    /// Creates a new panel and registers it to the sui system.
    /// </summary>
    /// <param name="id">The id by which you can manage the panel later. Needs to be unique</param>
    /// <param name="parent">The transform to parent the panel to</param>
    /// <param name="enableInput">If true enables the mouse and disables game keyboard input once the panel is showing</param>
    /// <param name="toggleKey">Optional key by which you can toggle the panel</param>
    /// <exception cref="ArgumentException">The panel ID is already registered.</exception>
    /// <returns></returns>
    public static SPanelOptions RegisterNewPanel(string id, Transform parent, bool enableInput = false, KeyCode? toggleKey = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.UiPanels.Register(id, parent, enableInput, toggleKey);

        return Fallback.RegisterPanel(id, parent, enableInput, toggleKey);
    }
    
    public static SPanelOptions GetPanel(string id)
    {
        return Fallback.GetPanel(id);
    }

    public static bool TogglePanel(string id)
    {
        return Fallback.TogglePanel(id);
    }
    
    public static SPanelOptions TogglePanel(string id, bool show)
    {
        return Fallback.TogglePanel(id, show);
    }
    
    public static bool RemovePanel(string id)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.UiPanels.Remove(id);

        return Fallback.RemovePanel(id);
    }

    internal static void ReleasePanel(SPanelOptions panel)
    {
        Fallback.ReleasePanel(panel);
    }

    internal static void OnPauseMenuCreated(PauseMenu menu)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.MenuButtons.OnPauseMenuCreated(menu);
            return;
        }

        Fallback.OnPauseMenuCreated(menu);
    }

    public static void AddButtonToPauseMenu(Func<SUiElement> generator, string id, int index = -1)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (!MenuButtonRegistrationPolicy.Allows(id))
            return;

        if (driver != null)
        {
            driver.MenuButtons.Register(generator, id, ESonsScene.Game, index);
            return;
        }

        Fallback.RegisterMenuButton(generator, id, ESonsScene.Game, index);
    }
    
    public static void AddButtonToTitleMenu(Func<SUiElement> generator, string id, int index = -1)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (!MenuButtonRegistrationPolicy.Allows(id))
            return;

        if (driver != null)
        {
            driver.MenuButtons.Register(generator, id, ESonsScene.Title, index);
            return;
        }

        Fallback.RegisterMenuButton(generator, id, ESonsScene.Title, index);
    }
    
    public static IEnumerable<GameObject> GetHoveredUiObjects(GraphicRaycaster raycaster)
    {
        foreach (var gameObject in Fallback.GetHoveredUiObjects(raycaster))
            yield return gameObject;
    }

    private static void CreateTestUi()
    {
        var flareGunIcon = AssetLoaders.LoadAsset<Sprite>("FlareGunIcon");
        
        void OnSliderChanged(float val)
        {
            RLog.Msg(System.Drawing.Color.Aquamarine, $"Slider: {val}");
        }

        void OnOptionChanged(string val)
        {
            RLog.Msg(System.Drawing.Color.Aquamarine, $"Option: {val}");
        }
        
        void OnOkClicked()
        {
            RLog.Msg(System.Drawing.Color.Aquamarine, "Ok clicked");
        }

        var myText = new Observable<string>("hello");

        var rootBoi = CreatePanel().Dock(EDockType.Left).Width(600).Vertical().Padding(20).LayoutMode("EX").Background(new Color(0,0,0,0.6f), EBackground.None)
                      - SLabelDivider.Text("Custom").FontSize(24).FontColor(Color.cyan)
                      - SSlider.Text("Steps").Range(1, 10).IntStep().Value(4).Notify(OnSliderChanged)
                      - SOptions.Text("Mode").Options("Low", "Mid", "High", "Crazy", "Godlike").Value("Crazy").Notify(OnOptionChanged)
                      - STextbox.Text("Custom Mode").Placeholder("Type something...").Bind(myText)
                      - SToggle.Text("Enable something 1")
                      - SToggle.Text("Enable something 2").Value(false);

        rootBoi.Add(SHorizontal
                    - SLabel.Text("Hello")
                    - SLabel.Text("World")
                    - SLabel.Text("Hello"));

        rootBoi.Add(SLabel.Bind(myText));

        rootBoi.Add(SHorizontal.LayoutMode("EC").AutoSize("UM")
                    - IconButton("Toni", flareGunIcon, () => RLog.Msg(System.Drawing.Color.Aquamarine, "Toni clicked"))
                    - IconButton("Macaroni", flareGunIcon, () => RLog.Msg(System.Drawing.Color.Aquamarine, "Macaroni clicked")));

        rootBoi.Add(SHorizontal
                    - SButton.Text("Ok").Notify(OnOkClicked)
                    - SButton.Text("Cancel"));
    }
    
    public static SContainerOptions IconButton(string title, Sprite icon, Action onClick)
    {
        return SVertical.Background(BG_CYAN, EBackground.None).LayoutMode("EC").PaddingVertical(25).Spacing(40).OnClick(onClick)
               - (SContainer.MHeight(100) - SSprite.Anchor(AnchorType.FillVertical).Size(100, 0).Sprite(icon))
               - SLabel.MHeight(20).Text(title);
    }

    public static Sprite GetBackgroundSprite(EBackground type) => type switch
        {
            EBackground.Sons => _sonsBackgroundSprite,
            EBackground.RoundedStandard => _roundBackgroundSprite,
            EBackground.Round8 => GetSprite("RoundRect8"),
            EBackground.Round10 => GetSprite("RoundRect10"),
            EBackground.RoundSmall => GetSprite("RoundRectSmall"),
            EBackground.Round28 => GetSprite("RoundRect28"),
            EBackground.RoundNormal => GetSprite("RoundRectNormal"),
            EBackground.RoundOutline => GetSprite("RectOutline"),
            EBackground.RoundOutline10 => GetSprite("RoundRect10Outline"),
            EBackground.ShadowPanel => GetSprite("ShadowPanel"),
            _ => null
        };

    public static SPanelOptions CreatePanel(Transform parent = null)
    {
        var rootGo = new GameObject("Root");
        rootGo.AddComponent<RectTransform>();
        var panel = new SPanelOptions(rootGo);
        panel.Anchor(AnchorType.Fill);
        
        if (!parent)
        {
            parent = SUIViewport.transform;
        }
        
        panel.SetParent(parent);
        
        return panel;
    }

    internal static void CreateViewport()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.UiViewport.Create();
            return;
        }

        Fallback.CreateViewport();
    }

    internal static void PublishViewport(Canvas canvas, GraphicRaycaster raycaster, GameObject eventSystem)
    {
        // Keep the public viewport unchanged until its input components are ready.
        _eventSystemObject = eventSystem;
        SUIRaycaster = raycaster;
        SUIViewport = canvas;
    }
    
    internal static void SetEventSystemActive(bool active)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.UiViewport.SetEventSystemActive(active);
            return;
        }

        Fallback.SetEventSystemActive(active);
    }

    public static Sprite GetSprite(string name)
    {
        if(GameResources.Sprites.TryGetValue(name, out var sprite))
            return sprite;
        
        RLog.Debug("Did not find sprite: " + name);
        return null;
    }

    public static Color ColorFromString(string color)
    {
        if (string.IsNullOrEmpty(color))
            return Color.white;

        if (color.StartsWith("#"))
            color = color.Substring(1);

        if (color.Length == 3 || color.Length == 4) 
        {
            var rChar = color[0];
            var gChar = color[1];
            var bChar = color[2];
            var aChar = color.Length == 4 ? color[3] : 'F';
            color = $"{rChar}{rChar}{gChar}{gChar}{bChar}{bChar}{aChar}{aChar}";
        }
        else if (color.Length != 6 && color.Length != 8)
        {
            return Color.white;
        }

        var r = Convert.ToInt32(color.Substring(0, 2), 16);
        var g = Convert.ToInt32(color.Substring(2, 2), 16);
        var b = Convert.ToInt32(color.Substring(4, 2), 16);
        var a = color.Length == 8 ? Convert.ToInt32(color.Substring(6, 2), 16) : 255;

        return new Color((float)r/255, (float)g/255, (float)b/255, (float)a/255);
    }

    internal static GameObject CreateMaskedImagePrefab()
    {
        var rootgo = new GameObject("MaskedImage");
        rootgo.AddComponent<RectTransform>();
        var rawImage = rootgo.AddComponent<RawImage>();
        rawImage.texture = GetSprite("Background (400ppu)").texture;
        var mask = rootgo.AddComponent<UnityEngine.UI.Mask>();
        mask.showMaskGraphic = false;
        
        var imageGo = new GameObject("Image");
        imageGo.transform.SetParent(rootgo.transform);
        imageGo.AddComponent<RectTransform>();
        imageGo.AddComponent<RawImage>();

        return rootgo.DontDestroyOnLoad().HideAndDontSave();
    }

    internal static GameObject CreateTogglePrefab()
    {
        var toggleGameObject = new GameObject("Toggle");

        var toggle = toggleGameObject.AddComponent<Toggle>();

        SLabel
            .Text("Toggle")
            .FontAutoSize(false)
            .FontSize(20)
            .UpperCase()
            .FontColor(new Color(0.834f, 0.7804f, 0.7804f))
            .Dock(EDockType.Fill)
            .Alignment(TextAlignmentOptions.MidlineLeft)
            .Margin(0,0,0,0)
            .Name("Label")
            .SetParent(toggleGameObject.transform);
        
        var background = SDiv
            .Anchor(AnchorType.MiddleRight)
            .Size(42, 42)
            .Pivot(1, 0.5f)
            .Position(-20, 0)
            .Background(SpriteBackground400ppu, ColorFromString("#333"), Image.Type.Tiled).Ppu(5);
        background.SetParent(toggleGameObject.transform);

        var checkmark = SDiv
            .Dock(EDockType.Fill)
            .Size(-15, -15)
            .Pivot(0.5f, 0.5f)
            .Background(new Color(1,1,1,0.3f), EBackground.None);
        checkmark.SetParent(background);

        toggle.graphic = checkmark.Root.GetComponent<Image>();
        toggle.targetGraphic = background.Root.GetComponent<Image>();
        
        return toggleGameObject.DontDestroyOnLoad().HideAndDontSave();
    }

    internal static GameObject CreateTabControllerPrefab()
    {
        var tabPanel = SDiv.Name("TabController").Vertical(0, "EC");

        var tabHeader = SContainer.PHeight(50).Horizontal(10,"XC").Name("TabHeader").Padding(10);
        tabPanel.Add(tabHeader);
        tabPanel.Add(SContainer.Background(Color.black).PHeight(5).Name("TabDivider"));
        var tabContent = SContainer.FlexHeight(1).Name("TabContent");
        tabPanel.Add(tabContent);

        return tabPanel.Root.DontDestroyOnLoad().HideAndDontSave();
    }

    public static TMP_FontAsset GetFont(EFont font) => font switch
    {
        EFont.RobotoDefault => GameResources.Fonts["Roboto-Regular SDF"],
        EFont.NotoSans => GameResources.Fonts["NotoSansTC-Regular SDF"],
        EFont.RobotoBold => GameResources.Fonts["RobotoCondensed-Bold SDF"],
        EFont.RobotoBlur => GameResources.Fonts["RobotoCondensed-Bold SDFBlur"],
        EFont.Montserrat => GameResources.Fonts["Montserrat-Medium SDF"],
        EFont.RobotoRegular => GameResources.Fonts["RobotoCondensed-Regular SDF"],
        EFont.RobotoLight => GameResources.Fonts["RobotoCondensed-Regular SDF"], // fallback from "RobotoCondensed-Light SDF", got removed in 1.0
        EFont.FatDebug => GameResources.Fonts["VailDebugFont"],
        EFont.LiberationSans => GameResources.Fonts["LiberationSans SDF"],
        _ => throw new ArgumentOutOfRangeException(nameof(font), font, null)
    };

    /// <summary>
    /// Get a rich text string for a sprite
    /// </summary>
    /// <param name="spriteName">Name of the sprite</param>
    /// <param name="color">Optional color of the sprite. White by default</param>
    /// <returns>A string in the form of &lt;sprite name="sprite" color=#000&gt;</returns>
    public static string SpriteText(string spriteName, string color = null)
    {
        if (color == null)
            return $"<sprite name=\"{spriteName}\">";
        
        return $"<sprite name=\"{spriteName}\" color={color}>";
    }
    
    /// <summary>
    /// Wraps some text in a rich text color tag
    /// </summary>
    /// <param name="text">The text to wrap</param>
    /// <param name="color">The color of the text</param>
    /// <returns>A string in the form of &lt;color=#000&gt;text&lt;/color&gt;</returns>
    public static string WrapColor(string text, string color)
    {
        return $"<color={color}>{text}</color>";
    }

    internal struct MenuButtonRegistration
    {
        public readonly Func<SUiElement> ElementFactory;
        public readonly string Id;
        public readonly ESonsScene Scene;
        public readonly int Index;
        
        public MenuButtonRegistration(Func<SUiElement> elementFactory, string id, ESonsScene scene, int index)
        {
            ElementFactory = elementFactory;
            Id = id;
            Scene = scene;
            Index = index;
        }
    }

    internal class EventSystemEnabler : MonoBehaviour
    {
        private static int ActiveEnablers = 0;
        
        static EventSystemEnabler()
        {
            ClassInjector.RegisterTypeInIl2Cpp<EventSystemEnabler>();
        }
        
        private void OnEnable()
        {
            Fallback.OnEventSystemEnabled(ref ActiveEnablers);
        }
        
        private void OnDisable()
        {
            Fallback.OnEventSystemDisabled(ref ActiveEnablers);
        }
    }

    public struct BackgroundDefinition
    {
        public Color Color;
        public Sprite Sprite;
        public Image.Type Type;
        
        public BackgroundDefinition(Color color, Sprite sprite, Image.Type type)
        {
            Color = color;
            Sprite = sprite;
            Type = type;
        }
        
        public BackgroundDefinition(string color, Sprite sprite, Image.Type type)
        {
            Color = ColorFromString(color);
            Sprite = sprite;
            Type = type;
        }
        
        public void ApplyTo(Image image)
        {
            image.color = Color;
            image.sprite = Sprite;
            image.type = Type;
        }
    }

    public enum EFont
    {
        RobotoDefault,
        NotoSans,
        RobotoBold,
        RobotoBlur,
        Montserrat,
        RobotoRegular,
        RobotoLight,
        FatDebug,
        LiberationSans
    }
}
