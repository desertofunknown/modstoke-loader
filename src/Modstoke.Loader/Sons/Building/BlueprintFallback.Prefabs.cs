// SPDX-License-Identifier: GPL-3.0-or-later
// Blueprint fallback adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Crafting.Structures;
using SonsSdk.Building;
using UnityEngine;

namespace Modstoke.Loader;

internal sealed partial class BlueprintFallback : ISdkBlueprintFallbackDriver
{
    public void InitializePrefabs() => BlueprintRegistration.InitializePrefabs();
    public void DiscardPrefabCandidate(UnityEngine.Object candidate) =>
        BlueprintRegistration.DiscardPrefabCandidate(candidate);
    public void ReportPrefabCleanupFailure(Exception exception) =>
        BlueprintRegistration.ReportPrefabCleanupFailure(exception);
    public void PrepareBuiltStructure(GameObject built, StructureRecipe recipe) =>
        BlueprintRegistration.Instance.PrepareBuiltStructure(built, recipe);
    public void ProcessFreeformStructure(Transform freeformStructure) =>
        BlueprintRegistration.Instance.ProcessFreeformStructure(freeformStructure);
    public List<StructureCraftingNodeIngredient> ProcessStructure(Transform structureRoot, int itemId) =>
        BlueprintRegistration.Instance.ProcessStructure(structureRoot, itemId);
    public Il2CppArrayBase<StructureCraftingNodeIngredient> GetIngredients(Transform structureRoot) =>
        BlueprintRegistration.Instance.GetIngredients(structureRoot);
    public StructureCraftingNode CreateCraftingNode(Transform transformToWrap, int? ingredientItemId = null) =>
        BlueprintRegistration.Instance.CreateCraftingNode(transformToWrap, ingredientItemId);
    public void InitObjectInteraction(StructureCraftingNode node) =>
        BlueprintRegistration.Instance.InitObjectInteraction(node);
}
