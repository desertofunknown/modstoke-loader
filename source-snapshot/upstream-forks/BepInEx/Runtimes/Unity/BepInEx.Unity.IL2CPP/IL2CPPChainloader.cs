using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core;
using BepInEx.Preloader.Core.Logging;
using BepInEx.Unity.IL2CPP.Hook;
using BepInEx.Unity.IL2CPP.Logging;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Logger = BepInEx.Logging.Logger;

namespace BepInEx.Unity.IL2CPP;

public class IL2CPPChainloader : BaseChainloader<BasePlugin>
{
    private static RuntimeInvokeDetourDelegate originalInvoke;

    private static readonly ConfigEntry<bool> ConfigUnityLogging = ConfigFile.CoreConfig.Bind(
     "Logging", "UnityLogListening",
     true,
     "Enables showing unity log messages in the BepInEx logging system.");

    private static readonly ConfigEntry<bool> ConfigDiskWriteUnityLog = ConfigFile.CoreConfig.Bind(
     "Logging.Disk", "WriteUnityLog",
     false,
     "Include unity log messages in log file output.");


    private static INativeDetour RuntimeInvokeDetour { get; set; }

    public static IL2CPPChainloader Instance { get; set; }

    /// <summary>
    ///     Register and add a Unity Component (for example MonoBehaviour) into BepInEx global manager.
    ///     Automatically registers the type with Il2Cpp type system if it isn't initialised already.
    /// </summary>
    /// <typeparam name="T">Type of the component to add.</typeparam>
    public static T AddUnityComponent<T>() where T : Il2CppObjectBase => AddUnityComponent(typeof(T)).Cast<T>();

    /// <summary>
    ///     Register and add a Unity Component (for example MonoBehaviour) into BepInEx global manager.
    ///     Automatically registers the type with Il2Cpp type system if it isn't initialised already.
    /// </summary>
    /// <param name="t">Type of the component to add</param>
    public static Il2CppObjectBase AddUnityComponent(Type t) => Il2CppUtils.AddComponent(t);

    /// <summary>
    ///     Occurs after a plugin is instantiated and just before <see cref="BasePlugin.Load"/> is called.
    /// </summary>
    public event Action<PluginInfo, Assembly, BasePlugin> PluginLoad;

    /// <summary>
    ///     Raises the <see cref="PluginLoad"/> event before the plugin's <see cref="BasePlugin.Load"/> method is called.
    /// </summary>
    /// <param name="pluginInfo">Information about the plugin being loaded.</param>
    /// <param name="pluginAssembly">The assembly containing the plugin.</param>
    /// <param name="plugin">The instantiated plugin.</param>
    protected void OnPluginLoad(PluginInfo pluginInfo, Assembly pluginAssembly, BasePlugin plugin) =>
        PluginLoad?.Invoke(pluginInfo, pluginAssembly, plugin);

    public override void Initialize(string gameExePath = null)
    {
        base.Initialize(gameExePath);
        Instance = this;
        OpenACAIIL2CPPHookStatus.MarkInitializing();

        if (!NativeLibrary.TryLoad("GameAssembly", typeof(IL2CPPChainloader).Assembly, null, out var il2CppHandle))
        {
            OpenACAIIL2CPPHookStatus.MarkGameAssemblyMissing(
                "Could not locate Il2Cpp game assembly (GameAssembly.dll, UserAssembly.dll or libil2cpp.so).");
            Logger.Log(LogLevel.Fatal,
                       "Could not locate Il2Cpp game assembly (GameAssembly.dll, UserAssembly.dll or libil2cpp.so). The game might be obfuscated or use a yet unsupported build of Unity.");
            return;
        }

        OpenACAIIL2CPPHookStatus.MarkGameAssemblyLoaded();

        if (!NativeLibrary.TryGetExport(il2CppHandle, "il2cpp_runtime_invoke", out var runtimeInvokePtr))
        {
            OpenACAIIL2CPPHookStatus.MarkRuntimeInvokeMissing(
                "Could not locate il2cpp_runtime_invoke in the Il2Cpp game assembly. IL2CPP plugin loading is disabled for this run.");
            Logger.Log(LogLevel.Fatal,
                       "Could not locate il2cpp_runtime_invoke in the Il2Cpp game assembly. IL2CPP plugin loading is disabled for this run.");
            return;
        }

        OpenACAIIL2CPPHookStatus.MarkRuntimeInvokeExportFound(runtimeInvokePtr);
        PreloaderLogger.Log.Log(LogLevel.Debug, $"Runtime invoke pointer: 0x{runtimeInvokePtr.ToInt64():X}");
        RuntimeInvokeDetourDelegate invokeMethodDetour = OnInvokeMethod;

        try
        {
            RuntimeInvokeDetour =
                INativeDetour.CreateAndApply(runtimeInvokePtr, invokeMethodDetour, out originalInvoke);
            PreloaderLogger.Log.Log(LogLevel.Debug, "Runtime invoke patched");
            OpenACAIIL2CPPHookStatus.MarkRuntimeInvokePatched(runtimeInvokePtr);
        }
        catch (Exception ex)
        {
            originalInvoke = null;
            RuntimeInvokeDetour = null;
            OpenACAIIL2CPPHookStatus.MarkRuntimeInvokePatchFailed(ex);
            Logger.Log(LogLevel.Fatal,
                       "Unable to patch il2cpp_runtime_invoke. IL2CPP plugin loading is disabled for this run.");
            Logger.Log(LogLevel.Error, ex);
        }
    }

    private static IntPtr OnInvokeMethod(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
    {
        var methodName = Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name(method));

        var unhook = false;

        if (methodName == "Internal_ActiveSceneChanged")
            try
            {
                OpenACAIIL2CPPHookStatus.MarkChainloaderExecuting(methodName);

                if (ConfigUnityLogging.Value)
                {
                    Logger.Sources.Add(new IL2CPPUnityLogSource());

                    Application.CallLogCallback("Test call after applying unity logging hook", "", LogType.Assert,
                                                true);
                }

                unhook = true;

                Il2CppInteropManager.PreloadInteropAssemblies();

                Instance.Execute();
                OpenACAIIL2CPPHookStatus.MarkChainloaderExecuted();
            }
            catch (Exception ex)
            {
                OpenACAIIL2CPPHookStatus.MarkChainloaderFailed(ex);
                Logger.Log(LogLevel.Fatal, "Unable to execute IL2CPP chainloader");
                Logger.Log(LogLevel.Error, ex);
            }

        var result = originalInvoke?.Invoke(method, obj, parameters, exc) ?? IntPtr.Zero;

        if (unhook)
        {
            TryDisposeRuntimeInvokeDetour();
        }

        return result;
    }

    private static void TryDisposeRuntimeInvokeDetour()
    {
        var detour = RuntimeInvokeDetour;
        RuntimeInvokeDetour = null;
        originalInvoke = null;

        if (detour == null) return;

        try
        {
            detour.Dispose();
            PreloaderLogger.Log.Log(LogLevel.Debug, "Runtime invoke unpatched");
            OpenACAIIL2CPPHookStatus.MarkRuntimeInvokeUnpatched();
        }
        catch (Exception ex)
        {
            OpenACAIIL2CPPHookStatus.MarkRuntimeInvokeCleanupFailed(ex);
            Logger.Log(LogLevel.Warning, "Runtime invoke detour cleanup failed");
            Logger.Log(LogLevel.Warning, ex);
        }
    }

    protected override void InitializeLoggers()
    {
        base.InitializeLoggers();

        if (!ConfigDiskWriteUnityLog.Value) DiskLogListener.BlacklistedSources.Add("Unity");

        ChainloaderLogHelper.RewritePreloaderLogs();

        Logger.Sources.Add(new IL2CPPLogSource());
    }

    public override BasePlugin LoadPlugin(PluginInfo pluginInfo, Assembly pluginAssembly)
    {
        var type = pluginAssembly.GetType(pluginInfo.TypeName);

        var pluginInstance = (BasePlugin) Activator.CreateInstance(type);

        OnPluginLoad(pluginInfo, pluginAssembly, pluginInstance);
        pluginInstance.Load();

        return pluginInstance;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RuntimeInvokeDetourDelegate(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);
}
