// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Crafting.Structures;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public static partial class ServerStorage
    {
        public const int MaximumContentsSlots = 64;
        public const int MaximumContentsItemsPerSlot = 256;
        /// <summary>Nonempty maps above this backing-entry count are not enumerated, including sparse maps.</summary>
        public const int MaximumContentsMapEntries = 4096;

        internal static StorageReadStatus InspectTargetContents(StorageRequest request,
            int maximumSlots, int maximumItemsPerSlot, out StorageContentsSnapshot snapshot)
        {
            snapshot = null;
            if (maximumSlots < 1 || maximumSlots > MaximumContentsSlots
                || maximumItemsPerSlot < 1 || maximumItemsPerSlot > MaximumContentsItemsPerSlot)
                return StorageReadStatus.InvalidLimit;

            try
            {
                var networkId = request.RequestedTargetNetworkId;
                var status = ResolveStorageTarget(networkId, out var target);
                if (status != StorageReadStatus.Success)
                    return status;

                var prefabId = target.Entity.prefabId.Value;
                var isServerOwned = target.Entity.isOwner;
                var slots = new StorageSlotContentsSnapshot[Math.Min(target.SlotCount, maximumSlots)];
                for (var index = 0; index < slots.Length; index++)
                    slots[index] = CaptureStorageSlotContents(target, index, maximumItemsPerSlot);

                status = ResolveStorageTarget(networkId, out var current);
                if (status != StorageReadStatus.Success)
                    return status;
                if (!MatchesStorageEntity(current.Entity, target.Entity)
                    || !SameStorageReference(current.Component, target.Component)
                    || !SameStorageReference(current.Slots, target.Slots)
                    || !SameStorageReference(current.State, target.State)
                    || !SameStorageReference(current.ReplicatedSlots, target.ReplicatedSlots)
                    || current.SlotCount != target.SlotCount || current.SlotVersion != target.SlotVersion
                    || current.ReplicatedSlotCount != target.ReplicatedSlotCount
                    || current.Kind != target.Kind || current.IsBusy != target.IsBusy
                    || current.Entity.prefabId.Value != prefabId || current.Entity.isOwner != isServerOwned)
                    return StorageReadStatus.BindingChanged;

                snapshot = new StorageContentsSnapshot(networkId, prefabId, isServerOwned,
                    target.Kind, target.IsBusy, target.SlotCount, target.ReplicatedSlotCount, slots);
                return StorageReadStatus.Success;
            }
            catch (Exception)
            {
                return StorageReadStatus.Failed;
            }
        }

        private static StorageSlotContentsSnapshot CaptureStorageSlotContents(StorageInspectionTarget target,
            int slotIndex, int maximumItems)
        {
            int? nativeSlotIndex = null;
            int? itemEntryCount = null;
            StorageSlotContentsSnapshot Unavailable(StorageReadStatus status) =>
                new(slotIndex, nativeSlotIndex, status, itemEntryCount, Array.Empty<StorageItemCountSnapshot>());

            try
            {
                if (slotIndex >= target.ReplicatedSlotCount)
                    return Unavailable(StorageReadStatus.SlotOutOfRange);
                var slot = target.Slots[slotIndex];
                if (!IsLiveStorageObject(slot))
                    return Unavailable(StorageReadStatus.SlotUnavailable);
                nativeSlotIndex = slot._StorageSlotIndex_k__BackingField;
                if (!MatchesStorageEntity(slot._boltEntity, target.Entity))
                    return Unavailable(StorageReadStatus.StorageOwnerMismatch);

                var manager = slot._itemInstanceManager;
                if (!IsLiveStorageObject(manager))
                    return Unavailable(StorageReadStatus.ItemStateUnavailable);
                var items = manager._itemsMap;
                if (items is null || items.WasCollected)
                    return Unavailable(StorageReadStatus.ItemStateUnavailable);
                var count = items.Count;
                var backingCount = items._count;
                var version = items._version;
                if (count < 0 || backingCount < count)
                    return Unavailable(StorageReadStatus.ItemStateUnavailable);
                itemEntryCount = count;

                // MoveNext can skip removed entries. Bound the backing range as well as the copied rows.
                if (count != 0 && backingCount > MaximumContentsMapEntries)
                    return Unavailable(StorageReadStatus.ScanLimitExceeded);

                var copies = count == 0
                    ? Array.Empty<StorageItemCountSnapshot>()
                    : new StorageItemCountSnapshot[Math.Min(count, maximumItems)];
                var records = copies.Length == 0
                    ? Array.Empty<ItemInstanceManager.Items>()
                    : new ItemInstanceManager.Items[copies.Length];
                var uniquePointers = copies.Length == 0
                    ? Array.Empty<IntPtr>()
                    : new IntPtr[copies.Length];
                if (copies.Length != 0)
                {
                    var iterator = items.GetEnumerator();
                    if (iterator is null || iterator.WasCollected)
                        return Unavailable(StorageReadStatus.ItemStateUnavailable);
                    try
                    {
                        for (var index = 0; index < copies.Length; index++)
                        {
                            if (!iterator.MoveNext())
                                return Unavailable(StorageReadStatus.BindingChanged);
                            var pair = iterator.Current;
                            if (pair is null || pair.WasCollected)
                                return Unavailable(StorageReadStatus.ItemStateUnavailable);
                            var record = pair.Value;
                            if (record is null || record.WasCollected)
                                return Unavailable(StorageReadStatus.ItemStateUnavailable);
                            var unique = record.UniqueInstances;
                            int? uniqueCount = null;
                            if (unique is not null)
                            {
                                if (unique.WasCollected)
                                    return Unavailable(StorageReadStatus.ItemStateUnavailable);
                                uniqueCount = unique.Count;
                                if (uniqueCount.Value < 0)
                                    return Unavailable(StorageReadStatus.ItemStateUnavailable);
                                uniquePointers[index] = unique.Pointer;
                            }
                            records[index] = record;
                            copies[index] = new StorageItemCountSnapshot(pair.Key,
                                record.Count, record.MaxCount, uniqueCount);
                        }
                    }
                    finally
                    {
                        iterator.Dispose();
                    }
                }

                if (!IsLiveStorageObject(slot) || !IsLiveStorageObject(manager)
                    || !MatchesStorageEntity(slot._boltEntity, target.Entity)
                    || slot._StorageSlotIndex_k__BackingField != nativeSlotIndex
                    || !SameStorageReference(slot._itemInstanceManager, manager)
                    || !SameStorageReference(manager._itemsMap, items)
                    || !SameStorageReference(target.Slots[slotIndex], slot)
                    || target.Slots._version != target.SlotVersion
                    || items.Count != count || items._count != backingCount || items._version != version)
                    return Unavailable(StorageReadStatus.BindingChanged);

                // Item fields can change without changing the dictionary version.
                for (var index = 0; index < records.Length; index++)
                {
                    var record = records[index];
                    var copy = copies[index];
                    if (record.WasCollected || record.Count != copy.NativeItemCount
                        || record.MaxCount != copy.NativeMaximumCount)
                        return Unavailable(StorageReadStatus.BindingChanged);
                    var unique = record.UniqueInstances;
                    if (unique is null)
                    {
                        if (uniquePointers[index] != IntPtr.Zero)
                            return Unavailable(StorageReadStatus.BindingChanged);
                    }
                    else if (unique.WasCollected || unique.Pointer != uniquePointers[index]
                        || unique.Count != copy.NativeUniqueInstanceCount)
                        return Unavailable(StorageReadStatus.BindingChanged);
                }

                return new StorageSlotContentsSnapshot(slotIndex, nativeSlotIndex,
                    StorageReadStatus.Success, count, copies);
            }
            catch (Exception)
            {
                return Unavailable(StorageReadStatus.Failed);
            }
        }

        private static StorageReadStatus ResolveStorageTarget(ulong networkId,
            out StorageInspectionTarget target)
        {
            target = null;
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                return StorageReadStatus.NotServer;
            var entity = networkId == 0 ? null : BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (!IsLiveStorageObject(entity) || !entity.isAttached || entity.networkId.PackedValue != networkId)
                return StorageReadStatus.TargetUnavailable;

            // Match the native callback's first active child implementing this interface.
            var storage = entity.GetComponentInChildren<IScrewStructureWithStorageBase>();
            if (storage is null || storage.WasCollected)
                return StorageReadStatus.StorageUnavailable;
            Il2CppSystem.Collections.Generic.List<StructureStorage> slots;
            UnityEngine.Component component;
            BoltEntity storageOwner;
            bool isBusy;
            StorageKind kind;
            var stationary = storage.TryCast<ScrewStructureWithStorage>();
            if (stationary is not null)
            {
                if (!IsLiveStorageObject(stationary))
                    return StorageReadStatus.StorageUnavailable;
                component = stationary;
                storageOwner = stationary.entity;
                slots = stationary._storageSlots;
                isBusy = stationary._isBusy;
                kind = StorageKind.Stationary;
            }
            else
            {
                var mobile = storage.TryCast<ScrewStructureWithStorageMobile>();
                if (mobile is null)
                    return StorageReadStatus.UnsupportedStorage;
                if (!IsLiveStorageObject(mobile))
                    return StorageReadStatus.StorageUnavailable;
                component = mobile;
                storageOwner = mobile.entity;
                slots = mobile._storageSlots;
                isBusy = mobile._isBusy;
                kind = StorageKind.Mobile;
            }

            if (!MatchesStorageEntity(storageOwner, entity))
                return StorageReadStatus.StorageOwnerMismatch;
            if (slots is null || slots.WasCollected || !entity.StateIs<IScrewStructureWithStorageState>())
                return StorageReadStatus.StorageStateUnavailable;
            var state = entity.GetState<IScrewStructureWithStorageState>();
            if (state is null || state.WasCollected)
                return StorageReadStatus.StorageStateUnavailable;
            var replicatedSlots = state.StorageSlots;
            if (replicatedSlots is null || replicatedSlots.WasCollected)
                return StorageReadStatus.StorageStateUnavailable;
            var slotCount = slots.Count;
            var replicatedSlotCount = replicatedSlots.Length;
            if (slotCount < 0 || replicatedSlotCount < 0)
                return StorageReadStatus.StorageStateUnavailable;

            target = new StorageInspectionTarget(entity, component, slots, state, replicatedSlots,
                slotCount, slots._version, replicatedSlotCount, kind, isBusy);
            return StorageReadStatus.Success;
        }

        private static bool SameStorageReference(Il2CppObjectBase left, Il2CppObjectBase right) =>
            left is not null && right is not null && !left.WasCollected && !right.WasCollected
            && left.Pointer == right.Pointer;

        private sealed record StorageInspectionTarget(BoltEntity Entity, UnityEngine.Component Component,
            Il2CppSystem.Collections.Generic.List<StructureStorage> Slots, IScrewStructureWithStorageState State,
            Bolt.NetworkArray_Objects<StoredItemsData> ReplicatedSlots, int SlotCount, int SlotVersion,
            int ReplicatedSlotCount, StorageKind Kind, bool IsBusy);
    }

    /// <summary>
    /// Managed copies of sequential storage reads, not a transaction, reservation or permission verdict.
    /// Local item maps are inspected; replicated slot contents and item instances are not traversed.
    /// </summary>
    public sealed class StorageContentsSnapshot
    {
        internal StorageContentsSnapshot(ulong targetNetworkId, int prefabId, bool isServerOwned,
            StorageKind kind, bool isBusy, int slotCount, int replicatedSlotCount,
            StorageSlotContentsSnapshot[] slots)
        {
            TargetNetworkId = targetNetworkId;
            PrefabId = prefabId;
            IsServerOwned = isServerOwned;
            Kind = kind;
            IsBusy = isBusy;
            SlotCount = slotCount;
            ReplicatedSlotCount = replicatedSlotCount;
            Slots = Array.AsReadOnly(slots);
            var unreadableSlots = 0;
            var itemsTruncated = false;
            foreach (var slot in slots)
            {
                if (slot.Status != StorageReadStatus.Success)
                    unreadableSlots++;
                if (slot.IsTruncated)
                    itemsTruncated = true;
            }
            UnreadableSlotCount = unreadableSlots;
            ItemEntriesTruncated = itemsTruncated;
            CapturedAt = DateTimeOffset.UtcNow;
        }

        public ulong TargetNetworkId { get; }
        public int PrefabId { get; }
        /// <summary>Native entity ownership, not requesting-player permission or proof of inventory authority.</summary>
        public bool IsServerOwned { get; }
        public StorageKind Kind { get; }
        public bool IsBusy { get; }
        public int SlotCount { get; }
        public int ReplicatedSlotCount { get; }
        /// <summary>Bounded prefix in local slot-list order, including unavailable slot results.</summary>
        public IReadOnlyList<StorageSlotContentsSnapshot> Slots { get; }
        public int UnreadableSlotCount { get; }
        public bool SlotsTruncated => Slots.Count < SlotCount;
        public bool ItemEntriesTruncated { get; }
        public bool IsTruncated => SlotsTruncated || ItemEntriesTruncated;
        /// <summary>All local slots and map entries were copied; does not establish transfer eligibility.</summary>
        public bool IsComplete => !IsTruncated && UnreadableSlotCount == 0;
        public DateTimeOffset CapturedAt { get; }
    }

    /// <summary>Copied item-map records for one local slot; a failed read is not an empty inventory.</summary>
    public sealed class StorageSlotContentsSnapshot
    {
        internal StorageSlotContentsSnapshot(int slotIndex, int? nativeSlotIndex,
            StorageReadStatus status, int? itemEntryCount, StorageItemCountSnapshot[] items)
        {
            SlotIndex = slotIndex;
            NativeSlotIndex = nativeSlotIndex;
            Status = status;
            NativeItemEntryCount = itemEntryCount;
            Items = Array.AsReadOnly(items);
        }

        public int SlotIndex { get; }
        /// <summary>Slot's own index field when readable; not assumed to equal SlotIndex.</summary>
        public int? NativeSlotIndex { get; }
        public StorageReadStatus Status { get; }
        /// <summary>Observed dictionary entry count, including zero-count records; null when unknown.</summary>
        public int? NativeItemEntryCount { get; }
        /// <summary>Managed copies in native dictionary order; empty on any non-success slot status.</summary>
        public IReadOnlyList<StorageItemCountSnapshot> Items { get; }
        /// <summary>True only for an explicit scan or output limit, not an unavailable or changed map.</summary>
        public bool IsTruncated => Status == StorageReadStatus.ScanLimitExceeded
            || Status == StorageReadStatus.Success && NativeItemEntryCount.HasValue
                && Items.Count < NativeItemEntryCount.Value;
        public bool IsComplete => Status == StorageReadStatus.Success && !IsTruncated;
    }

    /// <summary>Raw count fields for one item-map key. No inventory instance or module is retained.</summary>
    public sealed class StorageItemCountSnapshot
    {
        internal StorageItemCountSnapshot(int itemId, int count, int maximumCount, int? uniqueInstanceCount)
        {
            ItemId = itemId;
            NativeItemCount = count;
            NativeMaximumCount = maximumCount;
            NativeUniqueInstanceCount = uniqueInstanceCount;
        }

        /// <summary>Raw dictionary key, not a validated item definition or source-player item identity.</summary>
        public int ItemId { get; }
        /// <summary>Raw Items.Count, including zero or negative values; do not add the unique-instance count.</summary>
        public int NativeItemCount { get; }
        /// <summary>Raw Items.MaxCount, including negative values; not computed free capacity or admission.</summary>
        public int NativeMaximumCount { get; }
        /// <summary>UniqueInstances length, or null when its list is absent. No list entries are read.</summary>
        public int? NativeUniqueInstanceCount { get; }
    }
}
