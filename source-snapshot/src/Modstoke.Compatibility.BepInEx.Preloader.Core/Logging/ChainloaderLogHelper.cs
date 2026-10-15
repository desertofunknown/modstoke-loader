// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace BepInEx.Preloader.Core.Logging;

/// <summary>Provides the startup banner and buffered preloader log replay.</summary>
public static class ChainloaderLogHelper
{
    /// <summary>Logs loader branding, build information, and the current operating environment.</summary>
    /// <param name="log">The source used for the startup banner.</param>
    public static void PrintLogInfo(ManualLogSource log)
    {
        var bepinVersion = Paths.BepInExVersion;
        var consoleTitle = OpenACAIBranding.ConsoleTitle;
        log.Log(LogLevel.Message, $"{consoleTitle} ({File.GetLastWriteTime(Paths.ExecutablePath)})");
        log.Log(LogLevel.Message,
            $"BepInEx compatibility runtime {OpenACAIBranding.BepInExRuntimeVersion}; RedLoader/SonsSDK compatibility is handled by {OpenACAIBranding.LoaderName}");

        if (ConsoleManager.ConsoleActive)
            ConsoleManager.SetConsoleTitle(consoleTitle);

        if (!string.IsNullOrEmpty(bepinVersion.Build))
            log.Log(LogLevel.Message, $"Built from commit {bepinVersion.Build}");

        Logger.Log(LogLevel.Info, $"System platform: {GetPlatformString()}");
        Logger.Log(LogLevel.Info,
            $"Process bitness: {(PlatformUtils.ProcessIs64Bit ? "64-bit" : "32-bit")} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})");
    }

    private static string GetPlatformString()
    {
        var platform = RuntimeInformation.OSDescription.Trim();
        if (!string.IsNullOrEmpty(PlatformUtils.WineVersion))
            platform += $" (Wine {PlatformUtils.WineVersion})";

        return $"{platform} {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";
    }

    /// <summary>Replays a snapshot of the preloader events without clearing the shared buffer.</summary>
    /// <remarks>
    /// Call after removing the buffering listener. The first console listener is temporarily removed
    /// to avoid duplicate console output and is restored even if a replay listener throws.
    /// Events appended after the snapshot are available on the next replay.
    /// </remarks>
    public static void RewritePreloaderLogs()
    {
        LogEventArgs[] events;
        lock (PreloaderConsoleListener.LogEvents)
            events = PreloaderConsoleListener.LogEvents.ToArray();

        if (events.Length == 0)
            return;

        var consoleListener = Logger.Listeners.FirstOrDefault(listener => listener is ConsoleLogListener);
        var removed = consoleListener != null && Logger.Listeners.Remove(consoleListener);
        try
        {
            foreach (var logEvent in events)
                Logger.InternalLogEvent(PreloaderLogger.Log, logEvent);
        }
        finally
        {
            if (removed)
                Logger.Listeners.Add(consoleListener);
        }
    }
}
