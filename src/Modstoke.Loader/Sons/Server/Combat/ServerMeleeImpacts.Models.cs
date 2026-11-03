// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using global::Sons.Weapon;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons;

public enum MeleeImpactHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

public sealed class MeleeImpactHookState
{
    internal MeleeImpactHookState(MeleeImpactHookStatus status, string detail)
    { Status = status; Detail = detail; }
    public MeleeImpactHookStatus Status { get; }
    public string Detail { get; }
}

/// <summary>
/// Copied observations from a server-owned NPC weapon's direct native contact. This is not a
/// unique attack, a damage calculation, or proof that Unity originated the contact callback.
/// Decisions and native reads are valid only during its synchronous Receiving callback.
/// </summary>
public sealed class MeleeImpactRequest
{
    private readonly ServerMeleeImpacts.SourceCapture _source;
    private readonly int _thread, _colliderId, _nodeId;
    private bool _open = true;
    private bool _rejected;

    internal MeleeImpactRequest(object contact, WorldRecordContext world, int frame, Collider collider,
        DamageNode node, ServerMeleeImpacts.SourceCapture source)
    {
        Contact = contact;
        World = world;
        Frame = frame;
        GameTime = Time.time;
        Collider = collider;
        Node = node;
        _source = source;
        _thread = Environment.CurrentManagedThreadId;
        _colliderId = collider.GetInstanceID();
        _nodeId = node.GetInstanceID();
        var entity = ServerWorld.GetMeleeContactEntity(collider);
        if (entity is not null && !entity.WasCollected && entity && entity.isAttached)
        {
            TargetNetworkId = entity.networkId.PackedValue;
            TargetSession = ServerSessions.ResolveSource(entity.source);
        }
    }

    internal object Contact { get; }
    internal WorldRecordContext World { get; }
    internal Collider Collider { get; }
    internal DamageNode Node { get; }
    internal ServerMeleeImpacts.SourceCapture Source => _source;
    public int Frame { get; }
    public float GameTime { get; }
    public ulong ActorNetworkId => _source.ActorNetworkId;
    public int ActorInstanceId => _source.ActorInstanceId;
    public int ActorTypeId => _source.ActorTypeId;
    /// <summary>A copied existing mapping entry, if available; mapping population and actor authority are not established.</summary>
    public int? ActorClassId => _source.ActorClassId;
    /// <summary>The copied status-list count, or null when unavailable; this does not describe status contents.</summary>
    public int? StatusEffectCount => _source.StatusEffectCount;
    public int WeaponInstanceId => _source.WeaponInstanceId;
    /// <summary>A nearest-entity hint. TryReadPlayerDamageNode establishes the actual player route.</summary>
    public ulong? TargetNetworkId { get; }
    /// <summary>The hinted entity's source session, if known; not a verified damage target.</summary>
    public ServerSession TargetSession { get; }
    public int AnimationStateHash => _source.AnimationStateHash;
    /// <summary>A live actor-owned sequence was observed; it does not authorize this attack.</summary>
    public bool HasRunningAction => _source.HasRunningAction;
    /// <summary>The cached native ID, possibly zero. It is neither unique nor a replay token.</summary>
    public int NativeImpactId => _source.NativeImpactId;
    public int MeleeType => _source.MeleeType;
    public bool IsDemonic => _source.IsDemonic;
    public float OriginalRawDamage => _source.OriginalDamage;
    public float RemainingRawDamage => _source.RemainingDamage;
    public float AccumulatedDamage => _source.AccumulatedDamage;
    public float Weight => _source.Weight;
    public Vector3 Center => new(_source.Center.x, _source.Center.y, _source.Center.z);
    public Vector3 Velocity => new(_source.Velocity.x, _source.Velocity.y, _source.Velocity.z);
    public bool IsRejected => Volatile.Read(ref _rejected);

    /// <summary>
    /// Gets the observed component activation only during this current synchronous callback.
    /// A missing birth observation cannot be recovered from a later contact's pristine values.
    /// </summary>
    public bool TryGetActivation(out MeleeActivation activation)
        => ServerMeleeImpacts.TryGetActivation(this, out activation);

    /// <summary>
    /// Skips this direct DamageNode.OnImpact, before absorption, breaking or callback forwarding.
    /// Does not undo earlier contacts or stop the outer weapon callback's remaining work.
    /// </summary>
    public bool Reject()
    {
        if (Environment.CurrentManagedThreadId != _thread || !_open) return false;
        Volatile.Write(ref _rejected, true);
        return true;
    }

    /// <summary>
    /// Reads the direct node and exact player callback route without invoking damage. This uses
    /// the shared collision-read budget. The result expires when this callback closes.
    /// </summary>
    public bool TryReadPlayerDamageNode(PlayerHandle binding,
        out PlayerDamageNodeSnapshot snapshot, out CollisionReadStatus status)
        => ServerWorld.TryReadMeleePlayerDamageNode(this, binding, out snapshot, out status);

    internal bool IsCurrent() => IsCurrent(true);

    // The receiver scan allocates a native array; callers between handlers skip it and rely on
    // one full check before the policy decision is accepted.
    internal bool IsCurrent(bool checkReceivers)
    {
        if (Environment.CurrentManagedThreadId != _thread || !_open) return false;
        try
        {
            return ServerMeleeImpacts.Current(this, false) && Collider.GetInstanceID() == _colliderId
                && Node.GetInstanceID() == _nodeId && _source.IsCurrent()
                && float.IsFinite(GameTime) && BitConverter.SingleToInt32Bits(GameTime) == BitConverter.SingleToInt32Bits(Time.time)
                && ServerMeleeImpacts.Current(this, checkReceivers);
        }
        catch (Exception error) { ServerMeleeImpacts.Report(error); return false; }
    }

    internal void Close() => _open = false;
}
