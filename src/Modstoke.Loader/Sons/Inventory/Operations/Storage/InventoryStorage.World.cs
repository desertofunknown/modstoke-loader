// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Math = System.Math;
using Bolt;
using PathologicalGames;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using TheForest.Items.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        private PickUp _pickup;
        private CoopDynamicPickUp _dynamicPickup;
        private ItemInstance _pickupItem;
        private Vector3 _dropPosition;
        private Quaternion _dropRotation;
        private Transform _dropPrefab;

        /// <summary>
        /// Quiet drop only: no pre-spawned object, velocity, torque, kinematic state or bundle.
        /// The bound server geometry authorizes placement; the integrator selects the exact player item key.
        /// </summary>
        internal static bool TryReserveDrop(PlayerBridge bridge,
            PlayerInventoryAuthority authority, in PlayerInventoryFrame reservedRemoval,
            DropItem request, out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "Only one plain inventory item dropped through its native pickup prefab is supported.";
            try
            {
                if (!TryPrepare(bridge, authority, in reservedRemoval, TransferKind.Drop, out var candidate, out detail)
                    || !candidate.SourceMatches(request) || request.ItemCount != 1
                    || request.GetPreSpawnedNetworkIdValue().PackedValue != 0 || request.PreSpawned != null
                    || request.IsKinematic || request.AvoidImpacts || !request.ShouldDespawn
                    || !request.Velocity.Equals(Vector3.zero) || !request.Torque.Equals(Vector3.zero)
                    || !Finite(request.Position) || !Finite(request.Rotation)
                    || !Plain(request.ItemInstance?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance, reservedRemoval.ItemId))
                    return false;
                var prefab = candidate.Item.Data.PickupPrefab;
                if (!Live(prefab) || !ItemUtils.IsValidForCurrentGameMode(candidate.Item.Data)
                    || PoolManager.Pools == null || PoolManager.Pools.WasCollected
                    || !PoolManager.Pools.TryGetValue("PickUps", out var pool) || !Live(pool)) return false;
                var prefabEntity = prefab.GetComponent<BoltEntity>();
                var pickup = prefab.GetComponentInChildren<PickUp>();
                var dynamicPickup = prefab.GetComponent<CoopDynamicPickUp>();
                if (!Live(prefabEntity) || prefabEntity.prefabId.Value != request.PrefabId.Value
                    || !Live(dynamicPickup) || !PlainPickupShape(pickup, reservedRemoval.ItemId)) return false;
                candidate._dropPrefab = prefab;
                candidate._dropPosition = request.Position;
                candidate._dropRotation = request.Rotation;
                if (!candidate.AccessCurrent() || !candidate.Register()) return false;
                transfer = candidate;
                detail = candidate.Detail;
                return true;
            }
            catch (Exception error) { detail = "Drop reservation failed: " + error.Message; return false; }
        }

        private bool CapturePickup()
        {
            _pickup = _world.GetComponentInChildren<PickUp>();
            _dynamicPickup = _world.GetComponent<CoopDynamicPickUp>();
            if (!PlainPickupShape(_pickup, Action.ItemId) || !Live(_dynamicPickup)) return false;
            _pickupItem = _pickup._ItemInstance_k__BackingField;
            return PickupCurrent() && (Kind == TransferKind.Drop || CapturePickupInteraction());
        }

        private bool PickupCurrent()
            => WorldCurrent() && PlainPickupShape(_pickup, Action.ItemId) && Live(_dynamicPickup)
                && Same(_world.GetComponentInChildren<PickUp>(), _pickup)
                && Same(_world.GetComponent<CoopDynamicPickUp>(), _dynamicPickup)
                && Same(_pickup.gameObject, _worldObject) && _worldObject.activeInHierarchy && !_pickup._forceDisabled
                && Same(_pickup._destroyTarget, _worldObject) && Same(_dynamicPickup.entity, _world)
                && (_pickup._cachedBoltEntity is null || Same(_pickup._cachedBoltEntity, _world))
                && _dynamicPickup._bundleCount is >= 0 and <= 1 && IsUnpooledPickup(_pickup)
                && !_pickup._pendingDestroyThroughBolt && !_pickup._isBeingDestroyed && !_pickup._isCollected
                && !_pickup._Used_k__BackingField && _pickup._remainingItemCountNotCollected == 0
                && Same(_pickup._ItemInstance_k__BackingField, _pickupItem)
                && (IsWorldVariant ? Same(_variant.Donor.Native, _pickupItem) && _variant.Donor.Current()
                    : Plain(_pickupItem, Action.ItemId))
                && PlayerInventoryBaseline.JsonEqual(Modules,
                    PlayerInventoryBaseline.CaptureModules(_pickupItem));

        private static bool PlainPickupShape(PickUp pickup, int id)
        {
            if (!Live(pickup) || pickup.GetIl2CppType().FullName != "Sons.Gameplay.PickUp" || pickup._itemId != id
                || pickup._infinite || pickup._disableInsteadOfDestroy || pickup._destroyIfAlreadyOwned
                || pickup._destroyIfFull || pickup._forceAutoEquip || pickup._needsFullBodyInteraction
                || pickup._poolManagerDespawnCreature
                || pickup._spawnSpiders || pickup._spawnOnPickup != null || pickup._spawnAfterPickupPrefab != null
                || pickup._spawnAfterPickupPrefab2 != null || pickup._spawnAfterPickupTr != null
                || pickup._destroyTarget != null && !Same(pickup._destroyTarget, pickup.gameObject)
                || pickup._amount == null || pickup._amount.WasCollected
                || pickup._amount._min != 1 || pickup._amount._max != 1) return false;
            var additional = pickup._additionalItems;
            return additional == null || !additional.WasCollected && additional.Count == 0;
        }

        private static bool IsUnpooledPickup(PickUp pickup)
        {
            // TryDespawnToPool checks actual PickUps membership even when the creature-pool
            // flag is false. A prefab flag or pool name alone does not establish lifetime.
            var pools = PoolManager.Pools;
            return pools != null && !pools.WasCollected && pools.TryGetValue("PickUps", out var pool)
                && Live(pool) && !pool.IsSpawned(pickup._destroyTarget.transform);
        }

        private bool TakePickup(out string detail)
        {
            detail = Detail;
            if (!PickupCurrent() || !AccessCurrent()) return false;
            // The qualified unpooled branch of OnEvent(DestroyPickUp) retires this exact
            // entity with BoltNetwork.Destroy. Do not call DestroyThroughBolt: that would
            // send another request and can trigger a fake-drop refund on the old actor.
            State = TransferState.TakingSource;
            BoltNetwork.Destroy(_worldObject);
            State = TransferState.SourceRetiring;
            Detail = detail = "Pickup retirement requested once; grant waits for exact source destruction.";
            return ObservePickupRetirement(out detail);
        }

        internal bool TryObservePickupRetirement(out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || Kind != TransferKind.Pickup
                || State != TransferState.SourceRetiring) return false;
            _busy = true;
            try { return ObservePickupRetirement(out detail); }
            catch (Exception error) { return Uncertain("Pickup retirement is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        private bool ObservePickupRetirement(out string detail)
        {
            detail = Detail;
            if (!Current()) return Uncertain("Actor retired while its pickup source was being destroyed.", out detail);
            var found = BoltNetwork.FindEntity(new NetworkId(_worldId));
            if (found is not null && !found.WasCollected && found)
            {
                if (!Same(found, _world)) return Uncertain("Pickup network identity was reused.", out detail);
                return false;
            }
            // A detached entity or inactive/pool-returned object alone is not source destruction.
            if (_worldObject is null || _worldObject.WasCollected)
                return Uncertain("Pickup wrapper ended before source destruction could be observed.", out detail);
            if (_worldObject) return false;
            State = TransferState.SourceTaken;
            Detail = detail = "Exact pickup source destroyed; one reserved native grant may now be sent.";
            return true;
        }

        internal bool TryApplyDrop(in PlayerInventoryFrame removalApplied, out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || Kind != TransferKind.Drop
                || State != TransferState.RemovalRequested || !RemovalReceipt(removalApplied))
                return false;
            _busy = true;
            try
            {
                if (!Current() || !ItemCurrent() || !AccessCurrent() || !Live(_dropPrefab) || !Same(Item.Data.PickupPrefab, _dropPrefab)
                    || !ItemUtils.IsValidForCurrentGameMode(Item.Data))
                    return Uncertain("Drop definition or actor changed after the native player removal.", out detail);
                State = TransferState.ApplyingDestination;
                // Use the game's item factory, retaining its actual return rather than scanning
                // for a nearby prefab. The inventory was removed only by the reserved command.
                // Generated wrappers unbox Nullable<T>; their null defaults are not usable.
                var noVelocity = new Il2CppSystem.Nullable<Vector3>();
                var noAngularVelocity = new Il2CppSystem.Nullable<Vector3>();
                var result = ItemUtils.SpawnItemPickup(Item.Data, _dropPosition, _dropRotation, Vector3.zero,
                    false, true, true, false, Item, null, 1, noVelocity, noAngularVelocity, true, false);
                if (!Live(result)) return Uncertain("Native drop factory returned no live object.", out detail);
                _worldObject = result;
                var entity = result.GetComponent<BoltEntity>();
                if (!CaptureWorld(entity) || !Same(result, _worldObject) || !CapturePickup() || !Current())
                    return Uncertain("Native drop return did not establish one exact plain pickup.", out detail);
                State = TransferState.WorldApplied;
                Detail = detail = "One native pickup created after the exact player removal receipt; ledger settlement is pending.";
                return true;
            }
            catch (Exception error) { return Uncertain("World drop is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Finite(Quaternion value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w)
                && Math.Abs(Quaternion.Dot(value, value) - 1f) < 0.001f;
    }
}
