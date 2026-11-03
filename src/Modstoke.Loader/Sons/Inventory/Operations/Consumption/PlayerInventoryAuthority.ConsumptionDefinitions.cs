// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private InventoryConsumption.DefinitionCatalog _nativeEdibleDefinitions;

        /// <summary>Read the server's loaded original inventory prefab assets, without a client policy callback.</summary>
        public bool TryBindConsumptionDefinitions(out string detail)
        {
            detail = "Native edible definitions require idle prepared server inventory custody.";
            if (System.Environment.CurrentManagedThreadId != _thread || _nativeEdibleDefinitions != null
                || !HasAcceptedBaseline || !PreparedReady(out _)
                || _pending != null || _stow != null || _equip != null || HasNativeConsumptionReservation) return false;
            try
            {
                if (!InventoryConsumption.DefinitionCatalog.TryCapture(out var catalog, out detail)) return false;
                _nativeEdibleDefinitions = catalog;
                return true;
            }
            catch (Exception error) { detail = "Native edible catalog refused: " + error.Message; return false; }
        }

        internal bool TryAuthorizeNativeDefinedIntake(in PlayerConsumptionFrame request,
            out PlayerConsumptionFrame prepared, out string detail)
        {
            prepared = default;
            detail = "Exact server inventory custody and its native edible catalog are required.";
            if (!Ready(out _) || _nativeEdibleDefinitions == null
                || request.Debit != default || !_items.TryGetValue(request.Action.Key, out var item)
                || item.Id != request.Action.ItemId || !item.IdentityCurrent()) return false;
            try
            {
                global::Sons.Inventory.PerishableItemInstanceModule decay = null;
                if (_decay.TryGetValue(item.Key, out var clock))
                {
                    if (!clock.Item.IdentityCurrent() || clock.Item.Key != item.Key || clock.Item.Id != item.Id) return false;
                    decay = clock.Module;
                }
                if (!_nativeEdibleDefinitions.TryRead(item.Id, item.Item, decay, out int uses, out detail)) return false;
                // The established reservation still owns effect validation, exact actor/state,
                // module/debit receipts and one-attempt commit. Catalog reads grant none of those.
                return TryReserveConsumption(in request, uses, out prepared, out _, out detail);
            }
            catch (Exception error) { detail = "Server native edible definition refused: " + error.Message; return false; }
        }
    }
}
