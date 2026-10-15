// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned IL2CPP chainloader adapter. This keeps the BepInEx
// IL2CPPChainloader instance as a compatibility dependency for now, but moves
// the creation/binding/initialization boundary into OpenACAI-owned code.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal sealed class OpenACAIIL2CPPChainloaderAdapter
    {
        internal const string StateRelativePath = "OpenACAI\\Runtime\\openacai-il2cpp-chainloader-state.json";

        private static readonly string[] PlannedPhaseNames =
        {
            "openacai.create-chainloader-adapter",
            "openacai.plan-chainloader-port",
            "openacai.plan-direct-core-invocation",
            "openacai.arm-direct-core-invocation-trigger",
            "openacai.execute-direct-core-runtime",
            "compatibility.create-bepinex-il2cpp-chainloader-instance",
            "compatibility.bind-preloader-chainloader-property",
            "compatibility.initialize-bepinex-il2cpp-chainloader-instance"
        };

        private readonly string _gameRoot;
        private readonly string _runtimeDirectory;
        private readonly string _compatibilityDirectory;
        private readonly string _bepInExCoreDirectory;
        private readonly string _bridgePath;
        private readonly string _enteredUtc;
        private readonly string _mode;
        private readonly Func<string, string, Type> _loadRequiredType;
        private readonly Action _preloadInteropAssemblies;
        private readonly OpenACAIPreloaderBoundary _preloaderServices;
        private readonly Action<string> _trace;
        private readonly List<PhaseRecord> _phases;
        private readonly object _directCoreSync = new object();
        private string _status = "PLANNED";
        private string _detail = "OpenACAI IL2CPP chainloader adapter is planned.";
        private string _currentPhase = "";
        private string _exceptionType = "";
        private string _compatibilityChainloaderTypeName = "";
        private string _chainloaderPortPlanDetail = "OpenACAI IL2CPP chainloader port readiness has not been checked yet.";
        private string _directCoreInvocationPlanDetail = "OpenACAI direct core invocation readiness has not been checked yet.";
        private string _directCoreInvocationFallbackReason = "";
        private string _directCoreInvocationExceptionType = "";
        private string _directCoreTriggerMethod = "Internal_ActiveSceneChanged";
        private string _openAcaiCoreAssemblyPath = "";
        private string _runtimeInvokePointerText = "";
        private bool _adapterCreated;
        private bool _chainloaderPortPlanCreated;
        private bool _chainloaderPortPlanReady;
        private bool _directCoreInvocationPlanCreated;
        private bool _directCoreInvocationPlanReady;
        private bool _directCoreInvocationArmed;
        private bool _directCoreInvocationTriggerOwned;
        private bool _directCoreInvocationTriggered;
        private bool _directCoreInvocationExecuting;
        private bool _directCoreInvocationExecuted;
        private bool _directCoreInvocationFailed;
        private bool _directCoreEntrypointSignatureResolvable;
        private bool _directCoreLogSourceFactoryResolvable;
        private bool _directCoreLogSourceCreated;
        private bool _interopPreloadResolvable;
        private bool _interopPreloadInvoked;
        private bool _interopPreloadReturned;
        private bool _directCoreLoadInvoked;
        private bool _directCoreLoadReturned;
        private bool _runtimeInvokeDetourDisposed;
        private bool _runtimeInvokeDetourCleanupFailed;
        private bool _runtimeInvokeActivationAttempted;
        private bool _runtimeInvokeDetourRetained;
        private bool _runtimeInvokeTriggerRetired;
        private bool _compatibilityFallbackBlocked;
        private int _directCoreExecutionClaimed;
        private int _runtimeInvokeActivationFailed;
        private bool _gameAssemblyResolvable;
        private bool _runtimeInvokeExportResolvable;
        private bool _nativeDetourFactoryResolvable;
        private bool _basePluginResolvable;
        private bool _openAcaiCoreAssemblyResolvable;
        private bool _openAcaiCoreEntrypointResolvable;
        private bool _manualLogSourceResolvable;
        private bool _openAcaiIl2CppChainloaderPortComplete;
        private bool _compatibilityExecutionFallbackRequired = true;
        private bool _compatibilityExecutionFallbackUsed;
        private bool _bepInExIl2CppChainloaderInstanceUsed;
        private bool _compatibilityInstanceCreated;
        private bool _preloaderChainloaderPropertyBound;
        private bool _initializeInvoked;
        private bool _initializeReturned;
        private IntPtr _gameAssemblyHandle;
        private IntPtr _runtimeInvokePointer;
        private RuntimeInvokeDetourDelegate _directCoreInvokeDelegate;
        private RuntimeInvokeDetourDelegate _directCoreOriginalInvoke;
        private IDisposable _directCoreRuntimeInvokeDetour;
        private MethodInfo _il2CppMethodGetNameMethod;
        private object _compatibilityChainloader;
        private Type _compatibilityChainloaderType;

        internal OpenACAIIL2CPPChainloaderAdapter(
            string gameRoot,
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            string bridgePath,
            string enteredUtc,
            string mode,
            Func<string, string, Type> loadRequiredType,
            Action preloadInteropAssemblies,
            OpenACAIPreloaderBoundary preloaderServices,
            Action<string> trace)
        {
            _gameRoot = string.IsNullOrWhiteSpace(gameRoot) ? Environment.CurrentDirectory : gameRoot;
            _runtimeDirectory = runtimeDirectory ?? Path.Combine(_gameRoot, "OpenACAI", "Runtime");
            _compatibilityDirectory = compatibilityDirectory ?? Path.Combine(_runtimeDirectory, "Compatibility");
            _bepInExCoreDirectory = bepInExCoreDirectory ?? Path.Combine(_gameRoot, "BepInEx", "core");
            _bridgePath = bridgePath ?? Path.Combine(_bepInExCoreDirectory, "BepInEx.Unity.IL2CPP.dll");
            _enteredUtc = enteredUtc ?? DateTimeOffset.UtcNow.ToString("O");
            _mode = mode ?? "openacai-native";
            _loadRequiredType = loadRequiredType ?? throw new ArgumentNullException(nameof(loadRequiredType));
            _preloadInteropAssemblies = preloadInteropAssemblies ?? throw new ArgumentNullException(nameof(preloadInteropAssemblies));
            _preloaderServices = preloaderServices ?? throw new ArgumentNullException(nameof(preloaderServices));
            _trace = trace ?? (_ => { });
            _phases = PlannedPhaseNames.Select(name => new PhaseRecord(name)).ToList();
        }

        internal void Initialize()
        {
            // A newly activated hook can run on another thread before Apply returns.
            // Matching callbacks wait until initialization has published its complete state.
            lock (_directCoreSync)
                InitializeCore();
        }

        private void InitializeCore()
        {
            _status = "RUNNING";
            _detail = "OpenACAI IL2CPP chainloader adapter entered.";
            WriteState();

            try
            {
                RunPhase("openacai.create-chainloader-adapter", "Creating the OpenACAI-owned IL2CPP chainloader adapter.", CreateAdapterBoundary);
                if (string.Equals(_mode, "compatibility-bridge", StringComparison.OrdinalIgnoreCase))
                {
                    const string detail = "Explicit compatibility startup uses the legacy plugin registry and execution contract.";
                    MarkPhaseReady("openacai.plan-chainloader-port", detail);
                    MarkPhaseReady("openacai.plan-direct-core-invocation", detail);
                    MarkPhaseReady("openacai.arm-direct-core-invocation-trigger", detail);
                }
                else
                {
                    RunPhase("openacai.plan-chainloader-port", "Checking OpenACAI-owned IL2CPP chainloader replacement readiness.", PlanOpenAcaiChainloaderPort);
                    RunPhase("openacai.plan-direct-core-invocation", "Checking OpenACAI direct core invocation readiness.", PlanOpenAcaiDirectCoreInvocation);
                    RunPhase("openacai.arm-direct-core-invocation-trigger", "Arming the OpenACAI-owned IL2CPP direct core invocation trigger.", ArmOpenAcaiDirectCoreInvocationTrigger);
                }

                if (_directCoreInvocationArmed)
                {
                    _preloaderServices.StartRuntimeLogging();
                    MarkPhasePending(
                        "openacai.execute-direct-core-runtime",
                        "OpenACAI direct core trigger is armed and waiting for Internal_ActiveSceneChanged.");
                    SkipCompatibilityExecutionPhases("OpenACAI direct core invocation trigger is armed; compatibility chainloader setup was not used.");
                    _status = "ARMED";
                    _currentPhase = "";
                    _detail = "OpenACAI direct core invocation trigger is armed and waiting for Unity scene activation.";
                    WriteState();
                    WriteChainloaderState("ARMED", _detail);
                    return;
                }

                MarkPhaseReady(
                    "openacai.execute-direct-core-runtime",
                    "OpenACAI direct core trigger was not armed; compatibility chainloader execution will run.");
                RunCompatibilityChainloaderPhases();

                _status = "READY";
                _currentPhase = "";
                _detail = "OpenACAI IL2CPP chainloader adapter completed successfully.";
                WriteState();
                WriteChainloaderState("READY", "The compatibility IL2CPP chainloader is initialized. Individual plugin startup completes on Unity scene activation.");
            }
            catch (Exception ex)
            {
                var unwrapped = Unwrap(ex);
                _status = "FAILED";
                _exceptionType = unwrapped.GetType().FullName ?? unwrapped.GetType().Name;
                _detail = unwrapped.Message ?? "";
                WriteState();
                _trace("OpenACAI IL2CPP chainloader adapter failed: " + unwrapped);
                throw unwrapped;
            }
        }

        internal bool BepInExIl2CppChainloaderInstanceUsed => _bepInExIl2CppChainloaderInstanceUsed;

        internal bool OpenAcaiIl2CppChainloaderPortComplete => _openAcaiIl2CppChainloaderPortComplete;

        private void RunPhase(string name, string detail, Action action)
        {
            var phase = _phases.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            _currentPhase = name;
            _detail = detail;
            phase.Status = "RUNNING";
            phase.StartedUtc = DateTimeOffset.UtcNow.ToString("O");
            phase.Detail = detail;
            WriteState();
            _trace("OpenACAI IL2CPP chainloader adapter phase started: " + name);

            try
            {
                action();
                phase.Status = "READY";
                phase.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                phase.Detail = string.IsNullOrWhiteSpace(_detail) ? detail : _detail;
                _trace("OpenACAI IL2CPP chainloader adapter phase completed: " + name);
                WriteState();
            }
            catch (Exception ex)
            {
                var unwrapped = Unwrap(ex);
                phase.Status = "FAILED";
                phase.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                phase.ExceptionType = unwrapped.GetType().FullName ?? unwrapped.GetType().Name;
                phase.Detail = unwrapped.Message ?? "";
                WriteState();
                throw unwrapped;
            }
        }

        private void CreateAdapterBoundary()
        {
            _adapterCreated = true;
        }

        private void PlanOpenAcaiChainloaderPort()
        {
            _chainloaderPortPlanCreated = true;
            _openAcaiCoreAssemblyPath = Path.Combine(_runtimeDirectory, "OpenACAI.Endnight.Loader.Core.dll");
            _gameAssemblyResolvable = TryResolveRuntimeInvokeExport();
            _nativeDetourFactoryResolvable = _preloaderServices.PrepareRuntimeInvokeHook != null;
            _basePluginResolvable = _loadRequiredType("BepInEx.Unity.IL2CPP", "BepInEx.Unity.IL2CPP.BasePlugin") != null;
            _manualLogSourceResolvable = _loadRequiredType("BepInEx.Core", "BepInEx.Logging.ManualLogSource") != null;
            _openAcaiCoreAssemblyResolvable = File.Exists(_openAcaiCoreAssemblyPath)
                && AssemblyName.GetAssemblyName(_openAcaiCoreAssemblyPath) != null;
            _openAcaiCoreEntrypointResolvable = _openAcaiCoreAssemblyResolvable
                && AssemblyHasMethod(
                    _openAcaiCoreAssemblyPath,
                    "OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime",
                    "Load");
            _chainloaderPortPlanReady = _gameAssemblyResolvable
                && _runtimeInvokeExportResolvable
                && _nativeDetourFactoryResolvable
                && _basePluginResolvable
                && _manualLogSourceResolvable
                && _openAcaiCoreAssemblyResolvable
                && _openAcaiCoreEntrypointResolvable;
            _compatibilityExecutionFallbackRequired = true;
            _chainloaderPortPlanDetail = _chainloaderPortPlanReady
                ? "OpenACAI resolved its native hook bridge, runtime invoke export, compatibility types, and direct core entrypoint. The next phase arms the owned trigger."
                : "OpenACAI chainloader port readiness is incomplete: " + BuildMissingPortPlanDetail();
        }

        private void PlanOpenAcaiDirectCoreInvocation()
        {
            _directCoreInvocationPlanCreated = true;
            _directCoreInvocationArmed = false;
            _directCoreInvocationTriggerOwned = false;

            var loggerType = _loadRequiredType("BepInEx.Core", "BepInEx.Logging.Logger");
            var manualLogSourceType = _loadRequiredType("BepInEx.Core", "BepInEx.Logging.ManualLogSource");
            _directCoreLogSourceFactoryResolvable = TryResolveCreateLogSource(loggerType, manualLogSourceType);
            _interopPreloadResolvable = true;
            _directCoreEntrypointSignatureResolvable = _openAcaiCoreEntrypointResolvable && _manualLogSourceResolvable;
            _directCoreInvocationPlanReady = _chainloaderPortPlanReady
                && _directCoreEntrypointSignatureResolvable
                && _directCoreLogSourceFactoryResolvable
                && _interopPreloadResolvable;

            _directCoreInvocationPlanDetail = _directCoreInvocationPlanReady
                ? "OpenACAI direct core invocation can be armed: the trigger name, interop preload method, log source factory, and OpenACAIEndnightRuntime.Load entrypoint are resolvable."
                : "OpenACAI direct core invocation readiness is incomplete: " + BuildMissingDirectCoreInvocationDetail();
        }

        private void ArmOpenAcaiDirectCoreInvocationTrigger()
        {
            if (!_directCoreInvocationPlanReady)
            {
                MarkDirectCoreFallback("OpenACAI direct core invocation plan is not ready.");
                return;
            }

            try
            {
                InvokeHookStatus("MarkInitializing");

                if (!TryResolveRuntimeInvokeExportForDirectTrigger())
                {
                    MarkDirectCoreFallback("OpenACAI direct core invocation could not resolve il2cpp_runtime_invoke.");
                    return;
                }

                InvokeHookStatus("MarkGameAssemblyLoaded");
                InvokeHookStatus("MarkRuntimeInvokeExportFound", new[] { typeof(IntPtr) }, _runtimeInvokePointer);

                _directCoreInvokeDelegate = OnDirectCoreRuntimeInvoke;
                _directCoreRuntimeInvokeDetour = _preloaderServices.PrepareRuntimeInvokeHook(
                    _runtimeInvokePointer, _directCoreInvokeDelegate, out var trampoline, out var apply);
                var original = trampoline as RuntimeInvokeDetourDelegate
                    ?? throw new InvalidOperationException("OpenACAI runtime-invoke preparation returned an incompatible trampoline.");
                if (_directCoreRuntimeInvokeDetour == null || apply == null)
                    throw new InvalidOperationException("OpenACAI runtime-invoke preparation did not return a complete hook.");

                // Publication must precede native activation: another thread can enter immediately.
                Volatile.Write(ref _directCoreOriginalInvoke, original);

                _directCoreInvocationArmed = true;
                _directCoreInvocationTriggerOwned = true;
                _bepInExIl2CppChainloaderInstanceUsed = false;
                _compatibilityExecutionFallbackRequired = false;
                _compatibilityExecutionFallbackUsed = false;
                _directCoreInvocationFallbackReason = "";
                _runtimeInvokeActivationAttempted = true;
                _runtimeInvokeDetourRetained = true;
                apply();
                _detail = "OpenACAI direct core invocation trigger armed at " + _runtimeInvokePointerText + ".";
                InvokeHookStatus("MarkRuntimeInvokePatched", new[] { typeof(IntPtr) }, _runtimeInvokePointer);
            }
            catch (Exception ex)
            {
                var unwrapped = Unwrap(ex);
                Volatile.Write(ref _runtimeInvokeActivationFailed, 1);
                _directCoreInvocationArmed = false;
                InvokeHookStatus("MarkRuntimeInvokePatchFailed", new[] { typeof(Exception) }, unwrapped);
                if (!_runtimeInvokeActivationAttempted)
                    TryCancelPreparedDirectCoreDetour();

                _runtimeInvokeDetourRetained = _runtimeInvokePointer != IntPtr.Zero
                    && !_preloaderServices.CanUseCompatibilityRuntimeInvoke(_runtimeInvokePointer);
                if (!CanUseCompatibilityFallback())
                {
                    _compatibilityFallbackBlocked = true;
                    _compatibilityExecutionFallbackRequired = true;
                    _compatibilityExecutionFallbackUsed = false;
                    _directCoreInvocationFallbackReason = "Startup hook state is uncertain or retained; compatibility fallback is blocked. Restart the game.";
                    throw new InvalidOperationException(_directCoreInvocationFallbackReason, unwrapped);
                }

                MarkDirectCoreFallback("OpenACAI direct core invocation trigger setup failed: " + unwrapped.Message);
                _trace("OpenACAI direct core invocation trigger setup failed; falling back to compatibility chainloader: " + unwrapped);
            }
        }

        private void RunCompatibilityChainloaderPhases()
        {
            if (!CanUseCompatibilityFallback())
            {
                _compatibilityFallbackBlocked = true;
                throw new InvalidOperationException("Compatibility chainloader cannot replace a live or uncertain owned runtime-invoke hook.");
            }
            _bepInExIl2CppChainloaderInstanceUsed = true;
            _preloaderServices.PrepareCompatibilityLogging();
            _compatibilityExecutionFallbackRequired = true;
            _compatibilityExecutionFallbackUsed = true;
            RunPhase("compatibility.create-bepinex-il2cpp-chainloader-instance", "Creating the compatibility IL2CPPChainloader instance.", CreateCompatibilityInstance);
            RunPhase("compatibility.bind-preloader-chainloader-property", "Binding the compatibility chainloader instance to the legacy preloader property.", BindPreloaderChainloaderProperty);
            RunPhase("compatibility.initialize-bepinex-il2cpp-chainloader-instance", "Initializing the compatibility IL2CPPChainloader instance through the OpenACAI adapter.", InitializeCompatibilityInstance);
        }

        private void SkipCompatibilityExecutionPhases(string detail)
        {
            MarkPhaseReady("compatibility.create-bepinex-il2cpp-chainloader-instance", detail);
            MarkPhaseReady("compatibility.bind-preloader-chainloader-property", detail);
            MarkPhaseReady("compatibility.initialize-bepinex-il2cpp-chainloader-instance", detail);
        }

        private void CreateCompatibilityInstance()
        {
            _compatibilityChainloaderType = _loadRequiredType("BepInEx.Unity.IL2CPP", "BepInEx.Unity.IL2CPP.IL2CPPChainloader");
            _compatibilityChainloaderTypeName = _compatibilityChainloaderType.FullName ?? _compatibilityChainloaderType.Name;
            _compatibilityChainloader = Activator.CreateInstance(_compatibilityChainloaderType);
            _compatibilityInstanceCreated = _compatibilityChainloader != null;
        }

        private void BindPreloaderChainloaderProperty()
        {
            if (_compatibilityChainloader == null)
            {
                throw new InvalidOperationException("Compatibility IL2CPPChainloader instance has not been created.");
            }

            var preloaderType = _loadRequiredType("BepInEx.Unity.IL2CPP", "BepInEx.Unity.IL2CPP.Preloader");
            var chainloaderProperty = preloaderType.GetProperty("Chainloader", BindingFlags.NonPublic | BindingFlags.Static);
            if (chainloaderProperty == null)
            {
                throw new MissingMemberException("BepInEx.Unity.IL2CPP.Preloader", "Chainloader");
            }

            chainloaderProperty.SetValue(null, _compatibilityChainloader);
            _preloaderChainloaderPropertyBound = true;
        }

        private void InitializeCompatibilityInstance()
        {
            if (_compatibilityChainloader == null || _compatibilityChainloaderType == null)
            {
                throw new InvalidOperationException("Compatibility IL2CPPChainloader instance has not been created.");
            }

            var initialize = _compatibilityChainloaderType.GetMethod(
                "Initialize",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            if (initialize == null)
            {
                throw new MissingMethodException(_compatibilityChainloaderTypeName, "Initialize");
            }

            _initializeInvoked = true;
            WriteState();
            initialize.Invoke(_compatibilityChainloader, new object[] { null });
            _initializeReturned = true;
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
                AppendProperty(json, "serviceKey", "openacai.loader.il2CppChainloaderAdapter", true);
                AppendProperty(json, "enteredUtc", _enteredUtc, true);
                AppendProperty(json, "updatedUtc", DateTimeOffset.UtcNow.ToString("O"), true);
                AppendProperty(json, "status", _status, true);
                AppendProperty(json, "detail", _detail, true);
                AppendProperty(json, "exceptionType", _exceptionType, true);
                AppendProperty(json, "mode", _mode, true);
                AppendProperty(json, "adapter", "OpenACAI IL2CPP chainloader adapter", true);
                AppendProperty(json, "currentPhase", _currentPhase, true);
                AppendProperty(json, "phaseCount", _phases.Count.ToString(), false);
                AppendProperty(json, "completedPhaseCount", completedCount.ToString(), false);
                AppendProperty(json, "failedPhaseCount", failedCount.ToString(), false);
                AppendProperty(json, "openAcaiIl2CppChainloaderAdapterOwned", "true", false);
                AppendProperty(json, "bepInExIl2CppChainloaderInstanceUsed", JsonBool(_bepInExIl2CppChainloaderInstanceUsed), false);
                AppendProperty(json, "openAcaiIl2CppChainloaderPortComplete", JsonBool(_openAcaiIl2CppChainloaderPortComplete), false);
                AppendProperty(json, "adapterCreated", JsonBool(_adapterCreated), false);
                AppendProperty(json, "openAcaiIl2CppChainloaderPortPlanCreated", JsonBool(_chainloaderPortPlanCreated), false);
                AppendProperty(json, "openAcaiIl2CppChainloaderPortPlanReady", JsonBool(_chainloaderPortPlanReady), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationPlanCreated", JsonBool(_directCoreInvocationPlanCreated), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationPlanReady", JsonBool(_directCoreInvocationPlanReady), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationArmed", JsonBool(_directCoreInvocationArmed), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationTriggerOwned", JsonBool(_directCoreInvocationTriggerOwned), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationTriggered", JsonBool(_directCoreInvocationTriggered), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationExecuting", JsonBool(_directCoreInvocationExecuting), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationExecuted", JsonBool(_directCoreInvocationExecuted), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationFailed", JsonBool(_directCoreInvocationFailed), false);
                AppendProperty(json, "openAcaiDirectCoreInvocationExceptionType", _directCoreInvocationExceptionType, true);
                AppendProperty(json, "directCoreInvocationFallbackReason", _directCoreInvocationFallbackReason, true);
                AppendProperty(json, "directCoreEntrypointSignatureResolvable", JsonBool(_directCoreEntrypointSignatureResolvable), false);
                AppendProperty(json, "directCoreLogSourceFactoryResolvable", JsonBool(_directCoreLogSourceFactoryResolvable), false);
                AppendProperty(json, "directCoreLogSourceCreated", JsonBool(_directCoreLogSourceCreated), false);
                AppendProperty(json, "runtimeLogging", _preloaderServices.CreateLoggingStateJson(), false);
                AppendProperty(json, "interopPreloadResolvable", JsonBool(_interopPreloadResolvable), false);
                AppendProperty(json, "interopPreloadInvoked", JsonBool(_interopPreloadInvoked), false);
                AppendProperty(json, "interopPreloadReturned", JsonBool(_interopPreloadReturned), false);
                AppendProperty(json, "openAcaiInteropPreloadUsed", JsonBool(_interopPreloadInvoked), false);
                AppendProperty(json, "directCoreLoadInvoked", JsonBool(_directCoreLoadInvoked), false);
                AppendProperty(json, "directCoreLoadReturned", JsonBool(_directCoreLoadReturned), false);
                AppendProperty(json, "runtimeInvokeDetourDisposed", JsonBool(_runtimeInvokeDetourDisposed), false);
                AppendProperty(json, "runtimeInvokeDetourCleanupFailed", JsonBool(_runtimeInvokeDetourCleanupFailed), false);
                AppendProperty(json, "runtimeInvokeActivationAttempted", JsonBool(_runtimeInvokeActivationAttempted), false);
                AppendProperty(json, "runtimeInvokeDetourRetained", JsonBool(_runtimeInvokeDetourRetained), false);
                AppendProperty(json, "runtimeInvokeTriggerRetired", JsonBool(_runtimeInvokeTriggerRetired), false);
                AppendProperty(json, "runtimeInvokeHookLifetime", _runtimeInvokeDetourRetained ? "process"
                    : _runtimeInvokeDetourDisposed ? "cancelled-before-activation" : "not-activated", true);
                AppendProperty(json, "compatibilityFallbackBlocked", JsonBool(_compatibilityFallbackBlocked), false);
                AppendProperty(json, "runtimeInvokePointer", _runtimeInvokePointerText, true);
                AppendProperty(json, "directCoreTriggerMethod", _directCoreTriggerMethod, true);
                AppendProperty(json, "directCoreInvocationPlanDetail", _directCoreInvocationPlanDetail, true);
                AppendProperty(json, "gameAssemblyResolvable", JsonBool(_gameAssemblyResolvable), false);
                AppendProperty(json, "runtimeInvokeExportResolvable", JsonBool(_runtimeInvokeExportResolvable), false);
                AppendProperty(json, "nativeDetourFactoryResolvable", JsonBool(_nativeDetourFactoryResolvable), false);
                AppendProperty(json, "basePluginResolvable", JsonBool(_basePluginResolvable), false);
                AppendProperty(json, "manualLogSourceResolvable", JsonBool(_manualLogSourceResolvable), false);
                AppendProperty(json, "openAcaiCoreAssemblyResolvable", JsonBool(_openAcaiCoreAssemblyResolvable), false);
                AppendProperty(json, "openAcaiCoreEntrypointResolvable", JsonBool(_openAcaiCoreEntrypointResolvable), false);
                AppendProperty(json, "compatibilityExecutionFallbackRequired", JsonBool(_compatibilityExecutionFallbackRequired), false);
                AppendProperty(json, "compatibilityExecutionFallbackUsed", JsonBool(_compatibilityExecutionFallbackUsed), false);
                AppendProperty(json, "chainloaderPortPlanDetail", _chainloaderPortPlanDetail, true);
                AppendProperty(json, "openAcaiCoreAssembly", _openAcaiCoreAssemblyPath, true);
                AppendProperty(json, "compatibilityInstanceCreated", JsonBool(_compatibilityInstanceCreated), false);
                AppendProperty(json, "preloaderChainloaderPropertyBound", JsonBool(_preloaderChainloaderPropertyBound), false);
                AppendProperty(json, "initializeInvoked", JsonBool(_initializeInvoked), false);
                AppendProperty(json, "initializeReturned", JsonBool(_initializeReturned), false);
                AppendProperty(json, "compatibilityChainloaderType", _compatibilityChainloaderTypeName, true);
                AppendProperty(json, "compatibilityBridgeAssembly", _bridgePath, true);
                AppendProperty(json, "runtimeInvokeHookStateFile", Path.Combine(_gameRoot, "BepInEx", "plugins", "OpenACAILoader", "openacai-il2cpp-hook-state.json"), true);
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

        private void MarkPhasePending(string name, string detail)
        {
            MarkPhase(name, "PENDING", "", detail);
        }

        private void MarkPhaseRunning(string name, string detail)
        {
            MarkPhase(name, "RUNNING", "", detail);
        }

        private void MarkPhaseReady(string name, string detail)
        {
            MarkPhase(name, "READY", "", detail);
        }

        private void MarkPhaseFailed(string name, Exception exception, string detail)
        {
            var unwrapped = Unwrap(exception);
            MarkPhase(name, "FAILED", unwrapped.GetType().FullName ?? unwrapped.GetType().Name, detail);
        }

        private void MarkPhase(string name, string status, string exceptionType, string detail)
        {
            var phase = _phases.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            phase.Status = status;
            if (string.IsNullOrWhiteSpace(phase.StartedUtc))
            {
                phase.StartedUtc = DateTimeOffset.UtcNow.ToString("O");
            }

            if (string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                phase.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
            }

            phase.ExceptionType = exceptionType ?? "";
            phase.Detail = detail ?? "";
            WriteState();
        }

        private void MarkDirectCoreFallback(string reason)
        {
            _directCoreInvocationArmed = false;
            _directCoreInvocationTriggerOwned = false;
            _compatibilityExecutionFallbackRequired = true;
            _compatibilityExecutionFallbackUsed = false;
            _directCoreInvocationFallbackReason = reason ?? "OpenACAI direct core invocation fell back to compatibility execution.";
            _detail = _directCoreInvocationFallbackReason;
        }

        private bool TryResolveRuntimeInvokeExportForDirectTrigger()
        {
            if (!NativeLibrary.TryLoad(_preloaderServices.GetGameAssemblyPath(), out _gameAssemblyHandle))
            {
                _runtimeInvokeExportResolvable = false;
                _runtimeInvokePointer = IntPtr.Zero;
                _runtimeInvokePointerText = "";
                return false;
            }

            _runtimeInvokeExportResolvable = NativeLibrary.TryGetExport(_gameAssemblyHandle, "il2cpp_runtime_invoke", out _runtimeInvokePointer)
                && _runtimeInvokePointer != IntPtr.Zero;
            _runtimeInvokePointerText = FormatPointer(_runtimeInvokePointer);
            if (!_runtimeInvokeExportResolvable)
            {
                NativeLibrary.Free(_gameAssemblyHandle);
                _gameAssemblyHandle = IntPtr.Zero;
            }
            return _runtimeInvokeExportResolvable;
        }

        private IntPtr OnDirectCoreRuntimeInvoke(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
        {
            var original = Volatile.Read(ref _directCoreOriginalInvoke);
            if (Volatile.Read(ref _directCoreExecutionClaimed) != 0 || Volatile.Read(ref _runtimeInvokeActivationFailed) != 0)
                return original(method, obj, parameters, exc);

            var methodName = TryGetIl2CppMethodName(method);
            if (!string.Equals(methodName, _directCoreTriggerMethod, StringComparison.Ordinal))
                return original(method, obj, parameters, exc);

            var shouldExecute = false;
            lock (_directCoreSync)
            {
                if (_runtimeInvokeActivationFailed == 0 && _directCoreInvocationArmed
                    && Interlocked.CompareExchange(ref _directCoreExecutionClaimed, 1, 0) == 0)
                {
                    _directCoreInvocationTriggered = true;
                    _directCoreInvocationExecuting = true;
                    shouldExecute = true;
                    _status = "RUNNING";
                    _currentPhase = "openacai.execute-direct-core-runtime";
                    _detail = "Unity method " + methodName + " triggered OpenACAI direct core runtime execution.";
                    MarkPhaseRunning("openacai.execute-direct-core-runtime", _detail);
                    InvokeHookStatus("MarkChainloaderExecuting", new[] { typeof(string) }, methodName);
                    WriteChainloaderState("RUNNING", _detail);
                }
            }

            if (shouldExecute)
            {
                try
                {
                    ExecuteDirectCoreRuntime();
                    InvokeHookStatus("MarkChainloaderExecuted");
                }
                catch (Exception ex)
                {
                    var unwrapped = Unwrap(ex);
                    _directCoreInvocationFailed = true;
                    _directCoreInvocationExceptionType = unwrapped.GetType().FullName ?? unwrapped.GetType().Name;
                    _status = "FAILED";
                    _detail = "OpenACAI direct core runtime execution failed: " + unwrapped.Message;
                    MarkPhaseFailed("openacai.execute-direct-core-runtime", unwrapped, _detail);
                    InvokeHookStatus("MarkChainloaderFailed", new[] { typeof(Exception) }, unwrapped);
                    WriteChainloaderState("FAILED", _detail, _directCoreInvocationExceptionType);
                    _trace("OpenACAI direct core runtime execution failed: " + unwrapped);
                }
                finally
                {
                    lock (_directCoreSync)
                    {
                        _directCoreInvocationExecuting = false;
                        _directCoreInvocationArmed = false;
                        _runtimeInvokeTriggerRetired = true;
                        WriteState();
                    }
                }
            }

            // Keep the gateway and trampoline alive for concurrent entries already redirected here.
            return original(method, obj, parameters, exc);
        }

        private void ExecuteDirectCoreRuntime()
        {
            // The interop API's static initializer needs a live domain. Do not attach native logging while arming.
            _preloaderServices.AttachNativeRuntimeLogging();
            _interopPreloadInvoked = true;
            WriteState();
            _preloadInteropAssemblies();
            _interopPreloadReturned = true;
            WriteState();

            var manualLogSourceType = _loadRequiredType("BepInEx.Core", "BepInEx.Logging.ManualLogSource");
            var loggerType = _loadRequiredType("BepInEx.Core", "BepInEx.Logging.Logger");
            var createLogSource = loggerType.GetMethod(
                "CreateLogSource",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (createLogSource == null)
            {
                throw new MissingMethodException(loggerType.FullName, "CreateLogSource");
            }

            var logSource = createLogSource.Invoke(null, new object[] { "Modstoke Loader Core" });
            if (logSource == null || !manualLogSourceType.IsInstanceOfType(logSource))
            {
                throw new InvalidOperationException("OpenACAI direct core invocation could not create a ManualLogSource.");
            }

            _directCoreLogSourceCreated = true;
            WriteState();

            var coreAssembly = FindLoadedAssembly("OpenACAI.Endnight.Loader.Core")
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(_openAcaiCoreAssemblyPath);
            var runtimeType = coreAssembly.GetType("OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime", true, false);
            var load = runtimeType.GetMethod(
                "Load",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { manualLogSourceType },
                null);
            if (load == null)
            {
                throw new MissingMethodException("OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime", "Load");
            }

            _directCoreLoadInvoked = true;
            WriteState();
            WriteChainloaderState("RUNNING", "OpenACAI direct core runtime is invoking OpenACAIEndnightRuntime.Load.");
            load.Invoke(null, new[] { logSource });
            _directCoreLoadReturned = true;
            _directCoreInvocationExecuted = true;
            _directCoreInvocationFailed = false;
            _directCoreInvocationExceptionType = "";
            _openAcaiIl2CppChainloaderPortComplete = true;
            _status = "READY";
            _currentPhase = "";
            _detail = "OpenACAI direct core runtime executed successfully.";
            MarkPhaseReady("openacai.execute-direct-core-runtime", _detail);
            WriteChainloaderState("READY", "OpenACAI direct core runtime executed successfully without upstream BepInEx IL2CPP chainloader execution or hooks.",
                portComplete: true);
        }

        private void WriteChainloaderState(string status, string detail, string exceptionType = "", bool portComplete = false)
        {
            OpenACAIChainloaderBoundary.WriteState(
                _gameRoot,
                _runtimeDirectory,
                _compatibilityDirectory,
                _bepInExCoreDirectory,
                _enteredUtc,
                _mode,
                status,
                _bridgePath,
                false,
                false,
                exceptionType,
                detail,
                portComplete);
        }

        private bool CanUseCompatibilityFallback() => !_runtimeInvokeActivationAttempted
            && (_runtimeInvokePointer == IntPtr.Zero || _preloaderServices.CanUseCompatibilityRuntimeInvoke(_runtimeInvokePointer));

        private void TryCancelPreparedDirectCoreDetour()
        {
            var detour = _directCoreRuntimeInvokeDetour;
            if (detour == null)
            {
                return;
            }

            try
            {
                detour.Dispose();
                _directCoreRuntimeInvokeDetour = null;
                _directCoreOriginalInvoke = null;
                _runtimeInvokeDetourDisposed = true;
            }
            catch (Exception ex)
            {
                _runtimeInvokeDetourCleanupFailed = true;
                InvokeHookStatus("MarkRuntimeInvokeCleanupFailed", new[] { typeof(Exception) }, Unwrap(ex));
                _trace("OpenACAI prepared runtime-invoke hook cleanup failed: " + ex);
            }
            finally
            {
                WriteState();
            }
        }

        private string TryGetIl2CppMethodName(IntPtr method)
        {
            try
            {
                if (_il2CppMethodGetNameMethod == null)
                {
                    var il2CppType = _loadRequiredType("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.IL2CPP");
                    _il2CppMethodGetNameMethod = il2CppType.GetMethod(
                        "il2cpp_method_get_name",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(IntPtr) },
                        null);
                }

                if (_il2CppMethodGetNameMethod == null)
                {
                    return "";
                }

                var namePtr = _il2CppMethodGetNameMethod.Invoke(null, new object[] { method });
                return namePtr is IntPtr pointer ? Marshal.PtrToStringAnsi(pointer) ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        private void InvokeHookStatus(string methodName)
        {
            InvokeHookStatus(methodName, Type.EmptyTypes);
        }

        private void InvokeHookStatus(string methodName, Type[] parameterTypes, params object[] args)
        {
            try
            {
                var hookStatusType = _loadRequiredType("BepInEx.Unity.IL2CPP", "BepInEx.Unity.IL2CPP.OpenACAIIL2CPPHookStatus");
                var method = hookStatusType.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    parameterTypes ?? Type.EmptyTypes,
                    null);
                method?.Invoke(null, args ?? Array.Empty<object>());
            }
            catch
            {
            }
        }

        private static Assembly FindLoadedAssembly(string assemblyName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatPointer(IntPtr pointer)
        {
            return pointer == IntPtr.Zero ? "0x0" : "0x" + pointer.ToInt64().ToString("X");
        }

        private bool TryResolveRuntimeInvokeExport()
        {
            IntPtr handle;
            if (!NativeLibrary.TryLoad(_preloaderServices.GetGameAssemblyPath(), out handle))
            {
                _runtimeInvokeExportResolvable = false;
                return false;
            }

            try
            {
                IntPtr runtimeInvokePtr;
                _runtimeInvokeExportResolvable = NativeLibrary.TryGetExport(handle, "il2cpp_runtime_invoke", out runtimeInvokePtr)
                    && runtimeInvokePtr != IntPtr.Zero;
                return _runtimeInvokeExportResolvable;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    NativeLibrary.Free(handle);
                }
            }
        }

        private static bool TryResolveCreateLogSource(Type loggerType, Type manualLogSourceType)
        {
            if (loggerType == null || manualLogSourceType == null)
            {
                return false;
            }

            var method = loggerType.GetMethod(
                "CreateLogSource",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            return method != null && manualLogSourceType.IsAssignableFrom(method.ReturnType);
        }

        private static bool AssemblyHasMethod(string assemblyPath, string typeFullName, string methodName)
        {
            try
            {
                using (var stream = File.OpenRead(assemblyPath))
                using (var peReader = new PEReader(stream))
                {
                    if (!peReader.HasMetadata)
                    {
                        return false;
                    }

                    var reader = peReader.GetMetadataReader();
                    foreach (var typeHandle in reader.TypeDefinitions)
                    {
                        var typeDefinition = reader.GetTypeDefinition(typeHandle);
                        if (!string.Equals(ReadTypeFullName(reader, typeDefinition), typeFullName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        foreach (var methodHandle in typeDefinition.GetMethods())
                        {
                            var method = reader.GetMethodDefinition(methodHandle);
                            if (string.Equals(reader.GetString(method.Name), methodName, StringComparison.Ordinal))
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static string ReadTypeFullName(MetadataReader reader, TypeDefinition typeDefinition)
        {
            var typeName = reader.GetString(typeDefinition.Name);
            var ns = reader.GetString(typeDefinition.Namespace);
            return string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
        }

        private string BuildMissingPortPlanDetail()
        {
            var missing = new List<string>();
            AddMissing(missing, _gameAssemblyResolvable, "GameAssembly");
            AddMissing(missing, _runtimeInvokeExportResolvable, "il2cpp_runtime_invoke");
            AddMissing(missing, _nativeDetourFactoryResolvable, "native detour factory");
            AddMissing(missing, _basePluginResolvable, "BasePlugin compatibility type");
            AddMissing(missing, _manualLogSourceResolvable, "ManualLogSource");
            AddMissing(missing, _openAcaiCoreAssemblyResolvable, "OpenACAI core assembly");
            AddMissing(missing, _openAcaiCoreEntrypointResolvable, "OpenACAIEndnightRuntime.Load");
            return missing.Count == 0 ? "unknown readiness issue" : string.Join(", ", missing.ToArray());
        }

        private string BuildMissingDirectCoreInvocationDetail()
        {
            var missing = new List<string>();
            AddMissing(missing, _chainloaderPortPlanReady, "chainloader port readiness");
            AddMissing(missing, _directCoreEntrypointSignatureResolvable, "direct core entrypoint signature");
            AddMissing(missing, _directCoreLogSourceFactoryResolvable, "Logger.CreateLogSource");
            AddMissing(missing, _interopPreloadResolvable, "OpenACAI interop preload callback");
            return missing.Count == 0 ? "unknown direct invocation readiness issue" : string.Join(", ", missing.ToArray());
        }

        private static void AddMissing(List<string> missing, bool ready, string name)
        {
            if (!ready)
            {
                missing.Add(name);
            }
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

        private static Exception Unwrap(Exception ex)
        {
            return ex is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : ex;
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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RuntimeInvokeDetourDelegate(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);
    }
}
