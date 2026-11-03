// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    internal enum CraftVariantDisposition { Consume, ReturnReusable, Result, Ingredient }

    internal sealed record CraftVariantInput(ulong Key, int Id, int Quantity, int Before,
        string Modules, PlayerInventoryModuleState State, CraftVariantDisposition Disposition);
    internal sealed record CraftVariantOutput(ulong Key, int Id, ulong RetainedKey, bool Ingredient,
        string Modules, PlayerInventoryModuleState State);
    internal sealed record CraftVariantPlan(int RecipeIndex, string RecipeFingerprint, ulong Transaction,
        ulong LastTransaction, CraftVariantInput[] Inputs, CraftVariantOutput[] Outputs,
        int[] CounterResults, CraftCounter[] Counters, bool RecipeWasNew);

    internal static class CraftVariant
    {
        internal static void Require(bool condition, string detail)
            => PlayerInventoryBaseline.Require(condition, detail);

        internal static RangedWeaponItemInstanceModule Weapon(ItemInstance instance)
        {
            RangedWeaponItemInstanceModule module = null;
            Require(instance != null && !instance.WasCollected
                && instance.TryGetModule<RangedWeaponItemInstanceModule>(out module)
                && module != null && !module.WasCollected, "Retained weapon has no initialized ranged-weapon module.");
            return module;
        }

        internal static void ChangeRetained(InventoryCraftRecipe recipe, ItemInstance instance)
        {
            if (recipe.Type == CraftingRecipe.Type.ApplyWeaponMod)
            {
                var module = Weapon(instance);
                var mod = recipe.Recipe._weaponMod;
                Require(mod != null && !mod.WasCollected && module.IsWeaponModCompatible(mod),
                    "Weapon modification is not compatible with the retained instance.");
                module.AddWeaponMod(mod);
            }
            else if (recipe.Type == CraftingRecipe.Type.DismantleWeaponMods) Weapon(instance).RemoveAllMods();
        }

        internal static void InitializeOutput(InventoryCraftRecipe recipe, ItemInstance instance)
        {
            PlayerInventoryEnforcement.InitializeGrantModules(instance);
            if (recipe.Type != CraftingRecipe.Type.ArrowUpgrade) return;
            // SetType reads the module's initialized item id, not just its new enum value.
            Require(instance.TryGetModule<ArrowUpgradeItemInstanceModule>(out var arrow)
                && arrow != null && !arrow.WasCollected, "Arrow output lacks its initialized upgrade module.");
            arrow.SetType(recipe.Recipe._arrowUpgradeType);
            Require(arrow._upgradeType == recipe.Recipe._arrowUpgradeType && arrow._data != null,
                "Arrow upgrade has no native item definition.");
        }

        internal static PlayerInventoryBaseline.Entry Entry(ulong key, int id, string modules,
            PlayerInventoryModuleState state) => new()
            { Key = key, ItemId = id, Quantity = 1, Modules = modules, ModuleState = state };
    }

    public sealed partial class PlayerInventoryAuthority
    {
        private NativeVariantReservation _nativeVariant;

        internal CraftVariantPlan PrepareCraftVariant(int index, string fingerprint,
            CraftIngredient[] selections)
        {
            NativeCraftRequire(Ready(out var target) && HasAcceptedBaseline && _pending == null && _stow == null && _equip == null
                && MutationEnforcement != null && MutationEnforcement.CanQueue && !MutationEnforcement.HasPendingCustody
                && !HasPendingNativePowerSupply
                && _requestCraftDatabase != null && _craftRouteDatabase?.Pointer == _requestCraftDatabase.Pointer
                && _authorizeCraftRecipe != null && CraftEffectsOwnerCurrent(), "Variant crafting account is not idle and bound.");
            var recipe = InventoryCraftRecipe.Capture(_requestCraftDatabase, index, true);
            NativeCraftRequire(recipe.IsVariant && recipe.Fingerprint == fingerprint, "Variant recipe definition differs.");
            bool approved;
            _busy = true;
            try { approved = _authorizeCraftRecipe(index); }
            finally { _busy = false; }
            NativeCraftRequire(approved && recipe.Current() && Ready(out target), "Server declined the variant recipe.");
            var inputs = CaptureCraftInputs(recipe, selections);
            var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
            NativeCraftRequire(equipment != null && OwnedEquipmentMatches(equipment), "Variant equipment custody changed.");
            var reservation = new NativeVariantReservation(recipe, inputs, equipment);
            _busy = true;
            try
            {
                ulong transaction = checked(_lastTransaction + 1);
                var declared = new List<int>(recipe.Results);
                if (recipe.Recipe._useContainerDataForResultingItems)
                {
                    // Random selection is already an irreversible part of this reservation.
                    _nativeVariant = reservation;
                    recipe.Recipe.GenerateResultingItemList();
                    // GenerateResultingItems would reroll an empty cache. Read the one
                    // generated selection directly so even an empty container is final.
                    var generated = recipe.Recipe._generatedResultingItems;
                    NativeCraftRequire(generated != null && generated.Count <= InventoryCraftRecipe.MaximumRows,
                        "Container result count exceeds native custody bounds.");
                    declared.Clear();
                    foreach (var row in generated) { NativeCraftRequire(row != null, "Container result is absent."); declared.Add(row.Id); }
                }
                var actual = new List<int>(declared);
                if (recipe.Type == CraftingRecipe.Type.DismantleWeaponMods)
                {
                    NativeCraftRequire(actual.Count > 0, "Dismantle recipe has no retained weapon.");
                    var weapon = Array.Find(inputs, item => item.Record.Id == actual[0]);
                    NativeCraftRequire(weapon != null && weapon.Quantity == 1, "Dismantle weapon custody is absent.");
                    NativeCraftRequire(CraftVariant.Weapon(weapon.Record.Item).TryGetActiveModItemIds(out var installed)
                        && installed != null && installed.Count > 0, "Dismantle has no installed modifications.");
                    for (int i = actual.Count - 1; i > 0; i--) if (!installed.Contains(actual[i])) actual.RemoveAt(i);
                    foreach (int id in installed)
                        NativeCraftRequire(actual.Contains(id), "Installed modification has no dismantle result.");
                }
                var inputRows = new CraftVariantInput[inputs.Length];
                for (int i = 0; i < inputs.Length; i++)
                {
                    var input = inputs[i];
                    var ingredient = Array.Find(recipe.Ingredients, item => item.Id == input.Record.Id);
                    NativeCraftRequire(!ingredient.Reusable || ingredient.Quantity == 1,
                        "Native reuse listener returns exactly one ingredient per callback.");
                    inputRows[i] = new(input.Record.Key, input.Record.Id, input.Quantity, input.BeforeQuantity,
                        input.Modules, input.Record.Item == null ? null : PlayerInventoryBaseline.CaptureModuleState(input.Record.Item),
                        ingredient.Reusable ? CraftVariantDisposition.ReturnReusable : CraftVariantDisposition.Consume);
                }
                var retainedInputs = new List<int>();
                var retainedKeys = new HashSet<ulong>();
                var counts = new Dictionary<int, long>();
                foreach (var item in _items.Values) if (!item.Consumed)
                { counts.TryGetValue(item.Id, out long n); counts[item.Id] = n + item.Quantity; }
                foreach (int id in actual)
                {
                    var data = ItemDatabaseManager.ItemById(id);
                    NativeCraftRequire(data != null && !data.WasCollected && data && id > 0
                        && id <= ServerInventory.MaxItemId && !data._canNotBeStored && data._maxAmount > 0,
                        "Variant result has no storable native definition.");
                    int retained = recipe.Type == CraftingRecipe.Type.ArrowUpgrade || recipe.Recipe._forceNewItemInstance
                        ? -1 : Array.FindIndex(inputRows, item => item.Id == id);
                    bool weaponLayout = recipe.Type is CraftingRecipe.Type.ApplyWeaponMod or CraftingRecipe.Type.DismantleWeaponMods
                        && ((int)data._type & 4) != 0;
                    if (retained >= 0)
                    {
                        var input = inputRows[retained];
                        NativeCraftRequire(input.Quantity == 1 && input.Before == 1 && inputs[retained].Record.Item != null
                            && input.Disposition == CraftVariantDisposition.Consume && retainedKeys.Add(input.Key)
                            && Array.FindLastIndex(inputRows, row => row.Id == id) == retained,
                            "Result reuse requires one exact ingredient instance, not repeated outputs or a reusable tool.");
                        NativeCraftRequire(!weaponLayout || retainedInputs.Count == 0,
                            "Only the primary retained weapon may remain in its ingredient layout.");
                        if (recipe.Type == CraftingRecipe.Type.ApplyWeaponMod)
                        {
                            var mod = recipe.Recipe._weaponMod;
                            NativeCraftRequire(mod != null && !mod.WasCollected
                                && CraftVariant.Weapon(inputs[retained].Record.Item).IsWeaponModCompatible(mod),
                                "Weapon modification is not compatible with its retained ingredient.");
                        }
                        inputRows[retained] = input with { Disposition = weaponLayout
                            ? CraftVariantDisposition.Ingredient : CraftVariantDisposition.Result };
                    }
                    else
                    {
                        NativeCraftRequire(!weaponLayout, "A new weapon ingredient output needs its own layout custody.");
                        counts.TryGetValue(id, out long n); counts[id] = n + 1;
                    }
                    retainedInputs.Add(retained);
                }
                foreach (var row in inputRows) if (row.Disposition == CraftVariantDisposition.Consume) counts[row.Id] -= row.Quantity;
                foreach (var pair in counts)
                    NativeCraftRequire(pair.Value >= 0 && pair.Value <= ItemDatabaseManager.ItemById(pair.Key)._maxAmount,
                        "Variant outputs exceed final native capacity.");
                NativeCraftRequire(_items.Count + actual.Count - retainedKeys.Count <= MaximumIssuedItems,
                    "Variant output key limit exceeded.");
                NativeCraftRequire(checked(transaction + (ulong)(actual.Count - retainedKeys.Count)) < ulong.MaxValue,
                    "Variant transaction range is exhausted.");

                // No deterministic refusal above has changed non-container custody. From
                // this point detached construction or module mutation may have taken effect.
                _nativeVariant = reservation;
                var outputs = new List<CraftVariantOutput>();
                ulong next = transaction;
                for (int outputIndex = 0; outputIndex < actual.Count; outputIndex++)
                {
                    int id = actual[outputIndex], retained = retainedInputs[outputIndex];
                    var data = ItemDatabaseManager.ItemById(id);
                    ItemInstance native = null;
                    ulong key, retainedKey = 0;
                    bool ingredientLayout = false;
                    if (retained >= 0)
                    {
                        var input = inputRows[retained];
                        retainedKey = key = input.Key;
                        try
                        {
                            PlayerInventoryBaseline.RestoreInstance(
                                CraftVariant.Entry(key, id, input.Modules, input.State), out native);
                        }
                        finally { if (native != null) reservation.Temporary.Add(native); }
                        MutationEnforcement.RetainPausedModules(native);
                        CraftVariant.ChangeRetained(recipe, native);
                        ingredientLayout = input.Disposition == CraftVariantDisposition.Ingredient;
                    }
                    else
                    {
                        key = checked(++next);
                        NativeCraftRequire(!_items.ContainsKey(key), "Variant output key already exists.");
                        native = new ItemInstance(id);
                        reservation.Temporary.Add(native);
                        CraftVariant.InitializeOutput(recipe, native);
                    }
                    MutationEnforcement.RetainPausedModules(native);
                    NativeCraftRequire(data._eachInstanceIsUnique || native._modules.Count == 0,
                        "Stacked output cannot retain unique native module state.");
                    outputs.Add(new(key, id, retainedKey, ingredientLayout,
                        PlayerInventoryBaseline.CaptureModules(native),
                        PlayerInventoryBaseline.CaptureModuleState(native)));
                    reservation.Outputs.Add(native);
                }
                var counters = new Dictionary<int, int>();
                foreach (int id in declared) { counters.TryGetValue(id, out int n); counters[id] = n + 1; }
                var effects = new List<CraftCounter>();
                foreach (var pair in counters)
                {
                    int before = _craftCounters.GetCrafted(pair.Key);
                    NativeCraftRequire(before >= 0, "Variant counter is negative.");
                    effects.Add(new(pair.Key, before, checked(before + pair.Value)));
                }
                reservation.Plan = new(index, recipe.Fingerprint, transaction, next, inputRows, outputs.ToArray(),
                    declared.ToArray(), effects.ToArray(), _craftCounters.Get("isNewCraftingRecipe_" + recipe.Name, false));
                _lastTransaction = next;
                NativeCraftRequire(VariantInputsCurrent(reservation), "Variant inputs changed while preparing outputs.");
                return reservation.Plan;
            }
            catch (Exception error)
            {
                if (ReferenceEquals(_nativeVariant, reservation))
                {
                    foreach (var item in reservation.Temporary) MutationEnforcement.RetainUncertainCraftResult(item);
                    Fault("Variant reservation is uncertain: " + error.Message);
                }
                throw;
            }
            finally { _busy = false; }
        }

        private bool VariantInputsCurrent(NativeVariantReservation reservation)
        {
            if (!CraftEffectsOwnerCurrent() || !reservation.Recipe.Current() || !Current(out var target)
                || !reservation.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
            for (int i = 0; i < reservation.Inputs.Length; i++)
            {
                var input = reservation.Inputs[i];
                if (!_items.TryGetValue(input.Record.Key, out var item) || !ReferenceEquals(item, input.Record)
                    || item.Consumed || item.Quantity != input.BeforeQuantity || !item.IdentityCurrent()
                    || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0
                    || (item.Item != null && !PlayerInventoryBaseline.JsonEqual(input.Modules,
                        PlayerInventoryBaseline.CaptureModules(item.Item)))) return false;
                if (reservation.Plan != null && item.Item != null && reservation.Plan.Inputs[i].State
                    != PlayerInventoryBaseline.CaptureModuleState(item.Item)) return false;
            }
            return true;
        }

        internal void CommitCraftVariant(CraftVariantPlan plan)
        {
            var reservation = _nativeVariant;
            NativeCraftRequire(Environment.CurrentManagedThreadId == _thread && !_busy && !_suspended
                && reservation != null && ReferenceEquals(reservation.Plan, plan), "Variant commit lost its reservation.");
            if (reservation.Committed) return;
            NativeCraftRequire(!reservation.Attempted && VariantInputsCurrent(reservation), "Variant commit custody changed.");
            _busy = true;
            reservation.Attempted = true;
            try
            {
                foreach (var counter in plan.Counters)
                    NativeCraftRequire(_craftCounters.GetCrafted(counter.ItemId) == counter.Before, "Variant counters changed before commit.");
                NativeCraftRequire(_craftCounters.Get("isNewCraftingRecipe_" + reservation.Recipe.Name, false) == plan.RecipeWasNew,
                    "Variant notification state changed before commit.");
                for (int i = 0; i < plan.Outputs.Length; i++)
                {
                    var output = plan.Outputs[i];
                    ItemRecord record;
                    if (output.RetainedKey != 0)
                    {
                        record = _items[output.RetainedKey];
                        CraftVariant.ChangeRetained(reservation.Recipe, record.Item);
                        NativeCraftRequire(PlayerInventoryBaseline.JsonEqual(output.Modules,
                            PlayerInventoryBaseline.CaptureModules(record.Item))
                            && output.State == PlayerInventoryBaseline.CaptureModuleState(record.Item),
                            "Retained weapon commit differs from its reserved module result.");
                    }
                    else
                    {
                        var native = reservation.Outputs[i];
                        record = native.Data._eachInstanceIsUnique ? new(output.Key, native) : new(output.Key, output.Id, 1);
                        NativeCraftRequire(record.CaptureIdentity(), "Variant result identity is unavailable.");
                        _items.Add(record.Key, record);
                    }
                }
                foreach (var input in plan.Inputs) if (input.Disposition == CraftVariantDisposition.Consume)
                {
                    var item = _items[input.Key];
                    item.Quantity -= input.Quantity;
                    item.Consumed = item.Quantity == 0;
                }
                foreach (int id in plan.CounterResults) _craftCounters.IncCrafted(id);
                _craftCounters.Set("isNewCraftingRecipe_" + reservation.Recipe.Name, false);
                foreach (var counter in plan.Counters)
                    NativeCraftRequire(_craftCounters.GetCrafted(counter.ItemId) == counter.After, "Variant counter commit differs.");
                NativeCraftRequire(VariantCommittedCurrent(reservation), "Variant committed custody readback differs.");
                reservation.Committed = true;
            }
            catch (Exception error) { Fault("Variant commit is uncertain: " + error.Message); throw; }
            finally { _busy = false; }
        }

        internal void CompleteCraftVariant(CraftVariantPlan plan)
        {
            NativeCraftRequire(Environment.CurrentManagedThreadId == _thread && !_busy && !_suspended
                && _nativeVariant != null && ReferenceEquals(_nativeVariant.Plan, plan) && _nativeVariant.Committed
                && VariantCommittedCurrent(_nativeVariant), "Variant completion lost its committed owner.");
            foreach (var counter in plan.Counters)
                NativeCraftRequire(_craftCounters.GetCrafted(counter.ItemId) == counter.After, "Variant counter changed before source receipt.");
            for (int i = 0; i < plan.Outputs.Length; i++)
                if (plan.Outputs[i].RetainedKey != 0) MutationEnforcement.ReleaseDetachedModules(_nativeVariant.Outputs[i]);
            foreach (var input in plan.Inputs)
                if (_items[input.Key].Consumed) MutationEnforcement.ReleaseDetachedModules(_items[input.Key].Item);
            _nativeVariant = null;
        }

        private bool VariantCommittedCurrent(NativeVariantReservation reservation)
        {
            var plan = reservation.Plan;
            if (!CraftEffectsOwnerCurrent() || !reservation.Recipe.Current() || !Current(out var target)
                || !reservation.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
            foreach (var input in plan.Inputs)
            {
                int quantity = input.Before - (input.Disposition == CraftVariantDisposition.Consume ? input.Quantity : 0);
                if (!_items.TryGetValue(input.Key, out var item) || !item.IdentityCurrent() || item.Quantity != quantity
                    || item.Consumed != (quantity == 0) || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0) return false;
                var result = Array.Find(plan.Outputs, row => row.RetainedKey == input.Key);
                if (item.Item != null && (!PlayerInventoryBaseline.JsonEqual(result?.Modules ?? input.Modules,
                        PlayerInventoryBaseline.CaptureModules(item.Item))
                    || (result?.State ?? input.State) != PlayerInventoryBaseline.CaptureModuleState(item.Item))) return false;
            }
            foreach (var output in plan.Outputs)
                if (!_items.TryGetValue(output.Key, out var item) || !item.IdentityCurrent() || item.Quantity != 1
                    || item.Consumed || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0
                    || (item.Item != null && (!PlayerInventoryBaseline.JsonEqual(output.Modules,
                            PlayerInventoryBaseline.CaptureModules(item.Item))
                        || output.State != PlayerInventoryBaseline.CaptureModuleState(item.Item)))) return false;
            return !_craftCounters.Get("isNewCraftingRecipe_" + reservation.Recipe.Name, true);
        }

        private sealed class NativeVariantReservation(InventoryCraftRecipe recipe, NativeCraftInput[] inputs, EquipmentState equipment)
        {
            internal readonly InventoryCraftRecipe Recipe = recipe;
            internal readonly NativeCraftInput[] Inputs = inputs;
            internal readonly EquipmentState Equipment = equipment;
            internal readonly List<ItemInstance> Temporary = new(), Outputs = new();
            internal CraftVariantPlan Plan;
            internal bool Attempted, Committed;
        }
    }
}
