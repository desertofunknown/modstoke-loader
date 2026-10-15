// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using RedLoader;
using Sons.Gui;
using Sons.Loading;
using SonsSdk;
using SUI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Sui = SUI.SUI;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsUiMenuButtons : ISdkMenuButtonDriver
    {
        internal static readonly OpenACAISonsUiMenuButtons Instance = new();

        private OpenACAISonsUiMenuButtons() { }

        public void Register(Func<SUiElement> generator, string id, ESonsScene scene, int index)
        {
            ArgumentNullException.ThrowIfNull(generator);
            ArgumentNullException.ThrowIfNull(id);
            if (Sui._registeredMenuButtons.Any(button => button.Scene == scene && button.Id == id))
                return;

            var button = new Sui.MenuButtonRegistration(generator, id, scene, index);
            // Publish before invoking a factory so reentrant registration sees this ID.
            Sui._registeredMenuButtons.Add(button);

            try
            {
                if (scene == ESonsScene.Title && SceneManager.GetActiveScene().name == SonsSceneManager.TitleSceneName)
                    AddToTitleMenu(button);
                else if (scene == ESonsScene.Game && Sui._pauseMenu)
                    AddToPauseMenu(Sui._pauseMenu, Sui._pauseMenu.transform.Find("Panel/BottomMenuPanel/LeftPanel"), button);
            }
            catch
            {
                Sui._registeredMenuButtons.Remove(button);
                throw;
            }
        }

        public void OnSceneInitialized(ESonsScene scene)
        {
            Sui._pauseMenu = null;
            if (scene == ESonsScene.Title)
            {
                Sui._titleMenuButtonsContainer = Resources.FindObjectsOfTypeAll<Button>()
                    .First(button => button.name == "SinglePlayerButton" && button.gameObject.activeSelf)
                    .transform.parent;
            }

            foreach (var button in Sui._registeredMenuButtons.ToArray())
            {
                if (scene == ESonsScene.Title && button.Scene == ESonsScene.Title)
                    AddToTitleMenu(button);
            }
        }

        public void OnPauseMenuCreated(PauseMenu menu)
        {
            var parent = menu.transform.Find("Panel/BottomMenuPanel/LeftPanel");
            if (!parent)
                throw new InvalidOperationException("Pause menu button container was not found.");

            Sui._pauseMenu = menu;

            foreach (var button in Sui._registeredMenuButtons.ToArray())
            {
                if (button.Scene == ESonsScene.Game)
                    AddToPauseMenu(menu, parent, button);
            }
        }

        private static void AddToTitleMenu(Sui.MenuButtonRegistration button)
        {
            if (Sui._titleMenuButtonsContainer.Find(button.Id) != null)
                return;

            var element = button.ElementFactory();
            element.Root.name = button.Id;
            element.SetParent(Sui._titleMenuButtonsContainer);
            RLog.Debug("Added button to title menu");
            if (button.Index != -1)
                element.Root.transform.SetSiblingIndex(button.Index);
        }

        private static void AddToPauseMenu(PauseMenu menu, Transform parent, Sui.MenuButtonRegistration button)
        {
            if (parent.GetChildren().Any(child => child.name == button.Id))
                return;

            var element = button.ElementFactory();
            element.Root.name = button.Id;
            element.SetParent(parent);
            if (button.Index != -1)
                element.RectTransform.SetSiblingIndex(button.Index);

            menu._activateForMenu.Add(element.Root);
        }
    }
}
