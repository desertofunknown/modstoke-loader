// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Gameplay;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalRuntime
    {
        private static bool _sleepHookAttempted, _sleepHookInstalled;

        private static void EnsureSleepContinuationHook()
        {
            if (_sleepHookInstalled) return;
            if (_sleepHookAttempted) throw new InvalidOperationException("Native sleep continuation hook is unavailable.");
            _sleepHookAttempted = true;
            if (!GameBuild.IsRecognized || HarmonySupport.CreateState()?.SafeForHarmonyPatches != true)
                throw new InvalidOperationException("Native sleep continuation requires the recognized binary and supported hooks.");
            var target = PlayerLifecycle.QualifyNative(typeof(SleepingManager),
                nameof(SleepingManager.SnapshotSleepingTimes), 0x33AEBD0, false, typeof(void), typeof(Il2CppSystem.TimeSpan));
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-actor-sleep-continuation");
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(PlayerSurvivalRuntime),
                    nameof(BeforeSleepContinuation)) { priority = Priority.Last });
                _sleepHookInstalled = true;
            }
            catch
            {
                harmony.UnpatchSelf();
                throw;
            }
        }

        private static void BeforeSleepContinuation(SleepingManager __instance, bool __runOriginal)
        {
            if (__runOriginal) BeforeNativeSleepFadeComplete(__instance);
        }
    }
}
