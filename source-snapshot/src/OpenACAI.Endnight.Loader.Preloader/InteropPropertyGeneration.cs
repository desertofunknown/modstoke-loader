// SPDX-License-Identifier: GPL-3.0-or-later
// Property generation adapted from Il2CppInterop Pass70 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropPropertyGeneration
    {
        internal static void Generate(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                var original = type.OriginalType;
                var countsByBaseName = new Dictionary<string, int>();
                foreach (var oldProperty in original.Properties)
                {
                    FixPropertyDefinitionParameters(oldProperty);
                    var name = UnmanglePropertyName(assembly, oldProperty, type.NewType, countsByBaseName);
                    var propertyType = InteropTypeReferences.Rewrite(assembly, oldProperty.Signature.ReturnType);
                    var signature = oldProperty.Signature.HasThis
                        ? PropertySignature.CreateInstance(propertyType)
                        : PropertySignature.CreateStatic(propertyType);
                    foreach (var parameter in oldProperty.Signature.ParameterTypes)
                        signature.ParameterTypes.Add(InteropTypeReferences.Rewrite(assembly, parameter));

                    var property = new PropertyDefinition(name, oldProperty.Attributes, signature);
                    type.NewType.Properties.Add(property);
                    property.SetSemanticMethods(
                        oldProperty.GetMethod == null ? null : type.GetMethodByOldMethod(oldProperty.GetMethod).NewMethod,
                        oldProperty.SetMethod == null ? null : type.GetMethodByOldMethod(oldProperty.SetMethod).NewMethod);
                }

                string defaultMemberName = null;
                if (original.CustomAttributes.FirstOrDefault(IsDefaultMemberAttributeFake) != null)
                    defaultMemberName = "Item";
                else
                {
                    var attribute = original.CustomAttributes.FirstOrDefault(IsDefaultMemberAttributeReal);
                    if (attribute != null)
                        defaultMemberName = attribute.Signature?.FixedArguments[0].Element?.ToString() ?? "Item";
                }

                if (defaultMemberName != null)
                {
                    var module = assembly.Imports.Module;
                    var attributeType = module.DefaultImporter.ImportTypeSignature(typeof(DefaultMemberAttribute)).ToTypeDefOrRef();
                    var constructor = new MemberReference(attributeType, ".ctor",
                        MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, module.CorLibTypeFactory.String));
                    type.NewType.CustomAttributes.Add(new CustomAttribute(constructor,
                        new CustomAttributeSignature(new CustomAttributeArgument(
                            module.CorLibTypeFactory.String, defaultMemberName))));
                }
            }
        }

        private static bool IsDefaultMemberAttributeFake(CustomAttribute attribute)
        {
            return attribute.Constructor?.DeclaringType?.Name == "AttributeAttribute" &&
                attribute.Signature.NamedArguments.Any(argument =>
                    (argument.MemberName == "Type" && argument.Argument.Element is ITypeDescriptor
                        { Namespace: "System.Reflection", Name: nameof(DefaultMemberAttribute) }) ||
                    // Name support preserves older dummy assembly metadata.
                    (argument.MemberName == "Name" && argument.Argument.GetElementAsString() == nameof(DefaultMemberAttribute)));
        }

        private static bool IsDefaultMemberAttributeReal(CustomAttribute attribute)
        {
            return attribute.Constructor?.DeclaringType is
                { Namespace.Value: "System.Reflection", Name.Value: nameof(DefaultMemberAttribute) };
        }

        private static string UnmanglePropertyName(InteropAssemblyContext assembly, PropertyDefinition property,
            ITypeDefOrRef declaringType, Dictionary<string, int> countsByBaseName)
        {
            var options = assembly.GlobalContext.Options;
            if (options.PassthroughNames || !property.Name.IsObfuscated(options))
                return property.Name;

            var baseName = "prop_" + InteropTypeReferences.Rewrite(assembly, property.Signature.ReturnType)
                .GetUnmangledName(property.DeclaringType);
            countsByBaseName.TryGetValue(baseName, out var index);
            countsByBaseName[baseName] = index + 1;
            var name = baseName + "_" + index;

            if (options.RenameMap.TryGetValue(
                    declaringType.GetNamespacePrefix() + "." + declaringType.Name + "::" + name, out var typeName))
                name = typeName;
            else if (options.RenameMap.TryGetValue(declaringType.GetNamespacePrefix() + "::" + name, out var namespaceName))
                name = namespaceName;
            return name;
        }

        private static void FixPropertyDefinitionParameters(PropertyDefinition property)
        {
            // Cpp2IL can omit indexer parameters from the property signature.
            if (property.Signature is null or { ParameterTypes.Count: > 0 })
                return;

            var getter = property.GetMethod;
            if (getter?.Signature != null)
            {
                foreach (var parameter in getter.Signature.ParameterTypes)
                    property.Signature.ParameterTypes.Add(parameter);
                return;
            }

            var setter = property.SetMethod;
            if (setter?.Signature != null && setter.Signature.ParameterTypes.Count > 1)
            {
                foreach (var parameter in setter.Signature.ParameterTypes.Take(setter.Signature.ParameterTypes.Count - 1))
                    property.Signature.ParameterTypes.Add(parameter);
            }
        }
    }
}
