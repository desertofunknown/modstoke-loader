// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using global::Sons.Animation;
using global::Sons.Inventory;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static (MethodInfo Method, string Prefix)[] WeaponInitializationTargets()
        {
            var controller = typeof(RangedWeaponController);
            var initialize = new[] { typeof(Animator), typeof(global::PlayerAnimatorControl),
                typeof(AnimationEventReceiver), typeof(Transform), typeof(Transform), typeof(ItemInstance) };
            return new (MethodInfo, string)[]
            {
                (AccessTools.Method(typeof(HeldControllerBase), "Initialize", initialize), nameof(BeforeHeldControllerInitialize)),
                (AccessTools.Method(controller, "Initialize", initialize), nameof(BeforeRangedControllerInitialize)),
                (AccessTools.Method(controller, "CacheRangedWeapon", Type.EmptyTypes), nameof(BeforeWeaponBinding)),
                (AccessTools.Method(controller, "SetAmmoItemInstance", Type.EmptyTypes), nameof(BeforeWeaponBinding)),
                // OnEnable shares native code with an unrelated input-device method.
                // Intercept its unique event-registration callee instead.
                (AccessTools.Method(controller, "RegisterEvents", Type.EmptyTypes), nameof(BeforeWeaponBinding))
            };
        }

        private static bool BeforeHeldControllerInitialize(HeldControllerBase __instance,
            ItemInstance __5, bool __runOriginal)
            => AllowControllerInitialization(__instance, __5, __runOriginal);

        private static bool BeforeRangedControllerInitialize(RangedWeaponController __instance,
            ItemInstance __5, bool __runOriginal)
            => AllowControllerInitialization(__instance, __5, __runOriginal)
                && BeforeWeaponBinding(__instance, __runOriginal);

        private static bool AllowControllerInitialization(HeldControllerBase controller,
            ItemInstance item, bool runOriginal)
        {
            if (!runOriginal || !IsObserving) return true;
            try
            {
                if (IsCapturedEquipmentItem(item) || IsCapturedController(controller)) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                if (controller is null || controller.WasCollected || !controller)
                    return AllowUnreadableOwner();
                return !IsCapturedEquipmentItem(controller._itemInstance);
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeWeaponBinding(RangedWeaponController __instance, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                if (IsCapturedController(__instance)) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                if (__instance is null || __instance.WasCollected || !__instance)
                    return AllowUnreadableOwner();
                if (IsCapturedEquipmentItem(__instance._itemInstance)) return false;
                var weapon = __instance._rangedWeapon;
                // A new controller can already point at retained weapon state. A null
                // association is normal before initialization and is not ownership.
                return weapon is null || !BlocksWeaponPointer(weapon.Pointer);
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool IsCapturedController(HeldControllerBase controller)
        {
            if (FindPreparedController(controller) != null) return true;
            foreach (var owner in Volatile.Read(ref _owners))
                if (Same(owner.Capture.Controller, controller)) return true;
            return false;
        }
    }
}
