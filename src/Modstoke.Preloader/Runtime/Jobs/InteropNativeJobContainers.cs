// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Startup;

namespace Modstoke.Preloader
{
    public static unsafe class InteropNativeJobContainers
    {
        /// <summary>Captures the contiguous JobHandle inputs of a native container operation.</summary>
        /// <remarks>The caller must keep the unboxed payload pinned and its handle buffer readable during this call.</remarks>
        public static NativeJobHandles.Scope Capture<TContainer>(IntPtr payload)
        {
            if (NativeJobHandles.TrackedHandleCount == 0) return null;
            if (payload == IntPtr.Zero) throw new ArgumentNullException(nameof(payload));
            var layout = Container<TContainer>.Layout;
            if (IL2CPP.il2cpp_domain_get() != layout.Domain ||
                Il2CppClassPointerStore<TContainer>.NativeClassPtr != layout.Class ||
                !ReferenceEquals(Il2CppInteropRuntime.Instance, layout.Runtime))
                throw new NotSupportedException("Native job container layouts cannot survive runtime, domain or class replacement.");
            var buffer = Unsafe.ReadUnaligned<IntPtr>((byte*)payload + layout.BufferOffset);
            var count = Unsafe.ReadUnaligned<int>((byte*)payload + layout.LengthOffset);
            // Unity's NativeSlice combine uses only buffer and length, ignoring m_Stride.
            return NativeJobHandles.CaptureBuffer(buffer, count);
        }

        private readonly record struct ContainerLayout(Il2CppInteropRuntime Runtime, IntPtr Domain,
            IntPtr Class, int BufferOffset, int LengthOffset);

        private static class Container<TContainer>
        {
            internal static readonly ContainerLayout Layout;

            // Keep metadata initialization behind Capture's empty-registry fast path.
            static Container() => Layout = Resolve();

            private static ContainerLayout Resolve()
            {
                var type = typeof(TContainer);
                if (!type.IsConstructedGenericType || type.ContainsGenericParameters ||
                    type.Assembly.GetName().Name != "UnityEngine.CoreModule" ||
                    type.GetGenericTypeDefinition().FullName is not
                        ("Unity.Collections.NativeArray`1" or "Unity.Collections.NativeSlice`1"))
                    throw new NotSupportedException("Job container capture requires an exact Unity NativeArray or NativeSlice wrapper.");
                var arguments = type.GetGenericArguments();
                if (arguments.Length != 1 || arguments[0].FullName != "Unity.Jobs.JobHandle" ||
                    arguments[0].Assembly.GetName().Name != "UnityEngine.CoreModule")
                    throw new NotSupportedException("Job container capture requires the exact Unity JobHandle element type.");

                var runtime = Il2CppInteropRuntime.Instance;
                var domain = IL2CPP.il2cpp_domain_get();
                if (domain == IntPtr.Zero)
                    throw new InvalidOperationException("No active IL2CPP domain is available.");
                var nativeClass = Il2CppClassPointerStore<TContainer>.NativeClassPtr;
                if (nativeClass == IntPtr.Zero || !IL2CPP.il2cpp_class_is_valuetype(nativeClass) ||
                    !IL2CPP.il2cpp_class_is_inflated(nativeClass))
                    throw new NotSupportedException("Job container capture requires an exact constructed native value type.");
                uint alignment = 0;
                var size = IL2CPP.il2cpp_class_value_size(nativeClass, ref alignment);
                var header = il2cpp_object_header_size();
                if (size <= 0 || header == 0 || (long)header + size > IL2CPP.il2cpp_class_instance_size(nativeClass))
                    throw new NotSupportedException("The native job container has no bounded boxed payload layout.");
                var buffer = ResolveField(nativeClass, "m_Buffer", Il2CppTypeEnum.IL2CPP_TYPE_PTR,
                    IntPtr.Size, header, size);
                var length = ResolveField(nativeClass, "m_Length", Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    sizeof(int), header, size);
                return new ContainerLayout(runtime, domain, nativeClass, buffer, length);
            }
        }

        private static int ResolveField(IntPtr nativeClass, string name, Il2CppTypeEnum kind,
            int fieldSize, uint header, int payloadSize)
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(nativeClass, name);
            if (field == IntPtr.Zero)
                throw new MissingFieldException($"The native job container field {name} is unavailable.");
            if (IL2CPP.il2cpp_field_get_parent(field) != nativeClass ||
                (IL2CPP.il2cpp_field_get_flags(field) & 0x50) != 0)
                throw new NotSupportedException($"Native job container field {name} is not an exact instance field.");
            var fieldType = IL2CPP.il2cpp_field_get_type(field);
            if (fieldType == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(fieldType) ||
                IL2CPP.il2cpp_type_get_type(fieldType) != (int)kind)
                throw new NotSupportedException($"Native job container field {name} has an incompatible type.");
            if (kind == Il2CppTypeEnum.IL2CPP_TYPE_PTR)
            {
                var signature = UnityVersionHandler.Wrap((Il2CppTypeStruct*)fieldType);
                if (signature.ValueType || signature.Data == IntPtr.Zero)
                    throw new NotSupportedException("The native job container buffer must be a void pointer.");
                var element = UnityVersionHandler.Wrap((Il2CppTypeStruct*)signature.Data);
                if (element.ByRef || element.Type != Il2CppTypeEnum.IL2CPP_TYPE_VOID)
                    throw new NotSupportedException("The native job container buffer must point to void.");
            }
            var offset = IL2CPP.il2cpp_field_get_offset(field);
            if (offset < header || offset > int.MaxValue || (long)offset - header + fieldSize > payloadSize)
                throw new NotSupportedException($"Native job container field {name} lies outside its value payload.");
            return checked((int)(offset - header));
        }

        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint il2cpp_object_header_size();
    }
}
