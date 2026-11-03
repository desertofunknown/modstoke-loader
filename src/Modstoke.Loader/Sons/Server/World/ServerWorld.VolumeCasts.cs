// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        public const int MaximumCapsuleSweepHitsPerQuery = MaximumCollisionSweepHitsPerQuery;
        public const int MaximumBoxSweepHitsPerQuery = MaximumCollisionSweepHitsPerQuery;

        /// <summary>Sweeps a capsule through the server's loaded default physics scene.</summary>
        /// <remarks>
        /// Endpoints are end-sphere centers; coincident endpoints use a sphere cast. Radius and
        /// distance must be finite/positive; direction must be finite/nonzero and is normalized.
        /// Game-thread/world guards apply. Costs one shared-budget read plus one per hit, up to 65.
        /// True means Success or Miss, not clearance. BufferFull returns a possibly truncated
        /// snapshot of 64 hits; other failures leave it null.
        /// </remarks>
        public static bool TrySweepCapsule(Vector3 point0, Vector3 point1, float radius,
            Vector3 direction, float maxDistance, int layerMask, CollisionTriggers triggers,
            out CapsuleSweep snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!ValidCollisionCapsule(point0, point1, radius)
                    || !TryCollisionSweepDirection(direction, maxDistance, out direction)
                    || !TryCollisionSweepEnd(point0, direction, maxDistance, out _)
                    || !TryCollisionSweepEnd(point1, direction, maxDistance, out _)
                    || layerMask == 0
                    || (triggers != CollisionTriggers.Ignore && triggers != CollisionTriggers.Collide))
                {
                    status = CollisionReadStatus.InvalidQuery;
                    return false;
                }
                var center = new Vector3((float)(((double)point0.X + point1.X) * 0.5),
                    (float)(((double)point0.Y + point1.Y) * 0.5), (float)(((double)point0.Z + point1.Z) * 0.5));
                if (!TryCollisionSweepEnd(center, direction, maxDistance, out var end))
                {
                    status = CollisionReadStatus.InvalidQuery;
                    return false;
                }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!_collisionSynced)
                {
                    if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                    _collisionSynced = true;
                }
                var scene = Physics.defaultPhysicsScene;
                if (!scene.IsValid()) return CollisionUnavailable(out status);
                var buffer = _collisionSweepHits ??= new Il2CppStructArray<RaycastHit>(MaximumCollisionSweepHitsPerQuery);
                var nativePoint0 = new UnityEngine.Vector3(point0.X, point0.Y, point0.Z);
                var nativePoint1 = new UnityEngine.Vector3(point1.X, point1.Y, point1.Z);
                var nativeDirection = new UnityEngine.Vector3(direction.X, direction.Y, direction.Z);
                var triggerMode = triggers == CollisionTriggers.Collide
                    ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
                int count = point0.Equals(point1)
                    ? scene.SphereCast(nativePoint0, radius, nativeDirection, buffer, maxDistance, layerMask, triggerMode)
                    : Physics.CapsuleCastNonAlloc(nativePoint0, nativePoint1, radius, nativeDirection,
                        buffer, maxDistance, layerMask, triggerMode);
                if (!TryCaptureCollisionSweep(world, frame, center, end, maxDistance, buffer, count,
                    layerMask, triggers, out var hits, out status)) return false;
                bool full = count == MaximumCapsuleSweepHitsPerQuery;
                snapshot = new CapsuleSweep(world, frame, point0, point1, radius, direction, maxDistance,
                    layerMask, triggers, hits, full);
                status = full ? CollisionReadStatus.BufferFull
                    : count == 0 ? CollisionReadStatus.Miss : CollisionReadStatus.Success;
                return !full;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }

        /// <summary>Sweeps a fixed-orientation box through the server's loaded default physics scene.</summary>
        /// <remarks>
        /// Half-extents and distance must be finite/positive; orientation and direction must be
        /// finite/nonzero and are normalized. Game-thread/world guards apply. Costs one
        /// shared-budget read plus one per hit, up to 65. True means Success or Miss, not clearance.
        /// BufferFull returns a possibly truncated snapshot of 64 hits; other failures leave it null.
        /// </remarks>
        public static bool TrySweepBox(Vector3 center, Vector3 halfExtents, Quaternion orientation,
            Vector3 direction, float maxDistance, int layerMask, CollisionTriggers triggers,
            out BoxSweep snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!TryCollisionBox(center, halfExtents, orientation, out orientation)
                    || !TryCollisionSweepDirection(direction, maxDistance, out direction)
                    || !TryCollisionSweepEnd(center, direction, maxDistance, out var end)
                    || layerMask == 0
                    || (triggers != CollisionTriggers.Ignore && triggers != CollisionTriggers.Collide))
                {
                    status = CollisionReadStatus.InvalidQuery;
                    return false;
                }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!_collisionSynced)
                {
                    if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                    _collisionSynced = true;
                }
                var scene = Physics.defaultPhysicsScene;
                if (!scene.IsValid()) return CollisionUnavailable(out status);
                var buffer = _collisionSweepHits ??= new Il2CppStructArray<RaycastHit>(MaximumCollisionSweepHitsPerQuery);
                var nativeCenter = new UnityEngine.Vector3(center.X, center.Y, center.Z);
                var nativeExtents = new UnityEngine.Vector3(halfExtents.X, halfExtents.Y, halfExtents.Z);
                var nativeOrientation = new UnityEngine.Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
                var nativeDirection = new UnityEngine.Vector3(direction.X, direction.Y, direction.Z);
                var triggerMode = triggers == CollisionTriggers.Collide
                    ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
                int count = Physics.BoxCastNonAlloc(nativeCenter, nativeExtents, nativeDirection, buffer,
                    nativeOrientation, maxDistance, layerMask, triggerMode);
                if (!TryCaptureCollisionSweep(world, frame, center, end, maxDistance, buffer, count,
                    layerMask, triggers, out var hits, out status)) return false;
                bool full = count == MaximumBoxSweepHitsPerQuery;
                snapshot = new BoxSweep(world, frame, center, halfExtents, orientation, direction, maxDistance,
                    layerMask, triggers, hits, full);
                status = full ? CollisionReadStatus.BufferFull
                    : count == 0 ? CollisionReadStatus.Miss : CollisionReadStatus.Success;
                return !full;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }

        private static bool TryCollisionSweepDirection(Vector3 direction, float maxDistance,
            out Vector3 normalizedDirection)
        {
            normalizedDirection = default;
            if (!Finite(direction) || !float.IsFinite(maxDistance) || maxDistance <= 0) return false;
            double length = Math.Sqrt((double)direction.X * direction.X
                + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
            if (length == 0) return false;
            normalizedDirection = new Vector3((float)(direction.X / length),
                (float)(direction.Y / length), (float)(direction.Z / length));
            return true;
        }

        private static bool TryCollisionSweepEnd(Vector3 origin, Vector3 direction, float maxDistance,
            out Vector3 end)
        {
            end = new Vector3((float)(origin.X + (double)direction.X * maxDistance),
                (float)(origin.Y + (double)direction.Y * maxDistance),
                (float)(origin.Z + (double)direction.Z * maxDistance));
            return Finite(end);
        }
    }
}
