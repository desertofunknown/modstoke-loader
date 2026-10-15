using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Preloader.Core;

namespace BepInEx.Unity.IL2CPP;

internal static class UnityPreloaderRunner
{
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

    public static void PreloaderMain()
    {
        TraceBootstrap("UnityPreloaderRunner.PreloaderMain entered");
        var bepinPath =
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(EnvVars.DOORSTOP_INVOKE_DLL_PATH)));
        TraceBootstrap($"Computed BepInEx path: {bepinPath}");

        TraceBootstrap("Setting platform");
        PlatformUtils.SetPlatform();
        TraceBootstrap("Platform set");

        TraceBootstrap("Setting BepInEx paths");
        Paths.SetExecutablePath(EnvVars.DOORSTOP_PROCESS_PATH, bepinPath, EnvVars.DOORSTOP_MANAGED_FOLDER_DIR, false,
                                EnvVars.DOORSTOP_DLL_SEARCH_DIRS);
        TraceBootstrap($"BepInEx paths set. ManagedPath={Paths.ManagedPath}");

        // Cecil 0.11 requires one to manually set up list of trusted assemblies for assembly resolving
        // The main BCL path
        TraceBootstrap("Adding Cecil platform assemblies from ManagedPath");
        AppDomain.CurrentDomain.AddCecilPlatformAssemblies(Paths.ManagedPath);
        TraceBootstrap("Added Cecil platform assemblies from ManagedPath");
        // The parent path -> .NET has some extra managed DLLs in there
        TraceBootstrap("Adding Cecil platform assemblies from ManagedPath parent");
        AppDomain.CurrentDomain.AddCecilPlatformAssemblies(Path.GetDirectoryName(Paths.ManagedPath));
        TraceBootstrap("Added Cecil platform assemblies from ManagedPath parent");

        TraceBootstrap("Registering local assembly resolver");
        AppDomain.CurrentDomain.AssemblyResolve += LocalResolve;
        TraceBootstrap("Calling Preloader.Run");

        Preloader.Run();
        TraceBootstrap("Preloader.Run returned");
    }

    internal static Assembly LocalResolve(object sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name);

        var foundAssembly = AppDomain.CurrentDomain.GetAssemblies()
                                     .FirstOrDefault(x => x.GetName().Name == assemblyName.Name);

        if (foundAssembly != null)
            return foundAssembly;

        if (Utility.TryResolveDllAssembly(assemblyName, Paths.BepInExAssemblyDirectory, out foundAssembly)
         || Utility.TryResolveDllAssembly(assemblyName, Paths.PatcherPluginPath, out foundAssembly)
         || Utility.TryResolveDllAssembly(assemblyName, Paths.PluginPath, out foundAssembly))
            return foundAssembly;

        return null;
    }
}
