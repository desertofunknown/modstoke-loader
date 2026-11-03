// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateNativeReferenceIdentity(CilInstruction instruction, out CilInstruction copy)
        {
            copy = null;
            var code = instruction.OpCode.Code;
            if (code is not (CilCode.Ceq or CilCode.Beq or CilCode.Bne_Un) ||
                !TryGetStackValue(instruction, 1, out var left) ||
                !TryGetStackValue(instruction, 0, out var right) ||
                left.IsNull && right.IsNull || !IsNativeIdentityOperand(left) || !IsNativeIdentityOperand(right))
                return false;
            if (code != CilCode.Ceq && instruction.Operand is not CilInstructionLabel)
            {
                Fail("unsupported-reference-identity-branch", "A native identity branch requires an instruction label.", instruction);
                return true;
            }
            copy = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeReferenceIdentity).GetMethod(nameof(InteropNativeReferenceIdentity.AreEqual))));
            if (code != CilCode.Ceq)
                body.Add(code == CilCode.Beq ? CilOpCodes.Brtrue : CilOpCodes.Brfalse,
                    CopyBranchLabel((CilInstructionLabel)instruction.Operand));
            return true;
        }

        private bool IsNativeIdentityOperand(StackValue value) => value.IsNull ||
            value.Type != null && !InteropTypeReferences.IsValueType(value.Type) &&
            TryGetAddressElement(value.Type, out TypeSignature rewritten) && IsNativeArrayReference(rewritten);
    }
}
