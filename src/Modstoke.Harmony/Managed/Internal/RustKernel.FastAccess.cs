// Copyright (c) 2026 Alex Cooper. MIT license.
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum FastAccessKind { Instantiate, PropertyGet, FieldGet, PropertySet, FieldSet }

    private static class FastAccessRecipes
    {
        private static readonly delegate* unmanaged[Cdecl]<uint*, uint, uint> Read =
            (delegate* unmanaged[Cdecl]<uint*, uint, uint>)Export("mh_fastaccess_recipes_v1");

        internal static readonly (OpCode Opcode, uint Operand)[][] Plans = Load();

        private static (OpCode Opcode, uint Operand)[][] Load()
        {
            var values = new uint[25];
            fixed (uint* output = values)
                Check(Read(output, (uint)values.Length), "fast access recipes");

            var plans = new (OpCode Opcode, uint Operand)[5][];
            for (var operation = 0; operation < plans.Length; operation++)
            {
                var start = operation * 5;
                var count = values[start];
                if (count == 0 || count > 4)
                    throw new RustBackendException("Modstoke Harmony received an invalid fast access recipe length.");
                var plan = new (OpCode Opcode, uint Operand)[count];
                for (var i = 0; i < plan.Length; i++)
                {
                    var instruction = values[start + 1 + i];
                    var operand = instruction >> 16;
                    if (operand > 3 || !Current.Value.OpCodes.TryGetValue(unchecked((short)instruction), out var opcode))
                        throw new RustBackendException("Modstoke Harmony received an invalid fast access instruction.");
                    plan[i] = (opcode, operand);
                }
                plans[operation] = plan;
            }
            return plans;
        }
    }

    internal static void EmitFastAccess(ILGenerator generator, FastAccessKind kind, MemberInfo member)
    {
        foreach (var (opcode, operand) in FastAccessRecipes.Plans[(int)kind])
        {
            switch (operand)
            {
                case 0: generator.Emit(opcode); break;
                case 1: generator.Emit(opcode, (ConstructorInfo)member); break;
                case 2: generator.Emit(opcode, (MethodInfo)member); break;
                case 3: generator.Emit(opcode, (FieldInfo)member); break;
            }
        }
    }
}
