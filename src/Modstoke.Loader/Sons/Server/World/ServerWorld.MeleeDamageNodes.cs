// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Weapon;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        internal static BoltEntity GetMeleeContactEntity(UnityEngine.Collider collider)
            => CollisionAncestor(collider);

        internal static bool TryReadMeleePlayerDamageNode(MeleeImpactRequest request,
            PlayerHandle binding, out PlayerDamageNodeSnapshot snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (request == null || !ReferenceEquals(world, request.World) || frame != request.Frame
                    || !request.IsCurrent())
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!TryValidatePlayerBinding(binding, out var bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!TryReadMeleePlayerImpactRoute(request, binding, out var route, out status)) return false;
                if (!DamageNodeObservation.TryCaptureMelee(request, binding, route.DirectNode,
                    out var observation, out status)) return false;
                if (!TryReadMeleePlayerImpactRoute(request, binding, out var currentRoute, out status)) return false;
                if (!route.Matches(currentRoute) || !observation.IsCurrent(currentRoute.DirectNode))
                {
                    status = CollisionReadStatus.ReceiverChanged;
                    return false;
                }
                if (!TryValidatePlayerBinding(binding, out bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!LiveCollider(request.Collider)
                    || !MatchesPlayerAttachment(binding, CollisionAncestor(request.Collider)))
                {
                    status = CollisionReadStatus.PlayerMismatch;
                    return false;
                }
                if (!CollisionContextCurrent(world, frame) || !request.IsCurrent())
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                snapshot = new PlayerDamageNodeSnapshot(observation, route);
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }

        private static bool TryReadMeleePlayerImpactRoute(MeleeImpactRequest request,
            PlayerHandle binding, out PlayerImpactRoute route,
            out CollisionReadStatus status)
        {
            route = default;
            status = CollisionReadStatus.StaleHit;
            if (!request.IsCurrent()) return false;
            status = CollisionReadStatus.PlayerMismatch;
            var collider = request.Collider;
            if (!LiveCollider(collider) || !MatchesPlayerAttachment(binding, CollisionAncestor(collider))) return false;
            status = CollisionReadStatus.ReceiverUnavailable;
            // Native melee chooses a receiver on the contact collider's own GameObject.
            // Reject multiple receivers rather than assuming TryGetComponent's selection order.
            var receivers = collider.gameObject.GetComponents<IImpactReceiver>();
            if (receivers == null || receivers.WasCollected) return false;
            int receiverCount = receivers.Length;
            if (receiverCount == 0) return false;
            if (receiverCount != 1)
            {
                status = CollisionReadStatus.ReceiverAmbiguous;
                return false;
            }
            var receiver = receivers[0];
            if (receiver == null || receiver.WasCollected) return false;
            if (!TryReadPlayerImpactRoute(receiver, binding, out route, out status)) return false;
            status = CollisionReadStatus.ReceiverChanged;
            if (!SameBallisticObject(route.DirectNode, request.Node)) return false;
            status = CollisionReadStatus.Success;
            return true;
        }

        private static bool IsCurrentMeleePlayerDamageNodeSnapshot(PlayerDamageNodeSnapshot snapshot,
            out CollisionReadStatus status)
        {
            var retained = snapshot.Observation;
            if (!TryReadMeleePlayerDamageNode(retained.MeleeRequest, retained.Binding, out var current, out status))
                return false;
            status = CollisionReadStatus.ReceiverChanged;
            if (!snapshot.Route.Matches(current.Route) || !retained.Matches(current.Observation)) return false;
            status = CollisionReadStatus.StaleHit;
            if (!CollisionContextCurrent(retained.World, retained.Frame) || !retained.MeleeRequest.IsCurrent()) return false;
            status = CollisionReadStatus.Success;
            return true;
        }
    }
}
