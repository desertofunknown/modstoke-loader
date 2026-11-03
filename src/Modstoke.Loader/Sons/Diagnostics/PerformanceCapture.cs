// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Modstoke.Loader;
using Modstoke.Sons;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class PerformanceCapture
    {
        private const int SampleCapacity = 2048;
        private const double DurationSeconds = 900;
        private static readonly string[] MarkerNames = {
            "Main Thread", "Render Thread", "Physics.Simulate", "Physics.Processing",
            "Physics.FetchResults", "GC.Collect", "GC Allocated In Frame", "Draw Calls Count",
            "SetPass Calls Count", "Triangles Count", "Total Used Memory"
        };
        private static readonly string[] SchedulerTreeMarkerNames = {
            "GC Allocation In Frame Count", "GC Used Memory", "GC Reserved Memory"
        };
        private static readonly List<Counter> Counters = new();
        private static readonly HashSet<string> AttemptedMarkers = new(StringComparer.Ordinal);
        private static readonly ProfilerRecorderSample[] Samples = new ProfilerRecorderSample[SampleCapacity];
        private static readonly Statistics Frames = new();
        private static BlockingCollection<object> _output;
        private static Thread _writer;
        private static volatile bool _active;
        private static Exception _writerError;
        private static long _started, _lastFrame, _lastFlush;
        private static int _fixedUpdates, _over33Ms, _over50Ms, _dropped;
        private static double _captureWorkMs, _maxCaptureTickMs;
        private static int _rescanCounters;
        private static string _directory, _outputPath;
        private static bool _attempted, _schedulerTreeCapture;

        internal static void Initialize(HarmonySupportSnapshot support)
        {
            if (_attempted) return;
            _attempted = true;
            try
            {
                _directory = Path.Combine(LoaderPaths.UserDataDirectory, "PerformanceCaptures");
                string focusedArm = Path.Combine(_directory, "scheduler-tree-next-launch.flag");
                _schedulerTreeCapture = File.Exists(focusedArm);
                string arm = _schedulerTreeCapture ? focusedArm : Path.Combine(_directory, "capture-next-launch.flag");
                if (!File.Exists(arm)) return;
                File.Delete(arm);
                _started = _lastFlush = Stopwatch.GetTimestamp();
                _outputPath = Path.Combine(_directory, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")
                    + "-" + Environment.ProcessId + ".jsonl");
                InitializeCallbackTimings();
                // Open before activation: a missing/unwritable output must not leave hooks recording.
                var stream = new StreamWriter(new FileStream(_outputPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read), new UTF8Encoding(false));
                _output = new BlockingCollection<object>(256);
                _writer = new Thread(() => WriteOutput(stream)) { IsBackground = true, Name = "Modstoke performance capture" };
                try { _writer.Start(); }
                catch { stream.Dispose(); throw; }
                _active = true;
                Emit(new { kind = "start", utc = DateTimeOffset.UtcNow, schema = 3, durationLimitSeconds = DurationSeconds,
                    mode = _schedulerTreeCapture ? "scheduler-tree" : "general",
                    unity = Application.unityVersion, game = Application.version,
                    coreModule = typeof(PerformanceCapture).Module.ModuleVersionId,
                    notes = "Update intervals are wall time, not GPU time. Native timings are inclusive and may overlap. "
                        + "Unity samples are completed-frame samples. Zero samples does not prove zero work. "
                        + "Callback wall times and allocated bytes are inclusive; nested scopes must not be summed. "
                        + "Hitches cover consecutive loader Update ticks, not exact Unity frame boundaries; at most eight "
                        + "over-50ms details per window, with omitted counts. Callback/GC collection overhead is included. "
                        + "Managed GC counters describe the mod .NET CLR, not Unity IL2CPP; collection counts overlap "
                        + "generations and process allocation deltas are approximate. GC pause correlation is not causation. "
                        + "Native captureThread timings run on the loader Update thread; otherThreads timings may overlap it. "
                        + "Native hitch totals are attributed by completion interval, including calls that began earlier. "
                        + "Timings include some detour/interop overhead; captureTickWorkMs excludes native hook accounting. "
                        + "Instrumentation affects this session; timings are diagnostic, not a baseline benchmark. "
                        + "Unity GC Allocated In Frame is game IL2CPP activity, not per-method allocation attribution. "
                        + "Unavailable Unity counters stay unavailable; GC Used/Reserved Memory is heap occupancy, not allocation volume. "
                        + "No save contents, save paths, player identities or locations are recorded." });
                InitializeCounters();
                InitializeNativeTimings(support);
                GameRuntime.SceneChanged += OnScene;
                GameRuntime.WorldChanged += OnWorld;
                SaveLifecycle.Lifecycle += OnSave;
                Log("Performance capture started for this launch only: " + _outputPath);
            }
            catch (Exception error)
            {
                Log("Performance capture could not start: " + error.GetType().Name);
                Stop("initialization-failed");
            }
        }

        internal static void Tick()
        {
            if (!_active) return;
            long begin = Stopwatch.GetTimestamp();
            try
            {
                if (Volatile.Read(ref _writerError) != null) { Stop("writer-failed"); return; }
                ObserveCallbackInterval(begin);
                if (_lastFrame != 0)
                {
                    double ms = Milliseconds(begin - _lastFrame);
                    Frames.Add(ms);
                    if (ms > 1000.0 / 30) _over33Ms++;
                    if (ms > 50) _over50Ms++;
                }
                _lastFrame = begin;
                if (Interlocked.Exchange(ref _rescanCounters, 0) != 0) InitializeCounters();
                if (Milliseconds(begin - _lastFlush) < 1000) return;
                Flush(begin);
                if (File.Exists(Path.Combine(_directory, "stop.flag")))
                {
                    File.Delete(Path.Combine(_directory, "stop.flag"));
                    Stop("requested");
                }
                else if (Milliseconds(begin - _started) >= DurationSeconds * 1000)
                    Stop("duration-limit");
            }
            catch (Exception error)
            {
                Log("Performance capture stopped after a diagnostic error: " + error.GetType().Name);
                Stop("capture-error");
            }
            finally
            {
                double ms = Milliseconds(Stopwatch.GetTimestamp() - begin);
                _captureWorkMs += ms;
                _maxCaptureTickMs = Math.Max(_maxCaptureTickMs, ms);
            }
        }

        internal static void FixedTick()
        {
            if (_active) _fixedUpdates++;
        }

        internal static void Stop(string reason)
        {
            if (!_active)
            {
                if (reason == "application-quit" && _writer?.IsAlive == true) _writer.Join(1500);
                return;
            }
            try { Flush(Stopwatch.GetTimestamp()); }
            catch { }
            _active = false;
            GameRuntime.SceneChanged -= OnScene;
            GameRuntime.WorldChanged -= OnWorld;
            SaveLifecycle.Lifecycle -= OnSave;
            StopNativeTimings();
            foreach (var counter in Counters) counter.Dispose();
            Counters.Clear();
            Emit(new { kind = "stop", elapsedMs = ElapsedMs(), reason, droppedRecords = Volatile.Read(ref _dropped) });
            _output?.CompleteAdding();
            // The worker drains independently. Wait only during shutdown, never during gameplay.
            if (reason == "application-quit") _writer?.Join(1500);
            Log("Performance capture stopped (" + reason + "): " + _outputPath);
        }

        private static void InitializeCounters()
        {
            var requested = new List<string>(MarkerNames);
            if (_schedulerTreeCapture) requested.AddRange(SchedulerTreeMarkerNames);
            var found = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var handles = new Il2CppSystem.Collections.Generic.List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(handles);
                for (int i = 0; i < handles.Count; i++)
                {
                    var handle = handles[i];
                    var description = ProfilerRecorderHandle.GetDescription(handle);
                    string name = description.Name;
                    if (!requested.Contains(name) || !found.Add(name) || !AttemptedMarkers.Add(name)) continue;
                    ProfilerRecorder recorder = default;
                    try
                    {
                        if (description.DataType != ProfilerMarkerDataType.Int64)
                            throw new NotSupportedException("Counter is not a signed integer sample.");
                        recorder = new ProfilerRecorder(handle, SampleCapacity,
                            ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                        if (!recorder.Valid) throw new InvalidOperationException("Recorder unavailable.");
                        Counters.Add(new Counter(name, description.UnitType.ToString(), recorder));
                        Emit(new { kind = "counter", name, unit = description.UnitType.ToString(), status = "recording" });
                    }
                    catch (Exception error)
                    {
                        try { if (recorder.handle != 0) recorder.Dispose(); } catch { }
                        Emit(new { kind = "counter", name, status = "unavailable", error = error.GetType().Name });
                    }
                }
            }
            catch (Exception error)
            {
                Emit(new { kind = "counter-discovery", status = "unavailable", error = error.GetType().Name });
            }
            foreach (string name in requested)
                if (!found.Contains(name) && !AttemptedMarkers.Contains(name))
                    Emit(new { kind = "counter", name, status = "not-exposed", elapsedMs = ElapsedMs() });
        }

        private static void Flush(long now)
        {
            var counterValues = new List<object>(Counters.Count);
            foreach (var counter in Counters)
                counterValues.Add(counter.Take());
            Emit(new { kind = "window", elapsedMs = Milliseconds(now - _started), windowMs = Milliseconds(now - _lastFlush),
                updateIntervalsMs = Frames.Take(), intervalsOver33Ms = _over33Ms, intervalsOver50Ms = _over50Ms,
                fixedUpdates = _fixedUpdates, focused = Application.isFocused, timeScale = Time.timeScale,
                captureTickWorkMs = _captureWorkMs, maxCaptureTickMs = _maxCaptureTickMs, unityCounters = counterValues,
                nativeMethods = TakeNativeTimings(), callbackTimings = TakeCallbackTimings(),
                droppedRecords = Volatile.Read(ref _dropped) });
            _lastFlush = now;
            _fixedUpdates = _over33Ms = _over50Ms = 0;
            // Include this flush's collection work in the following window; disk IO runs elsewhere.
            _captureWorkMs = _maxCaptureTickMs = 0;
        }

        private static void OnScene(global::Modstoke.Sons.SceneEvent e)
        {
            if (_active) Emit(new { kind = "scene", elapsedMs = ElapsedMs(), phase = e.Phase, scene = e.SceneKind });
        }

        private static void OnWorld(WorldEvent e)
        {
            if (!_active) return;
            Emit(new { kind = "world", elapsedMs = ElapsedMs(), phase = e.Phase, inWorld = e.IsInWorld });
            if (e.IsInWorld) Interlocked.Exchange(ref _rescanCounters, 1);
        }

        private static void OnSave(SaveLifecycleEvent e)
        {
            if (_active) Emit(new { kind = "save-lifecycle", elapsedMs = ElapsedMs(), operation = e.Operation,
                phase = e.Phase, success = e.Success });
        }

        private static void Emit(object value)
        {
            try { if (_output == null || !_output.TryAdd(value)) Interlocked.Increment(ref _dropped); }
            catch (InvalidOperationException) { Interlocked.Increment(ref _dropped); }
        }

        private static void WriteOutput(StreamWriter stream)
        {
            try
            {
                using (stream)
                    foreach (object item in _output.GetConsumingEnumerable())
                    {
                        stream.WriteLine(JsonSerializer.Serialize(item));
                        stream.Flush();
                    }
            }
            catch (Exception error) { Volatile.Write(ref _writerError, error); }
        }

        private static double Milliseconds(long ticks) => ticks * (1000.0 / Stopwatch.Frequency);
        private static double ElapsedMs() => Milliseconds(Stopwatch.GetTimestamp() - _started);
        private static void Log(string message)
        {
            try { RuntimeEntryPoint.LogSource?.LogInfo(message); } catch { }
        }

        private sealed class Statistics
        {
            private int _count;
            private double _sum, _max;
            internal void Add(double value) { _count++; _sum += value; _max = Math.Max(_max, value); }
            internal object Take()
            {
                var result = new { count = _count, sum = _sum, mean = _count == 0 ? (double?)null : _sum / _count,
                    max = _count == 0 ? (double?)null : _max };
                _count = 0;
                _sum = _max = 0;
                return result;
            }
        }

        private sealed class Counter
        {
            private readonly string _name, _unit;
            private ProfilerRecorder _recorder;
            private bool _failed;
            internal Counter(string name, string unit, ProfilerRecorder recorder)
            { _name = name; _unit = unit; _recorder = recorder; }
            internal unsafe object Take()
            {
                if (_failed) return new { name = _name, status = "failed" };
                try
                {
                    var values = new Statistics();
                    bool wrapped = _recorder.WrappedAround;
                    int count;
                    fixed (ProfilerRecorderSample* samples = Samples)
                        count = _recorder.CopyTo(samples, Samples.Length, true);
                    long calls = 0;
                    for (int i = 0; i < count; i++) { values.Add(Samples[i].value); calls += Samples[i].count; }
                    return new { name = _name, unit = _unit, samples = values.Take(), markerCalls = calls,
                        bufferWrapped = wrapped, status = count == 0 ? "no-samples" : "sampled" };
                }
                catch (Exception error)
                {
                    _failed = true;
                    Dispose();
                    return new { name = _name, status = "failed", error = error.GetType().Name };
                }
            }
            internal void Dispose() { try { _recorder.Dispose(); } catch { } }
        }
    }
}
