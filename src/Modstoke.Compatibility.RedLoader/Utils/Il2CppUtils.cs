// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace RedLoader.Unity.IL2CPP.Utils;

internal static class Il2CppUtils
{
    private static readonly Lazy<Func<Type, Il2CppObjectBase>> AddComponentHandler = new(() =>
    {
        var host = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.UnityComponentHost", true);
        var method = host.GetMethod("AddRedLoaderComponent", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(Type) }, null)
            ?? throw new MissingMethodException(host.FullName, "AddRedLoaderComponent");
        return method.CreateDelegate<Func<Type, Il2CppObjectBase>>();
    });

    public static Il2CppObjectBase AddComponent(Type t) => AddComponentHandler.Value(t);
}
