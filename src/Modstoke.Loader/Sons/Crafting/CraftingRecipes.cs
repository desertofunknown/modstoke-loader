// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        /// <summary>
        /// Reads every recipe declaring the supplied result item on the initialized
        /// Unity lifecycle thread. True with an empty list means no declared match;
        /// false means the read failed. This does not predict actual crafting output
        /// or availability, and itemId is not a recipe ID.
        /// </summary>
        public static bool TryFindRecipesForResult(int itemId,
            out IReadOnlyList<RecipeSnapshot> recipes, out string status)
        {
            recipes = Array.Empty<RecipeSnapshot>();
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
                var localPlayer = LocalPlayer._instance;
                if (localPlayer is null || localPlayer.WasCollected || !localPlayer)
                {
                    status = "A live local player is required to read crafting recipes.";
                    return false;
                }

                var inventory = LocalPlayer.Inventory;
                if (inventory is null || inventory.WasCollected || !inventory)
                {
                    status = "The local player's inventory is not available.";
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

                var matches = new List<RecipeSnapshot>();
                var recipeCount = source.Count;
                for (var i = 0; i < recipeCount; i++)
                {
                    var recipe = source[i];
                    if (recipe is null || recipe.WasCollected || !recipe)
                        continue;

                    var declaredResults = recipe._resultingItems;
                    if (declaredResults == null)
                        continue;

                    var resultCount = declaredResults.Count;
                    var matchesResult = false;
                    for (var j = 0; j < resultCount; j++)
                    {
                        if (declaredResults[j].Id != itemId)
                            continue;
                        matchesResult = true;
                        break;
                    }
                    if (matchesResult)
                    {
                        var resultIds = new int[resultCount];
                        for (var j = 0; j < resultIds.Length; j++)
                            resultIds[j] = declaredResults[j].Id;
                        matches.Add(ReadRecipe(recipe, resultIds));
                    }
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

        /// <summary>
        /// Compares a selected recipe snapshot's declared ingredients with current
        /// local inventory counts on the initialized Unity lifecycle thread.
        /// craftQuantity counts recipe executions, not output items. Consumable
        /// counts scale by that quantity; reusable counts are required only once.
        /// Duplicate item IDs are uncertain and fail the read rather than being
        /// summed or treated as alternatives. False leaves requirements null.
        /// The supplied recipe is not refreshed or resolved to a live recipe.
        /// Results do not account for outputs replenishing ingredients, item
        /// instance/container constraints, unlocks, output capacity or execution.
        /// Inventory options are forwarded unchanged to TryGetItemCount.
        /// </summary>
        public static bool TryGetRecipeRequirements(RecipeSnapshot recipe,
            int craftQuantity, out RecipeRequirementsSnapshot requirements,
            out string status, bool includeEquipped = true, bool checkNextSlot = false)
        {
            requirements = null;
            status = "";
            if (recipe == null)
            {
                status = "A recipe snapshot is required.";
                return false;
            }
            if (craftQuantity <= 0)
            {
                status = "A positive number of recipe executions is required.";
                return false;
            }
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = "Recipe requirement reads require the initialized Unity lifecycle thread.";
                return false;
            }

            var itemIds = new HashSet<int>();
            foreach (var ingredient in recipe.Ingredients)
            {
                if (ingredient == null || ingredient.ItemId <= 0 || ingredient.Count <= 0)
                {
                    status = "Recipe requirements are uncertain: every ingredient must declare a positive item ID and count.";
                    return false;
                }
                if (!itemIds.Add(ingredient.ItemId))
                {
                    status = "Recipe requirements are uncertain: item " + ingredient.ItemId
                        + " appears in multiple ingredient rows; duplicate semantics are not known.";
                    return false;
                }
            }

            try
            {
                var localPlayer = LocalPlayer._instance;
                if (localPlayer is null || localPlayer.WasCollected || !localPlayer)
                {
                    status = "A live local player is required to read recipe requirements.";
                    return false;
                }
                var inventory = LocalPlayer.Inventory;
                if (inventory is null || inventory.WasCollected || !inventory)
                {
                    status = "The local player's inventory is not available.";
                    return false;
                }

                var rows = new RecipeIngredientRequirementSnapshot[recipe.Ingredients.Count];
                var hasRequiredItemCounts = true;
                for (var i = 0; i < rows.Length; i++)
                {
                    var ingredient = recipe.Ingredients[i];
                    if (!Inventory.TryGetItemCount(ingredient.ItemId,
                        out var inventoryCount, out status, includeEquipped, checkNextSlot))
                    {
                        status = "Could not read ingredient " + ingredient.ItemId + ": " + status;
                        return false;
                    }
                    if (inventoryCount < 0)
                    {
                        status = "Recipe requirements are uncertain: the inventory returned a negative count for item "
                            + ingredient.ItemId + ".";
                        return false;
                    }

                    var requiredCount = ingredient.IsReusable
                        ? (long)ingredient.Count
                        : (long)ingredient.Count * craftQuantity;
                    var row = new RecipeIngredientRequirementSnapshot(
                        ingredient, requiredCount, inventoryCount);
                    rows[i] = row;
                    hasRequiredItemCounts &= row.ShortageCount == 0;
                }

                var currentPlayer = LocalPlayer._instance;
                var currentInventory = LocalPlayer.Inventory;
                if (localPlayer.WasCollected || !localPlayer || inventory.WasCollected || !inventory
                    || currentPlayer is null || currentPlayer.WasCollected || !currentPlayer
                    || currentInventory is null || currentInventory.WasCollected || !currentInventory
                    || currentPlayer.Pointer != localPlayer.Pointer || currentInventory.Pointer != inventory.Pointer)
                {
                    status = "The local player or inventory changed while reading recipe requirements.";
                    return false;
                }

                requirements = new RecipeRequirementsSnapshot(recipe, craftQuantity,
                    includeEquipped, checkNextSlot, rows, hasRequiredItemCounts);
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read recipe requirements: " + error.Message;
                return false;
            }
        }

        private static RecipeSnapshot ReadRecipe(CraftingRecipe recipe, int[] resultIds)
        {
            var source = recipe.Ingredients;
            if (source == null)
                throw new InvalidOperationException("A matching recipe's ingredient list is not available.");

            var ingredients = new RecipeIngredientSnapshot[source.Count];
            for (var i = 0; i < ingredients.Length; i++)
            {
                var ingredient = source[i];
                ingredients[i] = new RecipeIngredientSnapshot(
                    ingredient.ItemId, ingredient.Count, ingredient.IsReusable);
            }

            return ReadRecipe(recipe, resultIds, ingredients);
        }

        private static RecipeSnapshot ReadRecipe(CraftingRecipe recipe, int[] resultIds,
            RecipeIngredientSnapshot[] ingredients)
        {
            return new RecipeSnapshot(
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
    public sealed class RecipeSnapshot
    {
        internal RecipeSnapshot(string name, string recipeType,
            bool hasCustomName, string localizationId, bool forceNewItemInstance,
            bool usesContainerDataForResults, int[] declaredResultItemIds,
            RecipeIngredientSnapshot[] ingredients)
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
        public IReadOnlyList<RecipeIngredientSnapshot> Ingredients { get; }
    }

    public sealed class RecipeIngredientSnapshot
    {
        internal RecipeIngredientSnapshot(int itemId, int count, bool isReusable)
        {
            ItemId = itemId;
            Count = count;
            IsReusable = isReusable;
        }

        public int ItemId { get; }
        public int Count { get; }
        public bool IsReusable { get; }
    }

    /// <summary>
    /// Managed comparison of supplied recipe metadata and current inventory item
    /// counts. This neither reserves ingredients nor establishes craftability.
    /// </summary>
    public sealed class RecipeRequirementsSnapshot
    {
        internal RecipeRequirementsSnapshot(RecipeSnapshot recipe,
            int craftQuantity, bool includeEquipped, bool checkNextSlot,
            RecipeIngredientRequirementSnapshot[] ingredients, bool hasRequiredItemCounts)
        {
            Recipe = recipe;
            CraftQuantity = craftQuantity;
            IncludeEquipped = includeEquipped;
            CheckNextSlot = checkNextSlot;
            Ingredients = Array.AsReadOnly(ingredients);
            HasRequiredItemCounts = hasRequiredItemCounts;
        }

        public RecipeSnapshot Recipe { get; }
        /// <summary>Number of recipe executions requested, not the number of resulting items.</summary>
        public int CraftQuantity { get; }
        public bool IncludeEquipped { get; }
        public bool CheckNextSlot { get; }
        public IReadOnlyList<RecipeIngredientRequirementSnapshot> Ingredients { get; }
        /// <summary>True only means every declared ingredient count is covered by the observed inventory counts.</summary>
        public bool HasRequiredItemCounts { get; }
    }

    public sealed class RecipeIngredientRequirementSnapshot
    {
        internal RecipeIngredientRequirementSnapshot(RecipeIngredientSnapshot ingredient,
            long requiredCount, int inventoryCount)
        {
            ItemId = ingredient.ItemId;
            CountPerCraft = ingredient.Count;
            IsReusable = ingredient.IsReusable;
            RequiredCount = requiredCount;
            InventoryCount = inventoryCount;
            ShortageCount = Math.Max(0L, requiredCount - inventoryCount);
        }

        public int ItemId { get; }
        public int CountPerCraft { get; }
        public bool IsReusable { get; }
        /// <summary>Declared starting stock for the requested quantity; reusable rows are not multiplied.</summary>
        public long RequiredCount { get; }
        /// <summary>The game's AmountOf result with the query's inventory options.</summary>
        public int InventoryCount { get; }
        public long ShortageCount { get; }
    }
}
