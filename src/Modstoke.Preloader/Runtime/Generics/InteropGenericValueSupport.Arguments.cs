// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        // Generated dispatch: 0 = CLR value address, 1 = native boxed payload, 2 = native object slot.
        public static int ArgumentKind<T>() => ArgumentType<T>.Kind;

        /// <summary>Pins native value storage, initializing a null wrapper to default(T).</summary>
        /// <returns>A native GC handle owned by the caller; release with InteropNativeValueStorage.ReleaseArgument.</returns>
        public static IntPtr PinValueArgument<T>(ref T location)
        {
            if (ArgumentType<T>.Kind != 1)
                throw new NotSupportedException("This argument is not a boxed native value.");
            if (location is null) InitializeDefault(ref location);
            if (location is not Il2CppObjectBase value || value.ObjectClass != NativeType<T>.Class)
                throw new InvalidCastException("The argument does not have its exact native value storage type.");
            return PinArgumentObject(value);
        }

        /// <summary>Pins a native object reference, returning zero for null.</summary>
        /// <returns>A native GC handle owned by the caller; release with InteropNativeValueStorage.ReleaseArgument.</returns>
        public static IntPtr PinReferenceArgument<T>(T value)
        {
            if (ArgumentType<T>.Kind != 2)
                throw new NotSupportedException("This argument is not a native object reference.");
            if (value is null) return IntPtr.Zero;
            if (typeof(T) == typeof(string)) return PinStringArgument((string)(object)value);
            var native = GetNativeObject(value);
            try
            {
                if (!IL2CPP.il2cpp_class_is_assignable_from(NativeType<T>.Class, native.ObjectClass))
                    throw new InvalidCastException("The argument does not match its declared native reference type.");
                return PinArgumentObject(native);
            }
            finally { GC.KeepAlive(native); GC.KeepAlive(value); }
        }

        /// <summary>Converts and pins a native string, returning zero for null.</summary>
        /// <returns>A native GC handle owned by the caller; release with InteropNativeValueStorage.ReleaseArgument.</returns>
        public static IntPtr PinStringArgument(string value)
        {
            if (value == null) return IntPtr.Zero;
            var pointer = IL2CPP.ManagedStringToIl2Cpp(value);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("The native string argument could not be allocated.");
            // The converted string has no existing wrapper to own its native lifetime.
            var handle = IL2CPP.il2cpp_gchandle_new(pointer, true);
            return handle != IntPtr.Zero ? handle :
                throw new InvalidOperationException("The native string argument could not be pinned.");
        }

        // The returned object address is borrowed from the handle and must not outlive its release.
        public static IntPtr PinnedReferenceArgumentAddress(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            var pointer = IL2CPP.il2cpp_gchandle_get_target(handle);
            return pointer != IntPtr.Zero ? pointer :
                throw new InvalidOperationException("The pinned native reference argument is unavailable.");
        }

        public static void WriteReferenceArgument<T>(ref T location, IntPtr pointer)
        {
            if (ArgumentType<T>.Kind != 2)
                throw new NotSupportedException("Only native reference arguments require object-slot writeback.");
            if (pointer == IntPtr.Zero)
            {
                location = default;
                return;
            }
            var native = new Il2CppObjectBase(pointer);
            try
            {
                if (!IL2CPP.il2cpp_class_is_assignable_from(NativeType<T>.Class, native.ObjectClass))
                    throw new InvalidCastException("The returned reference does not match its declared native type.");
                location = IL2CPP.PointerToValueGeneric<T>(pointer, false, true);
            }
            finally { GC.KeepAlive(native); }
        }

        private static IntPtr PinArgumentObject(Il2CppObjectBase value)
        {
            try
            {
                var handle = IL2CPP.il2cpp_gchandle_new(value.Pointer, true);
                return handle != IntPtr.Zero ? handle :
                    throw new InvalidOperationException("The native argument could not be pinned.");
            }
            finally { GC.KeepAlive(value); }
        }

        private static class ArgumentType<T>
        {
            internal static readonly int Kind = Resolve();

            private static int Resolve()
            {
                // NativeType also checks the size/layout and reference-free status of CLR values.
                var nativeClass = NativeType<T>.Class;
                if (typeof(T).ContainsGenericParameters ||
                    IL2CPP.il2cpp_class_is_generic(nativeClass) && !IL2CPP.il2cpp_class_is_inflated(nativeClass))
                    throw new NotSupportedException("A native argument requires a fully constructed type.");
                return typeof(T).IsValueType ? 0 : NativeType<T>.IsNativeValue ? 1 : 2;
            }
        }
    }
}
