// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanForwardedInternalReferenceCall(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var owner = method.DeclaringType?.Resolve();
            if (instruction.OpCode.Code != CilCode.Call || owner == null || !owner.IsValueType ||
                owner.GenericParameters.Count != 0 || !method.Signature.HasThis ||
                consumed.Length != method.Signature.ParameterTypes.Count + 1 || consumed[0] == null)
                return false;
            var type = context.TryGetAssemblyByName(owner.DeclaringModule.Assembly.Name)?.TryGetTypeByName(owner.FullName);
            if (type?.OriginalType == null || resolver.ResolveRaw(owner.ToTypeSignature())?.Resolve() != type.NewType)
                return false;

            var visited = new HashSet<MethodDefinition>();
            var current = method;
            while (current is not MethodSpecification && current.Resolve() is { } definition && visited.Add(definition))
            {
                if (definition.DeclaringType != owner || definition.IsConstructor || definition.IsVirtual ||
                    definition.IsAbstract || definition.GenericParameters.Count != 0 ||
                    definition.Signature.GenericParameterCount != 0 ||
                    definition.IsStatic != !definition.Signature.HasThis ||
                    !InteropDelegateContracts.HasStandardConvention(definition.Signature) ||
                    !SignatureComparer.Default.Equals(current.Signature, definition.Signature) ||
                    (definition.ImplAttributes & MethodImplAttributes.Synchronized) != 0)
                    return false;
                if ((definition.ImplAttributes & MethodImplAttributes.InternalCall) != 0)
                    return !definition.HasMethodBody &&
                        TryPlanInternalReferenceCall(instruction, current, consumed, out use);
                // Follow only restored bodies. Existing native methods keep their native dispatch.
                if (type.TryGetRestoredBody(definition) == null ||
                    !TryGetArgumentForwarder(definition, out current))
                    return false;
            }
            return false;
        }

        private static bool TryGetArgumentForwarder(MethodDefinition source, out IMethodDescriptor target)
        {
            target = null;
            if (!source.HasMethodBody || source.CilMethodBody.ExceptionHandlers.Count != 0 ||
                (source.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
                return false;
            var code = source.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            var count = source.Signature.ParameterTypes.Count + (source.Signature.HasThis ? 1 : 0);
            if (code.Length < count + 2 || code[count].OpCode.Code != CilCode.Call ||
                code[count].Operand is not IMethodDescriptor next || next is MethodSpecification ||
                next.DeclaringType?.Resolve() != source.DeclaringType ||
                next.Signature == null || next.Signature.GenericParameterCount != 0 ||
                !InteropDelegateContracts.HasStandardConvention(next.Signature) ||
                !SignatureComparer.Default.Equals(source.Signature.ReturnType, next.Signature.ReturnType) ||
                next.Signature.ParameterTypes.Count + (next.Signature.HasThis ? 1 : 0) != count)
                return false;
            for (var index = 0; index < count; index++)
            {
                if (ForwardedArgumentIndex(code[index], source) != index ||
                    !SignatureComparer.Default.Equals(ForwardedArgumentType(source, index), ForwardedArgumentType(next, index)))
                    return false;
            }
            if (!HasForwardedReturn(source, code, count + 1)) return false;
            target = next;
            return true;
        }

        private static TypeSignature ForwardedArgumentType(IMethodDescriptor method, int index)
        {
            if (!method.Signature.HasThis) return method.Signature.ParameterTypes[index];
            return index == 0 ? method.DeclaringType.ToTypeSignature().MakeByReferenceType()
                : method.Signature.ParameterTypes[index - 1];
        }

        private static int ForwardedArgumentIndex(CilInstruction instruction, MethodDefinition method) =>
            instruction.OpCode.Code switch
            {
                CilCode.Ldarg_0 => 0,
                CilCode.Ldarg_1 => 1,
                CilCode.Ldarg_2 => 2,
                CilCode.Ldarg_3 => 3,
                CilCode.Ldarg or CilCode.Ldarg_S when instruction.Operand is Parameter parameter =>
                    parameter.MethodSignatureIndex,
                _ => -1
            };

        private static bool HasForwardedReturn(MethodDefinition method, CilInstruction[] code, int start)
        {
            var locals = method.CilMethodBody.LocalVariables;
            if (locals.Count == 0)
                return code.Length == start + 1 && code[start].OpCode.Code == CilCode.Ret;
            if (locals.Count != 1 || method.Signature.ReturnType.ElementType == ElementType.Void ||
                !SignatureComparer.Default.Equals(locals[0].VariableType, method.Signature.ReturnType) ||
                code.Length < start + 3 || !IsForwardedReturnLocal(code[start], locals[0], true))
                return false;
            var load = start + 1;
            if (code[load].OpCode.Code is CilCode.Br or CilCode.Br_S)
            {
                if (code[load].Operand is not CilInstructionLabel label || label.Instruction != code[load + 1]) return false;
                load++;
            }
            return code.Length == load + 2 && IsForwardedReturnLocal(code[load], locals[0], false) &&
                code[load + 1].OpCode.Code == CilCode.Ret;
        }

        private static bool IsForwardedReturnLocal(CilInstruction instruction, CilLocalVariable local, bool store) =>
            store
                ? instruction.OpCode.Code == CilCode.Stloc_0 ||
                    instruction.OpCode.Code is CilCode.Stloc or CilCode.Stloc_S && ReferenceEquals(instruction.Operand, local)
                : instruction.OpCode.Code == CilCode.Ldloc_0 ||
                    instruction.OpCode.Code is CilCode.Ldloc or CilCode.Ldloc_S && ReferenceEquals(instruction.Operand, local);
    }
}
