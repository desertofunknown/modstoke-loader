// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        // Size queries do not grant permission to expose the storage as a raw buffer.
        public static int SizeOf<T>() => TypeSize<T>.Value;
        public static int NativeValueSize<T>() => NativeValueTypeSize<T>.Value;

        private static class TypeSize<T>
        {
            internal static readonly int Value = Resolve();

            private static int Resolve()
            {
                if (!typeof(Il2CppObjectBase).IsAssignableFrom(typeof(T)))
                    return Unsafe.SizeOf<T>();
                return ArgumentKind<T>() == 1 ? NativeValueSize<T>() : IntPtr.Size;
            }
        }

        private static class NativeValueTypeSize<T>
        {
            internal static readonly int Value = Resolve();

            private static int Resolve()
            {
                if (ArgumentKind<T>() == 2)
                    throw new NotSupportedException("A native payload size requires a value type: " + typeof(T).FullName + ".");
                uint alignment = 0;
                var size = IL2CPP.il2cpp_class_value_size(NativeType<T>.Class, ref alignment);
                return size > 0 ? size :
                    throw new NotSupportedException("The native value has no valid payload size: " + typeof(T).FullName + ".");
            }
        }
    }
}
