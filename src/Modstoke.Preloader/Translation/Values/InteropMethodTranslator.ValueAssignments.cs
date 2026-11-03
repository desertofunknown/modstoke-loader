// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly HashSet<object> nativeValueAddressedSlots = new(ReferenceEqualityComparer.Instance);

        private bool TryTranslateNativeValueAssignment(CilInstruction instruction, out CilInstruction copy)
        {
            copy = null;
            var code = instruction.OpCode.Code;
            TypeSignature source = null;
            if (code == CilCode.Stloc && instruction.Operand is CilLocalVariable local) source = local.VariableType;
            else if (code == CilCode.Starg && instruction.Operand is Parameter parameter) source = SourceParameterType(parameter);
            if (source != null && nativeValueAddressedSlots.Contains(instruction.Operand) &&
                TryGetNativeValueWrapper(source, out var slotWrapper))
            {
                if (!ValidateNativeValueStack(instruction, source)) return true;
                var value = AddArrayLocal(slotWrapper);
                copy = body.Add(CilOpCodes.Stloc, value);
                if (instruction.Operand is CilLocalVariable variable) body.Add(CilOpCodes.Ldloca, locals[variable]);
                else body.Add(CilOpCodes.Ldarga,
                    TargetParameter((Parameter)instruction.Operand));
                body.Add(CilOpCodes.Ldloc, value);
                EmitValueStorageCall(nameof(InteropNativeValueStorage.Store), slotWrapper);
                return true;
            }

            if (code is not (CilCode.Stobj or CilCode.Initobj or CilCode.Cpobj) ||
                instruction.Operand is not ITypeDefOrRef type ||
                !TryGetNativeValueWrapper(type.ToTypeSignature(), out var wrapper)) return false;
            source = type.ToTypeSignature();
            if (!ValidateNativeValueStoragePrefix() ||
                !ValidateNativeValueLocation(instruction, code == CilCode.Initobj ? 0 : 1, source)) return true;
            if (code == CilCode.Stobj && !ValidateNativeValueStack(instruction, source) ||
                code == CilCode.Cpobj && !ValidateNativeValueLocation(instruction, 0, source)) return true;
            copy = EmitValueStorageCall(code switch
            {
                CilCode.Initobj => nameof(InteropNativeValueStorage.Initialize),
                CilCode.Cpobj => nameof(InteropNativeValueStorage.CopyLocation),
                _ => nameof(InteropNativeValueStorage.Store)
            }, wrapper);
            return true;
        }

        private bool ValidateNativeValueStack(CilInstruction instruction, TypeSignature type) =>
            TryGetStackValue(instruction, 0, out var value) && !value.IsNull &&
            SignatureComparer.Default.Equals(value.Type, type) ||
            Fail("unsupported-native-value-assignment", "A value assignment requires its exact source storage type.", instruction);

        private bool ValidateNativeValueLocation(CilInstruction instruction, int depth, TypeSignature type) =>
            TryGetStackValue(instruction, depth, out var value) && value.Type is ByReferenceTypeSignature reference &&
            SignatureComparer.Default.Equals(reference.BaseType, type) ||
            Fail("unsupported-native-value-location", "A value operation requires its exact managed location.", instruction);

        private bool ValidateNativeValueStoragePrefix() =>
            body.Count == 0 || !IsAddressPrefix(body[body.Count - 1].OpCode.Code) ||
            Fail("unsupported-native-value-prefix", "Native value helpers cannot preserve this memory-access prefix.", currentInstruction);

        private void RewriteNativeValueFieldStore(CilInstruction access, TypeSignature wrapper)
        {
            var field = (IFieldDescriptor)access.Operand;
            var address = access.OpCode.Code == CilCode.Stsfld ? CilOpCodes.Ldsflda : CilOpCodes.Ldflda;
            var value = AddArrayLocal(wrapper);
            access.OpCode = CilOpCodes.Stloc;
            access.Operand = value;
            body.Add(address, field);
            body.Add(CilOpCodes.Ldloc, value);
            EmitValueStorageCall(nameof(InteropNativeValueStorage.Store), wrapper);
        }
    }
}
