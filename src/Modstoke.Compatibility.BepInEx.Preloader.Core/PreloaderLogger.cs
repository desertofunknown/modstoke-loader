// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using BepInEx.Logging;

namespace BepInEx.Preloader.Core;

/// <summary>Provides the shared preloader logging source.</summary>
public static class PreloaderLogger
{
    /// <summary>Gets the registered log source named Preloader.</summary>
    public static ManualLogSource Log { get; } = Logger.CreateLogSource("Preloader");
}
