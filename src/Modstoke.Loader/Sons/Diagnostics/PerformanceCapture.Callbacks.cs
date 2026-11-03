// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    public readonly struct CaptureTimingScope : IDisposable
    {
        private readonly Modstoke.Loader.PerformanceCapture.CallbackTiming _timing;
        private readonly long _started, _allocated, _interval;

        internal CaptureTimingScope(Modstoke.Loader.PerformanceCapture.CallbackTiming timing,
            long interval)
        {
            _timing = timing;
            _interval = interval;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_timing != null)
                global::Modstoke.Loader.PerformanceCapture.EndCallback(_timing, _interval, _started, _allocated);
        }
    }

    internal static partial class PerformanceCapture
    {
        private const int MaxCallbackNames = 256;
        private const int MaxHitchesPerWindow = 8;
        private static readonly Dictionary<string, CallbackTiming> CallbackTimings = new(StringComparer.Ordinal);
        private static readonly Dictionary<(string, MethodInfo), string> CallbackNames = new();
        private static int _captureThread, _hitchesEmitted, _hitchesOmitted, _callbackNamesOmitted;
        private static long _callbackInterval;
        private static int _intervalStartUnityFrame;
        private static ManagedSample _intervalGc, _windowGc;

        internal static CaptureTimingScope MeasureCallback(string name)
        {
            if (!_active || Environment.CurrentManagedThreadId != _captureThread) return default;
            if (string.IsNullOrEmpty(name) || name.Length > 256) return default;
            if (!CallbackTimings.TryGetValue(name, out var timing))
            {
                if (CallbackTimings.Count >= MaxCallbackNames) { _callbackNamesOmitted++; return default; }
                timing = new CallbackTiming(name);
                CallbackTimings.Add(name, timing);
            }
            return new CaptureTimingScope(timing, _callbackInterval);
        }

        internal static CaptureTimingScope MeasureCallback(Delegate callback, string category)
        {
            if (!_active || Environment.CurrentManagedThreadId != _captureThread || callback == null) return default;
            var key = (category, callback.Method);
            if (!CallbackNames.TryGetValue(key, out var name))
            {
                if (CallbackNames.Count >= MaxCallbackNames) { _callbackNamesOmitted++; return default; }
                // Cache code identity, never the delegate target or player data.
                name = category + "." + key.Item2.Module.Assembly.GetName().Name + "."
                    + key.Item2.DeclaringType?.FullName + "." + key.Item2.Name;
                if (name.Length > 256) name = name.Substring(0, 256);
                CallbackNames.Add(key, name);
            }
            return MeasureCallback(name);
        }

        internal static void EndCallback(CallbackTiming timing, long interval, long started, long allocated)
        {
            if (!_active || Environment.CurrentManagedThreadId != _captureThread) return;
            timing.Record(interval, Milliseconds(Stopwatch.GetTimestamp() - started),
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocated));
        }

        private static void InitializeCallbackTimings()
        {
            _captureThread = Environment.CurrentManagedThreadId;
            _intervalGc = _windowGc = ManagedSample.Read();
            _intervalStartUnityFrame = UnityEngine.Time.frameCount;
        }

        private static void ObserveCallbackInterval(long now)
        {
            long nativeInterval = _schedulerTreeCapture ? Interlocked.Increment(ref _nativeInterval) - 1 : -1;
            var gc = ManagedSample.Read();
            int frame = UnityEngine.Time.frameCount;
            double intervalMs = Milliseconds(now - _lastFrame);
            if (_lastFrame != 0 && intervalMs > 50)
            {
                if (_hitchesEmitted < MaxHitchesPerWindow)
                {
                    var callbacks = new List<object>();
                    foreach (var timing in CallbackTimings.Values)
                        if (timing.Interval == _callbackInterval) callbacks.Add(timing.TakeInterval());
                    Emit(new { kind = "hitch", elapsedMs = Milliseconds(now - _started), intervalMs,
                        startUnityFrame = _intervalStartUnityFrame, endUnityFrame = frame,
                        focused = UnityEngine.Application.isFocused, timeScale = UnityEngine.Time.timeScale,
                        callbacks, managedDotnet = gc.Delta(_intervalGc),
                        nativeMethods = _schedulerTreeCapture ? TakeNativeInterval(nativeInterval) : null });
                    _hitchesEmitted++;
                }
                else _hitchesOmitted++;
            }
            _callbackInterval++;
            _intervalStartUnityFrame = frame;
            _intervalGc = gc;
        }

        private static object TakeCallbackTimings()
        {
            var callbacks = new List<object>(CallbackTimings.Count);
            foreach (var timing in CallbackTimings.Values)
                if (timing.HasWindowSamples) callbacks.Add(timing.TakeWindow());
            var gc = ManagedSample.Read();
            var result = new { callbacks, managedDotnet = gc.Delta(_windowGc),
                hitchRecords = _hitchesEmitted, hitchRecordsOmitted = _hitchesOmitted,
                callbackNamesOmitted = _callbackNamesOmitted };
            _windowGc = gc;
            _hitchesEmitted = _hitchesOmitted = _callbackNamesOmitted = 0;
            return result;
        }

        internal sealed class CallbackTiming
        {
            private readonly string _name;
            private int _count, _intervalCount;
            private double _totalMs, _maxMs, _intervalMs, _intervalMaxMs;
            private long _allocated, _intervalAllocated;
            internal long Interval { get; private set; } = -1;
            internal bool HasWindowSamples => _count != 0;

            internal CallbackTiming(string name) { _name = name; }

            internal void Record(long interval, double ms, long allocated)
            {
                _count++;
                _totalMs += ms;
                _maxMs = Math.Max(_maxMs, ms);
                _allocated += allocated;
                if (Interval != interval)
                {
                    Interval = interval;
                    _intervalCount = 0;
                    _intervalMs = _intervalMaxMs = 0;
                    _intervalAllocated = 0;
                }
                _intervalCount++;
                _intervalMs += ms;
                _intervalMaxMs = Math.Max(_intervalMaxMs, ms);
                _intervalAllocated += allocated;
            }

            internal object TakeInterval() => new { name = _name, calls = _intervalCount,
                inclusiveMs = _intervalMs, maxMs = _intervalMaxMs, inclusiveAllocatedBytes = _intervalAllocated };

            internal object TakeWindow()
            {
                var result = new { name = _name, calls = _count, inclusiveMs = _totalMs, maxMs = _maxMs,
                    inclusiveAllocatedBytes = _allocated };
                _count = 0;
                _totalMs = _maxMs = 0;
                _allocated = 0;
                return result;
            }
        }

        private readonly struct ManagedSample
        {
            private readonly int _gen0, _gen1, _gen2;
            private readonly long _pauseTicks, _mainAllocated, _processAllocated;

            private ManagedSample(int gen0, int gen1, int gen2, long pauseTicks, long mainAllocated, long processAllocated)
            {
                _gen0 = gen0; _gen1 = gen1; _gen2 = gen2;
                _pauseTicks = pauseTicks; _mainAllocated = mainAllocated; _processAllocated = processAllocated;
            }

            internal static ManagedSample Read() => new(GC.CollectionCount(0), GC.CollectionCount(1),
                GC.CollectionCount(2), GC.GetTotalPauseDuration().Ticks, GC.GetAllocatedBytesForCurrentThread(),
                GC.GetTotalAllocatedBytes(precise: false));

            internal object Delta(ManagedSample earlier) => new {
                gen0Collections = _gen0 - earlier._gen0, gen1Collections = _gen1 - earlier._gen1,
                gen2Collections = _gen2 - earlier._gen2,
                totalPauseMs = (_pauseTicks - earlier._pauseTicks) / (double)TimeSpan.TicksPerMillisecond,
                mainThreadAllocatedBytes = Math.Max(0, _mainAllocated - earlier._mainAllocated),
                approximateProcessAllocatedBytes = Math.Max(0, _processAllocated - earlier._processAllocated)
            };
        }
    }
}
