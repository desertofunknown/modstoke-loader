// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Modstoke.Preloader
{
    public partial struct InteropNativeArrayStorage<T> : IEnumerable<T> where T : struct
    {
        /// <summary>Returns an enumerator that aliases storage without owning it.</summary>
        /// <remarks>The caller must keep the allocation alive and synchronized during enumeration.</remarks>
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
            private InteropNativeArrayStorage<T> m_Array;
            private int m_Index;
            private T value;

            public Enumerator(ref InteropNativeArrayStorage<T> array)
            {
                m_Array = array;
                m_Index = -1;
                value = default;
            }

            public readonly T Current => value;
            readonly object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                m_Index = unchecked(m_Index + 1);
                if (m_Index < m_Array.Length)
                {
                    value = m_Array[m_Index];
                    return true;
                }
                value = default;
                return false;
            }

            public void Reset() => m_Index = -1;
            public readonly void Dispose() { }
        }

        public readonly partial struct ReadOnly : IEnumerable<T>
        {
            /// <summary>Returns an enumerator that aliases storage without owning it.</summary>
            /// <remarks>The caller must keep the allocation alive and synchronized during enumeration.</remarks>
            public readonly Enumerator GetEnumerator()
            {
                var array = this;
                return new Enumerator(ref array);
            }

            IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            [StructLayout(LayoutKind.Sequential)]
            public struct Enumerator : IEnumerator<T>
            {
                private ReadOnly m_Array;
                private int m_Index;
                private T value;

                public Enumerator(ref ReadOnly array)
                {
                    m_Array = array;
                    m_Index = -1;
                    value = default;
                }

                public readonly T Current => value;
                readonly object IEnumerator.Current => Current;

                public bool MoveNext()
                {
                    m_Index = unchecked(m_Index + 1);
                    if (m_Index < m_Array.Length)
                    {
                        value = m_Array[m_Index];
                        return true;
                    }
                    value = default;
                    return false;
                }

                public void Reset() => m_Index = -1;
                public readonly void Dispose() { }
            }
        }
    }
}
