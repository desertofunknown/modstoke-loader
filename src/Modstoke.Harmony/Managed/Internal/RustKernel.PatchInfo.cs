// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Reflection;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    private static class PatchInfoNative
    {
        internal static readonly delegate* unmanaged[Cdecl]<uint, uint, int*, uint, uint> AppendIndices =
            (delegate* unmanaged[Cdecl]<uint, uint, int*, uint, uint>)Export("mh_patchinfo_append_indices_v1");
        internal static readonly delegate* unmanaged[Cdecl]<uint*, uint, uint, uint, uint*, uint, uint*, uint> Filter =
            (delegate* unmanaged[Cdecl]<uint*, uint, uint, uint, uint*, uint, uint*, uint>)Export("mh_patchinfo_filter_v1");
        internal static readonly int[] Comparisons = LoadComparisons();

        private static int[] LoadComparisons()
        {
            var load = (delegate* unmanaged[Cdecl]<int*, uint, uint>)Export("mh_patchinfo_comparisons_v1");
            var comparisons = new int[9];
            fixed (int* output = comparisons)
                Check(load(output, (uint)comparisons.Length), "patch comparison table");
            foreach (var value in comparisons)
                if (value < -1 || value > 1)
                    throw new RustBackendException("Modstoke Harmony received an invalid patch comparison.");
            return comparisons;
        }
    }

    internal static int ComparePatchPriority(int index, int priority, int theirIndex, int theirPriority) =>
        PatchInfoNative.Comparisons[(priority.CompareTo(theirPriority) + 1) * 3 + index.CompareTo(theirIndex) + 1];

    internal static Patch[] AppendPatches(string owner, HarmonyMethod[] additions, Patch[] current)
    {
        // Keep the no-addition identity and null-array behavior of the public API.
        if (additions.Length == 0) return current;
        var initialIndex = current.Length;
        var indices = new int[additions.Length];
        fixed (int* output = indices)
            Check(PatchInfoNative.AppendIndices((uint)initialIndex, (uint)additions.Length,
                output, (uint)indices.Length), "patch addition indices");

        var roots = new Patch[checked(initialIndex + additions.Length)];
        var identities = new uint[roots.Length];
        Array.Copy(current, roots, initialIndex);
        var constructed = 0;
        for (var i = 0; i < additions.Length; i++)
        {
            // Read each live entry only when reached: a MethodInfo implementation can
            // invoke managed code from the constructor's metadata accessors.
            var method = additions[i];
            if (method is null) continue;
            roots[initialIndex + i] = new Patch(method, indices[constructed++], owner);
            identities[initialIndex + i] = 1;
        }
        return SelectPatchMetadata(roots, identities, (uint)initialIndex, 0);
    }

    internal static Patch[] RemovePatchesByOwner(string owner, Patch[] current)
    {
        // Wildcard removal does not enumerate or inspect the current array.
        if (owner == "*") return [];
        ArgumentNullException.ThrowIfNull(current, "source");
        var identities = new uint[current.Length];
        var roots = new Patch[current.Length];
        for (var i = 0; i < current.Length; i++)
        {
            var patch = current[i];
            roots[i] = patch;
            identities[i] = patch.owner != owner ? 1u : 0u;
        }
        return SelectPatchMetadata(roots, identities, 0, 0);
    }

    internal static Patch[] RemovePatchesByMethod(MethodInfo method, Patch[] current)
    {
        ArgumentNullException.ThrowIfNull(current, "source");
        var roots = new Patch[current.Length];
        var identities = new uint[current.Length];
        for (var i = 0; i < current.Length; i++)
        {
            var patch = current[i];
            roots[i] = patch;
            // Method resolution and CLR equality remain ordered managed operations.
            // Do not hash MethodInfo objects or resolve later entries in advance.
            identities[i] = patch.PatchMethod != method ? 1u : 0u;
        }
        return SelectPatchMetadata(roots, identities, 0, 0);
    }

    internal static string[] FilterPatchEmitPaths(string[] paths)
    {
        var identities = new uint[paths.Length];
        for (var i = 0; i < paths.Length; i++)
            identities[i] = paths[i] is null ? 0u : 1u;
        return SelectPatchMetadata(paths, identities, 0, 0);
    }

    private static T[] SelectPatchMetadata<T>(T[] roots, uint[] identities, uint retainedPrefix, uint excluded)
    {
        Span<uint> ordinals = identities.Length <= 128
            ? stackalloc uint[identities.Length]
            : new uint[identities.Length];
        uint written = 0;
        fixed (uint* input = identities)
        fixed (uint* output = ordinals)
            Check(PatchInfoNative.Filter(input, (uint)identities.Length, retainedPrefix,
                excluded, output, (uint)ordinals.Length, &written), "patch metadata selection");
        if (written > roots.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid patch selection length.");
        if (written == 0) return [];
        // Every caller supplies a newly allocated snapshot. When all entries survive,
        // that snapshot is already the result without exposing the public input array.
        if (written == roots.Length)
        {
            for (var i = 0; i < roots.Length; i++)
                if (ordinals[i] != (uint)i)
                    throw new RustBackendException("Modstoke Harmony received an invalid patch selection ordinal.");
            return roots;
        }
        var result = new T[(int)written];
        for (var i = 0; i < result.Length; i++)
        {
            var ordinal = ordinals[i];
            if (ordinal >= roots.Length)
                throw new RustBackendException("Modstoke Harmony received an invalid patch selection ordinal.");
            result[i] = roots[(int)ordinal];
        }
        return result;
    }
}
