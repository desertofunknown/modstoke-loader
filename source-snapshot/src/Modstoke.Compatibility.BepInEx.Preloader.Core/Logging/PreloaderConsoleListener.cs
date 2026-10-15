// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace BepInEx.Preloader.Core.Logging;

/// <summary>Buffers preloader log events for replay after chainloader logging is initialized.</summary>
public class PreloaderConsoleListener : ILogListener
{
    private static readonly ConfigEntry<LogLevel> ConfigConsoleDisplayedLevel = ConfigFile.CoreConfig.Bind(
        "Logging.Console", "LogLevels",
        LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info,
        "Which log levels to show in the console output.");

    /// <summary>Gets the shared list of buffered preloader events.</summary>
    /// <remarks>Lock this list when reading or modifying it. Replay and disposal do not clear it.</remarks>
    public static List<LogEventArgs> LogEvents { get; } = new();

    /// <inheritdoc />
    public LogLevel LogLevelFilter => ConfigConsoleDisplayedLevel.Value;

    /// <inheritdoc />
    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        lock (LogEvents)
            LogEvents.Add(eventArgs);
    }

    /// <inheritdoc />
    public void Dispose() { }
}
