// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateManagedVectorInstruction(CilInstruction instruction, out CilInstruction translated)
        {
            translated = null;
            if (instruction.OpCode.FlowControl == CilFlowControl.Meta)
            {
                var source = sourceMethod.CilMethodBody.Instructions;
                var position = source.IndexOf(instruction);
                var next = position + 1;
                while (next < source.Count && source[next].OpCode.FlowControl == CilFlowControl.Meta)
                    next++;
                if (next >= source.Count || !TryGetManagedVectorSource(source[next], out _, out _))
                    return false;
                if (instruction.OpCode.Code != CilCode.Readonly || next != position + 1 ||
                    source[next].OpCode.Code != CilCode.Ldelema ||
                    !ValidateManagedVectorInstruction(source[next], out _))
                {
                    translated = FailInstruction("unsupported-managed-vector-prefix",
                        "Only a single readonly. prefix immediately preceding an exact CLR-vector ldelema is supported.");
                    return true;
                }
                translated = body.Add(CilOpCodes.Readonly);
                return true;
            }

            if (!TryGetManagedVectorSource(instruction, out _, out _))
                return false;
            if (!ValidateManagedVectorInstruction(instruction, out var element))
                return true;

            // Keep the original receiver and index on the stack, including for readonly. ldelema.
            // No native location descriptor, accessor, spill, or element copy is involved.
            translated = instruction.OpCode.Code is CilCode.Newarr or CilCode.Ldelem or CilCode.Stelem or CilCode.Ldelema
                ? body.Add(instruction.OpCode,
                    imports.Module.DefaultImporter.ImportTypeSignature(element).ToTypeDefOrRef())
                : body.Add(instruction.OpCode);
            return true;
        }

        private bool TryGetManagedVectorSource(CilInstruction instruction,
            out TypeSignature sourceVector, out TypeSignature sourceElement)
        {
            sourceVector = null;
            sourceElement = null;
            if (instruction.OpCode.Code == CilCode.Newarr)
            {
                if (instruction.Operand is not ITypeDefOrRef element)
                    return false;
                sourceVector = element.ToTypeSignature().MakeSzArrayType();
            }
            else
            {
                var depth = ManagedVectorOperandDepth(instruction.OpCode.Code);
                if (depth < 0 || !TryGetStackValue(instruction, depth, out var receiver))
                    return false;
                sourceVector = receiver.Type;
            }
            return InteropManagedVectorLowering.TryGetElement(context, sourceVector, out sourceElement);
        }

        private static int ManagedVectorOperandDepth(CilCode code) => code switch
        {
            CilCode.Ldlen => 0,
            CilCode.Ldelema or CilCode.Ldelem or CilCode.Ldelem_Ref or
                CilCode.Ldelem_I1 or CilCode.Ldelem_U1 or CilCode.Ldelem_I2 or CilCode.Ldelem_U2 or
                CilCode.Ldelem_I4 or CilCode.Ldelem_U4 or CilCode.Ldelem_I8 or CilCode.Ldelem_I or
                CilCode.Ldelem_R4 or CilCode.Ldelem_R8 => 1,
            CilCode.Stelem or CilCode.Stelem_Ref or CilCode.Stelem_I or CilCode.Stelem_I1 or
                CilCode.Stelem_I2 or CilCode.Stelem_I4 or CilCode.Stelem_I8 or
                CilCode.Stelem_R4 or CilCode.Stelem_R8 => 2,
            _ => -1
        };

        private bool ValidateManagedVectorInstruction(CilInstruction instruction, out TypeSignature element)
        {
            element = null;
            if (!TryGetManagedVectorSource(instruction, out var sourceVector, out var sourceElement))
                return Fail("unsupported-managed-vector-signature", "The source CLR vector is not proven.", instruction);
            var code = instruction.OpCode.Code;
            var referenceOpcode = code is CilCode.Ldelem_Ref or CilCode.Stelem_Ref;
            var storage = ArrayStorageType(sourceElement);
            if (code is not (CilCode.Newarr or CilCode.Ldlen or CilCode.Ldelema) &&
                !ArrayOpcodeMatches(code, sourceElement, storage))
                return Fail("unsupported-managed-vector-opcode",
                    "The element opcode must match the CLR vector's value, enum or reference storage.", instruction);
            if (!ValidateManagedVectorPrefix(instruction))
                return false;

            TypeSignature rewrittenVector;
            if (code == CilCode.Newarr)
            {
                rewrittenVector = resolver.Resolve(sourceVector);
                element = resolver.Resolve(sourceElement);
            }
            else if (!TryGetArrayOperand(instruction, ManagedVectorOperandDepth(code), out var array,
                         out rewrittenVector, out element) ||
                     !SignatureComparer.Default.Equals(array, sourceVector) ||
                     !SignatureComparer.Default.Equals(array.BaseType, sourceElement))
                return Fail("unsupported-managed-vector-signature",
                    "The actual source vector and resolved element types are not proven.", instruction);

            if (element == null || element.IsValueType != sourceElement.IsValueType ||
                referenceOpcode && element.IsValueType || rewrittenVector is not SzArrayTypeSignature vector ||
                !SignatureComparer.Default.Equals(vector.BaseType, element) ||
                storage != null && ArrayStorageType(element) != storage)
                return Fail("unsupported-managed-vector-signature",
                    "The resolved vector must preserve its element's value or reference representation.", instruction);
            if (code == CilCode.Ldlen)
                return true;
            if (code is (CilCode.Newarr or CilCode.Ldelem or CilCode.Stelem or CilCode.Ldelema) &&
                (instruction.Operand is not ITypeDefOrRef explicitElement ||
                !SignatureComparer.Default.Equals(explicitElement.ToTypeSignature(), sourceElement)))
                return Fail("unsupported-managed-vector-element",
                    "The explicit element operand must exactly match the source vector element.", instruction);
            var store = ManagedVectorOperandDepth(code) == 2;
            if (!TryGetStackValue(instruction, store ? 1 : 0, out var index) ||
                index.IsNull || NumericStackValue(index.Type).Type?.ElementType is not (ElementType.I4 or ElementType.I))
                return Fail("unsupported-managed-vector-index",
                    "The vector length or index must be a proven Int32 or native integer.", instruction);
            if (store && (!TryGetStackValue(instruction, 0, out var value) ||
                (value.IsNull ? element.IsValueType : resolver.Resolve(value.Type) == null ||
                    (element.IsValueType ? !ArrayValueMatches(value, sourceElement, code) :
                        !InteropManagedVectorLowering.CanAssignElement(context, value.Type, sourceElement)))))
                return Fail("unsupported-managed-vector-store",
                    "The stored value must have a proven CLR assignment to the vector element.", instruction);
            return true;
        }

        private bool ValidateManagedVectorPrefix(CilInstruction instruction)
        {
            var original = sourceMethod.CilMethodBody;
            var source = original.Instructions;
            var position = source.IndexOf(instruction);
            if (position == 0 || source[position - 1].OpCode.FlowControl != CilFlowControl.Meta)
                return true;
            if (instruction.OpCode.Code != CilCode.Ldelema || source[position - 1].OpCode.Code != CilCode.Readonly ||
                (position > 1 && source[position - 2].OpCode.FlowControl == CilFlowControl.Meta))
                return Fail("unsupported-managed-vector-prefix",
                    "Only a single readonly. prefix is supported on CLR-vector ldelema.", instruction);

            // One-to-one instruction mapping preserves targets at the prefix. An interior entry or
            // EH boundary would separate the prefix from its operation and is rejected instead.
            foreach (var candidate in source)
            {
                if (candidate.Operand is ICilLabel branch && IsInterior(branch))
                    return RejectInterior();
                if (candidate.Operand is IReadOnlyList<ICilLabel> branches)
                    foreach (var targetLabel in branches)
                        if (IsInterior(targetLabel))
                            return RejectInterior();
            }
            foreach (var handler in original.ExceptionHandlers)
                if (IsInterior(handler.TryStart) || IsInterior(handler.TryEnd) ||
                    IsInterior(handler.HandlerStart) || IsInterior(handler.HandlerEnd) || IsInterior(handler.FilterStart))
                    return RejectInterior();
            return true;

            bool IsInterior(ICilLabel label) => label is CilInstructionLabel entry &&
                ReferenceEquals(entry.Instruction, instruction);

            bool RejectInterior() => Fail("unsupported-managed-vector-prefix-boundary",
                "A branch or exception boundary cannot enter or split readonly. ldelema.", instruction);
        }
    }
}
