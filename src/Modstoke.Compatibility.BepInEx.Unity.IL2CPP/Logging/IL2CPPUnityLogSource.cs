// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx.Unity.IL2CPP.Logging;

public class IL2CPPUnityLogSource : ILogSource
{
    private static readonly Lazy<Action<Action<string, string, LogType>>> Register = new(() =>
    {
        var owner = Assembly.Load("Modstoke.Loader")
                            .GetType("Modstoke.Loader.UnityLogSource", true);
        var register = owner.GetMethod("RegisterCompatibility", BindingFlags.NonPublic | BindingFlags.Static,
                                       null, new[] { typeof(Action<string, string, LogType>) }, null)
                       ?? throw new MissingMethodException(owner.FullName, "RegisterCompatibility");
        return (Action<Action<string, string, LogType>>)register.CreateDelegate(
            typeof(Action<Action<string, string, LogType>>));
    });

    public IL2CPPUnityLogSource() => Register.Value(UnityLogCallback);

    public string SourceName { get; } = "Unity";
    public event EventHandler<LogEventArgs> LogEvent;

    /// <summary>Does not unregister the callback or stop runtime-owned log capture.</summary>
    public void Dispose() { }

    public void UnityLogCallback(string logLine, string exception, LogType type)
    {
        var level = type switch
        {
            LogType.Error or LogType.Assert or LogType.Exception => LogLevel.Error,
            LogType.Warning => LogLevel.Warning,
            LogType.Log => LogLevel.Message,
            _ => LogLevel.Message
        };
        var text = logLine ?? "";
        if ((level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != LogLevel.None
            && !string.IsNullOrWhiteSpace(exception))
            text += Environment.NewLine + exception;
        LogEvent?.Invoke(this, new LogEventArgs(text, level, this));
    }
}
