// SPDX-License-Identifier: GPL-3.0-or-later
// Component hosting adapted from BepInEx's Il2CppUtils.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIUnityComponentHost
    {
        private static GameObject managerGo;

        internal static Il2CppObjectBase AddComponent(Type t)
        {
            if (managerGo == null)
                managerGo = new GameObject { hideFlags = HideFlags.HideAndDontSave, name = "BepInEx_Manager" };

            if (!ClassInjector.IsTypeRegisteredInIl2Cpp(t))
                ClassInjector.RegisterTypeInIl2Cpp(t);

            return managerGo.AddComponent(Il2CppType.From(t));
        }
    }
}
