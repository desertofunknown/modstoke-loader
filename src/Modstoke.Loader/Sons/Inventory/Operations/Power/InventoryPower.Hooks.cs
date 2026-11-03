// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Weapon;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private static bool _nativePowerHooksAttempted, _nativePowerHooksInstalled;

        // Install after inventory module guards, before capturing or resuming any source.
        internal static void InstallPowerPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_nativePowerHooksAttempted) return;
            _nativePowerHooksAttempted = true;
            if (!_hooksInstalled || support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized
                || Environment.CurrentManagedThreadId != _hookThread) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var update = AccessTools.Method(typeof(FlashlightController), "Update", Type.EmptyTypes);
                QualifyBoundary(update, (typeof(void), 0x34CBBD0, 1696,
                    "B96BAB65A4511969FB5DBA1EF528C9D81F1F47FA079E5A7D53B1BAE15DFEE3BD"));
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-inventory-power");
                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeFlashlightPower)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterFlashlightPower)) { priority = Priority.Last });
                _nativePowerHooksInstalled = true;
            }
            catch (Exception error)
            {
                string rollback = "";
                try { harmony?.UnpatchSelf(); }
                catch (Exception cleanup) { rollback = " Patch rollback unconfirmed: " + cleanup.Message; }
                warning?.Invoke("Flashlight consumer exclusion unavailable: " + error.Message + rollback);
            }
        }

        // Empty is already suppressed for this retained module. A negative threshold makes
        // the native refill START unreachable, before Owns/RemoveItem/audio/UI notifications.
        // Keep the native Update running: it also performs IK, light and battery presentation.
        private static bool BeforeFlashlightPower(FlashlightController __instance, bool __runOriginal,
            out FlashlightUpdateScope __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            if (__instance == null || __instance.WasCollected || !__instance) return true;
            var guard = _guard;
            if (guard == null || !guard.IsNativeGuardHeld || !guard.RetainsModule(__instance._powerCapacity)) return true;
            if (!guard.OnThread) throw new InvalidOperationException("Retained flashlight Update left its game thread.");
            if (!InventoryPower.ValidModule(__instance._powerCapacity))
            {
                // Fault once and keep skipping this Update; a throwing prefix would log every frame.
                if (!guard.IsFaulted) guard.Fail("Retained flashlight power is outside its admitted native range.");
                return false;
            }
            EnterWriter(out bool entered);
            __state = new(guard, __instance._powerCapacity.Pointer, __instance._autoRefilThreshold, entered);
            __instance._autoRefilThreshold = -1;
            return true;
        }

        private static Exception AfterFlashlightPower(FlashlightController __instance, Exception __exception,
            FlashlightUpdateScope __state)
        {
            if (__state.Guard == null) return __exception;
            try
            {
                if (__instance != null && !__instance.WasCollected && __instance)
                {
                    bool unchanged = __instance._autoRefilThreshold == -1
                        && __instance._powerCapacity?.Pointer == __state.Module;
                    __instance._autoRefilThreshold = __state.Threshold;
                    if (!unchanged) __state.Guard.Fail("Flashlight consumer binding changed inside its retained Update.");
                }
                if (__exception != null) __state.Guard.Fail("Retained flashlight Update failed: " + __exception.Message);
            }
            catch (Exception error)
            {
                __state.Guard.Fail("Flashlight consumer exclusion could not restore its exact scope: " + error.Message);
                return __exception ?? error;
            }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }

        private readonly record struct FlashlightUpdateScope(PlayerInventoryEnforcement Guard,
            nint Module, float Threshold, bool Entered);
    }
}
