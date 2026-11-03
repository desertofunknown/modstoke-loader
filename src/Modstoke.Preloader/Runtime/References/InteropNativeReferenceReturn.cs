// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceReturn
    {
        // The translator proves that both native return paths alias the current holder's
        // value field. This is not a general conversion of returned native references.
        public static unsafe InteropReferenceLocation Acquire<TReceiver, THolder, TValue>(
            InteropReferenceLocation receiver, string methodPointerName, string ownerFieldPointerName,
            string valueFieldPointerName)
            where TReceiver : Il2CppObjectBase
            where THolder : Il2CppObjectBase
        {
            ArgumentNullException.ThrowIfNull(receiver);
            var method = Contract<TReceiver, THolder, TValue>.Methods.GetOrAdd(
                (methodPointerName, ownerFieldPointerName, valueFieldPointerName),
                static fields => Contract<TReceiver, THolder, TValue>.Resolve(fields));
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                var instance = scope.ValueReceiver(receiver, method);
                var holderSlot = InteropNativeReferenceStorage.Field<TReceiver, THolder>(
                    receiver, ownerFieldPointerName, isReadOnly: true);
                var exception = IntPtr.Zero;
                // runtime_invoke boxes a byref value return. That allocation is not its
                // original storage, and failure may occur after the receiver has changed.
                _ = IL2CPP.il2cpp_runtime_invoke(method, instance, null, ref exception);
                InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
                var holder = InteropNativeReferenceObject.Read<THolder>(holderSlot);
                if (holder == null) throw new NullReferenceException();
                return InteropNativeReferenceStorage.Validate<TValue>(new InteropReferenceLocation(
                    holder, 0, typeof(THolder), valueFieldPointerName));
            }
            finally { GC.KeepAlive(receiver); }
        }

        private static class Contract<TReceiver, THolder, TValue>
            where TReceiver : Il2CppObjectBase
            where THolder : Il2CppObjectBase
        {
            internal static readonly ConcurrentDictionary<(string Method, string Owner, string Value), IntPtr> Methods = new();

            internal static unsafe IntPtr Resolve((string Method, string Owner, string Value) fields)
            {
                InteropGenericValueSupport.RequireNativeType<TReceiver>();
                InteropGenericValueSupport.RequireNativeType<THolder>();
                InteropGenericValueSupport.RequireNativeType<TValue>();
                var receiver = Il2CppClassPointerStore<TReceiver>.NativeClassPtr;
                var holder = Il2CppClassPointerStore<THolder>.NativeClassPtr;
                var value = Il2CppClassPointerStore<TValue>.NativeClassPtr;
                if (receiver == IntPtr.Zero || holder == IntPtr.Zero || value == IntPtr.Zero ||
                    !IL2CPP.il2cpp_class_is_valuetype(receiver) || IL2CPP.il2cpp_class_is_valuetype(holder) ||
                    !IL2CPP.il2cpp_class_is_valuetype(value))
                    throw new NotSupportedException("The returned field requires exact native receiver, holder and value identities.");
                var method = InteropNativeReferenceCall.ReadMetadataPointer(typeof(TReceiver), fields.Method);
                var result = IL2CPP.il2cpp_method_get_return_type(method);
                uint implementationFlags = 0;
                if (IL2CPP.il2cpp_method_get_class(method) != receiver ||
                    (IL2CPP.il2cpp_method_get_flags(method, ref implementationFlags) & (0x10 | 0x40 | 0x400)) != 0 ||
                    IL2CPP.il2cpp_method_is_generic(method) || IL2CPP.il2cpp_method_get_param_count(method) != 0 ||
                    result == IntPtr.Zero || !IL2CPP.il2cpp_type_is_byref(result) ||
                    IL2CPP.il2cpp_class_from_type(result) != value)
                    throw new MissingMethodException("The mapped method does not return the expected native value reference.");
                var info = UnityVersionHandler.Wrap((Il2CppMethodInfo*)method);
                if (info.MethodPointer == IntPtr.Zero || info.InvokerMethod == IntPtr.Zero)
                    throw new MissingMethodException("The native reference-return method has no callable implementation.");
                ValidateField(typeof(TReceiver), fields.Owner, receiver, holder);
                ValidateField(typeof(THolder), fields.Value, holder, value);
                return method;
            }

            private static void ValidateField(Type owner, string name, IntPtr ownerClass, IntPtr valueClass)
            {
                var field = InteropNativeReferenceCall.ReadMetadataPointer(owner, name);
                if (IL2CPP.il2cpp_field_get_parent(field) != ownerClass ||
                    (IL2CPP.il2cpp_field_get_flags(field) & 0x70) != 0 ||
                    IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) != valueClass)
                    throw new MissingFieldException("The returned reference does not identify an exact writable native field.");
            }
        }
    }
}
