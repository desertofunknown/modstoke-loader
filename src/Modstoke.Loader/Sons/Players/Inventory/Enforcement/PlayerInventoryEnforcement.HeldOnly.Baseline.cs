// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Items.Inventory;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        /// <summary>
        /// Narrow replacement for the inventory baseline's empty-held check, not for
        /// its equip/view/projectile guards. Capture HeldOnlySource under the settled
        /// lifecycle freeze first and include/approve its exact snapshot separately.
        /// The native representative remains in that controller, never ordinary storage.
        /// </summary>
        internal static bool TryReadHeldOnlyBaselineCustody(BoltMigrationPlan plan,
            PlayerInventory inventory, out Held.Snapshot snapshot, out string detail)
        {
            snapshot = null;
            detail = "Nonempty held custody needs the retained native source and its unchanged separately approved snapshot.";
            var source = _heldOnlySource;
            if (source == null || source.Baseline.Plan != plan || !source.Lifetime(false)
                || source.Inventory?.Pointer != inventory?.Pointer || !source.IsNativeGuardHeld
                || source.HasPendingCustody || source.HasUnownedForcedCallback || _heldCall != null
                || HasObservedHeldOnlyWork(inventory)) return false;
            try
            {
                Held.Snapshot current;
                if (source.Bridge == null)
                {
                    if (!PlayerLifecycle.IsInventoryIngressFenced(inventory, out bool frozen) || !frozen
                        || !Held.Snapshot.TryCaptureGuarded(plan, inventory, source.Controller, out current, out detail)) return false;
                }
                else if (!source.Lifetime(true, false)
                    || !Held.Snapshot.TryCaptureRetained(source.Bridge, inventory, source.Controller, out current, out detail)) return false;
                if (!source.MatchesSettledCustody(current)) return false;
                var representative = source.Controller._heldItemInstance;
                var manager = inventory._itemInstanceManager;
                if (!HeldOnlySource.Live(manager) || manager._itemsMap == null || manager._itemsMap.WasCollected
                    || inventory._equipmentSlots == null || inventory._equipmentSlots.WasCollected) return false;
                if (representative != null)
                {
                    foreach (var item in inventory._equipmentSlots)
                        if (item?.Pointer == representative.Pointer) return false;
                    foreach (int id in manager._itemsMap.Keys)
                    {
                        var stored = manager._itemsMap[id];
                        if (stored == null || stored.WasCollected || stored.UniqueInstances == null || stored.UniqueInstances.WasCollected) return false;
                        foreach (var item in stored.UniqueInstances)
                            if (item?.Pointer == representative.Pointer) return false;
                    }
                }
                snapshot = current;
                detail = "Exact guarded native held representative, modules and raw count remain separate from ordinary inventory custody.";
                return true;
            }
            catch (Exception error) { detail = "Held baseline custody unavailable: " + error.Message; return false; }
        }
    }
}
