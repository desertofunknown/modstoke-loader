// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    /// <summary>A borrowed CLR slice descriptor, not a replacement Unity type or native generic identity.</summary>
    /// <remarks>
    /// This descriptor never allocates, frees or owns its buffer. Assignments and subslices alias storage.
    /// The caller owns the buffer lifetime and synchronization, including outstanding jobs. Copies require
    /// non-overlapping source and destination regions, except that identical descriptors are a no-op.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe partial struct InteropNativeSliceStorage<T> : IEquatable<InteropNativeSliceStorage<T>>, IEnumerable<T>
        where T : struct
    {
        internal IntPtr m_Buffer;
        internal int m_Stride;
        internal int m_Length;

        internal static class Cache
        {
            internal static readonly (int Size, int Alignment) Layout = GetLayout();

            private static (int Size, int Alignment) GetLayout()
            {
                if (IntPtr.Size != 8)
                    throw new PlatformNotSupportedException("Native slice storage requires the x64 descriptor layout.");
                return NativeArrayMemory.GetElementLayout<T>();
            }
        }

        public InteropNativeSliceStorage(InteropNativeSliceStorage<T> slice, int start)
            : this(slice, start, RemainingLength(slice.m_Length, start)) { }

        public InteropNativeSliceStorage(InteropNativeSliceStorage<T> slice, int start, int length)
        {
            CheckRange(slice.m_Length, start, length);
            ValidateBuffer(slice.m_Buffer, slice.m_Stride, slice.m_Length);
            this = FromExistingData(AddBytes(slice.m_Buffer, checked((long)slice.m_Stride * start)), slice.m_Stride, length);
        }

        public InteropNativeSliceStorage(InteropNativeArrayStorage<T> array)
            : this(array, 0, array.m_Length) { }

        public InteropNativeSliceStorage(InteropNativeArrayStorage<T> array, int start)
            : this(array, start, RemainingLength(array.m_Length, start)) { }

        public InteropNativeSliceStorage(InteropNativeArrayStorage<T> array, int start, int length)
        {
            CheckRange(array.m_Length, start, length);
            var stride = Cache.Layout.Size;
            ValidateBuffer(array.m_Buffer, stride, array.m_Length);
            this = FromExistingData(AddBytes(array.m_Buffer, checked((long)stride * start)), stride, length);
        }

        public static implicit operator InteropNativeSliceStorage<T>(InteropNativeArrayStorage<T> array) => new(array);

        public readonly int Length => m_Length;
        public readonly int Stride => m_Stride;

        public T this[int index]
        {
            readonly get => Unsafe.ReadUnaligned<T>(ElementAddress(index).ToPointer());
            set => Unsafe.WriteUnaligned(ElementAddress(index).ToPointer(), value);
        }

        /// <summary>Wraps existing storage without allocating or establishing ownership.</summary>
        /// <remarks>Zero stride and overlapping elements are permitted; the caller must provide the full readable extent.</remarks>
        public static InteropNativeSliceStorage<T> FromExistingData(IntPtr buffer, int stride, int length)
        {
            ValidateBuffer(buffer, stride, length);
            return new InteropNativeSliceStorage<T> { m_Buffer = buffer, m_Stride = stride, m_Length = length };
        }

        /// <summary>Returns an unowned pointer; the caller must keep the buffer alive and synchronized.</summary>
        public readonly IntPtr GetUnsafePtr()
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            return m_Buffer;
        }

        public readonly bool Equals(InteropNativeSliceStorage<T> other) =>
            m_Buffer == other.m_Buffer && m_Stride == other.m_Stride && m_Length == other.m_Length;
        public override readonly bool Equals(object obj) => obj is InteropNativeSliceStorage<T> other && Equals(other);
        public override readonly int GetHashCode() => unchecked(((((int)m_Buffer.ToInt64() * 397) ^ m_Stride) * 397) ^ m_Length);
        public static bool operator ==(InteropNativeSliceStorage<T> left, InteropNativeSliceStorage<T> right) => left.Equals(right);
        public static bool operator !=(InteropNativeSliceStorage<T> left, InteropNativeSliceStorage<T> right) => !left.Equals(right);

        /// <summary>Returns an enumerator that reads the borrowed storage when Current is accessed.</summary>
        public readonly Enumerator GetEnumerator()
        {
            var array = this;
            return new Enumerator(ref array);
        }

        readonly IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        [StructLayout(LayoutKind.Sequential)]
        public struct Enumerator : IEnumerator<T>
        {
            private InteropNativeSliceStorage<T> m_Array;
            private int m_Index;

            public Enumerator(ref InteropNativeSliceStorage<T> array)
            {
                m_Array = array;
                m_Index = -1;
            }

            public readonly T Current => m_Array[m_Index];
            readonly object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                m_Index = unchecked(m_Index + 1);
                return m_Index < m_Array.Length;
            }

            public void Reset() => m_Index = -1;
            public readonly void Dispose() { }
        }

        internal static void ValidateBuffer(IntPtr buffer, int stride, int length)
        {
            if (stride < 0) throw new ArgumentOutOfRangeException(nameof(stride));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            var size = Cache.Layout.Size;
            if (length == 0) return;
            if (buffer == IntPtr.Zero) throw new InvalidOperationException("Nonempty slice storage has no buffer.");
            var extent = checked((long)(length - 1) * stride + size);
            _ = AddBytes(buffer, extent - 1);
        }

        internal static IntPtr AddBytes(IntPtr pointer, long bytes)
        {
            var address = unchecked((ulong)pointer.ToInt64());
            var result = checked(address + checked((ulong)bytes));
            return new IntPtr(unchecked((long)result));
        }

        private static void CheckRange(int total, int start, int length)
        {
            if (start < 0 || start > total) throw new ArgumentOutOfRangeException(nameof(start));
            if (length < 0 || length > total - start) throw new ArgumentOutOfRangeException(nameof(length));
        }

        private static int RemainingLength(int total, int start)
        {
            CheckRange(total, start, 0);
            return total - start;
        }

        private readonly IntPtr ElementAddress(int index)
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            if ((uint)index >= (uint)m_Length) throw new IndexOutOfRangeException();
            return AddBytes(m_Buffer, checked((long)index * m_Stride));
        }
    }
}
