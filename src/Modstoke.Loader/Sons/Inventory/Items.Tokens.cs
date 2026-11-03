// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public static partial class ItemCatalog
    {
        /// <summary>
        /// Copies a positive requested item ID from an ItemInstanceBoltDataToken on the initialized
        /// Unity lifecycle thread. Failure returns itemId zero. Does not retain native objects or
        /// validate definitions, modules, quantities, inventory membership or authority.
        /// Callers must distinguish an absent token from an unreadable event getter before choosing
        /// any event-specific fallback; an unreadable or incompatible token provides no item ID.
        /// </summary>
        public static bool TryReadInstanceTokenItemId(Il2CppObjectBase token, out int itemId, out string status)
        {
            itemId = 0;
            status = "";
            if (!CanReadGameDatabase())
            {
                status = "Item catalog reads require the initialized Unity lifecycle thread.";
                return false;
            }

            try
            {
                if (token is null || token.WasCollected)
                {
                    status = "The item instance token is unavailable.";
                    return false;
                }
                var itemToken = token.TryCast<ItemInstanceBoltDataToken>();
                if (itemToken is null || itemToken.WasCollected)
                {
                    status = "The token is not a readable item instance token.";
                    return false;
                }
                var instance = itemToken.ItemInstance;
                if (instance is null || instance.WasCollected)
                {
                    status = "The token has no readable item instance.";
                    return false;
                }
                var id = instance._itemID;
                if (id <= 0)
                {
                    status = "The token does not identify a positive item ID.";
                    return false;
                }
                itemId = id;
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read the item instance token: " + error.Message;
                return false;
            }
        }
    }
}
