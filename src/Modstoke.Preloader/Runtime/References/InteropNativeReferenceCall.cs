// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Generated bodies pass native owners or location descriptors, never CLR byrefs into native storage.
    // Native addresses exist only inside this synchronous, finally-protected invocation.
    public static class InteropNativeReferenceCall
    {
        private static readonly ConditionalWeakTable<Type, ConcurrentDictionary<string, IntPtr>> metadataPointers = new();

        public static Il2CppObjectBase Invoke(Type declaringType, string methodPointerName,
            object receiver, bool virtualDispatch, object[] arguments) =>
            InvokeCore(declaringType, methodPointerName, receiver, virtualDispatch, arguments, constructor: false);

        public static void Construct(Type declaringType, string methodPointerName, object receiver, object[] arguments) =>
            InvokeCore(declaringType, methodPointerName, receiver, false, arguments, constructor: true);

        private static unsafe Il2CppObjectBase InvokeCore(Type declaringType, string methodPointerName,
            object receiver, bool virtualDispatch, object[] arguments, bool constructor)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Scoped native references require the verified x64 runtime contract.");
            var method = ReadMetadataPointer(declaringType, methodPointerName);
            using var scope = new InteropNativeReferenceScope(arguments.Length + 1);
            try
            {
                uint implementationFlags = 0;
                var isStatic = (IL2CPP.il2cpp_method_get_flags(method, ref implementationFlags) & 0x10) != 0;
                var returnType = IL2CPP.il2cpp_method_get_return_type(method);
                var ownerClass = IL2CPP.il2cpp_method_get_class(method);
                if (ownerClass == IntPtr.Zero || returnType == IntPtr.Zero)
                    throw new MissingMethodException("The mapped native method has no declaring class or return metadata.");
                var returnKind = IL2CPP.il2cpp_type_get_type(returnType);
                if (IL2CPP.il2cpp_type_is_byref(returnType) || returnKind is 0x0f or 0x16 or 0x1b)
                    throw new NotSupportedException("A returned native reference or pointer has no owner/lifetime contract and cannot leave this scope.");
                if (IL2CPP.il2cpp_method_get_param_count(method) != arguments.Length)
                    throw new MissingMethodException("The native method parameter count differs from the scoped call.");
                if (constructor && (isStatic ||
                    !IL2CPP.il2cpp_class_is_valuetype(ownerClass) || IL2CPP.il2cpp_method_is_generic(method) ||
                    IL2CPP.il2cpp_method_get_name_(method) != ".ctor" ||
                    returnKind != 0x01))
                    throw new MissingMethodException("The mapped method is not an exact native value constructor.");
                var instance = IntPtr.Zero;
                if (isStatic)
                {
                    if (receiver != null || virtualDispatch)
                        throw new NotSupportedException("A static native call cannot have a receiver.");
                }
                else if (receiver is InteropReferenceLocation location)
                {
                    if (virtualDispatch)
                        throw new NotSupportedException("A native value location cannot use reference-object virtual dispatch.");
                    instance = constructor ? scope.StorageAddress(location, ownerClass, writable: true) :
                        scope.WritableAddress(location, ownerClass);
                }
                else
                {
                    if (receiver == null) throw new NullReferenceException();
                    if (receiver is not Il2CppObjectBase native ||
                        !constructor && IL2CPP.il2cpp_class_is_valuetype(ownerClass))
                        throw new NotSupportedException("A native value receiver requires its original location, not a detached box.");
                    instance = scope.Pin(native);
                    var actualClass = IL2CPP.il2cpp_object_get_class(instance);
                    if (constructor ? actualClass != ownerClass : !IL2CPP.il2cpp_class_is_assignable_from(ownerClass, actualClass))
                        throw new InvalidCastException("The receiver does not match the native method owner.");
                    if (constructor) instance = IL2CPP.il2cpp_object_unbox(instance);
                    else if (virtualDispatch)
                    {
                        method = IL2CPP.il2cpp_object_get_virtual_method(instance, method);
                        if (method == IntPtr.Zero) throw new MissingMethodException("The native virtual method is unavailable.");
                    }
                }

                // Each table entry is the actual byref slot, an unboxed by-value buffer, or
                // a by-value object pointer. Equal locations deliberately produce equal addresses.
                Span<IntPtr> pointers = arguments.Length <= 16 ? stackalloc IntPtr[arguments.Length] : new IntPtr[arguments.Length];
                for (var index = 0; index < arguments.Length; index++)
                {
                    pointers[index] = IntPtr.Zero;
                    var parameter = IL2CPP.il2cpp_method_get_param(method, (uint)index);
                    var parameterClass = IL2CPP.il2cpp_class_from_type(parameter);
                    if (parameterClass == IntPtr.Zero)
                        throw new NotSupportedException("A native argument has no resolved class.");
                    if (IL2CPP.il2cpp_type_is_byref(parameter))
                    {
                        if (arguments[index] is not InteropReferenceLocation argumentLocation)
                            throw new NotSupportedException("Every native byref argument must carry its actual owner and location.");
                        pointers[index] = scope.WritableAddress(argumentLocation, parameterClass);
                    }
                    else if (arguments[index] == null)
                    {
                        if (IL2CPP.il2cpp_class_is_valuetype(parameterClass)) throw new NullReferenceException();
                    }
                    else
                    {
                        if (arguments[index] is not Il2CppObjectBase value)
                            throw new NotSupportedException("A by-value native argument requires a rooted native object or value box.");
                        var pointer = scope.Pin(value);
                        var actualClass = IL2CPP.il2cpp_object_get_class(pointer);
                        var isValue = IL2CPP.il2cpp_class_is_valuetype(parameterClass);
                        if (isValue ? actualClass != parameterClass :
                            !IL2CPP.il2cpp_class_is_assignable_from(parameterClass, actualClass))
                            throw new InvalidCastException("The argument does not match the native parameter class.");
                        pointers[index] = isValue ? IL2CPP.il2cpp_object_unbox(pointer) : pointer;
                    }
                }
                var exception = IntPtr.Zero;
                IntPtr result;
                fixed (IntPtr* table = pointers)
                    result = IL2CPP.il2cpp_runtime_invoke(method, instance, (void**)table, ref exception);
                // Root the returned native object before releasing pins or converting its value.
                // Preserve the existing native exception identity used by translated catch/filter.
                InteropExceptionSupport.RaiseExceptionIfNecessary(exception);
                return returnKind == 0x01 || result == IntPtr.Zero ? null : new Il2CppObjectBase(result);
            }
            finally
            {
                GC.KeepAlive(receiver);
                GC.KeepAlive(arguments);
                GC.KeepAlive(declaringType);
            }
        }

        internal static IntPtr ReadMetadataPointer(Type declaringType, string fieldName)
        {
            ArgumentNullException.ThrowIfNull(declaringType);
            ArgumentException.ThrowIfNullOrEmpty(fieldName);
            return metadataPointers.GetValue(declaringType, static _ => new(StringComparer.Ordinal))
                .GetOrAdd(fieldName, static (name, owner) => ResolveMetadataPointer(owner, name), declaringType);
        }

        private static IntPtr ResolveMetadataPointer(Type declaringType, string fieldName)
        {
            RuntimeHelpers.RunClassConstructor(declaringType.TypeHandle);
            // The name comes from the exact generated mapping, not a native name/arity guess.
            var field = declaringType.GetField(fieldName,
                BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.FieldType != typeof(IntPtr) || field.GetValue(null) is not IntPtr pointer || pointer == IntPtr.Zero)
                throw new MissingMemberException(declaringType.FullName, fieldName);
            return pointer;
        }
    }
}
