// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Gui;
using SonsSdk;
using SUI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Sui = SUI.SUI;

namespace Modstoke.Loader
{
    internal sealed class UiMenuButtons : ISdkMenuButtonDriver
    {
        internal static readonly UiMenuButtons Instance = new();
        private static readonly HashSet<(ESonsScene Scene, string Id)> PendingRegistrations = new();

        private UiMenuButtons() { }

        public void Register(Func<SUiElement> generator, string id, ESonsScene scene, int index)
        {
            ArgumentNullException.ThrowIfNull(generator);
            ArgumentNullException.ThrowIfNull(id);
            if (Sui._registeredMenuButtons.Any(button => button.Scene == scene && button.Id == id))
                return;

            var registration = new Sui.MenuButtonRegistration(generator, id, scene, index);
            // Publish before invoking a factory so reentrant registration sees this ID.
            Sui._registeredMenuButtons.Add(registration);
            try
            {
                if (scene == ESonsScene.Title && Sui._titleMenuButtonsContainer != null)
                    AddRegistration(registration, Sui._titleMenuButtonsContainer, null);
                else if (scene == ESonsScene.Game && Sui._pauseMenu != null)
                    AddRegistration(registration, Sui._pauseMenu.transform.Find(GameMenuBridge.PauseButtonPath), Sui._pauseMenu);
            }
            catch
            {
                Sui._registeredMenuButtons.Remove(registration);
                throw;
            }
        }

        public void OnSceneInitialized(ESonsScene scene)
        {
            if (scene == ESonsScene.Title)
                GameMenuBridge.BindTitleScene(SceneManager.GetSceneByName(GameMenuBridge.TitleSceneName));
        }

        public void OnPauseMenuCreated(PauseMenu menu)
        {
            GameMenuBridge.OnPauseMenuAvailable(menu);
        }

        internal static void BindTitle(Transform parent)
        {
            Sui._titleMenuButtonsContainer = parent;
            AddRegistrations(ESonsScene.Title, parent, null);
        }

        internal static void BindPause(PauseMenu menu, Transform parent)
        {
            Sui._pauseMenu = menu;
            AddRegistrations(ESonsScene.Game, parent, menu);
        }

        /// <summary>
        /// The buttons mods registered for the title or pause menu, as they were attached to the game's bar, for
        /// Modstoke's own menus to list in its place: each one's text and its press.
        /// </summary>
        internal static List<(string Label, Action Press)> Registered(ESonsScene scene)
        {
            var entries = new List<(string, Action)>();
            Transform container = null;
            if (scene == ESonsScene.Title) container = Sui._titleMenuButtonsContainer;
            else if (scene == ESonsScene.Game && Sui._pauseMenu != null && Sui._pauseMenu)
                container = Sui._pauseMenu.transform.Find(GameMenuBridge.PauseButtonPath);
            if (container == null || !container) return entries;
            foreach (var registration in Sui._registeredMenuButtons.ToArray())
            {
                if (registration.Scene != scene) continue;
                var root = container.Find(registration.Id);
                var button = root != null ? root.GetComponentInChildren<Button>(true) : null;
                if (button == null) continue;
                var text = root.GetComponentInChildren<TMPro.TMP_Text>(true);
                var label = text != null && !string.IsNullOrWhiteSpace(text.text) ? text.text.Trim() : registration.Id;
                entries.Add((label, () => button.onClick.Invoke()));
            }
            return entries;
        }

        internal static void ClearTitle() => Sui._titleMenuButtonsContainer = null;
        internal static void ClearPause() => Sui._pauseMenu = null;

        private static void AddRegistrations(ESonsScene scene, Transform parent, PauseMenu menu)
        {
            foreach (var registration in Sui._registeredMenuButtons.ToArray())
            {
                if (registration.Scene != scene)
                    continue;
                try
                {
                    AddRegistration(registration, parent, menu);
                }
                catch (Exception exception)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Menu button '" + registration.Id + "' failed: " + exception.Message);
                }
            }
        }

        private static void AddRegistration(Sui.MenuButtonRegistration registration, Transform parent, PauseMenu menu)
        {
            if (parent == null || parent.Find(registration.Id) != null)
                return;

            var key = (registration.Scene, registration.Id);
            if (!PendingRegistrations.Add(key))
                return;

            SUiElement element = null;
            GameObject root = null;
            try
            {
                element = registration.ElementFactory();
                root = element?.Root;
                if (!root)
                    throw new InvalidOperationException("Menu button factory did not return a live UI element.");
                if (!parent)
                    throw new InvalidOperationException("Menu button container was destroyed by its factory.");

                root.name = registration.Id;
                Attach(root, parent, registration.Index, menu);
            }
            catch
            {
                DiscardFailedRegistration(element, root);
                throw;
            }
            finally
            {
                PendingRegistrations.Remove(key);
            }
        }

        private static void DiscardFailedRegistration(SUiElement element, GameObject root)
        {
            try { if (root) root.SetActive(false); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
            try { element?.Remove(); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
            finally
            {
                try { if (root) UnityEngine.Object.Destroy(root); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
            }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RuntimeEntryPoint.LogSource?.LogWarning("Failed to clean up a menu button: " + exception); }
            catch { /* Preserve the factory or attachment failure. */ }
        }

        internal static void Attach(GameObject root, Transform parent, int index, PauseMenu menu)
        {
            root.transform.SetParent(parent, false);
            if (index >= 0)
                root.transform.SetSiblingIndex(index);
            else
                root.transform.SetAsLastSibling();

            var selectable = root.GetComponent<Selectable>();
            if (selectable != null)
                RegisterNavigation(parent, selectable);

            if (menu != null)
            {
                AddOnce(menu._activateForMenu, root);
                AddOnce(menu._deactivateForOptions, root);
                AddOnce(menu._deactivateForPlayers, root);
                AddOnce(menu._deactivateForSave, root);
                AddOnce(menu._deactivateForLoad, root);
            }

            root.SetActive(menu == null || (menu._isActive && !menu._optionsMenuActive
                && !menu._playersMenuActive && !menu._saveMenuActive && !menu._loadMenuActive));
        }

        private static void AddOnce(Il2CppSystem.Collections.Generic.List<GameObject> targets, GameObject root)
        {
            if (targets != null && !targets.Contains(root))
                targets.Add(root);
        }

        private static void RegisterNavigation(Transform parent, Selectable selectable)
        {
            // Retain the game's authored navigation groups and let its own controller
            // skip hidden/disabled actions when options or multiplayer state changes.
            var registered = false;
            foreach (var navigation in parent.root.GetComponentsInChildren<DynamicVerticalNavigation>(true))
            {
                var first = InsertSelectable(navigation._selectables1, parent, selectable);
                var second = InsertSelectable(navigation._selectables2, parent, selectable);
                if (first != null)
                {
                    navigation._selectables1 = first;
                    registered = true;
                }
                if (second != null)
                {
                    navigation._selectables2 = second;
                    registered = true;
                }
            }
            if (!registered)
            {
                var navigation = selectable.navigation;
                navigation.mode = Navigation.Mode.Automatic;
                navigation.selectOnUp = null;
                navigation.selectOnDown = null;
                navigation.selectOnLeft = null;
                navigation.selectOnRight = null;
                selectable.navigation = navigation;
            }
        }

        private static Il2CppReferenceArray<Selectable> InsertSelectable(
            Il2CppReferenceArray<Selectable> source, Transform parent, Selectable selectable)
        {
            if (source == null)
                return null;

            var entries = new List<Selectable>(source.Length + 1);
            var insertion = -1;
            var sibling = selectable.transform.GetSiblingIndex();
            foreach (var entry in source)
            {
                if (entry == selectable)
                    return source;
                if (entry != null && entry.transform.parent == parent)
                {
                    var isBefore = entry.transform.GetSiblingIndex() < sibling;
                    if (insertion == -1 || isBefore)
                        insertion = entries.Count + (isBefore ? 1 : 0);
                }
                entries.Add(entry);
            }
            if (insertion == -1)
                return null;

            entries.Insert(insertion, selectable);
            return new Il2CppReferenceArray<Selectable>(entries.ToArray());
        }
    }
}
