// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // Installed before retirement. Unknown newborns are never selected by pose or prefab.
        // Only their callbacks touching exact retained occupants are fenced before the receipt.
        private sealed class CartReplacementGate
        {
            private static readonly List<CartReplacementGate> Pending = new();
            private readonly Binding _source;
            private readonly HashSet<ulong> _occupantIds = new();
            private GolfCartController _cart;
            private BoltEntity _entity;
            private Rigidbody _body;
            private bool _kinematic, _collisions, _frozen, _restoredPhysics, _physicsHeld;
            private CollisionDetectionMode _collisionMode;
            private ulong _authorizedId, _id;
            private IntPtr _backing, _serializer, _socket;
            private string _failure;

            internal CartReplacementGate(Binding source)
            {
                _source = source;
                for (int i = 0; i < 4; i++)
                {
                    ulong id = source.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue;
                    if (id != 0) _occupantIds.Add(id);
                }
            }

            internal void Arm()
            {
                foreach (var gate in Pending)
                    Require(gate._source.Id != _source.Id, "This exact cart already has a pending replacement.");
                Pending.Add(this);
            }

            internal void Claim(BoltEntity entity)
            {
                Require(_failure == null, _failure);
                Require(Live(entity) && entity.isAttached && entity.networkId.PackedValue != _source.Id
                    && entity.prefabId.Value == _source.Prefab && SourceRetired, "Replacement identity is not ready.");
                if (_entity != null)
                {
                    Require(SameEntity(_entity, entity) && Matches(), "A second cart or changed lifetime attempted to claim this migration.");
                    return;
                }
                Require(_authorizedId != 0 && entity.networkId.PackedValue == _authorizedId,
                    "Only the exact native recreation return value or an authoritative replacement receipt may identify the newborn cart.");
                var carts = entity.GetComponentsInChildren<GolfCartController>(true);
                var bodies = entity.GetComponentsInChildren<Rigidbody>(true);
                Require(carts.Length == 1 && bodies.Length == 1, "Replacement writer topology differs.");
                _entity = entity; _cart = carts[0]; _body = bodies[0];
                _id = entity.networkId.PackedValue; _backing = entity._entity.Pointer;
                _serializer = entity._entity.Serializer.Pointer; _socket = BoltCore._udpSocket.Pointer;
            }

            internal void HoldPhysics()
            {
                Require(_failure == null && Matches(), "Exact replacement physics lifetime changed.");
                if (_physicsHeld) return;
                _kinematic = _body.isKinematic; _collisions = _body.detectCollisions; _frozen = _entity.isFrozen;
                _collisionMode = _body.collisionDetectionMode;
                _physicsHeld = true;
                _entity.Freeze(true); _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                _body.isKinematic = true; _body.detectCollisions = false;
            }

            internal void Authorize(ulong id)
            {
                Require(id != 0 && id != _source.Id && (_authorizedId == 0 || _authorizedId == id)
                    && (_entity == null || _id == id), "Replacement authorization conflicts with the recorded identity.");
                _authorizedId = id;
                // A reliable receipt can precede Unity's deferred destruction locally.
                // Remember authority now; the coordinator repeats this after retirement.
                if (!SourceRetired) return;
                var entity = BoltNetwork.FindEntity(new NetworkId(id));
                if (Live(entity) && entity.isAttached) { Claim(entity); HoldPhysics(); }
            }

            private bool SourceRetired => !Live(_source.Entity) && !Live(_source.Saver);

            private bool Matches() => Live(_entity) && _entity.isAttached && _entity.networkId.PackedValue == _id
                && _entity._entity?.Pointer == _backing && _entity._entity.Serializer?.Pointer == _serializer
                && BoltCore._udpSocket?.Pointer == _socket && SameEntity(BoltNetwork.FindEntity(new NetworkId(_id)), _entity);

            internal BoltMigrationAdmission Read(BoltEntity entity, out string detail)
            {
                detail = _failure;
                if (_failure != null) return BoltMigrationAdmission.Rejected;
                if (_entity == null || !SameEntity(_entity, entity))
                { detail = "Waiting for the exact replacement attachment to enter its birth gate."; return BoltMigrationAdmission.Pending; }
                if (!Matches() || !_physicsHeld || !_entity.isFrozen || !_body.isKinematic || _body.detectCollisions
                    || _body.collisionDetectionMode != CollisionDetectionMode.Discrete)
                { detail = "Replacement native lifetime or birth hold changed."; return BoltMigrationAdmission.Rejected; }
                if (!CartWriterHooks.HasSettledStart(_cart))
                { detail = "Waiting for ordinary native cart Start and the end of its initialization frame."; return BoltMigrationAdmission.Pending; }
                detail = "Replacement native Start observed once; driver callbacks remain held.";
                return BoltMigrationAdmission.Ready;
            }

            internal void RestorePhysicsBeforeHold()
            {
                Require(_failure == null && !_restoredPhysics && Live(_body)
                    && Read(_entity, out _) == BoltMigrationAdmission.Ready, "Replacement initialization is not ready.");
                _body.isKinematic = _kinematic;
                _body.collisionDetectionMode = _collisionMode; _body.detectCollisions = _collisions;
                _entity.Freeze(_frozen); _restoredPhysics = true;
            }

            internal void Release() => Pending.Remove(this);

            internal static bool Allow(Component component, string method)
            {
                if (Pending.Count == 0) return true;
                var cart = component.TryCast<GolfCartController>() ?? component.GetComponentInParent<GolfCartController>();
                if (!Live(cart)) return true;
                foreach (var gate in Pending)
                {
                    if (gate._source.Cart.Pointer == cart.Pointer) continue;
                    // Until the receipt arrives, protect only the retained occupants. This
                    // suppresses a connection callback before it writes its processed-seat
                    // caches; it does not identify, freeze or otherwise mutate an unknown cart.
                    if (gate._cart?.Pointer != cart.Pointer && (method is "OnConnectedSlotChanged" or "RefreshConnectedSlots"
                        or "ConstrainToGhost" or "ConstrainPassenger"))
                    {
                        var attached = cart.entity;
                        if (Live(attached) && attached.isAttached && attached.TryFindState<IGolfCartState>(out var state)
                            && state.ConnectedSlots.Length == 4)
                            for (int i = 0; i < 4; i++)
                                if (gate._occupantIds.Contains(state.ConnectedSlots.GetValueNetworkId(i).PackedValue)) return false;
                    }
                    if (gate._cart == null)
                    {
                        if (method != "Attached" || !gate.SourceRetired) continue;
                        var entity = cart.entity;
                        if (!Live(entity) || entity.prefabId.Value != gate._source.Prefab
                            || gate._authorizedId == 0 || entity.networkId.PackedValue != gate._authorizedId) continue;
                        try { gate.Claim(entity); }
                        catch (Exception error) { gate._failure = error.Message; return false; }
                    }
                    if (gate._cart?.Pointer != cart.Pointer) continue;
                    // Keep the hierarchy active until Start. Native lifecycle registration runs
                    // normally once; no connection/action/update callback is replayed afterward.
                    return method is "Start" or "Attached" or "OnEnable" or "OnDisable" or "OnDestroy" or "RegisterThreads";
                }
                return true;
            }

            internal static void AttachedCompleted(Component component)
            {
                foreach (var gate in Pending)
                    if (gate._cart?.Pointer == component.Pointer)
                        try { gate.HoldPhysics(); }
                        catch (Exception error) { gate._failure = error.Message; }
            }
        }
    }
}
