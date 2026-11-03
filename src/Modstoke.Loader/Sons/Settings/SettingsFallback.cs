// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility settings handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using RedLoader;
using SUI;

namespace Modstoke.Loader;

internal sealed partial class SettingsFallback : ISdkSettingsFallbackDriver
{
    private static readonly SettingsFallback Driver = new();

    internal static ISdkSettingsFallbackDriver Create() => Driver;

    public void Create(ModBase mod, object settingsObject, Type settingsType,
        bool changesNeedRestart, Action callback, SContainerOptions userContent)
    {
        SettingsRegistration.Instance.Create(mod, settingsObject, settingsType,
            changesNeedRestart, callback, userContent);
    }
}
