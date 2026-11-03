// SPDX-License-Identifier: GPL-3.0-or-later
// Source contracts adapted from BepInEx's TraceLogSource and HarmonyLogSource.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLogger = HarmonyLib.Tools.Logger;

namespace Modstoke.Preloader
{
    internal sealed class RuntimeLogSources : IDisposable
    {
        private readonly object _sync = new object();
        private TraceSource _trace;
        private HarmonySource _harmony;
        private IL2CPPLogSource _native;
        private string _traceStatus = "not-attached";
        private string _harmonyStatus = "not-attached";
        private string _nativeStatus = "not-attached";
        private string _nativeError = "";
        private bool _attached;
        private bool _attachAttempted;
        private bool _disposed;

        internal void Attach()
        {
            Exception failure = null;
            lock (_sync)
            {
                if (_attachAttempted || _disposed)
                    return;
                _attachAttempted = true;

                try
                {
                    if (TraceLogSource.IsListening)
                        _traceStatus = "compatibility";
                    else
                    {
                        _trace = new TraceSource();
                        Logger.Sources.Add(_trace);
                        Trace.Listeners.Add(_trace);
                        _traceStatus = "owned";
                    }

                    if (Logger.Sources.Any(source => source is HarmonyLogSource))
                        _harmonyStatus = "compatibility";
                    else
                    {
                        _harmony = new HarmonySource();
                        Logger.Sources.Add(_harmony);
                        _harmony.Attach();
                        _harmonyStatus = "owned";
                    }
                    _attached = true;
                }
                catch (Exception error)
                {
                    failure = error;
                }
            }
            if (failure != null)
            {
                try { Dispose(); }
                catch { /* Preserve the original attachment failure. */ }
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        internal void AttachNativeSource()
        {
            lock (_sync)
            {
                if (_disposed || _nativeStatus != "not-attached")
                    return;
                if (!_attached)
                    throw new InvalidOperationException("Managed runtime log sources must be attached first.");
                _native = IL2CPPLogSource.Instance;
                try
                {
                    _native.Attach();
                    _nativeStatus = "owned";
                }
                catch (Exception error)
                {
                    _nativeStatus = "failed";
                    _nativeError = DescribeError(error);
                }
            }
            if (_nativeError.Length != 0)
                PreloaderLog.Log.LogWarning("Could not attach IL2CPP native logging: " + _nativeError);
        }

        // Diagnostic reads must not take the source lock while a listener holds its own lock.
        internal object CreateState() => new
        {
            trace = Volatile.Read(ref _traceStatus),
            harmony = Volatile.Read(ref _harmonyStatus),
            native = Volatile.Read(ref _nativeStatus),
            nativeError = Volatile.Read(ref _nativeError),
            nativeCallback = IL2CPPLogSource.Instance.CreateState(),
            disposed = Volatile.Read(ref _disposed)
        };

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                Volatile.Write(ref _disposed, true);
            }

            // Trace owns a global dispatch lock. Detach outside our state lock to avoid inversion.
            Exception failure = null;
            Cleanup(() => _native?.Dispose());
            if (_trace != null)
            {
                Cleanup(() => Logger.Sources.Remove(_trace));
                Cleanup(_trace.Dispose);
            }
            if (_harmony != null)
            {
                Cleanup(() => Logger.Sources.Remove(_harmony));
                Cleanup(_harmony.Dispose);
            }
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();

            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) { failure ??= error; }
            }
        }

        private static string DescribeError(Exception error)
        {
            try { return error.Message ?? error.GetType().FullName ?? "Native logging attachment failed."; }
            catch { return error.GetType().FullName ?? "Native logging attachment failed."; }
        }

        private sealed class TraceSource : TraceListener, ILogSource
        {
            private bool _disposed;
            public string SourceName => "Trace";
            public event EventHandler<LogEventArgs> LogEvent;

            public override void Write(string message) => Emit(LogLevel.Info, message);
            public override void WriteLine(string message) => Emit(LogLevel.Info, message);

            public override void TraceEvent(TraceEventCache eventCache, string source,
                TraceEventType eventType, int id, string format, params object[] args)
            {
                var filter = Filter;
                if (Volatile.Read(ref _disposed)
                    || (filter != null && !filter.ShouldTrace(eventCache, source, eventType, id, format, args, null, null)))
                    return;
                EmitTraceEvent(eventType, args == null ? format : string.Format(format, args));
            }

            public override void TraceEvent(TraceEventCache eventCache, string source,
                TraceEventType eventType, int id, string message)
            {
                var filter = Filter;
                if (Volatile.Read(ref _disposed)
                    || (filter != null && !filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null)))
                    return;
                EmitTraceEvent(eventType, message);
            }

            private void EmitTraceEvent(TraceEventType eventType, string message)
            {
                var level = eventType switch
                {
                    TraceEventType.Critical => LogLevel.Fatal,
                    TraceEventType.Error => LogLevel.Error,
                    TraceEventType.Warning => LogLevel.Warning,
                    TraceEventType.Information => LogLevel.Info,
                    _ => LogLevel.Debug
                };
                Emit(level, message?.Trim() ?? "");
            }

            private void Emit(LogLevel level, string message)
            {
                if (!Volatile.Read(ref _disposed))
                    LogEvent?.Invoke(this, new LogEventArgs(message, level, this));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !Volatile.Read(ref _disposed))
                {
                    Volatile.Write(ref _disposed, true);
                    Trace.Listeners.Remove(this);
                }
                base.Dispose(disposing);
            }
        }

        private sealed class HarmonySource : ILogSource
        {
            private readonly HarmonyLogger.LogChannel _channels;
            private HarmonyLogger.LogChannel _previousChannels;
            private bool _attached;
            private bool _disposed;

            internal HarmonySource()
            {
                // Keep the original enum identity: compatibility callers bind this same config entry.
                _channels = ConfigFile.CoreConfig.Bind("Harmony.Logger", "LogChannels",
                    HarmonyLogger.LogChannel.Warn | HarmonyLogger.LogChannel.Error,
                    "Specifies which Harmony log channels to listen to.\nNOTE: IL channel dumps the whole patch methods, use only when needed!").Value;
            }

            public string SourceName => "HarmonyX";
            public event EventHandler<LogEventArgs> LogEvent;

            internal void Attach()
            {
                _previousChannels = HarmonyLogger.ChannelFilter;
                HarmonyLogger.ChannelFilter = _channels;
                HarmonyLogger.MessageReceived += OnMessage;
                _attached = true;
            }

            private void OnMessage(object sender, HarmonyLogger.LogEventArgs args)
            {
                if (Volatile.Read(ref _disposed))
                    return;
                var level = args.LogChannel switch
                {
                    HarmonyLogger.LogChannel.Info => LogLevel.Info,
                    HarmonyLogger.LogChannel.Warn => LogLevel.Warning,
                    HarmonyLogger.LogChannel.Error => LogLevel.Error,
                    HarmonyLogger.LogChannel.IL => LogLevel.Debug,
                    _ => LogLevel.None
                };
                if (level != LogLevel.None)
                    LogEvent?.Invoke(this, new LogEventArgs(args.Message, level, this));
            }

            public void Dispose()
            {
                Volatile.Write(ref _disposed, true);
                if (!_attached)
                    return;
                HarmonyLogger.MessageReceived -= OnMessage;
                // Do not overwrite a filter subsequently selected by a mod.
                if (HarmonyLogger.ChannelFilter == _channels)
                    HarmonyLogger.ChannelFilter = _previousChannels;
                _attached = false;
            }
        }
    }
}
