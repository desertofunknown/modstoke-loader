// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
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
        private const string ChoiceDialog = "MultiPlayerModeDialog";
        private const string ClassicKey = "classicMultiplayerMenu";
        private const float HostDelaySeconds = 0.6f;
        private static int _titleBound, _browserBound;
        private static bool _hostRequested, _wantHost, _classicThisVisit;
        private static Button _multiplayerButton;
        private static CanvasGroup _choiceVeil;
        private static string _pendingChoice;
        private static float _pendingUntil, _veilUntil, _hostAt;

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
                    TickHostRequest();
                    TickChoice(title);
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
                // The screen stays closed for this visit; the game's own browser shows instead.
                RuntimeEntryPoint.LogSource?.LogWarning("The Modstoke multiplayer screen stopped: " + error);
                MultiplayerScreen.Close();
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
            _multiplayerButton = null;
            _choiceVeil = null;
            _pendingChoice = null;
            foreach (var root in title.GetRootGameObjects())
            {
                foreach (var button in root.GetComponentsInChildren<Button>(true))
                    if (button != null && button.name == "MultiplayerButton") _multiplayerButton = button;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (transform == null || transform.name != ChoiceDialog) continue;
                    _choiceVeil = transform.GetComponent<CanvasGroup>();
                    if (_choiceVeil == null || !_choiceVeil) _choiceVeil = transform.gameObject.AddComponent<CanvasGroup>();
                }
            }
            if (_multiplayerButton == null) return;
            _titleBound = title.handle;
            // Runtime listeners run after the game's own, which opens its Join/Host choice.
            _multiplayerButton.onClick.AddListener((UnityAction)AfterMultiplayerPressed);
            // The title's menus settle their online state over the first frames.
            if (_hostRequested) _hostAt = Time.realtimeSinceStartup + HostDelaySeconds;
        }

        private static void TickHostRequest()
        {
            if (!_hostRequested || _multiplayerButton == null || !_multiplayerButton || Time.realtimeSinceStartup < _hostAt) return;
            _hostRequested = false;
            _wantHost = true;
            _multiplayerButton.onClick.Invoke();
            _wantHost = false;
        }

        private static void AfterMultiplayerPressed()
        {
            try
            {
                if (!_wantHost && !Enabled) return;
                // The game's choice is answered for the player, so it stays out of sight while that happens.
                _pendingChoice = _wantHost ? "HostButton" : "JoinButton";
                _pendingUntil = Time.realtimeSinceStartup + (_wantHost ? 2f : 0.75f);
                _veilUntil = _pendingUntil;
                if (_choiceVeil != null && _choiceVeil) _choiceVeil.alpha = 0;
                TickChoice(SceneManager.GetSceneByName(TitleScene));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Multiplayer opened the game's own menu: " + error.Message);
                _pendingChoice = null;
                Unveil();
            }
        }

        // The game's Join can be unavailable for a few frames while it checks Steam, so the press is retried briefly.
        private static void TickChoice(Scene title)
        {
            var now = Time.realtimeSinceStartup;
            if (_pendingChoice != null)
            {
                if (Press(title, _pendingChoice))
                {
                    // Join loads the browser scene, so the choice stays hidden until this scene goes.
                    _veilUntil = _pendingChoice == "JoinButton" ? now + 6f : now;
                    _pendingChoice = null;
                }
                else if (now >= _pendingUntil)
                {
                    RuntimeEntryPoint.LogSource?.LogInfo("Multiplayer opened the game's own menu: its "
                        + (_pendingChoice == "JoinButton" ? "Join" : "Host") + " choice is not available.");
                    _pendingChoice = null;
                    _veilUntil = now;
                }
            }
            if (_pendingChoice == null && now >= _veilUntil) Unveil();
        }

        private static void Unveil()
        {
            if (_choiceVeil != null && _choiceVeil && _choiceVeil.alpha < 1) _choiceVeil.alpha = 1;
        }

        private static bool Press(Scene scene, string buttonName)
        {
            if (!scene.IsValid() || !scene.isLoaded) return false;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var button in root.GetComponentsInChildren<Button>(true))
                {
                    // Offline, the game makes Join unavailable and its own choice stays open.
                    if (button == null || button.name != buttonName || !button.interactable
                        || !button.gameObject.activeInHierarchy || !Under(button.transform, ChoiceDialog)) continue;
                    button.onClick.Invoke();
                    return true;
                }
            return false;
        }

        private static bool Under(Transform transform, string ancestor)
        {
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                if (parent.name == ancestor) return true;
            return false;
        }
    }
}
