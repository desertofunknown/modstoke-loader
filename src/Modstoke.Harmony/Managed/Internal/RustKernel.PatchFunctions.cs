// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum ReversePatchAction : uint
    {
        FetchSnapshot,
        AppendSnapshotTranspilers,
        AppendSnapshotManipulators,
        AppendPostTranspiler,
        AppendPostManipulator
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnpatchRange
    {
        internal uint Category, Count;
    }

    private static class PatchFunctionsNative
    {
        internal static readonly delegate* unmanaged[Cdecl]<int, uint, ReversePatchAction*, uint, uint*, uint> ReversePlan =
            (delegate* unmanaged[Cdecl]<int, uint, ReversePatchAction*, uint, uint*, uint>)Export("mh_patchfunctions_reverse_plan_v1");
        internal static readonly delegate* unmanaged[Cdecl]<uint*, uint, UnpatchRange*, uint, uint> UnpatchRanges =
            (delegate* unmanaged[Cdecl]<uint*, uint, UnpatchRange*, uint, uint>)Export("mh_patchfunctions_unpatch_ranges_v1");
    }

    internal static int PlanReversePatch(HarmonyReversePatchType? reverseType, bool postTranspiler,
        bool postManipulator, Span<ReversePatchAction> actions)
    {
        var callbacks = (postTranspiler ? 1u : 0u) | (postManipulator ? 2u : 0u);
        uint written = 0;
        fixed (ReversePatchAction* output = actions)
            Check(PatchFunctionsNative.ReversePlan((int)(reverseType ?? HarmonyReversePatchType.Original),
                callbacks, output, (uint)actions.Length, &written), "reverse patch setup");
        if (written > actions.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid reverse patch plan length.");
        return (int)written;
    }

    internal static void PlanConditionalUnpatch(ReadOnlyCollection<Patch>[] categories, Span<UnpatchRange> ranges)
    {
        if (categories.Length != 5 || ranges.Length != 5)
            throw new ArgumentException("Conditional unpatch requires five patch categories and ranges.");
        Span<uint> counts = stackalloc uint[5];
        for (var i = 0; i < categories.Length; i++)
            counts[i] = (uint)categories[i].Count;
        fixed (uint* input = counts)
        fixed (UnpatchRange* output = ranges)
            Check(PatchFunctionsNative.UnpatchRanges(input, (uint)counts.Length,
                output, (uint)ranges.Length), "conditional unpatch traversal");
    }
}
