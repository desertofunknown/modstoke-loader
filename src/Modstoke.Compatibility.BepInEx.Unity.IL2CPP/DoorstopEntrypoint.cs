// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

namespace Doorstop;

/// <summary>Compatibility entrypoint for the Modstoke runtime.</summary>
public static class Entrypoint
{
    /// <summary>Enters compatibility startup without repeating bootstrap mode selection.</summary>
    public static void Start() => BepInEx.Unity.IL2CPP.Preloader.Run();
}
