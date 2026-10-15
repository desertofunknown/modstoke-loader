// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace BepInEx.Unity.IL2CPP.Utils;

internal static class Il2CppUtils
{
    private static readonly Lazy<Func<Type, Il2CppObjectBase>> AddComponentHandler = new(() =>
    {
        var host = Assembly.Load("OpenACAI.Endnight.Loader.Core")
                           .GetType("OpenACAI.Endnight.Loader.OpenACAIUnityComponentHost", true);
        var addComponent = host.GetMethod("AddComponent", BindingFlags.NonPublic | BindingFlags.Static,
                                          null, new[] { typeof(Type) }, null)
                           ?? throw new MissingMethodException(host.FullName, "AddComponent");
        return (Func<Type, Il2CppObjectBase>)addComponent.CreateDelegate(typeof(Func<Type, Il2CppObjectBase>));
    });

    public static T AddComponent<T>() where T : Il2CppObjectBase => AddComponent(typeof(T)).Cast<T>();

    public static Il2CppObjectBase AddComponent(Type t) => AddComponentHandler.Value(t);
}
