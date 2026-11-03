// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BepInEx;

namespace Modstoke.Loader
{
    public static partial class BepInExPluginCompatibility
    {
        private static readonly string[] UnityLifecycleMethodNames =
        {
            "Awake",
            "Start",
            "OnEnable",
            "OnDisable",
            "OnDestroy",
            "Update",
            "LateUpdate",
            "FixedUpdate",
            "OnGUI",
            "OnApplicationQuit"
        };

        private static readonly HashSet<string> UnityLifecycleMethodNameLookup =
            new HashSet<string>(UnityLifecycleMethodNames, StringComparer.Ordinal);

        private static IEnumerable<BepInExPluginCompatibilityCandidateSnapshot> CreateInventoryCandidates(
            ModInventorySnapshot inventory, string generatedUtc)
        {
            var sequenceNumber = 1;
            foreach (var entry in inventory.LegacyBepInExPlugins
                .Where(entry => entry != null && !entry.IsLibrary && !entry.IsOpenAcai && !entry.IsLegacyRedLoader)
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssemblyPath ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var pluginTypes = new List<BepInExPluginAssemblyMetadata>();
                var assembly = InspectAssemblyMetadata(entry.AssemblyPath, pluginTypes, allowModuleInitialization: true);
                if (pluginTypes.Count == 0)
                    pluginTypes.Add(assembly);

                foreach (var metadata in pluginTypes)
                    yield return CreateCandidate(sequenceNumber++, entry, metadata, generatedUtc);
            }
        }

        private static BepInExPluginCompatibilityCandidateSnapshot CreateCandidate(
            int sequenceNumber,
            ModInfo entry,
            BepInExPluginAssemblyMetadata metadata,
            string generatedUtc)
        {
            var assemblyPath = entry == null ? "" : entry.AssemblyPath;
            var assemblyExists = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath);
            var status = "CHECK";
            var issueCode = "not-validated";
            var detail = "BepInEx plugin compatibility metadata has not been validated.";
            var compatibilityReady = false;

            if (entry == null)
            {
                issueCode = "missing-inventory-entry";
                detail = "BepInEx plugin compatibility candidate did not include an inventory entry.";
            }
            else if (!entry.Enabled)
            {
                status = "INFO";
                issueCode = "disabled-plugin";
                detail = "The BepInEx plugin candidate is disabled and will not be planned by Modstoke.";
            }
            else if (!entry.HasAssembly || !assemblyExists)
            {
                issueCode = "missing-assembly";
                detail = "The BepInEx plugin candidate does not have a loadable assembly file.";
            }
            else if (!metadata.Readable)
            {
                issueCode = metadata.IssueCode;
                detail = metadata.Detail;
            }
            else if (metadata.PluginAttributeCount == 0)
            {
                issueCode = "bepinex-plugin-attribute-missing";
                detail = "Assembly metadata does not contain a BepInEx.BepInPlugin attribute on a plugin type.";
            }
            else if (string.IsNullOrWhiteSpace(metadata.PluginGuid)
                || string.IsNullOrWhiteSpace(metadata.PluginName)
                || string.IsNullOrWhiteSpace(metadata.PluginVersion))
            {
                issueCode = "bepinex-plugin-metadata-incomplete";
                detail = "BepInEx plugin metadata was found, but guid, name, or version was missing.";
            }
            else if (!BepInExPluginMetadata.TryCreateIdentity(
                metadata.PluginGuid, metadata.PluginName, metadata.PluginVersion, out _))
            {
                issueCode = "bepinex-plugin-metadata-invalid";
                detail = "The BepInEx plugin GUID or version is invalid under the compatibility host's metadata rules.";
            }
            else if (metadata.UnresolvedAssemblyReferenceCount > 0)
            {
                issueCode = "unresolved-assembly-references";
                detail = metadata.UnresolvedAssemblyReferenceCount + " assembly reference(s) could not be resolved by the Modstoke BepInEx compatibility dependency pass.";
            }
            else if (metadata.ExecutionPlan == null || !metadata.ExecutionPlan.Ready)
            {
                issueCode = string.IsNullOrWhiteSpace(metadata.ExecutionPlan?.IssueCode)
                    ? "bepinex-plugin-execution-plan-not-ready"
                    : metadata.ExecutionPlan.IssueCode;
                detail = string.IsNullOrWhiteSpace(metadata.ExecutionPlan?.Detail)
                    ? "BepInEx plugin execution-plan metadata is not ready."
                    : metadata.ExecutionPlan.Detail;
            }
            else
            {
                compatibilityReady = true;
                status = "READY";
                issueCode = "";
                detail = "BepInEx plugin metadata is valid; the BepInEx host loads this plugin with Assembly.LoadFrom.";
            }

            return new BepInExPluginCompatibilityCandidateSnapshot(
                sequenceNumber,
                entry == null ? "" : NormalizeId(entry.Id),
                string.IsNullOrWhiteSpace(metadata.PluginName) ? entry?.DisplayName ?? "" : metadata.PluginName,
                string.IsNullOrWhiteSpace(metadata.AssemblyName) ? Path.GetFileNameWithoutExtension(assemblyPath) : metadata.AssemblyName,
                assemblyPath,
                entry == null ? "" : entry.PackagePath,
                CandidateSourceInventory,
                false,
                string.IsNullOrWhiteSpace(metadata.AssemblyVersion) && entry != null ? entry.AssemblyVersion : metadata.AssemblyVersion,
                entry == null ? 0 : entry.AssemblySize ?? 0,
                entry != null && entry.Enabled,
                entry == null ? "" : entry.Status,
                entry == null ? "" : entry.IssueCode,
                assemblyExists,
                metadata.Readable,
                metadata.AssemblyReferences,
                metadata.AssemblyReferenceCount,
                metadata.ResolvedAssemblyReferenceCount,
                metadata.UnresolvedAssemblyReferenceCount,
                metadata.PluginAttributeCount,
                metadata.PluginAttributeCount > 0,
                metadata.PluginGuid,
                metadata.PluginName,
                metadata.PluginVersion,
                metadata.PluginTypeName,
                metadata.ExecutionPlan,
                compatibilityReady,
                RuntimePluginLoadingEnabled,
                compatibilityReady,
                compatibilityReady,
                false,
                IsLoadedByOpenAcai(metadata, assemblyPath),
                status,
                issueCode,
                detail,
                generatedUtc);
        }

        private static IEnumerable<BepInExPluginCompatibilityCandidateSnapshot> CreateCandidateHarnesses(
            int firstSequenceNumber,
            string candidateHarnessRoot,
            string pluginRoot,
            string generatedUtc)
        {
            if (string.IsNullOrWhiteSpace(candidateHarnessRoot) || !Directory.Exists(candidateHarnessRoot))
            {
                yield break;
            }

            var remaining = Math.Max(0, MaxCandidateCount - Math.Max(0, firstSequenceNumber - 1));
            var sequenceNumber = Math.Max(1, firstSequenceNumber);
            foreach (var assemblyPath in Directory.EnumerateFiles(candidateHarnessRoot, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .Take(remaining))
            {
                yield return CreateCandidateHarness(sequenceNumber, assemblyPath, pluginRoot, generatedUtc);
                sequenceNumber++;
            }
        }

        private static BepInExPluginCompatibilityCandidateSnapshot CreateCandidateHarness(
            int sequenceNumber,
            string assemblyPath,
            string pluginRoot,
            string generatedUtc)
        {
            var metadata = InspectAssemblyMetadata(assemblyPath);
            var assemblyExists = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath);
            var locatedOutsidePluginRoot = !IsPathUnderRoot(assemblyPath, pluginRoot);
            var status = "CHECK";
            var issueCode = "not-validated";
            var detail = "BepInEx candidate validation probe has not been validated.";
            var compatibilityReady = false;

            if (!assemblyExists)
            {
                issueCode = "missing-assembly";
                detail = "Candidate validation probe assembly file was not found.";
            }
            else if (!locatedOutsidePluginRoot)
            {
                issueCode = "unsafe-candidate-harness-location";
                detail = "Candidate validation probe assembly is inside BepInEx/plugins and could be loaded by the BepInEx host.";
            }
            else if (!metadata.Readable)
            {
                issueCode = metadata.IssueCode;
                detail = metadata.Detail;
            }
            else if (metadata.PluginAttributeCount == 0)
            {
                issueCode = "bepinex-plugin-attribute-missing";
                detail = "Candidate validation probe metadata does not contain a BepInEx.BepInPlugin attribute on a plugin type.";
            }
            else if (string.IsNullOrWhiteSpace(metadata.PluginGuid)
                || string.IsNullOrWhiteSpace(metadata.PluginName)
                || string.IsNullOrWhiteSpace(metadata.PluginVersion))
            {
                issueCode = "bepinex-plugin-metadata-incomplete";
                detail = "Candidate validation probe BepInPlugin metadata was found, but guid, name, or version was missing.";
            }
            else if (metadata.UnresolvedAssemblyReferenceCount > 0)
            {
                issueCode = "unresolved-assembly-references";
                detail = metadata.UnresolvedAssemblyReferenceCount + " candidate validation probe assembly reference(s) could not be resolved by the Modstoke BepInEx compatibility dependency pass.";
            }
            else if (metadata.ExecutionPlan != null && metadata.ExecutionPlan.Issue)
            {
                issueCode = string.IsNullOrWhiteSpace(metadata.ExecutionPlan.IssueCode)
                    ? "candidate-harness-execution-plan-not-ready"
                    : metadata.ExecutionPlan.IssueCode;
                detail = "Candidate validation probe BepInEx execution plan is not ready: " + metadata.ExecutionPlan.Detail;
            }
            else
            {
                compatibilityReady = true;
                status = "READY";
                issueCode = "";
                detail = "Modstoke validated this probe's BepInEx metadata; the BepInEx host never loads validation probes.";
            }

            var assemblyName = string.IsNullOrWhiteSpace(metadata.AssemblyName)
                ? Path.GetFileNameWithoutExtension(assemblyPath)
                : metadata.AssemblyName;
            var pluginId = string.IsNullOrWhiteSpace(metadata.PluginGuid)
                ? "openacai.validation.bepinex.candidate." + NormalizeId(Path.GetFileNameWithoutExtension(assemblyPath))
                : metadata.PluginGuid;
            var displayName = string.IsNullOrWhiteSpace(metadata.PluginName)
                ? assemblyName
                : metadata.PluginName;

            return new BepInExPluginCompatibilityCandidateSnapshot(
                sequenceNumber,
                pluginId,
                displayName,
                assemblyName,
                assemblyPath,
                Path.GetDirectoryName(assemblyPath) ?? "",
                CandidateSourceValidationProbe,
                true,
                metadata.AssemblyVersion,
                GetFileSize(assemblyPath),
                true,
                CandidateSourceValidationProbe,
                "",
                assemblyExists,
                metadata.Readable,
                metadata.AssemblyReferences,
                metadata.AssemblyReferenceCount,
                metadata.ResolvedAssemblyReferenceCount,
                metadata.UnresolvedAssemblyReferenceCount,
                metadata.PluginAttributeCount,
                metadata.PluginAttributeCount > 0,
                metadata.PluginGuid,
                metadata.PluginName,
                metadata.PluginVersion,
                metadata.PluginTypeName,
                metadata.ExecutionPlan,
                compatibilityReady,
                RuntimePluginLoadingEnabled,
                compatibilityReady,
                compatibilityReady,
                false,
                IsLoadedByOpenAcai(metadata, assemblyPath),
                status,
                issueCode,
                detail,
                generatedUtc);
        }

        private static IReadOnlyList<BepInExPluginMetadataHarnessSnapshot> CreateParserHarnesses(
            string parserHarnessRoot,
            string pluginRoot,
            string generatedUtc)
        {
            if (string.IsNullOrWhiteSpace(parserHarnessRoot) || !Directory.Exists(parserHarnessRoot))
            {
                return Array.Empty<BepInExPluginMetadataHarnessSnapshot>();
            }

            return Directory.EnumerateFiles(parserHarnessRoot, "*.dll", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxParserHarnessCount)
                .Select((assemblyPath, index) => CreateParserHarness(index + 1, assemblyPath, pluginRoot, generatedUtc))
                .ToArray();
        }

        private static BepInExPluginMetadataHarnessSnapshot CreateParserHarness(
            int sequenceNumber,
            string assemblyPath,
            string pluginRoot,
            string generatedUtc)
        {
            var metadata = InspectAssemblyMetadata(assemblyPath);
            var assemblyExists = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath);
            var locatedOutsidePluginRoot = !IsPathUnderRoot(assemblyPath, pluginRoot);
            var status = "CHECK";
            var issueCode = "not-validated";
            var detail = "BepInEx plugin metadata parser harness has not been validated.";
            var positiveMetadataParserVerified = false;

            if (!assemblyExists)
            {
                issueCode = "missing-assembly";
                detail = "Parser harness assembly file was not found.";
            }
            else if (!locatedOutsidePluginRoot)
            {
                issueCode = "unsafe-harness-location";
                detail = "Parser harness assembly is inside BepInEx/plugins and could be loaded by the BepInEx host.";
            }
            else if (!metadata.Readable)
            {
                issueCode = metadata.IssueCode;
                detail = metadata.Detail;
            }
            else if (metadata.UnresolvedAssemblyReferenceCount > 0)
            {
                issueCode = "unresolved-assembly-references";
                detail = metadata.UnresolvedAssemblyReferenceCount + " parser harness assembly reference(s) could not be resolved by the Modstoke BepInEx compatibility dependency pass.";
            }
            else if (metadata.PluginAttributeCount == 0)
            {
                issueCode = "bepinex-plugin-attribute-missing";
                detail = "Parser harness assembly metadata does not contain a BepInEx.BepInPlugin attribute.";
            }
            else if (string.IsNullOrWhiteSpace(metadata.PluginGuid)
                || string.IsNullOrWhiteSpace(metadata.PluginName)
                || string.IsNullOrWhiteSpace(metadata.PluginVersion))
            {
                issueCode = "bepinex-plugin-metadata-incomplete";
                detail = "Parser harness BepInPlugin metadata was found, but guid, name, or version was missing.";
            }
            else if (metadata.ExecutionPlan != null && metadata.ExecutionPlan.Issue)
            {
                issueCode = string.IsNullOrWhiteSpace(metadata.ExecutionPlan.IssueCode)
                    ? "parser-harness-execution-plan-not-ready"
                    : metadata.ExecutionPlan.IssueCode;
                detail = "Parser harness BepInEx execution plan is not ready: " + metadata.ExecutionPlan.Detail;
            }
            else
            {
                status = "READY";
                issueCode = "";
                positiveMetadataParserVerified = true;
                detail = "Modstoke parsed valid BepInPlugin metadata from a harness assembly outside BepInEx/plugins without loading it.";
            }

            return new BepInExPluginMetadataHarnessSnapshot(
                sequenceNumber,
                Path.GetFileNameWithoutExtension(assemblyPath),
                string.IsNullOrWhiteSpace(metadata.AssemblyName) ? Path.GetFileNameWithoutExtension(assemblyPath) : metadata.AssemblyName,
                assemblyPath,
                metadata.AssemblyVersion,
                GetFileSize(assemblyPath),
                assemblyExists,
                metadata.Readable,
                metadata.AssemblyReferences,
                metadata.AssemblyReferenceCount,
                metadata.ResolvedAssemblyReferenceCount,
                metadata.UnresolvedAssemblyReferenceCount,
                metadata.PluginAttributeCount,
                metadata.PluginAttributeCount > 0,
                metadata.PluginGuid,
                metadata.PluginName,
                metadata.PluginVersion,
                metadata.PluginTypeName,
                metadata.ExecutionPlan,
                locatedOutsidePluginRoot,
                positiveMetadataParserVerified,
                status,
                issueCode,
                detail,
                generatedUtc);
        }

        private static BepInExPluginAssemblyMetadata InspectAssemblyMetadata(
            string assemblyPath, ICollection<BepInExPluginAssemblyMetadata> pluginTypes = null,
            bool allowModuleInitialization = false)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return BepInExPluginAssemblyMetadata.Missing("missing-assembly", "No assembly path is available for BepInEx plugin metadata validation.");
            }

            if (!File.Exists(assemblyPath))
            {
                return BepInExPluginAssemblyMetadata.Missing("missing-assembly", "Assembly file was not found for BepInEx plugin metadata validation.");
            }

            try
            {
                using (var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var peReader = new PEReader(stream))
                {
                    if (!peReader.HasMetadata)
                    {
                        return BepInExPluginAssemblyMetadata.Missing("invalid-assembly-metadata", "Assembly file does not contain managed metadata.");
                    }

                    var reader = peReader.GetMetadataReader();
                    var assemblyName = "";
                    var assemblyVersion = "";
                    var assemblyReferences = ReadAssemblyReferences(reader, Path.GetDirectoryName(assemblyPath));
                    if (reader.IsAssembly)
                    {
                        var definition = reader.GetAssemblyDefinition();
                        assemblyName = reader.GetString(definition.Name);
                        assemblyVersion = definition.Version == null ? "" : definition.Version.ToString();
                    }

                    var plugins = new List<BepInExPluginAssemblyMetadata>();
                    var moduleInitializerCount = CountModuleInitializerMethods(reader);

                    foreach (var typeHandle in reader.TypeDefinitions)
                    {
                        var typeDefinition = reader.GetTypeDefinition(typeHandle);
                        var typeName = ReadTypeDefinitionFullName(reader, typeDefinition);
                        var pluginAttributeCount = 0;
                        var pluginGuid = "";
                        var pluginName = "";
                        var pluginVersion = "";
                        var selectedPluginMetadataComplete = false;
                        foreach (var attributeHandle in typeDefinition.GetCustomAttributes())
                        {
                            var attribute = reader.GetCustomAttribute(attributeHandle);
                            var attributeTypeName = ReadAttributeTypeName(reader, attribute.Constructor);
                            if (!IsBepInExPluginAttribute(attributeTypeName))
                            {
                                continue;
                            }

                            pluginAttributeCount++;
                            var attributeReadable = TryReadBepInExPluginAttribute(reader, attribute, out var guid, out var name, out var version);
                            var metadataComplete = attributeReadable
                                && !string.IsNullOrWhiteSpace(guid)
                                && !string.IsNullOrWhiteSpace(name)
                                && !string.IsNullOrWhiteSpace(version);
                            if (pluginAttributeCount == 1 || (!selectedPluginMetadataComplete && metadataComplete))
                            {
                                pluginGuid = attributeReadable ? guid : "";
                                pluginName = attributeReadable ? name : "";
                                pluginVersion = attributeReadable ? version : "";
                                selectedPluginMetadataComplete = metadataComplete;
                            }
                        }

                        if (pluginAttributeCount == 0)
                            continue;

                        BepInExPluginExecutionPlanSnapshot executionPlan;
                        try
                        {
                            executionPlan = CreateExecutionPlan(reader, typeHandle, typeName,
                                Path.GetDirectoryName(assemblyPath), moduleInitializerCount, allowModuleInitialization);
                        }
                        catch (Exception ex)
                        {
                            executionPlan = BepInExPluginExecutionPlanSnapshot.CreateUnavailable(
                                typeName, "bepinex-plugin-execution-plan-error", "Plugin metadata could not be inspected: " + ex.Message);
                        }

                        plugins.Add(new BepInExPluginAssemblyMetadata(
                            true, "", "Plugin metadata was read successfully.", assemblyName, assemblyVersion,
                            assemblyReferences, pluginAttributeCount, pluginGuid, pluginName, pluginVersion, typeName, executionPlan));
                    }

                    // Keep the assembly summary for the existing metadata-only probe surfaces.
                    var selected = plugins.FirstOrDefault(plugin => !string.IsNullOrWhiteSpace(plugin.PluginGuid)
                        && !string.IsNullOrWhiteSpace(plugin.PluginName) && !string.IsNullOrWhiteSpace(plugin.PluginVersion))
                        ?? plugins.FirstOrDefault();
                    var summary = new BepInExPluginAssemblyMetadata(
                        true,
                        "",
                        "Assembly metadata was read successfully.",
                        assemblyName,
                        assemblyVersion,
                        assemblyReferences,
                        plugins.Sum(plugin => plugin.PluginAttributeCount),
                        selected?.PluginGuid,
                        selected?.PluginName,
                        selected?.PluginVersion,
                        selected?.PluginTypeName,
                        selected?.ExecutionPlan);
                    if (pluginTypes != null)
                    {
                        foreach (var plugin in plugins)
                            pluginTypes.Add(plugin);
                    }
                    return summary;
                }
            }
            catch (BadImageFormatException ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("invalid-assembly-image", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (IOException ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("assembly-metadata-io", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("assembly-metadata-access", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (Exception ex)
            {
                return BepInExPluginAssemblyMetadata.Missing("assembly-metadata-error", "Assembly metadata could not be read: " + ex.Message);
            }
        }

        private static BepInExPluginExecutionPlanSnapshot CreateExecutionPlan(
            MetadataReader reader,
            TypeDefinitionHandle pluginTypeHandle,
            string pluginTypeName,
            string requestingDirectory,
            int moduleInitializerCount,
            bool allowModuleInitialization)
        {
            if (reader == null || pluginTypeHandle.IsNil)
            {
                return BepInExPluginExecutionPlanSnapshot.CreateUnavailable(
                    pluginTypeName,
                    "bepinex-plugin-type-unavailable",
                    "No BepInEx plugin type was available for execution-plan validation.");
            }

            var typeDefinition = reader.GetTypeDefinition(pluginTypeHandle);
            var normalizedTypeName = string.IsNullOrWhiteSpace(pluginTypeName)
                ? ReadTypeDefinitionFullName(reader, typeDefinition)
                : pluginTypeName;
            var typePublic = IsPublicType(typeDefinition.Attributes);
            var typeAbstract = (typeDefinition.Attributes & TypeAttributes.Abstract) == TypeAttributes.Abstract;
            var typeOpenGeneric = typeDefinition.GetGenericParameters().Count > 0;
            var baseTypeName = ReadTypeName(reader, typeDefinition.BaseType);
            var basePluginChain = FindBaseTypeInDefinitionChain(reader, typeDefinition, requestingDirectory, IsBepInExBasePluginTypeName);
            var baseUnityPluginChain = FindBaseTypeInDefinitionChain(reader, typeDefinition, requestingDirectory, IsBepInExBaseUnityPluginTypeName);
            var basePluginReferenceFound = basePluginChain.Found;
            var baseUnityPluginReferenceFound = baseUnityPluginChain.Found;
            var pluginBaseKind = basePluginReferenceFound
                ? "base-plugin"
                : baseUnityPluginReferenceFound ? "base-unity-plugin" : "unknown";
            var selectedBaseChain = basePluginReferenceFound
                ? basePluginChain
                : baseUnityPluginReferenceFound ? baseUnityPluginChain : BepInExBaseChainMetadata.Missing("");
            var constructorFound = false;
            var parameterlessConstructorFound = false;
            var publicParameterlessConstructorFound = false;
            var staticConstructorFound = false;
            var moduleInitializerFound = moduleInitializerCount > 0;
            var methods = new List<BepInExPluginMethodMetadata>();

            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var methodDefinition = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(methodDefinition.Name);
                var parameterCount = CountMethodParameters(reader, methodDefinition);
                var isPublic = (methodDefinition.Attributes & MethodAttributes.Public) == MethodAttributes.Public;
                var isStatic = (methodDefinition.Attributes & MethodAttributes.Static) == MethodAttributes.Static;
                if (string.Equals(methodName, ".ctor", StringComparison.Ordinal))
                {
                    constructorFound = true;
                    if (parameterCount == 0)
                    {
                        parameterlessConstructorFound = true;
                        if (isPublic)
                        {
                            publicParameterlessConstructorFound = true;
                        }
                    }

                    continue;
                }

                if (string.Equals(methodName, ".cctor", StringComparison.Ordinal))
                {
                    staticConstructorFound = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(methodName) || methodName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                methods.Add(new BepInExPluginMethodMetadata(methodName, isStatic, isPublic, parameterCount));
            }

            var lifecycleMethods = CreateLifecycleMethodPlans(methods, pluginBaseKind);
            var requiredEntrypointMethodName = string.Equals(pluginBaseKind, "base-plugin", StringComparison.Ordinal)
                ? "Load"
                : "";
            var requiredEntrypointMethodFound = string.IsNullOrWhiteSpace(requiredEntrypointMethodName)
                || lifecycleMethods.Any(method => method != null
                    && string.Equals(method.MethodName, requiredEntrypointMethodName, StringComparison.Ordinal)
                    && method.MethodFound);
            var lifecycleIssueCount = lifecycleMethods.Count(method => method != null && method.Issue);
            var ready = false;
            var status = "CHECK";
            var issueCode = "bepinex-plugin-execution-plan-not-ready";
            var detail = "BepInEx plugin execution-plan metadata is not ready.";

            if (string.IsNullOrWhiteSpace(normalizedTypeName))
            {
                issueCode = "bepinex-plugin-type-missing";
                detail = "BepInEx plugin execution plan could not identify the plugin type.";
            }
            else if (typeAbstract)
            {
                issueCode = "bepinex-plugin-type-abstract";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' is abstract and cannot be instantiated by the compatibility load path.";
            }
            else if (typeOpenGeneric)
            {
                issueCode = "bepinex-plugin-type-open-generic";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' has unbound generic parameters and cannot be instantiated by the compatibility load path.";
            }
            else if (!basePluginReferenceFound && !baseUnityPluginReferenceFound)
            {
                issueCode = "bepinex-plugin-base-type-unrecognized";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' does not expose a metadata-visible BasePlugin or BaseUnityPlugin inheritance path.";
            }
            else if (!basePluginReferenceFound)
            {
                issueCode = "bepinex-plugin-base-type-unsupported";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' derives from BaseUnityPlugin; the IL2CPP compatibility host requires BasePlugin.";
            }
            else if (!parameterlessConstructorFound)
            {
                issueCode = "bepinex-plugin-parameterless-constructor-missing";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' does not expose a parameterless constructor in metadata.";
            }
            else if (basePluginReferenceFound && !publicParameterlessConstructorFound)
            {
                issueCode = "bepinex-plugin-parameterless-constructor-not-public";
                detail = "BepInEx plugin type '" + normalizedTypeName + "' does not expose the public parameterless constructor used by the compatibility host.";
            }
            else if (moduleInitializerFound && (!allowModuleInitialization || !basePluginReferenceFound))
            {
                issueCode = "bepinex-plugin-module-initializer-present";
                detail = "BepInEx plugin assembly declares module initialization; this metadata/validation path does not permit its execution.";
            }
            else if (lifecycleIssueCount > 0)
            {
                issueCode = "bepinex-plugin-lifecycle-method-issue";
                detail = lifecycleIssueCount + " BepInEx plugin lifecycle/entrypoint method issue(s) were found on '" + normalizedTypeName + "'.";
            }
            else
            {
                ready = true;
                status = "READY";
                issueCode = "";
                detail = "BepInEx plugin execution-plan metadata is ready for the BepInEx host.";
            }

            return new BepInExPluginExecutionPlanSnapshot(
                normalizedTypeName,
                true,
                typePublic,
                typeAbstract,
                baseTypeName,
                pluginBaseKind,
                basePluginReferenceFound,
                baseUnityPluginReferenceFound,
                selectedBaseChain.MatchTypeName,
                selectedBaseChain.MatchDepth,
                selectedBaseChain.ExternalAssemblyResolved,
                selectedBaseChain.ExternalAssemblyName,
                selectedBaseChain.ExternalAssemblyPath,
                selectedBaseChain.ExternalAssemblySource,
                selectedBaseChain.Detail,
                constructorFound,
                parameterlessConstructorFound,
                publicParameterlessConstructorFound,
                staticConstructorFound,
                moduleInitializerFound,
                moduleInitializerCount,
                requiredEntrypointMethodName,
                requiredEntrypointMethodFound,
                lifecycleMethods,
                ready,
                RuntimePluginLoadingEnabled,
                ready,
                false,
                status,
                issueCode,
                detail);
        }

        private static int CountModuleInitializerMethods(MetadataReader reader)
        {
            if (reader == null)
            {
                return 0;
            }

            var count = 0;
            var moduleConstructorFound = false;
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeDefinition = reader.GetTypeDefinition(typeHandle);
                var isModule = reader.GetString(typeDefinition.Name) == "<Module>";
                foreach (var methodHandle in typeDefinition.GetMethods())
                {
                    var methodDefinition = reader.GetMethodDefinition(methodHandle);
                    if (isModule && reader.GetString(methodDefinition.Name) == ".cctor"
                        && (methodDefinition.Attributes & MethodAttributes.Static) != 0)
                        moduleConstructorFound = true;
                    foreach (var attributeHandle in methodDefinition.GetCustomAttributes())
                    {
                        var attribute = reader.GetCustomAttribute(attributeHandle);
                        if (IsModuleInitializerAttribute(ReadAttributeTypeName(reader, attribute.Constructor)))
                        {
                            count++;
                            break;
                        }
                    }
                }
            }

            // Attribute counts describe source methods; a module constructor can exist without them.
            return Math.Max(count, moduleConstructorFound ? 1 : 0);
        }

        private static IReadOnlyList<BepInExPluginLifecycleMethodSnapshot> CreateLifecycleMethodPlans(
            IReadOnlyList<BepInExPluginMethodMetadata> methods,
            string pluginBaseKind)
        {
            var plans = new List<BepInExPluginLifecycleMethodSnapshot>();
            if (string.Equals(pluginBaseKind, "base-plugin", StringComparison.Ordinal))
            {
                // The host calls the BasePlugin virtual contract, not a method declared by name.
                plans.Add(new BepInExPluginLifecycleMethodSnapshot(
                    "Load", "entrypoint", true, false, true, 0, true, "READY", "",
                    "BasePlugin.Load() is the virtual entrypoint, including inherited overrides. "
                    + "The runtime validates the concrete implementation when loading the plugin type."));
                return plans;
            }

            foreach (var methodName in UnityLifecycleMethodNames)
            {
                if (plans.Count >= MaxLifecycleMethodCount)
                {
                    break;
                }

                var method = SelectBestMethod(methods, methodName);
                if (method == null)
                {
                    continue;
                }

                plans.Add(CreateLifecycleMethodPlan(method, methodName, "unity-lifecycle", false));
            }

            return plans;
        }

        private static BepInExPluginLifecycleMethodSnapshot CreateLifecycleMethodPlan(
            BepInExPluginMethodMetadata method,
            string methodName,
            string methodKind,
            bool requirePublic)
        {
            var status = "CHECK";
            var issueCode = "bepinex-plugin-lifecycle-method-not-ready";
            var detail = "BepInEx plugin lifecycle/entrypoint method metadata is not ready.";
            var ready = false;

            if (method == null)
            {
                issueCode = "bepinex-plugin-required-entrypoint-missing";
                detail = "Required BepInEx plugin entrypoint method '" + methodName + "' was not found.";
                return new BepInExPluginLifecycleMethodSnapshot(methodName, methodKind, false, false, false, 0, false, status, issueCode, detail);
            }

            if (method.IsStatic)
            {
                issueCode = "bepinex-plugin-lifecycle-method-static";
                detail = "BepInEx plugin lifecycle/entrypoint method '" + methodName + "' must be an instance method.";
            }
            else if (method.ParameterCount > 0)
            {
                issueCode = "bepinex-plugin-lifecycle-method-parameters";
                detail = "BepInEx plugin lifecycle/entrypoint method '" + methodName + "' must not require parameters in the current metadata gate.";
            }
            else if (requirePublic && !method.IsPublic)
            {
                issueCode = "bepinex-plugin-entrypoint-method-not-public";
                detail = "BepInEx BasePlugin entrypoint method '" + methodName + "' must be public.";
            }
            else
            {
                ready = true;
                status = "READY";
                issueCode = "";
                detail = "BepInEx plugin lifecycle/entrypoint method '" + methodName + "' metadata is ready.";
            }

            return new BepInExPluginLifecycleMethodSnapshot(
                method.MethodName,
                methodKind,
                true,
                method.IsStatic,
                method.IsPublic,
                method.ParameterCount,
                ready,
                status,
                issueCode,
                detail);
        }

        private static BepInExPluginMethodMetadata SelectBestMethod(
            IReadOnlyList<BepInExPluginMethodMetadata> methods,
            string methodName)
        {
            if (methods == null || string.IsNullOrWhiteSpace(methodName))
            {
                return null;
            }

            return methods
                .Where(method => method != null && string.Equals(method.MethodName, methodName, StringComparison.Ordinal))
                .OrderByDescending(method => !method.IsStatic && method.ParameterCount == 0)
                .ThenBy(method => method.ParameterCount)
                .FirstOrDefault();
        }

        private static BepInExBaseChainMetadata FindBaseTypeInDefinitionChain(
            MetadataReader reader,
            TypeDefinition typeDefinition,
            string requestingDirectory,
            Func<string, bool> match)
        {
            return FindBaseTypeInDefinitionChain(
                reader,
                typeDefinition,
                requestingDirectory,
                match,
                new HashSet<string>(StringComparer.Ordinal),
                0);
        }

        private static BepInExBaseChainMetadata FindBaseTypeInDefinitionChain(
            MetadataReader reader,
            TypeDefinition typeDefinition,
            string requestingDirectory,
            Func<string, bool> match,
            ISet<string> visited,
            int depth)
        {
            if (reader == null || match == null)
            {
                return BepInExBaseChainMetadata.Missing("Base-chain metadata was unavailable.");
            }

            if (depth >= MaxExternalBaseChainDepth)
            {
                return BepInExBaseChainMetadata.Missing("Base-chain traversal reached the Modstoke depth guard.");
            }

            var baseType = ReadBaseTypeHandle(reader, typeDefinition.BaseType);
            if (baseType.IsNil)
            {
                return BepInExBaseChainMetadata.Missing("Plugin type does not expose a metadata-visible base type.");
            }

            var baseTypeName = ReadTypeName(reader, baseType);
            if (match(baseTypeName))
            {
                return BepInExBaseChainMetadata.CreateFound(
                    baseTypeName,
                    depth + 1,
                    "Matched metadata-visible BepInEx base type '" + baseTypeName + "'.");
            }

            if (baseType.Kind != HandleKind.TypeDefinition)
            {
                return baseType.Kind == HandleKind.TypeReference
                    ? FindExternalBaseTypeInDefinitionChain(
                        reader,
                        (TypeReferenceHandle)baseType,
                        requestingDirectory,
                        match,
                        visited,
                        depth + 1)
                    : BepInExBaseChainMetadata.Missing("Base-chain metadata handle kind '" + baseType.Kind + "' is not supported.");
            }

            var baseTypeHandle = (TypeDefinitionHandle)baseType;
            var visitedKey = "local:" + reader.GetGuid(reader.GetModuleDefinition().Mvid) + ":" + baseTypeName;
            if (!visited.Add(visitedKey))
            {
                return BepInExBaseChainMetadata.Missing("Base-chain traversal encountered a repeated local base type '" + baseTypeName + "'.");
            }

            return FindBaseTypeInDefinitionChain(
                reader,
                reader.GetTypeDefinition(baseTypeHandle),
                requestingDirectory,
                match,
                visited,
                depth + 1);
        }

        private static BepInExBaseChainMetadata FindExternalBaseTypeInDefinitionChain(
            MetadataReader reader,
            TypeReferenceHandle typeReferenceHandle,
            string requestingDirectory,
            Func<string, bool> match,
            ISet<string> visited,
            int depth)
        {
            if (reader == null || typeReferenceHandle.IsNil || match == null || depth >= MaxExternalBaseChainDepth)
            {
                return BepInExBaseChainMetadata.Missing("External base-chain metadata was unavailable or exceeded the depth guard.");
            }

            var typeReference = reader.GetTypeReference(typeReferenceHandle);
            var typeName = ReadTypeName(reader, typeReferenceHandle);
            if (match(typeName))
            {
                return BepInExBaseChainMetadata.CreateFound(
                    typeName,
                    depth,
                    "Matched metadata-visible BepInEx external base type '" + typeName + "'.");
            }

            BepInExPluginAssemblyReferenceSnapshot resolvedReference;
            if (!TryResolveTypeReferenceAssembly(reader, typeReference, requestingDirectory, out resolvedReference)
                || resolvedReference == null
                || string.IsNullOrWhiteSpace(resolvedReference.ResolvedPath)
                || !File.Exists(resolvedReference.ResolvedPath))
            {
                return BepInExBaseChainMetadata.Missing("External base type '" + typeName + "' could not be resolved through Modstoke resolver roots.");
            }

            string fullAssemblyPath;
            try
            {
                fullAssemblyPath = Path.GetFullPath(resolvedReference.ResolvedPath);
            }
            catch
            {
                return BepInExBaseChainMetadata.Missing("External base type '" + typeName + "' resolved to an invalid assembly path.");
            }

            var visitedKey = "external:" + fullAssemblyPath.ToUpperInvariant() + ":" + typeName;
            if (!visited.Add(visitedKey))
            {
                return BepInExBaseChainMetadata.Missing("Base-chain traversal encountered a repeated external base type '" + typeName + "'.");
            }

            try
            {
                using (var stream = new FileStream(fullAssemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var peReader = new PEReader(stream))
                {
                    if (!peReader.HasMetadata)
                    {
                        return BepInExBaseChainMetadata.Missing("External base assembly '" + fullAssemblyPath + "' did not expose metadata.");
                    }

                    var externalReader = peReader.GetMetadataReader();
                    var externalTypeHandle = FindTypeDefinition(externalReader, typeName);
                    if (externalTypeHandle.IsNil)
                    {
                        return BepInExBaseChainMetadata.Missing("External base type '" + typeName + "' was not found in resolved assembly metadata.");
                    }

                    var result = FindBaseTypeInDefinitionChain(
                        externalReader,
                        externalReader.GetTypeDefinition(externalTypeHandle),
                        Path.GetDirectoryName(fullAssemblyPath),
                        match,
                        visited,
                        depth);
                    return result.Found
                        ? result.WithExternalAssembly(
                            resolvedReference.AssemblyName,
                            fullAssemblyPath,
                            resolvedReference.Source,
                            "Resolved external base type '" + typeName + "' through " + resolvedReference.Source + ". " + result.Detail)
                        : result;
                }
            }
            catch (Exception ex)
            {
                return BepInExBaseChainMetadata.Missing("External base assembly metadata could not be read: " + ex.Message);
            }
        }

        private static bool TryResolveTypeReferenceAssembly(
            MetadataReader reader,
            TypeReference typeReference,
            string requestingDirectory,
            out BepInExPluginAssemblyReferenceSnapshot resolvedReference)
        {
            resolvedReference = null;
            if (reader == null)
            {
                return false;
            }

            var scope = typeReference.ResolutionScope;
            while (!scope.IsNil)
            {
                if (scope.Kind == HandleKind.AssemblyReference)
                {
                    var reference = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                    var name = reader.GetString(reference.Name);
                    var version = reference.Version == null ? "" : reference.Version.ToString();
                    var resolved = ResolveAssemblyReference(name, version, requestingDirectory);
                    if (resolved != null
                        && resolved.Resolved
                        && !string.IsNullOrWhiteSpace(resolved.ResolvedPath)
                        && File.Exists(resolved.ResolvedPath))
                    {
                        resolvedReference = resolved;
                        return true;
                    }

                    return false;
                }

                if (scope.Kind == HandleKind.TypeReference)
                {
                    scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
                    continue;
                }

                return false;
            }

            return false;
        }

        private static TypeDefinitionHandle FindTypeDefinition(MetadataReader reader, string fullName)
        {
            if (reader == null || string.IsNullOrWhiteSpace(fullName))
            {
                return default(TypeDefinitionHandle);
            }

            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeDefinition = reader.GetTypeDefinition(typeHandle);
                var typeName = ReadTypeDefinitionFullName(reader, typeDefinition);
                if (string.Equals(typeName, fullName, StringComparison.Ordinal))
                {
                    return typeHandle;
                }
            }

            return default(TypeDefinitionHandle);
        }

        private static bool IsBepInExBasePluginTypeName(string fullName)
        {
            return string.Equals(fullName, "BepInEx.Unity.IL2CPP.BasePlugin", StringComparison.Ordinal)
                || string.Equals(fullName, "BepInEx.BasePlugin", StringComparison.Ordinal)
                || string.Equals(ShortTypeName(fullName), "BasePlugin", StringComparison.Ordinal);
        }

        private static bool IsBepInExBaseUnityPluginTypeName(string fullName)
        {
            return string.Equals(fullName, "BepInEx.BaseUnityPlugin", StringComparison.Ordinal)
                || string.Equals(ShortTypeName(fullName), "BaseUnityPlugin", StringComparison.Ordinal);
        }

        private static bool IsPublicType(TypeAttributes attributes)
        {
            var visibility = attributes & TypeAttributes.VisibilityMask;
            return visibility == TypeAttributes.Public || visibility == TypeAttributes.NestedPublic;
        }

        private static int CountMethodParameters(MetadataReader reader, MethodDefinition methodDefinition)
        {
            // Parameter table rows carry names/attributes and may be omitted; the signature is authoritative.
            return methodDefinition.DecodeSignature(BepInExBaseTypeSignatureProvider.Instance, 0).ParameterTypes.Length;
        }

        private static IReadOnlyList<BepInExPluginAssemblyReferenceSnapshot> ReadAssemblyReferences(MetadataReader reader, string requestingDirectory)
        {
            if (reader == null)
            {
                return Array.Empty<BepInExPluginAssemblyReferenceSnapshot>();
            }

            var references = new List<BepInExPluginAssemblyReferenceSnapshot>();
            foreach (var referenceHandle in reader.AssemblyReferences)
            {
                if (references.Count >= MaxAssemblyReferenceCount)
                {
                    break;
                }

                var reference = reader.GetAssemblyReference(referenceHandle);
                var name = reader.GetString(reference.Name);
                var version = reference.Version == null ? "" : reference.Version.ToString();
                references.Add(ResolveAssemblyReference(name, version, requestingDirectory));
            }

            return references;
        }

        private static BepInExPluginAssemblyReferenceSnapshot ResolveAssemblyReference(
            string assemblyName,
            string requiredVersion,
            string requestingDirectory)
        {
            var normalizedName = string.IsNullOrWhiteSpace(assemblyName) ? "" : assemblyName.Trim();
            var normalizedVersion = string.IsNullOrWhiteSpace(requiredVersion) ? "" : requiredVersion.Trim();
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return new BepInExPluginAssemblyReferenceSnapshot(
                    "",
                    normalizedVersion,
                    false,
                    "invalid",
                    "",
                    "",
                    "Assembly reference name was empty.");
            }

            var loaded = FindLoadedAssembly(normalizedName);
            if (loaded != null)
            {
                return new BepInExPluginAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    "loaded",
                    SafeAssemblyVersion(loaded),
                    SafeAssemblyLocation(loaded),
                    "Assembly reference is already loaded in the runtime domain.");
            }

            string resolvedPath;
            string resolvedSource;
            if (AssemblyResolver.TryFindAssemblyPath(normalizedName, requestingDirectory, out resolvedPath, out resolvedSource))
            {
                return new BepInExPluginAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    string.IsNullOrWhiteSpace(resolvedSource) ? "resolver" : resolvedSource,
                    SafeAssemblyVersion(resolvedPath),
                    resolvedPath,
                    "Assembly reference was found through Modstoke resolver roots.");
            }

            if (IsFrameworkReference(normalizedName))
            {
                return new BepInExPluginAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    "framework-reference",
                    "",
                    "",
                    "Assembly reference is supplied by the current .NET runtime.");
            }

            return new BepInExPluginAssemblyReferenceSnapshot(
                normalizedName,
                normalizedVersion,
                false,
                "unresolved",
                "",
                "",
                "No loaded assembly or Modstoke resolver path matched this assembly reference.");
        }

        private static Assembly FindLoadedAssembly(string assemblyName)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AssemblyName name;
                    try
                    {
                        name = assembly.GetName();
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.Equals(name.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool IsFrameworkReference(string assemblyName)
        {
            return string.Equals(assemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "netstandard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "System", StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "Microsoft.VisualBasic.Core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assemblyName, "Microsoft.Win32.Registry", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeAssemblyVersion(Assembly assembly)
        {
            try
            {
                return assembly == null || assembly.GetName().Version == null ? "" : assembly.GetName().Version.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string SafeAssemblyVersion(string assemblyPath)
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(assemblyPath);
                return name.Version == null ? "" : name.Version.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string SafeAssemblyLocation(Assembly assembly)
        {
            try
            {
                return assembly == null ? "" : assembly.Location ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool TryReadBepInExPluginAttribute(
            MetadataReader reader,
            CustomAttribute attribute,
            out string guid,
            out string name,
            out string version)
        {
            guid = "";
            name = "";
            version = "";

            try
            {
                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.Length < 2 || blob.ReadUInt16() != 1)
                {
                    return false;
                }

                guid = blob.ReadSerializedString() ?? "";
                name = blob.ReadSerializedString() ?? "";
                version = blob.ReadSerializedString() ?? "";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadAttributeTypeName(MetadataReader reader, EntityHandle constructor)
        {
            if (reader == null || constructor.IsNil)
            {
                return "";
            }

            switch (constructor.Kind)
            {
                case HandleKind.MemberReference:
                    var memberReference = reader.GetMemberReference((MemberReferenceHandle)constructor);
                    return ReadTypeName(reader, memberReference.Parent);
                case HandleKind.MethodDefinition:
                    var methodDefinition = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
                    return ReadTypeName(reader, methodDefinition.GetDeclaringType());
                default:
                    return "";
            }
        }

        private static EntityHandle ReadBaseTypeHandle(MetadataReader reader, EntityHandle handle)
        {
            return handle.Kind == HandleKind.TypeSpecification
                ? BepInExBaseTypeSignatureProvider.Instance.GetTypeFromSpecification(reader, 0, (TypeSpecificationHandle)handle, 0)
                : handle;
        }

        private static string ReadTypeName(MetadataReader reader, EntityHandle handle)
        {
            if (reader == null || handle.IsNil)
            {
                return "";
            }

            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return ReadTypeDefinitionFullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle));
                case HandleKind.TypeReference:
                    var typeReference = reader.GetTypeReference((TypeReferenceHandle)handle);
                    if (typeReference.ResolutionScope.Kind == HandleKind.TypeReference)
                        return ReadTypeName(reader, typeReference.ResolutionScope) + "+" + reader.GetString(typeReference.Name);
                    return CombineTypeName(reader.GetString(typeReference.Namespace), reader.GetString(typeReference.Name));
                case HandleKind.TypeSpecification:
                    return ReadTypeName(reader, ReadBaseTypeHandle(reader, handle));
                default:
                    return "";
            }
        }

        private static string ReadTypeDefinitionFullName(MetadataReader reader, TypeDefinition typeDefinition)
        {
            var declaringType = typeDefinition.GetDeclaringType();
            if (!declaringType.IsNil)
                return ReadTypeDefinitionFullName(reader, reader.GetTypeDefinition(declaringType)) + "+" + reader.GetString(typeDefinition.Name);
            return CombineTypeName(reader.GetString(typeDefinition.Namespace), reader.GetString(typeDefinition.Name));
        }

        private static string CombineTypeName(string typeNamespace, string name)
        {
            return string.IsNullOrWhiteSpace(typeNamespace) ? name ?? "" : typeNamespace + "." + name;
        }

        private static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return "";
            }

            var index = fullName.LastIndexOf('.');
            return index < 0 || index >= fullName.Length - 1 ? fullName : fullName.Substring(index + 1);
        }

        private static bool IsBepInExPluginAttribute(string fullName)
        {
            return string.Equals(fullName, "BepInEx.BepInPlugin", StringComparison.Ordinal)
                || string.Equals(fullName, "BepInEx.BepInPluginAttribute", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(fullName), "BepInPlugin", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(fullName), "BepInPluginAttribute", StringComparison.Ordinal);
        }

        private static bool IsModuleInitializerAttribute(string fullName)
        {
            var shortName = ShortTypeName(fullName);
            return string.Equals(fullName, "System.Runtime.CompilerServices.ModuleInitializerAttribute", StringComparison.Ordinal)
                || string.Equals(shortName, "ModuleInitializerAttribute", StringComparison.Ordinal)
                || string.Equals(shortName, "ModuleInitializer", StringComparison.Ordinal);
        }

        private static string CandidatePlanId(BepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            return candidate?.CandidateId ?? "";
        }

        private static string PlanKey(BepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            return CandidatePlanId(candidate);
        }

        private static string NormalizeId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static string ResolvePluginRoot()
        {
            try
            {
                return string.IsNullOrWhiteSpace(Paths.PluginPath) ? "" : Paths.PluginPath;
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveParserHarnessRoot()
        {
            try
            {
                return Path.Combine(LoaderPaths.ValidationProbesDirectory, ParserHarnessDirectoryName);
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveCandidateHarnessRoot()
        {
            try
            {
                return Path.Combine(LoaderPaths.ValidationProbesDirectory, CandidateHarnessDirectoryName);
            }
            catch
            {
                return "";
            }
        }

        private static bool IsPathUnderRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static long GetFileSize(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? 0 : new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        // Ancestry needs the generic definition, not substituted argument types or runtime Type objects.
        private sealed class BepInExBaseTypeSignatureProvider : ISignatureTypeProvider<EntityHandle, int>
        {
            internal static readonly BepInExBaseTypeSignatureProvider Instance = new BepInExBaseTypeSignatureProvider();

            public EntityHandle GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => handle;
            public EntityHandle GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;
            public EntityHandle GetGenericInstantiation(EntityHandle genericType, ImmutableArray<EntityHandle> typeArguments) => genericType;
            public EntityHandle GetModifiedType(EntityHandle modifier, EntityHandle unmodifiedType, bool isRequired) => unmodifiedType;

            public EntityHandle GetTypeFromSpecification(MetadataReader reader, int genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                if (genericContext >= MaxExternalBaseChainDepth)
                    throw new BadImageFormatException("Base-type signature traversal exceeded the Modstoke depth guard.");
                return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext + 1);
            }

            public EntityHandle GetArrayType(EntityHandle elementType, ArrayShape shape) => default;
            public EntityHandle GetSZArrayType(EntityHandle elementType) => default;
            public EntityHandle GetByReferenceType(EntityHandle elementType) => default;
            public EntityHandle GetPointerType(EntityHandle elementType) => default;
            public EntityHandle GetPinnedType(EntityHandle elementType) => default;
            public EntityHandle GetFunctionPointerType(MethodSignature<EntityHandle> signature) => default;
            public EntityHandle GetGenericMethodParameter(int genericContext, int index) => default;
            public EntityHandle GetGenericTypeParameter(int genericContext, int index) => default;
            public EntityHandle GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
        }

        private sealed class BepInExPluginMethodMetadata
        {
            internal BepInExPluginMethodMetadata(string methodName, bool isStatic, bool isPublic, int parameterCount)
            {
                MethodName = methodName ?? "";
                IsStatic = isStatic;
                IsPublic = isPublic;
                ParameterCount = Math.Max(0, parameterCount);
            }

            internal string MethodName { get; }
            internal bool IsStatic { get; }
            internal bool IsPublic { get; }
            internal int ParameterCount { get; }
        }

        private sealed class BepInExBaseChainMetadata
        {
            private BepInExBaseChainMetadata(
                bool found,
                string matchTypeName,
                int matchDepth,
                bool externalAssemblyResolved,
                string externalAssemblyName,
                string externalAssemblyPath,
                string externalAssemblySource,
                string detail)
            {
                Found = found;
                MatchTypeName = matchTypeName ?? "";
                MatchDepth = Math.Max(0, matchDepth);
                ExternalAssemblyResolved = externalAssemblyResolved;
                ExternalAssemblyName = externalAssemblyName ?? "";
                ExternalAssemblyPath = externalAssemblyPath ?? "";
                ExternalAssemblySource = externalAssemblySource ?? "";
                Detail = detail ?? "";
            }

            internal bool Found { get; }
            internal string MatchTypeName { get; }
            internal int MatchDepth { get; }
            internal bool ExternalAssemblyResolved { get; }
            internal string ExternalAssemblyName { get; }
            internal string ExternalAssemblyPath { get; }
            internal string ExternalAssemblySource { get; }
            internal string Detail { get; }

            internal BepInExBaseChainMetadata WithExternalAssembly(
                string assemblyName,
                string assemblyPath,
                string assemblySource,
                string detail)
            {
                return new BepInExBaseChainMetadata(
                    Found,
                    MatchTypeName,
                    MatchDepth,
                    true,
                    assemblyName,
                    assemblyPath,
                    assemblySource,
                    detail);
            }

            internal static BepInExBaseChainMetadata CreateFound(string matchTypeName, int matchDepth, string detail)
            {
                return new BepInExBaseChainMetadata(
                    true,
                    matchTypeName,
                    matchDepth,
                    false,
                    "",
                    "",
                    "",
                    detail);
            }

            internal static BepInExBaseChainMetadata Missing(string detail)
            {
                return new BepInExBaseChainMetadata(
                    false,
                    "",
                    0,
                    false,
                    "",
                    "",
                    "",
                    detail);
            }
        }

        private sealed class BepInExPluginAssemblyMetadata
        {
            internal BepInExPluginAssemblyMetadata(
                bool readable,
                string issueCode,
                string detail,
                string assemblyName,
                string assemblyVersion,
                IReadOnlyList<BepInExPluginAssemblyReferenceSnapshot> assemblyReferences,
                int pluginAttributeCount,
                string pluginGuid,
                string pluginName,
                string pluginVersion,
                string pluginTypeName,
                BepInExPluginExecutionPlanSnapshot executionPlan)
            {
                Readable = readable;
                IssueCode = issueCode ?? "";
                Detail = detail ?? "";
                AssemblyName = assemblyName ?? "";
                AssemblyVersion = assemblyVersion ?? "";
                AssemblyReferences = assemblyReferences ?? Array.Empty<BepInExPluginAssemblyReferenceSnapshot>();
                AssemblyReferenceCount = AssemblyReferences.Count;
                ResolvedAssemblyReferenceCount = AssemblyReferences.Count(reference => reference.Resolved);
                UnresolvedAssemblyReferenceCount = AssemblyReferences.Count(reference => !reference.Resolved);
                PluginAttributeCount = Math.Max(0, pluginAttributeCount);
                PluginGuid = pluginGuid ?? "";
                PluginName = pluginName ?? "";
                PluginVersion = pluginVersion ?? "";
                PluginTypeName = pluginTypeName ?? "";
                ExecutionPlan = executionPlan ?? BepInExPluginExecutionPlanSnapshot.CreateUnavailable(pluginTypeName, "execution-plan-unavailable", "BepInEx plugin execution plan metadata was not available.");
            }

            internal bool Readable { get; }
            internal string IssueCode { get; }
            internal string Detail { get; }
            internal string AssemblyName { get; }
            internal string AssemblyVersion { get; }
            internal IReadOnlyList<BepInExPluginAssemblyReferenceSnapshot> AssemblyReferences { get; }
            internal int AssemblyReferenceCount { get; }
            internal int ResolvedAssemblyReferenceCount { get; }
            internal int UnresolvedAssemblyReferenceCount { get; }
            internal int PluginAttributeCount { get; }
            internal string PluginGuid { get; }
            internal string PluginName { get; }
            internal string PluginVersion { get; }
            internal string PluginTypeName { get; }
            internal BepInExPluginExecutionPlanSnapshot ExecutionPlan { get; }

            internal static BepInExPluginAssemblyMetadata Missing(string issueCode, string detail)
            {
                return new BepInExPluginAssemblyMetadata(
                    false,
                    issueCode,
                    detail,
                    "",
                    "",
                    Array.Empty<BepInExPluginAssemblyReferenceSnapshot>(),
                    0,
                    "",
                    "",
                    "",
                    "",
                    BepInExPluginExecutionPlanSnapshot.CreateUnavailable("", issueCode, detail));
            }
        }
    }
}
