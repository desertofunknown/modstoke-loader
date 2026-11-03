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
        /// <summary>
        /// Classifies every result of a bounded loaded-physics overlap at the point.
        /// Unlike a surface lookup, an empty successful query produces a snapshot.
        /// Coverage is limited to the supplied layers/tag and loaded physics scene;
        /// it does not establish dry land, complete world loading or trusted movement.
        /// </summary>
        public static bool TryReadWaterCoverage(Vector3 position, int waterLayerMask,
            string requiredTag, out WaterCoverage snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!Finite(position) || waterLayerMask == 0 || requiredTag is { Length: 0 })
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!_collisionSynced)
                {
                    if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                    _collisionSynced = true;
                }
                if (!Physics.defaultPhysicsScene.IsValid()) return CollisionUnavailable(out status);
                _waterOverlaps ??= new Il2CppReferenceArray<Collider>(MaximumWaterColliders);
                var point = new UnityEngine.Vector3(position.X, position.Y, position.Z);
                int count = Physics.OverlapSphereNonAlloc(point, 0.1f, _waterOverlaps,
                    waterLayerMask, QueryTriggerInteraction.Collide);
                if (count < 0)
                { status = CollisionReadStatus.Failed; return false; }
                if (count >= MaximumWaterColliders)
                { status = CollisionReadStatus.BufferFull; return false; }

                var candidates = count == 0 ? Array.Empty<WaterCoverageCandidate>() : new WaterCoverageCandidate[count];
                for (int i = 0; i < count; i++)
                {
                    var collider = _waterOverlaps[i];
                    if (!WaterColliderCurrent(collider, waterLayerMask)) return CollisionChanged(out status);
                    var bounds = collider.bounds;
                    var min = bounds.min;
                    var max = bounds.max;
                    if (!Finite(min) || !Finite(max) || min.x > max.x || min.y > max.y || min.z > max.z)
                        return CollisionChanged(out status);
                    int id = collider.GetInstanceID();
                    nint pointer = collider.Pointer;
                    int layer = collider.gameObject.layer;
                    string tag = collider.gameObject.tag;
                    bool trigger = collider.isTrigger;
                    bool ocean = LiveBallisticObject(collider.GetComponent<IsOcean>());
                    bool acceptedTag = requiredTag == null || string.Equals(tag, requiredTag, StringComparison.Ordinal);
                    bool contains = false;
                    if (acceptedTag && !TryWaterContainsPoint(collider, point, world, frame, out contains, out status))
                        return false;
                    if (!WaterColliderCurrent(collider, waterLayerMask) || collider.Pointer != pointer
                        || collider.GetInstanceID() != id || collider.gameObject.layer != layer
                        || !string.Equals(collider.gameObject.tag, tag, StringComparison.Ordinal)
                        || collider.isTrigger != trigger || !collider.bounds.Equals(bounds)
                        || LiveBallisticObject(collider.GetComponent<IsOcean>()) != ocean)
                        return CollisionChanged(out status);
                    candidates[i] = new WaterCoverageCandidate(id, pointer, layer, tag, trigger,
                        ocean, acceptedTag, contains, bounds);
                }
                // Physics result order is unspecified. Compare later reads as bounded sets.
                Array.Sort(candidates, static (left, right) => left.InstanceId.CompareTo(right.InstanceId));
                for (int i = 1; i < candidates.Length; i++)
                    if (candidates[i - 1].InstanceId == candidates[i].InstanceId)
                        return CollisionChanged(out status);
                if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                snapshot = new WaterCoverage(world, frame, position, waterLayerMask, requiredTag, candidates);
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
                        if (_waterOverlaps != null)
                            for (int i = 0; i < MaximumWaterColliders; i++) _waterOverlaps[i] = null;
                    }
                    catch (Exception error) { _waterOverlaps = null; ReportFailure(error); }
                    finally { _collisionReading = false; }
                }
            }
        }

        /// <summary>
        /// Repeats the bounded overlap and containment checks in the original frame.
        /// Compares candidate identities, filters, bounds and point relations, not
        /// complete mesh/shape contents or water-surface heights.
        /// </summary>
        public static bool IsCurrentWaterCoverage(WaterCoverage snapshot,
            out CollisionReadStatus status)
        {
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (snapshot == null)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (!ReferenceEquals(world, snapshot.World) || frame != snapshot.Frame)
                { status = CollisionReadStatus.StaleHit; return false; }
                if (!TryReadWaterCoverage(snapshot.Position, snapshot.WaterLayerMask, snapshot.RequiredTag,
                    out var current, out status)) return false;
                if (!snapshot.Matches(current)) return CollisionChanged(out status);
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        internal readonly record struct WaterCoverageCandidate(int InstanceId, nint Pointer, int Layer,
            string Tag, bool IsTrigger, bool IsOcean, bool AcceptedTag, bool ContainsPoint, Bounds Bounds);
    }

    /// <summary>
    /// Copied classification of loaded colliders near a point. Counts apply only to
    /// the supplied water layers and tag. No count is a complete wet/dry verdict.
    /// </summary>
    public sealed class WaterCoverage
    {
        private readonly ServerWorld.WaterCoverageCandidate[] _candidates;

        internal WaterCoverage(WorldRecordContext world, int frame, Vector3 position,
            int waterLayerMask, string requiredTag, ServerWorld.WaterCoverageCandidate[] candidates)
        {
            World = world;
            Frame = frame;
            Position = position;
            WaterLayerMask = waterLayerMask;
            RequiredTag = requiredTag;
            _candidates = candidates;
            foreach (var candidate in candidates)
            {
                if (!candidate.AcceptedTag) IgnoredByTagCount++;
                else if (!candidate.ContainsPoint) OutsidePointCount++;
                else if (candidate.IsOcean) OceanVolumeCount++;
                else NonOceanVolumeCount++;
            }
        }

        internal WorldRecordContext World { get; }
        public int Frame { get; }
        public Vector3 Position { get; }
        public int WaterLayerMask { get; }
        public string RequiredTag { get; }
        public int NearbyColliderCount => _candidates.Length;
        public int IgnoredByTagCount { get; }
        public int OutsidePointCount { get; }
        public int NonOceanVolumeCount { get; }
        public int OceanVolumeCount { get; }

        internal bool Matches(WaterCoverage other)
        {
            if (other == null || !ReferenceEquals(World, other.World) || Frame != other.Frame
                || Position != other.Position || WaterLayerMask != other.WaterLayerMask
                || !string.Equals(RequiredTag, other.RequiredTag, StringComparison.Ordinal)
                || _candidates.Length != other._candidates.Length) return false;
            for (int i = 0; i < _candidates.Length; i++)
                if (_candidates[i] != other._candidates[i]) return false;
            return true;
        }
    }
}
