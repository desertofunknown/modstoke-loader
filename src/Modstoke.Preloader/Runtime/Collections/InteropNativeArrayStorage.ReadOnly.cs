// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Modstoke.Preloader
{
    public unsafe partial struct InteropNativeArrayStorage<T> where T : struct
    {
        /// <summary>A non-owning read-only alias. Other aliases can still mutate or free the allocation.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly partial struct ReadOnly
        {
            internal readonly IntPtr m_Buffer;
            internal readonly int m_Length;

            internal ReadOnly(IntPtr buffer, int length)
            {
                m_Buffer = buffer;
                m_Length = length;
            }

            public int Length => m_Length;
            public bool IsCreated => m_Buffer != IntPtr.Zero;
            public T this[int index] => Unsafe.ReadUnaligned<T>(ElementAddress(m_Buffer, m_Length, index).ToPointer());

            public ReadOnlySpan<T> AsReadOnlySpan()
            {
                ValidateBuffer(m_Buffer, m_Length);
                return new ReadOnlySpan<T>(m_Buffer.ToPointer(), m_Length);
            }

            public InteropNativeArrayStorage<U>.ReadOnly Reinterpret<U>() where U : struct =>
                FromExistingData(m_Buffer, m_Length).Reinterpret<U>().AsReadOnly();

            public T[] ToArray()
            {
                ValidateBuffer(m_Buffer, m_Length);
                var result = new T[m_Length];
                Copy(this, result);
                return result;
            }

            public void CopyTo(T[] destination) => Copy(this, destination);
            public void CopyTo(InteropNativeArrayStorage<T> destination) => Copy(this, destination);
        }
    }
}
