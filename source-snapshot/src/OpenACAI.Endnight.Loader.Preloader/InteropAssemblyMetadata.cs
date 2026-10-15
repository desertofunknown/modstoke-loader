// SPDX-License-Identifier: GPL-3.0-or-later
// Metadata loading adapted from Il2CppInterop AssemblyMetadataAccess (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using AsmResolver.DotNet;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropAssemblyMetadata : IDisposable
    {
        private readonly InteropAssemblyResolver resolver = new();
        private readonly List<AssemblyDefinition> assemblies = new();

        internal InteropAssemblyMetadata(IEnumerable<string> paths)
            : this(paths.Select(AssemblyDefinition.FromFile)) { }

        internal InteropAssemblyMetadata(IEnumerable<AssemblyDefinition> source)
        {
            try
            {
                foreach (var assembly in source)
                {
                    assemblies.Add(assembly);
                    assembly.ManifestModule.MetadataResolver = new DefaultMetadataResolver(resolver);
                    resolver.Register(assembly);
                }
            }
            catch (Exception error)
            {
                var failure = ExceptionDispatchInfo.Capture(error);
                InteropConversion.Cleanup(ref failure, "dispose partially loaded assembly metadata", Dispose);
                failure.Throw();
            }
        }

        internal IReadOnlyList<AssemblyDefinition> Assemblies => assemblies;

        public void Dispose()
        {
            ExceptionDispatchInfo failure = null;
            InteropConversion.Cleanup(ref failure, "clear assembly metadata resolver", resolver.ClearCache);
            assemblies.Clear();
            failure?.Throw();
        }
    }
}
