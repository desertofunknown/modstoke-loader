// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerLifePresentation
    {
        private sealed class Hand
        {
            internal ItemInstance Item;
            internal int ItemId;
            internal EquipmentSlot Slot;
            internal PlayerInventoryEnforcement.EquipmentIntent Stow, Restore;
        }

        private Hand[] _hands = Array.Empty<Hand>();
        private int _restoreCalls;

        private void CaptureHands()
        {
            RequireNextHandsEmpty();
            _hands = new[] { Capture(_inventory.LeftHandItem, EquipmentSlot.LeftHand),
                Capture(_inventory.RightHandItem, EquipmentSlot.RightHand) };
            _restoreCalls = 0;
            static Hand Capture(ItemInstance item, EquipmentSlot slot)
            {
                if (item != null && (item.WasCollected || !PlayerInventoryEnforcement.SupportsHeldStow(item.Data)))
                    throw new InvalidOperationException("Native death equipment has no supported exact stow receipt.");
                return new Hand { Item = item, ItemId = item?._itemID ?? 0, Slot = slot };
            }
        }

        private bool PumpHands(bool stow, out string detail)
        {
            detail = "Life equipment waits for its exact server transaction.";
            RequireCurrent();
            if (!stow && _restoreCalls != 3) throw new InvalidOperationException("Finalize did not request both native hand restorations.");
            foreach (var hand in _hands)
            {
                var current = hand.Slot == EquipmentSlot.LeftHand ? _inventory.LeftHandItem : _inventory.RightHandItem;
                if (hand.Item == null)
                {
                    if (current != null) throw new InvalidOperationException("An empty life hand gained unowned equipment.");
                    continue;
                }
                if (hand.Item.WasCollected || hand.Item._itemID != hand.ItemId)
                    throw new InvalidOperationException("The retained life equipment instance expired.");
                var intent = stow ? hand.Stow : hand.Restore;
                if (intent == null)
                {
                    RequireNextHandsEmpty();
                    if (stow ? !SameItem(current, hand.Item) : current != null)
                        throw new InvalidOperationException("Life hand changed before its exact equipment request.");
                    var submitted = stow
                        ? PlayerInventoryEnforcement.RequestEquipmentStow(_inventory, hand.Item, out intent, out detail)
                        : PlayerInventoryEnforcement.RequestEquipmentEquip(_inventory, hand.Item, hand.ItemId, out intent, out detail);
                    if (submitted == EquipmentIntentSubmission.Deferred) return false;
                    if (submitted != EquipmentIntentSubmission.Submitted || intent == null)
                        throw new InvalidOperationException("Life equipment was not submitted to authority: " + detail);
                    if (stow) hand.Stow = intent; else hand.Restore = intent;
                }
                switch (intent.ReadState())
                {
                    case EquipmentIntentState.Pending:
                    case EquipmentIntentState.Accepted:
                        detail = intent.Detail; return false;
                    case EquipmentIntentState.Settled:
                        if (!ReferenceEquals(intent.Owner, _source) || intent.IsStow != stow
                            || intent.Slot != hand.Slot || !SameItem(intent.Item, hand.Item)
                            || intent.Receipt is not { Applied: true })
                            throw new InvalidOperationException("Life equipment receipt changed its exact instance or slot.");
                        current = hand.Slot == EquipmentSlot.LeftHand ? _inventory.LeftHandItem : _inventory.RightHandItem;
                        if (stow ? current != null : !SameItem(current, hand.Item))
                            throw new InvalidOperationException("Life equipment receipt disagrees with the actual native hand.");
                        break;
                    default: throw new InvalidOperationException("Life equipment settlement is unconfirmed: " + intent.Detail);
                }
            }
            RequireNextHandsEmpty();
            return true;
        }

        private void RequireNextHandsEmpty()
        {
            var next = _inventory._equipmentSlotsNext;
            if (next == null || next.WasCollected || next.Length != 6
                || next[(int)EquipmentSlot.LeftHand] != null || next[(int)EquipmentSlot.RightHand] != null)
                throw new InvalidOperationException("Life equipment has an unsettled native hand sequence.");
        }

        private void RequireEmptyHands()
        {
            RequireNextHandsEmpty();
            if (_inventory.LeftHandItem != null || _inventory.RightHandItem != null)
                throw new InvalidOperationException("Native death/get-up requires the receipt-stowed hands.");
        }

        private static bool SameItem(ItemInstance left, ItemInstance right)
            => left != null && right != null && !left.WasCollected && !right.WasCollected && left.Pointer == right.Pointer;

        private static bool BeforeLifeInventory(PlayerInventory __instance, MethodBase __originalMethod, object[] __args)
        {
            string name = __originalMethod.Name;
            if (name is nameof(PlayerInventory.EquipPreviousLeftHandItem) or nameof(PlayerInventory.EquipPreviousRightHandItem)
                && PlayerInventoryEnforcement.TryHandleHeldOnlyPreviousEquipment(__instance,
                    name == nameof(PlayerInventory.EquipPreviousLeftHandItem), (bool)__args[0])) return false;
            var owner = FindInventory(__instance);
            if (owner == null || owner.Call == null) return true;
            try
            {
                owner.RequireCurrent();
                if (owner.Call == nameof(PlayerStats.FinalizeResetFromHeal))
                {
                    int bit = name == nameof(PlayerInventory.EquipPreviousLeftHandItem) ? 1
                        : name == nameof(PlayerInventory.EquipPreviousRightHandItem) ? 2 : 0;
                    if (bit == 0 || (owner._restoreCalls & bit) != 0)
                        throw new InvalidOperationException("Unexpected native get-up equipment request.");
                    owner.RequireEmptyHands();
                    owner._restoreCalls |= bit;
                    return false;
                }
                if (owner.Call is nameof(PlayerStats.StartDeathRoutine) or nameof(PlayerStats.KillPlayerWithRespawnPrompt))
                {
                    // StashHeldItems is inlined in these two native bodies. The callback
                    // still runs; its already-settled leaf mutations must not submit twice.
                    if (name is not (nameof(PlayerInventory.MemorizeEquippedSlot) or nameof(PlayerInventory.UnequipItemAtSlot)
                        or nameof(PlayerInventory.StashRightHandItem)))
                        throw new InvalidOperationException("Unexpected equipment call inside the native death callback graph.");
                    owner.RequireEmptyHands();
                    return false;
                }
                throw new InvalidOperationException("An external native life callback attempted equipment mutation.");
            }
            catch (Exception error) { owner.Fail(error.Message, out _); return false; }
        }

        private static PlayerLifePresentation FindInventory(PlayerInventory inventory)
        {
            foreach (var owner in Owners)
                if (inventory != null && !inventory.WasCollected && owner._inventory.Pointer == inventory.Pointer) return owner;
            return null;
        }
    }
}
