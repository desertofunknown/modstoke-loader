// SPDX-License-Identifier: GPL-3.0-or-later
// Rewrite context adapted from Il2CppInterop RewriteGlobalContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropRewriteContext : IDisposable
    {
        private readonly Dictionary<string, InteropAssemblyContext> assemblies = new();
        private readonly Dictionary<AssemblyDefinition, InteropAssemblyContext> originalAssemblies = new();
        private readonly Dictionary<AssemblyDefinition, InteropAssemblyContext> rewrittenAssemblies = new();
        private readonly Dictionary<ModuleDefinition, InteropRuntimeReferences> imports = new();

        internal InteropRewriteContext(InteropGenerationOptions options, InteropAssemblyMetadata gameAssemblies,
            InteropAssemblyMetadata unityAssemblies, InteropScanState scanState)
        {
            Options = options;
            GameAssemblies = gameAssemblies;
            UnityAssemblies = unityAssemblies;
            ScanState = scanState;

            var resolver = new InteropAssemblyResolver();
            foreach (var original in gameAssemblies.Assemblies)
            {
                if (original.Name == "Il2CppDummyDll")
                    continue;

                var target = new AssemblyDefinition(original.Name.UnSystemify(options), original.Version);
                var module = new ModuleDefinition(original.ManifestModule?.Name.UnSystemify(options),
                    KnownCorLibs.SystemRuntime_v6_0_0_0);
                target.Modules.Add(module);
                module.MetadataResolver = new DefaultMetadataResolver(resolver);
                resolver.Register(target);
                AddAssemblyContext(original.Name, new InteropAssemblyContext(this, original, target));
            }
        }

        internal InteropGenerationOptions Options { get; }
        internal InteropAssemblyMetadata GameAssemblies { get; }
        internal InteropAssemblyMetadata UnityAssemblies { get; }
        internal InteropScanState ScanState { get; }
        internal List<long> MethodStartAddresses { get; } = new();
        internal bool HasGcWbarrierFieldWrite { get; set; }
        internal IEnumerable<InteropAssemblyContext> Assemblies => assemblies.Values;
        internal InteropAssemblyContext CorLib => assemblies["mscorlib"];

        public void Dispose() => UnityAssemblies.Dispose();

        internal void AddAssemblyContext(string name, InteropAssemblyContext assembly)
        {
            assemblies[name] = assembly;
            if (assembly.OriginalAssembly != null)
                originalAssemblies[assembly.OriginalAssembly] = assembly;
            rewrittenAssemblies[assembly.NewAssembly] = assembly;
        }

        internal InteropRuntimeReferences GetImports(ModuleDefinition module)
        {
            if (!imports.TryGetValue(module, out var references))
            {
                references = new InteropRuntimeReferences(module, this);
                imports.Add(module, references);
            }
            return references;
        }

        internal InteropAssemblyContext GetAssemblyByName(string name) => assemblies[name];

        internal InteropAssemblyContext TryGetAssemblyByName(string name)
        {
            if (name == null)
                return null;
            if (assemblies.TryGetValue(name, out var assembly))
                return assembly;
            return name == "netstandard" && assemblies.TryGetValue("mscorlib", out assembly) ? assembly : null;
        }

        internal InteropAssemblyContext GetNewAssemblyForOriginal(AssemblyDefinition assembly) => originalAssemblies[assembly];
        internal InteropAssemblyContext GetContextForNewAssembly(AssemblyDefinition assembly) => rewrittenAssemblies[assembly];

        internal InteropTypeContext GetNewTypeForOriginal(TypeDefinition type) =>
            originalAssemblies[type.DeclaringModule.Assembly].GetContextForOriginalType(type);

        internal InteropTypeContext TryGetNewTypeForOriginal(TypeDefinition type) =>
            originalAssemblies.TryGetValue(type.DeclaringModule.Assembly, out var assembly)
                ? assembly.TryGetContextForOriginalType(type) : null;

        internal InteropTypeContext GetContextForNewType(TypeDefinition type) =>
            rewrittenAssemblies[type.DeclaringModule.Assembly].GetContextForNewType(type);

    }
}
