// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Cutscenes;
using global::Sons.Multiplayer;
using global::Sons.Player.Stats;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static bool InstallHooks()
        {
            if (_patchAttempted)
                return _hooksInstalled;
            _patchAttempted = true;
            if (_support == null || !_support.SafeForHarmonyPatches)
                return false;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var stats = typeof(PlayerStats);
                var targets = new List<(MethodInfo Method, string Prefix)>
                {
                    (AccessTools.Method(stats, "OnNetworkHit", new[] { typeof(int), typeof(global::ImpactType), typeof(float), typeof(Vector3), typeof(float) }), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "StartDeathRoutine", new[] { typeof(DeathType) }), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "DoBasicDeath", new[] { typeof(bool) }), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "CheckFirstDeathRoutine", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "CheckFirstDeathRoutineNoPrompt", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "CheckFirstDeathOption", new[] { typeof(bool) }), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "EndDeathRoutine", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "EndSwimmingDeathRoutine", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "KillPlayer", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "QuickKillPlayer", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "KillPlayerWithRespawnPrompt", new[] { typeof(bool) }), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "MultiplayerRespawn", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "FirstDeathRoutine", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "OnDeathMarkerRequestResponse", new[] { typeof(global::DeathMarkerRequest) }), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "StartDrownWakeup", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "InstantRespawnRoutine", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "FinalizeKillPlayer", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "HealedMp", new[] { typeof(bool) }), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "StartResetFromHeal", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "FinalizeResetFromHeal", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "SetFinalDeath", Type.EmptyTypes), nameof(BeforeStats)),
                    (AccessTools.Method(stats, "CheckDeath", new[] { typeof(DeathType) }), nameof(BeforeCheckDeath)),
                    (AccessTools.Method(stats, "SetDyingState", new[] { typeof(PlayerStats.DyingState) }), nameof(BeforeDyingState)),
                    (AccessTools.Method(typeof(PlayerStats._MultiplayerRespawnWorker_d__260), "MoveNext", Type.EmptyTypes), nameof(BeforeRespawnWorker)),
                    (AccessTools.Method(typeof(PlayerStats._ResetPlayerFromHeal_d__264), "MoveNext", Type.EmptyTypes), nameof(BeforeResetWorker)),
                    (AccessTools.Method(typeof(PlayerStats._DyingMpAudio_d__248), "MoveNext", Type.EmptyTypes), nameof(BeforeDyingAudio)),
                    (AccessTools.Method(typeof(PlayerStats._WakeUpOnBeachCutScene_d__336), "MoveNext", Type.EmptyTypes), nameof(BeforeBeachWakeup)),
                    (AccessTools.Method(typeof(MultiplayerRespawnTimer), "DoRespawn", Type.EmptyTypes), nameof(BeforeTimer)),
                    (AccessTools.Method(typeof(PlayerDeathCutsceneBase), "SelectAndStartDeathCutscene", new[] { typeof(Animator), typeof(global::camFollowHead), typeof(PlayerDeathCutsceneMarker) }), nameof(BeforeCutscene)),
                    (AccessTools.Method(typeof(PlayerDeathCutsceneBase), "SelectAndStartDeathCutscene", new[] { typeof(Animator), typeof(global::camFollowHead) }), nameof(BeforeCutscene)),
                    (AccessTools.Method(typeof(PlayerDeathCutsceneBase), "SelectAndStartDeathCutsceneFromEvent", new[] { typeof(Animator), typeof(global::camFollowHead), typeof(global::DeathCutsceneType), typeof(int) }), nameof(BeforeCutscene)),
                    (AccessTools.Method(typeof(PlayerDeathCutsceneBase), "SelectAndStartDeathCutsceneInternal", new[] { typeof(Animator), typeof(global::camFollowHead), typeof(global::DeathCutsceneType), typeof(Vector3) }), nameof(BeforeCutscene))
                };
                targets.AddRange(NativeHealthTargets());
                targets.AddRange(NativeEquipmentTargets());
                foreach (var target in targets)
                    if (target.Method == null)
                        throw new MissingMethodException("A strict combat native life hook is unavailable.");
                // Shared native setters need raw receiver gates, not generated Stat casts.
                // Resolve and qualify every target before installing any of this hook set.
                _sharedHealthSetters = PrepareSharedHealthSetters();

                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-strict-combat-native-life");
                foreach (var target in targets)
                {
                    string finalizer = target.Prefix switch
                    {
                        nameof(BeforeVitalsInitialization) => nameof(AfterVitalsInitialization),
                        nameof(BeforeVitalsLoad) => nameof(AfterVitalsLoad),
                        _ => null
                    };
                    harmony.Patch(target.Method, prefix: new HarmonyMethod(typeof(StrictCombatLifeHooks), target.Prefix)
                    {
                        priority = Priority.First
                    }, finalizer: finalizer == null ? null
                        : new HarmonyMethod(typeof(StrictCombatLifeHooks), finalizer) { priority = Priority.Last });
                }
                foreach (var setter in _sharedHealthSetters) setter.Install();
                _hooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                try { _warning?.Invoke("Strict combat native life hooks unavailable: " + error.Message); }
                catch { }
                // Native detour callbacks and trampolines stay rooted; only managed patches are removed.
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                return false;
            }
        }

        private static bool BeforeStats(PlayerStats __instance, bool __runOriginal) => AllowStats(__instance, __runOriginal);

        private static bool BeforeCheckDeath(PlayerStats __instance, bool __runOriginal) => AllowCheckDeath(__instance, __runOriginal);

        private static bool BeforeDyingState(PlayerStats __instance, PlayerStats.DyingState __0, bool __runOriginal) =>
            AllowDyingState(__instance, __0, __runOriginal);

        private static bool BeforeTimer(MultiplayerRespawnTimer __instance, bool __runOriginal) => AllowTimer(__instance, __runOriginal);

        private static bool BeforeCutscene(bool __runOriginal) => AllowCutscene(__runOriginal);

        private static bool BeforeRespawnWorker(PlayerStats._MultiplayerRespawnWorker_d__260 __instance, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || !IsObserving) return true;
            PlayerStats owner;
            try { owner = __instance.__4__this; }
            catch { return AllowMoveNext(AllowUnreadableOwner(), ref __result); }
            return AllowMoveNext(AllowStats(owner, __runOriginal), ref __result);
        }

        private static bool BeforeResetWorker(PlayerStats._ResetPlayerFromHeal_d__264 __instance, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || !IsObserving) return true;
            PlayerStats owner;
            try { owner = __instance.__4__this; }
            catch { return AllowMoveNext(AllowUnreadableOwner(), ref __result); }
            return AllowMoveNext(AllowStats(owner, __runOriginal), ref __result);
        }

        private static bool BeforeDyingAudio(PlayerStats._DyingMpAudio_d__248 __instance, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || !IsObserving) return true;
            PlayerStats owner;
            try { owner = __instance.__4__this; }
            catch { return AllowMoveNext(AllowUnreadableOwner(), ref __result); }
            return AllowMoveNext(AllowStats(owner, __runOriginal), ref __result);
        }

        private static bool BeforeBeachWakeup(PlayerStats._WakeUpOnBeachCutScene_d__336 __instance, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || !IsObserving) return true;
            PlayerStats owner;
            try { owner = __instance.__4__this; }
            catch { return AllowMoveNext(AllowUnreadableOwner(), ref __result); }
            return AllowMoveNext(AllowStats(owner, __runOriginal), ref __result);
        }

        private static bool AllowMoveNext(bool allow, ref bool result)
        {
            if (allow)
                return true;
            result = false;
            return false;
        }
    }
}
