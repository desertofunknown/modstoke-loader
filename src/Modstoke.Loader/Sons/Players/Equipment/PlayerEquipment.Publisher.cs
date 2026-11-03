// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerEquipment
    {
        private const int MaxStateItems = 32;
        private const int MaxItemModules = 32;
        private const int MaxWeaponMods = 16;

        private PlayerEquipmentReceipt PublishCore(BoltEntity target,
            in PlayerEquipmentChange change, out string detail)
        {
            detail = "Equipment publication is not authorized.";
            bool attempted = false;
            try
            {
                if (!_server || _authorize == null || !change.IsValid || !Current(out var current)
                    || !Same(current, target)) return PlayerEquipmentReceipt.Rejected;
                // Native OnHeldItemRemoved does nothing for mpIgnoreCallback, even before looking up state.
                if (change.Kind == PlayerEquipmentKind.HeldRemoved && change.Flag)
                    return PlayerEquipmentReceipt.Ignored;

                var state = target.GetState<IPlayerState>();
                if (state == null || state.WasCollected) return PlayerEquipmentReceipt.Unsupported;
                var decision = _authorize(_bridge, in change, out var heldItem, out detail);
                if (decision != PlayerEquipmentDecision.Approved)
                    return decision == PlayerEquipmentDecision.Unsupported
                        ? PlayerEquipmentReceipt.Unsupported : PlayerEquipmentReceipt.Rejected;
                if (!Current(out current) || !Same(current, target)
                    || current.GetState<IPlayerState>()?.Pointer != state.Pointer)
                {
                    detail = "Equipment target changed during authorization.";
                    return PlayerEquipmentReceipt.Rejected;
                }
                if (!Preflight(state, in change, heldItem, out var modules, out detail))
                    return PlayerEquipmentReceipt.Unsupported;
                if (!Current(out current) || !Same(current, target)
                    || current.GetState<IPlayerState>()?.Pointer != state.Pointer)
                    return PlayerEquipmentReceipt.Rejected;

                attempted = true;
                switch (change.Kind)
                {
                    // Native helpers use this setup's state and the supplied item, not LocalPlayer.
                    // They also maintain weapon-mod/perishable/dismember and transfer state. Never
                    // substitute new ItemInstance(clientId), which would invent unowned module data.
                    case PlayerEquipmentKind.HeldInitialized:
                        modules.PublishAppearance(state);
                        _setup.AddHeldItem(heldItem);
                        // Native dismember cleanup also clears perishable data. Publish it last
                        // for a perishable without a limb module, using the same native writer.
                        if (modules.RestorePerishable) _setup.TryAddRightHandItemPerishableState(heldItem);
                        modules.Expected.PublishModules(state);
                        break;
                    case PlayerEquipmentKind.HeldRemoved:
                        _setup.RemoveHeldItem(change.Value);
                        break;
                    case PlayerEquipmentKind.ClothingEquipped:
                        _setup.OnClothingEquipped(change.Value, null);
                        break;
                    case PlayerEquipmentKind.ClothingUnequipped:
                        _setup.OnClothingUnequipped(change.Value, null);
                        break;
                    case PlayerEquipmentKind.ArmourEquipped:
                        state.PlayerArmourIds[change.ToSlot] = change.Value;
                        break;
                    case PlayerEquipmentKind.ArmourUnequipped:
                    case PlayerEquipmentKind.ArmourBroken:
                        state.PlayerArmourIds[change.ToSlot] = 0;
                        break;
                    case PlayerEquipmentKind.RaceChanged:
                        state.PlayerRace = change.Value;
                        break;
                    case PlayerEquipmentKind.RightHandLit:
                        state.RightHandItemIsLit = change.Flag;
                        break;
                    case PlayerEquipmentKind.SlotTransferred:
                        var transfer = state.HeldItemEquipSlotTransfer;
                        transfer.ItemId = change.Value;
                        transfer.FromSlot = change.FromSlot;
                        transfer.ToSlot = change.ToSlot;
                        break;
                    default: throw new InvalidOperationException("Unknown equipment callback.");
                }
                if (!Current(out current) || !Same(current, target)
                    || current.GetState<IPlayerState>()?.Pointer != state.Pointer || !ReadBack(state, in change)
                    || (modules != null && !modules.Confirms(state)))
                {
                    detail = "Equipment write or current target could not be confirmed; do not replay it.";
                    return PlayerEquipmentReceipt.Unconfirmed;
                }
                detail = "Authorized equipment callback published to the current server-owned player.";
                return PlayerEquipmentReceipt.Applied;
            }
            catch (Exception error)
            {
                detail = "Equipment publication failed (" + error.GetType().Name + ").";
                return attempted ? PlayerEquipmentReceipt.Unconfirmed : PlayerEquipmentReceipt.Unsupported;
            }
        }

        internal static bool Preflight(IPlayerState state, in PlayerEquipmentChange change,
            ItemInstance heldItem, out string detail)
            => Preflight(state, in change, heldItem, out _, out detail);

        private static bool Preflight(IPlayerState state, in PlayerEquipmentChange change,
            ItemInstance heldItem, out HeldModulePublication modules, out string detail)
        {
            modules = null;
            detail = "Equipment state shape or item definition is unsupported.";
            // Catalog membership is only a shape check AFTER server policy approved possession and use.
            if (change.Kind is not (PlayerEquipmentKind.RaceChanged or PlayerEquipmentKind.RightHandLit))
            {
                var data = ItemDatabaseManager.ItemById(change.Value);
                if (!Live(data) || data.Id != change.Value) return false;
            }
            switch (change.Kind)
            {
                case PlayerEquipmentKind.HeldInitialized:
                    // AddHeldItem returns before publishing modules if the ID is already held.
                    if (!ValidIds(state.HeldItems) || Contains(state.HeldItems, change.Value)
                        || !Contains(state.HeldItems, 0)
                        || !ValidHeldState(state)) return false;
                    return TryPrepareHeldModules(state, heldItem, change.Value, out modules, out detail);
                case PlayerEquipmentKind.HeldRemoved:
                    if (!ValidIds(state.HeldItems) || !Contains(state.HeldItems, change.Value)
                        || !ValidHeldState(state)) return false;
                    modules = HeldModulePublication.Removal(state, ItemDatabaseManager.ItemById(change.Value));
                    return true;
                case PlayerEquipmentKind.ClothingEquipped:
                    return ValidIds(state.PlayerClothingIds) && HasSpaceOrItem(state.PlayerClothingIds, change.Value);
                case PlayerEquipmentKind.ClothingUnequipped:
                    return ValidIds(state.PlayerClothingIds);
                case PlayerEquipmentKind.ArmourEquipped:
                    return ValidIds(state.PlayerArmourIds) && change.ToSlot < state.PlayerArmourIds.Length;
                case PlayerEquipmentKind.ArmourUnequipped:
                case PlayerEquipmentKind.ArmourBroken:
                    // Do not clear a replacement piece because an older callback arrived late.
                    return ValidIds(state.PlayerArmourIds) && change.ToSlot < state.PlayerArmourIds.Length
                        && (state.PlayerArmourIds[change.ToSlot] == change.Value || state.PlayerArmourIds[change.ToSlot] == 0);
                case PlayerEquipmentKind.SlotTransferred:
                    return ValidIds(state.HeldItems) && Contains(state.HeldItems, change.Value)
                        && state.HeldItemEquipSlotTransfer != null && !state.HeldItemEquipSlotTransfer.WasCollected;
                case PlayerEquipmentKind.RaceChanged:
                case PlayerEquipmentKind.RightHandLit:
                    return true;
                default: return false;
            }
        }

        private static bool ValidHeldState(IPlayerState state)
        {
            var mods = state.HeldItemsWeaponMods;
            if (mods == null || mods.WasCollected || mods.Length is < 1 or > MaxStateItems) return false;
            for (int i = 0; i < mods.Length; i++)
            {
                if (mods[i] == null || mods[i].WasCollected || mods[i].ItemId < 0
                    || mods[i].WeaponModIds == null || mods[i].WeaponModIds.WasCollected
                    || mods[i].WeaponModIds.Length is < 1 or > MaxWeaponMods) return false;
                // WeaponMod.Id is a signed hash, not its positive inventory ModItemId.
                for (int j = 0; j < i; j++)
                    if (mods[i].ItemId != 0 && mods[i].ItemId == mods[j].ItemId) return false;
            }
            return state.HeldRightHandItemPerishableState != null && !state.HeldRightHandItemPerishableState.WasCollected
                && float.IsFinite(state.HeldRightHandItemPerishableState.TimeRemainingInState)
                && state.HeldRightHandItemDismemberState != null && !state.HeldRightHandItemDismemberState.WasCollected
                && state.HeldItemEquipSlotTransfer != null && !state.HeldItemEquipSlotTransfer.WasCollected;
        }

        private static bool ValidIds(NetworkArray_Integer values)
        {
            if (values == null || values.WasCollected || values.Length is < 1 or > MaxStateItems) return false;
            for (int i = 0; i < values.Length; i++) if (values[i] < 0) return false;
            return true;
        }

        private static bool Contains(NetworkArray_Integer values, int id)
        {
            if (!ValidIds(values)) return false;
            for (int i = 0; i < values.Length; i++) if (values[i] == id) return true;
            return false;
        }

        private static bool HasSpaceOrItem(NetworkArray_Integer values, int id)
            => Contains(values, id) || Contains(values, 0);

        private static bool ReadBack(IPlayerState state, in PlayerEquipmentChange change)
        {
            return change.Kind switch
            {
                PlayerEquipmentKind.HeldInitialized => Contains(state.HeldItems, change.Value) && ValidHeldState(state),
                PlayerEquipmentKind.HeldRemoved => ValidIds(state.HeldItems)
                    && !Contains(state.HeldItems, change.Value) && ValidHeldState(state),
                PlayerEquipmentKind.ClothingEquipped => Contains(state.PlayerClothingIds, change.Value),
                PlayerEquipmentKind.ClothingUnequipped => ValidIds(state.PlayerClothingIds)
                    && !Contains(state.PlayerClothingIds, change.Value),
                PlayerEquipmentKind.ArmourEquipped => state.PlayerArmourIds[change.ToSlot] == change.Value,
                PlayerEquipmentKind.ArmourUnequipped or PlayerEquipmentKind.ArmourBroken
                    => state.PlayerArmourIds[change.ToSlot] == 0,
                PlayerEquipmentKind.RaceChanged => state.PlayerRace == change.Value,
                PlayerEquipmentKind.RightHandLit => state.RightHandItemIsLit == change.Flag,
                PlayerEquipmentKind.SlotTransferred => state.HeldItemEquipSlotTransfer.ItemId == change.Value
                    && state.HeldItemEquipSlotTransfer.FromSlot == change.FromSlot
                    && state.HeldItemEquipSlotTransfer.ToSlot == change.ToSlot,
                _ => false
            };
        }
    }
}
