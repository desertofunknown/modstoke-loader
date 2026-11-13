// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Sons.Gui.Multiplayer;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// Replaces the game's multiplayer entry with Modstoke's screens. The title's Multiplayer button goes straight
    /// to joining (the game's Join path, which loads its browser scene), and the browser scene gets the Modstoke
    /// screen over the game's own browsers, which keep doing the work. Hosting still uses the game's flow.
    /// Setting classicMultiplayerMenu=true in the loader settings keeps the game's menus.
    /// </summary>
    internal static class MultiplayerTakeover
    {
        private const string TitleScene = "SonsTitleScene";
        private const string BrowserScene = "MultiplayerMenu";
        private const string ClassicKey = "classicMultiplayerMenu";
        private static int _titleBound, _browserBound;
        private static bool _hostRequested, _classicThisVisit;
        private static Button _multiplayerButton;

        internal static bool Enabled => !RuntimePreferences.ReadBoolean(ClassicKey, false);

        internal static void Tick()
        {
            if (GameBuild.IsDedicatedServer) return;
            try
            {
                var title = SceneManager.GetSceneByName(TitleScene);
                if (title.IsValid() && title.isLoaded)
                {
                    if (title.handle != _titleBound) BindTitle(title);
                    TickHostRequest(title);
                }
                var browser = SceneManager.GetSceneByName(BrowserScene);
                if (browser.IsValid() && browser.isLoaded)
                {
                    if (browser.handle != _browserBound)
                    {
                        _browserBound = browser.handle;
                        if (Enabled && !_classicThisVisit) MultiplayerScreen.Open(browser);
                    }
                    MultiplayerScreen.Tick();
                }
                else if (_browserBound != 0)
                {
                    _browserBound = 0;
                    _classicThisVisit = false;
                    MultiplayerScreen.Close();
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The Modstoke multiplayer screen stopped: " + error.Message);
                MultiplayerScreen.Close();
                _browserBound = -1;
            }
        }

        /// <summary>Shows the game's own browser for the rest of this visit.</summary>
        internal static void UseClassicBrowser()
        {
            _classicThisVisit = true;
            MultiplayerScreen.Close();
        }

        /// <summary>Leaves the browser for the title screen, which then opens the game's Host choices.</summary>
        internal static void RequestHost(Button back)
        {
            _hostRequested = true;
            back.onClick.Invoke();
        }

        private static void BindTitle(Scene title)
        {
            _multiplayerButton = Find(title, "MultiplayerButton");
            if (_multiplayerButton == null) return;
            _titleBound = title.handle;
            _multiplayerButton.onClick.AddListener((UnityAction)AfterMultiplayerPressed);
            // The title's menus settle their online state over the first frames.
            if (_hostRequested) _hostAt = Time.realtimeSinceStartup + HostDelaySeconds;
        }

        private const float HostDelaySeconds = 0.6f;
        private static float _hostAt;
        private static bool _openingHost;

        private static void TickHostRequest(Scene title)
        {
            if (!_hostRequested || _multiplayerButton == null || Time.realtimeSinceStartup < _hostAt) return;
            _hostRequested = false;
            _openingHost = true;
            try
            {
                // The game's own listener opens its Join/Host choice; Host is pressed for the player.
                _multiplayerButton.onClick.Invoke();
                Press(title, "MultiPlayerModeDialog", "HostButton");
            }
            finally
            {
                _openingHost = false;
            }
        }

        // Runs after the game's own listener has opened its Join/Host choice.
        private static void AfterMultiplayerPressed()
        {
            if (!Enabled || _openingHost) return;
            var title = SceneManager.GetSceneByName(TitleScene);
            if (!Press(title, "MultiPlayerModeDialog", "JoinButton"))
                RuntimeEntryPoint.LogSource?.LogInfo("Multiplayer opened the game's own menu: its Join choice is not available.");
        }

        private static bool Press(Scene scene, string dialogName, string buttonName)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != dialogName) continue;
                foreach (var button in root.GetComponentsInChildren<Button>(true))
                {
                    // Offline, the game makes Join unavailable and its own choice stays open.
                    if (button == null || button.name != buttonName || !button.interactable) continue;
                    button.onClick.Invoke();
                    return true;
                }
            }
            return false;
        }

        private static Button Find(Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var button in root.GetComponentsInChildren<Button>(true))
                    if (button != null && button.name == name) return button;
            return null;
        }
    }
}
