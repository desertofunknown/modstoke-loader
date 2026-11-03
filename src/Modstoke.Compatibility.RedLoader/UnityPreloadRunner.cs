// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.IO;
using System.Reflection;

namespace RedLoader.Unity.IL2CPP;

internal static class UnityPreloaderRunner
{
    private const string OwnerAssemblyName = "Modstoke.Preloader";
    private static readonly Lazy<Type> Owner = new(LoadOwner);
    private static readonly Lazy<Action> StartOwned = new(() => Bind<Action>("Start"));
    private static readonly Lazy<Action> RunOwned = new(() => Bind<Action>("PreloaderMain"));
    private static readonly Lazy<ResolveEventHandler> ResolveOwned = new(() => Bind<ResolveEventHandler>("LocalResolve"));
    private static readonly Lazy<Action> InitReshadeOwned = new(() => Bind<Action>("InitReshade"));

    internal static void Start() => StartOwned.Value();

    public static void PreloaderMain() => RunOwned.Value();

    internal static Assembly LocalResolve(object sender, ResolveEventArgs args) => ResolveOwned.Value(sender, args);

    internal static void InitReshade() => InitReshadeOwned.Value();

    private static T Bind<T>(string name) where T : Delegate
    {
        var type = Owner.Value;
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, name);
        return (T)method.CreateDelegate(typeof(T));
    }

    private static Type LoadOwner()
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(OwnerAssemblyName);
        }
        catch (FileNotFoundException error) when (error.FileName != null &&
                                                new AssemblyName(error.FileName).Name == OwnerAssemblyName)
        {
            // Doorstop may enter this facade before any managed resolver has been installed.
            var processPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH") ?? Environment.ProcessPath;
            var root = Path.GetDirectoryName(processPath);
            var runtime = Path.Combine(root, "Modstoke", "Runtime");
            if (!Directory.Exists(runtime))
                runtime = Path.Combine(root, "OpenACAI", "Runtime");
            assembly = Assembly.LoadFrom(Path.Combine(runtime, OwnerAssemblyName + ".dll"));
        }

        return assembly.GetType("Modstoke.Preloader.RedStartup", true);
    }
}
