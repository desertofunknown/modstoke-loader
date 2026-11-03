// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Reflection;
using RedLoader.Bootstrap;
using RedLoader.Utils;
using Mono.Cecil;

namespace RedLoader.Preloader.Core.Patching;

/// <summary>Hosts RedLoader patcher plugins through Modstoke Preloader.</summary>
public class AssemblyPatcher : IDisposable
{
    private static readonly Lazy<AssemblyPatcherFactory> Factory = new(BindFactory);
    private readonly IAssemblyPatcherBackend _backend;

    /// <summary>Creates a patching session with the caller's assembly loader.</summary>
    /// <param name="assemblyLoader">Receives patched bytes and the original assembly path.</param>
    public AssemblyPatcher(Func<byte[], string, Assembly> assemblyLoader)
    {
        _backend = Factory.Value(PatcherContext, assemblyLoader,
            ConfigDumpAssemblies, ConfigLoadDumpedAssemblies, ConfigBreakBeforeLoadAssemblies);
    }

    /// <summary>Gets the mutable context shared with this session's patchers.</summary>
    public PatcherContext PatcherContext { get; } = new()
    {
        DumpedAssembliesPath = Utility.CombinePaths(LoaderEnvironment.LoaderDirectory, "DumpedAssemblies", LoaderEnvironment.GameExecutableName)
    };

    /// <summary>Discovers patcher plugins in the supplied directory.</summary>
    public void AddPatchersFromDirectory(string directory) => _backend.AddPatchersFromDirectory(directory);

    /// <summary>Reads DLLs from the supplied directories for patching.</summary>
    public void LoadAssemblyDirectories(params string[] directories) =>
        LoadAssemblyDirectories(directories, new[] { "dll" });

    /// <summary>Reads assemblies with the supplied extensions without loading them into the runtime.</summary>
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

    /// <summary>Runs patcher callbacks and loads patched assemblies.</summary>
    public void PatchAndLoad() => _backend.PatchAndLoad();

    /// <summary>Releases this session's assembly metadata and patcher references.</summary>
    public void Dispose() => _backend.Dispose();

    private static AssemblyPatcherFactory BindFactory()
    {
        var owner = Assembly.Load("Modstoke.Preloader")
            .GetType("Modstoke.Preloader.RedAssemblyPatcher", true);
        var create = owner.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, new[]
            {
                typeof(PatcherContext), typeof(Func<byte[], string, Assembly>),
                typeof(ConfigEntry<bool>), typeof(ConfigEntry<bool>), typeof(ConfigEntry<bool>)
            }, null) ?? throw new MissingMethodException(owner.FullName, "Create");
        return create.CreateDelegate<AssemblyPatcherFactory>();
    }

    #region Config

    private static readonly ConfigEntry<bool> ConfigDumpAssemblies = CorePreferences.CoreCategory.CreateEntry(
     "dump_assemblies", false, "Dump Assemblies",
     "If enabled, Redloader will save patched assemblies into Redloader/DumpedAssemblies.\nThis can be used by developers to inspect and debug preloader patchers.");

    private static readonly ConfigEntry<bool> ConfigLoadDumpedAssemblies = CorePreferences.CoreCategory.CreateEntry(
     "load_dumped_assemblies", false, "Load Dumped Assemblies",
     "If enabled, Redloader will load patched assemblies from Redloader/DumpedAssemblies instead of memory.\nThis can be used to be able to load patched assemblies into debuggers like dnSpy.\nIf set to true, will override DumpAssemblies.");

    private static readonly ConfigEntry<bool> ConfigBreakBeforeLoadAssemblies = CorePreferences.CoreCategory.CreateEntry(
     "break_before_load_assemblies", false, "Break Before Load Assemblies",
     "If enabled, Redloader will call Debugger.Break() once before loading patched assemblies.\nThis can be used with debuggers like dnSpy to install breakpoints into patched assemblies before they are loaded.");

    #endregion
}

internal interface IAssemblyPatcherBackend : IDisposable
{
    void AddPatchersFromDirectory(string directory);
    void LoadAssemblyDirectories(IEnumerable<string> directories, IEnumerable<string> assemblyExtensions);
    void PatchAndLoad();
}

internal delegate IAssemblyPatcherBackend AssemblyPatcherFactory(
    PatcherContext context, Func<byte[], string, Assembly> assemblyLoader,
    ConfigEntry<bool> dumpAssemblies, ConfigEntry<bool> loadDumpedAssemblies,
    ConfigEntry<bool> breakBeforeLoadAssemblies);
