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
        private static bool NativeHalfStairsFreeFormMatches(Structure structure)
        {
            if (structure.GetIl2CppType().Pointer != Il2CppType.Of<HalfStairsStructure>().Pointer) return true;
            var links = structure._structures;
            if (links == null || links.Length != 2 || !NativeHalfStairsFrameMatches(structure.transform)
                || !NativeHalfStairsWallMatches(links[0]) || !NativeHalfStairsWallMatches(links[1])) return false;
            var back = links[0];
            var forward = links[1];
            for (int view = 0; view < 2; view++)
            {
                bool converted = view != 0;
                var template = NativeHalfStairsTemplateFor(structure, converted);
                if (!NativeHalfStairsTemplateMatches(structure, template, converted)
                    || !NativeHalfStairsCompleted(back, converted)
                    || (back != forward && !NativeHalfStairsCompleted(forward, converted))) return false;
                var rows = back.ElementRoot;
                for (int i = 0; i < rows.childCount; i++)
                {
                    if (!NativeHalfStairsElementAt(back, i, converted, out _, out var profile, out _)
                        || !profile._middleHoleProfile) return false;
                }
            }

            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                var side = sideIndex == 0 ? Directions.Left : Directions.Right;
                int slot = side == Directions.Left
                    ? (int)WallDirections.LeftBottomStructure : (int)WallDirections.RightBottomStructure;
                if (back._structures[slot] != structure) continue;
                for (int endIndex = 0; endIndex < 2; endIndex++)
                {
                    var end = endIndex == 0 ? Directions.Back : Directions.Forward;
                    if (NativeHalfStairsCandidateMatches(structure, back, forward, side, end, false)
                        && NativeHalfStairsCandidateMatches(structure, back, forward, side, end, true)) return true;
                }
            }
            return false;
        }

        private static bool NativeHalfStairsCandidateMatches(Structure stairs, Structure back,
            Structure forward, Directions side, Directions end, bool converted)
        {
            if (!NativeHalfStairsElementAt(stairs, 0, converted, out _, out var profile, out _)) return false;
            var prefab = profile.Prefab;
            if (!prefab || prefab.gameObject.scene.IsValid()
                || !NativeHalfStairsContinuityMatches(back, side, end, converted, out bool bottom)
                || !NativeHalfStairsPoseMatches(back, stairs, prefab, side, end, bottom, converted)) return false;
            return back == forward || NativeHalfStairsCornerMatches(stairs, back, forward, side, end, converted);
        }

        private static bool NativeHalfStairsWallMatches(Structure structure)
        {
            return structure && structure.GetIl2CppType().Pointer == Il2CppType.Of<WallStructure>().Pointer
                && structure._structures != null && structure._structures.Length == 18
                && structure.ElementRoot == structure.transform && NativeHalfStairsFrameMatches(structure.transform);
        }

        private static PresetElementsTemplate NativeHalfStairsTemplateFor(Structure structure, bool converted)
        {
            var links = structure._structures;
            if (links == null || links.Length != 2 || !NativeHalfStairsWallMatches(links[0])
                || !NativeHalfStairsWallMatches(links[1]) || !NativeHalfStairsFrameMatches(structure.transform)
                || !NativeHalfStairsElementAt(structure, 0, converted, out _, out var profile, out _)) return null;
            var wall = links[0].transform;
            var position = structure.transform.position;
            var local = wall.InverseTransformPoint(position);
            float alignment = Vector3.Dot(wall.forward, structure.transform.forward);
            float height = MathF.Abs(wall.position.y - position.y);
            if (!FiniteVector(local) || !float.IsFinite(alignment) || !float.IsFinite(height)) return null;
            bool clockwise = local.x > 0f;
            if (!(alignment > 0f)) clockwise = !clockwise;
            return NativeHalfStairsTemplate(profile, clockwise, height < 0.5f, links[0] != links[1]);
        }

        private static bool NativeHalfStairsElementAt(Structure owner, int index, bool converted,
            out StructureElement element, out ElementProfile profile, out Vector3 size)
        {
            element = null;
            profile = null;
            size = default;
            var root = owner.ElementRoot;
            if (!root || index < 0 || index >= root.childCount) return false;
            element = root.GetChild(index).GetComponent<StructureElement>();
            if (!element || !element._profile || !NativeHalfStairsFrameMatches(element.transform)) return false;
            profile = element._profile;
            size = element._size;
            if (converted)
            {
                var prefab = profile.Prefab;
                if (!prefab || !prefab._profile || prefab.gameObject.scene.IsValid()) return false;
                profile = prefab._profile;
                size = prefab._size;
                var projectedScale = NativeHalfStairsConvertedScale(element);
                if (!FiniteVector(projectedScale) || projectedScale.x == 0f
                    || projectedScale.y == 0f || projectedScale.z == 0f) return false;
                float scale = element.transform.localScale.z;
                if (!float.IsFinite(scale) || scale <= 0f) return false;
                if (MathF.Abs(scale - 1f) > 0.001f)
                {
                    var sizeSource = profile.Prefab;
                    if (!sizeSource || sizeSource.gameObject.scene.IsValid()) return false;
                    size = sizeSource._size;
                    float ends = 2f * profile._extremityToSnapPositionOffset;
                    size.z = (size.z - ends) * scale + ends;
                }
            }
            return FiniteVector(size) && float.IsFinite(profile._extremityToSnapPositionOffset);
        }

        private static Vector3 NativeHalfStairsConvertedScale(StructureElement element)
        {
            float lengthScale = element.transform.localScale.z;
            return MathF.Abs(lengthScale - 1f) > 0.001f
                ? new Vector3(1f, 1f, lengthScale) : element._profile.Prefab.transform.localScale;
        }

        private static bool NativeHalfStairsCompleted(Structure structure, bool converted)
        {
            var type = structure.GetIl2CppType().Pointer;
            int maximum;
            if (type == Il2CppType.Of<WallStructure>().Pointer)
            {
                if (!NativeHalfStairsWallMatches(structure)) return false;
                var above = structure._structures[(int)WallDirections.AboveStructure];
                if (above && above.GetIl2CppType().Pointer == Il2CppType.Of<BeamStructure>().Pointer) return true;
                maximum = 5;
            }
            else if (type == Il2CppType.Of<HalfStairsStructure>().Pointer)
            {
                var template = NativeHalfStairsTemplateFor(structure, converted);
                if (!template || template.gameObject.scene.IsValid() || template._elementsTr == null) return false;
                maximum = template._elementsTr.Length;
            }
            else return false;

            var root = structure.ElementRoot;
            if (!root) return false;
            int count = root.childCount;
            // Native CountElements trims only the trailing ignored run, not every ignored row.
            while (count > 0)
            {
                if (!NativeHalfStairsElementAt(structure, count - 1, converted, out _, out var profile, out _)) return false;
                if (!profile._ignoreInElementsCount) break;
                count--;
            }
            return count != 0 && count >= maximum;
        }

        private static bool NativeHalfStairsFrameMatches(Transform transform)
        {
            if (!transform || !FiniteVector(transform.position)) return false;
            var scale = transform.lossyScale;
            float norm = Quaternion.Dot(transform.rotation, transform.rotation);
            return FiniteVector(scale) && scale.x != 0f && scale.y != 0f && scale.z != 0f
                && float.IsFinite(norm) && MathF.Abs(norm - 1f) < 0.001f;
        }

        private static PresetElementsTemplate NativeHalfStairsTemplate(ElementProfile profile,
            bool clockwise, bool bottom, bool curved)
        {
            if (!profile || profile.GetIl2CppType().Pointer != Il2CppType.Of<HalfStairsElementProfile>().Pointer)
                return null;
            var stairs = profile.Cast<HalfStairsElementProfile>();
            var group = clockwise
                ? (bottom ? stairs._clockwiseBottom : stairs._clockwiseTop)
                : (bottom ? stairs._counterClockwiseBottom : stairs._counterClockwiseTop);
            return group == null ? null : curved ? group._curved : group._straight;
        }

        private static bool NativeHalfStairsTemplateMatches(Structure structure,
            PresetElementsTemplate template, bool converted)
        {
            if (!template || template.gameObject.scene.IsValid()) return false;
            var slots = template._elementsTr;
            var overrides = template._elementsOverride;
            var root = structure.ElementRoot;
            if (root != structure.transform || slots == null || overrides == null
                || slots.Length != root.childCount) return false;

            for (int i = 0; i < slots.Length; i++)
            {
                var expected = slots[i];
                if (!expected || expected.gameObject.scene.IsValid()) return false;
                var authored = root.GetChild(i);
                if (!NativeHalfStairsElementAt(structure, i, converted, out var element, out var profile, out _)) return false;
                if (i == slots.Length - 1 && profile._ignoreInElementsCount) return false;
                var scale = converted ? NativeHalfStairsConvertedScale(element) : authored.localScale;

                // Native template coordinates are relative to the structure, not the
                // template object's authoring transform. Quaternions may differ in sign.
                var position = authored.localPosition;
                var expectedPosition = expected.localPosition;
                var expectedScale = expected.localScale;
                if (!FiniteVector(position) || !FiniteVector(expectedPosition)
                    || !FiniteVector(scale) || !FiniteVector(expectedScale)
                    || (position - expectedPosition).sqrMagnitude > 0.00000001f
                    || (scale - expectedScale).sqrMagnitude > 0.00000001f
                    || !NativeHalfStairsRotationMatches(authored.localRotation, expected.localRotation)) return false;

                // WorldTrInfo accepts a short override array, but dereferences every
                // present entry even though HalfStairs instantiates the passed prefab.
                if (i < overrides.Length && (!overrides[i] || !overrides[i]._profile)) return false;
            }
            return true;
        }

        private static bool NativeHalfStairsRotationMatches(Quaternion actual, Quaternion expected)
        {
            float actualNorm = Quaternion.Dot(actual, actual);
            float expectedNorm = Quaternion.Dot(expected, expected);
            float alignment = MathF.Abs(Quaternion.Dot(actual, expected));
            return float.IsFinite(actualNorm) && float.IsFinite(expectedNorm) && float.IsFinite(alignment)
                && MathF.Abs(actualNorm - 1f) < 0.001f && MathF.Abs(expectedNorm - 1f) < 0.001f
                && alignment * alignment > 0.99998f * actualNorm * expectedNorm;
        }
    }
}
