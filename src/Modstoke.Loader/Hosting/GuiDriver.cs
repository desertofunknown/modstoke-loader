// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// The loader's IMGUI pass. Unity runs layout, repaint and input events for every enabled behaviour with an OnGUI,
// so this lives apart from the runtime driver and is enabled only while something draws.

using System;
using UnityEngine;

namespace Modstoke.Loader
{
    public sealed class GuiDriver : MonoBehaviour
    {
        private const float NativePollSeconds = 1f;
        private static float _nextNativePoll;
        private static bool _nativeGui;

        public GuiDriver(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>
        /// Whether anything draws with IMGUI: the fallback mods menu, a RedLoader OnGUI subscriber or a native mod's Gui
        /// hook. Native hooks only change as mods start or fail, so they are looked at once a second.
        /// </summary>
        internal static bool Wanted
        {
            get
            {
                if (MenuController.DrawsFallback || LegacyLifecycleBridge.HasGuiSubscribers)
                    return true;
                var now = Time.unscaledTime;
                if (now >= _nextNativePoll)
                {
                    _nextNativePoll = now + NativePollSeconds;
                    _nativeGui = ModRuntime.Dispatches(ModRuntime.Hook.Gui);
                }
                return _nativeGui;
            }
        }

        private void OnGUI()
        {
            UnityLifecycle.InvokeLifecycle("OnGUI", LegacyLifecycleBridge.PublishGui);
            UnityLifecycle.InvokeLifecycle("OnGUI.NativeMods", DispatchNative);
            UnityLifecycle.InvokeLifecycle("OnGUI.ModMenu", MenuController.OnGUI);
        }

        private static void DispatchNative() => ModRuntime.Dispatch(ModRuntime.Hook.Gui);
    }
}
