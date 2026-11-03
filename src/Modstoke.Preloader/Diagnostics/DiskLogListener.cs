// SPDX-License-Identifier: GPL-3.0-or-later
// Disk logging contract and formatting adapted from BepInEx's DiskLogListener.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal sealed class DiskLogListener : ILogListener
    {
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
        private const int UnityDiagnosticRepeats = 3;
        private const int MaxTrackedUnityDiagnostics = 4096;
        private readonly object _sync = new object();
        private readonly Dictionary<(int Hash, int Length), int> _unityDiagnosticCounts = new();
        private readonly bool _instantFlushing;
        private readonly bool _writeUnityLog;
        private readonly bool _writeUnityDiagnostics;
        private FileStream _stream;
        private StreamWriter _writer;
        private Timer _flushTimer;
        private string _lastError;
        private bool _failed;
        private bool _disposed;

        private DiskLogListener(string filePath, LogLevel levels, bool instantFlushing,
            bool writeUnityLog, bool writeUnityDiagnostics)
        {
            FilePath = filePath;
            LogLevelFilter = levels;
            _instantFlushing = instantFlushing;
            _writeUnityLog = writeUnityLog;
            _writeUnityDiagnostics = writeUnityDiagnostics;
        }

        internal string FilePath { get; }

        internal string LastError
        {
            get
            {
                lock (_sync)
                    return _lastError;
            }
        }

        public LogLevel LogLevelFilter { get; }

        internal static DiskLogListener TryCreate(string directory, LogLevel levels,
            bool append, bool instantFlushing, int fileLimit, bool writeUnityLog,
            bool writeUnityDiagnostics, out string error)
        {
            error = null;
            DiskLogListener listener = null;
            try
            {
                int limit = Math.Max(1, fileLimit);
                for (int index = 0; index < limit; index++)
                {
                    string name = index == 0 ? "LogOutput.log" : $"LogOutput.{index}.log";
                    bool retainPrevious = !append && index == 0;
                    listener = new DiskLogListener(Path.Combine(directory, name), levels,
                        instantFlushing, writeUnityLog, writeUnityDiagnostics);
                    lock (listener._sync)
                    {
                        try
                        {
                            listener._stream = new FileStream(listener.FilePath,
                                append ? FileMode.Append : retainPrevious ? FileMode.OpenOrCreate : FileMode.Create,
                                retainPrevious ? FileAccess.ReadWrite : FileAccess.Write, FileShare.Read);

                            if (retainPrevious)
                            {
                                // Keep this handle across retention and truncation: no writer or rename can race us.
                                // Only the primary slot rotates history; numbered slots belong to concurrent runs.
                                if (listener._stream.Length != 0)
                                    listener.RetainPreviousLog();
                                listener._stream.SetLength(0);
                                listener._stream.Position = 0;
                            }
                        }
                        catch (IOException exception) when (OperatingSystem.IsWindows()
                            && ((exception.HResult & 0xffff) == 32 || (exception.HResult & 0xffff) == 33)
                            && index < limit - 1)
                        {
                            // A locked primary log or history file must not prevent a concurrent log slot.
                            listener.Dispose();
                            continue;
                        }

                        listener._writer = new StreamWriter(listener._stream, new UTF8Encoding(false));
                        listener._writer.WriteLine($"[{FormatTimestamp(DateTimeOffset.Now)}] [Session] " +
                            $"Process {Environment.ProcessId} started disk logging ({(append ? "append" : "fresh")}).");
                        if (instantFlushing)
                            listener._writer.Flush();
                        else
                            listener._flushTimer = new Timer(listener.Flush, null, 2000, 2000);
                    }
                    return listener;
                }
            }
            catch (Exception exception)
            {
                if (listener == null)
                    error = exception.ToString();
                else
                {
                    lock (listener._sync)
                        listener.Fail(exception);
                    error = listener.LastError;
                }
            }
            return null;
        }

        private void RetainPreviousLog()
        {
            string previousPath = Path.ChangeExtension(FilePath, "previous.log");
            string temporaryPath = previousPath + ".tmp";
            bool temporaryCreated = false;
            try
            {
                using (var previous = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    temporaryCreated = true;
                    _stream.CopyTo(previous);
                }
                // Publish only the complete copy; on failure the current log has not been truncated.
                File.Move(temporaryPath, previousPath, true);
            }
            finally
            {
                if (temporaryCreated)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception exception) { RecordError(exception); }
                }
            }
        }

        public void LogEvent(object sender, LogEventArgs eventArgs) =>
            LogEvent(sender, eventArgs, DateTimeOffset.Now);

        internal void LogEvent(object sender, LogEventArgs eventArgs, DateTimeOffset timestamp)
        {
            lock (_sync)
            {
                if (_disposed || _failed)
                    return;
            }

            string text;
            bool unityDiagnostic;
            try
            {
                if ((eventArgs.Level & LogLevelFilter) == LogLevel.None
                    || global::BepInEx.Logging.DiskLogListener.BlacklistedSources.Contains(eventArgs.Source.SourceName))
                    return;

                unityDiagnostic = eventArgs.Source.SourceName == "Unity" && !_writeUnityLog;
                if (unityDiagnostic && (!_writeUnityDiagnostics
                        || (eventArgs.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) == LogLevel.None))
                    return;

                // Formatting can execute plugin code, so keep it outside the writer lock.
                text = $"{eventArgs}";
            }
            catch (Exception exception)
            {
                // A bad source or payload is not a failed file. Keep accepting subsequent entries.
                var detail = DescribeError(exception);
                lock (_sync)
                {
                    if (!_disposed && !_failed)
                        _lastError = detail;
                }
                return;
            }

            lock (_sync)
            {
                if (_disposed || _failed || unityDiagnostic && !AdmitUnityDiagnostic(ref text))
                    return;
                try
                {
                    Span<char> timestampBuffer = stackalloc char[29];
                    timestamp.TryFormat(timestampBuffer, out var timestampLength,
                        TimestampFormat, CultureInfo.InvariantCulture);
                    _writer.Write('[');
                    _writer.Write(timestampBuffer.Slice(0, timestampLength));
                    _writer.Write("] ");
                    _writer.WriteLine(text);
                    if (_instantFlushing)
                        _writer.Flush();
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }
        }

        // Unity repeats some diagnostics for every instance or frame. Keep the first few of each message.
        // Keys are a hash and length, not the text, and a full table starts over rather than stop capping.
        private bool AdmitUnityDiagnostic(ref string text)
        {
            var key = (StringComparer.Ordinal.GetHashCode(text), text.Length);
            _unityDiagnosticCounts.TryGetValue(key, out var count);
            if (count >= UnityDiagnosticRepeats)
                return false;
            if (count == 0 && _unityDiagnosticCounts.Count >= MaxTrackedUnityDiagnostics)
                _unityDiagnosticCounts.Clear();
            _unityDiagnosticCounts[key] = ++count;
            if (count == UnityDiagnosticRepeats)
                text += " (Repeated; further identical Unity diagnostics are not written.)";
            return true;
        }

        private static string FormatTimestamp(DateTimeOffset timestamp) =>
            timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        private void Flush(object state)
        {
            lock (_sync)
            {
                if (_disposed || _failed)
                    return;

                try
                {
                    _writer.Flush();
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }
        }

        // Called under _sync; close the stream first so cleanup cannot retry buffered writes.
        private void Fail(Exception exception)
        {
            RecordError(exception);
            _failed = true;
            Dispose();
        }

        private void RecordError(Exception exception)
        {
            string message = DescribeError(exception);
            _lastError = _lastError == null ? message : _lastError + Environment.NewLine + message;
        }

        private static string DescribeError(Exception exception)
        {
            try { return exception.ToString(); }
            catch { return exception.GetType().FullName ?? "Log error"; }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;

                // Dispose does not wait for queued callbacks; they observe _disposed under this lock.
                try { _flushTimer?.Dispose(); }
                catch (Exception exception) { RecordError(exception); }
                _flushTimer = null;

                if (_failed)
                {
                    try { _stream?.Dispose(); }
                    catch (Exception exception) { RecordError(exception); }
                }

                try { _writer?.Dispose(); }
                catch (Exception exception) { RecordError(exception); }
                _writer = null;

                if (!_failed)
                {
                    try { _stream?.Dispose(); }
                    catch (Exception exception) { RecordError(exception); }
                }
                _stream = null;
            }
        }
    }
}
