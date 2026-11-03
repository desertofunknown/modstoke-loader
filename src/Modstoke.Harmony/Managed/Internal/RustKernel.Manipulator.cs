// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum ManipulatorStage : uint { Prefix, Postfix, Finalizer }
    internal enum ManipulatorAction : uint
    {
        Begin, Call, AssignArgs, RestoreInstance, InvokeResultRef, RestoreResult, RestoreArguments,
        InvalidPrefix, MergeRunOriginal, Wrap, LoadResult, StoreResult, ValidatePassthrough, StoreException,
        IfVoid, IfNonVoid, IfBool, DisableRethrow
    }
    internal enum ManipulatorArgument : uint
    {
        Ordinary, OriginalMethod, Instance, Args, Field, State, Result, ResultRef, Indexed, NullName
    }
    internal enum ManipulatorTransfer : uint { InitializeArray, AssignArray, RestoreBoxes, LoadInstance }
    internal enum ManipulatorTransferAction : uint
    {
        IfNotOut, PrepareOutType, NewArray, ReadType, OpenType, Duplicate, ElementIndex, LoadArgument,
        IfNotByRef, IfNotStruct, LoadObject, LoadIndirect, Jump, IfNotValue, Box, StoreElement,
        LoadArray, LoadElement, Unbox, Cast, StoreObject, StoreIndirect, StoreRef, StoreArgument, LoadBox,
        IfNotPrimitive, InitializeValue, InitializeObject, LoadNull, BeginArguments, NextArgument,
        IfNotBoxByRef, IfInstanceNotStatic, ReadInstanceType, IfInstanceStruct, IfNotParameterByRef,
        IfTypedInstance, LoadInstance, InstanceAddress, DeclareInstanceBox, StoreInstanceBox, LoadInstanceBoxAddress
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ManipulatorTransferStep
    {
        internal ManipulatorTransferAction Action;
        internal uint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ManipulatorMethod
    {
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ManipulatorStep
    {
        internal ManipulatorAction Action;
        internal uint Method, Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ManipulatorName
    {
        internal uint Start, Length;
    }

    internal sealed class ManipulatorPlan
    {
        internal ManipulatorStep[] Steps;
        internal bool CanModifyControlFlow, CanRethrow;
    }

    private static class ManipulatorNative
    {
        internal static readonly delegate* unmanaged[Cdecl]<uint, ManipulatorMethod*, uint, uint,
            ManipulatorStep*, uint, uint*, uint*, uint> Stage =
            (delegate* unmanaged[Cdecl]<uint, ManipulatorMethod*, uint, uint,
                ManipulatorStep*, uint, uint*, uint*, uint>)Export("mh_manipulator_stage_v1");
        internal static readonly delegate* unmanaged[Cdecl]<ManipulatorName*, uint, char*, uint,
            ManipulatorArgument*, uint, uint> Arguments =
            (delegate* unmanaged[Cdecl]<ManipulatorName*, uint, char*, uint,
                ManipulatorArgument*, uint, uint>)Export("mh_manipulator_arguments_v1");
        internal static readonly delegate* unmanaged[Cdecl]<uint, ManipulatorTransferStep*, uint, uint*, uint> Transfer =
            (delegate* unmanaged[Cdecl]<uint, ManipulatorTransferStep*, uint, uint*, uint>)Export("mh_manipulator_transfer_v2");
    }

    private static class ManipulatorTransfers
    {
        internal static readonly ManipulatorTransferStep[] Initialize = ReadManipulatorTransfer(ManipulatorTransfer.InitializeArray);
        internal static readonly ManipulatorTransferStep[] Assign = ReadManipulatorTransfer(ManipulatorTransfer.AssignArray);
        internal static readonly ManipulatorTransferStep[] Restore = ReadManipulatorTransfer(ManipulatorTransfer.RestoreBoxes);
        internal static readonly ManipulatorTransferStep[] Instance = ReadManipulatorTransfer(ManipulatorTransfer.LoadInstance);
    }

    internal static ReadOnlySpan<ManipulatorTransferStep> PlanManipulatorTransfer(ManipulatorTransfer kind) => kind switch
    {
        ManipulatorTransfer.InitializeArray => ManipulatorTransfers.Initialize,
        ManipulatorTransfer.AssignArray => ManipulatorTransfers.Assign,
        ManipulatorTransfer.RestoreBoxes => ManipulatorTransfers.Restore,
        ManipulatorTransfer.LoadInstance => ManipulatorTransfers.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ManipulatorTransferStep[] ReadManipulatorTransfer(ManipulatorTransfer kind)
    {
        Span<ManipulatorTransferStep> steps = stackalloc ManipulatorTransferStep[64];
        uint written = 0;
        fixed (ManipulatorTransferStep* stepPtr = steps)
            Check(ManipulatorNative.Transfer((uint)kind, stepPtr, (uint)steps.Length, &written), "patch argument transfer plan");
        if (written > steps.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid argument transfer plan.");
        for (var i = 0; i < written; i++)
        {
            var step = steps[i];
            var branch = step.Action is ManipulatorTransferAction.IfNotOut or
                ManipulatorTransferAction.IfNotByRef or ManipulatorTransferAction.IfNotStruct or
                ManipulatorTransferAction.IfNotValue or ManipulatorTransferAction.IfNotPrimitive or
                ManipulatorTransferAction.Jump or ManipulatorTransferAction.BeginArguments or
                ManipulatorTransferAction.IfNotBoxByRef or ManipulatorTransferAction.IfInstanceNotStatic or
                ManipulatorTransferAction.IfInstanceStruct or ManipulatorTransferAction.IfNotParameterByRef or
                ManipulatorTransferAction.IfTypedInstance;
            if (step.Action > ManipulatorTransferAction.LoadInstanceBoxAddress ||
                (step.Action == ManipulatorTransferAction.NextArgument
                    ? step.Target == 0 || step.Target >= i ||
                      steps[checked((int)step.Target) - 1].Action != ManipulatorTransferAction.BeginArguments ||
                      steps[checked((int)step.Target) - 1].Target != i + 1
                    : branch ? step.Target <= i || step.Target > written : step.Target != 0))
                throw new RustBackendException("Modstoke Harmony received an invalid argument transfer step.");
        }
        return steps[..checked((int)written)].ToArray();
    }

    internal static ManipulatorPlan PlanManipulator(ManipulatorStage stage, ManipulatorMethod[] methods,
        bool hasResult, bool suppressExceptions = false, bool canModifyControlFlow = false)
    {
        var steps = new ManipulatorStep[checked(methods.Length * 32 + 1)];
        uint written = 0, summary = 0;
        var context = (hasResult ? 1u : 0u) | (suppressExceptions ? 2u : 0u) |
            (canModifyControlFlow ? 4u : 0u);
        fixed (ManipulatorMethod* methodPtr = methods)
        fixed (ManipulatorStep* stepPtr = steps)
            Check(ManipulatorNative.Stage((uint)stage, methodPtr, (uint)methods.Length, context,
                stepPtr, (uint)steps.Length, &written, &summary), "patch emission plan");
        if (written > steps.Length || (summary & ~3u) != 0)
            throw new RustBackendException("Modstoke Harmony received an invalid patch emission plan.");
        for (var i = 0; i < written; i++)
        {
            var step = steps[i];
            if (step.Action > ManipulatorAction.DisableRethrow || step.Target > written ||
                (step.Action >= ManipulatorAction.IfVoid && step.Action <= ManipulatorAction.IfBool &&
                 step.Target <= i) ||
                (step.Method >= methods.Length &&
                 !(step.Method == uint.MaxValue && step.Action == ManipulatorAction.LoadResult)))
                throw new RustBackendException("Modstoke Harmony received an invalid patch emission step.");
        }
        Array.Resize(ref steps, checked((int)written));
        return new ManipulatorPlan
        {
            Steps = steps, CanModifyControlFlow = (summary & 1) != 0, CanRethrow = (summary & 2) != 0
        };
    }

    internal static ManipulatorArgument[] ClassifyManipulatorArguments(IReadOnlyList<string> names)
    {
        var spans = new ManipulatorName[names.Count];
        var length = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            spans[i] = new ManipulatorName
            {
                Start = (uint)length, Length = name == null ? uint.MaxValue : (uint)name.Length
            };
            length = checked(length + (name?.Length ?? 0));
        }
        var text = new char[length];
        for (var i = 0; i < names.Count; i++)
            names[i]?.CopyTo(0, text, checked((int)spans[i].Start), names[i].Length);
        var result = new ManipulatorArgument[names.Count];
        fixed (ManipulatorName* spanPtr = spans)
        fixed (char* textPtr = text)
        fixed (ManipulatorArgument* resultPtr = result)
            Check(ManipulatorNative.Arguments(spanPtr, (uint)spans.Length, textPtr, (uint)text.Length,
                resultPtr, (uint)result.Length), "patch argument classification");
        foreach (var kind in result)
            if (kind > ManipulatorArgument.NullName)
                throw new RustBackendException("Modstoke Harmony received an invalid patch argument kind.");
        return result;
    }
}
