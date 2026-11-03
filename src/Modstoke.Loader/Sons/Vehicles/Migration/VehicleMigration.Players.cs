// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Exact peer-local player objects retained across a physical-cart replacement.</summary>
    public sealed record CartMigrationPlayer(int Slot, ulong SeatNetworkId, ulong PlayerNetworkId,
        BoltEntity Entity, BoltEntity DriverGhost, PlayerGolfCartAction LocalAction, Transform Rig,
        Transform Projection, Animator Animator);

    public sealed class CartPlayerMigrationContext
    {
        internal CartPlayerMigrationContext(BoltMigrationPlan plan,
            BoltMigrationRole role, BoltEntity original, IReadOnlyList<CartMigrationPlayer> players)
        { Plan = plan; Role = role; OriginalCart = original; Players = players; }

        public BoltMigrationPlan Plan { get; }
        public BoltMigrationRole Role { get; }
        public BoltEntity OriginalCart { get; }
        public IReadOnlyList<CartMigrationPlayer> Players { get; }
    }

    /// <summary>
    /// Supplies the player/network domains not owned by the cart's local action and ghost holds.
    /// Calls are synchronous on the selected game thread inside the vehicle operation gate;
    /// implementations must not reenter the public vehicle APIs. CartPlayerMigrationRuntime
    /// supplies the concrete existing-player runtime; root ingress remains coordinator-owned.
    /// </summary>
    public interface ICartPlayerMigrationLifecycle
    {
        // Read-only: require qualified remote rig/replication writers, lossless committed-event
        // retention, original input/camera preservation and an exact peer/driver authority route.
        BoltMigrationAdmission ReadAdmission(BoltEntity original,
            IReadOnlyList<CartMigrationPlayer> players, out string detail);

        // Publish the context-bound lease before any mutation, including when returning false.
        // The cart already holds local seat actions/animation and ghost writers. Do not acquire
        // them a second time. CartOccupantWriterHold also owns remote animation/rig writers;
        // retain existing managed samplers and verify the upstream network/continuation holds.
        bool TryAcquire(CartPlayerMigrationContext context,
            out ICartPlayerMigrationHold hold, out string detail);
    }

    public interface ICartPlayerMigrationHold
    {
        CartPlayerMigrationContext Context { get; }
        // OriginalCart can be destroyed after retirement. Use captured identities, not a lookup
        // for a new player. No player or ghost is itself migrated by this physical-cart lease.
        bool TryValidateHeld(out string detail);
        bool TryRebind(BoltEntity replacement, out string detail);
        // Read-only release checks. Resume also requires this plan's aggregate peer repair and
        // a prepared server motor/client input + ghost policy; a capability flag is not proof.
        bool TryPrepareResume(out string detail);
        bool TryPrepareCancel(out string detail);
        // Called after stock cart/action writers resume, before returning to the game loop.
        // Resume must activate the prepared authority policy without replaying native entry.
        bool TryResume(out string detail);
        bool TryCancel(out string detail);
        // Must also cover a partially completed acquire/rebind/release, including after resume.
        // Keep all retained player writers and driver authority quarantined; never drop ingress.
        void Quarantine();
    }

    public static partial class VehiclePreservation
    {
        private sealed class CartPlayerMigration
        {
            private readonly ICartPlayerMigrationLifecycle _lifecycle;
            private readonly IReadOnlyList<CartMigrationPlayer> _players;
            private readonly PlayerIdentity[] _identities;
            private CartPlayerMigrationContext _context;
            private ICartPlayerMigrationHold _hold;
            private Binding _replacement;
            private bool _attempted, _acquired, _resumePrepared, _cancelPrepared, _finished;

            internal static CartPlayerMigration Capture(CartOccupants occupants,
                ICartPlayerMigrationLifecycle lifecycle)
            {
                var players = new List<CartMigrationPlayer>(4);
                var ids = new HashSet<ulong>();
                var cart = occupants.Source.Cart;
                foreach (var seat in occupants.RetainedOccupants())
                {
                    if (seat.PlayerNetworkId == 0)
                    {
                        Require(seat.LocalPlayerAction == null && seat.DriverGhost == null,
                            "A player action or ghost has no exact player identity.");
                        continue;
                    }
                    Require(ids.Add(seat.PlayerNetworkId) && seat.LocalActor == null,
                        "Player seats must have distinct retained identities and no actor action.");
                    var entity = RequireCartEndpoint(seat.PlayerNetworkId);
                    Require(entity.TryFindState<IPlayerState>(out _), "Retained cart occupant is not a player.");
                    bool local = seat.LocalPlayerAction != null;
                    var rig = local ? LocalPlayer.Transform : entity.transform;
                    var projection = cart._externalEntityTransforms[seat.Slot];
                    var animator = local ? LocalPlayer.Animator : cart._externalPlayerAnimators[seat.Slot];
                    Require(Live(rig) && Live(animator) && Live(animator.runtimeAnimatorController)
                        && !rig.IsChildOf(occupants.Source.Entity.transform)
                        && (animator.transform.Pointer == rig.Pointer || animator.transform.IsChildOf(rig)),
                        "The exact retained player rig/animator is missing or belongs to the retiring cart.");
                    Require(local ? SameEntity(LocalPlayer.Entity, entity)
                        && seat.LocalPlayerAction.transform.IsChildOf(rig)
                        : Live(projection), "Local action or remote projection does not identify the exact occupant.");
                    Require(projection == null || (Live(projection)
                        && (projection.Pointer == rig.Pointer || projection.IsChildOf(rig))),
                        "A seat projection points outside its exact retained player rig.");
                    Require((seat.Slot == 0) == (seat.DriverGhost != null)
                        && (seat.Slot == 0 || seat.SeatNetworkId == seat.PlayerNetworkId),
                        "A driver seat must name its ghost; a passenger seat must name its player.");
                    players.Add(new(seat.Slot, seat.SeatNetworkId, seat.PlayerNetworkId, entity,
                        seat.DriverGhost, seat.LocalPlayerAction, rig, projection, animator));
                }
                if (players.Count == 0) return null;
                Require(lifecycle != null,
                    "Player-occupied cart migration requires a bound remote-rig, committed-ingress and driver-authority lifecycle.");
                var result = new CartPlayerMigration(lifecycle, players.AsReadOnly());
                Require(lifecycle.ReadAdmission(occupants.Source.Entity, result._players, out var detail)
                    == BoltMigrationAdmission.Ready, detail);
                result.RequireIdentities(false);
                foreach (var identity in result._identities) identity.RequireReleased();
                return result;
            }

            private CartPlayerMigration(ICartPlayerMigrationLifecycle lifecycle,
                IReadOnlyList<CartMigrationPlayer> players)
            {
                _lifecycle = lifecycle; _players = players;
                _identities = new PlayerIdentity[players.Count];
                for (int i = 0; i < players.Count; i++) _identities[i] = new(players[i]);
            }

            internal void Acquire(BoltMigrationPlan plan, BoltMigrationRole role, Binding source)
            {
                Require(!_attempted && plan.IsValid && source.Id == plan.OldNetworkId,
                    "Player cart preservation is single-use and bound to the exact plan.");
                RequireIdentities(false);
                _context = new(plan, role, source.Entity, _players); _attempted = true;
                bool acquired = _lifecycle.TryAcquire(_context, out _hold, out var detail);
                RequireOwnHold();
                Require(acquired, detail);
                _acquired = true;
                RequireHeld();
            }

            internal void RequireHeld()
            {
                Require(_acquired && !_finished, "Player cart lifecycle has no active acquired hold.");
                RequireOwnHold();
                Require(_hold.TryValidateHeld(out var detail), detail);
                RequireIdentities(true);
            }

            internal void Rebind(Binding replacement)
            {
                RequireHeld();
                Require(_replacement == null && replacement.Matches()
                    && replacement.Id != _context.Plan.OldNetworkId && replacement.Prefab == _context.Plan.PrefabId
                    && replacement.Entity.isOwner == (_context.Role == BoltMigrationRole.Destination),
                    "Player cart rebind requires the exact held replacement role.");
                // Retain the target before the callback: a false result can follow native writes.
                _replacement = replacement;
                Require(_hold.TryRebind(replacement.Entity, out var detail), detail);
                RequireHeld();
            }

            internal void PrepareResume(Binding replacement)
            {
                RequireHeld();
                Require(_replacement != null && SameEntity(_replacement.Entity, replacement.Entity),
                    "Player cart resume does not name its rebound replacement.");
                Require(_hold.TryPrepareResume(out var detail), detail);
                RequireHeld(); _resumePrepared = true;
            }

            internal void Resume()
            {
                Require(_resumePrepared && !_finished, "Player cart release was not prepared.");
                RequireOwnHold(); RequireIdentities(false);
                Require(_hold.TryResume(out var detail), detail);
                RequireIdentities(false);
                foreach (var identity in _identities) identity.RequireReleased();
                _finished = true;
            }

            internal void PrepareCancel()
            {
                if (!_attempted) return;
                Require(_attempted && _replacement == null && !_finished,
                    "Player cart cancellation cannot follow replacement binding or release.");
                RequireOwnHold(); RequireIdentities(false);
                Require(_hold.TryPrepareCancel(out var detail), detail);
                RequireIdentities(false); _cancelPrepared = true;
            }

            internal void Cancel()
            {
                if (!_attempted) return;
                Require(_cancelPrepared && !_finished, "Player cart cancellation was not prepared.");
                RequireOwnHold(); RequireIdentities(false);
                Require(_hold.TryCancel(out var detail), detail);
                RequireIdentities(false);
                foreach (var identity in _identities) identity.RequireReleased();
                _finished = true;
            }

            internal void Quarantine()
            {
                if (_attempted && _hold != null && ReferenceEquals(_hold.Context, _context)) _hold.Quarantine();
            }

            private void RequireOwnHold()
                => Require(_hold != null && ReferenceEquals(_hold.Context, _context),
                    "The player lifecycle did not retain this exact transaction's preservation lease.");

            private void RequireIdentities(bool held)
            {
                foreach (var identity in _identities) identity.RequireCurrent(held);
                Require(!_reentered, "Player cart lifecycle reentered vehicle preservation.");
            }

            private sealed class PlayerIdentity
            {
                private readonly CartMigrationPlayer _player;
                private readonly AttachmentIdentity _entity, _ghost;
                private readonly Transform _parent;
                private readonly Transform _camera;
                private readonly Component _character;
                private readonly GameObject _inputHolder;
                private readonly Il2CppObjectBase _actionState;
                private readonly RuntimeAnimatorController _controller;
                private readonly Vector3 _position, _scale, _projectionPosition;
                private readonly Quaternion _rotation, _projectionRotation;
                private readonly bool _animatorEnabled, _rootMotion, _inputsSet;
                private readonly float _speed;

                internal PlayerIdentity(CartMigrationPlayer player)
                {
                    _player = player; _entity = new(player.Entity, player.PlayerNetworkId);
                    if (player.DriverGhost != null) _ghost = new(player.DriverGhost, player.SeatNetworkId);
                    _parent = player.Rig.parent; _position = player.Rig.position;
                    _rotation = player.Rig.rotation; _scale = player.Rig.localScale;
                    if (player.Projection != null)
                    { _projectionPosition = player.Projection.position; _projectionRotation = player.Projection.rotation; }
                    _controller = player.Animator.runtimeAnimatorController;
                    _animatorEnabled = player.Animator.enabled; _rootMotion = player.Animator.applyRootMotion;
                    _speed = Finite(player.Animator.speed);
                    if (player.LocalAction != null)
                    {
                        _camera = LocalPlayer.MainCamTr; _character = LocalPlayer.FpCharacter;
                        _inputHolder = player.LocalAction._inputStateHolder; _inputsSet = player.LocalAction._hasSetupInputs;
                        _actionState = player.LocalAction._state;
                    }
                    RequireCurrent(false);
                }

                internal void RequireCurrent(bool held)
                {
                    _entity.RequireCurrent(); _ghost?.RequireCurrent();
                    var p = _player;
                    Require(Live(p.Rig) && SameObject(p.Rig.parent, _parent) && p.Rig.gameObject.activeInHierarchy
                        && Live(p.Animator) && SameObject(p.Animator.runtimeAnimatorController, _controller)
                        && p.Animator.enabled == _animatorEnabled && p.Animator.applyRootMotion == _rootMotion
                        && (p.Animator.transform.Pointer == p.Rig.Pointer || p.Animator.transform.IsChildOf(p.Rig)),
                        "The retained player rig or animator ownership changed.");
                    if (p.LocalAction != null)
                        Require(Live(p.LocalAction) && SameEntity(LocalPlayer.Entity, p.Entity)
                            && SameObject(LocalPlayer.Transform, p.Rig) && SameObject(LocalPlayer.Animator, p.Animator)
                            && Live(_camera) && SameObject(LocalPlayer.MainCamTr, _camera)
                            && Live(_character) && SameObject(LocalPlayer.FpCharacter, _character)
                            && SameObject(p.LocalAction._state, _actionState) && _actionState != null
                            && p.LocalAction._state.IsConnected && p.LocalAction._state.ConnectedSlotIndex == p.Slot
                            && SameObject(p.LocalAction._inputStateHolder, _inputHolder) && p.LocalAction._hasSetupInputs == _inputsSet
                            && p.LocalAction.transform.IsChildOf(p.Rig), "The original seated local rig was replaced.");
                    else Require(SameObject(p.Entity.transform, p.Rig), "The remote seated player rig was replaced.");
                    if (p.Projection != null)
                        Require(Live(p.Projection) && (p.Projection.Pointer == p.Rig.Pointer || p.Projection.IsChildOf(p.Rig)),
                            "The retained remote seat projection changed ownership.");
                    if (_ghost != null)
                    {
                        Require(p.DriverGhost.TryFindState<IGolfCartState>(out var state) && state.ConnectedSlots.Length == 4
                            && state.ConnectedSlots.GetValueNetworkId(0).PackedValue == p.PlayerNetworkId,
                            "The retained driver ghost no longer names the exact player.");
                        for (int i = 1; i < 4; i++) Require(state.ConnectedSlots.GetValueNetworkId(i).PackedValue == 0,
                            "A retained driver ghost acquired a passenger.");
                    }
                    Require(p.Rig.position == _position && p.Rig.rotation == _rotation && p.Rig.localScale == _scale
                        && (p.Projection == null || (p.Projection.position == _projectionPosition && p.Projection.rotation == _projectionRotation)),
                        "The retained seated player pose moved during preservation.");
                    if (held) Require(p.Animator.speed == 0, "The held seated player animation clock resumed.");
                }

                internal void RequireReleased()
                    => Require(_player.Animator.speed == _speed, "The original seated player animation speed was not restored.");
            }

            private sealed class AttachmentIdentity
            {
                private readonly BoltEntity _entity;
                private readonly ulong _id;
                private readonly IntPtr _backing, _state, _source, _controller;
                private readonly int _instance, _prefab;
                private readonly bool _owner;

                internal AttachmentIdentity(BoltEntity entity, ulong id)
                {
                    Require(Live(entity) && entity.isAttached && entity._entity?.Serializer != null,
                        "The retained player/ghost attachment is unavailable.");
                    _entity = entity; _id = id; _instance = entity.GetInstanceID(); _prefab = entity.prefabId.Value;
                    _backing = entity._entity.Pointer; _state = entity._entity.Serializer.Pointer;
                    _source = entity.source?.Pointer ?? IntPtr.Zero; _controller = entity.controller?.Pointer ?? IntPtr.Zero;
                    _owner = entity.isOwner;
                    RequireCurrent();
                }

                internal void RequireCurrent()
                    => Require(Live(_entity) && _entity.GetInstanceID() == _instance && _entity.isAttached
                        && _entity.networkId.PackedValue == _id && _entity.prefabId.Value == _prefab && _entity.isOwner == _owner
                        && _entity._entity?.Pointer == _backing && _entity._entity.Serializer?.Pointer == _state
                        && (_entity.source?.Pointer ?? IntPtr.Zero) == _source
                        && (_entity.controller?.Pointer ?? IntPtr.Zero) == _controller
                        && SameEntity(BoltNetwork.FindEntity(new NetworkId(_id)), _entity),
                        "The retained player/ghost attachment, state or authority changed.");
            }

            private static bool SameObject(Il2CppObjectBase a, Il2CppObjectBase b)
                => b is null ? a is null : a is not null && !a.WasCollected && !b.WasCollected && a.Pointer == b.Pointer;
        }
    }
}
