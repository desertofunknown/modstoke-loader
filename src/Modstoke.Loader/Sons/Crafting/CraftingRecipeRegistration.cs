// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;
using global::Sons.Crafting;
using UnityEngine;
using NativeIngredient = global::Sons.Crafting.CraftingIngredient;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>A crafting mat recipe that combines ingredients into new items.</summary>
    public sealed class CraftRecipeSpec
    {
        public CraftRecipeSpec(string modId, string key, IReadOnlyList<(int ItemId, int Count)> ingredients, int resultItemId, int resultCount = 1)
        {
            ModId = modId;
            Key = key;
            Ingredients = ingredients ?? Array.Empty<(int, int)>();
            ResultItemId = resultItemId;
            ResultCount = resultCount;
        }

        public string ModId { get; }
        public string Key { get; }
        public IReadOnlyList<(int ItemId, int Count)> Ingredients { get; }
        public int ResultItemId { get; }
        public int ResultCount { get; }

        /// <summary>An item whose own crafting recipe lends this one its craft animation and sounds; 0 takes the game's
        /// first animated recipe.</summary>
        public int AnimationFromItemId { get; init; }
    }

    /// <summary>
    /// Adds crafting mat recipes. Strict inventory checks a craft against the server's recipe list by position and
    /// content, so every recipe database in every process gets the same additions in the same order, after the
    /// game's own: weapon attachment recipes first, then these sorted by mod id and key. Register at mod start in
    /// every process, clients and servers alike; recipes go in once a world is loading.
    /// </summary>
    public static class CraftingRecipeRegistration
    {
        private const int MaximumRecipes = 128, MaximumRows = 8;
        private static readonly List<Registration> Registrations = new();
        private static float _nextCheck;
        private static bool _loggedFailure;

        private sealed class Registration
        {
            internal CraftRecipeSpec Spec;
            internal CraftingRecipe Recipe;
        }

        public static bool TryRegister(CraftRecipeSpec spec, out string problem)
        {
            problem = Validate(spec);
            if (problem != null) return false;
            lock (Registrations)
            {
                if (Registrations.Count >= MaximumRecipes) { problem = "The recipe limit is reached."; return false; }
                if (Registrations.Any(existing => existing.Spec.ModId == spec.ModId && existing.Spec.Key == spec.Key))
                {
                    problem = "A recipe with that key is already registered.";
                    return false;
                }
                Registrations.Add(new Registration { Spec = spec });
                Registrations.Sort((a, b) =>
                {
                    int order = string.CompareOrdinal(a.Spec.ModId, b.Spec.ModId);
                    return order != 0 ? order : string.CompareOrdinal(a.Spec.Key, b.Spec.Key);
                });
            }
            return true;
        }

        private static string Validate(CraftRecipeSpec spec)
        {
            if (spec == null || string.IsNullOrWhiteSpace(spec.ModId) || string.IsNullOrWhiteSpace(spec.Key))
                return "A recipe needs a mod id and a key.";
            if (!ItemIds.TryGetOwner(spec.ResultItemId, out var owner) || owner.ModId != spec.ModId)
                return "The result item " + spec.ResultItemId + " is not reserved by " + spec.ModId + ".";
            if (spec.ResultCount is < 1 or > 4) return "A recipe makes 1 to 4 of its item.";
            if (spec.Ingredients.Count is < 1 or > MaximumRows) return "A recipe takes 1 to " + MaximumRows + " kinds of ingredient.";
            var seen = new HashSet<int>();
            foreach (var (itemId, count) in spec.Ingredients)
            {
                if (itemId <= 0 || count is < 1 or > 99 || !seen.Add(itemId)) return "Each ingredient needs its own item id and a count of 1 to 99.";
                if (itemId == spec.ResultItemId) return "A recipe cannot use up its own result.";
            }
            return null;
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            if (!GameRuntime.IsGameScene) return;
            try
            {
                Registration[] registrations;
                lock (Registrations) registrations = Registrations.ToArray();
                int attachments = 0, recipes = 0;
                foreach (var database in Resources.FindObjectsOfTypeAll<CraftingRecipeDatabase>())
                {
                    var list = database == null ? null : database._recipes;
                    if (list == null || list.WasCollected || !IsMatDatabase(list)) continue;
                    attachments += WeaponAttachments.AppendRecipes(list);
                    recipes += Append(list, registrations);
                }
                if (attachments + recipes == 0) return;
                var system = CraftingSystem._Instance_k__BackingField;
                if (system != null && !system.WasCollected && system._isInitialized) system.RefreshAvailableRecipes(false);
                RuntimeEntryPoint.LogSource?.LogInfo("Crafting mat: " + attachments + " weapon attachment and " + recipes
                    + " mod recipe(s) added.");
            }
            catch (Exception error)
            {
                if (_loggedFailure) return;
                _loggedFailure = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Mod recipes could not be added to the crafting mat: " + error.Message);
            }
        }

        // The crafting mat's database is the one with the game's weapon dismantling; structure recipes live elsewhere.
        private static bool IsMatDatabase(Il2CppSystem.Collections.Generic.List<CraftingRecipe> list)
        {
            foreach (var recipe in list)
                if (recipe != null && !recipe.WasCollected && recipe._recipeType == CraftingRecipe.Type.DismantleWeaponMods) return true;
            return false;
        }

        private static int Append(Il2CppSystem.Collections.Generic.List<CraftingRecipe> list, Registration[] registrations)
        {
            int added = 0;
            foreach (var registration in registrations)
            {
                if (registration.Recipe != null && !registration.Recipe.WasCollected && list.Contains(registration.Recipe)) continue;
                var recipe = registration.Recipe != null && !registration.Recipe.WasCollected ? registration.Recipe : Build(list, registration.Spec);
                if (recipe == null) continue;
                registration.Recipe = recipe;
                list.Add(recipe);
                added++;
            }
            return added;
        }

        // A copy of one of the game's craft-new-item recipes, for its animation, sounds and settings.
        private static CraftingRecipe Build(Il2CppSystem.Collections.Generic.List<CraftingRecipe> list, CraftRecipeSpec spec)
        {
            CraftingRecipe template = null, fallback = null;
            foreach (var candidate in list)
            {
                if (candidate == null || candidate.WasCollected || candidate._recipeType != CraftingRecipe.Type.CraftNewItem
                    || candidate._useContainerDataForResultingItems || string.IsNullOrEmpty(candidate._animationStateName)) continue;
                fallback ??= candidate;
                if (spec.AnimationFromItemId != 0 && candidate._resultingItems != null)
                    foreach (var result in candidate._resultingItems)
                        if (result != null && result.Id == spec.AnimationFromItemId) { template = candidate; break; }
                if (template != null) break;
            }
            template ??= fallback;
            if (template == null) return null;
            var recipe = Object.Instantiate(template);
            recipe.name = spec.ModId + ":" + spec.Key;
            recipe.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            recipe._recipeType = CraftingRecipe.Type.CraftNewItem;
            recipe._hasCustomName = false;
            recipe._useContainerDataForResultingItems = false;
            recipe._containerItemData = null;
            recipe._forceNewItemInstance = false;
            recipe._weaponMod = null;
            recipe._ingredients = new();
            foreach (var (itemId, count) in spec.Ingredients)
                recipe._ingredients.Add(new NativeIngredient { ItemId = itemId, Count = count, IsReusable = false });
            recipe._resultingItems = new();
            for (int i = 0; i < spec.ResultCount; i++)
                recipe._resultingItems.Add(new CraftingRecipe.ResultingItem { Id = spec.ResultItemId });
            recipe._generatedResultingItems = new();
            return recipe;
        }
    }
}
