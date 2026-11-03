// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class InventoryConsumption
    {
        internal sealed class DefinitionCatalog
        {
            private readonly Dictionary<int, Definition> _definitions = new();
            private readonly ItemDatabase _database;

            private DefinitionCatalog(ItemDatabase database) { _database = database; }

            private sealed class Definition(InventoryLayoutItemGroup group, InventoryEdibleItem edible, ItemData data)
            {
                internal readonly InventoryLayoutItemGroup Group = group;
                internal readonly InventoryEdibleItem Edible = edible;
                internal readonly ItemData Data = data;
                internal readonly int Id = data._id, Uses = edible._numberOfUses;
                internal readonly bool Reusable = edible._isReusable;
                internal readonly bool Volume = data._isVolumeContainer, Perishable = data._isPerishable;

                internal bool Current()
                {
                    var items = ItemDatabaseManager._itemsCache;
                    return IsInventoryPrefabGroup(Group) && Live(Edible) && Live(Data)
                        && Group._itemId == Id && Edible.gameObject.Pointer == Group.gameObject.Pointer
                        && Group.GetComponent<InventoryEdibleItem>()?.Pointer == Edible.Pointer
                        && Data._id == Id && Data._isVolumeContainer == Volume && Data._isPerishable == Perishable
                        && Edible._numberOfUses == Uses && Edible._isReusable == Reusable
                        && items != null && !items.WasCollected && items.TryGetValue(Id, out var current)
                        && Live(current) && current.Pointer == Data.Pointer;
                }
            }

            internal static bool TryCapture(out DefinitionCatalog catalog, out string detail)
            {
                catalog = null;
                detail = "The server native item database and loaded inventory prefab definitions are required.";
                var database = ItemDatabaseManager._instance;
                var items = ItemDatabaseManager._itemsCache;
                if (!Live(database) || items == null || items.WasCollected) return false;
                var result = new DefinitionCatalog(database);
                // These are prefab assets, never a host/remote player's live inventory or a
                // crafting-result edible. Reading them does not initialize or instantiate a rig.
                var edibles = Resources.FindObjectsOfTypeAll<InventoryEdibleItem>();
                if (edibles == null || edibles.WasCollected) return false;
                foreach (var edible in edibles)
                {
                    if (!Live(edible)) continue;
                    var group = edible.GetComponent<InventoryLayoutItemGroup>();
                    if (!IsInventoryPrefabGroup(group)) continue;
                    if (edible.GetIl2CppType().FullName != typeof(InventoryEdibleItem).FullName
                        || group.GetIl2CppType().FullName != typeof(InventoryLayoutItemGroup).FullName)
                    { detail = "An inventory edible uses an unimplemented native action override."; return false; }
                    int id = group._itemId;
                    if (!items.TryGetValue(id, out var data) || !Live(data) || data._id != id)
                    { detail = "An inventory edible has no matching server item definition: " + id; return false; }
                    var components = group.GetComponents<InventoryEdibleItem>();
                    if (components == null || components.Length != 1 || components[0]?.Pointer != edible.Pointer
                        || !result._definitions.TryAdd(id, new Definition(group, edible, data)))
                    { detail = "The server inventory edible definition is ambiguous: " + id; return false; }
                }
                if (result._definitions.Count == 0) return false;
                catalog = result;
                detail = "Captured " + result._definitions.Count + " native inventory edible definitions without creating a player.";
                return true;
            }

            internal bool TryRead(int id, ItemInstance item, PerishableItemInstanceModule decay,
                out int numberOfUses, out string detail)
            {
                numberOfUses = 0;
                detail = "The exact native inventory edible definition is unavailable or changed.";
                if (!Live(_database) || ItemDatabaseManager._instance?.Pointer != _database.Pointer
                    || !_definitions.TryGetValue(id, out var definition) || !definition.Current()) return false;
                var data = definition.Data;
                if ((data._type & global::Sons.Items.Core.Types.Edible) == 0 || data._canNotBeStored
                    || definition.Reusable != definition.Volume || definition.Uses <= 0) return false;
                if (definition.Volume && definition.Perishable)
                {
                    detail = "This native edible has both volume and perishable modules; the existing debit route does not synchronize their custody.";
                    return false;
                }
                if (item == null)
                {
                    if (!SupportsPlainIntake(data)) return false;
                    detail = "Native nonunique edible and item-definition effects selected.";
                    return true;
                }
                if (item.WasCollected || item._itemID != id || item._data?.Pointer != data.Pointer
                    || item._modules == null || item._modules.WasCollected) return false;
                if (!definition.Volume && !definition.Perishable)
                {
                    if (item._modules.Count != 0) return false;
                    detail = "Native unique edible and item-definition effects selected.";
                    return true;
                }
                if (item._modules.Count != 1)
                { detail = "The native edible has additional or missing modules outside the proven debit route."; return false; }
                var module = item._modules[0];
                if (module == null || module.WasCollected || module.ItemId != id || !module._isInstantiated) return false;
                if (definition.Volume)
                {
                    var volume = module.TryCast<VolumeContainerItemInstanceModule>();
                    var volumeData = data._volumeContainerData;
                    if (volume == null || !Live(volumeData) || volume._data?.Pointer != volumeData.Pointer
                        || !float.IsFinite(volumeData.TotalVolume) || volumeData.TotalVolume <= 0
                        || volume._MaxVolume_k__BackingField != volumeData.TotalVolume
                        || !TrySelectVolumePortion(item, definition.Uses, out _)) return false;
                    // Null really means no recipe/effects. It must not become a default water
                    // recipe. Non-null recipes must be the server database's exact objects.
                    var recipe = volume._statRecipe;
                    if (recipe is not null && !IsCatalogRecipe(recipe))
                    { detail = "The retained volume recipe is not an exact server stat-recipe definition."; return false; }
                    numberOfUses = definition.Uses;
                    detail = "Native reusable edible, authored capacity/uses and server volume recipe selected.";
                    return true;
                }
                var perishable = module.TryCast<PerishableItemInstanceModule>();
                var perishableData = data._perishableData;
                if (perishable == null || !Live(perishableData) || perishable._data?.Pointer != perishableData.Pointer
                    || decay == null || decay.WasCollected || !decay._isInstantiated || decay.ItemId != id
                    || decay._data?.Pointer != perishableData.Pointer || decay._itemObject != null
                    || decay._currentState != perishable._currentState) return false;
                var state = decay._CurrentStateData_k__BackingField;
                var states = perishableData.StatesData;
                if (state == null || state.WasCollected || state.State != decay._currentState
                    || states == null || states.WasCollected) return false;
                int matches = 0;
                foreach (var candidate in states)
                {
                    if (candidate == null || candidate.WasCollected) return false;
                    if (candidate.State != state.State) continue;
                    if (candidate.Pointer != state.Pointer || ++matches != 1) return false;
                }
                if (matches != 1) return false;
                detail = "Native perishable edible and exact server decay-state definition selected.";
                return true;
            }

            private bool IsCatalogRecipe(StatRecipe recipe)
            {
                if (!Live(recipe)) return false;
                var data = _database._statRecipesData;
                if (!Live(data)) return false;
                var recipes = data._statRecipes;
                if (recipes == null || recipes.WasCollected) return false;
                int matches = 0;
                foreach (var candidate in recipes)
                {
                    if (!Live(candidate)) return false;
                    if (candidate._hashId != recipe._hashId) continue;
                    if (candidate.Pointer != recipe.Pointer || ++matches != 1) return false;
                }
                return matches == 1;
            }

            private static bool IsInventoryPrefabGroup(InventoryLayoutItemGroup group)
            {
                if (!Live(group) || !Live(group.gameObject) || group.gameObject.scene.IsValid()) return false;
                var groups = group.transform.parent;
                var inventory = Live(groups) ? groups.parent : null;
                var root = Live(inventory) ? inventory.parent : null;
                return Live(root) && !Live(root.parent) && root.name == "PlayerBase"
                    && inventory.name == "InventorySystem" && groups.name == "InventoryLayoutGroups";
            }

            private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        }
    }
}
