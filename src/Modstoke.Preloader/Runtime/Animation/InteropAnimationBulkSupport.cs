// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Public because restored AnimationModule methods call these closed generic helpers.
    public static class InteropAnimationBulkSupport
    {
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory =
            InteropExceptionSupport.WrapNativeException;

        public static void ReadSceneInts<TStream, THandle, TArray>(TStream stream,
            InteropNativeArrayStorage<THandle> handles, TArray buffer)
            where TStream : struct where THandle : struct where TArray : Il2CppObjectBase =>
            NativeAnimationCalls.InvokeBulkArray<TStream, THandle, TArray>(
                NativeAnimationCalls.BulkArrayOperation.ReadSceneInts, ref stream,
                handles.m_Buffer, handles.m_Length, buffer, false, ExceptionFactory);

        public static void ReadSceneFloats<TStream, THandle, TArray>(TStream stream,
            InteropNativeArrayStorage<THandle> handles, TArray buffer)
            where TStream : struct where THandle : struct where TArray : Il2CppObjectBase =>
            NativeAnimationCalls.InvokeBulkArray<TStream, THandle, TArray>(
                NativeAnimationCalls.BulkArrayOperation.ReadSceneFloats, ref stream,
                handles.m_Buffer, handles.m_Length, buffer, false, ExceptionFactory);

        public static void ReadStreamInts<TStream, THandle, TArray>(TStream stream,
            InteropNativeArrayStorage<THandle> handles, TArray buffer)
            where TStream : struct where THandle : struct where TArray : Il2CppObjectBase =>
            NativeAnimationCalls.InvokeBulkArray<TStream, THandle, TArray>(
                NativeAnimationCalls.BulkArrayOperation.ReadStreamInts, ref stream,
                handles.m_Buffer, handles.m_Length, buffer, false, ExceptionFactory);

        public static void ReadStreamFloats<TStream, THandle, TArray>(TStream stream,
            InteropNativeArrayStorage<THandle> handles, TArray buffer)
            where TStream : struct where THandle : struct where TArray : Il2CppObjectBase =>
            NativeAnimationCalls.InvokeBulkArray<TStream, THandle, TArray>(
                NativeAnimationCalls.BulkArrayOperation.ReadStreamFloats, ref stream,
                handles.m_Buffer, handles.m_Length, buffer, false, ExceptionFactory);

        public static void WriteStreamInts<TStream, THandle, TArray>(TStream stream,
            InteropNativeArrayStorage<THandle> handles, TArray buffer, bool useMask)
            where TStream : struct where THandle : struct where TArray : Il2CppObjectBase =>
            NativeAnimationCalls.InvokeBulkArray<TStream, THandle, TArray>(
                NativeAnimationCalls.BulkArrayOperation.WriteStreamInts, ref stream,
                handles.m_Buffer, handles.m_Length, buffer, useMask, ExceptionFactory);

        public static void WriteStreamFloats<TStream, THandle, TArray>(TStream stream,
            InteropNativeArrayStorage<THandle> handles, TArray buffer, bool useMask)
            where TStream : struct where THandle : struct where TArray : Il2CppObjectBase =>
            NativeAnimationCalls.InvokeBulkArray<TStream, THandle, TArray>(
                NativeAnimationCalls.BulkArrayOperation.WriteStreamFloats, ref stream,
                handles.m_Buffer, handles.m_Length, buffer, useMask, ExceptionFactory);
    }
}
