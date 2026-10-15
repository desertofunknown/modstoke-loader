// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// IL2CPP-registered Unity tick driver for Modstoke Loader.

using System;
using OpenACAI.Loader;
using OpenACAI.Sons;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    public sealed class OpenACAIRuntimeDriver : MonoBehaviour
    {
        private bool _quitting;

        public OpenACAIRuntimeDriver(IntPtr ptr) : base(ptr)
        {
        }

        private void Start()
        {
            OpenACAIUnityLifecycle.InvokeLifecycle("DriverStarted", () => OpenACAISons.PublishLifecycle("DriverStarted"));
            OpenACAIUnityLifecycle.InvokeLifecycle("InitialScene", OpenACAIUnityLifecycle.QueueInitialSceneIfAvailable);
            OpenACAIUnityLifecycle.InvokeLifecycle(nameof(Start), OpenACAILegacyLifecycleBridge.PublishApplicationLateStart);
            OpenACAIUnityLifecycle.InvokeLifecycle("Start.NativeMods", OpenACAINativeModRuntime.Start);
        }

        private void Update()
        {
            _quitting = false;
            OpenACAIUnityLifecycle.InvokeLifecycle("Update.Sons", OpenACAISons.Tick);
            OpenACAIUnityLifecycle.InvokeLifecycle("Update.Assets", OpenACAISonsAssets.Tick);
            OpenACAIUnityLifecycle.InvokeLifecycle("Update.MenuBridge", OpenAcaiSonsMenuBridge.Update);
            OpenACAIUnityLifecycle.InvokeLifecycle("Update.ModMenu", OpenAcaiModMenu.Update);
            OpenACAIUnityLifecycle.InvokeLifecycle("Update.Scenes", OpenACAIUnityLifecycle.PumpSceneInitialized);
            OpenACAIUnityLifecycle.InvokeLifecycle(nameof(Update), OpenACAILegacyLifecycleBridge.PublishUpdate);
            OpenACAIUnityLifecycle.InvokeLifecycle("Update.NativeMods", () => OpenACAINativeModRuntime.Dispatch(OpenACAINativeModRuntime.Hook.Update));
            OpenACAIUnityLifecycle.InvokeLifecycle("LoaderStateRefresh", () => OpenACAIEndnightRuntime.PumpLoaderStateRefresh());
        }

        private void FixedUpdate()
        {
            OpenACAIUnityLifecycle.InvokeLifecycle(nameof(FixedUpdate), OpenACAILegacyLifecycleBridge.PublishFixedUpdate);
            OpenACAIUnityLifecycle.InvokeLifecycle("FixedUpdate.NativeMods", () => OpenACAINativeModRuntime.Dispatch(OpenACAINativeModRuntime.Hook.FixedUpdate));
        }

        private void LateUpdate()
        {
            OpenACAIUnityLifecycle.InvokeLifecycle(nameof(LateUpdate), OpenACAILegacyLifecycleBridge.PublishLateUpdate);
            OpenACAIUnityLifecycle.InvokeLifecycle("LateUpdate.NativeMods", () => OpenACAINativeModRuntime.Dispatch(OpenACAINativeModRuntime.Hook.LateUpdate));
        }

        private void OnGUI()
        {
            OpenACAIUnityLifecycle.InvokeLifecycle(nameof(OnGUI), OpenACAILegacyLifecycleBridge.PublishGui);
            OpenACAIUnityLifecycle.InvokeLifecycle("OnGUI.NativeMods", () => OpenACAINativeModRuntime.Dispatch(OpenACAINativeModRuntime.Hook.Gui));
            OpenACAIUnityLifecycle.InvokeLifecycle("OnGUI.ModMenu", OpenAcaiModMenu.OnGUI);
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
            OpenACAIUnityLifecycle.InvokeLifecycle("ApplicationQuitting", () => OpenACAISons.PublishLifecycle("ApplicationQuitting"));
            OpenACAIUnityLifecycle.InvokeLifecycle("Quit.NativeMods", OpenACAINativeModRuntime.Shutdown);
            OpenACAIUnityLifecycle.InvokeLifecycle(nameof(OnApplicationQuit), OpenACAILegacyLifecycleBridge.PublishApplicationQuit);
            OpenACAIUnityLifecycle.InvokeLifecycle("Quit.AudioBanks", OpenACAISonsAudioBanks.Shutdown);
            OpenACAIUnityLifecycle.InvokeLifecycle("SavePreferences", OpenACAILegacyLifecycleBridge.SavePreferences);
            OpenACAIUnityLifecycle.InvokeLifecycle("UnityLogShutdown", OpenACAIUnityLogSource.Shutdown);
            OpenACAIUnityLifecycle.InvokeLifecycle("LoaderStateFlush", () => OpenACAIEndnightRuntime.PumpLoaderStateRefresh(force: true));
        }

        private void OnDestroy()
        {
            if (_quitting)
                OpenACAIUnityLifecycle.InvokeLifecycle("ApplicationDefiniteQuit", OpenACAILegacyLifecycleBridge.PublishApplicationDefiniteQuit);
        }
    }
}
