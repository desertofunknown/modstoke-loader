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
        public const int MaximumCollisionReadsPerFrame = 256;
        public const int MaximumCollisionOverlapsPerQuery = 64;
        private const int MaximumCollisionAncestors = 32;
        private static WorldRecordContext _collisionWorld;
        private static int _collisionFrame;
        private static int _collisionReads;
        private static bool _collisionSynced;
        private static bool _collisionReading;
        private static Il2CppReferenceArray<Collider> _collisionOverlapBuffer;

        /// <summary>
        /// Reads a finite segment in the server's default physics scene. True means the query
        /// completed: Success has a hit; Miss has none. Other statuses must not be treated as misses.
        /// Synchronizes transforms once before this API's first query each frame when auto-sync is off.
        /// </summary>
        public static bool TryTraceSegment(Vector3 origin, Vector3 end, int layerMask,
            CollisionTriggers triggers, out CollisionHit hit,
            out CollisionReadStatus status)
        {
            hit = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!TrySegment(origin, end, out var direction, out float distance) || layerMask == 0
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
                bool found = scene.Raycast(new UnityEngine.Vector3(origin.X, origin.Y, origin.Z), direction,
                    out var nativeHit, distance, layerMask,
                    triggers == CollisionTriggers.Collide ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);
                if (!CollisionContextCurrent(world, frame))
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                if (!found)
                {
                    status = CollisionReadStatus.Miss;
                    return true;
                }
                return TryCreateCollisionHit(world, frame, origin, end, layerMask, triggers, nativeHit, distance,
                    out hit, out status);
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }

        /// <summary>
        /// Reads an oriented box in the server's loaded default physics scene on the main thread.
        /// True means complete: Success has overlaps, Miss has none. False with BufferFull
        /// supplies a possibly truncated snapshot; every other failure leaves snapshot null.
        /// This observation does not guarantee safe placement or cover streamed-out geometry.
        /// </summary>
        /// <remarks>
        /// Half-extents are positive box-axis lengths; finite nonzero orientations are normalized.
        /// Uses explicit layers/triggers and the shared once-per-frame transform synchronization,
        /// not a frozen scene. Costs one collision-read unit plus one per collider, at most 65 units.
        /// </remarks>
        public static bool TryOverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, int layerMask,
            CollisionTriggers triggers, out BoxOverlap snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!TryCollisionBox(center, halfExtents, orientation, out orientation)
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
                var buffer = _collisionOverlapBuffer ??= new Il2CppReferenceArray<Collider>(MaximumCollisionOverlapsPerQuery);
                var nativeCenter = new UnityEngine.Vector3(center.X, center.Y, center.Z);
                var nativeExtents = new UnityEngine.Vector3(halfExtents.X, halfExtents.Y, halfExtents.Z);
                var nativeOrientation = new UnityEngine.Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
                var triggerMode = triggers == CollisionTriggers.Collide
                    ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
                ColliderOverlap[] colliders;
                int count;
                try
                {
                    count = Physics.OverlapBoxNonAlloc(nativeCenter, nativeExtents, buffer, nativeOrientation, layerMask, triggerMode);
                    if (!TryCaptureCollisionOverlaps(world, frame, buffer, count, layerMask, triggers,
                        out colliders, out status)) return false;
                }
                finally
                {
                    // The reusable native array must not retain collider references across world lifetimes.
                    for (int i = 0; i < MaximumCollisionOverlapsPerQuery; i++) buffer[i] = null;
                }
                if (!CollisionContextCurrent(world, frame))
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                bool full = count == MaximumCollisionOverlapsPerQuery;
                snapshot = new BoxOverlap(world, frame, center, halfExtents, orientation, layerMask,
                    triggers, colliders, full);
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

        /// <summary>
        /// Reads colliders touching a capsule in the server's default physics scene. Endpoints
        /// are sphere centers; coincident endpoints query a sphere. Uses explicit layers/triggers
        /// and the segment API's once-per-frame transform synchronization, not a frozen scene.
        /// True means complete: Success has overlaps, Miss has none. False with BufferFull
        /// supplies a possibly truncated snapshot; every other failure leaves snapshot null.
        /// Uses one shared collision-read unit plus one per returned collider, at most 65 units.
        /// This is not a sweep, a contact/damage query or proof of safe actor placement.
        /// </summary>
        public static bool TryOverlapCapsule(Vector3 point0, Vector3 point1, float radius, int layerMask,
            CollisionTriggers triggers, out CapsuleOverlap snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!ValidCollisionCapsule(point0, point1, radius)
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
                var buffer = _collisionOverlapBuffer ??= new Il2CppReferenceArray<Collider>(MaximumCollisionOverlapsPerQuery);
                var start = new UnityEngine.Vector3(point0.X, point0.Y, point0.Z);
                var end = new UnityEngine.Vector3(point1.X, point1.Y, point1.Z);
                var triggerMode = triggers == CollisionTriggers.Collide
                    ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
                ColliderOverlap[] colliders;
                int count;
                try
                {
                    count = point0.Equals(point1)
                        ? scene.OverlapSphere(start, radius, buffer, layerMask, triggerMode)
                        : scene.OverlapCapsule(start, end, radius, buffer, layerMask, triggerMode);
                    if (!TryCaptureCollisionOverlaps(world, frame, buffer, count, layerMask, triggers,
                        out colliders, out status)) return false;
                }
                finally
                {
                    // The reusable native array must not retain collider references across world lifetimes.
                    for (int i = 0; i < MaximumCollisionOverlapsPerQuery; i++) buffer[i] = null;
                }
                if (!CollisionContextCurrent(world, frame))
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                bool full = count == MaximumCollisionOverlapsPerQuery;
                snapshot = new CapsuleOverlap(world, frame, point0, point1, radius, layerMask,
                    triggers, colliders, full);
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

        private static bool TryCaptureCollisionOverlaps(WorldRecordContext world, int frame,
            Il2CppReferenceArray<Collider> buffer, int count, int layerMask, CollisionTriggers triggers,
            out ColliderOverlap[] colliders, out CollisionReadStatus status)
        {
            colliders = null;
            status = CollisionReadStatus.Success;
            if (!CollisionContextCurrent(world, frame))
            {
                status = CollisionReadStatus.StaleHit;
                return false;
            }
            if (count < 0 || count > MaximumCollisionOverlapsPerQuery)
            {
                status = CollisionReadStatus.Failed;
                return false;
            }
            colliders = count == 0 ? Array.Empty<ColliderOverlap>() : new ColliderOverlap[count];
            var observations = count == 0
                ? Array.Empty<CollisionOverlapObservation>() : new CollisionOverlapObservation[count];
            for (int i = 0; i < count; i++)
            {
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                if (!CollisionOverlapObservation.TryCapture(buffer[i], layerMask, triggers, out var observation))
                    return CollisionChanged(out status);
                observations[i] = observation;
                colliders[i] = observation.Snapshot;
            }
            for (int i = 0; i < count; i++)
                if (!observations[i].IsCurrent()) return CollisionChanged(out status);
            return true;
        }

        /// <summary>
        /// Matches the observed collider ancestry to an exact retained player attachment.
        /// A mismatch does not retire a valid player binding. This does not authorize damage.
        /// </summary>
        public static bool TryMatchHitPlayer(CollisionHit hit, PlayerHandle binding,
            out EntitySnapshot player, out CollisionReadStatus status)
            => TryMatchHitPlayerCore(hit, binding, false, false, out player, out _, out _, out status);

        /// <summary>
        /// Also requires one supported native impact-receiver route to this exact player.
        /// Reads callback identities without invoking them. This does not calculate or authorize damage.
        /// </summary>
        public static bool TryMatchHitPlayerReceiver(CollisionHit hit, PlayerHandle binding,
            out EntitySnapshot player, out CollisionReadStatus status)
            => TryMatchHitPlayerCore(hit, binding, true, false, out player, out _, out _, out status);

        private static bool TryMatchHitPlayerCore(CollisionHit hit, PlayerHandle binding,
            bool requireReceiver, bool requireNode, out EntitySnapshot player,
            out DamageNodeObservation nodeObservation, out PlayerImpactRoute capturedRoute,
            out CollisionReadStatus status)
        {
            player = null;
            nodeObservation = null;
            capturedRoute = default;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (hit == null || !ReferenceEquals(world, hit.World) || frame != hit.Frame)
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!SameCollision(hit)) return CollisionChanged(out status);
                if (hit.Candidate == null)
                {
                    status = CollisionReadStatus.EntityUnavailable;
                    return false;
                }
                if (!TryReadPlayerBinding(binding, out var snapshot, out var bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!MatchesPlayerAttachment(binding, hit.Candidate)
                    || hit.Attachment.Pointer != binding.Attachment.Pointer)
                {
                    status = CollisionReadStatus.PlayerMismatch;
                    return false;
                }
                PlayerImpactRoute route = default;
                if (requireReceiver && !TryReadPlayerImpactRoute(hit, binding, out route, out status)) return false;
                DamageNodeObservation node = null;
                if (requireNode && !DamageNodeObservation.TryCapture(hit, binding, route.DirectNode, out node, out status))
                    return false;
                if (!CollisionContextCurrent(world, frame) || !SameCollision(hit))
                    return CollisionChanged(out status);
                if (requireReceiver)
                {
                    if (!TryReadPlayerImpactRoute(hit, binding, out var currentRoute, out status)) return false;
                    if (!route.Matches(currentRoute) || (requireNode && !node.IsCurrent(currentRoute.DirectNode)))
                    {
                        status = CollisionReadStatus.ReceiverChanged;
                        return false;
                    }
                    if (!SameCollision(hit)) return CollisionChanged(out status);
                }
                // Revalidate the canonical player after native collider and callback reads.
                if (!TryReadPlayerBinding(binding, out snapshot, out bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!CollisionContextCurrent(world, frame))
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                player = snapshot;
                nodeObservation = node;
                capturedRoute = route;
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

        private static bool TryCreateCollisionHit(WorldRecordContext world, int frame,
            Vector3 origin, Vector3 end, int layerMask, CollisionTriggers triggers,
            RaycastHit nativeHit, float distance, out CollisionHit hit,
            out CollisionReadStatus status)
        {
            hit = null;
            var collider = nativeHit.collider;
            if (!LiveCollider(collider)) return CollisionChanged(out status);
            var surfaceTransform = nativeHit.transform;
            if (!LiveBallisticObject(surfaceTransform)) return CollisionChanged(out status);
            var point = nativeHit.point;
            var normal = nativeHit.normal;
            float hitDistance = nativeHit.distance;
            if (!Finite(point) || !Finite(normal) || !float.IsFinite(hitDistance)
                || hitDistance < 0 || hitDistance > distance)
                return CollisionChanged(out status);

            CaptureCollisionAttachment(collider, out var candidate, out var attachment, out var networkId);
            var observation = new CollisionHit(world, frame, origin, end, layerMask, triggers,
                new Vector3(point.x, point.y, point.z), new Vector3(normal.x, normal.y, normal.z),
                hitDistance, collider, collider.GetInstanceID(), collider.gameObject.layer, collider.isTrigger,
                surfaceTransform, surfaceTransform.GetInstanceID(), candidate, attachment, networkId);
            if (!CollisionContextCurrent(world, frame) || !SameCollision(observation))
                return CollisionChanged(out status);
            hit = observation;
            status = CollisionReadStatus.Success;
            return true;
        }

        private static bool TryCollisionContext(out WorldRecordContext world, out int frame,
            out CollisionReadStatus status)
        {
            world = null;
            frame = 0;
            var context = CheckContext();
            status = context == WorldReadStatus.WrongThread ? CollisionReadStatus.WrongThread
                : context == WorldReadStatus.NotServer ? CollisionReadStatus.NotServer
                : CollisionReadStatus.Unavailable;
            if (context != WorldReadStatus.Success) return false;
            if (_collisionReading) { status = CollisionReadStatus.Busy; return false; }
            if (!WorldRecords.TryGetCurrent(out world, out _))
            {
                status = CollisionReadStatus.WorldUnavailable;
                return false;
            }
            frame = Time.frameCount;
            return true;
        }

        private static bool ReserveCollisionRead(WorldRecordContext world, int frame,
            out CollisionReadStatus status)
        {
            if (!ReferenceEquals(world, _collisionWorld) || frame != _collisionFrame)
            {
                _collisionWorld = world;
                _collisionFrame = frame;
                _collisionReads = 0;
                _collisionSynced = false;
            }
            status = CollisionReadStatus.LimitReached;
            if (_collisionReads >= MaximumCollisionReadsPerFrame) return false;
            _collisionReads++;
            return true;
        }

        private static bool CollisionContextCurrent(WorldRecordContext world, int frame)
        {
            return CheckContext() == WorldReadStatus.Success
                && WorldRecords.IsCurrent(world) && Time.frameCount == frame;
        }

        private static bool ValidCollisionCapsule(Vector3 point0, Vector3 point1, float radius)
        {
            return Finite(point0) && Finite(point1) && float.IsFinite(radius) && radius > 0
                && (point0.Equals(point1) || TrySegment(point0, point1, out _, out _));
        }

        private static bool TryCollisionBox(Vector3 center, Vector3 halfExtents, Quaternion orientation,
            out Quaternion normalizedOrientation)
        {
            normalizedOrientation = default;
            if (!Finite(center) || !Finite(halfExtents)
                || halfExtents.X <= 0 || halfExtents.Y <= 0 || halfExtents.Z <= 0
                || !float.IsFinite(orientation.X) || !float.IsFinite(orientation.Y)
                || !float.IsFinite(orientation.Z) || !float.IsFinite(orientation.W)) return false;
            // Double precision avoids overflow/underflow when normalizing finite float components.
            var lengthSquared = (double)orientation.X * orientation.X + (double)orientation.Y * orientation.Y
                + (double)orientation.Z * orientation.Z + (double)orientation.W * orientation.W;
            if (lengthSquared == 0) return false;
            var inverseLength = 1 / Math.Sqrt(lengthSquared);
            normalizedOrientation = new Quaternion((float)(orientation.X * inverseLength), (float)(orientation.Y * inverseLength),
                (float)(orientation.Z * inverseLength), (float)(orientation.W * inverseLength));
            return true;
        }

        private static bool TrySegment(Vector3 origin, Vector3 end, out UnityEngine.Vector3 direction,
            out float distance)
        {
            direction = default;
            distance = 0;
            if (!Finite(origin) || !Finite(end)) return false;
            double x = (double)end.X - origin.X, y = (double)end.Y - origin.Y, z = (double)end.Z - origin.Z;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (!double.IsFinite(length) || length <= 0 || length > float.MaxValue) return false;
            distance = (float)length;
            if (distance <= 0) return false;
            direction = new UnityEngine.Vector3((float)(x / length), (float)(y / length), (float)(z / length));
            return true;
        }

        private static bool LiveCollider(Collider collider)
        {
            if (collider is null || collider.WasCollected || !collider || !collider.enabled) return false;
            var gameObject = collider.gameObject;
            return gameObject != null && !gameObject.WasCollected && gameObject && gameObject.activeInHierarchy;
        }

        private static void CaptureCollisionAttachment(Collider collider, out BoltEntity candidate,
            out Bolt.Entity attachment, out ulong? networkId)
        {
            candidate = CollisionAncestor(collider);
            attachment = null;
            networkId = null;
            if (candidate != null && !candidate.WasCollected && candidate && candidate.isAttached)
            {
                attachment = candidate._entity;
                if (attachment != null && !attachment.WasCollected)
                    networkId = candidate.networkId.PackedValue;
            }
            if (!networkId.HasValue)
            {
                candidate = null;
                attachment = null;
            }
        }

        private static BoltEntity CollisionAncestor(Collider collider)
        {
            var transform = collider.transform;
            for (int i = 0; i < MaximumCollisionAncestors && transform != null && !transform.WasCollected && transform; i++)
            {
                var candidate = transform.GetComponent<BoltEntity>();
                // Stop at the nearest entity, including an unattached one; never skip into its parent entity.
                if (candidate != null && !candidate.WasCollected && candidate) return candidate;
                transform = transform.parent;
            }
            return null;
        }

        private static bool SameCollision(CollisionHit hit)
        {
            var collider = hit.Collider;
            if (!LiveCollider(collider) || collider.GetInstanceID() != hit.ColliderInstanceId
                || collider.gameObject.layer != hit.ColliderLayer || collider.isTrigger != hit.IsTrigger)
                return false;
            var rigidbody = collider.attachedRigidbody;
            var surfaceTransform = LiveBallisticObject(rigidbody) ? rigidbody.transform : collider.transform;
            if (!SameBallisticObject(surfaceTransform, hit.SurfaceTransform)
                || surfaceTransform.GetInstanceID() != hit.SurfaceTransformInstanceId)
                return false;
            var current = CollisionAncestor(collider);
            if (hit.Candidate == null)
                return current == null || current.WasCollected || !current || !current.isAttached;
            var retained = hit.Candidate;
            var attachment = hit.Attachment;
            if (current is null || current.WasCollected || !current || !current.isAttached
                || retained.WasCollected || !retained || current.Pointer != retained.Pointer
                || attachment is null || attachment.WasCollected)
                return false;
            var active = current._entity;
            return active != null && !active.WasCollected && active.Pointer == attachment.Pointer
                && current.networkId.PackedValue == hit.CandidateNetworkId;
        }

        private sealed class CollisionOverlapObservation
        {
            private readonly Collider _collider;
            private readonly BoltEntity _candidate;
            private readonly Bolt.Entity _attachment;

            private CollisionOverlapObservation(Collider collider, BoltEntity candidate, Bolt.Entity attachment,
                ColliderOverlap snapshot)
            { _collider = collider; _candidate = candidate; _attachment = attachment; Snapshot = snapshot; }

            internal ColliderOverlap Snapshot { get; }

            internal static bool TryCapture(Collider collider, int layerMask, CollisionTriggers triggers,
                out CollisionOverlapObservation observation)
            {
                observation = null;
                if (!LiveCollider(collider)) return false;
                int layer = collider.gameObject.layer;
                bool isTrigger = collider.isTrigger;
                if (layer < 0 || layer >= 32 || (layerMask & (1 << layer)) == 0
                    || (triggers == CollisionTriggers.Ignore && isTrigger)) return false;
                var bounds = collider.bounds;
                var center = bounds.center;
                var extents = bounds.extents;
                if (!Finite(center) || !Finite(extents) || extents.x < 0 || extents.y < 0 || extents.z < 0)
                    return false;
                CaptureCollisionAttachment(collider, out var candidate, out var attachment, out var networkId);
                var snapshot = new ColliderOverlap(collider.GetInstanceID(), layer, isTrigger,
                    new Vector3(center.x, center.y, center.z), new Vector3(extents.x, extents.y, extents.z), networkId);
                observation = new CollisionOverlapObservation(collider, candidate, attachment, snapshot);
                return true;
            }

            internal bool IsCurrent()
            {
                if (!LiveCollider(_collider) || _collider.GetInstanceID() != Snapshot.ColliderInstanceId
                    || _collider.gameObject.layer != Snapshot.ColliderLayer || _collider.isTrigger != Snapshot.IsTrigger)
                    return false;
                var bounds = _collider.bounds;
                var center = bounds.center;
                var extents = bounds.extents;
                if (!Snapshot.BoundsCenter.Equals(new Vector3(center.x, center.y, center.z))
                    || !Snapshot.BoundsExtents.Equals(new Vector3(extents.x, extents.y, extents.z))) return false;
                var current = CollisionAncestor(_collider);
                if (_candidate == null)
                    return current == null || current.WasCollected || !current || !current.isAttached;
                if (current is null || current.WasCollected || !current || !current.isAttached
                    || _candidate.WasCollected || !_candidate || current.Pointer != _candidate.Pointer
                    || _attachment is null || _attachment.WasCollected) return false;
                var attachment = current._entity;
                return attachment != null && !attachment.WasCollected && attachment.Pointer == _attachment.Pointer
                    && current.networkId.PackedValue == Snapshot.CandidateNetworkId;
            }
        }

        private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        private static bool Finite(UnityEngine.Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool CollisionChanged(out CollisionReadStatus status)
        { status = CollisionReadStatus.HitChanged; return false; }
        private static bool CollisionUnavailable(out CollisionReadStatus status)
        { status = CollisionReadStatus.Unavailable; return false; }
        // Only a retired handle reports BindingChanged; other binding failures stay retryable.
        private static bool CollisionBindingFailed(WorldReadStatus bindingStatus, out CollisionReadStatus status)
        {
            status = bindingStatus == WorldReadStatus.BindingChanged
                ? CollisionReadStatus.BindingChanged : CollisionReadStatus.Unavailable;
            return false;
        }
    }
}
