// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections;
using System.Collections.Generic;

namespace Modstoke.Preloader
{
    public sealed partial class InteropNativeKeyDictionary<TKey, TValue> where TKey : class
    {
        private KeyCollection keys;
        private ValueCollection values;

        public KeyCollection Keys => keys ??= new KeyCollection(this);
        public ValueCollection Values => values ??= new ValueCollection(this);

        public Enumerator GetEnumerator() => new Enumerator(this);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly InteropNativeKeyDictionary<TKey, TValue> parent;
            private readonly int version;
            private Dictionary<TKey, TValue>.Enumerator inner;

            internal Enumerator(InteropNativeKeyDictionary<TKey, TValue> parent)
            {
                this.parent = parent;
                version = parent.version;
                inner = parent.entries.GetEnumerator();
            }

            public readonly KeyValuePair<TKey, TValue> Current => inner.Current;
            readonly object IEnumerator.Current => ((IEnumerator)inner).Current;

            public bool MoveNext()
            {
                parent.CheckVersion(version);
                return inner.MoveNext();
            }

            void IEnumerator.Reset()
            {
                parent.CheckVersion(version);
                inner = parent.entries.GetEnumerator();
            }

            public void Dispose() => inner.Dispose();
        }

        public sealed class KeyCollection : IEnumerable<TKey>
        {
            private readonly InteropNativeKeyDictionary<TKey, TValue> parent;

            internal KeyCollection(InteropNativeKeyDictionary<TKey, TValue> parent)
            {
                this.parent = parent;
            }

            public int Count => parent.Count;
            public bool Contains(TKey key) => parent.ContainsKey(key);
            public Enumerator GetEnumerator() => new Enumerator(parent);

            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public struct Enumerator : IEnumerator<TKey>
            {
                private readonly InteropNativeKeyDictionary<TKey, TValue> parent;
                private readonly int version;
                private Dictionary<TKey, TValue>.KeyCollection.Enumerator inner;

                internal Enumerator(InteropNativeKeyDictionary<TKey, TValue> parent)
                {
                    this.parent = parent;
                    version = parent.version;
                    inner = parent.entries.Keys.GetEnumerator();
                }

                public readonly TKey Current => inner.Current;
                readonly object IEnumerator.Current => ((IEnumerator)inner).Current;

                public bool MoveNext()
                {
                    parent.CheckVersion(version);
                    return inner.MoveNext();
                }

                void IEnumerator.Reset()
                {
                    parent.CheckVersion(version);
                    inner = parent.entries.Keys.GetEnumerator();
                }

                public void Dispose() => inner.Dispose();
            }
        }

        public sealed class ValueCollection : IEnumerable<TValue>
        {
            private readonly InteropNativeKeyDictionary<TKey, TValue> parent;

            internal ValueCollection(InteropNativeKeyDictionary<TKey, TValue> parent)
            {
                this.parent = parent;
            }

            public int Count => parent.Count;
            public bool Contains(TValue value) => parent.ContainsValue(value);
            public Enumerator GetEnumerator() => new Enumerator(parent);

            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public struct Enumerator : IEnumerator<TValue>
            {
                private readonly InteropNativeKeyDictionary<TKey, TValue> parent;
                private readonly int version;
                private Dictionary<TKey, TValue>.ValueCollection.Enumerator inner;

                internal Enumerator(InteropNativeKeyDictionary<TKey, TValue> parent)
                {
                    this.parent = parent;
                    version = parent.version;
                    inner = parent.entries.Values.GetEnumerator();
                }

                public readonly TValue Current => inner.Current;
                readonly object IEnumerator.Current => ((IEnumerator)inner).Current;

                public bool MoveNext()
                {
                    parent.CheckVersion(version);
                    return inner.MoveNext();
                }

                void IEnumerator.Reset()
                {
                    parent.CheckVersion(version);
                    inner = parent.entries.Values.GetEnumerator();
                }

                public void Dispose() => inner.Dispose();
            }
        }
    }
}
