// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;

namespace RedLoader.Unity.IL2CPP;

/// <summary>Compatibility entry point for the Modstoke-owned RedLoader preloader.</summary>
public static class Preloader
{
    private static readonly Lazy<Action> RunOwned = new(() =>
    {
        var owner = Assembly.Load("Modstoke.Preloader")
            .GetType("Modstoke.Preloader.RedPreloader", true);
        var method = owner.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(owner.FullName, "Run");
        return (Action)method.CreateDelegate(typeof(Action));
    });

    /// <summary>Runs the standalone compatibility preloader using its existing startup settings.</summary>
    public static void Run() => RunOwned.Value();
}
