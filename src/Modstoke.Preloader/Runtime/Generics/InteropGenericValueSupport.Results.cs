// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        public static T ReadNativeResult<T>(IntPtr pointer)
        {
            // Reference results cannot be boxed nullable values. Preserve the pooled-wrapper
            // path for common calls such as GetComponent<T>, without an extra managed owner.
            if (pointer != IntPtr.Zero && !IL2CPP.il2cpp_class_is_valuetype(IL2CPP.il2cpp_object_get_class(pointer)))
                return IL2CPP.PointerToValueGeneric<T>(pointer, false, true);

            // Root the result before resolving T, which can initialize native classes.
            var result = pointer == IntPtr.Zero ? null : new Il2CppObjectBase(pointer);
            return ReadOwnedNativeResult<T>(result);
        }

        internal static T ReadOwnedNativeResult<T>(Il2CppObjectBase result)
        {
            try
            {
                _ = ArgumentType<T>.Kind;
                if (NativeType<T>.IsNullable) return UnboxNativeNullable<T>(result);
                if (result == null && NativeType<T>.IsNativeValue) throw new NullReferenceException();
                // Native invocation and field-get boxing already provide independent value storage.
                return Read<T>(result, false);
            }
            finally { GC.KeepAlive(result); }
        }
    }
}
