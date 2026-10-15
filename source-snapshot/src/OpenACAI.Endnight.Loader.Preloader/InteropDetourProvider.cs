// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from BepInEx's Il2CppInteropDetourProvider.cs, copyright BepInEx contributors.

using System;
using Il2CppInterop.Runtime.Injection;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropDetourProvider : IDetourProvider
    {
        public IDetour Create<T>(IntPtr original, T target) where T : Delegate =>
            NativeDetourFactory.Create(original, target);
    }
}
