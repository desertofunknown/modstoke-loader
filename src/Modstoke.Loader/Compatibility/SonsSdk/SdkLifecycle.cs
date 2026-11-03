// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections;
using RedLoader;
using RedLoader.Utils;
using global::Sons.Gui;
using global::Sons.Loading;
using SonsSdk;
using SonsSdk.Private;
using TheForest.Utils;
using Color = System.Drawing.Color;

namespace Modstoke.Loader
{
    internal sealed partial class SdkLifecycle : ISdkLifecycleDriver
    {
        private static readonly SdkLifecycle Driver = new();
        internal static ISdkLifecycleDriver LegacyFallback => Driver;
        private bool _afterSpawnInvoked;
        private static Il2CppSystem.Action _gameActivationHandler;

        private SdkLifecycle() { }

        public ISdkOverlayDriver Overlay => SdkOverlay.Instance;
        public SUI.ISdkPanelBlurDriver Blur => SdkPanelBlur.Instance;
        public SUI.ISdkUiPrefabDriver UiPrefabs => global::Modstoke.Loader.UiPrefabs.Instance;
        public SUI.ISdkUiViewportDriver UiViewport => global::Modstoke.Loader.UiViewport.Instance;
        public SUI.ISdkUiPanelDriver UiPanels => global::Modstoke.Loader.UiPanels.Instance;
        public SUI.ISdkMenuButtonDriver MenuButtons => UiMenuButtons.Instance;
        public ISdkInputIconDriver InputIcons => global::Modstoke.Loader.InputIcons.Instance;
        public ISdkKeybindDriver Keybinds => global::Modstoke.Loader.Keybinds.Instance;
        public ISdkGlobalInputDriver GlobalInput => global::Modstoke.Loader.GlobalInput.Instance;
        public SUI.ISdkSettingsRegistrationDriver Settings => SettingsRegistration.Instance;
        public ISdkModalDialogDriver Dialogs => ModalDialogs.Instance;
        public ISdkTooltipDriver Tooltips => global::Modstoke.Loader.Tooltips.Instance;
        public SonsSdk.Building.ISdkBlueprintRegistrationDriver Blueprints => BlueprintRegistration.Instance;
        public SonsSdk.Networking.ISdkNetworkPrefabDriver NetworkPrefabs => global::Modstoke.Loader.NetworkPrefabs.Instance;
        public SonsSdk.Networking.ISdkPacketDriver Packets => global::Modstoke.Loader.Packets.Instance;
        public SonsSdk.Networking.ISdkServerCommandDriver ServerCommands => global::Modstoke.Loader.ServerCommands.Instance;
        public ISdkGameManagerDriver GameManagers => global::Modstoke.Loader.GameManagers.Instance;

        internal static void Install()
        {
            SdkEvents.SetLifecycleDriver(Driver);
        }

        public void OnSceneInitialized(int sceneIndex, string sceneName)
        {
            switch (sceneName)
            {
                case SdkEvents.TitleSceneName:
                    MainInitializer.InitTitleScene();
                    _afterSpawnInvoked = false;
                    SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Title);
                    break;
                case SdkEvents.LoadingSceneName:
                    SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Loading);
                    break;
                case SdkEvents.GameSceneName:
                    RLog.Msg(Color.DeepPink, "=== Game Scene Initialized ===");
                    // OnGameStart is static and outlives the scene, so keep one subscription.
                    _gameActivationHandler ??= (Il2CppSystem.Action)OnGameActivation;
                    LoadSave.OnGameStart -= _gameActivationHandler;
                    LoadSave.OnGameStart += _gameActivationHandler;
                    SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Game);
                    if (!LoaderEnvironment.IsDedicatedServer)
                        WaitForPauseMenu().RunCoro();
                    break;
            }
        }

        public void OnUpdate()
        {
            if (LoaderEnvironment.IsDedicatedServer)
                return;

            Keybinds.CheckAll();
            if (!LocalPlayer.IsInWorld)
                return;

            if (!_afterSpawnInvoked)
            {
                _afterSpawnInvoked = true;
                SdkEvents.OnAfterSpawn.Invoke();
            }

            SdkEvents.OnInWorldUpdate.Invoke();
        }

        public void OnGameStart()
        {
            SdkEvents.OnGameStart.Invoke();
            ItemTools.ItemHookAdder.Flush();
        }

        private static void OnGameActivation()
        {
            RLog.Msg(Color.DeepPink, "||| Game Activated |||");
            SdkEvents.OnGameActivated.Invoke();
        }

        private static IEnumerator WaitForPauseMenu()
        {
            while (!PauseMenu._instance)
                yield return null;

            SUI.SUI.OnPauseMenuCreated(PauseMenu._instance);
            SdkEvents.OnPauseMenuAvailable.Invoke(PauseMenu._instance);
        }
    }
}
