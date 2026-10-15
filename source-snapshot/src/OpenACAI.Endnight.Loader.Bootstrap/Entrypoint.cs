// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Doorstop target. Doorstop enters here first; the current
// compatibility mode can still delegate to the legacy BepInEx IL2CPP bridge
// while the OpenACAI preloader and chainloader are ported behind this boundary.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using OpenACAI.Endnight.Loader.Bootstrap;

// ReSharper disable once CheckNamespace
namespace Doorstop
{
    public static class Entrypoint
    {
        private const string BootstrapModeEnvironmentVariable = "OPENACAI_BOOTSTRAP_MODE";
        private const string CompatibilityBridgeEnvironmentVariable = "OPENACAI_BOOTSTRAP_COMPATIBILITY_BRIDGE";
        private const string CompatibilityBootstrapMode = "compatibility-bridge";
        private const string DefaultBootstrapMode = "openacai-native";
        private const string NativeBootstrapMode = "openacai-native";
        private const string DefaultCompatibilityBridgePath = "BepInEx\\core\\BepInEx.Unity.IL2CPP.dll";
        private const string StateRelativePath = "OpenACAI\\Runtime\\openacai-bootstrap-state.json";
        private const string LogFileName = "openacai-bootstrap.log";
        private static readonly object SyncRoot = new object();
        private static string _gameRoot;
        private static string _runtimeDirectory;
        private static string _compatibilityDirectory;
        private static string _bepInExCoreDirectory;
        private static OpenACAIBootstrapPlatformState _platformState;
        private static int _compatibilityStartupAttempted;
        private static OpenACAICecilPlatformAssemblyState[] _cecilPlatformAssemblies = Array.Empty<OpenACAICecilPlatformAssemblyState>();
        private static readonly JsonSerializerOptions StateJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static void Start()
        {
            var enteredUtc = DateTimeOffset.UtcNow.ToString("O");
            InitializeContext();

            var mode = Environment.GetEnvironmentVariable(BootstrapModeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = DefaultBootstrapMode;
            }

            Trace("OpenACAI bootstrap entered; mode " + mode + ".");
            WriteState(
                enteredUtc,
                mode,
                "ENTERED",
                false,
                false,
                "",
                "Doorstop reached the OpenACAI bootstrap target.");
            OpenACAIChainloaderBoundary.WriteState(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                enteredUtc,
                mode,
                "ENTERED",
                ResolveCompatibilityBridgePath(),
                false,
                false,
                "",
                "Doorstop reached the OpenACAI-owned chainloader boundary.");

            try
            {
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
                        "OpenACAI bootstrap ran the OpenACAI-owned preloader path successfully.");
                    // The adapter owns the asynchronous chainloader outcome after the preloader handoff.
                    Trace("OpenACAI native preloader path returned.");
                    return;
                }

                if (!string.Equals(mode, CompatibilityBootstrapMode, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unsupported OpenACAI bootstrap mode: " + mode + ".");
                }

                DelegateToCompatibilityBridge(enteredUtc, mode);
                WriteState(
                    enteredUtc,
                    mode,
                    "READY",
                    true,
                    true,
                    "",
                    "OpenACAI bootstrap delegated to the compatibility bridge successfully.");
                OpenACAIChainloaderBoundary.WriteState(
                    _gameRoot,
                    _runtimeDirectory,
                    _compatibilityDirectory,
                    _bepInExCoreDirectory,
                    enteredUtc,
                    mode,
                    "READY",
                    ResolveCompatibilityBridgePath(),
                    true,
                    true,
                    "",
                    "OpenACAI owned the chainloader handoff and the transitional compatibility bridge returned successfully.");
                Trace("OpenACAI bootstrap returned from compatibility bridge.");
            }
            catch (Exception ex)
            {
                try
                {
                    WriteState(
                        enteredUtc,
                        mode,
                        "FAILED",
                        false,
                        false,
                        ex.GetType().FullName ?? ex.GetType().Name,
                        ex.Message ?? "");
                    OpenACAIChainloaderBoundary.WriteState(
                        _gameRoot,
                        _runtimeDirectory,
                        _compatibilityDirectory,
                        _bepInExCoreDirectory,
                        enteredUtc,
                        mode,
                        "FAILED",
                        ResolveCompatibilityBridgePath(),
                        false,
                        false,
                        ex.GetType().FullName ?? ex.GetType().Name,
                        ex.Message ?? "");
                    Trace("OpenACAI bootstrap failed: " + ex);
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
            _cecilPlatformAssemblies = Array.Empty<OpenACAICecilPlatformAssemblyState>();
            _gameRoot = ResolveGameRoot();
            _runtimeDirectory = Path.Combine(_gameRoot, "OpenACAI", "Runtime");
            _compatibilityDirectory = Path.Combine(_runtimeDirectory, "Compatibility");
            _bepInExCoreDirectory = Path.Combine(_gameRoot, "BepInEx", "core");
        }

        // The facade enters here without returning through Start's mode-selection branch.
        public static void RunCompatibility()
        {
            if (Interlocked.CompareExchange(ref _compatibilityStartupAttempted, 1, 0) != 0)
                throw new InvalidOperationException("Compatibility startup has already been attempted.");
            InitializeContext();
            InstallAssemblyResolvers();
            var envVars = LoadRequiredType("BepInEx.Preloader.Core", "BepInEx.Preloader.Core.EnvVars");
            var loadVars = envVars.GetMethod("LoadVars", BindingFlags.Public | BindingFlags.Static,
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

            OpenACAIChainloaderBoundary.WriteState(
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
                "OpenACAI resolved runtime paths and is preparing its owned IL2CPP preloader path.");

            var processPath = ResolveProcessPath();
            var managedPath = ResolveManagedPath();
            var dllSearchDirectories = ResolveDllSearchDirectories();

            Trace("OpenACAI native path setting BepInEx compatibility paths for API compatibility.");
            _platformState = OpenACAIBootstrapEnvironment.CapturePlatform();
            OpenACAICompatibilityPathInitializer.Initialize(
                LoadRequiredType("BepInEx.Core", "BepInEx.Paths"),
                processPath,
                Path.Combine(_gameRoot, "BepInEx"),
                managedPath,
                dllSearchDirectories);
            _cecilPlatformAssemblies = new[]
            {
                OpenACAIBootstrapEnvironment.RegisterCecilDirectory(managedPath),
                OpenACAIBootstrapEnvironment.RegisterCecilDirectory(Path.GetDirectoryName(managedPath))
            };
            foreach (var registration in _cecilPlatformAssemblies)
            {
                if (registration.Status == "failed")
                    Trace("OpenACAI Cecil platform assembly warning for " + registration.Directory + ": " + registration.Error);
            }

            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAppDomainAssembly;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAppDomainAssembly;

            OpenACAIChainloaderBoundary.WriteState(
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
                "OpenACAI set compatibility paths and is invoking the IL2CPP preloader phase directly.");

            Trace("OpenACAI native path invoking OpenACAI IL2CPP preloader phase runner.");
            var preloaderRunner = new OpenACAIIL2CPPPreloaderRunner(
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
                : current.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
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
                OpenACAIChainloaderBoundary.WriteState(
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
                    "OpenACAI bootstrap compatibility bridge was not found.");
                throw new FileNotFoundException("OpenACAI bootstrap compatibility bridge was not found.", bridgePath);
            }

            var bridgeDirectory = Path.GetDirectoryName(bridgePath) ?? _bepInExCoreDirectory;
            _bepInExCoreDirectory = bridgeDirectory;
            OpenACAIChainloaderBoundary.WriteState(
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
                "OpenACAI resolved the chainloader bridge and prepared the compatibility plan.");
            InstallAssemblyResolvers();
            AddSearchDirectoriesToDoorstopEnvironment(bridgeDirectory);

            var previousInvokePath = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
            Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", bridgePath);

            WriteState(
                enteredUtc,
                mode,
                "DELEGATING",
                true,
                false,
                "",
                "OpenACAI bootstrap is delegating to the transitional BepInEx IL2CPP bridge after rewriting DOORSTOP_INVOKE_DLL_PATH.");
            OpenACAIChainloaderBoundary.WriteState(
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
                "OpenACAI is invoking the transitional BepInEx IL2CPP bridge from the owned chainloader boundary.");

            try
            {
                Trace("Loading compatibility bridge " + bridgePath + ".");
                var bridgeAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(bridgePath);
                var entrypointType = bridgeAssembly.GetType("Doorstop.Entrypoint", true, false);
                var startMethod = entrypointType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
                if (startMethod == null)
                {
                    throw new MissingMethodException("Doorstop.Entrypoint", "Start");
                }

                startMethod.Invoke(null, null);
                OpenACAIChainloaderBoundary.WriteState(
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
                    "The transitional BepInEx IL2CPP bridge returned to the OpenACAI chainloader boundary.");
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(previousInvokePath))
                {
                    Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", previousInvokePath);
                }
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

            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            foreach (var directory in SearchDirectories())
            {
                var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                }
                catch (FileLoadException)
                {
                    return AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
                }
            }

            return null;
        }

        private static Assembly LoadRequiredAssembly(string assemblyName)
        {
            var assembly = ResolveAssembly(new AssemblyName(assemblyName));
            if (assembly == null)
            {
                throw new FileNotFoundException("OpenACAI bootstrap could not resolve compatibility assembly: " + assemblyName + ".", assemblyName + ".dll");
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
            yield return Path.Combine(_gameRoot, "BepInEx", "interop");
            yield return Path.Combine(_gameRoot, "BepInEx", "plugins", "OpenACAILoader");
        }

        private static void AddSearchDirectoriesToDoorstopEnvironment(string bridgeDirectory)
        {
            var directories = SearchDirectories()
                .Concat(new[] { bridgeDirectory })
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.GetFullPath(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToArray();
            var current = Environment.GetEnvironmentVariable("DOORSTOP_DLL_SEARCH_DIRS");
            var merged = string.IsNullOrWhiteSpace(current)
                ? directories
                : current.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(directories)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            Environment.SetEnvironmentVariable("DOORSTOP_DLL_SEARCH_DIRS", string.Join(Path.PathSeparator.ToString(), merged));
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
                    File.AppendAllText(logPath, DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        private static void WriteState(
            string enteredUtc,
            string mode,
            string status,
            bool delegatedToCompatibilityBridge,
            bool compatibilityBridgeReturned,
            string exceptionType,
            string detail)
        {
            try
            {
                var statePath = Path.Combine(_gameRoot, StateRelativePath);
                var stateDirectory = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrWhiteSpace(stateDirectory))
                {
                    Directory.CreateDirectory(stateDirectory);
                }

                var json =
                    "{" + Environment.NewLine
                    + "  \"schema\": 1," + Environment.NewLine
                    + "  \"serviceKey\": \"openacai.loader.bootstrap\"," + Environment.NewLine
                    + "  \"enteredUtc\": \"" + Escape(enteredUtc) + "\"," + Environment.NewLine
                    + "  \"updatedUtc\": \"" + DateTimeOffset.UtcNow.ToString("O") + "\"," + Environment.NewLine
                    + "  \"status\": \"" + Escape(status) + "\"," + Environment.NewLine
                    + "  \"mode\": \"" + Escape(mode) + "\"," + Environment.NewLine
                    + "  \"doorstopTargetOwnedByOpenAcai\": true," + Environment.NewLine
                    + "  \"platform\": " + JsonSerializer.Serialize(_platformState, StateJsonOptions) + "," + Environment.NewLine
                    + "  \"cecilPlatformAssemblies\": " + JsonSerializer.Serialize(_cecilPlatformAssemblies, StateJsonOptions) + "," + Environment.NewLine
                    + "  \"delegatedToCompatibilityBridge\": " + JsonBool(delegatedToCompatibilityBridge) + "," + Environment.NewLine
                    + "  \"compatibilityBridgeReturned\": " + JsonBool(compatibilityBridgeReturned) + "," + Environment.NewLine
                    + "  \"bootstrapAssembly\": \"" + Escape(typeof(OpenACAIChainloaderBoundary).Assembly.Location ?? "") + "\"," + Environment.NewLine
                    + "  \"compatibilityBridgeAssembly\": \"" + Escape(ResolveCompatibilityBridgePath()) + "\"," + Environment.NewLine
                    + "  \"runtimeDirectory\": \"" + Escape(_runtimeDirectory) + "\"," + Environment.NewLine
                    + "  \"bepInExCoreDirectory\": \"" + Escape(_bepInExCoreDirectory) + "\"," + Environment.NewLine
                    + "  \"exceptionType\": \"" + Escape(exceptionType) + "\"," + Environment.NewLine
                    + "  \"detail\": \"" + Escape(detail) + "\"" + Environment.NewLine
                    + "}" + Environment.NewLine;
                File.WriteAllText(statePath, json);
            }
            catch
            {
            }
        }

        private static string JsonBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
