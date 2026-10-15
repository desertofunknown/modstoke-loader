// SPDX-License-Identifier: GPL-3.0-or-later
// Listener filtering follows BepInEx Logger, copyright BepInEx contributors.
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class OpenACAILogEventDispatcher : ILogEventDispatcher
    {
        private static readonly OpenACAILogEventDispatcher Instance = new();
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
            var active = _activeListeners ??= new HashSet<ILogListener>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < listeners.Count; i++)
            {
                var listener = listeners[i];
                // Formatting or a filter can log recursively. Other listeners still receive that nested event.
                if (!active.Add(listener))
                {
                    Interlocked.Increment(ref _reentrantDeliveriesSkipped);
                    continue;
                }

                try
                {
                    LogLevel filter;
                    try { filter = listener.LogLevelFilter; }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref _filterFailures);
                        RecordFailure(error);
                        continue;
                    }
                    if ((eventArgs.Level & filter) == LogLevel.None)
                        continue;
                    try { listener.LogEvent(sender, eventArgs); }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref _deliveryFailures);
                        RecordFailure(error);
                    }
                }
                finally { active.Remove(listener); }
            }
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
