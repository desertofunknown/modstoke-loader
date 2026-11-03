// SPDX-License-Identifier: GPL-3.0-or-later
// Plugin metadata, admission, and ordering behavior adapted from BepInEx's BaseChainloader.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace Modstoke.Preloader;

internal static class CompatibilityPluginCatalog
{
    private static Regex AllowedGuidRegex { get; } = new(@"\A[a-zA-Z0-9\._\-]+\z");

    internal static PluginInfo Read(TypeDefinition type, string assemblyLocation, Type pluginType)
    {
        if (type.IsInterface || type.IsAbstract || type.HasGenericParameters)
            return null;

        try
        {
            if (!type.IsSubtypeOf(pluginType))
                return null;
        }
        catch (AssemblyResolutionException)
        {
            // An unresolved ancestor excludes this type without suppressing metadata errors.
            return null;
        }

        var metadata = BepInPlugin.FromCecilType(type);
        if (metadata == null)
        {
            Logger.Log(LogLevel.Warning, $"Skipping over type [{type.FullName}] as no metadata attribute is specified");
            return null;
        }

        if (string.IsNullOrEmpty(metadata.GUID) || !AllowedGuidRegex.IsMatch(metadata.GUID))
        {
            Logger.Log(LogLevel.Warning,
                $"Skipping type [{type.FullName}] because its GUID [{metadata.GUID}] is of an illegal format.");
            return null;
        }

        if (metadata.Version == null)
        {
            Logger.Log(LogLevel.Warning, $"Skipping type [{type.FullName}] because its version is invalid.");
            return null;
        }

        if (metadata.Name == null)
        {
            Logger.Log(LogLevel.Warning, $"Skipping type [{type.FullName}] because its name is null.");
            return null;
        }

        var filters = BepInProcess.FromCecilType(type);
        var dependencies = BepInDependency.FromCecilType(type);
        var incompatibilities = BepInIncompatibility.FromCecilType(type);
        var bepinVersion =
            type.Module.AssemblyReferences.FirstOrDefault(reference => reference.Name == "BepInEx.Core")?.Version ??
            new Version();

        return new PluginInfo
        {
            Metadata = metadata,
            Processes = filters,
            Dependencies = dependencies,
            Incompatibilities = incompatibilities,
            TypeName = type.FullName,
            TargettedBepInExVersion = bepinVersion,
            Location = assemblyLocation
        };
    }

    internal static bool HasPlugins(AssemblyDefinition assembly, string coreAssemblyName)
    {
        if (assembly.MainModule.AssemblyReferences.All(reference => reference.Name != coreAssemblyName))
            return false;
        if (assembly.MainModule.GetTypeReferences().All(reference => reference.FullName != typeof(BepInPlugin).FullName))
            return false;

        return true;
    }

    internal static bool TargetsWrongVersion(PluginInfo plugin, Version currentVersion)
    {
        var pluginTarget = plugin.TargettedBepInExVersion;
        // Revision is the nightly build number and does not participate in compatibility.
        if (pluginTarget.Major != currentVersion.Major) return true;
        if (pluginTarget.Minor > currentVersion.Minor) return true;
        if (pluginTarget.Minor < currentVersion.Minor) return false;
        return pluginTarget.Build > currentVersion.Build;
    }

    internal static IList<PluginInfo> Order(
        IList<PluginInfo> plugins,
        Dictionary<string, PluginInfo> registry,
        List<string> dependencyErrors,
        Version currentVersion)
    {
        // Graph identity must match the case-sensitive registry and GUID grouping.
        var dependencyDict =
            new SortedDictionary<string, IEnumerable<string>>(StringComparer.Ordinal);
        var pluginsByGuid = new Dictionary<string, PluginInfo>();

        foreach (var pluginInfoGroup in plugins.GroupBy(info => info.Metadata.GUID))
        {
            // Cached metadata must pass the same identity admission as a fresh Cecil read.
            if (string.IsNullOrEmpty(pluginInfoGroup.Key) || !AllowedGuidRegex.IsMatch(pluginInfoGroup.Key))
            {
                Logger.Log(LogLevel.Warning, $"Skipping plugin GUID [{pluginInfoGroup.Key}] because it is of an illegal format.");
                continue;
            }

            if (registry.TryGetValue(pluginInfoGroup.Key, out var loadedPlugin))
            {
                Logger.Log(LogLevel.Warning,
                    $"Skipping [{pluginInfoGroup.Key}] because a plugin with a similar GUID ([{loadedPlugin}]) has been already loaded.");
                continue;
            }

            PluginInfo loadedVersion = null;
            foreach (var pluginInfo in pluginInfoGroup.OrderByDescending(info => info.Metadata.Version))
            {
                if (loadedVersion != null)
                {
                    Logger.Log(LogLevel.Warning,
                        $"Skipping [{pluginInfo}] because a newer version exists ({loadedVersion})");
                    continue;
                }

                var filters = pluginInfo.Processes.ToList();
                var invalidProcessName = filters.Count != 0 &&
                    filters.All(filter => !string.Equals(WithoutExecutableSuffix(filter.ProcessName),
                        Paths.ProcessName, StringComparison.InvariantCultureIgnoreCase));

                if (invalidProcessName)
                {
                    Logger.Log(LogLevel.Warning,
                        $"Skipping [{pluginInfo}] because of process filters ({string.Join(", ", pluginInfo.Processes.Select(process => process.ProcessName))})");
                    continue;
                }

                loadedVersion = pluginInfo;
                // Both hard and soft dependencies participate, in their original enumeration order.
                dependencyDict[pluginInfo.Metadata.GUID] = pluginInfo.Dependencies.Select(dependency => dependency.DependencyGUID);
                pluginsByGuid[pluginInfo.Metadata.GUID] = pluginInfo;
            }
        }

        foreach (var pluginInfo in pluginsByGuid.Values.ToList())
        {
            if (pluginInfo.Incompatibilities.Any(incompatibility =>
                    pluginsByGuid.ContainsKey(incompatibility.IncompatibilityGUID) ||
                    registry.ContainsKey(incompatibility.IncompatibilityGUID)))
            {
                pluginsByGuid.Remove(pluginInfo.Metadata.GUID);
                dependencyDict.Remove(pluginInfo.Metadata.GUID);

                // Removal affects subsequent admissions and the incompatibilities reported here.
                var incompatiblePluginsNew = pluginInfo.Incompatibilities.Select(incompatibility => incompatibility.IncompatibilityGUID)
                    .Where(guid => pluginsByGuid.ContainsKey(guid));
                var incompatiblePluginsExisting = pluginInfo.Incompatibilities.Select(incompatibility => incompatibility.IncompatibilityGUID)
                    .Where(guid => registry.ContainsKey(guid));
                var incompatiblePlugins = incompatiblePluginsNew.Concat(incompatiblePluginsExisting).ToArray();
                var message =
                    $@"Could not load [{pluginInfo}] because it is incompatible with: {string.Join(", ", incompatiblePlugins)}";
                dependencyErrors.Add(message);
                Logger.Log(LogLevel.Error, message);
            }
            else if (TargetsWrongVersion(pluginInfo, currentVersion))
            {
                var message =
                    $@"Plugin [{pluginInfo}] targets a wrong version of BepInEx ({pluginInfo.TargettedBepInExVersion}) and might not work until you update";
                dependencyErrors.Add(message);
                Logger.Log(LogLevel.Warning, message);
            }
        }

        // Registered plugins stay out of the graph. Missing nodes sort before their dependents.
        var emptyDependencies = Array.Empty<string>();
        var sortedPlugins = Utility.TopologicalSort(dependencyDict.Keys,
            guid => dependencyDict.TryGetValue(guid, out var dependencies) ? dependencies : emptyDependencies).ToList();

        return sortedPlugins.Where(pluginsByGuid.ContainsKey).Select(guid => pluginsByGuid[guid]).ToList();
    }

    private static string WithoutExecutableSuffix(string processName) =>
        processName != null && processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName.Substring(0, processName.Length - 4)
            : processName;
}
