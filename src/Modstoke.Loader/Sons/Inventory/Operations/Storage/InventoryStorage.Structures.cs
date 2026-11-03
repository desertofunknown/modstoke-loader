// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Crafting.Structures;
using global::Sons.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        private int _slotIndex = -1;
        private StructureStorage _slot;
        private ItemInstanceManager _manager;
        private ItemInstanceManager.Items _entry;
        private Il2CppSystem.Collections.Generic.Dictionary<int, ItemInstanceManager.Items> _map;
        private Il2CppSystem.Collections.Generic.List<StructureStorage> _slots;
        private Component _storage;
        private IScrewStructureWithStorageState _storageState;
        private Il2CppSystem.Action<int> _contentsChanged;
        private int _beforeCount, _maximumCount;

        internal static bool TryReserveWithdrawal(PlayerBridge bridge,
            PlayerInventoryAuthority authority, in PlayerInventoryFrame reservedGrant,
            ItemStorageRemoveItemEvent request, out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "Only a single plain counted withdrawal from the exact current server slot is supported.";
            try
            {
                if (!TryPrepare(bridge, authority, in reservedGrant, TransferKind.Withdrawal, out var candidate, out detail)
                    || !candidate.SourceMatches(request) || request.Count != 1 || request.ItemId != reservedGrant.ItemId
                    || !Plain(request.ItemInstance?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance, reservedGrant.ItemId)
                    || !candidate.CaptureStorage(request.GetTargetNetworkIdValue().PackedValue, request.StorageSlotIndex)
                    || candidate._beforeCount < 1 || !candidate.AccessCurrent() || !candidate.Register()) return false;
                transfer = candidate;
                detail = candidate.Detail;
                return true;
            }
            catch (Exception error) { detail = "Storage withdrawal reservation failed: " + error.Message; return false; }
        }

        internal static bool TryReserveDeposit(PlayerBridge bridge,
            PlayerInventoryAuthority authority, in PlayerInventoryFrame reservedRemoval,
            ItemStorageAddItemEvent request, out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "Only one plain counted deposit without a placement callback is supported.";
            try
            {
                if (!TryPrepare(bridge, authority, in reservedRemoval, TransferKind.Deposit, out var candidate, out detail)
                    || !candidate.SourceMatches(request) || request.Count != 1 || request.ItemId != reservedRemoval.ItemId
                    || !Plain(request.ItemInstance?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance, reservedRemoval.ItemId)
                    || !candidate.CaptureStorage(request.GetTargetNetworkIdValue().PackedValue, request.StorageSlotIndex)
                    || candidate._slot.ValidateItemPlacementCallback != null
                    || candidate._beforeCount >= candidate._maximumCount
                    || !candidate.AccessCurrent() || !candidate.Register()) return false;
                transfer = candidate;
                detail = candidate.Detail;
                return true;
            }
            catch (Exception error) { detail = "Storage deposit reservation failed: " + error.Message; return false; }
        }

        private bool CaptureStorage(ulong networkId, int slotIndex)
        {
            if (slotIndex < 0 || networkId == 0 || !CaptureWorld(BoltNetwork.FindEntity(new NetworkId(networkId)))) return false;
            _slotIndex = slotIndex;
            var storage = _world.GetComponentInChildren<IScrewStructureWithStorageBase>();
            if (storage == null || storage.WasCollected) return false;
            var stationary = storage.TryCast<ScrewStructureWithStorage>();
            if (Live(stationary))
            {
                if (stationary._isBusy || !Same(stationary.entity, _world)) return false;
                _storage = stationary;
                _slots = stationary._storageSlots;
            }
            else
            {
                var mobile = storage.TryCast<ScrewStructureWithStorageMobile>();
                if (!Live(mobile) || mobile._isBusy || !Same(mobile.entity, _world)) return false;
                _storage = mobile;
                _slots = mobile._storageSlots;
            }
            if (_slots == null || _slots.WasCollected || slotIndex >= _slots.Count
                || !_world.StateIs<IScrewStructureWithStorageState>()) return false;
            _storageState = _world.GetState<IScrewStructureWithStorageState>();
            if (_storageState == null || _storageState.WasCollected || _storageState.StorageSlots == null
                || _storageState.StorageSlots.WasCollected || slotIndex >= _storageState.StorageSlots.Length) return false;
            _slot = _slots[slotIndex];
            if (!Live(_slot) || !Same(_slot._boltEntity, _world) || _slot.StorageSlotIndex != slotIndex) return false;
            _manager = _slot._itemInstanceManager;
            if (!Live(_manager)) return false;
            _map = _manager._itemsMap;
            // Require an initialized native bucket. Missing entries do not mean unlimited capacity.
            if (_map == null || _map.WasCollected || !_map.TryGetValue(Action.ItemId, out _entry)
                || _entry == null || _entry.WasCollected || _entry.Count < 0 || _entry.MaxCount < 1
                || (!IsWorldVariant && !NoUniqueItems(_entry))) return false;
            _beforeCount = _entry.Count;
            _maximumCount = _entry.MaxCount;
            _contentsChanged = _slot.OnStorageContentsChanged;
            return (!IsWorldVariant || CaptureVariantStorage()) && StorageCurrent(_beforeCount)
                && CaptureStorageInteraction();
        }

        private bool StorageCurrent(int count)
        {
            if (!WorldCurrent() || !Live(_storage) || !Live(_slot) || !Live(_manager)
                || !Same(_world.GetComponentInChildren<IScrewStructureWithStorageBase>(), _storage)
                || !Same(_slot._boltEntity, _world) || _slot.StorageSlotIndex != _slotIndex
                || !Same(_slot._itemInstanceManager, _manager) || !Same(_manager._itemsMap, _map)
                || _slots == null || _slots.WasCollected || _slotIndex >= _slots.Count || !Same(_slots[_slotIndex], _slot)
                || !_world.StateIs<IScrewStructureWithStorageState>()
                || !Same(_world.GetState<IScrewStructureWithStorageState>(), _storageState)
                || _storageState.StorageSlots == null || _storageState.StorageSlots.WasCollected
                || _slotIndex >= _storageState.StorageSlots.Length) return false;
            var stationary = _storage.TryCast<ScrewStructureWithStorage>();
            if (stationary != null)
            {
                if (stationary._isBusy || !Same(stationary.entity, _world) || !Same(stationary._storageSlots, _slots)) return false;
            }
            else
            {
                var mobile = _storage.TryCast<ScrewStructureWithStorageMobile>();
                if (!Live(mobile) || mobile._isBusy || !Same(mobile.entity, _world) || !Same(mobile._storageSlots, _slots)) return false;
            }
            if (!_map.TryGetValue(Action.ItemId, out var entry) || !Same(entry, _entry)
                || entry.Count != count || entry.MaxCount != _maximumCount
                || (IsWorldVariant ? !VariantStorageCurrent(count) : !NoUniqueItems(entry))) return false;
            var callback = _slot.OnStorageContentsChanged;
            return _contentsChanged == null ? callback == null : Same(callback, _contentsChanged);
        }

        private static bool NoUniqueItems(ItemInstanceManager.Items entry)
            => entry.UniqueInstances == null || !entry.UniqueInstances.WasCollected && entry.UniqueInstances.Count == 0;

        private bool TakeStorage(out string detail)
        {
            detail = Detail;
            if (!StorageCurrent(_beforeCount) || !AccessCurrent()) return false;
            // Native ModifyItemCountFromClientRequest removes first, sends PlayerAddItem, then
            // invokes OnStorageContentsChanged. Separate its debit from delivery, never replay it.
            State = TransferState.TakingSource;
            int removed = IsWorldVariant && Item.Data._eachInstanceIsUnique
                ? RemoveVariantStorageItem() : RemoveReservedStorageItem();
            if (removed != 1 || !Current() || !StorageCurrent(_beforeCount - 1))
                return Uncertain("The exact storage debit was not confirmed.", out detail);
            _contentsChanged?.Invoke(Action.ItemId);
            if (!Current() || !StorageCurrent(_beforeCount - 1))
                return Uncertain("Storage identity changed during native contents publication.", out detail);
            State = TransferState.SourceTaken;
            Detail = detail = "One native storage unit removed and published; reserved grant remains unsent.";
            return true;
        }

        /// <summary>Run after the exact authenticated native removal receipt, before closing its action.</summary>
        internal bool TryApplyDeposit(in PlayerInventoryFrame removalApplied, out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || Kind != TransferKind.Deposit
                || State != TransferState.RemovalRequested || !RemovalReceipt(removalApplied)) return false;
            _busy = true;
            try
            {
                // The native player debit has already happened. Even a failed precondition is
                // now uncertain custody, not permission to send the removal a second time.
                if (!Current() || !WorldItemCurrent() || !AccessCurrent() || !StorageCurrent(_beforeCount)
                    || (IsWorldVariant ? !_variant.PlacementAccepted : _slot.ValidateItemPlacementCallback != null)
                    || _beforeCount >= _maximumCount)
                    return Uncertain("Deposit destination changed after the native player removal.", out detail);
                State = TransferState.ApplyingDestination;
                bool added = IsWorldVariant && Item.Data._eachInstanceIsUnique
                    ? AddVariantStorageItem() : AddReservedStorageItem();
                if (!added || !Current() || !StorageCurrent(_beforeCount + 1))
                    return Uncertain("The exact storage addition was not confirmed.", out detail);
                _contentsChanged?.Invoke(Action.ItemId);
                if (!Current() || !StorageCurrent(_beforeCount + 1))
                    return Uncertain("Deposit publication changed its native destination.", out detail);
                State = TransferState.WorldApplied;
                Detail = detail = "One native storage unit deposited after its exact player removal receipt.";
                return true;
            }
            catch (Exception error) { return Uncertain("Deposit is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        /// <summary>Call after the inventory integrator commits the same removal's ledger debit.</summary>
        internal bool TryCompleteRemoval(in PlayerInventoryFrame removalApplied, out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.WorldApplied
                || !RemovalReceipt(removalApplied)) return false;
            _busy = true;
            try
            {
                if (!Current()) return Uncertain("Actor changed before world transfer settlement.", out detail);
                if (Kind == TransferKind.Deposit ? !StorageCurrent(_beforeCount + 1) : !PickupCurrent())
                    return Uncertain("Native destination changed before world transfer settlement.", out detail);
                if (!_authority.TryReadItem(Action.Key, out var item) || item.ItemId != Action.ItemId
                    || item.Quantity != Action.BeforeQuantity - 1 || item.Consumed != (item.Quantity == 0))
                    return Uncertain("World receipt has no matching committed inventory debit.", out detail);
                if (IsWorldVariant) _authority.MutationEnforcement.ReleaseDetachedModules(Item);
                Close(TransferState.Committed);
                Detail = detail = "Player debit and native destination receipt settled; no second debit or spawn was issued.";
                return true;
            }
            catch (Exception error) { return Uncertain("World transfer settlement failed: " + error.Message, out detail); }
            finally { _busy = false; }
        }
    }
}
