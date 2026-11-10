// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        private static readonly Dictionary<(IntPtr Inventory, int Item), InventoryLayout> LayoutOwners = new();
        private static readonly Dictionary<IntPtr, InventoryLayout> LayoutItemOwners = new();
        // Opening the inventory detaches the mat from the player to lay it out in the world, so the
        // container seen while attached stays the inventory's mat after it leaves the player's hierarchy.
        private static readonly Dictionary<IntPtr, Transform> MatContainers = new();
        private static bool _layoutHookAttempted, _layoutHookInstalled, _layoutRestoreWarned;
        private static string _layoutHookFailure = "";

        /// <summary>Creates an inactive counted inventory group with one visual row on the Unity lifecycle thread.</summary>
        /// <remarks>
        /// The non-unique definition must be published in the current item database. The anchor supplies the parent,
        /// placement and native hover/audio settings. Only one SDK layout may own this inventory/item pair.
        /// Add caller-owned visuals and hit colliders beneath Item before TryRefresh; native initialization
        /// installs mouse handling on renderer objects. Keep group/row ownership fields and parents intact.
        /// Root and its children are owned by the returned handle; materials and other assets remain yours.
        /// No stock, item definition, primary action, held/crafting layout, save or network behavior is created.
        /// False may return a partial handle: retain and dispose it before retrying creation.
        /// </remarks>
        public static bool TryCreateLayout(ItemData definition, InventoryLayoutItemGroup anchor,
            out InventoryLayout layout, out string status)
        {
            layout = null;
            if (!TryGetCurrentInventory(out var inventory, out status)) return false;
            try
            {
                if (!InventoryLayout.Published(definition) || definition._eachInstanceIsUnique)
                {
                    status = "An exact published, non-unique item definition is required for a counted layout.";
                    return false;
                }
                if (AnchorProblem(anchor, inventory) is { } problem)
                {
                    status = problem;
                    return false;
                }
                var manager = inventory._itemInstanceManager;
                int layer = LayerMask.NameToLayer("Inventory");
                var scene = inventory.gameObject.scene;
                if (!InventoryLayout.Live(manager) || layer < 0 || !scene.IsValid() || !scene.isLoaded)
                {
                    status = "The inventory scene, instance manager or rendering layer is unavailable.";
                    return false;
                }
                var key = (inventory.Pointer, definition._id);
                if (LayoutOwners.ContainsKey(key))
                {
                    status = "An SDK layout already owns this inventory/item pair. Release it first.";
                    return false;
                }
                if (!EnsureLayoutHook(out status)) return false;
                layout = new InventoryLayout(inventory, manager, definition, anchor.transform.parent, _ownerThreadId);
                LayoutOwners.Add(key, layout);
                layout.Create(anchor, layer);
                if (!LayoutInventoryCurrent(layout))
                {
                    status = "The inventory or layout ownership changed during creation. Release the returned handle.";
                    return false;
                }
                status = "";
                return true;
            }
            catch (Exception error)
            {
                status = "Could not create the inventory layout: " + error.Message;
                return false;
            }
        }

        // Only what Create reads: the anchor's placement under this inventory and one row's settings. An inactive
        // native group may never have bound its own inventory field, and its rows need not sit beneath it.
        private static string AnchorProblem(InventoryLayoutItemGroup anchor, PlayerInventory inventory)
        {
            if (!InventoryLayout.Live(anchor)) return "The anchor group is not live.";
            var container = anchor.transform.parent;
            if (!InventoryLayout.Live(container)) return "The anchor group has no live parent.";
            if (anchor.transform.IsChildOf(inventory.transform))
                MatContainers[inventory.Pointer] = container;
            else if (!InventoryLayout.Same(container, MatContainer(inventory)))
                return "The anchor group is not part of the current inventory.";
            var rows = anchor._layoutItems;
            return rows is null || rows.WasCollected || rows.Count == 0 || !InventoryLayout.Live(rows[0])
                ? "The anchor group has no live visual row." : null;
        }

        private static bool EnsureLayoutHook(out string status)
        {
            if (!_layoutHookAttempted)
            {
                if (HarmonySupport.CreateState()?.SafeForHarmonyPatches != true)
                {
                    status = "Harmony support is not ready for inventory layouts.";
                    return false;
                }
                _layoutHookAttempted = true;
                try
                {
                    var target = AccessTools.DeclaredMethod(typeof(InventoryLayoutItem), nameof(InventoryLayoutItem.OnDisable), Type.EmptyTypes)
                        ?? throw new MissingMethodException(typeof(InventoryLayoutItem).FullName, nameof(InventoryLayoutItem.OnDisable));
                    new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-inventory-layouts").Patch(target,
                        prefix: new HarmonyMethod(typeof(Inventory), nameof(BeforeLayoutItemDisable)) { priority = Priority.Last });
                    _layoutHookInstalled = true;
                }
                catch (Exception error)
                {
                    _layoutHookFailure = "Could not install inventory layout material cleanup: " + error.Message;
                    RuntimeEntryPoint.LogSource?.LogWarning(_layoutHookFailure + "\n" + error);
                }
            }
            status = _layoutHookFailure;
            return _layoutHookInstalled;
        }

        private static void BeforeLayoutItemDisable(InventoryLayoutItem __instance, bool __runOriginal)
        {
            try
            {
                if (__runOriginal && __instance is not null
                    && LayoutItemOwners.TryGetValue(__instance.Pointer, out var owner) && owner.OwnsItem(__instance)
                    && __instance._isInitialized)
                    // Native cleanup destroys temporary grey-out materials; restore the surviving geometry first.
                    __instance.ApplyOriginalMaterials();
            }
            catch (Exception error)
            {
                // Teardown may already have destroyed a renderer. Let the native cleanup still run.
                if (_layoutRestoreWarned) return;
                _layoutRestoreWarned = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Inventory layout materials could not be restored: " + error.Message);
            }
        }

        internal static bool LayoutInventoryCurrent(InventoryLayout layout)
            => TryGetCurrentInventory(out var inventory, out _) && layout.MatchesInventory(inventory);

        /// <summary>The inventory's mat group container, once seen attached to the player; otherwise null.</summary>
        internal static Transform MatContainer(PlayerInventory inventory)
            => inventory is not null && MatContainers.TryGetValue(inventory.Pointer, out var container)
                && InventoryLayout.Live(container) ? container : null;

        internal static string LayoutMismatch(InventoryLayout layout)
            => TryGetCurrentInventory(out var inventory, out var status) ? layout.MismatchReason(inventory) ?? "it matches again" : status;

        internal static void RegisterLayoutItem(InventoryLayout layout)
            => LayoutItemOwners.Add(layout.Item.Pointer, layout);

        internal static bool LayoutHasPrimaryAction(IntPtr group) => PrimaryActionBindings.ContainsKey(group);

        internal static void ReleaseLayout(InventoryLayout layout)
        {
            if (LayoutOwners.TryGetValue(layout.Key, out var owner) && ReferenceEquals(owner, layout))
                LayoutOwners.Remove(layout.Key);
            if (LayoutItemOwners.TryGetValue(layout.ItemKey, out owner) && ReferenceEquals(owner, layout))
                LayoutItemOwners.Remove(layout.ItemKey);
        }
    }

    /// <summary>Owns one native inventory group and visual row. Bindings and asset lifetimes remain caller-owned.</summary>
    public sealed class InventoryLayout : IDisposable
    {
        private PlayerInventory _inventory;
        private ItemInstanceManager _manager;
        private ItemData _definition;
        private Transform _parent;
        private readonly int _threadId;
        private int _itemInstanceId;
        private IntPtr _groupKey;
        private GameObject _itemObject;
        private bool _releasing;

        internal InventoryLayout(PlayerInventory inventory, ItemInstanceManager manager, ItemData definition,
            Transform parent, int threadId)
        {
            _inventory = inventory;
            _manager = manager;
            _definition = definition;
            _parent = parent;
            _threadId = threadId;
            Key = (inventory.Pointer, definition._id);
        }

        internal (IntPtr Inventory, int Item) Key { get; }
        internal IntPtr ItemKey { get; private set; }
        public int ItemId => Key.Item;
        public GameObject Root { get; private set; }
        public InventoryLayoutItemGroup Group { get; private set; }
        public InventoryLayoutItem Item { get; private set; }
        public bool IsDisposed { get; private set; }

        internal static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        internal static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;

        internal static bool Published(ItemData definition)
        {
            var cache = ItemDatabaseManager._itemsCache;
            return Live(definition) && definition._id > 0 && cache is not null && !cache.WasCollected
                && cache.TryGetValue(definition._id, out var current) && Same(definition, current);
        }

        internal void Create(InventoryLayoutItemGroup anchor, int layer)
        {
            Root = new GameObject(_definition._name + " inventory group");
            Root.SetActive(false);
            Root.layer = layer;
            Root.transform.SetParent(_parent, false);
            Root.transform.localPosition = anchor.transform.localPosition;
            Root.transform.localRotation = anchor.transform.localRotation;
            Group = Root.AddComponent<InventoryLayoutItemGroup>();
            _groupKey = Group.Pointer;
            Group._itemId = ItemId;
            Group._itemData = _definition;
            Group._inventory = _inventory;
            Group._isOneToOneCollection = false;
            Group._isInfiniteGroupCount = false;
            Group._alwaysShowIfOwned = true;
            Group._startDisabled = false;
            Group._layoutItems = new();
            Group._onInteractAudioEvent = anchor._onInteractAudioEvent;
            Group._animationClip = null;
            Group._animationSpeedMultiplier = 1;
            Group._delayTime = 0;
            _itemObject = new GameObject(_definition._name + " inventory item");
            _itemObject.SetActive(false);
            _itemObject.layer = layer;
            _itemObject.transform.SetParent(Root.transform, false);
            Item = _itemObject.AddComponent<InventoryLayoutItem>();
            ItemKey = Item.Pointer;
            _itemInstanceId = Item.GetInstanceID();
            Inventory.RegisterLayoutItem(this);
            Item._doNotSnap = true;
            Item._setToInventoryLayer = true;
            Item._uiTriggerDelay = anchor._layoutItems[0]._uiTriggerDelay;
            Item._colliderActivationRange = anchor._layoutItems[0]._colliderActivationRange;
            Group._layoutItems.Add(Item);
        }

        internal bool OwnsItem(InventoryLayoutItem item)
            => Live(Item) && Same(Item, item) && item.GetInstanceID() == _itemInstanceId
                && Live(Root) && Same(item.transform.parent, Root.transform);

        internal bool MatchesInventory(PlayerInventory inventory) => MismatchReason(inventory) == null;

        /// <summary>Why this layout no longer belongs to the inventory, or null when it still does.</summary>
        internal string MismatchReason(PlayerInventory inventory)
        {
            if (_releasing || IsDisposed) return "it is being released";
            if (!Same(_inventory, inventory) || !Same(_manager, inventory._itemInstanceManager)) return "the inventory changed";
            if (!Published(_definition) || _definition._eachInstanceIsUnique || _definition._id != ItemId)
                return "the item definition is no longer published";
            if (!Live(Root) || !Live(Group) || !Same(Group.gameObject, Root) || !OwnsItem(Item)) return "its objects were destroyed";
            if (!Same(Root.transform.parent, _parent)) return "it was moved to another parent";
            if (!Root.transform.IsChildOf(inventory.transform) && !Same(_parent, Inventory.MatContainer(inventory)))
                return "its parent left the inventory";
            if (!Same(Group._inventory, inventory) || !Same(Group._itemData, _definition) || Group._itemId != ItemId)
                return "the game rebound its group";
            var items = Group._layoutItems;
            if (items is null || items.WasCollected || items.Count != 1 || !Same(items[0], Item))
                return "the game changed its rows (" + (items is null || items.WasCollected ? "none" : items.Count.ToString()) + ")";
            return Group._isOneToOneCollection || Group._isInfiniteGroupCount ? "the game changed its group mode" : null;
        }

        /// <summary>
        /// Refreshes the current native group, hiding it when stock is zero. Add all visuals before the
        /// first refresh with stock: native initialization caches renderers once. False retains ownership
        /// of any partial activation; hide/dispose explicitly. This never grants or removes items.
        /// </summary>
        public bool TryRefresh(out string status)
        {
            if (!OnThread(out status)) return false;
            try
            {
                if (!Inventory.LayoutInventoryCurrent(this))
                {
                    status = "The inventory layout is no longer current: " + Inventory.LayoutMismatch(this) + ".";
                    return false;
                }
                int count = _manager.GetItemCount(ItemId);
                if (count < 0 || !Inventory.LayoutInventoryCurrent(this))
                {
                    status = "The inventory stock or layout changed before refresh.";
                    return false;
                }
                if (count == 0) return TryHide(out status);
                // OnEnable must see an item instance, or native initialization misses the authored geometry.
                Root.SetActive(true);
                Group.RefreshItems();
                if (!Inventory.LayoutInventoryCurrent(this))
                {
                    status = "The inventory layout changed during refresh: " + Inventory.LayoutMismatch(this) + ".";
                    return false;
                }
                status = "";
                return true;
            }
            catch (Exception error)
            {
                status = "Could not refresh the inventory layout: " + error.Message;
                return false;
            }
        }

        /// <summary>Deactivates this captured layout, including after its player lifetime ends; bindings stay owned.</summary>
        public bool TryHide(out string status)
        {
            if (!OnThread(out status)) return false;
            try
            {
                if (Live(Root)) Root.SetActive(false);
                return true;
            }
            catch (Exception error)
            {
                status = "Could not hide the inventory layout: " + error.Message;
                return false;
            }
        }

        /// <summary>
        /// Hides and destroys owned group/row objects on their creating thread. Release primary-action
        /// bindings and caller-owned model resources after hiding and before disposal. Failed cleanup
        /// remains owned and retryable; a releasing layout cannot be refreshed again.
        /// </summary>
        public bool TryDispose(out string status)
        {
            status = "";
            if (IsDisposed) return true;
            if (!OnThread(out status)) return false;
            _releasing = true;
            if (!TryHide(out status)) return false;
            if (Inventory.LayoutHasPrimaryAction(_groupKey))
            {
                status = "Release the inventory group's primary-action binding before disposing its layout.";
                return false;
            }
            try
            {
                if (Live(_itemObject) && (!Live(Root) || !Same(_itemObject.transform.parent, Root.transform)))
                    UnityEngine.Object.Destroy(_itemObject);
                if (Live(Root)) UnityEngine.Object.Destroy(Root);
                Inventory.ReleaseLayout(this);
                Root = _itemObject = null;
                Group = null;
                Item = null;
                _inventory = null;
                _manager = null;
                _definition = null;
                _parent = null;
                IsDisposed = true;
                return true;
            }
            catch (Exception error)
            {
                status = "Could not dispose the inventory layout: " + error.Message;
                return false;
            }
        }

        private bool OnThread(out string status)
        {
            status = Environment.CurrentManagedThreadId == _threadId ? ""
                : "Inventory layout operations require their creating Unity lifecycle thread.";
            return status.Length == 0;
        }

        public void Dispose()
        {
            if (!TryDispose(out var status)) throw new InvalidOperationException(status);
        }
    }
}
