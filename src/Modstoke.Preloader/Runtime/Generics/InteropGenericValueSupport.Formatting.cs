// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropGenericValueSupport
    {
        public static string ConstrainedObjectToString<T, TObject>(ref T location, string slotField)
            where TObject : Il2CppObjectBase
        {
            var target = FormattingTarget<T, TObject>.Get(slotField);
            Il2CppObjectBase result;
            if (target.ValueOverride)
                result = InvokeConstrainedValue(ref location, target.Method);
            else
                result = InvokeBoxedFormatting<T, TObject>(ref location, target.Method);
            try { return UnboxAny<string>(result); }
            finally { GC.KeepAlive(result); }
        }

        private static unsafe Il2CppObjectBase InvokeBoxedFormatting<T, TObject>(ref T location, IntPtr method)
            where TObject : Il2CppObjectBase
        {
            var reference = ArgumentType<T>.Kind == 2;
            var receiver = reference ? ReferenceReceiver<T, TObject>(ref location) : Box<T, TObject>(location);
            if (receiver == null) throw new NullReferenceException();
            var handle = PinArgumentObject(receiver);
            try
            {
                var instance = PinnedReferenceArgumentAddress(handle);
                if (reference) method = IL2CPP.il2cpp_object_get_virtual_method(instance, method);
                if (method == IntPtr.Zero) throw new MissingMethodException("The native ToString implementation is unavailable.");
                // A reference may itself hold a boxed value. Such an override mutates that
                // box; inherited Object/ValueType/Enum implementations receive the object.
                var owner = IL2CPP.il2cpp_method_get_class(method);
                var address = IL2CPP.il2cpp_class_is_valuetype(owner) ? IL2CPP.il2cpp_object_unbox(instance) : instance;
                var exception = IntPtr.Zero;
                var pointer = IL2CPP.il2cpp_runtime_invoke(method, address, null, ref exception);
                InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
                return pointer == IntPtr.Zero ? null : new Il2CppObjectBase(pointer);
            }
            finally
            {
                InteropNativeValueStorage.ReleaseArgument(handle);
                GC.KeepAlive(receiver);
                GC.KeepAlive(location);
            }
        }

        private readonly record struct FormattingMethod(IntPtr Method, bool ValueOverride);

        private static class FormattingTarget<T, TObject> where TObject : Il2CppObjectBase
        {
            private static readonly ConcurrentDictionary<string, FormattingMethod> methods = new(StringComparer.Ordinal);

            internal static FormattingMethod Get(string field)
            {
                ArgumentException.ThrowIfNullOrEmpty(field);
                return methods.GetOrAdd(field, static name => Resolve(name));
            }

            private static FormattingMethod Resolve(string field)
            {
                var kind = ArgumentType<T>.Kind;
                var receiverClass = NativeType<T>.Class;
                var objectClass = IL2CPP.il2cpp_class_from_name(IL2CPP.il2cpp_get_corlib(), "System", "Object");
                if (objectClass == IntPtr.Zero || NativeType<TObject>.Class != objectClass)
                    throw new NotSupportedException("Constrained formatting requires the native Object contract.");
                var slot = InteropNativeReferenceCall.ReadMetadataPointer(typeof(TObject), field);
                Validate(slot);
                uint flags = 0;
                if (IL2CPP.il2cpp_method_get_class(slot) != objectClass ||
                    (IL2CPP.il2cpp_method_get_flags(slot, ref flags) & 0x40) == 0)
                    throw new MissingMethodException("The formatting slot is not native Object.ToString.");
                if (kind == 2) return new FormattingMethod(slot, false);

                // Resolve value dispatch once on actual zeroed storage, including Nullable.
                // Boxing a Nullable first would erase its own ToString implementation.
                var pointer = IL2CPP.il2cpp_object_new(receiverClass);
                if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                var receiver = new Il2CppObjectBase(pointer);
                var handle = IntPtr.Zero;
                try
                {
                    handle = PinArgumentObject(receiver);
                    var method = IL2CPP.il2cpp_object_get_virtual_method(PinnedReferenceArgumentAddress(handle), slot);
                    Validate(method);
                    var owner = IL2CPP.il2cpp_method_get_class(method);
                    if (owner == receiverClass) return new FormattingMethod(method, true);
                    if (IL2CPP.il2cpp_class_is_valuetype(owner) ||
                        !IL2CPP.il2cpp_class_is_assignable_from(owner, receiverClass))
                        throw new MissingMethodException("The value formatting implementation has an unrelated declaring type.");
                    return new FormattingMethod(method, false);
                }
                finally
                {
                    InteropNativeValueStorage.ReleaseArgument(handle);
                    GC.KeepAlive(receiver);
                }
            }

            private static void Validate(IntPtr method)
            {
                uint flags = 0;
                if (method == IntPtr.Zero || IL2CPP.il2cpp_method_get_name_(method) != "ToString" ||
                    IL2CPP.il2cpp_method_is_generic(method) || IL2CPP.il2cpp_method_get_param_count(method) != 0 ||
                    (IL2CPP.il2cpp_method_get_flags(method, ref flags) & (0x10 | 0x400)) != 0)
                    throw new MissingMethodException("The native formatting method is not a concrete parameterless instance ToString.");
                var result = IL2CPP.il2cpp_method_get_return_type(method);
                if (result == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(result) ||
                    IL2CPP.il2cpp_type_get_type(result) != 0x0e ||
                    IL2CPP.il2cpp_class_from_type(result) != NativeType<string>.Class)
                    throw new MissingMethodException("The native formatting method does not return String.");
            }
        }
    }
}
