// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    /// <summary>
    /// A bounded sphere sweep in the server's loaded default physics scene. Query settings
    /// are copied; hits retain the existing opaque collision handles for same-frame use.
    /// Results are unordered and may include several colliders for the same entity.
    /// This does not establish streamed-out geometry, safe placement or permission to damage.
    /// </summary>
    public sealed class SphereSweep
    {
        internal SphereSweep(WorldRecordContext world, int frame,
            Vector3 origin, Vector3 end, float radius, int layerMask, CollisionTriggers triggers,
            CollisionHit[] hits, bool mayBeTruncated)
        {
            WorldId = world.WorldId;
            WorldGeneration = world.Generation;
            Frame = frame;
            Origin = origin;
            End = end;
            Radius = radius;
            LayerMask = layerMask;
            Triggers = triggers;
            Hits = Array.AsReadOnly(hits);
            MayBeTruncated = mayBeTruncated;
        }

        public Guid WorldId { get; }
        public long WorldGeneration { get; }
        public int Frame { get; }
        /// <summary>World-space sphere center at the start of the sweep.</summary>
        public Vector3 Origin { get; }
        /// <summary>World-space sphere center at the end of the sweep.</summary>
        public Vector3 End { get; }
        public float Radius { get; }
        public int LayerMask { get; }
        public CollisionTriggers Triggers { get; }

        /// <summary>
        /// Native-order hits, not sorted by distance. Distance measures sphere-center travel,
        /// not the distance from Origin to Point. Point may lie off the centerline.
        /// Initial overlaps can report zero distance, a zero point and a normal opposite
        /// the sweep; those are not resolved contact geometry.
        /// Match handles only on the same game thread, frame and world lifetime.
        /// </summary>
        public IReadOnlyList<CollisionHit> Hits { get; }

        /// <summary>
        /// The native buffer filled. Additional hits and their total count are unknown;
        /// the returned subset is not guaranteed to contain the nearest hit.
        /// </summary>
        public bool MayBeTruncated { get; }
    }

    public static partial class ServerWorld
    {
        private const int MaximumCollisionSweepHitsPerQuery = 64;
        public const int MaximumSphereSweepHitsPerQuery = MaximumCollisionSweepHitsPerQuery;
        private static Il2CppStructArray<RaycastHit> _collisionSweepHits;

        /// <summary>
        /// Sweeps a positive finite-radius sphere between finite, distinct center positions,
        /// with explicit layers and triggers. Uses the collision API's shared guards, frame
        /// budget and once-per-frame transform synchronization, not a frozen physics scene.
        /// True means complete: Success has hits; Miss has none. False with BufferFull supplies
        /// a possibly truncated snapshot; every other failure leaves snapshot null, not a miss.
        /// Uses one collision-read unit plus one per returned hit, at most 65 units. Allocations
        /// are bounded by MaximumSphereSweepHitsPerQuery; the native result buffer is reused.
        /// A miss does not prove safe actor movement or complete collision coverage.
        /// </summary>
        public static bool TrySweepSphere(Vector3 origin, Vector3 end, float radius, int layerMask,
            CollisionTriggers triggers, out SphereSweep snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!TrySegment(origin, end, out var direction, out float distance)
                    || !float.IsFinite(radius) || radius <= 0 || layerMask == 0
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
                int count = scene.SphereCast(new UnityEngine.Vector3(origin.X, origin.Y, origin.Z), radius,
                    direction, buffer, distance, layerMask,
                    triggers == CollisionTriggers.Collide ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);
                if (!TryCaptureCollisionSweep(world, frame, origin, end, distance, buffer, count,
                    layerMask, triggers, out var hits, out status)) return false;
                bool full = count == MaximumSphereSweepHitsPerQuery;
                snapshot = new SphereSweep(world, frame, origin, end, radius, layerMask,
                    triggers, hits, full);
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

        private static bool TryCaptureCollisionSweep(WorldRecordContext world, int frame,
            Vector3 origin, Vector3 end, float distance, Il2CppStructArray<RaycastHit> buffer, int count,
            int layerMask, CollisionTriggers triggers, out CollisionHit[] hits, out CollisionReadStatus status)
        {
            hits = null;
            if (!CollisionContextCurrent(world, frame))
            {
                status = CollisionReadStatus.StaleHit;
                return false;
            }
            if (count < 0 || count > MaximumCollisionSweepHitsPerQuery)
            {
                status = CollisionReadStatus.Failed;
                return false;
            }
            var captured = count == 0 ? Array.Empty<CollisionHit>() : new CollisionHit[count];
            for (int i = 0; i < count; i++)
            {
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                if (!TryCreateCollisionHit(world, frame, origin, end, layerMask, triggers,
                    buffer[i], distance, out var hit, out status)) return false;
                if (hit.ColliderLayer < 0 || hit.ColliderLayer >= 32
                    || (layerMask & (1 << hit.ColliderLayer)) == 0
                    || (triggers == CollisionTriggers.Ignore && hit.IsTrigger))
                    return CollisionChanged(out status);
                captured[i] = hit;
            }
            for (int i = 0; i < count; i++)
                if (!SameCollision(captured[i])) return CollisionChanged(out status);
            if (!CollisionContextCurrent(world, frame))
            {
                status = CollisionReadStatus.StaleHit;
                return false;
            }
            hits = captured;
            status = CollisionReadStatus.Success;
            return true;
        }
    }
}
