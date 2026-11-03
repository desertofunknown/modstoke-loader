// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public static partial class ItemCatalog
    {
        /// <summary>
        /// Reads the item ID shared by every pickup in a Bolt prefab, including inactive children,
        /// and requires a live matching item definition. Requires the initialized Unity lifecycle
        /// thread. Empty, unreadable or mixed-item hierarchies fail with itemId zero.
        /// This template observation does not validate a live pickup or authorize item mutation.
        /// </summary>
        public static bool TryReadPrefabItemId(int prefabId, out int itemId, out string status)
        {
            itemId = 0;
            status = "";
            if (prefabId <= 0)
            {
                status = "A positive Bolt prefab ID is required.";
                return false;
            }
            if (!CanReadGameDatabase())
            {
                status = "Item catalog reads require the initialized Unity lifecycle thread.";
                return false;
            }

            try
            {
                var nativeId = new Bolt.PrefabId(prefabId);
                if (nativeId.Value != prefabId)
                {
                    status = "The native prefab ID did not match the requested value.";
                    return false;
                }
                var prefab = Bolt.PrefabDatabase.Find(nativeId);
                if (!TryReadPickupItemIdCore(prefab, out var candidate, out status))
                    return false;

                var definition = ItemDatabaseManager.ItemById(candidate);
                if (definition is null || definition.WasCollected || !definition || definition.Id != candidate)
                {
                    status = "The prefab item has no live matching item definition.";
                    return false;
                }
                itemId = candidate;
                return true;
            }
            catch (Exception error)
            {
                status = "Could not resolve the prefab item: " + error.Message;
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Modstoke SDK item lookup for Bolt prefab " + prefabId + " failed: " + error);
                return false;
            }
        }
    }
}
