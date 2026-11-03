// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

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

internal interface ISdkEventFallbackDriver
{
    void Initialize();
    System.Collections.IEnumerator WaitForPauseMenu();
    void OnSceneInitialized(int sceneIndex, string sceneName);
    void OnUpdate();
    void OnItemPickedUp(Il2CppSystem.Object value);
    void OnItemConsumed(Il2CppSystem.Object value);
    void OnArmorEquipped(object value);
    void OnItemCrafted(Il2CppSystem.Object value);
    void OnGameStart(object value);
    void OnGameActivation();
    void PatchGameplayEvents();
    void OnCheatsSceneInitialized(ESonsScene scene);
    void OnWorldSimActorAdded(WorldSimActor actor, bool onLoad);
    void OnWorldSimActorRemoved(WorldSimActor actor);
    void OnPauseMenuOpened(PauseMenu menu);
    void OnPauseMenuClosed(PauseMenu menu);
    void OnWorldExited();
    void OnMultiplayerCheatsSetting(ref bool result);
    void BeforeLoad(string directory, SaveGameType saveGameType);
    void AfterLoad(string directory, SaveGameType saveGameType);
    void BeforeSave(string directory, string gameName, bool savePlayerOnly);
    void AfterSave(string directory, string gameName, bool savePlayerOnly);
    void PatchSavingCallbacks();
    void ApplySavePatch(MethodInfo target, string prefixName, string postfixName, ref SaveHookPatchState state);
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
        Fallback.Initialize();
    }

    internal static IEnumerator WaitForPauseMenu()
    {
        return Fallback.WaitForPauseMenu();
    }

    internal static void OnSceneWasInitialized(int sceneIdx, string sceneName)
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnSceneInitialized(sceneIdx, sceneName);
            return;
        }

        Fallback.OnSceneInitialized(sceneIdx, sceneName);
    }
    
    internal static void OnUpdateInternal()
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnUpdate();
            return;
        }

        Fallback.OnUpdate();
    }
    
    #region Game Events
    
    internal static void SonsEventsOnItemPickedUp(Il2CppSystem.Object o)
    {
        Fallback.OnItemPickedUp(o);
    }
    
    internal static void SonsEventOnItemConsumed(Il2CppSystem.Object o)
    {
        Fallback.OnItemConsumed(o);
    }
    
    internal static void SonsEventOnArmorEquipped(object _)
    {
        Fallback.OnArmorEquipped(_);
    }
    
    internal static void SonsEventOnItemCrafted(Il2CppSystem.Object o)
    {
        Fallback.OnItemCrafted(o);
    }
    
    internal static void SonsEventsOnGameStart(object o)
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnGameStart();
            return;
        }

        Fallback.OnGameStart(o);
    }
    
    internal static void OnGameActivation()
    {
        Fallback.OnGameActivation();
    }
    
    #endregion
    
    internal const string TitleSceneName = "SonsTitleScene";
    internal const string LoadingSceneName = "SonsMainLoading";
    internal const string GameSceneName = "SonsMain";
    
    private static readonly Lazy<ISdkEventFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkEventFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkEventFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkEventFallbackDriver>)))();
    });
    internal static ISdkEventFallbackDriver Fallback => FallbackDriver.Value;

    private static ISdkLifecycleDriver _lifecycleDriver;
    private static bool _lifecycleSetupStarted;
    internal static bool _isInitialized;
    internal static bool _onAfterSpawnCalled;
    
    internal class Patches
    {
        internal static ConfiguredPatcher<Patches> _patcher;

        internal static bool? _prevCheatsEnabledState;
    
        public static void Patch()
        {
            Fallback.PatchGameplayEvents();
            
            OnSonsSceneInitialized.Subscribe(scene =>
            {
                Fallback.OnCheatsSceneInitialized(scene);
            });
        }

        [HarmonyPatch(typeof(VailWorldSimulation), nameof(VailWorldSimulation.AddActor))]
        internal static void AddWorldSimActor(WorldSimActor actor, bool onLoad)
        {
            Fallback.OnWorldSimActorAdded(actor, onLoad);
        }
    
        [HarmonyPatch(typeof(VailWorldSimulation), nameof(VailWorldSimulation.RemoveActor))]
        internal static void RemoveWorldSimActor(WorldSimActor removeActor)
        {
            Fallback.OnWorldSimActorRemoved(removeActor);
        }
        
        [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Open))]
        internal static void OpenPauseMenu(PauseMenu __instance)
        {
            Fallback.OnPauseMenuOpened(__instance);
        }
        
        [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Close))]
        internal static void ClosePauseMenu(PauseMenu __instance)
        {
            Fallback.OnPauseMenuClosed(__instance);
        }
        
        [HarmonyPatch(typeof(Sons.Save.GameState), nameof(Sons.Save.GameState.RunQuittingGameCallbacks))]
        internal static void RunQuittingGameCallbacks()
        {
            Fallback.OnWorldExited();
        }
        
        [HarmonyPatch(typeof(GameSetupManager), nameof(GameSetupManager.GetMultiplayerCheatsSetting))]
        internal static void GetMultiplayerCheatsSettingPatch(ref bool __result)
        {
            Fallback.OnMultiplayerCheatsSetting(ref __result);
        }
    }
    
    public class SavingCallbackPatches
    {
        internal static SaveHookPatchState _loadPatchState;
        internal static SaveHookPatchState _savePatchState;

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

            Fallback.BeforeLoad(dir, saveGameType);
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

            Fallback.AfterLoad(dir, saveGameType);
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

            Fallback.BeforeSave(dir, gameName, savePlayerOnly);
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

            Fallback.AfterSave(dir, gameName, savePlayerOnly);
        }
    
        public static void Patch()
        {
            Fallback.PatchSavingCallbacks();
        }

        internal static void ApplyPatch(MethodInfo target, string prefixName, string postfixName, ref SaveHookPatchState state)
        {
            Fallback.ApplySavePatch(target, prefixName, postfixName, ref state);
        }
    }
}
