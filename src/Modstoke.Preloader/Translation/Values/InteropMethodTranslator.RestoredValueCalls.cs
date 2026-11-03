// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanRestoredValueCall(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor slot, out IMethodDescriptor implementation)
        {
            implementation = null;
            if (receiver is not TypeDefOrRefSignature ||
                receiver.Resolve() is not { IsValueType: true, IsEnum: false } sourceType ||
                sourceType.GenericParameters.Count != 0 || slot is MethodSpecification ||
                !InteropDelegateContracts.HasStandardConvention(slot.Signature) ||
                !slot.Signature.HasThis || slot.Signature.GenericParameterCount != 0 ||
                !InteropRestorationPlan.For(context).IsManagedOnlyDefinition(receiver) ||
                !TryGetStackValue(prefix, slot.Signature.ParameterTypes.Count, out var value) ||
                value.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver))
                return false;

            var type = FindMappedSourceOwner(receiver);
            if (type?.OriginalType != null || type?.NewType is not { IsValueType: true, IsEnum: false } rewrittenType ||
                rewrittenType.GenericParameters.Count != 0 ||
                !SignatureComparer.Default.Equals(resolver.ResolveRaw(receiver), rewrittenType.ToTypeSignature()))
                return false;

            var owner = slot.DeclaringType?.ToTypeSignature();
            MethodDefinition sourceImplementation;
            if (sourceType.Interfaces.Any(entry =>
                SignatureComparer.Default.Equals(entry.Interface.ToTypeSignature(), owner)))
            {
                sourceImplementation = FindRestoredInterfaceImplementation(sourceType, slot);
            }
            else
            {
                var intrinsic = InteropRestoredGenericSupport.GetIntrinsicConstraint(owner);
                if (intrinsic != typeof(object) && intrinsic != typeof(System.ValueType)) return false;
                sourceImplementation = FindDirectObjectOverride(sourceType, slot);
            }
            if (sourceImplementation == null || !InteropRestoredValueContracts.CanRestore(sourceImplementation, rewrittenType))
                return false;

            var result = resolver.Resolve(sourceImplementation.Signature.ReturnType);
            if (result == null) return false;
            var signature = MethodSignature.CreateInstance(result);
            foreach (var parameter in sourceImplementation.Signature.ParameterTypes)
            {
                var rewritten = resolver.Resolve(parameter);
                if (rewritten == null) return false;
                signature.ParameterTypes.Add(rewritten);
            }
            if (rewrittenType.Methods.Count(method => method.Name == sourceImplementation.Name &&
                SignatureComparer.Default.Equals(method.Signature, signature)) != 1) return false;

            // An exact value receiver cannot have a subtype override. Call its restored
            // body with the original address, retaining mutations without boxing or a native identity.
            implementation = sourceImplementation;
            return true;
        }

        private static MethodDefinition FindRestoredInterfaceImplementation(TypeDefinition type, IMethodDescriptor slot)
        {
            var signature = slot.Signature.InstantiateGenericTypes(GenericContext.FromMethod(slot));
            var overrides = type.MethodImplementations.Where(entry =>
                entry.Declaration.Name == slot.Name &&
                SignatureComparer.Default.Equals(entry.Declaration.DeclaringType, slot.DeclaringType) &&
                SignatureComparer.Default.Equals(entry.Declaration.Signature.InstantiateGenericTypes(
                    GenericContext.FromMethod(entry.Declaration)), signature)).ToArray();
            if (overrides.Length != 0)
            {
                if (overrides.Length != 1) return null;
                var body = overrides[0].Body?.Resolve();
                return body?.DeclaringType == type && Matches(body) ? body : null;
            }
            var candidates = type.Methods.Where(method => method.IsPublic && method.Name == slot.Name && Matches(method)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;

            bool Matches(MethodDefinition method) => method.IsVirtual && !method.IsStatic && !method.IsAbstract &&
                method.Signature.GenericParameterCount == 0 && SignatureComparer.Default.Equals(method.Signature, signature);
        }
    }
}
