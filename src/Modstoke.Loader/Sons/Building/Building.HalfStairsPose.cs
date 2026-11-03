// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Construction;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        private static bool NativeHalfStairsPoseMatches(Structure wall, Structure stairs,
            StructureElement prefab, Directions side, Directions end, bool bottom, bool converted)
        {
            if (!prefab || !prefab._profile
                || (side != Directions.Left && side != Directions.Right)
                || (end != Directions.Back && end != Directions.Forward)
                || !NativeHalfStairsElementAt(wall, bottom ? 0 : 2, converted,
                    out var element, out var profile, out var size)) return false;
            var wallTransform = wall.transform;
            var stairsTransform = stairs.transform;
            var rowTransform = element.transform;

            var prefabSize = prefab._size;
            float prefabOffset = prefab._profile._extremityToSnapPositionOffset;
            float rowOffset = profile._extremityToSnapPositionOffset;
            var rowPosition = rowTransform.position;
            var actualPosition = stairsTransform.position;
            var right = wallTransform.right;
            var axis = wallTransform.forward * (end == Directions.Back ? 1f : -1f);
            if (!FiniteVector(prefabSize) || !float.IsFinite(prefabOffset)
                || !FiniteVector(right) || !FiniteVector(axis)) return false;
            var lookRight = Vector3.Cross(Vector3.up, axis);
            if (!FiniteVector(lookRight) || right.sqrMagnitude <= 0f
                || axis.sqrMagnitude <= 0f || lookRight.sqrMagnitude <= 0f) return false;

            float width = prefabOffset + prefabSize.z * 0.5f;
            var offset = right * (width * (side == Directions.Right ? 1f : -1f));
            // The predicted row size already includes ApplyLengthScale. Native endpoints
            // rotate this distance without applying the source transform's scale again.
            var rowAxis = rowTransform.TransformDirection(new Vector3(0f, 0f,
                size.z * 0.5f - rowOffset));
            var position = (end == Directions.Back ? rowPosition - rowAxis : rowPosition + rowAxis) + offset;
            position.y -= size.y * 0.5f;
            if (!FiniteVector(offset) || !FiniteVector(rowAxis) || !FiniteVector(position)) return false;

            // Placement uses the wall axis and world up, not the top-to-snap slope.
            var rotation = Quaternion.LookRotation(axis, Vector3.up);
            return (actualPosition - position).sqrMagnitude <= 0.00000001f
                && NativeHalfStairsRotationMatches(stairsTransform.rotation, rotation);
        }
    }
}
