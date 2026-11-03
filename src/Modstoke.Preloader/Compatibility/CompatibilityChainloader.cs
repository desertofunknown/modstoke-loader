// SPDX-License-Identifier: GPL-3.0-or-later
// Runtime trigger and logging behavior adapted from BepInEx's IL2CPPChainloader.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Logging;

namespace Modstoke.Preloader
{
    internal sealed class CompatibilityChainloader : ICompatibilityChainloader
    {
        private static readonly ConfigEntry<bool> ConfigUnityLogging = ConfigFile.CoreConfig.Bind(
            "Logging", "UnityLogListening", true,
            "Enables showing unity log messages in the BepInEx logging system.");
        private static readonly ConfigEntry<bool> ConfigDiskWriteUnityLog = ConfigFile.CoreConfig.Bind(
            "Logging.Disk", "WriteUnityLog", false,
            "Include unity log messages in log file output.");

        private readonly object _gate = new object();
        private readonly IL2CPPChainloader _owner;
        private readonly PreloaderServices _services;
        private readonly RuntimeInvokeDetourDelegate _callback;
        private IDisposable _runtimeInvokeDetour;
        private RuntimeInvokeDetourDelegate _originalInvoke;
        private IntPtr _gameAssemblyHandle;
        private bool _armAttempted;
        private bool _armed;
        private int _executionClaimed;
        private int _activationFailed;

        private CompatibilityChainloader(IL2CPPChainloader owner)
        {
            _owner = owner;
            _services = (PreloaderServices)PreloaderServices.GetSharedInstance();
            _callback = OnInvokeMethod;
        }

        internal static ICompatibilityChainloader Create(IL2CPPChainloader owner) =>
            new CompatibilityChainloader(owner);

        public void InitializeLoggers()
        {
            if (!ConfigDiskWriteUnityLog.Value)
                global::BepInEx.Logging.DiskLogListener.BlacklistedSources.Add("Unity");
            ChainloaderLogHelper.RewritePreloaderLogs();
        }

        public void Arm()
        {
            lock (_gate)
            {
                if (_armAttempted)
                    return;
                _armAttempted = true;
                HookStatus.MarkInitializing();

                try
                {
                    if (!NativeLibrary.TryLoad(_services.GameAssemblyPath, out _gameAssemblyHandle))
                    {
                        const string message = "Could not load the configured Il2Cpp game assembly. IL2CPP plugin loading is disabled for this run.";
                        HookStatus.MarkGameAssemblyMissing(message);
                        Log(LogLevel.Fatal, message);
                        return;
                    }
                }
                catch (Exception error)
                {
                    HookStatus.MarkGameAssemblyMissing("Could not load the configured Il2Cpp game assembly.");
                    Log(LogLevel.Error, error);
                    return;
                }

                HookStatus.MarkGameAssemblyLoaded();
                if (!NativeLibrary.TryGetExport(_gameAssemblyHandle, "il2cpp_runtime_invoke", out var runtimeInvoke))
                {
                    const string message = "Could not locate il2cpp_runtime_invoke in the Il2Cpp game assembly. IL2CPP plugin loading is disabled for this run.";
                    HookStatus.MarkRuntimeInvokeMissing(message);
                    Log(LogLevel.Fatal, message);
                    return;
                }

                HookStatus.MarkRuntimeInvokeExportFound(runtimeInvoke);
                var activationAttempted = false;
                try
                {
                    _runtimeInvokeDetour = _services.PrepareRuntimeInvokeHook(runtimeInvoke, _callback,
                        out var trampoline, out var apply);
                    // Publish the trampoline before activation. The factory roots the callback and its owner.
                    Volatile.Write(ref _originalInvoke, (RuntimeInvokeDetourDelegate)trampoline);
                    activationAttempted = true;
                    apply();
                    HookStatus.MarkRuntimeInvokePatched(runtimeInvoke);
                    _armed = true;
                }
                catch (Exception error)
                {
                    Volatile.Write(ref _activationFailed, 1);
                    // An attempted activation may already be reachable by native callers. Never release it.
                    if (!activationAttempted && _runtimeInvokeDetour != null)
                    {
                        try
                        {
                            _runtimeInvokeDetour.Dispose();
                        }
                        catch (Exception cleanupError)
                        {
                            Log(LogLevel.Warning, cleanupError);
                        }
                    }
                    HookStatus.MarkRuntimeInvokePatchFailed(error);
                    Log(LogLevel.Fatal, "Unable to patch il2cpp_runtime_invoke. IL2CPP plugin loading is disabled for this run.");
                    Log(LogLevel.Error, error);
                }
            }
        }

        private IntPtr OnInvokeMethod(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
        {
            var original = Volatile.Read(ref _originalInvoke);
            try
            {
                if (Volatile.Read(ref _executionClaimed) == 0 && Volatile.Read(ref _activationFailed) == 0
                    && method != IntPtr.Zero)
                {
                    var methodName = Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name(method));
                    var execute = false;
                    if (methodName == "Internal_ActiveSceneChanged")
                    {
                        // Other threads wait for arming; same-thread activation reentry only forwards.
                        lock (_gate)
                            execute = _armed && _activationFailed == 0
                                && Interlocked.CompareExchange(ref _executionClaimed, 1, 0) == 0;
                    }
                    if (execute)
                    {
                        HookStatus.MarkChainloaderExecuting(methodName);
                        _services.PreloadInteropAssemblies();
                        Logger.Sources.Add(new global::BepInEx.Unity.IL2CPP.Logging.IL2CPPLogSource());
                        if (ConfigUnityLogging.Value)
                            Logger.Sources.Add(new IL2CPPUnityLogSource());
                        _owner.Execute();
                        HookStatus.MarkChainloaderExecuted();
                    }
                }
            }
            catch (Exception error)
            {
                ReportCallbackFailure(error);
            }

            try
            {
                return original(method, obj, parameters, exc);
            }
            catch (Exception error)
            {
                ReportCallbackFailure(error);
                return IntPtr.Zero;
            }
        }

        private static void ReportCallbackFailure(Exception error)
        {
            try
            {
                HookStatus.MarkChainloaderFailed(error);
            }
            catch
            {
                // Failure reporting must not unwind through the native callback.
            }
            Log(LogLevel.Error, error);
        }

        private static void Log(LogLevel level, object message)
        {
            try
            {
                Logger.Log(level, message);
            }
            catch
            {
                // Log listeners must remain isolated from native callers.
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RuntimeInvokeDetourDelegate(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);
    }
}
