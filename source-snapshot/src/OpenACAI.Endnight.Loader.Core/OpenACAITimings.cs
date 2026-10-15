// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Lightweight loader timing service for startup and UI diagnostics.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OpenACAI.Loader
{
    public static class OpenACAITimings
    {
        public const string ServiceKey = "openacai.loader.timings";

        private const int MaxRecentEvents = 64;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAITimingMetric> Metrics = new Dictionary<string, OpenACAITimingMetric>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<OpenACAITimingEventSnapshot> RecentEvents = new Queue<OpenACAITimingEventSnapshot>();
        private static DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
        private static DateTimeOffset _lastUpdatedUtc = _startedUtc;
        private static long _startedTimestamp = Stopwatch.GetTimestamp();
        private static DateTimeOffset? _processStartedUtc;
        private static int? _processId;
        private static string _processName;
        private static int _eventSequence;
        private static bool _initialized;

        public static IReadOnlyDictionary<string, OpenACAITimingMetricSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return Metrics.ToDictionary(pair => pair.Key, pair => pair.Value.CreateSnapshot(), StringComparer.OrdinalIgnoreCase);
            }
        }

        public static OpenACAITimingStateSnapshot State => CreateState();

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                if (_initialized)
                {
                    PublishServiceNoLock();
                    return;
                }

                _startedUtc = DateTimeOffset.UtcNow;
                _lastUpdatedUtc = _startedUtc;
                _startedTimestamp = Stopwatch.GetTimestamp();
                _eventSequence = 0;
                CaptureProcessSnapshotNoLock();
                _initialized = true;
                AddEventNoLock("loader", "timings-initialized", null, null);
                PublishServiceNoLock();
            }
        }

        internal static OpenACAITimingScope Measure(string area, string phase, string detail = null)
        {
            EnsureInitialized();
            return new OpenACAITimingScope(area, phase, detail);
        }

        internal static void Mark(string area, string phase, string detail = null)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                AddEventNoLock(area, phase, null, detail);
                PublishServiceNoLock();
            }
        }

        internal static void RecordDuration(string area, string phase, TimeSpan duration, string detail = null)
        {
            EnsureInitialized();
            var durationMs = Math.Max(0d, duration.TotalMilliseconds);
            lock (SyncRoot)
            {
                var key = CreateKey(area, phase);
                OpenACAITimingMetric metric;
                if (!Metrics.TryGetValue(key, out metric))
                {
                    metric = new OpenACAITimingMetric(area, phase);
                    Metrics[key] = metric;
                }

                metric.Record(durationMs, detail);
                AddEventNoLock(area, phase, durationMs, detail);
                PublishServiceNoLock();
            }
        }

        internal static OpenACAITimingStateSnapshot CreateState()
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                return CreateStateNoLock();
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            Initialize();
        }

        private static OpenACAITimingStateSnapshot CreateStateNoLock()
        {
            var metrics = Metrics.ToDictionary(pair => pair.Key, pair => pair.Value.CreateSnapshot(), StringComparer.OrdinalIgnoreCase);
            var events = RecentEvents.ToArray();
            return new OpenACAITimingStateSnapshot(
                1,
                ServiceKey,
                _startedUtc.ToString("O"),
                _lastUpdatedUtc.ToString("O"),
                Environment.Version.ToString(),
                _processId,
                _processName,
                _processStartedUtc.HasValue ? _processStartedUtc.Value.ToString("O") : null,
                TimingsStartedAfterProcessStartMsNoLock(),
                ElapsedSinceStartMsNoLock(),
                ElapsedSinceProcessStartMsNoLock(DateTimeOffset.UtcNow),
                metrics.Count,
                _eventSequence,
                events.Length,
                metrics,
                events);
        }

        private static void PublishServiceNoLock()
        {
            OpenACAIModding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static void AddEventNoLock(string area, string phase, double? durationMs, string detail)
        {
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
            _eventSequence++;
            RecentEvents.Enqueue(new OpenACAITimingEventSnapshot(
                _eventSequence,
                _lastUpdatedUtc.ToString("O"),
                area ?? "unknown",
                phase ?? "unknown",
                ElapsedSinceStartMsNoLock(),
                ElapsedSinceProcessStartMsNoLock(_lastUpdatedUtc),
                durationMs,
                detail));

            while (RecentEvents.Count > MaxRecentEvents)
            {
                RecentEvents.Dequeue();
            }
        }

        private static double ElapsedSinceStartMsNoLock()
        {
            return ((Stopwatch.GetTimestamp() - _startedTimestamp) * 1000d) / Stopwatch.Frequency;
        }

        private static double? ElapsedSinceProcessStartMsNoLock(DateTimeOffset nowUtc)
        {
            return _processStartedUtc.HasValue
                ? Math.Max(0d, (nowUtc - _processStartedUtc.Value).TotalMilliseconds)
                : (double?)null;
        }

        private static double? TimingsStartedAfterProcessStartMsNoLock()
        {
            return _processStartedUtc.HasValue
                ? Math.Max(0d, (_startedUtc - _processStartedUtc.Value).TotalMilliseconds)
                : (double?)null;
        }

        private static void CaptureProcessSnapshotNoLock()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    _processId = process.Id;
                    _processName = process.ProcessName;

                    try
                    {
                        _processStartedUtc = new DateTimeOffset(process.StartTime).ToUniversalTime();
                    }
                    catch
                    {
                        _processStartedUtc = null;
                    }
                }
            }
            catch
            {
                _processId = null;
                _processName = null;
                _processStartedUtc = null;
            }
        }

        private static string CreateKey(string area, string phase)
        {
            return (string.IsNullOrWhiteSpace(area) ? "unknown" : area.Trim())
                + "."
                + (string.IsNullOrWhiteSpace(phase) ? "unknown" : phase.Trim());
        }
    }

    internal sealed class OpenACAITimingScope : IDisposable
    {
        private readonly string _area;
        private readonly string _phase;
        private readonly string _detail;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        internal OpenACAITimingScope(string area, string phase, string detail)
        {
            _area = area;
            _phase = phase;
            _detail = detail;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            OpenACAITimings.RecordDuration(_area, _phase, _stopwatch.Elapsed, _detail);
        }
    }

    public sealed class OpenACAITimingStateSnapshot
    {
        internal OpenACAITimingStateSnapshot(
            int schema,
            string serviceKey,
            string startedUtc,
            string lastUpdatedUtc,
            string runtime,
            int? processId,
            string processName,
            string processStartedUtc,
            double? timingsStartedAfterProcessStartMs,
            double elapsedSinceStartMs,
            double? elapsedSinceProcessStartMs,
            int metricCount,
            int eventCount,
            int recentEventCount,
            IReadOnlyDictionary<string, OpenACAITimingMetricSnapshot> metrics,
            IReadOnlyCollection<OpenACAITimingEventSnapshot> recentEvents)
        {
            Schema = schema;
            ServiceKey = serviceKey;
            StartedUtc = startedUtc;
            LastUpdatedUtc = lastUpdatedUtc;
            Runtime = runtime;
            ProcessId = processId;
            ProcessName = processName;
            ProcessStartedUtc = processStartedUtc;
            TimingsStartedAfterProcessStartMs = timingsStartedAfterProcessStartMs;
            ElapsedSinceStartMs = elapsedSinceStartMs;
            ElapsedSinceProcessStartMs = elapsedSinceProcessStartMs;
            MetricCount = metricCount;
            EventCount = eventCount;
            RecentEventCount = recentEventCount;
            Metrics = metrics ?? new Dictionary<string, OpenACAITimingMetricSnapshot>(StringComparer.OrdinalIgnoreCase);
            RecentEvents = recentEvents ?? Array.Empty<OpenACAITimingEventSnapshot>();
        }

        public int Schema { get; }
        public string ServiceKey { get; }
        public string StartedUtc { get; }
        public string LastUpdatedUtc { get; }
        public string Runtime { get; }
        public int? ProcessId { get; }
        public string ProcessName { get; }
        public string ProcessStartedUtc { get; }
        public double? TimingsStartedAfterProcessStartMs { get; }
        public double ElapsedSinceStartMs { get; }
        public double? ElapsedSinceProcessStartMs { get; }
        public int MetricCount { get; }
        public int EventCount { get; }
        public int RecentEventCount { get; }
        public IReadOnlyDictionary<string, OpenACAITimingMetricSnapshot> Metrics { get; }
        public IReadOnlyCollection<OpenACAITimingEventSnapshot> RecentEvents { get; }
    }

    public sealed class OpenACAITimingMetricSnapshot
    {
        internal OpenACAITimingMetricSnapshot(
            string area,
            string phase,
            int count,
            double lastDurationMs,
            double totalDurationMs,
            double averageDurationMs,
            double minDurationMs,
            double maxDurationMs,
            string firstUtc,
            string lastUtc,
            string lastDetail)
        {
            Area = area;
            Phase = phase;
            Count = count;
            LastDurationMs = lastDurationMs;
            TotalDurationMs = totalDurationMs;
            AverageDurationMs = averageDurationMs;
            MinDurationMs = minDurationMs;
            MaxDurationMs = maxDurationMs;
            FirstUtc = firstUtc;
            LastUtc = lastUtc;
            LastDetail = lastDetail;
        }

        public string Area { get; }
        public string Phase { get; }
        public int Count { get; }
        public double LastDurationMs { get; }
        public double TotalDurationMs { get; }
        public double AverageDurationMs { get; }
        public double MinDurationMs { get; }
        public double MaxDurationMs { get; }
        public string FirstUtc { get; }
        public string LastUtc { get; }
        public string LastDetail { get; }
    }

    public sealed class OpenACAITimingEventSnapshot
    {
        internal OpenACAITimingEventSnapshot(
            int sequence,
            string utc,
            string area,
            string phase,
            double elapsedSinceStartMs,
            double? elapsedSinceProcessStartMs,
            double? durationMs,
            string detail)
        {
            Sequence = sequence;
            Utc = utc;
            Area = area;
            Phase = phase;
            ElapsedSinceStartMs = elapsedSinceStartMs;
            ElapsedSinceProcessStartMs = elapsedSinceProcessStartMs;
            DurationMs = durationMs;
            Detail = detail;
        }

        public int Sequence { get; }
        public string Utc { get; }
        public string Area { get; }
        public string Phase { get; }
        public double ElapsedSinceStartMs { get; }
        public double? ElapsedSinceProcessStartMs { get; }
        public double? DurationMs { get; }
        public string Detail { get; }
    }

    internal sealed class OpenACAITimingMetric
    {
        private readonly string _area;
        private readonly string _phase;
        private int _count;
        private double _lastDurationMs;
        private double _totalDurationMs;
        private double _minDurationMs = double.MaxValue;
        private double _maxDurationMs;
        private string _firstUtc;
        private string _lastUtc;
        private string _lastDetail;

        internal OpenACAITimingMetric(string area, string phase)
        {
            _area = string.IsNullOrWhiteSpace(area) ? "unknown" : area;
            _phase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase;
        }

        internal void Record(double durationMs, string detail)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            if (_count == 0)
            {
                _firstUtc = now;
                _minDurationMs = durationMs;
            }

            _count++;
            _lastDurationMs = durationMs;
            _totalDurationMs += durationMs;
            _minDurationMs = Math.Min(_minDurationMs, durationMs);
            _maxDurationMs = Math.Max(_maxDurationMs, durationMs);
            _lastUtc = now;
            _lastDetail = detail;
        }

        internal OpenACAITimingMetricSnapshot CreateSnapshot()
        {
            return new OpenACAITimingMetricSnapshot(
                _area,
                _phase,
                _count,
                _lastDurationMs,
                _totalDurationMs,
                _count == 0 ? 0d : _totalDurationMs / _count,
                _count == 0 ? 0d : _minDurationMs,
                _maxDurationMs,
                _firstUtc,
                _lastUtc,
                _lastDetail);
        }
    }
}

