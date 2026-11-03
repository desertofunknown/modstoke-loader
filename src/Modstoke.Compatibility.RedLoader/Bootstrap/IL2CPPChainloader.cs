// SPDX-License-Identifier: LGPL-2.1-only
// RedLoader IL2CPP chainloader contracts, derived from BepInEx, with Modstoke forwarding.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using Il2CppInterop.Runtime.InteropTypes;
using RedLoader.Bootstrap;
using RedLoader.Unity.IL2CPP.Utils;

namespace RedLoader.Unity.IL2CPP;

public class IL2CPPChainloader : BaseChainloader
{
    public static IL2CPPChainloader Instance { get; set; }

    /// <summary>Registers and adds a component to the compatibility manager.</summary>
    /// <typeparam name="T">Type of the component to add.</typeparam>
    public static T AddUnityComponent<T>() where T : Il2CppObjectBase => AddUnityComponent(typeof(T)).Cast<T>();

    /// <summary>Registers and adds a component to the compatibility manager.</summary>
    /// <param name="t">Type of the component to add.</param>
    public static Il2CppObjectBase AddUnityComponent(Type t) => Il2CppUtils.AddComponent(t);

    /// <summary>Initializes the chainloader and arms its native scene hook.</summary>
    /// <param name="gameExePath">Executable path, or null to retain the configured path.</param>
    public override void Initialize(string gameExePath = null)
    {
        base.Initialize(gameExePath);
        Instance = this;
        ChainloaderDriver.InstallRuntimeInvokeHook(Start);
    }

    private static void Start() => ChainloaderDriver.Start(() => ModProcessor, processor => ModProcessor = processor);

    protected override void InitializeLoggers() => base.InitializeLoggers();
}
