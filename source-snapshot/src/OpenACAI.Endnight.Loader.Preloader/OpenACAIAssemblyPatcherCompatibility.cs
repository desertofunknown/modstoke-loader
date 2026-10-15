// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Preloader.Core.Patching;

namespace OpenACAI.Endnight.Loader.Preloader;

internal sealed class OpenACAIAssemblyPatcherCompatibility : IAssemblyPatcherBackend
{
    private readonly OpenACAIAssemblyPatcher _engine;

    private OpenACAIAssemblyPatcherCompatibility(Func<byte[], string, Assembly> assemblyLoader)
    {
        _engine = new OpenACAIAssemblyPatcher(new AssemblyPatchingState(), assemblyLoader);
    }

    internal static IAssemblyPatcherBackend Create(Func<byte[], string, Assembly> assemblyLoader) =>
        new OpenACAIAssemblyPatcherCompatibility(assemblyLoader);

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
