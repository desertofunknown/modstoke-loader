// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Preloader
{
    // Public entry points are called by generated assemblies, never by the conversion passes.
    public static partial class InteropGenericValueSupport
    {
        public static void RequireNativeType<T>() => _ = NativeType<T>.Class;
        public static IntPtr RequireNativePointer(IntPtr pointer, string detail) =>
            pointer != IntPtr.Zero ? pointer : throw new MissingMethodException(detail);

        public static Il2CppArrayBase<T> AllocateArray<T>(long length)
        {
            if (length < 0) throw new OverflowException("An array length cannot be negative.");
            // The native class determines reference slots versus inline values, including
            // native structs represented by CLR wrapper classes.
            var pointer = IL2CPP.il2cpp_array_new(NativeType<T>.Class, checked((ulong)length));
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            var owner = new Il2CppObjectBase(pointer);
            try { return Il2CppArrayBase<T>.WrapNativeGenericArrayPointer(pointer); }
            finally { GC.KeepAlive(owner); }
        }

        public static void InitializeDefault<T>(ref T location)
        {
            if (!typeof(Il2CppObjectBase).IsAssignableFrom(typeof(T)))
            {
                location = default;
                return;
            }
            var nativeClass = NativeType<T>.Class;
            if (!NativeType<T>.IsNativeValue)
            {
                location = default;
                return;
            }
            // Object allocation zeroes the boxed payload but does not invoke the value
            // type's constructor. A null CLR wrapper would not represent default(T).
            var pointer = IL2CPP.il2cpp_object_new(nativeClass);
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            var owner = new Il2CppObjectBase(pointer);
            try { location = IL2CPP.PointerToValueGeneric<T>(pointer, false, true); }
            finally { GC.KeepAlive(owner); }
        }

        public static unsafe TObject Box<T, TObject>(T value) where TObject : Il2CppObjectBase
        {
            var nativeClass = NativeType<T>.Class;
            if (!typeof(T).IsValueType)
            {
                if (NativeType<T>.IsNativeValue) return BoxNativeValue<T, TObject>(value);
                if (value is null) return null;
                var existing = GetNativeObject(value);
                try { return existing.Cast<TObject>(); }
                finally { GC.KeepAlive(existing); GC.KeepAlive(value); }
            }
            // This copy has no CLR/native references and its size/layout was checked once per T.
            var local = value;
            var pointer = IL2CPP.il2cpp_value_box(nativeClass, (IntPtr)Unsafe.AsPointer(ref local));
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            var boxed = new Il2CppObjectBase(pointer);
            try { return boxed.Cast<TObject>(); }
            finally { GC.KeepAlive(boxed); }
        }

        public static T UnboxAny<T>(object value) => Read<T>(value, true);

        public static TObject TypeQueryReceiver<T, TObject>(ref T location) where TObject : Il2CppObjectBase
        {
            _ = ArgumentKind<T>();
            return Box<T, TObject>(location);
        }

        private static TObject BoxNativeValue<T, TObject>(T value) where TObject : Il2CppObjectBase
        {
            // A null wrapper represents default(T), not a null native value. Initialize
            // only this local copy; boxing must not change the caller's value storage.
            var handle = PinValueArgument(ref value);
            try
            {
                var pointer = IL2CPP.il2cpp_value_box(NativeType<T>.Class,
                    IL2CPP.il2cpp_object_unbox(PinnedReferenceArgumentAddress(handle)));
                // Native Nullable<T> boxing yields the underlying value or null.
                if (pointer == IntPtr.Zero)
                {
                    if (NativeType<T>.IsNullable) return null;
                    throw new OutOfMemoryException();
                }
                var boxed = new Il2CppObjectBase(pointer);
                try { return boxed.Cast<TObject>(); }
                finally { GC.KeepAlive(boxed); }
            }
            finally
            {
                IL2CPP.il2cpp_gchandle_free(handle);
                GC.KeepAlive(value);
            }
        }

        public static TTarget ReferenceReceiver<T, TTarget>(ref T location) where TTarget : Il2CppObjectBase
        {
            if (NativeType<T>.IsNativeValue || typeof(T).IsValueType)
                throw new NotSupportedException("A native value receiver needs an exact mutable reference contract.");
            var value = location;
            if (value is null) throw new NullReferenceException();
            var native = GetNativeObject(value);
            try { return native.Cast<TTarget>(); }
            finally { GC.KeepAlive(native); GC.KeepAlive(value); }
        }

        private static T Read<T>(object value, bool copyValue)
        {
            var nativeClass = NativeType<T>.Class;
            if (copyValue && NativeType<T>.IsNullable) return UnboxNativeNullable<T>(value);
            if (value == null)
            {
                if (typeof(T).IsValueType || copyValue && NativeType<T>.IsNativeValue) throw new NullReferenceException();
                return default;
            }
            if (typeof(T) == typeof(string) && value is string text)
                return (T)(object)text;
            var native = GetNativeObject(value);
            try
            {
                if (!IL2CPP.il2cpp_class_is_assignable_from(nativeClass, native.ObjectClass))
                    throw new InvalidCastException("The native object does not match " + typeof(T).FullName + ".");
                if (copyValue && NativeType<T>.IsNativeValue && !typeof(T).IsValueType)
                {
                    var copy = CopyBox(native, nativeClass);
                    try { return IL2CPP.PointerToValueGeneric<T>(copy.Pointer, false, true); }
                    finally { GC.KeepAlive(copy); }
                }
                return IL2CPP.PointerToValueGeneric<T>(native.Pointer, false, true);
            }
            finally { GC.KeepAlive(native); GC.KeepAlive(value); }
        }

        public static T CastReference<T>(object value)
        {
            if (typeof(T).IsValueType)
                throw new NotSupportedException("A castclass reference cannot be returned as a CLR value.");
            return Read<T>(value, false);
        }

        public static T TryCastReference<T>(object value)
        {
            if (typeof(T).IsValueType)
                throw new NotSupportedException("An isinst reference cannot be returned as a CLR value.");
            var nativeClass = NativeType<T>.Class;
            if (value == null) return default;
            if (typeof(T) == typeof(string) && value is string text) return (T)(object)text;
            var native = GetNativeObject(value);
            try
            {
                return IL2CPP.il2cpp_class_is_assignable_from(nativeClass, native.ObjectClass)
                    ? IL2CPP.PointerToValueGeneric<T>(native.Pointer, false, true) : default;
            }
            finally { GC.KeepAlive(native); GC.KeepAlive(value); }
        }

        public static TObject IsInstanceBoxed<T, TObject>(object value) where TObject : Il2CppObjectBase
        {
            var nativeClass = NativeType<T>.Class;
            if (value == null) return null;
            var native = GetNativeObject(value);
            try
            {
                return IL2CPP.il2cpp_class_is_assignable_from(nativeClass, native.ObjectClass)
                    ? native.Cast<TObject>() : null;
            }
            finally { GC.KeepAlive(native); GC.KeepAlive(value); }
        }

        public static TObject CastBoxed<T, TObject>(object value) where TObject : Il2CppObjectBase
        {
            var nativeClass = NativeType<T>.Class;
            if (value == null) return null;
            var native = GetNativeObject(value);
            try
            {
                if (!IL2CPP.il2cpp_class_is_assignable_from(nativeClass, native.ObjectClass))
                    throw new InvalidCastException("The native object does not match " + typeof(T).FullName + ".");
                return native.Cast<TObject>();
            }
            finally { GC.KeepAlive(native); GC.KeepAlive(value); }
        }

        private static Il2CppObjectBase GetNativeObject(object value) => value switch
        {
            Il2CppObjectBase native => native,
            string text => new Il2CppObjectBase(IL2CPP.ManagedStringToIl2Cpp(text)),
            _ => throw new NotSupportedException("A CLR-only object has no native object identity.")
        };

        private static Il2CppObjectBase CopyBox(Il2CppObjectBase value, IntPtr nativeClass)
        {
            try { return new Il2CppObjectBase(IL2CPP.il2cpp_value_box(nativeClass, IL2CPP.il2cpp_object_unbox(value.Pointer))); }
            finally { GC.KeepAlive(value); }
        }

        private static class NativeType<T>
        {
            internal static readonly IntPtr Class = Resolve();
            internal static readonly bool IsNativeValue = IL2CPP.il2cpp_class_is_valuetype(Class);
            internal static readonly bool IsNullable = IsNativeValue &&
                IL2CPP.il2cpp_class_get_image(Class) == IL2CPP.il2cpp_get_corlib() &&
                IL2CPP.il2cpp_class_get_name_(Class) == "Nullable`1" &&
                IL2CPP.il2cpp_class_get_namespace_(Class) == "System";

            private static IntPtr Resolve()
            {
                var type = typeof(T);
                if (Nullable.GetUnderlyingType(type) != null ||
                    !type.IsValueType && type != typeof(string) && !typeof(Il2CppObjectBase).IsAssignableFrom(type))
                    throw new NotSupportedException("No native representation is defined for " + type.FullName + ".");
                var nativeClass = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (nativeClass == IntPtr.Zero)
                    throw new NotSupportedException("No native class is available for " + type.FullName + ".");
                if (type.IsValueType)
                {
                    uint alignment = 0;
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() ||
                        !IL2CPP.il2cpp_class_is_valuetype(nativeClass) || IL2CPP.il2cpp_class_has_references(nativeClass) ||
                        !(type.IsPrimitive || type.IsEnum || type.IsLayoutSequential || type.IsExplicitLayout) ||
                        IL2CPP.il2cpp_class_value_size(nativeClass, ref alignment) != Unsafe.SizeOf<T>())
                        throw new NotSupportedException("The CLR and native value layouts do not match for " + type.FullName + ".");
                }
                return nativeClass;
            }
        }
    }
}
