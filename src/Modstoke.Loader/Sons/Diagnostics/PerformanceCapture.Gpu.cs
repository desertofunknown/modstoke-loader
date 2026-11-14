// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Modstoke.Loader
{
    // Windows keeps per-process GPU engine counters, the ones Task Manager shows. A capture samples them once a second
    // on its own thread, so each window says whether the game was waiting on the GPU or on its own threads, and whether
    // anything ran on the compute engines DirectX 12's async compute uses.
    internal static partial class PerformanceCapture
    {
        private const uint PdhFormatDouble = 0x00000200;
        private const int PdhMoreData = unchecked((int)0x800007D2);
        private static Thread _gpuSampler;
        private static volatile GpuSample _gpu;
        private static volatile bool _gpuStopping;

        private sealed class GpuSample
        {
            internal double Engine3dPercent, ComputePercent, CopyPercent, DedicatedMemoryMb;
        }

        private static void StartGpuSampling()
        {
            _gpuStopping = false;
            _gpuSampler = new Thread(SampleGpu) { IsBackground = true, Name = "Modstoke GPU usage" };
            try { _gpuSampler.Start(); }
            catch (Exception error) { Emit(new { kind = "gpu", status = "unavailable", error = error.GetType().Name }); }
        }

        private static void StopGpuSampling() => _gpuStopping = true;

        // The latest sample for a window record; null until two samples exist, since utilization is a rate.
        private static object TakeGpu()
        {
            var sample = _gpu;
            return sample == null ? null : new
            {
                engine3dPct = Math.Round(sample.Engine3dPercent, 1),
                computePct = Math.Round(sample.ComputePercent, 1),
                copyPct = Math.Round(sample.CopyPercent, 1),
                dedicatedMb = Math.Round(sample.DedicatedMemoryMb)
            };
        }

        private static void SampleGpu()
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) throw new InvalidOperationException("PdhOpenQuery");
                if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var engines) != 0)
                    throw new InvalidOperationException("GPU Engine counters");
                PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out var memory);
                string process = "pid_" + Environment.ProcessId + "_";
                PdhCollectQueryData(query);
                Emit(new { kind = "gpu", status = "sampling", elapsedMs = ElapsedMs() });
                while (!_gpuStopping && _active)
                {
                    Thread.Sleep(1000);
                    if (PdhCollectQueryData(query) != 0) continue;
                    var sample = new GpuSample();
                    foreach (var (name, value) in Read(engines))
                    {
                        if (!name.StartsWith(process, StringComparison.Ordinal)) continue;
                        // Engines of one type are reported separately; the busiest stands for the type, as in Task Manager.
                        // Compute engines are numbered ("engtype_Compute 0"), and some drivers add a high-priority one.
                        if (name.EndsWith("engtype_3D", StringComparison.Ordinal)) sample.Engine3dPercent = Math.Max(sample.Engine3dPercent, value);
                        else if (name.Contains("engtype_Compute", StringComparison.Ordinal) || name.EndsWith("engtype_High Priority Compute", StringComparison.Ordinal))
                            sample.ComputePercent = Math.Max(sample.ComputePercent, value);
                        else if (name.EndsWith("engtype_Copy", StringComparison.Ordinal)) sample.CopyPercent = Math.Max(sample.CopyPercent, value);
                    }
                    if (memory != IntPtr.Zero)
                        foreach (var (name, value) in Read(memory))
                            if (name.StartsWith(process, StringComparison.Ordinal)) sample.DedicatedMemoryMb += value / 1048576;
                    _gpu = sample;
                }
            }
            catch (Exception error)
            {
                Emit(new { kind = "gpu", status = "unavailable", error = error.Message });
            }
            finally
            {
                if (query != IntPtr.Zero) PdhCloseQuery(query);
            }
        }

        // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer, status, padding, double value.
        private static (string Name, double Value)[] Read(IntPtr counter)
        {
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(counter, PdhFormatDouble, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0)
                return Array.Empty<(string, double)>();
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFormatDouble, ref size, out var count, buffer) != 0)
                    return Array.Empty<(string, double)>();
                var items = new (string, double)[count];
                for (int index = 0; index < count; index++)
                {
                    IntPtr item = buffer + index * 24;
                    items[index] = (Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "",
                        Marshal.ReadInt32(item, 8) == 0 ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16)) : 0);
                }
                return items;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

        [DllImport("pdh.dll")]
        private static extern int PdhCloseQuery(IntPtr query);
    }
}
