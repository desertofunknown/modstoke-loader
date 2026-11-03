// SPDX-License-Identifier: GPL-3.0-or-later
// Chainloader lifecycle behavior adapted from BepInEx's BaseChainloader.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Modstoke.Preloader;

internal static class CompatibilityChainloaderLifecycle
{
    internal static void Initialize(string gameExePath, Action initializeLoggers, Action markInitialized)
    {
        if (gameExePath != null)
            Paths.SetExecutablePath(gameExePath);

        initializeLoggers();

        if (!Directory.Exists(Paths.PluginPath))
            Directory.CreateDirectory(Paths.PluginPath);

        if (!Directory.Exists(Paths.PatcherPluginPath))
            Directory.CreateDirectory(Paths.PatcherPluginPath);

        markInitialized();

        Logger.Log(LogLevel.Message, "Chainloader initialized");
    }

    internal static void InitializeLoggers(
        Func<string> consoleTitle,
        ConfigEntry<bool> diskLogging,
        ConfigEntry<LogLevel> diskLevels,
        ConfigEntry<bool> diskAppend,
        ConfigEntry<bool> diskFlush,
        ConfigEntry<int> diskFileLimit)
    {
        if (ConsoleManager.ConsoleEnabled && !ConsoleManager.ConsoleActive)
            ConsoleManager.CreateConsole();

        if (ConsoleManager.ConsoleActive)
        {
            if (!Logger.Listeners.Any(x => x is ConsoleLogListener))
                Logger.Listeners.Add(new ConsoleLogListener());

            ConsoleManager.SetConsoleTitle(consoleTitle());
        }

        if (diskLogging.Value)
            Logger.Listeners.Add(new global::BepInEx.Logging.DiskLogListener("LogOutput.log", diskLevels.Value,
                                                     diskAppend.Value, diskFlush.Value,
                                                     diskFileLimit.Value));

        if (!TraceLogSource.IsListening)
            Logger.Sources.Add(TraceLogSource.CreateSource());

        if (!Logger.Sources.Any(x => x is HarmonyLogSource))
            Logger.Sources.Add(new HarmonyLogSource());
    }

    internal static void Execute(
        Func<IList<PluginInfo>> discover,
        Action<IList<PluginInfo>> load,
        Action finished)
    {
        try
        {
            var plugins = discover();
            Logger.Log(LogLevel.Info, $"{plugins.Count} plugin{(plugins.Count == 1 ? "" : "s")} to load");
            load(plugins);
            finished();
        }
        catch (Exception ex)
        {
            try
            {
                if (ConsoleManager.ConsoleEnabled)
                    ConsoleManager.CreateConsole();
            }
            catch { }

            Logger.Log(LogLevel.Error, $"Error occurred loading plugins: {ex}");
        }

        Logger.Log(LogLevel.Message, "Chainloader startup complete");
    }
}
