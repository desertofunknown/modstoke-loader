// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using global::Sons.Atmosphere;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static void InitializeVitalsWithoutHealth(StrictCombatLifeBarrier owner)
        {
            int frame = Time.frameCount;
            if (!CanUpdateVitals(owner, frame)) return;
            var vitals = owner.Vitals;
            var manager = vitals._statsManager;
            // Acquisition requires the health and sickness objects to be registered already.
            // If the list disappears, retire rather than recreate an empty manager around them.
            foreach (var stat in manager._stats)
            {
                if (!CanUpdateVitals(owner, frame)) return;
                if (stat == null) continue;
                stat.OnValidate();
                if (!CanUpdateVitals(owner, frame)) return;
                var temperature = stat.TryCast<TemperatureStat>();
                if (temperature == null) continue;
                var temperatureManager = TemperatureManager.Instance;
                if (!CanUpdateVitals(owner, frame)) return;
                temperature.SetAmbientSourceMethod(
                    (Il2CppSystem.Func<Vector3, bool, float>)temperatureManager.GetAmbientTemperature);
                if (!CanUpdateVitals(owner, frame)) return;
            }
            manager._overrides = vitals._playerAwakeStatOverrides;
            if (!CanUpdateVitals(owner, frame)) return;
            // Strength may change independently; it no longer sets health maximum here.
            owner.Sickness.SetMax(owner.Health._max);
            CanUpdateVitals(owner, frame);
        }

        private static void GainStrengthWithoutHealth(StrictCombatLifeBarrier owner, float gain)
        {
            int frame = Time.frameCount;
            if (!CanUpdateVitals(owner, frame)) return;
            var vitals = owner.Vitals;
            float modifier = vitals._strengthBoostModifier;
            if (!Mathf.Approximately(modifier, 1f)) gain *= Mathf.Max(0f, modifier);
            if (!CanUpdateVitals(owner, frame)) return;
            if (vitals._currentStrengthLevel == vitals._maxStrengthLevel || gain < 0f) return;
            if (vitals._requiredCurrentStrengthPerLevel < Mathf.Epsilon)
                vitals._requiredCurrentStrengthPerLevel = vitals._requiredBaseStrengthPerLevel;

            float requirement = vitals._requiredCurrentStrengthPerLevel;
            if (gain > requirement)
            {
                var worker = vitals.AdjustStrengthWorker(gain, 1f);
                if (!CanUpdateVitals(owner, frame)) return;
                vitals.StartCoroutine(worker);
                CanUpdateVitals(owner, frame);
                return;
            }

            // Native fmodf uses the old requirement; the display below uses the new one.
            float total = gain + vitals._currentStrength;
            vitals._currentStrength = total % requirement;
            if (total >= requirement)
            {
                vitals._currentStrengthLevel++;
                float multiplier = vitals._requiredStrenghtMultiplierCurve.Evaluate(
                    (float)vitals._currentStrengthLevel / vitals._maxStrengthLevel);
                if (!CanUpdateVitals(owner, frame)) return;
                vitals._requiredCurrentStrengthPerLevel = multiplier * vitals._requiredBaseStrengthPerLevel;
            }

            // Keep the sickness bound and strength display, omitting native current, target
            // and maximum health writes on both the ordinary-gain and level-up paths.
            owner.Sickness.SetMax(owner.Health._max);
            if (!CanUpdateVitals(owner, frame)) return;
            var strength = vitals.Strength;
            if (!CanUpdateVitals(owner, frame)) return;
            strength.SetCurrentValue(
                (vitals._currentStrength / vitals._requiredCurrentStrengthPerLevel) * strength._max);
            CanUpdateVitals(owner, frame);
        }
    }
}
