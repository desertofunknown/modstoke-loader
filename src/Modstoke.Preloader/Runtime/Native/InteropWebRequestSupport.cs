// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static class InteropWebRequestSupport
    {
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory = InteropExceptionSupport.WrapNativeException;

        public static void InitializeOwned(Il2CppObjectBase value) =>
            NativeWebRequestConstruction.InitializeOwned(value, ExceptionFactory);
    }
}
