// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public enum PickupReadStatus
    {
        Unavailable,
        Success,
        WrongThread,
        NotServer,
        /// <summary>No live entity has the requested network ID, including zero.</summary>
        EntityUnavailable,
        NotAttached,
        /// <summary>A client owns the entity; the native receiver forwards these instead of destroying them.</summary>
        NotServerOwned,
        /// <summary>The entity hierarchy contains no pickup component.</summary>
        PickupUnavailable,
        /// <summary>
        /// No active pickup is selected by the native lookup and several inactive ones exist,
        /// or the hierarchy exceeds MaximumPickupHierarchyEntries.
        /// </summary>
        AmbiguousHierarchy,
        /// <summary>The entity, selected pickup or its item instance changed while the fields were copied.</summary>
        ContextChanged,
        Failed
    }

    public enum PickupKind
    {
        /// <summary>The exact component type is Sons.Gameplay.PickUp.</summary>
        Ordinary,
        /// <summary>A subclass whose MainEffect may grant differently from the fields copied here.</summary>
        Derived
    }

    /// <summary>
    /// The server's own copy of one pickup's configuration at read time, with no retained native objects.
    /// It is not what a collecting client added to its inventory: vanilla clients grant themselves the item
    /// before the cleanup event arrives, and the host cannot see that inventory. Network IDs can be reused
    /// by pooled pickups, so this is not a persistent identity.
    /// </summary>
    public sealed class PickupSnapshot
    {
        internal PickupSnapshot(ulong networkId, int prefabId, bool isServerOwned, string pickupTypeName,
            PickupKind kind, int activePickupCount, int totalPickupCount, bool selectedMatchesNativeLookup,
            int componentItemId, int? prefabItemId, bool hasItemInstance, int? instanceItemId,
            int? instanceModuleCount, int? amountMinimum, int? amountMaximum, int amountToAdd, int? bundleCount,
            bool infinite, int additionalItemEntryCount, bool hasSpawnOnPickup, int remainingItemCountNotCollected,
            bool pendingDestroyThroughBolt, bool isBeingDestroyed, bool isCollected, bool used,
            bool activeInHierarchy, bool isPooled, int frame)
        {
            NetworkId = networkId;
            PrefabId = prefabId;
            IsServerOwned = isServerOwned;
            PickupTypeName = pickupTypeName;
            Kind = kind;
            ActivePickupCount = activePickupCount;
            TotalPickupCount = totalPickupCount;
            SelectedMatchesNativeLookup = selectedMatchesNativeLookup;
            ComponentItemId = componentItemId;
            PrefabItemId = prefabItemId;
            HasItemInstance = hasItemInstance;
            InstanceItemId = instanceItemId;
            InstanceModuleCount = instanceModuleCount;
            AmountMinimum = amountMinimum;
            AmountMaximum = amountMaximum;
            AmountToAdd = amountToAdd;
            BundleCount = bundleCount;
            Infinite = infinite;
            AdditionalItemEntryCount = additionalItemEntryCount;
            HasSpawnOnPickup = hasSpawnOnPickup;
            RemainingItemCountNotCollected = remainingItemCountNotCollected;
            PendingDestroyThroughBolt = pendingDestroyThroughBolt;
            IsBeingDestroyed = isBeingDestroyed;
            IsCollected = isCollected;
            Used = used;
            ActiveInHierarchy = activeInHierarchy;
            IsPooled = isPooled;
            Frame = frame;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Current-world entity ID, not a persistent pickup identity.</summary>
        public ulong NetworkId { get; }
        /// <summary>Bolt prefab ID, not an inventory item ID.</summary>
        public int PrefabId { get; }
        /// <summary>Always true on a snapshot; client-owned entities return NotServerOwned instead.</summary>
        public bool IsServerOwned { get; }
        /// <summary>Full IL2CPP type name of the selected component.</summary>
        public string PickupTypeName { get; }
        public PickupKind Kind { get; }
        /// <summary>Pickup components on active objects under the entity, including nested entities.</summary>
        public int ActivePickupCount { get; }
        /// <summary>All pickup components under the entity, including inactive objects.</summary>
        public int TotalPickupCount { get; }
        /// <summary>
        /// True when the selected component is the one GetComponentInChildren&lt;PickUp&gt;() returns, which the
        /// native destroy receiver uses. False when only a single inactive pickup exists.
        /// </summary>
        public bool SelectedMatchesNativeLookup { get; }
        /// <summary>The selected component's _itemId. The native FakeDrop branch grants the event's item ID, not this one.</summary>
        public int ComponentItemId { get; }
        /// <summary>Item ID shared by every pickup in the Bolt prefab template, or null when that lookup fails.</summary>
        public int? PrefabItemId { get; }
        /// <summary>Whether the ItemInstance backing field was set. The getter, which can create an instance, is not called.</summary>
        public bool HasItemInstance { get; }
        /// <summary>Backing instance's item ID. For dropped items this came from the dropping client's token.</summary>
        public int? InstanceItemId { get; }
        /// <summary>Backing instance's module count. Module contents are not validated.</summary>
        public int? InstanceModuleCount { get; }
        /// <summary>Lower bound of the configured _amount range, or null without one. Not the value rolled at collection.</summary>
        public int? AmountMinimum { get; }
        /// <summary>Upper bound of the configured _amount range, or null without one.</summary>
        public int? AmountMaximum { get; }
        /// <summary>Raw _amountToAdd. How MainEffect combines it with the range and bundle count is not established.</summary>
        public int AmountToAdd { get; }
        /// <summary>Raw CoopDynamicPickUp._bundleCount on the entity, or null without that component.</summary>
        public int? BundleCount { get; }
        public bool Infinite { get; }
        /// <summary>Entries in _additionalItems; their items and ranges are not copied.</summary>
        public int AdditionalItemEntryCount { get; }
        /// <summary>True when any spawn-on-pickup or spawn-after-pickup reference, or the spider spawn flag, is set.</summary>
        public bool HasSpawnOnPickup { get; }
        /// <summary>Raw count left behind by a partial collection, as tracked by this process.</summary>
        public int RemainingItemCountNotCollected { get; }
        /// <summary>
        /// The server's own lifecycle flag. For a remote collector it is normally false, because the collecting
        /// peer runs DestroyThroughBolt; false is not evidence that nobody collected the pickup.
        /// </summary>
        public bool PendingDestroyThroughBolt { get; }
        /// <inheritdoc cref="PendingDestroyThroughBolt"/>
        public bool IsBeingDestroyed { get; }
        /// <inheritdoc cref="PendingDestroyThroughBolt"/>
        public bool IsCollected { get; }
        /// <summary>Raw Used flag as seen by this process.</summary>
        public bool Used { get; }
        public bool ActiveInHierarchy { get; }
        /// <summary>
        /// Whether the PickUps pool reports the destroy target, or the pickup's own object without one, as spawned.
        /// False also when that pool is unavailable.
        /// </summary>
        public bool IsPooled { get; }
        /// <summary>Unity frame of the read.</summary>
        public int Frame { get; }
        /// <summary>UTC read completion time, not a replication or collection timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
