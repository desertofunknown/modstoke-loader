// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using BepInEx.Logging;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using LoaderLogLevel = BepInEx.Logging.LogLevel;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropLogger : ILogger
    {
        private readonly ManualLogSource _source;

        internal InteropLogger(ManualLogSource source) => _source = source;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            (ConvertLevel(logLevel) & BepInEx.Logging.Logger.ListenedLogLevels) != LoaderLogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter)
        {
            try
            {
                if (!IsEnabled(logLevel))
                    return;

                var message = formatter(state, exception);
                if (exception != null)
                    message = string.IsNullOrEmpty(message) ? exception.ToString() : message + Environment.NewLine + exception;
                _source.Log(ConvertLevel(logLevel), message);
            }
            catch
            {
                // Formatting and listener failures must not escape into the interop runtime or generator.
            }
        }

        private static LoaderLogLevel ConvertLevel(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => LoaderLogLevel.Debug,
            LogLevel.Information => LoaderLogLevel.Info,
            LogLevel.Warning => LoaderLogLevel.Warning,
            LogLevel.Error => LoaderLogLevel.Error,
            LogLevel.Critical => LoaderLogLevel.Fatal,
            _ => LoaderLogLevel.None
        };

        private sealed class EmptyScope : IDisposable
        {
            internal static readonly EmptyScope Instance = new EmptyScope();
            public void Dispose() { }
        }
    }
}
