// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Construction;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        private static bool NativeHalfStairsContinuityMatches(Structure wall, Directions side,
            Directions end, bool converted, out bool bottom)
        {
            bottom = false;
            if (!wall || wall.GetIl2CppType().Pointer != Il2CppType.Of<WallStructure>().Pointer
                || (side != Directions.Left && side != Directions.Right)
                || (end != Directions.Back && end != Directions.Forward)
                || !NativeHalfStairsFrameMatches(wall.transform)) return false;
            var forward = wall.transform.forward;
            if (!FiniteVector(forward)) return false;
            var endForward = forward * (end == Directions.Back ? 1f : -1f);
            bool isBottom = true;

            // The outer graph gate validates exact types, membership and link arrays first.
            // Only Wall implements IHalfStairsSupport among those admitted types.
            for (int i = 0; i < 2; i++)
            {
                var support = wall._structures[i == 0
                    ? (int)WallDirections.BackWall : (int)WallDirections.ForwardWall];
                if (!support || support.GetIl2CppType().Pointer != Il2CppType.Of<WallStructure>().Pointer)
                    continue;
                var supportTransform = support.transform;
                if (!NativeHalfStairsFrameMatches(supportTransform)) return false;
                var supportForward = supportTransform.forward;
                float alignment = Vector3.Dot(forward, supportForward);
                if (!FiniteVector(supportForward) || !float.IsFinite(alignment)) return false;

                // This side change persists even when the selected stair lookup is empty.
                if (alignment < 0)
                    side = side == Directions.Left ? Directions.Right : Directions.Left;
                var stairs = support._structures[side == Directions.Right
                    ? (int)WallDirections.RightBottomStructure : (int)WallDirections.LeftBottomStructure];
                if (!stairs || stairs.GetIl2CppType().Pointer != Il2CppType.Of<HalfStairsStructure>().Pointer)
                    continue;
                var stairsTransform = stairs.transform;
                if (!NativeHalfStairsFrameMatches(stairsTransform)) return false;
                var stairsForward = stairsTransform.forward;
                float direction = Vector3.Dot(endForward, stairsForward);
                if (!FiniteVector(stairsForward) || !float.IsFinite(direction)) return false;

                if (direction < 0)
                {
                    float height = MathF.Abs(supportTransform.position.y - stairsTransform.position.y);
                    if (!float.IsFinite(height)) return false;
                    // A bottom-height continuation neither requires completion nor resets isBottom.
                    if (height < 0.5f) continue;
                }
                else
                {
                    var local = stairsTransform.InverseTransformPoint(supportTransform.position);
                    // Exact HalfStairs is not platform mode; HalfFloor is outside the graph gate.
                    if (!FiniteVector(local) || local.z <= 0) return false;
                }

                if (!NativeHalfStairsCompleted(stairs, converted)) return false;
                isBottom = false;
            }
            bottom = isBottom;
            return true;
        }
    }
}
