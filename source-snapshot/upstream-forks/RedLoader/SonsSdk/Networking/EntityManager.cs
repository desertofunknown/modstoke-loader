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

public static class EntityManager
{
    internal static readonly List<BoltEntity> RegisteredEntities = new();

    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.Initialize();
            return;
        }

        SdkEntryPoint.Harmony.Patch(
            AccessTools.Method(typeof(PrefabDatabase), nameof(PrefabDatabase.UpdateLookup)),
            postfix: new(typeof(EntityManager), nameof(OnUpdateLookup)));
    }

    internal static void Uninit()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.Clear();
            return;
        }

        RegisteredEntities.Clear();
    }
    
    private static void OnUpdateLookup()
    {
        foreach (var entity in RegisteredEntities)
        {
            var go = entity.gameObject;
            
            if (PrefabDatabase.Instance.Prefabs.Contains(go))
            {
                continue;
            }

            PrefabDatabase._lookup[entity.prefabId] = go;
        }

        RLog.Msg(Color.DeepPink, "initialized bolt prefabs");
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

        if(RegisteredEntities.Contains(entity))
            return;

        if (!string.IsNullOrEmpty(serializer))
        {
            entity._serializerGuid = serializer;
        }
        
        if (BoltNetwork.isRunning)
        {
            PrefabDatabase._lookup[entity.prefabId] = entity.gameObject;
        }
        
        RegisteredEntities.Add(entity);
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

        if (go.TryGetComponent(out BoltEntity entity))
            RegisterPrefab(entity, serializer);
    }
    
    public static void UnregisterPrefab(BoltEntity entity)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.NetworkPrefabs.UnregisterPrefab(entity);
            return;
        }

        RegisteredEntities.Remove(entity);
    }
}
