// SPDX-License-Identifier: GPL-3.0-or-later
// Chainloader execution adapted from RedLoader, derived from BepInEx.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RedLoader;
using RedLoader.Bootstrap;
using RedLoader.Scripting;
using RedLoader.Support;
using RedLoader.Support.Preferences;
using RedLoader.Unity.IL2CPP;
using RedLoader.Unity.IL2CPP.UnityEngine;
using RedLoader.Utils;

namespace Modstoke.Loader;

internal static class RedChainloaderLifecycle
{
    internal static void Execute(Func<IModProcessor> getProcessor, Action finished)
    {
        try
        {
            var plugins = getProcessor().LoadPlugins();
            foreach (var plugin in plugins)
                RegisterTypeInIl2Cpp.RegisterAssembly(plugin.ModAssembly);

            RLog.Msg($"{plugins.Count} plugin{(plugins.Count == 1 ? "" : "s")} to load");
            finished();
        }
        catch (Exception error)
        {
            try
            {
                if (ConsoleManager.ConsoleEnabled)
                    ConsoleManager.CreateConsole();
            }
            catch { }

            RLog.Error($"Error occurred loading plugins: {error}");
        }

        RLog.Msg("Chainloader startup complete");
    }

    internal static void Start(Func<IModProcessor> getProcessor, Action<IModProcessor> setProcessor)
    {
        RedLoader.Unity.IL2CPP.Il2CppInteropManager.PreloadInteropAssemblies();
        SplashWindow.SetProgressSteps(3);

        SceneHandler.Init();
        GlobalBehaviour.Init();

        var sdk = Assembly.LoadFrom(Path.Combine(LoaderEnvironment.LoaderAssemblyDirectory, "SonsSdk.dll"));
        var processorType = sdk.DefinedTypes.First(type => typeof(IModProcessor).IsAssignableFrom(type));
        setProcessor((IModProcessor)Activator.CreateInstance(processorType));

        IL2CPPChainloader.Instance.Execute();
        SplashWindow.SetProgressSteps(4);

        GlobalEvents.OnApplicationStart.Invoke();
        RegisterTypeInIl2Cpp.SetReady();
        GlobalEvents.MelonHarmonyInit.Invoke();
        UnityMappers.RegisterMappers();

        if (CorePreferences.RedirectUnityLogs.Value)
            UnityPatches.CreateAndApply();

        getProcessor().InitAfterUnity();
        SplashWindow.SetProgressSteps(5);

        if (CorePreferences.EnableScriptLoader.Value)
            RedScriptManager.Init();
    }
}
