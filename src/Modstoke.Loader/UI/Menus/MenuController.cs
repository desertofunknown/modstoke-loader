// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Lifecycle controller for the Modstoke Mods screen.

using System;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class MenuController
    {
        internal const int MaxConfigEditorBytes = 262144;
        private static bool _visible;
        private static object _liveControlsSession;
        private static Action _closeLiveControls;
        private static bool _openingLiveControls;
        private static bool _resumeAfterLiveControls;
        private static bool _liveControlsFromNative;
        private static bool _liveControlsFromFrontEnd;
        private static int _liveControlsSceneHandle;
        private static int _liveControlsClosedFrame;
        // The page a screen host resumes on, in place of the live controls' config entry.
        private static string _resumePage;

        internal static bool IsVisible => MenuDispatcher.IsVisible(_visible);

        /// <summary>Whether a mod's live settings or actions are open.</summary>
        internal static bool LiveControlsOpen => _liveControlsSession != null;

        internal static void SetInventory(ModInventoryState inventory)
        {
            MenuState.SetInventory(inventory);
        }

        /// <summary>
        /// Opens the Mods screen where Modstoke's menus are on: at the title, in the pause menu, and from the world over
        /// the pause menu it brings up. This menu's own home opens where they are off, or the pause menu can't open.
        /// </summary>
        internal static void Show()
        {
            if (!_openingLiveControls && !ScreenHost.IsOpen && _closeLiveControls == null && FrontEnd.OpenModsScreen())
            {
                return;
            }

            MenuState.EndHandOver();
            ShowNative();
        }

        /// <summary>Shows one mod's page, handed over from the Mods screen, which Back from that page returns to.</summary>
        internal static void ShowHandedOver(string pageKey)
        {
            MenuState.OpenHandedOver(pageKey);
            ShowNative();
        }

        /// <summary>
        /// Shows a mod's config file in this menu's text editor, handed over from the Mods screen's config page, for a file
        /// that page can't list; Back from the editor returns there.
        /// </summary>
        internal static void ShowConfigText(ModInventoryEntry entry)
        {
            ShowHandedOver(MenuKeys.Mod(entry));
            if (MenuRenderer.IsVisible) MenuRenderer.EditConfigText(entry);
        }

        private static void ShowNative()
        {
            // The pause menu's MODS button skips the focus checks that keep the menu shut under a screen host.
            if (_openingLiveControls || ScreenHost.IsOpen)
            {
                return;
            }

            if (_closeLiveControls != null)
            {
                _closeLiveControls();
                return;
            }

            var fallbackMode = MenuDispatcher.FallbackMode;
            try
            {
                RuntimeEntryPoint.RefreshModInventory();
            }
            catch (Exception ex)
            {
                MenuState.SetConfigStatus("Refresh failed: " + ex.Message);
                fallbackMode = MenuDispatcher.RefreshFailedMode;
            }

            MenuDispatcher.Show(MenuState.Inventory, ShowFallback, HideFallbackForNative, fallbackMode);
        }

        internal static void Hide()
        {
            MenuDispatcher.Hide(_visible, HideFallback);
        }

        internal static void Toggle()
        {
            if (MenuDispatcher.IsVisible(_visible))
            {
                // The native menu asks about unsaved config edits before it closes.
                MenuRenderer.GuardClose(Hide);
            }
            else
            {
                Show();
            }
        }

        internal static void Update()
        {
            MenuDispatcher.SyncFocus(MenuDispatcher.IsVisible(_visible));
            if (ModActions.Update())
            {
                if (_closeLiveControls != null && InputBindings.IsPressed(KeybindIds.ToggleModsMenu))
                {
                    _closeLiveControls();
                }

                return;
            }

            if (_resumeAfterLiveControls)
            {
                if (Time.frameCount == _liveControlsClosedFrame)
                {
                    return;
                }

                _resumeAfterLiveControls = false;
                var page = _resumePage;
                _resumePage = null;
                // A screen opened since the close owns the input, and the menu would sit under it without a lease.
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != _liveControlsSceneHandle || ScreenHost.IsOpen)
                {
                    return;
                }

                if (_liveControlsFromNative)
                {
                    MenuRenderer.ResumeConfig(page);
                }
                else
                {
                    ResumeFallback(page);
                }

                return;
            }

            if (_closeLiveControls != null)
            {
                if (InputBindings.IsPressed(KeybindIds.ToggleModsMenu))
                {
                    _closeLiveControls();
                }

                return;
            }

            MenuDispatcher.UpdateNative();
            UpdateFallback();

            if (InputBindings.IsPressed(KeybindIds.ToggleModsMenu))
            {
                Toggle();
            }

            if (_visible && InputBindings.IsPressed(KeybindIds.Back))
            {
                NavigateBack();
            }
        }

        /// <summary>
        /// Opens live controls for a mod page's entry through <paramref name="open"/>. They resume through the open config
        /// entry, so when they stay closed that entry is closed again; true then, with the reason for the page footer.
        /// </summary>
        internal static bool OpenLiveControlsFor(string entryId, Action open, out string status)
        {
            status = null;
            var entry = MenuState.FindEntry(entryId);
            if (entry == null)
            {
                return false;
            }

            MenuState.CloseConfig();
            MenuState.OpenConfig(entry, MaxConfigEditorBytes);
            open();
            if (!MenuDispatcher.IsVisible(_visible))
            {
                return false;
            }

            status = MenuState.ConfigStatus;
            MenuState.CloseConfig();
            return true;
        }

        /// <summary>
        /// Opens a mod's live settings or actions over the Mods screen, which stays as it is underneath and shows again
        /// once they close; false when the mod has none to open now.
        /// </summary>
        internal static bool OpenLiveControlsOverFrontEnd(string entryId, bool settings)
        {
            var entry = MenuState.FindEntry(entryId);
            if (entry == null)
            {
                return false;
            }

            // They open for the config entry, as from this menu's own pages.
            MenuState.CloseConfig();
            MenuState.OpenConfig(entry, MaxConfigEditorBytes);
            _liveControlsFromFrontEnd = true;
            try
            {
                if (settings)
                {
                    OpenLiveSettings();
                }
                else
                {
                    OpenModActions();
                }

                return _liveControlsSession != null;
            }
            finally
            {
                _liveControlsFromFrontEnd = false;
                MenuState.CloseConfig();
            }
        }

        /// <summary>
        /// Hides the menu for a screen host opened from one of its pages; false when it is not showing or live controls
        /// own it. <paramref name="resumePage"/> is the page to hand back to <see cref="ResumeAfterScreen"/>.
        /// </summary>
        internal static bool HandOffToScreen(out string resumePage)
        {
            resumePage = null;
            if (!MenuDispatcher.IsVisible(_visible) || _liveControlsSession != null || _openingLiveControls)
            {
                return false;
            }

            // The menu keeps its input, so the screen gives up waiting rather than dropping the edits.
            if (MenuState.IsConfigDirty)
            {
                MenuState.SetConfigStatus("Save or revert unsaved file edits before opening a screen.");
                return false;
            }

            resumePage = MenuState.ViewState.LastPage;
            _liveControlsFromNative = MenuRenderer.IsVisible;
            _liveControlsSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            _resumeAfterLiveControls = false;
            Hide();
            return true;
        }

        /// <summary>Shows the menu again on the next frame at the page a screen host took it from, in the same scene only.</summary>
        internal static void ResumeAfterScreen(string resumePage)
        {
            if (_liveControlsSession != null || MenuDispatcher.IsVisible(_visible))
            {
                return;
            }

            _resumePage = resumePage;
            _resumeAfterLiveControls = true;
            _liveControlsClosedFrame = Time.frameCount;
        }

        internal static void OpenLiveSettings()
        {
            OpenLiveControls("Mod Settings", (entry, onClosed) => SonsGameManager.ModSettingsUi.TryOpen(entry.Id, onClosed),
                SonsGameManager.ModSettingsUi.Close);
        }

        internal static void OpenModActions()
        {
            OpenLiveControls("Mod Actions", ModActions.TryOpen, ModActions.Close);
        }

        private static void OpenLiveControls(string title, Func<ModInventoryEntry, Action<bool>, bool> tryOpen, Action close)
        {
            var entry = MenuState.ConfigEntry;
            if (entry == null || _liveControlsSession != null || _openingLiveControls)
            {
                return;
            }

            if (MenuState.IsConfigDirty)
            {
                MenuState.SetConfigStatus("Save or revert unsaved file edits before opening live controls.");
                return;
            }

            var session = new object();
            var opened = false;
            var fromFrontEnd = _liveControlsFromFrontEnd;
            _openingLiveControls = true;
            _liveControlsSession = session;
            _closeLiveControls = close;
            _resumeAfterLiveControls = false;
            _resumePage = null;
            try
            {
                var friendlyMode = MenuState.FriendlyMode;
                _liveControlsFromNative = MenuRenderer.IsVisible;
                _liveControlsSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if (!tryOpen(entry, returnToOwner =>
                {
                    if (!ReferenceEquals(_liveControlsSession, session))
                    {
                        return;
                    }

                    _liveControlsSession = null;
                    _closeLiveControls = null;
                    // The Mods screen never left, so nothing has to be shown again for it.
                    _resumeAfterLiveControls = returnToOwner && opened && !fromFrontEnd;
                    _liveControlsClosedFrame = Time.frameCount;
                    UiState.MarkHidden(UiSurfaceIds.ModConfig, title, "sonssdk", "closed", title + " are closed.");
                    if (_resumeAfterLiveControls)
                    {
                        MenuState.OpenConfig(entry, MaxConfigEditorBytes);
                        MenuState.SetFriendlyMode(friendlyMode);
                    }
                }))
                {
                    _liveControlsSession = null;
                    _closeLiveControls = null;
                    MenuState.SetConfigStatus(title + " are not available for this mod.");
                    return;
                }

                if (!ReferenceEquals(_liveControlsSession, session))
                {
                    return;
                }

                if (!fromFrontEnd)
                {
                    Hide();
                }

                UiState.MarkVisible(UiSurfaceIds.ModConfig, title, "sonssdk", "live:" + entry.Id, title + " are visible.");
                opened = true;
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(_liveControlsSession, session))
                {
                    _liveControlsSession = null;
                    _closeLiveControls = null;
                    try
                    {
                        close();
                    }
                    catch
                    {
                        // Preserve the opening failure if panel cleanup also fails.
                    }
                }

                MenuState.SetConfigStatus("Could not open " + title + ": " + exception.Message);
                RuntimeEntryPoint.LogSource?.LogWarning("Could not open " + title + ": " + exception);
            }
            finally
            {
                _openingLiveControls = false;
            }
        }
    }
}
