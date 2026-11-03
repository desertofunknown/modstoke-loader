// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Lightweight loader timing service for startup and UI diagnostics.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Modstoke.Loader
{
    public static class Timings
    {
        public const string ServiceKey = "openacai.loader.timings";

        private const int MaxRecentEvents = 64;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, TimingMetric> Metrics = new Dictionary<string, TimingMetric>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<TimingEventSnapshot> RecentEvents = new Queue<TimingEventSnapshot>();
        private static DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
        private static DateTimeOffset _lastUpdatedUtc = _startedUtc;
        private static long _startedTimestamp = Stopwatch.GetTimestamp();
        private static DateTimeOffset? _processStartedUtc;
        private static int? _processId;
        private static string _processName;
        private static int _eventSequence;
        private static bool _initialized;
        private static IReadOnlyDictionary<string, TimingMetricSnapshot> _metricSnapshots;
        private static IReadOnlyCollection<TimingEventSnapshot> _eventSnapshots;
        private static string _startedUtcText;
        private static string _lastUpdatedUtcText;
        private static string _processStartedUtcText;
        private static readonly string RuntimeVersion = Environment.Version.ToString();

        public static IReadOnlyDictionary<string, TimingMetricSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return MetricSnapshotsNoLock();
            }
        }

        public static TimingStateSnapshot State => CreateState();

        // Static callback names only. Inactive outside an explicitly armed, main-thread capture.
        public static CaptureTimingScope MeasureCapture(string name)
            => global::Modstoke.Loader.PerformanceCapture.MeasureCallback(name);

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
                _startedUtcText = _startedUtc.ToString("O");
                _lastUpdatedUtcText = _startedUtcText;
                _startedTimestamp = Stopwatch.GetTimestamp();
                _eventSequence = 0;
                CaptureProcessSnapshotNoLock();
                _processStartedUtcText = _processStartedUtc?.ToString("O");
                _initialized = true;
                AddEventNoLock("loader", "timings-initialized", null, null);
                PublishServiceNoLock();
            }
        }

        internal static TimingScope Measure(string area, string phase, string detail = null)
        {
            EnsureInitialized();
            return new TimingScope(area, phase, detail);
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
                TimingMetric metric;
                if (!Metrics.TryGetValue(key, out metric))
                {
                    metric = new TimingMetric(area, phase);
                    Metrics[key] = metric;
                }

                AddEventNoLock(area, phase, durationMs, detail);
                metric.Record(durationMs, detail, _lastUpdatedUtcText);
                _metricSnapshots = null;
                PublishServiceNoLock();
            }
        }

        internal static TimingStateSnapshot CreateState()
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

        private static TimingStateSnapshot CreateStateNoLock()
        {
            var metrics = MetricSnapshotsNoLock();
            var events = _eventSnapshots ??= Array.AsReadOnly(RecentEvents.ToArray());
            return new TimingStateSnapshot(
                1,
                ServiceKey,
                _startedUtcText,
                _lastUpdatedUtcText,
                RuntimeVersion,
                _processId,
                _processName,
                _processStartedUtcText,
                ElapsedSinceProcessStartMsNoLock(_startedUtc),
                ElapsedSinceStartMsNoLock(),
                ElapsedSinceProcessStartMsNoLock(DateTimeOffset.UtcNow),
                metrics.Count,
                _eventSequence,
                events.Count,
                metrics,
                events);
        }

        private static IReadOnlyDictionary<string, TimingMetricSnapshot> MetricSnapshotsNoLock() =>
            _metricSnapshots ??= new ReadOnlyDictionary<string, TimingMetricSnapshot>(
                Metrics.ToDictionary(pair => pair.Key, pair => pair.Value.CreateSnapshot(), StringComparer.OrdinalIgnoreCase));

        private static void PublishServiceNoLock()
        {
            Modding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static void AddEventNoLock(string area, string phase, double? durationMs, string detail)
        {
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
            _lastUpdatedUtcText = _lastUpdatedUtc.ToString("O");
            _eventSnapshots = null;
            _eventSequence++;
            RecentEvents.Enqueue(new TimingEventSnapshot(
                _eventSequence,
                _lastUpdatedUtcText,
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

    internal sealed class TimingScope : IDisposable
    {
        private readonly string _area;
        private readonly string _phase;
        private readonly string _detail;
        private readonly long _startedTimestamp;
        private int _disposed;

        internal TimingScope(string area, string phase, string detail)
        {
            _area = area;
            _phase = phase;
            _detail = detail;
            _startedTimestamp = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Timings.RecordDuration(_area, _phase, Stopwatch.GetElapsedTime(_startedTimestamp), _detail);
        }
    }

    public sealed class TimingStateSnapshot
    {
        internal TimingStateSnapshot(
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
            IReadOnlyDictionary<string, TimingMetricSnapshot> metrics,
            IReadOnlyCollection<TimingEventSnapshot> recentEvents)
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
            Metrics = metrics ?? new Dictionary<string, TimingMetricSnapshot>(StringComparer.OrdinalIgnoreCase);
            RecentEvents = recentEvents ?? Array.Empty<TimingEventSnapshot>();
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
        public IReadOnlyDictionary<string, TimingMetricSnapshot> Metrics { get; }
        public IReadOnlyCollection<TimingEventSnapshot> RecentEvents { get; }
    }

    public sealed class TimingMetricSnapshot
    {
        internal TimingMetricSnapshot(
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

    public sealed class TimingEventSnapshot
    {
        internal TimingEventSnapshot(
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

    internal sealed class TimingMetric
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
        private TimingMetricSnapshot _snapshot;

        internal TimingMetric(string area, string phase)
        {
            _area = string.IsNullOrWhiteSpace(area) ? "unknown" : area;
            _phase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase;
        }

        internal void Record(double durationMs, string detail, string now)
        {
            _snapshot = null;
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

        internal TimingMetricSnapshot CreateSnapshot()
        {
            return _snapshot ??= new TimingMetricSnapshot(
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

