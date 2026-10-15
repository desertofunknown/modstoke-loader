// SPDX-License-Identifier: GPL-3.0-or-later
// Disk logging contract and formatting adapted from BepInEx's DiskLogListener.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class OpenACAIDiskLogListener : ILogListener
    {
        private readonly object _sync = new object();
        private readonly bool _instantFlushing;
        private FileStream _stream;
        private StreamWriter _writer;
        private Timer _flushTimer;
        private string _lastError;
        private bool _failed;
        private bool _disposed;

        private OpenACAIDiskLogListener(string filePath, LogLevel levels, bool instantFlushing)
        {
            FilePath = filePath;
            LogLevelFilter = levels;
            _instantFlushing = instantFlushing;
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

        internal static OpenACAIDiskLogListener TryCreate(string directory, LogLevel levels,
            bool append, bool instantFlushing, int fileLimit, out string error)
        {
            error = null;
            OpenACAIDiskLogListener listener = null;
            try
            {
                int limit = Math.Max(1, fileLimit);
                for (int index = 0; index < limit; index++)
                {
                    string name = index == 0 ? "LogOutput.log" : $"LogOutput.{index}.log";
                    listener = new OpenACAIDiskLogListener(Path.Combine(directory, name), levels, instantFlushing);
                    lock (listener._sync)
                    {
                        try
                        {
                            listener._stream = new FileStream(listener.FilePath,
                                append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                        }
                        catch (IOException exception) when (OperatingSystem.IsWindows()
                            && ((exception.HResult & 0xffff) == 32 || (exception.HResult & 0xffff) == 33)
                            && index < limit - 1)
                        {
                            continue;
                        }

                        listener._writer = new StreamWriter(listener._stream, new UTF8Encoding(false));
                        if (!instantFlushing)
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

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            try
            {
                if ((eventArgs.Level & LogLevelFilter) == LogLevel.None
                    || DiskLogListener.BlacklistedSources.Contains(eventArgs.Source.SourceName))
                    return;

                // Formatting can execute plugin code, so keep it outside the writer lock.
                var line = eventArgs.ToString();
                lock (_sync)
                {
                    if (_disposed || _failed)
                        return;

                    _writer.WriteLine(line);
                    if (_instantFlushing)
                        _writer.Flush();
                }
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    if (!_disposed)
                        Fail(exception);
                }
            }
        }

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
            string message = exception.ToString();
            _lastError = _lastError == null ? message : _lastError + Environment.NewLine + message;
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
