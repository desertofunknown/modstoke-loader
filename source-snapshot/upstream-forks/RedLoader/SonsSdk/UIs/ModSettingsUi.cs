using RedLoader;
using Sons.Ai.Vail;
using SonsSdk;
using SUI;
using UnityEngine;
using UnityEngine.UI;

namespace SonsGameManager;

using static SUI.SUI;

public class ModSettingsUi
{
    public const string MOD_SETTINGS_NAME = "ModSettingsPanel";
    
    public static GraphicRaycaster Raycaster { get; internal set; }

    internal static readonly BackgroundDefinition MainBg = new(
        "#fff",
        GetBackgroundSprite(EBackground.Sons),
        Image.Type.Simple);
    
    internal static readonly BackgroundDefinition ButtonBg = new(
        ColorFromString("#191A23"), 
        GetBackgroundSprite(EBackground.Round10), 
        Image.Type.Sliced);

    internal static SettingsRegistry.SettingsEntry _currentEntry;
    
    internal static bool _initialized;
    internal static SScrollContainerOptions _mainContainer;
    
    internal static readonly Observable<string> Title = new("Mod Settings");
    internal static Action<bool> _onClosed;

    public static void Create()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.CreatePanel();
            return;
        }

        if (_initialized)
            return;

        var panel = RegisterNewPanel(MOD_SETTINGS_NAME)
            .OverrideSorting(200)
            .Background(MainBg).Material(GameResources.GetMaterial(MaterialAssetMap.DarkGreyBackground))
            .Dock(EDockType.Fill).Size(-400,-100);
        panel.Active(false);
        
        Raycaster = panel.Root.GetComponent<GraphicRaycaster>();

        panel.Add(SLabel.Bind(Title).Dock(EDockType.Top)
            .Size(0, 60).Position(0, -30)
            .FontColor(Color.white.WithAlpha(0.3f)).Font(EFont.RobotoLight).FontSpacing(3).Active(false));

        // panel.Add(SContainer.Background(new (0.2f, 0.2f, 0.2f), EBackground.None)
        //     .Dock(EDockType.Top).Size(-100, 3).Position(0, -100));

        _mainContainer = SScrollContainer.Margin(100,100,100,160).As<SScrollContainerOptions>();
        _mainContainer.ContainerObject.Padding(2, 4, 0, 0);
        panel.Add(_mainContainer);
        
        panel.Add(SBgButton
            .Background(ButtonBg).Background("#990508").Ppu(3)
            .Pivot(0, 0).Anchor(AnchorType.BottomLeft).Position(80, 80).Size(300, 60)
            .RichText("\uF060 Back").FontColor("#ff5856").UpperCase().FontSize(20).Notify(Close));
        
        panel.Add(SBgButton
            .Background(ButtonBg).Background("#796C4E").Ppu(3)
            .Pivot(0, 0).Anchor(AnchorType.BottomLeft).Position(390, 80).Size(200, 60)
            .RichText("Revert \uf0e2").FontColor("#c3ba8b").UpperCase().FontSize(20).Notify(RevertSettings));

        SubscribeEvents();
        _initialized = true;
    }

    internal static void SubscribeEvents()
    {
        SdkEvents.OnSonsSceneInitialized.Subscribe(_ => Close(returnToOwner: false));
        GlobalEvents.OnUpdate.Subscribe(OnUpdate);
    }

    private static void OnUpdate()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.UpdatePanel();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
        }
    }

    public static void Open(string id)
    {
        TryOpen(id, null);
    }

    /// <summary>
    /// Opens registered settings and notifies the caller when the panel closes.
    /// </summary>
    /// <param name="id">The registered mod ID.</param>
    /// <param name="onClosed">Receives true when the caller can restore its UI, or false on scene changes, replacement or a restart prompt.</param>
    /// <returns>Whether the requested settings panel was opened.</returns>
    public static bool TryOpen(string id, Action<bool> onClosed)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Settings.TryOpenPanel(id, onClosed);

        if (!_initialized || SettingsRegistry.GetEntry(id) is not { } entry)
        {
            return false;
        }

        if (_currentEntry != null)
        {
            Close(returnToOwner: false);
            if (_currentEntry != null)
            {
                return false;
            }
        }

        RLog.Debug($"Found panel for {id}");
        try
        {
            entry.ParentTo(_mainContainer.ContainerObject.RectTransform);
            Title.Value = $"MOD SETTINGS <color=#ea2f4e>[{id}]</color>";
            TogglePanel(MOD_SETTINGS_NAME, true);
            TooltipProvider.RaycastFor(Raycaster);
            _currentEntry = entry;
            _onClosed = onClosed;
            return true;
        }
        catch
        {
            try
            {
                entry.Unparent();
                TogglePanel(MOD_SETTINGS_NAME, false);
                TooltipProvider.StopRaycasting();
            }
            catch
            {
                // Preserve the original opening failure if UI cleanup also fails.
            }

            throw;
        }
    }

    public static void Close()
    {
        Close(returnToOwner: true);
    }

    private static void Close(bool returnToOwner)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.ClosePanel(returnToOwner);
            return;
        }

        var entry = _currentEntry;
        var onClosed = _onClosed;
        _currentEntry = null;
        _onClosed = null;
        var completed = false;
        var showRestartPrompt = false;

        try
        {
            entry?.Unparent();
            TogglePanel(MOD_SETTINGS_NAME, false);
            TooltipProvider.StopRaycasting();

            if (entry != null)
            {
                entry.Save();
                entry.InvokeCallbacks();
                showRestartPrompt = entry.ChangesNeedRestart && entry.CheckForChanges();
            }

            if (showRestartPrompt)
            {
                GenericModalDialog.ShowDialog("Restart", "You need to restart the game for the changes to take effect.").SetOption1("Ok", ()=>{});
            }

            completed = true;
        }
        finally
        {
            onClosed?.Invoke(returnToOwner && completed && !showRestartPrompt && _currentEntry == null);
        }
    }

    public static void RevertSettings()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.RevertPanel();
            return;
        }

        if (_currentEntry == null)
        {
            return;
        }
        
        _currentEntry.RevertSettings();
    }
}
