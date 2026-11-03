// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private NativeCraftReservation _nativeCraft;
        private CraftReceipt? _nativeCraftReceipt;

        /// <summary>Ready must reject while this is true, including after an uncertain craft.</summary>
        public bool HasNativeCraftReservation => _nativeCraft != null || _nativeVariant != null || _craftEffectsAwaitingSource;

        /// <summary>
        /// Reserves one fixed-result recipe execution against actual accepted inventory records.
        /// The caller must independently authorize the recipe and keys on the server. This is not
        /// a client request validator, mat takeover, animation, statistic or unlock implementation.
        /// Ingredients retain logical stored account custody while the source may escrow the
        /// exact instances on its guarded mat. The authenticated Ready receipt precedes debits.
        /// Reserves transaction+1 through LastTransaction for native commands; result keys follow
        /// the ingredient command keys. Never retry/refund an uncertain reservation.
        /// </summary>
        public bool TryReserveCraft(ulong transaction, CraftingRecipeDatabase database, int recipeIndex,
            IReadOnlyList<CraftIngredient> ingredients, out string detail)
        {
            detail = "Crafting needs an idle accepted account, armed receiver, and fresh trusted server decision.";
            if (!Ready(out var target) || HasNativeCraftReservation || !HasAcceptedBaseline || !Fresh(transaction)
                || MutationEnforcement == null || !MutationEnforcement.CanQueue
                || (MutationEnforcement.HasPendingCustody && !_reservingCraftRequest)
                || _pending != null || _stow != null || _equip != null || HasPendingNativePowerSupply
                || ingredients == null || ingredients.Count is <= 0 or > 256) return false;
            NativeCraftReservation candidate = null;
            bool reserved = false;
            try
            {
                var recipe = InventoryCraftRecipe.Capture(database, recipeIndex);
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                NativeCraftRequire(equipment != null && OwnedEquipmentMatches(equipment), "Craft equipment custody differs.");
                var inputs = CaptureCraftInputs(recipe, ingredients);
                ulong last = checked(transaction + (ulong)inputs.Length + (ulong)recipe.Results.Length);
                NativeCraftRequire(_items.Count <= MaximumIssuedItems - recipe.Results.Length,
                    "Craft result keys exceed the account bound.");
                ValidateCraftCapacity(recipe);
                candidate = new NativeCraftReservation(transaction, last, recipe, inputs, equipment);
                if (_craftRouteDatabase != null)
                {
                    NativeCraftRequire(database.Pointer == _craftRouteDatabase.Pointer && CraftEffectsOwnerCurrent(),
                        "Craft recipe or actor effects owner differs from the bound server route.");
                    candidate.Effects = CaptureCraftEffects(recipe);
                }
                _nativeCraft = candidate;
                // Fail closed if the integration omitted the account-wide reservation gate.
                // No result exists and no source write has been queued at this point.
                if (Ready(out _))
                {
                    _nativeCraft = null;
                    detail = "Native crafting requires HasNativeCraftReservation in the authority Ready guard.";
                    return false;
                }
                _busy = true;
                _lastTransaction = last;
                reserved = true;
                for (int i = 0; i < recipe.Results.Length; i++)
                {
                    int id = recipe.Results[i];
                    ulong key = transaction + (ulong)inputs.Length + (ulong)i + 1;
                    NativeCraftRequire(!_items.ContainsKey(key), "Craft result key already has custody.");
                    var native = new ItemInstance(id);
                    var output = new NativeCraftOutput(native);
                    candidate.Outputs.Add(output); // Retain partial construction if initialization fails.
                    PlayerInventoryEnforcement.InitializeGrantModules(native);
                    MutationEnforcement.RetainPausedModules(native);
                    output.Modules = PlayerInventoryBaseline.CaptureModules(native);
                    output.ModuleHash = PlayerInventoryWire.HashModules(output.Modules);
                    var data = native.Data;
                    NativeCraftRequire(data != null && !data.WasCollected && data && data.Id == id
                        && (data._eachInstanceIsUnique || native._modules.Count == 0),
                        "Nonunique craft output cannot discard native modules.");
                    output.Record = data._eachInstanceIsUnique ? new ItemRecord(key, native) : new ItemRecord(key, id, 1);
                    NativeCraftRequire(output.Record.CaptureIdentity(), "Craft result identity is unavailable.");
                }
                NativeCraftRequire(Current(out var current) && current.Pointer == target.Pointer && NativeCraftCustodyCurrent(candidate),
                    "Craft custody changed while preparing exact native results.");
                Detail = detail = "Recipe and exact ingredient custody reserved; native debits and result receipts are pending.";
                SetCraftReceipt(candidate, detail);
                return true;
            }
            catch (Exception error)
            {
                detail = "Craft reservation failed: " + error.Message;
                if (reserved)
                {
                    foreach (var output in candidate.Outputs)
                    {
                        try { MutationEnforcement.RetainUncertainCraftResult(output.Native); }
                        catch (Exception cleanup) { detail += " Detached module cleanup is uncertain: " + cleanup.Message; }
                    }
                    FailCraft(candidate, detail);
                }
                else if (ReferenceEquals(_nativeCraft, candidate)) _nativeCraft = null;
                return false;
            }
            finally { if (reserved) _busy = false; }
        }

        // Call before ordinary passive progression from the inventory endpoint's game-thread pump.
        // Queue at most one mutation per call; existing receiver code owns native writer permits.
        internal void PumpCrafting()
        {
            var craft = _nativeCraft;
            if (Environment.CurrentManagedThreadId != _thread || _busy || craft == null
                || craft.State == CraftState.Unconfirmed) return;
            if (IsDisposed || IsFaulted || _suspended || MutationEnforcement == null || !Current(out _))
            { FailCraft(craft, "Craft lifetime ended or faulted; reserved custody must not be retried or refunded."); return; }
            if (!MutationEnforcement.NativeCraftRequestMayMutate) return;
            _busy = true;
            try
            {
                if (craft.Awaiting)
                {
                    if (!MutationEnforcement.TryReadCraftMutationReceipt(craft.Command, out bool complete, out string receipt))
                        throw new InvalidOperationException(receipt);
                    if (!complete) return;
                    if (craft.Command.Kind == PlayerInventoryMessage.Remove) craft.Removed++;
                    else craft.Delivered++;
                    craft.Awaiting = false;
                }
                NativeCraftRequire(NativeCraftCustodyCurrent(craft), "Reserved recipe, quantities, modules or equipment changed.");
                if (craft.Removed == craft.Inputs.Length && craft.Delivered == craft.Outputs.Count)
                {
                    ApplyCraftEffects(craft);
                    NativeCraftRequire(NativeCraftCustodyCurrent(craft), "Craft effects changed reserved custody.");
                    craft.State = CraftState.Applied;
                    Detail = craft.EffectsApplied
                        ? "Craft custody and bound actor crafted counters confirmed; source presentation still needs its receipt."
                        : "Craft custody completed: exact ingredient debits and every native result receipt confirmed.";
                    SetCraftReceipt(craft, Detail);
                    _nativeCraft = null;
                    return;
                }
                NativeCraftRequire(_removal == null && _grant == null && _pending == null && _stow == null && _passive == null,
                    "Another inventory transaction entered reserved craft custody.");
                if (!MutationEnforcement.CanQueue) return;
                if (craft.Removed < craft.Inputs.Length)
                {
                    var input = craft.Inputs[craft.Removed];
                    craft.Command = NativeCraftFrame(craft.Transaction + (ulong)craft.Removed + 1,
                        input.Record.Key, input.Record.Id, input.Quantity, input.BeforeQuantity, PlayerInventoryMessage.Remove);
                    _removal = new PendingRemoval(craft.Command, input.Record, input.Modules, craft.Equipment);
                    craft.State = CraftState.RemovingIngredients;
                }
                else
                {
                    var output = craft.Outputs[craft.Delivered];
                    int stored = ReadCraftStoredCount(output.Record.Id);
                    craft.Command = NativeCraftFrame(output.Record.Key, output.Record.Key, output.Record.Id, 1,
                        stored, PlayerInventoryMessage.GrantPrepare, output.ModuleHash);
                    _grant = new PendingGrant(craft.Command, output.Record, output.Native, output.Modules, craft.Equipment);
                    craft.State = CraftState.DeliveringResults;
                }
                // Advance before transport: a later failure can never authorize the same native write again.
                craft.Awaiting = true;
                MutationEnforcement.QueueMutation(craft.Command);
                Detail = "Craft native mutation queued once; exact receipt pending.";
                SetCraftReceipt(craft, Detail);
            }
            catch (Exception error) { FailCraft(craft, "Craft custody is unconfirmed: " + error.Message); }
            finally { _busy = false; }
        }

        public bool TryReadCraftReceipt(ulong transaction, out CraftReceipt receipt)
        {
            receipt = default;
            if (Environment.CurrentManagedThreadId != _thread) return false;
            if (_nativeCraft != null && (IsDisposed || IsFaulted) && _nativeCraft.State != CraftState.Unconfirmed)
                FailCraft(_nativeCraft, "Inventory retired or faulted with reserved craft custody; no retry or refund.");
            if (_nativeCraftReceipt is not { } current || current.Transaction != transaction) return false;
            receipt = current;
            return true;
        }

        // Invoke before clearing inventory records on retirement. Never releases or re-credits inputs.
        internal void RetireCrafting()
        {
            if (_nativeVariant != null)
                Fault("Inventory retired with retained-instance craft custody; no replay or refund is authorized.");
            if (_nativeCraft != null && _nativeCraft.State != CraftState.Unconfirmed)
                FailCraft(_nativeCraft, "Inventory retired with a reserved craft; partial native custody is retained as uncertain.");
            else if (_craftEffectsAwaitingSource && _nativeCraftReceipt is { } receipt)
            {
                const string detail = "Inventory retired before source craft effects were acknowledged; no replay or refund.";
                _nativeCraftReceipt = receipt with { State = CraftState.Unconfirmed, Detail = detail };
                Fault(detail);
            }
        }

        private NativeCraftInput[] CaptureCraftInputs(InventoryCraftRecipe recipe,
            IReadOnlyList<CraftIngredient> selections)
        {
            var required = new Dictionary<int, long>();
            foreach (var ingredient in recipe.Ingredients) required.Add(ingredient.Id, ingredient.Quantity);
            var keys = new HashSet<ulong>();
            var inputs = new NativeCraftInput[selections.Count];
            for (int i = 0; i < inputs.Length; i++)
            {
                var selection = selections[i];
                NativeCraftRequire(keys.Add(selection.InstanceKey) && selection.Quantity > 0
                    && _items.ContainsKey(selection.InstanceKey), "Craft selection has an unknown or repeated key.");
                var item = _items[selection.InstanceKey];
                NativeCraftRequire(!item.Consumed && !item.DefinitionOnly && item.HeldSlot < 0 && !item.Clothing
                    && item.ArmourSlot < 0 && item.Quantity >= selection.Quantity && item.IdentityCurrent()
                    && (item.Item == null || (item.Quantity == 1 && selection.Quantity == 1))
                    && required.ContainsKey(item.Id), "Craft ingredient lacks exact unequipped native custody.");
                required[item.Id] -= selection.Quantity;
                NativeCraftRequire(required[item.Id] >= 0, "Craft selection exceeds the native recipe quantity.");
                inputs[i] = new NativeCraftInput(item, selection.Quantity,
                    item.Item == null ? null : PlayerInventoryBaseline.CaptureModules(item.Item));
            }
            foreach (long remaining in required.Values)
                NativeCraftRequire(remaining == 0, "Craft selection does not supply the complete exact recipe.");
            return inputs;
        }

        private void ValidateCraftCapacity(InventoryCraftRecipe recipe)
        {
            var added = new Dictionary<int, int>();
            foreach (int id in recipe.Results) { added.TryGetValue(id, out int count); added[id] = checked(count + 1); }
            foreach (var pair in added)
            {
                long total = pair.Value;
                foreach (var item in _items.Values) if (!item.Consumed && item.Id == pair.Key) total += item.Quantity;
                var data = ItemDatabaseManager.ItemById(pair.Key);
                NativeCraftRequire(data != null && !data.WasCollected && data && !data._canNotBeStored
                    && total <= data._maxAmount && total < int.MaxValue, "Craft results exceed native inventory capacity.");
            }
        }

        private bool NativeCraftCustodyCurrent(NativeCraftReservation craft)
        {
            if (!craft.Recipe.Current() || !Current(out var target)
                || !craft.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
            for (int i = 0; i < craft.Inputs.Length; i++)
            {
                var input = craft.Inputs[i];
                var item = input.Record;
                int remaining = input.BeforeQuantity - (i < craft.Removed ? input.Quantity : 0);
                if (!_items.TryGetValue(item.Key, out var owned) || !ReferenceEquals(owned, item)
                    || item.Quantity != remaining || item.Consumed != (remaining == 0) || !item.IdentityCurrent()
                    || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0
                    || (item.Item != null && !PlayerInventoryBaseline.JsonEqual(input.Modules,
                        PlayerInventoryBaseline.CaptureModules(item.Item)))) return false;
            }
            if (craft.Outputs.Count != craft.Recipe.Results.Length) return false;
            for (int i = 0; i < craft.Outputs.Count; i++)
            {
                var output = craft.Outputs[i];
                var item = output.Record;
                if (item == null || !item.IdentityCurrent() || item.Quantity != 1 || item.Consumed
                    || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0
                    || output.Native == null || output.Native.WasCollected || output.Native._itemID != item.Id
                    || !PlayerInventoryBaseline.JsonEqual(output.Modules,
                        PlayerInventoryBaseline.CaptureModules(output.Native))) return false;
                bool owned = _items.TryGetValue(item.Key, out var existing);
                if (owned != (i < craft.Delivered) || (owned && !ReferenceEquals(existing, item))) return false;
            }
            return true;
        }

        private int ReadCraftStoredCount(int id)
        {
            int total = 0;
            foreach (var item in _items.Values)
                if (!item.Consumed && item.Id == id && item.HeldSlot < 0 && !item.Clothing && item.ArmourSlot < 0)
                    total = checked(total + item.Quantity);
            return total;
        }

        private PlayerInventoryFrame NativeCraftFrame(ulong transaction, ulong key, int id, int quantity,
            int before, PlayerInventoryMessage kind, string modules = null)
            => new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId, AcceptedBaselineFingerprint,
                transaction, key, id, quantity, before, kind, modules);

        private void FailCraft(NativeCraftReservation craft, string detail)
        {
            craft.State = CraftState.Unconfirmed;
            SetCraftReceipt(craft, detail);
            Fault(detail);
            MutationEnforcement?.FailCraftMutation(detail);
        }

        private void SetCraftReceipt(NativeCraftReservation craft, string detail)
            => _nativeCraftReceipt = new(craft.Transaction, craft.State, craft.Removed, craft.Inputs.Length,
                craft.Delivered, craft.Recipe.Results.Length, craft.Transaction + 1, craft.LastTransaction, detail)
            { RecipeIndex = craft.Recipe.Index, RecipeFingerprint = craft.Recipe.Fingerprint, EffectsApplied = craft.EffectsApplied };

        private static void NativeCraftRequire(bool condition, string detail)
            => PlayerInventoryBaseline.Require(condition, detail);

        private sealed class NativeCraftInput(ItemRecord record, int quantity, string modules)
        {
            internal readonly ItemRecord Record = record;
            internal readonly int Quantity = quantity, BeforeQuantity = record.Quantity;
            internal readonly string Modules = modules;
        }

        private sealed class NativeCraftOutput(ItemInstance native)
        {
            internal readonly ItemInstance Native = native;
            internal ItemRecord Record;
            internal string Modules, ModuleHash;
        }

        private sealed class NativeCraftReservation(ulong transaction, ulong lastTransaction,
            InventoryCraftRecipe recipe, NativeCraftInput[] inputs, EquipmentState equipment)
        {
            internal readonly ulong Transaction = transaction, LastTransaction = lastTransaction;
            internal readonly InventoryCraftRecipe Recipe = recipe;
            internal readonly NativeCraftInput[] Inputs = inputs;
            internal readonly EquipmentState Equipment = equipment;
            internal readonly List<NativeCraftOutput> Outputs = new();
            internal CraftState State = CraftState.Reserved;
            internal int Removed, Delivered;
            internal bool Awaiting;
            internal NativeCraftEffects Effects;
            internal bool EffectsAttempted, EffectsApplied;
            internal PlayerInventoryFrame Command;
        }
    }
}
