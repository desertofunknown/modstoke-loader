// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility game manager access adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using SonsSdk;
using UnityEngine;

namespace Modstoke.Loader;

internal sealed class SdkGameManagerFallback : ISdkGameManagerDriver
{
    private static readonly SdkGameManagerFallback Driver = new();

    internal static ISdkGameManagerDriver Create() => Driver;

    public void Initialize()
    {
        SdkEvents.OnGameActivated.Subscribe(global::SonsSdk.GameManagers.Create);
    }

    void ISdkGameManagerDriver.Create()
    {
        global::SonsSdk.GameManagers._instance = new global::SonsSdk.GameManagers();
    }

    public T GetManager<T>(string managerName) where T : Component
    {
        var managers = global::SonsSdk.GameManagers._instance;
        if (managers == null || !managers._gameManagersTr)
            return null;

        var manager = managers._gameManagersTr.Find(managerName);
        return manager ? manager.GetComponent<T>() : null;
    }
}
