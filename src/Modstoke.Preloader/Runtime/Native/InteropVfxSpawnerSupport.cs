// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Generated constructors call this facade to preserve native exception identity.
    public static class InteropVfxSpawnerSupport
    {
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory = InteropExceptionSupport.WrapNativeException;

        public static void InitializeOwned(Il2CppObjectBase value) =>
            NativeVfxSpawnerState.InitializeOwned(value, ExceptionFactory);
    }
}
