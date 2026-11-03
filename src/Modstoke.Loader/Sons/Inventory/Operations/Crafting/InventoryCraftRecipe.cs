// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using global::Sons.Crafting;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    // The database must be selected by the server, not resolved through its LocalPlayer.
    internal sealed class InventoryCraftRecipe
    {
        internal const int MaximumRows = 128;
        internal readonly CraftingRecipeDatabase Database;
        internal readonly CraftingRecipe Recipe;
        internal readonly int Index;
        internal readonly Ingredient[] Ingredients;
        internal readonly int[] Results;
        internal readonly string Name, Fingerprint;
        internal readonly CraftingRecipe.Type Type;
        internal readonly bool IsVariant;
        private readonly string _variantDefinition;
        private readonly IntPtr _variantContainer, _variantMod;
        private readonly string _animation, _completeAudio, _craftAudio;
        private readonly IntPtr _recipes, _ingredients, _results;
        private readonly bool _forceNew;

        internal readonly record struct Ingredient(int Id, int Quantity, bool Reusable = false);

        private InventoryCraftRecipe(CraftingRecipeDatabase database, int index,
            CraftingRecipe recipe, Ingredient[] ingredients, int[] results)
        {
            Database = database; Index = index; Recipe = recipe;
            Ingredients = ingredients; Results = results;
            _recipes = database._recipes.Pointer;
            _ingredients = recipe._ingredients.Pointer;
            _results = recipe._resultingItems.Pointer;
            _forceNew = recipe._forceNewItemInstance;
            Type = recipe._recipeType;
            IsVariant = Type != CraftingRecipe.Type.CraftNewItem || recipe._useContainerDataForResultingItems;
            foreach (var ingredient in ingredients)
                if (ingredient.Reusable || Array.IndexOf(results, ingredient.Id) >= 0) IsVariant = true;
            _variantDefinition = IsVariant ? VariantDefinition(recipe) : null;
            _variantContainer = recipe._containerItemData?.Pointer ?? IntPtr.Zero;
            _variantMod = recipe._weaponMod?.Pointer ?? IntPtr.Zero;
            Name = recipe.name;
            _animation = recipe._animationStateName;
            _completeAudio = recipe._craftCompleteAudioEvent;
            _craftAudio = recipe._duringCraftJingleAudioEvent;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var name = Encoding.UTF8.GetBytes(Name);
            Append(name.Length);
            hash.AppendData(name);
            AppendText(_animation); AppendText(_completeAudio); AppendText(_craftAudio);
            Append(index); Append(_forceNew ? 1 : 0); Append(ingredients.Length);
            foreach (var ingredient in ingredients) { Append(ingredient.Id); Append(ingredient.Quantity); }
            Append(results.Length);
            foreach (int id in results) Append(id);
            if (IsVariant)
            {
                AppendText(_variantDefinition);
                foreach (var ingredient in ingredients) Append(ingredient.Reusable ? 1 : 0);
            }
            Fingerprint = Convert.ToHexString(hash.GetHashAndReset());

            void Append(int value)
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }

            void AppendText(string value)
            {
                if (value == null) { Append(-1); return; }
                var bytes = Encoding.UTF8.GetBytes(value);
                Append(bytes.Length);
                hash.AppendData(bytes);
            }
        }

        internal static InventoryCraftRecipe Capture(CraftingRecipeDatabase database, int index, bool variants = false)
        {
            Require(GameBuild.IsRecognized, "Crafting requires the recognized native game build.");
            Require(database != null && !database.WasCollected && database,
                "The server recipe database is unavailable.");
            var recipes = database._recipes;
            Require(recipes != null && !recipes.WasCollected && recipes.Count is > 0 and <= 4096
                && index >= 0 && index < recipes.Count, "The server recipe index is unavailable or out of bounds.");
            var recipe = recipes[index];
            Require(recipe != null && !recipe.WasCollected && recipe
                && recipe._recipeType is CraftingRecipe.Type.CraftNewItem or CraftingRecipe.Type.ApplyWeaponMod
                    or CraftingRecipe.Type.DismantleWeaponMods or CraftingRecipe.Type.ArrowUpgrade,
                "Unknown native crafting recipe type.");
            var ingredients = recipe._ingredients;
            var results = recipe._resultingItems;
            Require(ingredients != null && !ingredients.WasCollected && ingredients.Count is > 0 and <= MaximumRows
                && results != null && !results.WasCollected && results.Count is >= 0 and <= MaximumRows
                && (results.Count != 0 || recipe._useContainerDataForResultingItems),
                "Recipe ingredient/result rows are unavailable or out of bounds.");
            var copiedIngredients = new Ingredient[ingredients.Count];
            var copiedResults = new int[results.Count];
            var ids = new HashSet<int>();
            for (int i = 0; i < copiedIngredients.Length; i++)
            {
                var row = ingredients[i];
                Require(row != null && !row.WasCollected && row.ItemId > 0 && row.Count > 0
                    && ids.Add(row.ItemId), "Duplicate or invalid ingredient row.");
                copiedIngredients[i] = new(row.ItemId, row.Count, row.IsReusable);
            }
            for (int i = 0; i < copiedResults.Length; i++)
            {
                var row = results[i];
                Require(row != null && !row.WasCollected && row.Id > 0
                    && row.Id <= ServerInventory.MaxItemId, "Invalid native result item.");
                var data = ItemDatabaseManager.ItemById(row.Id);
                Require(data != null && !data.WasCollected && data && data.Id == row.Id
                    && !data._canNotBeStored && data._maxAmount > 0,
                    "A result has no supported native stored-item definition.");
                copiedResults[i] = row.Id;
            }
            // Matched native bodies: GenerateResultingItems 0x32460A0 and FilterResultingItems
            // 0x3246110 preserve fixed rows. CraftResultingItem 0x324AC90 creates one instance
            // per row unless it reuses an ingredient or takes a non-CraftNewItem branch.
            var captured = new InventoryCraftRecipe(database, index, recipe, copiedIngredients, copiedResults);
            Require(variants || !captured.IsVariant, "This recipe requires the retained-instance variant route.");
            Require(captured.Current(), "Recipe changed during authoritative validation.");
            return captured;
        }

        internal bool Current()
        {
            if (Database == null || Database.WasCollected || !Database
                || Recipe == null || Recipe.WasCollected || !Recipe || Recipe.name != Name
                || Recipe._animationStateName != _animation || Recipe._craftCompleteAudioEvent != _completeAudio
                || Recipe._duringCraftJingleAudioEvent != _craftAudio) return false;
            if ((Recipe._containerItemData?.Pointer ?? IntPtr.Zero) != _variantContainer
                || (Recipe._weaponMod?.Pointer ?? IntPtr.Zero) != _variantMod) return false;
            var recipes = Database._recipes;
            var ingredients = Recipe._ingredients;
            var results = Recipe._resultingItems;
            if (recipes == null || recipes.WasCollected || recipes.Pointer != _recipes
                || Index >= recipes.Count || recipes[Index]?.Pointer != Recipe.Pointer
                || Recipe._recipeType != Type
                || (IsVariant ? VariantDefinition(Recipe) != _variantDefinition : Recipe._useContainerDataForResultingItems)
                || Recipe._forceNewItemInstance != _forceNew
                || ingredients == null || ingredients.WasCollected || ingredients.Pointer != _ingredients
                || ingredients.Count != Ingredients.Length || results == null || results.WasCollected
                || results.Pointer != _results || results.Count != Results.Length) return false;
            for (int i = 0; i < Ingredients.Length; i++)
            {
                var row = ingredients[i];
                if (row == null || row.WasCollected || row.ItemId != Ingredients[i].Id
                    || row.Count != Ingredients[i].Quantity || row.IsReusable != Ingredients[i].Reusable) return false;
            }
            for (int i = 0; i < Results.Length; i++)
                if (results[i] == null || results[i].WasCollected || results[i].Id != Results[i]) return false;
            return true;
        }

        private static string VariantDefinition(CraftingRecipe recipe)
        {
            var values = new List<int> { (int)recipe._recipeType, (int)recipe._arrowUpgradeType,
                recipe._useContainerDataForResultingItems ? 1 : 0 };
            var mod = recipe._weaponMod;
            Require(mod == null || !mod.WasCollected, "Weapon modification definition is unavailable.");
            values.Add(mod == null ? 0 : mod.ModItemId);
            values.Add(mod == null ? 0 : (int)mod.AttachesToSlot);
            values.Add(mod == null ? 0 : (int)mod.RequiredSlot);
            if (recipe._useContainerDataForResultingItems)
            {
                var container = recipe._containerItemData;
                Require(container != null && !container.WasCollected && container && container._items != null
                    && !container._items.WasCollected && container._items.Count <= MaximumRows,
                    "Container recipe definition is unavailable.");
                values.Add(BitConverter.SingleToInt32Bits(container._emptyChance));
                values.Add(container._doNotAllowDuplicates ? 1 : 0);
                values.Add(container._countRange.x); values.Add(container._countRange.y); values.Add(container._maxWeight);
                values.Add(container._items.Count);
                foreach (var item in container._items)
                {
                    Require(item != null && !item.WasCollected, "Container candidate disappeared.");
                    values.Add(item._id); values.Add(item._weight); values.Add(item._maxCount); values.Add(item._maxWeight);
                }
            }
            return string.Join(",", values);
        }

        private static void Require(bool condition, string detail)
            => PlayerInventoryBaseline.Require(condition, detail);
    }

    /// <summary>Exact stored server-owned key and quantity selected for one recipe execution.</summary>
    public readonly record struct CraftIngredient(ulong InstanceKey, int Quantity);

    public enum CraftState
    {
        Reserved, RemovingIngredients, DeliveringResults, Applied, Unconfirmed
    }

    /// <summary>Exact custody receipt. EffectsApplied separately certifies bound actor counters, not client presentation.</summary>
    public readonly record struct CraftReceipt(ulong Transaction,
        CraftState State, int ConfirmedIngredientRemovals, int IngredientRemovals,
        int ConfirmedResults, int Results, ulong FirstNativeTransaction, ulong LastNativeTransaction,
        string Detail)
    {
        public int RecipeIndex { get; init; }
        public string RecipeFingerprint { get; init; }
        public bool EffectsApplied { get; init; }
    }
}
