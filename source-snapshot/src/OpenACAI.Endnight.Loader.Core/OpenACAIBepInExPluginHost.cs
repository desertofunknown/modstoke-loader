// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned runtime host for BepInEx IL2CPP BasePlugin compatibility
// candidates. BepInEx remains the public API surface for legacy plugins, but
// OpenACAI owns discovery selection, execution, state, and failure isolation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Mono.Cecil;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIBepInExPluginHost
    {
        private static readonly List<PluginInfo> LoadedPlugins = new List<PluginInfo>();
        private static readonly Dictionary<string, SemanticVersioning.Version> LoadedVersions =
            new Dictionary<string, SemanticVersioning.Version>(StringComparer.Ordinal);
        private static readonly HashSet<string> AttemptedGuids = new HashSet<string>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<PluginInfo, DirectPluginIdentity> DirectlyLoadedIdentities = new();

        internal static BepInExPluginHostResult LoadPlugins(ModInventoryState inventory, Action<string> addStartupWarning)
        {
            var snapshot = OpenACAIBepInExPluginCompatibility.Refresh(inventory);
            var result = LoadCandidates(snapshot.Candidates, addStartupWarning, "startup", out _);
            OpenACAIBepInExPluginCompatibility.Refresh(inventory);
            return result;
        }

        internal static IList<PluginInfo> LoadDynamicPlugins(string[] directories, Action<string> addStartupWarning)
        {
            ArgumentNullException.ThrowIfNull(directories);
            // Finish discovery before loading anything, including when a later directory is invalid.
            var assemblyPaths = directories.Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
                .Where(path => !IsExcludedDynamicAssembly(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            OpenACAIAssemblyResolver.AddPluginDirectories(assemblyPaths.Select(Path.GetDirectoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var candidates = OpenACAIBepInExPluginCompatibility.DiscoverRuntimeCandidates(assemblyPaths);
            var result = LoadCandidates(candidates, addStartupWarning, "dynamic", out var loaded);
            TryReport(() => OpenACAILoaderState.SetBepInExPluginHostState(result.CreateState()));
            TryReport(() => OpenACAIEndnightRuntime.RequestLoaderStateRefresh("bepinex-dynamic-load"));
            return loaded;
        }

        internal static BasePlugin LoadPlugin(PluginInfo info, Assembly assembly, OpenACAIBepInExRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentNullException.ThrowIfNull(assembly);
            var type = ResolvePluginType(info, assembly);
            var identity = new DirectPluginIdentity(info.Metadata);
            var plugin = ActivatePlugin(info, assembly, type, registry);
            DirectlyLoadedIdentities.AddOrUpdate(info, identity);
            return plugin;
        }

        private static BepInExPluginHostResult LoadCandidates(
            IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> discovered,
            Action<string> addStartupWarning, string invocationKind, out IList<PluginInfo> loaded)
        {
            loaded = new List<PluginInfo>();
            var reports = new List<object>();
            var registry = IL2CPPChainloader.Instance as OpenACAIBepInExRegistry;
            LoadedVersions[OpenACAIEndnightRuntime.PluginGuid] = MetadataHelper.GetMetadata(typeof(Plugin)).Version;
            var candidates = SelectCandidates(discovered, reports, addStartupWarning);

            foreach (var selected in candidates)
            {
                var candidate = selected.Candidate;
                var metadata = selected.Info.Metadata;
                // Nested loads can activate a later member of this already-selected batch.
                if (IsRegisteredOrAttempted(metadata.GUID))
                {
                    reports.Add(CreateReport(candidate, "skipped", "This plugin GUID is already registered or activation was attempted. Restart to retry a failed activation.", ""));
                    continue;
                }

                var newConflicts = selected.Info.Incompatibilities
                    .Where(conflict => IsRegistered(conflict.IncompatibilityGUID))
                    .Select(conflict => conflict.IncompatibilityGUID).ToArray();
                if (newConflicts.Length > 0)
                {
                    Reject(candidate, "Incompatible plugins became registered before activation: " + string.Join(", ", newConflicts), reports, addStartupWarning);
                    continue;
                }
                var missingDependencies = selected.Info.Dependencies
                    .Where(dependency => (dependency.Flags & BepInDependency.DependencyFlags.HardDependency) != 0)
                    .Where(dependency => !TryGetDependencyVersion(dependency.DependencyGUID, out var version)
                        || (dependency.VersionRange != null && !dependency.VersionRange.IsSatisfied(version)))
                    .Select(dependency => dependency.DependencyGUID
                        + (dependency.VersionRange == null ? "" : " " + dependency.VersionRange))
                    .ToArray();
                if (missingDependencies.Length > 0)
                {
                    Reject(candidate, "Hard dependencies did not load or have incompatible versions: "
                        + string.Join(", ", missingDependencies), reports, addStartupWarning);
                    continue;
                }

                try
                {
                    AttemptedGuids.Add(metadata.GUID);
                    TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI BepInEx host loading " + candidate.AssemblyPath + " :: " + candidate.PluginTypeName));
                    var assembly = Assembly.LoadFrom(candidate.AssemblyPath);
                    var pluginType = ResolvePluginType(selected.Info, assembly);

                    registry?.BeginPlugin(selected.Info);
                    TryInitializeModule(pluginType, addStartupWarning);
                    var plugin = ActivatePlugin(selected.Info, assembly, pluginType, registry);
                    selected.Info.Instance = plugin;
                    LoadedPlugins.Add(selected.Info);
                    LoadedVersions.Add(metadata.GUID, metadata.Version);
                    OpenACAIBepInExPluginCompatibility.MarkLoadedByOpenAcai(candidate);
                    loaded.Add(selected.Info);
                    reports.Add(CreateReport(candidate, "loaded", "BepInEx BasePlugin.Load completed through the OpenACAI compatibility host.", pluginType.FullName));
                    registry?.NotifyPluginLoaded(selected.Info);
                    TryReport(() => OpenACAIEndnightRuntime.LogSource?.LogInfo("Loaded BepInEx plugin through OpenACAI host: " + candidate.DisplayName));
                }
                catch (ReflectionTypeLoadException ex)
                {
                    registry?.WithdrawPlugin(selected.Info);
                    var message = "Type load failure: " + string.Join("; ", ex.LoaderExceptions.Where(item => item != null).Select(GetFailureMessage));
                    reports.Add(CreateReport(candidate, "failed", message, candidate.PluginTypeName));
                    TryReport(() => addStartupWarning?.Invoke("BepInEx plugin skipped: " + candidate.DisplayName + " - " + message));
                    TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI BepInEx host type-load failure for " + candidate.AssemblyPath + ": " + message));
                }
                catch (Exception ex)
                {
                    registry?.WithdrawPlugin(selected.Info);
                    var message = GetFailureMessage(ex);
                    reports.Add(CreateReport(candidate, "failed", message, candidate.PluginTypeName));
                    TryReport(() => addStartupWarning?.Invoke("BepInEx plugin skipped: " + candidate.DisplayName + " - " + message));
                    TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI BepInEx host failure for " + candidate.AssemblyPath + ": " + ex));
                }
            }

            return new BepInExPluginHostResult(
                discovered.Count,
                discovered.Count(candidate => candidate.LoadEligible),
                loaded.Count,
                LoadedPlugins.ToArray(),
                invocationKind,
                reports);
        }

        private static Type ResolvePluginType(PluginInfo info, Assembly assembly)
        {
            var type = assembly.GetType(info.TypeName, throwOnError: true, ignoreCase: false);
            if (!typeof(BasePlugin).IsAssignableFrom(type))
                throw new InvalidOperationException("Resolved type is not assignable to BepInEx.Unity.IL2CPP.BasePlugin.");
            var metadata = MetadataHelper.GetMetadata(type);
            if (metadata == null || info.Metadata == null
                || !string.Equals(metadata.GUID, info.Metadata.GUID, StringComparison.Ordinal)
                || !Equals(metadata.Version, info.Metadata.Version))
                throw new InvalidOperationException("The loaded plugin identity does not match the selected GUID and version.");
            return type;
        }

        private static BasePlugin ActivatePlugin(PluginInfo info, Assembly assembly, Type type, OpenACAIBepInExRegistry registry)
        {
            var plugin = (BasePlugin)Activator.CreateInstance(type);
            registry?.BeforeLoad(info, assembly, plugin);
            plugin.Load();
            return plugin;
        }

        private static bool IsRegistered(string guid) => LoadedVersions.ContainsKey(guid)
            || (IL2CPPChainloader.Instance?.Plugins.ContainsKey(guid) ?? false);

        private static bool IsRegisteredOrAttempted(string guid) => IsRegistered(guid) || AttemptedGuids.Contains(guid);

        private static bool TryGetDependencyVersion(string guid, out SemanticVersioning.Version version)
        {
            if (LoadedVersions.TryGetValue(guid, out version))
                return true;

            // BeginPlugin publishes an owned entry before Load finishes. It is not a satisfied dependency yet.
            if (AttemptedGuids.Contains(guid))
                return false;
            if (IL2CPPChainloader.Instance is not OpenACAIBepInExRegistry registry
                || !registry.Plugins.TryGetValue(guid, out var info) || info?.Metadata == null
                || !DirectlyLoadedIdentities.TryGetValue(info, out var loadedIdentity)
                || !string.Equals(loadedIdentity.Guid, guid, StringComparison.Ordinal)
                || !string.Equals(info.Metadata.GUID, guid, StringComparison.Ordinal)
                || !Equals(info.Metadata.Version, loadedIdentity.Version))
                return false;

            version = loadedIdentity.Version;
            return version is not null;
        }

        private static bool IsExcludedDynamicAssembly(string path)
        {
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => string.Equals(segment, "_Disabled", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)))
                return true;

            // These roots have a different owner; a broad caller path must not reclassify their DLLs.
            return IsInside(path, OpenACAIPaths.RuntimeDirectory)
                || IsInside(path, OpenACAIPaths.BootstrapMirrorDirectory)
                || IsInside(path, OpenACAIPaths.ValidationProbesDirectory)
                || IsInside(path, OpenACAIPaths.ModsDirectory)
                || IsInside(path, Path.Combine(Paths.GameRootPath, "Mods"))
                || IsInside(path, Path.Combine(Paths.GameRootPath, "Libs"));
        }

        private static bool IsInside(string path, string directory) => path.StartsWith(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void TryInitializeModule(Type pluginType, Action<string> addStartupWarning)
        {
            try
            {
                RuntimeHelpers.RunModuleConstructor(pluginType.Module.ModuleHandle);
            }
            catch (Exception ex)
            {
                // Match BepInEx's warning-and-continue behavior; the runtime owns initialization state.
                TryReport(() => addStartupWarning?.Invoke("BepInEx module initialization failed for "
                    + pluginType.Assembly.FullName + " :: " + pluginType.FullName + ": " + ex));
            }
        }

        private static IReadOnlyList<PluginCandidate> SelectCandidates(
            IReadOnlyList<OpenACAIBepInExPluginCompatibilityCandidateSnapshot> candidates,
            List<object> reports,
            Action<string> addStartupWarning)
        {
            var metadataCandidates = new List<PluginCandidate>();
            var selfAssemblyPath = NormalizePath(Assembly.GetExecutingAssembly().Location);
            var selfPluginGuid = OpenACAIEndnightRuntime.PluginGuid;
            var selfPluginTypeName = typeof(Plugin).FullName;

            foreach (var candidate in candidates.Where(candidate => candidate != null && !candidate.ValidationProbe))
            {
                if (!candidate.LoadEligible)
                {
                    reports.Add(CreateReport(candidate, "skipped", candidate.Detail, ""));
                    continue;
                }

                if (IsSelfCandidate(candidate, selfAssemblyPath, selfPluginGuid, selfPluginTypeName))
                {
                    reports.Add(CreateReport(candidate, "skipped", "Candidate is OpenACAI runtime core plugin; already loaded by bootstrap.", selfPluginTypeName));
                    continue;
                }

                if (IL2CPPChainloader.Instance != null && IL2CPPChainloader.Instance is not OpenACAIBepInExRegistry)
                {
                    reports.Add(CreateReport(candidate, "skipped", "The upstream fallback chainloader owns plugin loading for this startup.", ""));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(candidate.AssemblyPath) || !File.Exists(candidate.AssemblyPath))
                {
                    reports.Add(CreateReport(candidate, "skipped", "Assembly file does not exist.", ""));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(candidate.PluginTypeName))
                {
                    reports.Add(CreateReport(candidate, "skipped", "BepInEx plugin type name was not available.", ""));
                    continue;
                }

                try
                {
                    var info = ReadPluginInfo(candidate);
                    if (info == null)
                    {
                        Reject(candidate, "Plugin metadata or BasePlugin type is invalid.", reports, addStartupWarning);
                        continue;
                    }

                    if (info.Dependencies.Any(dependency => string.IsNullOrWhiteSpace(dependency.DependencyGUID))
                        || info.Incompatibilities.Any(incompatibility => string.IsNullOrWhiteSpace(incompatibility.IncompatibilityGUID))
                        || info.Processes.Any(process => process.ProcessName == null))
                    {
                        Reject(candidate, "Plugin dependency, incompatibility or process metadata contains an invalid identifier.", reports, addStartupWarning);
                        continue;
                    }

                    metadataCandidates.Add(new PluginCandidate(candidate, info));
                }
                catch (Exception ex)
                {
                    Reject(candidate, "Plugin metadata could not be read: " + GetFailureMessage(ex), reports, addStartupWarning);
                }
            }

            var selected = new Dictionary<string, PluginCandidate>(StringComparer.Ordinal);
            foreach (var group in metadataCandidates.GroupBy(candidate => candidate.Info.Metadata.GUID, StringComparer.Ordinal))
            {
                foreach (var candidate in group.OrderByDescending(candidate => candidate.Info.Metadata.Version))
                {
                    var guid = candidate.Info.Metadata.GUID;
                    if (IsRegisteredOrAttempted(guid) || selected.ContainsKey(guid))
                    {
                        reports.Add(CreateReport(candidate.Candidate, "skipped", "This plugin GUID is already registered, selected or activation was attempted.", ""));
                        continue;
                    }

                    if (candidate.Info.Processes.Any() && !candidate.Info.Processes.Any(process =>
                        string.Equals(process.ProcessName.Replace(".exe", ""), Paths.ProcessName, StringComparison.InvariantCultureIgnoreCase)))
                    {
                        reports.Add(CreateReport(candidate.Candidate, "skipped", "The plugin's BepInProcess filters do not match this process.", ""));
                        continue;
                    }

                    selected.Add(guid, candidate);
                }
            }

            foreach (var candidate in selected.Values.ToArray())
            {
                var conflicts = candidate.Info.Incompatibilities
                    .Select(incompatibility => incompatibility.IncompatibilityGUID)
                    .Where(guid => selected.ContainsKey(guid) || IsRegistered(guid))
                    .ToArray();
                if (conflicts.Length == 0)
                    continue;

                selected.Remove(candidate.Info.Metadata.GUID);
                Reject(candidate.Candidate, "Incompatible plugins are present: " + string.Join(", ", conflicts), reports, addStartupWarning);
            }

            try
            {
                // Materialize before activation: even a late cycle must not leave a partly loaded batch.
                return OrderCandidates(selected);
            }
            catch (Exception ex)
            {
                foreach (var candidate in selected.Values)
                    Reject(candidate.Candidate, "Plugin dependency ordering failed: " + GetFailureMessage(ex), reports, addStartupWarning);
                return Array.Empty<PluginCandidate>();
            }
        }

        private static IReadOnlyList<PluginCandidate> OrderCandidates(Dictionary<string, PluginCandidate> selected)
        {
            var ordered = new List<PluginCandidate>();
            var completed = new Dictionary<string, bool>(StringComparer.Ordinal);
            var pending = new Stack<(string Guid, bool Complete)>();
            foreach (var guid in selected.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                pending.Push((guid, false));
                while (pending.TryPop(out var visit))
                {
                    if (visit.Complete)
                    {
                        completed[visit.Guid] = true;
                        if (selected.TryGetValue(visit.Guid, out var candidate))
                            ordered.Add(candidate);
                        continue;
                    }

                    if (completed.TryGetValue(visit.Guid, out var done))
                    {
                        if (!done)
                            throw new InvalidOperationException("Plugin dependency cycle includes " + visit.Guid + ".");
                        continue;
                    }

                    completed.Add(visit.Guid, false);
                    pending.Push((visit.Guid, true));
                    if (!selected.TryGetValue(visit.Guid, out var current))
                        continue;

                    // Reverse the pushes to retain declared dependency order in depth-first traversal.
                    var dependencies = current.Info.Dependencies.Select(dependency => dependency.DependencyGUID).ToArray();
                    for (var i = dependencies.Length - 1; i >= 0; i--)
                        pending.Push((dependencies[i], false));
                }
            }
            return ordered;
        }

        private static PluginInfo ReadPluginInfo(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            using (var resolver = new DefaultAssemblyResolver())
            {
                foreach (var directory in resolver.GetSearchDirectories())
                    resolver.RemoveSearchDirectory(directory);
                resolver.AddSearchDirectory(Path.GetDirectoryName(candidate.AssemblyPath));
                foreach (var directory in OpenACAIAssemblyResolver.SearchDirectoriesSnapshot())
                    resolver.AddSearchDirectory(directory);
                resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));

                using (var assembly = AssemblyDefinition.ReadAssembly(candidate.AssemblyPath,
                    new ReaderParameters { AssemblyResolver = resolver }))
                {
                    var type = assembly.MainModule.GetType(candidate.PluginTypeName.Replace('+', '/'));
                    var info = OpenACAIBepInExPluginMetadata.Read(type, candidate.AssemblyPath);
                    if (info != null)
                        info.TypeName = candidate.PluginTypeName;
                    return info;
                }
            }
        }

        private static void Reject(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate, string reason,
            List<object> reports, Action<string> addStartupWarning)
        {
            reports.Add(CreateReport(candidate, "skipped", reason, ""));
            if (IL2CPPChainloader.Instance is OpenACAIBepInExRegistry registry)
                registry.DependencyErrors.Add(candidate.DisplayName + ": " + reason);
            TryReport(() => addStartupWarning?.Invoke("BepInEx plugin skipped: " + candidate.DisplayName + " - " + reason));
            TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI BepInEx host skipped " + candidate.AssemblyPath + ": " + reason));
        }

        private sealed class DirectPluginIdentity
        {
            internal DirectPluginIdentity(BepInPlugin metadata)
            {
                Guid = metadata.GUID;
                Version = metadata.Version;
            }

            internal string Guid { get; }
            internal SemanticVersioning.Version Version { get; }
        }

        private sealed class PluginCandidate
        {
            internal PluginCandidate(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate, PluginInfo info)
            {
                Candidate = candidate;
                Info = info;
            }

            internal OpenACAIBepInExPluginCompatibilityCandidateSnapshot Candidate { get; }
            internal PluginInfo Info { get; }
        }

        private static bool IsSelfCandidate(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate, string selfAssemblyPath, string selfPluginGuid, string selfPluginTypeName)
        {
            if (candidate == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(selfPluginGuid)
                && string.Equals(candidate.PluginGuid, selfPluginGuid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var candidateAssemblyPath = NormalizePath(candidate.AssemblyPath);
            return !string.IsNullOrWhiteSpace(candidateAssemblyPath)
                && !string.IsNullOrWhiteSpace(selfAssemblyPath)
                && string.Equals(candidate.PluginTypeName, selfPluginTypeName, StringComparison.Ordinal)
                && string.Equals(candidateAssemblyPath, selfAssemblyPath, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetFailureMessage(Exception error)
        {
            // Exception.Message is virtual; a broken formatter must not abort the rest of the load batch.
            try { return error.Message ?? error.GetType().FullName; }
            catch { return error.GetType().FullName; }
        }

        private static void TryReport(Action report)
        {
            try
            {
                report();
            }
            catch
            {
                // Diagnostics cannot change a plugin's outcome or stop the next load.
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }

        private static object CreateReport(OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate, string status, string reason, string loadedTypeName)
        {
            return new
            {
                id = candidate == null ? "" : candidate.ModId,
                candidateId = candidate == null ? "" : candidate.CandidateId,
                guid = candidate == null ? "" : candidate.PluginGuid,
                name = candidate == null ? "" : candidate.DisplayName,
                assemblyName = candidate == null ? "" : candidate.AssemblyName,
                pluginTypeName = candidate == null ? "" : candidate.PluginTypeName,
                loadedTypeName = loadedTypeName ?? "",
                assemblyPath = candidate == null ? "" : candidate.AssemblyPath,
                status = status ?? "",
                reason = reason ?? "",
                loadEligible = candidate != null && candidate.LoadEligible,
                loadedByOpenAcai = string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase)
            };
        }
    }

    internal sealed class BepInExPluginHostResult
    {
        internal BepInExPluginHostResult(
            int discoveredCount,
            int loadEligibleCount,
            int loadedThisRunCount,
            IReadOnlyList<PluginInfo> retainedPlugins,
            string invocationKind,
            IReadOnlyList<object> loadReports)
        {
            DiscoveredCount = Math.Max(0, discoveredCount);
            LoadEligibleCount = Math.Max(0, loadEligibleCount);
            LoadedThisRunCount = Math.Max(0, loadedThisRunCount);
            _retainedPlugins = retainedPlugins;
            InvocationKind = invocationKind;
            LoadReports = loadReports ?? Array.Empty<object>();
        }

        internal int DiscoveredCount { get; }
        internal int LoadEligibleCount { get; }
        internal int LoadedThisRunCount { get; }
        private readonly IReadOnlyList<PluginInfo> _retainedPlugins;
        internal int RetainedPluginCount => _retainedPlugins.Count;
        internal string InvocationKind { get; }
        internal IReadOnlyList<object> LoadReports { get; }

        internal object CreateState()
        {
            return new
            {
                serviceKey = "openacai.loader.bepInExPluginHost",
                owner = "Modstoke Loader",
                compatibilityTarget = "BepInEx IL2CPP",
                mode = "openacai-bepinex-plugin-host",
                invocationKind = InvocationKind,
                runtimePluginLoadingEnabled = OpenACAIBepInExPluginCompatibility.RuntimePluginLoadingEnabled,
                legacyRegistryPublished = IL2CPPChainloader.Instance is OpenACAIBepInExRegistry,
                discoveredCount = DiscoveredCount,
                loadEligibleCount = LoadEligibleCount,
                loadedThisRunCount = LoadedThisRunCount,
                retainedPluginCount = RetainedPluginCount,
                retainedPlugins = _retainedPlugins.Select(info => new
                {
                    guid = info.Metadata.GUID,
                    version = info.Metadata.Version.ToString(),
                    typeName = info.TypeName,
                    assemblyPath = info.Location
                }).ToArray(),
                loadReports = LoadReports.ToArray()
            };
        }
    }
}
