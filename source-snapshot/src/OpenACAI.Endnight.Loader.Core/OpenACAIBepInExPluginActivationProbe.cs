// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned validation-probe activation for legacy BepInEx candidates.
// This is deliberately validation-probe only: it may load the generated probe
// assembly, optionally resolve its type, and an explicit instance probe may
// construct only that generated empty plugin. An explicit load probe may call
// Load() only on the generated empty validation plugin, never on third-party
// plugin code.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAIBepInExPluginActivationProbe
    {
        public const string ServiceKey = "openacai.loader.bepinexPluginActivationProbe";
        public const int MaxAttemptCount = 32;
        public const string ActivationKindAssemblyLoad = "validation-assembly-load";
        public const string ActivationKindTypeActivation = "validation-type-activation";
        public const string ActivationKindInstanceCreation = "validation-instance-creation";
        public const string ActivationKindLoadInvocation = "validation-load-invocation";
        private const string ContextNamePrefix = "openacai.bepinex.validation-probe";
        private static readonly object SyncRoot = new object();
        private static readonly List<OpenACAIBepInExPluginActivationProbeAttemptSnapshot> Attempts =
            new List<OpenACAIBepInExPluginActivationProbeAttemptSnapshot>();
        private static OpenACAIBepInExPluginActivationProbeStateSnapshot _state =
            OpenACAIBepInExPluginActivationProbeStateSnapshot.CreateInitial();

        public static OpenACAIBepInExPluginActivationProbeStateSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static OpenACAIBepInExPluginActivationProbeStateSnapshot CreateState()
        {
            return State;
        }

        public static OpenACAIBepInExPluginActivationProbeAttemptSnapshot ValidateAssemblyLoad(
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            var attempt = CreateAttempt(candidate, false, false, false);
            RecordAttempt(attempt);
            return attempt;
        }

        public static OpenACAIBepInExPluginActivationProbeAttemptSnapshot ValidateTypeResolution(
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            var attempt = CreateAttempt(candidate, true, false, false);
            RecordAttempt(attempt);
            return attempt;
        }

        public static OpenACAIBepInExPluginActivationProbeAttemptSnapshot ValidateInstanceCreation(
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            var attempt = CreateAttempt(candidate, true, true, false);
            RecordAttempt(attempt);
            return attempt;
        }

        public static OpenACAIBepInExPluginActivationProbeAttemptSnapshot ValidateLoadInvocation(
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate)
        {
            var attempt = CreateAttempt(candidate, true, true, true);
            RecordAttempt(attempt);
            return attempt;
        }

        private static void RecordAttempt(OpenACAIBepInExPluginActivationProbeAttemptSnapshot attempt)
        {
            lock (SyncRoot)
            {
                Attempts.Add(attempt);
                while (Attempts.Count > MaxAttemptCount)
                {
                    Attempts.RemoveAt(0);
                }

                _state = OpenACAIBepInExPluginActivationProbeStateSnapshot.Create(Attempts);
            }
        }

        private static OpenACAIBepInExPluginActivationProbeAttemptSnapshot CreateAttempt(
            OpenACAIBepInExPluginCompatibilityCandidateSnapshot candidate,
            bool resolvePluginType,
            bool createPluginInstance,
            bool invokeLoadMethod)
        {
            var generatedUtc = DateTimeOffset.UtcNow.ToString("O");
            var attemptIdPrefix = invokeLoadMethod
                ? "bepinex-load-probe-"
                : createPluginInstance
                    ? "bepinex-instance-probe-"
                    : resolvePluginType
                        ? "bepinex-activation-probe-"
                        : "bepinex-assembly-probe-";
            var activationKind = invokeLoadMethod
                ? ActivationKindLoadInvocation
                : createPluginInstance
                    ? ActivationKindInstanceCreation
                    : resolvePluginType
                        ? ActivationKindTypeActivation
                        : ActivationKindAssemblyLoad;
            var attemptId = attemptIdPrefix + generatedUtc.Replace(":", "").Replace(".", "").Replace("+", "-");
            var contextName = ContextNamePrefix + ":" + (candidate == null || string.IsNullOrWhiteSpace(candidate.ModId)
                ? "missing"
                : Normalize(candidate.ModId));
            var pluginId = candidate == null ? "" : candidate.ModId;
            var assemblyName = candidate == null ? "" : candidate.AssemblyName;
            var assemblyPath = candidate == null ? "" : candidate.AssemblyPath;
            var pluginTypeName = candidate == null ? "" : candidate.PluginTypeName;
            var assemblyDirectory = "";
            var assemblyExists = false;
            var activationAllowed = candidate != null
                && candidate.ValidationProbe
                && candidate.CompatibilityReady
                && candidate.AssemblyExists
                && candidate.UnresolvedAssemblyReferenceCount == 0
                && !string.IsNullOrWhiteSpace(candidate.AssemblyPath)
                && !string.IsNullOrWhiteSpace(candidate.PluginTypeName);

            try
            {
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    assemblyPath = Path.GetFullPath(assemblyPath);
                    assemblyDirectory = Path.GetDirectoryName(assemblyPath) ?? "";
                    assemblyExists = File.Exists(assemblyPath);
                }
            }
            catch
            {
                assemblyDirectory = "";
                assemblyExists = false;
            }

            if (candidate == null)
            {
                return OpenACAIBepInExPluginActivationProbeAttemptSnapshot.CreateRejected(
                    attemptId,
                    generatedUtc,
                    activationKind,
                    resolvePluginType,
                    createPluginInstance,
                    invokeLoadMethod,
                    "validation-probe-candidate-missing",
                    "No OpenACAI BepInEx validation probe candidate was available.");
            }

            if (!candidate.ValidationProbe)
            {
                return OpenACAIBepInExPluginActivationProbeAttemptSnapshot.CreateRejected(
                    attemptId,
                    generatedUtc,
                    activationKind,
                    resolvePluginType,
                    createPluginInstance,
                    invokeLoadMethod,
                    pluginId,
                    assemblyName,
                    assemblyPath,
                    assemblyDirectory,
                    pluginTypeName,
                    candidate.CandidateSource,
                    candidate.ValidationProbe,
                    candidate.CompatibilityReady,
                    assemblyExists,
                    candidate.AssemblyReferenceCount,
                    candidate.ResolvedAssemblyReferenceCount,
                    candidate.UnresolvedAssemblyReferenceCount,
                    contextName,
                    "candidate-not-validation-probe",
                    "OpenACAI activation probes only run against generated validation probe candidates.");
            }

            if (!activationAllowed)
            {
                return OpenACAIBepInExPluginActivationProbeAttemptSnapshot.CreateRejected(
                    attemptId,
                    generatedUtc,
                    activationKind,
                    resolvePluginType,
                    createPluginInstance,
                    invokeLoadMethod,
                    pluginId,
                    assemblyName,
                    assemblyPath,
                    assemblyDirectory,
                    pluginTypeName,
                    candidate.CandidateSource,
                    candidate.ValidationProbe,
                    candidate.CompatibilityReady,
                    assemblyExists,
                    candidate.AssemblyReferenceCount,
                    candidate.ResolvedAssemblyReferenceCount,
                    candidate.UnresolvedAssemblyReferenceCount,
                    contextName,
                    "validation-probe-not-ready",
                    "The validation probe candidate was not ready for OpenACAI-owned assembly/type activation.");
            }

            return ExecuteActivation(
                attemptId,
                generatedUtc,
                activationKind,
                resolvePluginType,
                createPluginInstance,
                invokeLoadMethod,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                pluginTypeName,
                candidate.CandidateSource,
                candidate.ValidationProbe,
                candidate.CompatibilityReady,
                assemblyExists,
                candidate.AssemblyReferenceCount,
                candidate.ResolvedAssemblyReferenceCount,
                candidate.UnresolvedAssemblyReferenceCount,
                contextName);
        }

        private static OpenACAIBepInExPluginActivationProbeAttemptSnapshot ExecuteActivation(
            string attemptId,
            string generatedUtc,
            string activationKind,
            bool resolvePluginType,
            bool createPluginInstance,
            bool invokeLoadMethod,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string pluginTypeName,
            string candidateSource,
            bool validationProbe,
            bool compatibilityReady,
            bool assemblyExists,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            string contextName)
        {
            var contextCreated = false;
            var resolverAttached = false;
            var assemblyLoadStarted = false;
            var assemblyLoadCompleted = false;
            var assemblyLoaded = false;
            var loadedAssemblyName = "";
            var loadedAssemblyPath = "";
            var typeResolutionStarted = false;
            var typeResolutionCompleted = false;
            var pluginTypeResolved = false;
            var resolvedTypeName = "";
            var resolvedBaseTypeName = "";
            var assignableToBasePlugin = false;
            var pluginInstanceCreationStarted = false;
            var pluginInstanceCreationCompleted = false;
            var pluginInstanceTypeName = "";
            var pluginInstanceBaseTypeName = "";
            var pluginInstanceAssignableToBasePlugin = false;
            var constructorInvoked = false;
            var pluginInstanceCreated = false;
            var pluginLoadInvocationStarted = false;
            var pluginLoadInvocationCompleted = false;
            var loadMethodInvoked = false;
            var pluginLoadInvoked = false;
            var pluginLoadInvocationFailureIsolated = true;
            var pluginLoadInvocationRollbackRequired = false;
            var pluginLoadInvocationExceptionType = "";
            var pluginLoadInvocationExceptionMessage = "";
            var weakReferenceCreated = false;
            var unloadRequested = false;
            var contextAliveAfterUnload = false;
            var status = "CHECK";
            var reasonCode = "activation-probe-not-run";
            var detail = "OpenACAI validation probe activation was not run.";
            WeakReference contextReference = null;
            ProbeAssemblyLoadContext context = null;
            Assembly loadedAssembly = null;
            Type resolvedType = null;
            object pluginInstance = null;
            BepInEx.Unity.IL2CPP.BasePlugin basePluginInstance = null;

            try
            {
                context = new ProbeAssemblyLoadContext(contextName, assemblyDirectory);
                contextCreated = true;
                resolverAttached = true;
                contextReference = new WeakReference(context);
                weakReferenceCreated = true;

                assemblyLoadStarted = true;
                loadedAssembly = context.LoadFromAssemblyPath(assemblyPath);
                assemblyLoadCompleted = true;
                assemblyLoaded = loadedAssembly != null;
                if (loadedAssembly != null)
                {
                    loadedAssemblyName = loadedAssembly.GetName().Name ?? "";
                    loadedAssemblyPath = SafeAssemblyLocation(loadedAssembly);
                }

                typeResolutionStarted = assemblyLoaded && resolvePluginType;
                if (loadedAssembly != null && resolvePluginType)
                {
                    resolvedType = loadedAssembly.GetType(pluginTypeName, false, false);
                    typeResolutionCompleted = true;
                    pluginTypeResolved = resolvedType != null;
                    if (resolvedType != null)
                    {
                        resolvedTypeName = resolvedType.FullName ?? resolvedType.Name;
                        resolvedBaseTypeName = resolvedType.BaseType == null
                            ? ""
                            : (resolvedType.BaseType.FullName ?? resolvedType.BaseType.Name);
                        assignableToBasePlugin = typeof(BepInEx.Unity.IL2CPP.BasePlugin).IsAssignableFrom(resolvedType);
                    }
                }

                if (createPluginInstance && assemblyLoaded && pluginTypeResolved && assignableToBasePlugin && resolvedType != null)
                {
                    pluginInstanceCreationStarted = true;
                    pluginInstance = Activator.CreateInstance(resolvedType);
                    constructorInvoked = true;
                    pluginInstanceCreationCompleted = true;
                    pluginInstanceCreated = pluginInstance != null;
                    if (pluginInstance != null)
                    {
                        var instanceType = pluginInstance.GetType();
                        pluginInstanceTypeName = instanceType.FullName ?? instanceType.Name;
                        pluginInstanceBaseTypeName = instanceType.BaseType == null
                            ? ""
                            : (instanceType.BaseType.FullName ?? instanceType.BaseType.Name);
                        basePluginInstance = pluginInstance as BepInEx.Unity.IL2CPP.BasePlugin;
                        pluginInstanceAssignableToBasePlugin = basePluginInstance != null;
                    }
                }

                if (invokeLoadMethod && basePluginInstance != null)
                {
                    pluginLoadInvocationStarted = true;
                    try
                    {
                        basePluginInstance.Load();
                        loadMethodInvoked = true;
                        pluginLoadInvoked = true;
                        pluginLoadInvocationCompleted = true;
                    }
                    catch (Exception ex)
                    {
                        pluginLoadInvocationExceptionType = ex.GetType().FullName ?? ex.GetType().Name;
                        pluginLoadInvocationExceptionMessage = ex.Message ?? "";
                        pluginLoadInvocationRollbackRequired = true;
                        throw;
                    }
                }

                if (assemblyLoaded
                    && (!resolvePluginType || pluginTypeResolved && assignableToBasePlugin)
                    && (!createPluginInstance || pluginInstanceCreated && pluginInstanceAssignableToBasePlugin)
                    && (!invokeLoadMethod || pluginLoadInvocationCompleted && loadMethodInvoked && pluginLoadInvoked))
                {
                    status = "READY";
                    reasonCode = "";
                    detail = invokeLoadMethod
                        ? "OpenACAI loaded the generated validation probe assembly, resolved its BasePlugin type, created the generated empty validation plugin instance, invoked its empty Load() method, and isolated the validation-only call."
                        : createPluginInstance
                        ? "OpenACAI loaded the generated validation probe assembly, resolved its BasePlugin type, created the generated empty validation plugin instance, and stopped before Load() invocation."
                        : resolvePluginType
                        ? "OpenACAI loaded the generated validation probe assembly, resolved its BasePlugin type, and stopped before constructor or Load() invocation."
                        : "OpenACAI loaded the generated validation probe assembly in a collectible OpenACAI context and stopped before type resolution, constructor, or Load() invocation.";
                }
                else if (!assemblyLoaded)
                {
                    status = "CHECK";
                    reasonCode = "validation-probe-assembly-not-loaded";
                    detail = "OpenACAI created the validation probe context but did not load the probe assembly.";
                }
                else if (!resolvePluginType)
                {
                    status = "CHECK";
                    reasonCode = "validation-probe-assembly-load-state-mismatch";
                    detail = "OpenACAI loaded the validation probe assembly but the assembly-load-only state did not satisfy the validation gate.";
                }
                else if (!pluginTypeResolved)
                {
                    status = "CHECK";
                    reasonCode = "validation-probe-plugin-type-not-resolved";
                    detail = "OpenACAI loaded the validation probe assembly but did not resolve the plugin type.";
                }
                else if (!assignableToBasePlugin)
                {
                    status = "CHECK";
                    reasonCode = "validation-probe-base-plugin-mismatch";
                    detail = "OpenACAI resolved the validation probe type but it was not assignable to BepInEx.Unity.IL2CPP.BasePlugin.";
                }
                else
                {
                    status = "CHECK";
                    reasonCode = invokeLoadMethod ? "validation-probe-load-not-invoked" : "validation-probe-instance-not-created";
                    detail = invokeLoadMethod
                        ? "OpenACAI created the validation probe instance but did not complete the validation-only Load() invocation."
                        : "OpenACAI resolved the validation probe type but did not create a BasePlugin-compatible validation instance.";
                }
            }
            catch (Exception ex)
            {
                status = "CHECK";
                reasonCode = "activation-probe-exception";
                detail = ex.GetType().FullName + ": " + ex.Message;
            }
            finally
            {
                basePluginInstance = null;
                pluginInstance = null;
                resolvedType = null;
                loadedAssembly = null;
                if (context != null)
                {
                    try
                    {
                        context.Unload();
                        unloadRequested = true;
                    }
                    catch (Exception ex)
                    {
                        detail = detail + " Context unload request failed: " + ex.GetType().Name + ": " + ex.Message;
                    }
                }

                context = null;
                for (var i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                contextAliveAfterUnload = contextReference != null && contextReference.IsAlive;
            }

            return new OpenACAIBepInExPluginActivationProbeAttemptSnapshot(
                attemptId,
                generatedUtc,
                activationKind,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                pluginTypeName,
                candidateSource,
                validationProbe,
                compatibilityReady,
                assemblyExists,
                assemblyReferenceCount,
                resolvedAssemblyReferenceCount,
                unresolvedAssemblyReferenceCount,
                true,
                contextName,
                true,
                contextCreated,
                resolverAttached,
                assemblyLoadStarted,
                assemblyLoadCompleted,
                assemblyLoaded,
                loadedAssemblyName,
                loadedAssemblyPath,
                typeResolutionStarted,
                typeResolutionCompleted,
                pluginTypeResolved,
                resolvedTypeName,
                resolvedBaseTypeName,
                assignableToBasePlugin,
                !createPluginInstance,
                !invokeLoadMethod,
                createPluginInstance,
                pluginInstanceCreationStarted,
                pluginInstanceCreationCompleted,
                constructorInvoked,
                pluginInstanceCreated,
                pluginInstanceTypeName,
                pluginInstanceBaseTypeName,
                pluginInstanceAssignableToBasePlugin,
                invokeLoadMethod,
                pluginLoadInvocationStarted,
                pluginLoadInvocationCompleted,
                pluginLoadInvocationFailureIsolated,
                pluginLoadInvocationRollbackRequired,
                pluginLoadInvocationExceptionType,
                pluginLoadInvocationExceptionMessage,
                loadMethodInvoked,
                pluginLoadInvoked,
                weakReferenceCreated,
                unloadRequested,
                contextAliveAfterUnload,
                "openacai.validation-probe-only",
                status,
                reasonCode,
                detail);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var chars = value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
            return new string(chars).Trim('-');
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

        private sealed class ProbeAssemblyLoadContext : AssemblyLoadContext
        {
            private readonly string _assemblyDirectory;

            internal ProbeAssemblyLoadContext(string name, string assemblyDirectory)
                : base(string.IsNullOrWhiteSpace(name) ? ContextNamePrefix : name, true)
            {
                _assemblyDirectory = assemblyDirectory ?? "";
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                if (assemblyName == null || string.IsNullOrWhiteSpace(assemblyName.Name))
                {
                    return null;
                }

                var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName)
                        || string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
                if (alreadyLoaded != null)
                {
                    return alreadyLoaded;
                }

                var localPath = string.IsNullOrWhiteSpace(_assemblyDirectory)
                    ? ""
                    : Path.Combine(_assemblyDirectory, assemblyName.Name + ".dll");
                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                {
                    return LoadFromAssemblyPath(localPath);
                }

                foreach (var directory in OpenACAIAssemblyResolver.SearchDirectoriesSnapshot())
                {
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    var dependencyPath = Path.Combine(directory, assemblyName.Name + ".dll");
                    if (File.Exists(dependencyPath))
                    {
                        return LoadFromAssemblyPath(dependencyPath);
                    }
                }

                return null;
            }
        }
    }

    public sealed class OpenACAIBepInExPluginActivationProbeStateSnapshot
    {
        internal OpenACAIBepInExPluginActivationProbeStateSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool validationProbeOnly,
            bool runtimePluginLoadingEnabled,
            bool pluginInstanceCreationEnabled,
            bool pluginLoadInvocationEnabled,
            IReadOnlyList<OpenACAIBepInExPluginActivationProbeAttemptSnapshot> attempts)
        {
            ServiceKey = serviceKey ?? OpenACAIBepInExPluginActivationProbe.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            ValidationProbeOnly = validationProbeOnly;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            PluginInstanceCreationEnabled = pluginInstanceCreationEnabled;
            PluginLoadInvocationEnabled = pluginLoadInvocationEnabled;
            Attempts = attempts ?? Array.Empty<OpenACAIBepInExPluginActivationProbeAttemptSnapshot>();
            AttemptCount = Attempts.Count;
            SuccessfulAttemptCount = Attempts.Count(attempt => attempt != null && attempt.Success);
            FailedAttemptCount = Attempts.Count(attempt => attempt != null && attempt.Issue);
            AssemblyLoadAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindAssemblyLoad, StringComparison.OrdinalIgnoreCase));
            SuccessfulAssemblyLoadAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindAssemblyLoad, StringComparison.OrdinalIgnoreCase) && attempt.Success);
            TypeActivationAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindTypeActivation, StringComparison.OrdinalIgnoreCase));
            SuccessfulTypeActivationAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindTypeActivation, StringComparison.OrdinalIgnoreCase) && attempt.Success);
            InstanceCreationAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindInstanceCreation, StringComparison.OrdinalIgnoreCase));
            SuccessfulInstanceCreationAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindInstanceCreation, StringComparison.OrdinalIgnoreCase) && attempt.Success);
            LoadInvocationAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindLoadInvocation, StringComparison.OrdinalIgnoreCase));
            SuccessfulLoadInvocationAttemptCount = Attempts.Count(attempt => attempt != null && string.Equals(attempt.ActivationKind, OpenACAIBepInExPluginActivationProbe.ActivationKindLoadInvocation, StringComparison.OrdinalIgnoreCase) && attempt.Success);
            ContextUnloadAttemptCount = Attempts.Count(attempt => attempt != null && attempt.WeakReferenceCreated && attempt.UnloadRequested);
            SuccessfulContextUnloadAttemptCount = Attempts.Count(attempt => attempt != null && attempt.WeakReferenceCreated && attempt.UnloadRequested && !attempt.ContextAliveAfterUnload);
            LatestAttempt = Attempts.LastOrDefault();
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool ValidationProbeOnly { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool PluginInstanceCreationEnabled { get; }
        public bool ValidationProbeInstanceCreationEnabled => true;
        public bool PluginLoadInvocationEnabled { get; }
        public bool ValidationProbeLoadInvocationEnabled => true;
        public IReadOnlyList<OpenACAIBepInExPluginActivationProbeAttemptSnapshot> Attempts { get; }
        public int AttemptCount { get; }
        public int SuccessfulAttemptCount { get; }
        public int FailedAttemptCount { get; }
        public int AssemblyLoadAttemptCount { get; }
        public int SuccessfulAssemblyLoadAttemptCount { get; }
        public int TypeActivationAttemptCount { get; }
        public int SuccessfulTypeActivationAttemptCount { get; }
        public int InstanceCreationAttemptCount { get; }
        public int SuccessfulInstanceCreationAttemptCount { get; }
        public int LoadInvocationAttemptCount { get; }
        public int SuccessfulLoadInvocationAttemptCount { get; }
        public int ContextUnloadAttemptCount { get; }
        public int SuccessfulContextUnloadAttemptCount { get; }
        public OpenACAIBepInExPluginActivationProbeAttemptSnapshot LatestAttempt { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginActivationProbeStateSnapshot CreateInitial()
        {
            return new OpenACAIBepInExPluginActivationProbeStateSnapshot(
                OpenACAIBepInExPluginActivationProbe.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                true,
                "INFO",
                "No OpenACAI BepInEx validation-probe activation attempts have been recorded.",
                true,
                false,
                false,
                false,
                Array.Empty<OpenACAIBepInExPluginActivationProbeAttemptSnapshot>());
        }

        internal static OpenACAIBepInExPluginActivationProbeStateSnapshot Create(
            IReadOnlyList<OpenACAIBepInExPluginActivationProbeAttemptSnapshot> attempts)
        {
            var latest = attempts == null ? null : attempts.LastOrDefault();
            return new OpenACAIBepInExPluginActivationProbeStateSnapshot(
                OpenACAIBepInExPluginActivationProbe.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                latest == null || latest.Success,
                latest == null ? "INFO" : latest.Status,
                latest == null
                    ? "No OpenACAI BepInEx validation-probe activation attempts have been recorded."
                    : latest.Detail,
                true,
                false,
                false,
                false,
                attempts == null ? Array.Empty<OpenACAIBepInExPluginActivationProbeAttemptSnapshot>() : attempts.ToArray());
        }
    }

    public sealed class OpenACAIBepInExPluginActivationProbeAttemptSnapshot
    {
        internal OpenACAIBepInExPluginActivationProbeAttemptSnapshot(
            string attemptId,
            string generatedUtc,
            string activationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string pluginTypeName,
            string candidateSource,
            bool validationProbe,
            bool compatibilityReady,
            bool assemblyExists,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            bool activationAllowed,
            string contextName,
            bool collectibleContext,
            bool contextCreated,
            bool resolverAttached,
            bool assemblyLoadStarted,
            bool assemblyLoadCompleted,
            bool assemblyLoaded,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            bool typeResolutionStarted,
            bool typeResolutionCompleted,
            bool pluginTypeResolved,
            string resolvedTypeName,
            string resolvedBaseTypeName,
            bool assignableToBasePlugin,
            bool pluginInstanceCreationBlocked,
            bool pluginLoadInvocationBlocked,
            bool pluginInstanceCreationRequested,
            bool pluginInstanceCreationStarted,
            bool pluginInstanceCreationCompleted,
            bool constructorInvoked,
            bool pluginInstanceCreated,
            string pluginInstanceTypeName,
            string pluginInstanceBaseTypeName,
            bool pluginInstanceAssignableToBasePlugin,
            bool pluginLoadInvocationRequested,
            bool pluginLoadInvocationStarted,
            bool pluginLoadInvocationCompleted,
            bool pluginLoadInvocationFailureIsolated,
            bool pluginLoadInvocationRollbackRequired,
            string pluginLoadInvocationExceptionType,
            string pluginLoadInvocationExceptionMessage,
            bool loadMethodInvoked,
            bool pluginLoadInvoked,
            bool weakReferenceCreated,
            bool unloadRequested,
            bool contextAliveAfterUnload,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            AttemptId = attemptId ?? "";
            GeneratedUtc = generatedUtc ?? "";
            ActivationKind = activationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            PluginTypeName = pluginTypeName ?? "";
            CandidateSource = candidateSource ?? "";
            ValidationProbe = validationProbe;
            CompatibilityReady = compatibilityReady;
            AssemblyExists = assemblyExists;
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            ActivationAllowed = activationAllowed;
            ContextName = contextName ?? "";
            CollectibleContext = collectibleContext;
            ContextCreated = contextCreated;
            ResolverAttached = resolverAttached;
            AssemblyLoadStarted = assemblyLoadStarted;
            AssemblyLoadCompleted = assemblyLoadCompleted;
            AssemblyLoaded = assemblyLoaded;
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            TypeResolutionStarted = typeResolutionStarted;
            TypeResolutionCompleted = typeResolutionCompleted;
            PluginTypeResolved = pluginTypeResolved;
            ResolvedTypeName = resolvedTypeName ?? "";
            ResolvedBaseTypeName = resolvedBaseTypeName ?? "";
            AssignableToBasePlugin = assignableToBasePlugin;
            PluginInstanceCreationBlocked = pluginInstanceCreationBlocked;
            PluginLoadInvocationBlocked = pluginLoadInvocationBlocked;
            PluginInstanceCreationRequested = pluginInstanceCreationRequested;
            PluginInstanceCreationStarted = pluginInstanceCreationStarted;
            PluginInstanceCreationCompleted = pluginInstanceCreationCompleted;
            ConstructorInvoked = constructorInvoked;
            PluginInstanceCreated = pluginInstanceCreated;
            PluginInstanceTypeName = pluginInstanceTypeName ?? "";
            PluginInstanceBaseTypeName = pluginInstanceBaseTypeName ?? "";
            PluginInstanceAssignableToBasePlugin = pluginInstanceAssignableToBasePlugin;
            PluginLoadInvocationRequested = pluginLoadInvocationRequested;
            PluginLoadInvocationStarted = pluginLoadInvocationStarted;
            PluginLoadInvocationCompleted = pluginLoadInvocationCompleted;
            PluginLoadInvocationFailureIsolated = pluginLoadInvocationFailureIsolated;
            PluginLoadInvocationRollbackRequired = pluginLoadInvocationRollbackRequired;
            PluginLoadInvocationExceptionType = pluginLoadInvocationExceptionType ?? "";
            PluginLoadInvocationExceptionMessage = pluginLoadInvocationExceptionMessage ?? "";
            LoadMethodInvoked = loadMethodInvoked;
            PluginLoadInvoked = pluginLoadInvoked;
            WeakReferenceCreated = weakReferenceCreated;
            UnloadRequested = unloadRequested;
            ContextAliveAfterUnload = contextAliveAfterUnload;
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string AttemptId { get; }
        public string GeneratedUtc { get; }
        public string ActivationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public string PluginTypeName { get; }
        public string CandidateSource { get; }
        public bool ValidationProbe { get; }
        public bool CompatibilityReady { get; }
        public bool AssemblyExists { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public bool ActivationAllowed { get; }
        public string ContextName { get; }
        public bool CollectibleContext { get; }
        public bool ContextCreated { get; }
        public bool ResolverAttached { get; }
        public bool AssemblyLoadStarted { get; }
        public bool AssemblyLoadCompleted { get; }
        public bool AssemblyLoaded { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public bool TypeResolutionStarted { get; }
        public bool TypeResolutionCompleted { get; }
        public bool PluginTypeResolved { get; }
        public string ResolvedTypeName { get; }
        public string ResolvedBaseTypeName { get; }
        public bool AssignableToBasePlugin { get; }
        public bool PluginInstanceCreationBlocked { get; }
        public bool PluginLoadInvocationBlocked { get; }
        public bool PluginInstanceCreationRequested { get; }
        public bool PluginInstanceCreationStarted { get; }
        public bool PluginInstanceCreationCompleted { get; }
        public bool ConstructorInvoked { get; }
        public bool PluginInstanceCreated { get; }
        public string PluginInstanceTypeName { get; }
        public string PluginInstanceBaseTypeName { get; }
        public bool PluginInstanceAssignableToBasePlugin { get; }
        public bool PluginLoadInvocationRequested { get; }
        public bool PluginLoadInvocationStarted { get; }
        public bool PluginLoadInvocationCompleted { get; }
        public bool PluginLoadInvocationFailureIsolated { get; }
        public bool PluginLoadInvocationRollbackRequired { get; }
        public string PluginLoadInvocationExceptionType { get; }
        public string PluginLoadInvocationExceptionMessage { get; }
        public bool LoadMethodInvoked { get; }
        public bool PluginLoadInvoked { get; }
        public bool WeakReferenceCreated { get; }
        public bool UnloadRequested { get; }
        public bool ContextAliveAfterUnload { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Success => string.Equals(Status, "READY", StringComparison.OrdinalIgnoreCase);
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static OpenACAIBepInExPluginActivationProbeAttemptSnapshot CreateRejected(
            string attemptId,
            string generatedUtc,
            string activationKind,
            bool resolvePluginType,
            bool pluginInstanceCreationRequested,
            bool pluginLoadInvocationRequested,
            string reasonCode,
            string detail)
        {
            return CreateRejected(
                attemptId,
                generatedUtc,
                activationKind,
                resolvePluginType,
                pluginInstanceCreationRequested,
                pluginLoadInvocationRequested,
                "",
                "",
                "",
                "",
                "",
                "",
                false,
                false,
                false,
                0,
                0,
                0,
                "",
                reasonCode,
                detail);
        }

        internal static OpenACAIBepInExPluginActivationProbeAttemptSnapshot CreateRejected(
            string attemptId,
            string generatedUtc,
            string activationKind,
            bool resolvePluginType,
            bool pluginInstanceCreationRequested,
            bool pluginLoadInvocationRequested,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string pluginTypeName,
            string candidateSource,
            bool validationProbe,
            bool compatibilityReady,
            bool assemblyExists,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            string contextName,
            string reasonCode,
            string detail)
        {
            return new OpenACAIBepInExPluginActivationProbeAttemptSnapshot(
                attemptId,
                generatedUtc,
                activationKind,
                pluginId,
                assemblyName,
                assemblyPath,
                assemblyDirectory,
                pluginTypeName,
                candidateSource,
                validationProbe,
                compatibilityReady,
                assemblyExists,
                assemblyReferenceCount,
                resolvedAssemblyReferenceCount,
                unresolvedAssemblyReferenceCount,
                false,
                contextName,
                true,
                false,
                false,
                false,
                false,
                false,
                "",
                "",
                false,
                false,
                false,
                "",
                "",
                false,
                true,
                true,
                pluginInstanceCreationRequested,
                false,
                false,
                false,
                false,
                "",
                "",
                false,
                pluginLoadInvocationRequested,
                false,
                false,
                true,
                false,
                "",
                "",
                false,
                false,
                false,
                false,
                false,
                "openacai.validation-probe-only",
                "CHECK",
                reasonCode,
                detail);
        }
    }
}
