// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropNativeValueStorage
    {
        public static IntPtr PinArgument<T>(ref T location) where T : Il2CppObjectBase
        {
            var value = Receiver(ref location);
            try
            {
                var handle = IL2CPP.il2cpp_gchandle_new(value.Pointer, true);
                if (handle == IntPtr.Zero) throw new InvalidOperationException("The native value argument could not be pinned.");
                return handle;
            }
            finally { GC.KeepAlive(value); }
        }

        public static IntPtr PinnedArgumentAddress(IntPtr handle)
        {
            var pointer = handle == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_gchandle_get_target(handle);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("The pinned native value argument is unavailable.");
            return IL2CPP.il2cpp_object_unbox(pointer);
        }

        public static void ReleaseArgument(IntPtr handle)
        {
            if (handle != IntPtr.Zero) IL2CPP.il2cpp_gchandle_free(handle);
        }
    }
}
