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
        /// <summary>Maximum native recipe slots inspected by one recipe query.</summary>
        public const int MaximumRecipeQueryEntries = 256;

        /// <summary>Maximum rows accepted in either ingredient or declared-result list.</summary>
        public const int MaximumRecipeQueryRows = 128;

        /// <summary>
        /// Finds recipes declaring an ingredient ID in a bounded page of the loaded recipe list.
        /// Requires the initialized Unity lifecycle thread and live local player/inventory.
        /// Only matching recipes have their complete metadata copied; no catalog is rebuilt.
        /// True may return incomplete coverage: inspect HasMoreRecipes, UnavailableRecipeCount
        /// and RowLimitRecipeCount. False leaves query null. maximumRecipes is 1..256.
        /// NextRecipeIndex resumes at the next native list slot, not the next match. List order
        /// and metadata may change between calls; pages are not a persistent catalog snapshot.
        /// This does not establish feasibility, consumption, unlocks or output quantities.
        /// </summary>
        public static bool TryQueryRecipesForIngredient(int itemId,
            out RecipeQuerySnapshot query, out string status,
            int startRecipeIndex = 0, int maximumRecipes = 128)
            => TryQueryRecipes(itemId, RecipeItemRole.Ingredient,
                startRecipeIndex, maximumRecipes, out query, out status);

        /// <summary>
        /// Finds recipes declaring a result ID using the same bounded paging and read guards
        /// as TryQueryRecipesForIngredient. Only _resultingItems declarations are matched;
        /// generated, filtered or container-dependent outputs are not evaluated.
        /// </summary>
        public static bool TryQueryRecipesForResult(int itemId,
            out RecipeQuerySnapshot query, out string status,
            int startRecipeIndex = 0, int maximumRecipes = 128)
            => TryQueryRecipes(itemId, RecipeItemRole.DeclaredResult,
                startRecipeIndex, maximumRecipes, out query, out status);

        private static bool TryQueryRecipes(int itemId, RecipeItemRole role,
            int startRecipeIndex, int maximumRecipes, out RecipeQuerySnapshot query,
            out string status)
        {
            query = null;
            status = "";
            if (itemId <= 0 || startRecipeIndex < 0
                || maximumRecipes < 1 || maximumRecipes > MaximumRecipeQueryEntries)
            {
                status = "A positive item ID, nonnegative recipe index and recipe limit between 1 and "
                    + MaximumRecipeQueryEntries + " are required.";
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
                var system = CraftingSystem._Instance_k__BackingField;
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
                if (source is null || source.WasCollected)
                {
                    status = "The game's crafting recipe list is not available.";
                    return false;
                }
                var total = source.Count;
                var version = source._version;
                if (total < 0 || startRecipeIndex > total)
                {
                    status = "The recipe index is outside the current recipe list.";
                    return false;
                }

                var nextIndex = startRecipeIndex + Math.Min(maximumRecipes, total - startRecipeIndex);
                var matches = new List<RecipeSnapshot>();
                var unavailable = 0;
                var rowLimited = 0;
                for (var index = startRecipeIndex; index < nextIndex; index++)
                {
                    var recipe = source[index];
                    if (recipe is null || recipe.WasCollected || !recipe)
                    {
                        unavailable++;
                        continue;
                    }
                    var outcome = ReadQueryRecipe(recipe, itemId, role, out var match);
                    if (outcome == RecipeQueryReadOutcome.Matched)
                        matches.Add(match);
                    else if (outcome == RecipeQueryReadOutcome.Unavailable)
                        unavailable++;
                    else if (outcome == RecipeQueryReadOutcome.RowLimit)
                        rowLimited++;
                }

                var currentPlayer = LocalPlayer._instance;
                var currentInventory = LocalPlayer.Inventory;
                var currentSystem = CraftingSystem._Instance_k__BackingField;
                if (localPlayer.WasCollected || !localPlayer || inventory.WasCollected || !inventory
                    || system.WasCollected || !system || database.WasCollected || !database
                    || currentPlayer is null || currentPlayer.WasCollected || !currentPlayer
                    || currentInventory is null || currentInventory.WasCollected || !currentInventory
                    || currentSystem is null || currentSystem.WasCollected || !currentSystem
                    || currentPlayer.Pointer != localPlayer.Pointer || currentInventory.Pointer != inventory.Pointer
                    || currentSystem.Pointer != system.Pointer)
                {
                    status = "The crafting read context changed while reading recipes.";
                    return false;
                }
                var currentDatabase = currentSystem._recipeDatabase;
                if (currentDatabase is null || currentDatabase.WasCollected || !currentDatabase
                    || currentDatabase.Pointer != database.Pointer)
                {
                    status = "The crafting recipe database changed while reading recipes.";
                    return false;
                }
                var currentSource = currentDatabase._recipes;
                if (source.WasCollected || currentSource is null || currentSource.WasCollected
                    || currentSource.Pointer != source.Pointer || source.Count != total || source._version != version)
                {
                    status = "The crafting recipe list changed while reading recipes.";
                    return false;
                }

                query = new RecipeQuerySnapshot(itemId, role, startRecipeIndex, nextIndex,
                    total, unavailable, rowLimited, matches.ToArray());
                if (query.HasUnknownRecipes)
                    status = "Some recipe entries were unavailable or exceeded the row limit; their matches or metadata remain unknown.";
                else if (query.HasMoreRecipes)
                    status = "More recipe entries remain; continue from NextRecipeIndex for another bounded page.";
                return true;
            }
            catch (Exception error)
            {
                query = null;
                status = "Could not query crafting recipes: " + error.Message;
                return false;
            }
        }

        private static RecipeQueryReadOutcome ReadQueryRecipe(CraftingRecipe recipe, int itemId,
            RecipeItemRole role, out RecipeSnapshot match)
        {
            match = null;
            var ingredients = recipe._ingredients;
            var results = recipe._resultingItems;
            var matches = false;
            if (role == RecipeItemRole.Ingredient)
            {
                if (ingredients is null || ingredients.WasCollected)
                    return RecipeQueryReadOutcome.Unavailable;
                var count = ingredients.Count;
                var version = ingredients._version;
                if (count < 0)
                    return RecipeQueryReadOutcome.Unavailable;
                if (count > MaximumRecipeQueryRows)
                    return RecipeQueryReadOutcome.RowLimit;
                for (var index = 0; index < count; index++)
                {
                    var ingredient = ingredients[index];
                    if (ingredient is null || ingredient.WasCollected)
                        return RecipeQueryReadOutcome.Unavailable;
                    if (ingredient.ItemId == itemId)
                    {
                        matches = true;
                        break;
                    }
                }
                if (ingredients._version != version || ingredients.Count != count)
                    throw new InvalidOperationException("A recipe's ingredients changed during the query.");
            }
            else
            {
                if (results is null || results.WasCollected)
                    return RecipeQueryReadOutcome.Unavailable;
                var count = results.Count;
                var version = results._version;
                if (count < 0)
                    return RecipeQueryReadOutcome.Unavailable;
                if (count > MaximumRecipeQueryRows)
                    return RecipeQueryReadOutcome.RowLimit;
                for (var index = 0; index < count; index++)
                {
                    var result = results[index];
                    if (result is null || result.WasCollected)
                        return RecipeQueryReadOutcome.Unavailable;
                    if (result.Id == itemId)
                    {
                        matches = true;
                        break;
                    }
                }
                if (results._version != version || results.Count != count)
                    throw new InvalidOperationException("A recipe's declared results changed during the query.");
            }
            if (!matches)
                return RecipeQueryReadOutcome.NoMatch;
            if (ingredients is null || ingredients.WasCollected || results is null || results.WasCollected)
                return RecipeQueryReadOutcome.Unavailable;
            var ingredientCount = ingredients.Count;
            var resultCount = results.Count;
            var ingredientVersion = ingredients._version;
            var resultVersion = results._version;
            if (ingredientCount < 0 || resultCount < 0)
                return RecipeQueryReadOutcome.Unavailable;
            if (ingredientCount > MaximumRecipeQueryRows || resultCount > MaximumRecipeQueryRows)
                return RecipeQueryReadOutcome.RowLimit;

            // Never pass truncated ingredients to the requirements API as a complete recipe.
            var copiedIngredients = new RecipeIngredientSnapshot[ingredientCount];
            var copiedResults = new int[resultCount];
            for (var index = 0; index < ingredientCount; index++)
            {
                var ingredient = ingredients[index];
                if (ingredient is null || ingredient.WasCollected)
                    return RecipeQueryReadOutcome.Unavailable;
                copiedIngredients[index] = new RecipeIngredientSnapshot(
                    ingredient.ItemId, ingredient.Count, ingredient.IsReusable);
            }
            for (var index = 0; index < resultCount; index++)
            {
                var result = results[index];
                if (result is null || result.WasCollected)
                    return RecipeQueryReadOutcome.Unavailable;
                copiedResults[index] = result.Id;
            }
            var copiedRecipe = ReadRecipe(recipe, copiedResults, copiedIngredients);
            if (recipe.WasCollected || !recipe || ingredients.WasCollected || results.WasCollected)
                throw new InvalidOperationException("A matching recipe became unavailable during the query.");
            var currentIngredients = recipe._ingredients;
            var currentResults = recipe._resultingItems;
            if (currentIngredients is null || currentIngredients.WasCollected
                || currentResults is null || currentResults.WasCollected
                || currentIngredients.Pointer != ingredients.Pointer || currentResults.Pointer != results.Pointer
                || ingredients.Count != ingredientCount || ingredients._version != ingredientVersion
                || results.Count != resultCount || results._version != resultVersion)
                throw new InvalidOperationException("A matching recipe's metadata changed during the query.");
            match = copiedRecipe;
            return RecipeQueryReadOutcome.Matched;
        }

        private enum RecipeQueryReadOutcome
        {
            NoMatch,
            Matched,
            Unavailable,
            RowLimit
        }
    }

    public enum RecipeItemRole
    {
        Ingredient,
        DeclaredResult
    }

    /// <summary>
    /// A bounded observation of the loaded recipe list, containing managed metadata only.
    /// Recipes preserve native list order and duplicate entries. The indices are temporary
    /// list positions, not recipe identities; no consistency across separate calls is implied.
    /// </summary>
    public sealed class RecipeQuerySnapshot
    {
        internal RecipeQuerySnapshot(int itemId, RecipeItemRole role,
            int startRecipeIndex, int nextRecipeIndex, int totalRecipeCount, int unavailableRecipeCount,
            int rowLimitRecipeCount, RecipeSnapshot[] recipes)
        {
            ItemId = itemId;
            Role = role;
            StartRecipeIndex = startRecipeIndex;
            NextRecipeIndex = nextRecipeIndex;
            TotalRecipeCount = totalRecipeCount;
            UnavailableRecipeCount = unavailableRecipeCount;
            RowLimitRecipeCount = rowLimitRecipeCount;
            Recipes = Array.AsReadOnly(recipes);
        }

        public int ItemId { get; }
        public RecipeItemRole Role { get; }
        public int StartRecipeIndex { get; }
        /// <summary>Next uninspected native list index; equals TotalRecipeCount at the end.</summary>
        public int NextRecipeIndex { get; }
        /// <summary>Native recipe list slots, not the total number of matching recipes.</summary>
        public int TotalRecipeCount { get; }
        public int InspectedRecipeCount => NextRecipeIndex - StartRecipeIndex;
        /// <summary>Inspected slots with unreadable matching fields or incomplete matching recipe metadata.</summary>
        public int UnavailableRecipeCount { get; }
        /// <summary>Inspected slots omitted because matching or metadata copying exceeded the row limit.</summary>
        public int RowLimitRecipeCount { get; }
        public bool HasMoreRecipes => NextRecipeIndex < TotalRecipeCount;
        public bool HasUnknownRecipes => UnavailableRecipeCount != 0 || RowLimitRecipeCount != 0;
        public bool Truncated => HasMoreRecipes || RowLimitRecipeCount != 0;
        /// <summary>True only when this call covered the entire current list without unknown entries.</summary>
        public bool IsComplete => StartRecipeIndex == 0 && !HasMoreRecipes && !HasUnknownRecipes;
        /// <summary>Page status; READY does not imply earlier list slots were inspected by this call.</summary>
        public string Status => HasUnknownRecipes ? "INCOMPLETE" : HasMoreRecipes ? "TRUNCATED" : "READY";
        /// <summary>Complete metadata for confirmed matches only; empty does not imply complete absence.</summary>
        public IReadOnlyList<RecipeSnapshot> Recipes { get; }
    }
}
