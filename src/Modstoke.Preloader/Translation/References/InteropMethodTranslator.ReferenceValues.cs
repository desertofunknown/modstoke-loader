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
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryGetRectangularReference(CilInstruction instruction, out AddressOperation operation)
        {
            operation = null;
            if (instruction.OpCode.Code is not (CilCode.Call or CilCode.Callvirt) ||
                instruction.Operand is not IMethodDescriptor method || method is MethodSpecification ||
                method.Name != "Address" || method.Signature is not { HasThis: true } signature ||
                signature.GenericParameterCount != 0 ||
                method.DeclaringType?.ToTypeSignature() is not ArrayTypeSignature array ||
                signature.ParameterTypes.Count != array.Rank ||
                signature.ParameterTypes.Any(type => type.ElementType != ElementType.I4) ||
                signature.ReturnType is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, array.BaseType) ||
                !InteropTypeReferences.IsValueType(array.BaseType) ||
                !TryGetAddressElement(array.BaseType, out var element) ||
                !InteropRectangularArraySupport.TryResolve(context, imports, array, out var owner, out _) ||
                !TryGetStackValue(instruction, array.Rank, out var receiver) ||
                !receiver.IsNull && !SameRectangularShape(receiver.Type, array))
                return false;
            for (var index = 0; index < array.Rank; index++)
                if (!TryGetStackValue(instruction, index, out var value) || value.IsNull ||
                    NumericStackValue(value.Type).Type?.ElementType != ElementType.I4)
                    return false;
            var candidate = new AddressOperation
            {
                SourceElement = array.BaseType,
                Element = element,
                OwnerType = owner,
                IndexType = imports.Module.CorLibTypeFactory.Int32,
                RectangularRank = array.Rank
            };
            if (!IsNativeStorageValue(candidate)) return false;
            operation = candidate;
            return true;
        }

        private bool TryGetNestedReferenceField(CilInstruction instruction, out AddressOperation operation, int receiverDepth = 0)
        {
            operation = null;
            return instruction.Operand is IFieldDescriptor field &&
                TryGetStackValue(instruction, receiverDepth, out var receiver) &&
                TryGetNestedReferenceField(field, receiver.Type, out operation);
        }

        private bool TryGetNestedReferenceField(IFieldDescriptor field, TypeSignature receiver, out AddressOperation operation)
        {
            operation = null;
            if (field.Signature == null ||
                field.DeclaringType?.ToTypeSignature() is not { } sourceOwner ||
                !InteropTypeReferences.IsValueType(sourceOwner) ||
                !TryGetAddressElement(sourceOwner, out var owner) ||
                receiver is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, sourceOwner))
                return false;
            var sourceElement = field.Signature.FieldType.InstantiateGenericTypes(GenericContext.FromField(field));
            if (!TryGetAddressElement(sourceElement, out var element))
                return false;
            var definition = resolver.ResolveRaw(sourceOwner)?.Resolve();
            if (definition == null || definition.GenericParameters.Count !=
                (sourceOwner is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0))
                return false;
            var mappedType = context.GetContextForNewType(definition);
            if (mappedType.OriginalType == null || mappedType.ComputedTypeSpecifics is not
                (InteropTypeContext.TypeSpecifics.BlittableStruct or InteropTypeContext.TypeSpecifics.NonBlittableStruct))
                return false;
            var mapped = mappedType.Fields.SingleOrDefault(fieldCandidate =>
                fieldCandidate.OriginalField.Name == field.Name &&
                fieldCandidate.OriginalField.Signature.FieldType.FullName == field.Signature.FieldType.FullName);
            if (mapped?.PointerField == null || mapped.OriginalField.IsStatic || mapped.OriginalField.IsLiteral)
                return false;
            var candidate = new AddressOperation
            {
                SourceElement = sourceElement,
                Element = element,
                OwnerType = owner,
                NativeFieldPointerName = mapped.PointerField.Name,
                IsNestedField = true,
                ReadOnly = mapped.OriginalField.IsInitOnly
            };
            if (!IsNativeStorageValue(candidate) && !IsNativeReferenceValue(candidate)) return false;
            operation = candidate;
            return true;
        }

        private bool ValidateNativeValueReferenceConsumer(AddressOperation operation)
        {
            var referenceValue = IsNativeReferenceValue(operation);
            if (!IsBlittableAddressValue(operation) && !referenceValue || operation.ReadOnly && operation.IsWrite) return false;
            var consumer = operation.Consumer;
            if (!TryGetStackValue(consumer, consumer.OpCode.Code == CilCode.Stobj ? 1 : 0, out var address) ||
                address.Type is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, operation.SourceElement))
                return false;
            if (consumer.OpCode.Code == CilCode.Ldfld)
                return !referenceValue && consumer.Operand is IFieldDescriptor member && member.Signature != null &&
                    member.Resolve()?.IsStatic == false &&
                    SignatureComparer.Default.Equals(member.DeclaringType.ToTypeSignature(), operation.SourceElement) &&
                    TryGetAddressElement(member.Signature.FieldType.InstantiateGenericTypes(GenericContext.FromField(member)), out _);
            if (consumer.Operand is not ITypeDefOrRef typed ||
                !SignatureComparer.Default.Equals(typed.ToTypeSignature(), operation.SourceElement))
                return false;
            if (consumer.OpCode.Code != CilCode.Stobj) return true;
            if (referenceValue)
                return TryGetStackValue(consumer, 0, out var storedReference) && IsReferenceStoreValue(storedReference, operation);
            return TryGetStackValue(consumer, 0, out var value) && !value.IsNull &&
                (SignatureComparer.Default.Equals(value.Type, operation.SourceElement) ||
                 ArrayStorageType(operation.SourceElement) != null &&
                 SameStackValue(NumericStackValue(value.Type), NumericStackValue(operation.SourceElement)));
        }

        private bool IsNativeStorageValue(AddressOperation operation)
        {
            if (!InteropTypeReferences.IsValueType(operation.SourceElement)) return false;
            if (IsBlittableAddressValue(operation)) return true;
            if (!TryGetAddressElement(operation.SourceElement, out var element) ||
                !SignatureComparer.Default.Equals(element, operation.Element)) return false;
            var definition = resolver.ResolveRaw(operation.SourceElement)?.Resolve();
            if (definition == null || definition.GenericParameters.Count !=
                (operation.SourceElement is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0))
                return false;
            var mapped = context.GetContextForNewType(definition);
            return mapped.OriginalType != null &&
                mapped.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.NonBlittableStruct;
        }

        private bool IsNativeReferenceValue(AddressOperation operation) =>
            !InteropTypeReferences.IsValueType(operation.SourceElement) && IsNativeArrayReference(operation.Element);

        private bool IsNativeAggregateLocation(AddressOperation operation) =>
            !operation.Element.IsValueType && IsNativeArrayReference(operation.Element) && IsNativeStorageValue(operation) &&
            (operation.IsScopedParameter || operation.IsArray || (operation.OwnerType != null || operation.StaticOwnerType != null) &&
                operation.NativeFieldPointerName != null);

        private static bool IsDirectNativeField(AddressOperation operation) => operation.IsScopedParameter
            ? operation.ScopedAggregateField
            : !operation.IsArray && !operation.IsNestedField && (operation.OwnerType != null || operation.StaticOwnerType != null) &&
                operation.NativeFieldPointerName != null;

        private static bool CanWriteNativeAggregate(AddressOperation operation) => operation.IsScopedParameter
            ? operation.ScopedAggregateRoot
            : !operation.IsNestedField && (operation.IsArray || IsDirectNativeField(operation));

        private CilInstruction EmitRectangularReferenceProducer(AddressOperation location)
        {
            var first = EmitRectangularReferenceCoordinates(location.OwnerType, location.RectangularRank,
                out var owner, out _, out var linear);
            body.Add(CilOpCodes.Ldloc, owner);
            body.Add(CilOpCodes.Ldloc, linear);
            body.Add(CilOpCodes.Ldc_I4, location.RectangularRank);
            var factory = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeReferenceStorage)
                .GetMethod(nameof(InteropNativeReferenceStorage.Rectangular)));
            body.Add(CilOpCodes.Call, factory.MakeGenericInstanceMethod(location.OwnerType, location.Element));
            return first;
        }

        private CilInstruction EmitRectangularReferenceCoordinates(TypeSignature ownerType, int rank,
            out CilLocalVariable owner, out CilLocalVariable[] indices, out CilLocalVariable linear)
        {
            var importer = imports.Module.DefaultImporter;
            indices = new CilLocalVariable[rank];
            var first = body.Add(CilOpCodes.Nop);
            for (var dimension = indices.Length - 1; dimension >= 0; dimension--)
            {
                indices[dimension] = AddArrayLocal(imports.Module.CorLibTypeFactory.Int32);
                body.Add(CilOpCodes.Stloc, indices[dimension]);
            }
            owner = AddArrayLocal(ownerType);
            body.Add(CilOpCodes.Stloc, owner);
            body.Add(CilOpCodes.Ldloc, owner);
            body.Add(CilOpCodes.Call, importer.ImportMethod(imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value));
            body.Add(CilOpCodes.Pop);
            linear = AddArrayLocal(imports.Module.CorLibTypeFactory.Int64);
            var relative = AddArrayLocal(imports.Module.CorLibTypeFactory.Int64);
            var length = AddArrayLocal(imports.Module.CorLibTypeFactory.Int32);
            body.Add(CilOpCodes.Ldc_I8, 0L);
            body.Add(CilOpCodes.Stloc, linear);
            var getLength = importer.ImportMethod(InteropRectangularArraySupport.NativeMethod(context, "GetLength", false, "System.Int32"));
            var getLowerBound = importer.ImportMethod(InteropRectangularArraySupport.NativeMethod(context, "GetLowerBound", false, "System.Int32"));
            // Native dimensions are immutable. Compute the row-major element identity using
            // supported shape accessors; no native bounds-structure layout is assumed here.
            for (var dimension = 0; dimension < indices.Length; dimension++)
            {
                body.Add(CilOpCodes.Ldloc, owner);
                body.Add(CilOpCodes.Ldc_I4, dimension);
                body.Add(CilOpCodes.Call, getLength);
                body.Add(CilOpCodes.Stloc, length);
                body.Add(CilOpCodes.Ldloc, indices[dimension]);
                body.Add(CilOpCodes.Conv_I8);
                body.Add(CilOpCodes.Ldloc, owner);
                body.Add(CilOpCodes.Ldc_I4, dimension);
                body.Add(CilOpCodes.Call, getLowerBound);
                body.Add(CilOpCodes.Conv_I8);
                body.Add(CilOpCodes.Sub);
                body.Add(CilOpCodes.Stloc, relative);
                body.Add(CilOpCodes.Ldloc, relative);
                body.Add(CilOpCodes.Ldloc, length);
                body.Add(CilOpCodes.Conv_U8);
                var valid = new CilInstructionLabel();
                body.Add(CilOpCodes.Blt_Un, valid);
                body.Add(CilOpCodes.Newobj, importer.ImportMethod(typeof(IndexOutOfRangeException).GetConstructor(Type.EmptyTypes)));
                body.Add(CilOpCodes.Throw);
                valid.Instruction = body.Add(CilOpCodes.Ldloc, linear);
                body.Add(CilOpCodes.Ldloc, length);
                body.Add(CilOpCodes.Conv_I8);
                body.Add(CilOpCodes.Mul_Ovf);
                body.Add(CilOpCodes.Ldloc, relative);
                body.Add(CilOpCodes.Add_Ovf);
                body.Add(CilOpCodes.Stloc, linear);
            }
            return first;
        }
    }
}
