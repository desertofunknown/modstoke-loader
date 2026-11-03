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
        public static IntPtr Constructor<T>(IntPtr method, int parameterCount, ref IntPtr validated)
            where T : Il2CppObjectBase
        {
            if (method == IntPtr.Zero) throw new MissingMethodException("The native value constructor is unavailable.");
            if (Volatile.Read(ref validated) == method) return method;
            var owner = IL2CPP.il2cpp_method_get_class(method);
            var result = IL2CPP.il2cpp_method_get_return_type(method);
            uint implementationFlags = 0;
            if (owner == IntPtr.Zero || result == IntPtr.Zero || owner != ValueType<T>.Class ||
                (IL2CPP.il2cpp_method_get_flags(method, ref implementationFlags) & 0x10) != 0 ||
                IL2CPP.il2cpp_method_is_generic(method) || IL2CPP.il2cpp_method_get_name_(method) != ".ctor" ||
                IL2CPP.il2cpp_type_is_byref(result) || IL2CPP.il2cpp_type_get_type(result) != 0x01 ||
                parameterCount < 0 || IL2CPP.il2cpp_method_get_param_count(method) != parameterCount)
                throw new MissingMethodException("The mapped method is not the expected native value constructor.");
            for (uint index = 0; index < parameterCount; index++)
            {
                var parameter = IL2CPP.il2cpp_method_get_param(method, index);
                if (parameter == IntPtr.Zero || IL2CPP.il2cpp_class_from_type(parameter) == IntPtr.Zero)
                    throw new MissingMethodException("The native value constructor has unresolved parameter metadata.");
            }
            // Each generated closed-owner field belongs to exactly one immutable constructor signature.
            Volatile.Write(ref validated, method);
            return method;
        }
    }
}
