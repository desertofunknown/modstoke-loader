// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private static bool TryGetReadonlyReference(TypeSignature type, out ByReferenceTypeSignature reference)
        {
            reference = type is CustomModifierTypeSignature
            {
                IsRequired: true,
                BaseType: ByReferenceTypeSignature value
            } modifier && modifier.ModifierType.FullName == "System.Runtime.InteropServices.InAttribute"
                ? value : null;
            return reference != null;
        }

        private bool TryGetFieldReturn(CilInstruction instruction, out AddressOperation operation)
        {
            operation = null;
            if (instruction.OpCode.Code != CilCode.Call || instruction.Operand is not IMethodDescriptor method ||
                method is MethodSpecification || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                !method.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                method.Signature.ParameterTypes.Count != 0 ||
                !(method.Signature.ReturnType is ByReferenceTypeSignature || TryGetReadonlyReference(method.Signature.ReturnType, out _)) ||
                method.Resolve() is not { IsStatic: false, IsConstructor: false, IsVirtual: false,
                    IsAbstract: false, HasMethodBody: true } original ||
                !original.DeclaringType.IsValueType || original.GenericParameters.Count != 0 ||
                (original.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL ||
                original.CilMethodBody.ExceptionHandlers.Count != 0 ||
                !SignatureComparer.Default.Equals(method.Signature, original.Signature))
                return false;

            var sourceOwner = method.DeclaringType.ToTypeSignature();
            var readOnly = TryGetReadonlyReference(method.Signature.ReturnType, out _);
            var resultType = method.Signature.ReturnType.InstantiateGenericTypes(GenericContext.FromMethod(method));
            var reference = resultType as ByReferenceTypeSignature;
            if (readOnly) TryGetReadonlyReference(resultType, out reference);
            if (original.DeclaringType.GenericParameters.Count !=
                    (sourceOwner is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0) ||
                reference == null || !TryGetAddressElement(reference.BaseType, out var element))
                return false;
            var type = FindMappedSourceOwner(sourceOwner);
            var mapped = type?.TryGetMethodByUnityAssemblyMethod(original);
            var owner = resolver.ResolveRaw(sourceOwner);
            if (type?.OriginalType == null || mapped?.NonGenericMethodInfoPointerField == null ||
                owner?.Resolve() != type.NewType ||
                mapped.NewMethod.Signature.ReturnType.InstantiateGenericTypes(GenericContext.FromType(owner)) is not
                    ByReferenceTypeSignature mappedReturn || !SignatureComparer.Default.Equals(mappedReturn.BaseType, element))
                return false;

            var code = original.CilMethodBody.Instructions.Where(item => item.OpCode.Code != CilCode.Nop).ToArray();
            IFieldDescriptor sourceRoot;
            IFieldDescriptor sourceField;
            if (readOnly)
            {
                if (code.Length != 4 || ForwardedArgumentIndex(code[0], original) != 0 ||
                    code[1].OpCode.Code != CilCode.Ldfld || code[1].Operand is not IFieldDescriptor rootField ||
                    rootField.DeclaringType.Resolve() != original.DeclaringType ||
                    code[2].OpCode.Code != CilCode.Ldflda || code[2].Operand is not IFieldDescriptor sourceValue ||
                    code[3].OpCode.Code != CilCode.Ret)
                    return false;
                sourceRoot = rootField;
                sourceField = sourceValue;
            }
            else if (!TryGetCopyOnWriteFields(original, code, out sourceRoot, out sourceField) ||
                !TryGetNativeValueWrapper(sourceOwner, out var receiverOwner) ||
                !SignatureComparer.Default.Equals(receiverOwner, owner))
                return false;
            var root = new MemberReference(sourceOwner.ToTypeDefOrRef(), sourceRoot.Name, sourceRoot.Signature);
            if (!TryGetNestedReferenceField(root, sourceOwner.MakeByReferenceType(), out var ownerField) ||
                !IsNativeReferenceValue(ownerField) || ownerField.Element.IsValueType ||
                !SignatureComparer.Default.Equals(sourceField.DeclaringType.ToTypeSignature()
                    .InstantiateGenericTypes(GenericContext.FromMethod(method)), ownerField.SourceElement))
                return false;
            IFieldDescriptor field = new MemberReference(ownerField.SourceElement.ToTypeDefOrRef(), sourceField.Name, sourceField.Signature);
            if (!SignatureComparer.Default.Equals(field.Signature.FieldType.InstantiateGenericTypes(GenericContext.FromField(field)),
                    reference.BaseType))
                return false;
            var storage = FindMappedSourceOwner(ownerField.SourceElement);
            var valueField = storage?.Fields.SingleOrDefault(candidate =>
                candidate.OriginalField.Name == field.Name &&
                candidate.OriginalField.Signature.FieldType.FullName == field.Signature.FieldType.FullName);
            if (storage?.OriginalType == null || storage.NewType != ownerField.Element.Resolve() ||
                valueField?.PointerField == null || valueField.OriginalField.IsStatic || valueField.OriginalField.IsLiteral ||
                !readOnly && valueField.OriginalField.IsInitOnly ||
                storage.NewType.Fields.Any(candidate => candidate.Name == field.Name || candidate.Name == valueField.UnmangledName))
                return false;
            var property = storage.NewType.Properties.SingleOrDefault(candidate => candidate.Name == valueField.UnmangledName);
            var getter = AddressAccessor(ownerField.Element, property?.GetMethod);
            if (getter == null || !getter.Signature.HasThis || getter.Signature.ParameterTypes.Count != 0 ||
                !SignatureComparer.Default.Equals(getter.Signature.ReturnType.InstantiateGenericTypes(GenericContext.FromMethod(getter)), element))
                return false;
            ownerField.ReadOnly = true;
            var result = new AddressOperation
            {
                SourceElement = reference.BaseType,
                Element = element,
                OwnerType = ownerField.Element,
                Getter = getter,
                NativeFieldPointerName = valueField.PointerField.Name,
                ReturnedOwnerField = ownerField,
                ReturnedMethodPointerName = readOnly ? null : mapped.NonGenericMethodInfoPointerField.Name,
                ReadOnly = readOnly
            };
            if (!IsNativeStorageValue(result)) return false;
            operation = result;
            return true;
        }

        private bool TryPlanFieldReturn(IMethodDescriptor method, ReferenceOrigins[] consumed,
            AddressOperation returned, out ReferenceUse use)
        {
            use = null;
            if (consumed.Length != 1 || !TryGetReferenceLocation(consumed[0], out var receiver, out _) ||
                !IsNativeStorageValue(receiver) ||
                !(receiver.IsScopedParameter || receiver.IsArray ||
                    (receiver.OwnerType != null || receiver.StaticOwnerType != null) && receiver.NativeFieldPointerName != null) ||
                !SignatureComparer.Default.Equals(receiver.SourceElement, method.DeclaringType.ToTypeSignature()) ||
                !SignatureComparer.Default.Equals(receiver.Element, returned.ReturnedOwnerField.OwnerType))
                return false;
            if (returned.ReturnedMethodPointerName != null &&
                !TryGetWritableCallLocation(consumed[0], receiver.SourceElement, out _, allowAggregate: true, allowStatic: true))
                return false;
            use = new ReferenceUse { Location = receiver };
            return true;
        }
    }
}
