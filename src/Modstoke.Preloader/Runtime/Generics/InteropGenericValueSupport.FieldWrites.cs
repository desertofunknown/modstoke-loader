// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        public static unsafe void WriteNativeField<T>(IntPtr field, Il2CppObjectBase owner, T value)
        {
            var contract = NativeField<T>.Contracts.GetOrAdd(field, NativeField<T>.Resolve);
            if ((contract.Attributes & 0x40) != 0)
                throw new FieldAccessException("A literal native field has no writable storage.");
            var ownerPin = IntPtr.Zero;
            var valuePin = IntPtr.Zero;
            try
            {
                ownerPin = PinNativeFieldOwner(contract, owner);
                var ownerPointer = PinnedReferenceArgumentAddress(ownerPin);
                var kind = ArgumentType<T>.Kind;
                if (kind == 0)
                {
                    // The field contract proved this CLR layout matches the native payload.
                    fixed (byte* payload = &Unsafe.As<T, byte>(ref value))
                        SetNativeField(field, contract, ownerPointer, payload);
                    return;
                }
                if (kind == 1)
                {
                    // A null value wrapper means default(T). The native setter zeroes the
                    // complete payload without allocating a temporary box.
                    if (value is null)
                    {
                        SetNativeField(field, contract, ownerPointer, null);
                        return;
                    }
                    valuePin = PinValueArgument(ref value);
                    SetNativeField(field, contract, ownerPointer,
                        (void*)InteropNativeValueStorage.PinnedArgumentAddress(valuePin));
                    return;
                }

                valuePin = PinReferenceArgument(value);
                // Reference setters take the object pointer itself, not a slot address.
                // Classify by the declared T: a boxed struct in an object field stays boxed.
                SetNativeField(field, contract, ownerPointer, (void*)PinnedReferenceArgumentAddress(valuePin));
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(valuePin);
                InteropNativeValueStorage.ReleaseArgument(ownerPin);
                GC.KeepAlive(value);
                GC.KeepAlive(owner);
            }
        }

        private static unsafe void SetNativeField(IntPtr field, NativeFieldContract contract, IntPtr owner, void* value)
        {
            if (contract.Static) IL2CPP.il2cpp_field_static_set_value(field, value);
            else IL2CPP.il2cpp_field_set_value(owner, field, value);
        }
    }
}
