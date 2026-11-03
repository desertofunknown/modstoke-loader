// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;

namespace RedLoader.Bootstrap;

internal static class ChainloaderDriver
{
    private static readonly Lazy<BootstrapBindings> Bootstrap = new(() => new BootstrapBindings());
    private static readonly Lazy<RuntimeBindings> Runtime = new(() => new RuntimeBindings());

    internal static void Initialize(string gameExePath, Action initializeLoggers, Action markInitialized) =>
        Bootstrap.Value.Initialize(gameExePath, initializeLoggers, markInitialized);

    internal static void InitializeLoggers(Func<string> consoleTitle) => Bootstrap.Value.InitializeLoggers(consoleTitle);
    internal static void InstallRuntimeInvokeHook(Action start) => Bootstrap.Value.InstallRuntimeInvokeHook(start);
    internal static void Execute(Func<IModProcessor> getProcessor, Action finished) => Runtime.Value.Execute(getProcessor, finished);
    internal static void Start(Func<IModProcessor> getProcessor, Action<IModProcessor> setProcessor) =>
        Runtime.Value.Start(getProcessor, setProcessor);

    private sealed class BootstrapBindings
    {
        internal readonly Action<string, Action, Action> Initialize;
        internal readonly Action<Func<string>> InitializeLoggers;
        internal readonly Action<Action> InstallRuntimeInvokeHook;

        internal BootstrapBindings()
        {
            var owner = Assembly.Load("Modstoke.Preloader")
                .GetType("Modstoke.Preloader.RedChainloaderBootstrap", true);
            Initialize = Bind<Action<string, Action, Action>>(owner, "Initialize", typeof(string), typeof(Action), typeof(Action));
            InitializeLoggers = Bind<Action<Func<string>>>(owner, "InitializeLoggers", typeof(Func<string>));
            InstallRuntimeInvokeHook = Bind<Action<Action>>(owner, "InstallRuntimeInvokeHook", typeof(Action));
        }
    }

    // Arming the native hook must not load Core or resolve its game-facing types.
    private sealed class RuntimeBindings
    {
        internal readonly Action<Func<IModProcessor>, Action> Execute;
        internal readonly Action<Func<IModProcessor>, Action<IModProcessor>> Start;

        internal RuntimeBindings()
        {
            var owner = Assembly.Load("Modstoke.Loader")
                .GetType("Modstoke.Loader.RedChainloaderLifecycle", true);
            Execute = Bind<Action<Func<IModProcessor>, Action>>(owner, "Execute", typeof(Func<IModProcessor>), typeof(Action));
            Start = Bind<Action<Func<IModProcessor>, Action<IModProcessor>>>(owner, "Start", typeof(Func<IModProcessor>), typeof(Action<IModProcessor>));
        }
    }

    private static T Bind<T>(Type owner, string name, params Type[] parameters) where T : Delegate
    {
        var method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, parameters, null) ?? throw new MissingMethodException(owner.FullName, name);
        return method.CreateDelegate<T>();
    }
}
