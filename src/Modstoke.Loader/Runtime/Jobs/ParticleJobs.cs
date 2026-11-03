// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Il2CppInterop.Runtime.Injection;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace Modstoke.Jobs;

/// <summary>A managed struct job that processes all particles in one callback.</summary>
public interface IParticleJob
{
    void Execute(in ParticleJobData particles);
}

/// <summary>A managed struct job that processes one particle at a time.</summary>
public interface IParticleParallelForJob
{
    /// <remarks>Write only the assigned particle index and exclusively owned output storage.</remarks>
    void Execute(in ParticleJobData particles, int index);
}

/// <summary>A managed struct job that processes a contiguous range of particles.</summary>
public interface IParticleParallelForBatchJob
{
    /// <remarks>Write only [startIndex, startIndex + count) and exclusively owned output storage.</remarks>
    void Execute(in ParticleJobData particles, int startIndex, int count);
}

/// <summary>Schedules managed particle jobs through Unity's native simulation dependencies.</summary>
/// <remarks>
/// Submit on the Unity main thread. Keep the particle system and payload buffers alive until
/// completion; do not destroy, clear, resize or otherwise mutate them concurrently. Data views
/// are borrowed only for Execute and must not escape through unsafe pointers. The payload is
/// copied without copyback. Native internal completion alone does not release managed ownership.
/// Requires the matched Windows x64 Unity 2022.2.16 runtime; does not enable Burst compilation.
/// </remarks>
public static class ParticleJobs
{
    public static NativeJob Schedule<T>(in T job, ParticleSystem system, NativeJob dependency = null)
        where T : struct, IParticleJob
    {
        ArgumentNullException.ThrowIfNull(system);
        return NativeJobScheduler.ScheduleParticleSystem(SingleProducer<T>.Registration, in job, system, dependency);
    }

    public static NativeJob ScheduleParallelFor<T>(in T job, ParticleSystem system, int batchSize,
        NativeJob dependency = null) where T : struct, IParticleParallelForJob
    {
        RequireParallelArguments(system, batchSize);
        return NativeJobScheduler.ScheduleParticleSystemParallelFor(
            IndexedProducer<T>.Registration, in job, system, batchSize, dependency);
    }

    public static NativeJob ScheduleBatch<T>(in T job, ParticleSystem system, int batchSize,
        NativeJob dependency = null) where T : struct, IParticleParallelForBatchJob
    {
        RequireParallelArguments(system, batchSize);
        return NativeJobScheduler.ScheduleParticleSystemParallelFor(
            BatchProducer<T>.Registration, in job, system, batchSize, dependency);
    }

    /// <summary>Schedules whole-system work with a tracked Unity handle instead of an explicit owner.</summary>
    /// <remarks>Requires the matching preloader completion observers; prefer Schedule for explicit ownership.</remarks>
    public static JobHandle ScheduleWithHandle<T>(in T job, ParticleSystem system, JobHandle dependency = default)
        where T : struct, IParticleJob
    {
        ArgumentNullException.ThrowIfNull(system);
        return NativeJobScheduler.ScheduleParticleSystemWithHandle<T, JobHandle>(
            SingleProducer<T>.Registration, in job, system, dependency);
    }

    /// <summary>Schedules indexed work with a tracked Unity handle.</summary>
    /// <remarks>Requires the matching preloader completion observers.</remarks>
    public static JobHandle ScheduleParallelForWithHandle<T>(in T job, ParticleSystem system, int batchSize,
        JobHandle dependency = default) where T : struct, IParticleParallelForJob
    {
        RequireParallelArguments(system, batchSize);
        return NativeJobScheduler.ScheduleParticleSystemParallelForWithHandle<T, JobHandle>(
            IndexedProducer<T>.Registration, in job, system, batchSize, dependency);
    }

    /// <summary>Schedules batch work with a tracked Unity handle.</summary>
    /// <remarks>Requires the matching preloader completion observers.</remarks>
    public static JobHandle ScheduleBatchWithHandle<T>(in T job, ParticleSystem system, int batchSize,
        JobHandle dependency = default) where T : struct, IParticleParallelForBatchJob
    {
        RequireParallelArguments(system, batchSize);
        return NativeJobScheduler.ScheduleParticleSystemParallelForWithHandle<T, JobHandle>(
            BatchProducer<T>.Registration, in job, system, batchSize, dependency);
    }

    private static void RequireParallelArguments(ParticleSystem system, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
    }

    private delegate void ExecuteJobFunction<T>(ref T job, IntPtr additionalData, IntPtr bufferRangePatchData,
        ref JobRanges ranges, int jobIndex) where T : struct;

    private static NativeJobRegistration GetRegistration(ref NativeJobRegistration slot, Delegate callback)
    {
        var registration = Volatile.Read(ref slot);
        if (registration != null) return registration;
        // Native registration already serializes cold initialization of this stable delegate.
        registration = NativeJobRegistration.GetOrCreate(callback);
        Interlocked.CompareExchange(ref slot, registration, null);
        return registration;
    }

    private static class SingleProducer<T> where T : struct, IParticleJob
    {
        private static readonly ExecuteJobFunction<T> Callback = Execute;
        private static NativeJobRegistration _registration;
        internal static NativeJobRegistration Registration => GetRegistration(ref _registration, Callback);

        private static void Execute(ref T job, IntPtr additionalData, IntPtr bufferRangePatchData,
            ref JobRanges ranges, int jobIndex)
        {
            var particles = ParticleJobData.Read(additionalData);
            job.Execute(in particles);
        }
    }

    private static class IndexedProducer<T> where T : struct, IParticleParallelForJob
    {
        private static readonly ExecuteJobFunction<T> Callback = Execute;
        private static NativeJobRegistration _registration;
        internal static NativeJobRegistration Registration => GetRegistration(ref _registration, Callback);

        private static void Execute(ref T job, IntPtr additionalData, IntPtr bufferRangePatchData,
            ref JobRanges ranges, int jobIndex)
        {
            var particles = ParticleJobData.Read(additionalData);
            while (JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out var begin, out var end))
            {
                RequireRange(particles.Count, begin, end);
                for (var index = begin; index < end; index++) job.Execute(in particles, index);
            }
        }
    }

    private static class BatchProducer<T> where T : struct, IParticleParallelForBatchJob
    {
        private static readonly ExecuteJobFunction<T> Callback = Execute;
        private static NativeJobRegistration _registration;
        internal static NativeJobRegistration Registration => GetRegistration(ref _registration, Callback);

        private static void Execute(ref T job, IntPtr additionalData, IntPtr bufferRangePatchData,
            ref JobRanges ranges, int jobIndex)
        {
            var particles = ParticleJobData.Read(additionalData);
            while (JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out var begin, out var end))
            {
                RequireRange(particles.Count, begin, end);
                job.Execute(in particles, begin, end - begin);
            }
        }
    }

    private static void RequireRange(int count, int begin, int end)
    {
        if (begin < 0 || end < begin || end > count)
            throw new InvalidOperationException("Unity supplied an invalid particle work range.");
    }
}
