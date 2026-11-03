// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Bolt;
using Construction;
using RedLoader;
using RedLoader.Utils;
using global::Sons.Crafting;
using global::Sons.Crafting.Structures;
using global::Sons.Weapon;
using SonsSdk;
using SonsSdk.Building;
using SonsSdk.Networking;
using UnityEngine;
using Blueprints = SonsSdk.Building.CustomBlueprintManager;
using Color = System.Drawing.Color;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    internal sealed partial class BlueprintRegistration
    {
        private static readonly HashSet<int> ProcessingRecipes = new();

        public void OnGameActivated()
        {
            InitializePrefabs();
            foreach (var registration in Blueprints.LoadedObjects.Values.ToArray())
            {
                try { SetupObjectAsCraftingNode(registration); }
                catch (Exception exception)
                {
                    try { RLog.Error($"Could not prepare blueprint {registration.recipeId}: {exception}"); }
                    catch { }
                }
            }
        }

        internal static void InitializePrefabs()
        {
            if (Blueprints._craftingNodePrefab)
                return;

            var structure = ConstructionTools.GetRecipe(25)._structureNodePrefab;
            var gameObject = structure.Instantiate();
            StructureRecipe newRecipe = null;
            try
            {
                gameObject.name = "CustomStructurePrefab";
                var node = gameObject.GetComponent<StructureCraftingNode>();
                gameObject.transform.Find("Ingredients").gameObject.TryDestroy();
                gameObject.DontDestroyOnLoad().SetActive(false);

                var recipePrefab = node._recipe;
                newRecipe = Object.Instantiate(recipePrefab);
                newRecipe._id = 777;
                node._recipe = newRecipe;

                // Publish the shared template only after its native setup finishes.
                Blueprints._recipePrefab = recipePrefab;
                Blueprints._craftingNodePrefab = node;
            }
            catch
            {
                try { gameObject.SetActive(false); }
                catch (Exception exception) { ReportPrefabCleanupFailure(exception); }
                DiscardPrefabCandidate(gameObject);
                DiscardPrefabCandidate(newRecipe);
                throw;
            }
        }

        internal static void DiscardPrefabCandidate(Object candidate)
        {
            try { if (candidate) Object.Destroy(candidate); }
            catch (Exception exception) { ReportPrefabCleanupFailure(exception); }
        }

        internal static void ReportPrefabCleanupFailure(Exception exception)
        {
            try { RLog.Error($"Could not clean up blueprint template: {exception}"); }
            catch { }
        }

        public void SetupObjectAsCraftingNode(ScrewStructureRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (global::Modstoke.Sons.Building.TryPrepareNativeProfileRegistration(registration)) return;
            if (Blueprints.ProcessedObjects.TryGetValue(registration.recipeId, out var existingRecipe))
            {
                if (StructureCraftingSystem.TryGetInstance(out var currentSystem)
                    && currentSystem._recipeDatabase.TryGetRecipeById(registration.recipeId, out var currentRecipe)
                    && currentRecipe != existingRecipe)
                    throw new InvalidOperationException($"Recipe id {registration.recipeId} is already in use.");
                AddRecipeToDatabase(existingRecipe);
                EntityManager.RegisterPrefab(existingRecipe._structureNodePrefab);
                EntityManager.RegisterPrefab(existingRecipe._builtPrefab);
                return;
            }
            if (!ProcessingRecipes.Add(registration.recipeId))
                return;

            GameObject ghost = null;
            GameObject built = null;
            StructureCraftingNode node = null;
            StructureRecipe recipe = null;
            StructureRecipeDatabase publishedDatabase = null;
            try
            {
                if (StructureCraftingSystem.TryGetInstance(out var system)
                    && system._recipeDatabase.TryGetRecipeById(registration.recipeId, out _))
                    throw new InvalidOperationException($"Recipe id {registration.recipeId} is already in use.");

                ghost = registration.prefab.Instantiate(Vector3.zero, Quaternion.identity);
                node = CreateCraftingNode(ghost.transform, null);
                var entity = node.GetComponent<BoltEntity>();
                entity._prefabId = registration.recipeId - 1;
                built = registration.prefab.Instantiate(Vector3.zero, Quaternion.identity);
                recipe = CreateRecipe(registration.recipeId, registration.recipeName, node, built);
                PrepareBuiltStructure(built, recipe);
                SetupRecipeIngredients(recipe);
                EntityManager.RegisterPrefab(entity);

                if (StructureCraftingSystem.TryGetInstance(out system))
                {
                    if (system._recipeDatabase.TryGetRecipeById(recipe._id, out _))
                        throw new InvalidOperationException($"Recipe id {recipe._id} is already in use.");
                    publishedDatabase = system._recipeDatabase;
                    publishedDatabase._recipes.Add(recipe);
                }
                Blueprints.ProcessedObjects.Add(recipe._id, recipe);
            }
            catch
            {
                var unpublished = true;
                if (publishedDatabase && recipe)
                {
                    try { publishedDatabase._recipes.Remove(recipe); }
                    catch (Exception exception)
                    {
                        unpublished = false;
                        ReportPrefabCleanupFailure(exception);
                    }
                }
                if (unpublished)
                {
                    var nodeReleased = DiscardRegisteredPrefab(node ? node.gameObject : ghost);
                    var builtReleased = DiscardRegisteredPrefab(built);
                    if (nodeReleased && builtReleased)
                        DiscardPrefabCandidate(recipe);
                }
                throw;
            }
            finally
            {
                ProcessingRecipes.Remove(registration.recipeId);
            }

            // The callback may query or reenter registration; publish completion first.
            Blueprints.OnCraftingNodeCreated.Invoke(node);
            RLog.Msg(Color.PaleGreen, $"Registered gltf crafting node with recipe id {recipe._id}");
        }

        private static bool DiscardRegisteredPrefab(GameObject prefab)
        {
            if (!prefab)
                return true;
            try
            {
                if (prefab.TryGetComponent(out BoltEntity entity))
                    EntityManager.UnregisterPrefab(entity);
                prefab.SetActive(false);
                Object.Destroy(prefab);
                return true;
            }
            catch (Exception exception)
            {
                ReportPrefabCleanupFailure(exception);
                return false;
            }
        }

        public StructureCraftingNode CreateCraftingNode(Transform transformToWrap, int? ingredientItemId)
        {
            var ingredients = ingredientItemId == null
                ? GetIngredients(transformToWrap).ToList()
                : ProcessStructure(transformToWrap, ingredientItemId.Value);

            var originalParent = transformToWrap.parent;
            var originalPosition = transformToWrap.localPosition;
            var originalRotation = transformToWrap.localRotation;
            var originalScale = transformToWrap.localScale;
            var node = Object.Instantiate(Blueprints._craftingNodePrefab);
            try
            {
                node.name = transformToWrap.name.Replace("(Clone)", "") + "CraftingNode";

                foreach (var ingredient in node.GetComponentsInChildren<StructureCraftingNodeIngredient>(true))
                    ingredient.gameObject.TryDestroy();
                foreach (var element in node.GetComponentsInChildren<StructureElement>(true))
                    element.gameObject.TryDestroy();
                foreach (var collider in transformToWrap.GetComponentsInChildren<Collider>())
                    Object.Destroy(collider);

                var gameObject = node.gameObject.DontDestroyOnLoad();
                node.enabled = false;
                node._isActive = false;
                gameObject.AsPrefab(node);
                var transform = gameObject.transform;
                transform.Find("Ingredients")?.gameObject.TryDestroy();
                transform.SetPositionAndRotation(transformToWrap.position, transformToWrap.rotation);
                transformToWrap.SetParent(transform, false);
                transformToWrap.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

                var groups = new Dictionary<int, Il2CppSystem.Collections.Generic.List<StructureCraftingNodeIngredient>>();
                foreach (var ingredient in ingredients)
                {
                    var itemId = ingredient.ItemId;
                    if (!groups.TryGetValue(itemId, out var group))
                    {
                        group = new();
                        groups.Add(itemId, group);
                    }
                    group.Add(ingredient);
                }

                node._craftingIngredientLinks.Clear();
                foreach (var (itemId, group) in groups)
                {
                    node._craftingIngredientLinks.Add(new()
                    {
                        Ingredient = new() { Count = group.Count, ItemId = itemId },
                        _ingredients = group
                    });
                }

                gameObject.SetActive(true);
                return node;
            }
            catch
            {
                // The caller owns this subtree even if wrapping fails after reparenting it.
                var detached = true;
                try
                {
                    if (transformToWrap && transformToWrap.parent == node.transform)
                    {
                        transformToWrap.SetParent(originalParent, false);
                        transformToWrap.localPosition = originalPosition;
                        transformToWrap.localRotation = originalRotation;
                        transformToWrap.localScale = originalScale;
                    }
                }
                catch (Exception exception)
                {
                    ReportPrefabCleanupFailure(exception);
                    // Do not destroy a wrapper that still owns the caller's subtree.
                    detached = false;
                }
                if (detached)
                    DiscardRegisteredPrefab(node.gameObject);
                throw;
            }
        }

        public StructureRecipe CreateNewRecipe(int id, string name, StructureCraftingNode node, GameObject built)
        {
            var previousRecipe = node ? node._recipe : null;
            var recipe = CreateRecipe(id, name, node, built);
            try
            {
                if (!AddRecipeToDatabase(recipe)
                    && StructureCraftingSystem.TryGetInstance(out var system)
                    && system._recipeDatabase.TryGetRecipeById(id, out var existingRecipe)
                    && existingRecipe != recipe)
                    throw new InvalidOperationException($"Recipe id {id} is already in use.");
            }
            catch
            {
                try
                {
                    if (node && node._recipe == recipe)
                        node._recipe = previousRecipe;
                    DiscardPrefabCandidate(recipe);
                }
                catch (Exception exception) { ReportPrefabCleanupFailure(exception); }
                throw;
            }
            RLog.Msg(Color.LawnGreen, $"Added recipe {recipe.name}");
            return recipe;
        }

        private StructureRecipe CreateRecipe(int id, string name, StructureCraftingNode node, GameObject built)
        {
            var recipe = Object.Instantiate(Blueprints._recipePrefab);
            try
            {
                recipe._id = id;
                recipe._boltPrefabId = id - 1;
                recipe._category = Blueprints.StructureCategory;
                if (node)
                {
                    recipe._structureNodePrefab = node.gameObject;
                }
                if (built)
                    recipe._builtPrefab = built;

                recipe.name = name;
                recipe._displayName = ToDisplayName(name);
                recipe.hideFlags = HideFlags.HideAndDontSave;
                if (node)
                    node._recipe = recipe;
                return recipe;
            }
            catch
            {
                DiscardPrefabCandidate(recipe);
                throw;
            }
        }

        public bool AddRecipeToDatabase(StructureRecipe recipe)
        {
            if (!StructureCraftingSystem.TryGetInstance(out var system))
                return false;
            if (system._recipeDatabase.TryGetRecipeById(recipe._id, out _))
                return false;

            system._recipeDatabase._recipes.Add(recipe);
            return true;
        }

        public void SetupRecipeIngredients(StructureRecipe recipe)
        {
            var node = recipe._structureNodePrefab.GetComponent<StructureCraftingNode>();
            var ingredients = new Il2CppSystem.Collections.Generic.List<StructureCraftingRecipeIngredient>();
            foreach (var link in node._craftingIngredientLinks)
            {
                var ingredient = link.Ingredient;
                ingredients.Add(new() { ItemId = ingredient.ItemId, Count = ingredient.Count });
            }
            recipe._ingredients = ingredients;
        }

        public void PrepareBuiltStructure(GameObject built, StructureRecipe recipe)
        {
            foreach (var ingredient in built.GetComponentsInChildren<StructureCraftingNodeIngredient>(true))
                Object.Destroy(ingredient);

            if (!built.GetComponent<BoltEntity>())
            {
                var entity = built.AddComponent<BoltEntity>().Init(recipe._id, BoltFactories.ScrewStructureState);
                entity._queryOptionIEntityBehaviour = QueryOptions.GetComponentsInChildren;
            }
            var structure = built.GetComponent<ScrewStructure>() ?? built.AddComponent<ScrewStructure>();
            structure._recipe = recipe;
            structure.enabled = false;
            if (!built.GetComponent<FreeFormStructureBuiltLinker>())
                built.AddComponent<FreeFormStructureBuiltLinker>();
            if (!built.GetComponent<ScrewStructureDestruction>())
                built.AddComponent<ScrewStructureDestruction>();
            if (!built.GetComponent<BoxCollider>())
                built.AddComponent<BoxCollider>();
            built.DontDestroyOnLoad();
            var enabler = built.GetComponent<SonsSdk.UnityUtils.PrefabComponentEnabler>();
            if (!enabler)
                built.AsPrefab(structure);
            else
            {
                var components = enabler.ComponentsToEnable.Get();
                if (components == null)
                {
                    components = new Il2CppSystem.Collections.Generic.List<MonoBehaviour>();
                    enabler.ComponentsToEnable.Set(components);
                }
                if (!components.Contains(structure))
                    components.Add(structure);
            }
            EntityManager.RegisterPrefab(built);
        }
    }
}
