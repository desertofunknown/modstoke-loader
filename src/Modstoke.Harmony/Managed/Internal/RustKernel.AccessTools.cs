// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccessDelegateInstruction
    {
        internal ushort OpCode, Operand;
        internal uint Index, Target;
    }

    private static class AccessToolsBinding
    {
        internal static readonly delegate* unmanaged[Cdecl]<uint, uint, AccessDelegateInstruction*, uint, uint*, uint> Plan =
            (delegate* unmanaged[Cdecl]<uint, uint, AccessDelegateInstruction*, uint, uint*, uint>)Export("mh_accesstools_delegate_plan_v1");
        // Only primitive shape keys and IL recipes are retained, never collectible CLR types or methods.
        internal static readonly ConcurrentDictionary<int, AccessDelegateInstruction[]> Plans = new();
    }

    private static AccessDelegateInstruction[] CreateAccessDelegatePlan(int parameterCount)
    {
        var closed = parameterCount == 0;
        var plan = new AccessDelegateInstruction[closed ? 4 : checked(parameterCount * 5 + 3)];
        uint written = 0;
        fixed (AccessDelegateInstruction* output = plan)
            Check(AccessToolsBinding.Plan(closed ? 1u : 0u, (uint)parameterCount,
                output, (uint)plan.Length, &written), "delegate IL planning");
        if (written != plan.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid delegate recipe length.");
        return plan;
    }

    internal static void EmitAccessOpenDelegate(ILGenerator il, MethodInfo method, Type declaringType,
        Type[] parameterTypes, Type[] delegateArguments)
    {
        var plan = AccessToolsBinding.Plans.GetOrAdd(parameterTypes.Length, CreateAccessDelegatePlan);
        EmitAccessDelegatePlan(il, plan, method, parameterTypes, delegateArguments, declaringType, null);
    }

    internal static void EmitAccessClosedDelegate(ILGenerator il, MethodInfo method, Type delegateType)
    {
        var plan = AccessToolsBinding.Plans.GetOrAdd(0, CreateAccessDelegatePlan);
        EmitAccessDelegatePlan(il, plan, method, null, null, null, delegateType);
    }

    private static void EmitAccessDelegatePlan(ILGenerator il, AccessDelegateInstruction[] plan,
        MethodInfo method, Type[] parameterTypes, Type[] delegateArguments, Type declaringType, Type delegateType)
    {
        for (var position = 0; position < plan.Length; position++)
        {
            var instruction = plan[position];
            if (instruction.Operand >= 5)
            {
                // These are CLR boundary queries, deliberately executed between IL emissions.
                var jump = instruction.Operand switch
                {
                    5 => !(declaringType != null && declaringType.IsValueType),
                    6 => instruction.Index >= delegateArguments.Length,
                    7 => delegateArguments[checked((int)instruction.Index)].IsByRef,
                    8 => !parameterTypes[checked((int)instruction.Index)].IsValueType,
                    9 => delegateArguments[checked((int)instruction.Index)].IsValueType,
                    10 => true,
                    _ => throw new RustBackendException("Modstoke Harmony received an invalid delegate condition."),
                };
                if (jump) position = checked((int)instruction.Target) - 1;
                continue;
            }
            var opcode = Current.Value.OpCodes[unchecked((short)instruction.OpCode)];
            switch (instruction.Operand)
            {
                case 0: il.Emit(opcode); break;
                case 1: il.Emit(opcode, checked((int)instruction.Index)); break;
                case 2: il.Emit(opcode, parameterTypes[checked((int)instruction.Index)]); break;
                case 3: il.Emit(opcode, method); break;
                case 4: il.Emit(opcode, delegateType.GetConstructor([typeof(object), typeof(IntPtr)])); break;
                default: throw new RustBackendException("Modstoke Harmony received an invalid delegate operand kind.");
            }
        }
    }
}
