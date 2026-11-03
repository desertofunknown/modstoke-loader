// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Renderer dispatcher for Modstoke Mods menu backends.

using System;
using System.Diagnostics;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class MenuDispatcher
    {
        internal const string NativeMode = "native";
        internal const string FallbackMode = "imgui-fallback";
        internal const string RefreshFailedMode = "refresh-failed";
        internal const string NativeShowFailedMode = "native-show-failed";
        internal const string NativeHideFailedMode = "native-hide-failed";
        internal const string HiddenMode = "already-hidden";
        private const string FocusOwnerId = "openacai.loader.mods-menu";

        private static IDisposable _focus;

        // While either view is open the menu holds SDK input focus, so other mods' windows and keybinds wait
        // for it. Called every frame; a window that already holds focus keeps it until it closes.
        internal static void SyncFocus(bool visible)
        {
            if (visible == (_focus != null))
                return;
            if (visible)
            {
                InputBindings.TryAcquireFocus(FocusOwnerId, out _focus, KeybindIds.ToggleModsMenu, KeybindIds.Back);
                return;
            }
            _focus.Dispose();
            _focus = null;
        }

        internal static bool IsVisible(bool fallbackVisible)
        {
            return fallbackVisible || MenuRenderer.IsVisible;
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
                    nativeShown = MenuRenderer.Show(inventory);
                }
                catch (Exception ex)
                {
                    mode = NativeShowFailedMode;
                    RuntimeEntryPoint.LogSource?.LogWarning("Native Modstoke Mods menu dispatch failed; falling back to IMGUI: " + ex.Message);
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
                Timings.RecordDuration("ui.mods-menu", "open", stopwatch.Elapsed, mode);
                RuntimeEntryPoint.RequestLoaderStateRefresh("mods-menu-open:" + mode);
            }
        }

        internal static void Hide(bool fallbackVisible, Action hideFallback)
        {
            var stopwatch = Stopwatch.StartNew();
            var mode = MenuRenderer.IsVisible ? NativeMode : (fallbackVisible ? FallbackMode : HiddenMode);
            try
            {
                try
                {
                    MenuRenderer.Hide();
                }
                catch (Exception ex)
                {
                    mode = NativeHideFailedMode;
                    RuntimeEntryPoint.LogSource?.LogWarning("Native Modstoke Mods menu hide dispatch failed: " + ex.Message);
                }

                hideFallback?.Invoke();
            }
            finally
            {
                stopwatch.Stop();
                Timings.RecordDuration("ui.mods-menu", "hide", stopwatch.Elapsed, mode);
                RuntimeEntryPoint.RequestLoaderStateRefresh("mods-menu-hide:" + mode);
            }
        }

        internal static void UpdateNative()
        {
            try
            {
                MenuRenderer.Update();
            }
            catch (Exception ex)
            {
                // A failure that repeats every frame would otherwise leave the menu open, holding focus, with no key to close it.
                RuntimeEntryPoint.LogSource?.LogWarning("Native Modstoke Mods menu update failed; closing it: " + ex);
                Hide(false, null);
            }
        }

        internal static bool ShouldDrawFallback(bool fallbackVisible)
        {
            return fallbackVisible && !MenuRenderer.IsVisible;
        }
    }
}

