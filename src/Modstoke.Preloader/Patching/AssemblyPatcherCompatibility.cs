// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Preloader.Core.Patching;

namespace Modstoke.Preloader;

internal sealed class AssemblyPatcherCompatibility : IAssemblyPatcherBackend
{
    private readonly AssemblyPatcher _engine;

    private AssemblyPatcherCompatibility(Func<byte[], string, Assembly> assemblyLoader)
    {
        _engine = new AssemblyPatcher(new AssemblyPatchingState(), assemblyLoader);
    }

    // Bound by name from BepInEx's AssemblyPatcher compatibility facade.
    internal static IAssemblyPatcherBackend Create(Func<byte[], string, Assembly> assemblyLoader) =>
        new AssemblyPatcherCompatibility(assemblyLoader);

    public PatcherContext Context => _engine.Context;

    public void AddPatchersFromDirectory(string directory) => _engine.DiscoverPatchers(directory);

    public void LoadAssemblyDirectories(IEnumerable<string> directories, IEnumerable<string> assemblyExtensions)
    {
        var roots = directories.ToArray();
        var files = assemblyExtensions.SelectMany(extension => Utility.GetUniqueFilesInDirectories(roots, "*." + extension));
        _engine.ReadAssemblyFiles(files);
    }

    public void PatchAndLoad() => _engine.PatchAndLoad();

    public void Dispose() => _engine.Dispose();
}
