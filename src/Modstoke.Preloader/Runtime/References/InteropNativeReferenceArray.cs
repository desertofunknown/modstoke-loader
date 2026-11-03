// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceArray
    {
        public static void Validate<T>(Il2CppArrayBase owner, long index, bool readOnly) where T : Il2CppObjectBase
        {
            if (owner == null) throw new NullReferenceException();
            try { ValidateLocation(owner.Pointer, index, ElementClass<T>.Pointer, readOnly); }
            finally { GC.KeepAlive(owner); }
        }

        public static T Read<T>(Il2CppArrayBase owner, int index, bool readOnly) where T : Il2CppObjectBase
        {
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                var address = scope.ReferenceArrayAddress(owner, index, ElementClass<T>.Pointer, readOnly);
                return InteropNativeReferenceIdentity.WrapReturned<T>(Marshal.ReadIntPtr(address));
            }
            finally { GC.KeepAlive(owner); }
        }

        public static void Write<T>(Il2CppArrayBase owner, int index, T value) where T : Il2CppObjectBase
        {
            using var scope = new InteropNativeReferenceScope(2);
            try
            {
                var elementClass = ElementClass<T>.Pointer;
                var address = scope.ReferenceArrayAddress(owner, index, elementClass, false);
                var pointer = value == null ? IntPtr.Zero : scope.Pin(value);
                if (pointer != IntPtr.Zero &&
                    !IL2CPP.il2cpp_class_is_assignable_from(elementClass, IL2CPP.il2cpp_object_get_class(pointer)))
                    throw new ArrayTypeMismatchException();
                IL2CPP.il2cpp_gc_wbarrier_set_field(owner.Pointer, address, pointer);
            }
            finally
            {
                GC.KeepAlive(owner);
                GC.KeepAlive(value);
            }
        }

        internal static void ValidateLocation(IntPtr pointer, long index, IntPtr elementClass, bool readOnly)
        {
            if (elementClass == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(elementClass))
                throw new NotSupportedException("The array reference requires a native reference element type.");
            var arrayClass = IL2CPP.il2cpp_object_get_class(pointer);
            if (IL2CPP.il2cpp_class_get_rank(arrayClass) != 1 ||
                IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_class_get_type(arrayClass)) != 0x1d ||
                IL2CPP.il2cpp_array_element_size(arrayClass) != IntPtr.Size)
                throw new ArrayTypeMismatchException("The reference requires a native reference-element vector.");
            if (index < 0 || (ulong)index >= IL2CPP.il2cpp_array_length(pointer))
                throw new IndexOutOfRangeException();
            if (index > int.MaxValue)
                throw new NotSupportedException("The array reference exceeds the supported wrapper index range.");
            var actualElement = IL2CPP.il2cpp_class_get_element_class(arrayClass);
            if (actualElement == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(actualElement) ||
                (readOnly ? !IL2CPP.il2cpp_class_is_assignable_from(elementClass, actualElement) : actualElement != elementClass))
                throw new ArrayTypeMismatchException("A writable array reference requires its exact native element type.");
        }

        private static class ElementClass<T> where T : Il2CppObjectBase
        {
            internal static readonly IntPtr Pointer = Resolve();

            private static IntPtr Resolve()
            {
                InteropGenericValueSupport.RequireNativeType<T>();
                var pointer = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (pointer == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(pointer))
                    throw new NotSupportedException("An array reference requires native reference identity.");
                return pointer;
            }
        }
    }
}
