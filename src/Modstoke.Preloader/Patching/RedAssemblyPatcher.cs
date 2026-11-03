// SPDX-License-Identifier: GPL-3.0-or-later
// Derived from RedLoader's assembly patcher.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

extern alias RedCompatibility;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RedCompatibility::RedLoader.Bootstrap;
using HarmonyLib;
using Mono.Cecil;
using RedCompatibility::RedLoader;
using RedCompatibility::RedLoader.Utils;
using RedCompatibility::RedLoader.Preloader.Core.Patching;

namespace Modstoke.Preloader;

internal sealed class RedAssemblyPatcher : IAssemblyPatcherBackend
{
    private static readonly string CurrentAssemblyName = typeof(BasePatcher).Assembly.GetName().Name;

    private readonly PatcherContext PatcherContext;
    private readonly Func<byte[], string, Assembly> assemblyLoader;
    private readonly ConfigEntry<bool> ConfigDumpAssemblies;
    private readonly ConfigEntry<bool> ConfigLoadDumpedAssemblies;
    private readonly ConfigEntry<bool> ConfigBreakBeforeLoadAssemblies;
    private readonly HashSet<AssemblyDefinition> _ownedAssemblies = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    private RedAssemblyPatcher(
        PatcherContext context,
        Func<byte[], string, Assembly> assemblyLoader,
        ConfigEntry<bool> dumpAssemblies,
        ConfigEntry<bool> loadDumpedAssemblies,
        ConfigEntry<bool> breakBeforeLoadAssemblies)
    {
        PatcherContext = context;
        this.assemblyLoader = assemblyLoader;
        ConfigDumpAssemblies = dumpAssemblies;
        ConfigLoadDumpedAssemblies = loadDumpedAssemblies;
        ConfigBreakBeforeLoadAssemblies = breakBeforeLoadAssemblies;
    }

    internal static IAssemblyPatcherBackend Create(
        PatcherContext context,
        Func<byte[], string, Assembly> assemblyLoader,
        ConfigEntry<bool> dumpAssemblies,
        ConfigEntry<bool> loadDumpedAssemblies,
        ConfigEntry<bool> breakBeforeLoadAssemblies) =>
        new RedAssemblyPatcher(
            context, assemblyLoader, dumpAssemblies, loadDumpedAssemblies, breakBeforeLoadAssemblies);

    private static Regex allowedGuidRegex { get; } = new(@"^[a-zA-Z0-9\._\-]+$");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        TrackAvailableAssemblies();
        foreach (var assembly in _ownedAssemblies)
        {
            try { assembly.Dispose(); }
            catch (Exception ex)
            {
                AssemblyPatcherDiagnostics.Write(() => RLog.Warning($"Could not dispose a patcher assembly definition: {ex}"));
            }
        }
        _ownedAssemblies.Clear();
        PatcherContext.AvailableAssemblies.Clear();
        PatcherContext.AvailableAssembliesPaths.Clear();
        PatcherContext.PatchDefinitions.Clear();
        PatcherContext.PatcherPlugins.Clear();
    }

    private PatcherPluginMetadata ToPatcherPlugin(TypeDefinition type, string assemblyPath)
    {
        if (type.IsInterface || type.IsAbstract || type.HasGenericParameters)
            return null;

        try
        {
            if (!type.IsSubtypeOf(typeof(BasePatcher)))
                return null;
        }
        catch (AssemblyResolutionException)
        {
            // Missing base-assembly metadata prevents confirming the patcher contract.
            return null;
        }

        var metadata = PatcherPluginInfoAttribute.FromCecilType(type);

        if (metadata == null)
        {
            RLog.Warning($"Skipping over type [{type.FullName}] as no metadata attribute is specified");
            return null;
        }

        if (string.IsNullOrEmpty(metadata.GUID) || !allowedGuidRegex.IsMatch(metadata.GUID))
        {
            RLog.Warning($"Skipping type [{type.FullName}] because its GUID [{metadata.GUID}] is of an illegal format");
            return null;
        }

        if (metadata.Version == null)
        {
            RLog.Warning($"Skipping type [{type.FullName}] because its version is invalid");
            return null;
        }

        if (metadata.Name == null)
        {
            RLog.Warning($"Skipping type [{type.FullName}] because its name is null");
            return null;
        }

        return new PatcherPluginMetadata
        {
            TypeName = type.FullName
        };
    }

    private bool HasPatcherPlugins(AssemblyDefinition ass) =>
        ass.MainModule.AssemblyReferences.Any(reference => reference.Name == CurrentAssemblyName) ||
        ass.Name.Name == CurrentAssemblyName;

    public void AddPatchersFromDirectory(string directory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Directory.Exists(directory))
            return;

        var patchers = TypeLoader.FindPluginTypes(directory, ToPatcherPlugin, HasPatcherPlugins);

        // TODO: Add dependency ordering and process attribute filtering

        foreach (var keyValuePair in patchers)
        {
            var assemblyPath = keyValuePair.Key;
            var patcherCollection = keyValuePair.Value;

            if (patcherCollection.Count == 0)
                continue;

            Assembly ass;
            try { ass = Assembly.LoadFrom(assemblyPath); }
            catch (Exception ex)
            {
                AssemblyPatcherDiagnostics.Write(() => RLog.Error($"Failed to load patcher assembly [{assemblyPath}]: {ex}"));
                continue;
            }

            foreach (var patcherPlugin in patcherCollection)
                try
                {
                    var type = ass.GetType(patcherPlugin.TypeName.Replace('/', '+'), throwOnError: true);
                    if (type.IsAbstract || type.ContainsGenericParameters || !typeof(BasePatcher).IsAssignableFrom(type))
                        throw new InvalidOperationException("The loaded type is not a concrete RedLoader patcher.");

                    var instance = (BasePatcher) Activator.CreateInstance(type);
                    instance.Context = PatcherContext;

                    var definitions = new List<PatchDefinition>();

                    var methods =
                        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    foreach (var method in methods)
                    {
                        var targetAssemblies = MetadataHelper.GetAttributes<TargetAssemblyAttribute>(method);
                        var targetTypes = MetadataHelper.GetAttributes<TargetTypeAttribute>(method);

                        if (targetAssemblies.Length == 0 && targetTypes.Length == 0)
                            continue;

                        var parameters = method.GetParameters();

                        var valid = !method.IsGenericMethod && !method.ContainsGenericParameters
                            && parameters.Length >= 1 && parameters.Length <= 2
                            && (parameters.Length != 2 || parameters[1].ParameterType == typeof(string))
                            && (method.ReturnType == typeof(void) || method.ReturnType == typeof(bool));
                        if (valid)
                        {
                            var firstParameter = parameters[0].ParameterType;
                            valid = ((firstParameter == typeof(AssemblyDefinition)
                                      || firstParameter == typeof(AssemblyDefinition).MakeByRefType())
                                     && targetAssemblies.Length > 0 && targetTypes.Length == 0)
                                || (firstParameter == typeof(TypeDefinition)
                                    && targetTypes.Length > 0 && targetAssemblies.Length == 0);
                        }
                        if (!valid
                            || targetAssemblies.Any(target => string.IsNullOrEmpty(target.TargetAssembly))
                            || targetTypes.Any(target => string.IsNullOrEmpty(target.TargetAssembly)
                                                       || string.IsNullOrEmpty(target.TargetType)))
                        {
                            AssemblyPatcherDiagnostics.Write(() => RLog.Warning($"Skipping method [{method.FullDescription()}] as it is not a valid patcher method"));
                            continue;
                        }

                        void AddDefinition(PatchDefinition definition)
                        {
                            AssemblyPatcherDiagnostics.Write(() => RLog.Debug($"Discovered patch [{definition.FullName}]"));
                            definitions.Add(definition);
                        }

                        foreach (var targetAssembly in targetAssemblies)
                            AddDefinition(new PatchDefinition(targetAssembly, instance, method));
                        foreach (var targetType in targetTypes)
                            AddDefinition(new PatchDefinition(targetType, instance, method));
                    }
                    PatcherContext.PatcherPlugins.Add(instance);
                    PatcherContext.PatchDefinitions.AddRange(definitions);
                }
                catch (Exception e)
                {
                    AssemblyPatcherDiagnostics.Write(() => RLog.Error($"Failed to load patchers from type [{patcherPlugin.TypeName}]: {(e is ReflectionTypeLoadException re ? TypeLoader.TypeLoadExceptionToString(re) : e.ToString())}"));
                }

            var assName = ass.GetName();
            AssemblyPatcherDiagnostics.Write(() => RLog.Msg($"Discovered {patcherCollection.Count} patcher type{(patcherCollection.Count == 1 ? "" : "s")} in [{assName.Name} {assName.Version}]"));
        }
    }

    public void LoadAssemblyDirectories(IEnumerable<string> directories, IEnumerable<string> assemblyExtensions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var roots = directories.ToArray();
        var filesToSearch = assemblyExtensions
            .SelectMany(ext => Utility.GetUniqueFilesInDirectories(roots, $"*.{ext}"));

        foreach (var assemblyPath in filesToSearch)
        {
            var fileName = Path.GetFileName(assemblyPath);
            if (PatcherContext.AvailableAssemblies.ContainsKey(fileName))
                continue;
            AssemblyDefinition assembly;
            try
            {
                if (!RedCompatibility::RedLoader.Preloader.Core.Patching.AssemblyPatcher.TryLoadAssembly(assemblyPath, out assembly))
                    continue;
            }
            catch (Exception ex)
            {
                AssemblyPatcherDiagnostics.Write(() => RLog.Error($"Could not read patch target [{assemblyPath}]: {ex}"));
                continue;
            }
            // These framework assemblies are already loaded and cannot be patched here.
            if (assembly.Name.Name == "System"
             || assembly.Name.Name == "mscorlib")
            {
                try { assembly.Dispose(); }
                catch (Exception ex)
                {
                    AssemblyPatcherDiagnostics.Write(() => RLog.Warning($"Could not dispose ignored assembly [{assemblyPath}]: {ex}"));
                }
                continue;
            }

            _ownedAssemblies.Add(assembly);
            PatcherContext.AvailableAssemblies.Add(fileName, assembly);
            PatcherContext.AvailableAssembliesPaths[fileName] = assemblyPath;

            AssemblyPatcherDiagnostics.Write(() => RLog.Debug($"Assembly loaded: {fileName}"));
        }
    }

    public void PatchAndLoad()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var plugins = PatcherContext.PatcherPlugins.ToArray();
        var patchedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TrackAvailableAssemblies();
        CaptureLoadedAssemblies(resolvedAssemblies, "before patcher initialization");
        try
        {
            foreach (var plugin in plugins)
                RunLifecycle(plugin, initializing: true);

            var patches = PatcherContext.PatchDefinitions.ToArray();
            AssemblyPatcherDiagnostics.Write(() => RLog.Msg($"Executing {patches.Length} patch(es)"));
            foreach (var patch in patches)
            {
                var target = patch.TargetAssembly?.TargetAssembly ?? patch.TargetType.TargetAssembly;
                var targets = target == TargetAssemblyAttribute.AllAssemblies
                    ? PatcherContext.AvailableAssemblies.Keys.ToArray() : new[] { target };
                foreach (var filename in targets)
                {
                    if (invalidAssemblies.Contains(filename)
                        || !PatcherContext.AvailableAssemblies.TryGetValue(filename, out var assembly))
                        continue;
                    RunPatch(patch, assembly, filename, patchedAssemblies, invalidAssemblies);
                }
                CaptureLoadedAssemblies(resolvedAssemblies, patch.FullName);
            }

            LoadPatchedAssemblies(patchedAssemblies, resolvedAssemblies);
        }
        finally
        {
            // Finalize the same instances that were initialized, while their Cecil definitions are still alive.
            foreach (var plugin in plugins)
                RunLifecycle(plugin, initializing: false);
        }

        void RunLifecycle(BasePatcher plugin, bool initializing)
        {
            var phase = initializing ? "initializer" : "finalizer";
            try
            {
                if (initializing)
                    plugin.Initialize();
                else
                    plugin.Finalizer();
            }
            catch (Exception ex)
            {
                AssemblyPatcherDiagnostics.Write(() => RLog.Error($"Failed to run {phase} of {plugin.Info?.GUID}: {ex}"));
            }
            finally
            {
                TrackAvailableAssemblies();
                CaptureLoadedAssemblies(resolvedAssemblies, phase);
            }
        }
    }

    private void RunPatch(PatchDefinition patch, AssemblyDefinition assembly, string filename,
        HashSet<string> patchedAssemblies, HashSet<string> invalidAssemblies)
    {
        var isAssemblyPatch = patch.TargetAssembly != null;
        object[] arguments = null;
        try
        {
            arguments = new object[patch.MethodInfo.GetParameters().Length];
            if (isAssemblyPatch)
                arguments[0] = assembly;
            else
            {
                var type = assembly.MainModule.GetType(patch.TargetType.TargetType);
                if (type == null)
                {
                    AssemblyPatcherDiagnostics.Write(() => RLog.Warning($"Unable to find type [{patch.TargetType.TargetType}] in {filename}. Skipping [{patch.FullName}]"));
                    return;
                }
                arguments[0] = type;
            }
            if (arguments.Length == 2)
                arguments[1] = filename;

            var result = patch.MethodInfo.Invoke(patch.Instance, arguments);
            if (patch.MethodInfo.ReturnType == typeof(bool) && !(bool)result)
                return;

            if (isAssemblyPatch)
                PatcherContext.AvailableAssemblies[filename] = (AssemblyDefinition)arguments[0]
                    ?? throw new InvalidOperationException("An assembly patch returned a null assembly.");
            patchedAssemblies.Add(filename);
        }
        catch (Exception ex)
        {
            patchedAssemblies.Remove(filename);
            invalidAssemblies.Add(filename);
            AssemblyPatcherDiagnostics.Write(() => RLog.Error($"Failed [{patch.FullName}] on {filename}; its patched output will not be loaded: {ex}"));
        }
        finally
        {
            if (isAssemblyPatch && arguments?.Length > 0 && arguments[0] is AssemblyDefinition replacement)
                _ownedAssemblies.Add(replacement);
            TrackAvailableAssemblies();
        }
    }

    private void LoadPatchedAssemblies(HashSet<string> patchedAssemblies, Dictionary<string, string> resolvedAssemblies)
    {
        // Initializers can add targets, and ref patches can replace their definitions.
        var assemblies = PatcherContext.AvailableAssemblies.Where(pair => patchedAssemblies.Contains(pair.Key)).ToArray();
        var dumpedAssemblyPaths = new Dictionary<string, string>();
        if (ConfigDumpAssemblies.Value || ConfigLoadDumpedAssemblies.Value)
        {
            foreach (var kv in assemblies)
            {
                try
                {
                    if (kv.Key != Path.GetFileName(kv.Key) || kv.Key == "." || kv.Key == "..")
                        throw new InvalidOperationException($"Patcher context contains an invalid dump filename: {kv.Key}");
                    Directory.CreateDirectory(PatcherContext.DumpedAssembliesPath);
                    var path = Path.Combine(PatcherContext.DumpedAssembliesPath, kv.Key);
                    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                        kv.Value.Write(stream);
                    dumpedAssemblyPaths[kv.Key] = path;
                }
                catch (Exception ex)
                {
                    AssemblyPatcherDiagnostics.Write(() => RLog.Error($"Could not dump {kv.Key}; loading from memory instead: {ex}"));
                }
            }
        }

        if (ConfigBreakBeforeLoadAssemblies.Value)
        {
            AssemblyPatcherDiagnostics.Write(() => RLog.Msg($"Redloader is about to load the following assemblies:\n{string.Join("\n", assemblies.Select(pair => pair.Key))}"));
            if (dumpedAssemblyPaths.Count != 0)
                AssemblyPatcherDiagnostics.Write(() => RLog.Msg($"The assemblies were dumped into {PatcherContext.DumpedAssembliesPath}"));
            Debugger.Break();
        }

        foreach (var kv in assemblies)
        {
            try
            {
                CaptureLoadedAssemblies(resolvedAssemblies, "while loading patched assemblies");
                if (resolvedAssemblies.TryGetValue(kv.Value.Name.Name, out var origin))
                {
                    AssemblyPatcherDiagnostics.Write(() => RLog.Warning($"Cannot load patched {kv.Key}: {kv.Value.Name.Name} was already loaded ({origin})."));
                    continue;
                }
                Assembly loadedAssembly;

                if (ConfigLoadDumpedAssemblies.Value &&
                    dumpedAssemblyPaths.TryGetValue(kv.Key, out var dumpedAssemblyPath))
                {
                    loadedAssembly = Assembly.LoadFrom(dumpedAssemblyPath);
                }
                else
                {
                    using var assemblyStream = new MemoryStream();
                    kv.Value.Write(assemblyStream);
                    loadedAssembly = assemblyLoader(assemblyStream.ToArray(),
                                                    PatcherContext.AvailableAssembliesPaths[kv.Key]);
                }

                if (loadedAssembly == null)
                    throw new InvalidOperationException("The assembly loader returned null.");
                PatcherContext.LoadedAssemblies.Add(kv.Key, loadedAssembly);
                AssemblyPatcherDiagnostics.Write(() => RLog.Debug($"Loaded '{kv.Value.FullName}' into memory"));
            }
            catch (Exception ex)
            {
                AssemblyPatcherDiagnostics.Write(() => RLog.Error($"Could not load patched {kv.Key}: {ex}"));
            }
        }
    }

    private void TrackAvailableAssemblies()
    {
        foreach (var assembly in PatcherContext.AvailableAssemblies.Values)
            if (assembly != null)
                _ownedAssemblies.Add(assembly);
    }

    private static void CaptureLoadedAssemblies(Dictionary<string, string> resolvedAssemblies, string origin)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (name != null)
                resolvedAssemblies.TryAdd(name, origin);
        }
    }
}
