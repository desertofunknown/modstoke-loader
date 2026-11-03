// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using HarmonyLib;
using RedLoader;
using SonsSdk;
using SonsSdk.Networking;
using UnityEngine;
using Color = System.Drawing.Color;

namespace Modstoke.Loader
{
    internal sealed class NetworkPrefabs : ISdkNetworkPrefabDriver
    {
        internal static readonly NetworkPrefabs Instance = new();
        private static readonly Dictionary<IntPtr, Registration> Registrations = new();
        private static bool _initialized;

        private sealed class Registration(BoltEntity entity)
        {
            internal readonly PrefabId Id = entity.prefabId;
            internal readonly GameObject Prefab = entity.gameObject;
            internal bool Published;
        }

        private NetworkPrefabs() { }

        public void Initialize()
        {
            if (_initialized)
                return;
            global::SonsSdk.SdkEntryPoint.Harmony.Patch(
                AccessTools.Method(typeof(PrefabDatabase), nameof(PrefabDatabase.UpdateLookup)),
                postfix: new(typeof(NetworkPrefabs), nameof(OnUpdateLookup)));
            _initialized = true;
        }

        public void Clear()
        {
            List<Exception> failures = null;
            foreach (var entity in EntityManager.RegisteredEntities.ToArray())
            {
                try { UnregisterPrefab(entity); }
                catch (Exception exception) { (failures ??= new()).Add(exception); }
            }
            if (failures != null)
                throw new AggregateException("Could not unregister all Bolt prefabs.", failures);
        }

        internal static void OnUpdateLookup()
        {
            foreach (var entity in EntityManager.RegisteredEntities.ToArray())
            {
                try
                {
                    if (!entity)
                    {
                        Instance.UnregisterPrefab(entity);
                        continue;
                    }
                    if (!Registrations.TryGetValue(entity.Pointer, out var registration))
                        continue;
                    if (!registration.Id.Equals(entity.prefabId))
                    {
                        var updated = new Registration(entity);
                        ValidateRegistration(updated, entity.Pointer);
                        RemoveLookup(registration);
                        Registrations[entity.Pointer] = updated;
                        registration = updated;
                    }
                    Publish(registration);
                }
                catch (Exception exception)
                {
                    try { RLog.Error($"Could not update a Bolt prefab registration: {exception}"); }
                    catch { }
                }
            }
            RLog.Msg(Color.DeepPink, "initialized bolt prefabs");
        }

        public void RegisterPrefab(BoltEntity entity, string serializer)
        {
            if (!entity)
                throw new ArgumentException("A live Bolt prefab is required.", nameof(entity));
            if (Registrations.ContainsKey(entity.Pointer))
                return;

            var registration = new Registration(entity);
            ValidateRegistration(registration, entity.Pointer);

            var previousSerializer = entity._serializerGuid;
            try
            {
                Registrations.Add(entity.Pointer, registration);
                EntityManager.RegisteredEntities.Add(entity);
                if (!string.IsNullOrEmpty(serializer))
                    entity._serializerGuid = serializer;
                if (BoltNetwork.isRunning)
                    Publish(registration);
            }
            catch
            {
                try { UnregisterPrefab(entity); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
                try { entity._serializerGuid = previousSerializer; }
                catch (Exception exception) { ReportCleanupFailure(exception); }
                throw;
            }
        }

        public void RegisterPrefab(GameObject gameObject, string serializer)
        {
            if (!gameObject)
                throw new ArgumentException("A live prefab is required.", nameof(gameObject));
            if (gameObject.TryGetComponent(out BoltEntity entity))
                RegisterPrefab(entity, serializer);
        }

        public void UnregisterPrefab(BoltEntity entity)
        {
            if (ReferenceEquals(entity, null))
            {
                EntityManager.RegisteredEntities.Remove(entity);
                return;
            }
            if (Registrations.TryGetValue(entity.Pointer, out var registration))
            {
                RemoveLookup(registration);
                Registrations.Remove(entity.Pointer);
            }
            EntityManager.RegisteredEntities.Remove(entity);
        }

        private static void ValidateRegistration(Registration registration, IntPtr entityPointer)
        {
            foreach (var existing in Registrations)
            {
                if (existing.Key != entityPointer && existing.Value.Id.Equals(registration.Id))
                    throw new InvalidOperationException($"Bolt prefab id {registration.Id} is already registered.");
            }
            ValidateLookup(registration);
        }

        private static void ValidateLookup(Registration registration)
        {
            var lookup = PrefabDatabase._lookup;
            if (lookup != null && lookup.TryGetValue(registration.Id, out var existing)
                && existing && existing.Pointer != registration.Prefab.Pointer)
                throw new InvalidOperationException($"Bolt prefab id {registration.Id} belongs to another prefab.");
        }

        private static void Publish(Registration registration)
        {
            ValidateLookup(registration);
            var lookup = PrefabDatabase._lookup;
            if (lookup == null)
                return;
            // Native database entries are borrowed, even if a mod explicitly registers one.
            var database = PrefabDatabase.Instance;
            if (database && database.Prefabs.Contains(registration.Prefab))
            {
                registration.Published = false;
                return;
            }
            if (lookup.TryGetValue(registration.Id, out var existing) && existing
                && existing.Pointer == registration.Prefab.Pointer)
                return;
            registration.Published = true;
            lookup[registration.Id] = registration.Prefab;
        }

        private static void RemoveLookup(Registration registration)
        {
            var lookup = PrefabDatabase._lookup;
            if (registration.Published && lookup != null
                && lookup.TryGetValue(registration.Id, out var existing)
                && !ReferenceEquals(existing, null) && existing.Pointer == registration.Prefab.Pointer)
                lookup.Remove(registration.Id);
            registration.Published = false;
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Error($"Could not roll back a Bolt prefab registration: {exception}"); }
            catch { }
        }
    }
}
