// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static partial class InteropNativeValueStorage
    {
        public static void Initialize<T>(ref T destination) where T : Il2CppObjectBase =>
            Store(ref destination, default);

        public static unsafe void Store<T>(ref T destination, T source) where T : Il2CppObjectBase
        {
            if (destination == null)
            {
                destination = Copy(source);
                return;
            }

            var nativeClass = ValueType<T>.Class;
            // Resolve every field and copy range before changing any destination bytes.
            var layout = Assignment<T>.Layout;
            var target = destination;
            Validate(target, nativeClass);
            source ??= CreateDefault<T>(nativeClass);
            Validate(source, nativeClass);
            var targetHandle = IntPtr.Zero;
            var sourceHandle = IntPtr.Zero;
            try
            {
                targetHandle = IL2CPP.il2cpp_gchandle_new(target.Pointer, true);
                if (targetHandle == IntPtr.Zero) throw new InvalidOperationException("The native destination could not be pinned.");
                sourceHandle = IL2CPP.il2cpp_gchandle_new(source.Pointer, true);
                if (sourceHandle == IntPtr.Zero) throw new InvalidOperationException("The native source could not be pinned.");
                var targetObject = IL2CPP.il2cpp_gchandle_get_target(targetHandle);
                var sourceObject = IL2CPP.il2cpp_gchandle_get_target(sourceHandle);
                if (targetObject == IntPtr.Zero || sourceObject == IntPtr.Zero)
                    throw new InvalidOperationException("Pinned native value storage is unavailable.");
                if (targetObject == sourceObject) return;
                CopyPayload(IL2CPP.il2cpp_object_unbox(sourceObject), targetObject, layout);
            }
            finally
            {
                if (sourceHandle != IntPtr.Zero) IL2CPP.il2cpp_gchandle_free(sourceHandle);
                if (targetHandle != IntPtr.Zero) IL2CPP.il2cpp_gchandle_free(targetHandle);
                GC.KeepAlive(source);
                GC.KeepAlive(target);
            }
        }

        // The caller keeps the exact source location pinned for this synchronous copy.
        internal static T CopyFromAddress<T>(IntPtr source) where T : Il2CppObjectBase
        {
            if (source == IntPtr.Zero) throw new ArgumentNullException(nameof(source));
            var layout = Assignment<T>.Layout;
            var value = CreateDefault<T>(ValueType<T>.Class);
            using var scope = new InteropNativeReferenceScope(1);
            try
            {
                CopyPayload(source, scope.Pin(value), layout);
                return value;
            }
            finally { GC.KeepAlive(value); }
        }

        private static unsafe void CopyPayload(IntPtr source, IntPtr target, AssignmentLayout layout)
        {
            var sourceBytes = (byte*)source;
            var targetBytes = (byte*)IL2CPP.il2cpp_object_unbox(target);
            // Raw ranges exclude GC slots. Field setters copy reference-bearing values
            // into a real boxed owner, never an adjusted array or struct header.
            foreach (var range in layout.RawRanges)
                Buffer.MemoryCopy(sourceBytes + range.Offset, targetBytes + range.Offset, range.Size, range.Size);
            foreach (var field in layout.ReferenceFields)
            {
                var storage = sourceBytes + field.Offset;
                IL2CPP.il2cpp_field_set_value(target, field.Metadata,
                    field.IsReference ? *(void**)storage : storage);
            }
        }

        private readonly record struct ValueRange(int Offset, int Size);
        private readonly record struct ReferenceField(IntPtr Metadata, int Offset, int Size, bool IsReference);
        private sealed record AssignmentLayout(ValueRange[] RawRanges, ReferenceField[] ReferenceFields);

        private static class Assignment<T> where T : Il2CppObjectBase
        {
            internal static readonly AssignmentLayout Layout = CreateAssignmentLayout(ValueType<T>.Class);
        }

        private static AssignmentLayout CreateAssignmentLayout(IntPtr nativeClass)
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Native value assignment requires the verified x64 runtime contract.");
            uint alignment = 0;
            var size = IL2CPP.il2cpp_class_value_size(nativeClass, ref alignment);
            var header = il2cpp_object_header_size();
            if (size <= 0 || header == 0 || (long)header + size > IL2CPP.il2cpp_class_instance_size(nativeClass))
                throw new NotSupportedException("The native value has no bounded boxed payload layout.");
            if (!IL2CPP.il2cpp_class_has_references(nativeClass))
                return new AssignmentLayout(new[] { new ValueRange(0, size) }, Array.Empty<ReferenceField>());

            var references = new List<ReferenceField>();
            var iterator = IntPtr.Zero;
            for (var field = IL2CPP.il2cpp_class_get_fields(nativeClass, ref iterator);
                field != IntPtr.Zero; field = IL2CPP.il2cpp_class_get_fields(nativeClass, ref iterator))
            {
                if ((IL2CPP.il2cpp_field_get_flags(field) & 0x50) != 0) continue; // Static or literal.
                if (IL2CPP.il2cpp_field_get_parent(field) != nativeClass)
                    throw new NotSupportedException("The native value field does not belong to its exact boxed type.");
                var fieldType = IL2CPP.il2cpp_field_get_type(field);
                if (fieldType == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(fieldType))
                    throw new NotSupportedException("A native value field has an unresolved or byref layout.");
                var kind = IL2CPP.il2cpp_type_get_type(fieldType);
                // Primitive values, unmanaged pointers and function pointers contain no GC slots.
                if (kind is >= 0x02 and <= 0x0d or 0x0f or 0x18 or 0x19 or 0x1b) continue;
                if (kind is not (0x0e or 0x11 or 0x12 or 0x14 or 0x15 or 0x1c or 0x1d))
                    throw new NotSupportedException("The native value field has no closed copy contract.");
                var fieldClass = IL2CPP.il2cpp_class_from_type(fieldType);
                if (fieldClass == IntPtr.Zero ||
                    IL2CPP.il2cpp_class_is_generic(fieldClass) && !IL2CPP.il2cpp_class_is_inflated(fieldClass))
                    throw new NotSupportedException("The native value field type is not fully constructed.");
                var fieldSize = IntPtr.Size;
                var isReference = !IL2CPP.il2cpp_class_is_valuetype(fieldClass);
                if (!isReference)
                {
                    fieldSize = IL2CPP.il2cpp_class_value_size(fieldClass, ref alignment);
                    if (fieldSize <= 0) throw new NotSupportedException("The embedded native value has no payload size.");
                    if (!IL2CPP.il2cpp_class_has_references(fieldClass)) continue;
                }
                var offset = IL2CPP.il2cpp_field_get_offset(field);
                if (offset < header || offset > int.MaxValue || (long)offset - header + fieldSize > size)
                    throw new NotSupportedException("The reference-bearing field lies outside the native value payload.");
                references.Add(new ReferenceField(field, checked((int)(offset - header)), fieldSize, isReference));
            }
            if (references.Count == 0)
                throw new NotSupportedException("The native value reports references but exposes no reference-bearing fields.");

            references.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
            var ranges = new List<ValueRange>();
            var end = 0;
            foreach (var field in references)
            {
                if (field.Offset > end) ranges.Add(new ValueRange(end, field.Offset - end));
                // Explicit-layout overlaps are copied from the same pinned source. Their union
                // must stay out of raw ranges, even when one field is contained in another.
                end = Math.Max(end, checked(field.Offset + field.Size));
            }
            if (end < size) ranges.Add(new ValueRange(end, size - end));
            return new AssignmentLayout(ranges.ToArray(), references.ToArray());
        }

        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint il2cpp_object_header_size();
    }
}
