// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility search roots follow BepInEx TypeLoader's Cecil resolver.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class AssemblyPatcherResolver : BaseAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.Ordinal);
        private readonly ISet<AssemblyDefinition> _ownedAssemblies;
        private readonly HashSet<AssemblyDefinition> _borrowed = new(ReferenceEqualityComparer.Instance);
        private bool _disposed;

        internal AssemblyPatcherResolver(ISet<AssemblyDefinition> ownedAssemblies)
        {
            _ownedAssemblies = ownedAssemblies;
            ReaderParameters = new ReaderParameters { AssemblyResolver = this };
            ResolveFailure += ResolveCompatibilityAssembly;
        }

        internal ReaderParameters ReaderParameters { get; }

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(name);
            if (_cache.TryGetValue(name.FullName, out var assembly))
                return assembly;

            assembly = Resolve(name, new ReaderParameters { AssemblyResolver = this });
            _cache.Add(name.FullName, assembly);
            return assembly;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var assembly = base.Resolve(name, parameters);
            if (!_borrowed.Contains(assembly))
                _ownedAssemblies.Add(assembly);
            return assembly;
        }

        private AssemblyDefinition ResolveCompatibilityAssembly(object sender, AssemblyNameReference reference)
        {
            AssemblyName name;
            try { name = new AssemblyName(reference.FullName); }
            catch (Exception) { return null; }

            var directories = new[]
            {
                Paths.BepInExAssemblyDirectory,
                Paths.PluginPath,
                Paths.PatcherPluginPath,
                Paths.ManagedPath
            }.Concat(TypeLoader.SearchDirectories);

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    AssemblyPatcherDiagnostics.Write(() =>
                        OpenACAIPreloaderLog.Log.LogDebug($"Unable to resolve Cecil search directory '{directory}'"));
                    continue;
                }

                var assembly = AssemblySearch.FindAndLoad(name, directory,
                    path => AssemblyDefinition.ReadAssembly(path, ReaderParameters));
                if (assembly != null)
                    return assembly;
            }

            var supplied = TypeLoader.InvokeAssemblyResolve(sender, reference);
            if (supplied != null)
                _borrowed.Add(supplied);
            return supplied;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing || _disposed)
                return;

            _disposed = true;
            ResolveFailure -= ResolveCompatibilityAssembly;
            // The engine releases roots and resolved dependencies from one identity-deduplicated set.
            _borrowed.Clear();
            _cache.Clear();
            base.Dispose(disposing);
        }
    }
}
