// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// IL2CPP-registered Unity tick driver for Modstoke Loader.

using System;
using Modstoke.Loader;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Loader
{
    public sealed class RuntimeDriver : MonoBehaviour
    {
        private bool _quitting;

        public RuntimeDriver(IntPtr ptr) : base(ptr)
        {
        }

        private void Start()
        {
            WarningCapture.MarkMainThread();
            UnityLifecycle.InvokeLifecycle("DriverStarted", () => GameRuntime.PublishLifecycle("DriverStarted"));
            UnityLifecycle.InvokeLifecycle("InitialScene", UnityLifecycle.QueueInitialSceneIfAvailable);
            UnityLifecycle.InvokeLifecycle(nameof(Start), LegacyLifecycleBridge.PublishApplicationLateStart);
            UnityLifecycle.InvokeLifecycle("Start.NativeMods", ModRuntime.Start);
            UnityLifecycle.InvokeLifecycle("Start.Console", RuntimeConsole.Initialize);
            UnityLifecycle.InvokeLifecycle("Start.Host", Host.Initialize);
            UnityLifecycle.InvokeLifecycle("Start.ConsoleOverlay", ConsoleOverlay.Initialize);
        }

        private void Update()
        {
            if (_quitting)
                return;
            UnityLifecycle.InvokeLifecycle("Update.Capture", PerformanceCapture.Tick);
            UnityLifecycle.InvokeLifecycle("Update.Sons", GameRuntime.Tick);
            UnityLifecycle.InvokeLifecycle("Update.Console", RuntimeConsole.Update);
            UnityLifecycle.InvokeLifecycle("Update.Host", Host.Tick);
            UnityLifecycle.InvokeLifecycle("Update.ConsoleOverlay", ConsoleOverlay.Update);
            UnityLifecycle.InvokeLifecycle("Update.OwnershipMigration", BoltMigrationCoordinator.Tick);
            UnityLifecycle.InvokeLifecycle("Update.PlayerBridge", PlayerBridge.Tick);
            UnityLifecycle.InvokeLifecycle("Update.PlayerClientMovement", PlayerClientMovement.Tick);
            UnityLifecycle.InvokeLifecycle("Update.PlayerEquipment", PlayerEquipment.Tick);
            UnityLifecycle.InvokeLifecycle("Update.PlayerInventory", PlayerInventoryEnforcement.Tick);
            UnityLifecycle.InvokeLifecycle("Update.ServerNotices", ServerNotices.Tick);
            UnityLifecycle.InvokeLifecycle("Update.LocatorRecords", LocatorRecordReads.Tick);
            UnityLifecycle.InvokeLifecycle("Update.CompanionJobs", CompanionJobs.Pump);
            UnityLifecycle.InvokeLifecycle("Update.Assets", Assets.Tick);
            UnityLifecycle.InvokeLifecycle("Update.PreviewStage", PreviewStage.Tick);
            UnityLifecycle.InvokeLifecycle("Update.ModMenu", MenuController.Update);
            UnityLifecycle.InvokeLifecycle("Update.ScreenHost", ScreenHost.Tick);
            UnityLifecycle.InvokeLifecycle("Update.Scenes", UnityLifecycle.PumpSceneInitialized);
            UnityLifecycle.InvokeLifecycle(nameof(Update), LegacyLifecycleBridge.PublishUpdate);
            UnityLifecycle.InvokeLifecycle("Update.NativeMods", () => ModRuntime.Dispatch(ModRuntime.Hook.Update));
            UnityLifecycle.InvokeLifecycle("LoaderStateRefresh", () => RuntimeEntryPoint.PumpLoaderStateRefresh());
        }

        private void FixedUpdate()
        {
            if (_quitting)
                return;
            UnityLifecycle.InvokeLifecycle("FixedUpdate.Capture", PerformanceCapture.FixedTick);
            UnityLifecycle.InvokeLifecycle("FixedUpdate.PlayerGroundMovement", PlayerGroundMovement.FixedTick);
            UnityLifecycle.InvokeLifecycle(nameof(FixedUpdate), LegacyLifecycleBridge.PublishFixedUpdate);
            UnityLifecycle.InvokeLifecycle("FixedUpdate.NativeMods", () => ModRuntime.Dispatch(ModRuntime.Hook.FixedUpdate));
        }

        private void LateUpdate()
        {
            if (_quitting)
                return;
            UnityLifecycle.InvokeLifecycle(nameof(LateUpdate), LegacyLifecycleBridge.PublishLateUpdate);
            UnityLifecycle.InvokeLifecycle("LateUpdate.NativeMods", () => ModRuntime.Dispatch(ModRuntime.Hook.LateUpdate));
        }

        private void OnGUI()
        {
            if (_quitting)
                return;
            UnityLifecycle.InvokeLifecycle(nameof(OnGUI), LegacyLifecycleBridge.PublishGui);
            UnityLifecycle.InvokeLifecycle("OnGUI.NativeMods", () => ModRuntime.Dispatch(ModRuntime.Hook.Gui));
            UnityLifecycle.InvokeLifecycle("OnGUI.ModMenu", MenuController.OnGUI);
        }

        private void OnApplicationQuit()
        {
            if (_quitting)
                return;
            _quitting = true;
            UnityLifecycle.InvokeLifecycle("Quit.ScreenHost", ScreenHost.Shutdown);
            UnityLifecycle.InvokeLifecycle("Quit.PreviewStage", PreviewStage.Shutdown);
            UnityLifecycle.InvokeLifecycle("Quit.ConsoleOverlay", ConsoleOverlay.Shutdown);
            UnityLifecycle.InvokeLifecycle("Quit.Host", Host.Shutdown);
            UnityLifecycle.InvokeLifecycle("Quit.Console", RuntimeConsole.Shutdown);
            UnityLifecycle.InvokeLifecycle("Quit.Capture", () => PerformanceCapture.Stop("application-quit"));
            UnityLifecycle.InvokeLifecycle("ApplicationQuitting", () => GameRuntime.PublishLifecycle("ApplicationQuitting"));
            UnityLifecycle.InvokeLifecycle("Quit.LocatorRecords", LocatorRecordReads.RequestReset);
            UnityLifecycle.InvokeLifecycle("Quit.NativeMods", ModRuntime.Shutdown);
            UnityLifecycle.InvokeLifecycle("Quit.ServerNotices", ServerNotices.ClearHostCommandSession);
            UnityLifecycle.InvokeLifecycle(nameof(OnApplicationQuit), LegacyLifecycleBridge.PublishApplicationQuit);
            UnityLifecycle.InvokeLifecycle("Quit.AudioBanks", AudioBanks.Shutdown);
            UnityLifecycle.InvokeLifecycle("SavePreferences", LegacyLifecycleBridge.SavePreferences);
            UnityLifecycle.InvokeLifecycle("Quit.WarningCapture", () => WarningCapture.Stop("application-quit"));
            UnityLifecycle.InvokeLifecycle("UnityLogShutdown", UnityLogSource.Shutdown);
            UnityLifecycle.InvokeLifecycle("LoaderStateFlush", () => RuntimeEntryPoint.PumpLoaderStateRefresh(force: true));
        }

        private void OnDestroy()
        {
            if (_quitting)
                UnityLifecycle.InvokeLifecycle("ApplicationDefiniteQuit", LegacyLifecycleBridge.PublishApplicationDefiniteQuit);
        }
    }
}
