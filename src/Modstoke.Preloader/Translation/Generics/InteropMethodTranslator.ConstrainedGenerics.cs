// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly HashSet<CilInstruction> constrainedGenericCalls = new(ReferenceEqualityComparer.Instance);

        private bool TryTranslateConstrainedGenericValue(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor slot, out CilInstruction copy)
        {
            copy = null;
            if (receiver is not GenericParameterSignature { ParameterType: GenericParameterType.Method } generic ||
                generic.Index >= sourceMethod.GenericParameters.Count || slot is MethodSpecification ||
                slot.DeclaringType?.ToTypeSignature() is not TypeDefOrRefSignature sourceInterface ||
                !InteropDelegateContracts.HasStandardConvention(slot.Signature) || !slot.Signature.HasThis ||
                slot.Signature.GenericParameterCount != 0 || slot.Signature.ParameterTypes.Count != 0 ||
                slot.Signature.ReturnType is ByReferenceTypeSignature or PointerTypeSignature or FunctionPointerTypeSignature ||
                InteropRestoredGenericSupport.ContainsGenericParameter(slot.Signature.ReturnType) ||
                !TryGetStackValue(prefix, 0, out var value) || value.Type is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, receiver)) return false;
            var parameter = sourceMethod.GenericParameters[generic.Index];
            if ((parameter.Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0 ||
                !parameter.Constraints.Any(constraint => SignatureComparer.Default.Equals(constraint.Constraint, slot.DeclaringType)))
                return false;

            var owner = FindMappedSourceOwner(sourceInterface);
            if (owner?.OriginalType is not { IsInterface: true } || owner.OriginalType.GenericParameters.Count != 0)
                return false;
            var mapped = FindMappedNativeMethod(slot, owner);
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.IsStatic ||
                !mapped.OriginalMethod.IsVirtual || mapped.OriginalMethod.Signature.GenericParameterCount != 0)
                return false;
            var plan = InteropRestorationPlan.For(context);
            var sourceResult = slot.Signature.ReturnType;
            if (plan.GetNativeIdentityRejection(sourceInterface) != null ||
                sourceResult.ElementType != ElementType.Void && plan.GetNativeIdentityRejection(sourceResult) != null)
                return false;
            var rewrittenReceiver = resolver.Resolve(receiver);
            var rewrittenInterface = resolver.Resolve(sourceInterface);
            var result = resolver.Resolve(sourceResult);
            if (rewrittenReceiver == null || rewrittenInterface == null || result == null) return false;

            // Native interface wrappers are CLR classes, not CLR constraints. The runtime
            // dispatch verifies the actual native interface and retains the original byref.
            var isVoid = result.ElementType == ElementType.Void;
            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport)
                .GetMethod(isVoid ? nameof(InteropGenericValueSupport.ConstrainedValueVoid) :
                    nameof(InteropGenericValueSupport.ConstrainedValueResult)));
            copy = body.Add(CilOpCodes.Ldstr, (string)mapped.NonGenericMethodInfoPointerField.Name);
            body.Add(CilOpCodes.Call, isVoid ? helper.MakeGenericInstanceMethod(rewrittenReceiver, rewrittenInterface) :
                helper.MakeGenericInstanceMethod(rewrittenReceiver, rewrittenInterface, result));
            return true;
        }
    }
}
