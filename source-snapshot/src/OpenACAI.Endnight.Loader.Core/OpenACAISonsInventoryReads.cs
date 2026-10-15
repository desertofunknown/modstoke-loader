// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Items.Inventory;
using TheForest.Utils;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsInventory
    {
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

        private static bool TryGetCurrentInventory(int itemId, out PlayerInventory inventory, out string status)
        {
            inventory = null;
            status = "";
            if (itemId <= 0)
            {
                status = "A positive Sons item ID is required.";
                return false;
            }
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = "Inventory reads require the initialized Unity lifecycle thread.";
                return false;
            }

            try
            {
                if (!LocalPlayer.IsInWorld)
                {
                    status = "An in-world local player is required to read inventory.";
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
}
