// SPDX-License-Identifier: GPL-3.0-or-later
// Blueprint fallback adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using global::Sons.Crafting.Structures;
using SonsSdk;
using SonsSdk.Building;
using SonsSdk.Networking;
using UnityEngine;

namespace Modstoke.Loader;

internal sealed partial class BlueprintFallback : ISdkBlueprintFallbackDriver
{
    public void SetupRecipeIngredients(StructureRecipe recipe) =>
        BlueprintRegistration.Instance.SetupRecipeIngredients(recipe);
    public bool AddRecipeToDatabase(StructureRecipe recipe) =>
        BlueprintRegistration.Instance.AddRecipeToDatabase(recipe);
    public StructureRecipe CreateNewRecipe(int id, string name, StructureCraftingNode node = null, GameObject built = null) =>
        BlueprintRegistration.Instance.CreateNewRecipe(id, name, node, built);
    public void InitRecipeLocalization(StructureRecipe recipe) =>
        BlueprintRegistration.Instance.InitRecipeLocalization(recipe);

    public void InitRecipe(StructureRecipe recipe)
    {
        CustomBlueprintManager.AddRecipeToDatabase(recipe);

        EntityManager.RegisterPrefab(recipe._builtPrefab);

        if (recipe._structureNodePrefab)
        {
            EntityManager.RegisterPrefab(recipe._structureNodePrefab);
            CustomBlueprintManager.InitObjectInteraction(recipe._structureNodePrefab.GetComponent<StructureCraftingNode>());
            return;
        }

        var builtCopy = recipe._builtPrefab.Instantiate();
        var craftingNode = CustomBlueprintManager.CreateCraftingNode(builtCopy.transform);
        recipe._structureNodePrefab = craftingNode.gameObject;
    }

    public void CreateBookPage(StructureRecipe topRecipe, StructureRecipe bottomRecipe, Texture2D background) =>
        BlueprintRegistration.Instance.CreateBookPage(topRecipe, bottomRecipe, background);
    public void SetupObjectAsCraftingNode(ScrewStructureRegistration reg) =>
        BlueprintRegistration.Instance.SetupObjectAsCraftingNode(reg);
}
