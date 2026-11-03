// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropNativeValueStorage
    {
        public static IntPtr Method<T>(IntPtr method, string name, int parameterCount, ref IntPtr validated)
            where T : Il2CppObjectBase
        {
            if (method == IntPtr.Zero) throw new MissingMethodException("The native value method is unavailable.");
            if (Volatile.Read(ref validated) == method) return method;
            return ValidateMethod(method, ValueType<T>.Class, name, parameterCount, false, ref validated);
        }

        public static IntPtr ReferenceMethod<T>(IntPtr method, string name, int parameterCount, ref IntPtr validated)
            where T : Il2CppObjectBase
        {
            if (method == IntPtr.Zero) throw new MissingMethodException("The native reference-object method is unavailable.");
            if (Volatile.Read(ref validated) == method) return method;
            InteropGenericValueSupport.RequireNativeType<T>();
            var owner = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (owner == IntPtr.Zero || typeof(T).ContainsGenericParameters || typeof(T).IsGenericType ||
                IL2CPP.il2cpp_class_is_valuetype(owner) || IL2CPP.il2cpp_class_is_generic(owner) ||
                IL2CPP.il2cpp_class_is_inflated(owner))
                throw new NotSupportedException("The scoped method requires an exact nongeneric native reference owner.");
            return ValidateMethod(method, owner, name, parameterCount, false, ref validated);
        }

        public static IntPtr StaticMethod<T>(IntPtr method, string name, int parameterCount, ref IntPtr validated)
        {
            if (method == IntPtr.Zero) throw new MissingMethodException("The native static method is unavailable.");
            if (Volatile.Read(ref validated) == method) return method;
            var owner = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (owner == IntPtr.Zero || typeof(T).IsGenericType || typeof(T).ContainsGenericParameters ||
                IL2CPP.il2cpp_class_is_generic(owner) || IL2CPP.il2cpp_class_is_inflated(owner))
                throw new NotSupportedException("The scoped static method requires an exact nongeneric native owner.");
            return ValidateMethod(method, owner, name, parameterCount, true, ref validated);
        }

        public static IntPtr ClosedGenericStaticMethod<T>(IntPtr method, IntPtr definition,
            string name, int parameterCount, ref IntPtr validated)
        {
            if (method == IntPtr.Zero || definition == IntPtr.Zero)
                throw new MissingMethodException("The closed native generic method is unavailable.");
            if (Volatile.Read(ref validated) == method) return method;
            if (!IL2CPP.il2cpp_method_is_generic(definition) || IL2CPP.il2cpp_method_is_inflated(definition) ||
                IL2CPP.il2cpp_method_is_generic(method) || !IL2CPP.il2cpp_method_is_inflated(method) ||
                IL2CPP.il2cpp_method_get_class(method) != IL2CPP.il2cpp_method_get_class(definition) ||
                IL2CPP.il2cpp_method_get_token(definition) == 0 ||
                IL2CPP.il2cpp_method_get_token(method) != IL2CPP.il2cpp_method_get_token(definition))
                throw new MissingMethodException("The closed native method does not match its mapped generic definition.");
            return StaticMethod<T>(method, name, parameterCount, ref validated);
        }

        private static IntPtr ValidateMethod(IntPtr method, IntPtr expectedOwner, string name,
            int parameterCount, bool isStatic, ref IntPtr validated)
        {
            var owner = IL2CPP.il2cpp_method_get_class(method);
            var result = IL2CPP.il2cpp_method_get_return_type(method);
            uint implementationFlags = 0;
            // The method pointer comes from the exact generated mapping, not a name/arity lookup.
            if (owner == IntPtr.Zero || owner != expectedOwner || result == IntPtr.Zero ||
                (IL2CPP.il2cpp_method_get_flags(method, ref implementationFlags) & (0x10 | 0x40 | 0x400)) !=
                    (isStatic ? 0x10 : 0) ||
                IL2CPP.il2cpp_method_is_generic(method) || string.IsNullOrEmpty(name) || name is ".ctor" or ".cctor" ||
                IL2CPP.il2cpp_method_get_name_(method) != name || parameterCount < 0 ||
                IL2CPP.il2cpp_method_get_param_count(method) != parameterCount)
                throw new MissingMethodException("The mapped method is not the expected native method.");
            var returnKind = IL2CPP.il2cpp_type_get_type(result);
            if (IL2CPP.il2cpp_type_is_byref(result) || returnKind is 0x0f or 0x16 or 0x1b ||
                returnKind != 0x01 && IL2CPP.il2cpp_class_from_type(result) == IntPtr.Zero)
                throw new NotSupportedException("The native return value has no supported scoped storage contract.");
            for (uint index = 0; index < parameterCount; index++)
            {
                var parameter = IL2CPP.il2cpp_method_get_param(method, index);
                if (parameter == IntPtr.Zero || IL2CPP.il2cpp_class_from_type(parameter) == IntPtr.Zero)
                    throw new MissingMethodException("The native method has unresolved parameter metadata.");
            }
            Volatile.Write(ref validated, method);
            return method;
        }
    }
}
