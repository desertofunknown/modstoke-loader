using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.IL2CPP.RuntimeFixes;
using BepInEx.Logging;
using BepInEx.Preloader.Core;
using BepInEx.Preloader.Core.Logging;
using BepInEx.Preloader.Core.Patching;
using BepInEx.Preloader.RuntimeFixes;
using BepInEx.Unity.Common;
using MonoMod.Utils;

namespace BepInEx.Unity.IL2CPP;

public static class Preloader
{
    private static PreloaderConsoleListener PreloaderLog { get; set; }

    internal static ManualLogSource Log => PreloaderLogger.Log;

    // TODO: This is not needed, maybe remove? (Instance is saved in IL2CPPChainloader itself)
    private static IL2CPPChainloader Chainloader { get; set; }

    private static void TraceBootstrap(string message)
    {
        try
        {
            var processPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
            var root = string.IsNullOrWhiteSpace(processPath)
                ? Environment.CurrentDirectory
                : Path.GetDirectoryName(processPath);
            var path = Path.Combine(root ?? Environment.CurrentDirectory, "openacai-bepinex-net10-bootstrap.log");
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void Run()
    {
        try
        {
            TraceBootstrap("Preloader.Run entered");
            TraceBootstrap("HarmonyBackendFix.Initialize");
            HarmonyBackendFix.Initialize();
            TraceBootstrap("ConsoleSetOutFix.Apply");
            ConsoleSetOutFix.Apply();
            TraceBootstrap("UnityInfo.Initialize");
            UnityInfo.Initialize(Paths.ExecutablePath, Paths.GameDataPath);

            TraceBootstrap("ConsoleManager.Initialize");
            ConsoleManager.Initialize(false, true);

            TraceBootstrap("Creating PreloaderConsoleListener");
            PreloaderLog = new PreloaderConsoleListener();
            TraceBootstrap("Adding PreloaderConsoleListener");
            Logger.Listeners.Add(PreloaderLog);

            if (ConsoleManager.ConsoleEnabled)
            {
                TraceBootstrap("Creating BepInEx console");
                ConsoleManager.CreateConsole();
                TraceBootstrap("Adding ConsoleLogListener");
                Logger.Listeners.Add(new ConsoleLogListener());
            }

            TraceBootstrap("RedirectStdErrFix.Apply");
            RedirectStdErrFix.Apply();

            TraceBootstrap("ChainloaderLogHelper.PrintLogInfo");
            ChainloaderLogHelper.PrintLogInfo(Log);

            TraceBootstrap("Writing runtime log lines");
            Logger.Log(LogLevel.Info, $"Running under Unity {UnityInfo.Version}");
            Logger.Log(LogLevel.Info, $"Runtime version: {Environment.Version}");
            Logger.Log(LogLevel.Info, $"Runtime information: {RuntimeInformation.FrameworkDescription}");

            Logger.Log(LogLevel.Debug, $"Game executable path: {Paths.ExecutablePath}");
            Logger.Log(LogLevel.Debug, $"Interop assembly directory: {Il2CppInteropManager.IL2CPPInteropAssemblyPath}");
            Logger.Log(LogLevel.Debug, $"BepInEx root path: {Paths.BepInExRootPath}");

            if (PlatformHelper.Is(Platform.Wine) && !Environment.Is64BitProcess)
            {
                if (!NativeLibrary.TryGetExport(NativeLibrary.Load("ntdll"), "RtlRestoreContext", out var _))
                {
                    Logger.Log(LogLevel.Warning,
                               "Your wine version doesn't support CoreCLR properly, expect crashes! Upgrade to wine 7.16 or higher.");
                }
            }

            TraceBootstrap("NativeLibrary.SetDllImportResolver");
            NativeLibrary.SetDllImportResolver(typeof(Il2CppInterop.Runtime.IL2CPP).Assembly, DllImportResolver);

            TraceBootstrap("Il2CppInteropManager.Initialize");
            Il2CppInteropManager.Initialize();
            TraceBootstrap("Il2CppInteropManager.Initialize returned");

            TraceBootstrap("Creating AssemblyPatcher");
            using (var assemblyPatcher = new AssemblyPatcher((data, _) => Assembly.Load(data)))
            {
                TraceBootstrap("Loading patchers");
                assemblyPatcher.AddPatchersFromDirectory(Paths.PatcherPluginPath);

                Log.LogInfo($"{assemblyPatcher.PatcherContext.PatcherPlugins.Count} patcher plugin{(assemblyPatcher.PatcherContext.PatcherPlugins.Count == 1 ? "" : "s")} loaded");

                TraceBootstrap("Loading interop assembly directories");
                assemblyPatcher.LoadAssemblyDirectories(Il2CppInteropManager.IL2CPPInteropAssemblyPath);

                Log.LogInfo($"{assemblyPatcher.PatcherContext.PatcherPlugins.Count} assemblies discovered");

                TraceBootstrap("PatchAndLoad");
                assemblyPatcher.PatchAndLoad();
                TraceBootstrap("PatchAndLoad returned");
            }


            TraceBootstrap("Removing PreloaderConsoleListener");
            Logger.Listeners.Remove(PreloaderLog);


            TraceBootstrap("Creating IL2CPPChainloader");
            Chainloader = new IL2CPPChainloader();

            TraceBootstrap("Initializing IL2CPPChainloader");
            Chainloader.Initialize();
            TraceBootstrap("IL2CPPChainloader.Initialize returned");
        }
        catch (Exception ex)
        {
            TraceBootstrap($"Preloader.Run exception: {ex}");
            Log.Log(LogLevel.Fatal, ex);

            throw;
        }
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "GameAssembly")
        {
            return NativeLibrary.Load(Il2CppInteropManager.GameAssemblyPath, assembly, searchPath);
        }

        return IntPtr.Zero;
    }
}
