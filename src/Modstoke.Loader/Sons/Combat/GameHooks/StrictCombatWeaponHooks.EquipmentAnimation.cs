// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Endnight.Animation;
using HarmonyLib;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static StrictCombatEquipmentAnimation[] _equipmentAnimations = Array.Empty<StrictCombatEquipmentAnimation>();

        private static void InstallEquipmentAnimationHooks(HarmonyLib.Harmony harmony)
        {
            var update = AccessTools.Method(typeof(AnimationSync), "FixedUpdateManaged", new[] { typeof(Animator), typeof(Animator) });
            var driver = AccessTools.Method(typeof(AnimationSync), "SetDriver", new[] { typeof(Animator) });
            if (update == null || driver == null)
                throw new MissingMethodException("The equipment animation hooks are unavailable.");
            // Both bodies are unique in the supported native image. The no-argument
            // fixed update and OnAnimatorMove both call this exact two-argument method.
            harmony.Patch(update,
                prefix: new HarmonyMethod(typeof(StrictCombatWeaponHooks), nameof(BeforeEquipmentAnimationUpdate)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(StrictCombatWeaponHooks), nameof(AfterEquipmentAnimationUpdate)) { priority = Priority.Last });
            harmony.Patch(driver,
                prefix: new HarmonyMethod(typeof(StrictCombatWeaponHooks), nameof(BeforeEquipmentAnimationDriver)) { priority = Priority.First });
        }

        internal static bool TryOwnEquipmentAnimation(StrictCombatEquipmentAnimation animation)
        {
            if (!OnOwnerThread || !_prepared || !_hooksInstalled || _historyIncomplete || animation == null) return false;
            if (animation.Sync is null) return true;
            var owners = Volatile.Read(ref _equipmentAnimations);
            if (owners.Length >= MaximumOwners) return false;
            foreach (var owner in owners)
                if (Same(owner.Sync, animation.Sync)) return false;
            var next = new StrictCombatEquipmentAnimation[owners.Length + 1];
            Array.Copy(owners, next, owners.Length);
            next[^1] = animation;
            Volatile.Write(ref _equipmentAnimations, next);
            return true;
        }

        private static StrictCombatEquipmentAnimation FindEquipmentAnimation(AnimationSync sync)
        {
            foreach (var owner in Volatile.Read(ref _equipmentAnimations))
                if (Same(owner.Sync, sync)) return owner;
            return null;
        }

        private static bool BeforeEquipmentAnimationDriver(AnimationSync __instance, bool __runOriginal)
            => !__runOriginal || FindEquipmentAnimation(__instance) == null;

        private static bool BeforeEquipmentAnimationUpdate(AnimationSync __instance, Animator __0, Animator __1,
            bool __runOriginal, ref StrictCombatEquipmentAnimation __state)
        {
            __state = null;
            if (!__runOriginal) return true;
            var owner = FindEquipmentAnimation(__instance);
            if (owner == null) return true;
            if (!owner.TryEnterUpdate(__0, __1)) return false;
            __state = owner;
            return true;
        }

        private static Exception AfterEquipmentAnimationUpdate(Exception __exception, StrictCombatEquipmentAnimation __state)
        {
            __state?.EndUpdate(__exception);
            return __exception;
        }
    }
}
