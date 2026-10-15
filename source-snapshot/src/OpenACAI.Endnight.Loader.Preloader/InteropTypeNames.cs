// SPDX-License-Identifier: GPL-3.0-or-later
// Type naming adapted from Il2CppInterop Pass05 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropTypeNames
    {
        private static readonly string[] ClassAccessNames =
            { "Private", "Public", "NPublic", "NPrivate", "NProtected", "NInternal", "NFamAndAssem", "NFamOrAssem" };

        internal readonly Dictionary<TypeDefinition, string> RenamedTypes = new();
        internal readonly Dictionary<(object, string, int), List<TypeDefinition>> RenameGroups = new();
        private readonly Dictionary<TypeDefinition, string> PreviousRenamedTypes = new();

        private InteropTypeNames()
        {
        }

        internal static InteropTypeNames Create(InteropRewriteContext context)
        {
            var names = new InteropTypeNames();
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.OriginalAssembly.ManifestModule.TopLevelTypes)
                names.ProcessType(context, type, false);

            var groupsToRemove = names.RenameGroups.Where(it => it.Value.Count > 1).ToList();
            foreach (var group in groupsToRemove)
            {
                names.RenameGroups.Remove(group.Key);
                foreach (var type in group.Value)
                    names.RenamedTypes.Remove(type);
            }

            // Only first-pass unique names participate in second-pass hash inputs.
            foreach (var renamedType in names.RenamedTypes)
                names.PreviousRenamedTypes[renamedType.Key] = renamedType.Value;

            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.OriginalAssembly.ManifestModule.TopLevelTypes)
                names.ProcessType(context, type, true);

            return names;
        }

        private void ProcessType(InteropRewriteContext context, TypeDefinition originalType,
            bool allowExtraHeuristics)
        {
            foreach (var nestedType in originalType.NestedTypes)
                ProcessType(context, nestedType, allowExtraHeuristics);

            if (RenamedTypes.ContainsKey(originalType)) return;

            var unobfuscatedName = GetUnobfuscatedNameBase(context, originalType, allowExtraHeuristics);
            if (unobfuscatedName == null) return;

            var key = ((object)originalType.DeclaringType ?? originalType.Namespace,
                unobfuscatedName, originalType.GenericParameters.Count);
            if (!RenameGroups.TryGetValue(key, out var group))
            {
                group = new List<TypeDefinition>();
                RenameGroups[key] = group;
            }
            group.Add(originalType);
            RenamedTypes[originalType] = unobfuscatedName;
        }

        private string GetUnobfuscatedNameBase(InteropRewriteContext context, TypeDefinition typeDefinition,
            bool allowExtraHeuristics)
        {
            var options = context.Options;
            if (options.PassthroughNames || !typeDefinition.Name.IsObfuscated(context.Options)) return null;

            var inheritanceDepth = 0;
            var firstUnobfuscatedType = typeDefinition.BaseType;
            while (firstUnobfuscatedType != null && firstUnobfuscatedType.Name.IsObfuscated(context.Options))
            {
                firstUnobfuscatedType = firstUnobfuscatedType.Resolve()?.BaseType?.Resolve();
                inheritanceDepth++;
            }

            var unobfuscatedInterfaces = typeDefinition.Interfaces.Select(it => it.Interface)
                .Where(it => !it.Name.IsObfuscated(context.Options));
            var accessName = ClassAccessNames[(int)(typeDefinition.Attributes & TypeAttributes.VisibilityMask)];
            var classifier = typeDefinition.IsInterface ? "Interface" : typeDefinition.IsValueType() ? "Struct" : "Class";
            var compilerGeneratedString = typeDefinition.Name.StartsWith("<") ? "CompilerGenerated" : "";
            var abstractString = typeDefinition.IsAbstract ? "Abstract" : "";
            var sealedString = typeDefinition.IsSealed ? "Sealed" : "";
            var specialNameString = typeDefinition.IsSpecialName ? "SpecialName" : "";

            var nameBuilder = new StringBuilder();
            nameBuilder.Append(firstUnobfuscatedType == null
                ? classifier
                : string.Concat(GenericNameToStrings(firstUnobfuscatedType.ToTypeSignature(), context)));
            if (inheritanceDepth > 0)
                nameBuilder.Append(inheritanceDepth);
            nameBuilder.Append(compilerGeneratedString);
            nameBuilder.Append(accessName);
            nameBuilder.Append(abstractString);
            nameBuilder.Append(sealedString);
            nameBuilder.Append(specialNameString);
            foreach (var interfaceRef in unobfuscatedInterfaces)
                nameBuilder.Append(string.Concat(GenericNameToStrings(interfaceRef.ToTypeSignature(), context)));

            var uniqContext = new InteropNameUniquifier(options);
            foreach (var field in typeDefinition.Fields)
            {
                if (!typeDefinition.IsEnum)
                    uniqContext.Push(GenericNameToStrings(field.Signature.FieldType, context));
                uniqContext.Push(field.Name);
                if (uniqContext.CheckFull()) break;
            }

            if (typeDefinition.IsEnum)
                uniqContext.Push(typeDefinition.Fields.Count + "v");

            foreach (var property in typeDefinition.Properties)
            {
                uniqContext.Push(GenericNameToStrings(property.Signature.ReturnType, context));
                uniqContext.Push(property.Name);
                if (uniqContext.CheckFull()) break;
            }

            if (firstUnobfuscatedType?.Name == "MulticastDelegate")
            {
                var invokeMethod = typeDefinition.Methods.SingleOrDefault(it => it.Name == "Invoke");
                if (invokeMethod != null)
                {
                    uniqContext.Push(GenericNameToStrings(invokeMethod.Signature.ReturnType, context));
                    foreach (var parameter in invokeMethod.Parameters)
                    {
                        uniqContext.Push(GenericNameToStrings(parameter.ParameterType, context));
                        if (uniqContext.CheckFull()) break;
                    }
                }
            }

            // Method order on non-interface types appears to be unstable.
            if (typeDefinition.IsInterface || allowExtraHeuristics)
                foreach (var method in typeDefinition.Methods)
                {
                    uniqContext.Push(method.Name);
                    uniqContext.Push(GenericNameToStrings(method.Signature.ReturnType, context));
                    foreach (var parameter in method.Parameters)
                    {
                        uniqContext.Push(parameter.Name);
                        uniqContext.Push(GenericNameToStrings(parameter.ParameterType, context));
                        if (uniqContext.CheckFull()) break;
                    }
                    if (uniqContext.CheckFull()) break;
                }

            nameBuilder.Append(uniqContext.GetTop());
            return nameBuilder.ToString();
        }

        private string NameOrRename(TypeSignature typeRef, InteropRewriteContext context)
        {
            var resolved = typeRef.Resolve();
            if (resolved != null && PreviousRenamedTypes.TryGetValue(resolved, out var rename))
                return (rename.StableHash() % (ulong)Math.Pow(10, context.Options.TypeDeobfuscationCharsPerUniquifier))
                    .ToString();

            return typeRef.Name;
        }

        private List<string> GenericNameToStrings(TypeSignature typeRef, InteropRewriteContext context)
        {
            if (typeRef is SzArrayTypeSignature szArrayType)
                return GenericNameToStrings(szArrayType.BaseType, context);
            if (typeRef is ArrayTypeSignature arrayType)
                return GenericNameToStrings(arrayType.BaseType, context);

            if (typeRef is GenericInstanceTypeSignature genericInstance)
            {
                var baseTypeName = NameOrRename(genericInstance.GenericType.ToTypeSignature(), context);
                var indexOfBacktick = baseTypeName.IndexOf('`');
                if (indexOfBacktick >= 0)
                    baseTypeName = baseTypeName.Substring(0, indexOfBacktick);

                var entries = new List<string>();
                entries.Add(baseTypeName);
                entries.Add(genericInstance.TypeArguments.Count.ToString());
                foreach (var genericArgument in genericInstance.TypeArguments)
                    entries.AddRange(GenericNameToStrings(genericArgument, context));
                return entries;
            }

            if (NameOrRename(typeRef, context).IsObfuscated(context.Options))
                return new List<string> { "Obf" };

            return new List<string> { NameOrRename(typeRef, context) };
        }
    }
}
