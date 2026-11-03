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
        public const int MaximumTerrainCandidates = 64;
        public const int MaximumTerrainHeightSamples = 64;

        /// <summary>
        /// Reads loaded heightmap geometry from exactly one containing, translation-only terrain.
        /// Holes and cave meshes remain unclassified; this is not cave membership, ocean
        /// eligibility or dryness. Uses three shared collision-read budget units.
        /// </summary>
        public static bool TryReadTerrainHeight(Vector3 position, out TerrainHeight snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!Finite(position))
                { status = CollisionReadStatus.InvalidQuery; return false; }
                // Reserve both bounded candidate scans and the single native height sample.
                for (int i = 0; i < 3; i++)
                    if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!TerrainHeightObservation.TryCapture(world, frame, position, out var before, out status))
                    return false;
                if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                float relativeHeight = before.SampleHeight(position);
                if (!TerrainHeightObservation.TryCapture(world, frame, position, out var after, out status))
                    return false;
                if (!before.Matches(after) || !CollisionContextCurrent(world, frame))
                    return CollisionChanged(out status);
                if (!before.TryAbsoluteHeight(relativeHeight, out float height))
                { status = CollisionReadStatus.SurfaceUnavailable; return false; }
                snapshot = new TerrainHeight(before, position, height);
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
        /// Reads 1 to MaximumTerrainHeightSamples points in input order in one server world/frame.
        /// True means the batch completed, not that every point has coverage: each sample is
        /// Success with a height or SurfaceUnavailable with no height. All other failures return
        /// no batch, never a truncated prefix. Empty, oversized and nonfinite inputs are rejected
        /// before terrain reads. Uses three shared collision-read budget units per attempted point.
        /// A failed batch may already have consumed budget; retry in a later frame.
        /// Reads are sequential, not a frozen heightmap; holes, caves, water and traversability
        /// remain unclassified. Requery after native terrain changes or a world/frame transition.
        /// </summary>
        public static bool TryReadTerrainHeights(ReadOnlySpan<Vector3> positions,
            out TerrainHeightBatch snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (positions.Length == 0 || positions.Length > MaximumTerrainHeightSamples)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                // Keep a private, bounded copy across the sequential native reads.
                Span<Vector3> points = stackalloc Vector3[positions.Length];
                positions.CopyTo(points);
                foreach (var point in points)
                    if (!Finite(point))
                    { status = CollisionReadStatus.InvalidQuery; return false; }

                var samples = new TerrainHeightSample[points.Length];
                int sampledCount = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                    if (TryReadTerrainHeight(points[i], out var height, out status))
                    {
                        if (!ReferenceEquals(world, height.World) || frame != height.Frame)
                            return CollisionChanged(out status);
                        sampledCount++;
                    }
                    else if (status != CollisionReadStatus.SurfaceUnavailable) return false;
                    samples[i] = new TerrainHeightSample(points[i], height, status);
                }
                if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                snapshot = new TerrainHeightBatch(frame, samples, sampledCount);
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

        /// <summary>
        /// Repeats the complete read in the original world and frame, comparing candidate
        /// identities, configuration and the sampled height. Identity does not freeze
        /// heightmap contents. Uses three shared collision-read budget units.
        /// </summary>
        public static bool IsCurrentTerrainHeight(TerrainHeight snapshot,
            out CollisionReadStatus status)
        {
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (snapshot == null)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (!ReferenceEquals(world, snapshot.World) || frame != snapshot.Frame)
                { status = CollisionReadStatus.StaleHit; return false; }
                if (!TryReadTerrainHeight(snapshot.Position, out var current, out status)) return false;
                _collisionReading = entered = true;
                if (!snapshot.Observation.Matches(current.Observation)
                    || !TerrainHeightObservation.SameFloat(snapshot.Height, current.Height)
                    || !CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
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

        internal sealed class TerrainHeightObservation
        {
            private readonly TerrainCandidate[] _candidates;
            private readonly TerrainCandidate _selected;

            private TerrainHeightObservation(WorldRecordContext world, int frame,
                TerrainCandidate[] candidates, TerrainCandidate selected)
            { World = world; Frame = frame; _candidates = candidates; _selected = selected; }

            internal WorldRecordContext World { get; }
            internal int Frame { get; }
            internal int TerrainInstanceId => _selected.TerrainId;

            internal float SampleHeight(Vector3 position)
                => _selected.Terrain.SampleHeight(new UnityEngine.Vector3(position.X, position.Y, position.Z));

            internal bool TryAbsoluteHeight(float relative, out float height)
            {
                height = relative + _selected.Position.y;
                return float.IsFinite(relative) && relative >= 0 && relative <= _selected.Size.y
                    && float.IsFinite(height);
            }

            internal static bool TryCapture(WorldRecordContext world, int frame, Vector3 point,
                out TerrainHeightObservation observation, out CollisionReadStatus status)
            {
                observation = null;
                status = CollisionReadStatus.SurfaceUnavailable;
                var active = Terrain.activeTerrains;
                if (active is null) return false;
                if (active.WasCollected)
                { status = CollisionReadStatus.UnsupportedProfile; return false; }
                int count = active.Length;
                if (count == 0) return false;
                if (count > MaximumTerrainCandidates)
                { status = CollisionReadStatus.BufferFull; return false; }
                var candidates = new TerrainCandidate[count];
                TerrainCandidate selected = null;
                for (int i = 0; i < count; i++)
                {
                    if (!TerrainCandidate.TryCapture(active[i], out var candidate))
                    { status = CollisionReadStatus.UnsupportedProfile; return false; }
                    for (int j = 0; j < i; j++)
                        if (SameBallisticObject(candidate.Terrain, candidates[j].Terrain)
                            || candidate.TerrainId == candidates[j].TerrainId)
                        { status = CollisionReadStatus.UnsupportedProfile; return false; }
                    candidates[i] = candidate;
                    if (!candidate.Contains(point)) continue;
                    if (selected != null)
                    { status = CollisionReadStatus.UnsupportedProfile; return false; }
                    selected = candidate;
                }
                if (!CollisionContextCurrent(world, frame)) return CollisionChanged(out status);
                if (selected == null) return false;
                observation = new TerrainHeightObservation(world, frame, candidates, selected);
                status = CollisionReadStatus.Success;
                return true;
            }

            internal bool Matches(TerrainHeightObservation other)
            {
                if (!ReferenceEquals(World, other.World) || Frame != other.Frame
                    || _candidates.Length != other._candidates.Length || !_selected.Matches(other._selected))
                    return false;
                foreach (var candidate in _candidates)
                {
                    bool found = false;
                    foreach (var current in other._candidates)
                        if (candidate.Matches(current)) { found = true; break; }
                    if (!found) return false;
                }
                return true;
            }

            internal static bool SameFloat(float first, float second)
                => BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second);

            private static bool SameVector(UnityEngine.Vector3 first, UnityEngine.Vector3 second)
                => SameFloat(first.x, second.x) && SameFloat(first.y, second.y) && SameFloat(first.z, second.z);

            private sealed class TerrainCandidate
            {
                internal readonly Terrain Terrain;
                internal readonly int TerrainId;
                internal readonly UnityEngine.Vector3 Position, Size;
                private readonly TerrainData _data;
                private readonly Transform _transform;
                private readonly int _dataId, _transformId;
                private readonly UnityEngine.Matrix4x4 _matrix;

                private TerrainCandidate(Terrain terrain, TerrainData data, Transform transform,
                    UnityEngine.Vector3 position, UnityEngine.Vector3 size, UnityEngine.Matrix4x4 matrix)
                {
                    Terrain = terrain; TerrainId = terrain.GetInstanceID();
                    _data = data; _dataId = data.GetInstanceID();
                    _transform = transform; _transformId = transform.GetInstanceID();
                    Position = position; Size = size; _matrix = matrix;
                }

                internal static bool TryCapture(Terrain terrain, out TerrainCandidate candidate)
                {
                    candidate = null;
                    if (!LiveBallisticObject(terrain) || !terrain.isActiveAndEnabled) return false;
                    var data = terrain.terrainData;
                    var transform = terrain.transform;
                    if (!LiveBallisticObject(data) || !LiveBallisticObject(transform)) return false;
                    var position = terrain.GetPosition();
                    var size = data.size;
                    var transformPosition = transform.position;
                    var matrix = transform.localToWorldMatrix;
                    if (!Finite(position) || !Finite(size) || !Finite(transformPosition)
                        || size.x <= 0 || size.y <= 0 || size.z <= 0
                        || !float.IsFinite(position.x + size.x) || !float.IsFinite(position.y + size.y)
                        || !float.IsFinite(position.z + size.z)
                        || !SameVector(position, transformPosition)
                        || matrix.m00 != 1 || matrix.m11 != 1 || matrix.m22 != 1 || matrix.m33 != 1
                        || matrix.m01 != 0 || matrix.m02 != 0 || matrix.m10 != 0 || matrix.m12 != 0
                        || matrix.m20 != 0 || matrix.m21 != 0 || matrix.m30 != 0 || matrix.m31 != 0
                        || matrix.m32 != 0 || !SameFloat(matrix.m03, position.x)
                        || !SameFloat(matrix.m13, position.y) || !SameFloat(matrix.m23, position.z)) return false;
                    candidate = new TerrainCandidate(terrain, data, transform, position, size, matrix);
                    return true;
                }

                internal bool Contains(Vector3 point)
                    => point.X >= Position.x && point.X <= Position.x + Size.x
                        && point.Z >= Position.z && point.Z <= Position.z + Size.z;

                internal bool Matches(TerrainCandidate other)
                {
                    if (!SameBallisticObject(Terrain, other.Terrain) || TerrainId != other.TerrainId
                        || !SameBallisticObject(_data, other._data) || _dataId != other._dataId
                        || !SameBallisticObject(_transform, other._transform) || _transformId != other._transformId
                        || !SameVector(Position, other.Position) || !SameVector(Size, other.Size)) return false;
                    var matrix = other._matrix;
                    return SameFloat(_matrix.m00, matrix.m00) && SameFloat(_matrix.m01, matrix.m01)
                        && SameFloat(_matrix.m02, matrix.m02) && SameFloat(_matrix.m03, matrix.m03)
                        && SameFloat(_matrix.m10, matrix.m10) && SameFloat(_matrix.m11, matrix.m11)
                        && SameFloat(_matrix.m12, matrix.m12) && SameFloat(_matrix.m13, matrix.m13)
                        && SameFloat(_matrix.m20, matrix.m20) && SameFloat(_matrix.m21, matrix.m21)
                        && SameFloat(_matrix.m22, matrix.m22) && SameFloat(_matrix.m23, matrix.m23)
                        && SameFloat(_matrix.m30, matrix.m30) && SameFloat(_matrix.m31, matrix.m31)
                        && SameFloat(_matrix.m32, matrix.m32) && SameFloat(_matrix.m33, matrix.m33);
                }
            }
        }
    }

    /// <summary>
    /// Copied absolute loaded-heightmap height. Holes and cave meshes are unclassified.
    /// Native identities and configuration do not establish immutable terrain contents.
    /// </summary>
    public sealed class TerrainHeight
    {
        internal TerrainHeight(ServerWorld.TerrainHeightObservation observation,
            Vector3 position, float height)
        { Observation = observation; Position = position; Height = height; }

        internal ServerWorld.TerrainHeightObservation Observation { get; }
        internal WorldRecordContext World => Observation.World;
        public int Frame => Observation.Frame;
        public Vector3 Position { get; }
        public float Height { get; }
        public int TerrainInstanceId => Observation.TerrainInstanceId;
    }

    /// <summary>
    /// A complete, ordered batch of sequential heightmap observations in one world/frame.
    /// Complete coverage means qualified height samples only, not walkable or dry ground.
    /// </summary>
    public sealed class TerrainHeightBatch
    {
        internal TerrainHeightBatch(int frame, TerrainHeightSample[] samples,
            int sampledCount)
        { Frame = frame; Samples = Array.AsReadOnly(samples); SampledCount = sampledCount; }

        public int Frame { get; }
        public IReadOnlyList<TerrainHeightSample> Samples { get; }
        public int SampledCount { get; }
        public bool HasCompleteCoverage => SampledCount == Samples.Count;
    }

    /// <summary>One requested point. An unavailable height is null, never a zero-height fallback.</summary>
    public sealed class TerrainHeightSample
    {
        internal TerrainHeightSample(Vector3 position, global::Modstoke.Sons.TerrainHeight terrainHeight,
            CollisionReadStatus status)
        { Position = position; TerrainHeight = terrainHeight; Status = status; }

        public Vector3 Position { get; }
        /// <summary>Success or SurfaceUnavailable. The latter does not establish empty space.</summary>
        public CollisionReadStatus Status { get; }
        /// <summary>Present only for Success; can be rechecked with IsCurrentTerrainHeight.</summary>
        public global::Modstoke.Sons.TerrainHeight TerrainHeight { get; }
        public float? Height => TerrainHeight?.Height;
    }
}
