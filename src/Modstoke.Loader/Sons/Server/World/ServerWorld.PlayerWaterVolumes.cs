// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Pairs the remote player's native root-and-offset waterline with a containing
        /// non-ocean volume. Rechecks the player after the water query. A failed read
        /// never establishes dry land; success is geometry, not native swimming state,
        /// trusted movement, equipment ownership or permission to apply drowning.
        /// </summary>
        public static bool TryReadPlayerWaterVolume(PlayerHandle binding,
            int waterLayerMask, int blockerLayerMask, string requiredTag,
            out PlayerWaterVolume snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            if (waterLayerMask == 0 || requiredTag is { Length: 0 })
            { status = CollisionReadStatus.InvalidQuery; return false; }
            if (!TryReadPlayerWaterline(binding, out var waterline, out status)
                || !TryReadWaterVolume(waterline.Position, waterLayerMask, blockerLayerMask,
                    requiredTag, out var water, out status)
                || !IsCurrentPlayerWaterline(waterline, out status)) return false;
            if (water.Frame != waterline.Frame || !IsCurrentWaterColumn(water))
            { status = CollisionReadStatus.StaleHit; return false; }

            snapshot = new PlayerWaterVolume(waterline, water);
            status = CollisionReadStatus.Success;
            return true;
        }
    }

    /// <summary>
    /// One paired player-point and water-surface observation. Requery for later frames
    /// or after native mutations; the water geometry is not frozen by this object.
    /// </summary>
    public sealed class PlayerWaterVolume
    {
        internal PlayerWaterVolume(PlayerWaterlineSnapshot waterline,
            WaterColumn water)
        { Waterline = waterline; Water = water; }

        public PlayerWaterlineSnapshot Waterline { get; }
        public WaterColumn Water { get; }

        /// <summary>
        /// Strictly compares the copied point Y with the copied surface Y. Equality
        /// is not below. False does not establish dry land or native swimming state.
        /// </summary>
        public bool IsBelowSurface => Waterline.Position.Y < Water.Height;
    }
}
