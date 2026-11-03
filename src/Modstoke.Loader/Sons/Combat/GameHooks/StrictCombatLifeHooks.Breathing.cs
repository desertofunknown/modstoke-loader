// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static bool InstallBreathingHooks()
        {
            if (_breathingPatchAttempted) return _breathingHooksInstalled;
            _breathingPatchAttempted = true;
            if (_support == null || !_support.SafeForHarmonyPatches) return false;
            try
            {
                var drain = AccessTools.Method(typeof(PlayerStats), "ProcessAirDrain", new[] { typeof(BreathingData) });
                var transfer = AccessTools.Method(typeof(PlayerStats), "UseRebreather", new[] { typeof(bool) });
                var capacity = AccessTools.PropertyGetter(typeof(BreathingData), "MaxLungAirCapacityFinal");
                if (drain == null || drain.IsStatic || drain.ReturnType != typeof(bool)
                    || transfer == null || transfer.IsStatic || transfer.ReturnType != typeof(void)
                    || capacity == null || capacity.IsStatic || capacity.ReturnType != typeof(float))
                    throw new MissingMethodException("A native lung oxygen hook is unavailable.");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-strict-combat-native-breathing");
                harmony.Patch(drain,
                    prefix: new HarmonyMethod(typeof(StrictCombatLifeHooks), nameof(BeforeAirDrain)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(StrictCombatLifeHooks), nameof(FinishBreathingCall)) { priority = Priority.Last });
                harmony.Patch(transfer,
                    prefix: new HarmonyMethod(typeof(StrictCombatLifeHooks), nameof(BeforeBreathingTransfer)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(StrictCombatLifeHooks), nameof(FinishBreathingCall)) { priority = Priority.Last });
                harmony.Patch(capacity, prefix: new HarmonyMethod(typeof(StrictCombatLifeHooks), nameof(BeforeLungCapacity)) { priority = Priority.First });
                _breathingHooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                try { _warning?.Invoke("Strict combat native lung oxygen hooks unavailable: " + error.Message); }
                catch { }
                return false;
            }
        }

        private static bool BeforeAirDrain(PlayerStats __instance, BreathingData __0,
            bool __runOriginal, ref bool __result, out int __state)
        {
            __state = 0;
            if (!__runOriginal || (!_breathingPrepared && !HasBreathingOwner())) return true;
            try
            {
                if (FindBreathingOwner(__instance?.Pointer ?? 0, __0?.Pointer ?? 0) != null)
                {
                    // The result controls local warning audio, not oxygen accounting.
                    __result = false;
                    return false;
                }
                if (__instance == null || __0 == null || !OnOwnerThread
                    || __instance.WasCollected || __0.WasCollected)
                {
                    if (AllowUnknownBreathingOwner()) return true;
                    __result = false;
                    return false;
                }
                __state = BeginBreathingCall();
                RememberBreathing(__instance, __0);
                return true;
            }
            catch (Exception)
            {
                if (AllowUnknownBreathingOwner()) return true;
                __result = false;
                return false;
            }
        }

        private static bool BeforeBreathingTransfer(PlayerStats __instance, bool __runOriginal, out int __state)
        {
            __state = 0;
            if (!__runOriginal || (!_breathingPrepared && !HasBreathingOwner())) return true;
            try
            {
                if (FindBreathingOwner(__instance?.Pointer ?? 0, 0) != null) return false;
                if (__instance == null || !OnOwnerThread || __instance.WasCollected)
                    return AllowUnknownBreathingOwner();
                var breathing = __instance._vitals?.LungBreathing;
                if (FindBreathingOwner(0, breathing?.Pointer ?? 0) != null) return false;
                __state = BeginBreathingCall();
                RememberBreathing(__instance, breathing);
                return true;
            }
            catch (Exception) { return AllowUnknownBreathingOwner(); }
        }

        private static bool BeforeLungCapacity(BreathingData __instance, bool __runOriginal, ref float __result)
        {
            if (!__runOriginal) return true;
            var owner = FindBreathingOwner(0, __instance?.Pointer ?? 0);
            if (owner == null) return true;
            // Retired identities retain their last accepted capacity. Do not consult
            // Unity state or fall back to local difficulty during a late callback.
            __result = owner.Capacity;
            return false;
        }
    }
}
