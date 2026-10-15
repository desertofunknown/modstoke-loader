// SPDX-License-Identifier: LGPL-2.1-only
// Compatibility contract adapted from BepInEx's IL2CPPChainloader.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.InteropTypes;

namespace BepInEx.Unity.IL2CPP;

public class IL2CPPChainloader : BaseChainloader<BasePlugin>
{
    private static readonly object InitializationSync = new();
    private static readonly Lazy<Func<IL2CPPChainloader, ICompatibilityChainloader>> Factory = new(() =>
    {
        var backend = Assembly.Load("OpenACAI.Endnight.Loader.Preloader")
                              .GetType("OpenACAI.Endnight.Loader.Preloader.OpenACAICompatibilityChainloader", true);
        var create = backend.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static,
                                       null, new[] { typeof(IL2CPPChainloader) }, null)
                     ?? throw new MissingMethodException(backend.FullName, "Create");
        return (Func<IL2CPPChainloader, ICompatibilityChainloader>)create.CreateDelegate(
            typeof(Func<IL2CPPChainloader, ICompatibilityChainloader>));
    });

    private ICompatibilityChainloader _backend;
    private ICompatibilityChainloader Backend => _backend ??= Factory.Value(this);

    public static IL2CPPChainloader Instance { get; set; }

    /// <summary>Registers and adds a Unity component to the global manager.</summary>
    public static T AddUnityComponent<T>() where T : Il2CppObjectBase => AddUnityComponent(typeof(T)).Cast<T>();

    /// <summary>Registers and adds a Unity component to the global manager.</summary>
    public static Il2CppObjectBase AddUnityComponent(Type t) => Il2CppUtils.AddComponent(t);

    /// <summary>Occurs after construction and immediately before the plugin's Load method.</summary>
    public event Action<PluginInfo, Assembly, BasePlugin> PluginLoad;

    protected void OnPluginLoad(PluginInfo pluginInfo, Assembly pluginAssembly, BasePlugin plugin) =>
        PluginLoad?.Invoke(pluginInfo, pluginAssembly, plugin);

    public override void Initialize(string gameExePath = null)
    {
        lock (InitializationSync)
        {
            if (Instance != null && !ReferenceEquals(Instance, this))
                throw new InvalidOperationException("Another IL2CPPChainloader instance already owns startup. " +
                    "Use IL2CPPChainloader.Instance instead of initializing a second chainloader.");

            base.Initialize(gameExePath);
            Instance = this;
            Backend.Arm();
        }
    }

    protected override void InitializeLoggers()
    {
        base.InitializeLoggers();
        Backend.InitializeLoggers();
    }

    public override BasePlugin LoadPlugin(PluginInfo pluginInfo, Assembly pluginAssembly)
    {
        var type = pluginAssembly.GetType(pluginInfo.TypeName);
        var plugin = (BasePlugin)Activator.CreateInstance(type);
        OnPluginLoad(pluginInfo, pluginAssembly, plugin);
        plugin.Load();
        return plugin;
    }
}

internal interface ICompatibilityChainloader
{
    void Arm();
    void InitializeLoggers();
}
