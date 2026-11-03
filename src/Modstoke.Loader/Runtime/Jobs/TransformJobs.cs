// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine.Jobs;

namespace Modstoke.Jobs;

/// <summary>A managed struct job that operates on Unity's native transform access values.</summary>
public interface ITransformJob
{
    /// <summary>Processes one transform using its original index in the submitted array.</summary>
    /// <remarks>Do not retain transform, schedule jobs, or call main-thread-only Unity APIs here.</remarks>
    void Execute(int index, TransformAccess transform);
}

/// <summary>Schedules managed transform jobs through Unity's native hierarchy-aware scheduler.</summary>
/// <remarks>
/// Requires the matched Windows x64 Unity 2022.2.16 job runtime. Job payloads are copied;
/// their supported native container buffers and the transform array remain caller-owned.
/// Do not mutate, dispose, or reuse these resources until all jobs using them complete.
/// Read-only jobs must not write transforms. Unity's retail player does not enforce that promise.
/// This SDK interface does not change the native IJobParallelForTransform wrapper.
/// </remarks>
public static unsafe class TransformJobs
{
    /// <summary>Schedules writable transform work and returns an owner that must be completed or disposed.</summary>
    /// <remarks>Implicit native hierarchy dependencies do not release unrelated managed job owners.</remarks>
    public static NativeJob Schedule<T>(in T job, in TransformAccessArray transforms,
        NativeJob dependency = null) where T : struct, ITransformJob =>
        NativeJobScheduler.ScheduleParallelForTransform(Producer<T>.Registration, in job, in transforms, dependency);

    /// <summary>Schedules read-only work with a positive batch size and explicit completion ownership.</summary>
    public static NativeJob ScheduleReadOnly<T>(in T job, in TransformAccessArray transforms, int batchSize,
        NativeJob dependency = null) where T : struct, ITransformJob
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return NativeJobScheduler.ScheduleParallelForTransformReadOnly(
            Producer<T>.Registration, in job, in transforms, batchSize, dependency);
    }

    /// <summary>Runs read-only work synchronously and propagates a recorded job failure.</summary>
    public static void RunReadOnly<T>(in T job, in TransformAccessArray transforms) where T : struct, ITransformJob =>
        NativeJobScheduler.RunParallelForTransformReadOnly(Producer<T>.Registration, in job, in transforms);

    /// <summary>Schedules writable work and returns a Unity handle tracked by the loader's completion observers.</summary>
    /// <remarks>Requires the matching preloader handle observers; prefer Schedule for explicit ownership.</remarks>
    public static JobHandle ScheduleWithHandle<T>(in T job, in TransformAccessArray transforms,
        JobHandle dependency = default) where T : struct, ITransformJob =>
        NativeJobScheduler.ScheduleParallelForTransformWithHandle<T, TransformAccessArray, JobHandle>(
            Producer<T>.Registration, in job, in transforms, dependency);

    /// <summary>Schedules read-only work and returns a Unity handle tracked by the loader's completion observers.</summary>
    /// <remarks>Requires the matching preloader handle observers; prefer ScheduleReadOnly for explicit ownership.</remarks>
    public static JobHandle ScheduleReadOnlyWithHandle<T>(in T job, in TransformAccessArray transforms, int batchSize,
        JobHandle dependency = default) where T : struct, ITransformJob
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return NativeJobScheduler.ScheduleParallelForTransformReadOnlyWithHandle<T, TransformAccessArray, JobHandle>(
            Producer<T>.Registration, in job, in transforms, batchSize, dependency);
    }

    private static class Producer<T> where T : struct, ITransformJob
    {
        private delegate void ExecuteJobFunction(ref T job, IntPtr additionalData, IntPtr bufferRangePatchData,
            ref JobRanges ranges, int jobIndex);

        private static readonly ExecuteJobFunction Callback = Execute;
        private static NativeJobRegistration _registration;

        internal static NativeJobRegistration Registration
        {
            get
            {
                var registration = Volatile.Read(ref _registration);
                if (registration != null) return registration;
                // GetOrCreate already serializes native registration for this stable delegate.
                registration = NativeJobRegistration.GetOrCreate(Callback);
                Interlocked.CompareExchange(ref _registration, registration, null);
                return registration;
            }
        }

        private static void Execute(ref T job, IntPtr additionalData, IntPtr bufferRangePatchData,
            ref JobRanges ranges, int jobIndex)
        {
            RequireTransformLayout();
            if (additionalData == IntPtr.Zero)
                throw new InvalidOperationException("Unity supplied no transform job data.");
            var data = *(TransformJobData*)additionalData;
            if (data.TransformAccessArray == IntPtr.Zero || data.IsReadOnly is not (0 or 1))
                throw new InvalidOperationException("Unity supplied an invalid transform job descriptor.");
            var userIndices = (int*)TransformAccessArray.GetSortedToUserIndex(data.TransformAccessArray);
            var transforms = (TransformAccess*)TransformAccessArray.GetSortedTransformAccess(data.TransformAccessArray);
            if (data.IsReadOnly == 1)
            {
                while (JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out var begin, out var end))
                    ExecuteRange(ref job, userIndices, transforms, begin, end);
            }
            else
            {
                JobsUtility.GetJobRange(ref ranges, jobIndex, out var begin, out var end);
                ExecuteRange(ref job, userIndices, transforms, begin, end);
            }
        }

        private static void ExecuteRange(ref T job, int* userIndices, TransformAccess* transforms, int begin, int end)
        {
            if (begin < 0 || end < begin || begin != end && (userIndices == null || transforms == null))
                throw new InvalidOperationException("Unity supplied an invalid transform work range.");
            // Unity sorts by hierarchy. The user contract keeps original indices and value-copy access.
            for (var index = begin; index < end; index++)
                job.Execute(userIndices[index], transforms[index]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TransformJobData
    {
        internal IntPtr TransformAccessArray;
        internal int IsReadOnly;
    }

    private static readonly object LayoutGate = new();
    private static IntPtr _transformClass;

    // Called only inside the runtime's attached job callback, before reading native arrays.
    private static void RequireTransformLayout()
    {
        var validated = Volatile.Read(ref _transformClass);
        if (validated == IntPtr.Zero)
        {
            lock (LayoutGate)
            {
                validated = _transformClass;
                if (validated == IntPtr.Zero)
                {
                    if (IntPtr.Size != 8 || sizeof(TransformJobData) != 16 || sizeof(TransformAccess) != 16 ||
                        !typeof(TransformAccess).IsExplicitLayout ||
                        Marshal.OffsetOf<TransformAccess>("hierarchy") != IntPtr.Zero ||
                        Marshal.OffsetOf<TransformAccess>("index") != new IntPtr(8))
                        throw new NotSupportedException("The CLR transform access layout does not match Unity 2022.2.16 x64.");
                    validated = Il2CppClassPointerStore<TransformAccess>.NativeClassPtr;
                    uint alignment = 0;
                    if (validated == IntPtr.Zero || validated != IL2CPP.GetIl2CppClass(
                            "UnityEngine.CoreModule.dll", "UnityEngine.Jobs", "TransformAccess") ||
                        !IL2CPP.il2cpp_class_is_valuetype(validated) || IL2CPP.il2cpp_class_has_references(validated) ||
                        IL2CPP.il2cpp_class_value_size(validated, ref alignment) != 16 || alignment != 8 ||
                        IL2CPP.il2cpp_class_array_element_size(validated) != 16)
                        throw new NotSupportedException("The native transform access layout does not match the job producer.");
                    RequireField(validated, "hierarchy", 16, Il2CppClassPointerStore<IntPtr>.NativeClassPtr);
                    RequireField(validated, "index", 24, Il2CppClassPointerStore<int>.NativeClassPtr);
                    Volatile.Write(ref _transformClass, validated);
                }
            }
        }
        if (validated != Il2CppClassPointerStore<TransformAccess>.NativeClassPtr)
            throw new NotSupportedException("The transform access native class identity has changed.");
    }

    private static void RequireField(IntPtr type, string name, uint boxedOffset, IntPtr fieldType)
    {
        var field = IL2CPP.il2cpp_class_get_field_from_name(type, name);
        if (field == IntPtr.Zero || IL2CPP.il2cpp_field_get_offset(field) != boxedOffset ||
            IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) != fieldType)
            throw new NotSupportedException("The native transform access field is incompatible: " + name);
    }
}
