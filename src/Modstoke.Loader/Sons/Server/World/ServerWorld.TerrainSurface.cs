// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        public const int MaximumTerrainSurfaceSamples = 64;

        /// <summary>
        /// Reads the interpolated heightmap normal from exactly one containing, loaded,
        /// translation-only terrain and derives its angle from world up in degrees.
        /// Position Y does not select a floor. Holes, caves and meshes are not covered;
        /// a sample is not evidence of collision, walkability, dryness or cave membership.
        /// Uses four shared collision-read budget units. No native object is retained.
        /// </summary>
        public static bool TryReadTerrainSurface(Vector3 position, out TerrainSurface snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!Finite(position))
                { status = CollisionReadStatus.InvalidQuery; return false; }
                // Two configuration captures, one exact terrain lookup and one native normal read.
                for (int i = 0; i < 4; i++)
                    if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!TerrainHeightObservation.TryCapture(world, frame, position, out var before, out status))
                    return false;
                if (!TryResolveTerrainSurface(before.TerrainInstanceId, position,
                    out var terrain, out var data, out float x, out float z, out status)) return false;
                if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);

                var nativeNormal = data.GetInterpolatedNormal(x, z);
                if (!TerrainHeightObservation.TryCapture(world, frame, position, out var after, out status))
                    return false;
                if (!before.Matches(after) || !LiveBallisticObject(terrain)
                    || !SameBallisticObject(data, terrain.terrainData)
                    || !CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                if (!TryCopyTerrainSurfaceNormal(nativeNormal, out var normal, out float slope))
                { status = CollisionReadStatus.SurfaceUnavailable; return false; }

                snapshot = new TerrainSurface(world, frame, position,
                    before.TerrainInstanceId, normal, slope);
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

        /// <summary>
        /// Reads 1 to MaximumTerrainSurfaceSamples points in input order, preserving duplicates.
        /// True means the batch completed, not that all points have heightmap coverage.
        /// Each entry is Success or SurfaceUnavailable; all other failures discard the batch.
        /// Empty, oversized and nonfinite inputs are rejected before terrain reads. No prefix
        /// is returned on failure or truncation: an oversized terrain registry reports BufferFull.
        /// Uses four shared collision-read budget units per attempted point; failed batches may
        /// consume budget. Reads share a server world/frame but are not a frozen terrain snapshot.
        /// Requery after terrain changes; holes, caves, meshes and traversability remain unknown.
        /// </summary>
        public static bool TryReadTerrainSurfaces(ReadOnlySpan<Vector3> positions,
            out TerrainSurfaceBatch snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (positions.Length == 0 || positions.Length > MaximumTerrainSurfaceSamples)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                // Keep a private, bounded copy across the sequential native reads.
                Span<Vector3> points = stackalloc Vector3[positions.Length];
                positions.CopyTo(points);
                foreach (var point in points)
                    if (!Finite(point))
                    { status = CollisionReadStatus.InvalidQuery; return false; }

                var samples = new TerrainSurfaceSample[points.Length];
                int sampledCount = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                    if (TryReadTerrainSurface(points[i], out var surface, out status))
                    {
                        if (surface.WorldId != world.WorldId || surface.WorldGeneration != world.Generation
                            || surface.Frame != frame) return CollisionChanged(out status);
                        sampledCount++;
                    }
                    else if (status != CollisionReadStatus.SurfaceUnavailable) return false;
                    samples[i] = new TerrainSurfaceSample(points[i], surface, status);
                }
                if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                snapshot = new TerrainSurfaceBatch(world, frame, samples, sampledCount);
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

        private static bool TryResolveTerrainSurface(int terrainInstanceId, Vector3 point,
            out Terrain terrain, out TerrainData data, out float x, out float z,
            out CollisionReadStatus status)
        {
            terrain = null;
            data = null;
            x = z = 0;
            status = CollisionReadStatus.SurfaceUnavailable;
            var active = Terrain.activeTerrains;
            if (active is null) return false;
            if (active.WasCollected)
            { status = CollisionReadStatus.UnsupportedProfile; return false; }
            int count = active.Length;
            if (count > MaximumTerrainCandidates)
            { status = CollisionReadStatus.BufferFull; return false; }
            for (int i = 0; i < count; i++)
            {
                var candidate = active[i];
                if (!LiveBallisticObject(candidate) || !candidate.isActiveAndEnabled)
                { status = CollisionReadStatus.UnsupportedProfile; return false; }
                if (candidate.GetInstanceID() != terrainInstanceId) continue;
                if (terrain is not null)
                { status = CollisionReadStatus.UnsupportedProfile; return false; }
                terrain = candidate;
            }
            if (terrain is null) return CollisionChanged(out status);
            data = terrain.terrainData;
            if (!LiveBallisticObject(data)) return CollisionChanged(out status);
            var origin = terrain.GetPosition();
            var size = data.size;
            if (!Finite(origin) || !Finite(size) || size.x <= 0 || size.y <= 0 || size.z <= 0
                || !float.IsFinite(origin.x + size.x) || !float.IsFinite(origin.z + size.z)
                || point.X < origin.x || point.X > origin.x + size.x
                || point.Z < origin.z || point.Z > origin.z + size.z) return CollisionChanged(out status);

            // Bounds were checked first; clamping only absorbs float rounding at an included edge.
            x = (float)Math.Clamp(((double)point.X - origin.x) / size.x, 0d, 1d);
            z = (float)Math.Clamp(((double)point.Z - origin.z) / size.z, 0d, 1d);
            status = CollisionReadStatus.Success;
            return true;
        }

        private static bool TryCopyTerrainSurfaceNormal(UnityEngine.Vector3 value,
            out Vector3 normal, out float slope)
        {
            normal = default;
            slope = 0;
            if (!Finite(value)) return false;
            double length = Math.Sqrt((double)value.x * value.x + (double)value.y * value.y
                + (double)value.z * value.z);
            if (!double.IsFinite(length) || length <= 0) return false;
            double up = Math.Clamp(value.y / length, -1d, 1d);
            normal = new Vector3((float)(value.x / length), (float)up, (float)(value.z / length));
            slope = (float)(Math.Acos(up) * (180d / Math.PI));
            return Finite(normal) && float.IsFinite(slope);
        }
    }

    /// <summary>
    /// Copied loaded-heightmap orientation at the requested X/Z. Contains no native handles.
    /// World identity and Frame describe the read, not continued validity or heightmap immutability.
    /// A sample can exist above a cave or at a terrain hole; neither is classified by this read.
    /// </summary>
    public sealed class TerrainSurface
    {
        internal TerrainSurface(WorldRecordContext world, int frame,
            Vector3 position, int terrainInstanceId, Vector3 normal, float slopeDegrees)
        {
            WorldId = world.WorldId;
            WorldGeneration = world.Generation;
            Frame = frame;
            Position = position;
            TerrainInstanceId = terrainInstanceId;
            Normal = normal;
            SlopeDegrees = slopeDegrees;
        }

        public Guid WorldId { get; }
        public long WorldGeneration { get; }
        public int Frame { get; }
        /// <summary>Requested position. Only X and Z locate the heightmap sample; Y is ignored.</summary>
        public Vector3 Position { get; }
        /// <summary>Observation-time identity only; not a retained handle or mutation authority.</summary>
        public int TerrainInstanceId { get; }
        /// <summary>Unit world-space normal; only translation-only terrain transforms are accepted.</summary>
        public Vector3 Normal { get; }
        /// <summary>
        /// Angle from world up, in degrees from 0 to 180, derived from the sampled normal.
        /// This is not a separate GetSteepness read, collision normal or walkability decision.
        /// </summary>
        public float SlopeDegrees { get; }
    }

    /// <summary>
    /// Ordered, sequential heightmap orientation reads in one server world/frame.
    /// Complete coverage refers only to requested heightmap samples, never caves or meshes.
    /// </summary>
    public sealed class TerrainSurfaceBatch
    {
        internal TerrainSurfaceBatch(WorldRecordContext world, int frame,
            TerrainSurfaceSample[] samples, int sampledCount)
        {
            WorldId = world.WorldId;
            WorldGeneration = world.Generation;
            Frame = frame;
            Samples = Array.AsReadOnly(samples);
            SampledCount = sampledCount;
        }

        public Guid WorldId { get; }
        public long WorldGeneration { get; }
        public int Frame { get; }
        public IReadOnlyList<TerrainSurfaceSample> Samples { get; }
        public int SampledCount { get; }
        public bool HasCompleteCoverage => SampledCount == Samples.Count;
    }

    /// <summary>One requested point; unavailable orientation is null, never a flat-ground fallback.</summary>
    public sealed class TerrainSurfaceSample
    {
        internal TerrainSurfaceSample(Vector3 position, TerrainSurface surface,
            CollisionReadStatus status)
        { Position = position; Surface = surface; Status = status; }

        public Vector3 Position { get; }
        /// <summary>Success or SurfaceUnavailable. Unavailable does not establish empty space.</summary>
        public CollisionReadStatus Status { get; }
        /// <summary>Present only for Success. Requery for a new observation.</summary>
        public TerrainSurface Surface { get; }
        public Vector3? Normal => Surface?.Normal;
        public float? SlopeDegrees => Surface?.SlopeDegrees;
    }
}
