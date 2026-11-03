// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Preloader
{
    public static class InteropNativeArrayBuffer
    {
        // The caller must release the returned handle after the synchronous native copy.
        public static IntPtr Pin<T>(Il2CppStructArray<T> array, out IntPtr data) where T : unmanaged =>
            PinGeneric(array, out data);

        // Restored native signatures can erase struct constraints; validate CLR storage at runtime too.
        public static IntPtr PinGeneric<T>(Il2CppArrayBase<T> array, out IntPtr data)
        {
            ArgumentNullException.ThrowIfNull(array);
            data = IntPtr.Zero;
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Native array buffers require the verified x64 layout.");
            var handle = IntPtr.Zero;
            try
            {
                handle = IL2CPP.il2cpp_gchandle_new(array.Pointer, true);
                if (handle == IntPtr.Zero) throw new InvalidOperationException("The native array could not be pinned.");
                var pointer = IL2CPP.il2cpp_gchandle_get_target(handle);
                if (pointer == IntPtr.Zero) throw new InvalidOperationException("The pinned native array is unavailable.");
                ValidateLayout<T>(pointer);
                if (IL2CPP.il2cpp_array_length(pointer) != 0)
                    data = new IntPtr(checked(pointer.ToInt64() + il2cpp_array_object_header_size()));
                return handle;
            }
            catch
            {
                InteropNativeValueStorage.ReleaseArgument(handle);
                throw;
            }
            finally { GC.KeepAlive(array); }
        }

        public static IntPtr ElementAddress<T>(IntPtr handle, int index, uint count)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (handle == IntPtr.Zero)
            {
                if (index != 0 || count != 0) throw new ArgumentOutOfRangeException(nameof(index));
                return IntPtr.Zero;
            }
            var pointer = IL2CPP.il2cpp_gchandle_get_target(handle);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("The pinned native array is unavailable.");
            ValidateLayout<T>(pointer);
            var length = IL2CPP.il2cpp_array_length(pointer);
            // One-past-end is valid for an empty render range, but not a larger offset.
            if ((ulong)index > length) throw new ArgumentOutOfRangeException(nameof(index));
            if ((ulong)count > length - (ulong)index) throw new ArgumentOutOfRangeException(nameof(count));
            if (length == 0) return IntPtr.Zero;
            return new IntPtr(checked(pointer.ToInt64() + il2cpp_array_object_header_size() +
                (long)index * Unsafe.SizeOf<T>()));
        }

        private static void ValidateLayout<T>(IntPtr pointer)
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Native array buffers require the verified x64 layout.");
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                throw new NotSupportedException("The CLR array element must have unmanaged storage.");
            var arrayClass = IL2CPP.il2cpp_object_get_class(pointer);
            var elementClass = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (elementClass == IntPtr.Zero || !IL2CPP.il2cpp_class_is_valuetype(elementClass) ||
                IL2CPP.il2cpp_class_has_references(elementClass) ||
                IL2CPP.il2cpp_class_get_rank(arrayClass) != 1 ||
                IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_class_get_type(arrayClass)) != 0x1d ||
                IL2CPP.il2cpp_class_get_element_class(arrayClass) != elementClass ||
                IL2CPP.il2cpp_array_element_size(arrayClass) != Unsafe.SizeOf<T>())
                throw new NotSupportedException("The native array does not match the unmanaged element layout.");
        }

        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint il2cpp_array_object_header_size();
    }
}
