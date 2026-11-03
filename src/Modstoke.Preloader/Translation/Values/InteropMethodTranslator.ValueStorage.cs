// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

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
        private bool TryGetNativeValueWrapper(TypeSignature source, out TypeSignature wrapper)
        {
            wrapper = null;
            if (source is not (TypeDefOrRefSignature or GenericInstanceTypeSignature) || source.FullName == "System.Enum")
                return false;
            var rewritten = resolver.ResolveRaw(source);
            var definition = rewritten?.Resolve();
            if (rewritten == null || rewritten.IsValueType || definition == null ||
                definition.GenericParameters.Count !=
                    (source is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0))
                return false;
            // An unresolved facade can lose its value-type flag. Require the exact native
            // value mapping instead of treating the rewritten wrapper as a reference object.
            var mapped = FindMappedSourceOwner(source);
            if (mapped?.NewType != definition || mapped.OriginalType is not { IsValueType: true } ||
                mapped.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.NonBlittableStruct)
                return false;
            wrapper = imports.Module.DefaultImporter.ImportTypeSignature(rewritten);
            return true;
        }

        private CilInstruction EmitValueStorageCall(string name, TypeSignature wrapper) =>
            body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(name))).MakeGenericInstanceMethod(wrapper));

        private bool TryGetNativeValueCopy(TypeSignature source, out TypeSignature rewritten)
        {
            if (TryGetNativeValueWrapper(source, out rewritten)) return true;
            if (source is not GenericParameterSignature generic) return false;
            var parameters = generic.ParameterType == GenericParameterType.Method
                ? sourceMethod.GenericParameters : sourceMethod.DeclaringType.GenericParameters;
            if (generic.Index < 0 || generic.Index >= parameters.Count || IsReferenceGeneric(generic))
                return false;
            rewritten = resolver.Resolve(source);
            return rewritten != null;
        }

        private CilInstruction EmitNativeValueCopy(TypeSignature rewritten) =>
            rewritten is GenericParameterSignature
                ? body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.CopyValue))))
                    .MakeGenericInstanceMethod(rewritten))
                : EmitValueStorageCall(nameof(InteropNativeValueStorage.Copy), rewritten);

        private bool IsNativeValueThis(Parameter parameter) => parameter == sourceMethod.Parameters.ThisParameter &&
            SourceParameterType(parameter) is ByReferenceTypeSignature reference &&
            TryGetNativeValueWrapper(reference.BaseType, out _);

        private bool ValidateNativeValueThisWrites(CilMethodBody original)
        {
            if (sourceMethod.IsStatic || !IsNativeValueThis(sourceMethod.Parameters.ThisParameter)) return true;
            var self = ((ByReferenceTypeSignature)SourceParameterType(sourceMethod.Parameters.ThisParameter)).BaseType;
            foreach (var instruction in original.Instructions)
            {
                // Scoped native fields/arrays already mutate the actual payload, not a CLR slot.
                if (referenceUses.ContainsKey(instruction) || addressConsumers.ContainsKey(instruction)) continue;
                if (instruction.OpCode.Code is not (CilCode.Call or CilCode.Callvirt) ||
                    instruction.Operand is not IMethodDescriptor method || method.Signature == null)
                    continue;
                if (method.Signature.ParameterTypes.Any(parameter =>
                    parameter.InstantiateGenericTypes(GenericContext.FromMethod(method)) is ByReferenceTypeSignature reference &&
                    SignatureComparer.Default.Equals(reference.BaseType, self)) &&
                    !PreservesNativeValueThisArguments(instruction, method, self))
                    return Fail("unsupported-native-value-this-write",
                        "A ref call involving boxed this requires exact arguments and a generated in-place native wrapper or internal-call bridge; other restored or overridable CLR callees need their own preservation contract.",
                        instruction);
            }
            return true;
        }

        private void CopyNativeValueParameters()
        {
            foreach (var instruction in sourceMethod.CilMethodBody.Instructions)
                if (instruction.OpCode.Code is CilCode.Ldloca or CilCode.Ldarga)
                    nativeValueAddressedSlots.Add(instruction.Operand);
            // External callers also use wrapper classes. A by-value parameter owns its storage;
            // byref parameters and the implicit value-type this must retain the caller's location.
            foreach (var parameter in sourceMethod.Parameters)
            {
                if (!TryGetNativeValueCopy(SourceParameterType(parameter), out var wrapper) ||
                    !nativeValueAddressedSlots.Contains(parameter))
                    continue;
                var targetParameter = TargetParameter(parameter);
                body.Add(CilOpCodes.Ldarg, targetParameter);
                EmitNativeValueCopy(wrapper);
                body.Add(CilOpCodes.Starg, targetParameter);
            }
        }

        private bool TryTranslateNativeValueStorage(CilInstruction instruction, out CilInstruction copy)
        {
            if (TryTranslateNativeValueAssignment(instruction, out copy)) return true;
            copy = null;
            var code = instruction.OpCode.Code;
            if (code == CilCode.Ldarg && instruction.Operand is Parameter parameter)
            {
                if (IsNativeValueThis(parameter))
                {
                    copy = body.Add(CilOpCodes.Ldarga, TargetParameter(parameter));
                    return true;
                }
                if (!TryGetNativeValueCopy(SourceParameterType(parameter), out var wrapper)) return false;
                copy = body.Add(CilOpCodes.Ldarg, TargetParameter(parameter));
                EmitNativeValueCopy(wrapper);
                return true;
            }
            if (code == CilCode.Ldloc && instruction.Operand is CilLocalVariable local &&
                TryGetNativeValueCopy(local.VariableType, out var localWrapper))
            {
                copy = body.Add(CilOpCodes.Ldloc, locals[local]);
                EmitNativeValueCopy(localWrapper);
                return true;
            }
            if (code == CilCode.Dup && TryGetStackValue(instruction, 0, out var duplicated) &&
                TryGetNativeValueCopy(duplicated.Type, out var duplicatedWrapper))
            {
                copy = body.Add(CilOpCodes.Dup);
                EmitNativeValueCopy(duplicatedWrapper);
                return true;
            }
            if (instruction.Operand is ITypeDefOrRef type &&
                code == CilCode.Ldobj && TryGetNativeValueCopy(type.ToTypeSignature(), out var valueWrapper))
            {
                if (!ValidateNativeValueStoragePrefix() || !ValidateNativeValueLocation(instruction, 0, type.ToTypeSignature()))
                    return true;
                copy = body.Add(CilOpCodes.Ldobj, valueWrapper.ToTypeDefOrRef());
                EmitNativeValueCopy(valueWrapper);
                return true;
            }
            if (instruction.Operand is IFieldDescriptor field && code is CilCode.Ldfld or CilCode.Stfld or CilCode.Ldsfld or CilCode.Stsfld)
            {
                var sourceOwner = field.DeclaringType?.ToTypeSignature();
                var sourceField = field.Signature?.FieldType.InstantiateGenericTypes(GenericContext.FromField(field));
                var ownerIsValue = code is CilCode.Ldfld or CilCode.Stfld && TryGetNativeValueWrapper(sourceOwner, out _);
                var fieldIsValue = TryGetNativeValueWrapper(sourceField, out var fieldWrapper);
                if (!ownerIsValue && !fieldIsValue) return false;
                if (!ValidateNativeValueStoragePrefix() ||
                    fieldIsValue && code is CilCode.Stfld or CilCode.Stsfld &&
                    !ValidateNativeValueStack(instruction, sourceField)) return true;
                var first = body.Add(CilOpCodes.Nop);
                CilLocalVariable owner = null;
                if (ownerIsValue && !EmitNativeValueReceiver(sourceOwner,
                    code == CilCode.Stfld ? new[] { sourceField } : System.Array.Empty<TypeSignature>(), out owner, allowValue: true))
                    return true;
                var access = TranslateField(instruction.OpCode, field);
                if (access == null) return true;
                if (fieldIsValue && access.OpCode.Code is CilCode.Stfld or CilCode.Stsfld)
                    RewriteNativeValueFieldStore(access, fieldWrapper);
                if (owner != null) EmitAddressKeepAlive(owner);
                // Native property getters already snapshot inline values. CLR fields do not.
                if (fieldIsValue && access.OpCode.Code is CilCode.Ldfld or CilCode.Ldsfld)
                    EmitValueStorageCall(nameof(InteropNativeValueStorage.Copy), fieldWrapper);
                copy = first;
                return true;
            }
            return false;
        }

        private CilInstruction TranslateValueAwareMethod(CilOpCode code, IMethodDescriptor method)
        {
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (code.Code is not (CilCode.Call or CilCode.Callvirt) || method.Signature?.HasThis != true ||
                !TryGetNativeValueWrapper(sourceOwner, out _))
                return TranslateMethod(code, method);
            if (method.Name == ".ctor")
                return TranslateNativeValueConstructor(code, method);
            var first = body.Add(CilOpCodes.Nop);
            var parameters = method.Signature.ParameterTypes.Select(type =>
                type.InstantiateGenericTypes(GenericContext.FromMethod(method))).ToArray();
            if (!EmitNativeValueReceiver(sourceOwner, parameters, out var owner) || TranslateMethod(code, method) == null) return null;
            EmitAddressKeepAlive(owner);
            return first;
        }

        private bool EmitNativeValueReceiver(TypeSignature sourceOwner, TypeSignature[] parameters,
            out CilLocalVariable owner, bool allowValue = false)
        {
            owner = null;
            if (!TryGetNativeValueWrapper(sourceOwner, out var wrapper) ||
                !TryGetStackValue(currentInstruction, parameters.Length, out var receiver))
                return Fail("unsupported-native-value-receiver", "The native value receiver type is unresolved.", currentInstruction);
            var isValue = allowValue && SignatureComparer.Default.Equals(receiver.Type, sourceOwner);
            if (!isValue && (receiver.Type is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, sourceOwner)))
                return Fail("unsupported-native-value-receiver", "A native value call requires its exact mutable CLR location.", currentInstruction);
            var arguments = new CilLocalVariable[parameters.Length];
            for (var index = 0; index < arguments.Length; index++)
            {
                var rewritten = resolver.Resolve(parameters[index]);
                if (rewritten == null || rewritten is FunctionPointerTypeSignature ||
                    rewritten is PointerTypeSignature pointer &&
                    (parameters.Length != 1 || !CanSpillNativeValueFieldPointer(parameters[index], pointer)))
                    return Fail("unsupported-native-value-argument", "The receiver conversion cannot spill this argument representation.", currentInstruction);
                arguments[index] = AddArrayLocal(rewritten);
            }
            for (var index = arguments.Length - 1; index >= 0; index--) body.Add(CilOpCodes.Stloc, arguments[index]);
            if (!isValue) EmitValueStorageCall(nameof(InteropNativeValueStorage.Receiver), wrapper);
            owner = AddArrayLocal(wrapper);
            body.Add(CilOpCodes.Stloc, owner);
            body.Add(CilOpCodes.Ldloc, owner);
            foreach (var argument in arguments) body.Add(CilOpCodes.Ldloc, argument);
            return true;
        }

        private bool CanSpillNativeValueFieldPointer(TypeSignature source, PointerTypeSignature rewritten)
        {
            if (currentInstruction.OpCode.Code != CilCode.Stfld ||
                source is not PointerTypeSignature { BaseType: CorLibTypeSignature sourceElement } ||
                rewritten.BaseType is not CorLibTypeSignature rewrittenElement ||
                sourceElement.ElementType != rewrittenElement.ElementType ||
                sourceElement.ElementType is not (ElementType.Void or >= ElementType.Boolean and <= ElementType.R8 or
                    ElementType.I or ElementType.U))
                return false;
            // Only spill an existing unmanaged address with an unchanged scalar pointee.
            // This does not convert managed references or establish a native layout for other types.
            return TryGetStackValue(currentInstruction, 0, out var value) &&
                SignatureComparer.Default.Equals(value.Type, source);
        }
    }
}
