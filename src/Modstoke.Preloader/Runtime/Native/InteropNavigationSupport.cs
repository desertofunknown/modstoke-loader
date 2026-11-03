// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Preloader
{
    // Public because the restored AIModule calls this helper.
    public static class InteropNavigationSupport
    {
        public static void CalculateTriangulation<TVector>(out Il2CppStructArray<TVector> vertices,
            out Il2CppStructArray<int> indices, out Il2CppStructArray<int> areas) where TVector : unmanaged =>
            NativeNavigationCalls.CalculateTriangulation<TVector>(out vertices, out indices, out areas,
                InteropExceptionSupport.WrapNativeException);
    }
}
