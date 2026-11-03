// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceAggregate
    {
        public static InteropReferenceLocation Validate<T>(InteropReferenceLocation location) where T : Il2CppObjectBase
        {
            ArgumentNullException.ThrowIfNull(location);
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                if (IsStaticField(location)) StaticField<T>(location, !location.IsReadOnly);
                else scope.StorageAddress(location, ValueClass<T>.Pointer, !location.IsReadOnly);
                return location;
            }
            finally { GC.KeepAlive(location); }
        }

        public static unsafe T Read<T>(InteropReferenceLocation location) where T : Il2CppObjectBase
        {
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                if (IsStaticField(location))
                {
                    var field = StaticField<T>(location, writable: false);
                    var value = ZeroBox<T>();
                    var pointer = scope.Pin(value);
                    IL2CPP.il2cpp_field_static_get_value(field, (void*)IL2CPP.il2cpp_object_unbox(pointer));
                    return value;
                }
                var address = scope.StorageAddress(location, ValueClass<T>.Pointer, writable: false);
                return Box<T>(address);
            }
            finally { GC.KeepAlive(location); }
        }

        public static unsafe void Write<T>(InteropReferenceLocation location, T value) where T : Il2CppObjectBase
        {
            ArgumentNullException.ThrowIfNull(location);
            if (location.Parent != null)
                throw new NotSupportedException("A boxed aggregate cannot be assigned to an arbitrary nested native address.");
            using var scope = new InteropNativeReferenceScope(2);
            try
            {
                var staticField = IsStaticField(location) ? StaticField<T>(location, writable: true) : IntPtr.Zero;
                if (staticField == IntPtr.Zero)
                    scope.StorageAddress(location, ValueClass<T>.Pointer, writable: true);
                if (value == null && ValueClass<T>.IsNullable) value = ZeroBox<T>();
                var pointer = scope.Pin(value);
                if (IL2CPP.il2cpp_object_get_class(pointer) != ValueClass<T>.Pointer)
                    throw new InvalidCastException("The boxed value must have the exact native storage type.");
                if (staticField != IntPtr.Zero)
                {
                    IL2CPP.il2cpp_field_static_set_value(staticField, (void*)IL2CPP.il2cpp_object_unbox(pointer));
                    return;
                }
                var owner = (Il2CppObjectBase)location.Owner;
                if (location.FieldPointerName != null)
                    IL2CPP.il2cpp_field_set_value(owner.Pointer, Field(location),
                        (void*)IL2CPP.il2cpp_object_unbox(pointer));
                else
                    WriteArray<T>(owner.Pointer, location, pointer);
            }
            finally
            {
                GC.KeepAlive(location);
                GC.KeepAlive(value);
            }
        }

        public static unsafe void Initialize<T>(InteropReferenceLocation location) where T : Il2CppObjectBase
        {
            ArgumentNullException.ThrowIfNull(location);
            if (location.Parent != null)
                throw new NotSupportedException("An arbitrary nested aggregate has no native initialization contract.");
            using var scope = new InteropNativeReferenceScope(2);
            try
            {
                // Native field setters explicitly zero an aggregate for a null payload pointer.
                if (IsStaticField(location))
                {
                    IL2CPP.il2cpp_field_static_set_value(StaticField<T>(location, writable: true), null);
                    return;
                }
                scope.StorageAddress(location, ValueClass<T>.Pointer, writable: true);
                var owner = (Il2CppObjectBase)location.Owner;
                if (location.FieldPointerName != null)
                {
                    IL2CPP.il2cpp_field_set_value(owner.Pointer, Field(location), null);
                    return;
                }
                // Array assignment still requires a real zeroed value box, not a null wrapper.
                var value = ZeroBox<T>();
                try { WriteArray<T>(owner.Pointer, location, scope.Pin(value)); }
                finally { GC.KeepAlive(value); }
            }
            finally { GC.KeepAlive(location); }
        }

        public static unsafe void Copy<T>(InteropReferenceLocation destination, InteropReferenceLocation source)
            where T : Il2CppObjectBase
        {
            using var scope = new InteropNativeReferenceScope(2);
            try
            {
                var nativeClass = ValueClass<T>.Pointer;
                if (IsStaticField(destination))
                {
                    var field = StaticField<T>(destination, writable: true);
                    if (IsStaticField(source))
                    {
                        if (StaticField<T>(source, writable: false) == field) return;
                        var staticSnapshot = Read<T>(source);
                        try { Write(destination, staticSnapshot); }
                        finally { GC.KeepAlive(staticSnapshot); }
                    }
                    else
                    {
                        var address = scope.StorageAddress(source, nativeClass, writable: false);
                        IL2CPP.il2cpp_field_static_set_value(field, (void*)address);
                    }
                    return;
                }
                var target = scope.StorageAddress(destination, nativeClass, writable: true);
                if (IsStaticField(source))
                {
                    // Static storage stays with the runtime; only the destination is an interior pointer.
                    IL2CPP.il2cpp_field_static_get_value(StaticField<T>(source, writable: false), (void*)target);
                    return;
                }
                var origin = scope.StorageAddress(source, nativeClass, writable: false);
                if (target == origin) return;
                var distance = target.ToInt64() > origin.ToInt64()
                    ? (ulong)target.ToInt64() - (ulong)origin.ToInt64()
                    : (ulong)origin.ToInt64() - (ulong)target.ToInt64();
                if (distance >= (ulong)ValueClass<T>.Size)
                {
                    // These native APIs own the aggregate copy and its destination barrier.
                    // Their object arguments always name real owners, never adjusted headers.
                    if (source.Parent == null && source.FieldPointerName != null)
                    {
                        IL2CPP.il2cpp_field_get_value(((Il2CppObjectBase)source.Owner).Pointer, Field(source), (void*)target);
                        return;
                    }
                    if (destination.Parent == null && destination.FieldPointerName != null)
                    {
                        IL2CPP.il2cpp_field_set_value(((Il2CppObjectBase)destination.Owner).Pointer, Field(destination), (void*)origin);
                        return;
                    }
                }
                if (destination.Parent != null)
                    throw new NotSupportedException("A nested aggregate copy requires a nonoverlapping real source field.");
                // Arrays require a box; it also prevents partial overlap in explicit-layout fields.
                var snapshot = Box<T>(origin);
                try { Write(destination, snapshot); }
                finally { GC.KeepAlive(snapshot); }
            }
            finally
            {
                GC.KeepAlive(destination);
                GC.KeepAlive(source);
            }
        }

        private static T Box<T>(IntPtr address) where T : Il2CppObjectBase
        {
            if (ValueClass<T>.IsNullable)
                return InteropNativeValueStorage.CopyFromAddress<T>(address);
            var pointer = IL2CPP.il2cpp_value_box(ValueClass<T>.Pointer, address);
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            return InteropNativeReferenceIdentity.WrapReturned<T>(pointer);
        }

        private static T ZeroBox<T>() where T : Il2CppObjectBase
        {
            var pointer = IL2CPP.il2cpp_object_new(ValueClass<T>.Pointer);
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            return InteropNativeReferenceIdentity.WrapReturned<T>(pointer);
        }

        private static bool IsStaticField(InteropReferenceLocation location) =>
            location != null && location.Owner == null && location.Parent == null && location.Index == 0 &&
            location.RectangularRank == 0 && location.FieldOwner != null && location.FieldPointerName != null;

        private static IntPtr StaticField<T>(InteropReferenceLocation location, bool writable) where T : Il2CppObjectBase
        {
            if (IntPtr.Size != 8 || writable && location.IsReadOnly)
                throw new NotSupportedException("The static native field requires x64 storage and the requested access.");
            var field = Field(location);
            var flags = IL2CPP.il2cpp_field_get_flags(field);
            if ((flags & 0x50) != 0x10 || writable && (flags & 0x20) != 0 ||
                IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) != ValueClass<T>.Pointer)
                throw new NotSupportedException("The static field must have the exact native value type and requested access.");
            // Field accessors initialize layout, not the native type constructor.
            IL2CPP.il2cpp_runtime_class_init(IL2CPP.il2cpp_field_get_parent(field));
            return field;
        }

        private static IntPtr Field(InteropReferenceLocation location) =>
            InteropNativeReferenceCall.ReadMetadataPointer(location.FieldOwner, location.FieldPointerName);

        private static unsafe void WriteArray<T>(IntPtr owner, InteropReferenceLocation location, IntPtr value)
            where T : Il2CppObjectBase
        {
            var index = checked((int)(location.RectangularRank == 0 ? location.Index : location.LinearIndex));
            if (ValueClass<T>.IsNullable)
            {
                // System.Array accepts a boxed underlying value or null, not the
                // full Nullable payload that our mutable value wrapper retains.
                var pointer = IL2CPP.il2cpp_value_box(ValueClass<T>.Pointer, IL2CPP.il2cpp_object_unbox(value));
                var boxed = pointer == IntPtr.Zero ? null : new Il2CppObjectBase(pointer);
                using var scope = new InteropNativeReferenceScope(1);
                try { StoreArrayValue(owner, index, boxed == null ? IntPtr.Zero : scope.Pin(boxed)); }
                finally { GC.KeepAlive(boxed); }
                return;
            }
            StoreArrayValue(owner, index, value);
        }

        private static unsafe void StoreArrayValue(IntPtr owner, int index, IntPtr value)
        {
            IntPtr* arguments = stackalloc IntPtr[2];
            arguments[0] = value;
            arguments[1] = (IntPtr)(&index);
            var exception = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(ArrayStore.Method.Value, owner, (void**)arguments, ref exception);
            InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
        }

        private static class ArrayStore
        {
            internal static readonly Lazy<IntPtr> Method = new(Resolve);

            private static IntPtr Resolve()
            {
                var image = IL2CPP.il2cpp_get_corlib();
                var owner = IL2CPP.il2cpp_class_from_name(image, "System", "Array");
                var objectClass = IL2CPP.il2cpp_class_from_name(image, "System", "Object");
                if (owner == IntPtr.Zero || objectClass == IntPtr.Zero)
                    throw new MissingMethodException("The native core array contract is unavailable.");
                var method = IL2CPP.il2cpp_class_get_method_from_name(owner, "SetValueImpl", 2);
                uint flags = 0;
                if (method == IntPtr.Zero || IL2CPP.il2cpp_method_is_generic(method) ||
                    IL2CPP.il2cpp_method_get_class(method) != owner ||
                    (IL2CPP.il2cpp_method_get_flags(method, ref flags) & 0x10) != 0 ||
                    IL2CPP.il2cpp_method_get_param_count(method) != 2 ||
                    IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_method_get_return_type(method)) != 0x01)
                    throw new MissingMethodException("The native array value-store method is not an exact instance contract.");
                var value = IL2CPP.il2cpp_method_get_param(method, 0);
                var index = IL2CPP.il2cpp_method_get_param(method, 1);
                if (IL2CPP.il2cpp_type_is_byref(value) || IL2CPP.il2cpp_type_is_byref(index) ||
                    IL2CPP.il2cpp_class_from_type(value) != objectClass ||
                    IL2CPP.il2cpp_type_get_type(index) != 0x08)
                    throw new MissingMethodException("The native array value-store signature differs from Object, Int32.");
                return method;
            }
        }

        private static class ValueClass<T> where T : Il2CppObjectBase
        {
            internal static readonly IntPtr Pointer = Resolve();
            internal static readonly int Size = GetSize();
            internal static readonly bool IsNullable = IL2CPP.il2cpp_class_get_image(Pointer) == IL2CPP.il2cpp_get_corlib() &&
                IL2CPP.il2cpp_class_get_name_(Pointer) == "Nullable`1" &&
                IL2CPP.il2cpp_class_get_namespace_(Pointer) == "System";

            private static IntPtr Resolve()
            {
                InteropGenericValueSupport.RequireNativeType<T>();
                var pointer = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (typeof(T).ContainsGenericParameters || pointer == IntPtr.Zero || !IL2CPP.il2cpp_class_is_valuetype(pointer))
                    throw new NotSupportedException("Aggregate storage requires an exact closed native value wrapper.");
                var inflated = IL2CPP.il2cpp_class_is_inflated(pointer);
                if ((typeof(T).IsConstructedGenericType || IL2CPP.il2cpp_class_is_generic(pointer)) && !inflated)
                    throw new NotSupportedException("The native generic aggregate has not been constructed.");
                return pointer;
            }

            private static int GetSize()
            {
                uint alignment = 0;
                var size = IL2CPP.il2cpp_class_value_size(Pointer, ref alignment);
                return size > 0 ? size : throw new NotSupportedException("The native aggregate has no verified storage size.");
            }
        }
    }
}
