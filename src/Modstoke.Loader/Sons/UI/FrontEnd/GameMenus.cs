// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sons.Gameplay.GameSetup;
using Sons.Gui;
using Sons.Gui.Options;
using Sons.Multiplayer;
using Sons.Save;
using Steam.VirtualKeyboard;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>The game's pages that open on Modstoke's own screens instead.</summary>
    internal enum GamePage { None, CustomGame, HostOptions }

    /// <summary>
    /// The game's own title-scene menus, used as a command layer. The front end hides the game's button bar and
    /// draws its own screens; starting, continuing, joining, options and exit still run what the game wired to
    /// its own buttons, so games start exactly as they do in the game. When the game opens its custom game page
    /// or its host options, the page is closed and Modstoke's screen takes its place, finishing through the
    /// page's own handlers.
    /// </summary>
    internal static class GameMenus
    {
        private const string TitleScene = "SonsTitleScene";
        private const string CustomPage = "CustomGameSettings", HostPage = "MultiplayerHostOptionsDialog";
        private static readonly string[] OverTitle = { "SonsOptionsMenu", "SonsMainLoading", "MultiplayerMenu", "MultiplayerHostMenu" };
        private static readonly string[] KeptVisible = { "MenuBottomStripWithLogo", "GameSetupManageSceneProxy" };
        private const float LogoFadeSeconds = 0.18f;
        private static int _bound;
        private static CanvasGroup _bar, _logoGroup;
        private static RectTransform _barRect, _logo;
        private static GameObject _strip;
        private static readonly List<GameObject> Dialogs = new();

        /// <summary>Finds the title scene's menus once per load; true while the title scene is loaded.</summary>
        internal static bool Bind()
        {
            var scene = SceneManager.GetSceneByName(TitleScene);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                if (_bound != 0) Forget();
                return false;
            }
            if (scene.handle == _bound) return true;
            Forget();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "BottomCanvas")
                {
                    var panel = root.transform.Find("BottomMenuPanel");
                    if (panel == null) continue;
                    _bar = Group(panel);
                    _barRect = panel.GetComponent<RectTransform>();
                }
                else if (root.name == "TitleMenu")
                {
                    for (var i = 0; i < root.transform.childCount; i++)
                    {
                        var child = root.transform.GetChild(i).gameObject;
                        if (!KeptVisible.Contains(child.name)) Dialogs.Add(child);
                    }
                    var strip = root.transform.Find("MenuBottomStripWithLogo");
                    if (strip != null) _strip = strip.gameObject;
                    var logo = root.transform.Find("MenuBottomStripWithLogo/GameLogo");
                    if (logo != null) _logo = logo.GetComponent<RectTransform>();
                }
            }
            if (_bar == null) return false;
            _bound = scene.handle;
            return true;
        }

        /// <summary>Whether one of the game's own title pages is open.</summary>
        internal static bool DialogOpen
        {
            get
            {
                foreach (var dialog in Dialogs)
                    if (dialog != null && dialog && dialog.activeInHierarchy) return true;
                return false;
            }
        }

        /// <summary>Whether another of the game's scenes (options, loading, multiplayer) is up or arriving over the title.</summary>
        internal static bool OtherSceneOpen
        {
            get
            {
                foreach (var name in OverTitle)
                    if (SceneManager.GetSceneByName(name).IsValid()) return true;
                return false;
            }
        }

        /// <summary>
        /// The game opens its dialogs on its way through a start, as pressing its mode buttons does; every one is
        /// a menu Modstoke replaced, so it is closed and the player stays on the Modstoke screen.
        /// </summary>
        internal static void CloseReplaced()
        {
            foreach (var dialog in Dialogs)
                if (dialog != null && dialog && dialog.activeSelf)
                    dialog.SetActive(false);
        }

        /// <summary>
        /// The custom game page or the host options, if the game just opened one. It is closed, and the caller
        /// opens Modstoke's own screen for it; the page's handlers stay wired for that screen to finish through.
        /// </summary>
        internal static GamePage TakePage()
        {
            foreach (var dialog in Dialogs)
            {
                if (dialog == null || !dialog || !dialog.activeSelf) continue;
                var page = dialog.name switch
                {
                    CustomPage => GamePage.CustomGame,
                    HostPage => GamePage.HostOptions,
                    _ => GamePage.None
                };
                if (page == GamePage.None) continue;
                dialog.SetActive(false);
                return page;
            }
            return GamePage.None;
        }

        /// <summary>The kind of game being set up, as the game's setup has it.</summary>
        internal static SaveGameType SetupType(SaveGameType fallback)
        {
            try { return GameSetupManager.GetSaveGameType(); }
            catch (Exception) { return fallback; }
        }

        /// <summary>
        /// Starts the custom game through the custom page's own start handler: a single player game loads, and a
        /// hosted one opens the host options.
        /// </summary>
        internal static void StartCustomGame()
        {
            var page = Page(CustomPage) ?? throw new InvalidOperationException("The game's custom game page was not found.");
            var manager = page.GetComponentInChildren<CustomGameSetupGuiManager>(true)
                ?? throw new InvalidOperationException("The game's custom game handler was not found.");
            // The page wires its handlers while it is open, so it opens for the moment its start takes.
            page.SetActive(true);
            try
            {
                manager.OnStartGame();
            }
            finally
            {
                if (page.activeSelf) page.SetActive(false);
            }
        }

        /// <summary>
        /// Starts hosting with the game's own start button. Opening the page fills its fields from the game's
        /// settings, where the name, player limit and visibility already are.
        /// </summary>
        internal static void StartHosting(string name)
        {
            var page = Page(HostPage) ?? throw new InvalidOperationException("The game's host options were not found.");
            MultiplayerUtilities.SetServerName(name);
            page.SetActive(true);
            try
            {
                var helper = page.GetComponentInChildren<MultiplayerServerNameHelper>(true);
                if (helper != null) helper.ApplyServerNameToGui(name);
                Press(HostPage, "StartHostButton");
            }
            finally
            {
                if (page.activeSelf) page.SetActive(false);
            }
        }

        /// <summary>
        /// Opens Steam's on-screen keyboard for the server name the way the game's own name field does, and
        /// returns that field, which receives the text; null when the game's field is not there.
        /// </summary>
        internal static TMP_InputField NameKeyboard(string current)
        {
            var keyboard = Page(HostPage)?.GetComponentInChildren<VirtualKeyboardField>(true);
            var field = keyboard != null ? keyboard._field : null;
            if (field == null) return null;
            field.SetTextWithoutNotify(current ?? "");
            keyboard.TryShowVirtualKeyboard();
            return field;
        }

        /// <summary>Hides the game's button bar while the front end shows its own, or gives it back.</summary>
        internal static void HideBar(bool hidden)
        {
            if (_bar == null || !_bar) return;
            var alpha = hidden ? 0f : 1f;
            if (_bar.alpha == alpha && _bar.interactable == !hidden) return;
            _bar.alpha = alpha;
            _bar.interactable = !hidden;
            _bar.blocksRaycasts = !hidden;
        }

        /// <summary>
        /// Keeps the game's bottom strip up under Modstoke's screens. Its custom game page hides the strip on
        /// the way in and only its own Back brings it back, which a page Modstoke closed never presses.
        /// </summary>
        internal static void KeepStrip()
        {
            if (_strip != null && _strip && !_strip.activeSelf) _strip.SetActive(true);
        }

        /// <summary>Fades the game's logo in for the title row and out under every other screen.</summary>
        internal static void FadeLogo(bool shown, float seconds)
        {
            if (_logo == null || !_logo) return;
            if (_logoGroup == null || !_logoGroup) _logoGroup = Group(_logo);
            var target = shown ? 1f : 0f;
            if (_logoGroup.alpha != target) _logoGroup.alpha = Mathf.MoveTowards(_logoGroup.alpha, target, seconds / LogoFadeSeconds);
        }

        /// <summary>
        /// Where the game's own row of title choices starts and the line it sits on, in screen pixels, so
        /// Modstoke's row can take its place exactly.
        /// </summary>
        internal static bool TryBar(out float left, out float middle) => TrySpan(_barRect, out left, out _, out middle);

        /// <summary>The left and right edges of the game's logo on its bottom strip, in screen pixels.</summary>
        internal static bool TryLogo(out float left, out float right) => TrySpan(_logo, out left, out right, out _);

        private static bool TrySpan(RectTransform rect, out float left, out float right, out float middle)
        {
            left = right = middle = 0;
            if (rect == null || !rect || !rect.gameObject.activeInHierarchy) return false;
            try
            {
                var canvas = rect.GetComponentInParent<Canvas>();
                var root = canvas != null && canvas ? canvas.rootCanvas : null;
                var camera = root != null && root && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
                var corners = new Il2CppStructArray<Vector3>(4);
                rect.GetWorldCorners(corners);
                var low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
                var high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                left = low.x;
                right = high.x;
                middle = (low.y + high.y) / 2f;
                // Before the game has laid its menu out, these are all zero.
                return right - left > 1f && left >= 0 && right <= Screen.width + 1f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The game's event system must not keep one of its hidden buttons selected.</summary>
        internal static void ClearSelection()
        {
            var system = EventSystem.current;
            if (system == null || !system) return;
            var selected = system.currentSelectedGameObject;
            if (selected != null && selected) system.SetSelectedGameObject(null);
        }

        /// <summary>
        /// Starts a new game the way the game's own New Game path does: the kind of save, a fresh slot, then
        /// its button for the mode. Custom opens the game's settings page, and hosting its host options.
        /// </summary>
        internal static void NewGame(SaveGameType type, string mode)
        {
            var single = type == SaveGameType.SinglePlayer;
            ApplyType(type);
            var newGame = Button(single ? "SinglePlayerTypeDialog" : "MultiPlayerTypeDialog", "NewGameButton");
            var slot = newGame != null ? newGame.GetComponent<SaveGameIdSetter>() : null;
            if (slot == null) throw new InvalidOperationException("The game's new game slot was not found.");
            slot.SetAndApply(0);
            Press(single ? "SinglePlayerOptionsDialog" : "MultiPlayerOptionsDialog", mode + "Button");
        }

        /// <summary>
        /// The modes the game itself offers for this kind of game, out of <paramref name="modes"/>. Its mode
        /// list hides Creative until the story has been finished, and Modstoke's list follows that.
        /// </summary>
        internal static string[] OfferedModes(SaveGameType type, string[] modes)
        {
            var dialog = type == SaveGameType.SinglePlayer ? "SinglePlayerOptionsDialog" : "MultiPlayerOptionsDialog";
            try
            {
                // The game decides what its list shows when the list opens; it is asked to decide now.
                Find<EndGameModesUiEnabler>(dialog)?.ApplyState();
            }
            catch (Exception)
            {
                // The buttons then stand as the game last left them.
            }
            return Array.FindAll(modes, mode =>
            {
                var button = Button(dialog, mode + "Button");
                return button != null && button.gameObject.activeSelf && button.interactable;
            });
        }

        /// <summary>Continues a save through the game's load menu, which loads it or opens its host options.</summary>
        internal static void ContinueGame(SaveGameType type, uint saveId)
        {
            var id = (int)saveId;
            ApplyType(type);
            var menu = Find<LoadMenu>(type == SaveGameType.SinglePlayer ? "SinglePlayerLoadPanel" : "MultiPlayerLoadPanel")
                ?? throw new InvalidOperationException("The game's load menu was not found.");
            menu.LoadSlotActivated(id, SaveGameManager.GetData<GameStateData>(type, id));
        }

        /// <summary>Loads the game's server browser scene, where the Modstoke Join screen opens.</summary>
        internal static void Join() => Press("MultiPlayerModeDialog", "JoinButton");

        internal static void Options() => PressBar("SettingsButton");

        internal static List<uint> SaveIds(SaveGameType type)
        {
            var ids = new List<uint>();
            if (!SaveGameManager.TryGetSaveGameIds(type, out var found) || found == null) return ids;
            for (var i = 0; i < found.Count; i++) ids.Add(found[i]);
            return ids;
        }

        // The game's buttons carry a setter for the kind of save their path uses; the same one is applied here.
        private static void ApplyType(SaveGameType type)
        {
            foreach (var dialog in Dialogs)
            {
                if (dialog == null || !dialog) continue;
                foreach (var setter in dialog.GetComponentsInChildren<SaveGameTypeSetter>(true))
                {
                    if (setter == null || setter._saveGameType != type) continue;
                    setter.Apply();
                    return;
                }
            }
            throw new InvalidOperationException("The game's " + type + " save setter was not found.");
        }

        private static void Forget()
        {
            _bound = 0;
            _bar = _logoGroup = null;
            _barRect = _logo = null;
            _strip = null;
            Dialogs.Clear();
        }

        private static CanvasGroup Group(Transform transform)
        {
            var group = transform.GetComponent<CanvasGroup>();
            return group != null && group ? group : transform.gameObject.AddComponent<CanvasGroup>();
        }

        private static void PressBar(string name)
        {
            var button = _bar != null && _bar
                ? _bar.GetComponentsInChildren<Button>(true).FirstOrDefault(candidate => candidate != null && candidate.name == name)
                : null;
            if (button == null) throw new InvalidOperationException("The game's " + name + " was not found.");
            button.onClick.Invoke();
        }

        private static void Press(string dialog, string name)
        {
            var button = Button(dialog, name)
                ?? throw new InvalidOperationException("The game's " + dialog + "/" + name + " was not found.");
            button.onClick.Invoke();
        }

        private static Button Button(string dialog, string name)
        {
            var owner = Dialogs.FirstOrDefault(candidate => candidate != null && candidate && candidate.name == dialog);
            return owner == null
                ? null
                : owner.GetComponentsInChildren<Button>(true).FirstOrDefault(candidate => candidate != null && candidate.name == name);
        }

        private static T Find<T>(string dialog) where T : Component
        {
            var owner = Page(dialog);
            return owner == null ? null : owner.GetComponent<T>();
        }

        private static GameObject Page(string name) => Dialogs.FirstOrDefault(candidate => candidate != null && candidate && candidate.name == name);
    }
}
