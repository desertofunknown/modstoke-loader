// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility input dispatch adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using RedLoader;
using RedLoader.Preferences;
using SonsSdk;

namespace Modstoke.Loader;

internal sealed class InputFallback : ISdkKeybindDriver
{
    private static readonly InputFallback Driver = new();

    internal static ISdkKeybindDriver Create() => Driver;

    public ModKeybind GetKeybind(KeybindConfigEntry config)
    {
        return Keybinds.Instance.GetKeybind(config);
    }

    public void CheckAll()
    {
        // Registrations made by callbacks join the next update, not this dispatch.
        var keybinds = ModInputCache._keybindSnapshot ??= ModInputCache.Keybinds.Values.ToArray();
        foreach (var keybind in keybinds)
        {
            try
            {
                keybind.Check();
            }
            catch (Exception exception)
            {
                keybind.ReportFailure("poll", exception);
            }
        }
    }

    public void Check(ModKeybind keybind)
    {
        Keybinds.Instance.Check(keybind);
    }
}
