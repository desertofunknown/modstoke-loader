// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;

namespace BepInEx.Preloader.Core.Patching;

/// <summary>Connects a patch method and its plugin instance to an assembly or type target.</summary>
public class PatchDefinition
{
    /// <summary>Describes a method that patches an assembly.</summary>
    /// <param name="targetAssembly">The assembly selection declared by the method.</param>
    /// <param name="instance">The plugin instance that owns the method.</param>
    /// <param name="methodInfo">The patch method.</param>
    public PatchDefinition(TargetAssemblyAttribute targetAssembly, BasePatcher instance, MethodInfo methodInfo)
    {
        TargetAssembly = targetAssembly;
        Instance = instance;
        MethodInfo = methodInfo;
        FullName = $"{methodInfo.DeclaringType.FullName}/{methodInfo.Name} -> {targetAssembly.TargetAssembly}";
    }

    /// <summary>Describes a method that patches a type.</summary>
    /// <param name="targetType">The type selection declared by the method.</param>
    /// <param name="instance">The plugin instance that owns the method.</param>
    /// <param name="methodInfo">The patch method.</param>
    public PatchDefinition(TargetTypeAttribute targetType, BasePatcher instance, MethodInfo methodInfo)
    {
        TargetType = targetType;
        Instance = instance;
        MethodInfo = methodInfo;
        FullName = $"{methodInfo.DeclaringType.FullName}/{methodInfo.Name} -> {targetType.TargetAssembly}/{targetType.TargetType}";
    }

    /// <summary>Gets the assembly selection, or null for a type patch.</summary>
    public TargetAssemblyAttribute TargetAssembly { get; }

    /// <summary>Gets the type selection, or null for an assembly patch.</summary>
    public TargetTypeAttribute TargetType { get; }

    /// <summary>Gets the plugin instance that owns this patch.</summary>
    public BasePatcher Instance { get; }

    /// <summary>Gets the method that implements this patch.</summary>
    public MethodInfo MethodInfo { get; }

    /// <summary>Gets the diagnostic name combining the declaring type, method, and target.</summary>
    public string FullName { get; }
}

/// <summary>Holds the mutable assembly and plugin collections shared with the patcher engine.</summary>
public class PatcherContext
{
    /// <summary>Gets assemblies awaiting patching and loading, keyed by unique, deterministic filenames.</summary>
    /// <remarks>Keys need not match the original filenames; original paths are stored separately.</remarks>
    public Dictionary<string, AssemblyDefinition> AvailableAssemblies { get; } = new();

    /// <summary>Gets original file paths indexed by the keys in <see cref="AvailableAssemblies"/>.</summary>
    public Dictionary<string, string> AvailableAssembliesPaths { get; } = new();

    /// <summary>Gets loaded runtime assemblies indexed by their available-assembly keys.</summary>
    public Dictionary<string, Assembly> LoadedAssemblies { get; } = new();

    /// <summary>Gets the plugins in their initialization and execution order.</summary>
    public List<BasePatcher> PatcherPlugins { get; } = new();

    /// <summary>Gets the patches discovered from plugins for execution by <see cref="AssemblyPatcher"/>.</summary>
    public List<PatchDefinition> PatchDefinitions { get; } = new();

    /// <summary>Gets the diagnostic assembly output directory assigned by the engine.</summary>
    /// <remarks>Initially null; the engine normally uses BepInEx/DumpedAssemblies followed by the process name.</remarks>
    public string DumpedAssembliesPath { get; internal set; }
}
