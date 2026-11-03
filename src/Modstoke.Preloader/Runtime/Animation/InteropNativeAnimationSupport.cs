// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Public because restored AnimationScriptPlayable methods call these CLR entry points.
    public static class InteropNativeAnimationSupport
    {
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory = InteropExceptionSupport.WrapNativeException;

        public static T Read<T>(IntPtr handle) where T : struct =>
            NativeAnimationCalls.ReadJobData<T>(handle, ExceptionFactory);

        public static void Write<T>(IntPtr handle, in T value) where T : struct =>
            NativeAnimationCalls.WriteJobData(handle, in value, ExceptionFactory);
    }
}
