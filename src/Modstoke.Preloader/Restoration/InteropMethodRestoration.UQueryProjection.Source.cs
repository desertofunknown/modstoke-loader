// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private static bool TryGetUQueryProjectionSource(MethodDefinition source, out MethodDefinition traversal)
        {
            traversal = null;
            var type = source.DeclaringType;
            if (source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                source.MetadataToken.ToInt32() != 0x06001DF0 ||
                !type.IsValueType || type.IsEnum || !type.IsPublic || !type.IsSealed || !type.IsSequentialLayout ||
                type.DeclaringType != null || !HasUQueryElementConstraint(type) ||
                !HasUQueryTraversalSignature(source, 1, 2) ||
                source.GenericParameters[0].Attributes != GenericParameterAttributes.NonVariant ||
                source.GenericParameters[0].Constraints.Count != 0 ||
                source.Signature.ParameterTypes[0] is not GenericInstanceTypeSignature list ||
                source.Signature.ParameterTypes[1] is not GenericInstanceTypeSignature selector ||
                !IsUQuerySourceGeneric(list, "System.Collections.Generic.List`1", 1) ||
                !IsUQuerySourceGeneric(selector, "System.Func`2", 2) ||
                list.TypeArguments[0] is not GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 } ||
                selector.TypeArguments[0] is not GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 } ||
                !SignatureComparer.Default.Equals(list.TypeArguments[0], selector.TypeArguments[1])) return false;

            var action = type.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == 0x06001DEF);
            if (action == null || action.Name != source.Name || !HasUQueryTraversalSignature(action, 0, 1) ||
                action.Signature.ParameterTypes[0] is not GenericInstanceTypeSignature callback ||
                !IsUQuerySourceGeneric(callback, "System.Action`1", 1) ||
                !SignatureComparer.Default.Equals(callback.TypeArguments[0], selector.TypeArguments[0]) ||
                !HasUQueryTraversalBody(action, false) || !HasUQueryTraversalBody(source, true)) return false;

            var actionMatcher = action.CilMethodBody.LocalVariables[0].VariableType.Resolve();
            var projectionMatcher = source.CilMethodBody.LocalVariables[0].VariableType.Resolve();
            if (!HasUQueryMatcher(actionMatcher, type, false) || !HasUQueryMatcher(projectionMatcher, type, true) ||
                !HasUQueryMatchBody(actionMatcher, false) || !HasUQueryMatchBody(projectionMatcher, true)) return false;

            traversal = action;
            return true;
        }

        private static bool HasUQueryTraversalSignature(MethodDefinition method, int arity, int parameters) =>
            method.Attributes == (MethodAttributes.Public | MethodAttributes.HideBySig) &&
            method.ImplAttributes == MethodImplAttributes.IL && method.CilMethodBody != null &&
            InteropDelegateContracts.HasStandardConvention(method.Signature) && method.Signature.HasThis &&
            method.Signature.GenericParameterCount == arity && method.GenericParameters.Count == arity &&
            method.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void } &&
            method.Signature.ParameterTypes.Count == parameters && method.Parameters.Count == parameters &&
            method.Parameters.All(parameter => parameter.Definition is { } definition &&
                definition.Attributes == 0 && definition.MarshalDescriptor == null && definition.Constant == null);

        private static bool IsUQuerySourceGeneric(GenericInstanceTypeSignature type, string name, int arity) =>
            !type.IsValueType && type.GenericType.FullName == name && type.TypeArguments.Count == arity &&
            InteropRestoredTypeSupport.GetAssemblyName(type) is "netstandard" or "mscorlib";

        private static bool HasUQueryTraversalBody(MethodDefinition method, bool projection)
        {
            var body = method.CilMethodBody;
            var code = body.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] expected = projection
                ? [CilCode.Ldsfld, CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Callvirt, CilCode.Ldnull,
                    CilCode.Cgt_Un, CilCode.Stloc_1, CilCode.Ldloc_1, CilCode.Brfalse_S, CilCode.Newobj,
                    CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Ldarg_2, CilCode.Callvirt, CilCode.Ldloc_0,
                    CilCode.Ldarg_1, CilCode.Callvirt, CilCode.Ldloc_0, CilCode.Ldarg_0, CilCode.Ldfld,
                    CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Callvirt, CilCode.Leave_S, CilCode.Ldloc_0,
                    CilCode.Ldnull, CilCode.Callvirt, CilCode.Ldloc_0, CilCode.Ldnull, CilCode.Callvirt,
                    CilCode.Endfinally, CilCode.Ret]
                : [CilCode.Ldsfld, CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Callvirt, CilCode.Ldnull,
                    CilCode.Cgt_Un, CilCode.Stloc_1, CilCode.Ldloc_1, CilCode.Brfalse_S, CilCode.Newobj,
                    CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Ldarg_1, CilCode.Callvirt, CilCode.Ldloc_0,
                    CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Callvirt,
                    CilCode.Leave_S, CilCode.Ldloc_0, CilCode.Ldnull, CilCode.Callvirt, CilCode.Endfinally, CilCode.Ret];
            if (!HasUQueryOpcodes(code, expected) || !body.InitializeLocals || body.LocalVariables.Count != 2 ||
                body.LocalVariables[1].VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                body.LocalVariables[0].VariableType is not GenericInstanceTypeSignature matcher || matcher.IsValueType ||
                matcher.GenericType.DeclaringType?.Resolve() != method.DeclaringType ||
                matcher.TypeArguments.Count != (projection ? 2 : 1) ||
                matcher.TypeArguments[0] is not GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 } ||
                projection && matcher.TypeArguments[1] is not GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 } ||
                !IsUQueryTarget(code, code[8].Operand, 11) ||
                !IsUQueryTarget(code, code[projection ? 23 : 20].Operand, code.Length - 1) ||
                body.ExceptionHandlers.Count != 1) return false;
            var cleanup = body.ExceptionHandlers[0];
            if (cleanup.HandlerType != CilExceptionHandlerType.Finally || cleanup.ExceptionType != null ||
                cleanup.FilterStart != null || !IsUQueryTarget(code, cleanup.TryStart, 11) ||
                !IsUQueryTarget(code, cleanup.TryEnd, projection ? 24 : 21) ||
                !IsUQueryTarget(code, cleanup.HandlerStart, projection ? 24 : 21) ||
                !IsUQueryTarget(code, cleanup.HandlerEnd, code.Length - 1)) return false;

            var callback = method.Signature.ParameterTypes[projection ? 1 : 0];
            var voidType = method.DeclaringModule.CorLibTypeFactory.Void;
            var getter = projection ? 0x06001E04 : 0x06001E00;
            var setter = getter + 1;
            if (!IsUQueryField(code[0], method.DeclaringModule, projection ? 0x04000C72 : 0x04000C69) ||
                !IsUQueryMethod(code[3], matcher, getter, "get_callBack", callback) ||
                !IsUQueryMethod(code[9], matcher, projection ? 0x06001E09 : 0x06001E03, ".ctor", voidType) ||
                !IsUQueryMethod(code[13], matcher, setter, "set_callBack", voidType, callback) ||
                !IsUQueryMethod(code[projection ? 26 : 23], matcher, setter, "set_callBack", voidType, callback) ||
                !IsUQueryField(code[projection ? 19 : 16], method.DeclaringModule, 0x04000C6A) ||
                !IsUQueryField(code[projection ? 21 : 18], method.DeclaringModule, 0x04000C6B) ||
                code[projection ? 22 : 19].Operand is not IMethodDescriptor run ||
                run.Resolve()?.DeclaringModule != method.DeclaringModule || run.Resolve().MetadataToken.ToInt32() != 0x06001DCE)
                return false;
            return !projection ||
                IsUQueryMethod(code[16], matcher, 0x06001E07, "set_result", voidType, method.Signature.ParameterTypes[0]) &&
                IsUQueryMethod(code[29], matcher, 0x06001E07, "set_result", voidType, method.Signature.ParameterTypes[0]);
        }

        private static bool HasUQueryMatcher(TypeDefinition matcher, TypeDefinition owner, bool projection)
        {
            if (matcher == null || matcher.DeclaringType != owner || matcher.DeclaringModule != owner.DeclaringModule ||
                matcher.Name != (projection ? "DelegateQueryMatcher`1" : "ActionQueryMatcher") ||
                matcher.BaseType?.Name != "UQueryMatcher" ||
                matcher.BaseType.DeclaringType?.FullName != "UnityEngine.UIElements.UQuery" ||
                matcher.BaseType.Resolve()?.DeclaringModule != owner.DeclaringModule || matcher.Interfaces.Count != 0 ||
                matcher.GenericParameters.Count != (projection ? 2 : 1) ||
                matcher.GenericParameters[0].Attributes != GenericParameterAttributes.NonVariant ||
                matcher.GenericParameters[0].Constraints.Count != 1 ||
                !SignatureComparer.Default.Equals(matcher.GenericParameters[0].Constraints[0].Constraint,
                    owner.GenericParameters[0].Constraints[0].Constraint) ||
                projection && (matcher.GenericParameters[1].Attributes != GenericParameterAttributes.NonVariant ||
                    matcher.GenericParameters[1].Constraints.Count != 0)) return false;

            var first = projection ? 0x06001E04 : 0x06001E00;
            var field = projection ? 0x04000C70 : 0x04000C6F;
            // The replacement captures the same fields; reject accessors with additional behavior.
            for (var offset = 0; offset < (projection ? 4 : 2); offset++)
            {
                var accessor = matcher.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == first + offset);
                if (accessor?.CilMethodBody is not { } body || accessor.IsStatic || accessor.IsVirtual ||
                    body.ExceptionHandlers.Count != 0 || body.LocalVariables.Count != 0) return false;
                var setter = (offset & 1) != 0;
                var code = body.Instructions.ToArray();
                if (!HasUQueryOpcodes(code, setter ? [CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Stfld, CilCode.Ret]
                        : [CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Ret]) ||
                    !IsUQueryField(code[setter ? 2 : 1], owner.DeclaringModule, field + offset / 2)) return false;
            }
            var constructor = matcher.Methods.FirstOrDefault(method => method.Name == ".ctor");
            if (constructor?.CilMethodBody is not { } constructorBody || constructor.IsStatic ||
                constructor.Signature.ParameterTypes.Count != 0 || constructorBody.ExceptionHandlers.Count != 0 ||
                constructorBody.LocalVariables.Count != 0) return false;
            var ctor = constructorBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            return HasUQueryOpcodes(ctor, [CilCode.Ldarg_0, CilCode.Call, CilCode.Ret]) &&
                ctor[1].Operand is IMethodDescriptor initialize && initialize.Resolve()?.DeclaringModule == owner.DeclaringModule &&
                initialize.Resolve().MetadataToken.ToInt32() == 0x06001DC9;
        }

        private static bool HasUQueryMatchBody(TypeDefinition matcher, bool projection)
        {
            var method = matcher.Methods.FirstOrDefault(candidate => candidate.MetadataToken.ToInt32() ==
                (projection ? 0x06001E08 : 0x06001E02));
            if (method?.CilMethodBody is not { } body || method.Name != "OnRuleMatchedElement" ||
                method.Attributes != (MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig) ||
                method.ImplAttributes != MethodImplAttributes.IL || method.GenericParameters.Count != 0 ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || !method.Signature.HasThis ||
                method.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                method.Signature.ParameterTypes.Count != 2 ||
                method.Signature.ParameterTypes[0].FullName != "UnityEngine.UIElements.RuleMatcher" ||
                method.Signature.ParameterTypes[1].FullName != "UnityEngine.UIElements.VisualElement" ||
                method.Signature.ParameterTypes.Any(parameter => parameter.Resolve()?.DeclaringModule != matcher.DeclaringModule) ||
                body.ExceptionHandlers.Count != 0 || !body.InitializeLocals || body.LocalVariables.Count != 3 ||
                body.LocalVariables[0].VariableType is not GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 } ||
                body.LocalVariables.Skip(1).Any(local => local.VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean }))
                return false;
            var code = body.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] expected = projection
                ? [CilCode.Ldarg_2, CilCode.Isinst, CilCode.Unbox_Any, CilCode.Stloc_0, CilCode.Ldloc_0,
                    CilCode.Box, CilCode.Ldnull, CilCode.Cgt_Un, CilCode.Stloc_1, CilCode.Ldloc_1, CilCode.Brfalse_S,
                    CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_0, CilCode.Call, CilCode.Ldloc_0,
                    CilCode.Callvirt, CilCode.Callvirt, CilCode.Ldc_I4_0, CilCode.Stloc_2, CilCode.Br_S, CilCode.Ldloc_2, CilCode.Ret]
                : [CilCode.Ldarg_2, CilCode.Isinst, CilCode.Unbox_Any, CilCode.Stloc_0, CilCode.Ldloc_0,
                    CilCode.Box, CilCode.Ldnull, CilCode.Cgt_Un, CilCode.Stloc_1, CilCode.Ldloc_1, CilCode.Brfalse_S,
                    CilCode.Ldarg_0, CilCode.Call, CilCode.Ldloc_0, CilCode.Callvirt, CilCode.Ldc_I4_0,
                    CilCode.Stloc_2, CilCode.Br_S, CilCode.Ldloc_2, CilCode.Ret];
            if (!HasUQueryOpcodes(code, expected) || !IsUQueryTarget(code, code[10].Operand, projection ? 18 : 15) ||
                !IsUQueryTarget(code, code[^3].Operand, code.Length - 2) ||
                new[] { 1, 2, 5 }.Any(index => code[index].Operand is not ITypeDefOrRef type ||
                    type.ToTypeSignature() is not GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 }) ||
                code[projection ? 14 : 12].Operand is not IMethodDescriptor getter ||
                getter.Resolve()?.DeclaringType != matcher ||
                getter.Resolve().MetadataToken.ToInt32() != (projection ? 0x06001E04 : 0x06001E00) ||
                getter.Signature.ReturnType is not GenericInstanceTypeSignature callback ||
                code[projection ? 16 : 14].Operand is not IMethodDescriptor invoke ||
                !HasUQuerySourceMember(invoke, callback, "Invoke", true,
                    projection ? callback.TypeArguments[1] : matcher.DeclaringModule.CorLibTypeFactory.Void,
                    callback.TypeArguments[0])) return false;
            if (!projection) return true;
            return code[12].Operand is IMethodDescriptor getResult && getResult.Resolve()?.DeclaringType == matcher &&
                getResult.Resolve().MetadataToken.ToInt32() == 0x06001E06 &&
                getResult.Signature.ReturnType is GenericInstanceTypeSignature list &&
                code[17].Operand is IMethodDescriptor append &&
                HasUQuerySourceMember(append, list, "Add", true, matcher.DeclaringModule.CorLibTypeFactory.Void, callback.TypeArguments[1]);
        }

        private static bool HasUQueryOpcodes(CilInstruction[] code, CilCode[] expected) =>
            code.Length == expected.Length && code.Select(instruction => instruction.OpCode.Code).SequenceEqual(expected);

        private static bool IsUQueryTarget(CilInstruction[] code, object target, int expected) =>
            target is CilInstructionLabel { Instruction: { } instruction } &&
            code.FirstOrDefault(candidate => candidate.Offset >= instruction.Offset) == code[expected];

        private static bool IsUQueryField(CilInstruction instruction, ModuleDefinition module, int token) =>
            instruction.Operand is IFieldDescriptor field && field.Resolve() is { } definition &&
            definition.DeclaringModule == module && definition.MetadataToken.ToInt32() == token &&
            SignatureComparer.Default.Equals(field.Signature, definition.Signature);

        private static bool IsUQueryMethod(CilInstruction instruction, TypeSignature owner, int token,
            string name, TypeSignature result, params TypeSignature[] parameters) =>
            instruction.Operand is IMethodDescriptor method && method.Resolve() is { } definition &&
            definition.DeclaringModule == owner.Resolve()?.DeclaringModule && definition.MetadataToken.ToInt32() == token &&
            HasUQuerySourceMember(method, owner, name, true, result, parameters);
    }
}
