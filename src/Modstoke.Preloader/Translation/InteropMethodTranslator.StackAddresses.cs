// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
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
        private bool TryTranslateStackValueAddress(CilOpCode code, IMethodDescriptor method, out CilInstruction translated)
        {
            translated = null;
            if (code.Code != CilCode.Call || !TryGetValueAddressIntrinsic(method, out var argument)) return false;

            var source = sourceMethod.CilMethodBody;
            var position = source.Instructions.IndexOf(currentInstruction);
            if (position < 1 || GetAddressBoundaries(source).Contains(currentInstruction) ||
                position > 1 && IsAddressPrefix(source.Instructions[position - 2].OpCode.Code)) return false;
            var producer = source.Instructions[position - 1];
            var element = producer.OpCode.Code switch
            {
                CilCode.Ldarga when producer.Operand is Parameter parameter && parameter.Index >= 0 =>
                    SourceParameterType(parameter),
                CilCode.Ldloca when producer.Operand is CilLocalVariable local => local.VariableType,
                _ => null
            };
            if (element == null || !SignatureComparer.Default.Equals(element, argument) ||
                !TryGetStackValue(currentInstruction, 0, out var value) ||
                value.Type is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, element)) return false;
            var rewritten = resolver.Resolve(element);
            if (rewritten == null || !HasStackAddressLayout(element, rewritten, new HashSet<TypeDefinition>()) ||
                !SignatureComparer.Default.Equals(GetEmittedAddressReceiverType(producer), rewritten.MakeByReferenceType()))
                return false;

            // Method-owned locals and by-value arguments do not move with the GC.
            // Preserve that storage, not a temporary copy or a native wrapper slot.
            translated = body.Add(CilOpCodes.Conv_U);
            return true;
        }

        private static bool TryGetValueAddressIntrinsic(IMethodDescriptor method, out TypeSignature argument)
        {
            argument = null;
            if (method is not MethodSpecification specification ||
                method.Name != "AddressOf" || method.DeclaringType?.FullName != "Unity.Collections.LowLevel.Unsafe.UnsafeUtility" ||
                InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) != "UnityEngine.CoreModule" ||
                specification.Signature?.TypeArguments.Count != 1 ||
                method.Signature is not { HasThis: false, GenericParameterCount: 1 } signature ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.ParameterTypes.Count != 1 ||
                signature.ReturnType is not PointerTypeSignature { BaseType.ElementType: ElementType.Void } ||
                signature.ParameterTypes[0] is not ByReferenceTypeSignature
                { BaseType: GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 } } ||
                method.Resolve() is not { IsStatic: true, HasMethodBody: true } original ||
                original.GenericParameters.Count != 1 || !SignatureComparer.Default.Equals(original.Signature, signature) ||
                original.CilMethodBody.ExceptionHandlers.Count != 0 ||
                (original.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL)
                return false;

            var intrinsic = original.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            if (intrinsic.Length != 2 || ForwardedArgumentIndex(intrinsic[0], original) != 0 ||
                intrinsic[1].OpCode.Code != CilCode.Ret) return false;

            argument = specification.Signature.TypeArguments[0];
            return true;
        }

        private bool HasStackAddressLayout(TypeSignature source, TypeSignature rewritten, HashSet<TypeDefinition> visiting)
        {
            if (!rewritten.IsValueType || source is ByReferenceTypeSignature or GenericInstanceTypeSignature or
                GenericParameterSignature or CustomModifierTypeSignature or PinnedTypeSignature) return false;
            if (IsBlittableAddressValue(source, rewritten)) return true;

            var original = source.Resolve();
            var mapped = FindMappedSourceOwner(source);
            var restored = mapped?.NewType;
            var plan = InteropRestorationPlan.For(context);
            if (source is not TypeDefOrRefSignature || original is not { IsValueType: true } ||
                original.GenericParameters.Count != 0 || mapped?.OriginalType != null || restored == null ||
                restored.GenericParameters.Count != 0 || original.IsEnum != restored.IsEnum ||
                !SignatureComparer.Default.Equals(rewritten, restored.ToTypeSignature()) ||
                !plan.CanRestore(original) || plan.ContainsManagedOnlyStorage(source) ||
                original.IsSequentialLayout != restored.IsSequentialLayout || original.IsExplicitLayout != restored.IsExplicitLayout ||
                (original.ClassLayout?.PackingSize ?? 0) != (restored.ClassLayout?.PackingSize ?? 0) ||
                (original.ClassLayout?.ClassSize ?? 0) != (restored.ClassLayout?.ClassSize ?? 0)) return false;
            if (original.IsEnum) return HasRestoredEnumStorage(original, restored);
            if (!original.IsSequentialLayout && !original.IsExplicitLayout || !visiting.Add(original)) return false;
            try
            {
                var fields = original.Fields.Where(field => !field.IsStatic).ToArray();
                var copies = restored.Fields.Where(field => !field.IsStatic).ToArray();
                if (fields.Length != copies.Length) return false;
                for (var index = 0; index < fields.Length; index++)
                {
                    var field = fields[index];
                    var copy = copies[index];
                    var sourceField = InteropRestoredTypeSupport.StorageType(field.Signature.FieldType);
                    var targetField = InteropRestoredTypeSupport.StorageType(copy.Signature.FieldType);
                    if (field.Name != copy.Name || field.FieldOffset != copy.FieldOffset ||
                        !SignatureComparer.Default.Equals(resolver.ResolveFieldStorage(field.Signature.FieldType), copy.Signature.FieldType))
                        return false;
                    if (sourceField is PointerTypeSignature)
                    {
                        if (targetField is not CorLibTypeSignature { ElementType: ElementType.I }) return false;
                    }
                    else if (!HasStackAddressLayout(sourceField, targetField, visiting)) return false;
                }
                return true;
            }
            finally
            {
                visiting.Remove(original);
            }
        }

        private static bool HasRestoredEnumStorage(TypeDefinition original, TypeDefinition restored)
        {
            var fields = original.Fields.Where(field => !field.IsStatic).ToArray();
            var copies = restored.Fields.Where(field => !field.IsStatic).ToArray();
            if (fields.Length != 1 || copies.Length != 1) return false;
            var field = fields[0];
            var copy = copies[0];
            return field.Name == "value__" && copy.Name == "value__" &&
                !field.HasFieldMarshal && !copy.HasFieldMarshal && field.FieldRva == null && copy.FieldRva == null &&
                field.FieldOffset == copy.FieldOffset &&
                field.Signature?.FieldType is CorLibTypeSignature source &&
                copy.Signature?.FieldType is CorLibTypeSignature target && source.ElementType == target.ElementType &&
                source.ElementType is ElementType.I1 or ElementType.U1 or ElementType.I2 or ElementType.U2 or
                    ElementType.I4 or ElementType.U4 or ElementType.I8 or ElementType.U8;
        }
    }
}
