// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using CecilOpCode = Mono.Cecil.Cil.OpCode;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEmitterStep
    {
        internal uint Code, Operand;
    }

    internal enum EmitterOperand : uint
    {
        None, Type, NextLabel, StoreNewLocal, Local, Method, MarkLabel, DefineLabel,
        UnboxValueType, NullMessage, UnsupportedMessage, ExceptionConstructor
    }

    internal readonly struct EmitterStep(OpCode code, EmitterOperand operand)
    {
        internal readonly OpCode Code = code;
        internal readonly EmitterOperand Operand = operand;
    }

    private sealed class ReflectionEmitTables
    {
        internal readonly Dictionary<Type, (CecilOpCode Load, CecilOpCode Store)> Indirect = new();
        internal CecilOpCode DefaultLoad, DefaultStore;
        internal readonly OpCode[][] Fields = new OpCode[4][];
    }

    private static class ReflectionEmitBinding
    {
        internal static readonly ReflectionEmitTables Tables = CreateReflectionEmitTables();
    }

    private static class ReflectionDispatchBinding
    {
        internal static readonly EmitterStep[] Steps = CreateReflectionDispatch();
    }

    private static class ReflectionMemberBinding
    {
        internal static readonly delegate* unmanaged[Cdecl]<char*, uint, uint*, uint> Separator =
            (delegate* unmanaged[Cdecl]<char*, uint, uint*, uint>)Export("mh_reflectionemit_member_separator_v1");
    }

    internal static int ReflectionMemberSeparator(string text)
    {
        uint separator = uint.MaxValue;
        fixed (char* characters = text)
            Check(ReflectionMemberBinding.Separator(characters, (uint)text.Length, &separator), "type/member name parsing");
        if (separator == uint.MaxValue) return -1;
        if (separator >= text.Length || text[(int)separator] != ':')
            throw new RustBackendException("Invalid type/member name separator.");
        return (int)separator;
    }

    private static ReflectionEmitTables CreateReflectionEmitTables()
    {
        var indirect = new ushort[30];
        var fields = new ushort[16];
        var create = (delegate* unmanaged[Cdecl]<ushort*, uint, ushort*, uint, uint>)Export("mh_reflectionemit_tables_v1");
        fixed (ushort* indirectPointer = indirect)
        fixed (ushort* fieldPointer = fields)
            Check(create(indirectPointer, (uint)indirect.Length, fieldPointer, (uint)fields.Length), "reflection/emitter tables");

        var cecilCodes = new Dictionary<ushort, CecilOpCode>();
        foreach (var field in typeof(CecilOpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is CecilOpCode code) cecilCodes[unchecked((ushort)code.Value)] = code;
        foreach (var code in indirect)
            if (!cecilCodes.ContainsKey(code)) throw new RustBackendException("Invalid indirect opcode in reflection/emitter tables.");
        var tables = new ReflectionEmitTables { DefaultLoad = cecilCodes[indirect[0]], DefaultStore = cecilCodes[indirect[1]] };
        // Explicit identities preserve the original dictionary semantics; TypeCode alone
        // would also classify unrelated Type implementations and enums as primitives.
        Type[] types = [typeof(bool), typeof(sbyte), typeof(byte), typeof(char), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(IntPtr), typeof(UIntPtr)];
        for (var i = 0; i < types.Length; i++)
            tables.Indirect[types[i]] = (cecilCodes[indirect[(i + 1) * 2]], cecilCodes[indirect[(i + 1) * 2 + 1]]);
        for (var flags = 0; flags < tables.Fields.Length; flags++)
        {
            var count = 0;
            while (count < 4 && fields[flags * 4 + count] != ushort.MaxValue) count++;
            var recipe = new OpCode[count];
            for (var i = 0; i < count; i++)
            {
                if (!Current.Value.OpCodes.TryGetValue(unchecked((short)fields[flags * 4 + i]), out recipe[i]))
                    throw new RustBackendException("Invalid field opcode in reflection/emitter tables.");
                if (recipe[i].OperandType is not (OperandType.InlineNone or OperandType.InlineType or OperandType.InlineField))
                    throw new RustBackendException("Invalid field operand in reflection/emitter tables.");
            }
            tables.Fields[flags] = recipe;
        }
        return tables;
    }

    internal static CecilOpCode ReflectionIndirect(Type type, bool store)
    {
        var tables = ReflectionEmitBinding.Tables;
        return tables.Indirect.TryGetValue(type, out var codes)
            ? store ? codes.Store : codes.Load
            : store ? tables.DefaultStore : tables.DefaultLoad;
    }

    internal static ReadOnlySpan<OpCode> ReflectionFieldRecipe(bool isStatic, bool cast) =>
        ReflectionEmitBinding.Tables.Fields[(isStatic ? 1 : 0) | (cast ? 2 : 0)];

    internal static ReadOnlySpan<EmitterStep> ReflectionDispatchPrologue() => ReflectionDispatchBinding.Steps.AsSpan(0, 5);
    internal static ReadOnlySpan<EmitterStep> ReflectionDispatchOverload() => ReflectionDispatchBinding.Steps.AsSpan(5, 13);
    internal static ReadOnlySpan<EmitterStep> ReflectionDispatchEpilogue() => ReflectionDispatchBinding.Steps.AsSpan(18, 5);

    private static EmitterStep[] CreateReflectionDispatch()
    {
        // One fixed recipe is reused for each overload. CLR metadata is deliberately
        // absent: getters and conditional unboxing execute at their original IL step.
        const int count = 23;
        NativeEmitterStep* native = stackalloc NativeEmitterStep[count];
        var dispatch = (delegate* unmanaged[Cdecl]<NativeEmitterStep*, uint, uint>)Export("mh_reflectionemit_dispatch_v2");
        Check(dispatch(native, count), "emitter dispatch recipes");
        var result = new EmitterStep[count];
        for (var i = 0; i < result.Length; i++)
        {
            var step = native[i];
            if (step.Operand > (uint)EmitterOperand.ExceptionConstructor
                || step.Code > ushort.MaxValue || !Current.Value.OpCodes.TryGetValue(unchecked((short)step.Code), out var code))
                throw new RustBackendException("Invalid emitter dispatch instruction.");
            result[i] = new EmitterStep(code, (EmitterOperand)step.Operand);
        }
        return result;
    }
}
