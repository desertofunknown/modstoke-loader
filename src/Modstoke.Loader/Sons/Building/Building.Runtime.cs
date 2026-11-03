// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Modstoke.Loader;
using global::Sons.Crafting;
using global::Sons.Crafting.Structures;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using SonsSdk;
using SonsSdk.Building;
using SonsSdk.Networking;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        private static int _nativeThread;
        private static bool _nativeBusy;
        private static readonly Dictionary<string, RuntimeBlueprint> NativeBlueprints = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, BuildingBookPage> NativeBookPages = new(StringComparer.OrdinalIgnoreCase);

        private static void InitializeNativeBuilding()
        {
            _nativeThread = System.Environment.CurrentManagedThreadId;
            CustomBlueprintManager.OnCraftingNodeCreated.Subscribe(OnNativeBlueprintPrepared);
        }
        private static bool OnNativeThread => _nativeThread != 0 && System.Environment.CurrentManagedThreadId == _nativeThread;

        private static void OnNativeBlueprintPrepared(StructureCraftingNode node)
        {
            if (!OnNativeThread || !node || !node._recipe) return;
            foreach (var blueprint in NativeBlueprints.Values)
            {
                if (blueprint.RecipeId != node._recipe._id || !IsBlueprintPublished(blueprint)) continue;
                if (!ReferenceEquals(blueprint.Recipe, null) && blueprint.Recipe != node._recipe) return;
                FinishBlueprintPreparation(blueprint, node._recipe);
                return;
            }
        }

        /// <summary>
        /// Registers a prepared prefab through the SDK's native recipe/network-prefab path.
        /// Call on the initialized Unity thread, preferably before world loading. Each ingredient
        /// must have a configured StructureCraftingNodeIngredient and ghost visuals. The caller
        /// owns the prefab and its dependencies and must keep them alive and immutable until shutdown.
        /// The prefab must have unit root scale and enabled, non-trigger root BoxColliders
        /// describing the authored placement footprint. Ghosts use those boxes, not the template's size.
        /// All multiplayer peers must register identical assets and stable recipe IDs (including
        /// the reserved ghost ID recipeId - 1). Registration does not distribute assets to peers.
        /// The returned handle can be published even when a callback or preparation fails; inspect
        /// the result and use PrepareRuntimeBlueprint to retry. Registered means source publication,
        /// pending the existing native scene-activation preparation; Ready means a live recipe.
        /// Runtime recipes cannot be hot-unloaded. Metadata-only registrations remain separate.
        /// </summary>
        public static BuildingActionResult RegisterRuntimeBlueprint(
            string modId, string blueprintId, int recipeId, string displayName, GameObject prefab,
            out RuntimeBlueprint blueprint)
        {
            blueprint = null;
            if (!TryEnterNative(out var rejected)) return rejected;
            try { return RegisterRuntimeBlueprintCore(modId, blueprintId, recipeId, displayName, prefab, out blueprint); }
            finally { _nativeBusy = false; }
        }

        private static BuildingActionResult RegisterRuntimeBlueprintCore(
            string modId, string blueprintId, int recipeId, string displayName, GameObject prefab,
            out RuntimeBlueprint blueprint)
        {
            blueprint = null;
            bool attempted = false;
            RuntimeBlueprint candidate = null;
            string key = null;
            try
            {
                if (!TryNormalizeBlueprint(modId, blueprintId, out var mod, out var id, out var reason))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, reason);
                key = BlueprintKey(mod, id);
                if (NativeBlueprints.TryGetValue(key, out var existing))
                {
                    blueprint = existing;
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false,
                        "This runtime identity is already registered. Use its handle to prepare or inspect it; it cannot be replaced.");
                }
                if (recipeId <= 1 || NativeBlueprints.Count >= MaxBlueprintDefinitionCount || !prefab
                    || string.IsNullOrWhiteSpace(displayName))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false,
                        "A live prepared prefab, display name, available registration slot and recipe ID greater than one are required.");
                if ((TryGetBuildingSystem(out var system) && system._recipeDatabase.TryGetRecipeById(recipeId, out _))
                    || CustomBlueprintManager.LoadedObjects.ContainsKey(recipeId)
                    || CustomBlueprintManager.ProcessedObjects.ContainsKey(recipeId))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Recipe ID is already in use.");
                foreach (var registered in CustomBlueprintManager.LoadedObjects.Keys)
                    if ((long)registered == (long)recipeId - 1 || (long)registered == (long)recipeId + 1)
                        return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Recipe and ghost network IDs overlap another blueprint.");
                // The compatibility path creates both native network entities. An attached scene
                // entity, or an existing crafting node, is not an authored ingredient prefab.
                if (prefab.GetComponent<StructureCraftingNode>() || prefab.GetComponent<BoltEntity>()
                    || !prefab.activeSelf || (prefab.scene.IsValid() && prefab.scene.name != "DontDestroyOnLoad"))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false,
                        "Supply an active-self asset prefab or persistent template, not a scene-owned object, crafting node or network entity.");
                var bounds = prefab.GetComponents<BoxCollider>();
                if ((prefab.transform.parent && !IsImportedBlueprintSource(prefab))
                    || prefab.transform.localScale != Vector3.one || bounds.Length == 0)
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false,
                        "Supply a unit-scale root prefab with enabled non-trigger BoxColliders describing its placement footprint.");
                var footprint = new BlueprintFootprintBox[bounds.Length];
                for (int index = 0; index < bounds.Length; index++)
                {
                    var box = bounds[index];
                    if (!box.enabled || box.isTrigger || !ValidFootprintBox(box.center, box.size))
                        return BuildingResult(BuildingActionStatus.Rejected, recipeId, false,
                            "Every root footprint box must be enabled, non-trigger, finite and positive in size.");
                    footprint[index] = new(box.center, box.size);
                }
                var ingredients = prefab.GetComponentsInChildren<StructureCraftingNodeIngredient>(true);
                if (ingredients.Length == 0)
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Prepared ingredient components are required.");
                foreach (var ingredient in ingredients)
                    if (!ingredient || ingredient.ItemId <= 0
                        || (ItemDatabaseManager._instance && !ItemDatabaseManager.ItemById(ingredient.ItemId))
                        || !ingredient.GetComponentInChildren<Renderer>(true)
                        || !ingredient.GetComponentInChildren<StructureGhostSwapper>(true))
                        return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Every ingredient needs a valid item ID, authored visuals and ghost swappers.");

                candidate = new RuntimeBlueprint(mod, id, displayName.Trim(),
                    new ScrewStructureRegistration(prefab, recipeId, mod + "." + id + "Recipe"), footprint);
                // Reserve managed identity before invoking subscribers. The busy guard also rejects reentry.
                NativeBlueprints.Add(key, candidate);
                attempted = true;
                CustomBlueprintManager.TryRegister(candidate.Registration);
                blueprint = candidate;
                var prepared = PrepareBlueprintCore(candidate, ref attempted);
                return BuildingResult(prepared.Status, recipeId, attempted, prepared.Detail);
            }
            catch (Exception exception)
            {
                if (candidate != null && IsBlueprintPublished(candidate)) blueprint = candidate;
                return BuildingResult(BuildingActionStatus.Failed, recipeId, attempted,
                    exception.Message + (blueprint != null ? " The source registration remains published." : ""));
            }
            finally
            {
                if (candidate != null && blueprint == null && key != null) NativeBlueprints.Remove(key);
            }
        }

        /// <summary>Explicitly prepares or rebinds a published blueprint to the current native databases.</summary>
        public static BuildingActionResult PrepareRuntimeBlueprint(RuntimeBlueprint blueprint)
        {
            if (!TryEnterNative(out var rejected)) return rejected;
            bool attempted = false;
            try { return PrepareBlueprintCore(blueprint, ref attempted); }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, blueprint?.RecipeId ?? 0, attempted,
                    exception.Message + " Registration may already have been published; it was not rolled back.");
            }
            finally { _nativeBusy = false; }
        }

        private static BuildingActionResult PrepareBlueprintCore(RuntimeBlueprint blueprint, ref bool attempted)
        {
            int id = blueprint?.RecipeId ?? 0;
            if (!IsBlueprintPublished(blueprint))
                return BuildingResult(BuildingActionStatus.Rejected, id, false, "The handle is not the current published runtime registration.");
            if (blueprint.NativeProfile != null)
            {
                attempted = true;
                PrepareNativeProfile(blueprint);
                return BuildingResult(blueprint.Prepared ? BuildingActionStatus.Ready : BuildingActionStatus.Registered,
                    id, true, blueprint.Prepared ? "Explicit native recipe and prefab graph is ready. No structure was placed."
                        : "Explicit profile is published; the current native databases are not ready.");
            }
            if (!TryGetBuildingSystem(out _) || !ConstructionTools.GetRecipe(25))
                return BuildingResult(BuildingActionStatus.Registered, id, attempted, "Source registered. Native construction templates are not ready; scene activation or explicit preparation must finish before use.");
            if (!blueprint.Registration.prefab)
                return BuildingResult(BuildingActionStatus.Unavailable, id, false, "The caller's source prefab has been destroyed.");
            if (!ReferenceEquals(blueprint.Recipe, null) && CustomBlueprintManager.ProcessedObjects.TryGetValue(id, out var current)
                && current != blueprint.Recipe)
                return BuildingResult(BuildingActionStatus.Rejected, id, false, "The processed recipe identity changed.");

            attempted = true;
            BlueprintRegistration.InitializePrefabs();
            CustomBlueprintManager.SetupObjectAsCraftingNode(blueprint.Registration);
            if (!CustomBlueprintManager.ProcessedObjects.TryGetValue(id, out var recipe) || !recipe)
                return BuildingResult(BuildingActionStatus.Unavailable, id, true, "The source is published but recipe preparation has not completed.");
            FinishBlueprintPreparation(blueprint, recipe);
            return TryGetRuntimeBlueprintRecipe(blueprint, out _)
                ? BuildingResult(BuildingActionStatus.Ready, id, true, "Native recipe and construction prefabs are registered. No structure was placed.")
                : BuildingResult(BuildingActionStatus.Unavailable, id, true, "Preparation returned, but current recipe identity/prefabs are not ready.");
        }

        private static void FinishBlueprintPreparation(RuntimeBlueprint blueprint, StructureRecipe recipe)
        {
            blueprint.Prepared = false;
            if (blueprint.NativeProfile != null)
            {
                if (recipe != blueprint.NativeProfile.Recipe)
                    throw new InvalidOperationException("The explicit native profile recipe was replaced.");
                PrepareNativeProfile(blueprint);
                return;
            }
            if (!ItemDatabaseManager._instance || recipe._ingredients == null || recipe._ingredients.Count == 0)
                throw new InvalidOperationException("The native item database and recipe ingredients must be ready.");
            foreach (var ingredient in recipe._ingredients)
                if (ingredient == null || ingredient.Count <= 0 || ingredient.ItemId <= 0 || !ItemDatabaseManager.ItemById(ingredient.ItemId))
                    throw new InvalidOperationException("A prepared blueprint ingredient does not resolve to a current native item.");
            var node = recipe._structureNodePrefab.GetComponent<StructureCraftingNode>();
            var colliders = node.GetComponents<BoxCollider>();
            if (colliders.Length == 0 || colliders.Length > blueprint.Footprint.Count)
                throw new InvalidOperationException("The native construction template has an unexpected root collider layout.");
            blueprint.Recipe = recipe;
            recipe._displayName = blueprint.DisplayName;
            var expectedColliders = new BoxCollider[blueprint.Footprint.Count];
            // Native single-place collision checks enumerate GetColliders(). Keep the authored
            // boxes separate so a concave footprint does not become one oversized solid box.
            for (int index = 0; index < blueprint.Footprint.Count; index++)
            {
                var collider = index < colliders.Length ? colliders[index] : node.gameObject.AddComponent<BoxCollider>();
                if (index >= colliders.Length)
                {
                    collider.isTrigger = colliders[0].isTrigger;
                    collider.enabled = colliders[0].enabled;
                }
                collider.center = blueprint.Footprint[index].Center;
                collider.size = blueprint.Footprint[index].Size;
                expectedColliders[index] = collider;
            }
            node._maxDiameter = 0;
            node.InitColliders();
            var nativeColliders = node.GetColliders();
            if (nativeColliders == null || nativeColliders.Count != expectedColliders.Length)
                throw new InvalidOperationException("Native placement did not retain the authored footprint collider count.");
            foreach (var collider in expectedColliders)
                if (!nativeColliders.Contains(collider))
                    throw new InvalidOperationException("Native placement did not retain an authored footprint collider.");
            var diameter = node.GetMaxDiameter();
            if (!float.IsFinite(diameter) || diameter <= 0)
                throw new InvalidOperationException("Native placement did not produce a finite positive footprint diameter.");
            blueprint.Prepared = true;
        }

        private static bool FiniteVector(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static bool ValidFootprintBox(Vector3 center, Vector3 size)
            => FiniteVector(center) && FiniteVector(size) && size.x > 0 && size.y > 0 && size.z > 0
                && FiniteVector(center - size * 0.5f) && FiniteVector(center + size * 0.5f);

        /// <summary>Returns a borrowed native recipe only while its current database and prefab identities match.</summary>
        public static bool TryGetRuntimeBlueprintRecipe(RuntimeBlueprint blueprint, out StructureRecipe recipe)
        {
            recipe = null;
            if (!OnNativeThread) return false;
            try
            {
                if (!IsBlueprintPublished(blueprint) || !blueprint.Prepared || !blueprint.Registration.prefab || !TryGetBuildingSystem(out var system)
                    || !CustomBlueprintManager.ProcessedObjects.TryGetValue(blueprint.RecipeId, out var processed)
                    || !processed || (!ReferenceEquals(blueprint.Recipe, null) && processed != blueprint.Recipe)
                    || !system._recipeDatabase.TryGetRecipeById(blueprint.RecipeId, out var current)
                    || current != processed || !RuntimePrefabReady(processed._structureNodePrefab, blueprint.RecipeId - 1)
                    || ((blueprint.NativeProfile == null || !processed._useFreeFormStructures || processed._hybridFreeFormAndScrew)
                        && !RuntimePrefabReady(processed._builtPrefab, blueprint.RecipeId)))
                    return false;
                if (blueprint.NativeProfile != null && (!ItemDatabaseManager._instance
                    || !NativeProfileMatches(blueprint.NativeProfile, blueprint.RecipeId)
                    || !NativeFreeFormDependenciesReady(blueprint.NativeProfile))) return false;
                recipe = processed;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Adds a page to the actual blueprint book data using prepared runtime recipes. The caller
        /// retains the texture and supplies a registered title localization key. Close the book before
        /// registering/removing pages; the next normal opening rebuilds its native selection state.
        /// Re-register after a replacement book database is loaded. No discovery flags are overridden.
        /// </summary>
        public static BuildingActionResult RegisterBookPage(
            string modId, string pageId, RuntimeBlueprint top, RuntimeBlueprint bottom,
            Texture2D background, string titleLocalizationId, out BuildingBookPage page)
        {
            page = null;
            if (!TryEnterNative(out var rejected)) return rejected;
            bool attempted = false;
            try
            {
                if (!TryNormalizeBlueprint(modId, pageId, out var mod, out var id, out var reason))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, reason);
                if (!background || string.IsNullOrWhiteSpace(titleLocalizationId)
                    || top == null || !string.Equals(top.ModId, mod, StringComparison.OrdinalIgnoreCase)
                    || (bottom != null && !string.Equals(bottom.ModId, mod, StringComparison.OrdinalIgnoreCase))
                    || !TryGetRuntimeBlueprintRecipe(top, out var topRecipe))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "Supply current mod-owned recipes, a live page texture and a title localization key.");
                StructureRecipe bottomRecipe = null;
                if (bottom != null && !TryGetRuntimeBlueprintRecipe(bottom, out bottomRecipe))
                    return BuildingResult(BuildingActionStatus.Unavailable, bottom.RecipeId, false, "Bottom recipe is not ready.");
                if (!TryGetBook(out var controller) || !CanEditBook())
                    return BuildingResult(BuildingActionStatus.Unavailable, 0, false, "Book data is unavailable or the book is currently in use.");
                var key = BlueprintKey(mod, id);
                if (NativeBookPages.TryGetValue(key, out var existing))
                {
                    page = existing;
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "Page identity is already reserved. Remove that exact page before replacing or rebinding it.");
                }
                if (NativeBookPages.Count >= MaxBlueprintDefinitionCount)
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "Book page registration limit reached.");
                attempted = true;
                CustomBlueprintManager.InitRecipeLocalization(topRecipe);
                if (bottomRecipe) CustomBlueprintManager.InitRecipeLocalization(bottomRecipe);
                var data = new BlueprintBookPageData
                {
                    _topRecipe = topRecipe, _bottomRecipe = bottomRecipe,
                    _pageImage = background, _pageTitleLocalizationId = titleLocalizationId.Trim()
                };
                var registration = new BuildingBookPage(mod, id, controller._pages, data);
                controller._pages._pages.Add(data);
                page = registration;
                NativeBookPages[key] = registration;
                return BuildingResult(BuildingActionStatus.Ready, top.RecipeId, true, "Page added to native book data; it becomes available on the next normal book opening.");
            }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, 0, attempted, exception.Message);
            }
            finally { _nativeBusy = false; }
        }

        /// <summary>Removes only the exact page created by RegisterBookPage. Does not destroy borrowed assets or recipes.</summary>
        public static BuildingActionResult RemoveBookPage(BuildingBookPage page)
        {
            if (!TryEnterNative(out var rejected)) return rejected;
            bool attempted = false;
            try
            {
                var key = page == null ? null : BlueprintKey(page.ModId, page.PageId);
                if (page == null || !NativeBookPages.TryGetValue(key, out var current)
                    || !ReferenceEquals(current, page))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "Page handle is not current.");
                if (!CanEditBook()) return BuildingResult(BuildingActionStatus.Unavailable, 0, false, "Close the book before changing its pages.");
                if (page.Book && page.Book._pages != null)
                {
                    attempted = true;
                    page.Book._pages.Remove(page.Page);
                    if (page.Book._pages.Contains(page.Page))
                        return BuildingResult(BuildingActionStatus.Failed, 0, true, "The exact page is still present; its reservation was retained.");
                }
                NativeBookPages.Remove(key);
                return BuildingResult(BuildingActionStatus.Removed, 0, attempted, "The registered page is no longer present.");
            }
            catch (Exception exception) { return BuildingResult(BuildingActionStatus.Failed, 0, attempted, exception.Message); }
            finally { _nativeBusy = false; }
        }

        private static bool IsBlueprintPublished(RuntimeBlueprint blueprint)
            => blueprint != null && NativeBlueprints.TryGetValue(BlueprintKey(blueprint.ModId, blueprint.BlueprintId), out var current)
                && ReferenceEquals(current, blueprint)
                && CustomBlueprintManager.LoadedObjects.TryGetValue(blueprint.RecipeId, out var source)
                && ReferenceEquals(source, blueprint.Registration);

        private static bool TryGetBuildingSystem(out StructureCraftingSystem system)
        {
            system = null;
            return OnNativeThread && StructureCraftingSystem.TryGetInstance(out system) && system
                && system._recipeDatabase && system._recipeDatabase._recipes != null
                && ScrewStructureManager._instance && ScrewStructureManager._instance._database == system._recipeDatabase;
        }

        private static bool RuntimePrefabReady(GameObject prefab, int expectedId)
        {
            if (!prefab) return false;
            var entity = prefab.GetComponent<BoltEntity>();
            if (!entity || entity._prefabId != expectedId || entity.isAttached) return false;
            bool registered = false;
            foreach (var current in EntityManager.RegisteredEntities)
                if (current == entity) { registered = true; break; }
            return registered && (!BoltNetwork.isRunning || PrefabDatabase._lookup != null
                && PrefabDatabase._lookup.TryGetValue(entity.prefabId, out var published) && published == prefab);
        }

        private static bool TryGetBook(out BlueprintBookController controller)
        {
            controller = null;
            if (!OnNativeThread || GameRuntime.Environment.IsDedicatedServer || !ItemDatabaseManager._instance) return false;
            var prefab = ItemTools.GetHeldPrefab(ItemTools.Identifiers.BlueprintBook);
            if (!prefab) return false;
            controller = prefab.GetComponent<BlueprintBookController>();
            return controller && controller._pages && controller._pages._pages != null;
        }

        private static bool CanEditBook()
        {
            var inventory = LocalPlayer.Inventory;
            if (!inventory) return true;
            return LocalPlayer.IsInWorld && !LocalPlayer.IsInMidAction
                && !inventory.HasInSlotOrNextSlot(EquipmentSlot.LeftHand, ItemTools.Identifiers.BlueprintBook)
                && !inventory.HasInSlotOrNextSlot(EquipmentSlot.RightHand, ItemTools.Identifiers.BlueprintBook);
        }

        private static bool CanUseNativeBook()
        {
            if (!OnNativeThread) return false;
            try { return TryGetBook(out _) && CanEditBook(); }
            catch { return false; }
        }

        private static bool TryEnterNative(out BuildingActionResult rejected)
        {
            rejected = null;
            if (!OnNativeThread)
                rejected = BuildingResult(BuildingActionStatus.Unavailable, 0, false, "Call on the loader's initialized Unity thread. No work was queued.");
            else if (_nativeBusy)
                rejected = BuildingResult(BuildingActionStatus.Rejected, 0, false, "A building action is already executing; recursive actions are not supported.");
            if (rejected != null) return false;
            _nativeBusy = true;
            return true;
        }

        private static BuildingActionResult BuildingResult(BuildingActionStatus status, int recipeId, bool attempted, string detail)
            => new(status, recipeId, attempted, detail);
    }

    public enum BuildingActionStatus
    {
        Rejected, Unavailable, Registered, Ready, PlacementStarted, Submitted, PlacementCancelled, Removed, Failed, ImportStarted
    }

    /// <summary>Submitted is a native request, never proof of placement, ingredient delivery or server completion.</summary>
    public sealed class BuildingActionResult
    {
        internal BuildingActionResult(BuildingActionStatus status, int recipeId, bool attempted, string detail)
        { Status = status; RecipeId = recipeId; NativeCallAttempted = attempted; Detail = detail; }
        public BuildingActionStatus Status { get; }
        public int RecipeId { get; }
        public bool NativeCallAttempted { get; }
        public string Detail { get; }
    }

    public sealed class RuntimeBlueprint
    {
        internal RuntimeBlueprint(string mod, string id, string displayName, ScrewStructureRegistration registration,
            BlueprintFootprintBox[] footprint)
        { ModId = mod; BlueprintId = id; DisplayName = displayName; Registration = registration; Footprint = Array.AsReadOnly(footprint); }
        public string ModId { get; }
        public string BlueprintId { get; }
        public string DisplayName { get; }
        public int RecipeId => Registration.recipeId;
        internal ScrewStructureRegistration Registration { get; }
        internal StructureRecipe Recipe { get; set; }
        /// <summary>Present only for caller-authored complete native graphs; null for the ingredient-prefab registration path.</summary>
        public BuildingNativeProfile NativeProfile { get; internal set; }
        public IReadOnlyList<BlueprintFootprintBox> Footprint { get; }
        internal bool Prepared { get; set; }
    }

    /// <summary>An authored axis-aligned box in prefab-root local coordinates, not an inferred mesh bound.</summary>
    public readonly struct BlueprintFootprintBox
    {
        public BlueprintFootprintBox(Vector3 center, Vector3 size) { Center = center; Size = size; }
        public Vector3 Center { get; }
        public Vector3 Size { get; }
    }

    public sealed class BuildingBookPage
    {
        internal BuildingBookPage(string mod, string id, BlueprintBookPages book, BlueprintBookPageData page)
        { ModId = mod; PageId = id; Book = book; Page = page; }
        public string ModId { get; }
        public string PageId { get; }
        internal BlueprintBookPages Book { get; }
        internal BlueprintBookPageData Page { get; }
    }
}
