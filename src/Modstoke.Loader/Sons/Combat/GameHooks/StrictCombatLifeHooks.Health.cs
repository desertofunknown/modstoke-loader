// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using global::Sons.Items.Core;
using global::Sons.StatSystem;
using TheForest.Items.Utils;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static (MethodInfo Method, string Prefix)[] NativeHealthTargets()
        {
            var stat = typeof(Stat);
            var vitals = typeof(Vitals);
            return new (MethodInfo, string)[]
            {
                (AccessTools.Method(stat, "CopyFrom", new[] { stat }), nameof(BeforeHealthStat)),
                (AccessTools.Method(stat, "Adjust", new[] { typeof(float) }), nameof(BeforeHealthStat)),
                (AccessTools.Method(stat, "Update", new[] { typeof(Vector3), typeof(float) }), nameof(BeforeHealthStat)),
                (AccessTools.Method(stat, "SetCurrentValue", new[] { typeof(float) }), nameof(BeforeHealthStat)),
                (AccessTools.Method(stat, "SetFadeRate", new[] { typeof(float) }), nameof(BeforeHealthStat)),
                (AccessTools.Method(stat, "Initialize", Type.EmptyTypes), nameof(BeforeHealthStat)),
                (AccessTools.Method(stat, "Randomize", new[] { typeof(float) }), nameof(BeforeHealthStat)),
                (AccessTools.Method(typeof(HealthStat), "OnValidate", Type.EmptyTypes), nameof(BeforeHealthStat)),
                (AccessTools.Method(vitals, "SetFullHealth", Type.EmptyTypes), nameof(BeforeHealthVitals)),
                (AccessTools.Method(vitals, "SetHealth", new[] { typeof(float) }), nameof(BeforeHealthVitals)),
                (AccessTools.Method(vitals, "SetTargetHealth", new[] { typeof(float) }), nameof(BeforeHealthVitals)),
                (AccessTools.Method(vitals, "ApplyHealing", new[] { typeof(float) }), nameof(BeforeHealthVitals)),
                (AccessTools.Method(vitals, "ApplyDamage", new[] { typeof(float) }), nameof(BeforeHealthVitals)),
                (AccessTools.Method(vitals, "TriggerDeath", Type.EmptyTypes), nameof(BeforeHealthVitals)),
                (AccessTools.Method(vitals, "TriggerKnockedOut", Type.EmptyTypes), nameof(BeforeHealthVitals)),
                (AccessTools.Method(vitals, "SetSickness", new[] { typeof(float) }), nameof(BeforeSickness)),
                (AccessTools.Method(vitals, "Update", Type.EmptyTypes), nameof(BeforeVitalsUpdate)),
                (AccessTools.Method(vitals, "InitializeStats", Type.EmptyTypes), nameof(BeforeVitalsInitialization)),
                (AccessTools.Method(vitals, "LoadVitalsFromPlayerData", new[] { typeof(global::Sons.Player.PlayerState) }), nameof(BeforeVitalsLoad)),
                (AccessTools.Method(vitals, "GainStrength", new[] { typeof(float) }), nameof(BeforeStrengthGain)),
                (AccessTools.Method(typeof(ItemUtils), "ApplyEffectsToPlayerStats",
                    new[] { typeof(StatEffect), typeof(bool) }), nameof(BeforeHealthEffect))
            };
        }

        private static StrictCombatLifeBarrier FindHealthOwner(Stat stat)
        {
            if (stat is null) return null;
            foreach (var owner in Volatile.Read(ref _owners))
                if (stat.Pointer == owner.HealthPointer) return owner;
            return null;
        }

        private static StrictCombatLifeBarrier FindVitalsOwner(Vitals vitals)
        {
            if (vitals is null) return null;
            foreach (var owner in Volatile.Read(ref _owners))
                if (vitals.Pointer == owner.Vitals.Pointer) return owner;
            return null;
        }

        private static bool BeforeHealthStat(Stat __instance, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            try { return FindHealthOwner(__instance) == null; }
            catch { return AllowUnreadableOwner(); }
        }

        private static bool BeforeHealthVitals(Vitals __instance, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            try { return FindVitalsOwner(__instance) == null; }
            catch { return AllowUnreadableOwner(); }
        }

        private static bool BeforeVitalsUpdate(Vitals __instance, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            StrictCombatLifeBarrier owner = null;
            try
            {
                owner = FindVitalsOwner(__instance);
                if (owner == null) return true;
                if (!OnOwnerThread) { owner.Retire(); return false; }
                if (owner.UpdatingVitals) return false;
                owner.UpdatingVitals = true;
                try { UpdateVitalsWithoutHealth(owner); }
                finally { owner.UpdatingVitals = false; }
                return false;
            }
            catch
            {
                owner?.Retire();
                return AllowUnreadableOwner();
            }
        }

        private static bool BeforeSickness(Vitals __instance, float __0, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            StrictCombatLifeBarrier owner = null;
            try
            {
                owner = FindVitalsOwner(__instance);
                if (owner == null) return true;
                if (!OnOwnerThread) { owner.Retire(); return false; }
                int frame = Time.frameCount;
                if (!CanUpdateVitals(owner, frame)) return false;
                // Native SetSickness also clamps target health. Keep only its sickness write.
                SetStatCurrent(owner.Sickness, __0);
                CanUpdateVitals(owner, frame);
                return false;
            }
            catch
            {
                owner?.Retire();
                return AllowUnreadableOwner();
            }
        }

        private static bool BeforeVitalsInitialization(Vitals __instance, bool __runOriginal,
            out VitalsStartupCall __state)
        {
            __state = default;
            if (!__runOriginal) return true;
            __state = BeginVitalsStartup(__instance);
            StrictCombatLifeBarrier owner = null;
            try
            {
                owner = FindVitalsOwner(__instance);
                if (owner == null) return true;
                if (!OnOwnerThread || owner.InitializingVitals) { owner.Retire(); return false; }
                owner.InitializingVitals = true;
                try { InitializeVitalsWithoutHealth(owner); }
                finally { owner.InitializingVitals = false; }
                return false;
            }
            catch
            {
                owner?.Retire();
                return AllowUnreadableOwner();
            }
        }

        private static bool BeforeVitalsLoad(Vitals __instance, global::Sons.Player.PlayerState __0,
            bool __runOriginal, out VitalsStartupCall __state)
        {
            __state = default;
            if (!__runOriginal) return true;
            __state = BeginVitalsStartup(__instance);
            StrictCombatLifeBarrier owner = null;
            try
            {
                owner = FindVitalsOwner(__instance);
                if (owner == null) return true;
                if (!OnOwnerThread || owner.LoadingVitals) { owner.Retire(); return false; }
                owner.LoadingVitals = true;
                try { LoadVitalsWithoutHealth(owner, __0); }
                finally { owner.LoadingVitals = false; }
                return false;
            }
            catch
            {
                owner?.Retire();
                return AllowUnreadableOwner();
            }
        }

        private static bool BeforeStrengthGain(Vitals __instance, float __0, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            if (PlayerInventoryEnforcement.TryCaptureHeldOnlyStrength(__instance, __0)) return false;
            StrictCombatLifeBarrier owner = null;
            try
            {
                owner = FindVitalsOwner(__instance);
                if (owner == null) return true;
                if (!OnOwnerThread) { owner.Retire(); return false; }
                GainStrengthWithoutHealth(owner, __0);
                return false;
            }
            catch
            {
                owner?.Retire();
                return AllowUnreadableOwner();
            }
        }

        private static bool BeforeHealthEffect(StatEffect __0, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || Volatile.Read(ref _owners).Length == 0) return true;
            try
            {
                if (__0 == null || __0._type != StatEffect.Types.Health) return true;
                if (!OnOwnerThread)
                {
                    __result = true;
                    return AllowUnreadableOwner();
                }
                var owner = FindVitalsOwner(LocalPlayer.Vitals);
                if (owner == null)
                {
                    // A replaced Vitals object does not restore permission for the same Stats.
                    var stats = LocalPlayer.Stats;
                    foreach (var previous in Volatile.Read(ref _owners))
                        if (Same(previous.Stats, stats)) { owner = previous; break; }
                }
                if (owner == null) return true;
                if (!owner.Owner.IsCurrentNativeHealthOwner(owner)) owner.Retire();
                // The native dispatcher returns true, and its enumerable caller continues.
                // This covers both application and expiry without discarding other effects.
                __result = true;
                return false;
            }
            catch
            {
                __result = true;
                return AllowUnreadableOwner();
            }
        }
    }
}
