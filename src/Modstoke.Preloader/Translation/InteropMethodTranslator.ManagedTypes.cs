// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

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
        private bool IsManagedObjectConstructor(CilOpCode code, IMethodDescriptor method) =>
            code.Code == CilCode.Call && method.Name == ".ctor"
            && InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.DeclaringType, "System.Object")
            && method.Signature?.ParameterTypes.Count == 0 && target.IsConstructor && !target.IsStatic
            && ownerType.OriginalType == null && !ownerType.NewType.IsValueType
            && InteropRestoredUnmanagedDelegateSupport.IsSystemType(ownerType.NewType.BaseType, "System.Object")
            && TryGetStackValue(currentInstruction, 0, out var receiver)
            && receiver.Type?.Resolve() == sourceMethod.DeclaringType;

        private bool IsManagedExceptionConstructor(CilOpCode code, IMethodDescriptor method)
        {
            if (code.Code != CilCode.Call || method.Name != ".ctor" ||
                !InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.DeclaringType, "System.Exception") ||
                method.Signature is not { HasThis: true, GenericParameterCount: 0 } signature ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.ReturnType.ElementType != ElementType.Void ||
                signature.ParameterTypes.Count > 1 || signature.ParameterTypes.Count == 1 &&
                    signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.String } ||
                !sourceMethod.IsConstructor || sourceMethod.IsStatic || !target.IsConstructor || target.IsStatic ||
                ownerType.OriginalType != null || !InteropRestoredTypeSupport.IsManagedException(sourceMethod.DeclaringType) ||
                !SignatureComparer.Default.Equals(ownerType.NewType.BaseType?.ToTypeSignature(),
                    imports.Module.DefaultImporter.ImportTypeSignature(typeof(System.Exception)))) return false;
            return TryGetStackValue(currentInstruction, signature.ParameterTypes.Count, out var receiver) &&
                receiver.Type?.Resolve() == sourceMethod.DeclaringType;
        }

        private TypeSignature SourceParameterType(Parameter parameter)
        {
            if (parameter.Index >= 0 || sourceMethod.DeclaringType.GenericParameters.Count == 0)
                return parameter.ParameterType;
            var self = SourceDeclaringType(sourceMethod.DeclaringType);
            return sourceMethod.DeclaringType.IsValueType ? self.MakeByReferenceType() : self;
        }

        private static TypeSignature SourceDeclaringType(TypeDefinition owner)
        {
            if (owner.GenericParameters.Count == 0) return owner.ToTypeSignature();
            var self = new GenericInstanceTypeSignature(owner, owner.IsValueType);
            for (var index = 0; index < owner.GenericParameters.Count; index++)
                self.TypeArguments.Add(new GenericParameterSignature(owner.DeclaringModule, GenericParameterType.Type, index));
            return self;
        }

        private bool NativeMethodStorageCompatible(CilOpCode code, IMethodDescriptor method)
        {
            var plan = InteropRestorationPlan.For(context);
            if (plan.ContainsManagedOnlyStorage(method.Signature.ReturnType))
                return Fail("managed-storage-native-call", "A native method cannot return a CLR-only restored container.", currentInstruction);
            for (var index = 0; index < method.Signature.ParameterTypes.Count; index++)
            {
                if (plan.ContainsManagedOnlyStorage(method.Signature.ParameterTypes[index]) ||
                    TryGetStackValue(currentInstruction, method.Signature.ParameterTypes.Count - index - 1, out var value)
                    && plan.ContainsManagedOnlyStorage(value.Type))
                    return Fail("managed-storage-native-call", "A CLR-only restored container cannot be passed to a native method.", currentInstruction);
            }
            if (code.Code != CilCode.Newobj && method.Signature.HasThis &&
                TryGetStackValue(currentInstruction, method.Signature.ParameterTypes.Count, out var receiver))
            {
                var type = receiver.Type is ByReferenceTypeSignature reference ? reference.BaseType : receiver.Type;
                if (type != null && plan.GetNativeIdentityRejection(type) != null)
                    return Fail("managed-storage-native-receiver", "The receiver has no native object identity for this method.", currentInstruction);
            }
            return true;
        }

        private bool ValidateManagedStorageAssignment(CilInstruction instruction)
        {
            if (instruction.Operand is MethodSpecification spanMethod)
                foreach (var argument in spanMethod.Signature.TypeArguments)
                    if (InteropSpanLowering.ContainsSpan(context, argument))
                        return Fail("ref-like-generic-call",
                            "This generic call has no span lifetime contract.", instruction);
            if (nativeJobInstructions.ContainsKey(instruction) || sharedStaticFactories.ContainsKey(instruction)) return true;
            // The planned CLR Marshal call proves its concrete delegate; the source Delegate parameter is not native storage.
            if (unmanagedDelegateCalls.TryGetValue(instruction, out var marshal) && marshal.DelegateType != null) return true;
            if (instruction.Operand is ITypeDefOrRef operandType && !ValidateManagedGenericUse(operandType.ToTypeSignature()) ||
                instruction.Operand is IMemberDescriptor member && !ValidateManagedGenericUse(member.DeclaringType?.ToTypeSignature()))
                return false;
            if (instruction.Operand is MethodSpecification genericMethod)
                foreach (var argument in genericMethod.Signature.TypeArguments)
                    if (!ValidateManagedGenericUse(argument)) return false;
            if (instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt or CilCode.Newobj &&
                instruction.Operand is IMethodDescriptor method && method.Signature != null &&
                !IsManagedObjectConstructor(instruction.OpCode, method) && !IsBridgedDelegateConstructor(instruction))
                for (var index = 0; index < method.Signature.ParameterTypes.Count; index++)
                    if (!ManagedStorageAssignmentCompatible(instruction,
                        method.Signature.ParameterTypes[index].InstantiateGenericTypes(GenericContext.FromMethod(method)),
                        method.Signature.ParameterTypes.Count - index - 1))
                        return false;

            TypeSignature destination = instruction.OpCode.Code switch
            {
                CilCode.Stloc when instruction.Operand is CilLocalVariable local => local.VariableType,
                CilCode.Starg when instruction.Operand is AsmResolver.DotNet.Collections.Parameter parameter => parameter.ParameterType,
                CilCode.Stfld or CilCode.Stsfld when instruction.Operand is IFieldDescriptor field =>
                    field.Signature?.FieldType.InstantiateGenericTypes(GenericContext.FromField(field)),
                CilCode.Stobj when instruction.Operand is ITypeDefOrRef type => type.ToTypeSignature(),
                CilCode.Stind_Ref when TryGetStackValue(instruction, 1, out var address) &&
                    address.Type is ByReferenceTypeSignature reference => reference.BaseType,
                CilCode.Ret => sourceReturnType,
                _ => null
            };
            return ManagedStorageAssignmentCompatible(instruction, destination, 0);
        }

        private bool ManagedStorageAssignmentCompatible(CilInstruction instruction, TypeSignature destination, int depth)
        {
            if (destination == null || !TryGetStackValue(instruction, depth, out var value) || value.IsNull)
                return true;
            if (destination is GenericParameterSignature && value.Type?.FullName == "System.Object")
                return Fail("generic-unbox-required",
                    "A native boxed object cannot be stored as an unknown generic value without unbox.any.", instruction);
            var plan = InteropRestorationPlan.For(context);
            if (!plan.ContainsManagedOnlyStorage(value.Type) && !plan.ContainsManagedOnlyStorage(destination))
                return true;
            if (InteropManagedCollectionLowering.CanAssign(context, value.Type, destination)) return true;
            if (plan.CanAssignManagedReference(value.Type, destination)) return true;
            return SignatureComparer.Default.Equals(value.Type, destination) ||
                Fail("managed-storage-assignment", "CLR-only storage requires a proven managed assignment; native object/interface conversion is unavailable.", instruction);
        }
    }
}
