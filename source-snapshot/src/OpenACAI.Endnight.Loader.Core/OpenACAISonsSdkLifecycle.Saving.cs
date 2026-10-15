// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using RedLoader;
using Sons.Gameplay.GameSetup;
using Sons.Save;
using SonsSdk;
using SdkGameState = SonsSdk.GameState;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSdkLifecycle
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
