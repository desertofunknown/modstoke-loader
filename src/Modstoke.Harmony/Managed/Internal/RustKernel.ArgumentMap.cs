// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum ArgumentMapStage : uint { Parameter, Method, Type, Name, Direct, Fallback }
    internal enum ArgumentMapAction : uint { Complete, Method, Type, NullParameterArray, NullSearchArray }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArgumentMapRequest
    {
        internal ArgumentMapStage Stage;
        internal uint AttributePresent, AttributeName, FallbackName;
        internal int AttributeIndex;
        internal uint NamesPresent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArgumentMapResult
    {
        internal ArgumentMapAction Action;
        internal uint Source;
        internal int SourceIndex, Index;
    }

    private static class ArgumentMapExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<ArgumentMapRequest*, uint*, uint, ArgumentMapResult*, uint> Resolve =
            (delegate* unmanaged[Cdecl]<ArgumentMapRequest*, uint*, uint, ArgumentMapResult*, uint>)Export("mh_argumentmap_resolve_v1");
    }

    internal static (ArgumentMapAction Action, string Name, int Index) ResolveArgumentMap(
        ArgumentMapStage stage, HarmonyArgument attribute, string[] originalParameterNames, string fallbackName)
    {
        var attributeName = stage == ArgumentMapStage.Parameter ? attribute?.OriginalName : attribute?.NewName;
        var attributeIndex = attribute?.Index ?? -1;
        var count = originalParameterNames?.Length ?? 0;
        var indexedName = attributeIndex >= 0 && attributeIndex < count
            ? originalParameterNames[attributeIndex] : null;

        // Rust can select only these three names. Preserve ordinal equality for
        // them and collapse all other names into one unselectable ID, avoiding a
        // string-intern dictionary for every lazy scope. Null and empty stay distinct.
        uint NameId(string name)
        {
            if (name is null) return 0;
            if (name.Length == 0) return 1;
            if (name == attributeName) return 2;
            if (name == fallbackName) return 3;
            if (name == indexedName) return 4;
            return 5;
        }

        var request = new ArgumentMapRequest
        {
            Stage = stage,
            AttributePresent = attribute is null ? 0u : 1u,
            AttributeName = NameId(attributeName),
            FallbackName = NameId(fallbackName),
            AttributeIndex = attributeIndex,
            NamesPresent = originalParameterNames is null ? 0u : 1u,
        };
        Span<uint> names = count <= 128 ? stackalloc uint[count] : new uint[count];
        for (var i = 0; i < names.Length; i++)
            names[i] = NameId(i == attributeIndex ? indexedName : originalParameterNames[i]);
        ArgumentMapResult result = default;
        fixed (uint* nativeNames = names)
            Check(ArgumentMapExports.Resolve(&request, nativeNames, (uint)names.Length, &result), "argument mapping");

        // Keep the two distinct upstream null-array failures in the CLR.
        if (result.Action == ArgumentMapAction.NullParameterArray)
            throw new NullReferenceException();
        if (result.Action == ArgumentMapAction.NullSearchArray)
            throw new ArgumentNullException("array");
        if (result.Action > ArgumentMapAction.Type || result.Source > 3
            || (result.Source == 2 && (result.SourceIndex < 0 || result.SourceIndex >= names.Length))
            || result.Index < -1 || result.Index >= names.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid argument mapping.");

        // Project the original reference, not another string with the same ID.
        var name = result.Source switch
        {
            1 => attributeName,
            2 => originalParameterNames[result.SourceIndex],
            3 => fallbackName,
            _ => null,
        };
        return (result.Action, name, result.Index);
    }
}
