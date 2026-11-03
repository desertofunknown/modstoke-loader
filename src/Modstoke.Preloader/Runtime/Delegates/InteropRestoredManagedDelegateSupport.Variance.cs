// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static partial class InteropRestoredManagedDelegateSupport
    {
        internal static bool ValidateVariance(TypeDefinition owner, MethodSignature signature,
            Func<ITypeDefOrRef, TypeDefinition> resolveDefinition, bool nativeArrays, out string reason)
        {
            reason = null;
            var variant = false;
            foreach (var parameter in owner.GenericParameters)
            {
                var variance = parameter.Attributes & GenericParameterAttributes.VarianceMask;
                if (!IsVariance(variance))
                {
                    reason = "managed-delegate-variance: Invalid variance flags on " + parameter.Name;
                    return false;
                }
                variant |= variance != 0;
            }
            if (!variant) return true;
            if (!ValidPosition(signature.ReturnType, GenericParameterAttributes.Covariant, 0))
            {
                reason = "managed-delegate-variance: Invoke result violates the emitted covariance contract.";
                return false;
            }
            for (var index = 0; index < signature.ParameterTypes.Count; index++)
                if (!ValidPosition(signature.ParameterTypes[index], GenericParameterAttributes.Contravariant, 0))
                {
                    reason = "managed-delegate-variance: Invoke parameter " + index +
                        " violates the emitted contravariance contract.";
                    return false;
                }
            return true;

            bool ValidPosition(TypeSignature type, GenericParameterAttributes position, int depth)
            {
                if (type == null || depth > 32) return false;
                if (type is GenericParameterSignature parameter)
                {
                    if (parameter.ParameterType != GenericParameterType.Type ||
                        (uint)parameter.Index >= (uint)owner.GenericParameters.Count) return false;
                    var variance = owner.GenericParameters[parameter.Index].Attributes & GenericParameterAttributes.VarianceMask;
                    return variance == 0 || variance == position;
                }
                if (type is GenericInstanceTypeSignature instance)
                {
                    var definition = resolveDefinition(instance.GenericType);
                    if (definition == null || definition.GenericParameters.Count != instance.TypeArguments.Count) return false;
                    var variantContract = !definition.IsValueType && (definition.IsInterface ||
                        InteropRestoredUnmanagedDelegateSupport.IsSystemType(definition.BaseType, "System.MulticastDelegate"));
                    for (var index = 0; index < instance.TypeArguments.Count; index++)
                    {
                        var variance = definition.GenericParameters[index].Attributes & GenericParameterAttributes.VarianceMask;
                        if (!IsVariance(variance)) return false;
                        var nested = !variantContract || position == 0 ? 0 : variance;
                        if (position == GenericParameterAttributes.Contravariant)
                            nested = nested == GenericParameterAttributes.Covariant ? GenericParameterAttributes.Contravariant :
                                nested == GenericParameterAttributes.Contravariant ? GenericParameterAttributes.Covariant : 0;
                        if (!ValidPosition(instance.TypeArguments[index], nested, depth + 1)) return false;
                    }
                    return true;
                }
                if (type is ArrayBaseTypeSignature array)
                    // Planned native arrays become invariant wrapper classes, not covariant CLR arrays.
                    return ValidPosition(array.BaseType, nativeArrays ? 0 : position, depth + 1);
                if (type is ByReferenceTypeSignature reference)
                    return ValidPosition(reference.BaseType, 0, depth + 1);
                if (type is PointerTypeSignature pointer)
                    return ValidPosition(pointer.BaseType, 0, depth + 1);
                return type is CorLibTypeSignature or TypeDefOrRefSignature;
            }
        }

        private static bool IsVariance(GenericParameterAttributes value) =>
            value is 0 or GenericParameterAttributes.Covariant or GenericParameterAttributes.Contravariant;
    }
}
