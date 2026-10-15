// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Sons Of The Forest title/pause menu bridge for the Modstoke Mods screen.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenACAI.Loader;
using OpenACAI.Sons;
using Sons.Gui;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenAcaiSonsMenuBridge
    {
        private const string TitleSceneName = "SonsTitleScene";
        private const string GameSceneName = "SonsMain";
        private const string TitleButtonName = "OpenACAI_MODS";
        private const string PauseButtonName = "OpenACAI_MODS";
        private static readonly HashSet<int> LoggedTitleParents = new HashSet<int>();
        private static readonly HashSet<int> LoggedPauseParents = new HashSet<int>();
        private static GameObject _titleButtonInstance;
        private static GameObject _pauseButtonInstance;
        private static float _nextScanTime;
        private static float _titleScanUntil;
        private static float _activeScanUntil;
        private static float _titleSceneStartedAt;
        private static float _gameSceneStartedAt;
        private static float _pauseMenuAvailableAt;
        private static string _currentSceneName;

        internal static void OnSceneLoaded(string sceneName)
        {
            _currentSceneName = sceneName;
            OpenACAISonsMenuBridgeState.MarkScene(sceneName);
            if (string.Equals(sceneName, TitleSceneName, StringComparison.OrdinalIgnoreCase))
            {
                _titleButtonInstance = null;
                _titleSceneStartedAt = Time.realtimeSinceStartup;
                _titleScanUntil = Time.realtimeSinceStartup + 12f;
                _nextScanTime = 0f;
                OpenACAISonsMenuBridgeState.MarkButtonCleared(OpenACAISonsMenuBridgeState.TitleButtonId, "Title scene loaded.");
                TryAttachTitleButton();
            }
            else if (string.Equals(sceneName, GameSceneName, StringComparison.OrdinalIgnoreCase))
            {
                _pauseButtonInstance = null;
                _gameSceneStartedAt = Time.realtimeSinceStartup;
                _activeScanUntil = 0f;
                _nextScanTime = 0f;
                OpenACAISonsMenuBridgeState.MarkButtonCleared(OpenACAISonsMenuBridgeState.PauseButtonId, "Game scene loaded.");
                TryAttachPauseButton(false);
            }
            else
            {
                _titleScanUntil = 0f;
            }
        }

        internal static void OnPauseMenuAvailable(PauseMenu menu, bool forceVisible)
        {
            OpenACAISons.PublishPauseMenu("Available", menu != null, forceVisible);
            _pauseMenuAvailableAt = Time.realtimeSinceStartup;
            TryAttachPauseButton(menu, forceVisible);
        }

        internal static void Update()
        {
            KeepCloneCaption(_titleButtonInstance);
            KeepCloneCaption(_pauseButtonInstance);

            var now = Time.realtimeSinceStartup;
            if (OpenACAIInput.IsPressed(OpenACAIKeybindIds.Back))
            {
                _activeScanUntil = now + 3f;
                _nextScanTime = 0f;
            }

            if (Time.realtimeSinceStartup < _nextScanTime)
            {
                return;
            }

            var titleScan = now < _titleScanUntil && !HasUnityObject(_titleButtonInstance);
            var activeScan = now < _activeScanUntil;
            if (!titleScan && !activeScan)
            {
                _nextScanTime = now + 30f;
                return;
            }

            _nextScanTime = now + (titleScan ? 0.1f : 0.25f);

            if (titleScan)
            {
                TryAttachTitleButton();
            }

            if (activeScan && !HasUnityObject(_pauseButtonInstance))
            {
                TryAttachPauseButton(true);
            }
        }

        private static void TryAttachTitleButton()
        {
            var stopwatch = Stopwatch.StartNew();
            OpenACAISonsMenuBridgeState.MarkButtonAttempt(OpenACAISonsMenuBridgeState.TitleButtonId, "Scanning", "Searching title menu button templates.");
            try
            {
                var sources = Resources.FindObjectsOfTypeAll<Button>()
                    .Where(button => button != null && button.name == "SinglePlayerButton" && button.transform.parent != null)
                    .OrderByDescending(button => button.gameObject.activeInHierarchy)
                    .ThenByDescending(button => button.gameObject.activeSelf)
                    .ToArray();

                foreach (var source in sources)
                {
                    var parent = source.transform.parent;
                    if (parent == null)
                    {
                        continue;
                    }

                    var existing = parent.Find(TitleButtonName);
                    if (existing != null)
                    {
                        _titleButtonInstance = existing.gameObject;
                        MarkMenuButtonAttached(OpenACAISonsMenuBridgeState.TitleButtonId, parent, "existing-title-button", stopwatch.Elapsed, _titleSceneStartedAt);
                        return;
                    }

                    var parentId = parent.GetInstanceID();
                    var clone = CloneMenuButton(source.gameObject, TitleButtonName, "MODS", 200f, false);
                    if (clone == null)
                    {
                        continue;
                    }

                    clone.transform.SetParent(parent, false);
                    clone.transform.SetSiblingIndex(Math.Min(3, parent.childCount - 1));
                    SetButtonText(clone, "MODS");
                    _titleButtonInstance = clone;

                    if (LoggedTitleParents.Add(parentId))
                    {
                        OpenACAIEndnightRuntime.LogSource?.LogInfo("Attached Modstoke Mods button to title menu parent " + parent.name + ".");
                    }

                    MarkMenuButtonAttached(OpenACAISonsMenuBridgeState.TitleButtonId, parent, "cloned-from:" + source.name, stopwatch.Elapsed, _titleSceneStartedAt);
                    return;
                }

                OpenACAISonsMenuBridgeState.MarkButtonSkipped(OpenACAISonsMenuBridgeState.TitleButtonId, "No title menu template was found.");
            }
            catch (Exception ex)
            {
                OpenACAISonsMenuBridgeState.MarkButtonFailed(OpenACAISonsMenuBridgeState.TitleButtonId, ex.Message);
                OpenACAITimings.RecordDuration("ui.sons-menu-bridge", "title-attach-failed", stopwatch.Elapsed, ex.Message);
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("title-mods-button-failed");
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Title menu Mods button attach failed: " + ex.Message);
            }
        }

        private static void TryAttachPauseButton(bool allowResourceSearch)
        {
            try
            {
                var menu = PauseMenu._instance;
                if (menu == null && allowResourceSearch)
                {
                    menu = Resources.FindObjectsOfTypeAll<PauseMenu>().FirstOrDefault();
                }

                TryAttachPauseButton(menu, true);
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Pause menu Mods button attach failed: " + ex.Message);
            }
        }

        private static void TryAttachPauseButton(PauseMenu menu, bool forceVisible)
        {
            var stopwatch = Stopwatch.StartNew();
            OpenACAISonsMenuBridgeState.MarkButtonAttempt(OpenACAISonsMenuBridgeState.PauseButtonId, "Scanning", "Searching pause menu button templates.");
            try
            {
                var parent = FindPauseButtonParent(menu);
                if (parent == null)
                {
                    OpenACAISonsMenuBridgeState.MarkButtonSkipped(OpenACAISonsMenuBridgeState.PauseButtonId, "Pause menu button parent was not found.");
                    OpenACAIEndnightRuntime.LogSource?.LogDebug("Pause menu Mods button attach skipped: button parent was not found.");
                    return;
                }

                var existing = parent.Find(PauseButtonName);
                if (existing != null)
                {
                    _pauseButtonInstance = existing.gameObject;
                    MarkMenuButtonAttached(OpenACAISonsMenuBridgeState.PauseButtonId, parent, "existing-pause-button", stopwatch.Elapsed, PauseAttachStartedAt());
                    return;
                }

                var source = FindPauseButtonTemplate(parent);
                if (source == null)
                {
                    OpenACAISonsMenuBridgeState.MarkButtonSkipped(OpenACAISonsMenuBridgeState.PauseButtonId, "No pause menu source button was found under " + parent.name + ".");
                    OpenACAIEndnightRuntime.LogSource?.LogDebug("Pause menu Mods button attach skipped: no source button was found under " + parent.name + ".");
                    return;
                }

                var clone = CloneMenuButton(source.gameObject, PauseButtonName, "MODS", -1f, true);
                if (clone == null)
                {
                    return;
                }

                clone.transform.SetParent(parent, false);
                clone.transform.SetAsLastSibling();
                SetButtonText(clone, "MODS");
                if (forceVisible)
                {
                    clone.SetActive(true);
                }

                _pauseButtonInstance = clone;
                TryAddToPauseActivationList(menu, clone);

                if (LoggedPauseParents.Add(parent.GetInstanceID()))
                {
                    OpenACAIEndnightRuntime.LogSource?.LogInfo("Attached Modstoke Mods button to pause menu.");
                }

                MarkMenuButtonAttached(OpenACAISonsMenuBridgeState.PauseButtonId, parent, "cloned-from:" + source.name, stopwatch.Elapsed, PauseAttachStartedAt());
            }
            catch (Exception ex)
            {
                OpenACAISonsMenuBridgeState.MarkButtonFailed(OpenACAISonsMenuBridgeState.PauseButtonId, ex.Message);
                OpenACAITimings.RecordDuration("ui.sons-menu-bridge", "pause-attach-failed", stopwatch.Elapsed, ex.Message);
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("pause-mods-button-failed");
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Pause menu Mods button attach failed: " + ex.Message);
            }
        }

        private static void MarkMenuButtonAttached(string buttonId, Transform parent, string detail, TimeSpan scanDuration, float startedAt)
        {
            var parentPath = BuildTransformPath(parent);
            OpenACAISonsMenuBridgeState.MarkButtonAttached(buttonId, parentPath, detail);
            OpenACAITimings.RecordDuration("ui.sons-menu-bridge", buttonId + "-attach-scan", scanDuration, detail);
            if (string.Equals(buttonId, OpenACAISonsMenuBridgeState.TitleButtonId, StringComparison.Ordinal))
            {
                PublishObservedScene(TitleSceneName);
            }
            else if (string.Equals(buttonId, OpenACAISonsMenuBridgeState.PauseButtonId, StringComparison.Ordinal))
            {
                PublishObservedScene(GameSceneName);
            }

            if (startedAt > 0f)
            {
                var latencySeconds = Math.Max(0f, Time.realtimeSinceStartup - startedAt);
                OpenACAITimings.RecordDuration("ui.sons-menu-bridge", buttonId + "-attach-latency", TimeSpan.FromSeconds(latencySeconds), parentPath);
            }

            OpenACAIEndnightRuntime.RequestLoaderStateRefresh(buttonId + "-attached");
        }

        private static void PublishObservedScene(string sceneName)
        {
            var buildIndex = -1;
            try
            {
                var scene = SceneManager.GetSceneByName(sceneName);
                if (scene.IsValid())
                {
                    buildIndex = scene.buildIndex;
                }
            }
            catch
            {
            }

            _currentSceneName = sceneName;
            OpenACAISonsMenuBridgeState.MarkScene(sceneName);
            OpenACAISons.PublishScene("Observed", buildIndex, sceneName);
        }

        private static float PauseAttachStartedAt()
        {
            return _pauseMenuAvailableAt > 0f
                ? _pauseMenuAvailableAt
                : _gameSceneStartedAt;
        }

        private static GameObject FindPauseButtonTemplate(Transform parent)
        {
            var buttons = parent.GetComponentsInChildren<Button>(true)
                .Where(button => button != null && !button.name.StartsWith("OpenACAI", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return buttons
                .FirstOrDefault(button => !IsBackLikeButton(button) && !IsOptionsLikeButton(button))
                ?.gameObject
                ?? buttons.FirstOrDefault(button => !IsBackLikeButton(button))
                ?.gameObject
                ?? buttons.FirstOrDefault()?.gameObject;
        }

        private static Transform FindPauseButtonParent(PauseMenu menu)
        {
            if (menu == null)
            {
                return null;
            }

            var exact = menu.transform.Find("Panel/BottomMenuPanel/LeftPanel");
            if (exact != null)
            {
                return exact;
            }

            var buttons = menu.GetComponentsInChildren<Button>(true)
                .Where(button => button != null && !button.name.StartsWith("OpenACAI", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (buttons.Length == 0)
            {
                return null;
            }

            return buttons
                .Where(button => button.transform.parent != null)
                .GroupBy(button => button.transform.parent)
                .OrderByDescending(group => ParentScore(group.Key, group.Count()))
                .Select(group => group.Key)
                .FirstOrDefault();
        }

        private static int ParentScore(Transform parent, int buttonCount)
        {
            var score = buttonCount * 10;
            var path = BuildTransformPath(parent);
            if (path.IndexOf("LeftPanel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 100;
            }

            if (path.IndexOf("BottomMenuPanel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 80;
            }

            if (path.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 20;
            }

            return score;
        }

        private static string BuildTransformPath(Transform transform)
        {
            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static bool IsBackLikeButton(Button button)
        {
            if (button == null)
            {
                return true;
            }

            if (button.name.IndexOf("Back", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return button.GetComponentsInChildren<TMP_Text>(true)
                .Any(text => string.Equals((text?.text ?? string.Empty).Trim(), "BACK", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsOptionsLikeButton(Button button)
        {
            if (button == null)
            {
                return true;
            }

            if (button.name.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return button.GetComponentsInChildren<TMP_Text>(true)
                .Any(text => string.Equals((text?.text ?? string.Empty).Trim(), "OPTIONS", StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasUnityObject(UnityEngine.Object value)
        {
            return value != null;
        }

        private static void KeepCloneCaption(GameObject clone)
        {
            if (!HasUnityObject(clone))
            {
                return;
            }

            foreach (var tmp in clone.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmp != null && !string.Equals(tmp.text, "MODS", StringComparison.Ordinal))
                {
                    tmp.text = "MODS";
                    tmp.margin = Vector4.zero;
                }
            }

            foreach (var label in clone.GetComponentsInChildren<Text>(true))
            {
                if (label != null && !string.Equals(label.text, "MODS", StringComparison.Ordinal))
                {
                    label.text = "MODS";
                }
            }
        }

        private static GameObject CloneMenuButton(GameObject source, string name, string text, float preferredWidth, bool flexibleWidth)
        {
            if (source == null)
            {
                return null;
            }

            var clone = UnityEngine.Object.Instantiate(source);
            clone.name = name;
            RemoveLocalizationHooks(clone);
            SetButtonText(clone, text);

            foreach (var button in clone.GetComponentsInChildren<Button>(true))
            {
                if (button == null)
                {
                    continue;
                }

                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener((UnityAction)new Action(OpenAcaiModMenu.Show));
            }

            var layout = clone.GetComponent<LayoutElement>();
            if (layout != null)
            {
                if (preferredWidth > 0f)
                {
                    layout.preferredWidth = preferredWidth;
                    layout.minWidth = Math.Max(layout.minWidth, preferredWidth);
                }

                if (flexibleWidth)
                {
                    layout.flexibleWidth = -1f;
                }
            }

            var rect = clone.GetComponent<RectTransform>();
            if (rect != null && !flexibleWidth)
            {
                rect.sizeDelta = new Vector2(preferredWidth > 0f ? preferredWidth : rect.sizeDelta.x, rect.sizeDelta.y);
            }

            clone.SetActive(true);
            return clone;
        }

        private static void RemoveLocalizationHooks(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                var fullName = component.GetType().FullName ?? string.Empty;
                if (fullName.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0
                    || fullName.IndexOf("Localizer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    UnityEngine.Object.Destroy(component);
                }
            }
        }

        private static void SetButtonText(GameObject root, string text)
        {
            foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmp == null)
                {
                    continue;
                }

                tmp.text = text;
                tmp.margin = Vector4.zero;
            }

            foreach (var label in root.GetComponentsInChildren<Text>(true))
            {
                if (label != null)
                {
                    label.text = text;
                }
            }
        }

        private static void TryAddToPauseActivationList(PauseMenu menu, GameObject button)
        {
            try
            {
                if (menu == null || button == null)
                {
                    return;
                }

                menu._activateForMenu?.Add(button);
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.LogSource?.LogDebug("Pause menu activation-list registration skipped: " + ex.Message);
            }
        }
    }

    internal static class SonsMenuPatches
    {
        internal static void AfterPauseMenuAvailable()
        {
            var menu = ResolvePauseMenu();
            OpenACAISons.PublishPauseMenu("Available", menu != null, false);
            OpenAcaiSonsMenuBridge.OnPauseMenuAvailable(menu, false);
        }

        internal static void AfterPauseMenuOpen()
        {
            var menu = ResolvePauseMenu();
            OpenACAISons.PublishPauseMenu("Opened", menu != null, true);
            OpenAcaiSonsMenuBridge.OnPauseMenuAvailable(menu, true);
        }

        private static PauseMenu ResolvePauseMenu()
        {
            try
            {
                return PauseMenu._instance ?? Resources.FindObjectsOfTypeAll<PauseMenu>().FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}

