// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceStorage
    {
        public static InteropReferenceLocation Validate<T>(InteropReferenceLocation location)
        {
            ArgumentNullException.ThrowIfNull(location);
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                var elementClass = NativeClass<T>.Pointer;
                if (IL2CPP.il2cpp_class_is_valuetype(elementClass))
                    scope.StorageAddress(location, elementClass, !location.IsReadOnly);
                else scope.ReferenceAddress(location, elementClass, !location.IsReadOnly);
                return location;
            }
            finally { GC.KeepAlive(location); }
        }

        public static InteropReferenceLocation Field<TParent, T>(InteropReferenceLocation parent,
            string fieldPointerName, bool isReadOnly)
        {
            ArgumentNullException.ThrowIfNull(parent);
            var parentClass = NativeClass<TParent>.Pointer;
            var elementClass = NativeClass<T>.Pointer;
            var field = InteropNativeReferenceCall.ReadMetadataPointer(typeof(TParent), fieldPointerName);
            if (!IL2CPP.il2cpp_class_is_valuetype(parentClass) || IL2CPP.il2cpp_field_get_parent(field) != parentClass)
                throw new NotSupportedException("The nested field requires its exact containing native value type.");
            var location = new InteropReferenceLocation(parent, typeof(TParent), fieldPointerName,
                isReadOnly || (IL2CPP.il2cpp_field_get_flags(field) & 0x20) != 0);
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                if (IL2CPP.il2cpp_class_is_valuetype(elementClass))
                    scope.StorageAddress(location, elementClass, !location.IsReadOnly);
                else scope.ReferenceAddress(location, elementClass, !location.IsReadOnly);
                return location;
            }
            finally { GC.KeepAlive(parent); }
        }

        public static InteropReferenceLocation Rectangular<TArray, T>(Il2CppObjectBase owner, long index, int rank)
        {
            if (owner == null) throw new NullReferenceException();
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                if (rank < 1 || rank > 32 || owner.ObjectClass != NativeClass<TArray>.Pointer)
                    throw new ArrayTypeMismatchException("The reference requires its exact rectangular native array class.");
                var location = new InteropReferenceLocation(owner, index, rank);
                scope.StorageAddress(location, NativeClass<T>.Pointer, writable: true);
                return location;
            }
            finally { GC.KeepAlive(owner); }
        }

        private static class NativeClass<T>
        {
            internal static readonly IntPtr Pointer = Resolve();

            private static IntPtr Resolve()
            {
                InteropGenericValueSupport.RequireNativeType<T>();
                var pointer = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (pointer == IntPtr.Zero) throw new NotSupportedException("The storage type has no native identity.");
                return pointer;
            }
        }
    }
}
