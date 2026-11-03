// SPDX-License-Identifier: GPL-3.0-or-later
// Startup behavior adapted from RedLoader's Preloader.cs.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

extern alias RedCompatibility;

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using RedCompatibility::RedLoader;
using RedCompatibility::RedLoader.IL2CPP.RuntimeFixes;
using RedCompatibility::RedLoader.Preloader.Core.Patching;
using RedCompatibility::RedLoader.Preloader.RuntimeFixes;
using RedCompatibility::RedLoader.Unity.Common;
using RedCompatibility::RedLoader.Unity.IL2CPP;
using RedCompatibility::RedLoader.Utils;
using Platform = RedCompatibility::MonoMod.Utils.Platform;
using PlatformHelper = RedCompatibility::MonoMod.Utils.PlatformHelper;

namespace Modstoke.Preloader;

internal static class RedPreloader
{
    private static IL2CPPChainloader Chainloader { get; set; }

    internal static void Run()
    {
        try
        {
            HarmonyBackendFix.Initialize();
            ConsoleSetOutFix.Apply();
            UnityInfo.Initialize(LoaderEnvironment.GameExecutablePath, LoaderEnvironment.UnityGameDataDirectory);

            ConsoleManager.Initialize(false, true);
            if (ConsoleManager.ConsoleEnabled)
            {
                ConsoleManager.CreateConsole();
                if (!LoaderEnvironment.IsDedicatedServer)
                    ConsoleManager.SetConsoleRect(CorePreferences.ConsoleRect.Value);
            }

            if (!LoaderEnvironment.IsDedicatedServer)
            {
                SplashWindow.CreateWindow();
                SplashWindow.HookLog();
                SplashWindow.TotalProgressSteps = 5;
                GlobalEvents.OnApplicationLateStart.Subscribe(() =>
                {
                    SplashWindow.UnhookLog();
                    SplashWindow.CloseWindow();
                });
            }

            RedirectStdErrFix.Apply();

            RLog.Msg($"Running under Unity {UnityInfo.Version}");
            RLog.Msg($"Runtime version: {Environment.Version}");
            RLog.Msg($"Runtime information: {RuntimeInformation.FrameworkDescription}");
            RLog.Msg($"Game executable path: {LoaderEnvironment.GameExecutablePath}");
            RLog.Msg($"Interop assembly directory: {Il2CppInteropManager.IL2CPPInteropAssemblyPath}");
            RLog.Msg($"Modstoke compatibility root path: {LoaderEnvironment.LoaderDirectory}");

            if (PlatformHelper.Is(Platform.Wine) && !Environment.Is64BitProcess)
            {
                if (!NativeLibrary.TryGetExport(NativeLibrary.Load("ntdll"), "RtlRestoreContext", out _))
                    RLog.Warning("Your wine version doesn't support CoreCLR properly, expect crashes! Upgrade to wine 7.16 or higher.");
            }

            NativeLibrary.SetDllImportResolver(typeof(Il2CppInterop.Runtime.IL2CPP).Assembly, DllImportResolver);
            Il2CppInteropManager.Initialize();
            SplashWindow.SetProgressSteps(1);

            using (var patcher = new RedCompatibility::RedLoader.Preloader.Core.Patching.AssemblyPatcher((data, _) => Assembly.Load(data)))
            {
                patcher.AddPatchersFromDirectory(LoaderEnvironment.PatcherPluginPath);
                RLog.Msg($"{patcher.PatcherContext.PatcherPlugins.Count} patcher{(patcher.PatcherContext.PatcherPlugins.Count == 1 ? "" : "s")} loaded");

                patcher.LoadAssemblyDirectories(Il2CppInteropManager.IL2CPPInteropAssemblyPath);
                RLog.Msg($"{patcher.PatcherContext.PatcherPlugins.Count} assemblies discovered");
                patcher.PatchAndLoad();
            }

            SplashWindow.SetProgressSteps(2);
            Chainloader = new IL2CPPChainloader();
            Chainloader.Initialize();
        }
        catch (Exception error)
        {
            RLog.Error(error);
            throw;
        }
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return libraryName == "GameAssembly"
            ? NativeLibrary.Load(Il2CppInteropManager.GameAssemblyPath, assembly, searchPath)
            : IntPtr.Zero;
    }
}
