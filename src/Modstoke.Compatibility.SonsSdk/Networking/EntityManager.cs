using System.Reflection;
using Bolt;
using HarmonyLib;
using RedLoader;
using UnityEngine;
using Color = System.Drawing.Color;

namespace SonsSdk.Networking;

internal interface ISdkNetworkPrefabDriver
{
    void Initialize();
    void Clear();
    void RegisterPrefab(BoltEntity entity, string serializer);
    void RegisterPrefab(GameObject gameObject, string serializer);
    void UnregisterPrefab(BoltEntity entity);
}

internal interface ISdkNetworkPrefabFallbackDriver : ISdkNetworkPrefabDriver
{
    void OnUpdateLookup();
}

public static class EntityManager
{
    internal static readonly List<BoltEntity> RegisteredEntities = new();

    private static readonly Lazy<ISdkNetworkPrefabFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.NetworkPrefabFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkNetworkPrefabFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkNetworkPrefabFallbackDriver>)))();
    });
    internal static ISdkNetworkPrefabFallbackDriver Fallback => FallbackDriver.Value;

    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.Initialize();
            return;
        }

        Fallback.Initialize();
    }

    internal static void Uninit()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.Clear();
            return;
        }

        Fallback.Clear();
    }
    
    internal static void OnUpdateLookup()
    {
        Fallback.OnUpdateLookup();
    }
    
    /// <summary>
    /// Register a BoltEntity to Bolt.
    /// Optionally specify a serializer if the BoltEntity doesn't already have that filled in (See <see cref="BoltFactories"/>).
    /// </summary>
    /// <param name="entity">The BoltEntity to register</param>
    /// <param name="serializer">The optional serializer. If you don't use the overload here, make sure to assign it somewhere else before</param>
    public static void RegisterPrefab(BoltEntity entity, string serializer = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.RegisterPrefab(entity, serializer);
            return;
        }

        Fallback.RegisterPrefab(entity, serializer);
    }

    /// <summary>
    /// Register a BoltEntity to Bolt by getting the component from a GameObject.
    /// Optionally specify a serializer if the BoltEntity doesn't already have that filled in (See <see cref="BoltFactories"/>).
    /// </summary>
    /// <param name="go">The GameObject with the BoltEntity on it.</param>
    /// <param name="serializer">The optional serializer. If you don't use the overload here, make sure to assign it somewhere else before</param>
    public static void RegisterPrefab(GameObject go, string serializer = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.RegisterPrefab(go, serializer);
            return;
        }

        Fallback.RegisterPrefab(go, serializer);
    }
    
    public static void UnregisterPrefab(BoltEntity entity)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.UnregisterPrefab(entity);
            return;
        }

        Fallback.UnregisterPrefab(entity);
    }
}
