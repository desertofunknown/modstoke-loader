// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from BepInEx.Preloader.Core/Patching/AssemblyPatcher.cs.
// Copyright (c) BepInEx contributors and Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using Mono.Cecil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class AssemblyPatcherDiscovery
    {
        private static readonly Regex AllowedGuidRegex = new Regex(@"^[a-zA-Z0-9._-]+$");

        internal static void Discover(string directory, PatcherContext context, ManualLogSource log,
            AssemblyPatchingState state, ReaderParameters readerParameters)
        {
            if (!Directory.Exists(directory))
                return;

            string[] files;
            try
            {
                files = Directory.GetFiles(Path.GetFullPath(directory), "*.dll", SearchOption.AllDirectories);
            }
            catch (Exception exception)
            {
                state.DiscoveryFailureCount++;
                AssemblyPatcherDiagnostics.Write(() =>
                    log.LogError($"Failed to enumerate patcher directory [{directory}]: {exception}"));
                return;
            }

            var patcherAssemblyName = typeof(BasePatcher).Assembly.GetName().Name;
            foreach (var file in files)
            {
                var candidates = new List<(string TypeName, PatcherPluginInfoAttribute Metadata)>();
                try
                {
                    using (var assembly = AssemblyDefinition.ReadAssembly(file, readerParameters))
                    {
                        if (assembly.MainModule.AssemblyReferences.All(reference => reference.Name != patcherAssemblyName)
                            && assembly.Name.Name != patcherAssemblyName)
                            continue;
                        // A patcher can reach BasePatcher through a base class in another assembly.
                        foreach (var type in assembly.MainModule.Types)
                        {
                            try
                            {
                                if (type.IsInterface || type.IsAbstract || type.HasGenericParameters)
                                    continue;

                                try
                                {
                                    if (!AssemblyPatcherMetadata.IsPatcher(type))
                                        continue;
                                }
                                catch (AssemblyResolutionException)
                                {
                                    // Upstream skips unrelated types whose base assembly cannot be resolved.
                                    continue;
                                }

                                var metadata = AssemblyPatcherMetadata.ReadInfo(type);
                                if (metadata == null || string.IsNullOrEmpty(metadata.GUID)
                                    || !AllowedGuidRegex.IsMatch(metadata.GUID)
                                    || metadata.Name == null || metadata.Version == null)
                                {
                                    state.DiscoveryFailureCount++;
                                    AssemblyPatcherDiagnostics.Write(() =>
                                        log.LogWarning($"Skipping patcher type [{type.FullName}] in [{file}]: missing or invalid plugin metadata"));
                                    continue;
                                }

                                candidates.Add((type.FullName, metadata));
                            }
                            catch (Exception exception)
                            {
                                state.DiscoveryFailureCount++;
                                AssemblyPatcherDiagnostics.Write(() =>
                                    log.LogWarning($"Failed to discover patcher type [{type.FullName}] in [{file}]: {exception}"));
                            }
                        }
                    }
                }
                catch (BadImageFormatException exception)
                {
                    AssemblyPatcherDiagnostics.Write(() =>
                        log.LogDebug($"Skipping [{file}] because it is not a valid .NET assembly: {exception.Message}"));
                    continue;
                }
                catch (Exception exception)
                {
                    state.DiscoveryFailureCount++;
                    AssemblyPatcherDiagnostics.Write(() =>
                        log.LogError($"Failed to discover patchers in [{file}]: {exception}"));
                    continue;
                }

                if (candidates.Count == 0)
                    continue;

                // Only detached names and identity values survive Cecil disposal.
                Assembly runtimeAssembly;
                try
                {
                    runtimeAssembly = Assembly.LoadFrom(file);
                }
                catch (Exception exception)
                {
                    state.DiscoveryFailureCount++;
                    AssemblyPatcherDiagnostics.Write(() =>
                        log.LogError($"Failed to load patcher assembly [{file}]: {exception}"));
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    var typeName = candidate.TypeName;
                    try
                    {
                        var type = runtimeAssembly.GetType(typeName, true);
                        if (type.IsAbstract || type.ContainsGenericParameters || !typeof(BasePatcher).IsAssignableFrom(type))
                            throw new InvalidOperationException("The loaded type is not a concrete BepInEx patcher.");

                        var metadata = AssemblyPatcherMetadata.ReadInfo(type);
                        if (metadata == null
                            || !string.Equals(metadata.GUID, candidate.Metadata.GUID, StringComparison.Ordinal)
                            || !string.Equals(metadata.Name, candidate.Metadata.Name, StringComparison.Ordinal)
                            || !Equals(metadata.Version, candidate.Metadata.Version))
                            throw new InvalidOperationException("The loaded patcher identity does not match its discovered metadata.");

                        var instance = (BasePatcher)Activator.CreateInstance(type);
                        instance.Context = context;
                        var definitions = new List<PatchDefinition>();

                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            var targetAssemblies = (TargetAssemblyAttribute[])method.GetCustomAttributes(
                                typeof(TargetAssemblyAttribute), inherit: true);
                            var targetTypes = (TargetTypeAttribute[])method.GetCustomAttributes(
                                typeof(TargetTypeAttribute), inherit: true);
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
                                state.SkippedPatchCount++;
                                AssemblyPatcherDiagnostics.Write(() =>
                                    log.LogWarning($"Skipping method [{typeName}/{method}] as it is not a valid patcher method"));
                                continue;
                            }

                            foreach (var targetAssembly in targetAssemblies)
                                definitions.Add(new PatchDefinition(targetAssembly, instance, method));
                            foreach (var targetType in targetTypes)
                                definitions.Add(new PatchDefinition(targetType, instance, method));
                        }

                        // Method discovery must complete before publishing either collection.
                        context.PatcherPlugins.Add(instance);
                        context.PatchDefinitions.AddRange(definitions);
                    }
                    catch (Exception exception)
                    {
                        state.DiscoveryFailureCount++;
                        AssemblyPatcherDiagnostics.Write(() =>
                            log.LogError($"Failed to load patchers from type [{typeName}] in [{file}]: {exception}"));
                    }
                }
            }
        }
    }
}
