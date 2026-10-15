// SPDX-License-Identifier: GPL-3.0-or-later
// Logging contracts adapted from BepInEx's preloader and console listeners.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class OpenACAIPreloaderLog
    {
        // Services and detours can still emit messages after the preloader has finished.
        internal static readonly ManualLogSource Log = Logger.CreateLogSource("Preloader");
    }

    internal sealed class OpenACAIPreloaderLogging : ILogListener
    {
        private const LogLevel DefaultLevels = LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;
        private readonly object _sync = new object();
        private readonly List<LogEventArgs> _buffer = new List<LogEventArgs>();
        private readonly OpenACAIRuntimeLogSources _runtimeSources = new OpenACAIRuntimeLogSources();
        private readonly ConfigEntry<LogLevel> _consoleLevels;
        private readonly ConfigEntry<LogLevel> _diskLevels;
        private readonly ConfigEntry<bool> _diskEnabled;
        private readonly ConfigEntry<bool> _appendLog;
        private readonly ConfigEntry<bool> _instantFlushing;
        private readonly ConfigEntry<bool> _writeUnityLog;
        private readonly ConfigEntry<int> _fileLimit;
        private ConsoleListener _console;
        private OpenACAIDiskLogListener _disk;
        private string _mode = "not-started";
        private string _diskOpenError = "";
        private int _replayedCount;
        private bool _disposed;

        internal OpenACAIPreloaderLogging()
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
        }

        // Keep disk levels subscribed before the handoff, including when they differ from console levels.
        public LogLevel LogLevelFilter => _consoleLevels.Value | _diskLevels.Value;

        internal object Attach()
        {
            if (_mode != "not-started")
                throw new InvalidOperationException("Preloader logging has already been attached.");

            _mode = "preloader";
            Logger.Listeners.Add(this);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            if (ConsoleManager.ConsoleEnabled)
            {
                if (!ConsoleManager.ConsoleActive)
                    ConsoleManager.CreateConsole();
                _console = new ConsoleListener(_consoleLevels);
                Logger.Listeners.Add(_console);
            }
            return OpenACAIPreloaderLog.Log;
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            OpenACAIDiskLogListener disk;
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (_mode == "preloader")
                {
                    // Preserve the compatibility buffer's console-level filter, even with no console window.
                    if ((eventArgs.Level & _consoleLevels.Value) != LogLevel.None)
                        _buffer.Add(eventArgs);
                    return;
                }
                disk = _mode == "owned-runtime" ? _disk : null;
            }
            disk?.LogEvent(sender, eventArgs);
        }

        internal void StartRuntimeLogging()
        {
            lock (_sync)
            {
                if (_mode == "owned-runtime" || _disposed)
                    return;
                if (_mode != "preloader")
                    throw new InvalidOperationException("Owned runtime logging requires an active preloader buffer.");

                if (!_writeUnityLog.Value)
                    DiskLogListener.BlacklistedSources.Add("Unity");
                if (_diskEnabled.Value)
                    _disk = OpenACAIDiskLogListener.TryCreate(Paths.BepInExRootPath, _diskLevels.Value,
                        _appendLog.Value, _instantFlushing.Value, _fileLimit.Value, out _diskOpenError);

                var entries = _buffer.ToArray();
                _buffer.Clear();
                _mode = "owned-runtime";
                // Seal the batch before formatting: a logged object's ToString may itself log a message.
                // Other threads wait for replay to finish before obtaining the runtime disk listener.
                if (_disk != null)
                {
                    foreach (var entry in entries)
                        _disk.LogEvent(entry.Source, entry);
                    _replayedCount = entries.Length;
                }
            }

            if (!string.IsNullOrEmpty(_diskOpenError))
                OpenACAIPreloaderLog.Log.LogWarning("Could not open the runtime log: " + _diskOpenError);
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
                    PreloaderConsoleListener.LogEvents.AddRange(_buffer);
                _replayedCount = _buffer.Count;
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
                OpenACAIPreloaderLog.Log.LogFatal(error);
                if (_mode == "preloader")
                    StartRuntimeLogging();
            }
            finally
            {
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
                    dispatcher = OpenACAILogEventDispatcher.CreateState(),
                    sources = _runtimeSources.CreateState(),
                    bufferedCount = _buffer.Count,
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
                Logger.Listeners.Remove(this);
                if (_console != null)
                {
                    Logger.Listeners.Remove(_console);
                    _console.Dispose();
                }
                _disk?.Dispose();
                _buffer.Clear();
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            }
            _runtimeSources.Dispose();
        }

        private void OnProcessExit(object sender, EventArgs args) => Dispose();

        private sealed class ConsoleListener : ILogListener
        {
            private readonly object _sync = new object();
            private readonly ConfigEntry<LogLevel> _levels;
            private bool _disposed;
            private string _lastError = "";

            internal ConsoleListener(ConfigEntry<LogLevel> levels) => _levels = levels;
            public LogLevel LogLevelFilter => _levels.Value;
            internal string LastError { get { lock (_sync) return _lastError; } }

            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                try
                {
                    var text = eventArgs.ToStringLine();
                    lock (_sync)
                    {
                        if (_disposed || _lastError.Length != 0)
                            return;
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
                }
                catch (Exception error)
                {
                    lock (_sync)
                        _lastError = error.Message;
                }
            }

            public void Dispose() { lock (_sync) _disposed = true; }
        }
    }
}
