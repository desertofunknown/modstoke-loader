// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using global::Sons.Environment;
using global::Sons.Settings;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static bool CanUpdateVitals(StrictCombatLifeBarrier owner, int frame)
        {
            if (OnOwnerThread && !owner.IsRetired && owner.Owner.IsCurrentNativeHealthOwner(owner)
                && Time.frameCount == frame) return true;
            owner.Retire();
            return false;
        }

        // Preserve the native update order while omitting its inlined health regeneration.
        // The stat-tick hook handles health inside UpdateStats; the effect hook handles cooking.
        private static void UpdateVitalsWithoutHealth(StrictCombatLifeBarrier owner)
        {
            int frame = Time.frameCount;
            if (!CanUpdateVitals(owner, frame)) return;
            var vitals = owner.Vitals;
            var manager = vitals._statsManager;
            var position = vitals.transform.position;
            float elapsed = (float)TimeOfDayHolder.GetTimeOfDayDelta();
            if (!CanUpdateVitals(owner, frame)) return;
            manager.UpdateStats(position, elapsed);
            if (!CanUpdateVitals(owner, frame)) return;

            float hydrationFactor = StatFactor(vitals._hydration);
            vitals._currentHydrationBuffFactor -= Mathf.Max(0f,
                vitals._lastCachedHydrationFactor - hydrationFactor);
            vitals._lastCachedHydrationFactor = hydrationFactor;
            float fullnessFactor = StatFactor(vitals._fullness);
            vitals._currentFullnessBuffFactor -= Mathf.Max(0f,
                vitals._lastCachedFullnessFactor - fullnessFactor);
            vitals._lastCachedFullnessFactor = fullnessFactor;
            float restedFactor = StatFactor(vitals._rested);
            vitals._currentRestedBuffFactor -= Mathf.Max(0f,
                vitals._lastCachedRestedFactor - restedFactor);
            vitals._lastCachedRestedFactor = restedFactor;
            vitals.UpdateCookingBuffs();
            if (!CanUpdateVitals(owner, frame)) return;

            bool infiniteEnergy = Cheats.Setup._InfiniteEnergy_k__BackingField
                || Cheats.Overrides._InfiniteEnergy_k__BackingField;
            if (!CanUpdateVitals(owner, frame)) return;
            if (infiniteEnergy)
            {
                vitals.SetRest(vitals._rested._max);
                if (!CanUpdateVitals(owner, frame)) return;
                SetStatCurrent(vitals._stamina, vitals._stamina._max);
            }

            bool godMode = Cheats.GodModeEnabled;
            if (!CanUpdateVitals(owner, frame)) return;
            if (godMode)
            {
                vitals.SetRest(vitals._rested._max);
                if (!CanUpdateVitals(owner, frame)) return;
                SetStatCurrent(vitals._stamina, vitals._stamina._max);
                vitals.SetVitality(vitals._vitality._max);
                if (!CanUpdateVitals(owner, frame)) return;
                // SetFullHealth's unrelated sickness clear precedes fullness and hydration.
                SetStatCurrent(owner.Sickness, 0f);
                SetStatCurrent(vitals._fullness, vitals._fullness._max);
                SetStatCurrent(vitals._hydration, vitals._hydration._max);
                SetStatCurrent(owner.Sickness, 0f);
            }
            CanUpdateVitals(owner, frame);
        }

        // These are the native inlined operations, including their unguarded factor division
        // and NaN default behavior. Do not add clamps that change ordinary survival stats.
        private static float StatFactor(Stat stat)
            => (stat._currentValue - stat._min) / (stat._max - stat._min);

        private static void SetStatCurrent(Stat stat, float value)
        {
            if (float.IsNaN(value)) value = stat._defaultValue;
            else if (value < stat._min) value = stat._min;
            else if (value > stat._max) value = stat._max;
            stat._currentValue = value;
        }
    }
}
