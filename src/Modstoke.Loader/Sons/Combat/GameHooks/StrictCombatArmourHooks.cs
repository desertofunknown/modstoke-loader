// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Wearable;
using global::Sons.Wearable.Armour;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatArmourHooks
    {
        private static bool InstallHooks()
        {
            if (_patchAttempted) return _hooksInstalled;
            _patchAttempted = true;
            if (_support == null || !_support.SafeForHarmonyPatches) return false;
            try
            {
                var system = typeof(PlayerArmourSystem);
                var targets = new (MethodInfo Method, string Prefix)[]
                {
                    (AccessTools.Method(system, "EquipToNextAvailableSlot", new[] { typeof(ArmourPiece) }), nameof(BeforeBooleanMutation)),
                    (AccessTools.Method(system, "EquipToSlot", new[] { typeof(int), typeof(WearableSlots) }), nameof(BeforeBooleanMutation)),
                    (AccessTools.Method(system, "EquipToSlot", new[] { typeof(ArmourPiece), typeof(WearableSlots) }), nameof(BeforeBooleanMutation)),
                    (AccessTools.Method(system, "TryUnequipSlot", new[] { typeof(WearableSlots), typeof(bool) }), nameof(BeforeUnequip)),
                    (AccessTools.Method(system, "CalculateRemainingDamageAfterArmourHit", new[] { typeof(float), typeof(bool) }), nameof(BeforeDamageMutation)),
                    (AccessTools.Method(system, "WearArmourList", new[] { typeof(Il2CppStructArray<int>) }), nameof(BeforeMutation)),
                    (AccessTools.Method(system, "Sons_Save_ISaveGameSerializer_Sons_Wearable_Armour_PlayerArmourSystem_PlayerArmourSaveData__OnDeserialize", new[] { typeof(PlayerArmourSystem.PlayerArmourSaveData) }), nameof(BeforeMutation)),
                    (AccessTools.Method(system, "OnClothingEquippedEvent", new[] { typeof(int), typeof(ItemInstance) }), nameof(BeforeMutation)),
                    (AccessTools.Method(system, "OnValidate", Type.EmptyTypes), nameof(BeforeMutation))
                };
                foreach (var target in targets)
                    if (target.Method == null)
                        throw new MissingMethodException("A strict combat native armour hook is unavailable.");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-strict-combat-native-armour");
                foreach (var target in targets)
                    harmony.Patch(target.Method,
                        prefix: new HarmonyMethod(typeof(StrictCombatArmourHooks), target.Prefix) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(StrictCombatArmourHooks), nameof(AfterMutation)) { priority = Priority.Last });
                _hooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                try { _warning?.Invoke("Strict combat native armour hooks unavailable: " + error.Message); }
                catch { }
                return false;
            }
        }

        private static bool BeforeBooleanMutation(PlayerArmourSystem __instance, bool __runOriginal,
            ref bool __result, out bool __state)
        {
            if (EnterMutation(__instance, __runOriginal, out __state)) return true;
            __result = false;
            return false;
        }

        private static bool BeforeDamageMutation(PlayerArmourSystem __instance, bool __runOriginal,
            ref float __result, out bool __state)
        {
            if (EnterMutation(__instance, __runOriginal, out __state)) return true;
            __result = 0f;
            return false;
        }

        private static bool BeforeUnequip(PlayerArmourSystem __instance, WearableSlots __0, bool __1,
            bool __runOriginal, ref bool __result, out bool __state)
        {
            if (EnterUnequip(__instance, __0, __1, __runOriginal, out __state)) return true;
            __result = false;
            return false;
        }

        private static bool BeforeMutation(PlayerArmourSystem __instance, bool __runOriginal, out bool __state)
            => EnterMutation(__instance, __runOriginal, out __state);

        private static Exception AfterMutation(Exception __exception, bool __state)
            => FinishMutation(__state, __exception);
    }
}
