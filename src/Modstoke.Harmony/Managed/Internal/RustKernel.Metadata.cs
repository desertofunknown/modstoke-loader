// Copyright (c) 2026 Alex Cooper. MIT license.
using System;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum MetadataOperation : uint { Copy, Overlay, MergePair }

    [Flags]
    internal enum MetadataFacts
    {
        None = 0, MasterPresent = 1, DetailPresent = 2, Priority = 4,
        MasterUnset = 8, DetailUnset = 16, DetailHigher = 32
    }

    private static class MetadataPolicyBinding
    {
        internal static readonly uint[] Selections = CreateMetadataPolicy();
    }

    private static uint[] CreateMetadataPolicy()
    {
        var selections = new uint[3 * 64];
        var policy = (delegate* unmanaged[Cdecl]<uint*, uint, uint>)Export("mh_metadata_policy_v1");
        fixed (uint* nativeSelections = selections)
            Check(policy(nativeSelections, (uint)selections.Length), "metadata policy");
        foreach (var selection in selections)
            if (selection > 2)
                throw new RustBackendException("Modstoke Harmony received an invalid metadata selection.");
        return selections;
    }

    internal static ReadOnlySpan<uint> MetadataPolicy(MetadataOperation operation) =>
        MetadataPolicyBinding.Selections.AsSpan(checked((int)operation * 64), 64);
}
