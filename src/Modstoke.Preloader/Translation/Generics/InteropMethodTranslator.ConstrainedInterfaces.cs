// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateConstrainedInterface(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor slot, out CilInstruction copy)
        {
            copy = null;
            if (receiver is not GenericParameterSignature generic || slot is MethodSpecification ||
                slot.DeclaringType?.ToTypeSignature() is not (TypeDefOrRefSignature or GenericInstanceTypeSignature) ||
                !InteropDelegateContracts.HasStandardConvention(slot.Signature) || !slot.Signature.HasThis ||
                slot.Signature.GenericParameterCount != 0 || slot.Signature.ParameterTypes.Count > 4) return false;
            var declarations = generic.ParameterType == GenericParameterType.Method
                ? sourceMethod.GenericParameters : sourceMethod.DeclaringType.GenericParameters;
            var count = slot.Signature.ParameterTypes.Count;
            if (generic.Index < 0 || generic.Index >= declarations.Count ||
                !TryGetStackValue(prefix, count, out var value) || value.Type is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, receiver)) return false;

            var sourceInterface = slot.DeclaringType.ToTypeSignature();
            var owner = FindMappedSourceOwner(sourceInterface);
            if (owner?.OriginalType is not { IsInterface: true } || owner.OriginalType.GenericParameters.Count !=
                (sourceInterface is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0)) return false;
            var mapped = FindMappedNativeMethod(slot, owner);
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.IsStatic ||
                !mapped.OriginalMethod.IsVirtual || mapped.OriginalMethod.Signature.GenericParameterCount != 0) return false;

            var plan = InteropRestorationPlan.For(context);
            var binding = GenericContext.FromMethod(slot);
            var sourceResult = slot.Signature.ReturnType.InstantiateGenericTypes(binding);
            var isVoid = sourceResult.ElementType == ElementType.Void;
            if (plan.GetNativeIdentityRejection(sourceInterface) != null ||
                !isVoid && !CanPass(sourceResult)) return false;
            var rewrittenReceiver = resolver.Resolve(receiver);
            var rewrittenInterface = resolver.Resolve(sourceInterface);
            var result = resolver.Resolve(sourceResult);
            if (rewrittenReceiver == null || rewrittenInterface == null || result == null) return false;

            var arguments = new List<TypeSignature> { rewrittenReceiver, rewrittenInterface };
            for (var index = 0; index < count; index++)
            {
                var parameter = slot.Signature.ParameterTypes[index].InstantiateGenericTypes(binding);
                if (!CanPass(parameter) || !TryGetStackValue(prefix, count - index - 1, out var argument)) return false;
                var sourceArgument = argument.IsNull ? parameter : argument.Type;
                // Stack integers may stand for a declared enum or smaller integer type.
                if (!argument.IsNull && NumericStackValue(parameter).Type != null &&
                    SameStackValue(NumericStackValue(parameter), NumericStackValue(sourceArgument))) sourceArgument = parameter;
                if (!CanPass(sourceArgument)) return false;
                var rewritten = resolver.Resolve(sourceArgument);
                if (rewritten == null) return false;
                arguments.Add(rewritten);
            }
            if (!isVoid) arguments.Add(result);

            // Passing actual stack argument types avoids treating native interface wrappers
            // as CLR bases. The helper validates each closed native parameter before invocation.
            var name = (isVoid ? "ConstrainedInterfaceVoid" : "ConstrainedInterfaceResult") + count;
            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropGenericValueSupport).GetMethod(name));
            copy = body.Add(CilOpCodes.Ldstr, (string)mapped.NonGenericMethodInfoPointerField.Name);
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(arguments.ToArray()));
            return true;

            bool CanPass(TypeSignature type) => type != null &&
                type is not (ByReferenceTypeSignature or PointerTypeSignature or FunctionPointerTypeSignature or CustomModifierTypeSignature) &&
                type.ElementType is not (ElementType.Void or ElementType.TypedByRef) &&
                plan.GetNativeIdentityRejection(type) == null;
        }
    }
}
