// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    /// <summary>Runs managed mod callbacks through Core startup and its Unity driver.</summary>
    public static class ModRuntime
    {
        public const string ServiceKey = "openacai.loader.nativeModRuntime";
        internal const string RetiredLifecycleMessage = "Use ModRuntime.State. Lifecycle planning is retired; this view is derived from the native mod runtime.";

        private static readonly List<LoadedMod> Mods = new List<LoadedMod>();
        private static readonly Hook[] StartupHooks = { Hook.Initialize, Hook.Awake, Hook.Load, Hook.Enable };
        private static readonly Hook[] CleanupHooks = { Hook.Disable, Hook.Shutdown, Hook.Dispose };
        private static ModRuntimeSnapshot _state = new ModRuntimeSnapshot(
            false, false, false, false, Array.Empty<ModRuntimeEntry>());
        private static volatile bool _executionEnabled;
        private static bool _loadAttempted;
        private static bool _loadCompleted;
        private static bool _startRequested;
        private static bool _started;
        private static bool _stopped;

        public static ModRuntimeSnapshot State => Volatile.Read(ref _state);
        public static bool ExecutionEnabled => _executionEnabled;

        internal enum Hook { Initialize, Awake, Load, Enable, Start, FixedUpdate, Update, LateUpdate, Gui, Disable, Shutdown, Dispose }

        internal static void Load(ModInventoryState inventory, bool ownedDriverActive)
        {
            if (_loadAttempted)
                return;

            // Claim startup before loading any user assembly. A partial activation is never retried.
            _loadAttempted = true;
            _executionEnabled = ownedDriverActive;
            var candidates = ModHost.GetStartupCandidates(inventory);
            var plan = ModDependencies.Plan(candidates);
            var planned = plan.Ordered.ToDictionary(candidate => candidate, candidate => new LoadedMod(candidate));
            foreach (var candidate in plan.Ordered)
            {
                var mod = planned[candidate];
                mod.Required = plan.Required[candidate].Select(required => planned[required]).ToArray();
                Mods.Add(mod);
            }

            foreach (var mod in Mods)
            {
                var candidate = mod.Candidate;
                if (!candidate.Enabled || candidate.MetadataOnly)
                {
                    mod.Status = "Skipped";
                    mod.Detail = candidate.Detail;
                    continue;
                }

                if (!ownedDriverActive)
                {
                    mod.Status = "Deferred";
                    mod.Detail = "Native mods require the Modstoke Unity driver; legacy native Unity support is active.";
                    continue;
                }

                var phase = "startup validation";
                try
                {
                    if (plan.Issues.TryGetValue(candidate, out var issue))
                        throw new InvalidOperationException(issue);
                    if (!candidate.Hostable)
                        throw new InvalidOperationException(candidate.Detail);

                    RequireDependencies(mod);
                    ModHost.RequireUnchangedManifest(candidate);
                    phase = "startup binding";
                    Bind(mod);
                    ModHost.RequireUnchangedManifest(candidate);
                    phase = mod.Constructor != null ? "startup constructor" : "startup entrypoint";
                    mod.EntrypointInvoked = true;
                    if (mod.Constructor != null)
                        mod.Instance = mod.Constructor.Invoke(null);
                    else
                        mod.Entrypoint();
                    mod.EntrypointCompleted = true;

                    foreach (var hook in StartupHooks)
                    {
                        phase = "startup " + hook;
                        RequireDependencies(mod);
                        // An entry method also mapped to initialize is the same startup operation.
                        if (hook == Hook.Initialize && mod.Callbacks.TryGetValue(hook, out var initialize)
                            && initialize == mod.Entrypoint)
                            continue;
                        Invoke(mod, hook);
                    }

                    mod.Loaded = true;
                    mod.Status = "Loaded";
                    mod.Detail = "Entrypoint and startup callbacks completed.";
                }
                catch (Exception error)
                {
                    Fail(mod, phase, error);
                    Cleanup(mod);
                }
            }

            _loadCompleted = true;
            Publish();
            if (_startRequested)
                Start();
        }

        internal static void Start()
        {
            _startRequested = true;
            if (!_loadCompleted || _started || _stopped)
                return;
            _started = true;
            Dispatch(Hook.Start);
            Publish();
        }

        internal static void Dispatch(Hook hook)
        {
            if (!_loadCompleted || !_started || _stopped)
                return;

            foreach (var mod in Mods)
            {
                if (!mod.Loaded || mod.Failed)
                    continue;
                try
                {
                    RequireDependencies(mod);
                    Invoke(mod, hook);
                }
                catch (Exception error)
                {
                    Fail(mod, hook.ToString(), error);
                    Publish();
                }
            }
        }

        internal static void Shutdown()
        {
            if (_stopped)
                return;
            _stopped = true;

            for (var i = Mods.Count - 1; i >= 0; i--)
                Cleanup(Mods[i]);
            Publish();
        }

        private static void Cleanup(LoadedMod mod)
        {
            if (!mod.EntrypointInvoked || mod.CleanupAttempted)
                return;

            // A throwing entrypoint can leave resources behind. Only declared hooks own their cleanup.
            mod.CleanupAttempted = true;
            foreach (var hook in CleanupHooks)
            {
                try { Invoke(mod, hook); }
                catch (Exception error) { Fail(mod, hook.ToString(), error); }
            }
            if (!mod.Failed)
                mod.Status = "Stopped";
        }

        internal static bool IsLoaded(string manifestPath, string assemblyPath) => State.Mods.Any(mod => mod.Loaded
            && string.Equals(mod.ManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(mod.AssemblyPath, assemblyPath, StringComparison.OrdinalIgnoreCase));

        private static void Bind(LoadedMod mod)
        {
            var candidate = mod.Candidate;
            foreach (var plan in candidate.ExecutionPlan.LifecycleHookPlans)
            {
                if (!TryParseHook(plan.HookName, out _))
                    throw new InvalidOperationException("Unsupported native lifecycle hook: " + plan.HookName);
            }
            var path = Path.GetFullPath(candidate.AssemblyPath);
            Guid expectedModule;
            using (var stream = File.OpenRead(path))
            using (var pe = new PEReader(stream))
            {
                var metadata = pe.GetMetadataReader();
                expectedModule = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            }

            var assembly = Assembly.LoadFrom(path);
            if (!string.Equals(Path.GetFullPath(assembly.Location), path, StringComparison.OrdinalIgnoreCase)
                || assembly.ManifestModule.ModuleVersionId != expectedModule)
                throw new InvalidOperationException("The runtime resolved a different native assembly than the selected package.");

            var type = assembly.GetType(candidate.MatchedEntrypointTypeName, throwOnError: true, ignoreCase: false);
            if (!type.IsVisible || type.ContainsGenericParameters)
                throw new InvalidOperationException("The native entrypoint type must be public and closed (non-generic).");

            if (candidate.ExecutionPlan.RequiresInstance)
            {
                if (!type.IsClass || type.IsAbstract)
                    throw new InvalidOperationException("A constructor entrypoint must be a concrete class.");
                mod.Constructor = type.GetConstructor(Type.EmptyTypes)
                    ?? throw new MissingMethodException(type.FullName, ".ctor()");
            }
            else
            {
                mod.Entrypoint = BindCallback(type, candidate.EntrypointMethodName);
            }

            foreach (var plan in candidate.ExecutionPlan.LifecycleHookPlans)
            {
                if (!TryParseHook(plan.HookName, out var hook))
                    throw new InvalidOperationException("Unsupported native lifecycle hook: " + plan.HookName);
                var callback = BindCallback(type, plan.MethodName);
                if (mod.Callbacks.TryGetValue(hook, out var previous) && previous != callback)
                    throw new InvalidOperationException("Conflicting native lifecycle aliases for " + hook + ".");
                mod.Callbacks[hook] = callback;
            }
        }

        private static Action BindCallback(Type type, string name)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name && !method.IsGenericMethod && method.GetParameters().Length == 0)
                .ToArray();
            if (methods.Length != 1 || methods[0].ReturnType != typeof(void)
                || methods[0].IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
                throw new InvalidOperationException(type.FullName + "." + name
                    + " must be an unambiguous public static, non-generic, synchronous void method with no parameters.");
            return (Action)methods[0].CreateDelegate(typeof(Action));
        }

        internal static bool TryParseHook(string name, out Hook hook)
        {
            switch (name.ToLowerInvariant())
            {
                case "initialize": case "init": hook = Hook.Initialize; return true;
                case "awake": hook = Hook.Awake; return true;
                case "load": hook = Hook.Load; return true;
                case "enable": case "onenable": hook = Hook.Enable; return true;
                case "start": hook = Hook.Start; return true;
                case "fixedupdate": hook = Hook.FixedUpdate; return true;
                case "update": hook = Hook.Update; return true;
                case "lateupdate": hook = Hook.LateUpdate; return true;
                case "gui": case "ongui": hook = Hook.Gui; return true;
                case "disable": case "ondisable": hook = Hook.Disable; return true;
                case "shutdown": hook = Hook.Shutdown; return true;
                case "dispose": hook = Hook.Dispose; return true;
                default: hook = default; return false;
            }
        }

        private static void Invoke(LoadedMod mod, Hook hook)
        {
            if (mod.Callbacks.TryGetValue(hook, out var callback))
            {
                using var capture = PerformanceCapture.MeasureCallback(callback, "native");
                callback();
            }
        }

        private static void RequireDependencies(LoadedMod mod)
        {
            foreach (var required in mod.Required)
            {
                if (!required.Loaded || required.Failed)
                    throw new InvalidOperationException("Required native mod '" + required.Candidate.ModId
                        + "' did not start or has failed. This mod's callbacks will not continue.");
            }
        }

        private static void Fail(LoadedMod mod, string phase, Exception error)
        {
            if (error is TargetInvocationException invocation && invocation.InnerException != null)
                error = invocation.InnerException;
            var firstFailure = !mod.Failed;
            mod.Failed = true;
            mod.Status = "Failed";
            var detail = phase + ": " + DescribeFailure(error);
            if (firstFailure)
                mod.Detail = detail;
            try { RuntimeEntryPoint.LogSource?.LogError("Native mod '" + mod.Candidate.ModId + "' failed during " + detail); }
            catch { /* A logging listener must not break failure isolation. */ }
        }

        private static string DescribeFailure(Exception error)
        {
            // Plugin exceptions can override ToString or Message; reporting must not stop the next callback.
            try { return error.ToString() ?? error.GetType().FullName; }
            catch { return error.GetType().FullName; }
        }

        private static void Publish()
        {
            var snapshot = new ModRuntimeSnapshot(_loadAttempted, _loadCompleted, _executionEnabled, _stopped,
                Array.AsReadOnly(Mods.Select(mod => new ModRuntimeEntry(mod.Candidate.ModId,
                    mod.Candidate.DisplayName, mod.Candidate.Version,
                    mod.Candidate.ManifestPath, mod.Candidate.AssemblyPath, mod.Status, mod.Detail,
                    mod.EntrypointInvoked, mod.EntrypointCompleted, mod.Loaded, mod.Failed,
                    mod.Loaded && !mod.Failed && _executionEnabled && !_stopped,
                    Array.AsReadOnly(mod.Callbacks.OrderBy(pair => pair.Key)
                        .Select(pair => new ModRuntimeHook(pair.Key.ToString().ToLowerInvariant(), pair.Value.Method.Name))
                        .ToArray()))).ToArray()));
            Volatile.Write(ref _state, snapshot);
            try
            {
                Modding.SetService(ServiceKey, snapshot);
                RuntimeEntryPoint.RequestLoaderStateRefresh("native-mod-runtime");
            }
            catch { /* Execution state remains available even if diagnostics cannot publish it. */ }
        }

        private sealed class LoadedMod
        {
            internal LoadedMod(ModCandidateSnapshot candidate) { Candidate = candidate; }
            internal readonly ModCandidateSnapshot Candidate;
            internal readonly Dictionary<Hook, Action> Callbacks = new Dictionary<Hook, Action>();
            internal IReadOnlyList<LoadedMod> Required = Array.Empty<LoadedMod>();
            internal ConstructorInfo Constructor;
            internal object Instance;
            internal Action Entrypoint;
            internal bool EntrypointInvoked;
            internal bool EntrypointCompleted;
            internal bool CleanupAttempted;
            internal bool Loaded;
            internal bool Failed;
            internal string Status = "Pending";
            internal string Detail = "";
        }
    }

    public sealed class ModRuntimeSnapshot
    {
        internal ModRuntimeSnapshot(bool loadAttempted, bool loadCompleted, bool executionEnabled, bool stopped,
            IReadOnlyList<ModRuntimeEntry> mods)
        {
            LoadAttempted = loadAttempted;
            LoadCompleted = loadCompleted;
            ExecutionEnabled = executionEnabled;
            Stopped = stopped;
            Mods = mods;
            LoadedCount = mods.Count(mod => mod.Loaded);
            ActiveCount = mods.Count(mod => mod.Active);
            FailedCount = mods.Count(mod => mod.Failed);
            DeferredCount = mods.Count(mod => mod.Status == "Deferred");
            HookCount = mods.Sum(mod => mod.Hooks.Count);
        }

        public int Schema => 1;
        public bool LoadAttempted { get; }
        public bool LoadCompleted { get; }
        public bool ExecutionEnabled { get; }
        public bool Stopped { get; }
        public int LoadedCount { get; }
        public int ActiveCount { get; }
        public int FailedCount { get; }
        public int DeferredCount { get; }
        public int HookCount { get; }
        public IReadOnlyList<ModRuntimeEntry> Mods { get; }
    }

    public sealed class ModRuntimeEntry
    {
        internal ModRuntimeEntry(string modId, string name, string version, string manifestPath, string assemblyPath,
            string status, string detail, bool entrypointInvoked, bool entrypointCompleted, bool loaded, bool failed, bool active,
            IReadOnlyList<ModRuntimeHook> hooks)
        {
            ModId = modId;
            Name = name;
            Version = version;
            ManifestPath = manifestPath;
            AssemblyPath = assemblyPath;
            Status = status;
            Detail = detail;
            EntrypointInvoked = entrypointInvoked;
            EntrypointCompleted = entrypointCompleted;
            Loaded = loaded;
            Failed = failed;
            Active = active;
            Hooks = hooks;
        }

        public string ModId { get; }
        public string Name { get; }
        public string Version { get; }
        public string ManifestPath { get; }
        public string AssemblyPath { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool EntrypointInvoked { get; }
        public bool EntrypointCompleted { get; }
        public bool Loaded { get; }
        public bool Failed { get; }
        public bool Active { get; }
        /// <summary>Lifecycle hooks bound at startup, in dispatch order; empty for skipped or deferred mods.</summary>
        public IReadOnlyList<ModRuntimeHook> Hooks { get; }
    }

    public sealed class ModRuntimeHook
    {
        internal ModRuntimeHook(string name, string methodName)
        {
            Name = name;
            MethodName = methodName;
        }

        public string Name { get; }
        public string MethodName { get; }
    }
}
