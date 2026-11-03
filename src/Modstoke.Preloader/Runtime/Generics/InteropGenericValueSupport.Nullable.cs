// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        private static unsafe T UnboxNativeNullable<T>(object value)
        {
            var layout = NullableStorage<T>.Layout;
            if (value == null)
            {
                T empty = default;
                InitializeDefault(ref empty);
                return empty;
            }
            var source = GetNativeObject(value);
            var sourcePin = IntPtr.Zero;
            var targetPin = IntPtr.Zero;
            T target = default;
            try
            {
                if (source.ObjectClass == NativeType<T>.Class)
                    return NativeValueCopy<T>.Copy(IL2CPP.PointerToValueGeneric<T>(source.Pointer, false, true));
                if (source.ObjectClass != layout.ValueClass)
                    throw new InvalidCastException("The boxed object does not match the nullable underlying value type.");
                sourcePin = PinArgumentObject(source);
                targetPin = PinValueArgument(ref target);
                var destination = PinnedReferenceArgumentAddress(targetPin);
                var payload = IL2CPP.il2cpp_object_unbox(PinnedReferenceArgumentAddress(sourcePin));
                // Native field assignment also copies reference-bearing values with the GC
                // barrier, as in the existing aggregate/storage assignment paths.
                IL2CPP.il2cpp_field_set_value(destination, layout.ValueField, (void*)payload);
                byte hasValue = 1;
                IL2CPP.il2cpp_field_set_value(destination, layout.HasValueField, &hasValue);
                return target;
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(targetPin);
                InteropNativeValueStorage.ReleaseArgument(sourcePin);
                GC.KeepAlive(source);
                GC.KeepAlive(target);
            }
        }

        private readonly record struct NullableLayout(IntPtr HasValueField, IntPtr ValueField, IntPtr ValueClass);

        private static class NullableStorage<T>
        {
            internal static readonly NullableLayout Layout = Resolve();

            private static NullableLayout Resolve()
            {
                if (!NativeType<T>.IsNullable || ArgumentType<T>.Kind != 1)
                    throw new NotSupportedException("Nullable unboxing requires exact constructed native nullable storage.");
                var owner = NativeType<T>.Class;
                var hasValue = IntPtr.Zero;
                var value = IntPtr.Zero;
                var valueClass = IntPtr.Zero;
                var iterator = IntPtr.Zero;
                for (var field = IL2CPP.il2cpp_class_get_fields(owner, ref iterator);
                    field != IntPtr.Zero; field = IL2CPP.il2cpp_class_get_fields(owner, ref iterator))
                {
                    if ((IL2CPP.il2cpp_field_get_flags(field) & 0x50) != 0) continue;
                    var type = IL2CPP.il2cpp_field_get_type(field);
                    if (IL2CPP.il2cpp_field_get_parent(field) != owner || type == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(type))
                        throw new NotSupportedException("The native nullable field has no exact storage contract.");
                    var typeClass = IL2CPP.il2cpp_class_from_type(type);
                    var name = IL2CPP.il2cpp_field_get_name_(field);
                    if (name == "hasValue" && hasValue == IntPtr.Zero && typeClass == NativeType<bool>.Class)
                        hasValue = field;
                    else if (name == "value" && value == IntPtr.Zero && typeClass != IntPtr.Zero &&
                        IL2CPP.il2cpp_class_is_valuetype(typeClass) &&
                        (!IL2CPP.il2cpp_class_is_generic(typeClass) || IL2CPP.il2cpp_class_is_inflated(typeClass)))
                    {
                        value = field;
                        valueClass = typeClass;
                    }
                    else throw new NotSupportedException("The native nullable layout differs from its mapped fields.");
                }
                if (hasValue == IntPtr.Zero || value == IntPtr.Zero)
                    throw new MissingFieldException("The native nullable value/hasValue fields are unavailable.");
                return new NullableLayout(hasValue, value, valueClass);
            }
        }
    }
}
