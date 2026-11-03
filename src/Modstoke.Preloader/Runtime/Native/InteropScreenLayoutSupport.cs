// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    public static class InteropScreenLayoutSupport
    {
        public static void Fill<T>(List<T> list, Func<NativeScreenInfo, T> convert) where T : struct =>
            NativeScreenCalls.GetDisplayLayout<T>(list, convert, InteropExceptionSupport.WrapNativeException);
    }
}
