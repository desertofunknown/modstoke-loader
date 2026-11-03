// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum ProcessorOperation : uint { Add, RemoveOwner, RemoveMethod }

    internal enum ProcessorAction : uint
    {
        AddPrefix, AddPostfix, AddTranspiler, AddFinalizer, AddILManipulator,
        RemovePrefix, RemovePostfix, RemoveTranspiler, RemoveFinalizer, RemoveILManipulator,
        RemoveMethod, UpdateWrapper, RememberReplacement
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorOwner
    {
        internal uint Start, Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessorOwnerEntry
    {
        internal uint First, Last;
    }

    private static class ProcessorExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<uint, int, ProcessorAction*, uint, uint*, uint> Plan =
            (delegate* unmanaged[Cdecl]<uint, int, ProcessorAction*, uint, uint*, uint>)Export("mh_patchprocessor_plan_v1");
        internal static readonly delegate* unmanaged[Cdecl]<ProcessorOwner*, uint, char*, uint, ProcessorOwnerEntry*, uint, uint*, uint> Owners =
            (delegate* unmanaged[Cdecl]<ProcessorOwner*, uint, char*, uint, ProcessorOwnerEntry*, uint, uint*, uint>)Export("mh_patchprocessor_owners_v1");
    }

    internal static int PlanProcessor(ProcessorOperation operation, HarmonyPatchType type, Span<ProcessorAction> actions)
    {
        uint count = 0;
        fixed (ProcessorAction* output = actions)
            Check(ProcessorExports.Plan((uint)operation, (int)type, output, (uint)actions.Length, &count), "patch processor plan");
        if (count > actions.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid patch processor plan length.");
        foreach (var action in actions[..(int)count])
            if ((uint)action > (uint)ProcessorAction.RememberReplacement)
                throw new RustBackendException("Modstoke Harmony received an invalid patch processor action.");
        return (int)count;
    }

    internal static ReadOnlySpan<ProcessorOwnerEntry> ReduceProcessorOwners(List<(string Owner, Assembly Assembly)> observations)
    {
        var owners = new ProcessorOwner[observations.Count];
        var textLength = 0;
        for (var i = 0; i < owners.Length; i++)
        {
            var length = observations[i].Owner.Length;
            owners[i] = new ProcessorOwner { Start = (uint)textLength, Count = (uint)length };
            textLength = checked(textLength + length);
        }
        var text = new char[textLength];
        for (var i = 0; i < owners.Length; i++)
            observations[i].Owner.AsSpan().CopyTo(text.AsSpan((int)owners[i].Start, (int)owners[i].Count));
        var result = new ProcessorOwnerEntry[owners.Length];
        uint count = 0;
        fixed (ProcessorOwner* input = owners)
        fixed (char* characters = text)
        fixed (ProcessorOwnerEntry* output = result)
            Check(ProcessorExports.Owners(input, (uint)owners.Length, characters, (uint)text.Length,
                output, (uint)result.Length, &count), "patch processor owners");
        if (count > result.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid patch owner count.");
        for (var i = 0; i < (int)count; i++)
            if (result[i].First >= owners.Length || result[i].Last >= owners.Length)
                throw new RustBackendException("Modstoke Harmony received an invalid patch owner index.");
        // The caller consumes this synchronously; keep the owned array alive through
        // its span rather than allocating and copying a second, trimmed array.
        return result.AsSpan(0, (int)count);
    }
}
