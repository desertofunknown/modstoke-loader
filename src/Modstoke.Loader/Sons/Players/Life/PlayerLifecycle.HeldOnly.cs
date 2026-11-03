// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using TheForest.Items.Inventory;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        /// <summary>
        /// Parent includes this in native action draining BEFORE setting the final freeze.
        /// Does not cancel, replay or force-complete an already accepted native coroutine.
        /// </summary>
        public bool CanFreezeHeldOnlyWriters(PlayerInventory inventory, out string detail)
        {
            detail = "Exact native held-only calls, delayed renderers and outgoing custody must drain before freeze.";
            if (!Alive(Root) || !Alive(inventory) || LocalPlayer.Inventory?.Pointer != inventory.Pointer
                || !inventory.transform.IsChildOf(Root.transform)
                || PlayerInventoryEnforcement.HasObservedHeldOnlyWork(inventory)) return false;
            detail = "Observed native held-only work is drained; source capture still requires the final lifecycle pause.";
            return true;
        }

        public bool TryCaptureHeldOnlySource(BoltMigrationPlan plan, PlayerInventory inventory,
            out PlayerInventoryEnforcement.HeldOnlySource source, out string detail)
            => PlayerInventoryEnforcement.TryCaptureHeldOnlySource(plan, this, inventory, out source, out detail);
    }
}
