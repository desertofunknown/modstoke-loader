// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PatchModelText
    {
        internal uint Start, Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PatchModelJob
    {
        internal int Prefixes, Postfixes, Transpilers, Finalizers, ILManipulators;
    }

    private static class PatchModelExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<PatchModelText*, uint, char*, uint, uint*, uint> Classify =
            (delegate* unmanaged[Cdecl]<PatchModelText*, uint, char*, uint, uint*, uint>)Export("mh_patchmodels_classify_v1");
        internal static readonly delegate* unmanaged[Cdecl]<byte*, uint, uint*, uint, uint*, uint> Group =
            (delegate* unmanaged[Cdecl]<byte*, uint, uint*, uint, uint*, uint>)Export("mh_patchmodels_group_v2");
        internal static readonly delegate* unmanaged[Cdecl]<PatchModelJob*, uint, uint*, uint, uint*, uint> Jobs =
            (delegate* unmanaged[Cdecl]<PatchModelJob*, uint, uint*, uint, uint*, uint>)Export("mh_patchmodels_jobs_v1");
    }

    internal static HarmonyPatchType? ClassifyAttributePatch(string methodName, object[] attributes)
    {
        var texts = new PatchModelText[checked(attributes.Length + 1)];
        var characters = new List<char>();
        void AddText(int index, string value)
        {
            texts[index] = new PatchModelText { Start = (uint)characters.Count, Count = (uint)value.Length };
            characters.AddRange(value);
        }
        AddText(0, methodName ?? "");
        for (var i = 0; i < attributes.Length; i++)
            AddText(i + 1, attributes[i].GetType().FullName);
        var buffer = characters.ToArray();
        uint patchType = 0;
        fixed (PatchModelText* nativeTexts = texts)
        fixed (char* nativeCharacters = buffer)
            Check(PatchModelExports.Classify(nativeTexts, (uint)texts.Length, nativeCharacters,
                (uint)buffer.Length, &patchType), "attribute patch classification");
        if (patchType > (uint)HarmonyPatchType.ILManipulator)
            throw new RustBackendException("Modstoke Harmony received an invalid attribute patch type.");
        return patchType == 0 ? null : (HarmonyPatchType)patchType;
    }

    internal static (List<HarmonyMethod> Incomplete, List<HarmonyMethod> Complete) GroupAttributePatches(
        List<HarmonyMethod> methods, bool collectIncomplete)
    {
        var completeness = new byte[methods.Count];
        var capturedMethods = new HarmonyMethod[methods.Count];
        var methodIndex = 0;
        // Capture the same references and short-circuit completeness decisions as
        // ToLookup. Do not read argument arrays or invoke target equality here.
        foreach (var method in methods)
        {
            completeness[methodIndex] = (collectIncomplete || method.declaringType != null)
                && method.methodName != null ? (byte)1 : (byte)0;
            capturedMethods[methodIndex++] = method;
        }
        var indices = new uint[capturedMethods.Length];
        var counts = stackalloc uint[2];
        fixed (byte* nativeCompleteness = completeness)
        fixed (uint* nativeIndices = indices)
            Check(PatchModelExports.Group(nativeCompleteness, (uint)completeness.Length, nativeIndices,
                (uint)indices.Length, counts), "attribute patch grouping");
        if ((ulong)counts[0] + counts[1] != (ulong)indices.Length)
            throw new RustBackendException("Modstoke Harmony received invalid attribute patch group sizes.");
        var incomplete = new List<HarmonyMethod>((int)counts[0]);
        var complete = new List<HarmonyMethod>((int)counts[1]);
        for (var i = 0; i < counts[0] + counts[1]; i++)
        {
            var index = indices[i];
            if (index >= capturedMethods.Length)
                throw new RustBackendException("Modstoke Harmony received an invalid attribute patch index.");
            (i < counts[0] ? incomplete : complete).Add(capturedMethods[(int)index]);
        }
        return (incomplete, complete);
    }

    internal static uint[] SelectPatchJobs(PatchModelJob[] jobs)
    {
        var indices = new uint[jobs.Length];
        uint count = 0;
        fixed (PatchModelJob* nativeJobs = jobs)
        fixed (uint* nativeIndices = indices)
            Check(PatchModelExports.Jobs(nativeJobs, (uint)jobs.Length, nativeIndices,
                (uint)indices.Length, &count), "patch job selection");
        if (count > indices.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid patch job count.");
        Array.Resize(ref indices, (int)count);
        foreach (var index in indices)
            if (index >= jobs.Length)
                throw new RustBackendException("Modstoke Harmony received an invalid patch job index.");
        return indices;
    }
}
