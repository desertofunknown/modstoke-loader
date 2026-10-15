// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned IL2CPP preloader phase runner. This replaces the single
// BepInEx.Unity.IL2CPP.Preloader.Run() API call with an OpenACAI-controlled
// phase sequence while the underlying compatibility components are ported.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal sealed class OpenACAIIL2CPPPreloaderRunner
    {
        private const string StateRelativePath = "OpenACAI\\Runtime\\openacai-il2cpp-preloader-state.json";
        private static readonly string[] PlannedPhaseNames =
        {
            "openacai.harmony-backend",
            "openacai.console-output-redirect",
            "openacai.unity-info",
            "openacai.console-manager",
            "openacai.preloader-log-listener",
            "openacai.stderr-redirect",
            "openacai.environment-banner",
            "openacai.runtime-log-lines",
            "openacai.game-assembly-dllimport-resolver",
            "openacai.il2cpp-interop-initialize",
            "openacai.assembly-patcher-orchestration",
            "compatibility.il2cpp-chainloader-initialize"
        };

        private readonly string _gameRoot;
        private readonly string _runtimeDirectory;
        private readonly string _compatibilityDirectory;
        private readonly string _bepInExCoreDirectory;
        private readonly string _bridgePath;
        private readonly string _enteredUtc;
        private readonly string _mode;
        private readonly Action<string> _trace;
        private readonly List<PhaseRecord> _phases;
        private string _status = "PLANNED";
        private string _detail = "OpenACAI IL2CPP preloader phase runner is planned.";
        private string _currentPhase = "";
        private string _exceptionType = "";
        private object _preloaderLog;
        private OpenACAIPreloaderBoundary _preloaderServices;
        private bool _bepInExIl2CppChainloaderInstanceUsed;
        private bool _openAcaiIl2CppChainloaderPortComplete;
        private OpenACAIHarmonyBackendInitializationState _harmonyBackendState;
        private OpenACAIConsoleOutputRedirectState _consoleOutputState;
        private OpenACAIConsoleManagerInitializationState _consoleManagerState;
        private OpenACAIStdErrRedirectState _stdErrState;
        private OpenACAIEnvironmentBannerState _environmentBannerState;
        private OpenACAIUnityInfoInitializationState _unityInfoState;

        internal OpenACAIIL2CPPPreloaderRunner(
            string gameRoot,
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            string bridgePath,
            string enteredUtc,
            string mode,
            Action<string> trace)
        {
            _gameRoot = string.IsNullOrWhiteSpace(gameRoot) ? Environment.CurrentDirectory : gameRoot;
            _runtimeDirectory = runtimeDirectory ?? Path.Combine(_gameRoot, "OpenACAI", "Runtime");
            _compatibilityDirectory = compatibilityDirectory ?? Path.Combine(_runtimeDirectory, "Compatibility");
            _bepInExCoreDirectory = bepInExCoreDirectory ?? Path.Combine(_gameRoot, "BepInEx", "core");
            _bridgePath = bridgePath ?? Path.Combine(_bepInExCoreDirectory, "BepInEx.Unity.IL2CPP.dll");
            _enteredUtc = enteredUtc ?? DateTimeOffset.UtcNow.ToString("O");
            _mode = mode ?? "openacai-native";
            _trace = trace ?? (_ => { });
            _phases = PlannedPhaseNames.Select(name => new PhaseRecord(name)).ToList();
        }

        internal void Run()
        {
            _status = "RUNNING";
            _detail = "OpenACAI IL2CPP preloader phase runner entered.";
            WriteState();

            try
            {
                RunPhase("openacai.harmony-backend", "Initializing OpenACAI-owned Harmony/MonoMod backend selection.", InitializeOpenAcaiHarmonyBackend);
                RunPhase("openacai.console-output-redirect", "Redirecting console output through the OpenACAI console logging writer.", ApplyOpenAcaiConsoleOutputRedirect);
                RunPhase("openacai.unity-info", "Initializing OpenACAI-owned Unity player metadata.", InitializeOpenAcaiUnityInfo);
                RunPhase("openacai.console-manager", "Initializing OpenACAI-owned console manager compatibility state.", InitializeOpenAcaiConsoleManager);
                RunPhase("openacai.preloader-log-listener", "Attaching OpenACAI-owned preloader logging.", AttachPreloaderLogListeners);
                RunPhase("openacai.stderr-redirect", "Applying OpenACAI-owned native stderr redirect to the compatibility error log.", ApplyOpenAcaiStdErrRedirect);
                RunPhase("openacai.environment-banner", "Publishing OpenACAI-owned runtime banner and platform facts.", WriteOpenAcaiEnvironmentBanner);
                RunPhase("openacai.runtime-log-lines", "Publishing OpenACAI runtime log lines.", WriteRuntimeLogLines);
                RunPhase("openacai.game-assembly-dllimport-resolver", "Installing GameAssembly DllImport resolver.", InstallGameAssemblyDllImportResolver);
                RunPhase("openacai.il2cpp-interop-initialize", "Starting IL2CPP interop using OpenACAI generation services.", InitializeIl2CppInterop);
                RunPhase("openacai.assembly-patcher-orchestration", "Loading and executing patchers with the OpenACAI engine and compatible plugin contracts.", RunAssemblyPatcher);
                RunPhase("compatibility.il2cpp-chainloader-initialize", "Creating and initializing the IL2CPP chainloader through the OpenACAI adapter.", InitializeIl2CppChainloader);

                _status = "READY";
                _currentPhase = "";
                _detail = "OpenACAI IL2CPP preloader phase runner completed successfully.";
                WriteState();
            }
            catch (Exception ex)
            {
                var unwrapped = Unwrap(ex);
                _status = "FAILED";
                _exceptionType = unwrapped.GetType().FullName ?? unwrapped.GetType().Name;
                _detail = FailureDetail(unwrapped);
                TraceFailure("OpenACAI IL2CPP preloader phase runner failed: ", unwrapped);
                AbortPreloaderLogging(unwrapped);
                WriteState();
                if (!ReferenceEquals(ex, unwrapped))
                    ExceptionDispatchInfo.Capture(unwrapped).Throw();
                throw;
            }
        }

        private void RunPhase(string name, string detail, Action action)
        {
            var phase = _phases.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            _currentPhase = name;
            _detail = detail;
            phase.Status = "RUNNING";
            phase.StartedUtc = DateTimeOffset.UtcNow.ToString("O");
            phase.Detail = detail;
            WriteState();
            _trace("OpenACAI IL2CPP preloader phase started: " + name);

            try
            {
                action();
                phase.Status = "READY";
                phase.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                phase.Detail = detail;
                _trace("OpenACAI IL2CPP preloader phase completed: " + name);
                WriteState();
            }
            catch (Exception ex)
            {
                var unwrapped = Unwrap(ex);
                phase.Status = "FAILED";
                phase.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                phase.ExceptionType = unwrapped.GetType().FullName ?? unwrapped.GetType().Name;
                phase.Detail = FailureDetail(unwrapped);
                WriteState();
                if (!ReferenceEquals(ex, unwrapped))
                    ExceptionDispatchInfo.Capture(unwrapped).Throw();
                throw;
            }
        }

        private void InitializeOpenAcaiHarmonyBackend()
        {
            _harmonyBackendState = OpenACAIHarmonyBackendInitializer.Initialize(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                _trace);
        }

        private void ApplyOpenAcaiConsoleOutputRedirect()
        {
            _consoleOutputState = OpenACAIConsoleOutputRedirector.Apply(LoadRequiredType, _trace);
        }

        private void InitializeOpenAcaiUnityInfo()
        {
            _unityInfoState = OpenACAIUnityInfoInitializer.Initialize(_gameRoot, LoadRequiredType, _trace);
        }

        private void InitializeOpenAcaiConsoleManager()
        {
            _consoleManagerState = OpenACAIConsoleManagerInitializer.Initialize(LoadRequiredType, _trace);
        }

        private void AttachPreloaderLogListeners()
        {
            _preloaderLog = GetPreloaderServices().AttachPreloaderLogging();
        }

        private void ApplyOpenAcaiStdErrRedirect()
        {
            _stdErrState = OpenACAIStdErrRedirector.Apply(LoadRequiredType, _trace);
        }

        private void WriteOpenAcaiEnvironmentBanner()
        {
            _environmentBannerState = OpenACAIEnvironmentBannerWriter.Write(_gameRoot, _runtimeDirectory, _preloaderLog, _trace);
        }

        private void WriteRuntimeLogLines()
        {
            var unityInfoType = LoadRequiredType("BepInEx.Unity.Common", "BepInEx.Unity.Common.UnityInfo");
            var pathsType = LoadRequiredType("BepInEx.Core", "BepInEx.Paths");
            var unityVersion = GetStaticProperty(unityInfoType, "Version");
            InvokeLogSource(_preloaderLog, "LogInfo", "Running under Unity " + unityVersion);
            InvokeLogSource(_preloaderLog, "LogInfo", "Runtime version: " + Environment.Version);
            InvokeLogSource(_preloaderLog, "LogInfo", "Runtime information: " + RuntimeInformation.FrameworkDescription);
            InvokeLogSource(_preloaderLog, "LogDebug", "Game executable path: " + GetStaticString(pathsType, "ExecutablePath"));
            InvokeLogSource(_preloaderLog, "LogDebug", "Interop assembly directory: " + GetPreloaderServices().GetInteropAssemblyPath());
            InvokeLogSource(_preloaderLog, "LogDebug", "BepInEx root path: " + GetStaticString(pathsType, "BepInExRootPath"));
        }

        private void InstallGameAssemblyDllImportResolver()
        {
            GetPreloaderServices().InstallGameAssemblyResolver();
        }

        private void InitializeIl2CppInterop()
        {
            GetPreloaderServices().InitializeInterop();
        }

        private void RunAssemblyPatcher()
        {
            var services = GetPreloaderServices();
            if (string.Equals(_mode, "compatibility-bridge", StringComparison.OrdinalIgnoreCase))
                services.RunCompatibilityAssemblyPatchers();
            else
                services.RunAssemblyPatchers();
        }

        private void InitializeIl2CppChainloader()
        {
            var adapter = new OpenACAIIL2CPPChainloaderAdapter(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                _bridgePath,
                _enteredUtc,
                _mode,
                LoadRequiredType,
                PreloadInteropAssemblies,
                GetPreloaderServices(),
                _trace);
            adapter.Initialize();
            _bepInExIl2CppChainloaderInstanceUsed = adapter.BepInExIl2CppChainloaderInstanceUsed;
            _openAcaiIl2CppChainloaderPortComplete = adapter.OpenAcaiIl2CppChainloaderPortComplete;
        }

        private void PreloadInteropAssemblies()
        {
            try
            {
                GetPreloaderServices().PreloadInteropAssemblies();
            }
            finally
            {
                WriteState();
            }
        }

        private OpenACAIPreloaderBoundary GetPreloaderServices()
        {
            return _preloaderServices ?? (_preloaderServices = new OpenACAIPreloaderBoundary(LoadRequiredType));
        }

        private void AbortPreloaderLogging(Exception ex)
        {
            try
            {
                _preloaderServices?.AbortPreloaderLogging(ex);
            }
            catch (Exception loggingError)
            {
                TraceFailure("OpenACAI preloader log cleanup failed: ", loggingError);
            }
        }

        private void TraceFailure(string message, Exception error)
        {
            try
            {
                _trace(message + error);
            }
            catch
            {
                // Exception formatting and trace callbacks are both outside our control.
            }
        }

        private static string FailureDetail(Exception error)
        {
            try
            {
                return error.Message ?? "";
            }
            catch
            {
                return "Startup failed; exception message unavailable.";
            }
        }

        private static void InvokeLogSource(object logSource, string methodName, object data)
        {
            if (logSource == null)
            {
                return;
            }

            var method = logSource.GetType().GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(object) },
                null);
            method?.Invoke(logSource, new[] { data });
        }

        private Assembly LoadRequiredAssembly(string assemblyName)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            foreach (var directory in SearchDirectories())
            {
                var candidate = Path.Combine(directory, assemblyName + ".dll");
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
                    var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
                    if (alreadyLoaded != null)
                    {
                        return alreadyLoaded;
                    }

                    throw;
                }
            }

            throw new FileNotFoundException("OpenACAI IL2CPP preloader could not resolve compatibility assembly: " + assemblyName + ".", assemblyName + ".dll");
        }

        private Type LoadRequiredType(string assemblyName, string typeName)
        {
            return LoadRequiredAssembly(assemblyName).GetType(typeName, true, false);
        }

        private IEnumerable<string> SearchDirectories()
        {
            yield return _runtimeDirectory;
            yield return _compatibilityDirectory;
            yield return _bepInExCoreDirectory;
            yield return Path.Combine(_gameRoot, "BepInEx", "patchers");
            yield return Path.Combine(_gameRoot, "BepInEx", "plugins");
            yield return Path.Combine(_gameRoot, "BepInEx", "interop");
            yield return Path.Combine(_gameRoot, "BepInEx", "plugins", "OpenACAILoader");
        }

        private static object GetStaticProperty(Type type, string propertyName)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property == null)
            {
                throw new MissingMemberException(type.FullName, propertyName);
            }

            return property.GetValue(null);
        }

        private static string GetStaticString(Type type, string propertyName)
        {
            return Convert.ToString(GetStaticProperty(type, propertyName)) ?? "";
        }

        private static Exception Unwrap(Exception ex)
        {
            return ex is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : ex;
        }

        private void WriteState()
        {
            try
            {
                var statePath = Path.Combine(_gameRoot, StateRelativePath);
                var stateDirectory = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrWhiteSpace(stateDirectory))
                {
                    Directory.CreateDirectory(stateDirectory);
                }

                var completedCount = _phases.Count(phase => string.Equals(phase.Status, "READY", StringComparison.OrdinalIgnoreCase));
                var failedCount = _phases.Count(phase => string.Equals(phase.Status, "FAILED", StringComparison.OrdinalIgnoreCase));
                var json = new StringBuilder();
                json.AppendLine("{");
                AppendProperty(json, "schema", "1", false);
                AppendProperty(json, "serviceKey", "openacai.loader.il2cppPreloader", true);
                AppendProperty(json, "enteredUtc", _enteredUtc, true);
                AppendProperty(json, "updatedUtc", DateTimeOffset.UtcNow.ToString("O"), true);
                AppendProperty(json, "status", _status, true);
                AppendProperty(json, "detail", _detail, true);
                AppendProperty(json, "exceptionType", _exceptionType, true);
                AppendProperty(json, "mode", _mode, true);
                AppendProperty(json, "runner", "OpenACAI IL2CPP preloader phase runner", true);
                AppendProperty(json, "currentPhase", _currentPhase, true);
                AppendProperty(json, "phaseCount", _phases.Count.ToString(), false);
                AppendProperty(json, "completedPhaseCount", completedCount.ToString(), false);
                AppendProperty(json, "failedPhaseCount", failedCount.ToString(), false);
                AppendProperty(json, "openAcaiPreloaderPhaseRunnerOwned", "true", false);
                AppendProperty(json, "bepInExPreloaderRunApiDependencyUsed", "false", false);
                AppendProperty(json, "bepInExIl2CppCompatibilityComponentsUsed", "true", false);
                AppendProperty(json, "bepInExIl2CppChainloaderInstanceUsed", JsonBool(_bepInExIl2CppChainloaderInstanceUsed), false);
                AppendProperty(json, "openAcaiPreloaderPortComplete", "false", false);
                AppendProperty(json, "interopServices", _preloaderServices?.CreateStateJson() ?? "null", false);
                AppendProperty(json, "openAcaiIl2CppChainloaderPortComplete", JsonBool(_openAcaiIl2CppChainloaderPortComplete), false);
                AppendProperty(json, "openAcaiHarmonyBackendOwned", JsonBool(_harmonyBackendState?.OpenAcaiOwned == true), false);
                AppendProperty(json, "bepInExHarmonyBackendFixUsed", JsonBool(_harmonyBackendState?.BepInExHarmonyBackendFixUsed == true), false);
                AppendProperty(json, "harmonyBackendSource", _harmonyBackendState?.BackendSource ?? "", true);
                AppendProperty(json, "harmonyBackend", _harmonyBackendState?.NormalizedBackend ?? "", true);
                AppendProperty(json, "harmonyBackendEnvironmentApplied", JsonBool(_harmonyBackendState?.EnvironmentApplied == true), false);
                AppendProperty(json, "monoModCoreAssemblyVersion", _harmonyBackendState?.MonoModCoreAssemblyVersion ?? "", true);
                AppendProperty(json, "monoModCore110RuntimeRequired", JsonBool(_harmonyBackendState?.MonoModCore110RuntimeRequired == true), false);
                AppendProperty(json, "monoModCore110RuntimeResolvable", JsonBool(_harmonyBackendState?.MonoModCore110RuntimeResolvable == true), false);
                AppendProperty(json, "monoModRuntimeBridgeStatus", _harmonyBackendState?.MonoModRuntimeBridgeStatus ?? "", true);
                AppendProperty(json, "monoModRuntimeBridgeDetail", _harmonyBackendState?.MonoModRuntimeBridgeDetail ?? "", true);
                AppendProperty(json, "openAcaiConsoleOutputRedirectOwned", JsonBool(_consoleOutputState?.OpenAcaiOwned == true), false);
                AppendProperty(json, "bepInExConsoleSetOutFixUsed", JsonBool(_consoleOutputState?.BepInExConsoleSetOutFixUsed == true), false);
                AppendProperty(json, "consoleLogSourceCreated", JsonBool(_consoleOutputState?.ConsoleLogSourceCreated == true), false);
                AppendProperty(json, "consoleLogSourceType", _consoleOutputState?.ConsoleLogSourceType ?? "", true);
                AppendProperty(json, "consoleWriterInstallAttempted", JsonBool(_consoleOutputState?.WriterInstallAttempted == true), false);
                AppendProperty(json, "consoleWriterInstalled", JsonBool(_consoleOutputState?.WriterInstalled == true), false);
                AppendProperty(json, "consoleWriterWrappedByRuntime", JsonBool(_consoleOutputState?.WriterWrappedByRuntime == true), false);
                AppendProperty(json, "consoleWriterReused", JsonBool(_consoleOutputState?.WriterReused == true), false);
                AppendProperty(json, "consoleOutTypeBefore", _consoleOutputState?.ConsoleOutTypeBefore ?? "", true);
                AppendProperty(json, "consoleOutTypeAfter", _consoleOutputState?.ConsoleOutTypeAfter ?? "", true);
                AppendProperty(json, "consoleSetOutPatchSkipped", JsonBool(_consoleOutputState?.SetOutPatchSkipped == true), false);
                AppendProperty(json, "consoleSetOutPatchSkipReason", _consoleOutputState?.SetOutPatchSkipReason ?? "", true);
                AppendProperty(json, "openAcaiConsoleManagerOwned", JsonBool(_consoleManagerState?.OpenAcaiOwned == true), false);
                AppendProperty(json, "bepInExConsoleManagerInitializeUsed", JsonBool(_consoleManagerState?.BepInExConsoleManagerInitializeUsed == true), false);
                AppendProperty(json, "consoleManagerAlreadyActiveRequested", JsonBool(_consoleManagerState?.AlreadyActiveRequested == true), false);
                AppendProperty(json, "consoleManagerUseManagedEncoder", JsonBool(_consoleManagerState?.UseManagedEncoder == true), false);
                AppendProperty(json, "consoleManagerConsoleEnabled", JsonBool(_consoleManagerState?.ConsoleEnabled == true), false);
                AppendProperty(json, "consoleManagerConsoleActive", JsonBool(_consoleManagerState?.ConsoleActive == true), false);
                AppendProperty(json, "consoleManagerDriverInitialized", JsonBool(_consoleManagerState?.DriverInitialized == true), false);
                AppendProperty(json, "consoleManagerDriverBound", JsonBool(_consoleManagerState?.DriverBound == true), false);
                AppendProperty(json, "consoleManagerDriverReused", JsonBool(_consoleManagerState?.DriverReused == true), false);
                AppendProperty(json, "consoleManagerDriverOpenAcaiOwned", JsonBool(_consoleManagerState?.DriverOpenAcaiOwned == true), false);
                AppendProperty(json, "consoleManagerStandardOutAvailable", JsonBool(_consoleManagerState?.StandardOutAvailable == true), false);
                AppendProperty(json, "consoleManagerConsoleOutAvailable", JsonBool(_consoleManagerState?.ConsoleOutAvailable == true), false);
                AppendProperty(json, "consoleManagerPlatform", _consoleManagerState?.Platform ?? "", true);
                AppendProperty(json, "consoleManagerCompatibilityType", _consoleManagerState?.ConsoleManagerType ?? "", true);
                AppendProperty(json, "consoleManagerExistingDriverType", _consoleManagerState?.ExistingDriverType ?? "", true);
                AppendProperty(json, "consoleManagerDriverType", _consoleManagerState?.DriverType ?? "", true);
                AppendProperty(json, "consoleManagerDetail", _consoleManagerState?.Detail ?? "", true);
                AppendProperty(json, "openAcaiStdErrRedirectOwned", JsonBool(_stdErrState?.OpenAcaiOwned == true), false);
                AppendProperty(json, "bepInExRedirectStdErrFixUsed", JsonBool(_stdErrState?.BepInExRedirectStdErrFixUsed == true), false);
                AppendProperty(json, "stdErrRedirectWindows", JsonBool(_stdErrState?.Windows == true), false);
                AppendProperty(json, "stdErrRedirectAttempted", JsonBool(_stdErrState?.RedirectAttempted == true), false);
                AppendProperty(json, "stdErrErrorFileOpened", JsonBool(_stdErrState?.ErrorFileOpened == true), false);
                AppendProperty(json, "stdErrRedirected", JsonBool(_stdErrState?.Redirected == true), false);
                AppendProperty(json, "stdErrWin32Error", (_stdErrState?.Win32Error ?? 0).ToString(), false);
                AppendProperty(json, "stdErrErrorLogPath", _stdErrState?.ErrorLogPath ?? "", true);
                AppendProperty(json, "stdErrRedirectDetail", _stdErrState?.Detail ?? "", true);
                AppendProperty(json, "openAcaiUnityInfoOwned", JsonBool(_unityInfoState?.OpenAcaiOwned == true), false);
                AppendProperty(json, "bepInExUnityInfoInitializeUsed", JsonBool(_unityInfoState?.BepInExUnityInfoInitializeUsed == true), false);
                AppendProperty(json, "unityInfoAlreadyInitialized", JsonBool(_unityInfoState?.UnityInfoAlreadyInitialized == true), false);
                AppendProperty(json, "unityInfoPropertiesSet", JsonBool(_unityInfoState?.UnityInfoPropertiesSet == true), false);
                AppendProperty(json, "unityInfoInitializedFieldSet", JsonBool(_unityInfoState?.UnityInfoInitializedFieldSet == true), false);
                AppendProperty(json, "unityInfoExecutableExists", JsonBool(_unityInfoState?.ExecutableExists == true), false);
                AppendProperty(json, "unityInfoGameDataPathExists", JsonBool(_unityInfoState?.GameDataPathExists == true), false);
                AppendProperty(json, "unityInfoExecutablePath", _unityInfoState?.ExecutablePath ?? "", true);
                AppendProperty(json, "unityInfoGameRootPath", _unityInfoState?.GameRootPath ?? "", true);
                AppendProperty(json, "unityInfoGameDataPath", _unityInfoState?.GameDataPath ?? "", true);
                AppendProperty(json, "unityInfoManagedPath", _unityInfoState?.ManagedPath ?? "", true);
                AppendProperty(json, "unityInfoType", _unityInfoState?.UnityInfoType ?? "", true);
                AppendProperty(json, "unityInfoVersion", _unityInfoState?.UnityVersion ?? "", true);
                AppendProperty(json, "unityInfoVersionRaw", _unityInfoState?.UnityVersionRaw ?? "", true);
                AppendProperty(json, "unityInfoVersionSource", _unityInfoState?.UnityVersionSource ?? "", true);
                AppendProperty(json, "unityInfoDetail", _unityInfoState?.Detail ?? "", true);
                AppendProperty(json, "openAcaiEnvironmentBannerOwned", JsonBool(_environmentBannerState?.OpenAcaiOwned == true), false);
                AppendProperty(json, "bepInExChainloaderLogHelperUsed", JsonBool(_environmentBannerState?.BepInExChainloaderLogHelperUsed == true), false);
                AppendProperty(json, "environmentBannerWritten", JsonBool(_environmentBannerState?.BannerWritten == true), false);
                AppendProperty(json, "environmentBannerConsoleTitleSet", JsonBool(_environmentBannerState?.ConsoleTitleSet == true), false);
                AppendProperty(json, "environmentBannerLoaderName", _environmentBannerState?.LoaderName ?? "", true);
                AppendProperty(json, "environmentBannerLoaderVersion", _environmentBannerState?.LoaderVersion ?? "", true);
                AppendProperty(json, "environmentBannerCompatibilityRuntimeVersion", _environmentBannerState?.BepInExCompatibilityRuntimeVersion ?? "", true);
                AppendProperty(json, "environmentBannerTitle", _environmentBannerState?.ConsoleTitle ?? "", true);
                AppendProperty(json, "environmentBannerPlatform", _environmentBannerState?.Platform ?? "", true);
                AppendProperty(json, "environmentBannerProcessBitness", _environmentBannerState?.ProcessBitness ?? "", true);
                AppendProperty(json, "environmentBannerBuiltFromCommit", _environmentBannerState?.BuiltFromCommit ?? "", true);
                AppendProperty(json, "environmentBannerExecutablePath", _environmentBannerState?.ExecutablePath ?? "", true);
                AppendProperty(json, "environmentBannerExecutableLastWriteTime", _environmentBannerState?.ExecutableLastWriteTime ?? "", true);
                AppendProperty(json, "environmentBannerDetail", _environmentBannerState?.Detail ?? "", true);
                AppendProperty(json, "compatibilityBridgeAssembly", _bridgePath, true);
                AppendProperty(json, "runtimeDirectory", _runtimeDirectory, true);
                AppendProperty(json, "compatibilityDirectory", _compatibilityDirectory, true);
                AppendProperty(json, "bepInExCoreDirectory", _bepInExCoreDirectory, true);
                json.AppendLine("  \"phases\": [");
                for (var i = 0; i < _phases.Count; i++)
                {
                    var phase = _phases[i];
                    json.AppendLine("    {");
                    AppendProperty(json, "name", phase.Name, true, 6);
                    AppendProperty(json, "status", phase.Status, true, 6);
                    AppendProperty(json, "startedUtc", phase.StartedUtc, true, 6);
                    AppendProperty(json, "completedUtc", phase.CompletedUtc, true, 6);
                    AppendProperty(json, "exceptionType", phase.ExceptionType, true, 6);
                    AppendProperty(json, "detail", phase.Detail, true, 6, false);
                    json.Append("    }");
                    if (i + 1 < _phases.Count)
                    {
                        json.Append(",");
                    }

                    json.AppendLine();
                }

                json.AppendLine("  ]");
                json.AppendLine("}");
                File.WriteAllText(statePath, json.ToString());
            }
            catch
            {
                // Diagnostic state must not alter loader startup.
            }
        }

        private static string JsonBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static void AppendProperty(StringBuilder builder, string name, string value, bool quote, int indent = 2, bool comma = true)
        {
            builder.Append(' ', indent);
            builder.Append('"');
            builder.Append(Escape(name));
            builder.Append("\": ");
            if (quote)
            {
                builder.Append('"');
                builder.Append(Escape(value));
                builder.Append('"');
            }
            else
            {
                builder.Append(value);
            }

            if (comma)
            {
                builder.Append(',');
            }

            builder.AppendLine();
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

        private sealed class PhaseRecord
        {
            internal PhaseRecord(string name)
            {
                Name = name;
            }

            internal string Name { get; }
            internal string Status { get; set; } = "PENDING";
            internal string StartedUtc { get; set; } = "";
            internal string CompletedUtc { get; set; } = "";
            internal string ExceptionType { get; set; } = "";
            internal string Detail { get; set; } = "";
        }
    }
}
