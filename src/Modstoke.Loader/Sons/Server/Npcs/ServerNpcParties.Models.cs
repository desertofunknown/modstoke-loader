// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons;

public enum NpcPartyHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

public sealed class NpcPartyState
{
    internal NpcPartyState(NpcPartyHookStatus status, string detail)
    { Status = status; Detail = detail; }
    public NpcPartyHookStatus Status { get; }
    public string Detail { get; }
}

/// <summary>
/// One synchronous opportunity to defer a naturally queued SearchPartyEvent before native
/// scheduling work. Deferral retains the native queue entry; it does not change its expiry.
/// </summary>
public sealed class NpcPartyRequest
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _open = true;
    private bool _deferAttempted;

    internal NpcPartyRequest(WorldRecordContext world, int day, float age)
    {
        WorldId = world.WorldId;
        WorldGeneration = world.Generation;
        ScheduledDay = day;
        QueueAgeHours = age;
        ObservedAt = DateTimeOffset.UtcNow;
    }

    public Guid WorldId { get; }
    public long WorldGeneration { get; }
    public int ScheduledDay { get; }
    public float QueueAgeHours { get; }
    public DateTimeOffset ObservedAt { get; }
    public bool IsDeferred { get; private set; }

    /// <summary>
    /// Defers only this queued scheduling call. Refuses deferral at age 1.25 game-hours;
    /// native expiry is later, but a clock jump may still expire the event before another call.
    /// No target, actor, native queue field or scheduling timestamp is changed.
    /// </summary>
    public bool TryDefer()
    {
        if (Environment.CurrentManagedThreadId != _thread || !Volatile.Read(ref _open)) return false;
        if (_deferAttempted) return IsDeferred;
        _deferAttempted = true;
        IsDeferred = ServerNpcParties.CanDefer(this);
        return IsDeferred;
    }

    internal void Close() => Volatile.Write(ref _open, false);
}

/// <summary>
/// A queued native scheduling call returned normally. Handled also includes cancellation
/// without a target; neither value proves that actors spawned or an encounter completed.
/// </summary>
public sealed class NpcPartyAttempt
{
    internal NpcPartyAttempt(NpcPartyRequest request, bool handled)
    {
        WorldId = request.WorldId;
        WorldGeneration = request.WorldGeneration;
        Handled = handled;
        ObservedAt = DateTimeOffset.UtcNow;
    }
    public Guid WorldId { get; }
    public long WorldGeneration { get; }
    public bool Handled { get; }
    public DateTimeOffset ObservedAt { get; }
}
