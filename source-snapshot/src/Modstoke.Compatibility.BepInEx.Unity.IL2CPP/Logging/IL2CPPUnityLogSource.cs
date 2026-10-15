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
        var owner = Assembly.Load("OpenACAI.Endnight.Loader.Core")
                            .GetType("OpenACAI.Endnight.Loader.OpenACAIUnityLogSource", true);
        var register = owner.GetMethod("RegisterCompatibility", BindingFlags.NonPublic | BindingFlags.Static,
                                       null, new[] { typeof(Action<string, string, LogType>) }, null)
                       ?? throw new MissingMethodException(owner.FullName, "RegisterCompatibility");
        return (Action<Action<string, string, LogType>>)register.CreateDelegate(
            typeof(Action<Action<string, string, LogType>>));
    });

    public IL2CPPUnityLogSource() => Register.Value(UnityLogCallback);

    public string SourceName { get; } = "Unity";
    public event EventHandler<LogEventArgs> LogEvent;

    public void Dispose() { }

    public void UnityLogCallback(string logLine, string exception, LogType type)
    {
        var level = type switch
        {
            LogType.Error => LogLevel.Error,
            LogType.Assert => LogLevel.Debug,
            LogType.Warning => LogLevel.Warning,
            LogType.Log => LogLevel.Message,
            LogType.Exception => LogLevel.Error,
            _ => LogLevel.Message
        };
        LogEvent?.Invoke(this, new LogEventArgs(logLine, level, this));
    }
}
