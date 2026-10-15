// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BepInEx.Unity.IL2CPP;

public static class OpenACAIIL2CPPHookStatus
{
    public const string StateDirectoryName = "OpenACAILoader";
    public const string StateFileName = "openacai-il2cpp-hook-state.json";

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, bool> ProviderAvailability = new(StringComparer.OrdinalIgnoreCase);
    private static Action<object> _stateWriter;

    private static string _status = "pending";
    private static string _detail = "IL2CPP hook status has not been initialized.";
    private static string _selectedProvider;
    private static string _detourProviderMode;
    private static string _runtimeInvokePointer;
    private static string _lastError;
    private static bool _gameAssemblyLoaded;
    private static bool _runtimeInvokeExportFound;
    private static bool _runtimeInvokePatched;
    private static bool _chainloaderExecutionStarted;
    private static bool _chainloaderExecuted;
    private static bool _hookDisposed;
    private static bool _failed;

    public static void MarkInitializing()
    {
        Update(() =>
        {
            _status = "initializing";
            _detail = "BepInEx IL2CPP chainloader is preparing the runtime invoke detour.";
            _failed = false;
            _lastError = null;
        });
    }

    public static void MarkGameAssemblyLoaded()
    {
        Update(() =>
        {
            _gameAssemblyLoaded = true;
            _status = "game-assembly-loaded";
            _detail = "GameAssembly was loaded and is ready for il2cpp_runtime_invoke lookup.";
        });
    }

    public static void MarkGameAssemblyMissing(string detail)
    {
        MarkFailed("game-assembly-missing", detail, null);
    }

    public static void MarkRuntimeInvokeMissing(string detail)
    {
        Update(() =>
        {
            _gameAssemblyLoaded = true;
            _runtimeInvokeExportFound = false;
            _runtimeInvokePatched = false;
            _status = "failed";
            _detail = detail;
            _failed = true;
            _lastError = detail;
        });
    }

    public static void MarkRuntimeInvokeExportFound(IntPtr runtimeInvokePtr)
    {
        Update(() =>
        {
            _runtimeInvokeExportFound = true;
            _runtimeInvokePointer = FormatPointer(runtimeInvokePtr);
            _status = "runtime-invoke-found";
            _detail = "il2cpp_runtime_invoke was found and is ready for native detour setup.";
        });
    }

    public static void RecordProviderProbe(string provider, bool available)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return;
        }

        Update(() =>
        {
            ProviderAvailability[provider] = available;
            _detail = available
                ? provider + " native detour provider is available."
                : provider + " native detour provider is unavailable.";
        });
    }

    public static void RecordProviderSelected(string provider, string mode)
    {
        Update(() =>
        {
            _selectedProvider = provider;
            _detourProviderMode = mode;
            _status = "provider-selected";
            _detail = provider + " native detour provider selected" + (string.IsNullOrWhiteSpace(mode) ? "." : " in " + mode + " mode.");
        });
    }

    public static void MarkRuntimeInvokePatched(IntPtr runtimeInvokePtr)
    {
        Update(() =>
        {
            _runtimeInvokeExportFound = true;
            _runtimeInvokePatched = true;
            _runtimeInvokePointer = FormatPointer(runtimeInvokePtr);
            _status = "ready";
            _detail = "il2cpp_runtime_invoke was patched successfully. Waiting for Unity scene activation to run plugins.";
            _failed = false;
            _lastError = null;
        });
    }

    public static void MarkRuntimeInvokePatchFailed(Exception exception)
    {
        MarkFailed("failed", "Unable to patch il2cpp_runtime_invoke. IL2CPP plugin loading is disabled for this run.", exception);
    }

    public static void MarkChainloaderExecuting(string methodName)
    {
        Update(() =>
        {
            _chainloaderExecutionStarted = true;
            _status = "chainloader-executing";
            _detail = "Unity method " + (string.IsNullOrWhiteSpace(methodName) ? "<unknown>" : methodName) + " triggered IL2CPP chainloader execution.";
        });
    }

    public static void MarkChainloaderExecuted()
    {
        Update(() =>
        {
            _chainloaderExecutionStarted = true;
            _chainloaderExecuted = true;
            _status = "loaded";
            _detail = "BepInEx IL2CPP chainloader executed successfully.";
            _failed = false;
            _lastError = null;
        });
    }

    public static void MarkChainloaderFailed(Exception exception)
    {
        MarkFailed("failed", "Unable to execute IL2CPP chainloader.", exception);
    }

    public static void MarkRuntimeInvokeUnpatched()
    {
        Update(() =>
        {
            _hookDisposed = true;
            if (_failed)
            {
                _detail += " Runtime invoke detour was released during failure cleanup.";
                return;
            }

            _status = _chainloaderExecuted ? "loaded" : "warning";
            _detail = _chainloaderExecuted
                ? "Runtime invoke detour was released after chainloader execution."
                : "Runtime invoke detour was released before the chainloader reported successful execution.";
        });
    }

    public static void MarkRuntimeInvokeCleanupFailed(Exception exception)
    {
        Update(() =>
        {
            if (_failed)
            {
                _detail += " Runtime invoke detour cleanup also failed.";
                _lastError = (_lastError ?? string.Empty) + Environment.NewLine + exception;
                return;
            }

            _status = "warning";
            _detail = "Runtime invoke detour cleanup failed after startup.";
            _lastError = exception?.ToString();
        });
    }

    private static void MarkFailed(string status, string detail, Exception exception)
    {
        Update(() =>
        {
            _status = string.IsNullOrWhiteSpace(status) ? "failed" : status;
            _detail = detail;
            _failed = true;
            _lastError = exception?.ToString() ?? detail;
        });
    }

    private static void Update(Action update)
    {
        lock (SyncRoot)
        {
            update?.Invoke();
            WriteNoThrow();
        }
    }

    internal static void RegisterStateWriter(Action<object> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (SyncRoot)
        {
            if (_stateWriter == writer)
                return;
            if (_stateWriter != null)
                throw new InvalidOperationException("A native hook status writer is already registered.");

            _stateWriter = writer;
            WriteNoThrow();
        }
    }

    private static void WriteNoThrow()
    {
        try
        {
            if (_stateWriter != null)
            {
                _stateWriter(CreateStateNoLock());
                return;
            }

            var directory = ResolveStateDirectory();
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, StateFileName);
            var json = JsonSerializer.Serialize(CreateStateNoLock(), new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(path, json);
        }
        catch
        {
            // This status file is diagnostic-only and must never affect startup.
        }
    }

    private static object CreateStateNoLock()
    {
        return new
        {
            schema = 1,
            generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            status = _status,
            detail = _detail,
            failed = _failed,
            gameAssemblyLoaded = _gameAssemblyLoaded,
            runtimeInvokeExportFound = _runtimeInvokeExportFound,
            runtimeInvokePatched = _runtimeInvokePatched,
            runtimeInvokePointer = _runtimeInvokePointer,
            chainloaderExecutionStarted = _chainloaderExecutionStarted,
            chainloaderExecuted = _chainloaderExecuted,
            hookDisposed = _hookDisposed,
            selectedProvider = _selectedProvider,
            detourProviderMode = _detourProviderMode,
            providerAvailability = new Dictionary<string, bool>(ProviderAvailability, StringComparer.OrdinalIgnoreCase),
            dotnetRuntimeVersion = Environment.Version.ToString(),
            dotnetFrameworkDescription = RuntimeInformation.FrameworkDescription,
            gameRoot = Paths.GameRootPath,
            processName = Paths.ProcessName,
            lastError = _lastError
        };
    }

    private static string ResolveStateDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Paths.PluginPath))
        {
            return Path.Combine(Paths.PluginPath, StateDirectoryName);
        }

        if (!string.IsNullOrWhiteSpace(Paths.BepInExRootPath))
        {
            return Path.Combine(Paths.BepInExRootPath, "plugins", StateDirectoryName);
        }

        if (!string.IsNullOrWhiteSpace(Paths.GameRootPath))
        {
            return Path.Combine(Paths.GameRootPath, "BepInEx", "plugins", StateDirectoryName);
        }

        return null;
    }

    private static string FormatPointer(IntPtr pointer)
    {
        return pointer == IntPtr.Zero ? "0x0" : "0x" + pointer.ToInt64().ToString("X");
    }
}
