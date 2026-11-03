// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using RedLoader;
using SonsSdk;
using SUI;
using UnityEngine;
using static SUI.SUI;
using SettingsEntry = SUI.SettingsRegistry.SettingsEntry;

namespace Modstoke.Loader
{
    internal sealed partial class SettingsRegistration
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

            foreach (var config in entry.ConfigEntries)
            {
                if (entry._dividers.ContainsKey(config.CategoryName))
                    continue;

                var divider = SLabelDivider;
                try
                {
                    divider.SetParent(entry.Container);
                    divider.RichText("\uf0d7 " + config.CategoryName).FontColor("#ea2f4e40").OnClick(() =>
                    {
                        entry.ToggleCategory(config.CategoryName, !entry._dividers[config.CategoryName].Item2);
                    });
                    entry._dividers.Add(config.CategoryName, (divider, true));
                }
                catch
                {
                    CleanupSettings(divider.Remove);
                    throw;
                }
            }
        }

        private static IEnumerator RefreshColorsAfterLayout(SettingsEntry entry, Transform parent)
        {
            yield return new WaitForSecondsRealtime(0.1f);

            if (!parent || !entry.Container.Root)
                yield break;

            foreach (var config in entry.ConfigEntries)
            {
                if (config.ConfigEntry is ConfigEntry<Color> && config.UiElement.Root &&
                    config.UiElement.Root.activeInHierarchy && config.UiElement.RectTransform.parent == parent)
                {
                    var picker = config.WrappedElement.As<SColorWheelOptions>();
                    if (picker == null || !picker.ColorWheelControl)
                        continue;

                    picker.ColorWheelControl.OnEnable();
                }
            }
        }

        public void ParentEntry(SettingsEntry entry, Transform parent)
        {
            ValidateDividers(entry);
            var categories = entry.ConfigEntries.GroupBy(config => config.CategoryName);

            foreach (var pair in categories)
            {
                entry._dividers[pair.Key].Item1.RectTransform.SetParent(parent, false);
                foreach (var config in pair)
                {
                    foreach (var header in config.HeaderElements)
                        header.RectTransform.SetParent(parent, false);

                    config.UiElement.RectTransform.SetParent(parent, false);
                    config.RefreshVisibility();
                }
            }

            entry.UserContentContainer?.RectTransform.SetParent(parent, false);
            RefreshColorsAfterLayout(entry, parent).RunCoro();
        }

        public void UnparentEntry(SettingsEntry entry)
        {
            ExceptionDispatchInfo failure = null;
            var parent = entry.Container.RectTransform;
            RunSettingsStep(() => ParkElement(entry.UserContentContainer, parent, false), ref failure);
            foreach (var config in entry.ConfigEntries)
            {
                if (config.WrappedElement is SKeybindOptions keybind)
                    RunSettingsStep(keybind.CancelRebind, ref failure);
                RunSettingsStep(() => ParkElement(config.UiElement, parent, true), ref failure);

                foreach (var header in config.HeaderElements)
                    RunSettingsStep(() => ParkElement(header, parent, true), ref failure);
            }

            if (entry._dividers != null)
            {
                foreach (var divider in entry._dividers.Values)
                    RunSettingsStep(() => ParkElement(divider.Item1, parent, false), ref failure);
            }
            failure?.Throw();
        }

        private static void ParkElement(SUiElement element, Transform parent, bool hide)
        {
            if (element == null || !element.Root)
                return;
            if (hide)
                element.Root.SetActive(false);
            element.RectTransform.SetParent(parent, false);
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
            ValidateDividers(entry);
            var divider = entry._dividers[identifier];
            foreach (var config in entry.ConfigEntries)
            {
                if (config.CategoryName == identifier)
                    config.ShouldBeVisible = show;
            }

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
            ExceptionDispatchInfo failure = null;
            if (entry.Callback != null)
                RunSettingsStep(entry.Callback, ref failure);
            if (entry.ConfigClassCallback != null)
                RunSettingsStep(entry.ConfigClassCallback, ref failure);
            failure?.Throw();
        }

        private static void RunSettingsStep(Action action, ref ExceptionDispatchInfo failure)
        {
            try { action(); }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
