// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Harmony;
using HarmonyLib;
using Sons.Ai.Vail;
using Sons.Gameplay.GameSetup;
using Sons.Gui;
using SonsSdk;
using SonsSdk.Private;
using NativeGameState = global::Sons.Save.GameState;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSdkLifecycle
    {
        public void PatchGameplayEvents()
        {
            GameplayPatches.Apply();
        }

        private sealed class GameplayPatches
        {
            private static bool? _previousCheatsEnabled;

            internal static void Apply()
            {
                var patcher = new ConfiguredPatcher<GameplayPatches>(SdkEntryPoint.Harmony);
                patcher.Patch(nameof(AddWorldSimActor));
                patcher.Patch(nameof(RemoveWorldSimActor));
                patcher.Patch(nameof(OpenPauseMenu));
                patcher.Patch(nameof(ClosePauseMenu));
                patcher.Patch(nameof(RunQuittingGameCallbacks));
                patcher.Patch(nameof(GetMultiplayerCheatsSettingPatch));

                SdkEvents.OnSonsSceneInitialized.Subscribe(OnSceneInitialized);
            }

            private static void OnSceneInitialized(ESonsScene scene)
            {
                if (scene == ESonsScene.Title)
                    _previousCheatsEnabled = null;
            }

            [HarmonyPatch(typeof(VailWorldSimulation), nameof(VailWorldSimulation.AddActor))]
            private static void AddWorldSimActor(WorldSimActor actor, bool onLoad)
            {
                SdkEvents.OnWorldSimActorAdded.Invoke(actor);
            }

            [HarmonyPatch(typeof(VailWorldSimulation), nameof(VailWorldSimulation.RemoveActor))]
            private static void RemoveWorldSimActor(WorldSimActor removeActor)
            {
                SdkEvents.OnWorldSimActorRemoved.Invoke(removeActor);
            }

            [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Open))]
            private static void OpenPauseMenu(PauseMenu __instance)
            {
                SdkEvents.OnPauseMenuOpened.Invoke(__instance);
            }

            [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Close))]
            private static void ClosePauseMenu(PauseMenu __instance)
            {
                SdkEvents.OnPauseMenuClosed.Invoke(__instance);
            }

            [HarmonyPatch(typeof(NativeGameState), nameof(NativeGameState.RunQuittingGameCallbacks))]
            private static void RunQuittingGameCallbacks()
            {
                SdkEvents.OnWorldExited.Invoke();
            }

            [HarmonyPatch(typeof(GameSetupManager), nameof(GameSetupManager.GetMultiplayerCheatsSetting))]
            private static void GetMultiplayerCheatsSettingPatch(ref bool __result)
            {
                if (_previousCheatsEnabled == null || _previousCheatsEnabled.Value != __result)
                {
                    SdkEvents.OnCheatsEnabledChanged.Invoke(__result);
                    _previousCheatsEnabled = __result;
                }
            }
        }
    }
}
