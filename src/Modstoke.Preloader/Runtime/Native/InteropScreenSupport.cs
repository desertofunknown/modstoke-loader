// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Called by restored CoreModule methods; Unity storage never contains CLR references.
    public static class InteropScreenSupport
    {
        public static NativeScreenInfo Read() =>
            NativeScreenCalls.GetMainWindowDisplayInfo(InteropExceptionSupport.WrapNativeException);

        public static T Move<T>(NativeScreenInfo display, int x, int y) where T : Il2CppObjectBase =>
            NativeScreenCalls.MoveMainWindow<T>(display, x, y, InteropExceptionSupport.WrapNativeException);
    }
}
