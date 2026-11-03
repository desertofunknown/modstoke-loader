// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        public static unsafe void ConstrainedInterfaceReference<T, TInterface, TArgument>(
            InteropReferenceLocation receiver, InteropReferenceLocation argument, string field)
            where TInterface : Il2CppObjectBase
        {
            var target = InterfaceTarget<T, TInterface>.Get(field, 1);
            if (!NativeType<T>.IsNativeValue || !target.ValueOverride || target.ReturnKind != 0x01 ||
                !target.First.ByReference || target.First.Class != NativeType<TArgument>.Class)
                throw new NotSupportedException("The interface call requires an exact native value receiver and byref argument.");

            using var scope = new InteropNativeReferenceScope(2);
            try
            {
                var address = scope.StorageAddress(receiver, NativeType<T>.Class, writable: true);
                var argumentAddress = target.First.Value
                    ? scope.StorageAddress(argument, target.First.Class, writable: true)
                    : scope.ReferenceAddress(argument, target.First.Class);
                // Equal field/array locations resolve to the same address. Native mutations,
                // including those preceding an exception, never need detached-value writeback.
                var exception = IntPtr.Zero;
                IL2CPP.il2cpp_runtime_invoke(target.Method, address, (void**)&argumentAddress, ref exception);
                InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
            }
            finally
            {
                GC.KeepAlive(receiver);
                GC.KeepAlive(argument);
            }
        }
    }
}
