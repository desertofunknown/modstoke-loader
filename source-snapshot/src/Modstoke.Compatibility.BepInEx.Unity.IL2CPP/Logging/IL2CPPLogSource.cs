// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using BepInEx.Logging;

namespace BepInEx.Unity.IL2CPP.Logging;

public class IL2CPPLogSource : ILogSource
{
    private static readonly Lazy<Action<Action<string>>> Register = new(() =>
    {
        var owner = Assembly.Load("OpenACAI.Endnight.Loader.Preloader")
                            .GetType("OpenACAI.Endnight.Loader.Preloader.OpenACAIIL2CPPLogSource", true);
        var register = owner.GetMethod("RegisterCompatibility", BindingFlags.NonPublic | BindingFlags.Static,
                                       null, new[] { typeof(Action<string>) }, null)
                       ?? throw new MissingMethodException(owner.FullName, "RegisterCompatibility");
        return (Action<Action<string>>)register.CreateDelegate(typeof(Action<Action<string>>));
    });

    public IL2CPPLogSource() => Register.Value(IL2CPPLogCallback);

    public string SourceName { get; } = "IL2CPP";
    public event EventHandler<LogEventArgs> LogEvent;

    public void Dispose() { }

    private void IL2CPPLogCallback(string message) =>
        LogEvent?.Invoke(this, new LogEventArgs(message, LogLevel.Message, this));
}
