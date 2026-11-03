// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Preloader
{
    public static class InteropShaderWarmupSupport
    {
        public static void Warmup<TVertex>(Il2CppObjectBase shader, Il2CppStructArray<TVertex> vertices)
            where TVertex : unmanaged =>
            NativeShaderWarmupCalls.Warmup(shader, vertices, InteropExceptionSupport.WrapNativeException);

        public static void WarmupFromCollection<TVertex>(Il2CppObjectBase collection, Il2CppObjectBase shader,
            Il2CppStructArray<TVertex> vertices) where TVertex : unmanaged =>
            NativeShaderWarmupCalls.WarmupFromCollection(collection, shader, vertices,
                InteropExceptionSupport.WrapNativeException);
    }
}
