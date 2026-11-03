// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    /// <summary>A CLR storage descriptor, not a replacement Unity type or native generic identity.</summary>
    /// <remarks>
    /// Assignments, subarrays, reinterpretations and spans alias storage. The caller owns its lifetime
    /// and synchronization, including outstanding jobs. Freeing one descriptor does not invalidate
    /// other copies: never access freed aliases or dispose the same allocation twice. Copy constructors
    /// allocate independent storage. Copies use native MemCpy and require non-overlapping regions.
    /// No finalizer or automatic ownership tracking is provided.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe partial struct InteropNativeArrayStorage<T> : IDisposable, IEquatable<InteropNativeArrayStorage<T>>
        where T : struct
    {
        internal IntPtr m_Buffer;
        internal int m_Length;
        internal int m_AllocatorLabel;

        // A failed constructor can leave an allocation requiring an explicit later Dispose retry.
        public const string RecoveryStorageKey = "Modstoke.NativeArrayStorage.Recovery";
        public const string CleanupFailureKey = "Modstoke.NativeArrayStorage.CleanupFailure";

        private static class Cache
        {
            internal static readonly (int Size, int Alignment) Layout = GetLayout();
            internal static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory =
                InteropExceptionSupport.WrapNativeException;

            private static (int Size, int Alignment) GetLayout()
            {
                if (IntPtr.Size != 8)
                    throw new PlatformNotSupportedException("Native array storage requires the x64 descriptor layout.");
                return NativeArrayMemory.GetElementLayout<T>();
            }
        }

        public InteropNativeArrayStorage(int length, int allocator, int options = 1)
        {
            this = default;
            var bytes = ByteCount(length);
            if (allocator <= 1) throw new ArgumentOutOfRangeException(nameof(allocator));
            if (options != 0 && options != 1) throw new ArgumentOutOfRangeException(nameof(options));
            m_Buffer = NativeArrayMemory.AllocateTracked(bytes, Cache.Layout.Alignment, allocator, Cache.ExceptionFactory);
            m_Length = length;
            m_AllocatorLabel = allocator;
            try
            {
                if (options == 1) NativeArrayMemory.Clear(m_Buffer, bytes, Cache.ExceptionFactory);
            }
            catch (Exception error)
            {
                RollbackConstruction(error);
                throw;
            }
        }

        public InteropNativeArrayStorage(T[] source, int allocator)
        {
            ArgumentNullException.ThrowIfNull(source);
            this = new InteropNativeArrayStorage<T>(source.Length, allocator, 0);
            try
            {
                Copy(source, this);
            }
            catch (Exception error)
            {
                RollbackConstruction(error);
                throw;
            }
        }

        public InteropNativeArrayStorage(InteropNativeArrayStorage<T> source, int allocator)
        {
            ValidateBuffer(source.m_Buffer, source.m_Length);
            this = new InteropNativeArrayStorage<T>(source.m_Length, allocator, 0);
            try
            {
                Copy(source, this);
            }
            catch (Exception error)
            {
                RollbackConstruction(error);
                throw;
            }
        }

        public readonly int Length => m_Length;
        public readonly bool IsCreated => m_Buffer != IntPtr.Zero;

        public T this[int index]
        {
            readonly get => Unsafe.ReadUnaligned<T>(ElementAddress(m_Buffer, m_Length, index).ToPointer());
            set => Unsafe.WriteUnaligned(ElementAddress(m_Buffer, m_Length, index).ToPointer(), value);
        }

        public void Dispose()
        {
            if (m_Buffer == IntPtr.Zero) return;
            if (m_AllocatorLabel == 0) throw new InvalidOperationException("The storage allocator is invalid.");
            if (m_AllocatorLabel > 1)
            {
                NativeArrayMemory.FreeTracked(m_Buffer, m_AllocatorLabel, Cache.ExceptionFactory);
                m_AllocatorLabel = 0;
            }
            m_Buffer = IntPtr.Zero;
        }

        /// <summary>Wraps existing storage without allocating or copying it.</summary>
        /// <remarks>Allocator 1 is borrowed. An owning allocator transfers disposal responsibility to the caller.</remarks>
        public static InteropNativeArrayStorage<T> FromExistingData(IntPtr buffer, int length, int allocator = 1)
        {
            ValidateBuffer(buffer, length);
            if (allocator < 0) throw new ArgumentOutOfRangeException(nameof(allocator));
            return new InteropNativeArrayStorage<T> { m_Buffer = buffer, m_Length = length, m_AllocatorLabel = allocator };
        }

        public readonly InteropNativeArrayStorage<T> GetSubArray(int start, int count) =>
            FromExistingData(RangeAddress(m_Buffer, m_Length, start, count), count);

        public readonly Span<T> AsSpan()
        {
            ValidateBuffer(m_Buffer, m_Length);
            return new Span<T>(m_Buffer.ToPointer(), m_Length);
        }

        public readonly ReadOnlySpan<T> AsReadOnlySpan() => AsSpan();
        public readonly ReadOnly AsReadOnly()
        {
            ValidateBuffer(m_Buffer, m_Length);
            return new ReadOnly(m_Buffer, m_Length);
        }

        /// <summary>Returns an unowned pointer; the caller must keep the allocation alive and synchronized.</summary>
        public readonly IntPtr GetUnsafePtr()
        {
            ValidateBuffer(m_Buffer, m_Length);
            return m_Buffer;
        }

        public readonly InteropNativeArrayStorage<U> Reinterpret<U>() where U : struct
        {
            if (Cache.Layout.Size != InteropNativeArrayStorage<U>.Cache.Layout.Size)
                throw new InvalidOperationException("Reinterpretation requires equal element sizes.");
            ValidateBuffer(m_Buffer, m_Length);
            return InteropNativeArrayStorage<U>.FromExistingData(m_Buffer, m_Length, m_AllocatorLabel);
        }

        public readonly InteropNativeArrayStorage<U> Reinterpret<U>(int expectedTypeSize) where U : struct
        {
            if (expectedTypeSize != Cache.Layout.Size)
                throw new ArgumentException("The expected source element size does not match.", nameof(expectedTypeSize));
            ValidateBuffer(m_Buffer, m_Length);
            var bytes = ByteCount(m_Length);
            var size = InteropNativeArrayStorage<U>.Cache.Layout.Size;
            if (bytes % size != 0) throw new InvalidOperationException("The byte extent is not a whole number of target elements.");
            return InteropNativeArrayStorage<U>.FromExistingData(m_Buffer, checked((int)(bytes / size)), m_AllocatorLabel);
        }

        public readonly U ReinterpretLoad<U>(int sourceIndex) where U : struct =>
            Unsafe.ReadUnaligned<U>(ReinterpretAddress<U>(sourceIndex).ToPointer());

        public readonly void ReinterpretStore<U>(int destinationIndex, U value) where U : struct =>
            Unsafe.WriteUnaligned(ReinterpretAddress<U>(destinationIndex).ToPointer(), value);

        private readonly IntPtr ReinterpretAddress<U>(int index) where U : struct
        {
            var size = InteropNativeArrayStorage<U>.Cache.Layout.Size;
            var pointer = ElementAddress(m_Buffer, m_Length, index);
            if (size > ByteCount(m_Length) - ByteCount(index))
                throw new ArgumentOutOfRangeException(nameof(index), "The target value exceeds the storage byte extent.");
            return pointer;
        }

        public readonly bool Equals(InteropNativeArrayStorage<T> other) => m_Buffer == other.m_Buffer && m_Length == other.m_Length;
        public override readonly bool Equals(object obj) => obj is InteropNativeArrayStorage<T> other && Equals(other);
        public override readonly int GetHashCode() => unchecked(((int)m_Buffer.ToInt64() * 397) ^ m_Length);
        public static bool operator ==(InteropNativeArrayStorage<T> left, InteropNativeArrayStorage<T> right) => left.Equals(right);
        public static bool operator !=(InteropNativeArrayStorage<T> left, InteropNativeArrayStorage<T> right) => !left.Equals(right);

        private void RollbackConstruction(Exception error)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanup)
            {
                // Free failed before descriptor invalidation. Retain a boxed descriptor for recovery;
                // the original exception, not the cleanup error, remains the thrown exception.
                error.Data[RecoveryStorageKey] = this;
                error.Data[CleanupFailureKey] = cleanup;
            }
        }

        private static long ByteCount(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            return checked((long)count * Cache.Layout.Size);
        }

        private static IntPtr AddBytes(IntPtr pointer, long bytes) => new IntPtr(checked(pointer.ToInt64() + bytes));

        private static void ValidateBuffer(IntPtr buffer, int length)
        {
            var bytes = ByteCount(length);
            if (bytes != 0 && buffer == IntPtr.Zero)
                throw new InvalidOperationException("Nonempty storage has no buffer, or this descriptor was disposed.");
            if (bytes != 0) _ = AddBytes(buffer, bytes - 1);
        }

        private static void CheckRange(int total, int index, int count)
        {
            if (index < 0 || index > total) throw new ArgumentOutOfRangeException(nameof(index));
            if (count < 0 || count > total - index) throw new ArgumentOutOfRangeException(nameof(count));
        }

        private static IntPtr RangeAddress(IntPtr buffer, int total, int index, int count)
        {
            CheckRange(total, index, count);
            ValidateBuffer(buffer, total);
            return AddBytes(buffer, ByteCount(index));
        }

        private static IntPtr ElementAddress(IntPtr buffer, int length, int index)
        {
            if ((uint)index >= (uint)length) throw new IndexOutOfRangeException();
            return RangeAddress(buffer, length, index, 1);
        }
    }
}
