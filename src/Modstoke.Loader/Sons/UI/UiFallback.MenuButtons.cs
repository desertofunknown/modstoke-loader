// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using global::Sons.Gui;
using SonsSdk;
using SUI;

namespace Modstoke.Loader;

internal sealed partial class UiFallback
{
    public void OnSceneInitialized(ESonsScene scene) =>
        UiMenuButtons.Instance.OnSceneInitialized(scene);

    public void OnPauseMenuCreated(PauseMenu menu) =>
        UiMenuButtons.Instance.OnPauseMenuCreated(menu);

    public void RegisterMenuButton(Func<SUiElement> generator, string id, ESonsScene scene, int index) =>
        UiMenuButtons.Instance.Register(generator, id, scene, index);
}
