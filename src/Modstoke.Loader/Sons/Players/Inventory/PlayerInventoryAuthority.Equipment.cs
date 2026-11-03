// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private sealed class PendingEquipment
        {
            internal readonly ItemRecord Item;
            internal readonly PlayerEquipmentChange Change;
            internal readonly int HeldSlot;
            internal readonly EquipmentState Before;
            internal bool Attempted;

            internal PendingEquipment(ItemRecord item, PlayerEquipmentChange change,
                int heldSlot, EquipmentState before)
            { Item = item; Change = change; HeldSlot = heldSlot; Before = before; }
        }

        private PlayerEquipmentDecision Authorize(PlayerBridge bridge,
            in PlayerEquipmentChange change, out ItemInstance authoritativeHeldItem, out string detail)
        {
            authoritativeHeldItem = null;
            detail = "No matching server-owned inventory transaction authorizes this callback.";
            if (!ReferenceEquals(bridge, _bridge) || !Ready(out var target)
                || (MutationEnforcement != null && _stow?.SourceConfirmed != true && _equip?.SourceConfirmed != true)
                || _pending == null || _pending.Attempted || _pending.Change != change)
                return PlayerEquipmentDecision.Rejected;
            var current = EquipmentState.Capture(target.GetState<IPlayerState>());
            if (!_pending.Before.Matches(current)
                || (_pending.Item != null && !_pending.Item.IdentityCurrent())
                || !ValidateTransition(_pending.Item, in change, _pending.HeldSlot, current, out detail))
                return PlayerEquipmentDecision.Unsupported;
            _pending.Attempted = true;
            if (change.Kind == PlayerEquipmentKind.HeldInitialized)
                authoritativeHeldItem = _pending.Item.Item;
            detail = "Exact server instance and reserved transition authorized.";
            return PlayerEquipmentDecision.Approved;
        }

        private bool ValidateTransition(ItemRecord item, in PlayerEquipmentChange change,
            int heldSlot, EquipmentState state, out string detail)
        {
            detail = "The reserved transition does not match the server inventory/equipment state.";
            if (state == null || !OwnedEquipmentMatches(state)
                || (change.Kind != PlayerEquipmentKind.HeldInitialized && heldSlot != -1))
                return false;
            if (change.Kind == PlayerEquipmentKind.RaceChanged) return item == null;
            if (item == null || item.Consumed) return false;
            bool stowed = item.HeldSlot < 0 && !item.Clothing && item.ArmourSlot < 0 && item.Quantity == 1;
            switch (change.Kind)
            {
                case PlayerEquipmentKind.HeldInitialized:
                    if (!stowed || item.Item == null || !HandSlot(heldSlot) || OccupiedHand(heldSlot)
                        || Count(state.Held, item.Id) != 0 || Count(state.Held, 0) == 0) return false;
                    // Unknown held IDs carry no trustworthy slot identity. Never overwrite them.
                    foreach (int id in state.Held)
                    {
                        if (id == 0) continue;
                        bool tracked = false;
                        foreach (var record in _items.Values)
                            if (!record.Consumed && record.Id == id && record.HeldSlot >= 0)
                            {
                                tracked = true;
                                break;
                            }
                        if (!tracked)
                        {
                            detail = "Existing vanilla held items have no authoritative instance/slot baseline.";
                            return false;
                        }
                    }
                    return Count(state.Clothing, item.Id) == 0 && Count(state.Armour, item.Id) == 0;
                case PlayerEquipmentKind.HeldRemoved:
                    return !change.Flag && item.HeldSlot >= 0 && Count(state.Held, item.Id) == 1
                        && (item.HeldSlot != (int)EquipmentSlot.RightHand || !state.Lit);
                case PlayerEquipmentKind.SlotTransferred:
                    return HandSlot(change.ToSlot) && item.HeldSlot == change.FromSlot
                        && !OccupiedHand(change.ToSlot) && Count(state.Held, item.Id) == 1
                        && (item.HeldSlot != (int)EquipmentSlot.RightHand || !state.Lit);
                case PlayerEquipmentKind.RightHandLit:
                    return item.HeldSlot == (int)EquipmentSlot.RightHand && Count(state.Held, item.Id) == 1;
                case PlayerEquipmentKind.ClothingEquipped:
                    // Layer/slot compatibility is not carried on this endpoint. Only an empty
                    // clothing set can accept one scoped garment without replacing unknown state.
                    return stowed && Count(state.Clothing, 0) == state.Clothing.Length
                        && Count(state.Held, item.Id) == 0 && Count(state.Armour, item.Id) == 0;
                case PlayerEquipmentKind.ClothingUnequipped:
                    return item.Clothing && Count(state.Clothing, item.Id) == 1;
                case PlayerEquipmentKind.ArmourEquipped:
                    return stowed && change.ToSlot < state.Armour.Length && state.Armour[change.ToSlot] == 0
                        && Count(state.Held, item.Id) == 0 && Count(state.Clothing, item.Id) == 0;
                case PlayerEquipmentKind.ArmourUnequipped:
                case PlayerEquipmentKind.ArmourBroken:
                    return item.ArmourSlot == change.ToSlot && change.ToSlot < state.Armour.Length
                        && state.Armour[change.ToSlot] == item.Id;
                default: return false;
            }
        }

        private bool OccupiedHand(int slot)
        {
            foreach (var item in _items.Values) if (!item.Consumed && item.HeldSlot == slot) return true;
            return false;
        }
        private static bool HandSlot(int slot) => slot == (int)EquipmentSlot.LeftHand || slot == (int)EquipmentSlot.RightHand;
        private static int Count(int[] values, int id)
        {
            int count = 0;
            foreach (int value in values) if (value == id) count++;
            return count;
        }

        internal PlayerEquipmentReceipt CompletePublication(in PlayerEquipmentChange change,
            PlayerEquipmentReceipt receipt, ref string detail)
        {
            var pending = _pending;
            if (receipt == PlayerEquipmentReceipt.Ignored) return receipt;
            if (receipt != PlayerEquipmentReceipt.Applied)
            {
                if (pending != null && pending.Attempted)
                {
                    // Unsupported/rejected is pre-write. Unconfirmed must retain the reservation:
                    // there may already be a native effect, and inventory must not be credited back.
                    if (receipt != PlayerEquipmentReceipt.Unconfirmed) _pending = null;
                    Fault(detail);
                }
                return receipt;
            }
            try
            {
                if (pending == null || !pending.Attempted || pending.Change != change || !Current(out var target)
                    || (pending.Item != null && !pending.Item.IdentityCurrent())
                    || !pending.Before.Confirms(EquipmentState.Capture(target.GetState<IPlayerState>()), in change))
                {
                    Fault("Native equipment effects cannot be reconciled with the reserved inventory transaction.");
                    detail = Detail;
                    return PlayerEquipmentReceipt.Unconfirmed;
                }
                var item = pending.Item;
                switch (change.Kind)
                {
                    case PlayerEquipmentKind.HeldInitialized: item.HeldSlot = pending.HeldSlot; break;
                    case PlayerEquipmentKind.HeldRemoved: item.HeldSlot = -1; break;
                    case PlayerEquipmentKind.SlotTransferred: item.HeldSlot = change.ToSlot; break;
                    case PlayerEquipmentKind.ClothingEquipped: item.Clothing = true; break;
                    case PlayerEquipmentKind.ClothingUnequipped: item.Clothing = false; break;
                    case PlayerEquipmentKind.ArmourEquipped: item.ArmourSlot = change.ToSlot; break;
                    case PlayerEquipmentKind.ArmourUnequipped: item.ArmourSlot = -1; break;
                    case PlayerEquipmentKind.ArmourBroken:
                        item.ArmourSlot = -1; item.Consumed = true; item.Quantity = 0; item.ArmourPoints = 0; break;
                }
                _pending = null;
                Detail = detail = "Server inventory transition committed after confirmed native equipment publication.";
                return receipt;
            }
            catch (Exception error)
            {
                Fault("Inventory commit is uncertain (" + error.GetType().Name + "); no retry or refund is allowed.");
                detail = Detail;
                return PlayerEquipmentReceipt.Unconfirmed;
            }
        }

        private static bool TryReadIds(NetworkArray_Integer source, out int[] values)
        {
            values = null;
            if (source == null || source.WasCollected || source.Length is < 1 or > 32) return false;
            var result = new int[source.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = source[i];
                if (result[i] < 0) return false;
            }
            values = result;
            return true;
        }

        private sealed class EquipmentState
        {
            internal int[] Held, Clothing, Armour;
            internal bool Lit;
            private int _race, _transferId, _fromSlot, _toSlot;
            private IntPtr _pointer;

            internal static EquipmentState Capture(IPlayerState state)
            {
                if (state == null || state.WasCollected || !TryReadIds(state.HeldItems, out var held)
                    || !TryReadIds(state.PlayerClothingIds, out var clothing)
                    || !TryReadIds(state.PlayerArmourIds, out var armour)) return null;
                var transfer = state.HeldItemEquipSlotTransfer;
                if (transfer == null || transfer.WasCollected) return null;
                return new EquipmentState
                {
                    Held = held, Clothing = clothing, Armour = armour,
                    Lit = state.RightHandItemIsLit, _race = state.PlayerRace, _pointer = state.Pointer,
                    _transferId = transfer.ItemId, _fromSlot = transfer.FromSlot, _toSlot = transfer.ToSlot
                };
            }

            internal bool Matches(EquipmentState other) => other != null && _pointer == other._pointer && SameValues(other);
            internal bool SameValues(EquipmentState other) => other != null
                && Equal(Held, other.Held) && Equal(Clothing, other.Clothing) && Equal(Armour, other.Armour)
                && Lit == other.Lit && _race == other._race && _transferId == other._transferId
                && _fromSlot == other._fromSlot && _toSlot == other._toSlot;

            internal bool Confirms(EquipmentState after, in PlayerEquipmentChange change)
            {
                if (after == null || _pointer != after._pointer) return false;
                bool heldAdd = change.Kind == PlayerEquipmentKind.HeldInitialized;
                bool heldRemove = change.Kind == PlayerEquipmentKind.HeldRemoved;
                bool clothingAdd = change.Kind == PlayerEquipmentKind.ClothingEquipped;
                bool clothingRemove = change.Kind == PlayerEquipmentKind.ClothingUnequipped;
                bool armourAdd = change.Kind == PlayerEquipmentKind.ArmourEquipped;
                bool armourRemove = change.Kind is PlayerEquipmentKind.ArmourUnequipped
                    or PlayerEquipmentKind.ArmourBroken;
                if (!(heldAdd || heldRemove ? SingleEdit(Held, after.Held, change.Value, heldAdd) : Equal(Held, after.Held))
                    || !(clothingAdd || clothingRemove ? SingleEdit(Clothing, after.Clothing, change.Value, clothingAdd)
                        : Equal(Clothing, after.Clothing))) return false;
                if (armourAdd || armourRemove)
                {
                    if (Armour.Length != after.Armour.Length || change.ToSlot >= Armour.Length) return false;
                    for (int i = 0; i < Armour.Length; i++)
                        if (after.Armour[i] != (i == change.ToSlot ? armourAdd ? change.Value : 0 : Armour[i])) return false;
                }
                else if (!Equal(Armour, after.Armour)) return false;
                if (after._race != (change.Kind == PlayerEquipmentKind.RaceChanged ? change.Value : _race)) return false;
                if (after.Lit != (change.Kind == PlayerEquipmentKind.RightHandLit ? change.Flag : Lit)) return false;
                // Add/RemoveHeldItem maintain their own transfer helper. The publisher validates
                // those native helpers; an explicit transfer must match all three requested fields.
                return change.Kind != PlayerEquipmentKind.SlotTransferred
                    || (after._transferId == change.Value && after._fromSlot == change.FromSlot && after._toSlot == change.ToSlot);
            }

            private static bool Equal(int[] left, int[] right) => left.AsSpan().SequenceEqual(right);
            private static bool SingleEdit(int[] before, int[] after, int id, bool add)
            {
                if (before.Length != after.Length) return false;
                int edits = 0;
                for (int i = 0; i < before.Length; i++)
                {
                    if (before[i] == after[i]) continue;
                    if (before[i] != (add ? 0 : id) || after[i] != (add ? id : 0)) return false;
                    edits++;
                }
                return edits == 1;
            }
        }
    }

    public sealed partial class PlayerEquipment
    {
        private PlayerInventoryAuthority _inventoryAuthority;

        internal void BindInventoryAuthority(PlayerInventoryAuthority authority)
        { _inventoryAuthority = authority; }

        private PlayerEquipmentReceipt Publish(BoltEntity target,
            in PlayerEquipmentChange change, out string detail, bool inventoryCommand = false)
        {
            if (!inventoryCommand && _inventoryAuthority?.TryConsumeEnforcedEquipNotification(in change) == true)
            {
                detail = "Native equip notification observed; only the separate exact custody receipt may publish equipment.";
                return PlayerEquipmentReceipt.Ignored;
            }
            var receipt = PublishCore(target, in change, out detail);
            return _inventoryAuthority == null ? receipt
                : _inventoryAuthority.CompletePublication(in change, receipt, ref detail);
        }
    }
}
