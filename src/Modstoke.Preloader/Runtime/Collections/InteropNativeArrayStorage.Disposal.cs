// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public unsafe partial struct InteropNativeArrayStorage<T> where T : struct
    {
        /// <summary>Schedules disposal after a Unity JobHandle dependency and returns its completion handle.</summary>
        /// <remarks>
        /// THandle must be the matching generated Unity.Jobs.JobHandle. Complete the returned handle,
        /// or an owned downstream handle, through the runtime's completion observers. All copies and
        /// views become unusable once disposal is scheduled; they are not invalidated automatically.
        /// A failure before submission leaves this descriptor unchanged. If a NativeJobSchedulingException
        /// is thrown, its Job owns the possibly submitted disposal and this descriptor is invalidated.
        /// With a valid handle, recover completion through that Job. Without one, ownership remains
        /// unresolved until process exit. Never free a saved alias or retry disposal separately.
        /// </remarks>
        public THandle Dispose<THandle>(THandle dependency) where THandle : struct
        {
            if (m_Buffer == IntPtr.Zero) return dependency;
            if (m_AllocatorLabel >= 64)
                throw new InvalidOperationException("Scheduled disposal does not support custom allocators.");
            if (m_AllocatorLabel <= 1)
            {
                m_Buffer = IntPtr.Zero;
                return dependency;
            }

            THandle result;
            try
            {
                result = InteropNativeArrayDisposal.Schedule(m_Buffer, m_AllocatorLabel, dependency);
            }
            catch (NativeJobSchedulingException)
            {
                // Native submission or handle export can fail after Unity takes the payload.
                // Its retained job is now the only owner allowed to complete disposal.
                m_Buffer = IntPtr.Zero;
                m_AllocatorLabel = 0;
                throw;
            }
            m_Buffer = IntPtr.Zero;
            m_AllocatorLabel = 0;
            return result;
        }
    }

    internal static class InteropNativeArrayDisposal
    {
        // One producer for every element/handle type; no captured buffer or per-call registration.
        private static readonly ExecuteCallback Execute = Free;
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory =
            InteropExceptionSupport.WrapNativeException;

        [StructLayout(LayoutKind.Sequential)]
        private struct Payload
        {
            internal IntPtr Buffer;
            internal int Allocator;
        }

        // The matched CoreModule layout. Registration verifies every field against native metadata.
        [StructLayout(LayoutKind.Sequential)]
        private struct JobRanges
        {
            internal int BatchSize;
            internal int NumJobs;
            internal int TotalIterationCount;
            internal IntPtr StartEndIndex;
        }

        private delegate void ExecuteCallback(ref Payload payload, IntPtr additionalData,
            IntPtr patchData, ref JobRanges ranges, int jobIndex);

        internal static THandle Schedule<THandle>(IntPtr buffer, int allocator, THandle dependency)
            where THandle : struct
        {
            // GetOrCreate rechecks the active runtime/domain without caching a failed startup.
            var registration = NativeJobRegistration.GetOrCreate(Execute);
            var payload = new Payload { Buffer = buffer, Allocator = allocator };
            return NativeJobScheduler.ScheduleWithHandle(registration, in payload, dependency);
        }

        private static void Free(ref Payload payload, IntPtr additionalData,
            IntPtr patchData, ref JobRanges ranges, int jobIndex)
        {
            NativeArrayMemory.FreeTracked(payload.Buffer, payload.Allocator, ExceptionFactory);
            payload.Buffer = IntPtr.Zero;
        }
    }
}
