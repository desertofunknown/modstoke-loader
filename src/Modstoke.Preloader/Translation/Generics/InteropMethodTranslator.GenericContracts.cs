// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        internal static bool ValidateGenericSignature(MethodDefinition original, MethodDefinition rewritten,
            InteropTypeContext owner, InteropRuntimeReferences imports, out string reason)
        {
            var translator = new InteropMethodTranslator(rewritten, owner, imports) { sourceMethod = original };
            var valid = translator.ValidateManagedGenericUse(original.Signature.ReturnType);
            foreach (var parameter in original.Signature.ParameterTypes)
                valid &= translator.ValidateManagedGenericUse(parameter);
            reason = translator.failure?.Detail;
            return valid;
        }

        private bool ValidateManagedGenericUse(TypeSignature source)
        {
            if (source is TypeSpecificationSignature specification && source is not GenericInstanceTypeSignature)
                return ValidateManagedGenericUse(specification.BaseType);
            if (source is not GenericInstanceTypeSignature instance) return true;
            foreach (var argument in instance.TypeArguments)
                if (!ValidateManagedGenericUse(argument)) return false;
            if (!InteropRestorationPlan.For(context).IsManagedOnlyDefinition(instance.GenericType.ToTypeSignature()))
                return true;
            var rewritten = resolver.Resolve(source) as GenericInstanceTypeSignature;
            var definition = rewritten?.GenericType.Resolve();
            if (definition == null || definition.GenericParameters.Count != rewritten.TypeArguments.Count)
                return Fail("managed-generic-contract", "The restored generic declaration or arity is unavailable.", currentInstruction, source);
            var substitution = GenericContext.FromType(rewritten);
            for (var index = 0; index < definition.GenericParameters.Count; index++)
            {
                var parameter = definition.GenericParameters[index];
                var argument = rewritten.TypeArguments[index];
                var flags = parameter.Attributes & GenericParameterAttributes.SpecialConstraintMask;
                if (!SatisfiesSpecialConstraints(argument, flags))
                    return Fail("managed-generic-contract", "A restored helper argument does not prove its CLR class/struct/new() constraints.", currentInstruction, source);
                foreach (var constraint in parameter.Constraints)
                    if (!ProvesManagedBase(argument, constraint.Constraint.ToTypeSignature().InstantiateGenericTypes(substitution),
                        new HashSet<string>(), 0))
                        return Fail("managed-generic-contract", "A restored helper argument does not prove its CLR base-type constraint.", currentInstruction, source);
            }
            return true;
        }

        private GenericParameter TargetGenericParameter(GenericParameterSignature signature)
        {
            var parameters = signature.ParameterType == GenericParameterType.Method
                ? target.GenericParameters : ownerType.NewType.GenericParameters;
            return signature.Index < parameters.Count ? parameters[signature.Index] : null;
        }

        private bool ValidateManagedMethodUse(MethodDefinition definition, IMethodDescriptor call)
        {
            if (call is not MethodSpecification specification) return true;
            if (definition.GenericParameters.Count != specification.Signature.TypeArguments.Count)
                return Fail("managed-generic-contract", "The restored method generic arity does not match.", currentInstruction);
            var substitution = GenericContext.FromMethod(call);
            for (var index = 0; index < definition.GenericParameters.Count; index++)
            {
                var parameter = definition.GenericParameters[index];
                var argument = specification.Signature.TypeArguments[index];
                if (!SatisfiesSpecialConstraints(argument, parameter.Attributes & GenericParameterAttributes.SpecialConstraintMask) ||
                    parameter.Constraints.Any(constraint => !ProvesManagedBase(argument,
                        constraint.Constraint.ToTypeSignature().InstantiateGenericTypes(substitution), new HashSet<string>(), 0)))
                    return Fail("managed-generic-contract", "The restored method argument does not prove its CLR constraints.", currentInstruction);
            }
            return true;
        }

        private bool SatisfiesSpecialConstraints(TypeSignature argument, GenericParameterAttributes flags)
        {
            if (flags == 0) return true;
            if (argument is GenericParameterSignature generic)
            {
                var actual = TargetGenericParameter(generic)?.Attributes ?? 0;
                if ((actual & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                    actual |= GenericParameterAttributes.DefaultConstructorConstraint;
                return (actual & flags) == flags;
            }
            if ((flags & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && argument.IsValueType) return false;
            if ((flags & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 &&
                (!argument.IsValueType || argument is GenericInstanceTypeSignature nullable &&
                    nullable.GenericType.FullName == "System.Nullable`1")) return false;
            if ((flags & GenericParameterAttributes.DefaultConstructorConstraint) == 0 || argument.IsValueType) return true;
            var definition = argument.Resolve();
            return definition is { IsAbstract: false } && definition.Methods.Any(method =>
                method.IsConstructor && !method.IsStatic && method.IsPublic && method.Signature.ParameterTypes.Count == 0);
        }

        private bool ProvesManagedBase(TypeSignature argument, TypeSignature required, HashSet<string> visited, int depth,
            bool rewritten = true)
        {
            if (depth > 32 || argument == null || required == null) return false;
            if (SignatureComparer.Default.Equals(argument, required)) return true;
            if (InteropRestoredGenericSupport.GetIntrinsicConstraint(required) == typeof(object)) return true;
            if (!visited.Add(argument.FullName + " -> " + required.FullName)) return false;
            if (ProvesRestoredDelegateVariance(argument, required, visited, depth)) return true;
            if (argument is GenericParameterSignature generic)
            {
                var method = rewritten ? target : sourceMethod;
                var type = rewritten ? ownerType.NewType : sourceMethod.DeclaringType;
                var parameters = generic.ParameterType == GenericParameterType.Method
                    ? method.GenericParameters : type.GenericParameters;
                var parameter = (uint)generic.Index < (uint)parameters.Count ? parameters[generic.Index] : null;
                if (parameter == null) return false;
                if (InteropRestoredGenericSupport.GetIntrinsicConstraint(required) == typeof(System.ValueType) &&
                    (parameter.Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) return true;
                return parameter.Constraints.Any(constraint =>
                    ProvesManagedBase(constraint.Constraint?.ToTypeSignature(), required, visited, depth + 1, rewritten));
            }
            if (InteropRestoredGenericSupport.GetIntrinsicConstraint(required) == typeof(System.ValueType) && argument.IsValueType)
                return true;
            var definition = argument.Resolve();
            if (definition != null)
                foreach (var implementation in definition.Interfaces)
                {
                    var contract = implementation.Interface.ToTypeSignature();
                    if (argument is GenericInstanceTypeSignature constructed)
                        contract = contract.InstantiateGenericTypes(GenericContext.FromType(constructed));
                    if (ProvesManagedBase(contract, required, visited, depth + 1, rewritten)) return true;
                }
            var parent = definition?.BaseType?.ToTypeSignature();
            if (parent != null && argument is GenericInstanceTypeSignature instance)
                parent = parent.InstantiateGenericTypes(GenericContext.FromType(instance));
            return ProvesManagedBase(parent, required, visited, depth + 1, rewritten);
        }
    }
}
