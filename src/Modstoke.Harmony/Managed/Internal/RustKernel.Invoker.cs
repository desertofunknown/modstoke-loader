// Copyright (c) 2026 Alex Cooper. MIT license.
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal const int InvokerLoadTarget = 0, InvokerUnboxTarget = 1;
    internal const int InvokerCallVirtual = 10, InvokerCall = 11;
    internal const int InvokerLoadNull = 12, InvokerBoxResult = 13, InvokerReturn = 14;
    private const int InvokerIntegerStart = 15, InvokerWideInteger = InvokerIntegerStart + 256;

    internal enum InvokerOperand : uint
    {
        None, Type, ObjectType, Method, Index, SignedByte, Integer, BoxLocal
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InvokerDescriptor
    {
        internal uint Kind;
        internal int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InvokerNativeInstruction
    {
        internal uint Opcode;
        internal InvokerOperand Operand;
    }

    internal readonly struct InvokerInstruction(OpCode opcode, InvokerOperand operand)
    {
        internal readonly OpCode Opcode = opcode;
        internal readonly InvokerOperand Operand = operand;
    }

    // One native batch for all metadata categories, including every short integer.
    // Live Type/MethodInfo getters stay interleaved with CLR emission in MethodInvoker.
    private static class InvokerNative
    {
        internal static readonly InvokerInstruction[][] Recipes = CreateInvokerRecipes();
    }

    internal static InvokerInstruction[] InvokerInstructions(int recipe) => InvokerNative.Recipes[recipe];

    internal static int InvokerParameter(bool byRef, bool valueType, bool direct) =>
        2 + (byRef ? 1 : 0) + (valueType ? 2 : 0) + (direct ? 4 : 0);

    internal static int InvokerInteger(int value) => value is >= sbyte.MinValue and <= sbyte.MaxValue
        ? InvokerIntegerStart + value - sbyte.MinValue : InvokerWideInteger;

    private static InvokerInstruction[][] CreateInvokerRecipes()
    {
        var descriptors = new InvokerDescriptor[InvokerWideInteger + 1];
        descriptors[InvokerLoadTarget] = new() { Kind = 0 };
        descriptors[InvokerUnboxTarget] = new() { Kind = 1 };
        for (var flags = 0; flags < 8; flags++)
            descriptors[2 + flags] = new() { Kind = 2, Value = flags };
        descriptors[InvokerCallVirtual] = new() { Kind = 3, Value = 0 };
        descriptors[InvokerCall] = new() { Kind = 3, Value = 1 };
        descriptors[InvokerLoadNull] = new() { Kind = 4, Value = 0 };
        descriptors[InvokerBoxResult] = new() { Kind = 4, Value = 1 };
        descriptors[InvokerReturn] = new() { Kind = 4, Value = 2 };
        for (var value = (int)sbyte.MinValue; value <= sbyte.MaxValue; value++)
            descriptors[InvokerInteger(value)] = new() { Kind = 5, Value = value };
        descriptors[InvokerWideInteger] = new() { Kind = 5, Value = 128 };

        var plan = (delegate* unmanaged[Cdecl]<InvokerDescriptor*, uint, InvokerNativeInstruction*, uint,
            uint*, uint, uint*, uint>)Export("mh_invoker_plan_v1");
        var instructions = new InvokerNativeInstruction[checked(descriptors.Length * 13)];
        var offsets = new uint[descriptors.Length + 1];
        uint written = 0;
        fixed (InvokerDescriptor* descriptorPtr = descriptors)
        fixed (InvokerNativeInstruction* instructionPtr = instructions)
        fixed (uint* offsetPtr = offsets)
            Check(plan(descriptorPtr, (uint)descriptors.Length, instructionPtr, (uint)instructions.Length,
                offsetPtr, (uint)offsets.Length, &written), "invocation instruction planning");
        if (written > instructions.Length || offsets[0] != 0 || offsets[^1] != written)
            throw new RustBackendException("Modstoke Harmony received an invalid invocation plan.");

        var recipes = new InvokerInstruction[descriptors.Length][];
        for (var i = 0; i < recipes.Length; i++)
        {
            var start = offsets[i];
            var end = offsets[i + 1];
            if (start >= end || end > written)
                throw new RustBackendException("Modstoke Harmony received an invalid invocation recipe range.");
            var recipe = new InvokerInstruction[end - start];
            for (var j = start; j < end; j++)
            {
                var instruction = instructions[j];
                if (instruction.Operand > InvokerOperand.BoxLocal || instruction.Opcode > ushort.MaxValue ||
                    !Current.Value.OpCodes.TryGetValue(unchecked((short)instruction.Opcode), out var opcode))
                    throw new RustBackendException("Modstoke Harmony received an invalid invocation instruction.");
                recipe[j - start] = new(opcode, instruction.Operand);
            }
            recipes[i] = recipe;
        }
        return recipes;
    }
}
