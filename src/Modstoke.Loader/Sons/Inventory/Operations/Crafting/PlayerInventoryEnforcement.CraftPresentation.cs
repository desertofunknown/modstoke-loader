// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private bool _craftAnimationStarted, _craftStartingNative, _craftBeginPermit, _craftCompleting;
        private bool _craftResultsPlaced, _craftIngredientsCleared, _craftRevealed;
        private int _craftProxyEntries;

        private CraftingResultLayoutItemGroup FindCraftResultGroup(int id)
        {
            CraftingResultLayoutItemGroup found = null;
            if (_craftSource._craftingResultLayoutItemGroups == null) return null;
            foreach (var group in _craftSource._craftingResultLayoutItemGroups)
            {
                if (group == null || group.WasCollected || !group || group._itemId != id) continue;
                if (found != null) return null;
                found = group;
            }
            return found;
        }

        private bool CanPresentCraft(InventoryCraftRecipe recipe)
        {
            if (_craftResults.Count != 0 || !CraftMatCurrent()) return false;
            // Native TryCombine first stashes every result group, not just this recipe's.
            var groups = _craftSource._craftingResultLayoutItemGroups;
            if (groups == null || groups.WasCollected) return false;
            foreach (var group in groups)
            {
                if (group == null || group.WasCollected || !group || group._layoutItems == null || group._layoutItems.WasCollected) return false;
                foreach (var layout in group._layoutItems)
                    if (layout == null || layout.WasCollected || !layout || layout.gameObject.activeSelf) return false;
            }
            // Variant output identities/counts are committed by the server. In particular,
            // unavailable dismantle candidates and container probabilities are not outputs.
            if (recipe.IsVariant) return true;
            var counts = new Dictionary<int, int>();
            foreach (int id in recipe.Results)
            {
                counts.TryGetValue(id, out int count); counts[id] = count + 1;
            }
            foreach (var pair in counts)
            {
                var group = FindCraftResultGroup(pair.Key);
                if (group?._layoutItems == null || group._layoutItems.WasCollected || group._layoutItems.Count < pair.Value) return false;
                foreach (var layout in group._layoutItems)
                    if (layout == null || layout.WasCollected || !layout || layout.gameObject.activeSelf) return false;
            }
            return true;
        }

        private bool CraftEffectsMatchBefore(CraftEffectsSnapshot effects)
        {
            if (CraftVariantPending) return _craftVariant.Prepared && VariantNativeResultsCurrent()
                && VariantCountersMatch(_craftVariant.Plan, false);
            var totals = new Dictionary<int, int>();
            foreach (int id in _craftSourceRecipe.Results) { totals.TryGetValue(id, out int n); totals[id] = n + 1; }
            if (effects.Counters.Count != totals.Count) return false;
            foreach (var counter in effects.Counters)
                if (!totals.Remove(counter.ItemId, out int amount) || counter.Before < 0
                    || (long)counter.After - counter.Before != amount
                    || _craftSource._playerState.GetCrafted(counter.ItemId) != counter.Before) return false;
            return _craftSource._playerState.Get("isNewCraftingRecipe_" + _craftSourceRecipe.Name, false) == effects.RecipeWasNew;
        }

        private void StartAuthoritativeCraftPresentation()
        {
            if (_craftAnimationStarted || _craftPresentationAttempted || !CanPresentCraft(_craftSourceRecipe)
                || _craftDebitIndex != _craftSourceRows.Length || !_craftMat.TrueForAll(item => item.Debited))
                throw new InvalidOperationException("Craft presentation lacks exact consumed mat custody or result layouts.");
            if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out var holdDetail))
                throw new InvalidOperationException("Accepted crafting could not yield the source body hold: " + holdDetail);
            // Grant receipts are already final. Move those same keys out of storage for the
            // native result mat, without constructing or requesting another authoritative result.
            for (int i = 0; i < _craftSourceRecipe.Results.Length; i++)
            {
                ulong key = _craftSourceReceipt.FirstNativeTransaction + (ulong)_craftSourceRows.Length + (ulong)i;
                var source = _sourceItems.Find(item => item.Entry.Key == key);
                if (source == null || source.Quantity != 1 || !Stowed(source.Entry)
                    || source.Entry.ItemId != _craftSourceRecipe.Results[i])
                    throw new InvalidOperationException("Craft result grant key is absent or differs.");
                var native = source.Instance ?? new ItemInstance(source.Entry.ItemId);
                var result = new CraftMatItem(source, native, true);
                result.Capture();
                _craftResults.Add(result); // Retain custody even if a native removal partially fails.
                int index = FindStoredEntry(_currentSnapshot, source.Entry.ItemId, source.Instance);
                if (index < 0) throw new InvalidOperationException("Craft result is not in exact native storage.");
                _permitId = source.Entry.ItemId;
                _permitQuantity = 1;
                _permitInstance = source.Instance?.Pointer ?? IntPtr.Zero;
                _permit = true;
                bool removed;
                try
                {
                    removed = source.Instance != null ? _manager.TryRemoveItem(source.Instance)
                        : _manager.RemoveItems(source.Entry.ItemId, 1, false) == 1;
                    if (_permit) throw new InvalidOperationException("Craft result removal permit was not observed.");
                }
                finally { _permit = false; }
                var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
                if (!removed || !_currentSnapshot.ConfirmsRemoval(after, index, 1, source.Instance) || !result.Current())
                    throw new InvalidOperationException("Craft result mat transfer is uncertain.");
                _currentSnapshot = after;
            }
            if (!_craftSource.HasSatisfiedRecipeWithCurrentIngredients(out var selected)
                || selected?.Pointer != _craftSourceRecipe.Recipe.Pointer || !_craftSourceRecipe.Current())
                throw new InvalidOperationException("Native recipe selection changed after authoritative custody.");
            _craftAnimationStarted = _craftStartingNative = _craftBeginPermit = true;
            try
            {
                if (!_craftSource.TryCombineIngredients() || _craftBeginPermit)
                    throw new InvalidOperationException("Native combine did not enter its approved animation path.");
            }
            finally { _craftStartingNative = _craftBeginPermit = false; }
        }

        private void PlaceAuthoritativeCraftResults()
        {
            if (!_craftCompleting || _craftResultsPlaced) throw new InvalidOperationException("Craft result placement was repeated.");
            _craftResultsPlaced = true;
            foreach (var result in _craftResults)
            {
                var group = FindCraftResultGroup(result.Source.Entry.ItemId);
                if (group == null || !result.Current() || !group.TryAddItem(result.Native, result.Source.Entry.ItemId, 1))
                    throw new InvalidOperationException("Native result layout rejected its exact granted instance.");
                foreach (var layout in group._layoutItems)
                    if (layout != null && !layout.WasCollected && layout && layout.gameObject.activeSelf
                        && layout.ItemInstance?.Pointer == result.Native.Pointer)
                    {
                        if (result.Layout != null) throw new InvalidOperationException("Result instance appeared in multiple layouts.");
                        result.Layout = layout;
                    }
                if (result.Layout == null) throw new InvalidOperationException("Native result layout lost its exact instance.");
                result.Placed = true;
            }
        }

        private void ClearConsumedCraftIngredients()
        {
            if (!_craftCompleting || !_craftResultsPlaced || _craftIngredientsCleared)
                throw new InvalidOperationException("Craft ingredient visual consumption is out of order.");
            _craftIngredientsCleared = true;
            foreach (var row in _craftSourceRecipe.Ingredients) RemoveCraftMatVisual(row.Id, row.Quantity);
            _craftMat.Clear(); // All exact native debits were acknowledged before animation began.
        }

        private readonly record struct CraftCompletionState(PlayerInventoryEnforcement Owner, bool Ran, bool Busy);

        private static bool BeforeAuthoritativeCraftComplete(CraftingSystem __instance, CraftingRecipe __0,
            bool __runOriginal, out CraftCompletionState __state)
        {
            bool allowed = EnterCraft(__instance, __runOriginal, out var owner, out bool protectedSource);
            __state = new(owner, false, owner?._busy ?? false);
            if (!protectedSource) return allowed;
            if (!allowed || !owner._craftUiPending || !owner._craftAnimationStarted || owner._craftPresentationAttempted
                || __0?.Pointer != owner._craftSourceRecipe?.Recipe.Pointer
                || (owner._craftProxyEntries != 1 && !(owner._craftStartingNative && !__0.HasCraftAnimation))) return false;
            try
            {
                if (!owner.CraftSourceCurrent() || !owner._craftSourceRecipe.Current() || !owner.CraftMatCurrent()
                    || !owner.Unchanged() || !owner.CraftEffectsMatchBefore(owner._craftSourceEffects))
                    throw new InvalidOperationException("Native completion no longer matches authoritative effects and custody.");
                var rows = __0.GenerateResultingItems();
                var expected = owner.CraftVariantPending ? owner._craftVariant.Plan.CounterResults : owner._craftSourceRecipe.Results;
                if (rows == null || rows.Count != expected.Length)
                    throw new InvalidOperationException("Native completion result count differs from the reserved recipe.");
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i]?.Id != expected[i]) throw new InvalidOperationException("Native completion result order differs.");
                owner._craftPresentationAttempted = owner._craftCompleting = owner._busy = true;
                __state = __state with { Ran = true };
                return true; // Native counters, ItemCrafted, OnCraftComplete, audio and cooldown, exactly once.
            }
            catch (Exception error) { owner.Fail(error.Message); return false; }
        }

        private static Exception AfterAuthoritativeCraftComplete(bool __runOriginal, Exception __exception, CraftCompletionState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                if (__state.Ran)
                {
                    if (!__runOriginal || __exception != null || owner.IsFaulted || !owner._craftResultsPlaced || !owner._craftIngredientsCleared
                        || !owner._craftResults.TrueForAll(item => item.Placed)
                        || !owner.CraftSourceCurrent() || !owner.CraftMatCurrent() || !owner.Unchanged()
                        || !owner._craftSourceRecipe.Current()
                        || owner._craftSource._playerState.Get("isNewCraftingRecipe_" + owner._craftSourceRecipe.Name, true)
                        || owner._craftSource._craftingAnimationEventsProxy?.CurrentRecipe != null)
                        throw new InvalidOperationException("Native craft completion did not confirm its entire visual/effect path.");
                    foreach (var counter in owner._craftSourceEffects.Counters)
                        if (owner._craftSource._playerState.GetCrafted(counter.ItemId) != counter.After)
                            throw new InvalidOperationException("Native craft counter differs from its authoritative effect receipt.");
                    owner._craftUiPending = false;
                    owner._craftEffectsReplyPending = true;
                    owner.Detail = "Native crafting animation completed once; exact results remain on the mat until collected.";
                }
            }
            catch (Exception error) { owner.Fail(error.Message); }
            finally
            {
                if (__state.Ran) { owner._craftCompleting = false; owner._busy = __state.Busy; }
                owner._craftNativeEntries--;
                if (__exception != null) owner.Fail("Native crafting completion threw: " + __exception.Message);
            }
            return __exception;
        }

        private static bool BeforeCraftResults(CraftingSystem __instance, CraftingRecipe __0, bool __runOriginal)
        {
            var owner = CraftOwner(__instance);
            if (owner == null) return __runOriginal;
            if (__runOriginal && !owner.IsFaulted && owner._craftCompleting && __0?.Pointer == owner._craftSourceRecipe?.Recipe.Pointer)
            {
                try
                {
                    if (owner.CraftVariantPending) owner.PlaceSourceVariant();
                    else owner.PlaceAuthoritativeCraftResults();
                }
                catch (Exception error) { owner.Fail("Native craft result placement failed: " + error.Message); }
            }
            return false;
        }

        private static bool BeforeCraftConsume(CraftingSystem __instance, CraftingRecipe __0, bool __runOriginal)
        {
            var owner = CraftOwner(__instance);
            if (owner == null) return __runOriginal;
            if (__runOriginal && !owner.IsFaulted && owner._craftCompleting && __0?.Pointer == owner._craftSourceRecipe?.Recipe.Pointer)
            {
                try
                {
                    if (owner.CraftVariantPending) owner.ClearSourceVariantIngredients();
                    else owner.ClearConsumedCraftIngredients();
                }
                catch (Exception error) { owner.Fail("Native craft ingredient completion failed: " + error.Message); }
            }
            return false;
        }

        private static bool BeforeCraftProxy(CraftingAnimationEventsProxy __instance, bool __runOriginal,
            out PlayerInventoryEnforcement __state)
        {
            __state = null;
            var owner = CraftOwner(_guard?._craftSource);
            if (owner == null || owner._craftSource._craftingAnimationEventsProxy?.Pointer != __instance.Pointer) return __runOriginal;
            if (!__runOriginal || !owner.OnThread || owner.IsFaulted || owner.IsDisposed || owner._busy
                || !owner._craftUiPending || !owner._craftAnimationStarted || owner._craftProxyEntries != 0
                || __instance.CurrentRecipe?.Pointer != owner._craftSourceRecipe?.Recipe.Pointer) return false;
            owner._craftProxyEntries++;
            __state = owner;
            return true;
        }

        private static Exception AfterCraftProxy(Exception __exception, PlayerInventoryEnforcement __state)
        {
            if (__state != null)
            {
                __state._craftProxyEntries--;
                if (__exception != null) __state.Fail("Native animation callback threw: " + __exception.Message);
            }
            return __exception;
        }

        private static bool BeforeCraftReveal(CraftingSystem __instance, CraftingRecipe __0, bool __runOriginal)
        {
            var owner = CraftOwner(__instance);
            if (owner == null) return __runOriginal;
            if (!__runOriginal || !owner.OnThread || owner.IsDisposed || owner.IsFaulted || owner._craftProxyEntries != 1
                || !owner._craftUiPending || !owner._craftAnimationStarted || owner._craftRevealed
                || __0?.Pointer != owner._craftSourceRecipe?.Recipe.Pointer) return false;
            owner._craftRevealed = true;
            return true;
        }
    }
}
