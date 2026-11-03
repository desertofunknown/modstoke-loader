// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private void FindAddressOperations(CilMethodBody original)
        {
            var boundaries = GetAddressBoundaries(original);
            var source = original.Instructions;
            for (var index = 0; index + 1 < source.Count; index++)
            {
                var address = source[index];
                if (ReferenceEquals(address, fixedArrayBuffer?.Producer) ||
                    address.OpCode.Code is not (CilCode.Ldflda or CilCode.Ldsflda or CilCode.Ldelema) ||
                    (index > 0 && IsAddressPrefix(source[index - 1].OpCode.Code)))
                    continue;

                var consumerIndex = index + 1;
                var consumer = source[consumerIndex];
                // The only permitted gap is a single nonthrowing, nonmutating value load for stobj.
                // Calls, dup, ref locals, pointer conversions and atomics never enter the lowering.
                if (IsAddressStoreValue(consumer) && consumerIndex + 1 < source.Count &&
                    source[consumerIndex + 1].OpCode.Code == CilCode.Stobj)
                    consumer = source[++consumerIndex];
                else if (consumer.OpCode.Code is not (CilCode.Initobj or CilCode.Ldobj or CilCode.Ldfld))
                    continue;
                var interiorEntry = false;
                for (var interior = index + 1; interior <= consumerIndex; interior++)
                    interiorEntry |= boundaries.Contains(source[interior]);
                if (interiorEntry)
                    continue;

                AddressOperation operation;
                if (address.OpCode.Code == CilCode.Ldelema)
                {
                    if (!TryGetArrayAddress(address, out operation))
                        continue;
                }
                else if (!TryGetFieldAddress(address,
                    index > 0 && !boundaries.Contains(address) ? source[index - 1] : null, out operation))
                    continue;
                operation.Consumer = consumer;
                if (!ValidateAddressConsumer(operation))
                    continue;

                addressOperations.Add(address, operation);
                addressConsumers.Add(consumer, operation);
                index = consumerIndex;
            }
            FindScopedReferenceOperations(original);
        }

        private static HashSet<CilInstruction> GetAddressBoundaries(CilMethodBody original)
        {
            var boundaries = new HashSet<CilInstruction>(ReferenceEqualityComparer.Instance);
            foreach (var instruction in original.Instructions)
            {
                if (instruction.Operand is ICilLabel branch)
                    AddBoundary(branch);
                else if (instruction.Operand is IReadOnlyList<ICilLabel> targets)
                    foreach (var branchTarget in targets)
                        AddBoundary(branchTarget);
            }
            // Region ends also constrain lowering: a sequence must remain within its exception region.
            foreach (var handler in original.ExceptionHandlers)
            {
                AddBoundary(handler.TryStart);
                AddBoundary(handler.TryEnd);
                AddBoundary(handler.HandlerStart);
                AddBoundary(handler.HandlerEnd);
                AddBoundary(handler.FilterStart);
            }
            return boundaries;

            void AddBoundary(ICilLabel label)
            {
                if (label is CilInstructionLabel { Instruction: not null } entry)
                    boundaries.Add(entry.Instruction);
            }
        }

        private static bool IsAddressPrefix(CilCode code) => code is
            CilCode.Readonly or CilCode.Volatile or CilCode.Unaligned or CilCode.Constrained or CilCode.Tailcall;

        private static bool IsAddressStoreValue(CilInstruction instruction) => instruction.OpCode.Code is
            CilCode.Ldarg or CilCode.Ldloc or CilCode.Ldnull or CilCode.Ldc_I4 or CilCode.Ldc_I8 or
            CilCode.Ldc_R4 or CilCode.Ldc_R8;

        private bool ValidateAddressConsumer(AddressOperation operation)
        {
            var consumer = operation.Consumer;
            // Whole boxed values need scoped ownership and native aggregate copy semantics.
            if (consumer.OpCode.Code != CilCode.Ldfld && IsNativeAggregateLocation(operation))
                return false;
            if (consumer.OpCode.Code == CilCode.Ldfld)
            {
                if (!InteropTypeReferences.IsValueType(operation.SourceElement) ||
                    consumer.Operand is not IFieldDescriptor member || member.Signature == null ||
                    member.Resolve()?.IsStatic != false ||
                    !SignatureComparer.Default.Equals(member.DeclaringType.ToTypeSignature(), operation.SourceElement) ||
                    !TryGetAddressElement(member.Signature.FieldType.InstantiateGenericTypes(
                        GenericContext.FromField(member)), out _))
                    return false;
            }
            else if (consumer.Operand is not ITypeDefOrRef typed ||
                !SignatureComparer.Default.Equals(typed.ToTypeSignature(), operation.SourceElement))
                return false;

            if (!TryGetStackValue(consumer, consumer.OpCode.Code == CilCode.Stobj ? 1 : 0, out var address) ||
                address.Type is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, operation.SourceElement))
                return false;

            if (!operation.IsArray && (operation.IsWrite ? operation.Setter : operation.Getter) == null)
                return false;
            if (!operation.IsWrite)
                return true;
            if (operation.ReadOnly)
                return false;

            // Nonblittable native values are boxed wrapper objects, not CLR values. Neither
            // initobj on that wrapper nor an unchecked byte copy implements the native write.
            if (InteropTypeReferences.IsValueType(operation.SourceElement) &&
                !IsBlittableAddressValue(operation))
                return false;
            if (consumer.OpCode.Code == CilCode.Initobj)
                return true;

            if (!TryGetStackValue(consumer, 0, out var value))
                return false;
            if (value.IsNull)
                return !InteropTypeReferences.IsValueType(operation.SourceElement);
            if (SignatureComparer.Default.Equals(value.Type, operation.SourceElement))
                return true;
            if (!InteropTypeReferences.IsValueType(operation.SourceElement) && IsReferenceStoreValue(value, operation))
                return true;
            return ArrayStorageType(operation.SourceElement) != null &&
                SameStackValue(NumericStackValue(value.Type), NumericStackValue(operation.SourceElement));
        }

        private bool IsBlittableAddressValue(AddressOperation operation) =>
            IsBlittableAddressValue(operation.SourceElement, operation.Element);

        private bool IsBlittableAddressValue(TypeSignature source, TypeSignature rewritten)
        {
            if (!rewritten.IsValueType)
                return false;
            if (source is CorLibTypeSignature)
                return ArrayStorageType(source) != null;
            // Classify expects the original native metadata definition, not a Unity base-library
            // definition. Resolve the actual rewritten type context instead.
            var definition = resolver.ResolveRaw(source)?.Resolve();
            return definition != null && context.GetContextForNewType(definition).ComputedTypeSpecifics ==
                InteropTypeContext.TypeSpecifics.BlittableStruct;
        }
    }
}
