// SPDX-License-Identifier: GPL-3.0-or-later
// Type creation adapted from Il2CppInterop Pass10/15 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropTypeGeneration
    {
        internal static void CreateTypes(InteropRewriteContext context, InteropTypeNames names)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.OriginalAssembly.ManifestModule.TopLevelTypes)
            {
                if (type.Name == "<Module>" ||
                    (type.Namespace?.Value.StartsWith("Cpp2ILInjected", StringComparison.Ordinal) ?? false))
                    continue;
                CreateType(assembly, type, null, names);
            }
        }

        internal static void CreateMembers(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
                type.AddMembers();
        }

        private static void CreateType(InteropAssemblyContext assembly, TypeDefinition original,
            TypeDefinition parent, InteropTypeNames names)
        {
            var name = GetConvertedTypeName(assembly.GlobalContext, original, parent, names);
            var copy = new TypeDefinition(name.Namespace ?? GetNamespace(original, assembly),
                name.Name, AdjustAttributes(original.Attributes))
            {
                IsSequentialLayout = false
            };
            if (original.IsSealed && original.IsAbstract)
                copy.IsSealed = copy.IsAbstract = true;

            if (parent == null)
                assembly.NewAssembly.ManifestModule.TopLevelTypes.Add(copy);
            else
                parent.NestedTypes.Add(copy);

            foreach (var nested in original.NestedTypes)
                CreateType(assembly, nested, copy, names);
            // Registration order is significant to the subsequent context traversals.
            assembly.RegisterTypeRewrite(new InteropTypeContext(assembly, original, copy));
        }

        private static string GetNamespace(TypeDefinition type, InteropAssemblyContext assembly) =>
            type.Name == "<Module>" || type.DeclaringType != null
                ? type.Namespace
                : type.Namespace.UnSystemify(assembly.GlobalContext.Options);

        private static (string Namespace, string Name) GetConvertedTypeName(InteropRewriteContext context,
            TypeDefinition original, TypeDefinition parent, InteropTypeNames names)
        {
            if (context.Options.PassthroughNames)
                return (null, original.Name);
            if (!original.Name.IsObfuscated(context.Options))
                return (null, original.Name.MakeValidInSource());

            var baseName = names.RenamedTypes[original];
            var arity = original.GenericParameters.Count;
            var group = names.RenameGroups[((object)original.DeclaringType ?? original.Namespace, baseName, arity)];
            var suffix = arity == 0 ? "" : "`" + arity;
            var name = baseName + (group.Count == 1 ? "Unique" : group.IndexOf(original).ToString()) + suffix;
            var fullName = parent == null
                ? original.Namespace
                : parent.GetNamespacePrefix() + "." + parent.Name;
            if (!context.Options.RenameMap.TryGetValue(fullName + "." + name, out var mappedName))
                return (null, name);

            var module = original.DeclaringModule;
            if (module.TopLevelTypes.Any(type => type.FullName == mappedName))
            {
                InteropGenerationDiagnostics.Log(LogLevel.Warning,
                    $"[Rename map issue] {mappedName} already exists in {module.Name} (mapped from {fullName}.{name})");
                mappedName += "_Duplicate";
            }

            var separator = mappedName.LastIndexOf(".");
            return separator >= 0
                ? (mappedName.Substring(0, separator), mappedName.Substring(separator + 1))
                : (null, mappedName);
        }

        private static TypeAttributes AdjustAttributes(TypeAttributes attributes)
        {
            attributes |= TypeAttributes.BeforeFieldInit;
            attributes &= ~(TypeAttributes.Abstract | TypeAttributes.Interface);
            var visibility = attributes & TypeAttributes.VisibilityMask;
            return visibility == 0 || visibility == TypeAttributes.Public
                ? attributes | TypeAttributes.Public
                : (attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic;
        }
    }
}
