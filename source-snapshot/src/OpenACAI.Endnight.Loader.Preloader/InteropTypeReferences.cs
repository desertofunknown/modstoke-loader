// SPDX-License-Identifier: GPL-3.0-or-later
// Type rewriting adapted from Il2CppInterop rewrite contexts, TypeReferenceEx and EnumEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropTypeReferences
    {
        internal static bool IsValueType(this ITypeDescriptor type) =>
            type.IsValueType && type.FullName != "System.Enum";

        internal static GenericParameterAttributes StripValueTypeConstraint(
            this GenericParameterAttributes attributes) =>
            attributes & ~(GenericParameterAttributes.NotNullableValueTypeConstraint |
                           GenericParameterAttributes.VarianceMask);

        internal static ITypeDefOrRef Rewrite(InteropAssemblyContext assembly, ITypeDescriptor type) =>
            Rewrite(assembly, type?.ToTypeSignature()).ToTypeDefOrRef();

        internal static TypeSignature Rewrite(InteropAssemblyContext assembly, TypeSignature type)
        {
            var imports = assembly.Imports;
            if (type == null)
                return imports.Il2CppObjectBase;

            var module = assembly.NewAssembly.ManifestModule;
            if (type is ArrayBaseTypeSignature array)
            {
                if (array.Rank != 1)
                    return imports.Il2CppObjectBase;

                var element = array.BaseType;
                if (element.FullName == "System.String")
                    return imports.Il2CppStringArray;

                var rewrittenElement = Rewrite(assembly, element);
                var arrayType = element is GenericParameterSignature
                    ? imports.Il2CppArrayBase
                    : InteropTypeReferences.IsValueType(rewrittenElement)
                        ? imports.Il2CppStructArray
                        : imports.Il2CppReferenceArray;
                return new GenericInstanceTypeSignature(arrayType.ToTypeDefOrRef(), false, rewrittenElement);
            }

            if (type is GenericParameterSignature parameter)
                return new GenericParameterSignature(module, parameter.ParameterType, parameter.Index);
            if (type is ByReferenceTypeSignature byReference)
                return new ByReferenceTypeSignature(Rewrite(assembly, byReference.BaseType));
            if (type is PointerTypeSignature pointer)
                return new PointerTypeSignature(Rewrite(assembly, pointer.BaseType));

            if (type is GenericInstanceTypeSignature generic)
            {
                var definition = Rewrite(assembly, generic.GenericType);
                var instance = new GenericInstanceTypeSignature(definition, InteropTypeReferences.IsValueType(definition));
                foreach (var argument in generic.TypeArguments)
                    instance.TypeArguments.Add(Rewrite(assembly, argument));
                return instance;
            }

            if (IsPrimitive(type) || type.FullName == "System.TypedReference")
                return module.DefaultImporter.ImportTypeSignature(typeof(string).Assembly.GetType(type.FullName));
            if (type.FullName == "System.Void")
                return imports.Module.CorLibTypeFactory.Void;
            if (type.FullName == "System.String")
                return imports.Module.CorLibTypeFactory.String;
            if (type.FullName == "System.Object" || type.FullName == "System.Attribute")
            {
                var coreType = assembly.GlobalContext.GetAssemblyByName("mscorlib")
                    .GetTypeByName(type.FullName).NewType;
                return module.DefaultImporter.ImportType(coreType).ToTypeSignature();
            }

            var original = type.Resolve();
            var rewritten = assembly.GlobalContext.GetNewAssemblyForOriginal(original.DeclaringModule.Assembly)
                .GetContextForOriginalType(original).NewType;
            return module.DefaultImporter.ImportType(rewritten).ToTypeSignature();
        }

        internal static InteropTypeContext.TypeSpecifics Classify(InteropRewriteContext context, TypeSignature type)
        {
            if (IsPrimitive(type) || type is PointerTypeSignature || type.FullName == "System.TypedReference")
                return InteropTypeContext.TypeSpecifics.BlittableStruct;

            // Generic instances keep the reference classification, even when their definition is a value type.
            if (type is CorLibTypeSignature { ElementType: ElementType.String or ElementType.Object } or
                ArrayBaseTypeSignature or ByReferenceTypeSignature or GenericParameterSignature or GenericInstanceTypeSignature)
                return InteropTypeContext.TypeSpecifics.ReferenceType;

            var original = type.Resolve() ?? throw new Exception("Could not resolve " + type.FullName);
            return context.GetNewTypeForOriginal(original).ComputedTypeSpecifics;
        }

        private static bool IsPrimitive(TypeSignature type) =>
            type is CorLibTypeSignature
                { ElementType: >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U };
    }
}
