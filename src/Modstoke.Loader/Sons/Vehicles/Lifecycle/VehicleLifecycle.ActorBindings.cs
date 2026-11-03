// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Ai.Vail;
using global::Sons.Ai.Vail.StimuliTypes;
using global::Sons.Gameplay;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed partial class CartOccupants
        {
            // VailActor.OnExitGolfCartState (0x182763920) follows _golfCartStimuli
            // back to IVailGolfCartController. The action pointer alone is insufficient.
            private sealed class CartActorBinding
            {
                private readonly VailActor _actor;
                private readonly GolfCartController _source;
                private GolfCartController _target;
                private readonly GolfCartStimuli _before;
                private GolfCartStimuli _after;
                private readonly VailActor.ActiveStimuliInfo _active;
                private readonly IStimuli _activeValue;
                private readonly MonoBehaviourStimuli _activeSource, _captured;
                private readonly List<TransformReference> _transforms = new();
                private readonly ActorStimuliList _recent;
                private readonly GolfCartStimuli.SeatInfo _oldSeat;
                private GolfCartStimuli.SeatInfo _newSeat;
                private readonly float _oldCooldown;
                private float _newCooldown;
                private readonly int _seatIndex, _seatCount, _actorId;
                private readonly CartTransformPath _seatPath, _entryPath;

                internal CartActorBinding(VailActor actor, Binding source, Binding target)
                {
                    Require(Live(actor) && !actor.transform.IsChildOf(source.Entity.transform),
                        "The retained actor must survive source cart retirement independently.");
                    _actor = actor; _source = source.Cart;
                    _actorId = actor.UniqueId;
                    _before = _source._robbyStimuli;
                    Require(_actorId > 0 && Live(_before) && actor._golfCartStimuli?.Pointer == _before.Pointer
                        && _before._golfCart?.Pointer == source.Entity.gameObject.Pointer,
                        "Kelvin's exact cart stimulus is missing or belongs to another cart.");
                    var oldSeats = _before._seats;
                    Require(oldSeats != null && oldSeats.Length > 0 && oldSeats.Length <= 4, "Kelvin stimulus seat topology differs.");
                    _seatCount = oldSeats.Length;
                    int found = -1;
                    for (int i = 0; i < oldSeats.Length; i++)
                    {
                        Require(oldSeats[i] != null, "Source AI seat schema is unavailable.");
                        if (Ptr(oldSeats[i]._actor) == actor.Pointer)
                        {
                            Require(found < 0, "Kelvin has multiple reserved seats."); found = i;
                        }
                    }
                    Require(found >= 0, "Seated Kelvin has no native stimulus seat reservation.");
                    _seatIndex = found; _oldSeat = oldSeats[found];
                    _seatPath = CartTransformPath.Capture(_oldSeat._seatTransform, source.Entity.transform);
                    _entryPath = CartTransformPath.Capture(_oldSeat._enterTransform, source.Entity.transform);
                    Require(_seatPath != null && _entryPath != null, "AI seat/entry transforms must belong to the source cart.");
                    _oldCooldown = Finite(_oldSeat._cooldownStart);
                    _active = actor._activeStimuli;
                    Require(_active != null && !_active.WasCollected, "Kelvin active stimulus state is unavailable.");
                    _activeValue = _active._stimuli; _activeSource = _active._stimuliSource; _captured = actor._capturedStimuli;
                    Add(() => _active._transform, value => _active._transform = value);
                    Add(() => actor._alignToTargetTransform, value => actor._alignToTargetTransform = value);
                    Add(() => actor._snapRelativeTarget, value => actor._snapRelativeTarget = value);
                    Add(() => actor._reservedLocationTarget, value => actor._reservedLocationTarget = value);
                    Add(() => actor._moveToTarget, value => actor._moveToTarget = value);
                    // _nearStimuliList is shared scratch reused by other actors. It is not
                    // retained actor state and must not be frozen or rewritten by this lease.
                    _recent = new ActorStimuliList(actor._recentlySeenStimuli, _before, _after);
                    if (target != null) BindTarget(target);
                    RequireCurrent(_source);

                    void Add(Func<UnityEngine.Transform> read, Action<UnityEngine.Transform> write)
                        => _transforms.Add(new TransformReference(read, write, source, target));
                }

                internal void BindTarget(Binding target)
                {
                    _target = target.Cart; _after = _target._robbyStimuli;
                    Require(Live(_after) && _after._golfCart?.Pointer == target.Entity.gameObject.Pointer
                        && _after._seats != null && _after._seats.Length == _seatCount,
                        "Replacement AI stimulus schema differs.");
                    foreach (var seat in _after._seats)
                        Require(seat != null && seat._actor is null, "Replacement has an occupied/reserved AI seat.");
                    _newSeat = _after._seats[_seatIndex];
                    Require(_seatPath.Resolve(target.Entity.transform).Pointer == _newSeat._seatTransform?.Pointer
                        && _entryPath.Resolve(target.Entity.transform).Pointer == _newSeat._enterTransform?.Pointer,
                        "Kelvin's replacement entry/seat transforms differ.");
                    _newCooldown = Finite(_newSeat._cooldownStart);
                    foreach (var reference in _transforms) reference.BindTarget(target);
                    _recent.BindTarget(_after);
                }

                internal void Move(GolfCartController cart)
                {
                    bool forward = cart.Pointer == _target?.Pointer;
                    _actor._golfCartStimuli = forward ? _after : _before;
                    _active._stimuli = forward && Ptr(_activeValue) == _before.Pointer ? _after.Cast<IStimuli>() : _activeValue;
                    _active._stimuliSource = forward && Ptr(_activeSource) == _before.Pointer ? _after : _activeSource;
                    _actor._capturedStimuli = forward && Ptr(_captured) == _before.Pointer ? _after : _captured;
                    foreach (var transform in _transforms) transform.Write(forward);
                    _recent.Move(forward);
                    _oldSeat._actor = forward ? null : _actor;
                    if (_newSeat != null)
                    {
                        _newSeat._actor = forward ? _actor : null;
                        _newSeat._cooldownStart = forward ? _oldCooldown : _newCooldown;
                    }
                }

                internal void RequireCurrent(GolfCartController cart, bool parked = false)
                {
                    bool moved = cart.Pointer == _target?.Pointer;
                    Require(Live(_actor) && _actor.UniqueId == _actorId
                        && _actor._golfCartStimuli?.Pointer == (moved ? _after : _before).Pointer
                        && Ptr(_actor._activeStimuli) == Ptr(_active)
                        && Ptr(_active._stimuli) == (moved && Ptr(_activeValue) == _before.Pointer ? _after.Pointer : Ptr(_activeValue))
                        && Ptr(_active._stimuliSource) == (moved && Ptr(_activeSource) == _before.Pointer ? _after.Pointer : Ptr(_activeSource))
                        && Ptr(_actor._capturedStimuli) == (moved && Ptr(_captured) == _before.Pointer ? _after.Pointer : Ptr(_captured))
                        && Ptr(_oldSeat._actor) == (moved ? IntPtr.Zero : _actor.Pointer)
                        && (_newSeat == null || Ptr(_newSeat._actor) == (moved ? _actor.Pointer : IntPtr.Zero))
                        && _oldSeat._cooldownStart == _oldCooldown
                        && (_newSeat == null || _newSeat._cooldownStart == (moved ? _oldCooldown : _newCooldown)),
                        "Kelvin's stimulus/seat reservation changed during cart handoff.");
                    foreach (var transform in _transforms) transform.RequireCurrent(moved, parked);
                    _recent.RequireCurrent(_actor._recentlySeenStimuli, moved);
                }
            }

            private sealed class ActorStimuliList
            {
                private readonly Il2CppSystem.Collections.Generic.List<MonoBehaviourStimuli> _list;
                private readonly MonoBehaviourStimuli[] _values;
                private readonly GolfCartStimuli _before;
                private GolfCartStimuli _after;
                internal ActorStimuliList(Il2CppSystem.Collections.Generic.List<MonoBehaviourStimuli> list,
                    GolfCartStimuli before, GolfCartStimuli after)
                {
                    _list = list; _before = before; _after = after;
                    Require(list != null && !list.WasCollected && list.Count <= 1024,
                        "Kelvin's local stimulus list is unavailable or unbounded.");
                    _values = new MonoBehaviourStimuli[list.Count];
                    for (int i = 0; i < _values.Length; i++) _values[i] = list[i];
                }
                internal void BindTarget(GolfCartStimuli target) => _after = target;
                internal void Move(bool forward)
                {
                    Require(!_list.WasCollected && _list.Count == _values.Length,
                        "Kelvin stimulus list changed before reference repair.");
                    // Only the actor-owned recent list participates. Each retained entry
                    // must still name its captured stimulus or the exact repaired cart.
                    for (int i = 0; i < _values.Length; i++)
                        Require(Ptr(_list[i]) == Ptr(_values[i])
                            || (Ptr(_values[i]) == _before.Pointer && Ptr(_list[i]) == Ptr(_after)),
                            "An actor-owned stimulus entry changed before exact reference repair.");
                    for (int i = 0; i < _values.Length; i++)
                        if (Ptr(_values[i]) == _before.Pointer) _list[i] = forward ? _after : _before;
                }
                internal void RequireCurrent(Il2CppSystem.Collections.Generic.List<MonoBehaviourStimuli> current, bool moved)
                {
                    Require(Ptr(current) == Ptr(_list) && !_list.WasCollected && _list.Count == _values.Length,
                        "Kelvin stimulus list changed.");
                    for (int i = 0; i < _values.Length; i++)
                        Require(Ptr(_list[i]) == (moved && Ptr(_values[i]) == _before.Pointer ? _after.Pointer : Ptr(_values[i])),
                            "Kelvin stimulus references changed.");
                }
            }
        }
    }
}
