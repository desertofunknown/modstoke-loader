// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropRestoredGenericSupport
    {
        internal static bool ContainsGenericParameter(TypeSignature type) => type switch
        {
            GenericParameterSignature => true,
            GenericInstanceTypeSignature instance => instance.TypeArguments.Any(ContainsGenericParameter),
            TypeSpecificationSignature specification => ContainsGenericParameter(specification.BaseType),
            _ => false
        };

        internal static Type GetIntrinsicConstraint(TypeSignature type)
        {
            if (InteropRestoredTypeSupport.GetAssemblyName(type) is not
                ("mscorlib" or "netstandard" or "System.Runtime" or "System.Private.CoreLib"))
                return null;
            return type.FullName switch
            {
                "System.Object" => typeof(object),
                "System.ValueType" => typeof(ValueType),
                "System.Enum" => typeof(Enum),
                _ => null
            };
        }

        internal static bool TryCopyMethodParameters(MethodDefinition original, MethodDefinition target,
            InteropUnstripTypeResolver resolver, out string reason)
        {
            reason = null;
            if (target.GenericParameters.Count != 0)
            {
                reason = "managed-generic-parameters-already-declared";
                return false;
            }
            var copies = new List<GenericParameter>();
            foreach (var parameter in original.GenericParameters)
            {
                if ((parameter.Attributes & GenericParameterAttributes.VarianceMask) != 0)
                {
                    reason = "managed-generic-method-variance: " + parameter.Name;
                    return false;
                }
                var copy = new GenericParameter(parameter.Name.MakeValidInSource(), parameter.Attributes);
                if (!TryCopyConstraints(parameter, copy, resolver, out reason)) return false;
                copies.Add(copy);
            }
            foreach (var copy in copies) target.GenericParameters.Add(copy);
            return true;
        }

        internal static bool TryCopyTypeConstraints(TypeDefinition original, TypeDefinition target,
            InteropUnstripTypeResolver resolver, out string reason)
        {
            reason = null;
            if (original.GenericParameters.Count != target.GenericParameters.Count)
            {
                reason = "managed-generic-arity-mismatch: " + original.FullName;
                return false;
            }
            for (var index = 0; index < original.GenericParameters.Count; index++)
                if (!TryCopyConstraints(original.GenericParameters[index], target.GenericParameters[index], resolver, out reason))
                    return false;
            return true;
        }

        private static bool TryCopyConstraints(GenericParameter original, GenericParameter target,
            InteropUnstripTypeResolver resolver, out string reason)
        {
            reason = null;
            foreach (var constraint in original.Constraints)
            {
                var rewritten = resolver.ResolveGenericConstraint(constraint.Constraint?.ToTypeSignature(), out reason);
                if (rewritten == null) return false;
                target.Constraints.Add(new GenericParameterConstraint(rewritten.ToTypeDefOrRef()));
            }
            return true;
        }
    }
}
