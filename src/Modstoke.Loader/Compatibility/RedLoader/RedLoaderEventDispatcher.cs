// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
// Event engine adapted from RedLoader/MelonLoader's MelonEvent implementation.
// Original implementation: RedLoader and MelonLoader contributors (LGPL-2.1).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RedLoader;

namespace Modstoke.Loader
{
    internal static class RedLoaderEventDispatcher
    {
        internal static IMelonEventDriver<T> Create<T>(bool oneTimeUse) where T : Delegate
        {
            return new EventDriver<T>(oneTimeUse);
        }

        private sealed class Failure
        {
            internal string Signature = "";
            internal int Repeats;
            internal long NextSummary;
        }

        private sealed class EventDriver<T> : IMelonEventDriver<T> where T : Delegate
        {
            private const int SummarySeconds = 60;
            private readonly List<MelonAction<T>> actions = new();
            private readonly ConditionalWeakTable<MelonAction<T>, Failure> failures = new();
            private MelonAction<T>[] cachedActionsArray = new MelonAction<T>[0];
            private bool hasOneShotActions;
            private volatile bool disposed;
            private readonly bool oneTimeUse;

            public bool Disposed => disposed;

            internal EventDriver(bool oneTimeUse)
            {
                this.oneTimeUse = oneTimeUse;
            }

            public bool CheckIfSubscribed(MethodInfo method, object obj = null)
            {
                lock (actions)
                {
                    foreach (var entry in actions)
                    {
                        if (entry.del.Method == method && (obj == null || entry.del.Target == obj))
                            return true;
                    }

                    return false;
                }
            }

            public void Subscribe(T action, int priority, bool unsubscribeOnFirstInvocation, List<Action> rollback, Func<bool> canInvoke = null)
            {
                lock (actions)
                {
                    if (Disposed)
                        return;

                    var acts = MelonAction<T>.Get(action, priority, unsubscribeOnFirstInvocation, canInvoke);
                    var changed = false;
                    try
                    {
                        foreach (var a in acts)
                        {
                            if (CheckIfSubscribed(a.del.Method, a.del.Target))
                                continue;

                            var index = 0;
                            while (index < actions.Count && actions[index].priority <= a.priority)
                                index++;

                            actions.Insert(index, a);
                            changed = true;
                            // Capture this entry, not its delegate: a later replacement or a
                            // pre-existing subscription must survive registration rollback.
                            rollback?.Add(() => Unsubscribe(a));
                        }
                    }
                    finally
                    {
                        if (changed)
                            UpdateEnumerator();
                    }
                }
            }

            private void Unsubscribe(MelonAction<T> action)
            {
                lock (actions)
                {
                    if (actions.Remove(action))
                        UpdateEnumerator();
                }
            }

            public void Unsubscribe(T action)
            {
                var delegates = action.GetInvocationList();
                lock (actions)
                {
                    if (actions.RemoveAll(entry =>
                        {
                            foreach (var del in delegates)
                            {
                                if (entry.del.Method == del.Method && ReferenceEquals(entry.del.Target, del.Target))
                                    return true;
                            }

                            return false;
                        }) > 0)
                        UpdateEnumerator();
                }
            }

            public void UnsubscribeAll()
            {
                lock (actions)
                {
                    actions.Clear();
                    UpdateEnumerator();
                }
            }

            public void Unsubscribe(MethodInfo method, object obj = null)
            {
                lock (actions)
                {
                    if (Disposed)
                        return;

                    if (method.IsStatic)
                        obj = null;

                    if (actions.RemoveAll(entry => entry.del.Method == method && (obj == null || entry.del.Target == obj)) > 0)
                        UpdateEnumerator();
                }
            }

            private void UpdateEnumerator()
            {
                cachedActionsArray = actions.ToArray();
                hasOneShotActions = actions.Exists(entry => entry.unsubscribeOnFirstInvocation);
            }

            public MelonEventBase<T>.MelonEventSubscriber[] GetSubscribers()
            {
                MelonAction<T>[] snapshot;
                lock (actions)
                    snapshot = cachedActionsArray;

                return Array.ConvertAll(snapshot, entry => new MelonEventBase<T>.MelonEventSubscriber
                {
                    del = entry.del,
                    unsubscribeOnFirstInvocation = entry.unsubscribeOnFirstInvocation,
                    priority = entry.priority
                });
            }

            public void Invoke(Action<T> delegateInvoker)
            {
                foreach (var subscriber in BeginInvocation())
                    InvokeSubscriber(subscriber, delegateInvoker);
            }

            public MelonAction<T>[] BeginInvocation()
            {
                lock (actions)
                {
                    if (Disposed)
                        return Array.Empty<MelonAction<T>>();

                    var snapshot = cachedActionsArray;
                    // Claim one-shot delivery before calling mod code, which may invoke
                    // this event again or replace a subscription with the same delegate.
                    if (oneTimeUse)
                    {
                        disposed = true;
                        actions.Clear();
                        UpdateEnumerator();
                    }
                    else if (hasOneShotActions && actions.RemoveAll(entry => entry.unsubscribeOnFirstInvocation) > 0)
                        UpdateEnumerator();

                    return snapshot;
                }
            }

            public void InvokeSubscriber(MelonAction<T> subscriber, Action<T> delegateInvoker)
            {
                try
                {
                    if (subscriber.canInvoke != null && !subscriber.canInvoke())
                        return;
                    using var capture = global::Modstoke.Loader.PerformanceCapture.MeasureCallback(subscriber.del, "subscriber");
                    delegateInvoker(subscriber.del);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Report(subscriber, ex);
                    }
                    catch
                    {
                        // Diagnostic listeners must not stop delivery to the other mods.
                    }
                }
            }

            // A mod that throws on every update would write the same stack trace each time: one tester's log held 15,000
            // of them from a single menu mod in half an hour. The first is logged in full, repeats of it are counted and
            // summed up at most once a minute, and the mod keeps being called as RedLoader would.
            private void Report(MelonAction<T> subscriber, Exception error)
            {
                var failure = failures.GetOrCreateValue(subscriber);
                var signature = error.GetType().FullName + ": " + error.Message;
                var now = Environment.TickCount64;
                if (signature == failure.Signature)
                {
                    failure.Repeats++;
                    if (now < failure.NextSummary)
                        return;
                    RLog.Error(Repeated(subscriber, failure));
                    failure.Repeats = 0;
                    failure.NextSummary = now + SummarySeconds * 1000;
                    return;
                }

                if (failure.Repeats > 0)
                    RLog.Error(Repeated(subscriber, failure));
                failure.Signature = signature;
                failure.Repeats = 0;
                failure.NextSummary = now + SummarySeconds * 1000;
                RLog.Error(error);
            }

            private static string Repeated(MelonAction<T> subscriber, Failure failure)
            {
                var method = subscriber.del.Method;
                return (method.DeclaringType?.FullName ?? "A mod") + "." + method.Name + " threw " + failure.Signature + " "
                    + failure.Repeats + (failure.Repeats == 1 ? " more time" : " more times") + " since it was last logged.";
            }

            public void Dispose()
            {
                lock (actions)
                {
                    disposed = true;
                    actions.Clear();
                    UpdateEnumerator();
                }
            }
        }
    }
}
