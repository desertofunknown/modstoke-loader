// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RedLoader;
using SonsSdk;
using SUI;
using UnityEngine;
using static SUI.SUI;
using SettingsEntry = SUI.SettingsRegistry.SettingsEntry;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSettingsRegistration
    {
        public bool CheckEntryForChanges(SettingsEntry entry)
        {
            foreach (var configEntry in entry.ConfigEntries)
            {
                var config = configEntry.ConfigEntry;
                if (config.HasChanged)
                {
                    RLog.Debug($"{config.DisplayName} has changed ({config.GetDefaultValueAsString()} -> {config.GetValueAsString()})");
                    return true;
                }
            }

            return false;
        }

        private static void ValidateDividers(SettingsEntry entry)
        {
            entry._dividers ??= new Dictionary<string, (SLabelDividerOptions, bool)>();
            if (entry._dividers.Count != 0)
                return;

            foreach (var config in entry.ConfigEntries)
            {
                if (entry._dividers.ContainsKey(config.CategoryName))
                    continue;

                var divider = SLabelDivider.RichText("\uf0d7 " + config.CategoryName).FontColor("#ea2f4e40").OnClick(() =>
                {
                    entry.ToggleCategory(config.CategoryName, !entry._dividers[config.CategoryName].Item2);
                });
                entry._dividers.Add(config.CategoryName, (divider, true));
            }
        }

        private static IEnumerator RefreshColorsAfterLayout(SettingsEntry entry)
        {
            yield return new WaitForSeconds(0.1f);

            foreach (var config in entry.ConfigEntries)
            {
                if (config.ConfigEntry is ConfigEntry<Color> && config.UiElement.Root.activeSelf)
                {
                    var picker = config.WrappedElement.As<SColorWheelOptions>();
                    if (picker == null)
                        continue;

                    picker.ColorWheelControl.OnEnable();
                }
            }
        }

        public void ParentEntry(SettingsEntry entry, Transform parent)
        {
            ValidateDividers(entry);
            var categories = entry.ConfigEntries.GroupBy(config => config.CategoryName)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var pair in categories)
            {
                entry._dividers[pair.Key].Item1.RectTransform.SetParent(parent, false);
                foreach (var config in pair.Value)
                {
                    foreach (var header in config.HeaderElements)
                        header.RectTransform.SetParent(parent, false);

                    config.UiElement.RectTransform.SetParent(parent, false);
                    config.RefreshVisibility();
                }
            }

            entry.UserContentContainer?.RectTransform.SetParent(parent, false);
            RefreshColorsAfterLayout(entry).RunCoro();
        }

        public void UnparentEntry(SettingsEntry entry)
        {
            entry.UserContentContainer?.RectTransform.SetParent(entry.Container.RectTransform, false);
            foreach (var config in entry.ConfigEntries)
            {
                config.UiElement.RectTransform.SetParent(entry.Container.RectTransform, false);
                config.UiElement.Root.SetActive(false);

                foreach (var header in config.HeaderElements)
                {
                    header.RectTransform.SetParent(entry.Container.RectTransform, false);
                    header.Root.SetActive(false);
                }
            }

            foreach (var divider in entry._dividers)
                divider.Value.Item1.RectTransform.SetParent(entry.Container.RectTransform, false);
        }

        public void RevertEntry(SettingsEntry entry)
        {
            foreach (var config in entry.ConfigEntries)
            {
                if (config.WrappedElement is SKeybindOptions keybind)
                    keybind.RevertToDefault();
                else
                    config.ConfigEntry.ResetToDefault();
            }
        }

        public void ToggleEntryCategory(SettingsEntry entry, string identifier, bool show)
        {
            foreach (var config in entry.ConfigEntries)
            {
                if (config.CategoryName == identifier)
                    config.ShouldBeVisible = show;
            }

            var divider = entry._dividers[identifier];
            divider.Item2 = show;
            divider.Item1.RichText((show ? "\uf0d7 " : "\uf0da ") + identifier);
            entry._dividers[identifier] = divider;
        }

        public void SaveEntry(SettingsEntry entry)
        {
            foreach (var category in entry.ConfigEntries.GroupBy(config => config.ConfigEntry.Category).Select(group => group.Key))
                category.SaveToFile();
        }

        public void InvokeEntryCallbacks(SettingsEntry entry)
        {
            entry.Callback?.Invoke();
            entry.ConfigClassCallback?.Invoke();
        }
    }
}
