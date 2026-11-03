// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        private static readonly ConcurrentDictionary<nint, InventoryStorage> ManagerHolds = new();
        private bool _managerPermit;
        private int _managerPermitDepth;

        // Exact one-shot permits are consumed before callbacks. A reentrant/competing writer
        // cannot borrow the operation's busy scope, including during contents publication.
        internal static bool IsManagerHeld(ItemInstanceManager manager)
            => manager != null && !manager.WasCollected && ManagerHolds.TryGetValue(manager.Pointer, out var pending)
                && pending._load == SaveLifecycle.State.BeforeLoadCount;

        internal static bool PermitManagerWrite(ItemInstanceManager manager, int id, int count,
            ItemInstance instance, bool addition, bool suppressCallback)
        {
            if (manager == null || manager.WasCollected || !ManagerHolds.TryGetValue(manager.Pointer, out var pending)
                || pending._load != SaveLifecycle.State.BeforeLoadCount
                || !pending._managerPermit || !pending._busy || Environment.CurrentManagedThreadId != pending._thread
                || PlayerInventoryEnforcement.WorldWriterDepth != pending._managerPermitDepth + 1
                || id != pending.Action.ItemId || count != 1 || suppressCallback
                || (addition ? pending.State != TransferState.ApplyingDestination || !Same(instance, pending.Item)
                    : pending.State != TransferState.TakingSource || instance != null)) return false;
            pending._managerPermit = false;
            return true;
        }

        private int RemoveReservedStorageItem()
        {
            _managerPermit = true;
            _managerPermitDepth = PlayerInventoryEnforcement.WorldWriterDepth;
            try
            {
                int removed = _manager.RemoveItems(Action.ItemId, 1, false);
                return _managerPermit ? 0 : removed;
            }
            finally { _managerPermit = false; }
        }

        private bool AddReservedStorageItem()
        {
            _managerPermit = true;
            _managerPermitDepth = PlayerInventoryEnforcement.WorldWriterDepth;
            try
            {
                bool added = _manager.TryAddItem(Item, false);
                return !_managerPermit && added;
            }
            finally { _managerPermit = false; }
        }
    }
}
