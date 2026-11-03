// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum TranspilerOperand : uint
    {
        None, CacheField, CacheIndex, CacheGetter, TargetConstructor, DelegateMethod,
        DelegateConstructor, Argument, Invoke, DebugChannel, Text, LogText
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTranspilerInstruction
    {
        internal uint OpCode, Operand, Argument;
    }

    internal readonly struct TranspilerInstruction
    {
        internal readonly OpCode OpCode;
        internal readonly TranspilerOperand Operand;
        internal readonly uint Argument;

        internal TranspilerInstruction(OpCode opcode, TranspilerOperand operand, uint argument)
        {
            OpCode = opcode;
            Operand = operand;
            Argument = argument;
        }
    }

    private static class TranspilerExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<uint, uint, uint, NativeTranspilerInstruction*, uint, uint> Delegate =
            (delegate* unmanaged[Cdecl]<uint, uint, uint, NativeTranspilerInstruction*, uint, uint>)Export("mh_transpilers_delegate_v1");
        internal static readonly delegate* unmanaged[Cdecl]<NativeTranspilerInstruction*, uint, uint*, uint, uint> Templates =
            (delegate* unmanaged[Cdecl]<NativeTranspilerInstruction*, uint, uint*, uint, uint>)Export("mh_transpilers_templates_v1");
    }

    private static class TranspilerTemplates
    {
        internal static readonly TranspilerInstruction[] Debug;
        internal static readonly OpCode[] Replacement;

        static TranspilerTemplates()
        {
            Span<NativeTranspilerInstruction> debug = stackalloc NativeTranspilerInstruction[4];
            Span<uint> replacement = stackalloc uint[2];
            fixed (NativeTranspilerInstruction* nativeDebug = debug)
            fixed (uint* nativeReplacement = replacement)
                Check(TranspilerExports.Templates(nativeDebug, (uint)debug.Length,
                    nativeReplacement, (uint)replacement.Length), "transpiler templates");
            Debug = new TranspilerInstruction[debug.Length];
            DecodeTranspilerInstructions(debug, Debug);
            Replacement = new OpCode[replacement.Length];
            for (var i = 0; i < replacement.Length; i++)
                Replacement[i] = DecodeTranspilerOpcode(replacement[i]);
        }
    }

    internal static void PlanDelegateInstructions(bool preserveContext, bool hasTarget, int argumentCount,
        Span<TranspilerInstruction> instructions)
    {
        var count = checked(argumentCount + 5);
        if (instructions.Length != count)
            throw new ArgumentException("Delegate instruction buffer has an incorrect length.", nameof(instructions));
        // Keep the two recipe buffers off the heap for ordinary signatures, while
        // bounding stack usage independently of the reflected parameter count.
        Span<NativeTranspilerInstruction> native = count <= 64
            ? stackalloc NativeTranspilerInstruction[count] : new NativeTranspilerInstruction[count];
        fixed (NativeTranspilerInstruction* output = native)
            Check(TranspilerExports.Delegate(preserveContext ? 1u : 0u, hasTarget ? 1u : 0u,
                (uint)argumentCount, output, (uint)native.Length), "delegate instruction planning");
        DecodeTranspilerInstructions(native, instructions);
    }

    internal static TranspilerInstruction[] DebugLoggerInstructions => TranspilerTemplates.Debug;

    internal static OpCode ReplacementCallOpcode(bool isConstructor) => TranspilerTemplates.Replacement[isConstructor ? 1 : 0];

    private static OpCode DecodeTranspilerOpcode(uint value)
    {
        if (value > ushort.MaxValue || !Current.Value.OpCodes.TryGetValue(unchecked((short)value), out var opcode))
            throw new RustBackendException("Modstoke Harmony received an invalid transpiler opcode.");
        return opcode;
    }

    private static void DecodeTranspilerInstructions(ReadOnlySpan<NativeTranspilerInstruction> native,
        Span<TranspilerInstruction> result)
    {
        for (var i = 0; i < native.Length; i++)
        {
            var instruction = native[i];
            if (instruction.Operand > (uint)TranspilerOperand.LogText || instruction.Argument > int.MaxValue)
                throw new RustBackendException("Modstoke Harmony received an invalid transpiler operand recipe.");
            result[i] = new TranspilerInstruction(DecodeTranspilerOpcode(instruction.OpCode),
                (TranspilerOperand)instruction.Operand, instruction.Argument);
        }
    }
}
