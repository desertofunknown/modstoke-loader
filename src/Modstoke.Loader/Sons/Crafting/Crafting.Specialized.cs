// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Inventory;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        private static void RequireIngredientTransferLayout(IngredientLayoutItemGroup group,
            List<CraftTransferLayout> rows, int count, bool unique)
        {
            int capacity = group._layoutItems.Count;
            RequireCraftTransfer(capacity > 0 && count >= 0 && (unique
                ? count == rows.Count
                : rows.Count == Math.Min(count, capacity)),
                "The native ingredient count and available layout slots disagree.");
        }

        private static void RequireNativeIngredientReturn(int itemId, bool countOnly)
        {
            if (itemId != 483 || countOnly) return;
            // AddBackToInventory's overflow-count branch precedes this native gate.
            // The visible-row branch skips the add after death but still removes the row.
            var vitals = LocalPlayer.Vitals;
            RequireCraftTransfer(CraftExecutionLive(vitals) && !vitals._hasDied,
                "Native return of item 483 would skip its inventory add after player death, or the vitals state is unavailable.");
        }

        // A full stack layout represents a count, not one retained instance per ingredient.
        // These are before/after observations only; nothing is stored on the native objects.
        private sealed class CraftCompressedIngredientTransfer
        {
            private readonly IngredientLayoutItemGroup _group;
            private readonly int _itemId, _capacity;
            private readonly IntPtr _list, _data;
            private readonly List<CraftTransferLayout> _rows;
            private readonly CraftModuleIdentity[] _modules;

            internal CraftCompressedIngredientTransfer(IngredientLayoutItemGroup group,
                List<CraftTransferLayout> rows, int itemId)
            {
                var data = group.GetItemData();
                RequireCraftTransfer(CraftExecutionLive(data) && data.Id == itemId
                    && !data._eachInstanceIsUnique && !group._isOneToOneCollection
                    && rows.Count > 0 && rows.Count == group._layoutItems.Count,
                    "Count-only transfers require a full nonunique native ingredient layout.");
                _group = group;
                _itemId = itemId;
                _capacity = rows.Count;
                _list = group._layoutItems.Pointer;
                _data = data.Pointer;
                _rows = rows;
                _modules = new CraftModuleIdentity[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    var instanceData = rows[i].Instance.Data;
                    RequireCraftTransfer(CraftExecutionLive(instanceData) && instanceData.Pointer == _data,
                        "A compressed ingredient row has a different native item definition.");
                    _modules[i] = new CraftModuleIdentity(rows[i].Instance, itemId);
                    RequireCraftTransfer(_modules[i].Count == 0,
                        "Compressed native stack counts cannot retain per-instance ingredient modules.");
                }
            }

            internal bool Current()
            {
                if (!CraftExecutionLive(_group) || !_group.isActiveAndEnabled
                    || _group._itemId != _itemId || _group._isOneToOneCollection) return false;
                var rows = _group._layoutItems;
                if (rows is null || rows.WasCollected || rows.Pointer != _list || rows.Count != _capacity) return false;
                var data = _group.GetItemData();
                if (!CraftExecutionLive(data) || data.Pointer != _data || data.Id != _itemId || data._eachInstanceIsUnique)
                    return false;
                if (!TransferLayoutsSame(_rows, ReadTransferLayouts(rows, _itemId))) return false;
                for (int i = 0; i < _modules.Length; i++)
                {
                    var instanceData = _rows[i].Instance.Data;
                    if (!_modules[i].Current() || !CraftExecutionLive(instanceData) || instanceData.Pointer != _data)
                        return false;
                }
                return true;
            }
        }
    }
}
