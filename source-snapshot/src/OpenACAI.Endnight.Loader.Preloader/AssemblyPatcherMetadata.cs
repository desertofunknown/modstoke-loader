// SPDX-License-Identifier: GPL-3.0-or-later
// Metadata rules adapted from BepInEx Utility and patcher attribute helpers.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using BepInEx.Preloader.Core.Patching;
using Mono.Cecil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class AssemblyPatcherMetadata
    {
        internal static bool IsPatcher(TypeDefinition type)
        {
            var visited = new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance);
            while (type != null)
            {
                if (type.FullName == typeof(BasePatcher).FullName)
                    return true;
                if (type.FullName == "System.Object")
                    return false;
                if (!visited.Add(type))
                    throw new InvalidOperationException("Patcher ancestry contains a cycle at " + type.FullName + ".");

                type = type.BaseType?.Resolve();
            }
            return false;
        }

        internal static PatcherPluginInfoAttribute ReadInfo(TypeDefinition type)
        {
            foreach (var attribute in type.CustomAttributes)
            {
                if (attribute.AttributeType.FullName != typeof(PatcherPluginInfoAttribute).FullName)
                    continue;

                // Identity metadata is declared-only; the first matching attribute wins.
                // The shared constructor preserves semantic/long-version parsing rules.
                return new PatcherPluginInfoAttribute(
                    (string)attribute.ConstructorArguments[0].Value,
                    (string)attribute.ConstructorArguments[1].Value,
                    (string)attribute.ConstructorArguments[2].Value);
            }
            return null;
        }

        internal static PatcherPluginInfoAttribute ReadInfo(Type type)
        {
            foreach (var attribute in type.CustomAttributes)
            {
                if (attribute.AttributeType != typeof(PatcherPluginInfoAttribute))
                    continue;

                // Inspect declared arguments without invoking a candidate's attribute constructors.
                return new PatcherPluginInfoAttribute(
                    (string)attribute.ConstructorArguments[0].Value,
                    (string)attribute.ConstructorArguments[1].Value,
                    (string)attribute.ConstructorArguments[2].Value);
            }
            return null;
        }
    }
}
