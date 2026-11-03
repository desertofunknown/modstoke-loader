// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using global::Sons.Inventory;
using global::Sons.Player;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static (MethodInfo Method, string Prefix)[] EquipmentInitializationTargets()
        {
            var item = typeof(ItemInstance);
            var props = typeof(InventoryProps);
            var initialize = new[] { typeof(GameObject), typeof(InventoryProps.PropDefinition), item, typeof(bool) };
            return new (MethodInfo, string)[]
            {
                (AccessTools.Method(item, "SetItemObject", new[] { typeof(GameObject), typeof(bool) }), nameof(BeforeSetEquipmentItemObject)),
                (AccessTools.Method(item, "RefreshItemObject", new[] { typeof(bool) }), nameof(BeforeEquipmentItemRefresh)),
                (AccessTools.Method(item, "OnRenderableLoaded", new[] { typeof(Transform) }), nameof(BeforeEquipmentItemRefresh)),
                (AccessTools.Method(props, "SetupInitializeCallback", initialize), nameof(BeforeEquipmentInitializeCallback)),
                (AccessTools.Method(props, "InitializeCallback", initialize), nameof(BeforeEquipmentInitializeCallback)),
                (AccessTools.Method(props, "InitializeHeldItem", new[] { typeof(GameObject), item }), nameof(BeforeEquipmentInitializeHeldItem)),
                (AccessTools.Method(props, "InitializeWeaponInfo", new[] { typeof(GameObject) }), nameof(BeforeEquipmentInitializeWeaponInfo))
            };
        }

        // Captured identities remain protected after retirement, without native field reads.
        private static bool IsCapturedEquipmentItem(ItemInstance item)
        {
            foreach (var owner in Volatile.Read(ref _owners))
                if (Same(owner.Capture.Item, item)) return true;
            return false;
        }

        private static bool IsCapturedEquipmentObject(GameObject itemObject)
        {
            if (IsPreparedObject(itemObject)) return true;
            foreach (var owner in Volatile.Read(ref _owners))
                if (Same(owner.Capture.ItemObject, itemObject)) return true;
            return false;
        }

        private static bool IsCapturedEquipmentProps(InventoryProps props)
        {
            foreach (var owner in Volatile.Read(ref _owners))
                if (Same(owner.Capture.Props, props)) return true;
            return false;
        }

        private static bool BeforeSetEquipmentItemObject(ItemInstance __instance, GameObject __0,
            bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                if (IsCapturedEquipmentItem(__instance) || IsCapturedEquipmentObject(__0)) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                return AllowEquipmentItemRefresh(__instance);
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeEquipmentItemRefresh(ItemInstance __instance, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try { return AllowEquipmentItemRefresh(__instance); }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool AllowEquipmentItemRefresh(ItemInstance item)
        {
            if (IsCapturedEquipmentItem(item)) return false;
            if (!OnOwnerThread) return AllowUnreadableOwner();
            if (item is null || item.Pointer == 0 || item.WasCollected) return AllowUnreadableOwner();
            var itemObject = item._ItemObject_k__BackingField;
            // An unassigned object is normal before the first equip, and a cleared
            // reference is normal after removal. Neither grants ownership here.
            return !IsCapturedEquipmentObject(itemObject);
        }

        private static bool BeforeEquipmentInitializeCallback(InventoryProps __instance,
            GameObject __0, ItemInstance __2, bool __runOriginal)
            => AllowEquipmentInitialization(__instance, __0, __2, true, __runOriginal);

        private static bool BeforeEquipmentInitializeHeldItem(InventoryProps __instance,
            GameObject __0, ItemInstance __1, bool __runOriginal)
            => AllowEquipmentInitialization(__instance, __0, __1, true, __runOriginal);

        private static bool BeforeEquipmentInitializeWeaponInfo(InventoryProps __instance,
            GameObject __0, bool __runOriginal)
            => AllowEquipmentInitialization(__instance, __0, null, false, __runOriginal);

        private static bool AllowEquipmentInitialization(InventoryProps props, GameObject itemObject,
            ItemInstance item, bool hasItem, bool runOriginal)
        {
            if (!runOriginal || !IsObserving) return true;
            try
            {
                if (IsCapturedEquipmentItem(item) || IsCapturedEquipmentObject(itemObject)) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                if (props is null || props.WasCollected || !props)
                    return AllowUnreadableOwner();
                if (hasItem && (item is null || item.Pointer == 0 || item.WasCollected))
                    return AllowUnreadableOwner();

                bool ownsContext = IsCapturedEquipmentProps(props);
                if (!ownsContext)
                {
                    var inventory = props._playerInventory;
                    // Remote presentation can have no local PlayerInventory.
                    ownsContext = OwnsInventory(inventory);
                }
                if (!ownsContext) return true;
                if (hasItem && item._itemID == 355) return false;
                var objects = props._gameObjectInstances;
                if (objects is null || objects.Pointer == 0 || objects.WasCollected)
                    return AllowUnreadableOwner();
                if (!objects.TryGetValue(355, out var pistolObject)) return true;
                return !Same(pistolObject, itemObject);
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }
    }
}
