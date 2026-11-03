// SPDX-License-Identifier: GPL-3.0-or-later
// Logging contracts adapted from BepInEx's preloader and console listeners.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Logging;

namespace Modstoke.Preloader
{
    internal static class PreloaderLog
    {
        // Services and detours can still emit messages after the preloader has finished.
        internal static readonly ManualLogSource Log = Logger.CreateLogSource("Preloader");
    }

    internal sealed class PreloaderLogging : ILogListener
    {
        private const LogLevel DefaultLevels = LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;
        private readonly object _sync = new object();
        private List<(LogEventArgs Event, DateTimeOffset Timestamp)> _buffer =
            new List<(LogEventArgs Event, DateTimeOffset Timestamp)>();
        private readonly RuntimeLogSources _runtimeSources = new RuntimeLogSources();
        private readonly ConfigEntry<LogLevel> _consoleLevels;
        private readonly ConfigEntry<LogLevel> _diskLevels;
        private readonly ConfigEntry<bool> _diskEnabled;
        private readonly ConfigEntry<bool> _appendLog;
        private readonly ConfigEntry<bool> _instantFlushing;
        private readonly ConfigEntry<bool> _writeUnityLog;
        private readonly ConfigEntry<bool> _writeUnityDiagnostics;
        private readonly ConfigEntry<int> _fileLimit;
        private ConsoleListener _console;
        private DiskLogListener _disk;
        private string _mode = "not-started";
        private string _diskOpenError = "";
        private int _replayedCount;
        private bool _disposed;

        internal PreloaderLogging()
        {
            var config = ConfigFile.CoreConfig;
            _consoleLevels = config.Bind("Logging.Console", "LogLevels", DefaultLevels,
                "Only displays the specified log levels in the console output.");
            _diskEnabled = config.Bind("Logging.Disk", "Enabled", true, "Enables writing log messages to disk.");
            _diskLevels = config.Bind("Logging.Disk", "LogLevels", DefaultLevels,
                "Only displays the specified log levels in the disk log output.");
            _appendLog = config.Bind("Logging.Disk", "AppendLog", false,
                "Appends to the log file instead of overwriting, on game startup.");
            _instantFlushing = config.Bind("Logging.Disk", "InstantFlushing", false,
                "Flush each received log entry immediately. This costs performance but helps diagnose crashes.");
            _fileLimit = config.Bind("Logging.Disk", "ConcurrentFileLimit", 5,
                "Maximum number of concurrently open game log files.");
            _writeUnityLog = config.Bind("Logging.Disk", "WriteUnityLog", false,
                "Include unity log messages in log file output.");
            _writeUnityDiagnostics = config.Bind("Logging.Disk", "WriteUnityDiagnostics", true,
                "Include Unity warning, error and fatal messages even when WriteUnityLog is false. " +
                "Each identical message is written three times, the third noting that repeats are omitted; " +
                "tracking restarts after 4096 distinct messages. " +
                "Respects LogLevels and explicitly blacklisted sources.");
        }

        // Keep disk levels subscribed before the handoff, including when they differ from console levels.
        public LogLevel LogLevelFilter => _consoleLevels.Value | _diskLevels.Value;

        internal object Attach()
        {
            bool started = false;
            try
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_mode != "not-started")
                        throw new InvalidOperationException("Preloader logging has already been attached.");

                    _mode = "preloader";
                    started = true;
                    Logger.Listeners.Add(this);
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    if (ConsoleManager.ConsoleEnabled)
                    {
                        if (!ConsoleManager.ConsoleActive)
                            ConsoleManager.CreateConsole();
                        if (!ConsoleLogging.DesktopActive)
                        {
                            _console = new ConsoleListener(_consoleLevels);
                            Logger.Listeners.Add(_console);
                        }
                    }
                    return PreloaderLog.Log;
                }
            }
            catch
            {
                if (started)
                {
                    try { Dispose(); }
                    catch { /* Preserve the attachment failure after attempting every cleanup. */ }
                }
                throw;
            }
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            var timestamp = DateTimeOffset.Now;
            DiskLogListener disk;
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (_mode == "preloader")
                {
                    // Disk-only diagnostics must survive the preloader-to-runtime handoff.
                    if ((eventArgs.Level & LogLevelFilter) != LogLevel.None)
                        _buffer.Add((eventArgs, timestamp));
                    return;
                }
                disk = _mode == "owned-runtime" ? _disk : null;
            }
            if (disk != null)
                LogEventDispatcher.DispatchDisk(sender, eventArgs, disk, timestamp);
        }

        internal void StartRuntimeLogging() => StartRuntimeLogging(requireActiveBuffer: true);

        private void StartRuntimeLogging(bool requireActiveBuffer)
        {
            lock (_sync)
            {
                if (_mode == "owned-runtime" || _disposed)
                    return;
                if (_mode != "preloader")
                {
                    if (requireActiveBuffer)
                        throw new InvalidOperationException("Owned runtime logging requires an active preloader buffer.");
                    return;
                }

                if (_diskEnabled.Value)
                    _disk = DiskLogListener.TryCreate(Paths.BepInExRootPath, _diskLevels.Value,
                        _appendLog.Value, _instantFlushing.Value, _fileLimit.Value,
                        _writeUnityLog.Value, _writeUnityDiagnostics.Value, out _diskOpenError);

                var entries = _buffer;
                _buffer = null;
                _mode = "owned-runtime";
                // Seal the batch before formatting: a logged object's ToString may itself log a message.
                // Other threads wait for replay to finish before obtaining the runtime disk listener.
                if (_disk != null)
                {
                    foreach (var entry in entries)
                        LogEventDispatcher.DispatchDisk(entry.Event.Source, entry.Event, _disk, entry.Timestamp);
                    _replayedCount = entries.Count;
                }
            }

            if (!string.IsNullOrEmpty(_diskOpenError))
                PreloaderLog.Log.LogWarning("Could not open the runtime log: " + _diskOpenError);
        }

        internal void PrepareCompatibilityLogging()
        {
            lock (_sync)
            {
                if (_mode == "compatibility")
                    return;
                if (_mode != "preloader" || _disposed)
                    throw new InvalidOperationException("Compatibility logging requires an active preloader buffer.");

                // Only the fallback chainloader consumes this legacy buffer; default startup never uses it.
                lock (PreloaderConsoleListener.LogEvents)
                {
                    foreach (var entry in _buffer)
                    {
                        if ((entry.Event.Level & _consoleLevels.Value) == LogLevel.None)
                            continue;
                        PreloaderConsoleListener.LogEvents.Add(entry.Event);
                        _replayedCount++;
                    }
                }
                _mode = "compatibility";
            }
            Dispose();
        }

        internal void AttachRuntimeSources()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (_mode != "owned-runtime")
                    throw new InvalidOperationException("Runtime log sources require owned runtime logging.");
            }
            // Trace has its own dispatch lock. Never attach or detach it under the listener lock.
            _runtimeSources.Attach();
        }

        internal void AttachNativeRuntimeLogging() => _runtimeSources.AttachNativeSource();

        internal void Abort(Exception error)
        {
            try
            {
                PreloaderLog.Log.LogFatal(error);
                if (_mode == "preloader")
                    StartRuntimeLogging();
            }
            finally
            {
                ConsoleLogging.SetPhase("failed", "Preloader startup failed. See the fatal log entry for details.");
                Dispose();
            }
        }

        internal object CreateState()
        {
            lock (_sync)
                return new
                {
                    owner = "OpenACAI",
                    mode = _mode,
                    disposed = _disposed,
                    dispatcher = LogEventDispatcher.CreateState(),
                    sharedConsole = ConsoleLogging.CreateState(),
                    sources = _runtimeSources.CreateState(),
                    bufferedCount = _buffer?.Count ?? 0,
                    replayedCount = _replayedCount,
                    consoleAttached = _console != null && !_disposed,
                    consoleError = _console?.LastError ?? "",
                    diskEnabled = _diskEnabled.Value,
                    diskPath = _disk?.FilePath ?? "",
                    diskError = string.IsNullOrEmpty(_diskOpenError) ? _disk?.LastError ?? "" : _diskOpenError
                };
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _buffer = null;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            }

            // Removing a listener evaluates other listeners' filters and may execute mod code.
            // Do not hold our state lock, and do not let one failure abandon the remaining resources.
            Exception failure = null;
            Cleanup(() => Logger.Listeners.Remove(this));
            if (_console != null)
            {
                Cleanup(() => Logger.Listeners.Remove(_console));
                Cleanup(_console.Dispose);
            }
            Cleanup(() => _disk?.Dispose());
            Cleanup(_runtimeSources.Dispose);
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();

            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) { failure ??= error; }
            }
        }

        private void OnProcessExit(object sender, EventArgs args)
        {
            try
            {
                // A normal exit can occur before the chainloader takes over logging.
                StartRuntimeLogging(requireActiveBuffer: false);
            }
            finally
            {
                try { Dispose(); }
                finally { ConsoleLogging.Shutdown(); }
            }
        }

        private sealed class ConsoleListener : ILogListener
        {
            private readonly object _sync = new object();
            private readonly ConfigEntry<LogLevel> _levels;
            private bool _disposed;
            private bool _failed;
            private string _lastError = "";

            internal ConsoleListener(ConfigEntry<LogLevel> levels) => _levels = levels;
            public LogLevel LogLevelFilter => _levels.Value;
            internal string LastError { get { lock (_sync) return _lastError; } }

            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                lock (_sync)
                {
                    if (_disposed || _failed)
                        return;
                }

                string text;
                try
                {
                    text = eventArgs.ToStringLine();
                }
                catch (Exception error)
                {
                    var detail = DescribeError(error);
                    lock (_sync)
                    {
                        if (!_disposed && !_failed)
                            _lastError = detail;
                    }
                    return;
                }

                lock (_sync)
                {
                    if (_disposed || _failed)
                        return;
                    try
                    {
                        try
                        {
                            ConsoleManager.SetConsoleColor(eventArgs.Level.GetConsoleColor());
                            ConsoleManager.ConsoleStream?.Write(text);
                        }
                        finally
                        {
                            ConsoleManager.SetConsoleColor(ConsoleColor.Gray);
                        }
                    }
                    catch (Exception error)
                    {
                        _failed = true;
                        _lastError = DescribeError(error);
                    }
                }
            }

            private static string DescribeError(Exception error)
            {
                try { return error.Message; }
                catch { return error.GetType().FullName ?? "Console log error"; }
            }

            public void Dispose() { lock (_sync) _disposed = true; }
        }
    }
}
