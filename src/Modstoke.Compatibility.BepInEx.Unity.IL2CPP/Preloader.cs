// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using BepInEx.Logging;
using BepInEx.Preloader.Core;

namespace BepInEx.Unity.IL2CPP;

/// <summary>Legacy startup entrypoint hosted by Modstoke.</summary>
public static class Preloader
{
    private static readonly Lazy<Action> StartCompatibility = new(() =>
    {
        var bootstrap = Assembly.Load("Modstoke.Bootstrap").GetType("Doorstop.Entrypoint", true);
        var start = bootstrap.GetMethod("RunCompatibility", BindingFlags.Public | BindingFlags.Static,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException(bootstrap.FullName, "RunCompatibility");
        return start.CreateDelegate<Action>();
    });

    // Bootstrap publishes the compatibility instance through this reflected property.
    private static IL2CPPChainloader Chainloader { get; set; }
    internal static ManualLogSource Log => PreloaderLogger.Log;

    /// <summary>Starts the owned preloader with compatibility plugin execution.</summary>
    public static void Run() => StartCompatibility.Value();
}
