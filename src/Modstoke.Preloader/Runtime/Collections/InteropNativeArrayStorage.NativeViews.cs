// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public unsafe partial struct InteropNativeArrayStorage<T> where T : struct
    {
        /// <summary>Creates a native typed alias, preserving the original allocator and disposal responsibility.</summary>
        public readonly TArray ReinterpretToNative<U, TArray>() where U : struct where TArray : Il2CppObjectBase
        {
            var view = Reinterpret<U>();
            return NativeArrayMemory.CreateArrayView<U, TArray>(view.m_Buffer, view.m_Length, view.m_AllocatorLabel);
        }

        /// <summary>Reinterprets the complete byte extent as a native typed alias without copying its buffer.</summary>
        public readonly TArray ReinterpretToNative<U, TArray>(int expectedTypeSize)
            where U : struct where TArray : Il2CppObjectBase
        {
            var view = Reinterpret<U>(expectedTypeSize);
            return NativeArrayMemory.CreateArrayView<U, TArray>(view.m_Buffer, view.m_Length, view.m_AllocatorLabel);
        }

        /// <summary>Creates a CLR descriptor alias of a native array; neither descriptor gains exclusive ownership.</summary>
        public static InteropNativeArrayStorage<T> ReinterpretFromNative<U, TArray>(TArray source)
            where U : struct where TArray : Il2CppObjectBase
        {
            var header = NativeArrayMemory.ReadArrayView<U, TArray>(source, false);
            return InteropNativeArrayStorage<U>.FromExistingData(header.Buffer, header.Length, header.Allocator).Reinterpret<T>();
        }

        /// <summary>Reinterprets a native array's complete byte extent without copying or transferring its allocation.</summary>
        public static InteropNativeArrayStorage<T> ReinterpretFromNative<U, TArray>(TArray source, int expectedTypeSize)
            where U : struct where TArray : Il2CppObjectBase
        {
            var header = NativeArrayMemory.ReadArrayView<U, TArray>(source, false);
            return InteropNativeArrayStorage<U>.FromExistingData(header.Buffer, header.Length, header.Allocator)
                .Reinterpret<T>(expectedTypeSize);
        }

        public readonly partial struct ReadOnly
        {
            /// <summary>Creates a non-owning native readonly alias of this view.</summary>
            public TArray ReinterpretToNative<U, TArray>() where U : struct where TArray : Il2CppObjectBase
            {
                var view = Reinterpret<U>();
                return NativeArrayMemory.CreateReadOnlyArrayView<U, TArray>(view.m_Buffer, view.m_Length);
            }

            /// <summary>Creates a non-owning CLR readonly alias of a native readonly array.</summary>
            public static ReadOnly ReinterpretFromNative<U, TArray>(TArray source)
                where U : struct where TArray : Il2CppObjectBase
            {
                var header = NativeArrayMemory.ReadArrayView<U, TArray>(source, true);
                return InteropNativeArrayStorage<U>.FromExistingData(header.Buffer, header.Length)
                    .Reinterpret<T>().AsReadOnly();
            }
        }
    }
}
