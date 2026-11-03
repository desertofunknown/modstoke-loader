// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using global::Sons.Animation;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Player;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatRebreatherHooks
    {
        private static (MethodInfo Method, string Prefix)[] NativeRebreatherEquipmentTargets()
        {
            var item = typeof(ItemInstance);
            var props = typeof(InventoryProps);
            var callback = new[] { typeof(GameObject), typeof(InventoryProps.PropDefinition), item, typeof(bool) };
            var initialize = new[] { typeof(Animator), typeof(global::PlayerAnimatorControl),
                typeof(AnimationEventReceiver), typeof(Transform), typeof(Transform), item };
            return new (MethodInfo, string)[]
            {
                (AccessTools.Method(item, "SetItemObject", new[] { typeof(GameObject), typeof(bool) }), nameof(BeforeRebreatherSetItemObject)),
                (AccessTools.Method(item, "RefreshItemObject", new[] { typeof(bool) }), nameof(BeforeRebreatherItemRefresh)),
                (AccessTools.Method(item, "OnRenderableLoaded", new[] { typeof(Transform) }), nameof(BeforeRebreatherItemRefresh)),
                (AccessTools.Method(item, "Initialize", new[] { typeof(int) }), nameof(BeforeRebreatherItemRefresh)),
                (AccessTools.Method(item, "CheckForVolumeContainerModule", Type.EmptyTypes), nameof(BeforeRebreatherItemRefresh)),
                (AccessTools.Method(props, "SetupInitializeCallback", callback), nameof(BeforeRebreatherInitializeCallback)),
                (AccessTools.Method(props, "InitializeCallback", callback), nameof(BeforeRebreatherInitializeCallback)),
                (AccessTools.Method(props, "InitializeHeldItem", new[] { typeof(GameObject), item }), nameof(BeforeRebreatherInitializeHeldItem)),
                (AccessTools.Method(props, "InitializeWeaponInfo", new[] { typeof(GameObject) }), nameof(BeforeRebreatherInitializeWeaponInfo)),
                (AccessTools.Method(typeof(HeldControllerBase), "Initialize", initialize), nameof(BeforeRebreatherHeldControllerInitialize)),
                (AccessTools.Method(typeof(RebreatherController), "Initialize", initialize), nameof(BeforeRebreatherControllerInitialize))
            };
        }

        private static bool BeforeRebreatherSetItemObject(ItemInstance __instance, GameObject __0,
            bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                if (BlocksItemPointer(__instance?.Pointer ?? 0) || BlocksObjectPointer(__0?.Pointer ?? 0)) return false;
                return AllowRebreatherItemRefresh(__instance);
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeRebreatherItemRefresh(ItemInstance __instance, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try { return AllowRebreatherItemRefresh(__instance); }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool AllowRebreatherItemRefresh(ItemInstance item)
        {
            // Retained pointers are protected even after retirement, before field reads.
            if (BlocksItemPointer(item?.Pointer ?? 0)) return false;
            if (!OnOwnerThread) return AllowUnreadableOwner();
            if (item is null || item.Pointer == 0 || item.WasCollected) return AllowUnreadableOwner();
            var itemObject = item._ItemObject_k__BackingField;
            return !BlocksObjectPointer(itemObject?.Pointer ?? 0);
        }

        private static bool BeforeRebreatherInitializeCallback(InventoryProps __instance,
            GameObject __0, ItemInstance __2, bool __runOriginal)
            => AllowRebreatherEquipmentInitialization(__instance, __0, __2, true, __runOriginal);

        private static bool BeforeRebreatherInitializeHeldItem(InventoryProps __instance,
            GameObject __0, ItemInstance __1, bool __runOriginal)
            => AllowRebreatherEquipmentInitialization(__instance, __0, __1, true, __runOriginal);

        private static bool BeforeRebreatherInitializeWeaponInfo(InventoryProps __instance,
            GameObject __0, bool __runOriginal)
            => AllowRebreatherEquipmentInitialization(__instance, __0, null, false, __runOriginal);

        private static bool AllowRebreatherEquipmentInitialization(InventoryProps props, GameObject itemObject,
            ItemInstance item, bool hasItem, bool runOriginal)
        {
            if (!runOriginal || !IsObserving) return true;
            try
            {
                if (BlocksItemPointer(item?.Pointer ?? 0) || BlocksObjectPointer(itemObject?.Pointer ?? 0)) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                if (props is null || props.WasCollected || !props) return AllowUnreadableOwner();
                if (hasItem && (item is null || item.Pointer == 0 || item.WasCollected))
                    return AllowUnreadableOwner();
                if (!OwnsProps(props) && !OwnsInventory(props._playerInventory)) return true;
                if (hasItem && item._itemID == 444) return false;
                var objects = props._gameObjectInstances;
                if (objects is null || objects.Pointer == 0 || objects.WasCollected)
                    return AllowUnreadableOwner();
                if (!objects.TryGetValue(444, out var rebreatherObject)) return true;
                return rebreatherObject is null || itemObject is null
                    || rebreatherObject.Pointer != itemObject.Pointer;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeRebreatherHeldControllerInitialize(HeldControllerBase __instance,
            ItemInstance __5, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try { return AllowRebreatherControllerInitialization(__instance, __5); }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeRebreatherControllerInitialize(RebreatherController __instance,
            Animator __0, ItemInstance __5, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                // The override writes derived state after its base call. Guard it
                // independently, including new controllers for an owned animator.
                if (BlocksAnimatorPointer(__0?.Pointer ?? 0)) return false;
                return AllowRebreatherControllerInitialization(__instance, __5);
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool AllowRebreatherControllerInitialization(HeldControllerBase controller, ItemInstance item)
        {
            if (BlocksItemPointer(item?.Pointer ?? 0) || BlocksControllerPointer(controller?.Pointer ?? 0)) return false;
            if (!OnOwnerThread) return AllowUnreadableOwner();
            if (controller is null || controller.WasCollected || !controller) return AllowUnreadableOwner();
            return !BlocksItemPointer(controller._itemInstance?.Pointer ?? 0);
        }
    }
}
