// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using global::Sons.Items.Core;
using TheForest.Items.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        // Preserve native load order without restoring health or invoking SetSickness's health write.
        private static void LoadVitalsWithoutHealth(StrictCombatLifeBarrier owner, global::Sons.Player.PlayerState state)
        {
            int frame = Time.frameCount;
            if (!CanUpdateVitals(owner, frame)) return;
            var vitals = owner.Vitals;
            var strength = vitals.Strength;
            if (!CanUpdateVitals(owner, frame)) return;
            float value = state.Get("Strength", strength._max);
            if (!CanUpdateVitals(owner, frame)) return;
            strength.SetCurrentValue(value);
            if (!CanUpdateVitals(owner, frame)) return;
            int strengthLevel = state.Get("StrengthLevel", 1);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._currentStrengthLevel = strengthLevel;
            value = state.Get("CurrentStrength", 0f);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._currentStrength = value;
            value = state.Get("RequiredCurrentStrengthPerLevel", vitals._requiredBaseStrengthPerLevel);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._requiredCurrentStrengthPerLevel = value;

            owner.Sickness.SetMax(owner.Health._max);
            if (!CanUpdateVitals(owner, frame)) return;
            value = state.Get("Sickness", owner.Sickness._min);
            if (!CanUpdateVitals(owner, frame)) return;
            SetStatCurrent(owner.Sickness, value);

            var hydration = vitals.Hydration;
            if (!CanUpdateVitals(owner, frame)) return;
            value = state.Get("Hydration", hydration._max);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals.SetHydration(value);
            if (!CanUpdateVitals(owner, frame)) return;
            var fullness = vitals.Fullness;
            if (!CanUpdateVitals(owner, frame)) return;
            value = state.Get("Fullness", fullness._max);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals.SetFullness(value);
            if (!CanUpdateVitals(owner, frame)) return;
            var rested = vitals.Rested;
            if (!CanUpdateVitals(owner, frame)) return;
            value = state.Get("Rest", rested._max);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals.SetRest(value);
            if (!CanUpdateVitals(owner, frame)) return;
            var stamina = vitals.Stamina;
            if (!CanUpdateVitals(owner, frame)) return;
            value = state.Get("Stamina", stamina._max);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals.SetStamina(value);
            if (!CanUpdateVitals(owner, frame)) return;

            value = state.Get("HydrationBuff", 0f);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._currentHydrationBuffFactor = value;
            value = state.Get("FullnessBuff", 0f);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._currentFullnessBuffFactor = value;
            value = state.Get("RestBuff", 0f);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._currentRestedBuffFactor = value;
            value = state.Get("CookingBuffTimeRemaining", 0f);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._remainingCookingBuffTime = value;
            int recipeId = state.Get("CookingBuffRecipe", -1);
            if (!CanUpdateVitals(owner, frame)) return;
            if (recipeId == -1) return;

            var recipe = vitals._cookingRecipeDatabase.FindRecipeById(recipeId);
            if (!CanUpdateVitals(owner, frame)) return;
            vitals._activeCookingBuffRecipe = recipe;
            var effects = recipe._statEffects;
            var linkedEffects = new Il2CppSystem.Collections.Generic.List<StatEffect>();
            if (!CanUpdateVitals(owner, frame)) return;
            int count = effects.Count;
            if (!CanUpdateVitals(owner, frame)) return;
            for (int index = 0; index < count; index++)
            {
                var effect = effects[index];
                if (!CanUpdateVitals(owner, frame)) return;
                if (effect._linkedToCookingBuff)
                {
                    linkedEffects.Add(effect);
                    if (!CanUpdateVitals(owner, frame)) return;
                }
            }
            int linkedCount = linkedEffects.Count;
            if (!CanUpdateVitals(owner, frame)) return;
            if (linkedCount > 0)
            {
                var linkedEnumerable = linkedEffects.Cast<Il2CppSystem.Collections.Generic.IEnumerable<StatEffect>>();
                if (!CanUpdateVitals(owner, frame)) return;
                ItemUtils.ApplyEffectsToPlayerStats(linkedEnumerable, true, 1);
                CanUpdateVitals(owner, frame);
            }
        }
    }
}
