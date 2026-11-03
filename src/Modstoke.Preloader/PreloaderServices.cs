// SPDX-License-Identifier: GPL-3.0-or-later
// Runtime startup and preload adapted from BepInEx's Il2CppInteropManager.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using BepInEx.Unity.Common;
using Il2CppInterop.Common;
using Il2CppInterop.HarmonySupport;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Startup;

namespace Modstoke.Preloader
{
    // Public entrypoints use BCL types so Bootstrap can bind them after its resolver is installed.
    public sealed class PreloaderServices
    {
        private const string DatabaseLocationVariable = "IL2CPP_INTEROP_DATABASES_LOCATION";
        private static readonly JsonSerializerOptions StateJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private static readonly object OwnershipSync = new object();
        private static readonly Lazy<PreloaderServices> SharedInstance = new(() => new PreloaderServices());
        private static PreloaderServices _owner;
        private readonly object _resolverSync = new object();
        private readonly InteropSettings _settings = new InteropSettings();
        private bool _sharedInstanceRequested;
        private int _initializationAttempted;
        private string _initializationStatus = "not-started";
        private string _initializationDetail = "";
        private InteropNativeTypeRegistration.Report _nativeTypePublication = InteropNativeTypeRegistration.Report.NotAttempted;
        private bool _nativeResolverInstalled;
        private bool _assemblyResolverInstalled;
        private InteropGeneration _generation;
        private bool _wrappersValidated;
        private AssemblyPatchingState _patching;
        private string _preloadStatus = "not-started";
        private int _preloadedAssemblyCount;
        private int _preloadFailureCount;
        private PreloaderLogging _logging;

        public string InteropAssemblyPath => _settings.InteropDirectory;
        public string GameAssemblyPath => _settings.GameAssemblyPath;

        public static object GetSharedInstance()
        {
            lock (OwnershipSync)
            {
                if (_owner == null)
                {
                    var instance = SharedInstance.Value;
                    _owner ??= instance;
                }
                _owner._sharedInstanceRequested = true;
                return _owner;
            }
        }

        private void ClaimOwnership()
        {
            lock (OwnershipSync)
            {
                if (_owner == null)
                    _owner = this;
                else if (!ReferenceEquals(_owner, this))
                    throw new InvalidOperationException("Another preloader services instance owns runtime startup. " +
                        "Use PreloaderServices.GetSharedInstance() to access it.");
            }
        }

        public object AttachPreloaderLogging()
        {
            ClaimOwnership();
            LogEventDispatcher.Install();
            ConsoleLogging.Attach();
            NativeHookStatusWriter.Install();
            _logging ??= new PreloaderLogging();
            try { return _logging.Attach(); }
            finally { ConsoleLogging.RegisterShutdown(); }
        }

        public void StartRuntimeLogging()
        {
            _logging.StartRuntimeLogging();
            _logging.AttachRuntimeSources();
            ConsoleLogging.SetPhase("runtime", "Game runtime logging is active.");
        }
        public void PrepareCompatibilityLogging()
        {
            _logging.PrepareCompatibilityLogging();
            ConsoleLogging.SetPhase("runtime", "Compatibility runtime logging is active.");
        }
        public void AbortPreloaderLogging(Exception error) => _logging?.Abort(error);

        public void AttachNativeRuntimeLogging()
        {
            RequireReadyRuntime();
            _logging.AttachNativeRuntimeLogging();
        }

        public string CreateLoggingStateJson() => JsonSerializer.Serialize(_logging?.CreateState());

        public void InstallGameAssemblyResolver()
        {
            ClaimOwnership();
            lock (_resolverSync)
            {
                if (_nativeResolverInstalled)
                    return;

                NativeLibrary.SetDllImportResolver(typeof(IL2CPP).Assembly, ResolveNativeLibrary);
                Volatile.Write(ref _nativeResolverInstalled, true);
            }
        }

        public void InitializeInterop()
        {
            ClaimOwnership();
            if (!Volatile.Read(ref _nativeResolverInstalled))
                throw new InvalidOperationException("The GameAssembly resolver must be installed before IL2CPP startup.");
            if (Interlocked.CompareExchange(ref _initializationAttempted, 1, 0) != 0)
                throw new InvalidOperationException("IL2CPP interop initialization has already been attempted.");

            Volatile.Write(ref _initializationStatus, "starting");
            var previousDatabaseLocation = Environment.GetEnvironmentVariable(DatabaseLocationVariable);
            Il2CppInteropRuntime runtime = null;
            ManualLogSource logSource = null;
            try
            {
                Environment.SetEnvironmentVariable(DatabaseLocationVariable, InteropAssemblyPath);
                AssemblyLoadContext.Default.Resolving += ResolveManagedInteropAssembly;
                AppDomain.CurrentDomain.AssemblyResolve += ResolveInteropAssembly;
                _assemblyResolverInstalled = true;

                _generation = new InteropGeneration(_settings);
                _generation.EnsureReady();
                _wrappersValidated = true;

                var version = UnityInfo.Version;
                logSource = Logger.CreateLogSource("Il2CppInterop");
                runtime = Il2CppInteropRuntime.Create(new RuntimeConfiguration
                {
                    UnityVersion = new Version(version.Major, version.Minor, version.Build),
                    DetourProvider = new InteropDetourProvider()
                });
                runtime.AddLogger(new InteropLogger(logSource)).AddHarmonySupport().Start();
                var publication = InteropNativeTypeRegistration.TryPublish(GameAssemblyPath);
                Volatile.Write(ref _nativeTypePublication, publication);

                _initializationDetail = "IL2CPP interop started using validated wrappers. " + publication.Describe();
                Volatile.Write(ref _initializationStatus, "ready");
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _initializationStatus, "failed");
                try
                {
                    _initializationDetail = ex.Message;
                }
                catch
                {
                    _initializationDetail = "IL2CPP interop startup failed; exception message unavailable.";
                }

                CleanupFailedInterop("dispose runtime", () => runtime?.Dispose());
                CleanupFailedInterop("detach assembly resolver", () =>
                {
                    AssemblyLoadContext.Default.Resolving -= ResolveManagedInteropAssembly;
                    AppDomain.CurrentDomain.AssemblyResolve -= ResolveInteropAssembly;
                    _assemblyResolverInstalled = false;
                });
                CleanupFailedInterop("restore database location", () =>
                    Environment.SetEnvironmentVariable(DatabaseLocationVariable, previousDatabaseLocation));
                if (logSource != null)
                {
                    CleanupFailedInterop("remove log source", () => Logger.Sources.Remove(logSource));
                    CleanupFailedInterop("dispose log source", logSource.Dispose);
                }
                throw;
            }
        }

        private static void CleanupFailedInterop(string operation, Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception error)
            {
                try
                {
                    PreloaderLog.Log.LogWarning($"IL2CPP startup cleanup failed ({operation}): {error}");
                }
                catch
                {
                    // A failing log listener must not interrupt cleanup or replace the startup error.
                }
            }
        }

        public void RunAssemblyPatchers() => RunAssemblyPatchers(null);

        public void RunCompatibilityAssemblyPatchers() => RunAssemblyPatchers((bytes, _) => Assembly.Load(bytes));

        private void RunAssemblyPatchers(Func<byte[], string, Assembly> loadAssembly)
        {
            RequireReadyRuntime();
            if (_patching != null)
                throw new InvalidOperationException("Assembly patching has already been attempted.");

            _patching = new AssemblyPatchingState
            {
                CompatibilityEngineUsed = false,
                Status = "running",
                Detail = "Modstoke is running its patcher engine with BepInEx-compatible plugin contracts."
            };
            try
            {
                AssemblyPatching.Run(InteropAssemblyPath, _patching, loadAssembly);
            }
            catch (Exception ex)
            {
                _patching.Status = "failed";
                _patching.Detail = InteropGenerationDiagnostics.Message(ex);
                throw;
            }
        }

        public void PreloadInteropAssemblies()
        {
            RequireReadyRuntime();
            if (_patching?.Completed != true)
                throw new InvalidOperationException("Assembly patching must complete before interop preloading.");
            if (_preloadStatus != "not-started")
                return;
            InteropClassInitResolver.TryInstall(GameAssemblyPath);
            if (!_settings.PreloadEnabled)
            {
                _preloadStatus = "disabled";
                return;
            }

            _preloadStatus = "loading";
            var elapsed = Stopwatch.StartNew();
            try
            {
                var names = Directory.EnumerateFiles(InteropAssemblyPath, "*.dll")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.Equals(name, "netstandard", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "Il2Cppnetstandard", StringComparison.OrdinalIgnoreCase));
                Parallel.ForEach(names, name =>
                {
                    try
                    {
                        // Name-based loading reuses patched assemblies already loaded by the preloader.
                        var assembly = Assembly.Load(new AssemblyName(name));
                        InteropGenerationDiagnostics.Log(LogLevel.Debug,
                            $"Preloaded interop assembly {name} from {assembly.Location} (MVID {assembly.ManifestModule.ModuleVersionId}).");
                        Interlocked.Increment(ref _preloadedAssemblyCount);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _preloadFailureCount);
                        InteropGenerationDiagnostics.Log(LogLevel.Warning,
                            $"Could not preload interop assembly {name}.", ex);
                    }
                });
                _preloadStatus = _preloadFailureCount == 0 ? "completed" : "completed-with-errors";
                InteropGenerationDiagnostics.Log(LogLevel.Info, $"Preloaded {_preloadedAssemblyCount} interop assemblies in " +
                    $"{elapsed.Elapsed.TotalSeconds:F2}s ({_preloadFailureCount} failed).");
            }
            catch
            {
                _preloadStatus = "failed";
                throw;
            }
        }

        // The handle cancels preparation only. Calling apply retains the gateway until process exit.
        public IDisposable PrepareRuntimeInvokeHook(IntPtr original, Delegate callback, out Delegate trampoline, out Action apply)
        {
            RequireReadyRuntime();
            if (_patching?.Completed != true)
                throw new InvalidOperationException("Assembly patching must complete before runtime invoke hook preparation.");

            var detour = NativeDetourFactory.Create(original, callback);
            trampoline = detour.GenerateTrampoline(callback.GetType());
            apply = detour.ApplyForProcessLifetime;
            return detour;
        }

        public bool CanUseCompatibilityRuntimeInvoke(IntPtr original) => !NativeDetourFactory.HasLiveTarget(original);

        public string CreateStateJson() => JsonSerializer.Serialize(new
        {
            ownership = CreateOwnershipState(),
            initializationStatus = Volatile.Read(ref _initializationStatus),
            initializationDetail = _initializationDetail,
            nativeTypePublication = Volatile.Read(ref _nativeTypePublication),
            logging = _logging?.CreateState(),
            gameAssemblyResolverInstalled = Volatile.Read(ref _nativeResolverInstalled),
            interopAssemblyResolverInstalled = _assemblyResolverInstalled,
            compatibilityGeneratorInvoked = false,
            generation = _generation?.State,
            requiredWrappersValidated = _wrappersValidated,
            compatibilityNativeDetourEngineUsed = false,
            nativeDetours = NativeDetourFactory.CreateState(),
            nativeHookStatusWriter = NativeHookStatusWriter.CreateState(),
            patching = _patching,
            preloadStatus = _preloadStatus,
            preloadedAssemblyCount = _preloadedAssemblyCount,
            preloadFailureCount = _preloadFailureCount
        }, StateJsonOptions);

        private object CreateOwnershipState()
        {
            lock (OwnershipSync)
                return new
                {
                    ownerSelected = _owner != null,
                    isOwner = ReferenceEquals(_owner, this),
                    sharedInstanceRequested = _sharedInstanceRequested
                };
        }

        private IntPtr ResolveNativeLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
            name == "GameAssembly" ? NativeLibrary.Load(GameAssemblyPath, assembly, searchPath) : IntPtr.Zero;

        private Assembly ResolveInteropAssembly(object sender, ResolveEventArgs args) =>
            AssemblySearch.FindAndLoad(new AssemblyName(args.Name), InteropAssemblyPath, Assembly.LoadFrom);

        private Assembly ResolveManagedInteropAssembly(AssemblyLoadContext loadContext, AssemblyName name) =>
            AssemblySearch.FindAndLoad(name, InteropAssemblyPath, loadContext.LoadFromAssemblyPath);

        private void RequireReadyRuntime()
        {
            if (Volatile.Read(ref _initializationStatus) != "ready")
                throw new InvalidOperationException("IL2CPP interop has not started successfully.");
        }
    }
}
