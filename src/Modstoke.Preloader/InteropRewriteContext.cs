// SPDX-License-Identifier: GPL-3.0-or-later
// Rewrite context adapted from Il2CppInterop RewriteGlobalContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropRewriteContext : IDisposable
    {
        private readonly Dictionary<string, InteropAssemblyContext> assemblies = new();
        private readonly Dictionary<AssemblyDefinition, InteropAssemblyContext> originalAssemblies = new();
        private readonly Dictionary<AssemblyDefinition, InteropAssemblyContext> rewrittenAssemblies = new();
        private readonly Dictionary<ModuleDefinition, InteropRuntimeReferences> imports = new();
        private readonly InteropAssemblyResolver resolver = new();

        internal InteropRewriteContext(InteropGenerationOptions options, InteropAssemblyMetadata gameAssemblies,
            InteropAssemblyMetadata unityAssemblies, InteropScanState scanState)
        {
            Options = options;
            GameAssemblies = gameAssemblies;
            UnityAssemblies = unityAssemblies;
            ScanState = scanState;
            // Generated exception bridges call this loaded assembly; include its real declarations
            // in dependency inspection without treating them as game types to rewrite.
            var supportAssembly = typeof(InteropExceptionSupport).Assembly;
            var framework = supportAssembly.GetCustomAttribute<TargetFrameworkAttribute>()
                ?? throw new InvalidOperationException("The loaded interop support assembly has no target framework.");
            var corlib = typeof(object).Assembly.GetName();
            // The pinned metadata reader cannot infer .NET 11's implementation corlib.
            // Read the loaded support assembly using its actual host identity, not an older target.
            var runtime = new RuntimeContext(DotNetRuntimeInfo.Parse(framework.FrameworkName), resolver,
                new AssemblyReference(corlib.Name, corlib.Version) { PublicKeyOrToken = corlib.GetPublicKeyToken() }, null);
            resolver.Register(AssemblyDefinition.FromFile(supportAssembly.Location, new ModuleReaderParameters(runtime)));

            // Array-carrier and wrapper assignability must use the loaded bridge's real base types.
            // Register metadata only; these declarations are not game assemblies to rewrite.
            var bridgeAssembly = AssemblyDefinition.FromFile(typeof(Il2CppInterop.Runtime.IL2CPP).Assembly.Location,
                new ModuleReaderParameters(runtime));
            foreach (var module in bridgeAssembly.Modules)
                module.MetadataResolver = new DefaultMetadataResolver(resolver);
            resolver.Register(bridgeAssembly);

            foreach (var original in gameAssemblies.Assemblies)
            {
                if (original.Name == "Il2CppDummyDll")
                    continue;

                var target = new AssemblyDefinition(original.Name.UnSystemify(options), original.Version);
                var module = new ModuleDefinition(original.ManifestModule?.Name.UnSystemify(options),
                    KnownCorLibs.SystemRuntime_v6_0_0_0);
                target.Modules.Add(module);
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

        public void Dispose()
        {
            ExceptionDispatchInfo failure = null;
            InteropConversion.Cleanup(ref failure, "clear interop rewrite resolver", resolver.ClearCache);
            InteropConversion.Cleanup(ref failure, "dispose interop Unity metadata", UnityAssemblies.Dispose);
            imports.Clear();
            assemblies.Clear();
            originalAssemblies.Clear();
            rewrittenAssemblies.Clear();
            netstandardForwarders?.Clear();
            netstandardForwarders = null;
            MethodStartAddresses.Clear();
            failure?.Throw();
        }

        internal void AddAssemblyContext(string name, InteropAssemblyContext assembly)
        {
            // Restored assemblies participate in the same graph as the original rewritten assemblies.
            foreach (var module in assembly.NewAssembly.Modules)
                module.MetadataResolver = new DefaultMetadataResolver(resolver);
            resolver.Register(assembly.NewAssembly);
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
