// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;

namespace BepInEx.Unity.IL2CPP;

internal static class Il2CppInteropManager
{
    private static readonly Lazy<Services> Runtime = new(() => new Services());

    public static string GameAssemblyPath => Runtime.Value.GameAssemblyPath();
    internal static string IL2CPPInteropAssemblyPath => Runtime.Value.InteropAssemblyPath();

    public static void Initialize()
    {
        var runtime = Runtime.Value;
        runtime.InstallResolver();
        runtime.Initialize();
    }

    internal static void PreloadInteropAssemblies() => Runtime.Value.Preload();

    private sealed class Services
    {
        internal readonly Func<string> GameAssemblyPath;
        internal readonly Func<string> InteropAssemblyPath;
        internal readonly Action InstallResolver;
        internal readonly Action Initialize;
        internal readonly Action Preload;

        internal Services()
        {
            var type = Assembly.Load("Modstoke.Preloader")
                .GetType("Modstoke.Preloader.PreloaderServices", true);
            var factory = type.GetMethod("GetSharedInstance", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, "GetSharedInstance");
            var instance = factory.CreateDelegate<Func<object>>()();
            GameAssemblyPath = Bind<Func<string>>(instance, "get_GameAssemblyPath");
            InteropAssemblyPath = Bind<Func<string>>(instance, "get_InteropAssemblyPath");
            InstallResolver = Bind<Action>(instance, "InstallGameAssemblyResolver");
            Initialize = Bind<Action>(instance, "InitializeInterop");
            Preload = Bind<Action>(instance, "PreloadInteropAssemblies");
        }

        private static T Bind<T>(object instance, string name) where T : Delegate
        {
            var type = instance.GetType();
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, name);
            return method.CreateDelegate<T>(instance);
        }
    }
}
