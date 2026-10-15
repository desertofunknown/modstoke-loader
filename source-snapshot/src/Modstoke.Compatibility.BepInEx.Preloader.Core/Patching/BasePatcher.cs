// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using BepInEx.Configuration;
using BepInEx.Logging;

namespace BepInEx.Preloader.Core.Patching;

/// <summary>Provides metadata, logging, configuration, and lifecycle hooks for patcher plugins.</summary>
public abstract class BasePatcher
{
    /// <summary>Reads this concrete type's metadata and creates its log source and configuration.</summary>
    protected BasePatcher()
    {
        Info = PatcherPluginInfoAttribute.FromType(GetType());
        Log = Logger.CreateLogSource(Info.Name);

        var configPath = Utility.CombinePaths(Paths.ConfigPath, Info.GUID + ".cfg");
        var pluginIdentity = new BepInPlugin(Info.GUID, Info.Name, Info.Version.ToString());
        Config = new ConfigFile(configPath, false, pluginIdentity);
    }

    /// <summary>Gets the log source registered under the patcher's display name.</summary>
    public ManualLogSource Log { get; }

    /// <summary>Gets the configuration at the patcher's GUID-named file in the configuration directory.</summary>
    public ConfigFile Config { get; }

    /// <summary>Gets the metadata declared on the concrete patcher type.</summary>
    public PatcherPluginInfoAttribute Info { get; }

    /// <summary>Gets or sets the context supplied by the associated <see cref="AssemblyPatcher"/>.</summary>
    public PatcherContext Context { get; set; }

    /// <summary>Runs before patches are applied; the default implementation does nothing.</summary>
    public virtual void Initialize() { }

    /// <summary>Runs after all patches are applied; the default implementation does nothing.</summary>
    public virtual void Finalizer() { }
}
