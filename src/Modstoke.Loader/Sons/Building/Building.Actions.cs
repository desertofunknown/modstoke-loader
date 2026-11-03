// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Crafting.Structures;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Weapon;
using TheForest.Player.Actions;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        /// <summary>
        /// Starts ordinary interactive ghost placement for a current native recipe. It does not
        /// place a structure. Native input, collision/snap checks, ingredients and Bolt authority
        /// remain in charge. The player must be in world view, not busy, with the book closed.
        /// </summary>
        public static BuildingActionResult BeginRecipePlacement(int recipeId)
        {
            if (!TryEnterNative(out var rejected)) return rejected;
            bool attempted = false;
            try
            {
                if (!TryGetLocalPlacement(out var action, out var system))
                    return BuildingResult(BuildingActionStatus.Unavailable, recipeId, false, "The local native placement action is not ready.");
                if (action.InPlacementMode || system.InBuildMode || LocalPlayer.IsInMidAction)
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "The player is already placing, constructing or performing another action.");
                if (!system._recipeDatabase.TryGetRecipeById(recipeId, out var recipe) || !recipe
                    || !recipe._structureNodePrefab || ((!recipe._useFreeFormStructures || recipe._hybridFreeFormAndScrew) && !recipe._builtPrefab))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "The requested native recipe and prefabs are not available.");
                foreach (var blueprint in NativeBlueprints.Values)
                    if (blueprint.RecipeId == recipeId && !TryGetRuntimeBlueprintRecipe(blueprint, out _))
                        return BuildingResult(BuildingActionStatus.Unavailable, recipeId, false, "The registered blueprint has not finished native preparation in this session.");
                var creative = GameSetupManager.GetSetting("GameSetting.Survival.CreativeMode", false);
                if (!BlueprintBookController.CheckPageIsDiscovered(recipe, creative, out _))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "The native recipe has not been discovered for this game mode.");

                attempted = true;
                action.EnterPlacementMode(recipe, false, false, Vector3.forward);
                if (!action || !action.InPlacementMode || action._activePlaceOperation == null
                    || action._activePlaceOperation.Recipe != recipe)
                    return BuildingResult(BuildingActionStatus.Unavailable, recipeId, true, "Native placement entry returned without the requested active recipe.");
                return BuildingResult(BuildingActionStatus.PlacementStarted, recipeId, true,
                    "Native interactive placement started. Use normal input or an explicit current-placement action to continue.");
            }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, recipeId, attempted,
                    exception.Message + (attempted ? " Native placement may have partially started; no automatic retry or rollback was performed." : ""));
            }
            finally { _nativeBusy = false; }
        }

        /// <summary>Begins placement only if the exact runtime registration still owns the current recipe.</summary>
        public static BuildingActionResult BeginBlueprintPlacement(RuntimeBlueprint blueprint)
        {
            if (!TryGetRuntimeBlueprintRecipe(blueprint, out var recipe))
                return BuildingResult(BuildingActionStatus.Unavailable, blueprint?.RecipeId ?? 0, false, "Runtime blueprint is not ready on the Unity thread.");
            return BeginRecipePlacement(recipe._id);
        }

        /// <summary>
        /// Reads the current local native operation, including dynamic recipe overrides. No action
        /// or Unity reference is retained across calls; expectedRecipeId on mutations is a guard,
        /// not a claim that the SDK owns the player's placement session.
        /// </summary>
        public static bool TryGetCurrentPlacement(out int recipeId, out bool hasValidTarget)
        {
            recipeId = 0;
            hasValidTarget = false;
            if (!OnNativeThread) return false;
            try
            {
                if (!TryGetLocalPlacement(out var action, out _) || !action.InPlacementMode
                    || action._activePlaceOperation == null || !action._activePlaceOperation.Recipe)
                    return false;
                recipeId = action._activePlaceOperation.Recipe._id;
                hasValidTarget = action._activePlaceOperation.HasValidPlacementTarget;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Executes the native confirm path for the current valid ghost. Submitted means the native
        /// call returned, not that a structure exists or a server accepted it. Multi-point recipes
        /// still require their normal native lock-point input before they have a valid target.
        /// </summary>
        public static BuildingActionResult SubmitCurrentPlacement(int expectedRecipeId)
            => ActOnCurrentPlacement(expectedRecipeId, false);

        /// <summary>
        /// Submits the exact current operation and retains only nodes observed inside matching native
        /// placement calls. Does not wait, scan, or correlate later client/server messages by pose.
        /// The receipt can contain partial effects even when the action fails. Empty results do not
        /// establish rejection. Dispose is unnecessary; the receipt installs no per-instance listeners.
        /// The expected operation must be non-null; native operation objects are reusable, not session tokens.
        /// </summary>
        public static BuildingActionResult SubmitCurrentPlacement(int expectedRecipeId,
            PlayerStructurePlaceAction.IPlaceOperation expectedOperation, out PlacementReceipt receipt)
        {
            receipt = null;
            return expectedOperation == null
                ? BuildingResult(BuildingActionStatus.Rejected, expectedRecipeId, false, "An exact current native operation is required for observed submission.")
                : ActOnCurrentPlacement(expectedRecipeId, false, out receipt, expectedOperation);
        }

        /// <summary>Runs native cancellation/cleanup for the current expected recipe, including normal book return behavior.</summary>
        public static BuildingActionResult CancelCurrentPlacement(int expectedRecipeId)
            => ActOnCurrentPlacement(expectedRecipeId, true);

        private static BuildingActionResult ActOnCurrentPlacement(int expectedRecipeId, bool cancel)
            => ActOnCurrentPlacement(expectedRecipeId, cancel, out _, null);

        private static BuildingActionResult ActOnCurrentPlacement(int expectedRecipeId, bool cancel,
            out PlacementReceipt receipt, PlayerStructurePlaceAction.IPlaceOperation expectedOperation)
        {
            receipt = null;
            if (!TryEnterNative(out var rejected)) return rejected;
            bool attempted = false;
            try
            {
                if (!TryGetLocalPlacement(out var action, out var system) || !action.InPlacementMode)
                    return BuildingResult(BuildingActionStatus.Unavailable, expectedRecipeId, false, "There is no current local placement.");
                var operation = action._activePlaceOperation;
                if (operation == null || !operation.Recipe || operation.Recipe._id != expectedRecipeId)
                    return BuildingResult(BuildingActionStatus.Rejected, expectedRecipeId, false, "The current native placement recipe no longer matches.");
                if (expectedOperation != null && operation.Pointer != expectedOperation.Pointer)
                    return BuildingResult(BuildingActionStatus.Rejected, expectedRecipeId, false, "The native placement operation was replaced.");
                if (!cancel && !operation.HasValidPlacementTarget)
                    return BuildingResult(BuildingActionStatus.Rejected, expectedRecipeId, false, "Native placement has no valid target.");
                if (expectedOperation != null)
                {
                    if (!EnsurePlacementObservationHooks(out var detail))
                        return BuildingResult(BuildingActionStatus.Unavailable, expectedRecipeId, false, detail);
                    receipt = new PlacementReceipt(system, operation.Recipe, _nativeThread);
                    _placementReceipt = receipt;
                }
                attempted = true;
                // OnPlaceStructureNode checks the operation again, dispatches its native placement
                // and exits through the normal path. Never call PlaceStructureNode with a made-up pose.
                if (cancel) action.OnCancelPlacement();
                else action.OnPlaceStructureNode();
                if (receipt != null) receipt.SubmitReturned = true;
                return BuildingResult(cancel && action && !action.InPlacementMode
                        ? BuildingActionStatus.PlacementCancelled : BuildingActionStatus.Submitted,
                    expectedRecipeId, true, cancel
                        ? "Native cancellation returned; no construction completion was requested."
                        : "Native placement was submitted. World creation and multiplayer acceptance are not confirmed by this result.");
            }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, expectedRecipeId, attempted,
                    exception.Message + (attempted ? " Native side effects may have occurred; do not blindly retry." : ""));
            }
            finally { _placementReceipt = null; _nativePlacementScope = null; _nativeBusy = false; }
        }

        /// <summary>
        /// Requests the next ingredient through the local player's native crafting action. The
        /// caller must pass the exact node already selected by ordinary game interaction. The API
        /// never selects distant nodes, grants inventory, changes cheat settings or writes progress.
        /// Vanilla inventory/held-log conversion, animation and multiplayer request paths are used.
        /// Submitted does not guarantee that an ingredient was accepted or consumed.
        /// </summary>
        public static BuildingActionResult SubmitConstructionIngredient(StructureCraftingNode expectedNode)
            => ActOnCurrentConstruction(expectedNode, false);

        /// <summary>Cancels the exact currently selected construction using native refunds/networking and build-mode cleanup.</summary>
        public static BuildingActionResult CancelCurrentConstruction(StructureCraftingNode expectedNode)
            => ActOnCurrentConstruction(expectedNode, true);

        private static BuildingActionResult ActOnCurrentConstruction(StructureCraftingNode expectedNode, bool cancel)
        {
            if (!TryEnterNative(out var rejected)) return rejected;
            bool attempted = false;
            int recipeId = 0;
            try
            {
                if (!LocalBuildingPlayerReady() || !TryGetBuildingSystem(out var system)
                    || !expectedNode || !expectedNode.isActiveAndEnabled || expectedNode._pendingDestroy
                    || system.ActiveStructureNode != expectedNode || !system.InBuildMode)
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "The expected live node is not the player's current native construction target.");
                var action = LocalPlayer.SpecialActions.GetComponentInChildren<PlayerStructureCraftingAction>();
                if (!action || !action.isActiveAndEnabled || !action.InBuildMode
                    || action._structureCraftingSystem != system || action._inventory != LocalPlayer.Inventory
                    || LocalPlayer.IsInMidAction)
                    return BuildingResult(BuildingActionStatus.Unavailable, 0, false, "The local native crafting action is unavailable or busy.");
                recipeId = expectedNode._recipe ? expectedNode._recipe._id : 0;
                if (BoltNetwork.isRunning && (!expectedNode.entity || !expectedNode.entity.isAttached))
                    return BuildingResult(BuildingActionStatus.Unavailable, recipeId, false, "The construction node is not attached to the current multiplayer session.");
                // These native input receivers do not read CallbackContext. Supply a real boxed
                // zero context, not a null generated value-type wrapper, and retain native logic.
                var context = new InputAction.CallbackContext();
                attempted = true;
                if (cancel) action.OnCancelStructure(context);
                else action.OnAddIngredientToStructureAction(context);
                return BuildingResult(BuildingActionStatus.Submitted, recipeId, true,
                    cancel ? "Native cancellation/refund request submitted; authoritative removal is not confirmed."
                        : "Native ingredient action submitted; progress, inventory consumption and completion are not confirmed.");
            }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, recipeId, attempted,
                    exception.Message + (attempted ? " Native side effects may have occurred; do not blindly retry." : ""));
            }
            finally { _nativeBusy = false; }
        }

        private static bool LocalBuildingPlayerReady()
            => OnNativeThread && !GameRuntime.Environment.IsDedicatedServer
                && LocalPlayer.PlayerBase && LocalPlayer.Inventory && LocalPlayer.SpecialActions
                && LocalPlayer.AnimControl && LocalPlayer.IsInWorld && !global::Sons.Gui.PauseMenu.IsActive
                && !PlayerInventoryEnforcement.IsEquipmentAuthorityHeld(LocalPlayer.Inventory)
                && (!BoltNetwork.isRunning || LocalPlayer.Entity && LocalPlayer.Entity.isAttached && LocalPlayer.Entity.isOwner);

        private static bool TryGetLocalPlacement(out PlayerStructurePlaceAction action, out StructureCraftingSystem system)
        {
            action = null;
            system = null;
            if (!LocalBuildingPlayerReady() || !TryGetBuildingSystem(out system)) return false;
            action = LocalPlayer.SpecialActions.GetComponentInChildren<PlayerStructurePlaceAction>();
            return action && action.isActiveAndEnabled && action._inventory == LocalPlayer.Inventory
                && action._structureCraftingSystem == system;
        }

        private static bool CanUseNativePlacement()
        {
            if (!OnNativeThread) return false;
            try { return TryGetLocalPlacement(out _, out _); }
            catch { return false; }
        }
    }
}
