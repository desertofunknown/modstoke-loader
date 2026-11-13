// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Gui;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// The game's pause menu, as the source for Modstoke's. The game still opens and closes it, so pausing,
    /// the cursor, the hidden HUD, its music and multiplayer behaviour stay its own; only its button bar is made
    /// invisible, from the moment the menu is created, while Modstoke's pause screen shows in its place. The
    /// game's own pages that Modstoke has no screen for (the shelter save) still show as they are.
    /// </summary>
    internal static class PauseHost
    {
        private static readonly List<CanvasGroup> Hidden = new();
        private static PauseMenu _menu;
        private static int _hiddenFor;
        private static bool _escapeHeld;

        private static PauseMenu Menu
        {
            get
            {
                if (_menu != null && _menu) return _menu;
                try { _menu = PauseMenu._instance; }
                catch (Exception) { _menu = null; }
                return _menu != null && _menu ? _menu : null;
            }
        }

        /// <summary>Whether the pause menu is open on its own first page, the one Modstoke's screen replaces.</summary>
        internal static bool Open
        {
            get
            {
                var menu = Menu;
                if (menu == null) return false;
                try
                {
                    return menu._isActive && !menu._saveMenuActive && !menu._loadMenuActive && !menu._playersMenuActive
                        && !menu._optionsMenuActive && !menu._quitting;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        internal static bool Multiplayer
        {
            get
            {
                try { return BoltNetwork.isRunning; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>The game's pause menu was just created; its button bar is made invisible before it ever opens.</summary>
        internal static void Adopt(PauseMenu menu)
        {
            if (menu == null || !menu) return;
            _menu = menu;
            HideNative();
        }

        /// <summary>
        /// Makes the game's button bar invisible and unclickable, for as long as Modstoke's menus are on. The game
        /// still switches it on and off with its own pages; it simply never shows.
        /// </summary>
        internal static void HideNative()
        {
            var menu = Menu;
            if (menu == null || _hiddenFor == menu.GetInstanceID()) return;
            var targets = menu._deactivateForOptions;
            if (targets == null) return;
            _hiddenFor = menu.GetInstanceID();
            Hidden.Clear();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || !target) continue;
                var group = target.GetComponent<CanvasGroup>();
                if (group == null || !group) group = target.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.interactable = false;
                group.blocksRaycasts = false;
                Hidden.Add(group);
            }
        }

        /// <summary>Shows the game's button bar again, as when Modstoke's menus stop.</summary>
        internal static void Release()
        {
            foreach (var group in Hidden)
            {
                if (group == null || !group) continue;
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }
            Hidden.Clear();
            _hiddenFor = 0;
        }

        /// <summary>
        /// Escape and the controller's Back close the game's whole menu. While one of Modstoke's pages is open over
        /// the pause screen they step back to it instead, so the game's handling is held for that time.
        /// </summary>
        internal static void HoldEscape(bool held)
        {
            if (held == _escapeHeld) return;
            var menu = Menu;
            if (menu == null) return;
            try
            {
                menu.ManageInputEvents(!held);
                _escapeHeld = held;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The pause menu's Back keys could not be held: " + error.Message);
            }
        }

        internal static void Resume()
        {
            HoldEscape(false);
            Menu?.Close();
        }

        /// <summary>The game's own options page, for the key bindings Modstoke has no screen for yet.</summary>
        internal static void Options()
        {
            HoldEscape(false);
            Menu?.SetOptionsActive(true);
        }

        /// <summary>Leaves the game for the title through the game's own path, with its quitting callbacks.</summary>
        internal static void QuitToTitle()
        {
            HoldEscape(false);
            PauseMenu.QuitToTitleSkipConfirm();
        }

    }
}
