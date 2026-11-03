// Copyright (c) 2026 Alex Cooper. MIT license.
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PatchPriorityKey
    {
        internal int Index, Priority;
    }

    private static class PrioritySortExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<PatchPriorityKey*, uint, uint*, uint, uint> Sort =
            (delegate* unmanaged[Cdecl]<PatchPriorityKey*, uint, uint*, uint, uint>)Export("mh_prioritysort_v1");
    }

    internal static uint[] SortPatchPriorities(PatchPriorityKey[] keys)
    {
        var order = new uint[keys.Length];
        fixed (PatchPriorityKey* nativeKeys = keys)
        fixed (uint* nativeOrder = order)
            Check(PrioritySortExports.Sort(nativeKeys, (uint)keys.Length, nativeOrder,
                (uint)order.Length), "patch priority ordering");
        foreach (var ordinal in order)
            if (ordinal >= keys.Length)
                throw new RustBackendException("Modstoke Harmony received an invalid priority-sort ordinal.");
        return order;
    }
}
