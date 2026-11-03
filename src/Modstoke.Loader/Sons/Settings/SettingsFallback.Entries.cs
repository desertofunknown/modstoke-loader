// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility settings handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using RedLoader;
using SUI;
using UnityEngine;
using static SUI.SUI;
using SettingsConfigEntry = SUI.SettingsRegistry.SettingsConfigEntry;
using SettingsEntry = SUI.SettingsRegistry.SettingsEntry;

namespace Modstoke.Loader;

internal sealed partial class SettingsFallback
{
    public void RefreshConfigVisibility(SettingsConfigEntry owner)
    {
        if (!owner.ShouldBeVisible && owner.WrappedElement is SKeybindOptions keybind)
            keybind.CancelRebind();

        owner.UiElement.Root.SetActive(owner.ShouldBeVisible);
        foreach (var element in owner.HeaderElements)
            element.Root.SetActive(owner.ShouldBeVisible);
    }

    public void CreateConfigHeader(SettingsConfigEntry owner)
    {
        if (owner.HeaderInfo == null)
            return;

        var header = SLabel;
        try
        {
            header.SetParent(owner.UiElement.RectTransform.parent);
            header.Text(owner.HeaderInfo.Text).Alignment(owner.HeaderInfo.Alignment).Wrap(true).PHeight(50);
            if (owner.HeaderInfo.LightFont)
                header.Font(EFont.RobotoLight);
            if (owner.HeaderInfo.Color.HasValue)
                header.FontColor(owner.HeaderInfo.Color.Value);
            else
                header.FontColor("#ea2f4e25");
            header.RectTransform.SetSiblingIndex(owner.UiElement.RectTransform.GetSiblingIndex());
            header.Root.SetActive(owner.ShouldBeVisible);
            owner.HeaderElements.Add(header);
        }
        catch
        {
            SettingsRegistration.CleanupSettings(header.Remove);
            throw;
        }
    }

    public void CreateConfigSpacing(SettingsConfigEntry owner)
    {
        if (owner.SpacingInfo == null)
            return;

        var spacing = SContainer;
        try
        {
            spacing.SetParent(owner.UiElement.RectTransform.parent);
            spacing.PHeight(owner.SpacingInfo.Spacing);
            spacing.RectTransform.SetSiblingIndex(owner.UiElement.RectTransform.GetSiblingIndex());
            spacing.Root.SetActive(owner.ShouldBeVisible);
            owner.HeaderElements.Add(spacing);
        }
        catch
        {
            SettingsRegistration.CleanupSettings(spacing.Remove);
            throw;
        }
    }

    public bool CheckEntryForChanges(SettingsEntry owner) =>
        SettingsRegistration.Instance.CheckEntryForChanges(owner);

    public void ParentEntry(SettingsEntry owner, Transform parent) =>
        SettingsRegistration.Instance.ParentEntry(owner, parent);

    public void UnparentEntry(SettingsEntry owner) =>
        SettingsRegistration.Instance.UnparentEntry(owner);

    public void RevertEntry(SettingsEntry owner) =>
        SettingsRegistration.Instance.RevertEntry(owner);

    public void ToggleEntryCategory(SettingsEntry owner, string identifier, bool show) =>
        SettingsRegistration.Instance.ToggleEntryCategory(owner, identifier, show);

    public void SaveEntry(SettingsEntry owner) =>
        SettingsRegistration.Instance.SaveEntry(owner);

    public void InvokeEntryCallbacks(SettingsEntry owner) =>
        SettingsRegistration.Instance.InvokeEntryCallbacks(owner);

    public void SetEntryCharacterLimit(SettingsEntry owner, ConfigEntry entry, int characterLimit, bool disableAutoSizing)
    {
        if (owner.GetWrappedElementForEntry(entry) is not STextboxOptions textbox || !textbox.InputFieldObject)
            throw new ArgumentException("Config entry has no text input field.", nameof(entry));

        var input = textbox.InputFieldObject;
        input.characterLimit = characterLimit;
        if (disableAutoSizing)
        {
            input.textComponent.enableAutoSizing = false;
            input.textComponent.fontSize = 18;
        }
    }
}
