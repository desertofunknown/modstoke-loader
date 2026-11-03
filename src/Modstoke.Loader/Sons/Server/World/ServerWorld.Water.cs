// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Physics;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        public const float MaximumWaterSearchRadius = 4f;
        private const int MaximumWaterColliders = 32;
        private static Il2CppReferenceArray<Collider> _waterOverlaps;
        private static Il2CppSystem.Collections.Generic.List<Collider> _waterColumns;
        private static readonly Bounds[] WaterBounds = new Bounds[MaximumWaterColliders];

        /// <summary>
        /// Reads a native water surface among loaded colliders near the supplied position.
        /// The caller supplies qualified water and depth-blocker layers. This is a column
        /// observation, not a submerged-player, cave, ocean or safe-travel verdict.
        /// False, including SurfaceUnavailable, never means the position is dry.
        /// </summary>
        public static bool TryReadWaterColumn(Vector3 position, float searchRadius,
            int waterLayerMask, int blockerLayerMask, out WaterColumn snapshot,
            out CollisionReadStatus status)
            => TryReadWaterColumnCore(position, searchRadius, waterLayerMask, blockerLayerMask,
                false, null, out snapshot, out status);

        /// <summary>
        /// Reads a column using only supported non-ocean colliders containing the supplied
        /// point, including their boundary. Searches within 0.1 metres on the caller's water
        /// layers. A null tag allows any tag; otherwise an exact tag match is required.
        /// This does not identify a player's head or validate its position or oxygen supply.
        /// </summary>
        public static bool TryReadWaterVolume(Vector3 position, int waterLayerMask,
            int blockerLayerMask, string requiredTag, out WaterColumn snapshot,
            out CollisionReadStatus status)
            => TryReadWaterColumnCore(position, 0.1f, waterLayerMask, blockerLayerMask,
                true, requiredTag, out snapshot, out status);

        private static bool TryReadWaterColumnCore(Vector3 position, float searchRadius,
            int waterLayerMask, int blockerLayerMask, bool requireVolume, string requiredTag,
            out WaterColumn snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!Finite(position) || !float.IsFinite(searchRadius) || searchRadius <= 0
                    || searchRadius > MaximumWaterSearchRadius || waterLayerMask == 0
                    || requiredTag is { Length: 0 })
                {
                    status = CollisionReadStatus.InvalidQuery;
                    return false;
                }
                // The native rays use the global trigger setting. Do not change it for a read.
                if (!Physics.queriesHitTriggers)
                {
                    status = CollisionReadStatus.UnsupportedProfile;
                    return false;
                }
                // One overlap, up to six native water rays and one depth-blocker ray.
                // Reserve their full cost before doing any physics work.
                for (int i = 0; i < 8; i++)
                    if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!_collisionSynced)
                {
                    if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                    _collisionSynced = true;
                }
                if (!Physics.defaultPhysicsScene.IsValid()) return CollisionUnavailable(out status);
                _waterOverlaps ??= new Il2CppReferenceArray<Collider>(MaximumWaterColliders);
                _waterColumns ??= new Il2CppSystem.Collections.Generic.List<Collider>(MaximumWaterColliders);
                var query = new UnityEngine.Vector3(position.X, position.Y, position.Z);
                int count = Physics.OverlapSphereNonAlloc(query, searchRadius, _waterOverlaps,
                    waterLayerMask, QueryTriggerInteraction.Collide);
                if (count < 0) { status = CollisionReadStatus.Failed; return false; }
                if (count >= MaximumWaterColliders)
                {
                    status = CollisionReadStatus.BufferFull;
                    return false;
                }
                if (count == 0) { status = CollisionReadStatus.SurfaceUnavailable; return false; }

                float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
                int selected = 0;
                for (int i = 0; i < count; i++)
                {
                    var collider = _waterOverlaps[i];
                    if (!WaterColliderCurrent(collider, waterLayerMask)) return CollisionChanged(out status);
                    if (requireVolume && !MatchesWaterVolume(collider, requiredTag)) continue;
                    var bounds = collider.bounds;
                    var min = bounds.min;
                    var max = bounds.max;
                    if (!Finite(min) || !Finite(max) || min.x > max.x || min.y > max.y || min.z > max.z)
                        return CollisionChanged(out status);
                    if (requireVolume)
                    {
                        if (!TryWaterContainsPoint(collider, query, world, frame, out bool contains, out status))
                            return false;
                        if (!contains) continue;
                    }
                    // Compact only earlier entries, leaving unread overlap results intact.
                    _waterOverlaps[selected] = collider;
                    WaterBounds[selected++] = bounds;
                    top = Math.Max(top, max.y);
                    bottom = Math.Min(bottom, min.y);
                    _waterColumns.Add(collider);
                }
                count = selected;
                if (count == 0) { status = CollisionReadStatus.SurfaceUnavailable; return false; }
                float rayStart = top + 1f, rayDistance = rayStart - bottom;
                if (!float.IsFinite(rayStart) || rayStart <= top || !float.IsFinite(rayDistance) || rayDistance <= 0)
                {
                    status = CollisionReadStatus.InvalidQuery;
                    return false;
                }

                bool found = WaterLevelSensor.RayCastWaterLevel(query, _waterColumns,
                    waterLayerMask, blockerLayerMask, out float height, out float depth,
                    out var normal, out float volumeTop, out var hitCollider);
                if (!CollisionContextCurrent(world, frame) || !Physics.queriesHitTriggers
                    || _waterColumns.Count != count)
                    return CollisionChanged(out status);
                for (int i = 0; i < count; i++)
                {
                    var collider = _waterOverlaps[i];
                    if (!WaterColliderCurrent(collider, waterLayerMask)
                        || !SameBallisticObject(collider, _waterColumns[i])
                        || !collider.bounds.Equals(WaterBounds[i])
                        || (requireVolume && !MatchesWaterVolume(collider, requiredTag)))
                        return CollisionChanged(out status);
                }
                if (!found) { status = CollisionReadStatus.SurfaceUnavailable; return false; }
                if (!WaterColliderCurrent(hitCollider, waterLayerMask) || !_waterColumns.Contains(hitCollider)
                    || !float.IsFinite(height) || !float.IsFinite(depth) || depth < 0
                    || !float.IsFinite(volumeTop) || !Finite(normal) || normal.sqrMagnitude <= 0
                    || height < bottom || height > top || volumeTop != top)
                    return CollisionChanged(out status);
                if (requireVolume)
                {
                    if (!TryWaterContainsPoint(hitCollider, query, world, frame, out bool contains, out status))
                        return false;
                    if (!contains || !WaterColliderCurrent(hitCollider, waterLayerMask)
                        || !MatchesWaterVolume(hitCollider, requiredTag)
                        || !CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                }

                snapshot = new WaterColumn(world, frame, position, searchRadius,
                    waterLayerMask, blockerLayerMask, height, depth, volumeTop,
                    new Vector3(normal.x, normal.y, normal.z), hitCollider.GetInstanceID(),
                    requireVolume ? WaterSelection.ContainingNonOceanVolume
                        : WaterSelection.NearbyColumn, requiredTag);
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally
            {
                if (entered)
                {
                    try
                    {
                        _waterColumns?.Clear();
                        if (_waterOverlaps != null)
                            for (int i = 0; i < MaximumWaterColliders; i++) _waterOverlaps[i] = null;
                    }
                    catch (Exception error)
                    {
                        // Discard failed buffers rather than retaining their native objects.
                        _waterColumns = null;
                        _waterOverlaps = null;
                        ReportFailure(error);
                    }
                    finally { _collisionReading = false; }
                }
            }
        }

        /// <summary>Checks world and frame lifetime only; it does not freeze or requery the geometry.</summary>
        public static bool IsCurrentWaterColumn(WaterColumn snapshot)
            => snapshot != null && CollisionContextCurrent(snapshot.World, snapshot.Frame);

        private static bool WaterColliderCurrent(Collider collider, int layerMask)
        {
            if (!LiveCollider(collider)) return false;
            int layer = collider.gameObject.layer;
            return layer >= 0 && layer < 32 && (layerMask & (1 << layer)) != 0;
        }

        private static bool MatchesWaterVolume(Collider collider, string requiredTag)
        {
            // Native sensor admission checks the marker on this collider, not its ancestors.
            if (LiveBallisticObject(collider.GetComponent<IsOcean>())) return false;
            return requiredTag == null || string.Equals(collider.gameObject.tag, requiredTag, StringComparison.Ordinal);
        }

        private static bool TryWaterContainsPoint(Collider collider, UnityEngine.Vector3 point,
            WorldRecordContext world, int frame, out bool contains,
            out CollisionReadStatus status)
        {
            contains = false;
            status = CollisionReadStatus.UnsupportedProfile;
            if (collider.TryCast<BoxCollider>() == null && collider.TryCast<SphereCollider>() == null
                && collider.TryCast<CapsuleCollider>() == null)
            {
                var mesh = collider.TryCast<MeshCollider>();
                if (!LiveBallisticObject(mesh) || !mesh.convex || !LiveBallisticObject(mesh.sharedMesh)) return false;
            }
            if (!ReserveCollisionRead(world, frame, out status)) return false;
            var closest = collider.ClosestPoint(point);
            if (!Finite(closest) || !LiveCollider(collider) || !CollisionContextCurrent(world, frame))
                return CollisionChanged(out status);
            // Unity returns the input for containment or boundary contact. Approximate vector
            // equality would also admit nearby points outside the collider.
            contains = closest.x == point.x && closest.y == point.y && closest.z == point.z;
            status = CollisionReadStatus.Success;
            return true;
        }
    }

    public enum WaterSelection
    {
        NearbyColumn,
        ContainingNonOceanVolume
    }

    /// <summary>
    /// Copied native surface geometry from a nearby collider set. Contains no native objects.
    /// Layer selection, point authority and water-body qualification remain caller responsibilities.
    /// </summary>
    public sealed class WaterColumn
    {
        internal WaterColumn(WorldRecordContext world, int frame,
            Vector3 position, float searchRadius, int waterLayerMask, int blockerLayerMask,
            float height, float depth, float volumeTop, Vector3 normal, int colliderInstanceId,
            WaterSelection selection, string requiredTag)
        {
            World = world;
            Frame = frame;
            SearchPosition = position;
            SearchRadius = searchRadius;
            WaterLayerMask = waterLayerMask;
            BlockerLayerMask = blockerLayerMask;
            Height = height;
            Depth = depth;
            VolumeTop = volumeTop;
            Normal = normal;
            ColliderInstanceId = colliderInstanceId;
            Selection = selection;
            RequiredTag = requiredTag;
        }

        internal WorldRecordContext World { get; }
        public int Frame { get; }
        /// <summary>Center of the collider search, not a validated player position.</summary>
        public Vector3 SearchPosition { get; }
        public float SearchRadius { get; }
        public int WaterLayerMask { get; }
        public int BlockerLayerMask { get; }
        /// <summary>How the native collider set was selected. A nearby column has no point-containment guarantee.</summary>
        public WaterSelection Selection { get; }
        /// <summary>Exact tag filter used for a volume read, or null when no tag was required.</summary>
        public string RequiredTag { get; }
        /// <summary>Surface Y at the search position's X/Z; the native column ray ignores its Y.</summary>
        public float Height { get; }
        /// <summary>Native depth below the surface, adjusted by the supplied blocker layers.</summary>
        public float Depth { get; }
        /// <summary>Highest collider-bounds Y in the nearby set, not necessarily the surface.</summary>
        public float VolumeTop { get; }
        public Vector3 Normal { get; }
        /// <summary>Transient local Unity identity, never a durable world or water-body identifier.</summary>
        public int ColliderInstanceId { get; }
    }
}
