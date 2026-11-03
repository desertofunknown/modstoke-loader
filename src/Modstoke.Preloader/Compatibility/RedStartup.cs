// SPDX-License-Identifier: GPL-3.0-or-later
// Startup behavior adapted from RedLoader's DoorstopEntrypoint.cs and UnityPreloadRunner.cs.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

extern alias RedCompatibility;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using RedCompatibility::RedLoader;
using RedCompatibility::RedLoader.Preloader.Core;
using RedCompatibility::RedLoader.Unity.IL2CPP;
using RedCompatibility::RedLoader.Unity.IL2CPP.Utils;
using RedCompatibility::RedLoader.Utils;
using Platform = RedCompatibility::MonoMod.Utils.Platform;
using PlatformHelper = RedCompatibility::MonoMod.Utils.PlatformHelper;

namespace Modstoke.Preloader;

internal static class RedStartup
{
    internal static void Start()
    {
        var configuredLog = Path.GetFileName(Environment.GetEnvironmentVariable("PRELOADER_LOG"));
        var silentExceptionLog = string.IsNullOrEmpty(configuredLog)
            ? $"preloader_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log"
            : configuredLog;
        Mutex mutex = null;
        var acquired = false;

        try
        {
            EnvVars.LoadVars();
            silentExceptionLog = Path.Combine(Path.GetDirectoryName(EnvVars.DOORSTOP_PROCESS_PATH),
                "_Redloader", silentExceptionLog);

            using var process = Process.GetCurrentProcess();
            var mutexId = Utility.HashStrings(process.ProcessName, EnvVars.DOORSTOP_PROCESS_PATH, "Doorstop.Entrypoint");
            mutex = new Mutex(false, $"Global\\{mutexId}");
            try
            {
                acquired = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // WaitOne transfers ownership even when it reports an abandoned mutex.
                acquired = true;
                throw;
            }

            PreloaderMain();
        }
        catch (Exception error)
        {
            try
            {
                var logDirectory = Path.GetDirectoryName(silentExceptionLog);
                if (!string.IsNullOrEmpty(logDirectory))
                    Directory.CreateDirectory(logDirectory);
                File.WriteAllText(silentExceptionLog, error.ToString());
            }
            catch (Exception)
            {
                // The notification and exit policy below still apply when the log cannot be written.
            }

            try
            {
                if (PlatformHelper.Is(Platform.Windows))
                {
                    MessageBox.Show("Failed to start Modstoke Loader. Check the preloader log for details.",
                        "Modstoke Loader by OpenACAI");
                }
                else if (NotifySend.IsSupported)
                {
                    NotifySend.Send("Failed to start Modstoke Loader", "Check logs for details");
                }
                else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REDLOADER_FAIL_FAST")))
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Preserve the configured startup-failure policy if notification fails.
            }

            Environment.Exit(1);
        }
        finally
        {
            try
            {
                if (acquired)
                    mutex.ReleaseMutex();
            }
            finally
            {
                mutex?.Dispose();
            }
        }
    }

    internal static void PreloaderMain()
    {
        // Retain target-path validation before changing compatibility state.
        _ = Path.GetFullPath(EnvVars.DOORSTOP_INVOKE_DLL_PATH);
        PlatformUtils.SetPlatform();
        LoaderEnvironment.SetExecutablePath(EnvVars.DOORSTOP_PROCESS_PATH, EnvVars.DOORSTOP_DLL_SEARCH_DIRS);

        // Preferences already use owned Core; resolution must be available before they initialize.
        AppDomain.CurrentDomain.AssemblyResolve += UnityPreloaderRunner.LocalResolve;
        CorePreferences.Init();
        InitReshade();
        AppDomain.CurrentDomain.AddCecilPlatformAssemblies(Path.Combine(LoaderEnvironment.LoaderDirectory, "dotnet"));
        RedPreloader.Run();
    }

    internal static Assembly LocalResolve(object sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name);
        var found = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == assemblyName.Name);
        if (found != null)
            return found;

        if (Utility.TryResolveDllAssembly(assemblyName, LoaderEnvironment.LoaderAssemblyDirectory, out found) ||
            Utility.TryResolveDllAssembly(assemblyName, LoaderEnvironment.LibsDirectory, out found) ||
            Utility.TryResolveDllAssembly(assemblyName, LoaderEnvironment.PatcherPluginPath, out found) ||
            Utility.TryResolveDllAssembly(assemblyName, LoaderEnvironment.ModsDirectory, out found))
            return found;

        // The legacy search order remains first; owned runtime dependencies live in the suite layout.
        var root = LoaderEnvironment.GameRootDirectory;
        var runtime = Path.GetDirectoryName(typeof(RedStartup).Assembly.Location) ?? Path.Combine(root, "Modstoke", "Runtime");
        var directories = new[]
        {
            runtime,
            Path.Combine(runtime, "Compatibility"),
            Path.Combine(root, "BepInEx", "core"),
            Path.Combine(root, "BepInEx", "interop"),
            Path.Combine(root, "BepInEx", "plugins", "OpenACAILoader")
        };
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, assemblyName.Name + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }

        return null;
    }

    internal static void InitReshade()
    {
        try
        {
            var dxgi = Path.Combine(LoaderEnvironment.GameRootDirectory, "dxgi.dll");
            var reshade = Path.Combine(LoaderEnvironment.GameRootDirectory, "reshade.dll");
            // Other dxgi.dll proxies (DXVK, Special K, OptiScaler, ENB) must stay where their owner put them.
            if (!File.Exists(reshade) && File.Exists(dxgi) && IsReshade(dxgi))
            {
                File.Move(dxgi, reshade);
                RLog.Msg("Renamed ReShade dxgi.dll to reshade.dll");
            }

            if (File.Exists(reshade))
            {
                NativeLibrary.TryLoad(reshade, out var handle);
                RLog.Msg($"Loaded ReShade: {handle != IntPtr.Zero}");
            }
        }
        catch (Exception ex)
        {
            RLog.Warning($"Could not set up ReShade: {ex.Message}");
        }
    }

    private static bool IsReshade(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return (info.ProductName?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (info.FileDescription?.Contains("ReShade", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
