// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility settings handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;

namespace Modstoke.Loader;

internal sealed partial class SettingsFallback
{
    public void CreatePanel() => SettingsRegistration.Instance.CreatePanel();

    public void UpdatePanel() => SettingsRegistration.Instance.UpdatePanel();

    public bool TryOpenPanel(string id, Action<bool> onClosed) =>
        SettingsRegistration.Instance.TryOpenPanel(id, onClosed);

    public void ClosePanel(bool returnToOwner) =>
        SettingsRegistration.Instance.ClosePanel(returnToOwner);

    public void RevertPanel() => SettingsRegistration.Instance.RevertPanel();
}
