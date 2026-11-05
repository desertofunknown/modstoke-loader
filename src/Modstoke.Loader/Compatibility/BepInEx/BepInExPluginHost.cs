// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Modstoke-owned runtime host for BepInEx IL2CPP BasePlugin compatibility
// candidates. BepInEx remains the public API surface for legacy plugins, but
// Modstoke owns discovery selection, execution, state, and failure isolation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Mono.Cecil;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class BepInExPluginHost
    {
        private static readonly List<WeakReference<PluginInfo>> LoadedPlugins = new();
        private static readonly HashSet<string> AttemptedGuids = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ActivatingGuids = new(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<PluginInfo, CompletedPlugin> CompletedPlugins = new();
        private static readonly ConditionalWeakTable<Type, PluginIdentity> PluginTypeIdentities = new();
        private const int MaximumActivationObservations = 1024;
        private static readonly List<ActivationObservation> ActivationObservations = new();

        internal static bool ActivationObservationsTruncated { get; private set; }

        // The caller owns the startup/Unity thread boundary. These are successful Load observations,
        // not a claim that a plugin is still active or has not subsequently been unloaded.
        internal static IReadOnlyList<BepInExActivationSnapshot> CaptureActivations()
        {
            var registry = IL2CPPChainloader.Instance as BepInExRegistry;
            var snapshots = new BepInExActivationSnapshot[ActivationObservations.Count];
            for (var index = 0; index < snapshots.Length; index++)
            {
                var observation = ActivationObservations[index];
                var identity = observation.Identity;
                var registered = registry != null && identity.Guid != null
                    && registry.Plugins.TryGetValue(identity.Guid, out var current)
                    && observation.Info.TryGetTarget(out var info) && ReferenceEquals(current, info)
                    && observation.Plugin.TryGetTarget(out var plugin) && ReferenceEquals(info.Instance, plugin)
                    && info.Metadata != null
                    && string.Equals(info.Metadata.GUID, identity.Guid, StringComparison.Ordinal)
                    && identity.MatchesVersion(info.Metadata.Version);
                snapshots[index] = new BepInExActivationSnapshot(
                    identity.Id, identity.Version, registered, identity.Direct, identity.Direct);
            }
            return Array.AsReadOnly(snapshots);
        }

        internal static BepInExPluginHostResult LoadPlugins(ModInventoryState inventory, Action<string> addStartupWarning)
        {
            var snapshot = BepInExPluginCompatibility.Refresh(inventory);
            var result = LoadCandidates(snapshot.Candidates, addStartupWarning, "startup", out _);
            BepInExPluginCompatibility.Refresh(inventory);
            return result;
        }

        internal static IList<PluginInfo> LoadDynamicPlugins(string[] directories, Action<string> addStartupWarning)
        {
            ArgumentNullException.ThrowIfNull(directories);
            // Finish discovery before loading anything, including when a later directory is invalid.
            var assemblyPaths = directories.Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(EnumerateDynamicAssemblies)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AssemblyResolver.AddPluginDirectories(assemblyPaths.Select(Path.GetDirectoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var candidates = BepInExPluginCompatibility.DiscoverRuntimeCandidates(assemblyPaths);
            var result = LoadCandidates(candidates, addStartupWarning, "dynamic", out var loaded);
            TryReport(() => LoaderState.SetBepInExPluginHostState(result.CreateState()));
            TryReport(() => RuntimeEntryPoint.RequestLoaderStateRefresh("bepinex-dynamic-load"));
            return loaded;
        }

        internal static BasePlugin LoadPlugin(PluginInfo info, Assembly assembly, BepInExRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentNullException.ThrowIfNull(assembly);
            var identity = new PluginIdentity(info.Metadata);
            if (string.IsNullOrEmpty(identity.Guid))
                throw new InvalidOperationException("The plugin metadata has no GUID.");
            if (!ActivatingGuids.Add(identity.Guid))
                throw new InvalidOperationException("Activation is already in progress for plugin " + identity.Guid + ".");
            try
            {
                var type = ResolvePluginType(info, assembly);
                var observationIdentity = TryCaptureActivationIdentity(null, null, info.Metadata, direct: true);
                ActivatePlugin(info, assembly, type, registry, identity, ownsRegistration: false, out var plugin);
                RecordActivation(observationIdentity, info, plugin);
                CompletedPlugins.AddOrUpdate(info, new CompletedPlugin(identity, plugin));
                return plugin;
            }
            finally
            {
                ActivatingGuids.Remove(identity.Guid);
            }
        }

        private static BepInExPluginHostResult LoadCandidates(
            IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> discovered,
            Action<string> addStartupWarning, string invocationKind, out IList<PluginInfo> loaded)
        {
            loaded = new List<PluginInfo>();
            var reports = new List<object>();
            var registry = IL2CPPChainloader.Instance as BepInExRegistry;
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
                    var dependencyError = "Hard dependencies did not load or have incompatible versions: "
                        + string.Join(", ", missingDependencies);
                    Reject(candidate, dependencyError, reports, addStartupWarning);
                    RecordHostLoad(selected, "failed", 0, dependencyError);
                    continue;
                }

                var identity = new PluginIdentity(metadata);
                BasePlugin plugin = null;
                var activated = false;
                var timer = new System.Diagnostics.Stopwatch();
                ActivatingGuids.Add(identity.Guid);
                try
                {
                    RequireActiveRegistry(registry);
                    AttemptedGuids.Add(identity.Guid);
                    TryReport(() => RuntimeEntryPoint.TraceCompat("Modstoke BepInEx host loading " + candidate.AssemblyPath + " :: " + candidate.PluginTypeName));
                    timer.Start();
                    var assembly = Assembly.LoadFrom(candidate.AssemblyPath);
                    var pluginType = ResolvePluginType(selected.Info, assembly);
                    var observationIdentity = TryCaptureActivationIdentity(
                        candidate.PluginGuid, candidate.PluginVersion, metadata, direct: false);

                    registry?.BeginPlugin(selected.Info);
                    TryInitializeModule(pluginType, addStartupWarning);
                    ActivatePlugin(selected.Info, assembly, pluginType, registry, identity, ownsRegistration: true, out plugin);
                    RecordActivation(observationIdentity, selected.Info, plugin);
                    RequireRegistration(selected.Info, registry, identity, plugin);
                    selected.Info.Instance = plugin;
                    CompletedPlugins.AddOrUpdate(selected.Info, new CompletedPlugin(identity, plugin));
                    LoadedPlugins.Add(new WeakReference<PluginInfo>(selected.Info));
                    BepInExPluginCompatibility.MarkLoadedByOpenAcai(candidate);
                    loaded.Add(selected.Info);
                    reports.Add(CreateReport(candidate, "loaded", "BepInEx BasePlugin.Load completed through the Modstoke compatibility host.", pluginType.FullName));
                    RecordHostLoad(selected, "loaded", timer.Elapsed.TotalMilliseconds, null);
                    activated = true;
                }
                catch (Exception ex)
                {
                    WithdrawFailedPlugin(selected.Info, registry, identity, plugin);
                    var message = ex is ReflectionTypeLoadException typeLoadException
                        ? "Type load failure: " + string.Join("; ", typeLoadException.LoaderExceptions
                            .Where(item => item != null).Select(GetFailureMessage))
                        : GetFailureMessage(ex);
                    reports.Add(CreateReport(candidate, "failed", message, candidate.PluginTypeName));
                    RecordHostLoad(selected, "failed", timer.Elapsed.TotalMilliseconds, message);
                    TryReport(() => addStartupWarning?.Invoke("BepInEx plugin skipped: " + candidate.DisplayName + " - " + message));
                    TryReport(() => RuntimeEntryPoint.TraceCompat(ex is ReflectionTypeLoadException
                        ? "Modstoke BepInEx host type-load failure for " + candidate.AssemblyPath + ": " + message
                        : "Modstoke BepInEx host failure for " + candidate.AssemblyPath + ": " + ex));
                }
                finally
                {
                    ActivatingGuids.Remove(identity.Guid);
                }

                if (activated)
                {
                    TryReport(() => registry?.NotifyPluginLoaded(selected.Info));
                    TryReport(() => RuntimeEntryPoint.LogSource?.LogInfo("Loaded BepInEx plugin through Modstoke host: " + candidate.DisplayName));
                }
            }

            return new BepInExPluginHostResult(
                discovered.Count,
                discovered.Count(candidate => candidate.LoadEligible),
                loaded.Count,
                CaptureRetainedPlugins(),
                invocationKind,
                reports);
        }

        private static void RecordHostLoad(PluginCandidate selected, string status, double loadMs, string error)
        {
            var candidate = selected.Candidate;
            var metadata = selected.Info?.Metadata;
            var id = metadata?.GUID ?? candidate.PluginGuid;
            if (string.IsNullOrWhiteSpace(id))
                id = candidate.CandidateId ?? candidate.ModId;
            HostMods.RecordLoad(id, metadata?.Name ?? candidate.PluginName ?? candidate.DisplayName,
                metadata?.Version?.ToString() ?? candidate.PluginVersion, "bepinex",
                candidate.AssemblyPath, status, loadMs, error);
        }

        private static Type ResolvePluginType(PluginInfo info, Assembly assembly)
        {
            var type = assembly.GetType(info.TypeName, throwOnError: true, ignoreCase: false);
            if (!typeof(BasePlugin).IsAssignableFrom(type))
                throw new InvalidOperationException("Resolved type is not assignable to BepInEx.Unity.IL2CPP.BasePlugin.");
            if (!GetPluginTypeIdentity(type).Matches(info.Metadata))
                throw new InvalidOperationException("The loaded plugin identity does not match the selected GUID and version.");
            return type;
        }

        private static PluginIdentity GetPluginTypeIdentity(Type type) => PluginTypeIdentities.GetValue(
            type, static pluginType => new PluginIdentity(MetadataHelper.GetMetadata(pluginType)));

        private static void ActivatePlugin(PluginInfo info, Assembly assembly, Type type,
            BepInExRegistry registry, PluginIdentity identity, bool ownsRegistration, out BasePlugin plugin)
        {
            plugin = null;
            if (ownsRegistration)
                RequireRegistration(info, registry, identity, null);
            plugin = (BasePlugin)Activator.CreateInstance(type);
            if (ownsRegistration)
                RequireRegistration(info, registry, identity, plugin);
            registry?.BeforeLoad(info, assembly, plugin);
            if (ownsRegistration)
            {
                RequireRegistration(info, registry, identity, plugin);
                RequireActivationDependencies(info);
            }
            plugin.Load();
        }

        private static void RequireRegistration(PluginInfo info, BepInExRegistry registry,
            PluginIdentity identity, BasePlugin plugin)
        {
            RequireActiveRegistry(registry);
            if (!identity.Matches(info.Metadata)
                || (info.Instance != null && !ReferenceEquals(info.Instance, plugin))
                || !registry.Plugins.TryGetValue(identity.Guid, out var current) || !ReferenceEquals(current, info))
                throw new InvalidOperationException("The plugin registration changed during activation: " + identity.Guid + ".");
        }

        private static void RequireActiveRegistry(BepInExRegistry registry)
        {
            if (registry == null || !ReferenceEquals(IL2CPPChainloader.Instance, registry))
                throw new InvalidOperationException("The Modstoke plugin registry is no longer active.");
        }

        private static void WithdrawFailedPlugin(PluginInfo info, BepInExRegistry registry,
            PluginIdentity identity, BasePlugin plugin)
        {
            if (registry != null && registry.Plugins.TryGetValue(identity.Guid, out var current)
                && ReferenceEquals(current, info))
                registry.Plugins.Remove(identity.Guid);
            if (plugin != null && ReferenceEquals(info.Instance, plugin))
                info.Instance = null;
            if (CompletedPlugins.TryGetValue(info, out var completed)
                && ReferenceEquals(completed.Identity, identity)
                && completed.Plugin.TryGetTarget(out var completedInstance)
                && ReferenceEquals(completedInstance, plugin))
                CompletedPlugins.Remove(info);
        }

        private static void RequireActivationDependencies(PluginInfo info)
        {
            // Module, constructor and pre-load callbacks can change the registry after batch admission.
            foreach (var conflict in info.Incompatibilities)
            {
                if (IsRegistered(conflict.IncompatibilityGUID))
                    throw new InvalidOperationException("An incompatible plugin became registered before Load: "
                        + conflict.IncompatibilityGUID + ".");
            }
            foreach (var dependency in info.Dependencies)
            {
                if ((dependency.Flags & BepInDependency.DependencyFlags.HardDependency) != 0
                    && (!TryGetDependencyVersion(dependency.DependencyGUID, out var version)
                        || (dependency.VersionRange != null && !dependency.VersionRange.IsSatisfied(version))))
                    throw new InvalidOperationException("A hard dependency became unavailable before Load: "
                        + dependency.DependencyGUID + ".");
            }
        }

        private static bool IsRegistered(string guid) => string.Equals(guid, RuntimeEntryPoint.PluginGuid, StringComparison.Ordinal)
            || (IL2CPPChainloader.Instance?.Plugins.ContainsKey(guid) ?? false);

        private static bool IsRegisteredOrAttempted(string guid) => IsRegistered(guid)
            || AttemptedGuids.Contains(guid) || ActivatingGuids.Contains(guid);

        private static bool TryGetDependencyVersion(string guid, out SemanticVersioning.Version version)
        {
            // Core loads its plugins before publishing its own Instance.
            if (string.Equals(guid, RuntimeEntryPoint.PluginGuid, StringComparison.Ordinal))
            {
                version = GetPluginTypeIdentity(typeof(Plugin)).Version;
                return version is not null;
            }

            if (TryGetCompletedPlugin(guid, out _, out var completed))
            {
                version = completed.Identity.Version;
                return true;
            }

            version = null;
            return false;
        }

        private static bool TryGetCompletedPlugin(string guid, out PluginInfo info, out CompletedPlugin completed)
        {
            info = null;
            completed = null;
            // BeginPlugin and low-level callers can publish an entry before Load finishes.
            if (IL2CPPChainloader.Instance is not BepInExRegistry registry
                || !registry.Plugins.TryGetValue(guid, out info) || info == null
                || !CompletedPlugins.TryGetValue(info, out completed)
                || !string.Equals(completed.Identity.Guid, guid, StringComparison.Ordinal)
                || !completed.Identity.Matches(info.Metadata)
                || !completed.Plugin.TryGetTarget(out var plugin) || !ReferenceEquals(info.Instance, plugin))
                return false;

            return completed.Identity.Version is not null;
        }

        private static IReadOnlyList<PluginInfo> CaptureRetainedPlugins()
        {
            var retained = new List<PluginInfo>(LoadedPlugins.Count);
            var liveCount = 0;
            for (var index = 0; index < LoadedPlugins.Count; index++)
            {
                var reference = LoadedPlugins[index];
                if (!reference.TryGetTarget(out var info)
                    || !CompletedPlugins.TryGetValue(info, out var completed)
                    || !TryGetCompletedPlugin(completed.Identity.Guid, out var registered, out _)
                    || !ReferenceEquals(registered, info))
                    continue;

                retained.Add(info);
                LoadedPlugins[liveCount++] = reference;
            }
            LoadedPlugins.RemoveRange(liveCount, LoadedPlugins.Count - liveCount);
            return retained;
        }

        private static IEnumerable<string> EnumerateDynamicAssemblies(string directory)
        {
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                // Excluded roots must not be traversed, even when the caller supplies a broad path.
                if (IsExcludedDynamicAssembly(current))
                    continue;
                foreach (var path in Directory.EnumerateFiles(current, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    if (!IsExcludedDynamicAssembly(path))
                        yield return path;
                }
                foreach (var child in Directory.EnumerateDirectories(current))
                    pending.Push(child);
            }
        }

        private static bool IsExcludedDynamicAssembly(string path)
        {
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => string.Equals(segment, "_Disabled", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)))
                return true;

            // These roots have a different owner; a broad caller path must not reclassify their DLLs.
            // Leftovers in the pre-rename OpenACAI folder stay excluded too.
            return IsInside(path, LoaderPaths.RootDirectory)
                || IsInside(path, LoaderPaths.LegacyRootDirectory)
                || IsInside(path, LoaderPaths.BootstrapMirrorDirectory)
                || IsInside(path, Path.Combine(Paths.GameRootPath, "Mods"))
                || IsInside(path, Path.Combine(Paths.GameRootPath, "Libs"));
        }

        private static bool IsInside(string path, string directory)
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

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
            IReadOnlyList<BepInExPluginCompatibilityCandidateSnapshot> candidates,
            List<object> reports,
            Action<string> addStartupWarning)
        {
            var metadataCandidates = new List<PluginCandidate>();
            var selfAssemblyPath = NormalizePath(Assembly.GetExecutingAssembly().Location);
            var selfPluginGuid = RuntimeEntryPoint.PluginGuid;
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
                    reports.Add(CreateReport(candidate, "skipped", "Candidate is Modstoke runtime core plugin; already loaded by bootstrap.", selfPluginTypeName));
                    continue;
                }

                if (IL2CPPChainloader.Instance != null && IL2CPPChainloader.Instance is not BepInExRegistry)
                {
                    reports.Add(CreateReport(candidate, "skipped", "The upstream fallback chainloader owns plugin loading for this startup.", ""));
                    continue;
                }
                if (IL2CPPChainloader.Instance == null)
                {
                    Reject(candidate, "The Modstoke plugin registry is not active.", reports, addStartupWarning);
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
                    if (!BepInExPluginMetadata.TryCreateIdentity(
                        candidate.PluginGuid, candidate.PluginName, candidate.PluginVersion, out var discoveredIdentity)
                        || !string.Equals(info.Metadata.GUID, discoveredIdentity.GUID, StringComparison.Ordinal)
                        || !string.Equals(info.Metadata.Version.ToString(), discoveredIdentity.Version.ToString(), StringComparison.Ordinal))
                    {
                        Reject(candidate, "Plugin identity changed after discovery; refresh discovery before loading it.", reports, addStartupWarning);
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

        private static PluginInfo ReadPluginInfo(BepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            using (var resolver = new DefaultAssemblyResolver())
            {
                foreach (var directory in resolver.GetSearchDirectories())
                    resolver.RemoveSearchDirectory(directory);
                resolver.AddSearchDirectory(Path.GetDirectoryName(candidate.AssemblyPath));
                foreach (var directory in AssemblyResolver.SearchDirectoriesSnapshot())
                    resolver.AddSearchDirectory(directory);
                resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));

                using (var assembly = AssemblyDefinition.ReadAssembly(candidate.AssemblyPath,
                    new ReaderParameters { AssemblyResolver = resolver }))
                {
                    var type = assembly.MainModule.GetType(candidate.PluginTypeName.Replace('+', '/'));
                    var info = BepInExPluginMetadata.Read(type, candidate.AssemblyPath);
                    if (info != null)
                        info.TypeName = candidate.PluginTypeName;
                    return info;
                }
            }
        }

        private static void Reject(BepInExPluginCompatibilityCandidateSnapshot candidate, string reason,
            List<object> reports, Action<string> addStartupWarning)
        {
            reports.Add(CreateReport(candidate, "skipped", reason, ""));
            if (IL2CPPChainloader.Instance is BepInExRegistry registry)
                registry.DependencyErrors.Add(candidate.DisplayName + ": " + reason);
            TryReport(() => addStartupWarning?.Invoke("BepInEx plugin skipped: " + candidate.DisplayName + " - " + reason));
            TryReport(() => RuntimeEntryPoint.TraceCompat("Modstoke BepInEx host skipped " + candidate.AssemblyPath + ": " + reason));
        }

        private sealed class PluginIdentity
        {
            internal PluginIdentity(BepInPlugin metadata)
            {
                Guid = metadata?.GUID;
                Version = metadata?.Version;
            }

            internal string Guid { get; }
            internal SemanticVersioning.Version Version { get; }

            internal bool Matches(BepInPlugin metadata) => metadata != null && Guid != null
                && string.Equals(Guid, metadata.GUID, StringComparison.Ordinal)
                && Equals(Version, metadata.Version);
        }

        private sealed class CompletedPlugin
        {
            internal CompletedPlugin(PluginIdentity identity, BasePlugin plugin)
            {
                Identity = identity;
                Plugin = new WeakReference<BasePlugin>(plugin);
            }

            internal PluginIdentity Identity { get; }
            internal WeakReference<BasePlugin> Plugin { get; }
        }

        private static ActivationIdentity TryCaptureActivationIdentity(
            string id, string version, BepInPlugin metadata, bool direct)
        {
            try
            {
                if (direct)
                {
                    id = metadata.GUID;
                    version = metadata.Version?.ToString() ?? "";
                }
                // Do not retain unbounded user metadata, including the private registration identity.
                if (id?.Length > 512 || version?.Length > 256 || metadata.GUID?.Length > 512
                    || metadata.Version?.PreRelease?.Length > 256 || metadata.Version?.Build?.Length > 256)
                {
                    ActivationObservationsTruncated = true;
                    return null;
                }
                return new ActivationIdentity(id, version, metadata, direct);
            }
            catch
            {
                // Formatting or copying report metadata must not change whether the plugin loads.
                ActivationObservationsTruncated = true;
                return null;
            }
        }

        private static void RecordActivation(ActivationIdentity identity, PluginInfo info, BasePlugin plugin)
        {
            if (identity == null)
                return;
            try
            {
                if (ActivationObservations.Count >= MaximumActivationObservations)
                {
                    ActivationObservationsTruncated = true;
                    return;
                }
                // Keep every successful activation, including repeated direct calls for the same GUID.
                // Reporting must not extend the low-level API's plugin or PluginInfo lifetime.
                ActivationObservations.Add(new ActivationObservation(identity, info, plugin));
            }
            catch
            {
                // Observation storage cannot turn a completed Load into an activation failure.
                ActivationObservationsTruncated = true;
            }
        }

        private sealed class ActivationIdentity
        {
            private readonly SemanticVersioning.Version _metadataVersion;

            internal ActivationIdentity(string id, string version, BepInPlugin metadata, bool direct)
            {
                Id = id ?? "";
                Version = version ?? "";
                Guid = metadata.GUID;
                var metadataVersion = metadata.Version;
                if (metadataVersion is not null)
                    _metadataVersion = new SemanticVersioning.Version(metadataVersion.Major,
                        metadataVersion.Minor, metadataVersion.Patch, metadataVersion.PreRelease, metadataVersion.Build);
                Direct = direct;
            }

            internal string Id { get; }
            internal string Version { get; }
            internal string Guid { get; }
            internal bool Direct { get; }

            internal bool MatchesVersion(SemanticVersioning.Version version) =>
                _metadataVersion is not null && version is not null
                && _metadataVersion.Major == version.Major
                && _metadataVersion.Minor == version.Minor
                && _metadataVersion.Patch == version.Patch
                && string.Equals(_metadataVersion.PreRelease, version.PreRelease, StringComparison.Ordinal)
                && string.Equals(_metadataVersion.Build, version.Build, StringComparison.Ordinal);
        }

        private sealed class ActivationObservation
        {
            internal ActivationObservation(ActivationIdentity identity, PluginInfo info, BasePlugin plugin)
            {
                Identity = identity;
                Info = new WeakReference<PluginInfo>(info);
                Plugin = new WeakReference<BasePlugin>(plugin);
            }

            internal ActivationIdentity Identity { get; }
            internal WeakReference<PluginInfo> Info { get; }
            internal WeakReference<BasePlugin> Plugin { get; }
        }

        private sealed class PluginCandidate
        {
            internal PluginCandidate(BepInExPluginCompatibilityCandidateSnapshot candidate, PluginInfo info)
            {
                Candidate = candidate;
                Info = info;
            }

            internal BepInExPluginCompatibilityCandidateSnapshot Candidate { get; }
            internal PluginInfo Info { get; }
        }

        private static bool IsSelfCandidate(BepInExPluginCompatibilityCandidateSnapshot candidate, string selfAssemblyPath, string selfPluginGuid, string selfPluginTypeName)
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

        private static object CreateReport(BepInExPluginCompatibilityCandidateSnapshot candidate, string status, string reason, string loadedTypeName)
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
            _retainedPlugins = retainedPlugins.Select(info => (object)new
            {
                guid = info.Metadata.GUID,
                version = info.Metadata.Version.ToString(),
                typeName = info.TypeName,
                assemblyPath = info.Location
            }).ToArray();
            InvocationKind = invocationKind;
            LoadReports = loadReports ?? Array.Empty<object>();
        }

        internal int DiscoveredCount { get; }
        internal int LoadEligibleCount { get; }
        internal int LoadedThisRunCount { get; }
        private readonly object[] _retainedPlugins;
        internal int RetainedPluginCount => _retainedPlugins.Length;
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
                runtimePluginLoadingEnabled = BepInExPluginCompatibility.RuntimePluginLoadingEnabled,
                legacyRegistryPublished = IL2CPPChainloader.Instance is BepInExRegistry,
                discoveredCount = DiscoveredCount,
                loadEligibleCount = LoadEligibleCount,
                loadedThisRunCount = LoadedThisRunCount,
                retainedPluginCount = RetainedPluginCount,
                retainedPlugins = _retainedPlugins.ToArray(),
                loadReports = LoadReports.ToArray()
            };
        }
    }
}
