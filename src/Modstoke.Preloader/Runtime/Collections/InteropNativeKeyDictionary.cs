// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    /// <summary>CLR dictionary storage preserving native key ownership and Unity enumeration semantics.</summary>
    /// <remarks>
    /// Native keys use the supplied EqualityComparer&lt;TKey&gt;.Default and bound delegates; strings use ordinal equality.
    /// Callers must serialize mutations and must not mutate this dictionary from comparer callbacks.
    /// </remarks>
    public sealed partial class InteropNativeKeyDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : class
    {
        private readonly Dictionary<TKey, TValue> entries;
        private int version;

        public InteropNativeKeyDictionary(int capacity)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (typeof(TKey) != typeof(string))
                throw new NotSupportedException("Only string keys have a CLR default comparer contract.");
            entries = new Dictionary<TKey, TValue>(capacity, (IEqualityComparer<TKey>)(object)StringComparer.Ordinal);
        }

        public InteropNativeKeyDictionary(int capacity, Il2CppObjectBase nativeComparer,
            Func<TKey, TKey, bool> equals, Func<TKey, int> hash)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!typeof(Il2CppObjectBase).IsAssignableFrom(typeof(TKey)))
                throw new NotSupportedException("Native comparison requires a generated native reference key.");
            ArgumentNullException.ThrowIfNull(nativeComparer);
            ArgumentNullException.ThrowIfNull(equals);
            ArgumentNullException.ThrowIfNull(hash);
            entries = new Dictionary<TKey, TValue>(capacity, new KeyComparer(nativeComparer, equals, hash));
        }

        public int Count => entries.Count;

        public TValue this[TKey key]
        {
            get
            {
                using var lease = new KeyLease(key);
                return entries[lease.Query];
            }
            set
            {
                using var lease = new KeyLease(key);
                Changed();
                // Root before the mutating lookup; Dictionary preserves the existing key on replacement.
                entries[lease.Stored] = value;
            }
        }

        public void Add(TKey key, TValue value)
        {
            using var lease = new KeyLease(key);
            Changed();
            entries.Add(lease.Stored, value);
        }

        public bool TryAdd(TKey key, TValue value)
        {
            using var lease = new KeyLease(key);
            Changed();
            // A speculative root avoids a second native lookup or a ref held across callbacks.
            return entries.TryAdd(lease.Stored, value);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            using var lease = new KeyLease(key);
            return entries.TryGetValue(lease.Query, out value);
        }

        public bool ContainsKey(TKey key)
        {
            using var lease = new KeyLease(key);
            return entries.ContainsKey(lease.Query);
        }

        public bool ContainsValue(TValue value) => entries.ContainsValue(value);

        public bool Remove(TKey key)
        {
            using var lease = new KeyLease(key);
            var removed = entries.Remove(lease.Query);
            if (removed) Changed();
            return removed;
        }

        public bool Remove(TKey key, out TValue value)
        {
            using var lease = new KeyLease(key);
            var removed = entries.Remove(lease.Query, out value);
            if (removed) Changed();
            return removed;
        }

        public void Clear()
        {
            Changed();
            entries.Clear();
        }

        public int EnsureCapacity(int capacity)
        {
            var previous = entries.EnsureCapacity(0);
            var current = entries.EnsureCapacity(capacity);
            if (current != previous) Changed();
            return current;
        }

        public void TrimExcess() => TrimExcess(entries.Count);

        public void TrimExcess(int capacity)
        {
            var previous = entries.EnsureCapacity(0);
            entries.TrimExcess(capacity);
            if (entries.EnsureCapacity(0) != previous) Changed();
        }

        // Unity's native dictionary invalidates on overwrite and rejected inserts too, unlike modern .NET.
        private void Changed() { unchecked { version++; } }

        private void CheckVersion(int expected)
        {
            if (expected != version)
                throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
        }

        private readonly struct KeyLease : IDisposable
        {
            private readonly TKey value;
            private readonly IntPtr handle;

            internal KeyLease(TKey key)
            {
                if (key is null) throw new ArgumentNullException(nameof(key));
                value = key;
                handle = typeof(TKey) == typeof(string) ? IntPtr.Zero : InteropGenericValueSupport.PinReferenceArgument(key);
            }

            // Query keys borrow the operation's pin; only stored keys need a separate unpinned root.
            internal TKey Query => value;

            internal TKey Stored => typeof(TKey) == typeof(string) ? value : NativeKeys.Wrap(
                InteropGenericValueSupport.PinnedReferenceArgumentAddress(handle));

            public void Dispose()
            {
                try { if (handle != IntPtr.Zero) InteropNativeValueStorage.ReleaseArgument(handle); }
                finally { GC.KeepAlive(value); }
            }
        }

        private static class NativeKeys
        {
            internal static readonly Func<IntPtr, TKey> Wrap;

            static NativeKeys()
            {
                // The explicit initializer keeps native-only generic binding off the string path.
                Wrap = typeof(InteropNativeReferenceIdentity)
                    .GetMethod(nameof(InteropNativeReferenceIdentity.WrapOwned), BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(typeof(TKey)).CreateDelegate<Func<IntPtr, TKey>>();
            }
        }

        private sealed class KeyComparer : IEqualityComparer<TKey>
        {
            private readonly Il2CppObjectBase owner;
            private readonly Func<TKey, TKey, bool> equals;
            private readonly Func<TKey, int> hash;

            internal KeyComparer(Il2CppObjectBase nativeComparer, Func<TKey, TKey, bool> equals,
                Func<TKey, int> hash)
            {
                this.equals = equals;
                this.hash = hash;
                // Delegate targets may only weakly own their native receiver.
                try { owner = new Il2CppObjectBase(nativeComparer.Pointer); }
                finally { GC.KeepAlive(nativeComparer); }
            }

            public bool Equals(TKey x, TKey y)
            {
                try { return equals(x, y); }
                finally
                {
                    GC.KeepAlive(x);
                    GC.KeepAlive(y);
                    GC.KeepAlive(owner);
                }
            }

            public int GetHashCode(TKey key)
            {
                try { return hash(key); }
                finally
                {
                    GC.KeepAlive(key);
                    GC.KeepAlive(owner);
                }
            }
        }
    }
}
