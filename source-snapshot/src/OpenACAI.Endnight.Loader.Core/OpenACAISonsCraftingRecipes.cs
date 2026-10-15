// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Crafting;
using TheForest.Utils;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsCrafting
    {
        /// <summary>
        /// Reads every recipe declaring the supplied result item on the initialized
        /// Unity lifecycle thread. True with an empty list means no declared match;
        /// false means the read failed. This does not predict actual crafting output
        /// or availability, and itemId is not a recipe ID.
        /// </summary>
        public static bool TryFindRecipesForResult(int itemId,
            out IReadOnlyList<OpenACAISonsRecipeSnapshot> recipes, out string status)
        {
            recipes = Array.Empty<OpenACAISonsRecipeSnapshot>();
            status = "";
            if (itemId <= 0)
            {
                status = "A positive Sons result item ID is required.";
                return false;
            }
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = "Recipe reads require the initialized Unity lifecycle thread.";
                return false;
            }

            try
            {
                if (!LocalPlayer.IsInWorld)
                {
                    status = "An in-world local player is required to read crafting recipes.";
                    return false;
                }

                var system = CraftingSystem.Instance;
                if (system is null || system.WasCollected || !system)
                {
                    status = "The game's crafting system is not available.";
                    return false;
                }

                var database = system._recipeDatabase;
                if (database is null || database.WasCollected || !database)
                {
                    status = "The game's crafting recipe database is not available.";
                    return false;
                }

                var source = database._recipes;
                if (source == null)
                {
                    status = "The game's crafting recipe list is not available.";
                    return false;
                }

                var matches = new List<OpenACAISonsRecipeSnapshot>();
                var recipeCount = source.Count;
                for (var i = 0; i < recipeCount; i++)
                {
                    var recipe = source[i];
                    if (recipe is null || recipe.WasCollected || !recipe)
                        continue;

                    var declaredResults = recipe._resultingItems;
                    if (declaredResults == null)
                        continue;

                    var resultIds = new int[declaredResults.Count];
                    var matchesResult = false;
                    for (var j = 0; j < resultIds.Length; j++)
                    {
                        resultIds[j] = declaredResults[j].Id;
                        matchesResult |= resultIds[j] == itemId;
                    }
                    if (matchesResult)
                        matches.Add(ReadRecipe(recipe, resultIds));
                }

                recipes = matches.AsReadOnly();
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read crafting recipes: " + error.Message;
                return false;
            }
        }

        private static OpenACAISonsRecipeSnapshot ReadRecipe(CraftingRecipe recipe, int[] resultIds)
        {
            var source = recipe.Ingredients;
            if (source == null)
                throw new InvalidOperationException("A matching recipe's ingredient list is not available.");

            var ingredients = new OpenACAISonsRecipeIngredientSnapshot[source.Count];
            for (var i = 0; i < ingredients.Length; i++)
            {
                var ingredient = source[i];
                ingredients[i] = new OpenACAISonsRecipeIngredientSnapshot(
                    ingredient.ItemId, ingredient.Count, ingredient.IsReusable);
            }

            return new OpenACAISonsRecipeSnapshot(
                recipe.Name,
                recipe.RecipeType.ToString(),
                recipe.HasCustomName,
                recipe.LocalizationId,
                recipe.ForceNewItemInstance,
                recipe._useContainerDataForResultingItems,
                resultIds,
                ingredients);
        }
    }

    /// <summary>
    /// Selected metadata copied from a game recipe, not a persistent recipe
    /// identity or a prediction of generated results, consumption or availability.
    /// </summary>
    public sealed class OpenACAISonsRecipeSnapshot
    {
        internal OpenACAISonsRecipeSnapshot(string name, string recipeType,
            bool hasCustomName, string localizationId, bool forceNewItemInstance,
            bool usesContainerDataForResults, int[] declaredResultItemIds,
            OpenACAISonsRecipeIngredientSnapshot[] ingredients)
        {
            Name = name ?? "";
            RecipeType = recipeType ?? "";
            HasCustomName = hasCustomName;
            LocalizationId = localizationId ?? "";
            ForceNewItemInstance = forceNewItemInstance;
            UsesContainerDataForResults = usesContainerDataForResults;
            DeclaredResultItemIds = Array.AsReadOnly(declaredResultItemIds);
            Ingredients = Array.AsReadOnly(ingredients);
        }

        public string Name { get; }
        public string RecipeType { get; }
        public bool HasCustomName { get; }
        public string LocalizationId { get; }
        public bool ForceNewItemInstance { get; }
        public bool UsesContainerDataForResults { get; }
        public IReadOnlyList<int> DeclaredResultItemIds { get; }
        public IReadOnlyList<OpenACAISonsRecipeIngredientSnapshot> Ingredients { get; }
    }

    public sealed class OpenACAISonsRecipeIngredientSnapshot
    {
        internal OpenACAISonsRecipeIngredientSnapshot(int itemId, int count, bool isReusable)
        {
            ItemId = itemId;
            Count = count;
            IsReusable = isReusable;
        }

        public int ItemId { get; }
        public int Count { get; }
        public bool IsReusable { get; }
    }
}
