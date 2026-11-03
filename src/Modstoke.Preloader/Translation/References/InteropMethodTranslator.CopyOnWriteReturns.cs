// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private static bool TryGetCopyOnWriteFields(MethodDefinition original, CilInstruction[] code,
            out IFieldDescriptor root, out IFieldDescriptor value)
        {
            root = null;
            value = null;
            var body = original.CilMethodBody;
            if (body == null || body.ExceptionHandlers.Count != 0 || body.LocalVariables.Count != 3 ||
                original.Signature.ReturnType is not ByReferenceTypeSignature reference || code.Length != 30)
                return false;
            System.ReadOnlySpan<CilCode> pattern =
            [
                CilCode.Ldarg, CilCode.Ldfld, CilCode.Callvirt, CilCode.Ldc_I4, CilCode.Ceq,
                CilCode.Stloc, CilCode.Ldloc, CilCode.Brfalse, CilCode.Ldarg, CilCode.Ldfld,
                CilCode.Ldflda, CilCode.Stloc, CilCode.Br, CilCode.Ldarg, CilCode.Ldfld,
                CilCode.Stloc, CilCode.Ldarg, CilCode.Ldarg, CilCode.Ldfld, CilCode.Callvirt,
                CilCode.Stfld, CilCode.Ldloc, CilCode.Callvirt, CilCode.Ldarg, CilCode.Ldfld,
                CilCode.Ldflda, CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret
            ];
            for (var index = 0; index < code.Length; index++)
                if (InstructionCode(code[index]) != pattern[index]) return false;
            if (code[3].GetLdcI4Constant() != 1 ||
                !Local(5, 1) || !Local(6, 1) || !Local(11, 2) || !Local(15, 0) ||
                !Local(21, 0) || !Local(26, 2) || !Local(28, 2) ||
                !Branch(7, 13) || !Branch(12, 28) || !Branch(27, 28))
                return false;
            for (var index = 0; index < code.Length; index++)
                if (pattern[index] == CilCode.Ldarg && ForwardedArgumentIndex(code[index], original) != 0)
                    return false;

            if (code[1].Operand is not IFieldDescriptor sourceRoot ||
                code[10].Operand is not IFieldDescriptor sourceValue ||
                sourceRoot.Resolve() is not { IsStatic: false, IsInitOnly: false, IsLiteral: false } rootDefinition ||
                sourceValue.Resolve() is not { IsStatic: false, IsInitOnly: false, IsLiteral: false } valueDefinition ||
                rootDefinition.DeclaringType != original.DeclaringType ||
                sourceRoot.DeclaringType.Resolve() != original.DeclaringType ||
                !SignatureComparer.Default.Equals(sourceRoot.Signature, rootDefinition.Signature) ||
                !SignatureComparer.Default.Equals(sourceValue.Signature, valueDefinition.Signature))
                return false;
            TypeSignature sourceOwner = original.DeclaringType.ToTypeSignature();
            if (original.DeclaringType.GenericParameters.Count != 0)
                sourceOwner = new GenericInstanceTypeSignature(original.DeclaringType, original.DeclaringType.IsValueType,
                    Enumerable.Range(0, original.DeclaringType.GenericParameters.Count).Select(index =>
                        (TypeSignature)new GenericParameterSignature(original.DeclaringModule, GenericParameterType.Type, index)).ToArray());
            var holder = sourceRoot.Signature.FieldType.InstantiateGenericTypes(GenericContext.FromField(sourceRoot));
            if (!SignatureComparer.Default.Equals(sourceRoot.DeclaringType.ToTypeSignature(), sourceOwner) ||
                holder is not (TypeDefOrRefSignature or GenericInstanceTypeSignature) || holder.IsValueType ||
                holder.Resolve() is not { IsValueType: false, IsInterface: false } holderDefinition ||
                valueDefinition.DeclaringType != holderDefinition ||
                sourceValue.DeclaringType.Resolve() != holderDefinition ||
                !SignatureComparer.Default.Equals(sourceValue.DeclaringType.ToTypeSignature(), holder) ||
                !SignatureComparer.Default.Equals(sourceValue.Signature.FieldType.InstantiateGenericTypes(
                    GenericContext.FromField(sourceValue)), reference.BaseType) ||
                !SignatureComparer.Default.Equals(body.LocalVariables[0].VariableType, holder) ||
                !SignatureComparer.Default.Equals(body.LocalVariables[1].VariableType, original.DeclaringModule.CorLibTypeFactory.Boolean) ||
                !SignatureComparer.Default.Equals(body.LocalVariables[2].VariableType, reference))
                return false;
            for (var index = 0; index < code.Length; index++)
            {
                if (pattern[index] is CilCode.Ldfld or CilCode.Stfld)
                {
                    if (code[index].Operand is not IFieldDescriptor field ||
                        !SignatureComparer.Default.Equals(field, sourceRoot)) return false;
                }
                else if (pattern[index] == CilCode.Ldflda &&
                    (code[index].Operand is not IFieldDescriptor field ||
                        !SignatureComparer.Default.Equals(field, sourceValue))) return false;
            }
            if (!Call(2, "get_refCount", original.DeclaringModule.CorLibTypeFactory.Int32) ||
                !Call(19, "Copy", holder) || !Call(22, "Release", original.DeclaringModule.CorLibTypeFactory.Void))
                return false;
            root = sourceRoot;
            value = sourceValue;
            return true;

            bool Local(int position, int index) => code[position].OpCode.Code switch
            {
                CilCode.Ldloc_0 or CilCode.Stloc_0 => index == 0,
                CilCode.Ldloc_1 or CilCode.Stloc_1 => index == 1,
                CilCode.Ldloc_2 or CilCode.Stloc_2 => index == 2,
                CilCode.Ldloc or CilCode.Ldloc_S or CilCode.Stloc or CilCode.Stloc_S =>
                    ReferenceEquals(code[position].Operand, body.LocalVariables[index]),
                _ => false
            };

            bool Branch(int position, int destination)
            {
                if (code[position].Operand is not CilInstructionLabel { Instruction: not null } label) return false;
                var index = body.Instructions.IndexOf(label.Instruction);
                if (index < 0) return false;
                while (index < body.Instructions.Count && body.Instructions[index].OpCode.Code == CilCode.Nop) index++;
                return index < body.Instructions.Count && ReferenceEquals(body.Instructions[index], code[destination]);
            }

            bool Call(int position, string name, TypeSignature result)
            {
                if (code[position].Operand is not IMethodDescriptor method || method is MethodSpecification ||
                    method.Name != name || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                    !method.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                    method.Signature.ParameterTypes.Count != 0 ||
                    !SignatureComparer.Default.Equals(method.DeclaringType.ToTypeSignature(), holder) ||
                    !SignatureComparer.Default.Equals(method.Signature.ReturnType.InstantiateGenericTypes(
                        GenericContext.FromMethod(method)), result) ||
                    method.Resolve() is not { IsStatic: false, IsVirtual: false, IsAbstract: false, IsConstructor: false } definition)
                    return false;
                return definition.DeclaringType == holderDefinition && definition.GenericParameters.Count == 0 &&
                    SignatureComparer.Default.Equals(method.Signature, definition.Signature);
            }

            // Normalize only for comparison; source instructions and diagnostic offsets stay intact.
            static CilCode InstructionCode(CilInstruction instruction) => instruction.OpCode.Code switch
            {
                CilCode.Ldarg_0 or CilCode.Ldarg_S => CilCode.Ldarg,
                CilCode.Ldc_I4_1 or CilCode.Ldc_I4_S => CilCode.Ldc_I4,
                CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_S => CilCode.Ldloc,
                CilCode.Stloc_0 or CilCode.Stloc_1 or CilCode.Stloc_2 or CilCode.Stloc_S => CilCode.Stloc,
                CilCode.Brfalse_S => CilCode.Brfalse,
                CilCode.Br_S => CilCode.Br,
                _ => instruction.OpCode.Code
            };
        }
    }
}
