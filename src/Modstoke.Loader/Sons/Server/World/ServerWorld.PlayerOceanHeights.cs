// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Pairs the remote player's native root-and-offset waterline with the CPU
        /// ocean surface height at that point. Rechecks both observations in the same
        /// frame. Success does not establish ocean presence, cave eligibility, wet/dry
        /// state, oxygen state or position authority.
        /// </summary>
        public static bool TryReadPlayerOceanHeight(PlayerHandle binding,
            float minimumSpatialLength, out PlayerOceanHeight snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            if (!float.IsFinite(minimumSpatialLength) || minimumSpatialLength < 0)
            { status = CollisionReadStatus.InvalidQuery; return false; }
            if (!TryReadPlayerWaterline(binding, out var waterline, out status)
                || !TryReadOceanHeight(waterline.Position, minimumSpatialLength, out var ocean, out status)
                || !IsCurrentPlayerWaterline(waterline, out status)
                || !IsCurrentOceanHeight(ocean, out status)) return false;
            if (ocean.Frame != waterline.Frame)
            { status = CollisionReadStatus.StaleHit; return false; }

            snapshot = new PlayerOceanHeight(waterline, ocean);
            status = CollisionReadStatus.Success;
            return true;
        }
    }

    /// <summary>
    /// One copied player-point and CPU ocean-surface observation. Requery for later
    /// frames or after native mutations; the native geometry is not frozen by this object.
    /// </summary>
    public sealed class PlayerOceanHeight
    {
        internal PlayerOceanHeight(PlayerWaterlineSnapshot waterline,
            OceanHeight ocean)
        { Waterline = waterline; Ocean = ocean; }

        public PlayerWaterlineSnapshot Waterline { get; }
        public OceanHeight Ocean { get; }

        /// <summary>
        /// Strictly compares the copied point Y with the CPU ocean surface height;
        /// equality is not below. This relation does not establish ocean presence,
        /// cave eligibility, wet/dry state, oxygen state or position authority.
        /// </summary>
        public bool IsBelowSurface => Waterline.Position.Y < Ocean.Height;
    }
}
