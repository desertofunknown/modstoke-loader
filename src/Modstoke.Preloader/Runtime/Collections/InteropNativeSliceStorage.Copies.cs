// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;

namespace Modstoke.Preloader
{
    public unsafe partial struct InteropNativeSliceStorage<T> where T : struct
    {
        /// <summary>Copies Length elements from the source prefix, leaving any source tail unused.</summary>
        public readonly void CopyFrom(InteropNativeSliceStorage<T> source)
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            ValidateBuffer(source.m_Buffer, source.m_Stride, source.m_Length);
            if (source.m_Length < m_Length)
                throw new ArgumentException("The source is shorter than the destination slice.", nameof(source));
            if (Equals(source)) return;
            CopyElements(source.m_Buffer, source.m_Stride, m_Buffer, m_Stride, m_Length);
        }

        /// <summary>Copies Length elements from the source prefix, leaving any source tail unused.</summary>
        public readonly void CopyFrom(T[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            if (source.Length < m_Length)
                throw new ArgumentException("The source is shorter than the destination slice.", nameof(source));
            if (m_Length == 0) return;
            fixed (byte* src = &Unsafe.As<T, byte>(ref source[0]))
                CopyElements(new IntPtr(src), Cache.Layout.Size, m_Buffer, m_Stride, m_Length);
        }

        /// <summary>Copies Length elements to the destination prefix, preserving any destination tail.</summary>
        public readonly void CopyTo(InteropNativeArrayStorage<T> destination)
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            var stride = Cache.Layout.Size;
            ValidateBuffer(destination.m_Buffer, stride, destination.m_Length);
            if (destination.m_Length < m_Length)
                throw new ArgumentException("The destination is shorter than the source slice.", nameof(destination));
            if (m_Buffer == destination.m_Buffer && m_Stride == stride && m_Length == destination.m_Length) return;
            CopyElements(m_Buffer, m_Stride, destination.m_Buffer, stride, m_Length);
        }

        /// <summary>Copies Length elements to the destination prefix, preserving any destination tail.</summary>
        public readonly void CopyTo(T[] destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            if (destination.Length < m_Length)
                throw new ArgumentException("The destination is shorter than the source slice.", nameof(destination));
            if (m_Length == 0) return;
            fixed (byte* dst = &Unsafe.As<T, byte>(ref destination[0]))
                CopyElements(m_Buffer, m_Stride, new IntPtr(dst), Cache.Layout.Size, m_Length);
        }

        public readonly T[] ToArray()
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            var result = new T[m_Length];
            CopyTo(result);
            return result;
        }

        // Callers validate descriptors and capacity before entering this loop. Managed buffers stay
        // pinned for the whole copy; element addresses remain inside the already-checked extents.
        private static void CopyElements(IntPtr source, int sourceStride, IntPtr destination, int destinationStride, int length)
        {
            if (length == 0) return;
            if (sourceStride == Cache.Layout.Size && destinationStride == Cache.Layout.Size)
            {
                new Span<T>(source.ToPointer(), length).CopyTo(new Span<T>(destination.ToPointer(), length));
                return;
            }

            var src = (byte*)source.ToPointer();
            var dst = (byte*)destination.ToPointer();
            for (var index = 0; index < length; index++)
            {
                var value = Unsafe.ReadUnaligned<T>(src + (long)index * sourceStride);
                Unsafe.WriteUnaligned(dst + (long)index * destinationStride, value);
            }
        }
    }
}
