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
    /// the cursor, the hidden HUD, its music and multiplayer behaviour stay its own; only its button bar is put
    /// away, as the game itself does for its sub-pages, while Modstoke's pause screen shows. The game's own
    /// pages that Modstoke has no screen for yet (players, the shelter save) still show as they are.
    /// </summary>
    internal static class PauseHost
    {
        private static readonly List<GameObject> PutAway = new();
        private static PauseMenu _menu;
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

        /// <summary>Puts the game's button bar away while Modstoke's pause screen shows.</summary>
        internal static void HideNative()
        {
            var menu = Menu;
            if (menu == null || PutAway.Count > 0) return;
            var targets = menu._deactivateForOptions;
            if (targets == null) return;
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || !target || !target.activeSelf) continue;
                target.SetActive(false);
                PutAway.Add(target);
            }
        }

        /// <summary>
        /// Brings the game's button bar back while its menu is still on its first page; a page of its own that opened
        /// has put the bar away itself, and brings it back when it closes.
        /// </summary>
        internal static void Release()
        {
            var open = Open;
            foreach (var target in PutAway)
                if (open && target != null && target) target.SetActive(true);
            PutAway.Clear();
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

        internal static void Players() => Menu?.SetPlayersActive(true);

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

        internal static void Forget()
        {
            _menu = null;
            _escapeHeld = false;
            PutAway.Clear();
        }
    }
}
