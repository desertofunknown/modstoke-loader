// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Multiplayer.Client;
using global::Sons.Weapon;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        private const int MaximumMuzzleOverlaps = 32;
        private static Il2CppReferenceArray<Collider> _muzzleOverlaps;

        /// <summary>
        /// Checks body-center to muzzle and muzzle to origin clearance for a remote player.
        /// True means completed (Success); clear is false for an obstruction. Radius, distance,
        /// layers and triggers are caller policy. The live body must qualify as a capsule;
        /// this does not establish authoritative movement, trustworthy aim or asset geometry.
        /// </summary>
        public static bool TryCheckPlayerMuzzleClearance(PlayerHandle binding,
            Vector3 muzzle, Vector3 origin, float radius, float maximumDistance, int layerMask,
            CollisionTriggers triggers, out bool clear,
            out CollisionReadStatus status)
        {
            clear = false;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (binding == null || !Finite(muzzle) || !Finite(origin)
                    || !float.IsFinite(radius) || !float.IsFinite(maximumDistance)
                    || radius <= 0 || maximumDistance <= 0 || radius > maximumDistance || layerMask == 0
                    || (triggers != CollisionTriggers.Ignore && triggers != CollisionTriggers.Collide))
                {
                    status = CollisionReadStatus.InvalidQuery;
                    return false;
                }
                if (binding.IsLocalPlayer)
                {
                    status = CollisionReadStatus.UnsupportedProfile;
                    return false;
                }
                // This reservation covers the body lookup and the first native overlap.
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!TryReadPlayerBinding(binding, out var player, out var bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!_collisionSynced)
                {
                    if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                    _collisionSynced = true;
                }
                if (!Physics.defaultPhysicsScene.IsValid()) return CollisionUnavailable(out status);
                if (!TryMuzzleBody(binding, out var setup, out var body, out var center, out status)) return false;
                if (!MuzzleWithinDistance(center, player.Position, maximumDistance)
                    || !MuzzleWithinDistance(center, muzzle, maximumDistance)
                    || !MuzzleWithinDistance(center, origin, maximumDistance))
                {
                    status = CollisionReadStatus.InvalidQuery;
                    return false;
                }
                _muzzleOverlaps ??= new Il2CppReferenceArray<Collider>(MaximumMuzzleOverlaps);
                var nativeMuzzle = new UnityEngine.Vector3(muzzle.X, muzzle.Y, muzzle.Z);
                var nativeOrigin = new UnityEngine.Vector3(origin.X, origin.Y, origin.Z);
                if (!ReadMuzzleOverlap(center, nativeMuzzle, radius, layerMask, triggers,
                    binding, body, out bool firstClear, out status)) return false;
                bool secondClear = true;
                if (!muzzle.Equals(origin))
                {
                    if (!CollisionContextCurrent(world, frame))
                    {
                        status = CollisionReadStatus.StaleHit;
                        return false;
                    }
                    if (!ReserveCollisionRead(world, frame, out status)) return false;
                    if (!ReadMuzzleOverlap(nativeMuzzle, nativeOrigin, radius, layerMask, triggers,
                        binding, body, out secondClear, out status)) return false;
                }
                if (!TryMuzzleBody(binding, out var currentSetup, out var currentBody, out var currentCenter, out status))
                    return false;
                if (!SameBallisticObject(setup, currentSetup) || !SameBallisticObject(body, currentBody)
                    || !center.Equals(currentCenter)) return CollisionChanged(out status);
                if (!TryReadPlayerBinding(binding, out var currentPlayer, out bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!player.Position.Equals(currentPlayer.Position) || !player.Rotation.Equals(currentPlayer.Rotation))
                    return CollisionChanged(out status);
                if (!CollisionContextCurrent(world, frame))
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                clear = firstClear && secondClear;
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

        private static bool TryMuzzleBody(PlayerHandle binding,
            out CoopPlayerRemoteSetup setup, out CapsuleCollider body, out UnityEngine.Vector3 center,
            out CollisionReadStatus status)
        {
            setup = null;
            body = null;
            center = default;
            status = CollisionReadStatus.BindingChanged;
            if (!MatchesPlayerAttachment(binding, binding.Player)) return false;
            status = CollisionReadStatus.UnsupportedProfile;
            var setups = binding.Player.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
            if (setups == null || setups.WasCollected) return false;
            if (setups.Length > MaximumMuzzleOverlaps)
            {
                status = CollisionReadStatus.LimitReached;
                return false;
            }
            for (int i = 0; i < setups.Length; i++)
            {
                var candidate = setups[i];
                if (!LiveBallisticObject(candidate)) return false;
                // Read only the existing entity cache; the getter may lazily bind it.
                if (!MatchesPlayerAttachment(binding, candidate._entity)) continue;
                if (setup != null) return false;
                setup = candidate;
            }
            if (setup == null) return false;
            body = setup._bodyDamageCollider?.TryCast<CapsuleCollider>();
            if (!LiveCollider(body) || !MatchesPlayerAttachment(binding, CollisionAncestor(body))) return false;
            var bounds = body.bounds;
            var extents = bounds.extents;
            center = bounds.center;
            return Finite(center) && Finite(extents) && extents.x > 0 && extents.y > 0 && extents.z > 0
                && float.IsFinite(body.radius) && body.radius > 0 && float.IsFinite(body.height) && body.height > 0;
        }

        private static bool MuzzleWithinDistance(UnityEngine.Vector3 center, Vector3 point, float maximum)
        {
            if (!Finite(point)) return false;
            double x = (double)center.x - point.X, y = (double)center.y - point.Y, z = (double)center.z - point.Z;
            return x * x + y * y + z * z <= (double)maximum * maximum;
        }

        private static bool ReadMuzzleOverlap(UnityEngine.Vector3 start, UnityEngine.Vector3 end,
            float radius, int layerMask, CollisionTriggers triggers,
            PlayerHandle binding, CapsuleCollider body, out bool clear,
            out CollisionReadStatus status)
        {
            clear = false;
            status = CollisionReadStatus.Failed;
            var triggerMode = triggers == CollisionTriggers.Collide
                ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
            int count = start.Equals(end)
                ? Physics.OverlapSphereNonAlloc(start, radius, _muzzleOverlaps, layerMask, triggerMode)
                : Physics.OverlapCapsuleNonAlloc(start, end, radius, _muzzleOverlaps, layerMask, triggerMode);
            if (count < 0) return false;
            if (count >= MaximumMuzzleOverlaps)
            {
                status = CollisionReadStatus.BufferFull;
                return false;
            }
            bool unobstructed = true;
            for (int i = 0; i < count; i++)
            {
                var collider = _muzzleOverlaps[i];
                if (!MuzzleColliderCurrent(collider, layerMask, triggers)) return CollisionChanged(out status);
                if (!MatchesPlayerAttachment(binding, CollisionAncestor(collider)))
                { unobstructed = false; continue; }
                if (SameBallisticObject(collider, body)) continue;
                var rigidbody = collider.attachedRigidbody;
                var surface = LiveBallisticObject(rigidbody) ? rigidbody.transform : collider.transform;
                if (!LiveBallisticObject(surface)) return CollisionChanged(out status);
                var receivers = surface.GetComponents<IImpactReceiver>();
                var receiver = receivers != null && !receivers.WasCollected && receivers.Length == 1
                    ? receivers[0] : null;
                if (receiver == null || receiver.WasCollected
                    || !TryReadPlayerImpactRoute(receiver, binding, out var route, out _)
                    || !LiveBallisticObject(route.DirectNode)
                    || !SameBallisticObject(route.DirectNode._collider, collider))
                { unobstructed = false; continue; }
                var currentReceivers = surface.GetComponents<IImpactReceiver>();
                var currentRigidbody = collider.attachedRigidbody;
                var currentSurface = LiveBallisticObject(currentRigidbody) ? currentRigidbody.transform : collider.transform;
                var currentReceiver = SameBallisticObject(surface, currentSurface)
                    && currentReceivers != null && !currentReceivers.WasCollected && currentReceivers.Length == 1
                    ? currentReceivers[0] : null;
                if (currentReceiver == null || currentReceiver.WasCollected
                    || !TryReadPlayerImpactRoute(currentReceiver, binding, out var currentRoute, out _)
                    || !route.Matches(currentRoute) || !LiveBallisticObject(currentRoute.DirectNode)
                    || !SameBallisticObject(currentRoute.DirectNode._collider, collider)
                    || !MuzzleColliderCurrent(collider, layerMask, triggers)
                    || !MatchesPlayerAttachment(binding, CollisionAncestor(collider)))
                {
                    status = CollisionReadStatus.ReceiverChanged;
                    return false;
                }
            }
            clear = unobstructed;
            status = CollisionReadStatus.Success;
            return true;
        }

        private static bool MuzzleColliderCurrent(Collider collider, int layerMask, CollisionTriggers triggers)
        {
            if (!LiveCollider(collider)) return false;
            int layer = collider.gameObject.layer;
            return layer >= 0 && layer < 32 && (layerMask & (1 << layer)) != 0
                && (triggers == CollisionTriggers.Collide || !collider.isTrigger);
        }
    }
}
