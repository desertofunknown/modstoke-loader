// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System.Reflection;
using RedLoader;
using RedLoader.Bootstrap;

namespace SonsSdk;

internal interface ISdkEntryPointDriver
{
    List<ModBase> LoadPlugins();
    List<ModBase> TryLoadFromPath(string assemblyPath);
    List<ModBase> TryLoadFromPath(string assemblyPath, string manifestPath);
    void InitAfterUnity(bool createModManagerUi);
    void OnApplicationLateStart();
    void OnSdkInitialized();
    void OnGameStart();
    void OnCheatsEnabledChanged(bool isEnabled);
}

public class SdkEntryPoint : IModProcessor
{
    internal static HarmonyLib.Harmony Harmony = new("sonssdk_harmony");
    private static readonly Lazy<Type> Owner = new(() => Assembly.Load("Modstoke.Loader")
        .GetType("Modstoke.Loader.SdkEntryPoint", true));
    private static readonly Lazy<Func<SdkEntryPoint, ISdkEntryPointDriver>> CreateDriver = new(() =>
        Bind<Func<SdkEntryPoint, ISdkEntryPointDriver>>("Create"));
    private static readonly Lazy<ResolveEventHandler> ResolveOwned = new(() =>
        Bind<ResolveEventHandler>("ResolveModDependency"));
    private ISdkEntryPointDriver _driver;
    private ISdkEntryPointDriver Driver => _driver ??= CreateDriver.Value(this);

    public List<ModBase> LoadPlugins() => Driver.LoadPlugins();

    public List<ModBase> TryLoadFromPath(string assemblyPath) => Driver.TryLoadFromPath(assemblyPath);

    /// <summary>Loads a mod using the manifest selected by the hosting loader.</summary>
    /// <param name="assemblyPath">The mod assembly to load.</param>
    /// <param name="manifestPath">The selected manifest, or null to use the legacy location.</param>
    /// <returns>The accepted mod instances, or an empty list when loading is rejected.</returns>
    public List<ModBase> TryLoadFromPath(string assemblyPath, string manifestPath) =>
        Driver.TryLoadFromPath(assemblyPath, manifestPath);

    public void InitAfterUnity() => InitAfterUnity(createModManagerUi: true);

    /// <summary>
    /// Initializes the SDK with optional legacy mod-manager UI. Shared UI and settings remain available.
    /// </summary>
    /// <param name="createModManagerUi">Whether to create the legacy manager, mod indicator and menu buttons.</param>
    public void InitAfterUnity(bool createModManagerUi) => Driver.InitAfterUnity(createModManagerUi);

    // Retain the SDK method/target pairs used by event subscription and deduplication.
    internal void OnAppLateStart() => Driver.OnApplicationLateStart();
    internal void OnSdkInitialized() => Driver.OnSdkInitialized();
    internal void OnGameStart() => Driver.OnGameStart();
    internal void OnCheatsEnabledChanged(bool isEnabled) => Driver.OnCheatsEnabledChanged(isEnabled);
    internal static Assembly ResolveModDependency(object sender, ResolveEventArgs args) => ResolveOwned.Value(sender, args);

    private static T Bind<T>(string name) where T : Delegate
    {
        var type = Owner.Value;
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, name);
        return (T)method.CreateDelegate(typeof(T));
    }
}
