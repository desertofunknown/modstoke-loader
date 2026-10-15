// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using RedLoader;
using Sons.Gui.Input;
using SonsSdk;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsInputIcons : ISdkInputIconDriver
    {
        internal static readonly OpenACAISonsInputIcons Instance = new();

        private OpenACAISonsInputIcons() { }

        public IconAssetDatabase IconDatabase
        {
            get
            {
                if (!InputIconManager._iconDatabase)
                    InputIconManager._iconDatabase = Resources.FindObjectsOfTypeAll<IconAssetDatabase>().First();
                return InputIconManager._iconDatabase;
            }
        }

        public GameObject Get(string path, out string displayName, out bool isDefaultPrefab)
        {
            displayName = null;
            isDefaultPrefab = false;
            if (string.IsNullOrEmpty(path))
                return null;

            var prefab = IconDatabase.GetIcon(path, out isDefaultPrefab);
            if (!prefab)
            {
                isDefaultPrefab = false;
                return null;
            }

            var control = InputSystem.FindControl(path);
            displayName = control != null ? control.displayName : "NA";
            var icon = Object.Instantiate(prefab);
            try
            {
                if (isDefaultPrefab)
                {
                    var text = icon.GetComponentInChildren<TMP_Text>(true);
                    if (text != null)
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
            var icon = Get(path, out _, out _);
            if (!icon)
                return null;

            try
            {
                icon.SetActive(true);
                icon.transform.SetParent(parent, false);
                icon.GetComponent<RectTransform>().anchoredPosition = new Vector2(-40, 0);
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
            var template = dynamicIcon._defaultInstance;
            var icon = InputIconManager.Get(action, out _, out _);
            if (!icon)
                return null;

            GameObject activeIcon;
            try
            {
                if (!template)
                    throw new InvalidOperationException("The keybind icon template is missing.");

                var sourceRect = template.GetComponent<RectTransform>();
                var targetRect = icon.GetComponent<RectTransform>();
                if (!sourceRect || !targetRect)
                    throw new InvalidOperationException("Keybind icons require RectTransform components.");

                icon.transform.SetParent(template.transform.parent, false);
                CopyRectTransform(sourceRect, targetRect);
                icon.transform.SetSiblingIndex(template.transform.GetSiblingIndex());
                activeIcon = dynamicIcon._activeInstance;
            }
            catch
            {
                RetireIcon(icon);
                throw;
            }

            RetireIcon(activeIcon, template);
            if (previous != activeIcon)
                RetireIcon(previous, template);
            return icon;
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

        private static void RetireIcon(GameObject icon, GameObject template = null)
        {
            try { if (icon) icon.SetActive(false); }
            catch (Exception exception) { ReportCleanupFailure(exception); }

            try { if (icon && icon != template) Object.Destroy(icon); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Warning("Failed to clean up an input icon: " + exception); }
            catch { /* Preserve the original operation failure. */ }
        }
    }
}
