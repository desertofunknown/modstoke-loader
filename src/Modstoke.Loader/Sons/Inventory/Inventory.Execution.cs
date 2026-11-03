// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using PathologicalGames;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Items.Utils;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        private static bool _executingMutation;
        private readonly record struct InventoryMutationContext(int Load, bool Networked, BoltEntity Player);

        private static bool CanExecuteItemIdAction(InventoryActionKind kind)
            => kind is InventoryActionKind.AddItem
                or InventoryActionKind.RemoveItem or InventoryActionKind.EquipItem;

        /// <summary>
        /// Calls the current local inventory's native AddItem once. Preserves capacity,
        /// item modules, events and native auto-equip behavior. This is not remote-player
        /// authority or a transaction. False can follow a partial native change: inspect result.
        /// </summary>
        public static bool TryAddItem(int itemId, int quantity,
            out InventoryExecutionResult result, bool preventAutoEquip = true)
            => ExecuteInventoryAction(InventoryActionKind.AddItem, itemId, quantity,
                preventAutoEquip, out result);

        /// <summary>
        /// Uses native removal with amount overflow disabled and normal equipment/quick-select
        /// cleanup. Before/after counts are observations, not proof of transaction ownership.
        /// </summary>
        public static bool TryRemoveItem(int itemId, int quantity,
            out InventoryExecutionResult result)
            => ExecuteInventoryAction(InventoryActionKind.RemoveItem, itemId, quantity,
                true, out result);

        /// <summary>
        /// Requests normal native equipment of a locally owned item. A true return is native
        /// acceptance, not completed animation. Observe equipment slots for later presentation.
        /// </summary>
        public static bool TryEquipItem(int itemId, out InventoryExecutionResult result)
            => ExecuteInventoryAction(InventoryActionKind.EquipItem, itemId, 1,
                true, out result);

        private static bool ExecuteInventoryAction(InventoryActionKind kind,
            int itemId, int quantity, bool preventAutoEquip, out InventoryExecutionResult result)
        {
            result = new(kind, itemId, quantity, false, null, null, null, null, "Not attempted.");
            if (quantity <= 0 || !CanExecuteItemIdAction(kind))
            {
                result = result.WithDetail("A supported action and positive quantity are required.");
                return false;
            }
            if (!TryGetCurrentInventory(itemId, out var inventory, out var detail))
            {
                result = result.WithDetail(detail);
                return false;
            }
            if (_executingMutation)
            {
                result = result.WithDetail("An SDK inventory action is already executing; no action was queued.");
                return false;
            }

            int? before = null, after = null;
            bool? nativeResult = null, equipped = null;
            var attempted = false;
            _executingMutation = true;
            try
            {
                if (!TryGetMutationContext(inventory, itemId, out var context, out _, out detail))
                {
                    result = result.WithDetail(detail);
                    return false;
                }
                before = inventory.AmountOf(itemId, true, false);
                if (kind == InventoryActionKind.RemoveItem && before.Value < quantity
                    || kind == InventoryActionKind.EquipItem && !inventory.Owns(itemId))
                {
                    result = new(kind, itemId, quantity, false, null, before, before, null,
                        "The local inventory does not contain the requested item quantity.");
                    return false;
                }
                if (!MutationContextCurrent(inventory, in context, out detail))
                {
                    result = result.WithDetail("Local inventory changed before execution. " + detail);
                    return false;
                }

                attempted = true;
                nativeResult = kind switch
                {
                    InventoryActionKind.AddItem => inventory.AddItem(itemId, quantity,
                        preventAutoEquip, false, null),
                    InventoryActionKind.RemoveItem => inventory.RemoveItem(itemId, quantity,
                        false, true, true, null, false),
                    _ => inventory.TryEquip(itemId, false, true, false)
                };
                if (!MutationContextCurrent(inventory, in context, out detail))
                {
                    result = new(kind, itemId, quantity, attempted, nativeResult, before, null, null,
                        "Native call returned, but the local inventory changed. Do not automatically retry.");
                    return false;
                }
                after = inventory.AmountOf(itemId, true, false);
                equipped = inventory.TryGetEquipped(itemId, out _);
                result = new(kind, itemId, quantity, attempted, nativeResult, before, after, equipped,
                    nativeResult == true ? "Native action accepted; observations do not establish complete asynchronous effects or remote replication."
                        : "Native action returned false. Partial effects are possible; do not automatically retry.");
                return nativeResult == true;
            }
            catch (Exception error)
            {
                result = new(kind, itemId, quantity, attempted, nativeResult, before, after, equipped,
                    (attempted ? "Native action is unconfirmed; do not automatically retry. " : "No action was invoked. ") + error.Message);
                return false;
            }
            finally
            {
                _executingMutation = false;
            }
        }

        private static bool TryGetMutationContext(PlayerInventory inventory, int itemId,
            out InventoryMutationContext context, out ItemData data, out string detail)
        {
            context = default;
            data = null;
            detail = "";
            if (!LocalPlayer.IsInWorld || !GameBuild.IsRecognized
                || !TryGetExecutionItem(itemId, out data, out detail))
            {
                detail = "An in-world local player, recognized game and live item definition are required. " + detail;
                return false;
            }
            if (PlayerInventoryEnforcement.IsEquipmentAuthorityHeld(inventory))
            {
                detail = "The specialized player authority route holds this inventory; use its authorized transaction APIs.";
                return false;
            }
            var load = SaveLifecycle.State.BeforeLoadCount;
            var networked = BoltNetwork.isRunning;
            var player = networked ? LocalPlayer.Entity : null;
            if (networked && (player is null || player.WasCollected || !player || !player.isAttached || !player.isOwner))
            {
                detail = "Inventory actions require native local-player ownership, not a remote or migrated player.";
                return false;
            }
            context = new(load, networked, player);
            return true;
        }

        private static bool MutationContextCurrent(PlayerInventory inventory,
            in InventoryMutationContext context, out string detail)
        {
            if (!TryGetCurrentInventory(out var current, out detail)) return false;
            if (current.Pointer != inventory.Pointer || SaveLifecycle.State.BeforeLoadCount != context.Load
                || BoltNetwork.isRunning != context.Networked)
            {
                detail = "Local inventory or world identity changed.";
                return false;
            }
            if (context.Networked)
            {
                var player = context.Player;
                var currentPlayer = LocalPlayer.Entity;
                if (player is null || player.WasCollected || !player || !player.isAttached || !player.isOwner
                    || currentPlayer is null || currentPlayer.WasCollected || !currentPlayer
                    || currentPlayer.Pointer != player.Pointer)
                {
                    detail = "Local-player ownership changed.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Spawns a native pickup at an explicit world pose, offline or on the host only.
        /// It does not debit inventory or authorize a remote request. The game owns pickup
        /// pooling/network lifetime. A non-null result is not proof of delivery to other peers.
        /// No loop, placement search, global scan or automatic retry is performed.
        /// </summary>
        public static bool TrySpawnItem(int itemId, int quantity, Vector3 position, Quaternion rotation,
            out GameObject spawnedObject, out InventoryExecutionResult result,
            bool shouldDespawn = true)
        {
            spawnedObject = null;
            result = new(InventoryActionKind.SpawnItem, itemId, quantity,
                false, null, null, null, null, "Not attempted.");
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                result = result.WithDetail("Spawning requires the initialized Unity lifecycle thread.");
                return false;
            }
            if (_executingMutation || quantity <= 0 || !FiniteSpawnPose(position, rotation))
            {
                result = result.WithDetail("An idle SDK action, positive quantity and finite normalized world pose are required.");
                return false;
            }
            var attempted = false;
            _executingMutation = true;
            try
            {
                if (!GameBuild.IsRecognized || !GameRuntime.IsGameScene
                    || BoltNetwork.isRunning && !BoltNetwork.isServer
                    || !BoltNetwork.isRunning && !LocalPlayer.IsInWorld)
                {
                    result = result.WithDetail("Native pickup spawning requires a recognized offline world or running host; remote clients are not authorized by this API.");
                    return false;
                }
                var pools = PoolManager.Pools;
                if (pools is null || pools.WasCollected || !pools.TryGetValue("PickUps", out var pool)
                    || pool is null || pool.WasCollected || !pool)
                {
                    result = result.WithDetail("The world's native pickup service is not ready; no spawn was invoked.");
                    return false;
                }
                if (!TryGetExecutionItem(itemId, out var data, out var detail))
                {
                    result = result.WithDetail(detail);
                    return false;
                }
                if (!data._canBeSpawned || !data.PickupPrefab || !ItemUtils.IsValidForCurrentGameMode(data))
                {
                    result = result.WithDetail("This definition has no valid spawnable pickup in the current game mode.");
                    return false;
                }
                // Nullable<T> wrapper defaults must be real empty structs, not null wrappers.
                var noVelocity = new Il2CppSystem.Nullable<Vector3>();
                var noAngularVelocity = new Il2CppSystem.Nullable<Vector3>();
                attempted = true;
                spawnedObject = ItemUtils.SpawnItemPickup(data, position, rotation, Vector3.zero,
                    false, true, shouldDespawn, false, null, null, quantity,
                    noVelocity, noAngularVelocity, true, false);
                var live = spawnedObject is not null && !spawnedObject.WasCollected && spawnedObject;
                result = new(InventoryActionKind.SpawnItem, itemId, quantity,
                    true, live, null, null, null, live
                        ? "Native factory returned a live pickup. Remote delivery and pickup quantity semantics remain native-owned."
                        : "Native factory returned no live pickup. Do not automatically retry an unconfirmed spawn.");
                return live;
            }
            catch (Exception error)
            {
                result = new(InventoryActionKind.SpawnItem, itemId, quantity,
                    attempted, null, null, null, null,
                    (attempted ? "Spawn is unconfirmed; do not automatically retry. " : "No spawn was invoked. ") + error.Message);
                return false;
            }
            finally
            {
                _executingMutation = false;
            }
        }

        private static bool TryGetExecutionItem(int itemId, out ItemData data, out string detail)
        {
            data = null;
            detail = "The item definition is not present in the initialized native database.";
            var cache = ItemDatabaseManager._itemsCache;
            if (itemId <= 0 || cache is null || cache.WasCollected || !cache.TryGetValue(itemId, out data)
                || data is null || data.WasCollected || !data || data._id != itemId)
                return false;
            detail = "";
            return true;
        }

        private static bool FiniteSpawnPose(Vector3 position, Quaternion rotation)
            => float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
                && float.IsFinite(rotation.x) && float.IsFinite(rotation.y) && float.IsFinite(rotation.z)
                && float.IsFinite(rotation.w) && System.Math.Abs(Quaternion.Dot(rotation, rotation) - 1f) < 0.001f;
    }

    /// <summary>
    /// One invocation's observations, not an atomic transaction or remote acknowledgement.
    /// InvocationAttempted=true forbids assuming failure had no effects. Counts include
    /// currently equipped items and exclude next slots; they do not identify item instances.
    /// </summary>
    public sealed class InventoryExecutionResult
    {
        internal InventoryExecutionResult(InventoryActionKind kind,
            int itemId, int quantity, bool attempted, bool? nativeAccepted, int? before,
            int? after, bool? equipped, string detail)
        {
            Kind = kind;
            ItemId = itemId;
            RequestedQuantity = quantity;
            InvocationAttempted = attempted;
            NativeAccepted = nativeAccepted;
            BeforeCount = before;
            AfterCount = after;
            EquippedAfterCall = equipped;
            Detail = detail;
        }

        public InventoryActionKind Kind { get; }
        public int ItemId { get; }
        public int RequestedQuantity { get; }
        public bool InvocationAttempted { get; }
        public bool? NativeAccepted { get; }
        public int? BeforeCount { get; }
        public int? AfterCount { get; }
        public bool? EquippedAfterCall { get; }
        public string Detail { get; }

        internal InventoryExecutionResult WithDetail(string detail)
            => new(Kind, ItemId, RequestedQuantity, InvocationAttempted, NativeAccepted,
                BeforeCount, AfterCount, EquippedAfterCall, detail);
    }
}
