// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Construction;
using Endnight.Localization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sons.Crafting;
using Sons.Crafting.Structures;
using Sons.Weapon;
using SonsSdk;
using UnityEngine;
using Blueprints = SonsSdk.Building.CustomBlueprintManager;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsBlueprintRegistration
    {
        private static readonly Regex PascalToHumanRegex = new("[a-z][A-Z]", RegexOptions.Compiled);

        public void ProcessFreeformStructure(Transform freeformStructure)
        {
            foreach (var element in freeformStructure.GetComponentsInChildren<StructureElement>())
            {
                foreach (var renderer in element.gameObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer.name is "TerrainBumpProjector" or "Shadows" or "GrassPusher")
                    {
                        renderer.gameObject.TryDestroy();
                        continue;
                    }
                    if (!renderer.GetComponent<StructureGhostSwapper>())
                        renderer.gameObject.AddComponent<StructureGhostSwapper>();
                }

                var itemId = element._profile?._item?._id ?? 0;
                if (itemId < 1)
                    continue;

                var ingredient = element.gameObject.AddComponent<StructureCraftingNodeIngredient>();
                ingredient.OnValidate();
                ingredient._itemId = itemId;
                ingredient._requiresAllPreviousIngredients = false;
            }
        }

        public List<StructureCraftingNodeIngredient> ProcessStructure(Transform structureRoot, int itemId)
        {
            var ingredients = new List<StructureCraftingNodeIngredient>();
            foreach (var renderer in structureRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.name is "TerrainBumpProjector" or "Shadows" or "GrassPusher")
                {
                    renderer.gameObject.TryDestroy();
                    continue;
                }
                if (!renderer.GetComponent<StructureGhostSwapper>())
                    renderer.gameObject.AddComponent<StructureGhostSwapper>();

                var ingredient = renderer.gameObject.AddComponent<StructureCraftingNodeIngredient>();
                ingredient.OnValidate();
                ingredient._itemId = itemId;
                ingredient._requiresAllPreviousIngredients = false;
                ingredients.Add(ingredient);
            }
            return ingredients;
        }

        public Il2CppArrayBase<StructureCraftingNodeIngredient> GetIngredients(Transform structureRoot)
        {
            return structureRoot.GetComponentsInChildren<StructureCraftingNodeIngredient>(true);
        }

        public void InitObjectInteraction(StructureCraftingNode node)
        {
            if (node._ingredientUiTemplate)
                return;

            var interaction = Blueprints._craftingNodePrefab.transform.Find("StructureInteractionObjects").gameObject.Instantiate();
            try
            {
                var transform = interaction.transform;
                transform.SetParent(node.transform, false);
                transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

                var ingredientTemplate = transform.Find("Canvas/UiRoot/Ingredients/IngredientUiTemplate").gameObject;
                var cancelElement = transform.Find("Canvas/UiRoot/CancelStructureInteractionElement").gameObject;
                var ingredients = new Il2CppSystem.Collections.Generic.List<StructureCraftingNodeIngredientUi>();
                ingredients.Add(transform.Find("Canvas/UiRoot/Ingredients/IngredientUi").GetComponent<StructureCraftingNodeIngredientUi>());
                transform.Find("UiLocator").localPosition = node.GetComponent<BoxCollider>().center;

                node._cancelStructureInteractionElement = cancelElement;
                node._ingredientUi = ingredients;
                // This field also guards subsequent initialization attempts.
                node._ingredientUiTemplate = ingredientTemplate;
            }
            catch
            {
                try { interaction.SetActive(false); }
                catch (Exception exception) { ReportPrefabCleanupFailure(exception); }
                DiscardPrefabCandidate(interaction);
                throw;
            }
        }

        public void InitRecipeLocalization(StructureRecipe recipe)
        {
            var key = LocalizationUtils.GetLocalizationKey(recipe._displayName);
            LocalizationTools.BlueprintBookTable.AddEntry(key, recipe._displayName);
            recipe._localizationId = key;
        }

        public void CreateBookPage(StructureRecipe topRecipe, StructureRecipe bottomRecipe, Texture2D background)
        {
            if (topRecipe)
                InitRecipeLocalization(topRecipe);
            if (bottomRecipe)
                InitRecipeLocalization(bottomRecipe);

            var page = new BlueprintBookPageData
            {
                _topRecipe = topRecipe,
                _bottomRecipe = bottomRecipe,
                _pageTitleLocalizationId = Blueprints.NextLocalizationString
            };
            var controller = ItemTools.GetHeldPrefab(ItemTools.Identifiers.BlueprintBook).GetComponent<BlueprintBookController>();
            page._pageImage = background;
            controller._pages._pages.Add(page);
        }

        public string ToDisplayName(string name)
        {
            name = name.Replace("Recipe", "");
            name = name.Split('.')[0];
            return PascalToHumanRegex.Replace(name, match => $"{match.Value[0]} {char.ToLower(match.Value[1])}");
        }
    }
}
