// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility networking adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using Bolt;
using SonsSdk.Networking;
using UnityEngine;

namespace Modstoke.Loader;

internal sealed class NetworkPrefabFallback : ISdkNetworkPrefabFallbackDriver
{
    private static readonly NetworkPrefabFallback Driver = new();

    internal static ISdkNetworkPrefabFallbackDriver Create() => Driver;

    public void Initialize() => NetworkPrefabs.Instance.Initialize();
    public void Clear() => NetworkPrefabs.Instance.Clear();
    public void OnUpdateLookup() => NetworkPrefabs.OnUpdateLookup();
    public void RegisterPrefab(BoltEntity entity, string serializer) =>
        NetworkPrefabs.Instance.RegisterPrefab(entity, serializer);
    public void RegisterPrefab(GameObject go, string serializer) =>
        NetworkPrefabs.Instance.RegisterPrefab(go, serializer);
    public void UnregisterPrefab(BoltEntity entity) =>
        NetworkPrefabs.Instance.UnregisterPrefab(entity);
}
