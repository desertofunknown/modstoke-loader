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
        private bool TryPlanScopedInterfaceCall(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var position = sourceMethod.CilMethodBody.Instructions.IndexOf(instruction);
            if (instruction.OpCode.Code != CilCode.Callvirt || method is MethodSpecification || position < 1 ||
                sourceMethod.CilMethodBody.Instructions[position - 1] is not
                    { OpCode.Code: CilCode.Constrained, Operand: ITypeDefOrRef constraint } ||
                constraint.ToTypeSignature() is not GenericParameterSignature receiver ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || !method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 || method.Signature.ParameterTypes.Count != 1 ||
                method.Signature.ReturnType.ElementType != ElementType.Void || consumed.Length != 2)
                return false;
            var declarations = receiver.ParameterType == GenericParameterType.Method
                ? sourceMethod.GenericParameters : sourceMethod.DeclaringType.GenericParameters;
            if (receiver.Index < 0 || receiver.Index >= declarations.Count ||
                (declarations[receiver.Index].Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
                return false;

            var sourceInterface = method.DeclaringType?.ToTypeSignature();
            var owner = FindMappedSourceOwner(sourceInterface);
            if (owner?.OriginalType is not { IsInterface: true } || owner.OriginalType.GenericParameters.Count !=
                (sourceInterface is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(sourceInterface) != null)
                return false;
            var mapped = FindMappedNativeMethod(method, owner);
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.IsStatic ||
                !mapped.OriginalMethod.IsVirtual || mapped.OriginalMethod.Signature.GenericParameterCount != 0 ||
                method.Signature.ParameterTypes[0].InstantiateGenericTypes(GenericContext.FromMethod(method)) is not
                    ByReferenceTypeSignature argument ||
                !MatchesLocation(consumed[0], receiver) || !MatchesLocation(consumed[1], argument.BaseType))
                return false;
            var rewrittenReceiver = resolver.Resolve(receiver);
            var rewrittenInterface = resolver.Resolve(sourceInterface);
            var rewrittenArgument = resolver.Resolve(argument.BaseType);
            if (rewrittenReceiver == null || rewrittenInterface == null || rewrittenArgument == null) return false;
            use = new ReferenceUse { Call = new ReferenceCall
            {
                ConstrainedReceiver = rewrittenReceiver,
                Owner = rewrittenInterface,
                MethodPointerName = mapped.NonGenericMethodInfoPointerField.Name,
                Parameters = new[] { rewrittenArgument }
            } };
            return true;

            bool MatchesLocation(ReferenceOrigins origins, TypeSignature element)
            {
                if (element is not GenericParameterSignature)
                    return TryGetWritableCallLocation(origins, element, out _,
                        allowAggregate: true, allowReferenceSlots: true, allowStatic: true);
                return TryGetReferenceLocation(origins, out var location, out var readOnly) && !readOnly &&
                    SignatureComparer.Default.Equals(location.SourceElement, element) &&
                    SignatureComparer.Default.Equals(location.Element, resolver.Resolve(element)) &&
                    (location.OwnerType != null || location.StaticOwnerType != null) &&
                    location.NativeFieldPointerName != null;
            }
        }
    }
}
