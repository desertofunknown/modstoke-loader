// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using PathologicalGames;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using global::Sons.Weapon;
using TheForest.Items.Inventory;
using TheForest.Items.Utils;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        /// <summary>Drops one exact stored instance from the offline player or host's inventory.</summary>
        /// <remarks>
        /// Requires the Unity lifecycle thread, qualified inventory hooks and a unique ordinary pickup.
        /// Preserves native removal notifications and item modules. False may follow a debit or spawn;
        /// inspect the result and never automatically retry or refund. Remote replication is not confirmed.
        /// </remarks>
        public static bool TryDropStoredItem(ItemInstance item, out GameObject droppedObject, out ItemDropResult result)
            => TryDropItem(item, 0, false, out droppedObject, out result);

        /// <summary>Drops one plain count-based item from the local player's inventory.</summary>
        /// <remarks>
        /// On a client of a server without strict inventory the native drop is sent to the server, which
        /// spawns the pickup; strict servers take drops through their own request instead.
        /// Requires the Unity lifecycle thread, idle equipment and qualified inventory hooks.
        /// Items with modules or specialized pickups are rejected. False may follow a debit or spawn;
        /// inspect the result and never automatically retry or refund. Remote replication is not confirmed.
        /// </remarks>
        public static bool TryDropCountedItem(int itemId, out GameObject droppedObject, out ItemDropResult result)
            => TryDropItem(null, itemId, true, out droppedObject, out result);

        private static bool TryDropItem(ItemInstance item, int itemId, bool counted,
            out GameObject droppedObject, out ItemDropResult result)
        {
            droppedObject = null;
            result = new(0, false, null, false, false, false, null, null, false, "Not attempted.");
            if (!TryGetCurrentInventory(out var inventory, out var detail))
            {
                result = result.WithDetail(detail);
                return false;
            }
            if (_executingMutation || !PlayerInventoryEnforcement.LocalDropReady)
            {
                result = result.WithDetail("Drop requires idle SDK actions and qualified native inventory hooks.");
                return false;
            }

            StoredDrop drop = null;
            _executingMutation = true;
            try
            {
                if (!counted)
                {
                    if (item is null || item.WasCollected || item.Pointer == 0)
                    {
                        result = result.WithDetail("A live exact stored item is required.");
                        return false;
                    }
                    itemId = item._itemID;
                }
                if (!TryGetExecutionItem(itemId, out var data, out detail))
                {
                    result = result.WithDetail(detail);
                    return false;
                }
                drop = new StoredDrop(inventory, item, data, counted);
                if (!drop.TryEnter())
                {
                    result = drop.Result("Stored item custody changed before dispatch; no action was invoked.");
                    return false;
                }
                var observed = drop.Apply(out droppedObject);
                result = drop.Result(!observed ? "Drop is unconfirmed; do not automatically retry or refund."
                    : drop.SentToServer ? "Exact debit observed and the native drop was sent to the server, which spawns the pickup."
                    : "Exact debit and live pickup observed. Save persistence and remote delivery are not confirmed.");
                return observed;
            }
            catch (Exception error)
            {
                result = drop?.Result("Drop failed; inspect attempted phases before taking further action. " + error.Message)
                    ?? result.WithDetail("No native action was invoked. " + error.Message);
                return false;
            }
            finally
            {
                drop?.Dispose();
                _executingMutation = false;
            }
        }

        internal sealed class StoredDrop : IDisposable
        {
            private static volatile StoredDrop _current;
            internal static StoredDrop Active => _current;
            private static bool RemoteClient => BoltNetwork.isRunning && !BoltNetwork.isServer;
            /// <summary>A client's drop went to the server as the native DropItem event; it spawns the pickup.</summary>
            internal bool SentToServer { get; private set; }
            private readonly PlayerInventory _inventory;
            private readonly ItemInstanceManager _manager;
            private readonly ItemInstance _item;
            private readonly ItemData _data;
            private readonly InventoryMutationContext _context;
            private readonly EquipmentTransfer.Custody _before;
            private readonly InventoryStorage.WorldItemSnapshot _modules;
            private readonly GameObject _root;
            private readonly Transform _prefab;
            private readonly int _itemId;
            private readonly bool _counted;
            private bool _active, _removeEntered, _instanceEntered, _instanceObserved;
            private bool _remainderEntered, _remainderObserved, _debitObserved, _spawnEntered;
            private bool _debitAttempted, _spawnAttempted, _spawnReturned, _pickupObserved;
            private bool? _nativeDebit;
            private int? _afterCount;

            internal StoredDrop(PlayerInventory inventory, ItemInstance item, ItemData data, bool counted)
            {
                if (_current != null || !PlayerInventoryEnforcement.LocalDropReady
                    || !TryGetMutationContext(inventory, data._id, out _context, out var currentData, out var detail)
                    || !Same(data, currentData) || RemoteClient && PlayerInventoryEnforcement.StrictInventoryArmed)
                    throw new InvalidOperationException("A local player with unreserved inventory is required; strict "
                        + "inventory drops go through the server's own drop request.");
                _inventory = inventory;
                _manager = inventory._itemInstanceManager;
                _item = item;
                _itemId = data._id;
                _data = data;
                _counted = counted;
                _root = LocalPlayer.GameObject;
                _prefab = data.PickupPrefab;
                if (!Live(_manager) || !Live(_root) || !Live(_prefab))
                    throw new InvalidOperationException("The inventory, player or item's pickup prefab is not live.");
                if (counted ? !DefinitionCurrent() : !ProfileCurrent())
                    throw new InvalidOperationException("The item is not a storable, spawnable, droppable item valid in this game mode.");
                if (!EquipmentStowIdle(inventory))
                    throw new InvalidOperationException("The player must be in the world view with idle, empty hands.");
                if (!PickupServiceReady())
                    throw new InvalidOperationException("The world's pickup pool is not ready.");
                var prefabPickup = _prefab.GetComponent<PickUp>();
                if (!Live(prefabPickup) || prefabPickup._itemId != _itemId
                    || counted && !CountedPickupSupported(prefabPickup))
                    throw new InvalidOperationException("The definition has no matching ordinary pickup.");
                _before = EquipmentTransfer.Custody.Capture(inventory);
                foreach (var next in _before.Next)
                    if (next != 0) throw new InvalidOperationException("An equipment transition is still pending.");
                if (counted)
                {
                    if (!_before.IsCountedStorage(_itemId) || _before.HasHeldItem(_itemId))
                        throw new InvalidOperationException("The item must have positive count-only storage and no held instance.");
                    // The manager debits nonunique storage by ID. This representative is
                    // retained for native callbacks and pickup binding, never inserted into storage.
                    _item = new ItemInstance(_itemId);
                    if (!Alive(_item) || !_before.IsDetached(_item.Pointer) || Live(_item.ItemObject)
                        || !Alive(_item._modules) || _item._modules.Count != 0)
                        throw new InvalidOperationException("The count-only representative is not detached and module-free.");
                }
                else
                {
                    if (!_before.IsUniqueStorage(_itemId) || _before.Occurrences(item.Pointer) != 1
                        || _before.Objects.ContainsKey(_itemId) || Live(item.ItemObject))
                        throw new InvalidOperationException("The exact instance must be uniquely stored, not held or count-only.");
                }
                _modules = new InventoryStorage.WorldItemSnapshot(_item);
            }

            internal static StoredDrop ForInventory(PlayerInventory inventory)
            {
                var drop = _current;
                return drop != null && Same(inventory, drop._inventory) ? drop : null;
            }

            internal static StoredDrop ForManager(ItemInstanceManager manager)
            {
                var drop = _current;
                if (drop == null) return null;
                if (Same(manager, drop._manager)) return drop;
                return Environment.CurrentManagedThreadId == _ownerThreadId && Live(drop._inventory)
                    && Same(manager, drop._inventory._itemInstanceManager) ? drop : null;
            }

            internal bool TryEnter()
            {
                if (_current != null || !Current() || !CustodyMatches(0)) return false;
                _active = true;
                _current = this;
                return true;
            }

            internal bool Apply(out GameObject output)
            {
                output = null;
                if (!_active || !Current() || !CustodyMatches(0)) return false;
                _debitAttempted = true;
                _nativeDebit = _inventory.RemoveItem(_itemId, 1, false, false, true, _item, false);
                if (!Current()) return false;
                var after = EquipmentTransfer.Custody.Capture(_inventory);
                _afterCount = after.StoredCount(_itemId);
                if (_nativeDebit != true || !_debitObserved || !CustodyMatches(after, -1)) return false;
                _spawnAttempted = true;
                _afterCount = null;
                PlayerInventory.DropItemInternal(_itemId, _item, false, out output, 1);
                if (!Current()) return false;
                after = EquipmentTransfer.Custody.Capture(_inventory);
                _afterCount = after.StoredCount(_itemId);
                if (!_spawnReturned || !CustodyMatches(after, -1)) return false;
                // On a client the native drop sends DropItem and returns no object; the server spawns the pickup.
                if (RemoteClient) return SentToServer = true;
                if (!Live(output)) return false;
                var pickup = output.GetComponent<PickUp>();
                _pickupObserved = Live(pickup) && pickup._itemId == _itemId && pickup._amountToAdd == 1
                    && Same(pickup._ItemInstance_k__BackingField, _item)
                    && (!_counted || Same(_item.ItemObject, output) && CountedPickupSupported(pickup));
                return _pickupObserved;
            }

            internal bool PermitRemoval(int id, int amount, bool overflow, bool equipPrevious, bool quickSelect,
                ItemInstance item, bool instantDestroy, int depth)
            {
                if (!_active || !_debitAttempted || _removeEntered || depth != 1 || id != _itemId || amount != 1
                    || overflow || equipPrevious || !quickSelect || instantDestroy || !Same(item, _item)
                    || !Current() || !CustodyMatches(0)) return false;
                _removeEntered = true;
                return true;
            }

            internal bool PermitInstance(ItemInstance item, int depth)
            {
                if (!_active || !_removeEntered || _instanceEntered || depth != 2 || !Same(item, _item)
                    || !Current() || !CustodyMatches(0)) return false;
                _instanceEntered = true;
                return true;
            }

            internal void ObserveInstance(bool ranOriginal, bool result, Exception error)
            {
                _instanceObserved = ranOriginal && result && error == null && Current() && CustodyMatches(-1);
            }

            internal bool PermitRemainder(int id, int amount, bool suppressCallback, int depth)
            {
                // A successful exact removal emits a zero-count notification. Reject the
                // native fallback that would remove a different instance by item ID.
                if (!_active || !_instanceObserved || _remainderEntered || depth != 2
                    || id != _itemId || amount != 0 || suppressCallback || !Current() || !CustodyMatches(-1)) return false;
                _remainderEntered = true;
                return true;
            }

            internal void ObserveRemainder(bool ranOriginal, int result, Exception error)
            {
                _remainderObserved = ranOriginal && result == 0 && error == null && Current() && CustodyMatches(-1);
            }

            internal void ObserveRemoval(bool ranOriginal, bool result, Exception error)
            {
                _debitObserved = ranOriginal && result && error == null && _instanceObserved && _remainderObserved
                    && Current() && CustodyMatches(-1);
            }

            internal bool PermitSpawn(int id, ItemInstance item, bool fakeDrop, int count, int depth)
            {
                if (!_active || !_spawnAttempted || !_debitObserved || _spawnEntered || depth != 1 || id != _itemId
                    || !Same(item, _item) || fakeDrop || count != 1 || !Current() || !CustodyMatches(-1)
                    || _counted && !CountedPickupSupported(_prefab.GetComponent<PickUp>())) return false;
                _spawnEntered = true;
                return true;
            }

            internal void ObserveSpawn(bool ranOriginal, Exception error)
                => _spawnReturned = ranOriginal && error == null && _spawnEntered;

            private bool Current() => Environment.CurrentManagedThreadId == _ownerThreadId
                && (!_active || ReferenceEquals(_current, this))
                && MutationContextCurrent(_inventory, in _context, out _)
                && !(RemoteClient && PlayerInventoryEnforcement.StrictInventoryArmed)
                && !PlayerInventoryEnforcement.IsSpecializedInventoryHeld(_inventory)
                && !InventoryStorage.IsManagerHeld(_manager)
                && Same(_root, LocalPlayer.GameObject) && Same(_manager, _inventory._itemInstanceManager)
                && Live(LocalPlayer.Stats) && Same(LocalPlayer.Stats._inventory, _inventory)
                && !LocalPlayer.Stats._playerIsDowned && LocalPlayer.Stats._dyingState == PlayerStats.DyingState.Alive
                && !LocalPlayer.Stats._respawning
                && ProfileCurrent() && EquipmentStowIdle(_inventory) && _modules.Current();

            private bool ProfileCurrent() => Alive(_item) && Live(_data) && Same(_item._data, _data)
                && _item._itemID == _itemId && (!_counted || _spawnEntered || _item.ItemObject is null)
                && DefinitionCurrent();

            private bool DefinitionCurrent() => Live(_data) && _data._id == _itemId
                && _data._eachInstanceIsUnique != _counted
                && !_data._canNotBeStored && !_data._alwaysDropOnUnequip && _data._maxAmount > 0
                && ((int)_data._type & 0x10) != 0 && _data._canBeSpawned && Same(_prefab, _data.PickupPrefab)
                && (!_counted || !_data._hasVisualVariant && !_data._isDismemberedLimb && !_data._isPerishable
                    && !_data._isVolumeContainer && !_data._hasBloodReveal && !_data._isStructureElement
                    && !_data._isGPSLocator && !_data._canBePlated && !_data._isUpgradeableArrow)
                && ItemUtils.IsValidForCurrentGameMode(_data);

            private bool CustodyMatches(int delta)
                => CustodyMatches(EquipmentTransfer.Custody.Capture(_inventory), delta);

            private bool CustodyMatches(EquipmentTransfer.Custody after, int delta) => _counted
                ? after.IsDetached(_item.Pointer) && _before.CountedMatches(after, _itemId, delta)
                : _before.Matches(after, _item, delta, -1);

            private bool CountedPickupSupported(PickUp pickup)
            {
                if (!Live(pickup) || pickup._itemId != _itemId || pickup._infinite
                    || IL2CPP.il2cpp_object_get_class(pickup.Pointer) != Il2CppClassPointerStore<PickUp>.NativeClassPtr)
                    return false;
                var amount = pickup._amount;
                if (!Alive(amount) || amount._min != 1 || amount._max != 1) return false;
                // These components create modules or load a second representation during binding.
                return !Live(pickup.GetComponentInChildren<RangedWeapon>(true))
                    && !Live(pickup.GetComponentInChildren<WeaponMods>(true))
                    && !Live(pickup.GetComponentInChildren<ItemRenderable>(true));
            }

            private static bool PickupServiceReady()
            {
                var pools = PoolManager.Pools;
                return pools != null && !pools.WasCollected && pools.TryGetValue("PickUps", out var pool)
                    && Live(pool);
            }

            internal ItemDropResult Result(string detail) => new(_itemId, _debitAttempted, _nativeDebit,
                _debitObserved, _spawnAttempted, _spawnReturned, _before.StoredCount(_itemId), _afterCount,
                _pickupObserved, detail);

            public void Dispose()
            {
                if (ReferenceEquals(_current, this)) _current = null;
                _active = false;
            }

            private static bool Alive(Il2CppObjectBase value) => value != null && !value.WasCollected && value.Pointer != 0;
            private static bool Live(UnityEngine.Object value) => Alive(value) && value;
            private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
                => Alive(left) && Alive(right) && left.Pointer == right.Pointer;
        }
    }

    public sealed class ItemDropResult
    {
        internal ItemDropResult(int itemId, bool debitAttempted, bool? nativeDebit, bool debitObserved,
            bool spawnAttempted, bool spawnReturned, int? before, int? after, bool pickupObserved, string detail)
        {
            ItemId = itemId;
            DebitAttempted = debitAttempted;
            NativeDebitAccepted = nativeDebit;
            DebitObserved = debitObserved;
            SpawnAttempted = spawnAttempted;
            SpawnReturned = spawnReturned;
            BeforeCount = before;
            AfterCount = after;
            PickupObserved = pickupObserved;
            Detail = detail;
        }

        public int ItemId { get; }
        public bool DebitAttempted { get; }
        public bool? NativeDebitAccepted { get; }
        public bool DebitObserved { get; }
        public bool SpawnAttempted { get; }
        public bool SpawnReturned { get; }
        public int? BeforeCount { get; }
        public int? AfterCount { get; }
        public bool PickupObserved { get; }
        public string Detail { get; }

        internal ItemDropResult WithDetail(string detail) => new(ItemId, DebitAttempted, NativeDebitAccepted,
            DebitObserved, SpawnAttempted, SpawnReturned, BeforeCount, AfterCount, PickupObserved, detail);
    }
}
