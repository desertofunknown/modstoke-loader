// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using PathologicalGames;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Maximum pickup components, including inactive ones, inspected under one entity.</summary>
        public const int MaximumPickupHierarchyEntries = 64;

        /// <summary>
        /// Copies the configuration of the pickup under an exact server-owned entity on the server game thread.
        /// Selects the component the native destroy receiver would find, or the only pickup when none is active.
        /// Field reads only: the ItemInstance getter and item data cache are never touched. Pickups without an
        /// entity, which use DisablePickup, are not covered. The quantity fields describe a configured range and
        /// modifiers, not what a collector received, and a snapshot is not evidence of a duplicate or violation.
        /// </summary>
        public static bool TryReadPickup(ulong networkId, out PickupSnapshot snapshot, out PickupReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckPickupContext();
                if (status != PickupReadStatus.Success)
                    return false;

                var entity = networkId == 0 ? null : BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                if (!PickupLive(entity))
                {
                    status = PickupReadStatus.EntityUnavailable;
                    return false;
                }
                if (!entity.isAttached)
                {
                    status = PickupReadStatus.NotAttached;
                    return false;
                }
                if (entity.networkId.PackedValue != networkId)
                {
                    status = PickupReadStatus.EntityUnavailable;
                    return false;
                }
                if (!entity.isOwner)
                {
                    status = PickupReadStatus.NotServerOwned;
                    return false;
                }
                var frame = Time.frameCount;
                var prefabId = entity.prefabId.Value;
                var root = entity.gameObject;
                if (!PickupLive(root))
                {
                    status = PickupReadStatus.EntityUnavailable;
                    return false;
                }

                var pickups = root.GetComponentsInChildren<PickUp>(true);
                if (pickups is null || pickups.WasCollected || pickups.Length == 0)
                {
                    status = PickupReadStatus.PickupUnavailable;
                    return false;
                }
                var total = pickups.Length;
                if (total > MaximumPickupHierarchyEntries)
                {
                    status = PickupReadStatus.AmbiguousHierarchy;
                    return false;
                }
                var native = root.GetComponentInChildren<PickUp>();
                if (native is not null && !PickupLive(native))
                {
                    status = PickupReadStatus.ContextChanged;
                    return false;
                }
                PickUp selected = null;
                var active = 0;
                for (var index = 0; index < total; index++)
                {
                    var candidate = pickups[index];
                    if (!PickupLive(candidate))
                    {
                        status = PickupReadStatus.ContextChanged;
                        return false;
                    }
                    if (candidate.gameObject.activeInHierarchy)
                        active++;
                    if (native is not null && PickupSame(candidate, native))
                        selected = candidate;
                }
                var matchesNative = selected is not null;
                if (selected is null)
                {
                    if (native is not null)
                    {
                        status = PickupReadStatus.ContextChanged;
                        return false;
                    }
                    if (total != 1)
                    {
                        status = PickupReadStatus.AmbiguousHierarchy;
                        return false;
                    }
                    selected = pickups[0];
                }

                var typeName = selected.GetIl2CppType().FullName;
                var kind = typeName == "Sons.Gameplay.PickUp" ? PickupKind.Ordinary : PickupKind.Derived;
                var componentItemId = selected._itemId;
                var instance = selected._ItemInstance_k__BackingField;
                int? instanceItemId = null;
                int? instanceModuleCount = null;
                if (PickupNative(instance))
                {
                    instanceItemId = instance._itemID;
                    var modules = instance._modules;
                    instanceModuleCount = PickupNative(modules) ? modules.Count : null;
                }
                var amount = selected._amount;
                int? amountMinimum = PickupNative(amount) ? amount._min : null;
                int? amountMaximum = PickupNative(amount) ? amount._max : null;
                var amountToAdd = selected._amountToAdd;
                var dynamicPickup = root.GetComponent<CoopDynamicPickUp>();
                int? bundleCount = PickupLive(dynamicPickup) ? dynamicPickup._bundleCount : null;
                var infinite = selected._infinite;
                var additional = selected._additionalItems;
                if (additional is not null && additional.WasCollected)
                {
                    status = PickupReadStatus.ContextChanged;
                    return false;
                }
                var additionalCount = additional is null ? 0 : additional.Count;
                var hasSpawnOnPickup = selected._spawnOnPickup != null || selected._spawnAfterPickupPrefab != null
                    || selected._spawnAfterPickupPrefab2 != null || selected._spawnSpiders;
                var remaining = selected._remainingItemCountNotCollected;
                var pendingDestroy = selected._pendingDestroyThroughBolt;
                var beingDestroyed = selected._isBeingDestroyed;
                var collected = selected._isCollected;
                var used = selected._Used_k__BackingField;
                var activeInHierarchy = selected.gameObject.activeInHierarchy;
                var destroyTarget = selected._destroyTarget;
                var pooled = IsPooledPickup(PickupLive(destroyTarget) ? destroyTarget : selected.gameObject);
                int? prefabItemId = ItemCatalog.TryReadPrefabItemId(prefabId, out var templateItemId, out _)
                    ? templateItemId : null;

                if (!PickupLive(entity) || !entity.isAttached || entity.networkId.PackedValue != networkId
                    || !entity.isOwner || !PickupSame(BoltNetwork.FindEntity(new Bolt.NetworkId(networkId)), entity)
                    || !PickupLive(selected) || !PickupSameOptional(root.GetComponentInChildren<PickUp>(), native)
                    || !PickupSameOptional(selected._ItemInstance_k__BackingField, instance)
                    || Time.frameCount != frame)
                {
                    status = PickupReadStatus.ContextChanged;
                    return false;
                }
                status = CheckPickupContext();
                if (status != PickupReadStatus.Success)
                    return false;

                snapshot = new PickupSnapshot(networkId, prefabId, true, typeName, kind, active, total, matchesNative,
                    componentItemId, prefabItemId, instance is not null, instanceItemId, instanceModuleCount,
                    amountMinimum, amountMaximum, amountToAdd, bundleCount, infinite, additionalCount, hasSpawnOnPickup,
                    remaining, pendingDestroy, beingDestroyed, collected, used, activeInHierarchy, pooled, frame);
                return true;
            }
            catch (Exception error)
            {
                status = PickupReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static PickupReadStatus CheckPickupContext() => CheckContext() switch
        {
            WorldReadStatus.Success => PickupReadStatus.Success,
            WorldReadStatus.WrongThread => PickupReadStatus.WrongThread,
            WorldReadStatus.NotServer => PickupReadStatus.NotServer,
            _ => PickupReadStatus.Unavailable
        };

        private static bool IsPooledPickup(GameObject target)
        {
            var pools = PoolManager.Pools;
            return pools != null && !pools.WasCollected && pools.TryGetValue("PickUps", out var pool)
                && PickupNative(pool) && PickupLive(target) && pool.IsSpawned(target.transform);
        }

        private static bool PickupNative(Il2CppObjectBase value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool PickupLive(UnityEngine.Object value) => PickupNative(value) && value;
        private static bool PickupSame(Il2CppObjectBase left, Il2CppObjectBase right)
            => PickupNative(left) && PickupNative(right) && left.Pointer == right.Pointer;
        private static bool PickupSameOptional(Il2CppObjectBase left, Il2CppObjectBase right)
            => left is null ? right is null : PickupSame(left, right);
    }
}
