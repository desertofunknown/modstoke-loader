// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceAtomic
    {
        private static readonly ConcurrentDictionary<IntPtr, bool> methods = new();

        public static T CompareExchange<T>(InteropReferenceLocation location, T value, T comparand,
            Type methodOwner, string methodPointerName) where T : Il2CppObjectBase =>
            Invoke(location, value, comparand, methodOwner, methodPointerName, true);

        public static T Exchange<T>(InteropReferenceLocation location, T value,
            Type methodOwner, string methodPointerName) where T : Il2CppObjectBase =>
            Invoke(location, value, null, methodOwner, methodPointerName, false);

        private static unsafe T Invoke<T>(InteropReferenceLocation location, T value, T comparand,
            Type methodOwner, string methodPointerName, bool compareExchange) where T : Il2CppObjectBase
        {
            var method = InteropNativeReferenceCall.ReadMetadataPointer(methodOwner, methodPointerName);
            if (methods.GetOrAdd(method, ValidateMethod) != compareExchange)
                throw new MissingMethodException("The native atomic method differs from the requested operation.");
            InteropGenericValueSupport.RequireNativeType<T>();
            var elementClass = Il2CppClassPointerStore<T>.NativeClassPtr;
            using var scope = new InteropNativeReferenceScope(compareExchange ? 3 : 2);
            try
            {
                // The engine operation performs both the atomic update and its GC barrier.
                // A later field setter would overwrite a concurrent update and is never used.
                var address = scope.ReferenceAddress(location, elementClass);
                IntPtr* arguments = stackalloc IntPtr[3];
                arguments[0] = address;
                arguments[1] = PinValue(scope, value, elementClass);
                arguments[2] = compareExchange ? PinValue(scope, comparand, elementClass) : IntPtr.Zero;
                var exception = IntPtr.Zero;
                var result = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, (void**)arguments, ref exception);
                InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
                if (result == IntPtr.Zero) return null;
                if (compareExchange && result == arguments[2]) return comparand;
                return InteropNativeReferenceIdentity.WrapReturned<T>(result);
            }
            finally
            {
                GC.KeepAlive(location);
                GC.KeepAlive(value);
                GC.KeepAlive(comparand);
                GC.KeepAlive(methodOwner);
            }
        }

        private static IntPtr PinValue(InteropNativeReferenceScope scope, Il2CppObjectBase value, IntPtr elementClass)
        {
            if (value == null) return IntPtr.Zero;
            var pointer = scope.Pin(value);
            if (!IL2CPP.il2cpp_class_is_assignable_from(elementClass, IL2CPP.il2cpp_object_get_class(pointer)))
                throw new InvalidCastException("The atomic operand is not assignable to the native slot type.");
            return pointer;
        }

        private static bool ValidateMethod(IntPtr method)
        {
            if (method == IntPtr.Zero)
                throw new MissingMethodException("The native atomic method metadata is missing.");
            var owner = IL2CPP.il2cpp_method_get_class(method);
            var returnType = IL2CPP.il2cpp_method_get_return_type(method);
            if (owner == IntPtr.Zero || returnType == IntPtr.Zero)
                throw new MissingMethodException("The native atomic method metadata is incomplete.");
            var name = IL2CPP.il2cpp_method_get_name_(method);
            var compareExchange = name == "CompareExchange";
            var count = compareExchange ? 3u : 2u;
            uint implementationFlags = 0;
            var corlib = IL2CPP.il2cpp_get_corlib();
            if (corlib == IntPtr.Zero)
                throw new MissingMethodException("The native core library is unavailable.");
            var objectClass = IL2CPP.il2cpp_class_from_type(returnType);
            if ((!compareExchange && name != "Exchange") || IL2CPP.il2cpp_method_is_generic(method) ||
                (IL2CPP.il2cpp_method_get_flags(method, ref implementationFlags) & 0x17) != 0x16 ||
                IL2CPP.il2cpp_method_get_param_count(method) != count ||
                owner != IL2CPP.il2cpp_class_from_name(corlib, "System.Threading", "Interlocked") ||
                IL2CPP.il2cpp_type_is_byref(returnType) || objectClass == IntPtr.Zero ||
                IL2CPP.il2cpp_class_is_valuetype(objectClass) ||
                objectClass != IL2CPP.il2cpp_class_from_name(corlib, "System", "Object"))
                throw new MissingMethodException("An exact nongeneric native Interlocked object overload is required.");
            for (uint index = 0; index < count; index++)
            {
                var parameter = IL2CPP.il2cpp_method_get_param(method, index);
                if (parameter == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(parameter) != (index == 0) ||
                    IL2CPP.il2cpp_class_from_type(parameter) != objectClass)
                    throw new MissingMethodException("The native atomic reference signature differs from the generated contract.");
            }
            return compareExchange;
        }
    }
}
