// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Combines a qualified player point, loaded-water coverage and the selected
        /// native surface. Ocean treatment is explicit. Missing or ambiguous reads
        /// fail; they never become NoApplicableLoadedVolume or breathable air.
        /// </summary>
        public static bool TryReadPlayerWaterEnvironment(PlayerHandle binding,
            int waterLayerMask, int blockerLayerMask, string requiredTag,
            OceanWaterMode oceanMode, float minimumOceanSpatialLength,
            out PlayerWaterEnvironment snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            if (waterLayerMask == 0 || requiredTag is { Length: 0 }
                || oceanMode is not (OceanWaterMode.Include or OceanWaterMode.Ignore
                    or OceanWaterMode.IgnoreBelowTerrain)
                || !float.IsFinite(minimumOceanSpatialLength) || minimumOceanSpatialLength < 0)
            { status = CollisionReadStatus.InvalidQuery; return false; }
            if (!TryReadPlayerWaterline(binding, out var waterline, out status)
                || !TryReadWaterCoverage(waterline.Position, waterLayerMask, requiredTag, out var coverage, out status))
                return false;

            TerrainHeight terrain = null;
            OceanHeight ocean = null;
            WaterColumn water = null;
            bool useOcean = coverage.OceanVolumeCount > 0 && oceanMode != OceanWaterMode.Ignore;
            if (useOcean && oceanMode == OceanWaterMode.IgnoreBelowTerrain)
            {
                float threshold = waterline.Position.Y + 1f;
                if (!float.IsFinite(threshold))
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (!TryReadTerrainHeight(waterline.Position, out terrain, out status)) return false;
                // This reproduces the native height heuristic at the supplied point,
                // not cave membership, native overrides or the sensor's cached state.
                useOcean = terrain.Height <= threshold;
            }

            if (useOcean)
            {
                if (!TryReadOceanHeight(waterline.Position, minimumOceanSpatialLength, out ocean, out status)) return false;
            }
            else if (coverage.NonOceanVolumeCount > 0
                && !TryReadWaterVolume(waterline.Position, waterLayerMask, blockerLayerMask, requiredTag, out water, out status))
                return false;

            if (!IsCurrentWaterCoverage(coverage, out status)
                || (terrain != null && !IsCurrentTerrainHeight(terrain, out status))
                || (ocean != null && !IsCurrentOceanHeight(ocean, out status))
                || !IsCurrentPlayerWaterline(waterline, out status)) return false;
            if (coverage.Frame != waterline.Frame || (terrain != null && terrain.Frame != waterline.Frame)
                || (ocean != null && ocean.Frame != waterline.Frame)
                || (water != null && (water.Frame != waterline.Frame || !IsCurrentWaterColumn(water))))
            { status = CollisionReadStatus.StaleHit; return false; }

            snapshot = new PlayerWaterEnvironment(waterline, coverage, oceanMode, terrain, ocean, water);
            status = CollisionReadStatus.Success;
            return true;
        }
    }

    public enum OceanWaterMode
    {
        Include,
        Ignore,
        /// <summary>Exclude ocean when qualified terrain height is greater than point Y plus one metre.</summary>
        IgnoreBelowTerrain
    }

    public enum PlayerWaterRelation
    {
        NoApplicableLoadedVolume,
        AtOrAboveSurface,
        BelowSurface
    }

    /// <summary>
    /// A same-frame geometry observation under explicit layer, tag and ocean rules.
    /// It does not prove complete world loading, cave state, trusted movement or
    /// permission to restore oxygen or apply damage. Requery after native mutations.
    /// </summary>
    public sealed class PlayerWaterEnvironment
    {
        internal PlayerWaterEnvironment(PlayerWaterlineSnapshot waterline,
            WaterCoverage coverage, OceanWaterMode oceanMode,
            TerrainHeight terrain, OceanHeight ocean, WaterColumn water)
        {
            Waterline = waterline;
            Coverage = coverage;
            OceanMode = oceanMode;
            Terrain = terrain;
            Ocean = ocean;
            NonOceanSurface = water;
            SurfaceHeight = ocean?.Height ?? water?.Height;
            Relation = SurfaceHeight is not float height ? PlayerWaterRelation.NoApplicableLoadedVolume
                : waterline.Position.Y < height ? PlayerWaterRelation.BelowSurface
                : PlayerWaterRelation.AtOrAboveSurface;
        }

        public PlayerWaterlineSnapshot Waterline { get; }
        public WaterCoverage Coverage { get; }
        public OceanWaterMode OceanMode { get; }
        /// <summary>Present only when ocean eligibility required the terrain-height heuristic.</summary>
        public TerrainHeight Terrain { get; }
        public OceanHeight Ocean { get; }
        public WaterColumn NonOceanSurface { get; }
        public float? SurfaceHeight { get; }
        public PlayerWaterRelation Relation { get; }
    }
}
