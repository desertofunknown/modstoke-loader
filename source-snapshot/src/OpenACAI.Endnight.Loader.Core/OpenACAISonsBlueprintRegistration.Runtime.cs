// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Bolt;
using Construction;
using RedLoader;
using RedLoader.Utils;
using Sons.Crafting;
using Sons.Crafting.Structures;
using Sons.Weapon;
using SonsSdk;
using SonsSdk.Building;
using SonsSdk.Networking;
using UnityEngine;
using Blueprints = SonsSdk.Building.CustomBlueprintManager;
using Color = System.Drawing.Color;
using Object = UnityEngine.Object;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsBlueprintRegistration
    {
        public void OnGameActivated()
        {
            InitializePrefabs();
            foreach (var registration in Blueprints.LoadedObjects.Values)
                SetupObjectAsCraftingNode(registration);
        }

        private static void InitializePrefabs()
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

        private static void DiscardPrefabCandidate(Object candidate)
        {
            try { if (candidate) Object.Destroy(candidate); }
            catch (Exception exception) { ReportPrefabCleanupFailure(exception); }
        }

        private static void ReportPrefabCleanupFailure(Exception exception)
        {
            try { RLog.Error($"Could not clean up blueprint template: {exception}"); }
            catch { }
        }

        public void SetupObjectAsCraftingNode(ScrewStructureRegistration registration)
        {
            if (Blueprints.ProcessedObjects.TryGetValue(registration.recipeId, out var existingRecipe))
            {
                AddRecipeToDatabase(existingRecipe);
                return;
            }

            var ghost = registration.prefab.Instantiate(Vector3.zero, Quaternion.identity);
            var node = CreateCraftingNode(ghost.transform, null);
            var entity = node.GetComponent<BoltEntity>();
            entity._prefabId = registration.recipeId - 1;
            EntityManager.RegisterPrefab(entity);

            var built = registration.prefab.Instantiate(Vector3.zero, Quaternion.identity);
            var recipe = CreateNewRecipe(registration.recipeId, registration.recipeName, node, built);
            PrepareBuiltStructure(built, recipe);
            SetupRecipeIngredients(recipe);

            Blueprints.OnCraftingNodeCreated.Invoke(node);
            Blueprints.ProcessedObjects[recipe._id] = recipe;
            RLog.Msg(Color.PaleGreen, $"Registered gltf crafting node with recipe id {recipe._id}");
        }

        public StructureCraftingNode CreateCraftingNode(Transform transformToWrap, int? ingredientItemId)
        {
            var ingredients = ingredientItemId == null
                ? GetIngredients(transformToWrap).ToList()
                : ProcessStructure(transformToWrap, ingredientItemId.Value);

            var node = Object.Instantiate(Blueprints._craftingNodePrefab);
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

        public StructureRecipe CreateNewRecipe(int id, string name, StructureCraftingNode node, GameObject built)
        {
            var recipe = Object.Instantiate(Blueprints._recipePrefab);
            recipe._id = id;
            recipe._boltPrefabId = id - 1;
            recipe._category = Blueprints.StructureCategory;
            if (node)
            {
                recipe._structureNodePrefab = node.gameObject;
                node._recipe = recipe;
            }
            if (built)
                recipe._builtPrefab = built;

            recipe.name = name;
            recipe._displayName = ToDisplayName(name);
            recipe.hideFlags = HideFlags.HideAndDontSave;
            AddRecipeToDatabase(recipe);
            RLog.Msg(Color.LawnGreen, $"Added recipe {recipe.name}");
            return recipe;
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
            var ingredients = recipe._ingredients = new Il2CppSystem.Collections.Generic.List<StructureCraftingRecipeIngredient>();
            foreach (var link in node._craftingIngredientLinks)
            {
                var ingredient = link.Ingredient;
                ingredients.Add(new() { ItemId = ingredient.ItemId, Count = ingredient.Count });
            }
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
            EntityManager.RegisterPrefab(built);

            var structure = built.AddComponent<ScrewStructure>();
            structure._recipe = recipe;
            structure.enabled = false;
            built.AddComponent<FreeFormStructureBuiltLinker>();
            built.AddComponent<ScrewStructureDestruction>();
            built.AddComponent<BoxCollider>();
            built.DontDestroyOnLoad();
            built.AsPrefab(structure);
        }
    }
}
