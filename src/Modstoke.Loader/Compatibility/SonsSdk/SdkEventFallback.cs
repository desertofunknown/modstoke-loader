// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility event dispatch adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections;
using Harmony;
using RedLoader;
using RedLoader.Utils;
using global::Sons.Ai.Vail;
using global::Sons.Events;
using global::Sons.Gui;
using global::Sons.Inventory;
using global::Sons.Loading;
using SonsSdk;
using SonsSdk.Private;
using TheForest.Utils;
using Color = System.Drawing.Color;
using Priority = HarmonyLib.Priority;

namespace Modstoke.Loader;

internal sealed partial class SdkEventFallback : ISdkEventFallbackDriver
{
    private static readonly SdkEventFallback Driver = new();
    private static Il2CppSystem.Action _gameActivationHandler;

    internal static ISdkEventFallbackDriver Create() => Driver;

    public void Initialize()
    {
        if (SdkEvents._isInitialized)
            return;

        var driver = SdkEvents.BeginLifecycleSetup();
        GlobalEvents.OnSceneWasInitialized.Subscribe(SdkEvents.OnSceneWasInitialized, Priority.First);
        GlobalEvents.OnUpdate.Subscribe(SdkEvents.OnUpdateInternal, Priority.First);

        if (driver != null)
        {
            driver.RegisterGameEvents();
            driver.PatchGameplayEvents();
        }
        else
        {
            RegisterGameEvents();
            SdkEvents.Patches.Patch();
        }

        SdkEvents.SavingCallbackPatches.Patch();
        SdkEvents._isInitialized = true;
    }

    private static void RegisterGameEvents()
    {
        EventRegistry.Register(GameEvent.ItemPickedUp, (EventRegistry.SubscriberCallback)SdkEvents.SonsEventsOnItemPickedUp);
        EventRegistry.Register(GameEvent.LoadedMainScene, (EventRegistry.SubscriberCallback)SdkEvents.SonsEventsOnGameStart);
        EventRegistry.Register(GameEvent.ItemCrafted, (EventRegistry.SubscriberCallback)SdkEvents.SonsEventOnItemCrafted);
        EventRegistry.Register(GameEvent.ItemConsumed, (EventRegistry.SubscriberCallback)SdkEvents.SonsEventOnItemConsumed);
        EventRegistry.Register(GameEvent.ArmorEquipped, (EventRegistry.SubscriberCallback)SdkEvents.SonsEventOnArmorEquipped);
    }

    public IEnumerator WaitForPauseMenu()
    {
        if (LoaderEnvironment.IsDedicatedServer)
            yield break;

        while (!PauseMenu._instance)
            yield return null;

        SUI.SUI.OnPauseMenuCreated(PauseMenu._instance);
        SdkEvents.OnPauseMenuAvailable.Invoke(PauseMenu._instance);
    }

    public void OnSceneInitialized(int sceneIndex, string sceneName)
    {
        switch (sceneName)
        {
            case SdkEvents.TitleSceneName:
                MainInitializer.InitTitleScene();
                SdkEvents._onAfterSpawnCalled = false;
                SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Title);
                break;
            case SdkEvents.LoadingSceneName:
                SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Loading);
                break;
            case SdkEvents.GameSceneName:
                RLog.Msg(Color.DeepPink, "=== Game Scene Initialized ===");
                // OnGameStart is static and outlives the scene, so keep one subscription.
                _gameActivationHandler ??= (Il2CppSystem.Action)SdkEvents.OnGameActivation;
                LoadSave.OnGameStart -= _gameActivationHandler;
                LoadSave.OnGameStart += _gameActivationHandler;
                SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Game);
                if (!LoaderEnvironment.IsDedicatedServer)
                    SdkEvents.WaitForPauseMenu().RunCoro();
                break;
        }
    }

    public void OnUpdate()
    {
        if (LoaderEnvironment.IsDedicatedServer)
            return;

        ModInputCache.CheckAll();
        if (!LocalPlayer.IsInWorld)
            return;

        if (!SdkEvents._onAfterSpawnCalled)
        {
            SdkEvents._onAfterSpawnCalled = true;
            SdkEvents.OnAfterSpawn.Invoke();
        }

        SdkEvents.OnInWorldUpdate.Invoke();
    }

    public void OnItemPickedUp(Il2CppSystem.Object value)
    {
        var item = value.TryCast<ItemInstance.ItemInstanceAndCount>();
        if (item == null)
            return;

        SdkEvents.OnItemPickup.Invoke(item);
    }

    public void OnItemConsumed(Il2CppSystem.Object value)
    {
        var item = value.TryCast<ItemInstance>();
        if (item == null)
            return;

        SdkEvents.OnItemConsumed.Invoke(item);
    }

    public void OnArmorEquipped(object value) => SdkEvents.OnArmorEquipped.Invoke();

    public void OnItemCrafted(Il2CppSystem.Object value)
    {
        var itemId = value.Unbox<int>();
        SdkEvents.OnItemCrafted.Invoke(itemId);
    }

    public void OnGameStart(object value)
    {
        SdkEvents.OnGameStart.Invoke();
        ItemTools.ItemHookAdder.Flush();
    }

    public void OnGameActivation()
    {
        RLog.Msg(Color.DeepPink, "||| Game Activated |||");
        SdkEvents.OnGameActivated.Invoke();
    }

    public void PatchGameplayEvents()
    {
        SdkEvents.Patches._patcher = new ConfiguredPatcher<SdkEvents.Patches>(global::SonsSdk.SdkEntryPoint.Harmony);
        SdkEvents.Patches._patcher.Patch(nameof(SdkEvents.Patches.AddWorldSimActor));
        SdkEvents.Patches._patcher.Patch(nameof(SdkEvents.Patches.RemoveWorldSimActor));
        SdkEvents.Patches._patcher.Patch(nameof(SdkEvents.Patches.OpenPauseMenu));
        SdkEvents.Patches._patcher.Patch(nameof(SdkEvents.Patches.ClosePauseMenu));
        SdkEvents.Patches._patcher.Patch(nameof(SdkEvents.Patches.RunQuittingGameCallbacks));
        SdkEvents.Patches._patcher.Patch(nameof(SdkEvents.Patches.GetMultiplayerCheatsSettingPatch));
    }

    public void OnCheatsSceneInitialized(ESonsScene scene)
    {
        if (scene == ESonsScene.Title)
            SdkEvents.Patches._prevCheatsEnabledState = null;
    }

    public void OnWorldSimActorAdded(WorldSimActor actor, bool onLoad) => SdkEvents.OnWorldSimActorAdded.Invoke(actor);

    public void OnWorldSimActorRemoved(WorldSimActor actor) => SdkEvents.OnWorldSimActorRemoved.Invoke(actor);

    public void OnPauseMenuOpened(PauseMenu menu) => SdkEvents.OnPauseMenuOpened.Invoke(menu);

    public void OnPauseMenuClosed(PauseMenu menu) => SdkEvents.OnPauseMenuClosed.Invoke(menu);

    public void OnWorldExited() => SdkEvents.OnWorldExited.Invoke();

    public void OnMultiplayerCheatsSetting(ref bool result)
    {
        if (SdkEvents.Patches._prevCheatsEnabledState == null || SdkEvents.Patches._prevCheatsEnabledState.Value != result)
        {
            SdkEvents.OnCheatsEnabledChanged.Invoke(result);
            SdkEvents.Patches._prevCheatsEnabledState = result;
        }
    }
}
