// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Construction;
using Il2CppInterop.Runtime;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        private static bool NativeSlopedFreeFormLinksMatch(Structure structure)
        {
            var type = structure.GetIl2CppType().Pointer;
            bool stairs = type == Il2CppType.Of<StairsStructure>().Pointer;
            if (!stairs && type != Il2CppType.Of<RampStructure>().Pointer) return true;
            // Stairs inherits Ramp's three links and Structure's transform-based ElementRoot.
            // CalcRampPositions dereferences both IRampSupport endpoints; native deployment
            // preserves these links rather than constructing missing supports for the author.
            var links = structure._structures;
            if (structure.TypeID != (stairs ? 6 : 5) || links == null || links.Length != 3
                || links[(int)RampDirections.SolarPanel]
                || !NativeRampSupportMatches(links[(int)RampDirections.Back], structure)
                || !NativeRampSupportMatches(links[(int)RampDirections.Forward], structure)) return false;

            // Both the authored ghost and its converted graph need a real element for the
            // inherited floor/ramp geometry callbacks. Do not invoke CountElements on assets.
            bool sourceHasRealElement = false, convertedHasRealElement = false;
            var root = structure.ElementRoot;
            for (int i = 0; i < root.childCount; i++)
            {
                var element = root.GetChild(i).GetComponent<StructureElement>();
                sourceHasRealElement |= !element._profile._ignoreInElementsCount;
                convertedHasRealElement |= !element._profile.Prefab._profile._ignoreInElementsCount;
            }
            return sourceHasRealElement && convertedHasRealElement;
        }

        private static bool NativeRampSupportMatches(Structure support, Structure ramp)
        {
            if (!support) return false;
            var type = support.GetIl2CppType().Pointer;
            var links = support._structures;
            // The native IRampSupport implementations map Left/Right to these slots, not
            // Directions.Left/Right or WallDirections.LeftFloor/RightFloor. Read the authored
            // arrays directly: setters and support/placement queries can mutate native state.
            if (type == Il2CppType.Of<BeamStructure>().Pointer)
                return links != null && links.Length == 16
                    && (links[(int)BeamDirections.LeftFloor] == ramp || links[(int)BeamDirections.RightFloor] == ramp);
            if (type == Il2CppType.Of<WallStructure>().Pointer)
                return links != null && links.Length == 18
                    && (links[(int)WallDirections.LeftBottomStructure] == ramp || links[(int)WallDirections.RightBottomStructure] == ramp);
            return false;
        }
    }
}
