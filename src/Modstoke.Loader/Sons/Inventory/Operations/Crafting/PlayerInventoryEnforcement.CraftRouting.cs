// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private CraftingSystem _craftSource;
        private IntPtr _craftSourceCounterData;
        private IntPtr _craftSourceCounter;
        private InventoryCraftRecipe _craftSourceRecipe;
        private ulong _craftUiRequest;
        private ulong _craftSourceFloor;
        private bool _craftUiPending, _craftPresentationAttempted;
        private NativeCraftRequestReceipt _craftSourceReceipt;
        private CraftEffectsSnapshot _craftSourceEffects;
        private int _craftNativeEntries;
        private readonly Dictionary<IntPtr, Il2CppSystem.Collections.IEnumerator> _craftContinuations = new();

        /// <summary>Include in the account/lifecycle busy gate; cancellation is not a terminal craft receipt.</summary>
        public bool HasPendingNativeCraftRoute => CraftVariantPending || _craftUiPending || _craftEffectsReplyPending || HasNativeCraftMatCustody || _craftNativeEntries != 0
            || _craftProxyEntries != 0 || _craftContinuations.Count != 0;
        /// <summary>Exclude new source action intents until effects settle; do not use this to veto reserved native grant/remove commands.</summary>
        public bool HasPendingNativeCraftPresentation => CraftVariantPending || _craftUiPending || _craftEffectsReplyPending || HasNativeCraftMatCustody;

        /// <summary>
        /// Bind the retained source UI while closed and quiescent, after hook installation.
        /// Mat ownership retains exact account keys across native storage/layout transfers.
        /// Inventory request transport owns custody; the bound effects transport separately
        /// projects and acknowledges counters and notifications on that same authenticated peer.
        /// </summary>
        public bool TryBindCraftingRoute(CraftingSystem crafting, out string detail)
        {
            detail = "Craft UI route needs installed native hooks, a closed quiescent retained inventory, and exact initialized crafting state.";
            if (!OnThread || !_hooksInstalled || !_craftHooksInstalled || _authority != null || !ReferenceEquals(_guard, this)
                || IsDisposed || IsFaulted || _busy || _writerDepth != 0 || _craftSource != null || !Current()
                || HasPendingCustody || crafting == null || crafting.WasCollected || !crafting || !crafting._isInitialized
                || CraftingSystem._Instance_k__BackingField?.Pointer != crafting.Pointer
                || _inventory._currentView == TheForest.Items.Inventory.PlayerInventory.PlayerViews.Inventory) return false;
            try
            {
                if (!Unchanged() || crafting._CurrentIngredients_k__BackingField == null
                    || crafting._CurrentIngredients_k__BackingField.Count != 0 || crafting._waitForCogTutorialCompletion != null
                    || crafting._playerState == null || crafting._playerState.WasCollected || !crafting._playerState
                    || crafting._playerState._data == null || crafting._playerState._data.WasCollected
                    || !crafting._playerState.transform.IsChildOf(LocalPlayer.GameObject.transform)
                    || (crafting._craftingAnimationEventsProxy != null && crafting._craftingAnimationEventsProxy.CurrentRecipe != null)) return false;
                BindCraftMatDispatcher();
                _craftSource = crafting;
                _craftSourceCounter = crafting._playerState.Pointer;
                _craftSourceCounterData = crafting._playerState._data.Pointer;
                BindCraftEffectTransport();
                detail = "Native craft UI, exact mat custody and authoritative animation/effects route bound.";
                return true;
            }
            catch (Exception error) { detail = "Craft UI binding failed: " + error.Message; return false; }
        }

        /// <summary>Read the UI request awaiting authoritative effects; custody already uses the inventory request transport.</summary>
        public bool TryReadCraftPresentationRequest(out ulong request)
        {
            request = 0;
            if (!OnThread || CraftVariantPending || !_craftUiPending || IsDisposed || IsFaulted || !CraftSourceCurrent()) return false;
            if (TryReadCraftRequestReceipt(_craftUiRequest, out var receipt))
            {
                if (receipt.State == NativeCraftRequestState.Rejected)
                {
                    _craftUiPending = false;
                    _craftSourceRecipe = null;
                    Detail = receipt.Detail;
                    return false;
                }
                if (receipt.State == NativeCraftRequestState.Unconfirmed)
                {
                    Fail(receipt.Detail);
                    return false;
                }
            }
            request = _craftUiRequest;
            return true;
        }

        // Receipts and effects must be decoded only from the server for this exact peer/plan.
        // Fixed-result recipes continue through the existing grant/removal transport.
        internal bool TryApplyCraftPresentation(ulong request, CraftEffectsSnapshot effects, out string detail)
        {
            detail = "Craft presentation requires final exact native custody and authoritative actor effects.";
            if (!OnThread || CraftVariantPending || IsDisposed || IsFaulted || _busy || _writerDepth != 0 || !CraftSourceCurrent()
                || request != _craftUiRequest || !TryReadCraftRequestReceipt(request, out var receipt)
                || receipt.State != NativeCraftRequestState.Applied
                || effects == null || effects.Transaction != receipt.Transaction || _craftSourceRecipe == null
                || effects.RecipeFingerprint != _craftSourceRecipe.Fingerprint) return false;
            if (!_craftUiPending) return receipt == _craftSourceReceipt && SameCraftEffects(effects, _craftSourceEffects);
            if (_craftAnimationStarted)
            {
                detail = "Authoritative custody is final; the original native animation callback is still pending.";
                return receipt == _craftSourceReceipt && SameCraftEffects(effects, _craftSourceEffects);
            }
            if (_craftPresentationAttempted || _craftSourceRecipe == null || !_craftSourceRecipe.Current()
                || receipt.Transaction <= _craftSourceFloor || receipt.Transaction == ulong.MaxValue
                || receipt.FirstNativeTransaction != receipt.Transaction + 1
                || _craftSourceRows == null
                || receipt.LastNativeTransaction < receipt.FirstNativeTransaction
                || receipt.LastNativeTransaction - receipt.Transaction != (ulong)(_craftSourceRows.Length + _craftSourceRecipe.Results.Length)
                || LastCompletedTransaction != receipt.LastNativeTransaction || HasPendingCustody || !Unchanged()
                || LastReceipt is not { Applied: true, IsGrant: true } last
                || last.Transaction != receipt.LastNativeTransaction || last.InstanceKey != receipt.LastNativeTransaction
                || last.Quantity != 1 || last.ItemId != _craftSourceRecipe.Results[^1]) return false;
            _busy = true;
            try
            {
                if (!CraftEffectsMatchBefore(effects)) return false;
                _craftSourceReceipt = receipt;
                _craftSourceEffects = effects;
                StartAuthoritativeCraftPresentation();
                detail = "Native combine/animation started after exact authoritative results; completion callback owns final presentation.";
                return !IsFaulted;
            }
            catch (Exception error)
            {
                detail = "Craft presentation is unconfirmed: " + error.Message;
                Fail(detail);
                return false;
            }
            finally { _busy = false; }
        }

        private static bool SameCraftEffects(CraftEffectsSnapshot a, CraftEffectsSnapshot b)
        {
            if (a == null || b == null || a.Transaction != b.Transaction || a.RecipeFingerprint != b.RecipeFingerprint
                || a.RecipeWasNew != b.RecipeWasNew || a.Counters.Count != b.Counters.Count) return false;
            for (int i = 0; i < a.Counters.Count; i++) if (a.Counters[i] != b.Counters[i]) return false;
            return true;
        }

        private bool CraftSourceCurrent()
            => _authority == null && _bridge?.IsGameplayActive == true && Current()
                && _craftSource != null && !_craftSource.WasCollected && _craftSource && _craftSource._isInitialized
                && CraftingSystem._Instance_k__BackingField?.Pointer == _craftSource.Pointer
                && (_craftSourceRecipe == null || _craftSource._recipeDatabase?.Pointer == _craftSourceRecipe.Database.Pointer)
                && _craftSource._playerState != null && !_craftSource._playerState.WasCollected && _craftSource._playerState
                && _craftSource._playerState.Pointer == _craftSourceCounter
                && _craftSource._playerState._data != null && !_craftSource._playerState._data.WasCollected
                && _craftSource._playerState._data.Pointer == _craftSourceCounterData;

        private void CaptureCraftIntent(CraftingRecipe selected = null)
        {
            if (CraftVariantPending || _craftUiPending || _busy || IsDisposed || IsFaulted || !IsArmed || HasPendingCustody
                || _craftContinuations.Count != 0 || !CraftSourceCurrent() || !Unchanged()
                || _craftSource._timeUntilNextAllowedCraft > 0 || _craftSource._inCreativeMode) return;
            if (selected == null && !_craftSource.HasSatisfiedRecipeWithCurrentIngredients(out selected)) return;
            if (selected == null || selected.WasCollected || !selected) return;
            var database = _craftSource._recipeDatabase;
            if (database == null || database.WasCollected || !database || database._recipes == null
                || database._recipes.WasCollected || database._recipes.Count is <= 0 or > 4096) return;
            int index = -1;
            for (int i = 0; i < database._recipes.Count; i++)
                if (database._recipes[i]?.Pointer == selected.Pointer) { index = i; break; }
            if (index < 0) return;
            var recipe = InventoryCraftRecipe.Capture(database, index, true);
            if (!CanPresentCraft(recipe)) return;
            var mat = _craftSource._CurrentIngredients_k__BackingField;
            if (mat == null || mat.WasCollected || mat.Count != recipe.Ingredients.Length) return;
            var required = new Dictionary<int, InventoryCraftRecipe.Ingredient>();
            foreach (var ingredient in recipe.Ingredients) required.Add(ingredient.Id, ingredient);
            foreach (var ingredient in mat)
                if (ingredient == null || ingredient.WasCollected
                    || !required.Remove(ingredient.ItemId, out var expected) || ingredient.Count != expected.Quantity
                    || ingredient.IsReusable != expected.Reusable) return;
            var selections = new List<CraftIngredient>();
            foreach (var held in _craftMat)
            {
                if (held.Debited || !held.Current()) return;
                int row = selections.FindIndex(item => item.InstanceKey == held.Source.Entry.Key);
                if (row < 0) selections.Add(new(held.Source.Entry.Key, 1));
                else selections[row] = selections[row] with { Quantity = selections[row].Quantity + 1 };
            }
            if (recipe.IsVariant)
            {
                BeginCraftVariant(recipe, selections.ToArray());
                Detail = "Retained-instance craft submitted for exact server reservation.";
                return;
            }
            if (!TryRequestCraft(index, selections, out _, out var detail))
            { Detail = detail; return; }
            Detail = "Native recipe intent entered the authenticated inventory request transport; custody and effects receipts are pending.";
        }
    }
}
