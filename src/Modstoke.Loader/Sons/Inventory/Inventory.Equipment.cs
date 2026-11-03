// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        private static readonly (ItemEquipmentSlot Slot, EquipmentSlot NativeSlot)[] CapturedEquipmentSlots =
        {
            (ItemEquipmentSlot.RightHand, EquipmentSlot.RightHand),
            (ItemEquipmentSlot.LeftHand, EquipmentSlot.LeftHand),
            (ItemEquipmentSlot.Chest, EquipmentSlot.Chest),
            (ItemEquipmentSlot.Feet, EquipmentSlot.Feet),
            (ItemEquipmentSlot.FullBody, EquipmentSlot.FullBody),
            (ItemEquipmentSlot.Eyes, EquipmentSlot.Eyes)
        };

        /// <summary>Requests one native stow of the current local slot's item.</summary>
        /// <remarks>
        /// True means the slot was observed empty after the call, not that animation or replication finished.
        /// An attempted call may have effects even when false is returned; do not automatically retry it.
        /// Requires an idle equipment state, local ownership and the Unity lifecycle thread.
        /// </remarks>
        public static bool TryStowEquipmentSlot(ItemEquipmentSlot slot, out EquipmentStowResult result)
            => TryStowEquipmentSlot(slot, null, out result);

        /// <summary>Requests one native stow only when the current local slot matches a positive expected item ID.</summary>
        /// <remarks>
        /// The expected ID identifies an item type, not unique-instance custody.
        /// True means the slot was observed empty after the call, not that animation or replication finished.
        /// An attempted call may have effects even when false is returned; do not automatically retry it.
        /// Requires an idle equipment state, local ownership and the Unity lifecycle thread.
        /// </remarks>
        public static bool TryStowEquipmentSlot(ItemEquipmentSlot slot, int expectedItemId, out EquipmentStowResult result)
            => TryStowEquipmentSlot(slot, (int?)expectedItemId, out result);

        private static bool TryStowEquipmentSlot(ItemEquipmentSlot slot, int? expectedItemId, out EquipmentStowResult result)
        {
            result = new(slot, false, null, null, null, "Not attempted.");
            if (expectedItemId.HasValue && expectedItemId.Value <= 0)
            {
                result = new(slot, false, null, null, null, "A positive expected item ID is required.");
                return false;
            }
            var nativeSlot = default(EquipmentSlot);
            var knownSlot = false;
            foreach (var candidate in CapturedEquipmentSlots)
            {
                if (candidate.Slot != slot) continue;
                nativeSlot = candidate.NativeSlot;
                knownSlot = true;
                break;
            }
            if (!knownSlot)
            {
                result = new(slot, false, null, null, null, "A known equipment slot is required.");
                return false;
            }
            if (!TryGetCurrentInventory(out var inventory, out var detail))
            {
                result = new(slot, false, null, null, null, detail);
                return false;
            }
            if (_executingMutation)
            {
                result = new(slot, false, null, null, null, "An SDK inventory action is already executing; no action was queued.");
                return false;
            }

            InventoryEquipmentSlotCapture before = null, after = null;
            var attempted = false;
            _executingMutation = true;
            try
            {
                before = CaptureEquipmentSlot(inventory, slot, nativeSlot);
                if (!before.ReadSucceeded || before.CurrentItemFound != true || before.NextItemFound != false
                    || before.NativeSlotLockResult == true || !EquipmentStowIdle(inventory))
                {
                    result = new(slot, false, before, null, null,
                        "Stow requires a readable occupied slot and idle equipment. " + before.Status);
                    return false;
                }
                var itemId = before.CurrentItemId.Value;
                if (expectedItemId.HasValue && itemId != expectedItemId.Value)
                {
                    result = new(slot, false, before, null, null,
                        "The slot's current item does not match the expected item ID; no stow was invoked.");
                    return false;
                }
                if (!TryGetMutationContext(inventory, itemId, out var context, out var data, out detail))
                {
                    result = new(slot, false, before, null, null, detail);
                    return false;
                }
                if (data._canNotBeStored || data._alwaysDropOnUnequip)
                {
                    result = new(slot, false, before, null, null, "This item requires a drop workflow, not stow.");
                    return false;
                }
                if (!inventory.TryGetItemInSlot(nativeSlot, out var item) || item is null || item.WasCollected
                    || item.Pointer == IntPtr.Zero || item._itemID != itemId)
                {
                    result = new(slot, false, before, null, null, "The slot's current item changed before stow.");
                    return false;
                }
                if (!MutationContextCurrent(inventory, in context, out detail)
                    || PlayerInventoryEnforcement.IsEquipmentAuthorityHeld(inventory)
                    || !EquipmentStowIdle(inventory) || inventory.TryGetItemInNextSlot(nativeSlot, out _)
                    || !inventory.TryGetItemInSlot(nativeSlot, out var current) || current is null || current.WasCollected
                    || current.Pointer != item.Pointer)
                {
                    result = new(slot, false, before, null, null, "Equipment or inventory changed before stow. " + detail);
                    return false;
                }

                attempted = true;
                // Keep normal destruction and multiplayer callbacks; this is not a custody-transfer transaction.
                inventory.UnequipItemAtSlot(nativeSlot, false, true, false);
                if (!MutationContextCurrent(inventory, in context, out detail))
                {
                    result = new(slot, true, before, null, null,
                        "Stow returned, but the local inventory changed. Do not automatically retry. " + detail);
                    return false;
                }
                after = CaptureEquipmentSlot(inventory, slot, nativeSlot);
                if (!MutationContextCurrent(inventory, in context, out detail))
                {
                    after = null;
                    result = new(slot, true, before, null, null,
                        "Inventory changed during stow readback. Do not automatically retry. " + detail);
                    return false;
                }
                bool? empty = after.CurrentReadSucceeded && after.NextReadSucceeded
                    ? after.CurrentItemFound == false && after.NextItemFound == false : null;
                result = new(slot, true, before, after, empty,
                    (empty == true ? "Slot observed empty; animation and replication may still be pending. "
                        : "Stow returned without a confirmed empty slot; do not automatically retry. ") + after.Status);
                return empty == true;
            }
            catch (Exception error)
            {
                result = new(slot, attempted, before, after, null,
                    (attempted ? "Stow is unconfirmed; do not automatically retry. " : "No stow was invoked. ") + error.Message);
                return false;
            }
            finally
            {
                _executingMutation = false;
            }
        }

        private static bool EquipmentStowIdle(PlayerInventory inventory)
        {
            var props = inventory._InventoryProps_k__BackingField;
            return inventory._currentView == PlayerInventory.PlayerViews.World
                && inventory._currentEquipState == PlayerInventory.HandEquipState.Idle
                && inventory._equipSequenceRight == null && props is not null && !props.WasCollected && props
                && props._leftHandWideBlendCoroutine == null && !inventory._IsThrowing_k__BackingField
                && !inventory._IsInQuickSelect_k__BackingField && inventory.HeldOnlyItemController?.HasItem != true;
        }

        /// <summary>
        /// Observes the six known equipment slots in RightHand, LeftHand, Chest, Feet,
        /// FullBody and Eyes order. Reads current and next slots separately, plus each
        /// slot's lock predicate. Next-slot presence is not proof of a completed equip
        /// or an active transition. The native slot-lock predicate is reported verbatim,
        /// not as observed lock state or an equip authorization result.
        /// Requires the initialized Unity lifecycle thread and a live local inventory.
        /// False returns an empty list. True returns all six entries without truncation;
        /// inspect each entry's ReadSucceeded and Status for partial read failures.
        /// Results contain managed values only and are sequential, not atomic.
        /// Does not call equip, stash or slot-lock mutation routines.
        /// </summary>
        public static bool TryCaptureEquipmentSlots(
            out IReadOnlyList<InventoryEquipmentSlotCapture> entries, out string status)
        {
            entries = Array.Empty<InventoryEquipmentSlotCapture>();
            if (!TryGetCurrentInventory(out var inventory, out status))
                return false;

            try
            {
                var captured = new InventoryEquipmentSlotCapture[CapturedEquipmentSlots.Length];
                var hasFailures = false;
                for (var index = 0; index < captured.Length; index++)
                {
                    if (inventory.WasCollected || !inventory)
                    {
                        status = "The local player's inventory became unavailable during equipment capture. No results were returned.";
                        return false;
                    }

                    var slot = CapturedEquipmentSlots[index];
                    var entry = CaptureEquipmentSlot(inventory, slot.Slot, slot.NativeSlot);
                    captured[index] = entry;
                    hasFailures |= !entry.ReadSucceeded;
                }

                if (!TryGetCurrentInventory(out var currentInventory, out status))
                    return false;
                if (currentInventory.Pointer != inventory.Pointer)
                {
                    status = "The local player's inventory changed during equipment capture. No results were returned.";
                    return false;
                }

                entries = Array.AsReadOnly(captured);
                status = hasFailures ? "Some equipment reads failed. Inspect each entry's Status." : "";
                return true;
            }
            catch (Exception error)
            {
                status = "Could not capture the local player's equipment: " + error.Message;
                return false;
            }
        }

        private static InventoryEquipmentSlotCapture CaptureEquipmentSlot(PlayerInventory inventory,
            ItemEquipmentSlot slot, EquipmentSlot nativeSlot)
        {
            ReadEquipmentSlotItem(inventory, nativeSlot, false,
                out var currentItemFound, out var currentItemId, out var currentStatus);
            ReadEquipmentSlotItem(inventory, nativeSlot, true,
                out var nextItemFound, out var nextItemId, out var nextStatus);

            bool? isLocked = null;
            var lockStatus = "";
            try
            {
                isLocked = inventory.IsSlotLocked(nativeSlot);
            }
            catch (Exception error)
            {
                lockStatus = "Could not read the slot lock: " + error.Message;
            }

            var status = currentStatus.Length == 0 && nextStatus.Length == 0 && lockStatus.Length == 0
                ? ""
                : (currentStatus + " " + nextStatus + " " + lockStatus).Trim();
            return new InventoryEquipmentSlotCapture(slot,
                currentItemFound, currentItemId, nextItemFound, nextItemId, isLocked, status);
        }

        private static void ReadEquipmentSlotItem(PlayerInventory inventory, EquipmentSlot slot, bool nextSlot,
            out bool? itemFound, out int? itemId, out string status)
        {
            itemFound = null;
            itemId = null;
            status = "";
            var label = nextSlot ? "Next-slot" : "Current-slot";
            try
            {
                ItemInstance instance;
                var found = nextSlot
                    ? inventory.TryGetItemInNextSlot(slot, out instance)
                    : inventory.TryGetItemInSlot(slot, out instance);
                itemFound = found;
                if (!found)
                    return;

                if (instance is null || instance.WasCollected || instance.Pointer == IntPtr.Zero)
                {
                    status = label + " query reported an item, but its instance is unavailable.";
                    return;
                }

                var id = instance._itemID;
                if (id <= 0)
                {
                    status = label + " query reported an item with a nonpositive ID: " + id + ".";
                    return;
                }

                itemId = id;
            }
            catch (Exception error)
            {
                status = label + " item read failed: " + error.Message;
            }
        }
    }

    /// <summary>Copied observations from one local stow request, not a transaction or animation-completion signal.</summary>
    public sealed class EquipmentStowResult
    {
        internal EquipmentStowResult(ItemEquipmentSlot slot, bool attempted,
            InventoryEquipmentSlotCapture before, InventoryEquipmentSlotCapture after, bool? empty, string detail)
        {
            Slot = slot;
            InvocationAttempted = attempted;
            Before = before;
            After = after;
            ObservedEmpty = empty;
            Detail = detail;
        }

        public ItemEquipmentSlot Slot { get; }
        public bool InvocationAttempted { get; }
        public InventoryEquipmentSlotCapture Before { get; }
        public InventoryEquipmentSlotCapture After { get; }
        /// <summary>Whether both current and next slots were observed empty; null means no valid readback.</summary>
        public bool? ObservedEmpty { get; }
        public string Detail { get; }
    }

    /// <summary>
    /// Copied local equipment observations. Null query results mean unavailable, not
    /// an empty slot or an unlocked slot. Current and next item IDs are kept separate;
    /// matching IDs do not prove that the same item instance occupies both slots.
    /// </summary>
    public sealed class InventoryEquipmentSlotCapture
    {
        internal InventoryEquipmentSlotCapture(ItemEquipmentSlot slot,
            bool? currentItemFound, int? currentItemId, bool? nextItemFound, int? nextItemId,
            bool? isLocked, string status)
        {
            Slot = slot;
            CurrentItemFound = currentItemFound;
            CurrentItemId = currentItemId;
            NextItemFound = nextItemFound;
            NextItemId = nextItemId;
            NativeSlotLockResult = isLocked;
            Status = status;
        }

        public ItemEquipmentSlot Slot { get; }
        public bool ReadSucceeded => CurrentReadSucceeded && NextReadSucceeded && NativeSlotLockResult.HasValue;
        public bool CurrentReadSucceeded => CurrentItemFound.HasValue
            && (CurrentItemFound == false || CurrentItemId.HasValue);
        public bool NextReadSucceeded => NextItemFound.HasValue
            && (NextItemFound == false || NextItemId.HasValue);

        /// <summary>The game's TryGetItemInSlot result; null when that query failed.</summary>
        public bool? CurrentItemFound { get; }
        /// <summary>Positive current-slot ItemInstance ID, or null if none was reported or its ID could not be read.</summary>
        public int? CurrentItemId { get; }
        /// <summary>The game's TryGetItemInNextSlot result, never substituted for the current-slot result.</summary>
        public bool? NextItemFound { get; }
        /// <summary>Positive next-slot ItemInstance ID, not proof that the item has equipped or will equip.</summary>
        public int? NextItemId { get; }
        /// <summary>
        /// Raw IsSlotLocked return value, or null on failure. Some game builds implement this
        /// method as a constant false stub. Neither value establishes observed lock state,
        /// transition completion or equip readiness.
        /// </summary>
        public bool? NativeSlotLockResult { get; }
        /// <summary>Empty when all reads succeeded; otherwise identifies the unavailable observations.</summary>
        public string Status { get; }
    }
}
