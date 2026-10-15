// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using BepInEx.Configuration;

namespace BepInEx.Preloader.RuntimeFixes;

/// <summary>Applies the configured MonoMod backend selection for Harmony patches.</summary>
public static class HarmonyBackendFix
{
    private static readonly ConfigEntry<MonoModBackend> ConfigHarmonyBackend = ConfigFile.CoreConfig.Bind(
        "Preloader",
        "HarmonyBackend",
        MonoModBackend.auto,
        "Specifies which MonoMod backend to use for Harmony patches. Auto uses the best available backend.\nThis setting should only be used for development purposes (e.g. debugging in dnSpy). Other code might override this setting.");

    /// <summary>Sets MONOMOD_DMD_TYPE for an explicit backend, leaving it unchanged for auto.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The configured backend is unknown.</exception>
    public static void Initialize()
    {
        var backend = ConfigHarmonyBackend.Value;
        switch (backend)
        {
            case MonoModBackend.auto:
                break;
            case MonoModBackend.dynamicmethod:
            case MonoModBackend.methodbuilder:
            case MonoModBackend.cecil:
                Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", backend.ToString());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ConfigHarmonyBackend), backend, "Unknown backend");
        }
    }

    // Names and values are part of the existing configuration format.
    private enum MonoModBackend
    {
        [Description("Auto")]
        auto = 0,

        [Description("DynamicMethod")]
        dynamicmethod,

        [Description("MethodBuilder")]
        methodbuilder,

        [Description("Cecil")]
        cecil
    }
}
