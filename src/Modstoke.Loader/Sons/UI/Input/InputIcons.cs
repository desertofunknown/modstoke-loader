// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using RedLoader;
using global::Sons.Gui.Input;
using SonsSdk;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    internal sealed class InputIcons : ISdkInputIconDriver
    {
        internal static readonly InputIcons Instance = new();

        private InputIcons() { }

        public IconAssetDatabase IconDatabase
        {
            get
            {
                var database = InputIconManager._iconDatabase;
                if (IsLive(database))
                    return database;

                InputIconManager._iconDatabase = null;
                foreach (var candidate in Resources.FindObjectsOfTypeAll<IconAssetDatabase>())
                {
                    if (!IsLive(candidate))
                        continue;
                    InputIconManager._iconDatabase = candidate;
                    return candidate;
                }

                return null;
            }
        }

        public GameObject Get(string path, out string displayName, out bool isDefaultPrefab)
        {
            displayName = null;
            isDefaultPrefab = false;
            if (string.IsNullOrEmpty(path))
                return null;

            var database = IconDatabase;
            if (database is null)
                return null;

            var prefab = database.GetIcon(path, out isDefaultPrefab);
            if (!IsLive(prefab))
            {
                isDefaultPrefab = false;
                return null;
            }

            var control = InputSystem.FindControl(path);
            displayName = control is not null && !control.WasCollected ? control.displayName : "NA";
            var icon = Object.Instantiate(prefab);
            if (!IsLive(icon))
            {
                displayName = null;
                isDefaultPrefab = false;
                return null;
            }
            try
            {
                if (isDefaultPrefab)
                {
                    var text = icon.GetComponentInChildren<TMP_Text>(true);
                    if (IsLive(text))
                        text.text = displayName;
                }

                return icon;
            }
            catch
            {
                RetireIcon(icon);
                throw;
            }
        }

        public GameObject Add(string path, Transform parent)
        {
            if (parent is not null && !IsLive(parent))
                return null;

            var icon = Get(path, out _, out _);
            if (!IsLive(icon))
                return null;

            try
            {
                if (parent is not null && !IsLive(parent))
                {
                    RetireIcon(icon);
                    return null;
                }

                var targetRect = icon.GetComponent<RectTransform>();
                if (!IsLive(targetRect))
                    throw new InvalidOperationException("Input icons require a RectTransform component.");
                targetRect.SetParent(parent, false);
                targetRect.anchoredPosition = new Vector2(-40, 0);
                icon.SetActive(true);
                return icon;
            }
            catch
            {
                RetireIcon(icon);
                throw;
            }
        }

        public GameObject CreateKeybindIcon(InputAction action, DynamicInputIcon dynamicIcon, GameObject previous)
        {
            var icon = PrepareKeybindIcon(action, dynamicIcon, out var template, out var activeIcon);
            if (!IsLive(icon))
                return null;

            RetireIcon(activeIcon, template);
            if (IsLive(previous) && (!IsLive(activeIcon) || previous != activeIcon))
                RetireIcon(previous, template);
            return icon;
        }

        internal GameObject PrepareKeybindIcon(InputAction action, DynamicInputIcon dynamicIcon,
            out GameObject template, out GameObject activeIcon)
        {
            template = null;
            activeIcon = null;
            if (!IsLive(dynamicIcon) || action is null || action.WasCollected)
                return null;

            template = dynamicIcon._defaultInstance;
            if (!IsLive(template))
                return null;

            var controls = action.controls;
            if (controls.Count == 0)
                return null;
            var control = controls[0];
            if (control is null || control.WasCollected)
                return null;

            var sourceRect = template.GetComponent<RectTransform>();
            if (!IsLive(sourceRect))
                throw new InvalidOperationException("Keybind icons require RectTransform components.");

            var icon = Get(control.path, out _, out _);
            if (!IsLive(icon))
                return null;

            try
            {
                var targetRect = icon.GetComponent<RectTransform>();
                if (!IsLive(dynamicIcon) || !IsLive(sourceRect) || !IsLive(targetRect))
                    throw new InvalidOperationException("Keybind icons require RectTransform components.");

                targetRect.SetParent(sourceRect.parent, false);
                CopyRectTransform(sourceRect, targetRect);
                targetRect.SetSiblingIndex(sourceRect.GetSiblingIndex());
                activeIcon = dynamicIcon._activeInstance;
                return icon;
            }
            catch
            {
                RetireIcon(icon);
                throw;
            }
        }

        private static void CopyRectTransform(RectTransform source, RectTransform target)
        {
            target.localScale = source.localScale;
            target.localRotation = source.localRotation;
            target.localPosition = source.localPosition;
            target.anchorMin = source.anchorMin;
            target.anchorMax = source.anchorMax;
            target.anchoredPosition = source.anchoredPosition;
            target.sizeDelta = source.sizeDelta;
            target.pivot = source.pivot;
            target.offsetMin = source.offsetMin;
            target.offsetMax = source.offsetMax;
        }

        internal static bool IsLive(Object value) => value is not null && !value.WasCollected && value;

        internal static void RetireIcon(GameObject icon, GameObject template = null)
        {
            try { if (IsLive(icon)) icon.SetActive(false); }
            catch (Exception exception) { ReportCleanupFailure(exception); }

            try { if (IsLive(icon) && (!IsLive(template) || icon != template)) Object.Destroy(icon); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Warning("Failed to clean up an input icon: " + exception); }
            catch { /* Preserve the original operation failure. */ }
        }
    }
}
