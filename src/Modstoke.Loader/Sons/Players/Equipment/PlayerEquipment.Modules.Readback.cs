// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerEquipment
    {
        private sealed class HeldModuleState
        {
            internal readonly int[] WeaponItems;
            internal readonly int[][] WeaponMods;
            internal int PerishableState, ActorType, ActorSeed, Plating, Variant;
            internal int TransferId, TransferFrom, TransferTo;
            internal float PerishableTime;
            internal bool Bloody;
            private readonly IntPtr _state, _weapons, _perishable, _dismember, _transfer;
            private readonly IntPtr[] _weaponEntries, _weaponArrays;

            internal HeldModuleState(IPlayerState state)
            {
                _state = state.Pointer;
                var weapons = state.HeldItemsWeaponMods;
                _weapons = weapons.Pointer;
                WeaponItems = new int[weapons.Length];
                WeaponMods = new int[weapons.Length][];
                _weaponEntries = new IntPtr[weapons.Length];
                _weaponArrays = new IntPtr[weapons.Length];
                for (int i = 0; i < weapons.Length; i++)
                {
                    var entry = weapons[i];
                    var ids = entry.WeaponModIds;
                    _weaponEntries[i] = entry.Pointer;
                    _weaponArrays[i] = ids.Pointer;
                    WeaponItems[i] = entry.ItemId;
                    WeaponMods[i] = new int[ids.Length];
                    for (int j = 0; j < ids.Length; j++) WeaponMods[i][j] = ids[j];
                }
                var perishable = state.HeldRightHandItemPerishableState;
                _perishable = perishable.Pointer;
                PerishableState = perishable.CurrentState;
                PerishableTime = perishable.TimeRemainingInState;
                var dismember = state.HeldRightHandItemDismemberState;
                _dismember = dismember.Pointer;
                ActorType = dismember.ActorTypeId;
                ActorSeed = dismember.ActorSeed;
                Bloody = dismember.IsBloody;
                Plating = state.RightHandItemPlatingState;
                Variant = state.RightHandVisualVariantNum;
                var transfer = state.HeldItemEquipSlotTransfer;
                _transfer = transfer.Pointer;
                TransferId = transfer.ItemId;
                TransferFrom = transfer.FromSlot;
                TransferTo = transfer.ToSlot;
            }

            internal bool Matches(IPlayerState state)
            {
                if (state == null || state.WasCollected || state.Pointer != _state || !ValidHeldState(state)) return false;
                var weapons = state.HeldItemsWeaponMods;
                if (weapons.Pointer != _weapons || weapons.Length != WeaponItems.Length) return false;
                for (int i = 0; i < weapons.Length; i++)
                {
                    var entry = weapons[i];
                    var ids = entry.WeaponModIds;
                    if (entry.Pointer != _weaponEntries[i] || ids.Pointer != _weaponArrays[i]
                        || entry.ItemId != WeaponItems[i] || ids.Length != WeaponMods[i].Length) return false;
                    for (int j = 0; j < ids.Length; j++) if (ids[j] != WeaponMods[i][j]) return false;
                }
                var perishable = state.HeldRightHandItemPerishableState;
                var dismember = state.HeldRightHandItemDismemberState;
                var transfer = state.HeldItemEquipSlotTransfer;
                return perishable.Pointer == _perishable && perishable.CurrentState == PerishableState
                    && perishable.TimeRemainingInState.Equals(PerishableTime)
                    && dismember.Pointer == _dismember && dismember.ActorTypeId == ActorType
                    && dismember.ActorSeed == ActorSeed && dismember.IsBloody == Bloody
                    && state.RightHandItemPlatingState == Plating && state.RightHandVisualVariantNum == Variant
                    && transfer.Pointer == _transfer && transfer.ItemId == TransferId
                    && transfer.FromSlot == TransferFrom && transfer.ToSlot == TransferTo;
            }

            internal void PublishModules(IPlayerState state)
            {
                // Publish values only on the already qualified carrier. No local player,
                // token reconstruction, inventory mutation or native item initialization.
                for (int i = 0; i < WeaponItems.Length; i++)
                {
                    var entry = state.HeldItemsWeaponMods[i];
                    for (int j = 0; j < WeaponMods[i].Length; j++)
                        if (entry.WeaponModIds[j] != WeaponMods[i][j]) entry.WeaponModIds[j] = WeaponMods[i][j];
                    if (entry.ItemId != WeaponItems[i]) entry.ItemId = WeaponItems[i];
                }
                var perishable = state.HeldRightHandItemPerishableState;
                if (perishable.CurrentState != PerishableState) perishable.CurrentState = PerishableState;
                if (!perishable.TimeRemainingInState.Equals(PerishableTime)) perishable.TimeRemainingInState = PerishableTime;
                var dismember = state.HeldRightHandItemDismemberState;
                if (dismember.ActorTypeId != ActorType) dismember.ActorTypeId = ActorType;
                if (dismember.ActorSeed != ActorSeed) dismember.ActorSeed = ActorSeed;
                if (dismember.IsBloody != Bloody) dismember.IsBloody = Bloody;
                if (state.RightHandItemPlatingState != Plating) state.RightHandItemPlatingState = Plating;
                if (state.RightHandVisualVariantNum != Variant) state.RightHandVisualVariantNum = Variant;
            }
        }
    }
}
