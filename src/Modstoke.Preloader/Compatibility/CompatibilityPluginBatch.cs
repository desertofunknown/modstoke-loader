// SPDX-License-Identifier: GPL-3.0-or-later
// Plugin activation behavior adapted from BepInEx's BaseChainloader.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace Modstoke.Preloader;

internal static class CompatibilityPluginBatch
{
    internal static IList<PluginInfo> Load(
        IList<PluginInfo> plugins,
        Dictionary<string, PluginInfo> registry,
        List<string> dependencyErrors,
        Func<PluginInfo, Assembly, object> activate,
        Action<PluginInfo> loaded)
    {
        var attemptedIds = new HashSet<string>();
        var invalidIds = new HashSet<string>();
        var assembliesByPath = new Dictionary<string, Assembly>();
        var activatedPlugins = new List<PluginInfo>();

        foreach (var plugin in plugins)
        {
            if (!attemptedIds.Add(plugin.Metadata.GUID) || registry.ContainsKey(plugin.Metadata.GUID))
            {
                Log(LogLevel.Warning, () => $"Skipping [{plugin}] because its GUID is already registered or was already attempted in this batch.");
                continue;
            }

            List<BepInDependency> missingDependencies = null;
            var hasInvalidDependency = false;
            foreach (var dependency in plugin.Dependencies)
            {
                if (!IsHardDependency(dependency))
                    continue;

                var found = registry.TryGetValue(dependency.DependencyGUID, out var registeredPlugin)
                    && registeredPlugin.Instance != null;
                if (!found && invalidIds.Contains(dependency.DependencyGUID))
                {
                    hasInvalidDependency = true;
                    break;
                }

                if (!found
                    || dependency.VersionRange != null && !dependency.VersionRange.IsSatisfied(registeredPlugin.Metadata.Version))
                {
                    missingDependencies ??= new List<BepInDependency>();
                    missingDependencies.Add(dependency);
                }
            }

            if (hasInvalidDependency)
            {
                var message = $"Skipping [{plugin}] because it has a dependency that was not loaded. See previous errors for details.";
                dependencyErrors.Add(message);
                Log(LogLevel.Warning, () => message);
                invalidIds.Add(plugin.Metadata.GUID);
                continue;
            }

            if (missingDependencies != null)
            {
                var message = $"Could not load [{plugin}] because it has missing dependencies: {string.Join(", ",
                    missingDependencies.Select(dependency => dependency.VersionRange == null
                        ? dependency.DependencyGUID
                        : $"{dependency.DependencyGUID} ({dependency.VersionRange})"))}";
                dependencyErrors.Add(message);
                Log(LogLevel.Error, () => message);
                invalidIds.Add(plugin.Metadata.GUID);
                continue;
            }

            try
            {
                Log(LogLevel.Info, () => $"Loading [{plugin}]");
                if (!assembliesByPath.TryGetValue(plugin.Location, out var assembly))
                    assembliesByPath[plugin.Location] = assembly = Assembly.LoadFrom(plugin.Location);

                // Assembly resolution can reenter loading after the initial admission check.
                if (!registry.TryAdd(plugin.Metadata.GUID, plugin))
                    continue;
                TryRunModuleConstructor(plugin, assembly);
                plugin.Instance = activate(plugin, assembly)
                    ?? throw new InvalidOperationException("Plugin activation returned no instance.");
                activatedPlugins.Add(plugin);
            }
            catch (Exception error)
            {
                invalidIds.Add(plugin.Metadata.GUID);
                if (registry.TryGetValue(plugin.Metadata.GUID, out var registeredPlugin)
                    && ReferenceEquals(registeredPlugin, plugin))
                    registry.Remove(plugin.Metadata.GUID);
                plugin.Instance = null;
                Log(LogLevel.Error,
                    () => $"Error loading [{plugin}]: {(error is ReflectionTypeLoadException typeLoadError ? TypeLoader.TypeLoadExceptionToString(typeLoadError) : error.ToString())}");
                continue;
            }

            // Notification observers do not own the successfully initialized plugin's lifetime.
            try { loaded(plugin); }
            catch (Exception error)
            {
                Log(LogLevel.Error, () => $"Error notifying observers that [{plugin}] loaded: {error}");
            }
        }

        return activatedPlugins;
    }

    private static bool IsHardDependency(BepInDependency dependency) =>
        (dependency.Flags & BepInDependency.DependencyFlags.HardDependency) != 0;

    private static void TryRunModuleConstructor(PluginInfo plugin, Assembly assembly)
    {
        try
        {
            RuntimeHelpers.RunModuleConstructor(assembly.GetType(plugin.TypeName).Module.ModuleHandle);
        }
        catch (Exception error)
        {
            Log(LogLevel.Warning,
                () => $"Couldn't run Module constructor for {assembly.FullName}::{plugin.TypeName}: {error}");
        }
    }

    private static void Log(LogLevel level, Func<string> message)
    {
        try { Logger.Log(level, message()); }
        catch { }
    }
}
