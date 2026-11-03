// SPDX-License-Identifier: LGPL-2.1-only
// RedLoader chainloader contracts, derived from BepInEx, with Modstoke forwarding.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using RedLoader.Utils;

namespace RedLoader.Bootstrap;

public abstract class BaseChainloader
{
    protected static readonly string CurrentAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;
    protected static readonly Version CurrentAssemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;

    protected virtual string ConsoleTitle => $"Modstoke Loader by OpenACAI - {LoaderEnvironment.GameExecutableName}";

    private bool _initialized;

    /// <summary>Occurs after all plugins are loaded.</summary>
    public event Action Finished;

    public virtual void Initialize(string gameExePath = null)
    {
        if (_initialized)
            throw new InvalidOperationException("Chainloader cannot be initialized multiple times");

        ChainloaderDriver.Initialize(gameExePath, InitializeLoggers, () => _initialized = true);
    }

    protected virtual void InitializeLoggers() => ChainloaderDriver.InitializeLoggers(() => ConsoleTitle);

    /// <summary>The processor shared by chainloader instances.</summary>
    protected static IModProcessor ModProcessor;

    /// <summary>Runs the mod processor and publishes completion.</summary>
    public virtual void Execute() => ChainloaderDriver.Execute(() => ModProcessor, () => Finished?.Invoke());
}
