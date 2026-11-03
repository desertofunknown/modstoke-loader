// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using UnityEngine;

namespace Modstoke.Sons;

public enum NpcSearchHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

public sealed class NpcSearchState
{
    internal NpcSearchState(NpcSearchHookStatus status, string detail)
    { Status = status; Detail = detail; }
    public NpcSearchHookStatus Status { get; }
    public string Detail { get; }
}

public enum NpcSearchReadStatus
{
    Success, Closed, WrongThread, NotServer, Unavailable, LimitExceeded, BudgetExceeded, Failed
}

/// <summary>
/// A native lost-sight search candidate whose own stimulus and conditions passed.
/// Deferral only removes this candidate from the current selection; it requests no new action.
/// The copied destination is the actor's memory, not a player's current or verified position.
/// </summary>
public sealed class NpcSearchRequest
{
    private readonly int _thread;
    private bool _open = true;
    internal bool HasCompleteCount;

    internal NpcSearchRequest(int actorTypeId, Vector3 position, float memoryAge)
    {
        _thread = Environment.CurrentManagedThreadId;
        ActorTypeId = actorTypeId;
        TargetPosition = position;
        MemoryAgeSeconds = memoryAge;
        ObservedAt = DateTimeOffset.UtcNow;
    }

    public int ActorTypeId { get; }
    public Vector3 TargetPosition { get; }
    public float MemoryAgeSeconds { get; }
    public DateTimeOffset ObservedAt { get; }
    public bool IsDeferred { get; private set; }

    /// <summary>
    /// Counts other current stock lost-sight searches whose remembered destinations lie within
    /// the 3D radius (2–40 metres). No actor registry or native object survives the callback.
    /// More than 256 active actors, eight reads per frame, or incomplete state yields no count.
    /// </summary>
    public bool TryCountActiveSearches(float radius, out int count, out NpcSearchReadStatus status)
    {
        count = 0;
        status = CheckWindow();
        if (status != NpcSearchReadStatus.Success) return false;
        HasCompleteCount = false;
        var success = ServerNpcSearch.TryCount(this, radius, out count, out status);
        HasCompleteCount = success;
        return success;
    }

    /// <summary>Defers only after a complete count in this synchronous callback.</summary>
    public bool TryDefer()
    {
        if (CheckWindow() != NpcSearchReadStatus.Success || !HasCompleteCount
            || !ServerNpcSearch.IsCurrent(this)) return false;
        IsDeferred = true;
        return true;
    }

    private NpcSearchReadStatus CheckWindow() =>
        Environment.CurrentManagedThreadId != _thread ? NpcSearchReadStatus.WrongThread
        : Volatile.Read(ref _open) ? NpcSearchReadStatus.Success : NpcSearchReadStatus.Closed;

    internal void Close() => Volatile.Write(ref _open, false);
}
