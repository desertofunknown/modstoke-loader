// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons;

/// <summary>The observed ending of a managed coroutine and its managed child iterators.</summary>
public enum CoroutineOutcome
{
    Completed,
    Interrupted,
    Faulted
}

/// <summary>A terminal receipt published after the owned managed iterators have been disposed.</summary>
public sealed class CoroutineCompletion
{
    public CoroutineOutcome Outcome { get; }

    /// <summary>The observed failure, or an aggregate of distinct failures; null unless faulted.</summary>
    public Exception Error { get; }

    internal CoroutineCompletion(bool completedNaturally, Exception error)
    {
        Outcome = error != null ? CoroutineOutcome.Faulted
            : completedNaturally ? CoroutineOutcome.Completed
            : CoroutineOutcome.Interrupted;
        Error = error;
    }
}

/// <summary>Observable managed coroutines on the loader's existing Unity coroutine host.</summary>
public static class Coroutines
{
    /// <summary>
    /// Transfers a routine to the initialized Unity owner thread's scheduler and reports its ending once.
    /// Returns false without advancing or disposing the routine when the host or owner thread is unavailable.
    /// </summary>
    /// <remarks>
    /// Call on the Unity owner thread. A true return means ownership was accepted, not that execution
    /// succeeded: the completion callback may already have run, including for a start failure.
    /// The token may be null for synchronous completion or failure. Stop a running token through
    /// RedLoader.Coroutines.Stop; stopping, host loss and shutdown report Interrupted unless cleanup fails.
    /// The callback runs on the owner thread after managed cleanup, and callback exceptions are logged.
    /// This does not observe failures inside native yielded iterators or direct external Unity stop calls,
    /// and does not bind the routine to a world session or grant authority to its game operations.
    /// </remarks>
    public static bool TryStartObserved(IEnumerator routine, Action<CoroutineCompletion> completed,
        out Coroutine token, out string problem)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return global::Modstoke.Loader.Coroutines.TryStartObserved(routine,
            (natural, error) => completed(new CoroutineCompletion(natural, error)),
            out token, out problem);
    }
}
