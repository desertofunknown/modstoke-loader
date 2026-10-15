// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections;
using RedLoader;
using Sons.Gui;
using Sons.Loading;
using SonsSdk;
using SonsSdk.Private;
using TheForest.Utils;
using Color = System.Drawing.Color;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSdkLifecycle : ISdkLifecycleDriver
    {
        private static readonly OpenACAISonsSdkLifecycle Driver = new();
        private bool _afterSpawnInvoked;

        private OpenACAISonsSdkLifecycle() { }

        public ISdkOverlayDriver Overlay => OpenACAISonsSdkOverlay.Instance;
        public SUI.ISdkPanelBlurDriver Blur => OpenACAISonsSdkPanelBlur.Instance;
        public SUI.ISdkUiPrefabDriver UiPrefabs => OpenACAISonsUiPrefabs.Instance;
        public SUI.ISdkUiViewportDriver UiViewport => OpenACAISonsUiViewport.Instance;
        public SUI.ISdkUiPanelDriver UiPanels => OpenACAISonsUiPanels.Instance;
        public SUI.ISdkMenuButtonDriver MenuButtons => OpenACAISonsUiMenuButtons.Instance;
        public ISdkInputIconDriver InputIcons => OpenACAISonsInputIcons.Instance;
        public ISdkKeybindDriver Keybinds => OpenACAISonsKeybinds.Instance;
        public ISdkGlobalInputDriver GlobalInput => OpenACAISonsGlobalInput.Instance;
        public SUI.ISdkSettingsRegistrationDriver Settings => OpenACAISonsSettingsRegistration.Instance;
        public ISdkModalDialogDriver Dialogs => OpenACAISonsModalDialogs.Instance;
        public ISdkTooltipDriver Tooltips => OpenACAISonsTooltips.Instance;
        public SonsSdk.Building.ISdkBlueprintRegistrationDriver Blueprints => OpenACAISonsBlueprintRegistration.Instance;
        public SonsSdk.Networking.ISdkNetworkPrefabDriver NetworkPrefabs => OpenACAISonsNetworkPrefabs.Instance;
        public SonsSdk.Networking.ISdkPacketDriver Packets => OpenACAISonsPackets.Instance;
        public SonsSdk.Networking.ISdkServerCommandDriver ServerCommands => OpenACAISonsServerCommands.Instance;
        public ISdkGameManagerDriver GameManagers => OpenACAISonsGameManagers.Instance;

        internal static void Install()
        {
            SdkEvents.SetLifecycleDriver(Driver);
        }

        public void OnSceneInitialized(int sceneIndex, string sceneName)
        {
            switch (sceneName)
            {
                case "SonsTitleScene":
                    MainInitializer.InitTitleScene();
                    _afterSpawnInvoked = false;
                    SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Title);
                    break;
                case "SonsMainLoading":
                    SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Loading);
                    break;
                case "SonsMain":
                    RLog.Msg(Color.DeepPink, "=== Game Scene Initialized ===");
                    LoadSave.OnGameStart += (Il2CppSystem.Action)OnGameActivation;
                    SdkEvents.OnSonsSceneInitialized.Invoke(ESonsScene.Game);
                    WaitForPauseMenu().RunCoro();
                    break;
            }
        }

        public void OnUpdate()
        {
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
