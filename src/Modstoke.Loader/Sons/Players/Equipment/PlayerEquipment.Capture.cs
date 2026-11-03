// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using global::Sons.Wearable;
using global::Sons.Wearable.Armour;
using global::Sons.Wearable.Clothing;
using global::Sons.Wearable.Race;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerEquipment
    {
        private sealed class SourceSubscriptions
        {
            private readonly PlayerEquipment _owner;
            private readonly GameObject _root;
            private readonly PlayerInventory _inventory;
            private readonly InventoryProps _props;
            private readonly PlayerStats _stats;
            private readonly PlayerClothingSystem _clothing;
            private readonly PlayerArmourSystem _armour;
            private readonly PlayerRaceSystem _race;
            private readonly Il2CppSystem.Action<GameObject, InventoryProps.PropDefinition, ItemInstance> _held;
            private readonly Il2CppSystem.Action<int, bool> _removed;
            private readonly Il2CppSystem.Action<int, EquipmentSlot, EquipmentSlot> _transferred;
            private readonly Il2CppSystem.Action<bool> _lit;
            private readonly Il2CppSystem.Action<int, ItemInstance> _clothingOn, _clothingOff;
            private readonly Il2CppSystem.Action<ArmourPiece, WearableSlots> _armourOn, _armourOff, _armourBroken;
            private readonly Il2CppSystem.Action<PlayerRace.Race> _raceChanged;
            private int _attempted;
            private bool _active;

            private SourceSubscriptions(PlayerEquipment owner, GameObject root,
                PlayerInventory inventory, InventoryProps props, PlayerStats stats,
                PlayerClothingSystem clothing, PlayerArmourSystem armour, PlayerRaceSystem race)
            {
                _owner = owner;
                _root = root;
                _inventory = inventory;
                _props = props;
                _stats = stats;
                _clothing = clothing;
                _armour = armour;
                _race = race;
                // Retain these exact native delegate instances for removal, not fresh conversions.
                _held = (Action<GameObject, InventoryProps.PropDefinition, ItemInstance>)Held;
                _removed = (Action<int, bool>)((id, ignore) => Dispatch(new(PlayerEquipmentKind.HeldRemoved, id, Flag: ignore)));
                _transferred = (Action<int, EquipmentSlot, EquipmentSlot>)((id, from, to) =>
                    Dispatch(new(PlayerEquipmentKind.SlotTransferred, id, (int)from, (int)to)));
                _lit = (Action<bool>)(lit => Dispatch(new(PlayerEquipmentKind.RightHandLit, 0, Flag: lit)));
                _clothingOn = (Action<int, ItemInstance>)((id, item) => Clothing(PlayerEquipmentKind.ClothingEquipped, id, item));
                _clothingOff = (Action<int, ItemInstance>)((id, item) => Clothing(PlayerEquipmentKind.ClothingUnequipped, id, item));
                _armourOn = (Action<ArmourPiece, WearableSlots>)((piece, slot) => Armour(PlayerEquipmentKind.ArmourEquipped, piece, slot));
                _armourOff = (Action<ArmourPiece, WearableSlots>)((piece, slot) => Armour(PlayerEquipmentKind.ArmourUnequipped, piece, slot));
                _armourBroken = (Action<ArmourPiece, WearableSlots>)((piece, slot) => Armour(PlayerEquipmentKind.ArmourBroken, piece, slot));
                _raceChanged = (Action<PlayerRace.Race>)(raceValue => Dispatch(new(PlayerEquipmentKind.RaceChanged, (int)raceValue)));
            }

            internal static SourceSubscriptions Capture(PlayerEquipment owner)
            {
                var inventory = LocalPlayer.Inventory;
                var stats = LocalPlayer.Stats;
                if (!Live(inventory) || !Live(stats)) return null;
                var result = new SourceSubscriptions(owner, LocalPlayer.GameObject, inventory,
                    inventory.InventoryProps, stats, LocalPlayer.ClothingSystem, stats.ArmourSystem, LocalPlayer.RaceSystem);
                return result.IsCurrent() ? result : null;
            }

            internal SourceSubscriptions Replacement() => new(_owner, _root, _inventory, _props,
                _stats, _clothing, _armour, _race);

            internal bool IsDetached => _attempted == 0;

            internal bool IsCurrent() => Same(_root, LocalPlayer.GameObject)
                && Same(_inventory, LocalPlayer.Inventory) && Same(_stats, LocalPlayer.Stats)
                && Same(_props, _inventory.InventoryProps) && Same(_clothing, LocalPlayer.ClothingSystem)
                && Same(_armour, _stats.ArmourSystem) && Same(_race, LocalPlayer.RaceSystem);

            internal bool TrySubscribe()
            {
                if (_attempted != 0 || !_owner.OnThread) return false;
                try
                {
                    for (int index = 0; index < 10; index++)
                    {
                        if (!IsCurrent()) throw new InvalidOperationException("Equipment source changed.");
                        _attempted |= 1 << index;
                        Add(index);
                    }
                    if (!IsCurrent()) throw new InvalidOperationException("Equipment source changed.");
                    _active = true;
                    return true;
                }
                catch (Exception)
                {
                    TryDetach();
                    return false;
                }
            }

            internal bool TryDetach()
            {
                if (!_owner.OnThread) return false;
                _active = false;
                for (int index = 9; index >= 0; index--)
                {
                    int bit = 1 << index;
                    if ((_attempted & bit) == 0) continue;
                    try
                    {
                        // Teardown intentionally uses captured sources even after LocalPlayer changed.
                        Remove(index);
                        _attempted &= ~bit;
                    }
                    catch (Exception) { /* Keep the attempted bit so Dispose can retry exact cleanup. */ }
                }
                return IsDetached;
            }

            private void Held(GameObject instance, InventoryProps.PropDefinition definition, ItemInstance item)
            {
                if (!_owner.OnCallbackThread || !_active || _owner.IsFaulted || _owner._disposed) return;
                try
                {
                    if (!Live(instance) || item == null || item.WasCollected || definition == null
                        || definition.WasCollected || item._itemID != definition._itemId)
                    {
                        _owner.Fail("Held-item callback references are unavailable or inconsistent.");
                        return;
                    }
                    Dispatch(new(PlayerEquipmentKind.HeldInitialized, item._itemID));
                }
                catch (Exception) { _owner.Fail("Held-item callback capture failed."); }
            }

            private void Clothing(PlayerEquipmentKind kind, int id, ItemInstance item)
            {
                if (!_owner.OnCallbackThread || !_active || _owner.IsFaulted || _owner._disposed) return;
                try
                {
                    // Default clothes can have no inventory instance; vanilla uses only the ID.
                    if (item != null && (item.WasCollected || item._itemID != id))
                    {
                        _owner.Fail("Clothing callback item identity is inconsistent.");
                        return;
                    }
                    Dispatch(new(kind, id));
                }
                catch (Exception) { _owner.Fail("Clothing callback capture failed."); }
            }

            private void Armour(PlayerEquipmentKind kind, ArmourPiece piece, WearableSlots slot)
            {
                if (!_owner.OnCallbackThread || !_active || _owner.IsFaulted || _owner._disposed) return;
                try
                {
                    if (!Live(piece)) { _owner.Fail("Armour callback piece is unavailable."); return; }
                    Dispatch(new(kind, piece._itemId, ToSlot: (int)slot));
                }
                catch (Exception) { _owner.Fail("Armour callback capture failed."); }
            }

            private void Dispatch(in PlayerEquipmentChange change)
            {
                if (!_owner.OnCallbackThread || !_active || _owner._disposed || _owner.IsFaulted) return;
                try
                {
                    if (!IsCurrent()) { _owner.Fail("Equipment callback source lifetime ended."); return; }
                    if (_owner._bridge != null && !_owner.Current(out _)) { _owner.Dispose(); return; }
                    _owner.Enqueue(change);
                }
                catch (Exception) { _owner.Fail("Equipment callback capture failed."); }
            }

            private void Add(int index)
            {
                switch (index)
                {
                    case 0: _props.add_OnHeldItemInitialized(_held); break;
                    case 1: _props.add_OnHeldItemRemoved(_removed); break;
                    case 2: _inventory.OnItemTransferredToSlot = Combine(_inventory.OnItemTransferredToSlot, _transferred); break;
                    case 3: _inventory.OnRightHandItemLit = Combine(_inventory.OnRightHandItemLit, _lit); break;
                    case 4: _clothing.add_OnClothingEquipped(_clothingOn); break;
                    case 5: _clothing.add_OnClothingUnequipped(_clothingOff); break;
                    case 6: _armour.OnArmourEquipped = Combine(_armour.OnArmourEquipped, _armourOn); break;
                    case 7: _armour.OnArmourUnequipped = Combine(_armour.OnArmourUnequipped, _armourOff); break;
                    case 8: _armour.OnArmourBroken = Combine(_armour.OnArmourBroken, _armourBroken); break;
                    case 9: _race.add_OnRaceChanged(_raceChanged); break;
                }
            }

            private void Remove(int index)
            {
                switch (index)
                {
                    case 0: RequireLive(_props); _props.remove_OnHeldItemInitialized(_held); break;
                    case 1: RequireLive(_props); _props.remove_OnHeldItemRemoved(_removed); break;
                    case 2: RequireLive(_inventory); _inventory.OnItemTransferredToSlot = Subtract(_inventory.OnItemTransferredToSlot, _transferred); break;
                    case 3: RequireLive(_inventory); _inventory.OnRightHandItemLit = Subtract(_inventory.OnRightHandItemLit, _lit); break;
                    case 4: RequireLive(_clothing); _clothing.remove_OnClothingEquipped(_clothingOn); break;
                    case 5: RequireLive(_clothing); _clothing.remove_OnClothingUnequipped(_clothingOff); break;
                    case 6: RequireLive(_armour); _armour.OnArmourEquipped = Subtract(_armour.OnArmourEquipped, _armourOn); break;
                    case 7: RequireLive(_armour); _armour.OnArmourUnequipped = Subtract(_armour.OnArmourUnequipped, _armourOff); break;
                    case 8: RequireLive(_armour); _armour.OnArmourBroken = Subtract(_armour.OnArmourBroken, _armourBroken); break;
                    case 9: RequireLive(_race); _race.remove_OnRaceChanged(_raceChanged); break;
                }
            }

            private static T Combine<T>(T current, T handler) where T : Il2CppSystem.Delegate
                => current == null ? handler : Il2CppSystem.Delegate.Combine(current, handler).Cast<T>();

            private static T Subtract<T>(T current, T handler) where T : Il2CppSystem.Delegate
                => current == null ? null : Il2CppSystem.Delegate.Remove(current, handler)?.Cast<T>();

            private static void RequireLive(UnityEngine.Object source)
            {
                if (!Live(source)) throw new InvalidOperationException("Equipment subscription source was destroyed.");
            }
        }
    }
}
