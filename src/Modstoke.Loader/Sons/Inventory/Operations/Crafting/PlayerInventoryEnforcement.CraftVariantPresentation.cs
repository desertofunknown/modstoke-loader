// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private readonly List<ItemInstance> _variantOutputInstances = new();
        private Il2CppSystem.Collections.Generic.List<CraftingRecipe.ResultingItem> _variantNativeResults;
        private CraftMatItem _variantReuse;
        private bool _variantReuseReturned;

        private void PrepareSourceVariant(CraftVariantPlan plan)
        {
            CraftVariant.Require(_craftUiPending && _craftSourceRecipe != null && _craftSourceRecipe.Current()
                && plan.RecipeIndex == _craftSourceRecipe.Index && plan.RecipeFingerprint == _craftSourceRecipe.Fingerprint
                && plan.Transaction > _craftSourceFloor && plan.Transaction > _lastReceived
                && plan.LastTransaction >= plan.Transaction && plan.LastTransaction != ulong.MaxValue
                && plan.Inputs?.Length == _craftSourceRows.Length && plan.Outputs != null
                && plan.Outputs.Length <= InventoryCraftRecipe.MaximumRows
                && plan.CounterResults != null && plan.CounterResults.Length <= InventoryCraftRecipe.MaximumRows
                && plan.Counters != null && !HasPendingCustody && !InventoryFrozen && Unchanged() && CraftMatCurrent(),
                "Variant plan does not match the reserved source selection.");
            int newResults = 0;
            var outputKeys = new HashSet<ulong>();
            for (int i = 0; i < plan.Inputs.Length; i++)
            {
                var row = plan.Inputs[i];
                var selection = _craftSourceRows[i];
                var held = _craftMat.FindAll(item => item.Source.Entry.Key == row.Key);
                CraftVariant.Require(row.Key == selection.InstanceKey && row.Quantity == selection.Quantity
                    && row.Before == _craftSourceBefore[i] && held.Count == row.Quantity && held.Count > 0
                    && held.TrueForAll(item => !item.Debited && item.Current() && item.Source.Entry.ItemId == row.Id
                        && item.Source.Quantity == row.Before)
                    && (held[0].Source.Instance == null ? row.Modules == null && row.State == null
                        : PlayerInventoryBaseline.JsonEqual(row.Modules, held[0].Modules) && row.State == held[0].ModuleState),
                    "Variant input modules or exact retained key differ.");
            }
            var slots = new Dictionary<int, int>();
            foreach (var output in plan.Outputs)
            {
                CraftVariant.Require(output.Key != 0 && outputKeys.Add(output.Key), "Variant output key is repeated.");
                if (output.RetainedKey == 0)
                {
                    CraftVariant.Require(output.Key == plan.Transaction + (ulong)++newResults
                        && !_sourceItems.Exists(item => item.Entry.Key == output.Key), "Variant new-result key is not fresh.");
                }
                else
                {
                    var input = Array.Find(plan.Inputs, item => item.Key == output.RetainedKey);
                    CraftVariant.Require(input != null && input.Id == output.Id && input.Quantity == 1 && input.Before == 1
                        && output.Key == input.Key && input.Disposition == (output.Ingredient
                            ? CraftVariantDisposition.Ingredient : CraftVariantDisposition.Result), "Variant retained result is not the exact ingredient.");
                }
                if (output.Ingredient)
                {
                    var held = _craftMat.Find(item => item.Source.Entry.Key == output.RetainedKey);
                    var group = FindCraftIngredientGroup(output.Id);
                    CraftVariant.Require(held != null && group?._layoutItems != null && !group._layoutItems.WasCollected
                        && group._layoutItems.Count > 0 && group._layoutItems[0]?.ItemInstance?.Pointer == held.Native.Pointer,
                        "Retained weapon is not in its original primary ingredient layout.");
                    continue;
                }
                slots.TryGetValue(output.Id, out int n); slots[output.Id] = n + 1;
            }
            CraftVariant.Require(plan.LastTransaction - plan.Transaction == (ulong)newResults,
                "Variant reserved output range differs.");
            CraftVariant.Require(_sourceItems.Count + newResults <= PlayerInventoryBaseline.MaximumEntries,
                "Variant source account has no room for new result records.");
            var amounts = new Dictionary<int, long>();
            var stored = new Dictionary<int, long>();
            foreach (var item in _sourceItems)
            {
                amounts.TryGetValue(item.Entry.ItemId, out long n); amounts[item.Entry.ItemId] = n + item.Quantity;
                if (Stowed(item.Entry))
                { stored.TryGetValue(item.Entry.ItemId, out n); stored[item.Entry.ItemId] = n + item.Quantity; }
            }
            foreach (var input in plan.Inputs) if (input.Disposition == CraftVariantDisposition.Consume)
            { amounts[input.Id] -= input.Quantity; stored[input.Id] -= input.Quantity; }
            foreach (var output in plan.Outputs) if (output.RetainedKey == 0)
            {
                amounts.TryGetValue(output.Id, out long n); amounts[output.Id] = n + 1;
                stored.TryGetValue(output.Id, out n); stored[output.Id] = n + 1;
            }
            foreach (var pair in amounts)
            {
                var data = ItemDatabaseManager.ItemById(pair.Key);
                CraftVariant.Require(data != null && !data.WasCollected && data && pair.Value >= 0
                    && pair.Value <= data._maxAmount, "Variant source output capacity differs.");
                if (_manager._itemsMap.TryGetValue(pair.Key, out var bucket) && bucket != null && bucket.MaxCount >= 0)
                    CraftVariant.Require(!stored.TryGetValue(pair.Key, out long n) || n <= bucket.MaxCount,
                        "Variant results exceed actual source storage capacity.");
            }
            foreach (var pair in slots)
            {
                var group = FindCraftResultGroup(pair.Key);
                CraftVariant.Require(group?._layoutItems != null && group._layoutItems.Count >= pair.Value,
                    "Variant output lacks an actual result layout.");
                foreach (var layout in group._layoutItems)
                    CraftVariant.Require(layout != null && !layout.WasCollected && layout && !layout.gameObject.activeSelf,
                        "Variant result layout is occupied.");
            }
            _craftSourceEffects = new(plan.Transaction, plan.RecipeFingerprint, plan.Counters, plan.RecipeWasNew);
            CraftVariant.Require(VariantCountersMatch(plan, false), "Variant source counters differ before preparation.");
            _variantOutputInstances.Clear();
            foreach (var output in plan.Outputs)
            {
                if (output.RetainedKey != 0)
                {
                    _variantOutputInstances.Add(_craftMat.Find(item => item.Source.Entry.Key == output.RetainedKey).Native);
                    continue;
                }
                ItemInstance native = null;
                try
                {
                    PlayerInventoryBaseline.RestoreInstance(
                        CraftVariant.Entry(output.Key, output.Id, output.Modules, output.State), out native);
                }
                catch
                {
                    if (native != null) RetainUncertainCraftResult(native);
                    throw;
                }
                finally { if (native != null) _variantOutputInstances.Add(native); }
                RetainPausedModules(native);
                CraftVariant.Require(native.Data._eachInstanceIsUnique || native._modules.Count == 0,
                    "Variant stack output would discard its modules.");
            }
            _variantNativeResults = new();
            foreach (int id in plan.CounterResults) _variantNativeResults.Add(new CraftingRecipe.ResultingItem { Id = id });
            // OnCraftCompleteInternal inlines GenerateResultingItems for container recipes.
            // Its cache read must see this exact server selection, including an empty result.
            if (_craftSourceRecipe.Recipe._useContainerDataForResultingItems)
                _craftSourceRecipe.Recipe._generatedResultingItems = _variantNativeResults;
        }

        private bool VariantNativeResultsCurrent()
        {
            var plan = _craftVariant?.Plan;
            if (plan == null || _variantNativeResults == null || _variantNativeResults.WasCollected
                || _variantNativeResults.Count != plan.CounterResults.Length
                || (_craftSourceRecipe.Recipe._useContainerDataForResultingItems
                    && _craftSourceRecipe.Recipe._generatedResultingItems?.Pointer != _variantNativeResults.Pointer)) return false;
            for (int i = 0; i < plan.CounterResults.Length; i++)
                if (_variantNativeResults[i] == null || _variantNativeResults[i].WasCollected
                    || _variantNativeResults[i].Id != plan.CounterResults[i]) return false;
            return true;
        }

        private bool SourceVariantCollected()
        {
            var plan = _craftVariant.Plan;
            if (!Unchanged() || !CraftMatCurrent() || !VariantCountersMatch(plan, true)) return false;
            foreach (var input in plan.Inputs)
            {
                var source = _sourceItems.Find(item => item.Entry.Key == input.Key);
                int expected = input.Before - (input.Disposition == CraftVariantDisposition.Consume ? input.Quantity : 0);
                if (source == null || !Stowed(source.Entry) || source.Quantity != expected) return false;
            }
            for (int i = 0; i < plan.Outputs.Length; i++)
            {
                var output = plan.Outputs[i];
                var source = _sourceItems.Find(item => item.Entry.Key == output.Key);
                if (source == null || !Stowed(source.Entry) || source.Quantity != 1 || source.Entry.ItemId != output.Id
                    || (source.Instance != null && (source.Instance.Pointer != _variantOutputInstances[i].Pointer
                        || !PlayerInventoryBaseline.MatchesInstance(source.Entry, source.Instance)))
                    || FindStoredEntry(_currentSnapshot, output.Id, source.Instance) < 0) return false;
            }
            return true;
        }

        private bool VariantCountersMatch(CraftVariantPlan plan, bool after)
        {
            var counts = new Dictionary<int, int>();
            foreach (int id in plan.CounterResults) { counts.TryGetValue(id, out int n); counts[id] = n + 1; }
            if (counts.Count != plan.Counters.Length) return false;
            foreach (var counter in plan.Counters)
                if (!counts.Remove(counter.ItemId, out int amount) || counter.Before < 0
                    || (long)counter.After - counter.Before != amount
                    || _craftSource._playerState.GetCrafted(counter.ItemId) != (after ? counter.After : counter.Before)) return false;
            return _craftSource._playerState.Get("isNewCraftingRecipe_" + _craftSourceRecipe.Name, false)
                == (!after && plan.RecipeWasNew);
        }

        private void StartSourceVariant()
        {
            CraftVariant.Require(_craftVariant?.Prepared == true && _craftVariant.Committed && !_craftAnimationStarted
                && !InventoryFrozen && Unchanged() && CraftMatCurrent() && _craftSourceRecipe.Current()
                && _craftSource.HasSatisfiedRecipeWithCurrentIngredients(out var selected)
                && selected?.Pointer == _craftSourceRecipe.Recipe.Pointer, "Variant native combine lost its prepared recipe.");
            _lastReceived = _craftVariant.Plan.LastTransaction;
            _busy = true;
            _craftAnimationStarted = _craftStartingNative = _craftBeginPermit = true;
            try
            {
                if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out var holdDetail))
                    throw new InvalidOperationException("Accepted variant crafting could not yield the source body hold: " + holdDetail);
                CraftVariant.Require(_craftSource.TryCombineIngredients() && !_craftBeginPermit,
                    "Variant did not enter its original native animation path.");
            }
            finally { _craftStartingNative = _craftBeginPermit = _busy = false; }
        }

        private void PlaceSourceVariant()
        {
            var plan = _craftVariant.Plan;
            CraftVariant.Require(_craftCompleting && !_craftResultsPlaced && _craftVariant.Committed,
                "Variant result placement is not its once-only completion.");
            _craftResultsPlaced = true;
            for (int i = 0; i < plan.Outputs.Length; i++)
            {
                var output = plan.Outputs[i];
                var native = _variantOutputInstances[i];
                CraftMatItem held = null;
                SourceItem source;
                if (output.RetainedKey != 0)
                {
                    held = _craftMat.Find(item => item.Source.Entry.Key == output.RetainedKey);
                    CraftVariant.Require(held != null && held.Current() && held.Native.Pointer == native.Pointer,
                        "Retained result instance disappeared.");
                    CraftVariant.ChangeRetained(_craftSourceRecipe, native);
                    source = new(CraftVariant.Entry(output.Key, output.Id, output.Modules, output.State), native);
                    int at = _sourceItems.IndexOf(held.Source);
                    CraftVariant.Require(at >= 0, "Retained source record disappeared.");
                    _sourceItems[at] = held.Source = source;
                    held.Capture();
                }
                else
                {
                    bool unique = native.Data._eachInstanceIsUnique;
                    source = new(CraftVariant.Entry(output.Key, output.Id, unique ? output.Modules : null,
                        unique ? output.State : null), unique ? native : null);
                    _sourceItems.Add(source);
                }
                CraftVariant.Require(PlayerInventoryBaseline.JsonEqual(output.Modules,
                    PlayerInventoryBaseline.CaptureModules(native))
                    && output.State == PlayerInventoryBaseline.CaptureModuleState(native),
                    "Variant source result modules differ from server commit.");
                if (output.Ingredient)
                {
                    var group = FindCraftIngredientGroup(output.Id);
                    CraftVariant.Require(group?._layoutItems != null && group._layoutItems.Count > 0
                        && group._layoutItems[0]?.ItemInstance?.Pointer == native.Pointer,
                        "Retained weapon is not in the original primary ingredient layout.");
                    // Native CraftResultingItem refreshes that same layout, not a new weapon.
                    group._layoutItems[0].SetItemInstance(native);
                    CraftVariant.Require(held.Current(), "Weapon layout refresh changed committed modules.");
                    continue;
                }
                if (held != null) held.Debited = true; // Ownership moved to result escrow, not consumed.
                var result = new CraftMatItem(source, native, true);
                result.Capture();
                _craftResults.Add(result);
                var resultGroup = FindCraftResultGroup(output.Id);
                CraftVariant.Require(resultGroup != null && resultGroup.TryAddItem(native, output.Id, 1),
                    "Variant native result layout declined its instance.");
                foreach (var layout in resultGroup._layoutItems)
                    if (layout != null && !layout.WasCollected && layout && layout.gameObject.activeSelf
                        && layout.ItemInstance?.Pointer == native.Pointer)
                    {
                        CraftVariant.Require(result.Layout == null, "Variant instance appeared in multiple result layouts.");
                        result.Layout = layout;
                    }
                CraftVariant.Require(result.Layout != null && result.Current(), "Variant result placement was not confirmed.");
                result.Placed = true;
            }
        }

        private void ClearSourceVariantIngredients()
        {
            CraftVariant.Require(_craftCompleting && _craftResultsPlaced && !_craftIngredientsCleared,
                "Variant ingredient completion is out of order.");
            _craftIngredientsCleared = true;
            foreach (var input in _craftVariant.Plan.Inputs)
            {
                var held = _craftMat.FindAll(item => item.Source.Entry.Key == input.Key);
                CraftVariant.Require(held.Count == input.Quantity, "Variant ingredient mat count changed.");
                if (input.Disposition == CraftVariantDisposition.Ingredient) continue;
                if (input.Disposition == CraftVariantDisposition.ReturnReusable)
                {
                    CraftVariant.Require(held.Count == 1, "Reuse callback has more than one retained tool.");
                    _variantReuse = held[0];
                    _variantReuseReturned = false;
                    try
                    {
                        _craftSource.OnReuseIngredientEvent.Invoke(input.Id);
                        CraftVariant.Require(_variantReuseReturned, "Native reuse listener did not return the retained tool.");
                    }
                    finally { _variantReuse = null; }
                }
                else if (input.Disposition == CraftVariantDisposition.Consume)
                {
                    CraftVariant.Require(held[0].Source.Quantity == input.Before, "Variant source debit quantity changed.");
                    held[0].Source.Quantity -= input.Quantity;
                    foreach (var item in held) item.Debited = true;
                }
                RemoveCraftMatVisual(input.Id, input.Quantity);
                foreach (var item in held) _craftMat.Remove(item);
            }
            CraftVariant.Require(CraftMatCurrent() && Unchanged(), "Variant completion changed unrelated physical inventory.");
        }

        private static bool BeforeVariantReuseReturn(IngredientLayoutItemGroup __instance, int __0, bool __runOriginal)
        {
            var owner = CraftOwner(_guard?._craftSource);
            if (owner == null || !owner.OwnsIngredientGroup(__instance)) return __runOriginal;
            if (!__runOriginal || !owner.OnThread || owner.IsFaulted || !owner._craftCompleting || owner._variantReuse == null
                || __instance._itemId != __0 || owner._variantReuse.Source.Entry.ItemId != __0) return false;
            try
            {
                CraftVariant.Require(!owner._variantReuseReturned, "Native reuse listener ran twice.");
                owner._variantReuseReturned = true;
                owner.ReturnCraftMatItem(owner._variantReuse, false);
            }
            catch (Exception error) { owner.Fail("Variant reuse return failed: " + error.Message); }
            return false;
        }

        private static bool BeforeVariantGeneratedResults(CraftingRecipe __instance, bool __runOriginal,
            ref Il2CppSystem.Collections.Generic.List<CraftingRecipe.ResultingItem> __result)
        {
            var owner = CraftOwner(_guard?._craftSource);
            if (owner == null || !owner.CraftVariantPending || __instance?.Pointer != owner._craftSourceRecipe?.Recipe.Pointer)
                return __runOriginal;
            if (!__runOriginal || !owner.OnThread || owner.IsFaulted || owner._variantNativeResults == null) return false;
            __result = owner._variantNativeResults;
            return false;
        }

        private static bool BeforeVariantGenerateList(CraftingRecipe __instance, bool __runOriginal)
        {
            var owner = CraftOwner(_guard?._craftSource);
            return __runOriginal && (owner == null || !owner.CraftVariantPending
                || __instance?.Pointer != owner._craftSourceRecipe?.Recipe.Pointer);
        }
    }
}
