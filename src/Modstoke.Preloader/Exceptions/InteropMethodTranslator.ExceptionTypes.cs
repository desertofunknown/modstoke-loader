// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private string GetNativeExceptionRejection(TypeSignature sourceType, TypeSignature rewritten, bool catchObject = false)
        {
            if (sourceType == null || rewritten == null)
                return "The exception type could not be resolved.";
            var plan = InteropRestorationPlan.For(context);
            if (plan.ContainsManagedOnlyStorage(sourceType))
                return "A managed-only exception type has no native exception identity.";
            var rejection = plan.GetNativeIdentityRejection(sourceType);
            if (rejection != null)
                return rejection;
            var nativeException = context.TryGetAssemblyByName("mscorlib")?.TryGetTypeByName("System.Exception");
            if (nativeException?.OriginalType == null || nativeException.ClassPointerFieldRef == null ||
                sourceType is GenericParameterSignature parameter && !HasNativeExceptionConstraint(parameter, new()) ||
                (!catchObject && !HasExceptionBase(rewritten, nativeException.NewType.ToTypeSignature())) ||
                !HasExceptionBase(rewritten, imports.Il2CppObjectBase))
                return "The exception type or its generic constraints do not prove a metadata-backed native exception wrapper.";
            return null;
        }

        private bool HasNativeExceptionConstraint(GenericParameterSignature parameter,
            HashSet<(GenericParameterType, int)> visited)
        {
            if (!visited.Add((parameter.ParameterType, parameter.Index)))
                return false;
            var declaration = GetExceptionGenericParameter(parameter, sourceMethod);
            if (declaration == null)
                return false;
            foreach (var constraint in declaration.Constraints)
            {
                var type = constraint.Constraint?.ToTypeSignature();
                if (type is GenericParameterSignature other)
                {
                    if (HasNativeExceptionConstraint(other, visited))
                        return true;
                }
                else if (type != null && GetNativeExceptionRejection(type, resolver.Resolve(type)) == null)
                    return true;
            }
            return false;
        }

        private bool HasExceptionBase(TypeSignature type, TypeSignature expectedBase)
        {
            return Visit(type, new HashSet<TypeDefinition>(), new HashSet<(GenericParameterType, int)>());

            bool Visit(TypeSignature current, HashSet<TypeDefinition> definitions,
                HashSet<(GenericParameterType, int)> parameters)
            {
                if (current == null || current.IsValueType)
                    return false;
                if (SignatureComparer.Default.Equals(current, expectedBase))
                    return true;
                if (current is GenericParameterSignature parameter)
                {
                    if (!parameters.Add((parameter.ParameterType, parameter.Index)))
                        return false;
                    var declaration = GetExceptionGenericParameter(parameter, target);
                    if (declaration == null)
                        return false;
                    foreach (var constraint in declaration.Constraints)
                        if (Visit(constraint.Constraint?.ToTypeSignature(), definitions, parameters))
                            return true;
                    return false;
                }
                if (current is not (TypeDefOrRefSignature or GenericInstanceTypeSignature))
                    return false;
                var definition = current.Resolve();
                if (definition == null || !definitions.Add(definition))
                    return false;
                var arguments = current as GenericInstanceTypeSignature;
                if (definition.GenericParameters.Count != (arguments?.TypeArguments.Count ?? 0))
                    return false; // An uninstantiated generic definition is not an exception value.
                var baseType = definition.BaseType?.ToTypeSignature();
                if (baseType != null && arguments != null)
                    baseType = baseType.InstantiateGenericTypes(GenericContext.FromType(arguments));
                return Visit(baseType, definitions, parameters);
            }
        }

        private static GenericParameter GetExceptionGenericParameter(GenericParameterSignature parameter, MethodDefinition method)
        {
            if (parameter.Index < 0 || method == null)
                return null;
            if (parameter.ParameterType == GenericParameterType.Method)
                return parameter.Index < method.GenericParameters.Count ? method.GenericParameters[parameter.Index] : null;
            return parameter.ParameterType == GenericParameterType.Type && method.DeclaringType != null &&
                parameter.Index < method.DeclaringType.GenericParameters.Count
                ? method.DeclaringType.GenericParameters[parameter.Index] : null;
        }

        private IMethodDescriptor ImportExceptionHelper(string name, TypeSignature argument) =>
            ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropExceptionSupport)
                .GetMethod(name))).MakeGenericInstanceMethod(imports.Module.DefaultImporter.ImportTypeSignature(argument));
    }
}
