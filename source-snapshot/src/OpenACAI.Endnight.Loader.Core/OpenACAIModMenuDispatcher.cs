// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Renderer dispatcher for Modstoke Mods menu backends.

using System;
using System.Diagnostics;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenAcaiModMenuDispatcher
    {
        internal const string NativeMode = "native";
        internal const string FallbackMode = "imgui-fallback";
        internal const string RefreshFailedMode = "refresh-failed";
        internal const string NativeShowFailedMode = "native-show-failed";
        internal const string NativeHideFailedMode = "native-hide-failed";
        internal const string HiddenMode = "already-hidden";

        internal static bool IsVisible(bool fallbackVisible)
        {
            return fallbackVisible || OpenAcaiNativeModMenu.IsVisible;
        }

        internal static void Show(ModInventoryState inventory, Action showFallback, Action hideFallbackForNative, string fallbackMode)
        {
            var stopwatch = Stopwatch.StartNew();
            var mode = string.IsNullOrWhiteSpace(fallbackMode) ? FallbackMode : fallbackMode;
            try
            {
                var nativeShown = false;
                try
                {
                    nativeShown = OpenAcaiNativeModMenu.Show(inventory);
                }
                catch (Exception ex)
                {
                    mode = NativeShowFailedMode;
                    OpenACAIEndnightRuntime.LogSource?.LogWarning("Native Modstoke Mods menu dispatch failed; falling back to IMGUI: " + ex.Message);
                }

                if (nativeShown)
                {
                    mode = NativeMode;
                    hideFallbackForNative?.Invoke();
                    return;
                }

                showFallback?.Invoke();
            }
            finally
            {
                stopwatch.Stop();
                OpenACAITimings.RecordDuration("ui.mods-menu", "open", stopwatch.Elapsed, mode);
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("mods-menu-open:" + mode);
            }
        }

        internal static void Hide(bool fallbackVisible, Action hideFallback)
        {
            var stopwatch = Stopwatch.StartNew();
            var mode = OpenAcaiNativeModMenu.IsVisible ? NativeMode : (fallbackVisible ? FallbackMode : HiddenMode);
            try
            {
                try
                {
                    OpenAcaiNativeModMenu.Hide();
                }
                catch (Exception ex)
                {
                    mode = NativeHideFailedMode;
                    OpenACAIEndnightRuntime.LogSource?.LogWarning("Native Modstoke Mods menu hide dispatch failed: " + ex.Message);
                }

                hideFallback?.Invoke();
            }
            finally
            {
                stopwatch.Stop();
                OpenACAITimings.RecordDuration("ui.mods-menu", "hide", stopwatch.Elapsed, mode);
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("mods-menu-hide:" + mode);
            }
        }

        internal static void UpdateNative()
        {
            OpenAcaiNativeModMenu.Update();
        }

        internal static bool ShouldDrawFallback(bool fallbackVisible)
        {
            return fallbackVisible && !OpenAcaiNativeModMenu.IsVisible;
        }
    }
}

