// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Modstoke.Sons
{
    public enum CollisionTriggers
    {
        Ignore,
        Collide
    }

    public enum CollisionReadStatus
    {
        Success,
        Miss,
        Unavailable,
        WrongThread,
        NotServer,
        WorldUnavailable,
        InvalidQuery,
        LimitReached,
        Busy,
        StaleHit,
        HitChanged,
        EntityUnavailable,
        PlayerMismatch,
        BindingChanged,
        Failed,
        SurfaceUnavailable,
        InvalidMaterial,
        BufferFull,
        UnsupportedProfile,
        ReceiverUnavailable,
        ReceiverUnsupported,
        ReceiverAmbiguous,
        ReceiverChanged,
        InvalidDamageNode
    }

    /// <summary>
    /// Copied details of one overlapping collider, not a contact point or player binding.
    /// Instance IDs and optional network IDs are observation-time lookup hints only.
    /// </summary>
    public sealed class ColliderOverlap
    {
        internal ColliderOverlap(int colliderInstanceId, int colliderLayer, bool isTrigger,
            Vector3 boundsCenter, Vector3 boundsExtents, ulong? candidateNetworkId)
        {
            ColliderInstanceId = colliderInstanceId;
            ColliderLayer = colliderLayer;
            IsTrigger = isTrigger;
            BoundsCenter = boundsCenter;
            BoundsExtents = boundsExtents;
            CandidateNetworkId = candidateNetworkId;
        }

        public int ColliderInstanceId { get; }
        public int ColliderLayer { get; }
        public bool IsTrigger { get; }
        public Vector3 BoundsCenter { get; }
        public Vector3 BoundsExtents { get; }

        /// <summary>Null includes missing, unavailable or ancestry-limit-unknown entity association.</summary>
        public ulong? CandidateNetworkId { get; }
    }

    /// <summary>
    /// A bounded observation of the server's loaded default physics scene. Contains managed
    /// copies only; no collider is retained or authorized for mutation. Results are unordered,
    /// may include several colliders for one entity, and do not establish streamed-out geometry.
    /// </summary>
    public sealed class CapsuleOverlap
    {
        internal CapsuleOverlap(WorldRecordContext world, int frame,
            Vector3 point0, Vector3 point1, float radius, int layerMask, CollisionTriggers triggers,
            ColliderOverlap[] colliders, bool mayBeTruncated)
        {
            WorldId = world.WorldId;
            WorldGeneration = world.Generation;
            Frame = frame;
            Point0 = point0;
            Point1 = point1;
            Radius = radius;
            LayerMask = layerMask;
            Triggers = triggers;
            Colliders = Array.AsReadOnly(colliders);
            MayBeTruncated = mayBeTruncated;
        }

        public Guid WorldId { get; }
        public long WorldGeneration { get; }
        public int Frame { get; }
        /// <summary>World-space center of the capsule's first end sphere.</summary>
        public Vector3 Point0 { get; }
        /// <summary>World-space center of the capsule's second end sphere.</summary>
        public Vector3 Point1 { get; }
        public float Radius { get; }
        public int LayerMask { get; }
        public CollisionTriggers Triggers { get; }
        public IReadOnlyList<ColliderOverlap> Colliders { get; }

        /// <summary>
        /// The native buffer filled. Additional overlaps and their total count are unknown;
        /// the returned subset is not guaranteed to contain the nearest colliders.
        /// </summary>
        public bool MayBeTruncated { get; }
    }

    /// <summary>
    /// Copied box-overlap observation with no retained native objects or mutation authority.
    /// Results are unordered, may include several colliders per entity, and do not guarantee
    /// safe placement or cover streamed-out geometry.
    /// </summary>
    public sealed class BoxOverlap
    {
        internal BoxOverlap(WorldRecordContext world, int frame,
            Vector3 center, Vector3 halfExtents, Quaternion orientation, int layerMask, CollisionTriggers triggers,
            ColliderOverlap[] colliders, bool mayBeTruncated)
        {
            WorldId = world.WorldId;
            WorldGeneration = world.Generation;
            Frame = frame;
            Center = center;
            HalfExtents = halfExtents;
            Orientation = orientation;
            LayerMask = layerMask;
            Triggers = triggers;
            Colliders = Array.AsReadOnly(colliders);
            MayBeTruncated = mayBeTruncated;
        }

        public Guid WorldId { get; }
        public long WorldGeneration { get; }
        public int Frame { get; }
        /// <summary>World-space center of the box.</summary>
        public Vector3 Center { get; }
        /// <summary>Half-lengths along the box's local axes.</summary>
        public Vector3 HalfExtents { get; }
        /// <summary>Normalized world-space orientation used for the query.</summary>
        public Quaternion Orientation { get; }
        public int LayerMask { get; }
        public CollisionTriggers Triggers { get; }
        public IReadOnlyList<ColliderOverlap> Colliders { get; }

        /// <summary>
        /// The native buffer filled. Additional overlaps and their total count are unknown;
        /// the returned subset is not guaranteed to contain the nearest colliders.
        /// </summary>
        public bool MayBeTruncated { get; }
    }

    /// <summary>
    /// A bounded capsule sweep in the server's loaded default physics scene. Geometry is copied;
    /// hits retain opaque native handles for same-frame use, not mutation or damage authority.
    /// Does not establish streamed-out geometry, safe movement or placement clearance.
    /// </summary>
    public sealed class CapsuleSweep
    {
        internal CapsuleSweep(WorldRecordContext world, int frame, Vector3 point0, Vector3 point1,
            float radius, Vector3 direction, float maxDistance, int layerMask, CollisionTriggers triggers,
            CollisionHit[] hits, bool mayBeTruncated)
        {
            WorldId = world.WorldId;
            WorldGeneration = world.Generation;
            Frame = frame;
            Point0 = point0;
            Point1 = point1;
            Radius = radius;
            Direction = direction;
            MaxDistance = maxDistance;
            LayerMask = layerMask;
            Triggers = triggers;
            Hits = Array.AsReadOnly(hits);
            MayBeTruncated = mayBeTruncated;
        }

        public Guid WorldId { get; }
        public long WorldGeneration { get; }
        public int Frame { get; }
        /// <summary>World-space center of the first end sphere at the start of the sweep.</summary>
        public Vector3 Point0 { get; }
        /// <summary>World-space center of the second end sphere at the start of the sweep.</summary>
        public Vector3 Point1 { get; }
        public float Radius { get; }
        /// <summary>Normalized world-space translation direction used for the query.</summary>
        public Vector3 Direction { get; }
        public float MaxDistance { get; }
        public int LayerMask { get; }
        public CollisionTriggers Triggers { get; }

        /// <summary>
        /// Native-order hits, not sorted by distance; several colliders may belong to one entity.
        /// Origin/End describe capsule-midpoint travel. Distance measures translation, not distance
        /// to Point. Initial overlaps can return zero distance/point and an opposing normal;
        /// these are not resolved contacts. Match only on the same game thread, frame and world.
        /// </summary>
        public IReadOnlyList<CollisionHit> Hits { get; }

        /// <summary>
        /// The native buffer filled. Additional hits and their total count are unknown;
        /// the returned subset is not guaranteed to contain the nearest hit.
        /// </summary>
        public bool MayBeTruncated { get; }
    }

    /// <summary>
    /// A bounded, fixed-orientation box sweep in the server's loaded default physics scene.
    /// Geometry is copied; hits retain opaque native handles for same-frame use, not mutation
    /// or damage authority. Does not establish streamed-out geometry, safe movement or placement.
    /// </summary>
    public sealed class BoxSweep
    {
        internal BoxSweep(WorldRecordContext world, int frame, Vector3 center, Vector3 halfExtents,
            Quaternion orientation, Vector3 direction, float maxDistance, int layerMask, CollisionTriggers triggers,
            CollisionHit[] hits, bool mayBeTruncated)
        {
            WorldId = world.WorldId;
            WorldGeneration = world.Generation;
            Frame = frame;
            Center = center;
            HalfExtents = halfExtents;
            Orientation = orientation;
            Direction = direction;
            MaxDistance = maxDistance;
            LayerMask = layerMask;
            Triggers = triggers;
            Hits = Array.AsReadOnly(hits);
            MayBeTruncated = mayBeTruncated;
        }

        public Guid WorldId { get; }
        public long WorldGeneration { get; }
        public int Frame { get; }
        /// <summary>World-space box center at the start of the sweep.</summary>
        public Vector3 Center { get; }
        /// <summary>Half-lengths along the box's local axes.</summary>
        public Vector3 HalfExtents { get; }
        /// <summary>Normalized world-space orientation held fixed throughout the sweep.</summary>
        public Quaternion Orientation { get; }
        /// <summary>Normalized world-space translation direction used for the query.</summary>
        public Vector3 Direction { get; }
        public float MaxDistance { get; }
        public int LayerMask { get; }
        public CollisionTriggers Triggers { get; }

        /// <summary>
        /// Native-order hits, not sorted by distance; several colliders may belong to one entity.
        /// Origin/End describe box-center travel. Distance measures translation, not distance to
        /// Point. Initial overlaps can return zero distance/point and an opposing normal;
        /// these are not resolved contacts. Match only on the same game thread, frame and world.
        /// </summary>
        public IReadOnlyList<CollisionHit> Hits { get; }

        /// <summary>
        /// The native buffer filled. Additional hits and their total count are unknown;
        /// the returned subset is not guaranteed to contain the nearest hit.
        /// </summary>
        public bool MayBeTruncated { get; }
    }

    /// <summary>
    /// One native collision observation. Match it on the same game frame and world lifetime.
    /// A hit does not establish trusted player movement, a damage node or a permitted attack.
    /// </summary>
    public sealed class CollisionHit
    {
        internal CollisionHit(WorldRecordContext world, int frame,
            Vector3 origin, Vector3 end, int layerMask, CollisionTriggers triggers,
            Vector3 point, Vector3 normal, float distance, UnityEngine.Collider collider,
            int colliderInstanceId, int colliderLayer, bool isTrigger, UnityEngine.Transform surfaceTransform,
            int surfaceTransformInstanceId, BoltEntity candidate,
            Bolt.Entity attachment, ulong? networkId)
        {
            World = world;
            Frame = frame;
            Origin = origin;
            End = end;
            LayerMask = layerMask;
            Triggers = triggers;
            Point = point;
            Normal = normal;
            Distance = distance;
            Collider = collider;
            ColliderInstanceId = colliderInstanceId;
            ColliderLayer = colliderLayer;
            IsTrigger = isTrigger;
            SurfaceTransform = surfaceTransform;
            SurfaceTransformInstanceId = surfaceTransformInstanceId;
            Candidate = candidate;
            Attachment = attachment;
            CandidateNetworkId = networkId;
        }

        internal WorldRecordContext World { get; }
        internal UnityEngine.Collider Collider { get; }
        internal int ColliderInstanceId { get; }
        internal UnityEngine.Transform SurfaceTransform { get; }
        internal int SurfaceTransformInstanceId { get; }
        internal BoltEntity Candidate { get; }
        internal Bolt.Entity Attachment { get; }
        public int Frame { get; }
        public Vector3 Origin { get; }
        public Vector3 End { get; }
        public int LayerMask { get; }
        public CollisionTriggers Triggers { get; }
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        public float Distance { get; }
        public int ColliderLayer { get; }
        public bool IsTrigger { get; }

        /// <summary>A lookup hint only. Null includes unrecognized or unavailable attachment ancestry.</summary>
        public ulong? CandidateNetworkId { get; }
    }
}
