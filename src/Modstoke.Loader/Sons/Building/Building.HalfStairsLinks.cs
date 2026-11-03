// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Construction;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        private static bool NativeHalfStairsCornerMatches(Structure stairs, Structure back,
            Structure forward, Directions side, Directions end, bool converted)
        {
            int slot = (side == Directions.Left ? (int)WallDirections.LeftForwardWall
                : (int)WallDirections.RightForwardWall) + (end == Directions.Back ? 0 : 3);
            if (back._structures[slot] != forward
                || !NativeHalfStairsElementAt(back, 0, converted, out var backRow, out _, out _)
                || !NativeHalfStairsElementAt(forward, 0, converted, out var forwardRow, out _, out _)) return false;

            // The corner backlink uses the first element's frame, not the wall's frame.
            var local = forwardRow.transform.InverseTransformPoint(backRow.transform.position);
            if (converted)
            {
                // Deployment keeps position/rotation but replaces the local scale.
                // Re-express the source-local x in that projected element frame.
                var projectedScale = NativeHalfStairsConvertedScale(forwardRow);
                local.x = local.x * forwardRow.transform.localScale.x / projectedScale.x;
            }
            if (!FiniteVector(local)) return false;
            int stairsSlot = local.x < 0f ? (int)WallDirections.LeftBottomStructure
                : (int)WallDirections.RightBottomStructure;
            if (forward._structures[stairsSlot] != stairs
                || !NativeHalfStairsWallDirection(back, forward, out int backSlot)
                || !NativeHalfStairsWallDirection(forward, back, out int forwardSlot)
                || backSlot != slot || forward._structures[forwardSlot] != back) return false;

            // Native wall endpoints use the shared grid half-length, not element size or scale.
            float halfLength = GridSnapSize._HalfLength_k__BackingField;
            if (!float.IsFinite(halfLength)) return false;
            var backPosition = backRow.transform.position;
            var forwardPosition = forwardRow.transform.position;
            var backOffset = backRow.transform.forward * halfLength;
            var forwardOffset = forwardRow.transform.forward * halfLength;
            var a0 = backPosition - backOffset;
            var a1 = backPosition + backOffset;
            var b0 = forwardPosition - forwardOffset;
            var b1 = forwardPosition + forwardOffset;
            if (!FiniteVector(a0) || !FiniteVector(a1) || !FiniteVector(b0) || !FiniteVector(b1)) return false;
            return Vector3.Distance(a0, b0) < 0.1f || Vector3.Distance(a0, b1) < 0.1f
                || Vector3.Distance(a1, b0) < 0.1f || Vector3.Distance(a1, b1) < 0.1f;
        }

        private static bool NativeHalfStairsWallDirection(Structure from, Structure to, out int slot)
        {
            slot = -1;
            var fromTransform = from.transform;
            var point = to.transform.position + to.transform.forward;
            var fromPosition = fromTransform.position;
            var fromEnd = fromPosition + fromTransform.forward;
            var local = fromTransform.InverseTransformPoint(point);
            float backDistance = Vector3.Distance(fromPosition, point);
            float forwardDistance = Vector3.Distance(fromEnd, point);
            if (!FiniteVector(point) || !FiniteVector(fromEnd) || !FiniteVector(local)
                || !float.IsFinite(backDistance) || !float.IsFinite(forwardDistance)) return false;
            if (MathF.Abs(local.x) <= 0.05f)
                slot = local.z > 0f ? (int)WallDirections.ForwardWall : (int)WallDirections.BackWall;
            else
                slot = (forwardDistance > backDistance ? (int)WallDirections.LeftBackWall
                    : (int)WallDirections.LeftForwardWall) + (local.x > 0f ? 1 : 0);
            return true;
        }
    }
}
