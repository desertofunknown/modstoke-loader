// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace RedLoader.Unity.IL2CPP;

internal static class Il2CppInteropManager
{
    private static readonly Lazy<Type> Owner = new(() => Assembly.Load("Modstoke.Preloader")
        .GetType("Modstoke.Preloader.RedInteropManager", true));
    private static readonly Lazy<Func<string>> GameAssemblyPathOwned = new(() => Bind<Func<string>>("GetGameAssemblyPath"));
    private static readonly Lazy<Func<string>> AssemblyPathOwned = new(() => Bind<Func<string>>("GetInteropAssemblyPath"));
    private static readonly Lazy<Action> InitializeOwned = new(() => Bind<Action>("Initialize"));
    private static readonly Lazy<Action> PreloadOwned = new(() => Bind<Action>("PreloadInteropAssemblies"));

    public static string GameAssemblyPath => GameAssemblyPathOwned.Value();

    internal static string IL2CPPInteropAssemblyPath => AssemblyPathOwned.Value();

    public static void Initialize() => InitializeOwned.Value();

    internal static void PreloadInteropAssemblies() => PreloadOwned.Value();

    private static T Bind<T>(string name) where T : Delegate
    {
        var type = Owner.Value;
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, name);
        return (T)method.CreateDelegate(typeof(T));
    }
}

public class HarmonyExceptionLogger : ILogger
{
    private const string HarmonyMessage = "During invoking native->managed trampoline";
            
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        var msg = formatter(state, exception);
            
        if (exception != null && msg.Equals(HarmonyMessage))
        {
            RLog.MsgDirect(System.Drawing.Color.Fuchsia, "====== HARMONY EXCEPTION ======");
            RLog.MsgDirect(System.Drawing.Color.IndianRed, exception.ToString());
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel == LogLevel.Error;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        throw new NotImplementedException();
    }
}

internal class InteropLogger : ILogger
{
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        RLog.Msg(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        #if DEBUG
        return true;
        #else
        return logLevel != LogLevel.Debug && logLevel != LogLevel.Trace;
        #endif
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        throw new NotImplementedException();
    }
}
