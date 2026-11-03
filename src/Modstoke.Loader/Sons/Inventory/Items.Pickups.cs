// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class ItemCatalog
    {
        /// <summary>
        /// Copies the positive item ID shared by every pickup under the supplied root, including
        /// inactive children. Requires the initialized Unity lifecycle thread. Empty, unreadable
        /// or mixed-item hierarchies fail with itemId zero. Does not read item definitions,
        /// retain native objects, validate network identity or authorize item mutation.
        /// </summary>
        public static bool TryReadPickupItemId(GameObject root, out int itemId, out string status)
        {
            itemId = 0;
            if (!CanReadGameDatabase())
            {
                status = "Item catalog reads require the initialized Unity lifecycle thread.";
                return false;
            }
            return TryReadPickupItemIdCore(root, out itemId, out status);
        }

        private static bool TryReadPickupItemIdCore(GameObject root, out int itemId, out string status)
        {
            itemId = 0;
            status = "";
            try
            {
                if (root is null || root.WasCollected || !root)
                {
                    status = "The pickup root is unavailable.";
                    return false;
                }
                var pickups = root.GetComponentsInChildren<PickUp>(true);
                if (pickups is null || pickups.WasCollected)
                {
                    status = "The pickup hierarchy is unavailable.";
                    return false;
                }
                var candidate = 0;
                var count = pickups.Length;
                for (var index = 0; index < count; index++)
                {
                    var pickup = pickups[index];
                    if (pickup is null || pickup.WasCollected || !pickup)
                    {
                        status = "The hierarchy contains an unavailable pickup component.";
                        return false;
                    }
                    var id = pickup._itemId;
                    if (id <= 0 || (candidate != 0 && candidate != id))
                    {
                        status = "The pickup hierarchy does not identify one positive item ID.";
                        return false;
                    }
                    candidate = id;
                }
                if (candidate == 0)
                {
                    status = "The hierarchy contains no pickup components.";
                    return false;
                }
                itemId = candidate;
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read the pickup hierarchy: " + error.Message;
                return false;
            }
        }
    }
}
