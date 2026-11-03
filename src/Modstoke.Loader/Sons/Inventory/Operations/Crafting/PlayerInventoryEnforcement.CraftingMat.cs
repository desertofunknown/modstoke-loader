// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;
using TheForest.Items.Inventory;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        // Logical account quantities include escrow. _currentSnapshot describes physical storage.
        // A native grant/debit changes logical ownership; moving to/from the mat does not.
        private readonly List<CraftMatItem> _craftMat = new();
        private readonly List<CraftMatItem> _craftResults = new();
        private CraftMatTransfer _craftTransfer;
        private int _craftMatRemoveId, _craftMatRemoveCount, _craftDebitIndex;
        private bool _craftMatBindings;

        public bool HasNativeCraftMatCustody => _craftMat.Count != 0 || _craftResults.Count != 0 || _craftTransfer != null;
        internal bool CanCloseNativeCraftInventory => !_craftUiPending && _craftTransfer == null && !_craftCompleting;

        // The central installer owns the typed writers and Close fence; route binding
        // activates this dispatcher only after both hook sets installed successfully.
        private void BindCraftMatDispatcher()
        {
            if (!OnThread || !_hooksInstalled || !_craftHooksInstalled || _writerDepth != 0
                || _authority != null || _craftSource != null || _busy || IsDisposed || IsFaulted)
                throw new InvalidOperationException("Crafting dispatcher requires installed source inventory and crafting hooks.");
            _craftMatBindings = true;
        }

        private sealed class CraftMatItem(SourceItem source, ItemInstance native, bool result)
        {
            internal SourceItem Source = source;
            internal ItemInstance Native = native;
            internal readonly bool Result = result;
            internal bool Debited, Placed;
            internal string Modules;
            internal PlayerInventoryModuleState ModuleState;
            internal CraftingResultLayoutItem Layout;
            private IntPtr _moduleList;
            private IntPtr[] _moduleIdentities;

            internal bool Current() => Native != null && !Native.WasCollected && Native._itemID == Source.Entry.ItemId
                && ModuleIdentitiesCurrent()
                && (Source.Instance == null ? Native._modules != null && Native._modules.Count == 0
                    : Native.Pointer == Source.Instance.Pointer
                        && PlayerInventoryBaseline.MatchesInstance(Source.Entry, Native))
                && (Modules == null || PlayerInventoryBaseline.JsonEqual(Modules,
                    PlayerInventoryBaseline.CaptureModules(Native)))
                && ModuleState == PlayerInventoryBaseline.CaptureModuleState(Native);

            internal void Capture()
            {
                if (Native == null || Native.WasCollected || Native._modules == null || Native._modules.WasCollected)
                    throw new InvalidOperationException("Mat instance modules are unavailable.");
                _moduleList = Native._modules.Pointer;
                _moduleIdentities = new IntPtr[Native._modules.Count];
                for (int i = 0; i < _moduleIdentities.Length; i++)
                {
                    var module = Native._modules[i];
                    if (module == null || module.WasCollected) throw new InvalidOperationException("Mat module identity is unavailable.");
                    _moduleIdentities[i] = module.Pointer;
                }
                Modules = PlayerInventoryBaseline.CaptureModules(Native);
                ModuleState = PlayerInventoryBaseline.CaptureModuleState(Native);
                if (!Current()) throw new InvalidOperationException("Mat instance no longer matches its exact account key.");
            }

            private bool ModuleIdentitiesCurrent()
            {
                if (Native._modules == null || Native._modules.WasCollected || Native._modules.Pointer != _moduleList
                    || _moduleIdentities == null || Native._modules.Count != _moduleIdentities.Length) return false;
                for (int i = 0; i < _moduleIdentities.Length; i++)
                    if (Native._modules[i] == null || Native._modules[i].WasCollected
                        || Native._modules[i].Pointer != _moduleIdentities[i]) return false;
                return true;
            }
        }

        private sealed class CraftMatTransfer(CraftMatItem item, bool adding, int depth)
        {
            internal readonly CraftMatItem Item = item;
            internal readonly bool Adding = adding;
            internal readonly int Depth = depth;
            internal int StoredIndex = -1;
            internal bool InventoryPermit = true, ManagerPermit = true, Removed;
            internal bool ManagerObserved, InventoryObserved, ZeroObserved, ZeroEntered;
        }

        private bool CraftMatIdle() => OnThread && _craftMatBindings && !_busy && _writerDepth == 0
            && !IsDisposed && !IsFaulted && IsArmed && !_craftUiPending && !HasPendingCustody
            && !InventoryFrozen && CraftSourceCurrent() && Unchanged() && CraftMatCurrent();

        private int MatCount(ulong key)
        {
            int count = 0;
            foreach (var item in _craftMat) if (item.Source.Entry.Key == key && !item.Debited) count++;
            foreach (var item in _craftResults) if (item.Source.Entry.Key == key) count++;
            return count;
        }

        // GrantCapacity keeps its logical count/capacity checks, but the native manager
        // bucket excludes escrow. This does not authorize a grant or alter its quantity.
        internal bool TryReadCraftPhysicalStoredCount(int id, long logical, out long physical)
        {
            physical = logical;
            if (!HasNativeCraftMatCustody) return true;
            if (!OnThread || !CraftMatCurrent()) return false;
            foreach (var item in _craftMat) if (!item.Debited && item.Source.Entry.ItemId == id) physical--;
            foreach (var item in _craftResults) if (item.Source.Entry.ItemId == id) physical--;
            return physical >= 0;
        }

        private bool CraftMatCurrent()
        {
            if (_craftSource == null) return !HasNativeCraftMatCustody;
            var totals = new Dictionary<int, int>();
            foreach (var item in _craftMat)
            {
                if (!item.Current()) return false;
                var group = FindCraftIngredientGroup(item.Source.Entry.ItemId);
                if (group?._layoutItems == null) return false;
                int matches = 0;
                foreach (var layout in group._layoutItems)
                    if (layout != null && !layout.WasCollected && layout && layout.gameObject.activeSelf
                        && layout.ItemInstance?.Pointer == item.Native.Pointer) matches++;
                if (matches != 1) return false;
                totals.TryGetValue(item.Source.Entry.ItemId, out int count);
                totals[item.Source.Entry.ItemId] = count + 1;
            }
            var ingredients = _craftSource._CurrentIngredients_k__BackingField;
            if (ingredients == null || ingredients.WasCollected || ingredients.Count != totals.Count) return false;
            foreach (var row in ingredients)
                if (row == null || row.WasCollected || !totals.Remove(row.ItemId, out int count) || row.Count != count) return false;
            foreach (var item in _craftResults)
                if (!item.Current() || (item.Placed && (item.Layout == null || item.Layout.WasCollected || !item.Layout
                    || !item.Layout.gameObject.activeSelf || item.Layout.ItemInstance?.Pointer != item.Native.Pointer))) return false;
            return true;
        }

        private IngredientLayoutItemGroup FindCraftIngredientGroup(int id)
        {
            IngredientLayoutItemGroup found = null;
            if (_craftSource?._ingredientLayoutItemGroups == null) return null;
            foreach (var group in _craftSource._ingredientLayoutItemGroups)
            {
                if (group == null || group.WasCollected || !group || group._itemId != id) continue;
                if (found != null) return null;
                found = group;
            }
            return found;
        }

        private bool BeginNativeMatStage(InventoryLayoutItemGroup group)
        {
            if (!CraftMatIdle() || _craftEffectsReplyPending || _craftContinuations.Count != 0 || group == null || group.WasCollected || !group
                || group._inventory?.Pointer != _inventory.Pointer || group._layoutItems == null
                || _inventory._currentView != PlayerInventory.PlayerViews.Inventory
                || _craftSource._inCreativeMode || _craftResults.Count != 0) return false;
            InventoryLayoutItem highlighted = null;
            foreach (var layout in group._layoutItems)
                if (layout != null && !layout.WasCollected && layout && layout.IsHighlighted) { highlighted = layout; break; }
            if (highlighted == null) return false;
            var data = group.GetItemData();
            if (data == null || data.WasCollected || !data || data._eachInstanceIsUnique != group._isOneToOneCollection) return false;
            SourceItem source = null;
            foreach (var item in _sourceItems)
                if (item.Entry.ItemId == group._itemId && Stowed(item.Entry) && item.Quantity > MatCount(item.Entry.Key)
                    && (data._eachInstanceIsUnique ? item.Instance?.Pointer == highlighted.ItemInstance?.Pointer : item.Instance == null))
                { source = item; break; }
            if (source == null || _craftMat.Count >= PlayerCraftWire.MaximumSelections) return false;
            int storedIndex = FindStoredEntry(_currentSnapshot, source.Entry.ItemId, source.Instance);
            if (storedIndex < 0) return false;
            var destination = FindCraftIngredientGroup(group._itemId);
            if (destination?._layoutItems == null || destination._layoutItems.WasCollected
                || destination.GetActiveVisualCount() >= destination._layoutItems.Count) return false;
            var held = new CraftMatItem(source, source.Instance, false);
            if (held.Native != null) held.Capture();
            _craftMat.Add(held); // Reserve the key and instance before the native action can remove it.
            _craftTransfer = new(held, false, _writerDepth) { StoredIndex = storedIndex };
            _busy = true;
            return true;
        }

        private bool AdmitNativeMatIngredient(ItemInstance native)
        {
            var transfer = _craftTransfer;
            if (transfer == null || transfer.Adding || transfer.Removed || transfer.InventoryPermit || transfer.ManagerPermit
                || !transfer.ManagerObserved || !transfer.InventoryObserved
                || native == null || native.WasCollected || native._itemID != transfer.Item.Source.Entry.ItemId
                || (transfer.Item.Source.Instance != null && native.Pointer != transfer.Item.Source.Instance.Pointer)) return false;
            var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
            int index = transfer.StoredIndex;
            if (index < 0 || !_currentSnapshot.ConfirmsRemoval(after, index, 1, transfer.Item.Source.Instance))
                throw new InvalidOperationException("Native mat action did not transfer exactly one reserved ingredient.");
            if (transfer.Item.Native == null) { transfer.Item.Native = native; transfer.Item.Capture(); }
            else if (!transfer.Item.Current()) throw new InvalidOperationException("Native ingredient modules changed during mat transfer.");
            transfer.Removed = true;
            transfer.Item.Placed = true;
            _currentSnapshot = after;
            return true;
        }

        private void FinishNativeMatStage(bool ran, Exception error)
        {
            var transfer = _craftTransfer;
            try
            {
                if (transfer == null) return;
                if (error != null || !ran) throw new InvalidOperationException("Native mat action did not finish.", error);
                if (transfer.InventoryPermit && transfer.ManagerPermit && !transfer.Removed)
                { _craftMat.Remove(transfer.Item); return; } // Native mid-action/capacity checks declined before writes.
                if (!transfer.Removed || !Unchanged() || !CraftMatCurrent())
                    throw new InvalidOperationException("Native mat action left uncertain ingredient custody.");
            }
            catch (Exception failure) { Fail(failure.Message); }
            finally { _craftTransfer = null; _busy = false; }
        }

        private bool CraftTransferCurrent(bool adding, int depth) => OnThread && _craftMatBindings && _busy
            && !IsDisposed && !IsFaulted && IsArmed && !InventoryFrozen && CraftSourceCurrent()
            && _craftTransfer is { } transfer && transfer.Adding == adding && _writerDepth == transfer.Depth + depth;

        // Parent calls these only after its existing lifecycle/protected-writer checks and EnterWriter.
        internal bool TryPermitCraftRemoveItem(PlayerInventory inventory, int id, int amount, bool overflow,
            bool equipPrevious, bool quickSelect, ItemInstance instance, bool instantDestroy)
        {
            if (!CraftTransferCurrent(false, 1) || inventory?.Pointer != _inventory.Pointer
                || !_craftTransfer.InventoryPermit || !_craftTransfer.ManagerPermit || amount != 1 || overflow
                || !equipPrevious || !quickSelect || instantDestroy || id != _craftTransfer.Item.Source.Entry.ItemId
                || (instance?.Pointer ?? IntPtr.Zero) != (_craftTransfer.Item.Source.Instance?.Pointer ?? IntPtr.Zero)) return false;
            _craftTransfer.InventoryPermit = false;
            return true;
        }

        internal bool TryPermitCraftRemoveStack(ItemInstanceManager manager, int id, int amount, bool suppressCallback)
        {
            if (!CraftTransferCurrent(false, 2) || manager?.Pointer != _manager.Pointer || _craftTransfer.InventoryPermit
                || id != _craftTransfer.Item.Source.Entry.ItemId || suppressCallback) return false;
            if (_craftTransfer.Item.Source.Instance != null)
            {
                // Native RemoveItem emits RemoveItems(id, 0) after a successful unique debit.
                // Its failure fallback RemoveItems(id, 1) must never consume another instance.
                if (amount != 0 || _craftTransfer.ManagerPermit || !_craftTransfer.ManagerObserved
                    || _craftTransfer.ZeroEntered || _craftTransfer.InventoryObserved) return false;
                _craftTransfer.ZeroEntered = true;
                return true;
            }
            if (!_craftTransfer.ManagerPermit || amount != 1) return false;
            _craftTransfer.ManagerPermit = false;
            return true;
        }

        internal bool TryPermitCraftRemoveInstance(ItemInstanceManager manager, ItemInstance instance)
        {
            if (!CraftTransferCurrent(false, 2) || manager?.Pointer != _manager.Pointer || _craftTransfer.InventoryPermit
                || !_craftTransfer.ManagerPermit || instance == null || instance.WasCollected
                || instance.Pointer != _craftTransfer.Item.Source.Instance?.Pointer) return false;
            _craftTransfer.ManagerPermit = false;
            return true;
        }

        internal void ObserveCraftRemoveInstance(bool admitted, bool ran, bool result, Exception error)
        {
            if (!admitted) return;
            if (!CraftTransferCurrent(false, 2) || !ran || !result || error != null)
            { Fail("Native unique mat debit did not complete its admitted writer."); return; }
            _craftTransfer.ManagerObserved = true;
        }

        internal void ObserveCraftRemoveStack(bool admitted, bool ran, int result, Exception error)
        {
            if (!admitted) return;
            if (!CraftTransferCurrent(false, 2) || !ran || error != null
                || result != (_craftTransfer.Item.Source.Instance == null ? 1 : 0))
            { Fail("Native stack mat debit/zero-count notification did not complete."); return; }
            if (_craftTransfer.Item.Source.Instance == null) _craftTransfer.ManagerObserved = true;
            else _craftTransfer.ZeroObserved = true;
        }

        internal void ObserveCraftRemoveItem(bool admitted, bool ran, bool result, Exception error)
        {
            if (!admitted) return;
            if (!CraftTransferCurrent(false, 1) || !ran || !result || error != null || !_craftTransfer.ManagerObserved
                || (_craftTransfer.Item.Source.Instance != null && !_craftTransfer.ZeroObserved))
            { Fail("Native inventory mat debit or callbacks did not complete."); return; }
            _craftTransfer.InventoryObserved = true;
        }

        internal bool TryPermitCraftAddItem(PlayerInventory inventory, int id, int amount, bool preventAutoEquip,
            bool wasCrafted, ItemInstance instance)
        {
            if (!CraftTransferCurrent(true, 1) || inventory?.Pointer != _inventory.Pointer || !_craftTransfer.InventoryPermit
                || !_craftTransfer.ManagerPermit || id != _craftTransfer.Item.Source.Entry.ItemId || amount != 1
                || !preventAutoEquip || wasCrafted || instance?.Pointer != _craftTransfer.Item.Native.Pointer) return false;
            _craftTransfer.InventoryPermit = false;
            return true;
        }

        internal bool TryPermitCraftAddStack(ItemInstanceManager manager, int id, int amount, bool suppressCallback)
        {
            if (!CraftTransferCurrent(true, 2) || manager?.Pointer != _manager.Pointer || _craftTransfer.InventoryPermit
                || !_craftTransfer.ManagerPermit || _craftTransfer.Item.Source.Instance != null
                || id != _craftTransfer.Item.Source.Entry.ItemId || amount != 1 || suppressCallback) return false;
            _craftTransfer.ManagerPermit = false;
            return true;
        }

        internal bool TryPermitCraftAddInstance(ItemInstanceManager manager, ItemInstance instance, bool suppressCallback)
        {
            if (!CraftTransferCurrent(true, 2) || manager?.Pointer != _manager.Pointer || _craftTransfer.InventoryPermit
                || !_craftTransfer.ManagerPermit || _craftTransfer.Item.Source.Instance == null || suppressCallback
                || instance?.Pointer != _craftTransfer.Item.Native.Pointer) return false;
            _craftTransfer.ManagerPermit = false;
            return true;
        }

        private void ReturnCraftMatItem(CraftMatItem item, bool clearVisual = true)
        {
            if (item.Debited || !item.Current()) throw new InvalidOperationException("Consumed or changed mat custody cannot be returned.");
            bool hadStorage = _manager._itemsMap.ContainsKey(item.Source.Entry.ItemId);
            _craftTransfer = new(item, true, _writerDepth);
            try
            {
                // This is a transfer of the retained instance, never a grant or a second ItemCrafted.
                if (!_inventory.AddItem(item.Source.Entry.ItemId, 1, true, false, item.Native)
                    || _craftTransfer.InventoryPermit || _craftTransfer.ManagerPermit)
                    throw new InvalidOperationException("Exact mat return did not traverse both native writers.");
                var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
                int index = FindStoredEntry(after, item.Source.Entry.ItemId, item.Source.Instance);
                var bucket = hadStorage ? IntPtr.Zero : _manager._itemsMap[item.Source.Entry.ItemId].Pointer;
                if (index < 0 || !after.ConfirmsRemoval(_currentSnapshot, index, 1, item.Source.Instance, bucket) || !item.Current())
                    throw new InvalidOperationException("Mat return changed other inventory custody.");
                _currentSnapshot = after;
                if (!clearVisual) return; // Native reuse event runs before ingredient removal.
                if (item.Result)
                {
                    item.Layout.Reset();
                    item.Layout.gameObject.SetActive(false);
                    _craftResults.Remove(item);
                }
                else
                {
                    RemoveCraftMatVisual(item.Source.Entry.ItemId, 1);
                    _craftMat.Remove(item);
                }
            }
            finally { _craftTransfer = null; }
        }

        private void RemoveCraftMatVisual(int id, int count)
        {
            _craftMatRemoveId = id;
            _craftMatRemoveCount = count;
            try
            {
                _craftSource.RemoveIngredient(id, count);
                if (_craftMatRemoveCount != 0) throw new InvalidOperationException("Native ingredient removal hook did not consume its permit.");
            }
            finally { _craftMatRemoveCount = 0; }
        }

        private void ReturnCraftIngredients(int id, bool all)
        {
            if (!CraftMatIdle()) return;
            _busy = true;
            try
            {
                for (int i = _craftMat.Count - 1; i >= 0; i--)
                {
                    if (!all && _craftMat[i].Source.Entry.ItemId != id) continue;
                    ReturnCraftMatItem(_craftMat[i]);
                    if (!all) break;
                }
                if (!Unchanged() || !CraftMatCurrent()) throw new InvalidOperationException("Ingredient cancellation readback differs.");
            }
            catch (Exception error) { Fail("Mat cancellation is uncertain: " + error.Message); }
            finally { _busy = false; }
        }

        // Parent calls first in ApplyRemoval, after its ordinary lifetime/Unchanged checks.
        // true means handled, including refusal; never fall through to a second manager removal.
        internal bool TryApplyCraftMatRemoval(in PlayerInventoryFrame frame, out bool applied)
        {
            applied = false;
            ulong key = frame.Key;
            if (!_craftMat.Exists(item => item.Source.Entry.Key == key)) return false;
            if (!_craftUiPending || !_craftSourcePending || _craftSourceReply.Kind != PlayerCraftMessage.Reserved
                || _craftDebitIndex >= _craftSourceRows.Length || !CraftMatCurrent() || !Unchanged()) return true;
            var selection = _craftSourceRows[_craftDebitIndex];
            if (frame.Transaction != _craftSourceReply.FirstTransaction + (ulong)_craftDebitIndex
                || frame.Key != selection.InstanceKey || frame.Quantity != selection.Quantity
                || frame.BeforeQuantity != _craftSourceBefore[_craftDebitIndex]) return true;
            var matches = _craftMat.FindAll(item => !item.Debited && item.Source.Entry.Key == key);
            if (matches.Count != frame.Quantity || matches.Count == 0 || matches[0].Source.Entry.ItemId != frame.ItemId
                || matches[0].Source.Quantity != frame.BeforeQuantity) return true;
            foreach (var item in matches) item.Debited = true;
            matches[0].Source.Quantity -= frame.Quantity;
            _craftDebitIndex++;
            applied = true;
            return true;
        }

        // Parent calls first in ApplyPerishable. Storage is unchanged while the retained mat
        // instance receives the same authenticated absolute module projection as a stored item.
        internal bool TryApplyCraftMatPerishable(in PlayerInventoryFrame frame, out bool applied)
        {
            applied = false;
            ulong key = frame.Key;
            var held = _craftMat.Find(item => item.Source.Entry.Key == key)
                ?? _craftResults.Find(item => item.Source.Entry.Key == key);
            if (held == null) return false;
            if (!OnThread || InventoryFrozen || !Current() || !Unchanged() || !CraftMatCurrent() || held.Debited
                || held.Source.Quantity != 1 || held.Source.Instance == null || held.Source.Entry.ItemId != frame.ItemId
                || PlayerInventoryWire.HashModules(held.Modules) != frame.BeforeModuleFingerprint) return true;
            var instance = held.Native;
            var module = FindPerishable(instance);
            if (module == null) return true;
            var modulesPointer = instance._modules.Pointer;
            var pointers = new IntPtr[instance._modules.Count];
            for (int i = 0; i < pointers.Length; i++) pointers[i] = instance._modules[i].Pointer;
            int moduleId = module.GetSaveData().ModuleId, moduleIndex = -1;
            var saved = instance.GetModuleSaveData();
            for (int i = 0; i < saved.Count; i++) if (saved[i].ModuleId == moduleId) moduleIndex = i;
            ProjectPerishable(module, frame);
            string after = PlayerInventoryBaseline.CaptureModules(instance);
            if (!Current() || !Unchanged() || instance._modules.Pointer != modulesPointer || instance._modules.Count != pointers.Length
                || PlayerInventoryWire.HashModules(after) != frame.ModuleFingerprint
                || !PlayerInventoryBaseline.OnlyModuleChanged(held.Modules, after, moduleIndex)
                || PlayerInventoryBaseline.CaptureModuleState(instance) != held.ModuleState) return true;
            for (int i = 0; i < pointers.Length; i++) if (instance._modules[i]?.Pointer != pointers[i]) return true;
            int index = _sourceItems.IndexOf(held.Source);
            if (index < 0) return true;
            var entry = held.Source.Entry;
            var replacement = new SourceItem(new PlayerInventoryBaseline.Entry
            {
                Key = entry.Key, ItemId = entry.ItemId, Quantity = 1, Modules = after, ModuleState = entry.ModuleState,
                EquipmentSlot = entry.EquipmentSlot, ClothingSlots = entry.ClothingSlots, ArmourSlot = entry.ArmourSlot,
                ArmourPoints = entry.ArmourPoints, DefinitionOnly = entry.DefinitionOnly
            }, instance);
            _sourceItems[index] = held.Source = replacement;
            held.Modules = after;
            applied = CraftMatCurrent();
            return true;
        }
    }
}
