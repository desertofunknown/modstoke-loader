// SPDX-License-Identifier: GPL-3.0-or-later
// Resolver adapted from Il2CppInterop Il2CppAssemblyResolver (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using AsmResolver.DotNet;
using AsmResolver.IO;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropAssemblyResolver : AssemblyResolverBase
    {
        internal InteropAssemblyResolver() : base(new ByteArrayFileService()) { }
        protected override string ProbeRuntimeDirectories(AssemblyDescriptor assembly) => null;
        internal void Register(AssemblyDefinition assembly) => AddToCache(assembly, assembly);
    }
}
