using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Harmony;
using HarmonyLib;
using RedLoader;
using RedLoader.Utils;
using Sons.Ai.Vail;
using Sons.Cutscenes;
using Sons.Events;
using Sons.Gameplay.GameSetup;
using Sons.Gui;
using Sons.Gui.Options;
using Sons.Inventory;
using Sons.Loading;
using Sons.Save;
using SonsSdk.Private;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Action = Il2CppSystem.Action;
using Color = UnityEngine.Color;
using Int32 = Il2CppSystem.Int32;
using Object = UnityEngine.Object;
using Priority = HarmonyLib.Priority;

namespace SonsSdk;

internal enum SaveHookPatchState
{
    NotAttempted,
    Applying,
    Applied,
    Failed
}

internal interface ISdkLifecycleDriver
{
    ISdkOverlayDriver Overlay { get; }
    SUI.ISdkPanelBlurDriver Blur { get; }
    SUI.ISdkUiPrefabDriver UiPrefabs { get; }
    SUI.ISdkUiViewportDriver UiViewport { get; }
    SUI.ISdkUiPanelDriver UiPanels { get; }
    SUI.ISdkMenuButtonDriver MenuButtons { get; }
    ISdkInputIconDriver InputIcons { get; }
    ISdkKeybindDriver Keybinds { get; }
    ISdkGlobalInputDriver GlobalInput { get; }
    SUI.ISdkSettingsRegistrationDriver Settings { get; }
    ISdkModalDialogDriver Dialogs { get; }
    ISdkTooltipDriver Tooltips { get; }
    Building.ISdkBlueprintRegistrationDriver Blueprints { get; }
    Networking.ISdkNetworkPrefabDriver NetworkPrefabs { get; }
    Networking.ISdkPacketDriver Packets { get; }
    Networking.ISdkServerCommandDriver ServerCommands { get; }
    ISdkGameManagerDriver GameManagers { get; }
    void InitializeBootstrapPatches();
    void PatchMaxPlayerLimit();
    void OnApplicationLateStart();
    void OnSdkInitialized(bool createModManagerUi);
    void OnSdkGameStart();
    void OnCheatsEnabledChanged(bool isEnabled);
    void RegisterCommands(Type targetType, object instance);
    void RegisterCommand(string command, Func<string, bool> callback);
    void RegisterGameEvents();
    void PatchGameplayEvents();
    void OnSceneInitialized(int sceneIndex, string sceneName);
    void OnUpdate();
    void OnGameStart();
    void BeforeLoad(string directory, SaveGameType saveGameType);
    void AfterLoad(string directory, SaveGameType saveGameType);
    void BeforeSave(string directory, string gameName, bool savePlayerOnly);
    void AfterSave(string directory, string gameName, bool savePlayerOnly);
}

public static class SdkEvents
{
    /// <summary>
    /// Called when the player spawns in the world and gains control
    /// </summary>
    public static readonly MelonEvent OnGameStart = new();
    
    /// <summary>
    /// Called when the sdk has been fully initialized
    /// </summary>
    public static readonly MelonEvent OnSdkInitialized = new();

    /// <summary>
    /// Called after all <see cref="OnSdkInitialized"/> registrations have been invoked.
    /// Mostly used internally. Only use this when you absolutely need to make sure that all SdkInitialized registrations have ran.
    /// Otherwise just use <see cref="OnSdkInitialized"/>
    /// </summary>
    public static readonly MelonEvent OnSdkLateInitialized = new();

    /// <summary>
    /// Called once when shared SDK startup finishes on a client or dedicated server.
    /// A null exception indicates success; mod callback failures are reported separately.
    /// </summary>
    public static readonly MelonEvent<Exception> OnSdkInitializationCompleted = new(oneTimeUse: true);
    
    /// <summary>
    /// Called on update when the player is in the world
    /// </summary>
    public static readonly MelonEvent OnInWorldUpdate = new();

    /// <summary>
    /// Called on the first <see cref="OnInWorldUpdate"/> tick
    /// </summary>
    public static readonly MelonEvent OnAfterSpawn = new();
    
    /// <summary>
    /// Called when the player picks up an item
    /// </summary>
    public static readonly MelonEvent<ItemInstance.ItemInstanceAndCount> OnItemPickup = new();
    
    /// <summary>
    /// Called when the player crafts an item
    /// </summary>
    public static readonly MelonEvent<int> OnItemCrafted = new();
    
    /// <summary>
    /// Called when the player consumes an item
    /// </summary>
    public static readonly MelonEvent<ItemInstance> OnItemConsumed = new();
    
    /// <summary>
    /// Called when the player equips some armor
    /// </summary>
    public static readonly MelonEvent OnArmorEquipped = new();

    /// <summary>
    /// Called by the prefix for the SaveGameManager.Save entry point.
    /// </summary>
    public static readonly MelonEvent BeforeSaveLoading = new();
    
    /// <summary>
    /// Called by the postfix for the SaveGameManager.Save entry point.
    /// The argument is savePlayerOnly. This is not confirmation of durable disk writes.
    /// </summary>
    public static readonly MelonEvent<bool> AfterSaveLoading = new();
    
    /// <summary>
    /// Called by the prefix for the SaveGameManager.Load entry point.
    /// </summary>
    public static readonly MelonEvent BeforeLoadSave = new();
    
    /// <summary>
    /// Called by the postfix for the SaveGameManager.Load entry point.
    /// The load result is not checked; this event does not confirm a successful load.
    /// </summary>
    public static readonly MelonEvent AfterLoadSave = new();

    /// <summary>
    /// Called on the fith activation sequence when the game mode is initialized.
    /// This should be used for general world modifications.
    /// </summary>
    public static readonly MelonEvent OnGameActivated = new();

    /// <summary>
    /// Called when a world sim actor has been added to the world
    /// </summary>
    public static readonly MelonEvent<WorldSimActor> OnWorldSimActorAdded = new();
    
    /// <summary>
    /// Called when a world sim actor has been removed from the world
    /// </summary>
    public static readonly MelonEvent<WorldSimActor> OnWorldSimActorRemoved = new();

    public static readonly MelonEvent<ESonsScene> OnSonsSceneInitialized = new();

    /// <summary>
    /// Called when the player exits to main menu
    /// </summary>
    public static readonly MelonEvent OnWorldExited = new();

    public static readonly MelonEvent<PauseMenu> OnPauseMenuAvailable = new();
    public static readonly MelonEvent<PauseMenu> OnPauseMenuOpened = new();
    public static readonly MelonEvent<PauseMenu> OnPauseMenuClosed = new();

    /// <summary>
    /// Called when cheats get toggled. Also gets called when receiving the new cheats state from the multiplayer server.
    /// This event is always getting called when joining a multiplayer server with the servers setting.
    /// </summary>
    public static readonly MelonEvent<bool> OnCheatsEnabledChanged = new();

    internal static void SetLifecycleDriver(ISdkLifecycleDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (ReferenceEquals(_lifecycleDriver, driver))
            return;
        if (_lifecycleDriver != null || _lifecycleSetupStarted)
            throw new InvalidOperationException("SDK lifecycle ownership must be selected before SDK setup or command registration.");

        _lifecycleDriver = driver;
    }

    internal static ISdkLifecycleDriver BeginLifecycleSetup()
    {
        _lifecycleSetupStarted = true;
        return _lifecycleDriver;
    }

    internal static void Init()
    {
        if (_isInitialized)
        {
            return;
        }
        var driver = BeginLifecycleSetup();
        
        GlobalEvents.OnSceneWasInitialized.Subscribe(OnSceneWasInitialized, Priority.First);
        GlobalEvents.OnUpdate.Subscribe(OnUpdateInternal, Priority.First);
        
        if (driver != null)
        {
            driver.RegisterGameEvents();
        }
        else
        {
            EventRegistry.Register(GameEvent.ItemPickedUp, (EventRegistry.SubscriberCallback)SonsEventsOnItemPickedUp);
            EventRegistry.Register(GameEvent.LoadedMainScene, (EventRegistry.SubscriberCallback)SonsEventsOnGameStart);
            EventRegistry.Register(GameEvent.ItemCrafted, (EventRegistry.SubscriberCallback)SonsEventOnItemCrafted);
            EventRegistry.Register(GameEvent.ItemConsumed, (EventRegistry.SubscriberCallback)SonsEventOnItemConsumed);
            EventRegistry.Register(GameEvent.ArmorEquipped, (EventRegistry.SubscriberCallback)SonsEventOnArmorEquipped);
        }

        if (driver != null)
            driver.PatchGameplayEvents();
        else
            Patches.Patch();

        SavingCallbackPatches.Patch();

        _isInitialized = true;
    }

    private static IEnumerator WaitForPauseMenu()
    {
        while (!PauseMenu._instance)
        {
            yield return null;
        }
        
        SUI.SUI.OnPauseMenuCreated(PauseMenu._instance);
        OnPauseMenuAvailable.Invoke(PauseMenu._instance);
    }

    private static void OnSceneWasInitialized(int sceneIdx, string sceneName)
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnSceneInitialized(sceneIdx, sceneName);
            return;
        }

        switch (sceneName)
        {
            case TitleSceneName:
                MainInitializer.InitTitleScene();
                _onAfterSpawnCalled = false;
                OnSonsSceneInitialized.Invoke(ESonsScene.Title);
                break;
            case LoadingSceneName:
                OnSonsSceneInitialized.Invoke(ESonsScene.Loading);
                break;
            case GameSceneName:
                RLog.Msg(System.Drawing.Color.DeepPink, $"=== Game Scene Initialized ===");
                LoadSave.OnGameStart += (Action)OnGameActivation;
                OnSonsSceneInitialized.Invoke(ESonsScene.Game);
                WaitForPauseMenu().RunCoro();
                break;
        }
    }
    
    private static void OnUpdateInternal()
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnUpdate();
            return;
        }

        ModInputCache.CheckAll();
        
        if (!LocalPlayer.IsInWorld)
        {
            return;
        }
    
        if (!_onAfterSpawnCalled)
        {
            _onAfterSpawnCalled = true;
            OnAfterSpawn.Invoke();
        }
        
        OnInWorldUpdate.Invoke();
    }
    
    #region Game Events
    
    private static void SonsEventsOnItemPickedUp(Il2CppSystem.Object o)
    {
        var item = o.TryCast<ItemInstance.ItemInstanceAndCount>();
        if (item == null)
        {
            return;
        }
        
        OnItemPickup.Invoke(item);
    }
    
    private static void SonsEventOnItemConsumed(Il2CppSystem.Object o)
    {
        var item = o.TryCast<ItemInstance>();
        if (item == null)
        {
            return;
        }
        
        OnItemConsumed.Invoke(item);
    }
    
    private static void SonsEventOnArmorEquipped(object _)
    {
        OnArmorEquipped.Invoke();
    }
    
    private static void SonsEventOnItemCrafted(Il2CppSystem.Object o)
    {
        var id = o.Unbox<int>();
        OnItemCrafted.Invoke(id);
    }
    
    private static void SonsEventsOnGameStart(object o)
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnGameStart();
            return;
        }

        OnGameStart.Invoke();
        ItemTools.ItemHookAdder.Flush();
    }
    
    private static void OnGameActivation()
    {
        RLog.Msg(System.Drawing.Color.DeepPink, $"||| Game Activated |||");
        OnGameActivated.Invoke();
    }
    
    #endregion
    
    private const string TitleSceneName = "SonsTitleScene";
    private const string LoadingSceneName = "SonsMainLoading";
    private const string GameSceneName = "SonsMain";
    
    private static ISdkLifecycleDriver _lifecycleDriver;
    private static bool _lifecycleSetupStarted;
    private static bool _isInitialized;
    private static bool _onAfterSpawnCalled;
    
    private class Patches
    {
        private static ConfiguredPatcher<Patches> _patcher;

        private static bool? _prevCheatsEnabledState;
    
        public static void Patch()
        {
            _patcher = new ConfiguredPatcher<Patches>(SdkEntryPoint.Harmony);
            _patcher.Patch(nameof(AddWorldSimActor));
            _patcher.Patch(nameof(RemoveWorldSimActor));
            _patcher.Patch(nameof(OpenPauseMenu));
            _patcher.Patch(nameof(ClosePauseMenu));
            _patcher.Patch(nameof(RunQuittingGameCallbacks));
            _patcher.Patch(nameof(GetMultiplayerCheatsSettingPatch));
            
            OnSonsSceneInitialized.Subscribe(scene =>
            {
                if (scene == ESonsScene.Title)
                {
                    _prevCheatsEnabledState = null;
                }
            });
        }

        [HarmonyPatch(typeof(VailWorldSimulation), nameof(VailWorldSimulation.AddActor))]
        private static void AddWorldSimActor(WorldSimActor actor, bool onLoad)
        {
            OnWorldSimActorAdded.Invoke(actor);
        }
    
        [HarmonyPatch(typeof(VailWorldSimulation), nameof(VailWorldSimulation.RemoveActor))]
        private static void RemoveWorldSimActor(WorldSimActor removeActor)
        {
            OnWorldSimActorRemoved.Invoke(removeActor);
        }
        
        [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Open))]
        private static void OpenPauseMenu(PauseMenu __instance)
        {
            OnPauseMenuOpened.Invoke(__instance);
        }
        
        [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Close))]
        private static void ClosePauseMenu(PauseMenu __instance)
        {
            OnPauseMenuClosed.Invoke(__instance);
        }
        
        [HarmonyPatch(typeof(Sons.Save.GameState), nameof(Sons.Save.GameState.RunQuittingGameCallbacks))]
        private static void RunQuittingGameCallbacks()
        {
            OnWorldExited.Invoke();
        }
        
        [HarmonyPatch(typeof(GameSetupManager), nameof(GameSetupManager.GetMultiplayerCheatsSetting))]
        private static void GetMultiplayerCheatsSettingPatch(ref bool __result)
        {
            if (_prevCheatsEnabledState == null || _prevCheatsEnabledState.Value != __result)
            {
                OnCheatsEnabledChanged.Invoke(__result);
                _prevCheatsEnabledState = __result;
            }
        }
    }
    
    public class SavingCallbackPatches
    {
        private static SaveHookPatchState _loadPatchState;
        private static SaveHookPatchState _savePatchState;

        internal static SaveHookPatchState LoadPatchState => _loadPatchState;
        internal static SaveHookPatchState SavePatchState => _savePatchState;

        [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Load), typeof(string), typeof(SaveGameType))]
        [HarmonyPrefix]
        public static void BeforeLoad(string dir, SaveGameType saveGameType)
        {
            if (_lifecycleDriver != null)
            {
                _lifecycleDriver.BeforeLoad(dir, saveGameType);
                return;
            }

            if (!SaveGameManager.HasInstance)
            {
                RLog.Error("SaveGameManager not initialized, aborting load callback.");
                return;
            }
            
            // RLog.Msg($"Loading savegame from {dir} of type {saveGameType}");
        
            GameState.LastLoadedSaveId = GameSetupManager.GetSelectedSaveId();
            BeforeLoadSave.Invoke();
        }
        
        [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Load), typeof(string), typeof(SaveGameType))]
        [HarmonyPostfix]
        public static void AfterLoad(string dir, SaveGameType saveGameType)
        {
            if (_lifecycleDriver != null)
            {
                _lifecycleDriver.AfterLoad(dir, saveGameType);
                return;
            }

            if (!SaveGameManager.HasInstance)
            {
                return;
            }
            
            // RLog.Msg($"AfterLoad");
            
            AfterLoadSave.Invoke();
        }
        
        [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Save), typeof(string), typeof(string), typeof(bool))]
        [HarmonyPrefix]
        public static void BeforeSave(string dir, string gameName, bool savePlayerOnly)
        {
            if (_lifecycleDriver != null)
            {
                _lifecycleDriver.BeforeSave(dir, gameName, savePlayerOnly);
                return;
            }

            if (!SaveGameManager.HasInstance)
            {
                RLog.Error("SaveGameManager not initialized, aborting save callback.");
                return;
            }
            
            // RLog.Msg($"Saving savegame to {dir} with name {gameName} (SavePlayerOnly:{savePlayerOnly})");
            
            BeforeSaveLoading.Invoke();
        }
        
        [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Save), typeof(string), typeof(string), typeof(bool))]
        [HarmonyPostfix]
        public static void AfterSave(string dir, string gameName, bool savePlayerOnly)
        {
            if (_lifecycleDriver != null)
            {
                _lifecycleDriver.AfterSave(dir, gameName, savePlayerOnly);
                return;
            }

            if (!SaveGameManager.HasInstance)
            {
                return;
            }
            
            // RLog.Msg($"AfterSave");
            
            AfterSaveLoading.Invoke(savePlayerOnly);
        }
    
        public static void Patch()
        {
            ApplyPatch(
                AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Load), new[] { typeof(string), typeof(SaveGameType) }),
                nameof(BeforeLoad), nameof(AfterLoad), ref _loadPatchState);
            ApplyPatch(
                AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Save), new[] { typeof(string), typeof(string), typeof(bool) }),
                nameof(BeforeSave), nameof(AfterSave), ref _savePatchState);
        }

        private static void ApplyPatch(MethodInfo target, string prefixName, string postfixName, ref SaveHookPatchState state)
        {
            ArgumentNullException.ThrowIfNull(target);
            var prefix = new HarmonyMethod(typeof(SavingCallbackPatches), prefixName);
            var postfix = new HarmonyMethod(typeof(SavingCallbackPatches), postfixName);

            state = SaveHookPatchState.Applying;
            try
            {
                SdkEntryPoint.Harmony.Patch(target, prefix: prefix, postfix: postfix);
                state = SaveHookPatchState.Applied;
            }
            catch
            {
                // A failed detour attempt may already have changed native state.
                state = SaveHookPatchState.Failed;
                throw;
            }
        }
    }
}
