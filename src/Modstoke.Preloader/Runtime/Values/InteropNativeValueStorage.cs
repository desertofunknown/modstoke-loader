// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Generated CLR locations hold boxes; source value loads must not share their mutable payload.
    public static partial class InteropNativeValueStorage
    {
        public static T Receiver<T>(ref T location) where T : Il2CppObjectBase
        {
            var nativeClass = ValueType<T>.Class;
            if (location == null) location = CreateDefault<T>(nativeClass);
            else Validate(location, nativeClass);
            return location;
        }

        public static unsafe T Copy<T>(T value) where T : Il2CppObjectBase
        {
            var nativeClass = ValueType<T>.Class;
            if (value == null) return CreateDefault<T>(nativeClass);
            Validate(value, nativeClass);
            var handle = IL2CPP.il2cpp_gchandle_new(value.Pointer, true);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("The native value could not be pinned.");
            try
            {
                var source = IL2CPP.il2cpp_gchandle_get_target(handle);
                if (source == IntPtr.Zero) throw new InvalidOperationException("The pinned native value is unavailable.");
                IntPtr pointer;
                if (ValueType<T>.IsNullable)
                {
                    // value_box unwraps Nullable<T>. MemberwiseClone copies its actual boxed storage.
                    var exception = IntPtr.Zero;
                    pointer = IL2CPP.il2cpp_runtime_invoke(NullableClone.Method.Value, source, null, ref exception);
                    InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
                }
                else pointer = IL2CPP.il2cpp_value_box(nativeClass, IL2CPP.il2cpp_object_unbox(source));
                return Wrap<T>(pointer, nativeClass);
            }
            finally
            {
                IL2CPP.il2cpp_gchandle_free(handle);
                GC.KeepAlive(value);
            }
        }

        public static void CopyLocation<T>(ref T destination, ref T source) where T : Il2CppObjectBase =>
            Store(ref destination, source);

        private static T CreateDefault<T>(IntPtr nativeClass) where T : Il2CppObjectBase =>
            Wrap<T>(IL2CPP.il2cpp_object_new(nativeClass), nativeClass);

        private static T Wrap<T>(IntPtr pointer, IntPtr nativeClass) where T : Il2CppObjectBase
        {
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            var root = new Il2CppObjectBase(pointer);
            try
            {
                Validate(root, nativeClass);
                return IL2CPP.PointerToValueGeneric<T>(pointer, false, true);
            }
            finally { GC.KeepAlive(root); }
        }

        private static void Validate(Il2CppObjectBase value, IntPtr nativeClass)
        {
            if (value.ObjectClass != nativeClass)
                throw new InvalidCastException("The boxed value does not have the exact native storage type.");
        }

        private static class ValueType<T> where T : Il2CppObjectBase
        {
            internal static readonly IntPtr Class = Resolve();
            internal static readonly bool IsNullable = IL2CPP.il2cpp_class_get_image(Class) == IL2CPP.il2cpp_get_corlib() &&
                IL2CPP.il2cpp_class_get_name_(Class) == "Nullable`1" &&
                IL2CPP.il2cpp_class_get_namespace_(Class) == "System";

            private static IntPtr Resolve()
            {
                InteropGenericValueSupport.RequireNativeType<T>();
                var pointer = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (pointer == IntPtr.Zero || typeof(T).ContainsGenericParameters ||
                    !IL2CPP.il2cpp_class_is_valuetype(pointer) ||
                    (typeof(T).IsConstructedGenericType || IL2CPP.il2cpp_class_is_generic(pointer)) &&
                    !IL2CPP.il2cpp_class_is_inflated(pointer))
                    throw new NotSupportedException("Value storage requires an exact constructed native value wrapper.");
                return pointer;
            }
        }

        private static class NullableClone
        {
            internal static readonly Lazy<IntPtr> Method = new(Resolve);

            private static IntPtr Resolve()
            {
                var owner = IL2CPP.il2cpp_class_from_name(IL2CPP.il2cpp_get_corlib(), "System", "Object");
                if (owner == IntPtr.Zero) throw new MissingMethodException("The native Object type is unavailable.");
                var method = IL2CPP.il2cpp_class_get_method_from_name(owner, "MemberwiseClone", 0);
                uint flags = 0;
                if (method == IntPtr.Zero || IL2CPP.il2cpp_method_get_class(method) != owner ||
                    IL2CPP.il2cpp_method_is_generic(method) || IL2CPP.il2cpp_method_get_param_count(method) != 0 ||
                    (IL2CPP.il2cpp_method_get_flags(method, ref flags) & 0x10) != 0)
                    throw new MissingMethodException("Native MemberwiseClone is not the expected instance contract.");
                var result = IL2CPP.il2cpp_method_get_return_type(method);
                if (IL2CPP.il2cpp_type_is_byref(result) || IL2CPP.il2cpp_class_from_type(result) != owner)
                    throw new MissingMethodException("Native MemberwiseClone does not return Object.");
                return method;
            }
        }
    }
}
