// SPDX-License-Identifier: GPL-3.0-or-later
// Listener filtering follows BepInEx Logger, copyright BepInEx contributors.
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal sealed class LogEventDispatcher : ILogEventDispatcher
    {
        private static readonly LogEventDispatcher Instance = new();
        [ThreadStatic] private static HashSet<ILogListener> _activeListeners;
        private static int _installed;
        private long _filterFailures;
        private long _deliveryFailures;
        private long _reentrantDeliveriesSkipped;
        private string _lastFailureType = "";

        internal static void Install()
        {
            Logger.SetLogEventDispatcher(Instance);
            Volatile.Write(ref _installed, 1);
        }

        public void Dispatch(object sender, LogEventArgs eventArgs, IReadOnlyList<ILogListener> listeners)
        {
            for (var i = 0; i < listeners.Count; i++)
                DispatchListener(sender, eventArgs, listeners[i], null);
        }

        internal static void DispatchDisk(object sender, LogEventArgs eventArgs,
            DiskLogListener listener, DateTimeOffset timestamp) =>
            Instance.DispatchListener(sender, eventArgs, listener, timestamp);

        private void DispatchListener(object sender, LogEventArgs eventArgs, ILogListener listener,
            DateTimeOffset? timestamp)
        {
            // Shared capture already delivered this event; the legacy listener would write it back as raw text.
            if (listener.GetType() == typeof(ConsoleLogListener) && ConsoleLogging.DesktopActive)
                return;
            var active = _activeListeners ??= new HashSet<ILogListener>(ReferenceEqualityComparer.Instance);
            // Replay and live disk forwarding must share the same guard as public listeners.
            if (!active.Add(listener))
            {
                Interlocked.Increment(ref _reentrantDeliveriesSkipped);
                return;
            }

            try
            {
                LogLevel filter;
                try { filter = listener.LogLevelFilter; }
                catch (Exception error)
                {
                    Interlocked.Increment(ref _filterFailures);
                    RecordFailure(error);
                    return;
                }
                if ((eventArgs.Level & filter) == LogLevel.None)
                    return;
                try
                {
                    if (timestamp.HasValue && listener is DiskLogListener disk)
                        disk.LogEvent(sender, eventArgs, timestamp.Value);
                    else
                        listener.LogEvent(sender, eventArgs);
                }
                catch (Exception error)
                {
                    Interlocked.Increment(ref _deliveryFailures);
                    RecordFailure(error);
                }
            }
            finally { active.Remove(listener); }
        }

        private void RecordFailure(Exception error)
        {
            // Diagnostics must not call listeners or format the failed log payload again.
            Volatile.Write(ref _lastFailureType, error.GetType().FullName ?? error.GetType().Name);
        }

        internal static object CreateState() => new
        {
            owner = "OpenACAI",
            installed = Volatile.Read(ref _installed) != 0,
            filterFailures = Interlocked.Read(ref Instance._filterFailures),
            deliveryFailures = Interlocked.Read(ref Instance._deliveryFailures),
            reentrantDeliveriesSkipped = Interlocked.Read(ref Instance._reentrantDeliveriesSkipped),
            lastFailureType = Volatile.Read(ref Instance._lastFailureType)
        };
    }
}
