// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        // Buffer consumers read bytes; CLR values do not need an IL2CPP identity.
        public static int ValueBufferKind<T>() => ValueBufferType<T>.Kind;
        public static int ValueBufferSize<T>() => ValueBufferType<T>.Size;
        public static int UnmanagedValueBufferKind<T>() => UnmanagedValueBufferType<T>.Kind;

        private static class UnmanagedValueBufferType<T>
        {
            internal static readonly int Kind = Resolve();

            private static int Resolve()
            {
                var kind = ValueBufferType<T>.Kind;
                if (kind != 0 && IL2CPP.il2cpp_class_has_references(NativeType<T>.Class))
                    throw new NotSupportedException("An unmanaged value buffer cannot contain native references.");
                return kind;
            }
        }

        private static class ValueBufferType<T>
        {
            internal static readonly int Kind;
            internal static readonly int Size;

            static ValueBufferType()
            {
                if (typeof(T).IsValueType)
                {
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                        throw new NotSupportedException("A CLR value buffer cannot contain managed references.");
                    Kind = 0;
                    Size = Unsafe.SizeOf<T>();
                    return;
                }
                Size = NativeValueSize<T>();
                Kind = 1;
            }
        }
    }
}
