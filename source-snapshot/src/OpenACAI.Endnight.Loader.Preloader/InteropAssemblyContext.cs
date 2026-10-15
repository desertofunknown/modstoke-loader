// SPDX-License-Identifier: GPL-3.0-or-later
// Assembly context adapted from Il2CppInterop AssemblyRewriteContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using AsmResolver.DotNet;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropAssemblyContext
    {
        private readonly Dictionary<string, InteropTypeContext> typesByName = new();
        private readonly Dictionary<TypeDefinition, InteropTypeContext> originalTypes = new();
        private readonly Dictionary<TypeDefinition, InteropTypeContext> rewrittenTypes = new();

        internal InteropAssemblyContext(InteropRewriteContext context, AssemblyDefinition original, AssemblyDefinition target)
        {
            GlobalContext = context;
            OriginalAssembly = original;
            NewAssembly = target;
            Imports = context.GetImports(target.ManifestModule);
        }

        internal InteropRewriteContext GlobalContext { get; }
        internal AssemblyDefinition OriginalAssembly { get; }
        internal AssemblyDefinition NewAssembly { get; }
        internal InteropRuntimeReferences Imports { get; }
        internal IEnumerable<InteropTypeContext> Types => originalTypes.Values;

        internal void RegisterTypeRewrite(InteropTypeContext type, string sourceFullName = null)
        {
            // Restored types have no original and must stay out of ordinary generation passes.
            if (type.OriginalType != null)
                originalTypes[type.OriginalType] = type;
            rewrittenTypes[type.NewType] = type;
            typesByName[sourceFullName ?? (type.OriginalType ?? type.NewType).FullName] = type;
        }

        internal InteropTypeContext GetContextForOriginalType(TypeDefinition type) => originalTypes[type];
        internal InteropTypeContext GetContextForNewType(TypeDefinition type) => rewrittenTypes[type];
        internal InteropTypeContext GetTypeByName(string name) => typesByName[name];
        internal InteropTypeContext TryGetContextForOriginalType(TypeDefinition type) =>
            originalTypes.TryGetValue(type, out var context) ? context : null;
        internal InteropTypeContext TryGetTypeByName(string name) =>
            typesByName.TryGetValue(name, out var context) ? context : null;
    }
}
