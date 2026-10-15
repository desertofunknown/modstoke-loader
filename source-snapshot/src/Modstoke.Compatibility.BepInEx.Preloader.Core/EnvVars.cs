// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;

namespace BepInEx.Preloader.Core;

/// <summary>Exposes the Doorstop environment captured during preloader initialization.</summary>
public static class EnvVars
{
    /// <summary>Gets the assembly path configured as Doorstop's invocation target.</summary>
    public static string DOORSTOP_INVOKE_DLL_PATH { get; private set; }

    /// <summary>Gets the game's managed assembly directory.</summary>
    public static string DOORSTOP_MANAGED_FOLDER_DIR { get; private set; }

    /// <summary>Gets the path of the running game executable.</summary>
    public static string DOORSTOP_PROCESS_PATH { get; private set; }

    /// <summary>Gets Mono's initial assembly search directories, preserving empty path entries.</summary>
    public static string[] DOORSTOP_DLL_SEARCH_DIRS { get; private set; }

    /// <summary>Gets the library path supplying Mono's native imports.</summary>
    public static string DOORSTOP_MONO_LIB_PATH { get; private set; }

    internal static void LoadVars()
    {
        DOORSTOP_INVOKE_DLL_PATH = Environment.GetEnvironmentVariable(nameof(DOORSTOP_INVOKE_DLL_PATH));
        DOORSTOP_MANAGED_FOLDER_DIR = Environment.GetEnvironmentVariable(nameof(DOORSTOP_MANAGED_FOLDER_DIR));
        DOORSTOP_PROCESS_PATH = Environment.GetEnvironmentVariable(nameof(DOORSTOP_PROCESS_PATH));
        DOORSTOP_MONO_LIB_PATH = Environment.GetEnvironmentVariable(nameof(DOORSTOP_MONO_LIB_PATH));

        var searchPaths = Environment.GetEnvironmentVariable(nameof(DOORSTOP_DLL_SEARCH_DIRS));
        DOORSTOP_DLL_SEARCH_DIRS = searchPaths == null ? new string[0] : searchPaths.Split(Path.PathSeparator);
    }
}
