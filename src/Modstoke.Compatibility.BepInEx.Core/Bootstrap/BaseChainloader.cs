// SPDX-License-Identifier: LGPL-2.1-only
// BepInEx chainloader contracts and Modstoke forwarding bindings.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;

namespace BepInEx.Bootstrap;

public abstract class BaseChainloader<TPlugin>
{
    protected static readonly string CurrentAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;
    protected static readonly Version CurrentAssemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;

    private static readonly Lazy<CompatibilityCatalog> Catalog = new(() => new CompatibilityCatalog());

    private sealed class CompatibilityCatalog
    {
        internal readonly Func<TypeDefinition, string, Type, PluginInfo> Read;
        internal readonly Func<AssemblyDefinition, string, bool> HasPlugins;
        internal readonly Func<PluginInfo, Version, bool> TargetsWrongVersion;
        internal readonly Func<IList<PluginInfo>, Dictionary<string, PluginInfo>, List<string>, Version, IList<PluginInfo>> Order;

        internal CompatibilityCatalog()
        {
            var owner = Assembly.Load("Modstoke.Preloader")
                .GetType("Modstoke.Preloader.CompatibilityPluginCatalog", true);
            Read = Bind<Func<TypeDefinition, string, Type, PluginInfo>>(owner, "Read");
            HasPlugins = Bind<Func<AssemblyDefinition, string, bool>>(owner, "HasPlugins");
            TargetsWrongVersion = Bind<Func<PluginInfo, Version, bool>>(owner, "TargetsWrongVersion");
            Order = Bind<Func<IList<PluginInfo>, Dictionary<string, PluginInfo>, List<string>, Version, IList<PluginInfo>>>(owner, "Order");
        }

    }

    /// <summary>
    ///     Analyzes the given type definition and attempts to convert it to a valid <see cref="PluginInfo" />
    /// </summary>
    /// <param name="type">Type definition to analyze.</param>
    /// <param name="assemblyLocation">The filepath of the assembly, to keep as metadata.</param>
    /// <returns>If the type represent a valid plugin, returns a <see cref="PluginInfo" /> instance. Otherwise, return null.</returns>
    public static PluginInfo ToPluginInfo(TypeDefinition type, string assemblyLocation)
    {
        return Catalog.Value.Read(type, assemblyLocation, typeof(TPlugin));
    }

    protected static bool HasBepinPlugins(AssemblyDefinition ass)
    {
        return Catalog.Value.HasPlugins(ass, CurrentAssemblyName);
    }

    protected static bool PluginTargetsWrongBepin(PluginInfo pluginInfo)
    {
        return Catalog.Value.TargetsWrongVersion(pluginInfo, CurrentAssemblyVersion);
    }

    private static readonly Lazy<CompatibilityLifecycle> Lifecycle = new(() => new CompatibilityLifecycle());

    private sealed class CompatibilityLifecycle
    {
        internal readonly Action<string, Action, Action> Initialize;
        internal readonly Action<Func<string>, ConfigEntry<bool>, ConfigEntry<LogLevel>, ConfigEntry<bool>, ConfigEntry<bool>, ConfigEntry<int>> InitializeLoggers;
        internal readonly Action<Func<IList<PluginInfo>>, Action<IList<PluginInfo>>, Action> Execute;

        internal CompatibilityLifecycle()
        {
            var owner = Assembly.Load("Modstoke.Preloader")
                .GetType("Modstoke.Preloader.CompatibilityChainloaderLifecycle", true);
            Initialize = Bind<Action<string, Action, Action>>(owner, "Initialize");
            InitializeLoggers = Bind<Action<Func<string>, ConfigEntry<bool>, ConfigEntry<LogLevel>, ConfigEntry<bool>, ConfigEntry<bool>, ConfigEntry<int>>>(owner, "InitializeLoggers");
            Execute = Bind<Action<Func<IList<PluginInfo>>, Action<IList<PluginInfo>>, Action>>(owner, "Execute");
        }

    }

    private static T Bind<T>(Type owner, string name) where T : Delegate
    {
        var parameters = typeof(T).GetMethod("Invoke").GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        var method = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, null, parameters, null)
            ?? throw new MissingMethodException(owner.FullName, name);
        return (T)method.CreateDelegate(typeof(T));
    }

    #region Contract

    protected virtual string ConsoleTitle => Branding.ConsoleTitle;

    private bool _initialized;

    /// <summary>
    ///     List of all <see cref="PluginInfo" /> instances loaded via the chainloader.
    /// </summary>
    public Dictionary<string, PluginInfo> Plugins { get; } = new();

    /// <summary>
    ///     Collection of error chainloader messages that occured during plugin loading.
    ///     Contains information about what certain plugins were not loaded.
    /// </summary>
    public List<string> DependencyErrors { get; } = new();

    /// <summary>
    ///     Occurs after a plugin is loaded.
    /// </summary>
    public event Action<PluginInfo> PluginLoaded;

    /// <summary>
    ///     Occurs after all plugins are loaded.
    /// </summary>
    public event Action Finished;

    /// <summary>
    ///     Raises the <see cref="PluginLoaded"/> event after a plugin is loaded.
    /// </summary>
    /// <param name="plugin">Information about the loaded plugin.</param>
    protected void OnPluginLoaded(PluginInfo plugin) => PluginLoaded?.Invoke(plugin);

    /// <summary>
    ///     Raises the <see cref="Finished"/> event after all plugins are loaded.
    /// </summary>
    protected void OnFinished() => Finished?.Invoke();

    public virtual void Initialize(string gameExePath = null)
    {
        if (_initialized)
            throw new InvalidOperationException("Chainloader cannot be initialized multiple times");

        Lifecycle.Value.Initialize(gameExePath, InitializeLoggers, () => _initialized = true);
    }

    protected virtual void InitializeLoggers()
    {
        Lifecycle.Value.InitializeLoggers(() => ConsoleTitle, ConfigDiskLogging, ConfigDiskLoggingDisplayedLevel,
                                          ConfigDiskAppend, ConfigDiskLoggingInstantFlushing, ConfigDiskLoggingFileLimit);
    }

    /// <summary>
    /// Discovers all plugins in the plugin directory without loading them.
    /// </summary>
    /// <remarks>
    /// This is useful for discovering BepInEx plugin metadata.
    /// </remarks>
    /// <param name="path">Path from which to search the plugins.</param>
    /// <param name="cacheName">Cache name to use. If null, results are not cached.</param>
    /// <returns>List of discovered plugins and their metadata.</returns>
    protected IList<PluginInfo> DiscoverPluginsFrom(string path, string cacheName = "chainloader")
    {
        var pluginsToLoad =
            TypeLoader.FindPluginTypes(path, ToPluginInfo, HasBepinPlugins, cacheName);
        return pluginsToLoad.SelectMany(p => p.Value).ToList();
    }

    /// <summary>
    /// Discovers plugins to load.
    /// </summary>
    /// <returns>List of plugins to be loaded.</returns>
    protected virtual IList<PluginInfo> DiscoverPlugins()
    {
        return DiscoverPluginsFrom(Paths.PluginPath);
    }

    /// <summary>
    /// Preprocess the plugins and modify the load order.
    /// </summary>
    /// <remarks>Some plugins may be skipped if they cannot be loaded (wrong metadata, etc).</remarks>
    /// <param name="plugins">Plugins to process.</param>
    /// <returns>List of plugins to load in the correct load order.</returns>
    protected virtual IList<PluginInfo> ModifyLoadOrder(IList<PluginInfo> plugins)
    {
        return Catalog.Value.Order(plugins, Plugins, DependencyErrors, CurrentAssemblyVersion);
    }

    /// <summary>
    /// Run the chainloader and load all plugins from the plugins folder.
    /// </summary>
    public virtual void Execute()
    {
        Lifecycle.Value.Execute(DiscoverPlugins, plugins => LoadPlugins(plugins), OnFinished);
    }

    private IList<PluginInfo> LoadPlugins(IList<PluginInfo> plugins)
    {
        var sortedPlugins = ModifyLoadOrder(plugins);
        return CompatibilityPluginBatch.Value(sortedPlugins, Plugins, DependencyErrors,
                                              (plugin, assembly) => LoadPlugin(plugin, assembly), OnPluginLoaded);
    }

    private delegate IList<PluginInfo> LoadCompatibilityPluginBatch(
        IList<PluginInfo> plugins, Dictionary<string, PluginInfo> registry, List<string> dependencyErrors,
        Func<PluginInfo, Assembly, object> activate, Action<PluginInfo> loaded);

    // Bind only when legacy batch loading is requested; discovery does not start the owned runtime.
    private static readonly Lazy<LoadCompatibilityPluginBatch> CompatibilityPluginBatch = new(() =>
    {
        var owner = Assembly.Load("Modstoke.Preloader")
                            .GetType("Modstoke.Preloader.CompatibilityPluginBatch", true);
        return Bind<LoadCompatibilityPluginBatch>(owner, "Load");
    });

    /// <summary>
    /// Detects and loads all plugins in the specified directories.
    /// </summary>
    /// <remarks>
    /// It is better to collect all paths at once and use a single call to LoadPlugins than multiple calls.
    /// This allows to run proper dependency resolving and to load all plugins in one go.
    /// </remarks>
    /// <param name="pluginsPaths">Directories to search the plugins from.</param>
    /// <returns>List of loaded plugin infos.</returns>
    public virtual IList<PluginInfo> LoadPlugins(params string[] pluginsPaths)
    {
        // TODO: This is a temporary solution for 3rd party loaders. Instead, this should be done via metaplugins.
        var plugins = new List<PluginInfo>();
        foreach (var pluginsPath in pluginsPaths)
            plugins.AddRange(DiscoverPluginsFrom(pluginsPath));
        return LoadPlugins(plugins);
    }

    public abstract TPlugin LoadPlugin(PluginInfo pluginInfo, Assembly pluginAssembly);

    #endregion

    #region Config

    private static readonly ConfigEntry<bool> ConfigDiskAppend = ConfigFile.CoreConfig.Bind(
     "Logging.Disk", "AppendLog",
     false,
     "Appends to the log file instead of overwriting, on game startup.");

    private static readonly ConfigEntry<bool> ConfigDiskLogging = ConfigFile.CoreConfig.Bind(
     "Logging.Disk", "Enabled",
     true,
     "Enables writing log messages to disk.");

    private static readonly ConfigEntry<LogLevel> ConfigDiskLoggingDisplayedLevel = ConfigFile.CoreConfig.Bind(
     "Logging.Disk", "LogLevels",
     LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info,
     "Only displays the specified log levels in the disk log output.");

    private static readonly ConfigEntry<bool> ConfigDiskLoggingInstantFlushing = ConfigFile.CoreConfig.Bind(
     "Logging.Disk", "InstantFlushing",
     false,
     new StringBuilder()
         .AppendLine("If true, instantly writes any received log entries to disk.")
         .AppendLine("This incurs a major performance hit if a lot of log messages are being written, however it is really useful for debugging crashes.")
         .ToString());

    private static readonly ConfigEntry<int> ConfigDiskLoggingFileLimit = ConfigFile.CoreConfig.Bind(
     "Logging.Disk", "ConcurrentFileLimit",
     5,
     new StringBuilder()
         .AppendLine("The maximum amount of concurrent log files that will be written to disk.")
         .AppendLine("As one log file is used per open game instance, you may find it necessary to increase this limit when debugging multiple instances at the same time.")
         .ToString());

    #endregion
}
