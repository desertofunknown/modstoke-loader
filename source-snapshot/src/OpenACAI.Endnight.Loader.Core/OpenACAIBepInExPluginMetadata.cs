// SPDX-License-Identifier: GPL-3.0-or-later
// Metadata rules adapted from BepInEx BaseChainloader and attribute helpers.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Mono.Cecil;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIBepInExPluginMetadata
    {
        private static readonly Regex AllowedGuid = new(@"^[a-zA-Z0-9\._\-]+$");

        internal static PluginInfo Read(TypeDefinition type, string assemblyLocation)
        {
            if (type == null || type.IsInterface || type.IsAbstract)
                return null;

            try
            {
                if (!Ancestry(type).Any(ancestor => ancestor.FullName == typeof(BasePlugin).FullName))
                    return null;
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }

            var identity = type.CustomAttributes.FirstOrDefault(attribute =>
                attribute.AttributeType.FullName == typeof(BepInPlugin).FullName);
            if (identity == null)
                return null;

            // Identity is declared-only. Shared constructors preserve version and range parsing.
            var metadata = new BepInPlugin(
                (string)identity.ConstructorArguments[0].Value,
                (string)identity.ConstructorArguments[1].Value,
                (string)identity.ConstructorArguments[2].Value);
            if (string.IsNullOrEmpty(metadata.GUID) || !AllowedGuid.IsMatch(metadata.GUID)
                || metadata.Version == null || metadata.Name == null)
                return null;

            var processes = new List<BepInProcess>();
            var dependencies = new List<BepInDependency>();
            var incompatibilities = new List<BepInIncompatibility>();
            foreach (var ancestor in Ancestry(type))
            {
                foreach (var attribute in ancestor.CustomAttributes)
                {
                    var name = attribute.AttributeType.FullName;
                    if (name == typeof(BepInProcess).FullName)
                        processes.Add(new BepInProcess((string)attribute.ConstructorArguments[0].Value));
                    else if (name == typeof(BepInDependency).FullName)
                    {
                        var guid = (string)attribute.ConstructorArguments[0].Value;
                        var requirement = attribute.ConstructorArguments[1].Value;
                        dependencies.Add(requirement is string version
                            ? new BepInDependency(guid, version)
                            : new BepInDependency(guid, (BepInDependency.DependencyFlags)requirement));
                    }
                    else if (name == typeof(BepInIncompatibility).FullName)
                        incompatibilities.Add(new BepInIncompatibility((string)attribute.ConstructorArguments[0].Value));
                }
            }

            return new PluginInfo
            {
                Metadata = metadata,
                Processes = processes,
                Dependencies = dependencies,
                Incompatibilities = incompatibilities,
                TypeName = type.FullName,
                TargettedBepInExVersion = type.Module.AssemblyReferences
                    .FirstOrDefault(reference => reference.Name == "BepInEx.Core")?.Version ?? new Version(),
                Location = assemblyLocation
            };
        }

        private static IEnumerable<TypeDefinition> Ancestry(TypeDefinition type)
        {
            var visited = new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance);
            while (type != null && type.FullName != "System.Object")
            {
                if (!visited.Add(type))
                    throw new InvalidOperationException("Plugin ancestry contains a cycle at " + type.FullName + ".");
                yield return type;
                type = type.BaseType?.Resolve();
            }
        }
    }
}
