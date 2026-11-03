// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool CanAssignRestoredDelegate(TypeSignature source, TypeSignature destination) =>
            source is GenericInstanceTypeSignature && destination is GenericInstanceTypeSignature &&
            ProvesRestoredDelegateVariance(source, destination, new HashSet<string>(), 0) &&
            ProvesRestoredDelegateVariance(resolver.Resolve(source), resolver.Resolve(destination), new HashSet<string>(), 0);

        private bool ProvesRestoredDelegateVariance(TypeSignature argument, TypeSignature required,
            HashSet<string> visited, int depth)
        {
            if (depth > 32 || argument is not GenericInstanceTypeSignature from ||
                required is not GenericInstanceTypeSignature to) return false;
            var owner = RestoredDelegateOwner(from, out var fromRewritten);
            if (owner == null || RestoredDelegateOwner(to, out var toRewritten) != owner ||
                fromRewritten != toRewritten ||
                from.TypeArguments.Count != owner.NewType.GenericParameters.Count ||
                to.TypeArguments.Count != from.TypeArguments.Count) return false;

            for (var index = 0; index < from.TypeArguments.Count; index++)
            {
                var variance = owner.NewType.GenericParameters[index].Attributes & GenericParameterAttributes.VarianceMask;
                if (variance is not (0 or GenericParameterAttributes.Covariant or GenericParameterAttributes.Contravariant))
                    return false;
                var fromArgument = from.TypeArguments[index];
                var toArgument = to.TypeArguments[index];
                if (SignatureComparer.Default.Equals(fromArgument, toArgument)) continue;
                if (variance == 0 || !IsDelegateVarianceReference(fromArgument, fromRewritten) ||
                    !IsDelegateVarianceReference(toArgument, toRewritten)) return false;

                // Each argument gets the same ancestor path, not another argument's completed proof.
                var branch = new HashSet<string>(visited);
                if (variance == GenericParameterAttributes.Contravariant
                    ? !ProvesManagedBase(toArgument, fromArgument, branch, depth + 1, fromRewritten)
                    : !ProvesManagedBase(fromArgument, toArgument, branch, depth + 1, fromRewritten)) return false;
            }
            return true;
        }

        private InteropTypeContext RestoredDelegateOwner(GenericInstanceTypeSignature type, out bool rewritten)
        {
            rewritten = false;
            if (type.IsValueType) return null;
            var owner = FindMappedSourceOwner(type);
            if (!InteropRestoredManagedDelegateSupport.IsRestored(owner)) return null;
            rewritten = type.GenericType.Resolve() == owner.NewType;
            // Source and emitted signatures must bind to the same registered CLR declaration.
            return rewritten || resolver.ResolveRaw(type.GenericType.ToTypeSignature())?.Resolve() == owner.NewType
                ? owner : null;
        }

        private bool IsDelegateVarianceReference(TypeSignature type, bool rewritten) =>
            type is not ArrayBaseTypeSignature && IsDelegateReferenceType(type, rewritten) &&
            (type is GenericParameterSignature ||
             type is CorLibTypeSignature { ElementType: ElementType.Object or ElementType.String } ||
             type is TypeDefOrRefSignature or GenericInstanceTypeSignature && type.Resolve() is { IsValueType: false });
    }
}
