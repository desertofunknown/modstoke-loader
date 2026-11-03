// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Modstoke-owned Doorstop target. Both startup modes use the owned preloader;
// compatibility mode retains the legacy plugin registry and execution contract.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using Modstoke.Bootstrap;

// ReSharper disable once CheckNamespace
namespace Doorstop
{
    public static class Entrypoint
    {
        private const string BootstrapModeEnvironmentVariable = "OPENACAI_BOOTSTRAP_MODE";
        private const string CompatibilityBridgeEnvironmentVariable = "OPENACAI_BOOTSTRAP_COMPATIBILITY_BRIDGE";
        private const string CompatibilityBootstrapMode = "compatibility-bridge";
        private const string NativeBootstrapMode = "openacai-native";
        private static readonly string DefaultCompatibilityBridgePath = Path.Combine("BepInEx", "core", "BepInEx.Unity.IL2CPP.dll");
        private const string StateFileName = "openacai-bootstrap-state.json";
        private const string LogFileName = "openacai-bootstrap.log";
        private static readonly object SyncRoot = new object();
        private static StreamWriter _traceWriter;
        private static string _traceLogPath;
        private static string _gameRoot;
        private static string _runtimeDirectory;
        private static string _compatibilityDirectory;
        private static string _bepInExCoreDirectory;
        private static BootstrapPlatformState _platformState;
        private static int _compatibilityStartupAttempted;
        private static RootLayout.MigrationResult _rootMigration;
        private static CecilPlatformAssemblyState[] _cecilPlatformAssemblies = Array.Empty<CecilPlatformAssemblyState>();
        private static readonly JsonSerializerOptions StateJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static void Start()
        {
            var enteredUtc = DateTimeOffset.UtcNow.ToString("O");
            var mode = Environment.GetEnvironmentVariable(BootstrapModeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = NativeBootstrapMode;
            }

            var bridgePath = "";
            try
            {
                InitializeContext();
                MigrateLegacyRoot();
                JobTempTrace.Initialize(_gameRoot, _runtimeDirectory, Trace);
                bridgePath = ResolveCompatibilityBridgePath();

                Trace("Modstoke bootstrap entered; mode " + mode + ".");
                WriteState(
                    enteredUtc,
                    mode,
                    "ENTERED",
                    false,
                    false,
                    "",
                    "Doorstop reached the Modstoke bootstrap target.",
                    bridgePath);
                ChainloaderBoundary.WriteState(
                    _gameRoot,
                    _runtimeDirectory,
                    _compatibilityDirectory,
                    _bepInExCoreDirectory,
                    enteredUtc,
                    mode,
                    "ENTERED",
                    bridgePath,
                    false,
                    false,
                    "",
                    "Doorstop reached the Modstoke chainloader boundary.");

                if (string.Equals(mode, NativeBootstrapMode, StringComparison.OrdinalIgnoreCase))
                {
                    RunOpenAcaiNativePreloader(enteredUtc, mode);
                    WriteState(
                        enteredUtc,
                        mode,
                        "READY",
                        false,
                        false,
                        "",
                        "Modstoke bootstrap ran the Modstoke preloader path successfully.",
                        bridgePath);
                    // The adapter owns the asynchronous chainloader outcome after the preloader handoff.
                    Trace("Modstoke native preloader path returned.");
                    return;
                }

                if (!string.Equals(mode, CompatibilityBootstrapMode, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unsupported Modstoke bootstrap mode: " + mode + ".");
                }

                DelegateToCompatibilityBridge(enteredUtc, mode);
                WriteState(
                    enteredUtc,
                    mode,
                    "READY",
                    true,
                    true,
                    "",
                    "Modstoke bootstrap delegated to the compatibility bridge successfully.",
                    bridgePath);
                ChainloaderBoundary.WriteState(
                    _gameRoot,
                    _runtimeDirectory,
                    _compatibilityDirectory,
                    _bepInExCoreDirectory,
                    enteredUtc,
                    mode,
                    "READY",
                    bridgePath,
                    true,
                    true,
                    "",
                    "Modstoke completed compatibility initialization; plugin execution waits for the Unity scene trigger.");
                Trace("Modstoke bootstrap returned from compatibility bridge.");
            }
            catch (Exception ex)
            {
                try
                {
                    Trace("Modstoke bootstrap failed: " + ex);
                    WriteState(
                        enteredUtc,
                        mode,
                        "FAILED",
                        false,
                        false,
                        ex.GetType().FullName ?? ex.GetType().Name,
                        ex.Message ?? "",
                        bridgePath);
                    ChainloaderBoundary.WriteState(
                        _gameRoot,
                        _runtimeDirectory,
                        _compatibilityDirectory,
                        _bepInExCoreDirectory,
                        enteredUtc,
                        mode,
                        "FAILED",
                        bridgePath,
                        false,
                        false,
                        ex.GetType().FullName ?? ex.GetType().Name,
                        ex.Message ?? "");
                }
                catch
                {
                    // Diagnostic formatting must not replace the original startup failure.
                }
                throw;
            }
        }

        private static void InitializeContext()
        {
            _platformState = null;
            _cecilPlatformAssemblies = Array.Empty<CecilPlatformAssemblyState>();
            _gameRoot = ResolveGameRoot();
            _runtimeDirectory = RootLayout.RuntimeDirectory(_gameRoot);
            _compatibilityDirectory = Path.Combine(_runtimeDirectory, "Compatibility");
            _bepInExCoreDirectory = Path.Combine(_gameRoot, "BepInEx", "core");
        }

        // Runs before the preloader, Core, console or any mod can open the old data folders.
        private static void MigrateLegacyRoot()
        {
            if (_rootMigration != null)
                return;
            try
            {
                _rootMigration = RootLayout.Migrate(_gameRoot, Trace);
            }
            catch (Exception ex)
            {
                _rootMigration = new RootLayout.MigrationResult { Failed = 1 };
                Trace("Modstoke folder migration failed: " + ex.Message);
            }
        }

        // The facade enters here without returning through Start's mode-selection branch.
        public static void RunCompatibility()
        {
            if (Interlocked.CompareExchange(ref _compatibilityStartupAttempted, 1, 0) != 0)
                throw new InvalidOperationException("Compatibility startup has already been attempted.");
            InitializeContext();
            MigrateLegacyRoot();
            InstallAssemblyResolvers();
            var envVars = LoadRequiredType("BepInEx.Preloader.Core", "BepInEx.Preloader.Core.EnvVars");
            var loadVars = envVars.GetMethod("LoadVars", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(envVars.FullName, "LoadVars");
            loadVars.CreateDelegate<Action>()();
            RunOpenAcaiNativePreloader(DateTimeOffset.UtcNow.ToString("O"), CompatibilityBootstrapMode);
        }

        private static void RunOpenAcaiNativePreloader(string enteredUtc, string mode)
        {
            var bridgePath = ResolveCompatibilityBridgePath();
            var bridgeDirectory = Path.GetDirectoryName(bridgePath) ?? _bepInExCoreDirectory;
            _bepInExCoreDirectory = bridgeDirectory;
            InstallAssemblyResolvers();
            AddSearchDirectoriesToDoorstopEnvironment(bridgeDirectory);

            ChainloaderBoundary.WriteState(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                enteredUtc,
                mode,
                "PLANNED",
                bridgePath,
                false,
                false,
                "",
                "Modstoke resolved runtime paths and is preparing its owned IL2CPP preloader path.");

            var processPath = ResolveProcessPath();
            var managedPath = ResolveManagedPath();
            var dllSearchDirectories = ResolveDllSearchDirectories();

            Trace("Modstoke native path setting BepInEx compatibility paths for API compatibility.");
            _platformState = BootstrapEnvironment.CapturePlatform();
            CompatibilityPathInitializer.Initialize(
                LoadRequiredType("BepInEx.Core", "BepInEx.Paths"),
                processPath,
                Path.Combine(_gameRoot, "BepInEx"),
                managedPath,
                dllSearchDirectories);
            _cecilPlatformAssemblies = new[]
            {
                BootstrapEnvironment.RegisterCecilDirectory(managedPath),
                BootstrapEnvironment.RegisterCecilDirectory(Path.GetDirectoryName(managedPath))
            };
            foreach (var registration in _cecilPlatformAssemblies)
            {
                if (registration.Status == "failed")
                    Trace("Modstoke Cecil platform assembly warning for " + registration.Directory + ": " + registration.Error);
            }

            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAppDomainAssembly;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAppDomainAssembly;

            ChainloaderBoundary.WriteState(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                enteredUtc,
                mode,
                "RUNNING",
                bridgePath,
                false,
                false,
                "",
                "Modstoke set compatibility paths and is invoking the IL2CPP preloader phase directly.");

            Trace("Modstoke native path invoking Modstoke IL2CPP preloader phase runner.");
            var preloaderRunner = new IL2CPPPreloaderRunner(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                bridgePath,
                enteredUtc,
                mode,
                Trace);
            preloaderRunner.Run();
        }

        private static string ResolveProcessPath()
        {
            var processPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return Path.GetFullPath(processPath);
            }

            var processName = AppDomain.CurrentDomain.FriendlyName;
            if (!string.IsNullOrWhiteSpace(processName))
            {
                var candidate = Path.Combine(_gameRoot, processName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return Path.Combine(_gameRoot, "SonsOfTheForest.exe");
        }

        private static string ResolveManagedPath()
        {
            var managedPath = Environment.GetEnvironmentVariable("DOORSTOP_MANAGED_FOLDER_DIR");
            if (!string.IsNullOrWhiteSpace(managedPath))
            {
                return Path.GetFullPath(managedPath);
            }

            return Path.Combine(_gameRoot, "dotnet", "shared", "Microsoft.NETCore.App", Environment.Version.ToString());
        }

        private static string[] ResolveDllSearchDirectories()
        {
            var current = Environment.GetEnvironmentVariable("DOORSTOP_DLL_SEARCH_DIRS");
            var directories = string.IsNullOrWhiteSpace(current)
                ? Array.Empty<string>()
                : current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            return directories
                .Concat(SearchDirectories())
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.GetFullPath(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToArray();
        }

        private static void DelegateToCompatibilityBridge(string enteredUtc, string mode)
        {
            var bridgePath = ResolveCompatibilityBridgePath();
            if (!File.Exists(bridgePath))
            {
                ChainloaderBoundary.WriteState(
                    _gameRoot,
                    _runtimeDirectory,
                    _compatibilityDirectory,
                    _bepInExCoreDirectory,
                    enteredUtc,
                    mode,
                    "FAILED",
                    bridgePath,
                    false,
                    false,
                    typeof(FileNotFoundException).FullName,
                    "Modstoke bootstrap compatibility bridge was not found.");
                throw new FileNotFoundException("Modstoke bootstrap compatibility bridge was not found.", bridgePath);
            }

            var bridgeDirectory = Path.GetDirectoryName(bridgePath) ?? _bepInExCoreDirectory;
            _bepInExCoreDirectory = bridgeDirectory;
            ChainloaderBoundary.WriteState(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                enteredUtc,
                mode,
                "PLANNED",
                bridgePath,
                false,
                false,
                "",
                "Modstoke resolved the chainloader bridge and prepared the compatibility plan.");
            InstallAssemblyResolvers();
            AddSearchDirectoriesToDoorstopEnvironment(bridgeDirectory);

            var previousInvokePath = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
            try
            {
                Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", bridgePath);
                WriteState(
                    enteredUtc,
                    mode,
                    "DELEGATING",
                    true,
                    false,
                    "",
                    "Modstoke bootstrap is entering the IL2CPP compatibility facade after rewriting DOORSTOP_INVOKE_DLL_PATH.",
                    bridgePath);
                ChainloaderBoundary.WriteState(
                    _gameRoot,
                    _runtimeDirectory,
                    _compatibilityDirectory,
                    _bepInExCoreDirectory,
                    enteredUtc,
                    mode,
                    "DELEGATING",
                    bridgePath,
                    true,
                    false,
                    "",
                    "Modstoke is invoking the IL2CPP compatibility facade from the owned chainloader boundary.");

                Trace("Loading compatibility bridge " + bridgePath + ".");
                var bridgeAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(bridgePath);
                var entrypointType = bridgeAssembly.GetType("Doorstop.Entrypoint", true, false);
                var startMethod = entrypointType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (startMethod == null)
                {
                    throw new MissingMethodException("Doorstop.Entrypoint", "Start");
                }

                startMethod.Invoke(null, null);
                ChainloaderBoundary.WriteState(
                    _gameRoot,
                    _runtimeDirectory,
                    _compatibilityDirectory,
                    _bepInExCoreDirectory,
                    enteredUtc,
                    mode,
                    "READY",
                    bridgePath,
                    true,
                    true,
                    "",
                    "The IL2CPP compatibility facade returned to the Modstoke chainloader boundary.");
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException != null)
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", previousInvokePath);
            }
        }

        private static void InstallAssemblyResolvers()
        {
            AssemblyLoadContext.Default.Resolving -= ResolveManagedAssembly;
            AssemblyLoadContext.Default.Resolving += ResolveManagedAssembly;
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAppDomainAssembly;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAppDomainAssembly;
        }

        private static Assembly ResolveManagedAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            return ResolveAssembly(assemblyName);
        }

        private static Assembly ResolveAppDomainAssembly(object sender, ResolveEventArgs args)
        {
            return ResolveAssembly(new AssemblyName(args.Name));
        }

        private static Assembly ResolveAssembly(AssemblyName assemblyName)
        {
            if (assemblyName == null || string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                return null;
            }

            return BootstrapAssemblies.FindOrLoad(assemblyName.Name, SearchDirectories());
        }

        private static Assembly LoadRequiredAssembly(string assemblyName)
        {
            var assembly = ResolveAssembly(new AssemblyName(assemblyName));
            if (assembly == null)
            {
                throw new FileNotFoundException("Modstoke bootstrap could not resolve compatibility assembly: " + assemblyName + ".", assemblyName + ".dll");
            }

            return assembly;
        }

        private static Type LoadRequiredType(string assemblyName, string typeName)
        {
            var assembly = LoadRequiredAssembly(assemblyName);
            return assembly.GetType(typeName, true, false);
        }

        private static IEnumerable<string> SearchDirectories()
        {
            yield return _runtimeDirectory;
            yield return _compatibilityDirectory;
            yield return _bepInExCoreDirectory;
            yield return Path.Combine(_gameRoot, "BepInEx", "patchers");
            yield return Path.Combine(_gameRoot, "BepInEx", "plugins");
            // Generated wrappers belong to the preloader's configured resolver, not this early fallback.
            yield return Path.Combine(_gameRoot, "BepInEx", "plugins", "OpenACAILoader");
        }

        private static void AddSearchDirectoriesToDoorstopEnvironment(string bridgeDirectory)
        {
            var directories = SearchDirectories()
                .Append(bridgeDirectory)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.GetFullPath(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToArray();
            var current = Environment.GetEnvironmentVariable("DOORSTOP_DLL_SEARCH_DIRS");
            var merged = string.IsNullOrWhiteSpace(current)
                ? directories
                : current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(directories)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            Environment.SetEnvironmentVariable("DOORSTOP_DLL_SEARCH_DIRS", string.Join(Path.PathSeparator, merged));
        }

        private static string ResolveCompatibilityBridgePath()
        {
            var configured = Environment.GetEnvironmentVariable(CompatibilityBridgeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = DefaultCompatibilityBridgePath;
            }

            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(_gameRoot, configured));
        }

        private static string ResolveGameRoot()
        {
            var processPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var processDirectory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(processDirectory))
                {
                    return Path.GetFullPath(processDirectory);
                }
            }

            return Path.GetFullPath(Environment.CurrentDirectory);
        }

        private static void Trace(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    var logPath = Path.Combine(string.IsNullOrWhiteSpace(_gameRoot) ? Environment.CurrentDirectory : _gameRoot, LogFileName);
                    if (!string.Equals(logPath, _traceLogPath, StringComparison.OrdinalIgnoreCase))
                        OpenTraceLog(logPath);
                    _traceWriter?.WriteLine(DateTimeOffset.UtcNow.ToString("O") + " " + message);
                }
            }
            catch
            {
            }
        }

        // Each process starts a fresh log and keeps the previous run beside it, as LogOutput.log does.
        private static void OpenTraceLog(string logPath)
        {
            _traceWriter?.Dispose();
            _traceWriter = null;
            try
            {
                if (File.Exists(logPath))
                    File.Move(logPath, Path.ChangeExtension(logPath, ".previous.log"), true);
            }
            catch (IOException)
            {
                // Another game process may still be writing it; append instead.
            }
            catch (UnauthorizedAccessException)
            {
            }

            // Without delete sharing, another process cannot rotate this log while it is open.
            var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _traceWriter = new StreamWriter(stream) { AutoFlush = true };
            // Set only once open, so a failed open is retried by the next trace.
            _traceLogPath = logPath;
        }

        private static void WriteState(
            string enteredUtc,
            string mode,
            string status,
            bool delegatedToCompatibilityBridge,
            bool compatibilityBridgeReturned,
            string exceptionType,
            string detail,
            string compatibilityBridgeAssembly)
        {
            try
            {
                var statePath = Path.Combine(_runtimeDirectory, StateFileName);
                var json =
                    "{" + Environment.NewLine
                    + "  \"schema\": 1," + Environment.NewLine
                    + "  \"serviceKey\": \"openacai.loader.bootstrap\"," + Environment.NewLine
                    + "  \"enteredUtc\": \"" + StateJson.Escape(enteredUtc) + "\"," + Environment.NewLine
                    + "  \"updatedUtc\": \"" + DateTimeOffset.UtcNow.ToString("O") + "\"," + Environment.NewLine
                    + "  \"status\": \"" + StateJson.Escape(status) + "\"," + Environment.NewLine
                    + "  \"mode\": \"" + StateJson.Escape(mode) + "\"," + Environment.NewLine
                    + "  \"doorstopTargetOwnedByOpenAcai\": true," + Environment.NewLine
                    + "  \"platform\": " + JsonSerializer.Serialize(_platformState, StateJsonOptions) + "," + Environment.NewLine
                    + "  \"cecilPlatformAssemblies\": " + JsonSerializer.Serialize(_cecilPlatformAssemblies, StateJsonOptions) + "," + Environment.NewLine
                    + "  \"delegatedToCompatibilityBridge\": " + StateJson.JsonBool(delegatedToCompatibilityBridge) + "," + Environment.NewLine
                    + "  \"compatibilityBridgeReturned\": " + StateJson.JsonBool(compatibilityBridgeReturned) + "," + Environment.NewLine
                    + "  \"bootstrapAssembly\": \"" + StateJson.Escape(typeof(ChainloaderBoundary).Assembly.Location ?? "") + "\"," + Environment.NewLine
                    + "  \"compatibilityBridgeAssembly\": \"" + StateJson.Escape(compatibilityBridgeAssembly) + "\"," + Environment.NewLine
                    + "  \"runtimeDirectory\": \"" + StateJson.Escape(_runtimeDirectory) + "\"," + Environment.NewLine
                    + "  \"rootMigration\": " + JsonSerializer.Serialize(_rootMigration, StateJsonOptions) + "," + Environment.NewLine
                    + "  \"bepInExCoreDirectory\": \"" + StateJson.Escape(_bepInExCoreDirectory) + "\"," + Environment.NewLine
                    + "  \"exceptionType\": \"" + StateJson.Escape(exceptionType) + "\"," + Environment.NewLine
                    + "  \"detail\": \"" + StateJson.Escape(detail) + "\"" + Environment.NewLine
                    + "}" + Environment.NewLine;
                ChainloaderBoundary.WriteStateFile(statePath, json);
            }
            catch
            {
            }
        }
    }
}
