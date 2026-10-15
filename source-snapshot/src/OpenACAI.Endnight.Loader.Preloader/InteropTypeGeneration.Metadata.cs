// SPDX-License-Identifier: GPL-3.0-or-later
// Type metadata generation adapted from Il2CppInterop Pass12, Pass13 and Pass22 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropTypeGeneration
    {
        internal static void FillTypes(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                foreach (var parameter in type.OriginalType.GenericParameters)
                    type.NewType.GenericParameters.Add(new GenericParameter(
                        parameter.Name.MakeValidInSource(), parameter.Attributes.StripValueTypeConstraint()));

                var module = assembly.Imports.Module;
                if (type.OriginalType.IsEnum)
                    type.NewType.BaseType = module.DefaultImporter.ImportTypeSignature(typeof(Enum)).ToTypeDefOrRef();
                else if (type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct)
                    type.NewType.BaseType = module.DefaultImporter.ImportTypeSignature(typeof(ValueType)).ToTypeDefOrRef();
            }

            // Generic base rewriting requires every target's generic parameters and value-typeness first.
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
                if (!type.OriginalType.IsEnum &&
                    type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct)
                    type.NewType.BaseType = InteropTypeReferences.Rewrite(assembly, type.OriginalType.BaseType);
        }

        internal static void FillGenericConstraints(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                for (var i = 0; i < type.OriginalType.GenericParameters.Count; i++)
                {
                    var originalParameter = type.OriginalType.GenericParameters[i];
                    var newParameter = type.NewType.GenericParameters[i];
                    foreach (var constraint in originalParameter.Constraints)
                    {
                        // Match the installed internal constraint classifiers, including unresolved types.
                        if (constraint.Constraint?.FullName == "System.ValueType" ||
                            constraint.Constraint?.Resolve()?.IsInterface == true)
                            continue;

                        if (constraint.Constraint?.FullName == "System.Enum")
                        {
                            newParameter.Constraints.Add(new GenericParameterConstraint(
                                type.AssemblyContext.Imports.Module.DefaultImporter
                                    .ImportTypeSignature(typeof(Enum)).ToTypeDefOrRef()));
                            continue;
                        }

                        newParameter.Constraints.Add(new GenericParameterConstraint(
                            InteropTypeReferences.Rewrite(assembly, constraint.Constraint)));
                    }
                }
            }
        }

        internal static void GenerateEnums(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                var original = type.OriginalType;
                if (!original.IsEnum)
                    continue;

                var newType = type.NewType;
                var module = assembly.Imports.Module;
                if (original.Namespace.NameShouldBePrefixed(context.Options))
                {
                    newType.CustomAttributes.Add(new CustomAttribute(
                        (ICustomAttributeType)assembly.Imports.OriginalNameAttributector.Value,
                        new CustomAttributeSignature(
                            new CustomAttributeArgument(module.CorLibTypeFactory.String, original.DeclaringModule?.Name ?? ""),
                            new CustomAttributeArgument(module.CorLibTypeFactory.String, original.Namespace ?? ""),
                            new CustomAttributeArgument(module.CorLibTypeFactory.String, original.Name ?? ""))));
                }

                if (original.CustomAttributes.Any(attribute =>
                        attribute.Constructor?.DeclaringType?.FullName == "System.FlagsAttribute"))
                {
                    var attributeType = module.DefaultImporter.ImportTypeSignature(typeof(FlagsAttribute)).ToTypeDefOrRef();
                    var constructor = new MemberReference(attributeType, ".ctor",
                        MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
                    newType.CustomAttributes.Add(new CustomAttribute(constructor));
                }

                foreach (var field in original.Fields)
                {
                    var name = field.Name;
                    if (!context.Options.PassthroughNames && name.IsObfuscated(context.Options))
                        name = "EnumValue" + field.Constant;

                    if (context.Options.RenameMap.TryGetValue(
                            newType.GetNamespacePrefix() + "." + newType.Name + "::" + name, out var renamed))
                        name = renamed;

                    var copy = new FieldDefinition(name, field.Attributes | FieldAttributes.HasDefault,
                        InteropTypeReferences.Rewrite(assembly, field.Signature.FieldType));
                    newType.Fields.Add(copy);
                    copy.Constant = field.Constant;
                }
            }
        }
    }
}
