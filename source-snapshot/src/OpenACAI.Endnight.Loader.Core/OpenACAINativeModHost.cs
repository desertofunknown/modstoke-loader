// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned native mod host gate. This validates Modstoke-native package
// manifests and entrypoint intent without routing new mods through RedLoader or
// BepInEx-shaped hosting paths.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAINativeModHost
    {
        public const string ServiceKey = "openacai.loader.nativeModHost";
        public const int MaxCandidateCount = 256;
        public const int MaxModIdLength = 96;
        public const int MaxEntrypointLength = 192;
        public const int MaxAssemblyReferenceCount = 96;
        public const int MaxLifecycleHookCount = 24;
        public const int MaxLifecycleHookLength = 64;
        public const int MaxLifecycleHookMethodLength = MaxEntrypointLength;

        private static readonly object SyncRoot = new object();
        private static OpenACAINativeModHostSnapshot _state = OpenACAINativeModHostSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _planRequestCount;
        private static int _acceptedPlanCount;
        private static int _rejectedPlanCount;
        private static string _lastPlanModId = "";
        private static string _lastPlanStatus = "INFO";
        private static string _lastPlanDetail = "No native Modstoke mod host plan has been validated.";
        private static string _lastPlanReason = "none";

        public static OpenACAINativeModHostSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => true;
        public static bool RuntimeExecutionEnabled => OpenACAINativeModRuntime.ExecutionEnabled;
        public static IReadOnlyList<OpenACAINativeModCandidateSnapshot> Candidates => State.Candidates;

        public static IReadOnlyList<OpenACAINativeModCandidateSnapshot> GetCandidates()
        {
            return Candidates;
        }

        public static OpenACAINativeModHostPlan PlanHost(string modId)
        {
            var snapshot = State;
            var normalizedId = NormalizeId(modId);
            var accepted = false;
            var canLoadNow = false;
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Native mod host plan is invalid.";
            OpenACAINativeModCandidateSnapshot candidate = null;
            OpenACAINativeModExecutionPlanSnapshot executionPlan = null;

            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                reason = "missing-mod-id";
                detail = "A native Modstoke mod id is required.";
            }
            else
            {
                candidate = FindCandidate(snapshot, normalizedId);
                if (candidate == null)
                {
                    reason = "unknown-openacai-mod";
                    detail = "No Modstoke-native mod candidate matched '" + normalizedId + "'.";
                }
                else if (!candidate.Enabled)
                {
                    executionPlan = candidate.ExecutionPlan;
                    reason = "mod-disabled";
                    detail = "The Modstoke-native mod package is disabled.";
                }
                else if (!candidate.HasAssembly)
                {
                    executionPlan = candidate.ExecutionPlan;
                    reason = "missing-assembly";
                    detail = "The Modstoke-native mod package has no loadable assembly.";
                }
                else if (!candidate.Hostable)
                {
                    executionPlan = candidate.ExecutionPlan;
                    reason = candidate.IssueCode;
                    detail = candidate.Detail;
                }
                else
                {
                    executionPlan = candidate.ExecutionPlan;
                    accepted = true;
                    if (!RuntimeExecutionEnabled)
                    {
                        status = "DEFERRED";
                        reason = "runtime-execution-disabled";
                        detail = "OpenACAI accepted the native mod host plan; the owned startup execution path is not active.";
                    }
                    else
                    {
                        status = "READY";
                        reason = "startup-owned";
                        detail = "The package is eligible for startup hosting. Planning does not load or reload mods.";
                    }
                }
            }

            var plan = new OpenACAINativeModHostPlan(
                candidate == null ? normalizedId : candidate.ModId,
                candidate == null ? "" : candidate.DisplayName,
                candidate == null ? "" : candidate.AssemblyPath,
                candidate == null ? "" : candidate.EntrypointTypeName,
                candidate == null ? "" : candidate.EntrypointMethodName,
                executionPlan == null ? "" : executionPlan.InvocationKind,
                executionPlan == null ? Array.Empty<string>() : executionPlan.LifecycleHooks,
                executionPlan != null && executionPlan.Ready,
                executionPlan == null ? 0 : executionPlan.MethodParameterCount,
                executionPlan != null && executionPlan.RequiresInstance,
                executionPlan != null && executionPlan.ConstructorRequired,
                executionPlan != null && executionPlan.ConstructorReady,
                accepted,
                canLoadNow,
                RuntimeExecutionEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
            RecordPlan(plan);
            Refresh();
            return plan;
        }

        public static OpenACAINativeModHostSnapshot Refresh()
        {
            return Refresh(OpenACAIModding.ModInventory);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAINativeModHostSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native mod host provider could not be registered.");
            }
        }

        internal static OpenACAINativeModHostSnapshot Refresh(ModInventoryState inventory)
        {
            return Refresh(OpenACAIModInventorySnapshot.FromInternal(inventory));
        }

        internal static OpenACAINativeModCandidateSnapshot[] GetStartupCandidates(ModInventoryState inventory)
        {
            // Diagnostic display limits must not truncate the set of mods owned by the runtime.
            return OpenACAIModInventorySnapshot.FromInternal(inventory).OpenAcaiMods
                .Where(entry => entry != null && !entry.IsLibrary)
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssemblyPath ?? entry.ManifestPath, StringComparer.OrdinalIgnoreCase)
                .Select(CreateCandidate)
                .ToArray();
        }

        internal static OpenACAINativeModHostSnapshot Refresh(OpenACAIModInventorySnapshot inventory)
        {
            var snapshot = CreateSnapshot(inventory);
            Publish(snapshot);
            return snapshot;
        }

        private static void Publish(OpenACAINativeModHostSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAINativeModHostSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(OpenACAINativeModHostPlan plan)
        {
            lock (SyncRoot)
            {
                _planRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedPlanCount++;
                }
                else
                {
                    _rejectedPlanCount++;
                }

                _lastPlanModId = plan == null ? "" : plan.ModId;
                _lastPlanStatus = plan == null ? "CHECK" : plan.Status;
                _lastPlanDetail = plan == null ? "Native mod host plan was null." : plan.Detail;
                _lastPlanReason = plan == null ? "null-plan" : plan.ReasonCode;
            }
        }

        private static OpenACAINativeModHostSnapshot CreateSnapshot(OpenACAIModInventorySnapshot inventory)
        {
            inventory = inventory ?? OpenACAIModInventorySnapshot.Empty();
            var candidates = inventory.OpenAcaiMods
                .Where(entry => entry != null && !entry.IsLibrary)
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssemblyPath ?? entry.ManifestPath ?? "", StringComparer.OrdinalIgnoreCase)
                .Take(MaxCandidateCount)
                .Select(CreateCandidate)
                .ToArray();
            var libraryExcludedCount = inventory.Libraries.Count(entry => entry != null);

            int planRequestCount;
            int acceptedPlanCount;
            int rejectedPlanCount;
            string lastPlanModId;
            string lastPlanStatus;
            string lastPlanDetail;
            string lastPlanReason;
            lock (SyncRoot)
            {
                planRequestCount = _planRequestCount;
                acceptedPlanCount = _acceptedPlanCount;
                rejectedPlanCount = _rejectedPlanCount;
                lastPlanModId = _lastPlanModId ?? "";
                lastPlanStatus = _lastPlanStatus ?? "";
                lastPlanDetail = _lastPlanDetail ?? "";
                lastPlanReason = _lastPlanReason ?? "";
            }

            var validationIssueCount = candidates.Count(candidate => candidate.ValidationIssue);
            var hostableCount = candidates.Count(candidate => candidate.Hostable);
            var metadataOnlyCount = candidates.Count(candidate => candidate.MetadataOnly);
            var assemblyMetadataReadCount = candidates.Count(candidate => candidate.AssemblyMetadataReadable);
            var assemblyMetadataIssueCount = candidates.Count(candidate => candidate.HasAssembly && !candidate.AssemblyMetadataReadable);
            var assemblyReferenceCount = candidates.Sum(candidate => candidate.AssemblyReferenceCount);
            var resolvedAssemblyReferenceCount = candidates.Sum(candidate => candidate.ResolvedAssemblyReferenceCount);
            var unresolvedAssemblyReferenceCount = candidates.Sum(candidate => candidate.UnresolvedAssemblyReferenceCount);
            var executionPlanReadyCount = candidates.Count(candidate => candidate.ExecutionPlanReady);
            var executionPlanIssueCount = candidates.Count(candidate => candidate.ExecutionPlanIssue);
            var lifecycleHookDeclarationCount = candidates.Sum(candidate => candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleHookCount);
            var lifecycleHookMethodReadyCount = candidates.Sum(candidate => candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleHookMethodReadyCount);
            var lifecycleHookMethodIssueCount = candidates.Sum(candidate => candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleHookMethodIssueCount);
            var entrypointTypeMatchCount = candidates.Count(candidate => candidate.EntrypointTypeFound);
            var entrypointMethodMatchCount = candidates.Count(candidate => candidate.EntrypointMethodFound);
            var status = validationIssueCount == 0 ? "READY" : "CHECK";
            var detail = validationIssueCount == 0
                ? "Modstoke-native package validation is available. Actual startup results are published by the native mod runtime."
                : validationIssueCount + " Modstoke-native package validation issue(s) were found.";

            return new OpenACAINativeModHostSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                true,
                status,
                detail,
                OpenACAIPaths.ModsDirectory,
                Directory.Exists(OpenACAIPaths.ModsDirectory),
                PlanningEnabled,
                RuntimeExecutionEnabled,
                candidates,
                libraryExcludedCount,
                assemblyMetadataReadCount,
                assemblyMetadataIssueCount,
                assemblyReferenceCount,
                resolvedAssemblyReferenceCount,
                unresolvedAssemblyReferenceCount,
                executionPlanReadyCount,
                executionPlanIssueCount,
                lifecycleHookDeclarationCount,
                lifecycleHookMethodReadyCount,
                lifecycleHookMethodIssueCount,
                entrypointTypeMatchCount,
                entrypointMethodMatchCount,
                hostableCount,
                metadataOnlyCount,
                validationIssueCount,
                planRequestCount,
                acceptedPlanCount,
                rejectedPlanCount,
                lastPlanModId,
                lastPlanStatus,
                lastPlanDetail,
                lastPlanReason);
        }

        private static OpenACAINativeModCandidateSnapshot CreateCandidate(OpenACAIModInfo entry)
        {
            var manifest = ReadManifest(entry.ManifestPath);
            var manifestEntrypoint = ReadManifestValue(manifest, "entrypoint")
                ?? ReadManifestValue(manifest, "entryPoint")
                ?? ReadManifestValue(manifest, "main")
                ?? ReadManifestValue(manifest, "plugin");
            var typeName = ReadManifestValue(manifest, "entryType")
                ?? ReadManifestValue(manifest, "entry_type")
                ?? ReadManifestValue(manifest, "mainType")
                ?? ReadManifestValue(manifest, "pluginType")
                ?? ReadManifestValue(manifest, "typeName")
                ?? ReadManifestValue(manifest, "class")
                ?? manifestEntrypoint;
            var methodName = ReadManifestValue(manifest, "entryMethod")
                ?? ReadManifestValue(manifest, "entry_method")
                ?? ReadManifestValue(manifest, "method")
                ?? ReadManifestValue(manifest, "initializeMethod")
                ?? ReadManifestValue(manifest, "initMethod");
            var lifecycleHooks = ParseLifecycleHooks(
                ReadManifestValue(manifest, "lifecycle")
                ?? ReadManifestValue(manifest, "lifecycleHooks")
                ?? ReadManifestValue(manifest, "lifecycle_hooks")
                ?? ReadManifestValue(manifest, "hooks"));
            var lifecycleHookMethods = ParseLifecycleHookMethods(manifest, lifecycleHooks);
            var entryKind = DetermineEntrypointKind(typeName, methodName, entry.AssemblyPath);
            var metadataOnly = string.IsNullOrWhiteSpace(typeName);
            var assemblyMetadata = InspectAssemblyMetadata(entry.AssemblyPath, typeName, methodName);
            var executionPlan = CreateExecutionPlan(entry, assemblyMetadata, typeName, methodName, lifecycleHooks, lifecycleHookMethods, metadataOnly);
            var hostable = entry.Enabled
                && entry.HasAssembly
                && assemblyMetadata.Readable
                && assemblyMetadata.UnresolvedAssemblyReferenceCount == 0
                && !metadataOnly
                && assemblyMetadata.EntrypointTypeFound
                && executionPlan.Ready;
            var status = hostable
                ? RuntimeExecutionEnabled ? "READY" : "DEFERRED"
                : metadataOnly && entry.HasAssembly && entry.Enabled && assemblyMetadata.Readable ? "INFO" : "CHECK";
            var issueCode = "";
            var detail = "";

            if (!entry.Enabled)
            {
                status = "CHECK";
                issueCode = "disabled";
                detail = "The package or assembly is disabled.";
            }
            else if (!entry.HasAssembly)
            {
                status = "CHECK";
                issueCode = "missing-assembly";
                detail = "The Modstoke-native manifest was found, but no matching assembly was detected.";
            }
            else if (!assemblyMetadata.Readable)
            {
                status = "CHECK";
                issueCode = assemblyMetadata.IssueCode;
                detail = assemblyMetadata.Detail;
            }
            else if (assemblyMetadata.UnresolvedAssemblyReferenceCount > 0)
            {
                status = "CHECK";
                issueCode = "unresolved-assembly-references";
                detail = assemblyMetadata.UnresolvedAssemblyReferenceCount + " assembly reference(s) could not be resolved by the OpenACAI metadata dependency pass.";
            }
            else if (metadataOnly)
            {
                issueCode = "metadata-only";
                detail = "No OpenACAI entrypoint type was declared; package remains inventory/config metadata only.";
            }
            else if (!assemblyMetadata.EntrypointTypeFound)
            {
                status = "CHECK";
                issueCode = "entrypoint-type-missing";
                detail = "Declared OpenACAI entrypoint type '" + NormalizeEntrypoint(typeName) + "' was not found in assembly metadata.";
            }
            else if (!string.IsNullOrWhiteSpace(methodName) && !assemblyMetadata.EntrypointMethodFound)
            {
                status = "CHECK";
                issueCode = "entrypoint-method-missing";
                detail = "Declared OpenACAI entrypoint method '" + NormalizeEntrypoint(methodName) + "' was not found on '" + NormalizeEntrypoint(typeName) + "'.";
            }
            else if (!string.IsNullOrWhiteSpace(methodName) && !assemblyMetadata.EntrypointMethodStatic)
            {
                status = "CHECK";
                issueCode = "entrypoint-method-not-static";
                detail = "Declared OpenACAI entrypoint method '" + NormalizeEntrypoint(methodName) + "' must be public static.";
            }
            else if (!string.IsNullOrWhiteSpace(methodName) && !assemblyMetadata.EntrypointMethodPublic)
            {
                status = "CHECK";
                issueCode = "entrypoint-method-not-public";
                detail = "Declared OpenACAI entrypoint method '" + NormalizeEntrypoint(methodName) + "' must be public static.";
            }
            else if (!executionPlan.Ready)
            {
                status = executionPlan.Status;
                issueCode = executionPlan.IssueCode;
                detail = executionPlan.Detail;
            }
            else if (!RuntimeExecutionEnabled)
            {
                issueCode = "execution-disabled";
                detail = "Entrypoint metadata is valid; the owned startup execution path is not active.";
            }
            else
            {
                detail = "Entrypoint intent, assembly metadata, references, and execution plan are valid for OpenACAI native hosting.";
            }

            return new OpenACAINativeModCandidateSnapshot(
                NormalizeId(entry.Id),
                entry.DisplayName,
                entry.Version,
                entry.Author,
                entry.ManifestType,
                entry.PackagePath,
                entry.ManifestPath,
                entry.AssemblyPath,
                entry.Enabled,
                entry.HasAssembly,
                entry.AssemblyVersion,
                entry.AssemblySize ?? 0,
                assemblyMetadata.Readable,
                assemblyMetadata.AssemblyName,
                assemblyMetadata.AssemblyIdentity,
                assemblyMetadata.DefinedTypeCount,
                assemblyMetadata.MatchedTypeName,
                assemblyMetadata.AssemblyReferences,
                assemblyMetadata.AssemblyReferenceCount,
                assemblyMetadata.ResolvedAssemblyReferenceCount,
                assemblyMetadata.UnresolvedAssemblyReferenceCount,
                executionPlan,
                assemblyMetadata.EntrypointTypeFound,
                assemblyMetadata.EntrypointMethodFound,
                assemblyMetadata.EntrypointMethodStatic,
                assemblyMetadata.EntrypointMethodPublic,
                NormalizeEntrypoint(typeName),
                NormalizeEntrypoint(methodName),
                entryKind,
                hostable,
                metadataOnly,
                status,
                issueCode,
                detail);
        }

        private static OpenACAINativeModCandidateSnapshot FindCandidate(OpenACAINativeModHostSnapshot snapshot, string id)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return snapshot.Candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.ModId, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.DisplayName, id, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, string> ReadManifest(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return values;
                    }

                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        var value = FlattenManifestValue(property.Value);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values[property.Name] = value;
                        }
                    }

                    return values;
                }
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke native host could not read manifest '" + manifestPath + "': " + ex.Message);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string FlattenManifestValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetRawText();
                case JsonValueKind.Array:
                    return string.Join(
                        ",",
                        element.EnumerateArray()
                            .Select(FlattenManifestValue)
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                case JsonValueKind.Object:
                    return string.Join(
                        ",",
                        element.EnumerateObject()
                            .Select(property =>
                            {
                                var value = FlattenManifestValue(property.Value);
                                return string.IsNullOrWhiteSpace(value) ? "" : property.Name + "=" + value;
                            })
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                default:
                    return null;
            }
        }

        private static string ReadManifestValue(IReadOnlyDictionary<string, string> manifest, string key)
        {
            string value;
            return manifest != null && manifest.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }

        private static string DetermineEntrypointKind(string typeName, string methodName, string assemblyPath)
        {
            if (!string.IsNullOrWhiteSpace(typeName) && !string.IsNullOrWhiteSpace(methodName))
            {
                return "static-method";
            }

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                return "type";
            }

            return string.IsNullOrWhiteSpace(assemblyPath) ? "none" : "metadata-only";
        }

        private static OpenACAINativeModExecutionPlanSnapshot CreateExecutionPlan(
            OpenACAIModInfo entry,
            NativeAssemblyMetadata assemblyMetadata,
            string typeName,
            string methodName,
            IReadOnlyList<string> lifecycleHooks,
            IReadOnlyDictionary<string, string> lifecycleHookMethods,
            bool metadataOnly)
        {
            assemblyMetadata = assemblyMetadata ?? NativeAssemblyMetadata.Missing("assembly-metadata-missing", "Assembly metadata was not available.");

            var entryEnabled = entry != null && entry.Enabled;
            var entryHasAssembly = entry != null && entry.HasAssembly;
            var entryId = entry == null ? "" : entry.Id;
            var normalizedTypeName = NormalizeEntrypoint(typeName);
            var normalizedMethodName = NormalizeEntrypoint(methodName);
            var methodDeclared = !string.IsNullOrWhiteSpace(normalizedMethodName);
            var invocationKind = metadataOnly ? "metadata-only" : methodDeclared ? "static-method" : "type-constructor";
            var requiresInstance = !metadataOnly && !methodDeclared;
            var constructorRequired = requiresInstance;
            var constructorReady = !constructorRequired || (assemblyMetadata.EntrypointConstructorPublic && assemblyMetadata.EntrypointConstructorParameterless);
            var hookPlans = CreateLifecycleHookPlans(
                NormalizeId(entryId),
                assemblyMetadata,
                normalizedTypeName,
                normalizedMethodName,
                methodDeclared,
                lifecycleHooks,
                lifecycleHookMethods,
                metadataOnly);
            var hookPlanIssue = hookPlans.FirstOrDefault(hookPlan => hookPlan.Issue);
            var ready = false;
            var status = "CHECK";
            var issueCode = "not-validated";
            var detail = "Modstoke native execution plan has not been validated.";

            if (!entryEnabled)
            {
                issueCode = "disabled";
                detail = "The package or assembly is disabled.";
            }
            else if (!entryHasAssembly)
            {
                issueCode = "missing-assembly";
                detail = "The Modstoke-native mod package has no loadable assembly.";
            }
            else if (!assemblyMetadata.Readable)
            {
                issueCode = assemblyMetadata.IssueCode;
                detail = assemblyMetadata.Detail;
            }
            else if (assemblyMetadata.UnresolvedAssemblyReferenceCount > 0)
            {
                issueCode = "unresolved-assembly-references";
                detail = assemblyMetadata.UnresolvedAssemblyReferenceCount + " assembly reference(s) could not be resolved by the OpenACAI metadata dependency pass.";
            }
            else if (metadataOnly)
            {
                status = "INFO";
                issueCode = "metadata-only";
                detail = "No OpenACAI entrypoint type was declared; no execution plan is required.";
            }
            else if (!assemblyMetadata.EntrypointTypeFound)
            {
                issueCode = "entrypoint-type-missing";
                detail = "Declared OpenACAI entrypoint type '" + normalizedTypeName + "' was not found in assembly metadata.";
            }
            else if (methodDeclared && !assemblyMetadata.EntrypointMethodFound)
            {
                issueCode = "entrypoint-method-missing";
                detail = "Declared OpenACAI entrypoint method '" + normalizedMethodName + "' was not found on '" + normalizedTypeName + "'.";
            }
            else if (methodDeclared && !assemblyMetadata.EntrypointMethodStatic)
            {
                issueCode = "entrypoint-method-not-static";
                detail = "Declared OpenACAI entrypoint method '" + normalizedMethodName + "' must be public static.";
            }
            else if (methodDeclared && !assemblyMetadata.EntrypointMethodPublic)
            {
                issueCode = "entrypoint-method-not-public";
                detail = "Declared OpenACAI entrypoint method '" + normalizedMethodName + "' must be public static.";
            }
            else if (methodDeclared && assemblyMetadata.EntrypointMethodParameterCount > 0)
            {
                issueCode = "entrypoint-method-has-parameters";
                detail = "Declared OpenACAI entrypoint method '" + normalizedMethodName + "' must not require parameters for the current execution gate.";
            }
            else if (!methodDeclared && !assemblyMetadata.EntrypointTypePublic)
            {
                issueCode = "entrypoint-type-not-public";
                detail = "Declared OpenACAI entrypoint type '" + normalizedTypeName + "' must be public for type-constructor execution.";
            }
            else if (!methodDeclared && !assemblyMetadata.EntrypointConstructorFound)
            {
                issueCode = "entrypoint-constructor-missing";
                detail = "Declared OpenACAI entrypoint type '" + normalizedTypeName + "' does not expose a constructor for type-constructor execution.";
            }
            else if (!methodDeclared && !assemblyMetadata.EntrypointConstructorPublic)
            {
                issueCode = "entrypoint-constructor-not-public";
                detail = "Declared OpenACAI entrypoint type '" + normalizedTypeName + "' must expose a public parameterless constructor.";
            }
            else if (!methodDeclared && !assemblyMetadata.EntrypointConstructorParameterless)
            {
                issueCode = "entrypoint-constructor-has-parameters";
                detail = "Declared OpenACAI entrypoint type '" + normalizedTypeName + "' must expose a public parameterless constructor.";
            }
            else if (hookPlanIssue != null)
            {
                issueCode = hookPlanIssue.IssueCode;
                detail = hookPlanIssue.Detail;
            }
            else
            {
                ready = true;
                status = RuntimeExecutionEnabled ? "READY" : "DEFERRED";
                issueCode = RuntimeExecutionEnabled ? "" : "execution-disabled";
                detail = RuntimeExecutionEnabled
                    ? "Modstoke native execution plan is ready."
                    : "Modstoke native execution plan is valid; runtime execution remains disabled.";
            }

            return new OpenACAINativeModExecutionPlanSnapshot(
                NormalizeId(entryId),
                ready,
                status,
                issueCode,
                detail,
                invocationKind,
                normalizedTypeName,
                normalizedMethodName,
                lifecycleHooks ?? Array.Empty<string>(),
                hookPlans,
                requiresInstance,
                constructorRequired,
                constructorReady,
                assemblyMetadata.EntrypointTypePublic,
                assemblyMetadata.EntrypointConstructorFound,
                assemblyMetadata.EntrypointConstructorPublic,
                assemblyMetadata.EntrypointConstructorParameterless,
                assemblyMetadata.EntrypointMethodParameterCount,
                RuntimeExecutionEnabled);
        }

        private static IReadOnlyList<string> ParseLifecycleHooks(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeLifecycleHook)
                .Where(hook => !string.IsNullOrWhiteSpace(hook))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxLifecycleHookCount)
                .ToArray();
        }

        private static IReadOnlyDictionary<string, string> ParseLifecycleHookMethods(IReadOnlyDictionary<string, string> manifest, IReadOnlyList<string> lifecycleHooks)
        {
            var methods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (manifest == null)
            {
                return methods;
            }

            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "lifecycleMethods"));
            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "lifecycle_methods"));
            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "lifecycleHookMethods"));
            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "lifecycle_hook_methods"));
            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "hookMethods"));
            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "hook_methods"));
            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "hooksMethods"));
            AddLifecycleHookMethodPairs(methods, ReadManifestValue(manifest, "hooks_methods"));

            if (lifecycleHooks == null)
            {
                return methods;
            }

            foreach (var hook in lifecycleHooks)
            {
                var normalizedHook = NormalizeLifecycleHook(hook);
                if (string.IsNullOrWhiteSpace(normalizedHook))
                {
                    continue;
                }

                var methodName = ReadLifecycleHookMethodValue(manifest, normalizedHook);
                if (!string.IsNullOrWhiteSpace(methodName))
                {
                    methods[normalizedHook] = NormalizeLifecycleHookMethod(methodName);
                }
            }

            return methods;
        }

        private static void AddLifecycleHookMethodPairs(IDictionary<string, string> methods, string value)
        {
            if (methods == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            foreach (var pair in value.Split(new[] { ',', ';', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Trim().Split(new[] { "=>", "->", "=", ":" }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    continue;
                }

                var hook = NormalizeLifecycleHook(parts[0]);
                var method = NormalizeLifecycleHookMethod(parts[1]);
                if (!string.IsNullOrWhiteSpace(hook) && !string.IsNullOrWhiteSpace(method))
                {
                    methods[hook] = method;
                }
            }
        }

        private static string ReadLifecycleHookMethodValue(IReadOnlyDictionary<string, string> manifest, string hook)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(hook))
            {
                return null;
            }

            var alias = ToManifestAlias(hook);
            return ReadManifestValue(manifest, "on" + alias)
                ?? ReadManifestValue(manifest, hook + "Method")
                ?? ReadManifestValue(manifest, hook + "_method")
                ?? ReadManifestValue(manifest, hook + "Hook")
                ?? ReadManifestValue(manifest, hook + "_hook")
                ?? ReadManifestValue(manifest, "hook." + hook)
                ?? ReadManifestValue(manifest, "lifecycle." + hook);
        }

        private static string ToManifestAlias(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var chars = new List<char>();
            var upperNext = true;
            foreach (var character in value.Trim())
            {
                if (!char.IsLetterOrDigit(character))
                {
                    upperNext = true;
                    continue;
                }

                chars.Add(upperNext ? char.ToUpperInvariant(character) : character);
                upperNext = false;
            }

            return new string(chars.ToArray());
        }

        private static IReadOnlyList<OpenACAINativeLifecycleHookPlanSnapshot> CreateLifecycleHookPlans(
            string modId,
            NativeAssemblyMetadata assemblyMetadata,
            string normalizedTypeName,
            string normalizedMethodName,
            bool entryMethodDeclared,
            IReadOnlyList<string> lifecycleHooks,
            IReadOnlyDictionary<string, string> lifecycleHookMethods,
            bool metadataOnly)
        {
            if (lifecycleHooks == null || lifecycleHooks.Count == 0)
            {
                return Array.Empty<OpenACAINativeLifecycleHookPlanSnapshot>();
            }

            assemblyMetadata = assemblyMetadata ?? NativeAssemblyMetadata.Missing("assembly-metadata-missing", "Assembly metadata was not available.");
            var plans = new List<OpenACAINativeLifecycleHookPlanSnapshot>();
            foreach (var rawHook in lifecycleHooks.Take(MaxLifecycleHookCount))
            {
                var hook = NormalizeLifecycleHook(rawHook);
                if (string.IsNullOrWhiteSpace(hook))
                {
                    continue;
                }

                var declaredMethodName = "";
                var hookMethodDeclared = lifecycleHookMethods != null
                    && lifecycleHookMethods.TryGetValue(hook, out declaredMethodName)
                    && !string.IsNullOrWhiteSpace(declaredMethodName);
                var methodName = NormalizeLifecycleHookMethod(hookMethodDeclared ? declaredMethodName : entryMethodDeclared ? normalizedMethodName : "");
                var methodSource = hookMethodDeclared ? "manifest" : entryMethodDeclared ? "entrypoint-default" : "none";
                var method = assemblyMetadata.FindMethod(methodName);
                var methodFound = method != null;
                var methodStatic = methodFound && method.IsStatic;
                var methodPublic = methodFound && method.IsPublic;
                var methodParameterCount = methodFound ? method.ParameterCount : 0;
                var ready = false;
                var status = "CHECK";
                var issueCode = "not-validated";
                var detail = "OpenACAI lifecycle hook method metadata has not been validated.";

                if (metadataOnly)
                {
                    status = "INFO";
                    issueCode = "metadata-only";
                    detail = "No OpenACAI entrypoint type was declared; lifecycle hook method metadata is not required.";
                }
                else if (!assemblyMetadata.Readable)
                {
                    issueCode = assemblyMetadata.IssueCode;
                    detail = assemblyMetadata.Detail;
                }
                else if (!assemblyMetadata.EntrypointTypeFound)
                {
                    issueCode = "entrypoint-type-missing";
                    detail = "Declared OpenACAI entrypoint type '" + normalizedTypeName + "' was not found in assembly metadata.";
                }
                else if (string.IsNullOrWhiteSpace(methodName))
                {
                    issueCode = "lifecycle-hook-method-missing";
                    detail = "Lifecycle hook '" + hook + "' does not declare a method and no static entry method default is available.";
                }
                else if (!methodFound)
                {
                    issueCode = "lifecycle-hook-method-missing";
                    detail = "Lifecycle hook '" + hook + "' targets method '" + methodName + "', but that method was not found on '" + normalizedTypeName + "'.";
                }
                else if (!methodStatic)
                {
                    issueCode = "lifecycle-hook-method-not-static";
                    detail = "Lifecycle hook '" + hook + "' targets method '" + methodName + "', which must be public static for the current metadata gate.";
                }
                else if (!methodPublic)
                {
                    issueCode = "lifecycle-hook-method-not-public";
                    detail = "Lifecycle hook '" + hook + "' targets method '" + methodName + "', which must be public static for the current metadata gate.";
                }
                else if (methodParameterCount > 0)
                {
                    issueCode = "lifecycle-hook-method-has-parameters";
                    detail = "Lifecycle hook '" + hook + "' targets method '" + methodName + "', which must not require parameters for the current metadata gate.";
                }
                else
                {
                    ready = true;
                    status = RuntimeExecutionEnabled ? "READY" : "DEFERRED";
                    issueCode = RuntimeExecutionEnabled ? "" : "execution-disabled";
                    detail = RuntimeExecutionEnabled
                        ? "Lifecycle hook '" + hook + "' method metadata is ready for invocation."
                        : "Lifecycle hook '" + hook + "' method metadata is valid; runtime execution remains disabled.";
                }

                plans.Add(new OpenACAINativeLifecycleHookPlanSnapshot(
                    modId,
                    hook,
                    methodName,
                    hookMethodDeclared,
                    methodSource,
                    methodFound,
                    methodStatic,
                    methodPublic,
                    methodParameterCount,
                    ready,
                    status,
                    issueCode,
                    detail));
            }

            return plans;
        }

        private static NativeAssemblyMetadata InspectAssemblyMetadata(string assemblyPath, string typeName, string methodName)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return NativeAssemblyMetadata.Missing("missing-assembly", "No assembly path is available for metadata validation.");
            }

            if (!File.Exists(assemblyPath))
            {
                return NativeAssemblyMetadata.Missing("missing-assembly", "Assembly file was not found for metadata validation.");
            }

            try
            {
                using (var stream = File.OpenRead(assemblyPath))
                using (var peReader = new PEReader(stream))
                {
                    if (!peReader.HasMetadata)
                    {
                        return NativeAssemblyMetadata.Missing("invalid-assembly-metadata", "Assembly file does not contain managed metadata.");
                    }

                    var reader = peReader.GetMetadataReader();
                    var assemblyName = "";
                    var assemblyIdentity = "";
                    var assemblyReferences = ReadAssemblyReferences(reader, Path.GetDirectoryName(assemblyPath));
                    if (reader.IsAssembly)
                    {
                        var definition = reader.GetAssemblyDefinition();
                        assemblyName = reader.GetString(definition.Name);
                        assemblyIdentity = string.IsNullOrWhiteSpace(assemblyName)
                            ? ""
                            : assemblyName + ", Version=" + definition.Version;
                    }

                    var normalizedTypeName = NormalizeEntrypoint(typeName);
                    var normalizedMethodName = NormalizeEntrypoint(methodName);
                    var typeCount = 0;
                    var typeFound = false;
                    var matchedTypeName = "";
                    var methodFound = false;
                    var methodStatic = false;
                    var methodPublic = false;
                    var methodParameterCount = 0;
                    var matchedMethods = new List<NativeMethodMetadata>();
                    var typePublic = false;
                    var constructorFound = false;
                    var constructorPublic = false;
                    var constructorParameterless = false;
                    var exactTypeHandle = default(TypeDefinitionHandle);
                    var shortTypeHandle = default(TypeDefinitionHandle);
                    var shortTypeMatchCount = 0;

                    foreach (var typeHandle in reader.TypeDefinitions)
                    {
                        typeCount++;
                        if (string.IsNullOrWhiteSpace(normalizedTypeName))
                        {
                            continue;
                        }

                        var typeDefinition = reader.GetTypeDefinition(typeHandle);
                        var name = reader.GetString(typeDefinition.Name);
                        var fullName = GetMetadataTypeFullName(reader, typeHandle);

                        if (string.Equals(fullName, normalizedTypeName, StringComparison.Ordinal))
                        {
                            exactTypeHandle = typeHandle;
                        }

                        if (string.Equals(name, normalizedTypeName, StringComparison.Ordinal))
                        {
                            shortTypeHandle = typeHandle;
                            shortTypeMatchCount++;
                        }
                    }

                    if (exactTypeHandle.IsNil && shortTypeMatchCount > 1)
                    {
                        return NativeAssemblyMetadata.Missing("ambiguous-entrypoint-type", "Multiple types match the entrypoint short name; specify the full CLR type name.");
                    }

                    var matchedTypeHandle = exactTypeHandle.IsNil ? shortTypeHandle : exactTypeHandle;
                    if (!matchedTypeHandle.IsNil)
                    {
                        var typeDefinition = reader.GetTypeDefinition(matchedTypeHandle);
                        typeFound = true;
                        matchedTypeName = GetMetadataTypeFullName(reader, matchedTypeHandle);
                        typePublic = IsPublicType(typeDefinition.Attributes);
                        var declaringTypeHandle = typeDefinition.GetDeclaringType();
                        while (typePublic && !declaringTypeHandle.IsNil)
                        {
                            var declaringType = reader.GetTypeDefinition(declaringTypeHandle);
                            typePublic = IsPublicType(declaringType.Attributes);
                            declaringTypeHandle = declaringType.GetDeclaringType();
                        }

                        foreach (var methodHandle in typeDefinition.GetMethods())
                        {
                            var methodDefinition = reader.GetMethodDefinition(methodHandle);
                            var candidateName = reader.GetString(methodDefinition.Name);
                            var parameterCount = CountMethodParameters(reader, methodDefinition);
                            var isPublicMethod = (methodDefinition.Attributes & MethodAttributes.Public) == MethodAttributes.Public;
                            var isStaticMethod = (methodDefinition.Attributes & MethodAttributes.Static) == MethodAttributes.Static;
                            if (string.Equals(candidateName, ".ctor", StringComparison.Ordinal))
                            {
                                constructorFound = true;
                                if (isPublicMethod)
                                {
                                    constructorPublic = true;
                                }

                                if (isPublicMethod && parameterCount == 0)
                                {
                                    constructorParameterless = true;
                                }

                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(candidateName) || candidateName.StartsWith(".", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            matchedMethods.Add(new NativeMethodMetadata(candidateName, isStaticMethod, isPublicMethod, parameterCount));
                        }

                        var entrypointMethod = SelectBestMethod(matchedMethods, normalizedMethodName);
                        if (entrypointMethod != null)
                        {
                            methodFound = true;
                            methodStatic = entrypointMethod.IsStatic;
                            methodPublic = entrypointMethod.IsPublic;
                            methodParameterCount = entrypointMethod.ParameterCount;
                        }
                    }

                    return new NativeAssemblyMetadata(
                        true,
                        "",
                        "Assembly metadata was read successfully.",
                        assemblyName,
                        assemblyIdentity,
                        typeCount,
                        matchedTypeName,
                        assemblyReferences,
                        matchedMethods,
                        typePublic,
                        constructorFound,
                        constructorPublic,
                        constructorParameterless,
                        typeFound,
                        methodFound,
                        methodStatic,
                        methodPublic,
                        methodParameterCount);
                }
            }
            catch (BadImageFormatException ex)
            {
                return NativeAssemblyMetadata.Missing("invalid-assembly-image", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (IOException ex)
            {
                return NativeAssemblyMetadata.Missing("assembly-metadata-io", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return NativeAssemblyMetadata.Missing("assembly-metadata-access", "Assembly metadata could not be read: " + ex.Message);
            }
            catch (Exception ex)
            {
                return NativeAssemblyMetadata.Missing("assembly-metadata-error", "Assembly metadata could not be read: " + ex.Message);
            }
        }

        private static string GetMetadataTypeFullName(MetadataReader reader, TypeDefinitionHandle typeHandle)
        {
            var typeDefinition = reader.GetTypeDefinition(typeHandle);
            var name = reader.GetString(typeDefinition.Name);
            var declaringTypeHandle = typeDefinition.GetDeclaringType();
            if (!declaringTypeHandle.IsNil)
            {
                return GetMetadataTypeFullName(reader, declaringTypeHandle) + "+" + name;
            }

            var typeNamespace = reader.GetString(typeDefinition.Namespace);
            return string.IsNullOrWhiteSpace(typeNamespace) ? name : typeNamespace + "." + name;
        }

        private static bool IsPublicType(TypeAttributes attributes)
        {
            var visibility = attributes & TypeAttributes.VisibilityMask;
            return visibility == TypeAttributes.Public || visibility == TypeAttributes.NestedPublic;
        }

        private static int CountMethodParameters(MetadataReader reader, MethodDefinition methodDefinition)
        {
            var signature = reader.GetBlobReader(methodDefinition.Signature);
            var header = signature.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                signature.ReadCompressedInteger(); // GenericParameterCount precedes ParameterCount.
            }

            return signature.ReadCompressedInteger();
        }

        private static NativeMethodMetadata SelectBestMethod(IReadOnlyList<NativeMethodMetadata> methods, string methodName)
        {
            if (methods == null || string.IsNullOrWhiteSpace(methodName))
            {
                return null;
            }

            return methods
                .Where(method => method != null && string.Equals(method.MethodName, methodName, StringComparison.Ordinal))
                .OrderByDescending(method => method.IsPublic && method.IsStatic && method.ParameterCount == 0)
                .ThenBy(method => method.ParameterCount)
                .FirstOrDefault();
        }

        private static IReadOnlyList<OpenACAINativeModAssemblyReferenceSnapshot> ReadAssemblyReferences(MetadataReader reader, string requestingDirectory)
        {
            if (reader == null)
            {
                return Array.Empty<OpenACAINativeModAssemblyReferenceSnapshot>();
            }

            var references = new List<OpenACAINativeModAssemblyReferenceSnapshot>();
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

        private static OpenACAINativeModAssemblyReferenceSnapshot ResolveAssemblyReference(string assemblyName, string requiredVersion, string requestingDirectory)
        {
            var normalizedName = string.IsNullOrWhiteSpace(assemblyName) ? "" : assemblyName.Trim();
            var normalizedVersion = string.IsNullOrWhiteSpace(requiredVersion) ? "" : requiredVersion.Trim();
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return new OpenACAINativeModAssemblyReferenceSnapshot(
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
                return new OpenACAINativeModAssemblyReferenceSnapshot(
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
            if (OpenACAIAssemblyResolver.TryFindAssemblyPath(normalizedName, requestingDirectory, out resolvedPath, out resolvedSource))
            {
                return new OpenACAINativeModAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    string.IsNullOrWhiteSpace(resolvedSource) ? "resolver" : resolvedSource,
                    SafeAssemblyVersion(resolvedPath),
                    resolvedPath,
                    "Assembly reference was found through OpenACAI resolver roots.");
            }

            if (IsFrameworkReference(normalizedName))
            {
                return new OpenACAINativeModAssemblyReferenceSnapshot(
                    normalizedName,
                    normalizedVersion,
                    true,
                    "framework-reference",
                    "",
                    "",
                    "Assembly reference is supplied by the current .NET runtime.");
            }

            return new OpenACAINativeModAssemblyReferenceSnapshot(
                normalizedName,
                normalizedVersion,
                false,
                "unresolved",
                "",
                "",
                "No loaded assembly or OpenACAI resolver path matched this assembly reference.");
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

        private static string NormalizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            return trimmed.Length <= MaxModIdLength ? trimmed : trimmed.Substring(0, MaxModIdLength);
        }

        private static string NormalizeEntrypoint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            return trimmed.Length <= MaxEntrypointLength ? trimmed : trimmed.Substring(0, MaxEntrypointLength);
        }

        private static string NormalizeLifecycleHookMethod(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            return trimmed.Length <= MaxLifecycleHookMethodLength ? trimmed : trimmed.Substring(0, MaxLifecycleHookMethodLength);
        }

        private static string NormalizeLifecycleHook(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            return trimmed.Length <= MaxLifecycleHookLength ? trimmed : trimmed.Substring(0, MaxLifecycleHookLength);
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Native Host",
                "OpenACAI-owned native package host gate and entrypoint validation.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateCandidateSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAINativeModHostSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Native Host Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned native mod host snapshot."),
                    Row("Package Root", ShortPath(snapshot.NativeModRoot), snapshot.NativeModRootExists ? "READY" : "CHECK", snapshot.NativeModRoot),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "OpenACAI validates native mod host intent without RedLoader/BepInEx hosting paths."),
                    Row("Runtime Execution", snapshot.RuntimeExecutionEnabled ? "Enabled" : "Disabled", snapshot.RuntimeExecutionEnabled ? "READY" : "INFO", "Startup-owned execution; diagnostic plans never invoke mod code."),
                    Row("Runtime Results", OpenACAINativeModRuntime.State.LoadedCount + " loaded", OpenACAINativeModRuntime.State.FailedCount == 0 ? "INFO" : "CHECK", OpenACAINativeModRuntime.State.FailedCount + " failed, " + OpenACAINativeModRuntime.State.DeferredCount + " deferred."),
                    Row("Native Mods", snapshot.CandidateCount.ToString(), snapshot.ValidationIssueCount == 0 ? "READY" : "CHECK", snapshot.HostableCandidateCount + " hostable, " + snapshot.MetadataOnlyCandidateCount + " metadata-only."),
                    Row("Assembly Metadata", snapshot.AssemblyMetadataReadCount + " read", snapshot.AssemblyMetadataIssueCount == 0 ? "READY" : "CHECK", snapshot.AssemblyMetadataIssueCount + " metadata issue(s); " + snapshot.EntrypointTypeMatchCount + " entrypoint type match(es), " + snapshot.EntrypointMethodMatchCount + " method match(es)."),
                    Row("Assembly References", snapshot.ResolvedAssemblyReferenceCount + "/" + snapshot.AssemblyReferenceCount, snapshot.UnresolvedAssemblyReferenceCount == 0 ? "READY" : "CHECK", snapshot.UnresolvedAssemblyReferenceCount + " unresolved assembly reference(s)."),
                    Row("Execution Plans", snapshot.ExecutionPlanReadyCount + "/" + snapshot.CandidateCount, snapshot.ExecutionPlanIssueCount == 0 ? "READY" : "CHECK", snapshot.ExecutionPlanIssueCount + " execution plan issue(s); " + snapshot.LifecycleHookDeclarationCount + " lifecycle hook declaration(s)."),
                    Row("Hook Methods", snapshot.LifecycleHookMethodReadyCount + "/" + snapshot.LifecycleHookDeclarationCount, snapshot.LifecycleHookMethodIssueCount == 0 ? "READY" : "CHECK", snapshot.LifecycleHookMethodIssueCount + " lifecycle hook method issue(s)."),
                    Row("Libraries Excluded", snapshot.LibraryExcludedCount.ToString(), "READY", "Inventory libraries are surfaced through Libraries and excluded from native mod host candidates.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateCandidateSection(OpenACAINativeModHostSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Candidates", snapshot.CandidateCount.ToString(), snapshot.ValidationIssueCount == 0 ? "READY" : "CHECK", snapshot.ValidationIssueCount + " validation issue(s).")
            };

            foreach (var candidate in snapshot.Candidates.Take(10))
            {
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(candidate.DisplayName) ? candidate.ModId : candidate.DisplayName,
                    string.IsNullOrWhiteSpace(candidate.EntrypointKind) ? "none" : candidate.EntrypointKind,
                    candidate.Status,
                    candidate.Detail + " Assembly metadata: " + (candidate.AssemblyMetadataReadable ? "read" : "not read") + "; references " + candidate.ResolvedAssemblyReferenceCount + "/" + candidate.AssemblyReferenceCount + "; execution " + (candidate.ExecutionPlan == null ? "none" : candidate.ExecutionPlan.InvocationKind) + "; hook methods " + (candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleHookMethodReadyCount) + "/" + (candidate.ExecutionPlan == null ? 0 : candidate.ExecutionPlan.LifecycleHookCount) + "."));
            }

            if (snapshot.CandidateCount == 0)
            {
                rows.Add(Row("No Native Mods", "0", "INFO", "No Modstoke-native mod packages were discovered under OpenACAI/Mods."));
            }

            return new OpenACAIMenuSectionSnapshot("Candidates", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAINativeModHostSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Plan Activity",
                new[]
                {
                    Row("Plans", snapshot.PlanRequestCount + " requested", "INFO", snapshot.AcceptedPlanCount + " accepted, " + snapshot.RejectedPlanCount + " rejected."),
                    Row("Last Mod", string.IsNullOrWhiteSpace(snapshot.LastPlanModId) ? "none" : snapshot.LastPlanModId, "INFO", "Last native mod host plan target."),
                    Row("Last Plan", snapshot.LastPlanStatus, snapshot.LastPlanStatus, snapshot.LastPlanDetail),
                    Row("Last Reason", snapshot.LastPlanReason, snapshot.LastPlanStatus, "Last native mod host plan reason code.")
                });
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unavailable";
            }

            try
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.GetFileName(trimmed);
            }
            catch
            {
                return path;
            }
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.NativeModHost;
            public string DisplayName => "Modstoke Native Host";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 706;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned native mod host and entrypoint gate state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAINativeModHost.CreateContent();
            }
        }
    }

    public sealed class OpenACAINativeModHostSnapshot
    {
        internal OpenACAINativeModHostSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            string nativeModRoot,
            bool nativeModRootExists,
            bool planningEnabled,
            bool runtimeExecutionEnabled,
            IReadOnlyList<OpenACAINativeModCandidateSnapshot> candidates,
            int libraryExcludedCount,
            int assemblyMetadataReadCount,
            int assemblyMetadataIssueCount,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            int executionPlanReadyCount,
            int executionPlanIssueCount,
            int lifecycleHookDeclarationCount,
            int lifecycleHookMethodReadyCount,
            int lifecycleHookMethodIssueCount,
            int entrypointTypeMatchCount,
            int entrypointMethodMatchCount,
            int hostableCandidateCount,
            int metadataOnlyCandidateCount,
            int validationIssueCount,
            int planRequestCount,
            int acceptedPlanCount,
            int rejectedPlanCount,
            string lastPlanModId,
            string lastPlanStatus,
            string lastPlanDetail,
            string lastPlanReason)
        {
            ServiceKey = serviceKey ?? OpenACAINativeModHost.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            NativeModRoot = nativeModRoot ?? "";
            NativeModRootExists = nativeModRootExists;
            PlanningEnabled = planningEnabled;
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
            Candidates = candidates ?? Array.Empty<OpenACAINativeModCandidateSnapshot>();
            CandidateCount = Candidates.Count;
            LibraryExcludedCount = Math.Max(0, libraryExcludedCount);
            AssemblyMetadataReadCount = Math.Max(0, assemblyMetadataReadCount);
            AssemblyMetadataIssueCount = Math.Max(0, assemblyMetadataIssueCount);
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            ExecutionPlanReadyCount = Math.Max(0, executionPlanReadyCount);
            ExecutionPlanIssueCount = Math.Max(0, executionPlanIssueCount);
            LifecycleHookDeclarationCount = Math.Max(0, lifecycleHookDeclarationCount);
            LifecycleHookMethodReadyCount = Math.Max(0, lifecycleHookMethodReadyCount);
            LifecycleHookMethodIssueCount = Math.Max(0, lifecycleHookMethodIssueCount);
            EntrypointTypeMatchCount = Math.Max(0, entrypointTypeMatchCount);
            EntrypointMethodMatchCount = Math.Max(0, entrypointMethodMatchCount);
            HostableCandidateCount = Math.Max(0, hostableCandidateCount);
            MetadataOnlyCandidateCount = Math.Max(0, metadataOnlyCandidateCount);
            ValidationIssueCount = Math.Max(0, validationIssueCount);
            PlanRequestCount = Math.Max(0, planRequestCount);
            AcceptedPlanCount = Math.Max(0, acceptedPlanCount);
            RejectedPlanCount = Math.Max(0, rejectedPlanCount);
            LastPlanModId = lastPlanModId ?? "";
            LastPlanStatus = lastPlanStatus ?? "";
            LastPlanDetail = lastPlanDetail ?? "";
            LastPlanReason = lastPlanReason ?? "";
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public string NativeModRoot { get; }
        public bool NativeModRootExists { get; }
        public bool PlanningEnabled { get; }
        public bool RuntimeExecutionEnabled { get; }
        public IReadOnlyList<OpenACAINativeModCandidateSnapshot> Candidates { get; }
        public int CandidateCount { get; }
        public int LibraryExcludedCount { get; }
        public int AssemblyMetadataReadCount { get; }
        public int AssemblyMetadataIssueCount { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public int ExecutionPlanReadyCount { get; }
        public int ExecutionPlanIssueCount { get; }
        public int LifecycleHookDeclarationCount { get; }
        public int LifecycleHookMethodReadyCount { get; }
        public int LifecycleHookMethodIssueCount { get; }
        public int EntrypointTypeMatchCount { get; }
        public int EntrypointMethodMatchCount { get; }
        public int HostableCandidateCount { get; }
        public int MetadataOnlyCandidateCount { get; }
        public int ValidationIssueCount { get; }
        public int PlanRequestCount { get; }
        public int AcceptedPlanCount { get; }
        public int RejectedPlanCount { get; }
        public string LastPlanModId { get; }
        public string LastPlanStatus { get; }
        public string LastPlanDetail { get; }
        public string LastPlanReason { get; }

        internal static OpenACAINativeModHostSnapshot CreateUnknown()
        {
            return new OpenACAINativeModHostSnapshot(
                OpenACAINativeModHost.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Modstoke native mod host has not refreshed yet.",
                "",
                false,
                true,
                OpenACAINativeModHost.RuntimeExecutionEnabled,
                Array.Empty<OpenACAINativeModCandidateSnapshot>(),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "",
                "INFO",
                "No native Modstoke mod host plan has been validated.",
                "none");
        }
    }

    public sealed class OpenACAINativeModCandidateSnapshot
    {
        internal OpenACAINativeModCandidateSnapshot(
            string modId,
            string displayName,
            string version,
            string author,
            string manifestType,
            string packagePath,
            string manifestPath,
            string assemblyPath,
            bool enabled,
            bool hasAssembly,
            string assemblyVersion,
            long assemblySize,
            bool assemblyMetadataReadable,
            string assemblyName,
            string assemblyIdentity,
            int assemblyDefinedTypeCount,
            string matchedEntrypointTypeName,
            IReadOnlyList<OpenACAINativeModAssemblyReferenceSnapshot> assemblyReferences,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            OpenACAINativeModExecutionPlanSnapshot executionPlan,
            bool entrypointTypeFound,
            bool entrypointMethodFound,
            bool entrypointMethodStatic,
            bool entrypointMethodPublic,
            string entrypointTypeName,
            string entrypointMethodName,
            string entrypointKind,
            bool hostable,
            bool metadataOnly,
            string status,
            string issueCode,
            string detail)
        {
            ModId = modId ?? "";
            DisplayName = displayName ?? "";
            Version = version ?? "";
            Author = author ?? "";
            ManifestType = manifestType ?? "";
            PackagePath = packagePath ?? "";
            ManifestPath = manifestPath ?? "";
            AssemblyPath = assemblyPath ?? "";
            Enabled = enabled;
            HasAssembly = hasAssembly;
            AssemblyVersion = assemblyVersion ?? "";
            AssemblySize = Math.Max(0, assemblySize);
            AssemblyMetadataReadable = assemblyMetadataReadable;
            AssemblyName = assemblyName ?? "";
            AssemblyIdentity = assemblyIdentity ?? "";
            AssemblyDefinedTypeCount = Math.Max(0, assemblyDefinedTypeCount);
            MatchedEntrypointTypeName = matchedEntrypointTypeName ?? "";
            AssemblyReferences = assemblyReferences ?? Array.Empty<OpenACAINativeModAssemblyReferenceSnapshot>();
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            ExecutionPlan = executionPlan ?? OpenACAINativeModExecutionPlanSnapshot.Empty(ModId);
            EntrypointTypeFound = entrypointTypeFound;
            EntrypointMethodFound = entrypointMethodFound;
            EntrypointMethodStatic = entrypointMethodStatic;
            EntrypointMethodPublic = entrypointMethodPublic;
            EntrypointTypeName = entrypointTypeName ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            EntrypointKind = entrypointKind ?? "";
            Hostable = hostable;
            MetadataOnly = metadataOnly;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string ModId { get; }
        public string DisplayName { get; }
        public string Version { get; }
        public string Author { get; }
        public string ManifestType { get; }
        public string PackagePath { get; }
        public string ManifestPath { get; }
        public string AssemblyPath { get; }
        public bool Enabled { get; }
        public bool HasAssembly { get; }
        public string AssemblyVersion { get; }
        public long AssemblySize { get; }
        public bool AssemblyMetadataReadable { get; }
        public string AssemblyName { get; }
        public string AssemblyIdentity { get; }
        public int AssemblyDefinedTypeCount { get; }
        public string MatchedEntrypointTypeName { get; }
        public IReadOnlyList<OpenACAINativeModAssemblyReferenceSnapshot> AssemblyReferences { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public OpenACAINativeModExecutionPlanSnapshot ExecutionPlan { get; }
        public bool ExecutionPlanReady => ExecutionPlan != null && ExecutionPlan.Ready;
        public bool ExecutionPlanIssue => ExecutionPlan != null && string.Equals(ExecutionPlan.Status, "CHECK", StringComparison.OrdinalIgnoreCase);
        public bool EntrypointTypeFound { get; }
        public bool EntrypointMethodFound { get; }
        public bool EntrypointMethodStatic { get; }
        public bool EntrypointMethodPublic { get; }
        public string EntrypointTypeName { get; }
        public string EntrypointMethodName { get; }
        public string EntrypointKind { get; }
        public bool Hostable { get; }
        public bool MetadataOnly { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool ValidationIssue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class OpenACAINativeModExecutionPlanSnapshot
    {
        internal OpenACAINativeModExecutionPlanSnapshot(
            string modId,
            bool ready,
            string status,
            string issueCode,
            string detail,
            string invocationKind,
            string entrypointTypeName,
            string entrypointMethodName,
            IReadOnlyList<string> lifecycleHooks,
            IReadOnlyList<OpenACAINativeLifecycleHookPlanSnapshot> lifecycleHookPlans,
            bool requiresInstance,
            bool constructorRequired,
            bool constructorReady,
            bool entrypointTypePublic,
            bool entrypointConstructorFound,
            bool entrypointConstructorPublic,
            bool entrypointConstructorParameterless,
            int methodParameterCount,
            bool runtimeExecutionEnabled)
        {
            ModId = modId ?? "";
            Ready = ready;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            InvocationKind = invocationKind ?? "";
            EntrypointTypeName = entrypointTypeName ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            LifecycleHooks = lifecycleHooks ?? Array.Empty<string>();
            LifecycleHookCount = LifecycleHooks.Count;
            LifecycleHookPlans = lifecycleHookPlans ?? Array.Empty<OpenACAINativeLifecycleHookPlanSnapshot>();
            LifecycleHookPlanCount = LifecycleHookPlans.Count;
            LifecycleHookMethodReadyCount = LifecycleHookPlans.Count(hookPlan => hookPlan != null && hookPlan.Ready);
            LifecycleHookMethodIssueCount = LifecycleHookPlans.Count(hookPlan => hookPlan != null && hookPlan.Issue);
            RequiresInstance = requiresInstance;
            ConstructorRequired = constructorRequired;
            ConstructorReady = constructorReady;
            EntrypointTypePublic = entrypointTypePublic;
            EntrypointConstructorFound = entrypointConstructorFound;
            EntrypointConstructorPublic = entrypointConstructorPublic;
            EntrypointConstructorParameterless = entrypointConstructorParameterless;
            MethodParameterCount = Math.Max(0, methodParameterCount);
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
        }

        public int Schema => 1;
        public string ModId { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public string InvocationKind { get; }
        public string EntrypointTypeName { get; }
        public string EntrypointMethodName { get; }
        public IReadOnlyList<string> LifecycleHooks { get; }
        public int LifecycleHookCount { get; }
        public IReadOnlyList<OpenACAINativeLifecycleHookPlanSnapshot> LifecycleHookPlans { get; }
        public int LifecycleHookPlanCount { get; }
        public int LifecycleHookMethodReadyCount { get; }
        public int LifecycleHookMethodIssueCount { get; }
        public bool RequiresInstance { get; }
        public bool ConstructorRequired { get; }
        public bool ConstructorReady { get; }
        public bool EntrypointTypePublic { get; }
        public bool EntrypointConstructorFound { get; }
        public bool EntrypointConstructorPublic { get; }
        public bool EntrypointConstructorParameterless { get; }
        public int MethodParameterCount { get; }
        public bool RuntimeExecutionEnabled { get; }

        internal static OpenACAINativeModExecutionPlanSnapshot Empty(string modId)
        {
            return new OpenACAINativeModExecutionPlanSnapshot(
                modId,
                false,
                "INFO",
                "none",
                "No OpenACAI native execution plan was created.",
                "none",
                "",
                "",
                Array.Empty<string>(),
                Array.Empty<OpenACAINativeLifecycleHookPlanSnapshot>(),
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                0,
                OpenACAINativeModHost.RuntimeExecutionEnabled);
        }
    }

    public sealed class OpenACAINativeModAssemblyReferenceSnapshot
    {
        internal OpenACAINativeModAssemblyReferenceSnapshot(
            string assemblyName,
            string requiredVersion,
            bool resolved,
            string source,
            string resolvedVersion,
            string resolvedPath,
            string detail)
        {
            AssemblyName = assemblyName ?? "";
            RequiredVersion = requiredVersion ?? "";
            Resolved = resolved;
            Source = source ?? "";
            ResolvedVersion = resolvedVersion ?? "";
            ResolvedPath = resolvedPath ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string AssemblyName { get; }
        public string RequiredVersion { get; }
        public bool Resolved { get; }
        public string Source { get; }
        public string ResolvedVersion { get; }
        public string ResolvedPath { get; }
        public string Detail { get; }
    }

    public sealed class OpenACAINativeLifecycleHookPlanSnapshot
    {
        internal OpenACAINativeLifecycleHookPlanSnapshot(
            string modId,
            string hookName,
            string methodName,
            bool methodDeclared,
            string methodSource,
            bool methodFound,
            bool methodStatic,
            bool methodPublic,
            int methodParameterCount,
            bool ready,
            string status,
            string issueCode,
            string detail)
        {
            ModId = modId ?? "";
            HookName = hookName ?? "";
            MethodName = methodName ?? "";
            MethodDeclared = methodDeclared;
            MethodSource = methodSource ?? "";
            MethodFound = methodFound;
            MethodStatic = methodStatic;
            MethodPublic = methodPublic;
            MethodParameterCount = Math.Max(0, methodParameterCount);
            Ready = ready;
            Status = status ?? "";
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string ModId { get; }
        public string HookName { get; }
        public string MethodName { get; }
        public bool MethodDeclared { get; }
        public string MethodSource { get; }
        public bool MethodFound { get; }
        public bool MethodStatic { get; }
        public bool MethodPublic { get; }
        public int MethodParameterCount { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string IssueCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class NativeMethodMetadata
    {
        internal NativeMethodMetadata(string methodName, bool isStatic, bool isPublic, int parameterCount)
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

    internal sealed class NativeAssemblyMetadata
    {
        internal NativeAssemblyMetadata(
            bool readable,
            string issueCode,
            string detail,
            string assemblyName,
            string assemblyIdentity,
            int definedTypeCount,
            string matchedTypeName,
            IReadOnlyList<OpenACAINativeModAssemblyReferenceSnapshot> assemblyReferences,
            IReadOnlyList<NativeMethodMetadata> matchedMethods,
            bool entrypointTypePublic,
            bool entrypointConstructorFound,
            bool entrypointConstructorPublic,
            bool entrypointConstructorParameterless,
            bool entrypointTypeFound,
            bool entrypointMethodFound,
            bool entrypointMethodStatic,
            bool entrypointMethodPublic,
            int entrypointMethodParameterCount)
        {
            Readable = readable;
            IssueCode = issueCode ?? "";
            Detail = detail ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyIdentity = assemblyIdentity ?? "";
            DefinedTypeCount = Math.Max(0, definedTypeCount);
            MatchedTypeName = matchedTypeName ?? "";
            AssemblyReferences = assemblyReferences ?? Array.Empty<OpenACAINativeModAssemblyReferenceSnapshot>();
            MatchedMethods = matchedMethods ?? Array.Empty<NativeMethodMetadata>();
            AssemblyReferenceCount = AssemblyReferences.Count;
            ResolvedAssemblyReferenceCount = AssemblyReferences.Count(reference => reference.Resolved);
            UnresolvedAssemblyReferenceCount = AssemblyReferences.Count(reference => !reference.Resolved);
            EntrypointTypePublic = entrypointTypePublic;
            EntrypointConstructorFound = entrypointConstructorFound;
            EntrypointConstructorPublic = entrypointConstructorPublic;
            EntrypointConstructorParameterless = entrypointConstructorParameterless;
            EntrypointTypeFound = entrypointTypeFound;
            EntrypointMethodFound = entrypointMethodFound;
            EntrypointMethodStatic = entrypointMethodStatic;
            EntrypointMethodPublic = entrypointMethodPublic;
            EntrypointMethodParameterCount = Math.Max(0, entrypointMethodParameterCount);
        }

        internal bool Readable { get; }
        internal string IssueCode { get; }
        internal string Detail { get; }
        internal string AssemblyName { get; }
        internal string AssemblyIdentity { get; }
        internal int DefinedTypeCount { get; }
        internal string MatchedTypeName { get; }
        internal IReadOnlyList<OpenACAINativeModAssemblyReferenceSnapshot> AssemblyReferences { get; }
        internal IReadOnlyList<NativeMethodMetadata> MatchedMethods { get; }
        internal int AssemblyReferenceCount { get; }
        internal int ResolvedAssemblyReferenceCount { get; }
        internal int UnresolvedAssemblyReferenceCount { get; }
        internal bool EntrypointTypePublic { get; }
        internal bool EntrypointConstructorFound { get; }
        internal bool EntrypointConstructorPublic { get; }
        internal bool EntrypointConstructorParameterless { get; }
        internal bool EntrypointTypeFound { get; }
        internal bool EntrypointMethodFound { get; }
        internal bool EntrypointMethodStatic { get; }
        internal bool EntrypointMethodPublic { get; }
        internal int EntrypointMethodParameterCount { get; }

        internal NativeMethodMetadata FindMethod(string methodName)
        {
            if (MatchedMethods == null || string.IsNullOrWhiteSpace(methodName))
            {
                return null;
            }

            var normalizedMethodName = methodName.Trim();
            if (normalizedMethodName.Length > OpenACAINativeModHost.MaxLifecycleHookMethodLength)
            {
                normalizedMethodName = normalizedMethodName.Substring(0, OpenACAINativeModHost.MaxLifecycleHookMethodLength);
            }

            return MatchedMethods
                .Where(method => method != null && string.Equals(method.MethodName, normalizedMethodName, StringComparison.Ordinal))
                .OrderByDescending(method => method.IsPublic && method.IsStatic && method.ParameterCount == 0)
                .ThenBy(method => method.ParameterCount)
                .FirstOrDefault();
        }

        internal static NativeAssemblyMetadata Missing(string issueCode, string detail)
        {
            return new NativeAssemblyMetadata(
                false,
                issueCode,
                detail,
                "",
                "",
                0,
                "",
                Array.Empty<OpenACAINativeModAssemblyReferenceSnapshot>(),
                Array.Empty<NativeMethodMetadata>(),
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                0);
        }
    }

    public sealed class OpenACAINativeModHostPlan
    {
        internal OpenACAINativeModHostPlan(
            string modId,
            string displayName,
            string assemblyPath,
            string entrypointTypeName,
            string entrypointMethodName,
            string invocationKind,
            IReadOnlyList<string> lifecycleHooks,
            bool executionPlanReady,
            int methodParameterCount,
            bool requiresInstance,
            bool constructorRequired,
            bool constructorReady,
            bool accepted,
            bool canLoadNow,
            bool runtimeExecutionEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            ModId = modId ?? "";
            DisplayName = displayName ?? "";
            AssemblyPath = assemblyPath ?? "";
            EntrypointTypeName = entrypointTypeName ?? "";
            EntrypointMethodName = entrypointMethodName ?? "";
            InvocationKind = invocationKind ?? "";
            LifecycleHooks = lifecycleHooks ?? Array.Empty<string>();
            LifecycleHookCount = LifecycleHooks.Count;
            ExecutionPlanReady = executionPlanReady;
            MethodParameterCount = Math.Max(0, methodParameterCount);
            RequiresInstance = requiresInstance;
            ConstructorRequired = constructorRequired;
            ConstructorReady = constructorReady;
            Accepted = accepted;
            CanLoadNow = canLoadNow;
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public string ModId { get; }
        public string DisplayName { get; }
        public string AssemblyPath { get; }
        public string EntrypointTypeName { get; }
        public string EntrypointMethodName { get; }
        public string InvocationKind { get; }
        public IReadOnlyList<string> LifecycleHooks { get; }
        public int LifecycleHookCount { get; }
        public bool ExecutionPlanReady { get; }
        public int MethodParameterCount { get; }
        public bool RequiresInstance { get; }
        public bool ConstructorRequired { get; }
        public bool ConstructorReady { get; }
        public bool Accepted { get; }
        public bool CanLoadNow { get; }
        public bool RuntimeExecutionEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }
}
