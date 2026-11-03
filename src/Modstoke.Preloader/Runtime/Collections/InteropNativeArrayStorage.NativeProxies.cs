// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public unsafe partial struct InteropNativeArrayStorage<T> where T : struct
    {
        /// <summary>Reads a reference-free native value into an independent native box.</summary>
        public readonly U ReinterpretLoadProxy<U>(int sourceIndex) where U : Il2CppObjectBase
        {
            var address = ElementAddress(m_Buffer, m_Length, sourceIndex);
            return NativeArrayMemory.ReadNativeValue<U>(address, ByteCount(m_Length) - ByteCount(sourceIndex));
        }

        /// <summary>Copies a reference-free native value's payload, not the CLR wrapper, into this buffer.</summary>
        public readonly void ReinterpretStoreProxy<U>(int destinationIndex, U value) where U : Il2CppObjectBase
        {
            var address = ElementAddress(m_Buffer, m_Length, destinationIndex);
            NativeArrayMemory.WriteNativeValue(address, ByteCount(m_Length) - ByteCount(destinationIndex), value);
        }

        /// <summary>Creates a native value-proxy array alias with the original allocator and caller-owned lifetime.</summary>
        public readonly TArray ReinterpretProxyToNative<U, TArray>()
            where U : Il2CppObjectBase where TArray : Il2CppObjectBase
        {
            ValidateBuffer(m_Buffer, m_Length);
            return NativeArrayMemory.ReinterpretToArrayView<U, TArray>(m_Buffer, m_Length, Cache.Layout.Size,
                m_AllocatorLabel, false, false);
        }

        /// <summary>Reinterprets the complete byte extent as native value proxies without copying the buffer.</summary>
        public readonly TArray ReinterpretProxyToNative<U, TArray>(int expectedTypeSize)
            where U : Il2CppObjectBase where TArray : Il2CppObjectBase
        {
            if (expectedTypeSize != Cache.Layout.Size)
                throw new ArgumentException("The expected source element size does not match.", nameof(expectedTypeSize));
            ValidateBuffer(m_Buffer, m_Length);
            return NativeArrayMemory.ReinterpretToArrayView<U, TArray>(m_Buffer, m_Length, Cache.Layout.Size,
                m_AllocatorLabel, false, true);
        }

        /// <summary>Creates a CLR descriptor alias of a native value-proxy array without transferring ownership.</summary>
        public static InteropNativeArrayStorage<T> ReinterpretProxyFromNative<U, TArray>(TArray source)
            where U : Il2CppObjectBase where TArray : Il2CppObjectBase
        {
            var header = NativeArrayMemory.ReadNativeArrayView<U, TArray>(source, false);
            var count = ReinterpretProxyLength(header.Length, header.ElementSize, null);
            return FromExistingData(header.Buffer, count, header.Allocator);
        }

        /// <summary>Reinterprets a native value-proxy array's byte extent as caller-owned CLR value storage.</summary>
        public static InteropNativeArrayStorage<T> ReinterpretProxyFromNative<U, TArray>(TArray source, int expectedTypeSize)
            where U : Il2CppObjectBase where TArray : Il2CppObjectBase
        {
            var header = NativeArrayMemory.ReadNativeArrayView<U, TArray>(source, false);
            var count = ReinterpretProxyLength(header.Length, header.ElementSize, expectedTypeSize);
            return FromExistingData(header.Buffer, count, header.Allocator);
        }

        private static int ReinterpretProxyLength(int sourceLength, int sourceElementSize, int? expectedTypeSize)
        {
            if (!expectedTypeSize.HasValue)
            {
                if (sourceElementSize != Cache.Layout.Size)
                    throw new InvalidOperationException("Reinterpretation requires equal element sizes.");
                return sourceLength;
            }
            if (expectedTypeSize.Value != sourceElementSize)
                throw new ArgumentException("The expected source element size does not match.", nameof(expectedTypeSize));
            var bytes = checked((long)sourceLength * sourceElementSize);
            if (bytes % Cache.Layout.Size != 0)
                throw new InvalidOperationException("The byte extent is not a whole number of target elements.");
            return checked((int)(bytes / Cache.Layout.Size));
        }

        public readonly partial struct ReadOnly
        {
            /// <summary>Creates a non-owning native readonly alias for reference-free value proxies.</summary>
            public TArray ReinterpretProxyToNative<U, TArray>()
                where U : Il2CppObjectBase where TArray : Il2CppObjectBase
            {
                ValidateBuffer(m_Buffer, m_Length);
                return NativeArrayMemory.ReinterpretToArrayView<U, TArray>(m_Buffer, m_Length, Cache.Layout.Size, 1, true, false);
            }

            /// <summary>Creates a non-owning CLR readonly alias of a native value-proxy array.</summary>
            public static ReadOnly ReinterpretProxyFromNative<U, TArray>(TArray source)
                where U : Il2CppObjectBase where TArray : Il2CppObjectBase
            {
                var header = NativeArrayMemory.ReadNativeArrayView<U, TArray>(source, true);
                var count = ReinterpretProxyLength(header.Length, header.ElementSize, null);
                return FromExistingData(header.Buffer, count).AsReadOnly();
            }
        }
    }
}
