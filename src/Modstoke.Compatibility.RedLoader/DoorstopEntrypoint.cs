// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using RedLoader.Unity.IL2CPP;

// ReSharper disable once CheckNamespace
namespace Doorstop;

internal static class Entrypoint
{
    /// <summary>Enters the Modstoke-owned standalone compatibility startup.</summary>
    public static void Start() => UnityPreloaderRunner.Start();
}
