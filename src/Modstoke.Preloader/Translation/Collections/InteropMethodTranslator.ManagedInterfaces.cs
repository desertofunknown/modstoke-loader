// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateManagedInterface(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor method, out CilInstruction translated)
        {
            translated = null;
            if (receiver is not GenericParameterSignature || method is MethodSpecification ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || !method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 ||
                !TryGetStackValue(prefix, method.Signature.ParameterTypes.Count, out var value) ||
                value.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver)) return false;

            var sourceContract = method.DeclaringType?.ToTypeSignature();
            var plan = InteropRestorationPlan.For(context);
            if (!plan.IsManagedOnlyDefinition(sourceContract)) return false;
            var contract = FindMappedSourceOwner(sourceContract);
            if (contract?.OriginalType != null || contract?.NewType is not { IsInterface: true } declaration)
                return false;
            var rewrittenReceiver = resolver.Resolve(receiver);
            var rewrittenContract = resolver.Resolve(sourceContract);
            if (rewrittenReceiver == null || rewrittenContract == null ||
                !ProvesManagedBase(rewrittenReceiver, rewrittenContract, new HashSet<string>(), 0)) return false;

            var result = resolver.Resolve(method.Signature.ReturnType);
            if (result == null) return false;
            var signature = MethodSignature.CreateInstance(result);
            foreach (var parameter in method.Signature.ParameterTypes)
            {
                var rewritten = resolver.Resolve(parameter);
                if (rewritten == null) return false;
                signature.ParameterTypes.Add(rewritten);
            }
            if (declaration.Methods.Count(candidate => candidate.Name == method.Name &&
                SignatureComparer.Default.Equals(candidate.Signature, signature)) != 1) return false;

            // This is a real CLR interface and a constraint retained on the rewritten T.
            // Keep constrained.callvirt so CLR value receivers retain their original storage.
            translated = body.Add(CilOpCodes.Constrained, rewrittenReceiver.ToTypeDefOrRef());
            return true;
        }
    }
}
