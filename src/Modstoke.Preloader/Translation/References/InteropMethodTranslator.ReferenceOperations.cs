// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanReferenceUse(CilInstruction instruction, ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var code = instruction.OpCode.Code;
            if (code is CilCode.Call or CilCode.Callvirt or CilCode.Newobj)
                return TryPlanReferenceCall(instruction, consumed, out use);
            if (consumed.Length == 0 || !TryGetReferenceLocation(consumed[0], out var location, out var readOnly))
                return false;
            var write = IsScopedReferenceWrite(code);
            if (code == CilCode.Ldflda)
            {
                if (consumed.Length != 1 || !IsNativeStorageValue(location) ||
                    !(location.IsScopedParameter || location.IsArray ||
                        (location.OwnerType != null || location.StaticOwnerType != null) && location.NativeFieldPointerName != null) ||
                    !referenceProducers.TryGetValue(instruction, out var child) || !child.IsNestedField ||
                    instruction.Operand is not IFieldDescriptor field ||
                    !SignatureComparer.Default.Equals(field.DeclaringType.ToTypeSignature(), location.SourceElement))
                    return false;
                use = new ReferenceUse { Location = location };
                return true;
            }
            if (code == CilCode.Ldfld && consumed.Length == 1 && IsNativeStorageValue(location) &&
                (location.IsScopedParameter || location.IsArray ||
                    (location.OwnerType != null || location.StaticOwnerType != null) && location.NativeFieldPointerName != null) &&
                TryGetNestedReferenceField(instruction, out var readField) &&
                (IsBlittableAddressValue(readField) || IsNativeReferenceValue(readField) || IsNativeAggregateLocation(readField)))
            {
                use = new ReferenceUse { Location = location, NestedFieldRead = readField };
                return true;
            }
            if (code == CilCode.Stfld && consumed.Length == 2 && consumed[1] == null && !readOnly &&
                IsNativeStorageValue(location) &&
                (location.IsScopedParameter || location.IsArray ||
                    (location.OwnerType != null || location.StaticOwnerType != null) && location.NativeFieldPointerName != null) &&
                TryGetNestedReferenceField(instruction, out var writtenField, receiverDepth: 1) && !writtenField.ReadOnly &&
                TryGetStackValue(instruction, 0, out var storedValue))
            {
                var compatible = IsNativeReferenceValue(writtenField)
                    ? IsReferenceStoreValue(storedValue, writtenField)
                    : IsBlittableAddressValue(writtenField) && !storedValue.IsNull &&
                      (SignatureComparer.Default.Equals(storedValue.Type, writtenField.SourceElement) ||
                       ArrayStorageType(writtenField.SourceElement) != null &&
                       NumericStackValue(storedValue.Type).Type != null &&
                       SameStackValue(NumericStackValue(storedValue.Type), NumericStackValue(writtenField.SourceElement)));
                if (!compatible) return false;
                use = new ReferenceUse { Location = location, NestedFieldWrite = writtenField };
                return true;
            }
            if (code == CilCode.Cpobj)
            {
                if (consumed.Length != 2 || readOnly ||
                    !TryGetReferenceLocation(consumed[1], out var source, out _) ||
                    instruction.Operand is not ITypeDefOrRef copied ||
                    !SignatureComparer.Default.Equals(copied.ToTypeSignature(), location.SourceElement) ||
                    !SignatureComparer.Default.Equals(source.SourceElement, location.SourceElement) ||
                    !SignatureComparer.Default.Equals(source.Element, location.Element))
                    return false;
                if (IsNativeAggregateLocation(location) && IsNativeAggregateLocation(source))
                {
                    if (!CanWriteNativeAggregate(location) && !IsDirectNativeField(source)) return false;
                }
                else if (!IsBlittableAddressValue(location) &&
                    (InteropTypeReferences.IsValueType(location.SourceElement) || !IsNativeArrayReference(location.Element)) ||
                    !location.UsesNativeValueAccess && !location.IsArray && location.Setter == null ||
                    !source.UsesNativeValueAccess && !source.IsArray && source.Getter == null)
                    return false;
                use = new ReferenceUse { Location = location, CopySource = source };
                return true;
            }
            if (IsNativeAggregateLocation(location) && code is CilCode.Ldobj or CilCode.Stobj or CilCode.Initobj)
            {
                if (consumed.Length != (code == CilCode.Stobj ? 2 : 1) ||
                    consumed.Length == 2 && consumed[1] != null ||
                    instruction.Operand is not ITypeDefOrRef aggregate ||
                    !SignatureComparer.Default.Equals(aggregate.ToTypeSignature(), location.SourceElement) ||
                    !TryGetStackValue(instruction, code == CilCode.Stobj ? 1 : 0, out var address) ||
                    address.Type is not ByReferenceTypeSignature reference ||
                    !SignatureComparer.Default.Equals(reference.BaseType, location.SourceElement) ||
                    write && (readOnly || !CanWriteNativeAggregate(location)) ||
                    code == CilCode.Stobj && (!TryGetStackValue(instruction, 0, out var value) || value.IsNull ||
                        !SignatureComparer.Default.Equals(value.Type, location.SourceElement)))
                    return false;
                use = new ReferenceUse { Location = location };
                return true;
            }
            if (consumed.Length != (write && code != CilCode.Initobj ? 2 : 1) ||
                consumed.Length == 2 && consumed[1] != null ||
                write && readOnly || !location.UsesNativeValueAccess && !location.IsArray &&
                (write ? location.Setter : location.Getter) == null)
                return false;
            if (code is CilCode.Ldobj or CilCode.Stobj or CilCode.Initobj or CilCode.Ldfld)
            {
                var checkedOperation = CopyReferenceOperation(location, instruction);
                if (location.UsesNativeValueAccess ? !ValidateNativeValueReferenceConsumer(checkedOperation) :
                    !ValidateAddressConsumer(checkedOperation)) return false;
            }
            else
            {
                if (!ReferenceIndirectOpcodeMatches(code, location.SourceElement)) return false;
                if (write)
                {
                    if (InteropTypeReferences.IsValueType(location.SourceElement) && !IsBlittableAddressValue(location) ||
                        !TryGetStackValue(instruction, 0, out var value))
                        return false;
                    if (code == CilCode.Stind_Ref)
                    {
                        if (!IsReferenceStoreValue(value, location))
                            return false;
                    }
                    else if (value.IsNull || !SameStackValue(NumericStackValue(value.Type), NumericStackValue(location.SourceElement)) ||
                        NumericStackValue(value.Type).Type == null)
                        return false;
                }
            }
            use = new ReferenceUse { Location = location };
            return true;
        }

        private bool IsReferenceStoreValue(StackValue value, AddressOperation location) => value.IsNull ||
            SignatureComparer.Default.Equals(value.Type, location.SourceElement) ||
            value.Type != null && !InteropTypeReferences.IsValueType(value.Type) &&
            !InteropTypeReferences.IsValueType(location.SourceElement) && IsNativeArrayReference(location.Element) &&
            TryGetAddressElement(value.Type, out var rewritten) &&
            ProvesManagedBase(rewritten, location.Element, new System.Collections.Generic.HashSet<string>(), 0);

        private static AddressOperation CopyReferenceOperation(AddressOperation source, CilInstruction consumer) => new()
        {
            SourceElement = source.SourceElement,
            Element = source.Element,
            OwnerType = source.OwnerType,
            StaticOwnerType = source.StaticOwnerType,
            IndexType = source.IndexType,
            Getter = source.Getter,
            Setter = source.Setter,
            NativeFieldPointerName = source.NativeFieldPointerName,
            RectangularRank = source.RectangularRank,
            IsNestedField = source.IsNestedField,
            ScopedParameterIndex = source.ScopedParameterIndex,
            ScopedAggregateRoot = source.ScopedAggregateRoot,
            ScopedAggregateField = source.ScopedAggregateField,
            ReadOnly = source.ReadOnly,
            Consumer = consumer
        };

        private static bool IsScopedReferenceWrite(CilCode code) => code is
            CilCode.Stobj or CilCode.Initobj or CilCode.Stind_Ref or CilCode.Stind_I or CilCode.Stind_I1 or
            CilCode.Stind_I2 or CilCode.Stind_I4 or CilCode.Stind_I8 or CilCode.Stind_R4 or CilCode.Stind_R8;

        private static bool ReferenceIndirectOpcodeMatches(CilCode code, TypeSignature element)
        {
            var storage = ArrayStorageType(element);
            return code switch
            {
                CilCode.Ldind_Ref or CilCode.Stind_Ref => !InteropTypeReferences.IsValueType(element) && IsStackReference(element),
                CilCode.Ldind_I1 or CilCode.Ldind_U1 or CilCode.Stind_I1 => storage is ElementType.Boolean or ElementType.I1 or ElementType.U1,
                CilCode.Ldind_I2 or CilCode.Ldind_U2 or CilCode.Stind_I2 => storage is ElementType.Char or ElementType.I2 or ElementType.U2,
                CilCode.Ldind_I4 or CilCode.Ldind_U4 or CilCode.Stind_I4 => storage is ElementType.I4 or ElementType.U4,
                CilCode.Ldind_I8 or CilCode.Stind_I8 => storage is ElementType.I8 or ElementType.U8,
                CilCode.Ldind_I or CilCode.Stind_I => storage is ElementType.I or ElementType.U,
                CilCode.Ldind_R4 or CilCode.Stind_R4 => storage == ElementType.R4,
                CilCode.Ldind_R8 or CilCode.Stind_R8 => storage == ElementType.R8,
                _ => false
            };
        }

        private void EmitReferenceLoadConversion(CilCode code)
        {
            switch (code)
            {
                case CilCode.Ldind_I1: body.Add(CilOpCodes.Conv_I1); break;
                case CilCode.Ldind_U1: body.Add(CilOpCodes.Conv_U1); break;
                case CilCode.Ldind_I2: body.Add(CilOpCodes.Conv_I2); break;
                case CilCode.Ldind_U2: body.Add(CilOpCodes.Conv_U2); break;
                case CilCode.Ldind_I4: body.Add(CilOpCodes.Conv_I4); break;
                case CilCode.Ldind_U4: body.Add(CilOpCodes.Conv_U4); break;
                case CilCode.Ldind_I8: body.Add(CilOpCodes.Conv_I8); break;
                case CilCode.Ldind_I: body.Add(CilOpCodes.Conv_I); break;
                case CilCode.Ldind_R4: body.Add(CilOpCodes.Conv_R4); break;
                case CilCode.Ldind_R8: body.Add(CilOpCodes.Conv_R8); break;
            }
        }
    }
}
