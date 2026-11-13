// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine.SceneManagement;

namespace Modstoke.Loader
{
    /// <summary>
    /// Shows the Modstoke Join screen whenever the game's server browser scene is loaded, in place of the
    /// game's own browser, which keeps running underneath as the source of servers. With the game's own menus
    /// chosen in the Loader settings (classicMenus=true), its browser is left as it is.
    /// </summary>
    internal static class MultiplayerTakeover
    {
        private const string BrowserScene = "MultiplayerMenu";
        private static int _bound;
        private static bool _classicThisVisit;

        internal static void Tick()
        {
            if (GameBuild.IsDedicatedServer) return;
            try
            {
                var browser = SceneManager.GetSceneByName(BrowserScene);
                if (browser.IsValid() && browser.isLoaded)
                {
                    if (browser.handle != _bound)
                    {
                        _bound = browser.handle;
                        if (FrontEnd.Enabled && !_classicThisVisit) MultiplayerScreen.Open(browser);
                    }
                    MultiplayerScreen.Tick();
                }
                else if (_bound != 0)
                {
                    _bound = 0;
                    _classicThisVisit = false;
                    MultiplayerScreen.Close();
                }
            }
            catch (Exception error)
            {
                // The screen stays closed for this visit; the game's own browser shows instead.
                RuntimeEntryPoint.LogSource?.LogWarning("The Modstoke multiplayer screen stopped: " + error);
                MultiplayerScreen.Close();
            }
        }

        /// <summary>Shows the game's own browser for the rest of this visit.</summary>
        internal static void UseClassicBrowser()
        {
            _classicThisVisit = true;
            MultiplayerScreen.RestoreGameBrowser();
            MultiplayerScreen.Close();
        }
    }
}
