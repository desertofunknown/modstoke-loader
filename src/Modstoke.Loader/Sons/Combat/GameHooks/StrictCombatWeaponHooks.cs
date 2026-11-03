// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Weapon;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static bool InstallHooks()
        {
            if (_patchAttempted) return _hooksInstalled;
            _patchAttempted = true;
            if (_support == null || !_support.SafeForHarmonyPatches) return false;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var controller = typeof(RangedWeaponController);
                var weapon = typeof(RangedWeapon);
                var targets = new List<(MethodInfo Method, string Prefix)>
                {
                    (AccessTools.Method(controller, "CheckFireInput", Type.EmptyTypes), nameof(BeforeFireInput)),
                    (AccessTools.Method(controller, "OnReloadAction", new[] { typeof(InputAction.CallbackContext) }), nameof(BeforeReloadInput)),
                    (AccessTools.Method(controller, "FireWeapon", Type.EmptyTypes), nameof(BeforeFire)),
                    (AccessTools.Method(controller, "TriggerFireWeapon", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "TriggerStartAttack", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "OnAmmoSpent", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "Reload", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "ReloadAmmo", new[] { weapon, typeof(bool) }), nameof(BeforeController)),
                    (AccessTools.Method(controller, "CycleAmmoType", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "RangeShootCallback", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "TriggerShotFiredAudio", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(typeof(CompactPistolWeaponController), "TriggerShotFiredAudio", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "OnFireWeaponNetwork", Type.EmptyTypes), nameof(BeforeController)),
                    (AccessTools.Method(controller, "OnFireWeaponVail", new[] { typeof(bool) }), nameof(BeforeController)),
                    (AccessTools.Method(typeof(RangedWeaponController._ReloadDelayRoutine_d__89), "MoveNext", Type.EmptyTypes), nameof(BeforeReloadDelay)),
                    (AccessTools.Method(weapon, "Shoot", Type.EmptyTypes), nameof(BeforeWeapon)),
                    (AccessTools.Method(weapon, "ShootInternal", new[] { typeof(Quaternion) }), nameof(BeforeWeapon)),
                    (AccessTools.Method(weapon, "Shoot", new[] { typeof(Vector3), typeof(float), typeof(int) }), nameof(BeforeWeapon)),
                    (AccessTools.Method(typeof(Ballistics.Weapon), "Shoot", new[] { typeof(Vector3), typeof(float), typeof(int) }), nameof(BeforeBallisticsWeapon)),
                    (AccessTools.Method(weapon, "Sons_Ai_Vail_IVailRangedWeapon_Shoot", new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Vector3) }), nameof(BeforeWeapon)),
                    (AccessTools.Method(weapon, "Sons_Ai_Vail_IVailRangedWeapon_ShootClient", Type.EmptyTypes), nameof(BeforeWeapon))
                };
                targets.AddRange(AmmoTargets());
                targets.AddRange(CleanupTargets());
                targets.AddRange(EquipmentInitializationTargets());
                targets.AddRange(WeaponInitializationTargets());
                _sharedAmmoSetters = PrepareSharedAmmoSetters();
                _projectileHooks = new NativeProjectileHooks();
                // RangeReloadCallback dispatches Reload. Shot audio also emits native noise
                // and network audio; accepted local presentation uses explicit local playback.
                // OnItemLeavesHandCallback shares an empty native body and must not be patched.
                foreach (var target in targets)
                    if (target.Method == null)
                        throw new MissingMethodException("A strict combat native weapon hook is unavailable.");
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-strict-combat-native-weapon");
                foreach (var target in targets)
                    harmony.Patch(target.Method, prefix: new HarmonyMethod(typeof(StrictCombatWeaponHooks), target.Prefix)
                    {
                        priority = Priority.First
                    });
                foreach (var setter in _sharedAmmoSetters) setter.Install();
                _projectileHooks.Install();
                InstallEquipmentAnimationHooks(harmony);
                InstallEquipmentActivationHooks(harmony);
                _hooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                try { _warning?.Invoke("Strict combat native weapon hooks unavailable: " + error.Message); }
                catch { }
                // Native detour callbacks and trampolines stay rooted; only managed patches are removed.
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                return false;
            }
        }

        private static bool BeforeController(RangedWeaponController __instance, bool __runOriginal)
            => AllowController(__instance, __runOriginal);

        private static bool BeforeFire(RangedWeaponController __instance, bool __runOriginal)
            => AllowController(__instance, __runOriginal, observeFire: true);

        private static bool BeforeWeapon(RangedWeapon __instance, bool __runOriginal)
            => AllowWeapon(__instance, __runOriginal);

        private static bool BeforeBallisticsWeapon(Ballistics.Weapon __instance, bool __runOriginal)
            => AllowBallisticsWeapon(__instance, __runOriginal);

        private static bool BeforeFireInput(RangedWeaponController __instance, bool __runOriginal)
            => AllowInput(__instance, StrictCombatAction.Fire, __runOriginal);

        private static bool BeforeReloadInput(RangedWeaponController __instance,
            InputAction.CallbackContext __0, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            // Do not inspect the native context off-thread or when this receiver is unowned.
            try
            {
                var owner = FindController(__instance);
                if (owner == null) return AllowController(__instance, true);
                if (OnOwnerThread && !owner.IsRetired && __0.performed)
                    owner.Owner.ObserveWeaponInput(owner, __instance, StrictCombatAction.Reload);
                return false;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeReloadDelay(RangedWeaponController._ReloadDelayRoutine_d__89 __instance,
            bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || !IsObserving) return true;
            bool allow;
            if (!OnOwnerThread) allow = AllowUnreadableOwner();
            else
            {
                try { allow = AllowController(__instance.__4__this, true); }
                catch (Exception) { allow = AllowUnreadableOwner(); }
            }
            if (allow) return true;
            // The coroutine can outlive its player; never perform native cleanup here.
            __result = false;
            return false;
        }
    }
}
