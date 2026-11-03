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
        public static TResult ConstrainedInterfaceResult0<T, TInterface, TResult>(ref T location, string field)
            where TInterface : Il2CppObjectBase => ReadOwnedNativeResult<TResult>(
                InvokeInterfaceArguments<T, TInterface, byte, byte>(ref location, 0, 0,
                    InterfaceResultContract<T, TInterface, TResult>(field, 0)));

        public static TResult ConstrainedInterfaceResult1<T, TInterface, T1, TResult>(ref T location, T1 first, string field)
            where TInterface : Il2CppObjectBase => ReadOwnedNativeResult<TResult>(
                InvokeInterfaceArguments<T, TInterface, T1, byte>(ref location, first, 0,
                    InterfaceResultContract<T, TInterface, TResult>(field, 1)));

        public static TResult ConstrainedInterfaceResult2<T, TInterface, T1, T2, TResult>(ref T location, T1 first, T2 second, string field)
            where TInterface : Il2CppObjectBase => ReadOwnedNativeResult<TResult>(
                InvokeInterfaceArguments<T, TInterface, T1, T2>(ref location, first, second,
                    InterfaceResultContract<T, TInterface, TResult>(field, 2)));

        public static TResult ConstrainedInterfaceResult3<T, TInterface, T1, T2, T3, TResult>(
            ref T location, T1 first, T2 second, T3 third, string field)
            where TInterface : Il2CppObjectBase => ReadOwnedNativeResult<TResult>(
                InvokeInterfaceThreeArguments<T, TInterface, T1, T2, T3>(ref location, first, second, third,
                    InterfaceResultContract<T, TInterface, TResult>(field, 3)));

        public static TResult ConstrainedInterfaceResult4<T, TInterface, T1, T2, T3, T4, TResult>(
            ref T location, T1 first, T2 second, T3 third, T4 fourth, string field)
            where TInterface : Il2CppObjectBase => ReadOwnedNativeResult<TResult>(
                InvokeInterfaceFourArguments<T, TInterface, T1, T2, T3, T4>(ref location, first, second, third, fourth,
                    InterfaceResultContract<T, TInterface, TResult>(field, 4)));

        public static void ConstrainedInterfaceVoid0<T, TInterface>(ref T location, string field)
            where TInterface : Il2CppObjectBase =>
                _ = InvokeInterfaceArguments<T, TInterface, byte, byte>(ref location, 0, 0,
                    InterfaceVoidContract<T, TInterface>(field, 0));

        public static void ConstrainedInterfaceVoid1<T, TInterface, T1>(ref T location, T1 first, string field)
            where TInterface : Il2CppObjectBase =>
                _ = InvokeInterfaceArguments<T, TInterface, T1, byte>(ref location, first, 0,
                    InterfaceVoidContract<T, TInterface>(field, 1));

        public static void ConstrainedInterfaceVoid2<T, TInterface, T1, T2>(ref T location, T1 first, T2 second, string field)
            where TInterface : Il2CppObjectBase =>
                _ = InvokeInterfaceArguments<T, TInterface, T1, T2>(ref location, first, second,
                    InterfaceVoidContract<T, TInterface>(field, 2));

        public static void ConstrainedInterfaceVoid3<T, TInterface, T1, T2, T3>(
            ref T location, T1 first, T2 second, T3 third, string field)
            where TInterface : Il2CppObjectBase =>
                _ = InvokeInterfaceThreeArguments<T, TInterface, T1, T2, T3>(ref location, first, second, third,
                    InterfaceVoidContract<T, TInterface>(field, 3));

        public static void ConstrainedInterfaceVoid4<T, TInterface, T1, T2, T3, T4>(
            ref T location, T1 first, T2 second, T3 third, T4 fourth, string field)
            where TInterface : Il2CppObjectBase =>
                _ = InvokeInterfaceFourArguments<T, TInterface, T1, T2, T3, T4>(ref location, first, second, third, fourth,
                    InterfaceVoidContract<T, TInterface>(field, 4));

        private static InterfaceMethod InterfaceResultContract<T, TInterface, TResult>(string field, int count)
            where TInterface : Il2CppObjectBase
        {
            var method = InterfaceTarget<T, TInterface>.Get(field, count);
            if (method.ReturnKind == 0x01 || method.ReturnClass != NativeType<TResult>.Class)
                throw new NotSupportedException("The interface result does not match its native contract.");
            return method;
        }

        private static InterfaceMethod InterfaceVoidContract<T, TInterface>(string field, int count)
            where TInterface : Il2CppObjectBase
        {
            var method = InterfaceTarget<T, TInterface>.Get(field, count);
            if (method.ReturnKind != 0x01)
                throw new NotSupportedException("The interface method does not return void.");
            return method;
        }

        private static unsafe Il2CppObjectBase InvokeInterfaceArguments<T, TInterface, T1, T2>(ref T location,
            T1 first, T2 second, InterfaceMethod method) where TInterface : Il2CppObjectBase
        {
            var firstPin = IntPtr.Zero;
            var secondPin = IntPtr.Zero;
            try
            {
                // CLR addresses are used only for proven reference-free value layouts.
                // Native wrappers instead contribute their separately pinned native storage.
                fixed (byte* firstAddress = &Unsafe.As<T1, byte>(ref first))
                fixed (byte* secondAddress = &Unsafe.As<T2, byte>(ref second))
                {
                    var arguments = stackalloc IntPtr[2];
                    if (method.Count > 0)
                        arguments[0] = InterfaceArgumentAddress(ref first, (IntPtr)firstAddress, method.First, out firstPin);
                    if (method.Count > 1)
                        arguments[1] = InterfaceArgumentAddress(ref second, (IntPtr)secondAddress, method.Second, out secondPin);
                    return InvokeInterfaceReceiver<T, TInterface>(ref location, method, method.Count == 0 ? null : arguments);
                }
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(secondPin);
                InteropNativeValueStorage.ReleaseArgument(firstPin);
                GC.KeepAlive(first);
                GC.KeepAlive(second);
            }
        }

        private static unsafe Il2CppObjectBase InvokeInterfaceThreeArguments<T, TInterface, T1, T2, T3>(
            ref T location, T1 first, T2 second, T3 third, InterfaceMethod method) where TInterface : Il2CppObjectBase
        {
            var firstPin = IntPtr.Zero;
            var secondPin = IntPtr.Zero;
            var thirdPin = IntPtr.Zero;
            try
            {
                fixed (byte* firstAddress = &Unsafe.As<T1, byte>(ref first))
                fixed (byte* secondAddress = &Unsafe.As<T2, byte>(ref second))
                fixed (byte* thirdAddress = &Unsafe.As<T3, byte>(ref third))
                {
                    var arguments = stackalloc IntPtr[3];
                    arguments[0] = InterfaceArgumentAddress(ref first, (IntPtr)firstAddress, method.First, out firstPin);
                    arguments[1] = InterfaceArgumentAddress(ref second, (IntPtr)secondAddress, method.Second, out secondPin);
                    arguments[2] = InterfaceArgumentAddress(ref third, (IntPtr)thirdAddress, method.Third, out thirdPin);
                    return InvokeInterfaceReceiver<T, TInterface>(ref location, method, arguments);
                }
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(thirdPin);
                InteropNativeValueStorage.ReleaseArgument(secondPin);
                InteropNativeValueStorage.ReleaseArgument(firstPin);
                GC.KeepAlive(first);
                GC.KeepAlive(second);
                GC.KeepAlive(third);
            }
        }

        private static unsafe Il2CppObjectBase InvokeInterfaceFourArguments<T, TInterface, T1, T2, T3, T4>(
            ref T location, T1 first, T2 second, T3 third, T4 fourth, InterfaceMethod method) where TInterface : Il2CppObjectBase
        {
            var firstPin = IntPtr.Zero;
            var secondPin = IntPtr.Zero;
            var thirdPin = IntPtr.Zero;
            var fourthPin = IntPtr.Zero;
            try
            {
                fixed (byte* firstAddress = &Unsafe.As<T1, byte>(ref first))
                fixed (byte* secondAddress = &Unsafe.As<T2, byte>(ref second))
                fixed (byte* thirdAddress = &Unsafe.As<T3, byte>(ref third))
                fixed (byte* fourthAddress = &Unsafe.As<T4, byte>(ref fourth))
                {
                    var arguments = stackalloc IntPtr[4];
                    arguments[0] = InterfaceArgumentAddress(ref first, (IntPtr)firstAddress, method.First, out firstPin);
                    arguments[1] = InterfaceArgumentAddress(ref second, (IntPtr)secondAddress, method.Second, out secondPin);
                    arguments[2] = InterfaceArgumentAddress(ref third, (IntPtr)thirdAddress, method.Third, out thirdPin);
                    arguments[3] = InterfaceArgumentAddress(ref fourth, (IntPtr)fourthAddress, method.Fourth, out fourthPin);
                    return InvokeInterfaceReceiver<T, TInterface>(ref location, method, arguments);
                }
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(fourthPin);
                InteropNativeValueStorage.ReleaseArgument(thirdPin);
                InteropNativeValueStorage.ReleaseArgument(secondPin);
                InteropNativeValueStorage.ReleaseArgument(firstPin);
                GC.KeepAlive(first);
                GC.KeepAlive(second);
                GC.KeepAlive(third);
                GC.KeepAlive(fourth);
            }
        }

        private static IntPtr InterfaceArgumentAddress<T>(ref T value, IntPtr clrAddress,
            InterfaceParameter parameter, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (parameter.ByReference)
                throw new NotSupportedException("A native byref interface parameter requires its original storage location.");
            var kind = ArgumentType<T>.Kind;
            if (parameter.Value)
            {
                if (kind == 2 || NativeType<T>.Class != parameter.Class)
                    throw new InvalidCastException("The interface argument does not match its native value parameter.");
                if (kind == 0) return clrAddress;
                handle = PinValueArgument(ref value);
                return InteropNativeValueStorage.PinnedArgumentAddress(handle);
            }
            if (kind != 2)
                throw new InvalidCastException("A native reference parameter requires an explicitly boxed or reference argument.");
            handle = PinReferenceArgument(value);
            var pointer = PinnedReferenceArgumentAddress(handle);
            if (pointer != IntPtr.Zero &&
                !IL2CPP.il2cpp_class_is_assignable_from(parameter.Class, IL2CPP.il2cpp_object_get_class(pointer)))
                throw new InvalidCastException("The interface argument does not implement its native reference parameter.");
            return pointer;
        }

        private static unsafe Il2CppObjectBase InvokeInterfaceReceiver<T, TInterface>(ref T location,
            InterfaceMethod target, IntPtr* arguments) where TInterface : Il2CppObjectBase
        {
            var kind = ArgumentType<T>.Kind;
            if (target.ValueOverride && kind == 0)
            {
                fixed (byte* address = &Unsafe.As<T, byte>(ref location))
                    return InvokeInterfaceMethod(target.Method, (IntPtr)address, arguments);
            }
            var handle = IntPtr.Zero;
            Il2CppObjectBase boxed = null;
            try
            {
                if (target.ValueOverride) handle = PinValueArgument(ref location);
                else if (kind == 2) handle = PinReferenceArgument(location);
                else
                {
                    boxed = Box<T, TInterface>(location);
                    if (boxed != null) handle = PinArgumentObject(boxed);
                }
                var instance = PinnedReferenceArgumentAddress(handle);
                if (instance == IntPtr.Zero) throw new NullReferenceException();
                var method = target.Method;
                if (kind == 2)
                {
                    if (!IL2CPP.il2cpp_class_is_assignable_from(target.InterfaceClass, IL2CPP.il2cpp_object_get_class(instance)))
                        throw new InvalidCastException("The receiver does not implement the native interface.");
                    method = IL2CPP.il2cpp_object_get_virtual_method(instance, method);
                    if (method == IntPtr.Zero) throw new MissingMethodException("The native interface implementation is unavailable.");
                }
                var owner = IL2CPP.il2cpp_method_get_class(method);
                var address = IL2CPP.il2cpp_class_is_valuetype(owner) ? IL2CPP.il2cpp_object_unbox(instance) : instance;
                return InvokeInterfaceMethod(method, address, arguments);
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(handle);
                GC.KeepAlive(boxed);
                GC.KeepAlive(location);
            }
        }

        private static unsafe Il2CppObjectBase InvokeInterfaceMethod(IntPtr method, IntPtr receiver, IntPtr* arguments)
        {
            var exception = IntPtr.Zero;
            var result = IL2CPP.il2cpp_runtime_invoke(method, receiver, (void**)arguments, ref exception);
            InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
            return result == IntPtr.Zero ? null : new Il2CppObjectBase(result);
        }

        private readonly record struct InterfaceParameter(IntPtr Class, bool Value, bool ByReference);
        private readonly record struct InterfaceMethod(IntPtr Method, IntPtr InterfaceClass, bool ValueOverride,
            IntPtr ReturnClass, int ReturnKind, int Count, InterfaceParameter First, InterfaceParameter Second,
            InterfaceParameter Third, InterfaceParameter Fourth);

        private static class InterfaceTarget<T, TInterface> where TInterface : Il2CppObjectBase
        {
            private static readonly ConcurrentDictionary<string, InterfaceMethod> methods = new(StringComparer.Ordinal);

            internal static InterfaceMethod Get(string field, int count)
            {
                ArgumentException.ThrowIfNullOrEmpty(field);
                var method = methods.GetOrAdd(field, static name => Resolve(name));
                if (method.Count != count) throw new MissingMethodException("The native interface arity differs from the generated call.");
                return method;
            }

            private static InterfaceMethod Resolve(string field)
            {
                var kind = ArgumentType<T>.Kind;
                _ = ArgumentType<TInterface>.Kind;
                var receiverClass = NativeType<T>.Class;
                var interfaceClass = NativeType<TInterface>.Class;
                if ((IL2CPP.il2cpp_class_get_flags(interfaceClass) & 0x20) == 0)
                    throw new NotSupportedException("The constrained target must be an exact native interface.");
                var slot = InteropNativeReferenceCall.ReadMetadataPointer(typeof(TInterface), field);
                uint flags = 0;
                if (IL2CPP.il2cpp_method_get_class(slot) != interfaceClass ||
                    (IL2CPP.il2cpp_method_get_flags(slot, ref flags) & 0x40) == 0)
                    throw new MissingMethodException("The mapped slot is not declared on the native interface.");
                var contract = ReadContract(slot, interfaceClass, false, true);
                if (kind == 2) return contract;
                if (!IL2CPP.il2cpp_class_is_assignable_from(interfaceClass, receiverClass))
                    throw new InvalidCastException("The native value does not implement the requested interface.");

                var pointer = IL2CPP.il2cpp_object_new(receiverClass);
                if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                var receiver = new Il2CppObjectBase(pointer);
                var handle = IntPtr.Zero;
                try
                {
                    handle = PinArgumentObject(receiver);
                    var method = IL2CPP.il2cpp_object_get_virtual_method(PinnedReferenceArgumentAddress(handle), slot);
                    if (method == IntPtr.Zero) throw new MissingMethodException("The native value interface implementation is unavailable.");
                    var owner = IL2CPP.il2cpp_method_get_class(method);
                    if (owner != receiverClass && (IL2CPP.il2cpp_class_is_valuetype(owner) ||
                        !IL2CPP.il2cpp_class_is_assignable_from(owner, receiverClass)))
                        throw new MissingMethodException("The interface implementation has an unrelated declaring type.");
                    var implementation = ReadContract(method, interfaceClass, owner == receiverClass, false);
                    if (implementation.ReturnClass != contract.ReturnClass || implementation.ReturnKind != contract.ReturnKind ||
                        implementation.Count != contract.Count || implementation.First != contract.First ||
                        implementation.Second != contract.Second || implementation.Third != contract.Third ||
                        implementation.Fourth != contract.Fourth)
                        throw new MissingMethodException("The native interface implementation has a different signature.");
                    return implementation;
                }
                finally
                {
                    InteropNativeValueStorage.ReleaseArgument(handle);
                    GC.KeepAlive(receiver);
                }
            }

            private static InterfaceMethod ReadContract(IntPtr method, IntPtr interfaceClass, bool valueOverride, bool slot)
            {
                uint flags = 0;
                var count = checked((int)IL2CPP.il2cpp_method_get_param_count(method));
                if (count > 4 || IL2CPP.il2cpp_method_is_generic(method) ||
                    (IL2CPP.il2cpp_method_get_flags(method, ref flags) & (slot ? 0x10 : 0x410)) != 0)
                    throw new NotSupportedException("The native interface method requires a different invocation contract.");
                var result = ReadType(IL2CPP.il2cpp_method_get_return_type(method), true, out var resultKind);
                var first = count > 0 ? ReadType(IL2CPP.il2cpp_method_get_param(method, 0), false, out _) : default;
                var second = count > 1 ? ReadType(IL2CPP.il2cpp_method_get_param(method, 1), false, out _) : default;
                var third = count > 2 ? ReadType(IL2CPP.il2cpp_method_get_param(method, 2), false, out _) : default;
                var fourth = count > 3 ? ReadType(IL2CPP.il2cpp_method_get_param(method, 3), false, out _) : default;
                return new InterfaceMethod(method, interfaceClass, valueOverride, result.Class, resultKind, count,
                    first, second, third, fourth);
            }

            private static InterfaceParameter ReadType(IntPtr type, bool allowVoid, out int kind)
            {
                if (type == IntPtr.Zero) throw new MissingMethodException("The native interface type metadata is unavailable.");
                kind = IL2CPP.il2cpp_type_get_type(type);
                var byReference = IL2CPP.il2cpp_type_is_byref(type);
                if (byReference && allowVoid || kind is 0x0f or 0x13 or 0x16 or 0x1b or 0x1e)
                    throw new NotSupportedException("Unresolved native generics, pointers and references require an explicit interface lifetime contract.");
                if (kind == 0x01)
                {
                    if (!allowVoid) throw new NotSupportedException("An interface parameter cannot be void.");
                    return default;
                }
                var typeClass = IL2CPP.il2cpp_class_from_type(type);
                if (typeClass == IntPtr.Zero) throw new MissingMethodException("The native interface type has no resolved class.");
                return new InterfaceParameter(typeClass, IL2CPP.il2cpp_class_is_valuetype(typeClass), byReference);
            }
        }
    }
}
