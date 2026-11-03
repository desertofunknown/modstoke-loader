// SPDX-License-Identifier: GPL-3.0-or-later
// Component hosting adapted from BepInEx and RedLoader's Il2CppUtils.
// Copyright (c) BepInEx and RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static class UnityComponentHost
    {
        private static GameObject managerGo;
        private static GameObject redLoaderManagerGo;

        internal static Il2CppObjectBase AddComponent(Type t)
        {
            return AddComponentToHost(ref managerGo, "BepInEx_Manager", t);
        }

        internal static Il2CppObjectBase AddRedLoaderComponent(Type t)
        {
            return AddComponentToHost(ref redLoaderManagerGo, "Redloader_Manager", t);
        }

        private static Il2CppObjectBase AddComponentToHost(ref GameObject host, string hostName, Type t)
        {
            if (host == null)
                host = new GameObject { hideFlags = HideFlags.HideAndDontSave, name = hostName };

            if (!ClassInjector.IsTypeRegisteredInIl2Cpp(t))
                ClassInjector.RegisterTypeInIl2Cpp(t);

            return host.AddComponent(Il2CppType.From(t));
        }
    }
}
