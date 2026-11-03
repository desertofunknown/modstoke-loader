// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    public unsafe partial struct InteropNativeArrayStorage<T> where T : struct
    {
        public readonly T[] ToArray()
        {
            ValidateBuffer(m_Buffer, m_Length);
            var result = new T[m_Length];
            Copy(this, result);
            return result;
        }

        public readonly void CopyFrom(T[] source) => Copy(source, this);
        public readonly void CopyFrom(InteropNativeArrayStorage<T> source) => Copy(source, this);
        public readonly void CopyFrom(ReadOnly source) => Copy(source, this);
        public readonly void CopyTo(T[] destination) => Copy(this, destination);
        public readonly void CopyTo(InteropNativeArrayStorage<T> destination) => Copy(this, destination);

        /// <summary>Copies from a caller-owned native buffer whose capacity is given in elements.</summary>
        public readonly void CopyFrom(IntPtr source, int sourceLength) =>
            Copy(FromExistingData(source, sourceLength), this);

        /// <summary>Copies to a caller-owned native buffer whose capacity is given in elements.</summary>
        public readonly void CopyTo(IntPtr destination, int destinationLength) =>
            Copy(this, FromExistingData(destination, destinationLength));

        public static void Copy(InteropNativeArrayStorage<T> source, InteropNativeArrayStorage<T> destination) =>
            Copy(source, 0, destination, 0, source.m_Length);
        public static void Copy(InteropNativeArrayStorage<T> source, InteropNativeArrayStorage<T> destination, int length) =>
            Copy(source, 0, destination, 0, length);
        public static void Copy(ReadOnly source, InteropNativeArrayStorage<T> destination) =>
            Copy(source, 0, destination, 0, source.Length);
        public static void Copy(ReadOnly source, InteropNativeArrayStorage<T> destination, int length) =>
            Copy(source, 0, destination, 0, length);
        public static void Copy(T[] source, InteropNativeArrayStorage<T> destination)
        {
            ArgumentNullException.ThrowIfNull(source);
            Copy(source, 0, destination, 0, source.Length);
        }
        public static void Copy(T[] source, InteropNativeArrayStorage<T> destination, int length) =>
            Copy(source, 0, destination, 0, length);
        public static void Copy(InteropNativeArrayStorage<T> source, T[] destination) =>
            Copy(source, 0, destination, 0, source.m_Length);
        public static void Copy(InteropNativeArrayStorage<T> source, T[] destination, int length) =>
            Copy(source, 0, destination, 0, length);
        public static void Copy(ReadOnly source, T[] destination) => Copy(source, 0, destination, 0, source.Length);
        public static void Copy(ReadOnly source, T[] destination, int length) => Copy(source, 0, destination, 0, length);

        public static void Copy(InteropNativeArrayStorage<T> source, int sourceIndex,
            InteropNativeArrayStorage<T> destination, int destinationIndex, int length) =>
            Copy(source.AsReadOnly(), sourceIndex, destination, destinationIndex, length);

        public static void Copy(ReadOnly source, int sourceIndex,
            InteropNativeArrayStorage<T> destination, int destinationIndex, int length)
        {
            var src = RangeAddress(source.m_Buffer, source.m_Length, sourceIndex, length);
            var dst = RangeAddress(destination.m_Buffer, destination.m_Length, destinationIndex, length);
            NativeArrayMemory.Copy(dst, src, ByteCount(length), Cache.ExceptionFactory);
        }

        public static void Copy(T[] source, int sourceIndex,
            InteropNativeArrayStorage<T> destination, int destinationIndex, int length)
        {
            ArgumentNullException.ThrowIfNull(source);
            CheckRange(source.Length, sourceIndex, length);
            var dst = RangeAddress(destination.m_Buffer, destination.m_Length, destinationIndex, length);
            var bytes = ByteCount(length);
            if (bytes == 0) return;
            // Pin the actual array interior for the complete synchronous call, with no staging buffer.
            fixed (byte* src = &Unsafe.As<T, byte>(ref source[sourceIndex]))
                NativeArrayMemory.Copy(dst, new IntPtr(src), bytes, Cache.ExceptionFactory);
        }

        public static void Copy(InteropNativeArrayStorage<T> source, int sourceIndex,
            T[] destination, int destinationIndex, int length) =>
            Copy(source.AsReadOnly(), sourceIndex, destination, destinationIndex, length);

        public static void Copy(ReadOnly source, int sourceIndex, T[] destination, int destinationIndex, int length)
        {
            ArgumentNullException.ThrowIfNull(destination);
            CheckRange(destination.Length, destinationIndex, length);
            var src = RangeAddress(source.m_Buffer, source.m_Length, sourceIndex, length);
            var bytes = ByteCount(length);
            if (bytes == 0) return;
            fixed (byte* dst = &Unsafe.As<T, byte>(ref destination[destinationIndex]))
                NativeArrayMemory.Copy(new IntPtr(dst), src, bytes, Cache.ExceptionFactory);
        }
    }
}
