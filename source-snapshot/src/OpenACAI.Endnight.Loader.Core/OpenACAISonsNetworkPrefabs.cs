// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Bolt;
using HarmonyLib;
using RedLoader;
using SonsSdk;
using SonsSdk.Networking;
using UnityEngine;
using Color = System.Drawing.Color;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsNetworkPrefabs : ISdkNetworkPrefabDriver
    {
        internal static readonly OpenACAISonsNetworkPrefabs Instance = new();

        private OpenACAISonsNetworkPrefabs() { }

        public void Initialize()
        {
            SdkEntryPoint.Harmony.Patch(
                AccessTools.Method(typeof(PrefabDatabase), nameof(PrefabDatabase.UpdateLookup)),
                postfix: new(typeof(OpenACAISonsNetworkPrefabs), nameof(OnUpdateLookup)));
        }

        public void Clear()
        {
            EntityManager.RegisteredEntities.Clear();
        }

        private static void OnUpdateLookup()
        {
            foreach (var entity in EntityManager.RegisteredEntities)
            {
                var gameObject = entity.gameObject;
                if (PrefabDatabase.Instance.Prefabs.Contains(gameObject))
                    continue;

                PrefabDatabase._lookup[entity.prefabId] = gameObject;
            }
            RLog.Msg(Color.DeepPink, "initialized bolt prefabs");
        }

        public void RegisterPrefab(BoltEntity entity, string serializer)
        {
            if (EntityManager.RegisteredEntities.Contains(entity))
                return;

            if (!string.IsNullOrEmpty(serializer))
                entity._serializerGuid = serializer;

            if (BoltNetwork.isRunning)
                PrefabDatabase._lookup[entity.prefabId] = entity.gameObject;

            EntityManager.RegisteredEntities.Add(entity);
        }

        public void RegisterPrefab(GameObject gameObject, string serializer)
        {
            if (gameObject.TryGetComponent(out BoltEntity entity))
                RegisterPrefab(entity, serializer);
        }

        public void UnregisterPrefab(BoltEntity entity)
        {
            EntityManager.RegisteredEntities.Remove(entity);
        }
    }
}
