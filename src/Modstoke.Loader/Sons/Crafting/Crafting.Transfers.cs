// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine.InputSystem;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        /// <summary>
        /// Stages one item through the native group's OnAddItemToMat action. Pass the actual
        /// local inventory group and its visible source layout, not a constructed ItemInstance.
        /// Unique items must be the exact stored instance; stack instances are created by the
        /// game as in normal crafting. Uses native hover enter/exit if the source is not already
        /// selected; refuses another highlighted item in this group instead of changing it.
        /// Native inventory hover exit can defer deselection until the mouse moves.
        /// Full nonunique mat layouts can retain additional module-free ingredients as native
        /// counts only; those transfers return no destination instance or identity guarantee.
        /// Nonunique source modules cannot be copied by this native action and are refused,
        /// even when a physical mat slot is free.
        /// The native UI remains open. True confirms stored count/instance and mat readback.
        /// False can be Unconfirmed after an attempt; never automatically retry or refund it.
        /// </summary>
        public static bool TryStageIngredient(InventoryLayoutItemGroup group, InventoryLayoutItem source,
            out CraftingTransferResult result)
        {
            CraftTransferScope scope = null;
            var attempted = false;
            var itemId = 0;
            try
            {
                scope = new CraftTransferScope();
                RequireCraftTransfer(CraftExecutionLive(group) && group.isActiveAndEnabled
                    && CraftExecutionLive(group._inventory) && group._inventory.Pointer == scope.Inventory.Pointer,
                    "The source group must belong to the live local inventory.");
                RequireCraftTransfer(CraftExecutionLive(source) && source.isActiveAndEnabled
                    && source._isInitialized && source._isValidCraftingIngredient && !source._interactionBlocked,
                    "The source must be a live, interactable native crafting ingredient layout.");
                itemId = group._itemId;
                var data = group.GetItemData();
                RequireCraftTransfer(CraftExecutionLive(data) && data.Id == itemId && itemId > 0
                    && group._isOneToOneCollection == data._eachInstanceIsUnique,
                    "The source group's item definition or unique-instance mode is inconsistent.");
                bool unique = data._eachInstanceIsUnique;
                var destination = FindTransferIngredientGroup(scope.System, itemId);
                RequireCraftTransfer(destination._isOneToOneCollection == unique,
                    "The mat group and stored item disagree about instance ownership.");
                var before = ReadTransferLayouts(destination._layoutItems, itemId);
                int matCount = scope.System.GetIngredientCount(itemId);
                RequireIngredientTransferLayout(destination, before, matCount, unique);
                bool countOnly = !unique && before.Count == destination._layoutItems.Count;
                RequireCraftTransfer(matCount < int.MaxValue && (!unique || before.Count < destination._layoutItems.Count)
                    && scope.System.CanHoldMoreOfIngredient(itemId),
                    "The native mat must permit this ingredient and have a free slot for a unique instance.");
                var compressed = countOnly ? new CraftCompressedIngredientTransfer(destination, before, itemId) : null;
                var representative = !unique ? source.ItemInstance : null;
                var representativeModules = !unique ? new CraftModuleIdentity(representative, itemId) : null;
                RequireCraftTransfer(unique || representativeModules.Count == 0,
                    "Native nonunique staging creates a fresh instance and cannot preserve source modules.");
                var stored = ReadTransferStorage(scope.Manager, itemId, unique);
                RequireCraftTransfer(stored.Count > 0, "No stored item is available for native staging.");
                var exact = unique ? source.ItemInstance : null;
                var modules = unique ? new CraftModuleIdentity(exact, itemId) : null;
                RequireCraftTransfer(!unique || stored.Instances.Contains(exact.Pointer),
                    "The selected unique instance is not in this local inventory's storage.");

                scope.Select(group._layoutItems, source);
                scope.RequireCurrent();
                scope.RequireSelected(group._layoutItems, source);
                RequireCraftTransfer(group.isActiveAndEnabled && group._itemId == itemId
                    && group._inventory?.Pointer == scope.Inventory.Pointer && group._isOneToOneCollection == unique
                    && source.isActiveAndEnabled && source._isValidCraftingIngredient && !source._interactionBlocked
                    && FindTransferIngredientGroup(scope.System, itemId).Pointer == destination.Pointer
                    && destination._isOneToOneCollection == unique && scope.System.CanHoldMoreOfIngredient(itemId)
                    && TransferStorageSame(stored, ReadTransferStorage(scope.Manager, itemId, unique))
                    && TransferLayoutsSame(before, ReadTransferLayouts(destination._layoutItems, itemId))
                    && scope.System.GetIngredientCount(itemId) == matCount
                    && (!countOnly || compressed.Current())
                    && (unique || (source.ItemInstance?.Pointer == representative.Pointer && representativeModules.Current()))
                    && (!unique || (source.ItemInstance?.Pointer == exact.Pointer && modules.Current())),
                    "Native selection changed ingredient custody; no transfer was attempted.");
                // IL2CPP unboxes this value before invoking the native callback.
                var inputContext = new InputAction.CallbackContext();
                attempted = true;
                group.OnAddItemToMat(inputContext);
                scope.ReleaseSelection();
                scope.RequireCurrent();
                RequireCraftTransfer(FindTransferIngredientGroup(scope.System, itemId).Pointer == destination.Pointer,
                    "The native destination group changed during staging; transfer custody is unconfirmed.");
                var after = ReadTransferLayouts(destination._layoutItems, itemId);
                var afterStored = ReadTransferStorage(scope.Manager, itemId, unique);
                var added = countOnly ? null : FindAddedTransferLayout(before, after);
                bool complete = (countOnly ? compressed.Current() : added != null)
                    && scope.System.GetIngredientCount(itemId) == matCount + 1
                    && TransferStorageDelta(stored, afterStored, unique ? exact.Pointer : IntPtr.Zero, -1)
                    && (!unique || (added.Instance.Pointer == exact.Pointer && modules.Current()));
                result = TransferResult(CraftingTransferKind.StageIngredient, attempted, itemId,
                    complete, scope, complete && !countOnly ? added.Instance : null, complete && unique,
                    complete ? countOnly ? "One native stack ingredient count was transferred to the full mat layout; no per-instance custody is represented."
                        : "One native ingredient was transferred from storage to the mat."
                        : "Native staging returned without matching storage and mat custody. Do not retry or refund automatically.");
                return complete;
            }
            catch (Exception error)
            {
                result = TransferResult(CraftingTransferKind.StageIngredient, attempted, itemId,
                    false, scope, null, false, error.Message);
                return false;
            }
            finally { scope?.Dispose(); }
        }

        /// <summary>
        /// Returns one mat ingredient through native OnRemoveItemFromMat. The requested
        /// layout must be the LAST populated slot in this group: the native return path is LIFO,
        /// even when another slot is highlighted. Uses native hover selection, not field edits.
        /// Requires inventory capacity. Excess nonunique counts are returned without removing
        /// their representative layout; all such layouts must be module-free. Unique instances and
        /// their modules are retained. Nonunique module-bearing rows are refused because the
        /// game's return path passes no instance for stacks and cannot preserve those modules.
        /// Item 483's visible-row return is refused when native vitals report player death,
        /// because that branch skips the inventory add while still removing the ingredient.
        /// </summary>
        public static bool TryReturnIngredient(IngredientLayoutItemGroup group, IngredientLayoutItem source,
            out CraftingTransferResult result)
        {
            CraftTransferScope scope = null;
            var attempted = false;
            var itemId = 0;
            try
            {
                scope = new CraftTransferScope();
                RequireCraftTransfer(CraftExecutionLive(group), "A live native ingredient group is required.");
                itemId = group._itemId;
                RequireCraftTransfer(itemId > 0, "The ingredient has no valid native item identity.");
                RequireCraftTransfer(FindTransferIngredientGroup(scope.System, itemId).Pointer == group.Pointer,
                    "The ingredient group does not belong to the current local crafting mat.");
                var before = ReadTransferLayouts(group._layoutItems, itemId);
                RequireCraftTransfer(CraftExecutionLive(source) && source.isActiveAndEnabled && before.Count > 0
                    && before[before.Count - 1].Layout.Pointer == source.Pointer,
                    "Native ingredient return can only return the last populated layout slot in the group.");
                int matCount = scope.System.GetIngredientCount(itemId);
                var exact = before[before.Count - 1].Instance;
                var data = exact.Data;
                RequireCraftTransfer(CraftExecutionLive(data) && data.Id == itemId
                    && data._eachInstanceIsUnique == group._isOneToOneCollection,
                    "The ingredient definition and native group ownership mode differ.");
                bool unique = data._eachInstanceIsUnique;
                RequireIngredientTransferLayout(group, before, matCount, unique);
                bool countOnly = !unique && matCount > before.Count;
                var compressed = countOnly ? new CraftCompressedIngredientTransfer(group, before, itemId) : null;
                RequireNativeIngredientReturn(itemId, countOnly);
                var modules = new CraftModuleIdentity(exact, itemId);
                RequireCraftTransfer(unique || modules.Count == 0,
                    "Native stack ingredient return discards the instance; module-bearing nonunique rows cannot be returned safely.");
                var stored = ReadTransferStorage(scope.Manager, itemId, unique);
                RequireCraftTransfer(!stored.Instances.Contains(exact.Pointer) && scope.Inventory.HasRoomFor(itemId, 1),
                    "The ingredient is already stored or inventory capacity is unavailable.");
                scope.Select(group._layoutItems, source);
                scope.RequireCurrent();
                scope.RequireSelected(group._layoutItems, source);
                RequireCraftTransfer(FindTransferIngredientGroup(scope.System, itemId).Pointer == group.Pointer
                    && group._isOneToOneCollection == unique && source.isActiveAndEnabled
                    && modules.Current() && scope.Inventory.HasRoomFor(itemId, 1)
                    && TransferStorageSame(stored, ReadTransferStorage(scope.Manager, itemId, unique))
                    && TransferLayoutsSame(before, ReadTransferLayouts(group._layoutItems, itemId))
                    && scope.System.GetIngredientCount(itemId) == matCount && (!countOnly || compressed.Current()),
                    "Native selection changed ingredient custody or capacity; no transfer was attempted.");
                RequireNativeIngredientReturn(itemId, countOnly);
                var inputContext = new InputAction.CallbackContext();
                attempted = true;
                group.OnRemoveItemFromMat(inputContext);
                scope.ReleaseSelection();
                scope.RequireCurrent();
                RequireCraftTransfer(FindTransferIngredientGroup(scope.System, itemId).Pointer == group.Pointer,
                    "The native ingredient group changed during return; transfer custody is unconfirmed.");
                bool complete = scope.System.GetIngredientCount(itemId) == matCount - 1
                    && (countOnly ? compressed.Current()
                        : TransferLayoutRemoved(before, ReadTransferLayouts(group._layoutItems, itemId), source.Pointer))
                    && TransferStorageDelta(stored, ReadTransferStorage(scope.Manager, itemId, unique),
                        unique ? exact.Pointer : IntPtr.Zero, 1)
                    && (!unique || modules.Current());
                result = TransferResult(CraftingTransferKind.ReturnIngredient, attempted, itemId,
                    complete, scope, complete && unique ? exact : null, complete && unique,
                    complete ? countOnly ? "One excess native ingredient count was returned to local storage; the representative mat layouts are unchanged."
                        : "One native ingredient was returned to local storage."
                        : "Native return did not confirm both storage and mat custody. Do not retry or refund automatically.");
                return complete;
            }
            catch (Exception error)
            {
                result = TransferResult(CraftingTransferKind.ReturnIngredient, attempted, itemId,
                    false, scope, null, false, error.Message);
                return false;
            }
            finally { scope?.Dispose(); }
        }

        /// <summary>
        /// Collects one exact visible result through its native StashToInventory lifecycle,
        /// including native crafted notifications, layout reset and deactivation. Requires an
        /// idle open local inventory and capacity. No AddItem substitute or fake grant is used.
        /// The game can drop an output if AddItem fails despite preflight; Unconfirmed reports
        /// that possibility without claiming delivery or performing a compensating grant.
        /// </summary>
        public static bool TryCollectCraftedResult(CraftingResultLayoutItem source,
            out CraftingTransferResult result)
        {
            CraftTransferScope scope = null;
            var attempted = false;
            var itemId = 0;
            try
            {
                scope = new CraftTransferScope();
                RequireCraftTransfer(CraftExecutionLive(source) && source.isActiveAndEnabled,
                    "A live visible native crafting result is required.");
                var exact = source.ItemInstance;
                RequireCraftTransfer(exact is not null && !exact.WasCollected,
                    "The native no-instance result fallback can clear its layout without adding an item; collection requires an actual ItemInstance.");
                itemId = exact._itemID;
                var group = FindTransferResultGroup(scope.System, source, itemId);
                var before = ReadTransferLayouts(group._layoutItems, itemId, allowInactiveInstances: true);
                var data = exact.Data;
                RequireCraftTransfer(CraftExecutionLive(data) && data.Id == itemId,
                    "The result's native item definition is unavailable.");
                bool unique = data._eachInstanceIsUnique;
                var modules = new CraftModuleIdentity(exact, itemId);
                RequireCraftTransfer(unique || modules.Count == 0,
                    "Native stack storage cannot retain a nonunique result's per-instance modules.");
                var stored = ReadTransferStorage(scope.Manager, itemId, unique);
                RequireCraftTransfer(!stored.Instances.Contains(exact.Pointer) && scope.Inventory.HasRoomFor(itemId, 1),
                    "The result is already stored or inventory capacity is unavailable; nothing was collected.");
                scope.RequireCurrent();
                RequireCraftTransfer(source.ItemInstance?.Pointer == exact.Pointer && source.isActiveAndEnabled
                    && modules.Current() && TransferStorageSame(stored, ReadTransferStorage(scope.Manager, itemId, unique))
                    && TransferLayoutsSame(before, ReadTransferLayouts(group._layoutItems, itemId, allowInactiveInstances: true)),
                    "Result identity, modules or custody changed before native collection.");
                attempted = true;
                source.StashToInventory();
                scope.RequireCurrent();
                bool complete = TransferLayoutRemoved(before, ReadTransferLayouts(group._layoutItems, itemId, allowInactiveInstances: true), source.Pointer)
                    && TransferStorageDelta(stored, ReadTransferStorage(scope.Manager, itemId, unique),
                        unique ? exact.Pointer : IntPtr.Zero, 1)
                    && (!unique || modules.Current());
                result = TransferResult(CraftingTransferKind.CollectResult, attempted, itemId,
                    complete, scope, complete && unique ? exact : null, complete && unique,
                    complete ? "One native crafted result was collected into local storage."
                        : "Native collection did not confirm storage custody; the game may have dropped the output. Do not retry or grant a replacement automatically.");
                return complete;
            }
            catch (Exception error)
            {
                result = TransferResult(CraftingTransferKind.CollectResult, attempted, itemId,
                    false, scope, null, false, error.Message);
                return false;
            }
            finally { scope?.Dispose(); }
        }

        private sealed class CraftTransferScope : IDisposable
        {
            internal readonly LocalPlayer Player;
            internal readonly PlayerInventory Inventory;
            internal readonly CraftingSystem System;
            internal readonly ItemInstanceManager Manager;
            private readonly int _loadCount;
            private readonly bool _networked;
            private readonly IntPtr _entity, _map;
            private LayoutItem _selection;
            internal bool SelectionAttempted { get; private set; }

            internal CraftTransferScope()
            {
                RequireCraftTransfer(OnCraftExecutionThread, "Crafting transfers require the initialized Unity lifecycle thread.");
                RequireCraftTransfer(!_craftExecutionCallInProgress, "Another native crafting action is in progress.");
                RefreshActiveCraftExecution();
                RequireCraftTransfer(_activeCraftExecution == null, "A craft is pending; observe its completion before moving mat items.");
                RequireCraftTransfer(TryGetCraftExecutionContext(out var player, out var inventory, out var system, out var detail), detail);
                Player = player;
                Inventory = inventory;
                System = system;
                Manager = inventory._itemInstanceManager;
                RequireCraftTransfer(CraftExecutionLive(Manager) && Manager._itemsMap is not null && !Manager._itemsMap.WasCollected,
                    "Native local inventory storage is unavailable.");
                _map = Manager._itemsMap.Pointer;
                _loadCount = SaveLifecycle.State.BeforeLoadCount;
                _networked = BoltNetwork.isRunning;
                _entity = _networked ? LocalPlayer.Entity.Pointer : IntPtr.Zero;
                RequireCurrent();
                _craftExecutionCallInProgress = true;
            }

            internal void RequireCurrent()
            {
                RequireCraftTransfer(CraftExecutionContextCurrent(Player, Inventory, System, _loadCount, _networked, _entity)
                    && CraftExecutionLive(Manager) && Inventory._itemInstanceManager?.Pointer == Manager.Pointer
                    && Manager._itemsMap is not null && !Manager._itemsMap.WasCollected && Manager._itemsMap.Pointer == _map,
                    "The local crafting/storage lifetime or authority changed.");
                RequireCraftTransfer(Inventory._currentView == PlayerInventory.PlayerViews.Inventory && System.isActiveAndEnabled
                    && !LocalPlayer.IsInMidAction && !System._inCreativeMode,
                    "Transfers require an idle open native inventory outside creative mode.");
                var proxy = System._craftingAnimationEventsProxy;
                RequireCraftTransfer(!CraftExecutionLive(proxy) || proxy.CurrentRecipe == null,
                    "A native craft animation is still pending.");
            }

            internal void Select<T>(Il2CppSystem.Collections.Generic.List<T> rows, T source) where T : LayoutItem
            {
                RequireSelectionMember(rows, source);
                if (!source.IsHighlighted)
                {
                    _selection = source;
                    SelectionAttempted = true;
                    source.OnMouseEnter();
                }
                RequireSelected(rows, source);
            }

            internal void RequireSelected<T>(Il2CppSystem.Collections.Generic.List<T> rows, T source) where T : LayoutItem
            {
                RequireSelectionMember(rows, source);
                RequireCraftTransfer(source.IsHighlighted, "The native UI declined selection of this layout.");
            }

            private static void RequireSelectionMember<T>(Il2CppSystem.Collections.Generic.List<T> rows, T source) where T : LayoutItem
            {
                RequireCraftTransfer(CraftExecutionLive(source) && source.isActiveAndEnabled,
                    "The requested native source layout is no longer active.");
                RequireCraftTransfer(rows is not null && !rows.WasCollected && rows.Count <= MaximumCraftingMatEntries,
                    "The native layout list is unavailable or exceeds the supported limit.");
                int matches = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    RequireCraftTransfer(CraftExecutionLive(row), "A native layout row is unavailable.");
                    if (row.Pointer == source.Pointer) matches++;
                    else RequireCraftTransfer(!row.IsHighlighted, "Another row in this group is highlighted; resolve that selection first.");
                }
                RequireCraftTransfer(matches == 1, "The requested layout is not an unambiguous member of its native group.");
            }

            internal void ReleaseSelection()
            {
                var selection = _selection;
                _selection = null;
                if (CraftExecutionLive(selection) && selection.IsHighlighted) selection.OnMouseExit();
            }

            public void Dispose()
            {
                try { ReleaseSelection(); }
                catch (Exception error)
                {
                    global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                        "Native crafting selection cleanup failed: " + error.Message);
                }
                finally { _craftExecutionCallInProgress = false; }
            }
        }

        private static IngredientLayoutItemGroup FindTransferIngredientGroup(CraftingSystem system, int itemId)
        {
            var groups = system._ingredientLayoutItemGroups;
            RequireCraftTransfer(groups is not null && !groups.WasCollected && groups.Count <= MaximumCraftingMatEntries,
                "The native ingredient groups are unavailable or exceed the supported limit.");
            IngredientLayoutItemGroup found = null;
            foreach (var group in groups)
            {
                if (!CraftExecutionLive(group) || group._itemId != itemId) continue;
                RequireCraftTransfer(found is null, "More than one native ingredient group claims this item.");
                found = group;
            }
            RequireCraftTransfer(CraftExecutionLive(found) && found.isActiveAndEnabled,
                "No active native ingredient group is available for this item.");
            return found;
        }

        private static CraftingResultLayoutItemGroup FindTransferResultGroup(CraftingSystem system,
            CraftingResultLayoutItem source, int itemId)
        {
            var groups = system._craftingResultLayoutItemGroups;
            RequireCraftTransfer(groups is not null && !groups.WasCollected && groups.Count <= MaximumCraftingMatEntries,
                "The native result groups are unavailable or exceed the supported limit.");
            CraftingResultLayoutItemGroup found = null;
            foreach (var group in groups)
            {
                if (!CraftExecutionLive(group) || group._itemId != itemId) continue;
                var rows = ReadTransferLayouts(group._layoutItems, itemId, allowInactiveInstances: true);
                foreach (var row in rows)
                    if (row.Layout.Pointer == source.Pointer)
                    {
                        RequireCraftTransfer(found is null, "The result layout has ambiguous native group ownership.");
                        found = group;
                    }
            }
            RequireCraftTransfer(CraftExecutionLive(found) && found.isActiveAndEnabled,
                "The selected result is not in the current local crafting mat.");
            return found;
        }

        private sealed class CraftTransferLayout(LayoutItem layout, ItemInstance instance)
        {
            internal readonly LayoutItem Layout = layout;
            internal readonly ItemInstance Instance = instance;
        }

        private static List<CraftTransferLayout> ReadTransferLayouts<T>(Il2CppSystem.Collections.Generic.List<T> rows,
            int itemId, bool allowInactiveInstances = false) where T : LayoutItem
        {
            RequireCraftTransfer(rows is not null && !rows.WasCollected && rows.Count <= MaximumCraftingMatEntries,
                "The native mat layout list is unavailable or exceeds the supported limit.");
            var result = new List<CraftTransferLayout>();
            var identities = new HashSet<IntPtr>();
            foreach (var row in rows)
            {
                RequireCraftTransfer(CraftExecutionLive(row), "A mat layout row is unavailable.");
                var instance = row.ItemInstance;
                bool active = row.gameObject.activeSelf;
                // Native result StashToInventory resets/deactivates its visual but retains its
                // cached instance. Ingredient removal, unlike result collection, clears it.
                if (!active && allowInactiveInstances) continue;
                RequireCraftTransfer(active == (instance is not null), "A mat layout has inconsistent active/instance state.");
                if (!active) continue;
                RequireCraftTransfer(!instance.WasCollected && instance._itemID == itemId && identities.Add(instance.Pointer),
                    "A mat row contains an unavailable, mismatched or duplicated native instance.");
                result.Add(new CraftTransferLayout(row, instance));
            }
            return result;
        }

        private static bool TransferLayoutsSame(List<CraftTransferLayout> before, List<CraftTransferLayout> after)
        {
            if (before.Count != after.Count) return false;
            for (int i = 0; i < before.Count; i++)
                if (before[i].Layout.Pointer != after[i].Layout.Pointer || before[i].Instance.Pointer != after[i].Instance.Pointer)
                    return false;
            return true;
        }

        private static CraftTransferLayout FindAddedTransferLayout(List<CraftTransferLayout> before, List<CraftTransferLayout> after)
        {
            if (after.Count != before.Count + 1) return null;
            var remaining = new List<CraftTransferLayout>(after);
            foreach (var prior in before)
            {
                int index = remaining.FindIndex(row => row.Layout.Pointer == prior.Layout.Pointer && row.Instance.Pointer == prior.Instance.Pointer);
                if (index < 0) return null;
                remaining.RemoveAt(index);
            }
            return remaining[0];
        }

        private static bool TransferLayoutRemoved(List<CraftTransferLayout> before, List<CraftTransferLayout> after, IntPtr layout)
        {
            var expected = new List<CraftTransferLayout>(before);
            return expected.RemoveAll(row => row.Layout.Pointer == layout) == 1 && TransferLayoutsSame(expected, after);
        }

        private sealed class CraftTransferStorage(int count, HashSet<IntPtr> instances)
        {
            internal readonly int Count = count;
            internal readonly HashSet<IntPtr> Instances = instances;
        }

        private static CraftTransferStorage ReadTransferStorage(ItemInstanceManager manager, int itemId, bool unique)
        {
            var map = manager._itemsMap;
            RequireCraftTransfer(map is not null && !map.WasCollected, "The inventory item map is unavailable.");
            if (!map.TryGetValue(itemId, out var row)) return new CraftTransferStorage(0, new HashSet<IntPtr>());
            RequireCraftTransfer(row is not null && !row.WasCollected && row.Count >= 0
                && row.UniqueInstances is not null && !row.UniqueInstances.WasCollected,
                "The native stored item row is unavailable or inconsistent.");
            var identities = new HashSet<IntPtr>();
            var instances = row.UniqueInstances;
            RequireCraftTransfer(unique ? instances.Count == row.Count && instances.Count <= MaximumCraftingMatEntries : instances.Count == 0,
                "The native stored count/instance list disagrees with its item definition or exceeds the supported limit.");
            foreach (var instance in instances)
                RequireCraftTransfer(instance is not null && !instance.WasCollected && instance._itemID == itemId
                    && identities.Add(instance.Pointer), "Stored unique-instance custody is inconsistent.");
            return new CraftTransferStorage(row.Count, identities);
        }

        private static bool TransferStorageSame(CraftTransferStorage before, CraftTransferStorage after)
            => before.Count == after.Count && before.Instances.SetEquals(after.Instances);

        private static bool TransferStorageDelta(CraftTransferStorage before, CraftTransferStorage after, IntPtr instance, int delta)
        {
            if ((long)before.Count + delta != after.Count) return false;
            if (instance == IntPtr.Zero) return before.Instances.SetEquals(after.Instances);

            var withoutInstance = delta < 0 ? after.Instances : before.Instances;
            var withInstance = delta < 0 ? before.Instances : after.Instances;
            return !withoutInstance.Contains(instance) && withInstance.Contains(instance)
                && withInstance.Count == withoutInstance.Count + 1 && withoutInstance.IsSubsetOf(withInstance);
        }

        private sealed class CraftModuleIdentity
        {
            private readonly ItemInstance _instance;
            private readonly int _itemId;
            private readonly IntPtr _list;
            private readonly IntPtr[] _modules;
            internal int Count => _modules.Length;

            internal CraftModuleIdentity(ItemInstance instance, int itemId)
            {
                RequireCraftTransfer(instance is not null && !instance.WasCollected && instance._itemID == itemId,
                    "The exact native item instance is unavailable.");
                _instance = instance;
                _itemId = itemId;
                var modules = instance._modules;
                RequireCraftTransfer(modules is not null && !modules.WasCollected && modules.Count <= MaximumCraftingMatEntries,
                    "The native instance module list is unavailable or exceeds the supported limit.");
                _list = modules.Pointer;
                _modules = new IntPtr[modules.Count];
                for (int i = 0; i < _modules.Length; i++)
                {
                    var module = modules[i];
                    RequireCraftTransfer(module is not null && !module.WasCollected, "A native item module is unavailable.");
                    _modules[i] = module.Pointer;
                }
                RequireCraftTransfer(ItemRegistration.ValidateAuthoredModuleInstance(instance, false, out var detail), detail);
            }

            internal bool Current()
            {
                if (_instance.WasCollected || _instance._itemID != _itemId) return false;
                var modules = _instance._modules;
                if (modules is null || modules.WasCollected || modules.Pointer != _list || modules.Count != _modules.Length) return false;
                for (int i = 0; i < _modules.Length; i++)
                    if (modules[i] is null || modules[i].WasCollected || modules[i].Pointer != _modules[i]) return false;
                return ItemRegistration.ValidateAuthoredModuleInstance(_instance, false, out _);
            }
        }

        private static void RequireCraftTransfer(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }

        private static CraftingTransferResult TransferResult(CraftingTransferKind kind,
            bool attempted, int itemId, bool complete, CraftTransferScope scope, ItemInstance instance,
            bool exactInstancePreserved, string detail)
            => new(kind, attempted, scope?.SelectionAttempted == true, itemId,
                complete ? CraftingExecutionOutcome.Completed : attempted
                    ? CraftingExecutionOutcome.Unconfirmed : CraftingExecutionOutcome.Refused,
                instance, exactInstancePreserved, detail);
    }

    public enum CraftingTransferKind { StageIngredient, ReturnIngredient, CollectResult }

    /// <summary>One synchronous native UI transfer observation; not a server transaction or a rollback promise.</summary>
    public sealed class CraftingTransferResult
    {
        internal CraftingTransferResult(CraftingTransferKind kind, bool attempted,
            bool selectionAttempted, int itemId, CraftingExecutionOutcome outcome,
            ItemInstance instance, bool exactInstancePreserved, string detail)
        {
            Kind = kind;
            Attempted = attempted;
            NativeSelectionAttempted = selectionAttempted;
            ItemId = itemId;
            Outcome = outcome;
            TransferredInstance = instance;
            ExactInstancePreserved = exactInstancePreserved;
            Detail = detail;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public CraftingTransferKind Kind { get; }
        /// <summary>The native inventory/mat action was invoked; hover alone does not set this flag.</summary>
        public bool Attempted { get; }
        public bool NativeSelectionAttempted { get; }
        public int ItemId { get; }
        public CraftingExecutionOutcome Outcome { get; }
        public bool Completed => Outcome == CraftingExecutionOutcome.Completed;
        /// <summary>
        /// Exact native instance observed at the destination, or null for stack storage,
        /// compressed mat counts or no confirmed transfer. Only inspect on the Unity thread
        /// after revalidating its native lifetime.
        /// </summary>
        public ItemInstance TransferredInstance { get; }
        public bool ExactInstancePreserved { get; }
        public string Detail { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
