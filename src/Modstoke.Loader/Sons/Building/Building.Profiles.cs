// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting.Structures;
using global::Sons.Items.Core;
using SonsSdk.Building;
using SonsSdk.Networking;
using TheForest.Player.Actions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Modstoke.Sons
{
    /// <summary>
    /// An explicit caller-owned native graph, not a bag of flags copied onto a generic ghost.
    /// Author and retain the complete recipe, node, built prefab and dependencies before publication.
    /// Native placement handlers, colliders, ingredients, tree linkers, serializers and save modules
    /// must already be authored. This API does not synthesize them or distribute assets to peers.
    /// </summary>
    public sealed class BuildingNativeProfile
    {
        public BuildingNativeProfile(StructureRecipe recipe, StructureRecipe.PlaceModeType mode,
            GameObject nodePrefab, GameObject builtPrefab)
            : this(recipe, mode, nodePrefab, builtPrefab, null) { }
        public BuildingNativeProfile(StructureRecipe recipe, StructureRecipe.PlaceModeType mode,
            GameObject nodePrefab, GameObject builtPrefab, BuildingFreeFormGraph freeFormGraph)
        { Recipe = recipe; Mode = mode; NodePrefab = nodePrefab; BuiltPrefab = builtPrefab; FreeFormGraph = freeFormGraph; }
        public StructureRecipe Recipe { get; }
        public StructureRecipe.PlaceModeType Mode { get; }
        public GameObject NodePrefab { get; }
        public GameObject BuiltPrefab { get; }
        public BuildingFreeFormGraph FreeFormGraph { get; }
    }

    /// <summary>Managed observations around one lock-input call, not point acceptance or a built segment.</summary>
    /// <remarks>
    /// Counts include the live cursor. Null counts or continuity flags mean unavailable, not zero or unchanged.
    /// Continuity compares endpoints of this call only; reused operation objects are not session tokens.
    /// No native objects are retained. A changed count does not attribute the change to the native handler.
    /// </remarks>
    public sealed class PlacementPointReceipt
    {
        internal PlacementPointReceipt(int recipeId, bool attempted, bool returned, int? before, int? after,
            bool? operationUnchanged, bool? recipeUnchanged, bool? systemUnchanged, bool? worldUnchanged)
        {
            RecipeId = recipeId; NativeCallAttempted = attempted; NativeCallReturned = returned;
            BeforeNativePointCount = before; AfterNativePointCount = after;
            OperationUnchanged = operationUnchanged; RecipeUnchanged = recipeUnchanged;
            SystemUnchanged = systemUnchanged; WorldUnchanged = worldUnchanged;
        }
        public int RecipeId { get; }
        public bool NativeCallAttempted { get; }
        public bool NativeCallReturned { get; }
        public int? BeforeNativePointCount { get; }
        /// <summary>Read only after normal return with all four continuity observations unchanged.</summary>
        public int? AfterNativePointCount { get; }
        /// <summary>The same local placement action and active multi-point operation were observed.</summary>
        public bool? OperationUnchanged { get; }
        /// <summary>The same recipe object, ID and multi-point mode were observed.</summary>
        public bool? RecipeUnchanged { get; }
        /// <summary>The same crafting system and recipe database were observed.</summary>
        public bool? SystemUnchanged { get; }
        /// <summary>The loaded game scene, scene-event count and before-load count still match.</summary>
        public bool? WorldUnchanged { get; }
        /// <summary>True means changed, false means unchanged, null means no comparable counts.</summary>
        public bool? PointCountChanged => BeforeNativePointCount.HasValue && AfterNativePointCount.HasValue
            ? BeforeNativePointCount.Value != AfterNativePointCount.Value : null;
    }

    public static partial class Building
    {
        /// <summary>
        /// Publishes an explicitly authored Single, Multiple or Tree native profile without
        /// replacing its graph with the lean-to template. The recipe and used prefab IDs must
        /// already be assigned (node = recipe - 1, optional built = recipe). Native templates are borrowed,
        /// never destroyed or edited; keep the entire graph immutable/alive until shutdown.
        /// All peers need the same stable IDs and assets before loading saves that refer to them.
        /// A returned handle may retain partial publication after an exception; retry Prepare,
        /// not registration. Free-form/hybrid recipes require an explicit complete conversion graph;
        /// pure free-form recipes have no built prefab. They retain native database conversion.
        /// Free-form graphs require actual prefab assets, not persistent scene copies. For screw-only
        /// profiles, persistent source behaviours must be disabled and listed in a PrefabComponentEnabler;
        /// their root objects must remain active-self so normal native cloning can activate them.
        /// </summary>
        public static BuildingActionResult RegisterPreparedRuntimeBlueprint(
            string modId, string blueprintId, BuildingNativeProfile profile,
            out RuntimeBlueprint blueprint)
        {
            blueprint = null;
            if (!TryEnterNative(out var rejected)) return rejected;
            int recipeId = 0;
            bool attempted = false;
            RuntimeBlueprint candidate = null;
            string key = null;
            try
            {
                if (!TryNormalizeBlueprint(modId, blueprintId, out var mod, out var id, out var reason))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, reason);
                key = BlueprintKey(mod, id);
                if (NativeBlueprints.TryGetValue(key, out var existing))
                {
                    blueprint = existing;
                    return BuildingResult(BuildingActionStatus.Rejected, existing.RecipeId, false, "Runtime identity is already reserved.");
                }
                recipeId = profile != null && profile.Recipe ? profile.Recipe._id : 0;
                if (NativeBlueprints.Count >= MaxBlueprintDefinitionCount || !NativeProfileMatches(profile, recipeId))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false,
                        "Supply a complete immutable native profile with matching recipe, ingredient, conversion, collider, serializer and prefab identities.");
                if (CustomBlueprintManager.LoadedObjects.ContainsKey(recipeId) || CustomBlueprintManager.ProcessedObjects.ContainsKey(recipeId)
                    || (TryGetBuildingSystem(out var system) && system._recipeDatabase.TryGetRecipeById(recipeId, out _)))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Recipe ID is already in use.");
                foreach (var registered in CustomBlueprintManager.LoadedObjects.Keys)
                    if ((long)registered == (long)recipeId - 1 || (long)registered == (long)recipeId + 1)
                        return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Recipe and ghost network IDs overlap another blueprint.");
                candidate = new(mod, id, profile.Recipe._displayName,
                    new ScrewStructureRegistration(profile.BuiltPrefab ? profile.BuiltPrefab : profile.NodePrefab, recipeId, mod + "." + id + "Recipe"),
                    Array.Empty<BlueprintFootprintBox>()) { NativeProfile = profile, Recipe = profile.Recipe };
                NativeBlueprints.Add(key, candidate);
                attempted = true;
                CustomBlueprintManager.TryRegister(candidate.Registration);
                blueprint = candidate;
                return PrepareBlueprintCore(candidate, ref attempted);
            }
            catch (Exception exception)
            {
                if (candidate != null && IsBlueprintPublished(candidate)) blueprint = candidate;
                return BuildingResult(BuildingActionStatus.Failed, recipeId, attempted,
                    exception.Message + (blueprint != null ? " Publication remains reserved; retain assets and retry preparation with this handle." : ""));
            }
            finally
            {
                if (candidate != null && blueprint == null) NativeBlueprints.Remove(key);
                _nativeBusy = false;
            }
        }

        // Called by the shared registration driver BEFORE its generic template path, including
        // scene activation. A recognized profile must never fall through after failed preparation.
        internal static bool TryPrepareNativeProfileRegistration(ScrewStructureRegistration registration)
        {
            foreach (var blueprint in NativeBlueprints.Values)
            {
                if (blueprint.NativeProfile == null || !ReferenceEquals(blueprint.Registration, registration)) continue;
                if (!OnNativeThread) throw new InvalidOperationException("Native profiles require the initialized Unity thread.");
                PrepareNativeProfile(blueprint);
                return true;
            }
            return false;
        }

        private static void PrepareNativeProfile(RuntimeBlueprint blueprint)
        {
            blueprint.Prepared = false;
            var profile = blueprint.NativeProfile;
            if (!IsBlueprintPublished(blueprint) || !NativeProfileMatches(profile, blueprint.RecipeId))
                throw new InvalidOperationException("The published explicit native profile no longer matches its authored graph.");
            if (!ItemDatabaseManager._instance || !TryGetBuildingSystem(out var system)) return;
            if (!NativeFreeFormDependenciesReady(profile)) return;
            var recipe = profile.Recipe;
            if (system._recipeDatabase.TryGetRecipeById(blueprint.RecipeId, out var current) && current != recipe)
                throw new InvalidOperationException("The native recipe ID belongs to another recipe.");
            if (CustomBlueprintManager.ProcessedObjects.TryGetValue(blueprint.RecipeId, out var processed) && processed != recipe)
                throw new InvalidOperationException("The processed recipe ID belongs to another recipe.");
            // Partial publication is deliberately retained. Each retry uses exact identity and the
            // existing idempotent prefab driver; borrowed assets cannot safely be rolled back here.
            EntityManager.RegisterPrefab(profile.NodePrefab);
            if (profile.BuiltPrefab) EntityManager.RegisterPrefab(profile.BuiltPrefab);
            if (!current) system._recipeDatabase._recipes.Add(recipe);
            CustomBlueprintManager.ProcessedObjects[blueprint.RecipeId] = recipe;
            blueprint.Prepared = RuntimePrefabReady(profile.NodePrefab, blueprint.RecipeId - 1)
                && (!profile.BuiltPrefab || RuntimePrefabReady(profile.BuiltPrefab, blueprint.RecipeId));
        }

        private static bool NativeProfileMatches(BuildingNativeProfile profile, int id)
        {
            if (profile == null || id <= 1 || !profile.Recipe || !profile.NodePrefab
                || profile.NodePrefab == profile.BuiltPrefab || string.IsNullOrWhiteSpace(profile.Recipe._displayName)
                || (profile.Mode != StructureRecipe.PlaceModeType.Single && profile.Mode != StructureRecipe.PlaceModeType.Multiple
                    && profile.Mode != StructureRecipe.PlaceModeType.Tree)) return false;
            var recipe = profile.Recipe;
            if (recipe._id != id || recipe._boltPrefabId != id - 1 || recipe._placeMode != profile.Mode
                || recipe._structureNodePrefab != profile.NodePrefab || recipe._builtPrefab != profile.BuiltPrefab
                || (recipe._hybridFreeFormAndScrew && !recipe._useFreeFormStructures)
                || recipe._dynamicParentRecipeOverride || recipe._screwParentRecipeOverride || recipe._freeformParentRecipeOverride
                || !NativeProfilePrefab(profile.NodePrefab, id - 1, BoltFactories.StructureCraftingNodeState)
                || !NativeProfileBuiltMatches(profile, id) || !NativeFreeFormGraphMatches(profile)) return false;
            var node = profile.NodePrefab.GetComponent<StructureCraftingNode>();
            if (!node || node._recipe != recipe || node._pendingDestroy || !NativeProfileBehaviour(node)
                || node._craftingIngredientLinks == null || recipe._ingredients == null || recipe._ingredients.Count == 0
                || profile.NodePrefab.GetComponentsInChildren<Collider>(true).Length == 0) return false;
            if ((profile.Mode == StructureRecipe.PlaceModeType.Tree || recipe._allowsTreePlacement || recipe._isTreeStructure)
                && (!profile.NodePrefab.GetComponent<ScrewTreeLinker>()
                    || (profile.BuiltPrefab && !profile.BuiltPrefab.GetComponent<ScrewTreeLinker>()))) return false;
            var counts = new Dictionary<int, (int Count, int Disabled)>();
            foreach (var ingredient in recipe._ingredients)
            {
                if (ingredient == null || ingredient.ItemId <= 0 || ingredient.Count <= 0 || counts.ContainsKey(ingredient.ItemId)
                    || ingredient.DisabledCount < 0 || ingredient.DisabledCount > ingredient.Count
                    || (ItemDatabaseManager._instance && !ItemDatabaseManager.ItemById(ingredient.ItemId))) return false;
                counts.Add(ingredient.ItemId, (ingredient.Count, ingredient.DisabledCount));
            }
            foreach (var link in node._craftingIngredientLinks)
            {
                if (link == null || link.Ingredient == null || !counts.Remove(link.Ingredient.ItemId, out var count)
                    || count.Count != link.Ingredient.Count || count.Disabled != link.Ingredient.DisabledCount
                    || link.TotalCount != count.Count - count.Disabled || link.AppliedCount != 0) return false;
            }
            return counts.Count == 0;
        }

        private static bool NativeProfilePrefab(GameObject prefab, int id, string serializer)
        {
            if (!prefab.activeSelf || prefab.transform.parent || prefab.transform.localScale != Vector3.one) return false;
            if (prefab.scene.IsValid() && prefab.scene.name != "DontDestroyOnLoad") return false;
            var entity = prefab.GetComponent<BoltEntity>();
            return entity && !entity.isAttached && entity._prefabId == id
                && string.Equals(entity._serializerGuid, serializer, StringComparison.OrdinalIgnoreCase);
        }

        private static bool NativeProfileBehaviour(MonoBehaviour behaviour)
        {
            if (!behaviour.gameObject.scene.IsValid()) return behaviour.enabled;
            // Match the supported SDK template lifecycle: persistent source behaviours stay
            // disabled, and the authored enabler activates only instantiated scene clones.
            var enabler = behaviour.GetComponent<SonsSdk.UnityUtils.PrefabComponentEnabler>();
            var components = enabler ? enabler.ComponentsToEnable.Get() : null;
            return !behaviour.enabled && components != null && components.Contains(behaviour);
        }

        /// <summary>Locks the current multi-point cursor through the native operation's handler.</summary>
        /// <remarks>
        /// No point coordinates, collision flags or ingredient state are written. The
        /// expected operation must come from TryReadSpecializedPlacement. Native operation objects
        /// can be reused and are not session tokens. Submitted is an input attempt, not a placed segment.
        /// </remarks>
        public static BuildingActionResult LockCurrentPlacementPoint(int expectedRecipeId,
            PlayerStructurePlaceAction.IPlaceOperation expectedOperation)
            => LockCurrentPlacementPoint(expectedRecipeId, expectedOperation, out _);

        /// <summary>Submits the same native lock input and captures bounded, synchronous count evidence.</summary>
        /// <remarks>
        /// Call on the initialized Unity thread with an operation from TryReadSpecializedPlacement.
        /// Observation failures do not reject input. Native exceptions retain pre-call evidence, but
        /// leave post-call evidence unavailable; side effects may have occurred, so do not blindly retry.
        /// </remarks>
        public static BuildingActionResult LockCurrentPlacementPoint(int expectedRecipeId,
            PlayerStructurePlaceAction.IPlaceOperation expectedOperation, out PlacementPointReceipt receipt)
        {
            receipt = null;
            if (!TryEnterNative(out var rejected))
            {
                receipt = new(expectedRecipeId, false, false, null, null, null, null, null, null);
                return rejected;
            }
            bool attempted = false;
            bool returned = false;
            int? before = null, after = null;
            bool? operationUnchanged = null, recipeUnchanged = null, systemUnchanged = null, worldUnchanged = null;
            try
            {
                if (expectedOperation == null || !TryGetLocalPlacement(out var action, out var system) || !action.InPlacementMode
                    || action._activePlaceOperation == null || action._activePlaceOperation.Pointer != expectedOperation.Pointer
                    || !action._activePlaceOperation.Recipe || action._activePlaceOperation.Recipe._id != expectedRecipeId
                    || action._activePlaceOperation.Recipe._placeMode != StructureRecipe.PlaceModeType.Multiple)
                    return BuildingResult(BuildingActionStatus.Rejected, expectedRecipeId, false, "The exact multi-point operation is no longer active.");
                var multiple = action._activePlaceOperation.TryCast<PlayerStructurePlaceAction.MultiplePlaceOperation>();
                if (multiple == null || !multiple.CanLockPoint())
                    return BuildingResult(BuildingActionStatus.Rejected, expectedRecipeId, false, "The native multi-point operation cannot lock its current target.");
                before = ReadPlacementPointCount(multiple);
                bool contextAvailable = false;
                IntPtr recipePointer = IntPtr.Zero, databasePointer = IntPtr.Zero;
                int loadCount = 0, sceneEvents = 0, sceneHandle = 0;
                try
                {
                    recipePointer = multiple.Recipe.Pointer;
                    databasePointer = system._recipeDatabase.Pointer;
                    loadCount = SaveLifecycle.State.BeforeLoadCount;
                    var state = GameRuntime.State;
                    sceneEvents = state.SceneEventCount;
                    var scene = SceneManager.GetSceneByName(state.SceneName);
                    sceneHandle = scene.handle;
                    contextAvailable = GameRuntime.IsGameScene && scene.IsValid() && scene.isLoaded;
                }
                catch { /* Optional evidence must not prevent the native input. */ }
                // Pinned native LockPoint ignores CallbackContext and delegates to its custom handler.
                attempted = true;
                multiple.LockPoint(new InputAction.CallbackContext());
                returned = true;
                if (contextAvailable)
                {
                    try
                    {
                        var state = GameRuntime.State;
                        var scene = SceneManager.GetSceneByName(state.SceneName);
                        worldUnchanged = GameRuntime.IsGameScene && scene.IsValid() && scene.isLoaded
                            && scene.handle == sceneHandle && state.SceneEventCount == sceneEvents
                            && SaveLifecycle.State.BeforeLoadCount == loadCount;
                        if (TryGetLocalPlacement(out var currentAction, out var currentSystem))
                        {
                            var current = currentAction._activePlaceOperation;
                            operationUnchanged = currentAction == action && currentAction.InPlacementMode
                                && current != null && current.Pointer == multiple.Pointer;
                            var recipe = current?.Recipe;
                            recipeUnchanged = recipe && recipe.Pointer == recipePointer && recipe._id == expectedRecipeId
                                && recipe._placeMode == StructureRecipe.PlaceModeType.Multiple;
                            var database = currentSystem._recipeDatabase;
                            systemUnchanged = currentSystem == system && database && database.Pointer == databasePointer;
                            if (operationUnchanged == true && recipeUnchanged == true
                                && systemUnchanged == true && worldUnchanged == true)
                                after = ReadPlacementPointCount(multiple);
                        }
                    }
                    catch { /* A returned input remains submitted when post-call evidence is unreadable. */ }
                }
                return BuildingResult(BuildingActionStatus.Submitted, expectedRecipeId, true, "Native lock-point input returned. The custom handler can still decline the point.");
            }
            catch (Exception exception) { return BuildingResult(BuildingActionStatus.Failed, expectedRecipeId, attempted, exception.Message); }
            finally
            {
                _nativeBusy = false;
                receipt = new(expectedRecipeId, attempted, returned, before, after,
                    operationUnchanged, recipeUnchanged, systemUnchanged, worldUnchanged);
            }
        }

        private static int? ReadPlacementPointCount(PlayerStructurePlaceAction.MultiplePlaceOperation operation)
        {
            try
            {
                // One count read per side, without scanning or copying the native point collection.
                var points = operation._points;
                if (points == null) return null;
                int count = points.Count;
                return count >= 0 ? count : null;
            }
            catch { return null; }
        }

        /// <summary>Reads one current operation without scanning or retaining it internally. Native point count includes the live cursor.</summary>
        public static bool TryReadSpecializedPlacement(out PlayerStructurePlaceAction.IPlaceOperation operation,
            out StructureRecipe.PlaceModeType mode, out int nativePointCount, out bool canLock, out bool canPlace)
        {
            operation = null; mode = default; nativePointCount = 0; canLock = false; canPlace = false;
            if (!OnNativeThread) return false;
            try
            {
                if (!TryGetLocalPlacement(out var action, out _) || !action.InPlacementMode || action._activePlaceOperation == null
                    || !action._activePlaceOperation.Recipe) return false;
                var current = action._activePlaceOperation;
                mode = current.Recipe._placeMode;
                canPlace = current.HasValidPlacementTarget;
                if (mode == StructureRecipe.PlaceModeType.Multiple)
                {
                    var multiple = current.TryCast<PlayerStructurePlaceAction.MultiplePlaceOperation>();
                    if (multiple == null || multiple._points == null) return false;
                    nativePointCount = multiple._points.Count;
                    canLock = multiple.CanLockPoint();
                }
                else if (mode != StructureRecipe.PlaceModeType.Tree && mode != StructureRecipe.PlaceModeType.Single) return false;
                operation = current;
                return true;
            }
            catch { return false; }
        }
    }
}
