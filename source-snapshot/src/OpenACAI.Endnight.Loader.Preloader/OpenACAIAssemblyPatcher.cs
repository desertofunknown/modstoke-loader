// SPDX-License-Identifier: GPL-3.0-or-later
// Patcher contract and execution adapted from BepInEx's AssemblyPatcher.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using Mono.Cecil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class OpenACAIAssemblyPatcher : IDisposable
    {
        private readonly AssemblyPatchingState _state;
        private readonly Func<byte[], string, Assembly> _assemblyLoader;
        private readonly AssemblyPatcherResolver _resolver;
        private readonly AssemblyPatchingOptions _options = new AssemblyPatchingOptions();
        private readonly ManualLogSource _log = Logger.CreateLogSource("OpenACAI AssemblyPatcher");
        private readonly HashSet<AssemblyDefinition> _ownedAssemblies = new HashSet<AssemblyDefinition>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _patched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _loadedOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal OpenACAIAssemblyPatcher(AssemblyPatchingState state, Func<byte[], string, Assembly> assemblyLoader = null)
        {
            _state = state;
            _assemblyLoader = assemblyLoader;
            _resolver = new AssemblyPatcherResolver(_ownedAssemblies);
            Context = new PatcherContext
            {
                DumpedAssembliesPath = Path.Combine(Paths.BepInExRootPath, "DumpedAssemblies", Paths.ProcessName)
            };
        }

        internal PatcherContext Context { get; }

        internal void DiscoverPatchers(string directory) =>
            AssemblyPatcherDiscovery.Discover(directory, Context, _log, _state, _resolver.ReaderParameters);

        internal void ReadAssemblies(string directory) => ReadAssemblyFiles(Directory.EnumerateFiles(directory, "*.dll"));

        internal void ReadAssemblyFiles(IEnumerable<string> files)
        {
            foreach (var path in files)
            {
                AssemblyDefinition assembly;
                try
                {
                    assembly = AssemblyDefinition.ReadAssembly(path, _resolver.ReaderParameters);
                }
                catch (BadImageFormatException)
                {
                    continue;
                }

                _ownedAssemblies.Add(assembly);
                if (assembly.Name.Name == "System" || assembly.Name.Name == "mscorlib")
                    continue;

                var name = Path.GetFileName(path);
                Context.AvailableAssemblies.Add(name, assembly);
                Context.AvailableAssembliesPaths.Add(name, path);
            }
        }

        internal void PatchAndLoad()
        {
            // Pair lifecycle calls even if a plugin changes the context's plugin list.
            var plugins = Context.PatcherPlugins.ToArray();
            CaptureLoadedAssemblies("before patcher initialization");
            try
            {
                foreach (var plugin in plugins)
                    RunLifecycle(plugin, initializing: true);

                var patches = Context.PatchDefinitions.ToArray();
                _state.PatchDefinitionCount = patches.Length;
                AssemblyPatcherDiagnostics.Write(() => _log.LogInfo($"Executing {patches.Length} patches"));
                foreach (var patch in patches)
                {
                    var target = patch.TargetAssembly?.TargetAssembly ?? patch.TargetType.TargetAssembly;
                    var targets = target == TargetAssemblyAttribute.AllAssemblies
                        ? Context.AvailableAssemblies.Keys.ToArray() : new[] { target };
                    foreach (var filename in targets)
                        RunPatch(patch, filename);

                    CaptureLoadedAssemblies(patch.FullName);
                }

                LoadPatchedAssemblies();
            }
            finally
            {
                foreach (var plugin in plugins)
                    RunLifecycle(plugin, initializing: false);
            }
        }

        private void RunLifecycle(BasePatcher plugin, bool initializing)
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
                _state.LifecycleFailureCount++;
                AssemblyPatcherDiagnostics.Write(() =>
                    _log.LogError($"Failed to run {phase} of {plugin.Info.GUID}: {Unwrap(ex)}"));
            }
            finally
            {
                TrackAvailableAssemblies();
                CaptureLoadedAssemblies($"{phase} of {plugin.Info.GUID}");
            }
        }

        private void RunPatch(PatchDefinition patch, string filename)
        {
            if (_invalid.Contains(filename) || !Context.AvailableAssemblies.TryGetValue(filename, out var assembly))
            {
                _state.SkippedPatchCount++;
                return;
            }

            object[] arguments = null;
            var isAssemblyPatch = patch.TargetAssembly != null;
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
                        _state.SkippedPatchCount++;
                        AssemblyPatcherDiagnostics.Write(() =>
                            _log.LogWarning($"Skipping [{patch.FullName}]: type {patch.TargetType.TargetType} was not found in {filename}."));
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
                {
                    var replacement = (AssemblyDefinition)arguments[0]
                        ?? throw new InvalidOperationException("An assembly patch returned a null assembly.");
                    Context.AvailableAssemblies[filename] = replacement;
                }
                _patched.Add(filename);
                _state.AppliedPatchCount++;
            }
            catch (Exception ex)
            {
                _state.PatchFailureCount++;
                _patched.Remove(filename);
                _invalid.Add(filename);
                AssemblyPatcherDiagnostics.Write(() =>
                    _log.LogError($"Failed [{patch.FullName}] on {filename}; its patched output will not be loaded: {Unwrap(ex)}"));
            }
            finally
            {
                if (isAssemblyPatch && arguments?.Length > 0 && arguments[0] is AssemblyDefinition replacement)
                    _ownedAssemblies.Add(replacement);
                TrackAvailableAssemblies();
            }
        }

        private void LoadPatchedAssemblies()
        {
            // Use the live definitions, including initializer additions and ref-argument replacements.
            var assemblies = Context.AvailableAssemblies.Where(pair => _patched.Contains(pair.Key)).ToArray();
            var dumped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_options.DumpAssemblies)
            {
                foreach (var pair in assemblies)
                {
                    try
                    {
                        Directory.CreateDirectory(Context.DumpedAssembliesPath);
                        string path;
                        using (var output = OpenDumpFile(pair.Key))
                        {
                            pair.Value.Write(output);
                            path = output.Name;
                        }
                        dumped.Add(pair.Key, path);
                    }
                    catch (Exception ex)
                    {
                        _state.DumpFailureCount++;
                        AssemblyPatcherDiagnostics.Write(() =>
                            _log.LogError($"Could not dump {pair.Key}; loading from memory instead: {ex}"));
                    }
                }
            }

            if (_options.BreakBeforeLoad)
            {
                AssemblyPatcherDiagnostics.Write(() =>
                    _log.LogInfo("OpenACAI is about to load patched assemblies: " + string.Join(", ", assemblies.Select(pair => pair.Key))));
                if (dumped.Count != 0)
                    AssemblyPatcherDiagnostics.Write(() =>
                        _log.LogInfo($"Patched assemblies were dumped into {Context.DumpedAssembliesPath}."));
                Debugger.Break();
            }

            foreach (var pair in assemblies)
            {
                try
                {
                    CaptureLoadedAssemblies("while loading patched assemblies");
                    if (_loadedOrigins.TryGetValue(pair.Value.Name.Name, out var origin))
                    {
                        _state.EarlyLoadedAssemblyCount++;
                        AssemblyPatcherDiagnostics.Write(() =>
                            _log.LogWarning($"Cannot load patched {pair.Key}: {pair.Value.Name.Name} was already loaded ({origin})."));
                        continue;
                    }

                    Assembly loaded;
                    if (_options.LoadDumpedAssemblies && dumped.TryGetValue(pair.Key, out var path))
                        loaded = _assemblyLoader == null
                            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
                            : Assembly.LoadFrom(path);
                    else
                    {
                        using var stream = new MemoryStream();
                        pair.Value.Write(stream);
                        stream.Position = 0;
                        // Legacy callers own their loading route; the normal path keeps default-context identity.
                        loaded = _assemblyLoader == null
                            ? AssemblyLoadContext.Default.LoadFromStream(stream)
                            : _assemblyLoader(stream.ToArray(), Context.AvailableAssembliesPaths[pair.Key]);
                    }
                    Context.LoadedAssemblies.Add(pair.Key, loaded);
                    _state.LoadedAssemblyCount = Context.LoadedAssemblies.Count;
                    AssemblyPatcherDiagnostics.Write(() => _log.LogDebug($"Loaded patched assembly {loaded.FullName}"));
                }
                catch (Exception ex)
                {
                    _state.AssemblyLoadFailureCount++;
                    AssemblyPatcherDiagnostics.Write(() => _log.LogError($"Could not load patched {pair.Key}: {ex}"));
                }
            }
        }

        private FileStream OpenDumpFile(string filename)
        {
            if (filename != Path.GetFileName(filename) || filename == "." || filename == "..")
                throw new InvalidOperationException($"Patcher context contains an invalid dump filename: {filename}");

            var stem = Path.GetFileNameWithoutExtension(filename);
            var extension = Path.GetExtension(filename);
            for (var attempt = 0; ; attempt++)
            {
                var suffix = attempt == 0 ? "" : "_" + attempt;
                var path = Path.Combine(Context.DumpedAssembliesPath, stem + suffix + extension);
                try
                {
                    return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                }
                catch (IOException ex) when (attempt < 15 && ((ex.HResult & 0xFFFF) == 32 || (ex.HResult & 0xFFFF) == 33))
                {
                    // Only a sharing/lock violation merits another filename, never permission or disk errors.
                }
            }
        }

        private void TrackAvailableAssemblies()
        {
            foreach (var assembly in Context.AvailableAssemblies.Values)
                if (assembly != null)
                    _ownedAssemblies.Add(assembly);
        }

        private void CaptureLoadedAssemblies(string origin)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name != null)
                    _loadedOrigins.TryAdd(name, origin);
            }
        }

        private static Exception Unwrap(Exception exception) =>
            exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : exception;

        public void Dispose()
        {
            try
            {
                TrackAvailableAssemblies();
                foreach (var assembly in _ownedAssemblies)
                {
                    try
                    {
                        assembly.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _state.CleanupFailureCount++;
                        AssemblyPatcherDiagnostics.Write(() =>
                            _log.LogWarning("Could not dispose a patcher assembly definition: " + ex));
                    }
                }
                _ownedAssemblies.Clear();
                Context.AvailableAssemblies.Clear();
                Context.AvailableAssembliesPaths.Clear();
                Context.PatchDefinitions.Clear();
                Context.PatcherPlugins.Clear();
            }
            finally
            {
                try { _resolver.Dispose(); }
                catch (Exception ex)
                {
                    _state.CleanupFailureCount++;
                    AssemblyPatcherDiagnostics.Write(() =>
                        _log.LogWarning("Could not dispose the patcher assembly resolver: " + ex));
                }
                finally
                {
                    try { Logger.Sources.Remove(_log); }
                    catch (Exception ex)
                    {
                        _state.CleanupFailureCount++;
                        AssemblyPatcherDiagnostics.Write(() =>
                            _log.LogWarning("Could not remove the patcher log source: " + ex));
                    }
                    finally
                    {
                        _log.Dispose();
                    }
                }
            }
        }
    }
}
