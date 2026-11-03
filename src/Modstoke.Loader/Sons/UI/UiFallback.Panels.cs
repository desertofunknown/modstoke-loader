// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility shared UI handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using SUI;
using UnityEngine;
using Sui = SUI.SUI;

namespace Modstoke.Loader;

internal sealed partial class UiFallback
{
    public SPanelOptions RegisterPanel(string id, Transform parent, bool enableInput, KeyCode? toggleKey) =>
        UiPanels.Instance.Register(id, parent, enableInput, toggleKey);

    public SPanelOptions GetPanel(string id)
    {
        return UiPanels.Find(id);
    }

    public bool TogglePanel(string id)
    {
        var panel = Sui.GetPanel(id);
        if (panel == null)
            return false;
        panel.Toggle();
        return panel.Root.activeSelf;
    }

    public SPanelOptions TogglePanel(string id, bool show)
    {
        var panel = Sui.GetPanel(id);
        if (panel == null)
            return null;
        panel.Active(show);
        return panel;
    }

    public bool RemovePanel(string id)
    {
        return UiPanels.Instance.Remove(id);
    }

    public void ReleasePanel(SPanelOptions panel)
    {
        UiPanels.Release(panel);
    }
}
