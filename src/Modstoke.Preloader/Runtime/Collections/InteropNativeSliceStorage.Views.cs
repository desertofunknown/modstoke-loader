// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public unsafe partial struct InteropNativeSliceStorage<T> where T : struct
    {
        /// <summary>Reinterprets the slice's byte length without allocating or taking ownership.</summary>
        public readonly InteropNativeSliceStorage<U> SliceConvert<U>() where U : struct
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            var view = ConvertGeometry(m_Buffer, m_Stride, m_Length, Cache.Layout.Size,
                InteropNativeSliceStorage<U>.Cache.Layout.Size);
            return InteropNativeSliceStorage<U>.FromExistingData(view.Buffer, view.Stride, view.Length);
        }

        /// <summary>Views an inline field while preserving the original stride and length.</summary>
        public readonly InteropNativeSliceStorage<U> SliceWithStride<U>(int offset) where U : struct
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            var view = FieldGeometry(m_Buffer, m_Stride, m_Length, Cache.Layout.Size,
                InteropNativeSliceStorage<U>.Cache.Layout.Size, offset);
            return InteropNativeSliceStorage<U>.FromExistingData(view.Buffer, view.Stride, view.Length);
        }

        public readonly InteropNativeSliceStorage<U> SliceWithStride<U>() where U : struct => SliceWithStride<U>(0);

        public readonly TSlice SliceConvertToNative<U, TSlice>() where U : struct where TSlice : Il2CppObjectBase =>
            Box<U, TSlice>(SliceConvert<U>());
        public readonly TSlice SliceWithStrideToNative<U, TSlice>() where U : struct where TSlice : Il2CppObjectBase =>
            SliceWithStrideToNative<U, TSlice>(0);
        public readonly TSlice SliceWithStrideToNative<U, TSlice>(int offset)
            where U : struct where TSlice : Il2CppObjectBase => Box<U, TSlice>(SliceWithStride<U>(offset));

        private static TSlice Box<U, TSlice>(InteropNativeSliceStorage<U> view)
            where U : struct where TSlice : Il2CppObjectBase =>
            NativeArrayMemory.CreateSliceView<U, TSlice>(view.m_Buffer, view.m_Stride, view.m_Length);

        public static InteropNativeSliceStorage<T> SliceConvertFromNative<U, TSlice>(TSlice source)
            where U : struct where TSlice : Il2CppObjectBase
        {
            var view = NativeArrayMemory.ReadSliceView<U, TSlice>(source);
            return InteropNativeSliceStorage<U>.FromExistingData(view.Buffer, view.Stride, view.Length).SliceConvert<T>();
        }

        public static InteropNativeSliceStorage<T> SliceWithStrideFromNative<U, TSlice>(TSlice source)
            where U : struct where TSlice : Il2CppObjectBase => SliceWithStrideFromNative<U, TSlice>(source, 0);
        public static InteropNativeSliceStorage<T> SliceWithStrideFromNative<U, TSlice>(TSlice source, int offset)
            where U : struct where TSlice : Il2CppObjectBase
        {
            var view = NativeArrayMemory.ReadSliceView<U, TSlice>(source);
            return InteropNativeSliceStorage<U>.FromExistingData(view.Buffer, view.Stride, view.Length).SliceWithStride<T>(offset);
        }

        public readonly TSlice SliceConvertProxyToNative<U, TSlice>()
            where U : Il2CppObjectBase where TSlice : Il2CppObjectBase
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            var size = NativeArrayMemory.GetNativeSliceElementSize<U, TSlice>();
            var view = ConvertGeometry(m_Buffer, m_Stride, m_Length, Cache.Layout.Size, size);
            return NativeArrayMemory.CreateNativeSliceView<U, TSlice>(view.Buffer, view.Stride, view.Length);
        }

        public readonly TSlice SliceWithStrideProxyToNative<U, TSlice>()
            where U : Il2CppObjectBase where TSlice : Il2CppObjectBase => SliceWithStrideProxyToNative<U, TSlice>(0);
        public readonly TSlice SliceWithStrideProxyToNative<U, TSlice>(int offset)
            where U : Il2CppObjectBase where TSlice : Il2CppObjectBase
        {
            ValidateBuffer(m_Buffer, m_Stride, m_Length);
            var size = NativeArrayMemory.GetNativeSliceElementSize<U, TSlice>();
            var view = FieldGeometry(m_Buffer, m_Stride, m_Length, Cache.Layout.Size, size, offset);
            return NativeArrayMemory.CreateNativeSliceView<U, TSlice>(view.Buffer, view.Stride, view.Length);
        }

        public static InteropNativeSliceStorage<T> SliceConvertProxyFromNative<U, TSlice>(TSlice source)
            where U : Il2CppObjectBase where TSlice : Il2CppObjectBase
        {
            var sourceView = NativeArrayMemory.ReadNativeSliceView<U, TSlice>(source);
            var view = ConvertGeometry(sourceView.Buffer, sourceView.Stride, sourceView.Length,
                sourceView.ElementSize, Cache.Layout.Size);
            return FromExistingData(view.Buffer, view.Stride, view.Length);
        }

        public static InteropNativeSliceStorage<T> SliceWithStrideProxyFromNative<U, TSlice>(TSlice source)
            where U : Il2CppObjectBase where TSlice : Il2CppObjectBase => SliceWithStrideProxyFromNative<U, TSlice>(source, 0);
        public static InteropNativeSliceStorage<T> SliceWithStrideProxyFromNative<U, TSlice>(TSlice source, int offset)
            where U : Il2CppObjectBase where TSlice : Il2CppObjectBase
        {
            var sourceView = NativeArrayMemory.ReadNativeSliceView<U, TSlice>(source);
            var view = FieldGeometry(sourceView.Buffer, sourceView.Stride, sourceView.Length,
                sourceView.ElementSize, Cache.Layout.Size, offset);
            return FromExistingData(view.Buffer, view.Stride, view.Length);
        }

        private static (IntPtr Buffer, int Stride, int Length) ConvertGeometry(
            IntPtr buffer, int stride, int length, int sourceSize, int targetSize)
        {
            var bytes = checked((long)length * stride);
            var count = checked((int)(bytes / targetSize));
            var accessible = length == 0 ? 0 : checked((long)(length - 1) * stride + sourceSize);
            // A strided field view does not promise readable trailing padding after its last element.
            if (checked((long)count * targetSize) > accessible)
                throw new ArgumentException("The converted slice would extend past the source's final element.");
            return (buffer, targetSize, count);
        }

        private static (IntPtr Buffer, int Stride, int Length) FieldGeometry(
            IntPtr buffer, int stride, int length, int sourceSize, int targetSize, int offset)
        {
            if (offset < 0 || targetSize > sourceSize - offset)
                throw new ArgumentOutOfRangeException(nameof(offset), "The field view must fit within each source element.");
            return (AddBytes(buffer, offset), stride, length);
        }
    }
}
