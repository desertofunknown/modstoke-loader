// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils.Items;
using UnityEngine;
using SlotOverride = Il2CppSystem.Nullable<global::Sons.Items.Core.EquipmentSlot>;
using Types = global::Sons.Items.Core.Types;

namespace Modstoke.Sons
{
    public static partial class HeldItems
    {
        private static bool _hooksAttempted, _hooksInstalled;
        private static readonly HashSet<string> RefusalsLogged = new(StringComparer.Ordinal);

        private static void EnsureHooks()
        {
            if (!_hooksAttempted)
            {
                _hooksAttempted = true;
                try
                {
                    var slot = typeof(SlotOverride);
                    var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-held-items");
                    harmony.Patch(AccessTools.Method(typeof(InventoryProps), nameof(InventoryProps.GetPropDefinitionForItem),
                            new[] { typeof(int), slot }),
                        postfix: new HarmonyMethod(typeof(HeldItems), nameof(AfterPropDefinition)));
                    // Ahead of strict inventory's own prefix, so a refused equip never becomes a server request.
                    harmony.Patch(AccessTools.Method(typeof(PlayerInventory), nameof(PlayerInventory.TryEquip),
                            new[] { typeof(ItemInstance), typeof(bool), typeof(bool), slot, typeof(bool), typeof(bool) }),
                        prefix: new HarmonyMethod(typeof(HeldItems), nameof(BeforeEquip)) { priority = Priority.First + 1 },
                        postfix: new HarmonyMethod(typeof(HeldItems), nameof(AfterEquip)));
                    harmony.Patch(AccessTools.Method(typeof(ItemAnimatorHashHelper), nameof(ItemAnimatorHashHelper.ApplyAnimVars),
                            new[] { typeof(ItemData), typeof(bool), typeof(Animator), slot }),
                        postfix: new HarmonyMethod(typeof(HeldItems), nameof(AfterApplyAnimVars)));
                    _hooksInstalled = true;
                    GatePublication(harmony);
                }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Held item hooks are unavailable, so items with a held model "
                        + "stay in the inventory and cannot be equipped: " + error);
                }
            }
            // Without the hand definition the game's TryEquip would take the item and give nothing back.
            if (!_hooksInstalled)
                foreach (var profile in Profiles.Values)
                    profile.Item._type &= ~Types.Equippable;
        }

        // The game scans this list on every lookup, for the local player on each equip and for other players on
        // each change of what they hold, so the definition is added where and when it is first asked for.
        private static void AfterPropDefinition(InventoryProps __instance, int __0, SlotOverride __1,
            ref InventoryProps.PropDefinition __result)
        {
            if (__result != null || __0 < ItemIds.FirstModItemId || !Profiles.TryGetValue(__0, out var profile)) return;
            try
            {
                if (__1 != null && __1.HasValue && __1.Value != profile.Slot) return;
                // Another player's client-local id means whatever their machine registered there, not this profile.
                if (!ItemIds.IsNetworked(__0) && !Same(__instance, LocalProps())) return;
                // Nothing renders on a dedicated server, so its players' props never make held instances.
                if (GameBuild.IsDedicatedServer || Withheld.Contains(__0) || __instance == null) return;
                __result = Definition(__instance, profile);
            }
            catch (Exception error)
            {
                LogOnce("definition " + __0, "Item " + __0 + "'s hand definition could not be added: " + error.Message);
            }
        }

        private static InventoryProps.PropDefinition Definition(InventoryProps props, Profile profile)
        {
            var definitions = props._propDefinitions;
            if (definitions == null) return null;
            InventoryProps.PropDefinition donor = null;
            for (var index = 0; index < definitions.Count; index++)
            {
                var definition = definitions[index];
                if (definition == null) continue;
                // Natively a query for a hand only reads the hand overrides, never this list.
                if (definition._itemId == profile.ItemId) return definition;
                if (donor == null && definition._itemId == profile.DonorId) donor = definition;
            }
            if (donor == null) return null;
            var added = new InventoryProps.PropDefinition { _itemId = profile.ItemId, _heldLocator = donor._heldLocator };
            definitions.Add(added);
            return added;
        }

        // The game's TryEquip takes the item out of the inventory before it looks up the hand definition and does
        // not give it back when there is none, so every equip of a held item is checked here first.
        private static bool BeforeEquip(PlayerInventory __instance, ItemInstance __0, SlotOverride __3,
            bool __runOriginal, ref bool __result, out int __state)
        {
            __state = -1;
            if (!__runOriginal || Profiles.Count == 0 || __instance == null || __0 == null || __0.WasCollected) return true;
            try
            {
                var id = __0._itemID;
                if (!Profiles.TryGetValue(id, out var profile))
                {
                    YieldTo(__instance, __0, __3);
                    return true;
                }
                string refusal = null;
                if (__3 != null && __3.HasValue && __3.Value != profile.Slot)
                    refusal = "it only goes into its own hand";
                else if (SessionRefusal(id) is { } session)
                    refusal = session;
                else if (__instance.GetPropDefinitionForItem(id, null) == null)
                    refusal = "this player has no hand definition for it (pose donor " + profile.DonorId + ")";
                else if (profile.BothHands && __instance.HeldOnlyItemController?.HasItem == true)
                    refusal = "the player is carrying something in both hands";
                if (refusal != null)
                {
                    LogOnce("equip " + id + " " + refusal, "Item " + id + " was not equipped: " + refusal + ".");
                    __result = false;
                    return false;
                }
                YieldTo(__instance, __0, __3);
                if (profile.Slot == EquipmentSlot.LeftHand && profile.BothHands)
                {
                    __state = SlotItem(__instance, EquipmentSlot.RightHand, false);
                    var restoring = Restoring(__instance, id);
                    // With the inventory open the game has put the right-hand item away already and brings it
                    // back on close, next to this item; the SDK takes that memory over as it does a held one.
                    var previous = __instance.PreviousRightHandItem;
                    if (__state < 0 && !restoring && __instance._currentView == PlayerInventory.PlayerViews.Inventory
                        && previous != null && !previous.WasCollected)
                        __state = previous._itemID;
                    var replacing = restoring ? -1 : SlotItem(__instance, EquipmentSlot.LeftHand, false);
                    // With the inventory open the game only queues an equip, as the hand's previous item, so a held
                    // item queued earlier in the same visit is the one this replaces.
                    var queued = !restoring && replacing < 0 && __instance._currentView == PlayerInventory.PlayerViews.Inventory
                        ? PreviousLeft(__instance) : -1;
                    // Taking over from another two-handed held item, it owes back what that one did, never that item.
                    if (TwoHandedHeld(replacing) || TwoHandedHeld(queued)) replacing = -1;
                    else
                    {
                        // Brought out with the right hand empty it owes nothing back, unless the game is restoring it
                        // (closing the inventory, leaving water), which keeps what the first equip put away.
                        if (__state < 0 && !restoring) _remembered = -1;
                        // The left-hand item it owes back is read after the equip. Brought out again before a pending
                        // give-back ran, it keeps that item for its own put-away.
                        if (_leftGiveBackUntil > 0) _leftGiveBackUntil = 0;
                        else if (!restoring) _rememberedLeft = -1;
                    }
                    _replacingLeft = replacing;
                }
            }
            catch (Exception error)
            {
                LogOnce("equip hook", "The held item equip check failed: " + error.Message);
            }
            return true;
        }

        // The game re-equips stashed items from many unrelated paths (closing the inventory, leaving water), which
        // would put the right-hand item next to a left-hand item that needs both hands. The left-hand item it
        // replaced stays the game's previous item for that hand. The SDK takes both memories over and gives the
        // items back after the put-away.
        private static void AfterEquip(PlayerInventory __instance, ItemInstance __0, bool __runOriginal, bool __result, int __state)
        {
            if (!__runOriginal || !__result || __instance == null || __0 == null || __0.WasCollected
                || !Profiles.TryGetValue(__0._itemID, out var profile) || profile.Slot != EquipmentSlot.LeftHand || !profile.BothHands)
                return;
            try
            {
                var previous = __instance.PreviousRightHandItem;
                if (__state > 0 && previous != null && !previous.WasCollected && previous._itemID == __state)
                {
                    _remembered = __state;
                    _rememberedBy = __instance.Pointer;
                    __instance.ForgetPreviousItemInSlot(EquipmentSlot.RightHand);
                }
                // A memorizing equip records the item it replaces and opening the inventory recorded one already,
                // but the game's own toggle records nothing: then the item that was in the hand counts.
                var left = __instance.PreviousLeftHandItem;
                var recorded = left != null && !left.WasCollected;
                var leftId = recorded ? left._itemID : _replacingLeft;
                _replacingLeft = -1;
                if (leftId > 0 && leftId != profile.ItemId && !TwoHandedHeld(leftId))
                {
                    _rememberedLeft = leftId;
                    _rememberedLeftBy = __instance.Pointer;
                }
                if (recorded) __instance.ForgetPreviousItemInSlot(EquipmentSlot.LeftHand);
            }
            catch (Exception error)
            {
                LogOnce("equip memory", "The items put away for a held item could not be remembered: " + error.Message);
            }
        }

        // An equip into the other hand puts a two-handed held item away first, as the game does for its own.
        private static void YieldTo(PlayerInventory inventory, ItemInstance incoming, SlotOverride slotOverride)
        {
            var data = incoming.Data;
            if (data == null) return;
            var slot = slotOverride != null && slotOverride.HasValue ? slotOverride.Value : data._equipmentSlot;
            if (slot is not (EquipmentSlot.LeftHand or EquipmentSlot.RightHand)) return;
            var other = slot == EquipmentSlot.LeftHand ? EquipmentSlot.RightHand : EquipmentSlot.LeftHand;
            var held = SlotItem(inventory, other, false);
            var displaces = held >= 0 && Profiles.TryGetValue(held, out var profile) && profile.BothHands;
            var leftToGame = -1;
            // Anything else taking a hand ends the memory; only the two-handed item's own re-equips keep it.
            if (!Profiles.TryGetValue(incoming._itemID, out var own) || !own.BothHands || own.Slot != EquipmentSlot.LeftHand)
            {
                var leftOwed = _rememberedLeft > 0 && _rememberedLeftBy == inventory.Pointer ? _rememberedLeft : -1;
                if (slot == EquipmentSlot.LeftHand)
                {
                    HandBackQueuedRight(inventory);
                    ForgetLeft();
                }
                else
                {
                    // Chosen on the open inventory over a queued two-handed held item, which the game would bring out
                    // next to it on close: the queued one stays put away, as in the world.
                    var queued = QueuedTwoHanded(inventory);
                    if (queued) inventory.ForgetPreviousItemInSlot(EquipmentSlot.LeftHand);
                    var bothHands = data._requiresBothHands || own is { BothHands: true };
                    // A right-hand item that puts the held item away gives the left-hand item back once the hands are
                    // free, as a put-away through HeldItems does. One that needs both hands, or one chosen on the open
                    // inventory, leaves it to the game as the hand's previous item, as the game keeps it for its own
                    // two-handed items; the game equips it the next time it restores the left hand, such as when the
                    // inventory closes. A pending give-back otherwise ends only when something takes the left hand.
                    if (leftOwed > 0 && (bothHands || queued))
                    {
                        leftToGame = leftOwed;
                        ForgetLeft();
                    }
                    else if (displaces && leftOwed > 0) _leftGiveBackUntil = Time.unscaledTime + LeftGiveBackSeconds;
                    else if (_leftGiveBackUntil == 0) ForgetLeft();
                }
                _remembered = -1;
            }
            if (displaces)
            {
                _remembered = -1;
                if (other == EquipmentSlot.LeftHand) inventory.UnequipItemAtSlot(EquipmentSlot.LeftHand, false, true, false);
                else inventory.StashRightHandItem(false, false, false);
            }
            if (leftToGame > 0 && inventory.AmountOf(leftToGame, false, false) > 0)
                inventory.MemorizeItem(EquipmentSlot.LeftHand, leftToGame);
        }

        // A two-handed held item the open inventory has queued for the left hand, with nothing in either hand yet.
        private static bool QueuedTwoHanded(PlayerInventory inventory)
            => inventory._currentView == PlayerInventory.PlayerViews.Inventory
               && SlotItem(inventory, EquipmentSlot.LeftHand, false) < 0 && SlotItem(inventory, EquipmentSlot.RightHand, false) < 0
               && TwoHandedHeld(PreviousItem(inventory, EquipmentSlot.LeftHand));

        // A left-hand item chosen on the open inventory over a two-handed held item that was only queued there: the
        // right-hand item that one took over from the game goes back to the game, which brings it out on close.
        private static void HandBackQueuedRight(PlayerInventory inventory)
        {
            if (_remembered <= 0 || _rememberedBy != inventory.Pointer || !QueuedTwoHanded(inventory)
                || PreviousItem(inventory, EquipmentSlot.RightHand) > 0 || inventory.AmountOf(_remembered, false, false) < 1)
                return;
            inventory.MemorizeItem(EquipmentSlot.RightHand, _remembered);
        }

        private static bool TwoHandedHeld(int itemId)
            => itemId > 0 && Profiles.TryGetValue(itemId, out var profile) && profile.BothHands && profile.Slot == EquipmentSlot.LeftHand;

        // Whether the game is equipping the left hand's previous item again rather than a fresh bring-out.
        private static bool Restoring(PlayerInventory inventory, int itemId) => PreviousItem(inventory, EquipmentSlot.LeftHand) == itemId;

        // The game's previous item for the left hand: what it brings back there, such as on closing the inventory.
        private static int PreviousLeft(PlayerInventory inventory) => PreviousItem(inventory, EquipmentSlot.LeftHand);

        private static void LogOnce(string key, string message)
        {
            if (RefusalsLogged.Count < 256 && RefusalsLogged.Add(key)) RuntimeEntryPoint.LogSource?.LogInfo(message);
        }
    }
}
