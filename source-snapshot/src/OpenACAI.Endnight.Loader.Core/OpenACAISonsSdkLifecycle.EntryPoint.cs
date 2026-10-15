// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using RedLoader;
using RedLoader.Utils;
using Sons.Gui;
using Sons.Save;
using SonsGameManager;
using SonsSdk;
using SonsSdk.Private;
using SUI;
using TheForest;
using TheForest.Utils;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSdkLifecycle
    {
        private bool _shouldCheatsBeEnabled;

        public void OnApplicationLateStart()
        {
            if (MainInitializer.InitializationFailed)
                return;

            try
            {
                GlobalOverlays.CreateBlackScreen();
                SdkEvents.Init();
                MainInitializer.CompleteEventSetup();
            }
            catch (Exception exception)
            {
                MainInitializer.ReportInitializationFailure(exception);
            }
        }

        public void OnSdkInitialized(bool createModManagerUi)
        {
            if (createModManagerUi)
                ModManagerUi.Create();

            ModSettingsUi.Create();

            var save = Utility.GetCommandLineArgValue("--savegame");
            if (!string.IsNullOrEmpty(save) && int.TryParse(save, out var id))
            {
                RLog.Msg($"Found save game argument ({save}). Loading now...");
                Resources.FindObjectsOfTypeAll<LoadMenu>()
                    .First(menu => menu.name == "SinglePlayerLoadPanel")
                    .LoadSlotActivated(id, SaveGameManager.GetData<GameStateData>(SaveGameType.SinglePlayer, id));
            }
        }

        public void OnSdkGameStart()
        {
            GameCommands.Init();

            if (!BoltNetwork.isRunning)
                _shouldCheatsBeEnabled = true;

            SetConsoleEnabled(_shouldCheatsBeEnabled);
            PanelBlur.SetupBlur();
            LoadBootFile();
        }

        public void OnCheatsEnabledChanged(bool isEnabled)
        {
            SetConsoleEnabled(isEnabled);
            if (LocalPlayer._instance)
                SonsTools.ShowMessage("Cheats have been " + (isEnabled ? "enabled" : "disabled"));

            _shouldCheatsBeEnabled = isEnabled;
        }

        private static void SetConsoleEnabled(bool shouldEnable)
        {
            DebugConsole.SetCheatsAllowed(shouldEnable);
            if (DebugConsole.Instance)
                DebugConsole.Instance.SetBlockConsole(!shouldEnable);
        }

        private static void LoadBootFile()
        {
            try
            {
                var bootFile = Path.Combine(LoaderEnvironment.GameRootDirectory, "boot.txt");
                if (!File.Exists(bootFile))
                    return;

                var lines = File.ReadAllLines(bootFile);
                foreach (var line in lines)
                    DebugConsole.Instance.SendCommand(line);
            }
            catch (Exception exception)
            {
                RLog.Error("Error loading boot file: " + exception);
            }
        }
    }
}
