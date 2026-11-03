// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using global::Sons.Inventory;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static (MethodInfo Method, string Prefix)[] AmmoTargets()
        {
            var ammo = typeof(RangedWeapon.Ammo);
            var module = typeof(RangedWeaponItemInstanceModule);
            return new (MethodInfo, string)[]
            {
                (AccessTools.Method(ammo, "Remove", new[] { typeof(int) }), nameof(BeforeAmmoMutation)),
                (AccessTools.Method(ammo, "AddAmmo", new[] { typeof(int) }), nameof(BeforeAmmoMutation)),
                (AccessTools.Method(ammo, "SetItemType", new[] { typeof(int) }), nameof(BeforeAmmoBooleanMutation)),
                (AccessTools.Method(ammo, "Refresh", Type.EmptyTypes), nameof(BeforeAmmoBooleanMutation)),
                (AccessTools.Method(module, "Refresh", new[] { typeof(ItemInstance), typeof(GameObject) }), nameof(BeforeAmmoModuleRefresh)),
                (AccessTools.Method(module, "LoadFromSave", new[] { typeof(ItemInstanceModule.SaveData) }), nameof(BeforeAmmoModuleMutation))
            };
        }

        // Retired captures still own these pointers. Never inspect native fields here.
        private static bool BlocksAmmoPointer(nint pointer)
        {
            if (!IsObserving) return false;
            if (pointer == 0) return !AllowUnreadableOwner();
            try
            {
                if (IsPreparedAmmo(pointer)) return true;
                foreach (var owner in Volatile.Read(ref _owners))
                    if (owner.Capture.Ammo.Pointer == pointer) return true;
                return false;
            }
            catch (Exception) { return !AllowUnreadableOwner(); }
        }

        private static bool BlocksModulePointer(nint pointer)
        {
            if (!IsObserving) return false;
            if (pointer == 0) return !AllowUnreadableOwner();
            try
            {
                foreach (var owner in Volatile.Read(ref _owners))
                    if (owner.Capture.Module.Pointer == pointer) return true;
                return false;
            }
            catch (Exception) { return !AllowUnreadableOwner(); }
        }

        private static bool BlocksWeaponPointer(nint pointer)
        {
            if (!IsObserving) return false;
            if (pointer == 0) return !AllowUnreadableOwner();
            try
            {
                if (IsPreparedWeapon(pointer)) return true;
                foreach (var owner in Volatile.Read(ref _owners))
                    if (owner.Capture.Weapon.Pointer == pointer) return true;
                return false;
            }
            catch (Exception) { return !AllowUnreadableOwner(); }
        }

        private static bool BlocksItemPointer(nint pointer)
        {
            if (!IsObserving) return false;
            if (pointer == 0) return !AllowUnreadableOwner();
            try
            {
                foreach (var owner in Volatile.Read(ref _owners))
                    if (owner.Capture.Item.Pointer == pointer) return true;
                return false;
            }
            catch (Exception) { return !AllowUnreadableOwner(); }
        }

        private static bool BeforeAmmoMutation(RangedWeapon.Ammo __instance, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try { return __instance is null ? AllowUnreadableOwner() : !BlocksAmmoPointer(__instance.Pointer); }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeAmmoBooleanMutation(RangedWeapon.Ammo __instance,
            bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || !IsObserving) return true;
            if (BeforeAmmoMutation(__instance, true)) return true;
            __result = false;
            return false;
        }

        private static bool BeforeAmmoModuleMutation(RangedWeaponItemInstanceModule __instance,
            bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try { return __instance is null ? AllowUnreadableOwner() : !BlocksModulePointer(__instance.Pointer); }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeAmmoModuleRefresh(RangedWeaponItemInstanceModule __instance,
            ItemInstance __0, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                if (__instance is not null && BlocksModulePointer(__instance.Pointer)) return false;
                if (__0 is not null && BlocksItemPointer(__0.Pointer)) return false;
                if (__instance is null || __0 is null) return AllowUnreadableOwner();
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }
    }
}
