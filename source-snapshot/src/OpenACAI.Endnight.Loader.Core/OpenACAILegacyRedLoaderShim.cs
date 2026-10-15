// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned compatibility host for legacy RedLoader/SonsSdk mod packages.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using OpenACAI.Loader;
using RedLoader;
using RedLoader.Utils;
using RedLoader.Unity.IL2CPP.UnityEngine;
using SonsSdk;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAILegacyRedLoaderCompatibility
    {
        internal const string EnableRedLoaderModsEnvironmentVariableName = "OPENACAI_ENABLE_REDLOADER_MODS";
        internal const string RedLoaderModAllowlistEnvironmentVariableName = "OPENACAI_REDLOADER_MOD_ALLOWLIST";
        internal const string UseUpstreamRedLoaderBootstrapEnvironmentVariableName = "OPENACAI_USE_UPSTREAM_REDLOADER_BOOTSTRAP";
        internal const string EnableRedLoaderModsButtonEnvironmentVariableName = "OPENACAI_ENABLE_REDLOADER_MOD_BUTTON";

        internal static void SetExecutablePath(string executablePath)
        {
            InitializeLoaderEnvironment(executablePath, Array.Empty<string>());
        }

        internal static void InitializeCompatibilityDrivers()
        {
            if (!IsTruthy(Environment.GetEnvironmentVariable(UseUpstreamRedLoaderBootstrapEnvironmentVariableName)))
            {
                OpenACAIManagedEnumerator.InstallRedLoader();
                OpenACAICoroutineHost.Install();
                OpenACAICoroutines.Install();
                OpenACAIMenuRegistrationPolicy.Install();
                OpenACAISonsSdkLifecycle.Install();
            }
        }

        internal static void TryInitializeCorePreferences(Action<string> addStartupWarning)
        {
            try
            {
                CorePreferences.Init();
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke("RedLoader legacy core preferences could not initialize; continuing with OpenACAI config services: " + ex.Message);
                OpenACAIEndnightRuntime.TraceCompat("CorePreferences.Init failed: " + ex);
            }
        }

        internal static void InstallLogBridge(ref bool installed, ManualLogSource log, Action<string> addStartupWarning)
        {
            if (installed)
            {
                return;
            }

            try
            {
                RLog.MsgDrawingCallbackHandler += (nameColor, textColor, name, text) => log?.LogInfo(FormatLog(name, text));
                RLog.WarningCallbackHandler += (name, text) => log?.LogWarning(FormatLog(name, text));
                RLog.ErrorCallbackHandler += (name, text) =>
                {
                    log?.LogError(FormatLog(name, text));
                    RecordRuntimeIssue(name, text);
                };
                installed = true;
            }
            catch (Exception ex)
            {
                installed = false;
                addStartupWarning?.Invoke("RedLoader legacy log bridge could not attach; continuing with OpenACAI logging: " + ex.Message);
                OpenACAIEndnightRuntime.TraceCompat("InstallRedLoaderLogBridge failed: " + ex);
            }
        }

        internal static void TryInitializeConfigSystem(Action<string> addStartupWarning)
        {
            try
            {
                ConfigSystem.Load();
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke("RedLoader legacy config system could not initialize; continuing with OpenACAI config services: " + ex.Message);
                OpenACAIEndnightRuntime.TraceCompat("ConfigSystem.Load failed: " + ex);
            }
        }

        internal static RedLoaderHostResult LoadMods(SdkEntryPoint processor, Action<string> addStartupWarning)
        {
            var candidates = OpenAcaiRedLoaderHost.Discover(LoaderEnvironment.ModsDirectory, LoaderEnvironment.LibsDirectory, out var discoveryErrors);
            foreach (var error in discoveryErrors)
            {
                try { addStartupWarning?.Invoke("RedLoader discovery incomplete: " + error); }
                catch { /* Keep a failing warning listener outside discovery control flow. */ }
            }
            var allowlist = OpenAcaiRedLoaderHost.ParseAllowlist(Environment.GetEnvironmentVariable(RedLoaderModAllowlistEnvironmentVariableName));

            if (IsTruthy(Environment.GetEnvironmentVariable(UseUpstreamRedLoaderBootstrapEnvironmentVariableName)))
            {
                addStartupWarning?.Invoke("Using upstream RedLoader bootstrap because " + UseUpstreamRedLoaderBootstrapEnvironmentVariableName + " is enabled. This path is diagnostic-only on the OpenACAI " + CurrentDotnetLabel() + " branch.");
                OpenACAIEndnightRuntime.TraceCompat("Upstream RedLoader bootstrap explicitly enabled");
                var mods = processor.LoadPlugins();
                return RedLoaderHostResult.Upstream(mods, candidates).WithDiscoveryErrors(discoveryErrors);
            }

            if (!ShouldEnableModHosting())
            {
                var reason = "RedLoader mod hosting is disabled by " + EnableRedLoaderModsEnvironmentVariableName + ".";
                addStartupWarning?.Invoke(reason);
                return RedLoaderHostResult.InventoryOnly(candidates, reason).WithDiscoveryErrors(discoveryErrors);
            }

            return OpenAcaiRedLoaderHost.Load(candidates, allowlist).WithDiscoveryErrors(discoveryErrors);
        }

        internal static bool TryInitializeUnitySupport(Action<string> addStartupWarning)
        {
            if (Environment.Version.Major >= 10)
            {
                GlobalBehaviour.UseExternalLifecycle();
                addStartupWarning?.Invoke("RedLoader native Unity support is disabled on " + CurrentDotnetLabel() + "; using the OpenACAI compatibility driver.");
                return false;
            }

            try
            {
                InvokeNonPublicStatic("RedLoader.Support.SceneHandler, RedLoader", "Init");
                RedLoader.Unity.IL2CPP.UnityEngine.GlobalBehaviour.Init();
                OpenACAIEndnightRuntime.LogSource.LogInfo("RedLoader Unity support initialized through native SceneHandler/GlobalBehaviour.");
                return true;
            }
            catch (Exception ex)
            {
                GlobalBehaviour.UseExternalLifecycle();
                addStartupWarning?.Invoke("RedLoader native Unity support failed; falling back to compatibility driver: " + ex.Message);
                return false;
            }
        }

        internal static void PublishRedLoaderShimState(RedLoaderHostResult hostResult)
        {
            if (hostResult == null)
            {
                return;
            }

            var detail = CreateRedLoaderShimDetail(hostResult);
            var status = OpenACAICompatibilityShimStatus.Ready;
            var ready = true;
            if (string.Equals(hostResult.Mode, "inventory-only", StringComparison.OrdinalIgnoreCase))
            {
                status = OpenACAICompatibilityShimStatus.InventoryOnly;
                ready = false;
            }
            else if (string.Equals(hostResult.Mode, "upstream-redloader", StringComparison.OrdinalIgnoreCase))
            {
                status = OpenACAICompatibilityShimStatus.Warning;
                detail += " Upstream RedLoader bootstrap is diagnostic-only on the OpenACAI " + CurrentDotnetLabel() + " branch.";
            }

            if (hostResult.DiscoveryErrors.Count != 0)
            {
                if (ready)
                    status = OpenACAICompatibilityShimStatus.Warning;
                detail += " Discovery was incomplete; " + hostResult.DiscoveryErrors.Count + " failure(s) are recorded in host state.";
            }

            OpenACAIRuntime.MarkShim(
                OpenACAICompatibilityShimIds.RedLoaderMods,
                "RedLoader mods",
                "RedLoader",
                status,
                hostResult.Mode,
                true,
                ready,
                false,
                detail);
        }

        internal static void PublishSonsSdkShimState(RedLoaderHostResult hostResult, string initializationError, bool initializationCompleted = false)
        {
            if (hostResult == null)
            {
                return;
            }

            if (hostResult.InitializeSonsSdk)
            {
                if (initializationError != null)
                {
                    OpenACAIRuntime.MarkShimFailed(
                        OpenACAICompatibilityShimIds.SonsSdk,
                        "SonsSdk APIs",
                        "SonsSdk",
                        hostResult.Mode,
                        initializationError);
                    return;
                }

                OpenACAIRuntime.MarkShim(
                    OpenACAICompatibilityShimIds.SonsSdk,
                    "SonsSdk APIs",
                    "SonsSdk",
                    initializationCompleted ? OpenACAICompatibilityShimStatus.Ready : OpenACAICompatibilityShimStatus.Pending,
                    hostResult.Mode,
                    true,
                    initializationCompleted,
                    false,
                    initializationCompleted
                        ? "Shared SonsSdk initialization completed; individual mod callback failures are reported separately."
                        : "SonsSdk startup is pending; shared initialization has not completed.");
                return;
            }

            OpenACAIRuntime.MarkShim(
                OpenACAICompatibilityShimIds.SonsSdk,
                "SonsSdk APIs",
                "SonsSdk",
                OpenACAICompatibilityShimStatus.InventoryOnly,
                hostResult.Mode,
                true,
                false,
                false,
                "SonsSdk compatibility is available for metadata and UI mapping, but no hosted RedLoader mods required post-Unity initialization.");
        }

        internal static object CreateRuntimeIssueState()
        {
            lock (RuntimeIssueSync)
            {
                return new
                {
                    errorCount = _redLoaderRuntimeErrorCount,
                    lastError = _lastRedLoaderRuntimeError,
                    lastErrorUtc = _lastRedLoaderRuntimeErrorUtc
                };
            }
        }

        internal static void RegisterHostedModTypes(RedLoaderHostResult hostResult, Action<string> addStartupWarning)
        {
            if (hostResult == null)
            {
                return;
            }

            foreach (var mod in hostResult.HostedMods)
            {
                if (mod?.ModAssembly == null)
                {
                    addStartupWarning?.Invoke("Skipped IL2CPP type registration for a mod with no loaded assembly.");
                    continue;
                }

                try
                {
                    RegisterTypeInIl2Cpp.RegisterAssembly(mod.ModAssembly);
                }
                catch (Exception ex)
                {
                    addStartupWarning?.Invoke("IL2CPP type registration failed for " + mod.ModAssembly.GetName().Name + ": " + ex.Message);
                }
            }
        }

        internal static string CreateCompatibilityStartedMessage(RedLoaderHostResult hostResult)
        {
            var hostedCount = hostResult != null ? hostResult.HostedCount : 0;
            var discoveredCount = hostResult != null ? hostResult.DiscoveredCount : 0;
            return "RedLoader compatibility active. Hosted " + hostedCount + " RedLoader mod" + (hostedCount == 1 ? "" : "s")
                + " from " + LoaderEnvironment.ModsDirectory + "; discovered " + discoveredCount
                + " RedLoader/SonsSdk candidate" + (discoveredCount == 1 ? "" : "s") + " across Mods and Libs.";
        }

        private static string FormatLog(string name, string text)
        {
            return string.IsNullOrWhiteSpace(name)
                ? "[RedLoader API] " + text
                : "[RedLoader API:" + name + "] " + text;
        }

        private static void RecordRuntimeIssue(string name, string text)
        {
            string lastError;
            int errorCount;

            lock (RuntimeIssueSync)
            {
                _redLoaderRuntimeErrorCount++;
                _lastRedLoaderRuntimeError = TrimForState(FormatLog(name, text), 2048);
                _lastRedLoaderRuntimeErrorUtc = DateTimeOffset.UtcNow.ToString("O");
                lastError = _lastRedLoaderRuntimeError;
                errorCount = _redLoaderRuntimeErrorCount;
            }

            // RLog includes arbitrary mod errors; it cannot establish SDK or host failure.
            OpenACAIEndnightRuntime.TraceCompat("RedLoader runtime issue #" + errorCount + ": " + lastError);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("redloader-runtime-error");
        }

        private static string CreateRedLoaderShimDetail(RedLoaderHostResult hostResult)
        {
            var detail = "Discovered " + hostResult.DiscoveredCount + " RedLoader candidate(s), "
                + hostResult.HostableCount + " hostable, " + hostResult.HostedMods.Count + " hosted.";
            if (!string.IsNullOrWhiteSpace(hostResult.Reason))
            {
                detail += " " + hostResult.Reason;
            }

            return detail;
        }

        private static string TrimForState(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private static bool ShouldEnableModHosting()
        {
            var value = Environment.GetEnvironmentVariable(EnableRedLoaderModsEnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return IsTruthy(value);
            }

            return true;
        }

        private static string CurrentDotnetLabel()
        {
            return ".NET " + Environment.Version.Major;
        }

        internal static bool TryInitializeSupportModule(Action<string> addStartupWarning)
        {
            if (_supportModuleInitialized)
            {
                return true;
            }

            try
            {
                GlobalBehaviour.Init();
                _supportModuleInitialized = true;
                OpenACAIEndnightRuntime.TraceCompat("OpenACAILegacyRedLoaderCompatibility.TryInitializeSupportModule succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke("RedLoader support module compatibility init failed; coroutine support may be unavailable: " + ex.Message);
                OpenACAIEndnightRuntime.TraceCompat("TryInitializeSupportModule failed: " + ex);
                return false;
            }
        }

        internal static bool IsSupportModuleReady()
        {
            return GlobalBehaviour.Instance != null;
        }

        internal static bool IsRedLoaderModsButtonEnabled()
        {
            return IsTruthy(Environment.GetEnvironmentVariable(EnableRedLoaderModsButtonEnvironmentVariableName));
        }

        internal static void StartObservedCoroutine(IEnumerator routine, Action<Exception> onStartFailure)
        {
            var observed = new ObservedCoroutine(routine, onStartFailure);
            try { Coroutines.Start(observed); }
            catch (Exception error) { observed.ReportStartFailure(error); }
        }

        internal static void QueueCoroutineStart(IEnumerator routine)
        {
            if (routine == null)
            {
                return;
            }

            int queuedCount;
            lock (_queuedCoroutineLock)
            {
                _queuedCoroutineStarts.Enqueue(routine);
                queuedCount = _queuedCoroutineStarts.Count;
            }

            OpenACAIEndnightRuntime.TraceCompat("Queued RedLoader coroutine start; queued count = " + queuedCount);
        }

        internal static int DrainQueuedCoroutines()
        {
            int pendingCount;
            lock (_queuedCoroutineLock)
            {
                pendingCount = _queuedCoroutineStarts.Count;
            }

            var started = 0;
            // Keep unattempted work queued and defer new entries to the next tick.
            for (var remaining = pendingCount; remaining > 0; remaining--)
            {
                if (!IsSupportModuleReady())
                {
                    break;
                }

                IEnumerator routine;
                lock (_queuedCoroutineLock)
                {
                    if (_queuedCoroutineStarts.Count == 0)
                    {
                        break;
                    }

                    routine = _queuedCoroutineStarts.Dequeue();
                }

                try
                {
                    var startedToken = Coroutines.Start(routine);
                    if (startedToken != null)
                    {
                        started++;
                    }
                }
                catch (Exception ex)
                {
                    // Start can advance the enumerator before throwing; do not replay it.
                    if (routine is ObservedCoroutine observed)
                        observed.ReportStartFailure(ex);
                    OpenACAIEndnightRuntime.TraceCompat("Queued RedLoader coroutine failed: " + ex);
                }
            }

            if (started > 0)
            {
                OpenACAIEndnightRuntime.TraceCompat("Started " + started + " queued RedLoader coroutine(s).");
            }

            return started;
        }

        private sealed class ObservedCoroutine : IEnumerator, IDisposable
        {
            private readonly IEnumerator _routine;
            private Action<Exception> _onStartFailure;

            internal ObservedCoroutine(IEnumerator routine, Action<Exception> onStartFailure)
            {
                ArgumentNullException.ThrowIfNull(routine);
                ArgumentNullException.ThrowIfNull(onStartFailure);
                _routine = routine;
                _onStartFailure = onStartFailure;
            }

            public object Current => _routine.Current;
            public bool MoveNext() => _routine.MoveNext();
            public void Reset() => _routine.Reset();
            public void Dispose() => (_routine as IDisposable)?.Dispose();

            internal void ReportStartFailure(Exception error)
            {
                var callback = _onStartFailure;
                _onStartFailure = null;
                if (callback == null)
                    return;

                // Keep notification with the queued iterator, without claiming native ownership.
                try { callback(error); }
                catch (Exception callbackError)
                {
                    OpenACAIEndnightRuntime.TraceCompat("Coroutine start failure handler failed: "
                        + callbackError.GetType().FullName);
                }
            }
        }

        private static bool IsTruthy(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static void InitializeLoaderEnvironment(string executablePath, string[] dllSearchPath)
        {
            var gameRoot = Path.GetDirectoryName(executablePath);
            var gameExecutableName = Path.GetFileNameWithoutExtension(executablePath);
            var loaderDirectory = Path.Combine(gameRoot, "_Redloader");
            var userDataDirectory = Path.Combine(gameRoot, "UserData");
            var modsDirectory = Path.Combine(gameRoot, "Mods");
            var patcherPluginPath = Path.Combine(loaderDirectory, "Patchers");
            var loaderAssemblyDirectory = Path.Combine(loaderDirectory, "net6");

            SetLoaderEnvironmentProperty("RedloaderVersionString", GetRedLoaderVersionString());
            SetLoaderEnvironmentProperty("GameExecutablePath", executablePath);
            SetLoaderEnvironmentProperty("GameExecutableName", gameExecutableName);
            SetLoaderEnvironmentProperty("IsDedicatedServer", executablePath.EndsWith("DS.exe", StringComparison.OrdinalIgnoreCase));
            SetLoaderEnvironmentProperty("GameRootDirectory", gameRoot);
            SetLoaderEnvironmentProperty("UnityGameDataDirectory", Path.Combine(gameRoot, gameExecutableName + "_Data"));
            SetLoaderEnvironmentProperty("LoaderDirectory", loaderDirectory);
            SetLoaderEnvironmentProperty("UserDataDirectory", userDataDirectory);
            SetLoaderEnvironmentProperty("ModsDirectory", modsDirectory);
            SetLoaderEnvironmentProperty("PatcherPluginPath", patcherPluginPath);
            SetLoaderEnvironmentProperty("LoaderAssemblyDirectory", loaderAssemblyDirectory);
            SetLoaderEnvironmentProperty("LoaderAssemblyPath", Path.Combine(loaderAssemblyDirectory, "RedLoader.dll"));
            SetLoaderEnvironmentProperty("CachePath", Path.Combine(loaderDirectory, "cache"));
            SetLoaderEnvironmentProperty("DllSearchPaths", dllSearchPath ?? Array.Empty<string>());

            Directory.CreateDirectory(modsDirectory);
            Directory.CreateDirectory(patcherPluginPath);
            Directory.CreateDirectory(userDataDirectory);
            Directory.CreateDirectory(Path.Combine(modsDirectory, "Blueprints"));
        }

        private static string GetRedLoaderVersionString()
        {
            var informationalVersion = typeof(LoaderEnvironment).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var trimmed = informationalVersion.Split(new[] { '+', '-' }, 2)[0];
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }

            var version = typeof(LoaderEnvironment).Assembly.GetName().Version;
            return version != null
                ? version.Major + "." + version.Minor + "." + version.Build
                : "unknown";
        }

        private static void SetLoaderEnvironmentProperty(string propertyName, object value)
        {
            var property = typeof(LoaderEnvironment).GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = property?.GetSetMethod(true);
            if (setter == null)
            {
                throw new MissingMethodException(typeof(LoaderEnvironment).FullName, propertyName + " setter");
            }

            setter.Invoke(null, new[] { value });
        }

        private static void InvokeNonPublicStatic(string typeName, string methodName)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                throw new TypeLoadException("Could not find type " + typeName + ".");
            }

            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            method?.Invoke(null, null);
        }

        private static bool _supportModuleInitialized;
        private static readonly object RuntimeIssueSync = new object();
        private static int _redLoaderRuntimeErrorCount;
        private static string _lastRedLoaderRuntimeError;
        private static string _lastRedLoaderRuntimeErrorUtc;
        private static readonly Queue<IEnumerator> _queuedCoroutineStarts = new Queue<IEnumerator>();
        private static readonly object _queuedCoroutineLock = new object();
    }

    internal sealed class RedLoaderHostResult
    {
        private readonly IReadOnlyList<OpenAcaiRedLoaderCandidate> _candidates;
        private readonly IReadOnlyList<object> _loadReports;
        private readonly string _reason;

        private RedLoaderHostResult(
            string mode,
            bool initializeSonsSdk,
            IReadOnlyList<ModBase> hostedMods,
            IReadOnlyList<OpenAcaiRedLoaderCandidate> candidates,
            IReadOnlyList<object> loadReports,
            string reason,
            IReadOnlyList<string> discoveryErrors = null)
        {
            Mode = mode;
            InitializeSonsSdk = initializeSonsSdk;
            HostedMods = hostedMods;
            _candidates = candidates;
            _loadReports = loadReports;
            _reason = reason;
            DiscoveryErrors = Array.AsReadOnly((discoveryErrors ?? Array.Empty<string>()).ToArray());
        }

        internal string Mode { get; }
        internal bool InitializeSonsSdk { get; }
        internal IReadOnlyList<ModBase> HostedMods { get; }
        internal int HostedCount => HostedMods.Count;
        internal string Reason => _reason;
        internal int DiscoveredCount => _candidates.Count;
        internal int HostableCount => _candidates.Count(candidate => candidate.IsHostable);
        internal IReadOnlyList<string> DiscoveryErrors { get; }

        internal RedLoaderHostResult WithDiscoveryErrors(IReadOnlyList<string> errors) =>
            new(Mode, InitializeSonsSdk, HostedMods, _candidates, _loadReports, _reason, errors);

        internal static RedLoaderHostResult InventoryOnly(IReadOnlyList<OpenAcaiRedLoaderCandidate> candidates, string reason)
        {
            return new RedLoaderHostResult(
                "inventory-only",
                false,
                Array.Empty<ModBase>(),
                candidates,
                CreateInventoryOnlyReports(candidates, reason),
                reason);
        }

        internal static RedLoaderHostResult Upstream(IReadOnlyList<ModBase> hostedMods, IReadOnlyList<OpenAcaiRedLoaderCandidate> candidates)
        {
            return new RedLoaderHostResult("upstream-redloader", hostedMods.Count > 0, hostedMods, candidates, Array.Empty<object>(), null);
        }

        internal static RedLoaderHostResult OpenAcai(IReadOnlyList<ModBase> hostedMods, IReadOnlyList<OpenAcaiRedLoaderCandidate> candidates, IReadOnlyList<object> loadReports)
        {
            return new RedLoaderHostResult("openacai-managed", hostedMods.Count > 0, hostedMods, candidates, loadReports, null);
        }

        internal object CreateState()
        {
            return new
            {
                mode = Mode,
                reason = _reason,
                owner = "Modstoke Loader",
                compatibilityTarget = "RedLoader/SonsSdk",
                initializeSonsSdk = InitializeSonsSdk,
                discoveryComplete = DiscoveryErrors.Count == 0,
                discoveryErrors = DiscoveryErrors,
                discoveredCount = _candidates.Count,
                hostableCount = _candidates.Count(candidate => candidate.IsHostable),
                hostedCount = HostedMods.Count,
                controls = new
                {
                    enableHostingEnvironmentVariable = OpenACAIEndnightRuntime.EnableRedLoaderModsEnvironmentVariableName,
                    allowlistEnvironmentVariable = OpenACAIEndnightRuntime.RedLoaderModAllowlistEnvironmentVariableName,
                    modernRuntimeRequiresAllowlist = false,
                    allowlistWildcard = "*",
                    upstreamBootstrapEnvironmentVariable = OpenACAIEndnightRuntime.UseUpstreamRedLoaderBootstrapEnvironmentVariableName,
                    legacyModsButton = new
                    {
                        enabled = OpenACAILegacyRedLoaderCompatibility.IsRedLoaderModsButtonEnabled(),
                        suppressedByDefault = true,
                        enableEnvironmentVariable = OpenACAIEndnightRuntime.EnableRedLoaderModsButtonEnvironmentVariableName,
                        replacementSurface = "Modstoke mods menu"
                    }
                },
                candidates = _candidates.Select(candidate => candidate.CreateState()).ToArray(),
                loadReports = _loadReports.ToArray()
            };
        }

        private static IReadOnlyList<object> CreateInventoryOnlyReports(IReadOnlyList<OpenAcaiRedLoaderCandidate> candidates, string reason)
        {
            return (candidates ?? Array.Empty<OpenAcaiRedLoaderCandidate>())
                .Select(candidate => candidate.CreateLoadReport("skipped", GetInventoryOnlySkipReason(candidate, reason), 0))
                .ToArray();
        }

        private static string GetInventoryOnlySkipReason(OpenAcaiRedLoaderCandidate candidate, string reason)
        {
            if (candidate == null)
            {
                return reason;
            }

            if (!candidate.Enabled)
            {
                return "Disabled package or assembly path";
            }

            if (candidate.IsLibrary)
            {
                return "RedLoader library assembly";
            }

            if (string.IsNullOrWhiteSpace(candidate.AssemblyPath) || !File.Exists(candidate.AssemblyPath))
            {
                return "No mod assembly was found for the manifest";
            }

            return reason;
        }
    }

    internal static class OpenAcaiRedLoaderHost
    {
        private const int MaxManifestDepth = 4;

        internal static IReadOnlyList<OpenAcaiRedLoaderCandidate> Discover(string modsDirectory)
        {
            return Discover(modsDirectory, null);
        }

        internal static IReadOnlyList<OpenAcaiRedLoaderCandidate> Discover(string modsDirectory, string libsDirectory)
        {
            return Discover(modsDirectory, libsDirectory, out _);
        }

        internal static IReadOnlyList<OpenAcaiRedLoaderCandidate> Discover(string modsDirectory, string libsDirectory, out IReadOnlyList<string> errors)
        {
            var candidates = new List<OpenAcaiRedLoaderCandidate>();
            var failures = new List<string>();
            var seenFailures = new HashSet<string>(StringComparer.Ordinal);
            void Report(string path, Exception error)
            {
                var detail = path + ": " + error.GetType().Name + ": " + error.Message;
                if (seenFailures.Add(detail))
                    failures.Add(detail);
            }

            candidates.AddRange(DiscoverRoot(modsDirectory, "legacy-root-dll", "manifest-package", "Mod", Report));
            candidates.AddRange(DiscoverRoot(libsDirectory, "legacy-library-root-dll", "library-manifest-package", "Library", Report));
            errors = Array.AsReadOnly(failures.ToArray());

            return candidates
                .GroupBy(candidate => candidate.DeduplicationKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(candidate => candidate.Score).First())
                .OrderBy(candidate => candidate.Id ?? candidate.Name ?? candidate.ManifestPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<OpenAcaiRedLoaderCandidate> DiscoverRoot(string rootDirectory, string rootDllSourceShape,
            string manifestSourceShape, string defaultManifestType, Action<string, Exception> report)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                yield break;
            }

            foreach (var assemblyPath in OpenACAIDiscoveryFiles.EnumerateFiles(rootDirectory, "*.dll", SearchOption.TopDirectoryOnly, report))
            {
                if (IsDisabledPath(assemblyPath))
                {
                    continue;
                }

                OpenAcaiRedLoaderCandidate candidate;
                try
                {
                    var manifestPath = ResolveLegacyManifestPath(assemblyPath);
                    candidate = CreateCandidate(rootDirectory, manifestPath, assemblyPath, rootDllSourceShape, defaultManifestType, report);
                }
                catch (Exception ex)
                {
                    report(assemblyPath, ex);
                    continue;
                }
                yield return candidate;
            }

            foreach (var manifestPath in FindManifestFiles(rootDirectory, 0, report))
            {
                OpenAcaiRedLoaderCandidate candidate;
                try
                {
                    var assemblyPath = FindAssemblyForManifest(rootDirectory, manifestPath, report);
                    candidate = CreateCandidate(rootDirectory, manifestPath, assemblyPath, manifestSourceShape, defaultManifestType, report);
                }
                catch (Exception ex)
                {
                    report(manifestPath, ex);
                    continue;
                }
                yield return candidate;
            }
        }

        internal static ISet<string> ParseAllowlist(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                rawValue
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        internal static RedLoaderHostResult Load(IReadOnlyList<OpenAcaiRedLoaderCandidate> candidates, ISet<string> allowlist)
        {
            var hostedMods = new List<ModBase>();
            var loadReports = new List<object>();
            var attemptedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                var skipReason = GetSkipReason(candidate, allowlist, attemptedIds);
                if (skipReason != null)
                {
                    loadReports.Add(candidate.CreateLoadReport("skipped", skipReason, 0));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(candidate.Id))
                {
                    attemptedIds.Add(candidate.Id);
                }

                TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI RedLoader host loading " + candidate.AssemblyPath));
                SonsMod loaded;
                try
                {
                    loaded = OpenACAIRedLoaderModActivation.Activate(candidate.AssemblyPath, candidate.ManifestPath);
                }
                catch (Exception ex)
                {
                    RecordLoadFailure(candidate, DescribeLoadFailure(ex), loadReports);
                    TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI RedLoader host exception for " + candidate.AssemblyPath + ": " + ex));
                    continue;
                }

                hostedMods.Add(loaded);
                loadReports.Add(candidate.CreateLoadReport("loaded", null, 1));
                TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI RedLoader host loaded 1 mod type from " + candidate.AssemblyPath));
            }

            return RedLoaderHostResult.OpenAcai(hostedMods, candidates, loadReports);
        }

        private static void RecordLoadFailure(OpenAcaiRedLoaderCandidate candidate, string reason, List<object> reports)
        {
            reports.Add(candidate.CreateLoadReport("failed", reason, 0));
            TryReport(() => OpenACAIEndnightRuntime.TraceCompat("OpenACAI RedLoader host failed for " + candidate.AssemblyPath + ": " + reason));
            TryReport(() => OpenACAIEndnightRuntime.LogSource?.LogWarning("RedLoader mod not loaded: " + candidate.DisplayName + " - " + reason));
        }

        private static string DescribeLoadFailure(Exception error)
        {
            try
            {
                if (error is ReflectionTypeLoadException typeLoad)
                {
                    var details = (typeLoad.LoaderExceptions ?? Array.Empty<Exception>())
                        .Where(exception => exception != null)
                        .Select(exception => exception.Message)
                        .Where(message => !string.IsNullOrWhiteSpace(message));
                    var message = string.Join("; ", details);
                    return "Type load failure: " + (message.Length == 0 ? error.Message : message);
                }
                return error.Message;
            }
            catch
            {
                // Mod-defined exception formatting must not hide the activation failure.
                return error.GetType().FullName;
            }
        }

        private static void TryReport(Action report)
        {
            try { report(); }
            catch { /* Diagnostics cannot change a mod's outcome or stop the next load. */ }
        }

        private static string GetSkipReason(OpenAcaiRedLoaderCandidate candidate, ISet<string> allowlist, ISet<string> attemptedIds)
        {
            if (!candidate.Enabled)
            {
                return "Disabled package or assembly path";
            }

            if (candidate.IsLibrary)
            {
                return "RedLoader library assembly";
            }

            if (string.IsNullOrWhiteSpace(candidate.AssemblyPath) || !File.Exists(candidate.AssemblyPath))
            {
                return "No mod assembly was found for the manifest";
            }

            if (!string.IsNullOrWhiteSpace(candidate.Id) && attemptedIds.Contains(candidate.Id))
            {
                return "Duplicate RedLoader mod id";
            }

            if (allowlist.Count > 0 && !candidate.MatchesAllowlist(allowlist))
            {
                return "Not included in " + OpenACAIEndnightRuntime.RedLoaderModAllowlistEnvironmentVariableName;
            }

            return null;
        }

        private static OpenAcaiRedLoaderCandidate CreateCandidate(string rootDirectory, string manifestPath, string assemblyPath,
            string sourceShape, string defaultManifestType, Action<string, Exception> report)
        {
            var effectiveManifestPath = !string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath)
                ? manifestPath
                : null;
            var manifest = ReadManifest(effectiveManifestPath, report);
            var packagePath = !string.IsNullOrWhiteSpace(effectiveManifestPath)
                ? Path.GetDirectoryName(effectiveManifestPath)
                : Path.GetDirectoryName(assemblyPath);
            var assemblyName = !string.IsNullOrWhiteSpace(assemblyPath)
                ? Path.GetFileNameWithoutExtension(assemblyPath)
                : null;
            var id = ReadManifestValue(manifest, "id") ?? assemblyName ?? Path.GetFileName(packagePath);
            var name = ReadManifestValue(manifest, "name") ?? id;
            var manifestType = ReadManifestValue(manifest, "type") ?? defaultManifestType ?? "Mod";

            return new OpenAcaiRedLoaderCandidate(
                id,
                name,
                ReadManifestValue(manifest, "version"),
                ReadManifestAuthor(manifest),
                ReadManifestValue(manifest, "platform") ?? "Client",
                manifestType,
                effectiveManifestPath,
                packagePath,
                assemblyPath,
                sourceShape,
                !IsDisabledPath(packagePath) && !IsDisabledPath(assemblyPath),
                ScoreCandidate(rootDirectory, effectiveManifestPath, assemblyPath));
        }

        private static IEnumerable<string> FindManifestFiles(string root, int depth, Action<string, Exception> report)
        {
            if (string.IsNullOrWhiteSpace(root) || depth > MaxManifestDepth || !Directory.Exists(root))
            {
                yield break;
            }

            foreach (var file in OpenACAIDiscoveryFiles.EnumerateFiles(root, "manifest.json", SearchOption.TopDirectoryOnly, report))
            {
                if (!IsDisabledPath(file))
                {
                    yield return file;
                }
            }

            if (depth == MaxManifestDepth)
            {
                yield break;
            }

            foreach (var directory in OpenACAIDiscoveryFiles.GetDirectories(root, report))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, "_Disabled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var nested in FindManifestFiles(directory, depth + 1, report))
                {
                    yield return nested;
                }
            }
        }

        private static string FindAssemblyForManifest(string modsDirectory, string manifestPath, Action<string, Exception> report)
        {
            var manifestDirectory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(manifestDirectory))
            {
                return null;
            }

            var manifest = ReadManifest(manifestPath, report);
            var packageRoot = GetPackageRoot(modsDirectory, manifestDirectory);
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ReadManifestValue(manifest, "id"),
                ReadManifestValue(manifest, "name"),
                Path.GetFileName(manifestDirectory),
                Path.GetFileName(packageRoot)
            };

            foreach (var assemblyName in assemblyNames.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                foreach (var path in GetLikelyAssemblyPaths(packageRoot, manifestDirectory, assemblyName))
                {
                    if (File.Exists(path) && !IsDisabledPath(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }

                foreach (var path in Directory.EnumerateFiles(packageRoot, assemblyName + ".dll", SearchOption.AllDirectories))
                {
                    if (!IsDisabledPath(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
            }

            return Directory.EnumerateFiles(manifestDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(path => !IsDisabledPath(path))
                .Select(Path.GetFullPath)
                .FirstOrDefault();
        }

        private static IEnumerable<string> GetLikelyAssemblyPaths(string packageRoot, string manifestDirectory, string assemblyName)
        {
            yield return Path.Combine(manifestDirectory, assemblyName + ".dll");
            yield return Path.Combine(Directory.GetParent(manifestDirectory)?.FullName ?? manifestDirectory, assemblyName + ".dll");
            yield return Path.Combine(packageRoot, assemblyName + ".dll");
            yield return Path.Combine(packageRoot, "Mods", assemblyName + ".dll");
            yield return Path.Combine(packageRoot, "Mods", assemblyName, assemblyName + ".dll");
        }

        private static string ResolveLegacyManifestPath(string assemblyPath)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            return Path.Combine(Path.GetDirectoryName(assemblyPath) ?? "", assemblyName, "manifest.json");
        }

        private static string GetPackageRoot(string modsDirectory, string path)
        {
            var modsRoot = Path.GetFullPath(modsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = new DirectoryInfo(Path.GetFullPath(path));
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    modsRoot, StringComparison.OrdinalIgnoreCase))
                return current.FullName;
            while (current.Parent != null &&
                   !string.Equals(current.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                  modsRoot,
                                  StringComparison.OrdinalIgnoreCase))
            {
                current = current.Parent;
            }

            if (current.Parent == null)
                throw new ArgumentException("The package path is outside its mod root.", nameof(path));
            return current.FullName;
        }

        private static int ScoreCandidate(string modsDirectory, string manifestPath, string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return 100;
            }

            var legacyManifestPath = ResolveLegacyManifestPath(assemblyPath);
            if (string.Equals(legacyManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(Path.GetDirectoryName(assemblyPath), Path.GetDirectoryName(manifestPath), StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            var packageRoot = GetPackageRoot(modsDirectory, Path.GetDirectoryName(manifestPath) ?? modsDirectory);
            return assemblyPath.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? 5 : 10;
        }

        private static Dictionary<string, string> ReadManifest(string manifestPath, Action<string, Exception> report)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    return document.RootElement.EnumerateObject()
                        .Where(property => property.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                report(manifestPath, ex);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string ReadManifestValue(IReadOnlyDictionary<string, string> manifest, string key)
        {
            string value;
            return manifest != null && manifest.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        }

        private static string ReadManifestAuthor(IReadOnlyDictionary<string, string> manifest)
        {
            return ReadManifestValue(manifest, "author")
                ?? ReadManifestValue(manifest, "creator")
                ?? ReadManifestValue(manifest, "createdBy")
                ?? ReadManifestValue(manifest, "created_by")
                ?? ReadManifestValue(manifest, "owner");
        }

        private static bool IsDisabledPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && (path.IndexOf(Path.DirectorySeparatorChar + "_Disabled" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
                    || path.EndsWith(Path.DirectorySeparatorChar + "_Disabled", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".pending", StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class OpenAcaiRedLoaderCandidate
    {
        internal OpenAcaiRedLoaderCandidate(
            string id,
            string name,
            string version,
            string author,
            string platform,
            string manifestType,
            string manifestPath,
            string packagePath,
            string assemblyPath,
            string sourceShape,
            bool enabled,
            int score)
        {
            Id = id;
            Name = name;
            Version = version;
            Author = author;
            Platform = platform;
            ManifestType = manifestType;
            ManifestPath = SafeFullPath(manifestPath);
            PackagePath = SafeFullPath(packagePath);
            AssemblyPath = SafeFullPath(assemblyPath);
            SourceShape = sourceShape;
            Enabled = enabled;
            Score = score;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal string Version { get; }
        internal string Author { get; }
        internal string Platform { get; }
        internal string ManifestType { get; }
        internal string ManifestPath { get; }
        internal string PackagePath { get; }
        internal string AssemblyPath { get; }
        internal string SourceShape { get; }
        internal bool Enabled { get; }
        internal int Score { get; }
        internal bool IsLibrary => string.Equals(ManifestType, "Library", StringComparison.OrdinalIgnoreCase);
        internal bool IsHostable => Enabled && !IsLibrary && !string.IsNullOrWhiteSpace(AssemblyPath) && File.Exists(AssemblyPath);
        internal string DisplayName => Name ?? Id ?? Path.GetFileNameWithoutExtension(AssemblyPath) ?? ManifestPath;
        internal string DeduplicationKey => !string.IsNullOrWhiteSpace(AssemblyPath) ? AssemblyPath : ManifestPath;

        internal bool MatchesAllowlist(ISet<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0)
            {
                return true;
            }

            if (allowlist.Contains("*"))
            {
                return true;
            }

            var assemblyName = !string.IsNullOrWhiteSpace(AssemblyPath) ? Path.GetFileNameWithoutExtension(AssemblyPath) : null;
            return Matches(allowlist, Id)
                || Matches(allowlist, Name)
                || Matches(allowlist, assemblyName);
        }

        internal object CreateState()
        {
            return new
            {
                id = Id,
                name = Name,
                version = Version,
                author = Author,
                platform = Platform,
                manifestType = ManifestType,
                sourceShape = SourceShape,
                enabled = Enabled,
                hostable = IsHostable,
                manifestPath = ManifestPath,
                packagePath = PackagePath,
                assemblyPath = AssemblyPath
            };
        }

        internal object CreateLoadReport(string status, string reason, int loadedTypeCount)
        {
            return new
            {
                id = Id,
                name = Name,
                assemblyPath = AssemblyPath,
                status,
                reason,
                loadedTypeCount
            };
        }

        private static bool Matches(ISet<string> allowlist, string value)
        {
            return !string.IsNullOrWhiteSpace(value) && allowlist.Contains(value);
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
