using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using MonoMod.Utils.Cil;
using OpCode = System.Reflection.Emit.OpCode;
using CecilOpCode = Mono.Cecil.Cil.OpCode;

namespace HarmonyLib.Internal.Util
{
    internal static class EmitterExtensions
    {
        private static DynamicMethodDefinition emitDMD;
        private static MethodInfo emitDMDMethod;
        private static Action<CecilILGenerator, OpCode, object> emitCodeDelegate;

        [MethodImpl(MethodImplOptions.Synchronized)]
        static EmitterExtensions()
        {
            if (emitDMD != null)
                return;
            InitEmitterHelperDMD();
        }

        public static CecilOpCode GetCecilStoreOpCode(this Type t)
        {
            var realType = t;
            if (t.IsEnum)
                realType = Enum.GetUnderlyingType(realType);
            return RustKernel.ReflectionIndirect(realType, store: true);
        }

        public static CecilOpCode GetCecilLoadOpCode(this Type t)
        {
            var realType = t;
            if (t.IsEnum)
                realType = Enum.GetUnderlyingType(realType);
            return RustKernel.ReflectionIndirect(realType, store: false);
        }

        public static Type OpenRefType(this Type t)
        {
            if (t.IsByRef)
                return t.GetElementType();
            return t;
        }

        private static void InitEmitterHelperDMD()
        {
            emitDMD = new DynamicMethodDefinition("EmitOpcodeWithOperand", typeof(void),
                                                  new[] {typeof(CecilILGenerator), typeof(OpCode), typeof(object)});
            var il = emitDMD.GetILGenerator();

            var current = il.DefineLabel();

            EmitDispatchRecipe(il, RustKernel.ReflectionDispatchPrologue(), ref current);

            foreach (var method in typeof(CecilILGenerator).GetMethods().Where(m => m.Name == "Emit"))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 2)
                    continue;
                // Both ParameterType getters precede the OpCode comparison upstream.
                var first = parameters[0].ParameterType;
                var second = parameters[1].ParameterType;
                if (first != typeof(OpCode))
                    continue;
                EmitDispatchRecipe(il, RustKernel.ReflectionDispatchOverload(), ref current, second, method);
            }

            EmitDispatchRecipe(il, RustKernel.ReflectionDispatchEpilogue(), ref current);

            emitDMDMethod = emitDMD.Generate();
            emitCodeDelegate = emitDMDMethod
	                .CreateDelegate<Action<CecilILGenerator, OpCode, object>>();
        }

        private static void EmitDispatchRecipe(ILGenerator il, ReadOnlySpan<RustKernel.EmitterStep> recipe,
                                               ref Label current, Type operandType = null, MethodInfo method = null)
        {
            LocalBuilder local = null;
            foreach (var step in recipe)
            {
                switch (step.Operand)
                {
                    case RustKernel.EmitterOperand.None:
                        il.Emit(step.Code);
                        break;
                    case RustKernel.EmitterOperand.Type:
                        il.Emit(step.Code, operandType);
                        break;
                    case RustKernel.EmitterOperand.UnboxValueType:
                        if (operandType.IsValueType)
                            il.Emit(step.Code, operandType);
                        break;
                    case RustKernel.EmitterOperand.NextLabel:
                        il.Emit(step.Code, current);
                        break;
                    case RustKernel.EmitterOperand.StoreNewLocal:
                        local = il.DeclareLocal(operandType);
                        il.Emit(step.Code, local);
                        break;
                    case RustKernel.EmitterOperand.Local:
                        il.Emit(step.Code, local);
                        break;
                    case RustKernel.EmitterOperand.Method:
                        il.Emit(step.Code, method);
                        break;
                    case RustKernel.EmitterOperand.MarkLabel:
                        il.MarkLabel(current);
                        break;
                    case RustKernel.EmitterOperand.DefineLabel:
                        current = il.DefineLabel();
                        break;
                    case RustKernel.EmitterOperand.NullMessage:
                        il.Emit(step.Code, "Provided operand is null!");
                        break;
                    case RustKernel.EmitterOperand.UnsupportedMessage:
                        il.Emit(step.Code, "The operand is none of the supported types!");
                        break;
                    case RustKernel.EmitterOperand.ExceptionConstructor:
                        il.Emit(step.Code, typeof(Exception).GetConstructor(new[] {typeof(string)}));
                        break;
                }
            }
        }

        public static void Emit(this CecilILGenerator il, OpCode opcode, object operand)
        {
            emitCodeDelegate(il, opcode, operand);
        }

        public static void MarkBlockBefore(this CecilILGenerator il, ExceptionBlock block)
        {
            switch (block.blockType)
            {
                case ExceptionBlockType.BeginExceptionBlock:
                    il.BeginExceptionBlock();
                    return;
                case ExceptionBlockType.BeginCatchBlock:
                    il.BeginCatchBlock(block.catchType);
                    return;
                case ExceptionBlockType.BeginExceptFilterBlock:
                    il.BeginExceptFilterBlock();
                    return;
                case ExceptionBlockType.BeginFaultBlock:
                    il.BeginFaultBlock();
                    return;
                case ExceptionBlockType.BeginFinallyBlock:
                    il.BeginFinallyBlock();
                    return;
                case ExceptionBlockType.EndExceptionBlock:
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public static void MarkBlockAfter(this CecilILGenerator il, ExceptionBlock block)
        {
            if (block.blockType == ExceptionBlockType.EndExceptionBlock)
                il.EndExceptionBlock();
        }

        public static LocalBuilder GetLocal(this CecilILGenerator il, VariableDefinition varDef)
        {
            var vars = (Dictionary<LocalBuilder, VariableDefinition>) AccessTools
                                                                      .Field(typeof(CecilILGenerator), "_Variables")
                                                                      .GetValue(il);
            var loc = vars.FirstOrDefault(kv => kv.Value == varDef).Key;
            if (loc != null)
                return loc;
            // TODO: Remove once MonoMod allows to specify this manually
            var type = varDef.VariableType.ResolveReflection();
            var pinned = varDef.VariableType.IsPinned;
            var index = varDef.Index;
            loc = (LocalBuilder) (
                c_LocalBuilder_params == 4 ? c_LocalBuilder.Invoke(new object[] { index, type, null, pinned }) :
                c_LocalBuilder_params == 3 ? c_LocalBuilder.Invoke(new object[] { index, type, null }) :
                c_LocalBuilder_params == 2 ? c_LocalBuilder.Invoke(new object[] { type, null }) :
                c_LocalBuilder_params == 0 ? c_LocalBuilder.Invoke(new object[] { }) :
                throw new NotSupportedException()
            );

            f_LocalBuilder_position?.SetValue(loc, (ushort) index);
            f_LocalBuilder_is_pinned?.SetValue(loc, pinned);
            vars[loc] = varDef;
            return loc;
        }

        // https://github.com/MonoMod/MonoMod/commit/2011243901351e69b6b5da89631e01bc8eb61da5
        // https://github.com/dotnet/runtime/blob/f1332ab0d82ee0e21ca387cbd1c8a87c5dfa4906/src/coreclr/System.Private.CoreLib/src/System/Reflection/Emit/RuntimeLocalBuilder.cs
        // In .NET 9, LocalBuilder is an abstract type, so we look for RuntimeLocalBuilder first.
        private static readonly Type t_LocalBuilder =
            Type.GetType("System.Reflection.Emit.RuntimeLocalBuilder") ?? typeof(LocalBuilder);
        private static readonly ConstructorInfo c_LocalBuilder =
            t_LocalBuilder.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                .OrderByDescending(c => c.GetParameters().Length).First();
        private static readonly FieldInfo f_LocalBuilder_position =
            t_LocalBuilder.GetField("position", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo f_LocalBuilder_is_pinned =
            t_LocalBuilder.GetField("is_pinned", BindingFlags.NonPublic | BindingFlags.Instance);
        private static int c_LocalBuilder_params = c_LocalBuilder.GetParameters().Length;
    }
}
