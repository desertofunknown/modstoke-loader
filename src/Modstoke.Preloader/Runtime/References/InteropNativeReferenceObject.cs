// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceObject
    {
        public static T Read<T>(InteropReferenceLocation location) where T : Il2CppObjectBase
        {
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                var address = scope.ReferenceAddress(location, ReferenceClass<T>.Pointer, writable: false);
                return InteropNativeReferenceIdentity.WrapReturned<T>(Marshal.ReadIntPtr(address));
            }
            finally { GC.KeepAlive(location); }
        }

        public static void Write<T>(InteropReferenceLocation location, T value) where T : Il2CppObjectBase
        {
            using var scope = new InteropNativeReferenceScope(2);
            try
            {
                var elementClass = ReferenceClass<T>.Pointer;
                var address = scope.ReferenceAddress(location, elementClass);
                var pointer = value == null ? IntPtr.Zero : scope.Pin(value);
                if (pointer != IntPtr.Zero &&
                    !IL2CPP.il2cpp_class_is_assignable_from(elementClass, IL2CPP.il2cpp_object_get_class(pointer)))
                    throw new InvalidCastException("The value is not assignable to the native reference field.");
                // Static storage has no instance owner; the barrier marks the destination slot.
                var owner = IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)location.Owner);
                IL2CPP.il2cpp_gc_wbarrier_set_field(owner, address, pointer);
            }
            finally
            {
                GC.KeepAlive(location);
                GC.KeepAlive(value);
            }
        }

        private static class ReferenceClass<T> where T : Il2CppObjectBase
        {
            internal static readonly IntPtr Pointer = Resolve();

            private static IntPtr Resolve()
            {
                InteropGenericValueSupport.RequireNativeType<T>();
                var pointer = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (pointer == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(pointer))
                    throw new NotSupportedException("The field requires a native reference type.");
                return pointer;
            }
        }
    }
}
