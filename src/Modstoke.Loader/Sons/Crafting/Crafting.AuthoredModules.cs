// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        /// <summary>
        /// Applies caller-authored native SaveData records to one actual unique result on the
        /// current idle local mat.
        /// </summary>
        /// <remarks>
        /// ExecuteModuleItemRegistration must have declared its complete
        /// module shape, and normal native layout initialization must have supplied those exact
        /// built-in modules. Supply one fully authored record for every declared module; omitted
        /// state is not inferred, and modules, instances or replacement results are never created.
        /// Uses each existing module's native LoadFromSave, then RefreshItemObject. Native
        /// normalization and callbacks apply. Completed means the native calls returned with
        /// the same instance, modules and mat/storage custody, not exact value round-trip proof.
        /// This is not collection, saving, a remote acknowledgement or a grant. Collect later
        /// through TryCollectCraftedResult. Missing-instance outputs and nonunique modules remain
        /// unsupported. A failure after invocation may leave partially applied state; no automatic
        /// rollback, refund or retry is safe.
        /// </remarks>
        public static bool TryApplyAuthoredResultModules(CraftingResultLayoutItem source,
            IReadOnlyList<ItemInstanceModule.SaveData> records, out CraftingModuleResult result)
            => TryApplyAuthoredResultModulesCore(source, records, null, out result);

        /// <summary>
        /// Restores a stored module snapshot onto an already-created authored unique result.
        /// </summary>
        /// <remarks>
        /// Retains native custody checks and verifies complete snapshot readback, including
        /// fields omitted by native saving. Does not create an item or debit a module store.
        /// An unconfirmed partial application must not be retried or automatically refunded.
        /// </remarks>
        public static bool TryApplyStoredResultModules(CraftingResultLayoutItem source,
            ItemModuleSnapshot snapshot, out CraftingModuleResult result)
        {
            IReadOnlyList<ItemInstanceModule.SaveData> records;
            try
            {
                if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
                records = snapshot.CreateNativeRecords();
            }
            catch (Exception error)
            {
                result = new CraftingModuleResult(snapshot?.ItemId ?? 0, false, 0,
                    CraftingExecutionOutcome.Refused, error.Message);
                return false;
            }
            return TryApplyAuthoredResultModulesCore(source, records, snapshot, out result);
        }

        private static bool TryApplyAuthoredResultModulesCore(CraftingResultLayoutItem source,
            IReadOnlyList<ItemInstanceModule.SaveData> records, ItemModuleSnapshot snapshot,
            out CraftingModuleResult result)
        {
            CraftTransferScope scope = null;
            bool attempted = false;
            int itemId = 0;
            int callsCompleted = 0;
            try
            {
                scope = new CraftTransferScope();
                RequireCraftTransfer(CraftExecutionLive(source) && source.isActiveAndEnabled,
                    "A live visible native crafting result is required.");
                var instance = source.ItemInstance;
                RequireCraftTransfer(ItemRegistration.ValidateAuthoredModuleInstance(instance, true, out var detail), detail);
                itemId = instance._itemID;
                RequireCraftTransfer(snapshot == null || snapshot.ItemId == itemId,
                    "The stored module snapshot belongs to a different item definition.");
                var group = FindTransferResultGroup(scope.System, source, itemId);
                var data = instance.Data;
                RequireCraftTransfer(group._isOneToOneCollection && CraftExecutionLive(group._itemData)
                    && group._itemData.Pointer == data.Pointer && CraftExecutionLive(instance.ItemObject)
                    && instance.ItemObject.Pointer == source.gameObject.Pointer,
                    "The exact authored unique result and its native presentation must belong to the selected group.");
                var rows = group._layoutItems;
                var before = ReadTransferLayouts(rows, itemId, allowInactiveInstances: true);
                var stored = ReadTransferStorage(scope.Manager, itemId, true);
                RequireCraftTransfer(!stored.Instances.Contains(instance.Pointer), "The selected result already has inventory custody.");
                var identities = new CraftModuleIdentity(instance, itemId);
                RequireCraftTransfer(records != null && records.Count == identities.Count,
                    "Supply exactly one native state record for every declared module.");
                var moduleList = instance._modules;
                var modules = new ItemInstanceModule[identities.Count];
                var payloads = new ItemInstanceModule.SaveData[identities.Count];
                for (int i = 0; i < modules.Length; i++) modules[i] = moduleList[i];
                int supplied = 0;
                for (int i = 0; i < payloads.Length; i++)
                {
                    var record = records[i];
                    RequireCraftTransfer(record is not null && !record.WasCollected && record.ModuleId >= 0 && record.ModuleId <= 9,
                        "A native module record is missing or has an unsupported discriminator.");
                    int bit = 1 << record.ModuleId;
                    RequireCraftTransfer((supplied & bit) == 0, "Duplicate native module records are not supported.");
                    supplied |= bit;
                    int match = -1;
                    for (int j = 0; j < modules.Length; j++)
                    {
                        if (!ItemRegistration.NativeModuleRecordMatches(modules[j], record))
                            continue;
                        match = j;
                        break;
                    }
                    RequireCraftTransfer(match >= 0, "Every record must have the exact native SaveData class for its declared module.");
                    payloads[match] = record;
                }
                RequireAuthoredModuleActionCurrent();
                for (int i = 0; i < modules.Length; i++)
                {
                    // Strong references retain the exact modules and records across callbacks.
                    // No GetOrAddModule, new instance, stash or inventory grant is performed.
                    attempted = true;
                    modules[i].LoadFromSave(payloads[i]);
                    callsCompleted++;
                    RequireAuthoredModuleActionCurrent();
                }
                if (snapshot != null)
                {
                    snapshot.RestoreSupplemental(instance, records);
                    RequireAuthoredModuleActionCurrent();
                }
                instance.RefreshItemObject();
                RequireAuthoredModuleActionCurrent();
                if (snapshot != null)
                {
                    RequireCraftTransfer(ItemModuleSnapshot.TryCapture(instance, out var observed, out var captureDetail)
                        && snapshot.SameState(observed), "Restored module state did not match the stored snapshot. " + captureDetail);
                    RequireAuthoredModuleActionCurrent();
                }
                result = new CraftingModuleResult(itemId, true, callsCompleted,
                    CraftingExecutionOutcome.Completed,
                    snapshot == null
                        ? "Native module state calls and presentation refresh completed with unchanged result, module and storage custody. The result remains on the mat."
                        : "Stored module snapshot restored and read back on the same native result. The result remains on the mat; no module-store count was consumed.");
                return true;

                void RequireAuthoredModuleActionCurrent()
                {
                    scope.RequireCurrent();
                    RequireCraftTransfer(CraftExecutionLive(source) && source.isActiveAndEnabled
                        && source.ItemInstance?.Pointer == instance.Pointer && CraftExecutionLive(instance.ItemObject)
                        && instance.ItemObject.Pointer == source.gameObject.Pointer && identities.Current()
                        && CraftExecutionLive(group) && group.isActiveAndEnabled && group._isOneToOneCollection
                        && group._itemData?.Pointer == data.Pointer && group._layoutItems?.Pointer == rows.Pointer
                        && FindTransferResultGroup(scope.System, source, itemId).Pointer == group.Pointer
                        && TransferLayoutsSame(before, ReadTransferLayouts(rows, itemId, allowInactiveInstances: true))
                        && TransferStorageSame(stored, ReadTransferStorage(scope.Manager, itemId, true)),
                        "The authored result's native identity, modules or custody changed during the action.");
                    for (int j = 0; j < modules.Length; j++)
                        RequireCraftTransfer(ItemRegistration.NativeModuleRecordMatches(modules[j], payloads[j]),
                            "A caller-authored record changed its native class or discriminator during the action.");
                    RequireAuthoredResultCustody(scope, source, instance);
                }
            }
            catch (Exception error)
            {
                result = new CraftingModuleResult(itemId, attempted, callsCompleted,
                    attempted ? CraftingExecutionOutcome.Unconfirmed : CraftingExecutionOutcome.Refused,
                    error.Message + (attempted ? " Module state may be partially applied; do not automatically retry, roll back or grant a replacement." : ""));
                return false;
            }
            finally { scope?.Dispose(); }
        }

        private static void RequireAuthoredResultCustody(CraftTransferScope scope, CraftingResultLayoutItem source, ItemInstance instance)
        {
            RequireNoAuthoredEquipmentReference(scope.Inventory._equipmentSlots, instance);
            RequireNoAuthoredEquipmentReference(scope.Inventory._equipmentSlotsNext, instance);
            var projectiles = scope.Inventory._projectileItemInstances;
            RequireCraftTransfer(projectiles is not null && !projectiles.WasCollected && projectiles.Count <= MaximumCraftingMatEntries,
                "Native projectile custody is unavailable or exceeds the supported limit.");
            foreach (var entry in projectiles)
                RequireCraftTransfer(entry.Value is null || (!entry.Value.WasCollected && entry.Value.Pointer != instance.Pointer),
                    "The selected result also has projectile custody.");
            var ingredients = scope.System._ingredientLayoutItemGroups;
            var results = scope.System._craftingResultLayoutItemGroups;
            RequireCraftTransfer(ingredients is not null && !ingredients.WasCollected && ingredients.Count <= MaximumCraftingMatEntries
                && results is not null && !results.WasCollected && results.Count <= MaximumCraftingMatEntries,
                "Native crafting custody is unavailable or exceeds the supported limit.");
            foreach (var group in ingredients)
            {
                RequireCraftTransfer(CraftExecutionLive(group), "An ingredient group is unavailable.");
                RequireNoOtherAuthoredResultReference(group._layoutItems, source, instance);
            }
            foreach (var group in results)
            {
                RequireCraftTransfer(CraftExecutionLive(group), "A result group is unavailable.");
                RequireNoOtherAuthoredResultReference(group._layoutItems, source, instance);
            }
        }

        private static void RequireNoAuthoredEquipmentReference(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ItemInstance> slots, ItemInstance instance)
        {
            RequireCraftTransfer(slots is not null && !slots.WasCollected && slots.Length <= MaximumCraftingMatEntries,
                "Native equipment custody is unavailable or exceeds the supported limit.");
            foreach (var slot in slots)
                RequireCraftTransfer(slot is null || (!slot.WasCollected && slot.Pointer != instance.Pointer),
                    "The selected result also has equipment custody.");
        }

        private static void RequireNoOtherAuthoredResultReference<T>(Il2CppSystem.Collections.Generic.List<T> rows,
            CraftingResultLayoutItem source, ItemInstance instance) where T : LayoutItem
        {
            RequireCraftTransfer(rows is not null && !rows.WasCollected && rows.Count <= MaximumCraftingMatEntries,
                "Native mat row custody is unavailable or exceeds the supported limit.");
            foreach (var row in rows)
            {
                RequireCraftTransfer(CraftExecutionLive(row), "A native mat row is unavailable.");
                if (row.Pointer == source.Pointer || !row.gameObject.activeSelf) continue;
                var other = row.ItemInstance;
                RequireCraftTransfer(other is null || (!other.WasCollected && other.Pointer != instance.Pointer),
                    "Another visible native mat row owns the selected instance.");
            }
        }
    }

    /// <summary>Local native module invocation and custody observations; not collection or save/network acceptance.</summary>
    public sealed class CraftingModuleResult
    {
        internal CraftingModuleResult(int itemId, bool attempted, int callsCompleted,
            CraftingExecutionOutcome outcome, string detail)
        {
            ItemId = itemId;
            MutationAttempted = attempted;
            ModuleCallsCompleted = callsCompleted;
            Outcome = outcome;
            Detail = detail;
        }

        public int ItemId { get; }
        public bool MutationAttempted { get; }
        /// <summary>Native LoadFromSave calls that returned, excluding the subsequent presentation refresh.</summary>
        public int ModuleCallsCompleted { get; }
        public CraftingExecutionOutcome Outcome { get; }
        public bool Completed => Outcome == CraftingExecutionOutcome.Completed;
        public string Detail { get; }
    }
}
