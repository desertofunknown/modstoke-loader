// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Modstoke.Loader;
using Modstoke.Sons;
using global::Sons.Gui;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.PropertyVariants;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal static class GameMenuBridge
    {
        internal const string TitleSceneName = "SonsTitleScene";
        internal const string PauseButtonPath = "Panel/BottomMenuPanel/LeftPanel";
        private const string ButtonName = "OpenACAI_MODS";
        private static Button _titleTemplate;
        private static GameObject _titleButton;
        private static GameObject _pauseButton;
        private static int _titleSceneHandle;
        private static int _pauseSceneHandle;

        internal static void OnSceneLoaded(Scene scene)
        {
            // A dedicated server loads the same scenes but has no menu anyone can open.
            if (RedLoader.Utils.LoaderEnvironment.IsDedicatedServer
                || scene.name != TitleSceneName && scene.name != "SonsMain")
                return;

            MenuBridgeState.MarkScene(scene.name);
            if (scene.name == TitleSceneName)
                BindTitleScene(scene);
        }

        internal static void OnSceneUnloaded(Scene scene)
        {
            if (scene.handle == _titleSceneHandle)
            {
                _titleTemplate = null;
                _titleButton = null;
                _titleSceneHandle = 0;
                UiMenuButtons.ClearTitle();
                MenuBridgeState.MarkButtonCleared(MenuBridgeState.TitleButtonId, "Title scene unloaded.");
            }
            if (scene.handle == _pauseSceneHandle)
            {
                _pauseButton = null;
                _pauseSceneHandle = 0;
                UiMenuButtons.ClearPause();
                MenuBridgeState.MarkButtonCleared(MenuBridgeState.PauseButtonId, "Pause menu scene unloaded.");
            }
            MenuRenderer.OnSceneUnloaded(scene.handle);
        }

        internal static Button FindTitleTemplate()
        {
            if (_titleTemplate != null)
                return _titleTemplate;

            var scene = SceneManager.GetSceneByName(TitleSceneName);
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var button in root.GetComponentsInChildren<Button>(true))
                {
                    if (button != null && button.name == "SinglePlayerButton" && button.transform.parent != null)
                    {
                        _titleTemplate = button;
                        _titleSceneHandle = scene.handle;
                        return button;
                    }
                }
            }
            return null;
        }

        internal static void BindTitleScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.name != TitleSceneName)
                return;

            var stopwatch = Stopwatch.StartNew();
            var id = MenuBridgeState.TitleButtonId;
            try
            {
                var source = FindTitleTemplate();
                if (source == null)
                {
                    MenuBridgeState.MarkButtonSkipped(id, "Title scene button is not available yet.");
                    return;
                }

                var parent = source.transform.parent;
                if (_titleButton == null)
                {
                    MenuBridgeState.MarkButtonAttempt(id, "Attaching", "Binding to the title scene menu.");
                    _titleButton = CreateButton(source, parent, 3, null);
                    MarkAttached(id, parent, stopwatch.Elapsed);
                }
                UiMenuButtons.BindTitle(parent);
            }
            catch (Exception exception)
            {
                RecordFailure(id, exception);
            }
        }

        internal static void OnPauseMenuAvailable(PauseMenu menu)
        {
            if (menu == null || RedLoader.Utils.LoaderEnvironment.IsDedicatedServer)
                return;

            var stopwatch = Stopwatch.StartNew();
            var id = MenuBridgeState.PauseButtonId;
            try
            {
                GameRuntime.ObservePauseMenu();
                FrontEnd.PauseMenuAvailable(menu);
                var parent = menu.transform.Find(PauseButtonPath);
                if (parent == null)
                    throw new InvalidOperationException("Pause menu button container was not found.");

                if (_pauseButton == null || _pauseButton.transform.parent != parent)
                {
                    MenuBridgeState.MarkButtonAttempt(id, "Attaching", "Binding to the game's pause menu instance.");
                    var source = FindPauseTemplate(parent);
                    if (source == null)
                        throw new InvalidOperationException("Pause menu has no native button template.");
                    _pauseButton = CreateButton(source, parent, -1, menu);
                    _pauseSceneHandle = menu.gameObject.scene.handle;
                    MarkAttached(id, parent, stopwatch.Elapsed);
                }
                UiMenuButtons.BindPause(menu, parent);
            }
            catch (Exception exception)
            {
                RecordFailure(id, exception);
            }
        }

        private static Button FindPauseTemplate(Transform parent)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                // Native pause actions are direct children of the authored button group.
                if (child.name.StartsWith("OpenACAI", StringComparison.Ordinal))
                    continue;
                var button = child.GetComponent<Button>();
                if (button != null)
                    return button;
            }
            return null;
        }

        private static GameObject CreateButton(Button source, Transform parent, int index, PauseMenu menu)
        {
            var existing = parent.Find(ButtonName);
            if (existing != null)
                return existing.gameObject;

            GameObject clone = null;
            try
            {
                clone = CloneInactiveTemplate(source.gameObject, true);
                clone.name = ButtonName;
                foreach (var label in clone.GetComponentsInChildren<TMP_Text>(true))
                {
                    label.text = "MODS";
                    label.margin = Vector4.zero;
                }
                foreach (var label in clone.GetComponentsInChildren<Text>(true))
                    label.text = "MODS";

                var button = clone.GetComponent<Button>();
                button.interactable = true;
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener((UnityAction)new Action(MenuController.Show));
                UiMenuButtons.Attach(clone, parent, index, menu);
                return clone;
            }
            catch
            {
                if (clone != null)
                    UnityEngine.Object.Destroy(clone);
                throw;
            }
        }

        internal static GameObject CloneInactiveTemplate(GameObject source, bool clearButton)
        {
            // Configure while inactive so native localization and selection callbacks
            // cannot run with the source template's bindings.
            var staging = new GameObject("Modstoke Template Staging");
            staging.SetActive(false);
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source, staging.transform, false);
                clone.SetActive(false);
                PrepareMenuTemplate(clone);
                if (clearButton)
                    ClearPersistentEvents(clone);
                clone.transform.SetParent(null, false);
                return clone;
            }
            catch
            {
                if (clone != null)
                    UnityEngine.Object.Destroy(clone);
                throw;
            }
            finally
            {
                UnityEngine.Object.Destroy(staging);
            }
        }

        private static void PrepareMenuTemplate(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<LocalizeStringEvent>(true))
                RemoveBinding(component);
            foreach (var component in root.GetComponentsInChildren<GameObjectLocalizer>(true))
                RemoveBinding(component);
            foreach (var component in root.GetComponentsInChildren<SetSelectable>(true))
                RemoveBinding(component);
            foreach (var component in root.GetComponentsInChildren<OnSelectUi>(true))
                component._onSelect = new UnityEvent();
        }

        private static void ClearPersistentEvents(GameObject root)
        {
            foreach (var button in root.GetComponentsInChildren<Button>(true))
                button.onClick = new Button.ButtonClickedEvent();
            foreach (var dropdown in root.GetComponentsInChildren<TMP_Dropdown>(true))
                dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
        }

        private static void RemoveBinding(Behaviour component)
        {
            component.enabled = false;
            // This is an inactive, owned clone; remove bindings before its first Awake/OnEnable.
            UnityEngine.Object.DestroyImmediate(component);
        }

        private static void MarkAttached(string id, Transform parent, TimeSpan duration)
        {
            var path = new List<string>();
            for (var current = parent; current != null; current = current.parent)
                path.Add(current.name);
            path.Reverse();
            MenuBridgeState.MarkButtonAttached(id, string.Join("/", path), "native-lifecycle");
            Timings.RecordDuration("ui.sons-menu-bridge", "attach", duration, id);
            RuntimeEntryPoint.RequestLoaderStateRefresh("menu-button-attached:" + id);
            RuntimeEntryPoint.LogSource?.LogInfo("Attached Modstoke Mods button to " + parent.name + ".");
        }

        private static void RecordFailure(string id, Exception exception)
        {
            MenuBridgeState.MarkButtonFailed(id, exception.Message);
            RuntimeEntryPoint.LogSource?.LogWarning("Menu button attachment failed (" + id + "): " + exception.Message);
        }
    }

    internal static class SonsMenuPatches
    {
        internal static void AfterPauseMenuAvailable(PauseMenu __instance)
        {
            GameMenuBridge.OnPauseMenuAvailable(__instance);
        }
    }
}

