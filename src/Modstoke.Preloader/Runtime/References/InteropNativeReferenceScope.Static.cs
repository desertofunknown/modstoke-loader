// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace Modstoke.Preloader
{
    public sealed partial class InteropNativeReferenceScope
    {
        private static bool IsStaticField(InteropReferenceLocation location) =>
            location.Owner == null && location.Parent == null && location.Index == 0 &&
            location.RectangularRank == 0 && location.FieldOwner != null && location.FieldPointerName != null;

        private static IntPtr StaticAddress(InteropReferenceLocation location, IntPtr elementClass, int size, bool writable)
        {
            var field = InteropNativeReferenceCall.ReadMetadataPointer(location.FieldOwner, location.FieldPointerName);
            var flags = IL2CPP.il2cpp_field_get_flags(field);
            if ((flags & 0x50) != 0x10 || writable && (flags & 0x20) != 0 ||
                IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) != elementClass)
                throw new NotSupportedException("The static field must have the exact native type and requested access.");
            var owner = IL2CPP.il2cpp_field_get_parent(field);
            if (owner == IntPtr.Zero) throw new MissingFieldException("The native static field has no declaring class.");
            IL2CPP.il2cpp_runtime_class_init(owner);
            var offset = IL2CPP.il2cpp_field_get_offset(field);
            var capacity = il2cpp_class_get_data_size(owner);
            // Thread-static offsets use a sentinel and are not addresses in this class-owned block.
            if (size <= 0 || offset < 0 || offset > int.MaxValue || (ulong)offset + (uint)size > capacity)
                throw new NotSupportedException("The field is outside ordinary native static storage.");
            var data = il2cpp_class_get_static_field_data(owner);
            if (data == IntPtr.Zero) throw new MissingFieldException("The native static storage is unavailable.");
            // Static storage belongs to the loaded native class, not a movable managed owner.
            return new IntPtr(checked(data.ToInt64() + offset));
        }

        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint il2cpp_class_get_data_size(IntPtr nativeClass);

        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_class_get_static_field_data(IntPtr nativeClass);
    }
}
