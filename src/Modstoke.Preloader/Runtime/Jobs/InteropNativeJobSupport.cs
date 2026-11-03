// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // These CLR entry points are called by restored producers, not during conversion.
    public static class InteropNativeJobSupport
    {
        public static void Run<T>(ref T payload, IntPtr reflectionData) where T : struct =>
            NativeJobScheduler.Run(NativeJobRegistration.FromReflectionData(reflectionData), in payload);

        public static void RunParallelFor<T>(ref T payload, IntPtr reflectionData, int length) where T : struct =>
            NativeJobScheduler.RunParallelFor(NativeJobRegistration.FromReflectionData(reflectionData), in payload, length);

        public static THandle Schedule<T, THandle>(ref T payload, IntPtr reflectionData, THandle dependency)
            where T : struct where THandle : struct =>
            NativeJobScheduler.ScheduleWithHandle(NativeJobRegistration.FromReflectionData(reflectionData), in payload, dependency);

        public static THandle ScheduleFor<T, THandle>(ref T payload, IntPtr reflectionData, int length, THandle dependency)
            where T : struct where THandle : struct =>
            NativeJobScheduler.ScheduleForWithHandle(NativeJobRegistration.FromReflectionData(reflectionData), in payload, length, dependency);

        public static THandle ScheduleParallelFor<T, THandle>(ref T payload, IntPtr reflectionData, int length,
            int batchSize, THandle dependency) where T : struct where THandle : struct =>
            NativeJobScheduler.ScheduleParallelForWithHandle(NativeJobRegistration.FromReflectionData(reflectionData),
                in payload, length, batchSize, dependency);

        // Transform arrays remain caller-owned until observed completion of the tracked handle.
        public static THandle ScheduleTransform<T, TTransforms, THandle>(ref T payload, IntPtr reflectionData,
            ref TTransforms transforms, THandle dependency) where T : struct where TTransforms : struct where THandle : struct =>
            NativeJobScheduler.ScheduleParallelForTransformWithHandle<T, TTransforms, THandle>(
                NativeJobRegistration.FromReflectionData(reflectionData), in payload, in transforms, dependency);

        public static THandle ScheduleTransformReadOnly<T, TTransforms, THandle>(ref T payload, IntPtr reflectionData,
            ref TTransforms transforms, int batchSize, THandle dependency)
            where T : struct where TTransforms : struct where THandle : struct =>
            NativeJobScheduler.ScheduleParallelForTransformReadOnlyWithHandle<T, TTransforms, THandle>(
                NativeJobRegistration.FromReflectionData(reflectionData), in payload, in transforms, batchSize, dependency);

        public static THandle ScheduleParticleSystem<T, THandle>(ref T payload, IntPtr reflectionData,
            Il2CppObjectBase system, THandle dependency) where T : struct where THandle : struct =>
            NativeJobScheduler.ScheduleParticleSystemWithHandle(
                NativeJobRegistration.FromReflectionData(reflectionData), in payload, system, dependency);

        public static THandle ScheduleParticleSystemParallelFor<T, THandle>(ref T payload, IntPtr reflectionData,
            Il2CppObjectBase system, int batchSize, THandle dependency) where T : struct where THandle : struct =>
            NativeJobScheduler.ScheduleParticleSystemParallelForWithHandle(
                NativeJobRegistration.FromReflectionData(reflectionData), in payload, system, batchSize, dependency);

        public static IntPtr CreateReflectionData(Type payloadType, Delegate execute) =>
            CreateReflectionData(payloadType, payloadType, execute);

        public static IntPtr CreateReflectionData(Type payloadType, Type userType, Delegate execute)
            => CreateReflectionData(payloadType, userType, execute, NativeJobCallbackKind.Scheduled);

        public static IntPtr CreateAnimationReflectionData(Type payloadType, Delegate execute)
            => CreateReflectionData(payloadType, payloadType, execute, NativeJobCallbackKind.Animation);

        private static IntPtr CreateReflectionData(Type payloadType, Type userType, Delegate execute,
            NativeJobCallbackKind kind)
        {
            ArgumentNullException.ThrowIfNull(payloadType);
            ArgumentNullException.ThrowIfNull(userType);
            ArgumentNullException.ThrowIfNull(execute);
            var invoke = execute.GetType().GetMethod("Invoke",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var parameters = invoke?.GetParameters();
            if (parameters == null || parameters.Length != 5 ||
                !parameters[0].ParameterType.IsByRef || parameters[0].ParameterType.GetElementType() != payloadType)
                throw new ArgumentException("The job reflection payload must match the callback's first reference argument.", nameof(payloadType));

            // The runtime validates data layout, the complete callback signature and the native
            // method contract. Unity owns the resulting record; job completion must not free it.
            return NativeJobRegistration.GetOrCreate(execute, kind,
                userType == payloadType ? null : userType).ReflectionData;
        }
    }
}
