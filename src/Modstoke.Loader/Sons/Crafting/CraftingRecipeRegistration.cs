// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
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

        /// <summary>An item whose own crafting recipe lends this one its craft animation and sounds. The animation moves
        /// that recipe's own parts in the player's hands, so name one only for the same ingredients; 0 completes the
        /// craft at once.</summary>
        public int AnimationFromItemId { get; init; }
    }

    /// <summary>
    /// Adds crafting mat recipes. Strict inventory checks a craft against the server's recipe list by position and
    /// content, so every recipe database in every process gets the same additions in the same order, after the
    /// game's own: weapon attachment recipes first, then these sorted by mod id, more ingredient kinds first, and
    /// key. Register at mod start in every process, clients and servers alike; recipes go in once a world is loading.
    /// The mat makes the first recipe all of whose ingredients lie on it, in at least each count (other items may
    /// lie there too and stay), and only while every result still fits the inventory: a player who carries the most
    /// of an item they may cannot make another.
    /// </summary>
    public static class CraftingRecipeRegistration
    {
        private const int MaximumRecipes = 128, MaximumRows = 8, Searches = 3;
        private const float SearchSpacing = 30f;
        private static readonly List<Registration> Registrations = new();
        private static readonly List<CraftingRecipeDatabase> Known = new();
        private static float _nextCheck, _nextSearch;
        private static int _searches;
        private static bool _loggedFailure, _loggedNoTemplate, _hooked, _loggedGuardFailure;
        private static IntPtr _lastRefused;

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
                // The mat makes the first recipe all of whose ingredients lie on it, so within a mod the recipes with
                // more kinds come first: a recipe whose ingredients include another's wins when they are all there.
                Registrations.Sort((a, b) =>
                {
                    int order = string.CompareOrdinal(a.Spec.ModId, b.Spec.ModId);
                    if (order == 0) order = b.Spec.Ingredients.Count.CompareTo(a.Spec.Ingredients.Count);
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

        // Finding every recipe database walks every loaded object, so it runs when a world loads and twice more over
        // the next minute, or when a database it found was unloaded. The local crafting system's database is checked
        // every time, at the cost of one field read, so a new player database is filled at once.
        internal static void Tick()
        {
            if (!GameRuntime.IsGameScene)
            {
                if (_searches != 0) { Known.Clear(); _searches = 0; }
                return;
            }
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            try
            {
                Registration[] registrations;
                lock (Registrations) registrations = Registrations.ToArray();
                if (registrations.Length > 0) Hook();
                bool lost = Known.RemoveAll(database => database == null || database.WasCollected) > 0;
                var system = CraftingSystem._Instance_k__BackingField;
                if (system != null && !system.WasCollected && system._recipeDatabase != null) Remember(system._recipeDatabase);
                if (lost || (_searches < Searches && Time.unscaledTime >= _nextSearch))
                {
                    _searches = Math.Min(_searches + 1, Searches);
                    _nextSearch = Time.unscaledTime + SearchSpacing;
                    foreach (var database in Resources.FindObjectsOfTypeAll<CraftingRecipeDatabase>()) Remember(database);
                }
                int attachments = 0, recipes = 0;
                foreach (var database in Known)
                {
                    var list = database._recipes;
                    if (list == null || list.WasCollected) continue;
                    attachments += WeaponAttachments.AppendRecipes(list);
                    recipes += Append(list, registrations);
                }
                if (attachments + recipes == 0) return;
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

        private static void Remember(CraftingRecipeDatabase database)
        {
            if (database == null || database.WasCollected || Known.Exists(known => known.Pointer == database.Pointer)) return;
            var list = database._recipes;
            if (list != null && !list.WasCollected && IsMatDatabase(list)) Known.Add(database);
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
                if (registration.Recipe == null) WarnIfShadowed(list, registration.Spec);
                registration.Recipe = recipe;
                list.Add(recipe);
                added++;
            }
            return added;
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-craft-recipes");
                harmony.Patch(HarmonyLib.AccessTools.Method(typeof(CraftingSystem), nameof(CraftingSystem.TryCombineIngredients)),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(CraftingRecipeRegistration), nameof(BeforeCombine)));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Mod recipes are made even when their results do not fit: " + error.Message);
            }
        }

        // With nothing that fits, the game still makes the last recipe it found, its results left on the mat, and a
        // smaller recipe whose ingredients are all among those laid is made when the larger one has no room. So a mod
        // recipe is made only while its results fit, and when the mat holds exactly one mod recipe's ingredients it
        // makes that recipe or nothing.
        private static bool BeforeCombine(CraftingSystem __instance)
        {
            try
            {
                var current = __instance?._CurrentIngredients_k__BackingField;
                var inventory = LocalPlayer.Inventory;
                if (current == null || current.Count == 0 || inventory == null) return true;
                Registration[] registrations;
                lock (Registrations) registrations = Registrations.ToArray();
                foreach (var registration in registrations)
                {
                    var recipe = registration.Recipe;
                    if (recipe == null || recipe.WasCollected || !LaysExactly(registration.Spec, current)) continue;
                    return Fits(recipe, inventory) || Refuse(recipe);
                }
                if (!__instance.HasSatisfiedRecipeWithCurrentIngredients(out var chosen) || chosen == null) return true;
                foreach (var registration in registrations)
                    if (registration.Recipe != null && registration.Recipe.Pointer == chosen.Pointer)
                        return Fits(chosen, inventory) || Refuse(chosen);
                return true;
            }
            catch (Exception error)
            {
                if (!_loggedGuardFailure)
                {
                    _loggedGuardFailure = true;
                    RuntimeEntryPoint.LogSource?.LogWarning("The crafting mat's mod recipe check failed: " + error.Message);
                }
                return true;
            }
        }

        private static bool LaysExactly(CraftRecipeSpec spec, Il2CppSystem.Collections.Generic.List<NativeIngredient> current)
        {
            if (current.Count != spec.Ingredients.Count) return false;
            foreach (var (itemId, count) in spec.Ingredients)
            {
                var laid = false;
                foreach (var ingredient in current)
                    if (ingredient != null && ingredient.ItemId == itemId) { laid = ingredient.Count >= count; break; }
                if (!laid) return false;
            }
            return true;
        }

        private static bool Fits(CraftingRecipe recipe, PlayerInventory inventory)
        {
            var wanted = new Dictionary<int, int>();
            foreach (var result in recipe._resultingItems)
                if (result != null) wanted[result.Id] = wanted.TryGetValue(result.Id, out var count) ? count + 1 : 1;
            foreach (var (itemId, count) in wanted)
                if (!inventory.HasRoomFor(itemId, count)) return false;
            return true;
        }

        // The cog does nothing, so the player is told why, on the game's own message line.
        private static bool Refuse(CraftingRecipe recipe)
        {
            if (_lastRefused != recipe.Pointer)
            {
                _lastRefused = recipe.Pointer;
                RuntimeEntryPoint.LogSource?.LogInfo("Crafting mat: " + recipe.name + " is not made; its result does not fit the inventory.");
            }
            try
            {
                var result = recipe._resultingItems != null && recipe._resultingItems.Count > 0 ? recipe._resultingItems[0] : null;
                var item = result != null ? ItemDatabaseManager.ItemById(result.Id) : null;
                var name = item != null && !string.IsNullOrEmpty(item._name) ? item._name : "that";
                var hud = HudGui._instance;
                if (hud != null && hud) hud.DisplayGeneralMessage("You can't carry another " + name + ".", 3f);
            }
            catch (Exception) { /* The message is a courtesy; the refusal stands without it. */ }
            return false;
        }

        // The mat makes the first recipe all of whose ingredients lie on it, so an earlier recipe that takes only
        // some of this one's ingredients, in no greater counts, is made instead whenever this one could be.
        private static void WarnIfShadowed(Il2CppSystem.Collections.Generic.List<CraftingRecipe> list, CraftRecipeSpec spec)
        {
            var counts = spec.Ingredients.ToDictionary(ingredient => ingredient.ItemId, ingredient => ingredient.Count);
            foreach (var other in list)
            {
                if (other == null || other.WasCollected || other._recipeType != CraftingRecipe.Type.CraftNewItem
                    || other._ingredients is not { Count: > 0 } ingredients) continue;
                var within = true;
                foreach (var ingredient in ingredients)
                    if (ingredient == null || !counts.TryGetValue(ingredient.ItemId, out var count) || ingredient.Count > count)
                    {
                        within = false;
                        break;
                    }
                if (!within) continue;
                RuntimeEntryPoint.LogSource?.LogWarning("Recipe " + spec.ModId + ":" + spec.Key + " can never be made: "
                    + other.name + " comes first on the crafting mat and takes only ingredients this one lays down.");
                return;
            }
        }

        /// <summary>
        /// The "crafting check" console command: for each mod recipe, lays its ingredients on the mat in thought and
        /// asks the game's own matching which recipe it would make, then puts the mat back as it was. A recipe that
        /// makes nothing names the results the inventory has no room for.
        /// </summary>
        internal static string Check()
        {
            var system = CraftingSystem._Instance_k__BackingField;
            if (system == null || system.WasCollected || system._recipeDatabase == null || system._recipeDatabase._recipes == null)
                return "The crafting mat is not loaded; enter a world first.";
            Registration[] registrations;
            lock (Registrations) registrations = Registrations.ToArray();
            if (registrations.Length == 0) return "No mod recipes are registered.";
            var inventory = LocalPlayer.Inventory;
            var lines = new List<string>();
            var ingredients = system._CurrentIngredients_k__BackingField;
            var available = system._availableRecipes;
            try
            {
                foreach (var registration in registrations)
                {
                    var spec = registration.Spec;
                    var name = spec.ModId + ":" + spec.Key;
                    if (registration.Recipe == null || registration.Recipe.WasCollected
                        || !system._recipeDatabase._recipes.Contains(registration.Recipe))
                    {
                        lines.Add(name + ": not on this mat yet.");
                        continue;
                    }
                    var laid = new Il2CppSystem.Collections.Generic.List<NativeIngredient>();
                    foreach (var (itemId, count) in spec.Ingredients)
                        laid.Add(new NativeIngredient { ItemId = itemId, Count = count, IsReusable = false });
                    // The game's own search reads each ingredient's item instance for arrow upgrades, which imagined
                    // ingredients lack, so the candidates are the craft-new-item recipes that hold every kind laid.
                    var candidates = new Il2CppSystem.Collections.Generic.List<CraftingRecipe>();
                    foreach (var recipe in system._recipeDatabase._recipes)
                    {
                        if (recipe == null || recipe.WasCollected || recipe._recipeType != CraftingRecipe.Type.CraftNewItem) continue;
                        var holdsAll = true;
                        foreach (var (itemId, _) in spec.Ingredients)
                            if (!recipe.HasIngredient(itemId)) { holdsAll = false; break; }
                        if (holdsAll) candidates.Add(recipe);
                    }
                    system._CurrentIngredients_k__BackingField = laid;
                    system._availableRecipes = candidates;
                    if (inventory != null && !Fits(registration.Recipe, inventory))
                    {
                        lines.Add(name + ": not made, its result does not fit the inventory.");
                        continue;
                    }
                    lines.Add(name + (system.HasSatisfiedRecipeWithCurrentIngredients(out var made) && made != null
                        ? ": makes " + (made.Pointer == registration.Recipe.Pointer ? "it" : made.name + " instead") + "."
                        : ": makes nothing."));
                }
            }
            finally
            {
                system._CurrentIngredients_k__BackingField = ingredients;
                system._availableRecipes = available;
            }
            return string.Join(" ", lines);
        }

        /// <summary>
        /// The "crafting try &lt;mod:key&gt;" console command for test runs: with the inventory open, lays the recipe's
        /// ingredients on the mat the way a click does and turns the cog.
        /// </summary>
        /// <summary>"crafting places": every ingredient place on the mat, in the mat root's own frame, for layout work.</summary>
        internal static string Places()
        {
            var system = CraftingSystem._Instance_k__BackingField;
            if (system == null || system.WasCollected || system._ingredientLayoutGroups == null) return "The crafting mat is not loaded.";
            var root = system._ingredientLayoutGroups;
            var lines = new List<string> { "root scale " + root.lossyScale.ToString("F3") + ", right " + root.right.ToString("F2") + ", forward " + root.forward.ToString("F2") };
            foreach (var group in system._ingredientLayoutItemGroups)
            {
                if (group == null || group.WasCollected) continue;
                var rows = group._layoutItems;
                var world = rows != null && rows.Count > 0 && rows[0] != null ? rows[0].transform.position : group.transform.position;
                var local = root.InverseTransformPoint(world);
                var camera = Camera.main;
                var screen = camera != null ? camera.WorldToScreenPoint(world) : Vector3.zero;
                lines.Add(group._itemId + " " + local.ToString("F3") + " screen (" + (int)screen.x + ", " + (int)(Screen.height - screen.y) + ")");
            }
            return string.Join(" | ", lines);
        }

        /// <summary>"crafting stock &lt;mod:key&gt;": with the inventory closed, tops the player up with the recipe's ingredients.</summary>
        internal static string Stock(string name)
        {
            var spec = Find(name);
            if (spec == null) return "No mod recipe is called " + name + ".";
            var added = new List<string>();
            foreach (var (itemId, count) in spec.Ingredients)
            {
                Inventory.TryGetItemCount(itemId, out var have, out _);
                if (have >= count) continue;
                if (!Inventory.TryAddItem(itemId, count - have, out var result))
                    return "Item " + itemId + " could not be added to the inventory: " + result?.Detail;
                added.Add((count - have) + " x " + itemId);
            }
            return name + ": " + (added.Count == 0 ? "every ingredient is already carried." : "added " + string.Join(", ", added) + ".");
        }

        private static CraftRecipeSpec Find(string name)
        {
            lock (Registrations) return Registrations.Find(entry => entry.Spec.ModId + ":" + entry.Spec.Key == name)?.Spec;
        }

        internal static string TryCraft(string name, bool turnCog = true)
        {
            var spec = Find(name);
            if (spec == null) return "No mod recipe is called " + name + ".";
            var inventory = LocalPlayer.Inventory;
            if (inventory == null || inventory._currentView != PlayerInventory.PlayerViews.Inventory)
                return "Open the inventory first.";
            Inventory.TryGetItemCount(spec.ResultItemId, out var before, out _);
            var steps = new List<string>();
            foreach (var (itemId, count) in spec.Ingredients)
            {
                Inventory.TryGetItemCount(itemId, out var have, out _);
                if (have < count) return "Item " + itemId + " is short (" + have + " of " + count + "); run crafting stock first.";
                // Mod items lie on the gear page, the game's own on its page; the page that holds the item is shown.
                InventoryLayoutItemGroup group = null;
                foreach (var page in new[] { false, true })
                {
                    Inventory.TrySetGearPage(page);
                    foreach (var candidate in inventory.GetComponentsInChildren<InventoryLayoutItemGroup>(true))
                        if (candidate != null && candidate._itemId == itemId && candidate.isActiveAndEnabled) { group = candidate; break; }
                    if (group != null) break;
                }
                if (group == null) return "Item " + itemId + " has no place in the inventory.";
                for (var laid = 0; laid < count; laid++)
                {
                    InventoryLayoutItem source = null;
                    int rows = 0, active = 0, initialized = 0, valid = 0;
                    foreach (var row in group._layoutItems)
                    {
                        if (row == null) continue;
                        rows++;
                        if (!row.isActiveAndEnabled) continue;
                        active++;
                        if (row._isInitialized) initialized++;
                        if (row._isValidCraftingIngredient) valid++;
                        if (row._isInitialized && row._isValidCraftingIngredient) source ??= row;
                    }
                    if (source == null)
                        return "Item " + itemId + " has no item on the inventory mat to lay down (group " + group.name + ": " + rows
                            + " rows, " + active + " active, " + initialized + " initialized, " + valid + " valid ingredients).";
                    Crafting.TryStageIngredient(group, source, out var moved);
                    steps.Add(itemId + ": " + (moved?.Outcome.ToString() ?? "none"));
                    if (moved == null || !moved.Completed) return "Laying item " + itemId + " on the mat failed: " + moved?.Detail;
                }
            }
            Inventory.TrySetGearPage(false);
            var system = CraftingSystem._Instance_k__BackingField;
            var mat = string.Join(", ", spec.Ingredients.Select(ingredient => ingredient.ItemId + " x" + system.GetIngredientCount(ingredient.ItemId)));
            if (!turnCog) return name + ": laid " + string.Join(", ", steps) + "; mat " + mat + ".";
            var room = inventory.HasRoomFor(spec.ResultItemId, 1);
            var amount = inventory.AmountOf(spec.ResultItemId, true, false);
            var begun = Crafting.TryBeginCurrentCraft(out var execution, out var status);
            var state = execution.ReadState();
            Inventory.TryGetItemCount(spec.ResultItemId, out var after, out _);
            return name + ": laid " + string.Join(", ", steps) + "; mat " + mat + "; room for " + spec.ResultItemId + " " + room
                + " (carried " + amount + ", counted " + before + "); cog " + (begun ? "accepted" : "refused") + " (" + status
                + "); recipe " + state.RecipeName + ", " + state.Outcome + "; counted after " + after + ".";
        }

        // A copy of one of the game's craft-new-item recipes, for its sounds and settings. It keeps the craft animation
        // only when it is the recipe that makes AnimationFromItemId; any other completes at once, as the weapon
        // attachment recipes do, since a game animation plays its own recipe's parts in the player's hands.
        private static CraftingRecipe Build(Il2CppSystem.Collections.Generic.List<CraftingRecipe> list, CraftRecipeSpec spec)
        {
            CraftingRecipe chosen = null, first = null;
            foreach (var candidate in list)
            {
                if (candidate == null || candidate.WasCollected || candidate._recipeType != CraftingRecipe.Type.CraftNewItem
                    || candidate._useContainerDataForResultingItems) continue;
                first ??= candidate;
                if (spec.AnimationFromItemId == 0) break;
                if (string.IsNullOrEmpty(candidate._animationStateName) || candidate._resultingItems == null) continue;
                foreach (var result in candidate._resultingItems)
                    if (result != null && result.Id == spec.AnimationFromItemId) { chosen = candidate; break; }
                if (chosen != null) break;
            }
            var template = chosen ?? first;
            if (template == null)
            {
                if (!_loggedNoTemplate)
                {
                    _loggedNoTemplate = true;
                    RuntimeEntryPoint.LogSource?.LogWarning("The crafting mat has no craft-new-item recipe to copy, so mod recipes cannot be added.");
                }
                return null;
            }
            if (spec.AnimationFromItemId != 0 && chosen == null)
                RuntimeEntryPoint.LogSource?.LogWarning("Recipe " + spec.ModId + ":" + spec.Key + " found no animated recipe that makes item "
                    + spec.AnimationFromItemId + ", so it completes at once.");
            RuntimeEntryPoint.LogSource?.LogInfo("Recipe " + spec.ModId + ":" + spec.Key + " copies the game's " + template.name
                + (chosen != null ? " with its craft animation " + chosen._animationStateName + "." : " and completes at once."));
            var recipe = Object.Instantiate(template);
            recipe.name = spec.ModId + ":" + spec.Key;
            recipe.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            recipe._recipeType = CraftingRecipe.Type.CraftNewItem;
            if (chosen == null) recipe._animationStateName = "";
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
