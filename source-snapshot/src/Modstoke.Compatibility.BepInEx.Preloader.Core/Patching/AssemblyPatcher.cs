// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using Mono.Cecil;

namespace BepInEx.Preloader.Core.Patching;

/// <summary>Hosts legacy patcher plugins through the Modstoke patching engine.</summary>
public class AssemblyPatcher : IDisposable
{
    private static readonly Lazy<Func<Func<byte[], string, Assembly>, IAssemblyPatcherBackend>> Factory = new(BindFactory);
    private readonly IAssemblyPatcherBackend _backend;

    /// <summary>Creates a patching session with the caller's in-memory assembly loader.</summary>
    /// <param name="assemblyLoader">Receives patched bytes and the original assembly path.</param>
    public AssemblyPatcher(Func<byte[], string, Assembly> assemblyLoader)
    {
        ArgumentNullException.ThrowIfNull(assemblyLoader);
        _backend = Factory.Value(assemblyLoader);
    }

    /// <summary>Gets the mutable context shared with this session's patchers.</summary>
    public PatcherContext PatcherContext => _backend.Context;

    /// <summary>Discovers patcher plugins in the supplied directory.</summary>
    public void AddPatchersFromDirectory(string directory) => _backend.AddPatchersFromDirectory(directory);

    /// <summary>Reads DLLs from the supplied directories for patching.</summary>
    public void LoadAssemblyDirectories(params string[] directories) => LoadAssemblyDirectories(directories, new[] { "dll" });

    /// <summary>Reads assemblies with the supplied file extensions, without loading them into the runtime.</summary>
    public void LoadAssemblyDirectories(IEnumerable<string> directories, IEnumerable<string> assemblyExtensions) =>
        _backend.LoadAssemblyDirectories(directories, assemblyExtensions);

    /// <summary>Reads managed assembly metadata; non-managed files return false.</summary>
    /// <param name="path">The assembly file to read.</param>
    /// <param name="assembly">The definition owned by the caller on success; otherwise null.</param>
    public static bool TryLoadAssembly(string path, out AssemblyDefinition assembly)
    {
        try
        {
            assembly = AssemblyDefinition.ReadAssembly(path, TypeLoader.ReaderParameters);
            return true;
        }
        catch (BadImageFormatException)
        {
            assembly = null;
            return false;
        }
    }

    /// <summary>Runs patcher lifecycle methods and loads successfully patched assemblies.</summary>
    public void PatchAndLoad() => _backend.PatchAndLoad();

    /// <summary>Releases metadata and patcher resources owned by this session.</summary>
    public void Dispose() => _backend.Dispose();

    private static Func<Func<byte[], string, Assembly>, IAssemblyPatcherBackend> BindFactory()
    {
        // Defer the engine dependency so patcher contract types can load before the owned preloader.
        var assembly = Assembly.Load("OpenACAI.Endnight.Loader.Preloader");
        var type = assembly.GetType("OpenACAI.Endnight.Loader.Preloader.OpenACAIAssemblyPatcherCompatibility", throwOnError: true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(Func<byte[], string, Assembly>) }, null)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return method.CreateDelegate<Func<Func<byte[], string, Assembly>, IAssemblyPatcherBackend>>();
    }
}

internal interface IAssemblyPatcherBackend : IDisposable
{
    PatcherContext Context { get; }
    void AddPatchersFromDirectory(string directory);
    void LoadAssemblyDirectories(IEnumerable<string> directories, IEnumerable<string> assemblyExtensions);
    void PatchAndLoad();
}
