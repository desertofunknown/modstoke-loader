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
        public static unsafe T ReadNativeField<T>(IntPtr field, Il2CppObjectBase owner)
        {
            var contract = NativeField<T>.Contracts.GetOrAdd(field, NativeField<T>.Resolve);
            var ownerPin = IntPtr.Zero;
            try
            {
                ownerPin = PinNativeFieldOwner(contract, owner);
                var ownerPointer = PinnedReferenceArgumentAddress(ownerPin);
                if (ArgumentType<T>.Kind == 0)
                {
                    // NativeType<T> proved an exact reference-free CLR layout. The local
                    // has the closed T's size, even when it exceeds a native pointer.
                    T value = default;
                    fixed (byte* storage = &Unsafe.As<T, byte>(ref value))
                    {
                        if (contract.Static) IL2CPP.il2cpp_field_static_get_value(field, storage);
                        else IL2CPP.il2cpp_field_get_value(ownerPointer, field, storage);
                    }
                    return value;
                }
                if (ArgumentType<T>.Kind == 2)
                {
                    var reference = IntPtr.Zero;
                    if (contract.Static) IL2CPP.il2cpp_field_static_get_value(field, &reference);
                    else IL2CPP.il2cpp_field_get_value(ownerPointer, field, &reference);
                    return IL2CPP.PointerToValueGeneric<T>(reference, false, true);
                }

                // Native structs need runtime-sized boxed storage, including reference-bearing
                // values. Do not box a second time or copy their payload through CLR storage.
                var pointer = IL2CPP.il2cpp_field_get_value_object(field, ownerPointer);
                return ReadNativeResult<T>(pointer);
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(ownerPin);
                GC.KeepAlive(owner);
            }
        }

        private static IntPtr PinNativeFieldOwner(NativeFieldContract contract, Il2CppObjectBase owner)
        {
            if (contract.Static)
            {
                if (owner != null) throw new ArgumentException("A static native field has no instance owner.", nameof(owner));
                IL2CPP.il2cpp_runtime_class_init(contract.Parent);
                return IntPtr.Zero;
            }
            if (owner == null) throw new NullReferenceException();
            if (!IL2CPP.il2cpp_class_is_assignable_from(contract.Parent, owner.ObjectClass))
                throw new InvalidCastException("The instance does not own the requested native field.");
            return PinArgumentObject(owner);
        }

        private readonly record struct NativeFieldContract(IntPtr Parent, int Attributes)
        {
            internal bool Static => (Attributes & 0x10) != 0;
        }

        private static class NativeField<T>
        {
            internal static readonly ConcurrentDictionary<IntPtr, NativeFieldContract> Contracts = new();

            internal static NativeFieldContract Resolve(IntPtr field)
            {
                if (field == IntPtr.Zero) throw new MissingFieldException("The native field is unavailable.");
                _ = ArgumentType<T>.Kind;
                var type = IL2CPP.il2cpp_field_get_type(field);
                if (type == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(type) ||
                    IL2CPP.il2cpp_class_from_type(type) != NativeType<T>.Class)
                    throw new NotSupportedException("The native field does not have its exact mapped type.");
                var parent = IL2CPP.il2cpp_field_get_parent(field);
                if (parent == IntPtr.Zero || IL2CPP.il2cpp_class_is_generic(parent) && !IL2CPP.il2cpp_class_is_inflated(parent))
                    throw new NotSupportedException("The native field needs a constructed declaring class.");
                return new NativeFieldContract(parent, IL2CPP.il2cpp_field_get_flags(field));
            }
        }
    }
}
