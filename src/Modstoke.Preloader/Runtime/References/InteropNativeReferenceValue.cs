// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceValue
    {
        public static IntPtr ArgumentAddress<T>(InteropNativeReferenceScope scope, InteropReferenceLocation location)
        {
            ArgumentNullException.ThrowIfNull(scope);
            // The caller retains this scope until the synchronous native invocation finishes.
            return scope.WritableAddress(location, ValueClass<T>.Pointer);
        }

        public static InteropReferenceLocation Rectangular<TArray, T>(Il2CppObjectBase owner, long index, int rank)
        {
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                if (owner == null) throw new NullReferenceException();
                if (rank < 1 || rank > 32)
                    throw new NotSupportedException("A rectangular reference requires a rank from 1 through 32.");
                var expected = Il2CppClassPointerStore<TArray>.NativeClassPtr;
                if (expected == IntPtr.Zero || owner.ObjectClass != expected)
                    throw new ArrayTypeMismatchException("The rectangular reference requires the exact native array class.");
                var location = new InteropReferenceLocation(owner, index, rank);
                scope.WritableAddress(location, ValueClass<T>.Pointer);
                return location;
            }
            finally { GC.KeepAlive(owner); }
        }

        public static InteropReferenceLocation Field<TParent, T>(InteropReferenceLocation parent, string fieldPointerName)
            => Field<TParent, T>(parent, fieldPointerName, false);

        public static InteropReferenceLocation Field<TParent, T>(InteropReferenceLocation parent, string fieldPointerName, bool isReadOnly)
        {
            ArgumentNullException.ThrowIfNull(parent);
            var field = InteropNativeReferenceCall.ReadMetadataPointer(typeof(TParent), fieldPointerName);
            if (IL2CPP.il2cpp_field_get_parent(field) != ValueClass<TParent>.Pointer)
                throw new NotSupportedException("The nested field does not belong to the exact native containing value.");
            var location = new InteropReferenceLocation(parent, typeof(TParent), fieldPointerName,
                isReadOnly || (IL2CPP.il2cpp_field_get_flags(field) & 0x20) != 0);
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                if (location.IsReadOnly) scope.ReadAddress(location, ValueClass<T>.Pointer);
                else scope.WritableAddress(location, ValueClass<T>.Pointer);
                return location;
            }
            finally { GC.KeepAlive(parent); }
        }

        public static unsafe T Read<T>(InteropReferenceLocation location)
        {
            using var scope = new InteropNativeReferenceScope(1);
            try { return Unsafe.ReadUnaligned<T>((void*)scope.ReadAddress(location, ValueClass<T>.Pointer)); }
            finally { GC.KeepAlive(location); }
        }

        public static unsafe void Write<T>(InteropReferenceLocation location, T value)
        {
            using var scope = new InteropNativeReferenceScope(1);
            try { Unsafe.WriteUnaligned((void*)scope.WritableAddress(location, ValueClass<T>.Pointer), value); }
            finally { GC.KeepAlive(location); }
        }

        private static class ValueClass<T>
        {
            internal static readonly IntPtr Pointer = Resolve();

            private static IntPtr Resolve()
            {
                var type = typeof(T);
                var pointer = Il2CppClassPointerStore<T>.NativeClassPtr;
                uint alignment = 0;
                if (!type.IsValueType || type.IsGenericType || Nullable.GetUnderlyingType(type) != null ||
                    RuntimeHelpers.IsReferenceOrContainsReferences<T>() || pointer == IntPtr.Zero ||
                    !IL2CPP.il2cpp_class_is_valuetype(pointer) || IL2CPP.il2cpp_class_has_references(pointer) ||
                    !(type.IsPrimitive || type.IsEnum || type.IsLayoutSequential || type.IsExplicitLayout) ||
                    IL2CPP.il2cpp_class_value_size(pointer, ref alignment) != Unsafe.SizeOf<T>())
                    throw new NotSupportedException("The scoped reference requires matching reference-free CLR/native value layouts: " + type.FullName);
                return pointer;
            }
        }
    }
}
