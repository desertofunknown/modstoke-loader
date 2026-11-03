// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using RedLoader;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Save;
using SonsSdk;
using SdkGameState = SonsSdk.GameState;

namespace Modstoke.Loader
{
    internal sealed partial class SdkLifecycle
    {
        public void BeforeLoad(string directory, SaveGameType saveGameType)
        {
            if (!SaveGameManager.HasInstance)
            {
                RLog.Error("SaveGameManager not initialized, aborting load callback.");
                return;
            }

            SdkGameState.LastLoadedSaveId = GameSetupManager.GetSelectedSaveId();
            SdkEvents.BeforeLoadSave.Invoke();
        }

        public void AfterLoad(string directory, SaveGameType saveGameType)
        {
            if (!SaveGameManager.HasInstance)
                return;

            SdkEvents.AfterLoadSave.Invoke();
        }

        public void BeforeSave(string directory, string gameName, bool savePlayerOnly)
        {
            if (!SaveGameManager.HasInstance)
            {
                RLog.Error("SaveGameManager not initialized, aborting save callback.");
                return;
            }

            SdkEvents.BeforeSaveLoading.Invoke();
        }

        public void AfterSave(string directory, string gameName, bool savePlayerOnly)
        {
            if (!SaveGameManager.HasInstance)
                return;

            SdkEvents.AfterSaveLoading.Invoke(savePlayerOnly);
        }
    }
}
