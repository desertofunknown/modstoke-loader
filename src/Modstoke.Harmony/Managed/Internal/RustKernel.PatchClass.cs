// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum ClassAction : uint
    {
        Start, Prepare, Targets, Reverse, ReverseSingle, Bulk, Attributes,
        Cleanup, CleanupEmpty, Report, Done
    }

    internal enum ClassTargetAction : uint
    {
        Start, InspectAll, Declared, Many, Single, InvalidElements, Done
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ClassStep
    {
        internal ClassAction Action;
        internal uint CatchException;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClassAnnotation
    {
        internal uint Flags;
        internal int MethodType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ClassAnnotationResult
    {
        internal uint ValidCount, Error;
    }

    private static class PatchClassNative
    {
        internal static readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint, ClassStep*, uint> Advance =
            (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, ClassStep*, uint>)Export("mh_patchclass_advance_v1");
        internal static readonly delegate* unmanaged[Cdecl]<uint, uint, uint*, uint, uint*, uint> Targets =
            (delegate* unmanaged[Cdecl]<uint, uint, uint*, uint, uint*, uint>)Export("mh_patchclass_targets_v1");
        internal static readonly delegate* unmanaged[Cdecl]<ClassAnnotation*, uint, ClassAnnotationResult*, uint> Bulk =
            (delegate* unmanaged[Cdecl]<ClassAnnotation*, uint, ClassAnnotationResult*, uint>)Export("mh_patchclass_bulk_v1");
    }

    internal static ClassStep AdvancePatchClass(ClassAction previous, bool unpatch, bool failed, bool prepared, int targetCount)
    {
        ClassStep next = default;
        Check(PatchClassNative.Advance((uint)previous, unpatch ? 1u : 0u,
            (failed ? 1u : 0u) | (prepared ? 2u : 0u), (uint)targetCount, &next), "patch class lifecycle");
        if ((uint)next.Action < (uint)ClassAction.Prepare || (uint)next.Action > (uint)ClassAction.Done || next.CatchException > 1)
            throw new RustBackendException("Modstoke Harmony received an invalid patch class step.");
        return next;
    }

    internal static ClassTargetAction AdvanceClassTargets(ClassTargetAction previous, bool present, List<MethodBase> methods = null)
    {
        // Enumeration has already happened in the CLR at the target callback boundary.
        var count = methods?.Count ?? 0;
        Span<uint> elements = count <= 128 ? stackalloc uint[count] : new uint[count];
        for (var i = 0; i < elements.Length; i++)
            elements[i] = methods[i] is null ? 0u : 1u;
        uint next = 0;
        fixed (uint* values = elements)
            Check(PatchClassNative.Targets((uint)previous, present ? 1u : 0u, values, (uint)elements.Length, &next), "patch class targets");
        if (next < (uint)ClassTargetAction.InspectAll || next > (uint)ClassTargetAction.Done)
            throw new RustBackendException("Modstoke Harmony received an invalid patch target step.");
        return (ClassTargetAction)next;
    }

    internal static ClassAnnotationResult PlanClassAnnotations(List<AttributePatch> patches)
    {
        // Refresh after each job lookup: custom MethodBase equality may change metadata.
        var count = patches.Count;
        Span<ClassAnnotation> annotations = count <= 64
            ? stackalloc ClassAnnotation[count] : new ClassAnnotation[count];
        for (var i = 0; i < annotations.Length; i++)
        {
            var info = patches[i]?.info;
            if (info is null)
            {
                // Defer the null dereference until earlier valid patches have been added.
                annotations[i] = new ClassAnnotation { Flags = 8 };
                continue;
            }
            annotations[i] = new ClassAnnotation
            {
                Flags = (info.methodName is null ? 0u : 1u) | (info.methodType.HasValue ? 2u : 0u)
                    | (info.argumentTypes is null ? 0u : 4u),
                MethodType = (int)info.methodType.GetValueOrDefault()
            };
        }
        ClassAnnotationResult result = default;
        fixed (ClassAnnotation* values = annotations)
            Check(PatchClassNative.Bulk(values, (uint)annotations.Length, &result), "bulk patch annotations");
        if (result.ValidCount > annotations.Length || result.Error > 4
            || (result.Error == 0 ? result.ValidCount != annotations.Length : result.ValidCount == annotations.Length))
            throw new RustBackendException("Modstoke Harmony received an invalid bulk patch plan.");
        return result;
    }
}
