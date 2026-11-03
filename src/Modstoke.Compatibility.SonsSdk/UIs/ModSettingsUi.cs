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

        SettingsRegistry.Fallback.CreatePanel();
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

        SettingsRegistry.Fallback.UpdatePanel();
    }

    internal static void LogOpenedSettings(string id)
    {
        RLog.Debug($"Found panel for {id}");
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

        return SettingsRegistry.Fallback.TryOpenPanel(id, onClosed);
    }

    public static void Close()
    {
        Close(returnToOwner: true);
    }

    internal static void Close(bool returnToOwner)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.ClosePanel(returnToOwner);
            return;
        }

        SettingsRegistry.Fallback.ClosePanel(returnToOwner);
    }

    public static void RevertSettings()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.RevertPanel();
            return;
        }

        SettingsRegistry.Fallback.RevertPanel();
    }
}
