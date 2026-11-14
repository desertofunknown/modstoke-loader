// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TheForest.Items.Inventory;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        /// <summary>Maximum number of requested entries in a single inventory capture, including duplicates.</summary>
        public const int MaxCaptureItems = 256;

        /// <summary>
        /// Copies the local inventory view on the initialized Unity lifecycle thread.
        /// No view changes, item reads or crafting callbacks are invoked. False returns
        /// a default snapshot, not evidence that inventory is closed. A successful
        /// observation does not establish whether an inventory action is permitted.
        /// </summary>
        public static bool TryReadView(out InventoryViewSnapshot snapshot, out string status)
        {
            snapshot = default;
            if (!TryGetCurrentInventory(out var inventory, out status)) return false;

            try
            {
                var view = inventory._currentView;
                if (!TryGetCurrentInventory(out var currentInventory, out status)) return false;
                if (inventory.WasCollected || !inventory || currentInventory.Pointer != inventory.Pointer
                    || currentInventory._currentView != view)
                {
                    status = "The local inventory view changed during capture. No snapshot was returned.";
                    return false;
                }

                snapshot = new InventoryViewSnapshot(view);
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read the local inventory view: " + error.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads PlayerInventory.AmountOf on the initialized Unity lifecycle thread.
        /// Both options are forwarded to the game unchanged. False means the read
        /// failed, not that the inventory contains zero items.
        /// </summary>
        public static bool TryGetItemCount(int itemId, out int count, out string status,
            bool includeEquipped = true, bool checkNextSlot = false)
        {
            count = 0;
            if (!TryGetCurrentInventory(itemId, out var inventory, out status))
                return false;

            try
            {
                count = inventory.AmountOf(itemId, includeEquipped, checkNextSlot);
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read the inventory item count: " + error.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads the game's Owns predicate. The return value indicates a successful
        /// query; ownsItem contains the result and is not inferred from a count.
        /// </summary>
        public static bool TryHasItem(int itemId, out bool ownsItem, out string status)
        {
            ownsItem = false;
            if (!TryGetCurrentInventory(itemId, out var inventory, out status))
                return false;

            try
            {
                ownsItem = inventory.Owns(itemId);
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read inventory ownership: " + error.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads the game's TryGetEquipped result without exposing or retaining its
        /// native ItemInstance. A successful query can report equipped=false.
        /// </summary>
        public static bool TryIsItemEquipped(int itemId, out bool equipped, out string status)
        {
            equipped = false;
            if (!TryGetCurrentInventory(itemId, out var inventory, out status))
                return false;

            try
            {
                equipped = inventory.TryGetEquipped(itemId, out _);
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read equipped item state: " + error.Message;
                return false;
            }
        }

        /// <summary>
        /// Captures count, ownership and equipped state for up to MaxCaptureItems requested IDs.
        /// Input order and duplicates are preserved; each distinct positive ID is read once.
        /// Count options are forwarded to AmountOf only, never used to infer ownership or equipment.
        /// False means the whole capture failed and entries is empty. True means a capture is
        /// available; inspect each entry's ReadSucceeded before using it, since invalid IDs and
        /// failed item reads are not absent items. Oversized requests fail without truncation.
        /// Requires the initialized Unity lifecycle thread. Results contain managed values only
        /// and are sequential observations, not an atomic inventory transaction.
        /// </summary>
        public static bool TryCaptureItems(IReadOnlyList<int> itemIds,
            out IReadOnlyList<InventoryItemCapture> entries, out string status,
            bool includeEquipped = true, bool checkNextSlot = false)
        {
            entries = Array.Empty<InventoryItemCapture>();
            status = "";
            if (itemIds == null)
            {
                status = "An item ID list is required.";
                return false;
            }

            int[] requested;
            try
            {
                var requestedCount = itemIds.Count;
                if (requestedCount > MaxCaptureItems)
                {
                    status = "Inventory captures accept at most " + MaxCaptureItems
                        + " entries, including duplicates. No items were read.";
                    return false;
                }

                // Finish caller-owned collection access before acquiring the native inventory.
                requested = new int[requestedCount];
                for (var index = 0; index < requested.Length; index++)
                    requested[index] = itemIds[index];
            }
            catch (Exception error)
            {
                status = "Could not read the requested item IDs: " + error.Message;
                return false;
            }

            if (!TryGetCurrentInventory(out var inventory, out status))
                return false;

            try
            {
                var captured = new InventoryItemCapture[requested.Length];
                var byItemId = new Dictionary<int, InventoryItemCapture>(requested.Length);
                var hasFailures = false;
                for (var index = 0; index < requested.Length; index++)
                {
                    if (inventory.WasCollected || !inventory)
                    {
                        status = "The local player's inventory became unavailable during capture. No results were returned.";
                        return false;
                    }

                    var itemId = requested[index];
                    if (!byItemId.TryGetValue(itemId, out var item))
                    {
                        item = CaptureItem(inventory, itemId, includeEquipped, checkNextSlot);
                        byItemId.Add(itemId, item);
                    }
                    captured[index] = item;
                    hasFailures |= !item.ReadSucceeded;
                }

                if (!TryGetCurrentInventory(out var currentInventory, out status))
                    return false;
                if (currentInventory.Pointer != inventory.Pointer)
                {
                    status = "The local player's inventory changed during capture. No results were returned.";
                    return false;
                }

                entries = Array.AsReadOnly(captured);
                status = hasFailures ? "Some item reads failed. Inspect each entry's Status." : "";
                return true;
            }
            catch (Exception error)
            {
                status = "Could not capture the local player's inventory: " + error.Message;
                return false;
            }
        }

        private static InventoryItemCapture CaptureItem(PlayerInventory inventory, int itemId,
            bool includeEquipped, bool checkNextSlot)
        {
            if (itemId <= 0)
                return new InventoryItemCapture(itemId, null, null, null,
                    "A positive Sons item ID is required.");

            try
            {
                var count = inventory.AmountOf(itemId, includeEquipped, checkNextSlot);
                var ownsItem = inventory.Owns(itemId);
                var equipped = inventory.TryGetEquipped(itemId, out _);
                return new InventoryItemCapture(itemId, count, ownsItem, equipped, "");
            }
            catch (Exception error)
            {
                return new InventoryItemCapture(itemId, null, null, null,
                    "Could not capture inventory item state: " + error.Message);
            }
        }

        private static bool TryGetCurrentInventory(int itemId, out PlayerInventory inventory, out string status)
        {
            inventory = null;
            status = "";
            if (itemId <= 0)
            {
                status = "A positive Sons item ID is required.";
                return false;
            }
            return TryGetCurrentInventory(out inventory, out status);
        }

        internal static bool TryGetCurrentInventory(out PlayerInventory inventory, out string status)
        {
            inventory = null;
            status = "";
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = "Inventory reads require the initialized Unity lifecycle thread.";
                return false;
            }

            try
            {
                var localPlayer = LocalPlayer._instance;
                if (localPlayer is null || localPlayer.WasCollected || !localPlayer)
                {
                    status = "A live local player is required to read inventory.";
                    return false;
                }

                var current = LocalPlayer.Inventory;
                if (current is null || current.WasCollected || !current)
                {
                    status = "The local player's inventory is not available.";
                    return false;
                }

                inventory = current;
                return true;
            }
            catch (Exception error)
            {
                status = "Could not access the local player's inventory: " + error.Message;
                return false;
            }
        }
    }

    /// <summary>A copied local view observation; no native inventory or player is retained.</summary>
    public readonly struct InventoryViewSnapshot
    {
        internal InventoryViewSnapshot(PlayerInventory.PlayerViews view)
        {
            NativeView = (int)view;
            IsInventoryOpen = view == PlayerInventory.PlayerViews.Inventory;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Raw PlayerViews value, including values unknown to this SDK.</summary>
        public int NativeView { get; }
        /// <summary>True only for the game's Inventory view; other and unknown views are false.</summary>
        public bool IsInventoryOpen { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>A managed observation of one requested item. Failed reads have null values, not zero or false.</summary>
    public sealed class InventoryItemCapture
    {
        internal InventoryItemCapture(int itemId, int? count, bool? ownsItem, bool? equipped,
            string status)
        {
            ItemId = itemId;
            Count = count;
            OwnsItem = ownsItem;
            Equipped = equipped;
            Status = status;
        }

        public int ItemId { get; }
        /// <summary>True only when all three native reads completed; zero or false can be successful results.</summary>
        public bool ReadSucceeded => Count.HasValue && OwnsItem.HasValue && Equipped.HasValue;
        /// <summary>Raw AmountOf result with the capture's includeEquipped and checkNextSlot options.</summary>
        public int? Count { get; }
        /// <summary>The game's Owns result, independent of the count options.</summary>
        public bool? OwnsItem { get; }
        /// <summary>The game's TryGetEquipped result. No native ItemInstance is retained.</summary>
        public bool? Equipped { get; }
        /// <summary>Empty on success; otherwise explains why this item could not be captured.</summary>
        public string Status { get; }
    }
}
