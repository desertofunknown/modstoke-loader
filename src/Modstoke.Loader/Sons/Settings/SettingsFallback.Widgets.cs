// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility settings handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using RedLoader;
using SUI;
using SettingsConfigEntry = SUI.SettingsRegistry.SettingsConfigEntry;

namespace Modstoke.Loader;

internal sealed partial class SettingsFallback
{
    public void GenerateUi(ModBase mod, object settingsObject, Type settingsType,
        SContainerOptions container, List<SettingsConfigEntry> outConfigList)
    {
        SettingsRegistration.Instance.GenerateUi(mod, settingsObject, settingsType, container, outConfigList);
    }
}
