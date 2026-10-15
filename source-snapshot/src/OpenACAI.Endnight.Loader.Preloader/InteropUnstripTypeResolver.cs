// SPDX-License-Identifier: GPL-3.0-or-later
// Type resolution adapted from Il2CppInterop Pass80UnstripMethods (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropUnstripTypeResolver
    {
        private readonly InteropRewriteContext context;
        private readonly InteropRuntimeReferences imports;

        internal InteropUnstripTypeResolver(InteropRewriteContext context, InteropRuntimeReferences imports)
        {
            this.context = context;
            this.imports = imports;
        }

        internal TypeSignature Resolve(TypeSignature type)
        {
            return Resolve(type, true);
        }

        internal TypeSignature Resolve(TypeSignature type, bool useSystemCorlibPrimitives)
        {
            var resolved = ResolveRaw(type, useSystemCorlibPrimitives);
            return resolved == null ? null : imports.Module.DefaultImporter.ImportTypeSignature(resolved);
        }

        internal TypeSignature ResolveRaw(TypeSignature type, bool useSystemCorlibPrimitives = true)
        {
            if (type == null)
                return null;
            if (type is GenericParameterSignature parameter)
                return new GenericParameterSignature(imports.Module, parameter.ParameterType, parameter.Index);
            if (type is ByReferenceTypeSignature reference)
                return Resolve(reference.BaseType)?.MakeByReferenceType();
            if (type is ArrayBaseTypeSignature array)
            {
                if (array.Rank != 1)
                    return null;
                var element = Resolve(array.BaseType);
                if (element == null)
                    return null;
                if (element.FullName == "System.String")
                    return imports.Il2CppStringArray;
                var arrayType = element is GenericParameterSignature
                    ? imports.Il2CppArrayBase
                    : element.IsValueType ? imports.Il2CppStructArray : imports.Il2CppReferenceArray;
                return new GenericInstanceTypeSignature(arrayType.ToTypeDefOrRef(), false, element);
            }
            if (type is PointerTypeSignature pointer)
                return Resolve(pointer.BaseType)?.MakePointerType();
            if (type is PinnedTypeSignature pinned)
                return Resolve(pinned.BaseType)?.MakePinnedType();
            if (type is CustomModifierTypeSignature modifier)
            {
                var element = Resolve(modifier.BaseType);
                var modifierType = Resolve(modifier.ModifierType.ToTypeSignature());
                return element != null && modifierType != null
                    ? new CustomModifierTypeSignature(modifierType.ToTypeDefOrRef(), modifier.IsRequired, element)
                    : null;
            }
            if (type is GenericInstanceTypeSignature instance)
            {
                var definition = ResolveRaw(instance.GenericType.ToTypeSignature());
                if (definition == null)
                    return null;
                var resolved = new GenericInstanceTypeSignature(definition.ToTypeDefOrRef(),
                    InteropTypeReferences.IsValueType(definition));
                foreach (var argument in instance.TypeArguments)
                {
                    var resolvedArgument = Resolve(argument);
                    if (resolvedArgument == null)
                        return null;
                    resolved.TypeArguments.Add(resolvedArgument);
                }
                return resolved;
            }
            if (type is BoxedTypeSignature or FunctionPointerTypeSignature)
                return null;
            if (type is SentinelTypeSignature)
                return type;
            if (type.DeclaringType != null)
            {
                var enclosing = ResolveRaw(type.DeclaringType.ToTypeSignature())?.Resolve();
                if (enclosing == null)
                    return null;
                var assembly = context.GetContextForNewAssembly(enclosing.DeclaringModule.Assembly);
                var nested = assembly.TryGetTypeByName(type.FullName)?.NewType
                    ?? enclosing.NestedTypes.FirstOrDefault(candidate => candidate.Name == type.Name);
                return nested?.ToTypeSignature();
            }

            var assemblyName = type.Scope.Name;
            if (assemblyName.EndsWith(".dll"))
                assemblyName = assemblyName.Substring(0, assemblyName.Length - 4);

            // Static member declarers can require rewritten corlib types; nested signatures still use managed primitives.
            var elementType = type.ElementType;
            var primitive = type is CorLibTypeSignature &&
                (elementType is >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U);
            if (useSystemCorlibPrimitives && (primitive || elementType is ElementType.String or ElementType.Void))
                return imports.Module.CorLibTypeFactory.FromElementType(elementType);

            if (assemblyName == "UnityEngine")
            {
                foreach (var assembly in context.Assemblies)
                {
                    if (!assembly.NewAssembly.Name.StartsWith("UnityEngine"))
                        continue;
                    var resolved = assembly.TryGetTypeByName(type.FullName)?.NewType;
                    if (resolved != null)
                        return resolved.ToTypeSignature();
                }
            }
            return context.TryGetAssemblyByName(assemblyName)?.TryGetTypeByName(type.FullName)
                ?.NewType.ToTypeSignature();
        }
    }
}
