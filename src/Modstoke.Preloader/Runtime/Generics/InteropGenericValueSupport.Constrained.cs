// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        public static T CopyValue<T>(T value) =>
            GenericValueCopy<T>.Required ? NativeValueCopy<T>.Copy(value) : value;

        private static class GenericValueCopy<T>
        {
            internal static readonly bool Required = !typeof(T).IsValueType &&
                typeof(Il2CppObjectBase).IsAssignableFrom(typeof(T)) && ArgumentType<T>.Kind == 1;
        }

        private static class NativeValueCopy<T>
        {
            internal static readonly Func<T, T> Copy;

            // Delayed until a wrapper type is proven. CLR value instantiations must not
            // bind the wrapper-only helper, even on runtimes eager to initialize statics.
            static NativeValueCopy() => Copy = (Func<T, T>)typeof(InteropNativeValueStorage)
                .GetMethod(nameof(InteropNativeValueStorage.Copy)).MakeGenericMethod(typeof(T))
                .CreateDelegate(typeof(Func<T, T>));
        }

        public static TResult ConstrainedValueResult<T, TInterface, TResult>(ref T location, string slotField)
            where TInterface : Il2CppObjectBase
        {
            var target = ConstrainedValueTarget<T, TInterface>.Get(slotField);
            if (target.ReturnKind == 0x01 || target.ReturnClass != NativeType<TResult>.Class)
                throw new NotSupportedException("The constrained result does not match its native return type.");
            var result = InvokeConstrainedValue(ref location, target.Method);
            return ReadOwnedNativeResult<TResult>(result);
        }

        public static void ConstrainedValueVoid<T, TInterface>(ref T location, string slotField)
            where TInterface : Il2CppObjectBase
        {
            var target = ConstrainedValueTarget<T, TInterface>.Get(slotField);
            if (target.ReturnKind != 0x01)
                throw new NotSupportedException("The constrained method does not return void.");
            _ = InvokeConstrainedValue(ref location, target.Method);
        }

        private static unsafe Il2CppObjectBase InvokeConstrainedValue<T>(ref T location, IntPtr method)
        {
            var exception = IntPtr.Zero;
            IntPtr result;
            if (ArgumentType<T>.Kind == 0)
            {
                // NativeType proved reference-free matching layouts. Mutate the actual CLR
                // location, including when the native method raises an exception.
                fixed (byte* pointer = &Unsafe.As<T, byte>(ref location))
                    result = IL2CPP.il2cpp_runtime_invoke(method, (IntPtr)pointer, null, ref exception);
                InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
                return result == IntPtr.Zero ? null : new Il2CppObjectBase(result);
            }

            var handle = PinValueArgument(ref location);
            try
            {
                var instance = IL2CPP.il2cpp_gchandle_get_target(handle);
                if (instance == IntPtr.Zero) throw new InvalidOperationException("The constrained receiver is unavailable.");
                result = IL2CPP.il2cpp_runtime_invoke(method, IL2CPP.il2cpp_object_unbox(instance), null, ref exception);
                InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
                return result == IntPtr.Zero ? null : new Il2CppObjectBase(result);
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(handle);
                GC.KeepAlive(location);
            }
        }

        private readonly record struct ConstrainedValueMethod(IntPtr Method, IntPtr ReturnClass, int ReturnKind);

        private static class ConstrainedValueTarget<T, TInterface> where TInterface : Il2CppObjectBase
        {
            private static readonly ConcurrentDictionary<string, ConstrainedValueMethod> methods = new(StringComparer.Ordinal);

            internal static ConstrainedValueMethod Get(string field)
            {
                ArgumentException.ThrowIfNullOrEmpty(field);
                return methods.GetOrAdd(field, static name => Resolve(name));
            }

            private static ConstrainedValueMethod Resolve(string field)
            {
                var receiverClass = NativeType<T>.Class;
                var interfaceClass = NativeType<TInterface>.Class;
                if (!NativeType<T>.IsNativeValue || ArgumentType<T>.Kind is not (0 or 1) ||
                    (IL2CPP.il2cpp_class_get_flags(interfaceClass) & 0x20) == 0 ||
                    !IL2CPP.il2cpp_class_is_assignable_from(interfaceClass, receiverClass))
                    throw new NotSupportedException("The constrained receiver must implement the exact native interface as a value type.");
                var slot = InteropNativeReferenceCall.ReadMetadataPointer(typeof(TInterface), field);
                uint flags = 0;
                if (IL2CPP.il2cpp_method_get_class(slot) != interfaceClass ||
                    IL2CPP.il2cpp_method_get_param_count(slot) != 0 || IL2CPP.il2cpp_method_is_generic(slot) ||
                    (IL2CPP.il2cpp_method_get_flags(slot, ref flags) & (0x10 | 0x40)) != 0x40)
                    throw new MissingMethodException("The mapped constrained slot is not a parameterless native interface method.");

                var returnType = IL2CPP.il2cpp_method_get_return_type(slot);
                var returnClass = ValidateResult(returnType, out var returnKind);
                // Resolve once using a zero-initialized native value box, never a constructor.
                // Subsequent calls invoke this exact implementation on the original location.
                var pointer = IL2CPP.il2cpp_object_new(receiverClass);
                if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                var owner = new Il2CppObjectBase(pointer);
                var handle = IntPtr.Zero;
                try
                {
                    handle = PinArgumentObject(owner);
                    var instance = IL2CPP.il2cpp_gchandle_get_target(handle);
                    if (instance == IntPtr.Zero) throw new InvalidOperationException("The dispatch receiver is unavailable.");
                    var method = IL2CPP.il2cpp_object_get_virtual_method(instance, slot);
                    if (method == IntPtr.Zero || IL2CPP.il2cpp_method_get_class(method) != receiverClass ||
                        IL2CPP.il2cpp_method_is_generic(method) || IL2CPP.il2cpp_method_get_param_count(method) != 0 ||
                        (IL2CPP.il2cpp_method_get_flags(method, ref flags) & (0x10 | 0x400)) != 0)
                        throw new MissingMethodException("The interface slot has no concrete implementation on the native value type.");
                    var implementationResult = ValidateResult(IL2CPP.il2cpp_method_get_return_type(method), out var implementationKind);
                    if (implementationResult != returnClass || implementationKind != returnKind)
                        throw new MissingMethodException("The native interface implementation has a different return contract.");
                    return new ConstrainedValueMethod(method, returnClass, returnKind);
                }
                finally
                {
                    InteropNativeValueStorage.ReleaseArgument(handle);
                    GC.KeepAlive(owner);
                }
            }

            private static IntPtr ValidateResult(IntPtr type, out int kind)
            {
                if (type == IntPtr.Zero) throw new MissingMethodException("The constrained return metadata is unavailable.");
                kind = IL2CPP.il2cpp_type_get_type(type);
                if (IL2CPP.il2cpp_type_is_byref(type) || kind is 0x0f or 0x16 or 0x1b)
                    throw new NotSupportedException("A constrained pointer or reference result cannot escape its receiver.");
                var result = kind == 0x01 ? IntPtr.Zero : IL2CPP.il2cpp_class_from_type(type);
                if (kind != 0x01 && result == IntPtr.Zero)
                    throw new MissingMethodException("The constrained return class is unavailable.");
                return result;
            }
        }
    }
}
