// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        /// <summary>
        /// Publishes a caller-authored ingredient group in the current native mat's lookup list.
        /// </summary>
        /// <remarks>
        /// The item must have been published by ExecuteItemRegistration. The enabled group must
        /// already be a direct child of this mat's ingredient root, with empty inactive rows,
        /// matching item ids and ownership mode. Native initialization discovers these same
        /// direct children only once; this adds one late-authored group without reinitializing
        /// the mat. Does not create assets, instances, recipes, callbacks or inventory layouts.
        /// Allow the engine's normal Start/OnEnable lifecycle to run; this result confirms list
        /// publication only, not that arbitrary caller-authored components are functional.
        /// The caller retains the group and its assets for this player lifetime. No hot removal,
        /// save/network integration or replay after a player/database lifetime change is provided.
        /// </remarks>
        public static bool TryRegisterAuthoredIngredientLayout(ItemData item, IngredientLayoutItemGroup group,
            out CraftingLayoutRegistrationResult result)
            => RegisterAuthoredCraftingLayout<IngredientLayoutItemGroup, IngredientLayoutItem>(item, group,
                system => system._ingredientLayoutItemGroups, system => system._ingredientLayoutGroups, out result);

        /// <summary>
        /// Publishes an empty caller-authored result group under the current mat's native result
        /// root.
        /// </summary>
        /// <remarks>
        /// Uses the same ownership/lifetime contract as TryRegisterAuthoredIngredientLayout.
        /// Normal native crafting supplies actual result instances and invokes their renderable
        /// and module lifecycle. This does not repair no-instance fallback results or permit
        /// nonunique per-instance modules to be discarded during collection.
        /// </remarks>
        public static bool TryRegisterAuthoredResultLayout(ItemData item, CraftingResultLayoutItemGroup group,
            out CraftingLayoutRegistrationResult result)
            => RegisterAuthoredCraftingLayout<CraftingResultLayoutItemGroup, CraftingResultLayoutItem>(item, group,
                system => system._craftingResultLayoutItemGroups, system => system._craftingResultLayoutGroups, out result);

        private static bool RegisterAuthoredCraftingLayout<TGroup, TLayout>(ItemData item, TGroup group,
            Func<CraftingSystem, Il2CppSystem.Collections.Generic.List<TGroup>> getGroups,
            Func<CraftingSystem, Transform> getRoot, out CraftingLayoutRegistrationResult result)
            where TGroup : LayoutItemGroup<TLayout> where TLayout : LayoutItem
        {
            CraftTransferScope scope = null;
            Il2CppSystem.Collections.Generic.List<TGroup> groups = null;
            IntPtr[] before = null;
            IntPtr[] rows = null;
            IntPtr rowList = IntPtr.Zero;
            Transform root = null;
            var attempted = false;
            var itemId = 0;
            try
            {
                scope = new CraftTransferScope();
                RequireCraftTransfer(ItemRegistration.IsPublishedRuntimeItem(item),
                    "The exact authored ItemData must still be published by the SDK in the current native database.");
                itemId = item._id;
                groups = getGroups(scope.System);
                root = getRoot(scope.System);
                before = ReadAuthoredCraftingGroups<TGroup, TLayout>(groups, itemId);
                RequireCraftTransfer(before.Length < MaximumCraftingMatEntries,
                    "The native crafting group list has reached the supported limit.");
                rows = ValidateAuthoredCraftingGroup<TGroup, TLayout>(item, group, root);
                rowList = group._layoutItems.Pointer;
                RequireNoAuthoredItemCustody(scope.System, itemId);

                // Neither Start nor OnEnable is replayed. The caller supplies an engine-owned
                // group; native TryAddItem/SetItemInstance will bind the actual future instance.
                scope.RequireCurrent();
                RequireCraftTransfer(ItemRegistration.IsPublishedRuntimeItem(item)
                    && item._id == itemId
                    && getGroups(scope.System)?.Pointer == groups.Pointer
                    && getRoot(scope.System)?.Pointer == root.Pointer
                    && SameAuthoredCraftingGroups(groups, before, null)
                    && group._layoutItems?.Pointer == rowList
                    && SameAuthoredCraftingRows(group._layoutItems, rows),
                    "Native item, root or layout ownership changed before publication.");
                attempted = true;
                groups.Add(group);
                scope.RequireCurrent();
                RequireCraftTransfer(ItemRegistration.IsPublishedRuntimeItem(item)
                    && item._id == itemId
                    && getGroups(scope.System)?.Pointer == groups.Pointer
                    && getRoot(scope.System)?.Pointer == root.Pointer
                    && SameAuthoredCraftingGroups(groups, before, group)
                    && group._layoutItems?.Pointer == rowList
                    && SameAuthoredCraftingRows(group._layoutItems, rows),
                    "Native crafting layout publication could not be confirmed.");
                RequireNoAuthoredItemCustody(scope.System, itemId);
                ValidateAuthoredCraftingGroup<TGroup, TLayout>(item, group, root);
                result = new CraftingLayoutRegistrationResult(true, true, false, itemId,
                    "The authored group is in the current native crafting lookup list. Asset behavior, inventory/held layouts and remote delivery are separate.");
                return true;
            }
            catch (Exception error)
            {
                // Only the exact entry appended by this invocation may be removed. Never
                // destroy borrowed scene objects, clear another registration or replay events.
                bool rolledBack = !attempted;
                if (attempted)
                {
                    try
                    {
                        scope.RequireCurrent();
                        RequireCraftTransfer(ItemRegistration.IsPublishedRuntimeItem(item)
                            && item._id == itemId && group._itemId == itemId
                            && getGroups(scope.System)?.Pointer == groups.Pointer
                            && getRoot(scope.System)?.Pointer == root.Pointer && CraftExecutionLive(group)
                            && group._layoutItems?.Pointer == rowList
                            && SameAuthoredCraftingRows(group._layoutItems, rows),
                            "The published group lifetime or custody changed; it must not be removed.");
                        RequireNoAuthoredItemCustody(scope.System, itemId);
                        rolledBack = RemoveAuthoredCraftingPublication(groups, before, group);
                    }
                    catch { /* Retain a publication whose lifetime or custody cannot be verified. */ }
                }
                result = new CraftingLayoutRegistrationResult(false, attempted, !rolledBack, itemId,
                    error.Message + (rolledBack ? "" : " Native publication remains uncertain; do not repeat the registration automatically."));
                return false;
            }
            finally { scope?.Dispose(); }
        }

        private static void RequireNoAuthoredItemCustody(CraftingSystem system, int itemId)
        {
            RequireCraftTransfer(system.GetIngredientCount(itemId) == 0,
                "Authored layouts cannot be published while this item has ingredient custody.");
            var results = system._craftingResultLayoutItemGroups;
            RequireCraftTransfer(results is not null && !results.WasCollected && results.Count <= MaximumCraftingMatEntries,
                "Native result ownership is unavailable or exceeds the supported limit.");
            foreach (var group in results)
            {
                RequireCraftTransfer(CraftExecutionLive(group), "A native result group is unavailable.");
                if (group._itemId != itemId) continue;
                RequireCraftTransfer(ReadTransferLayouts(group._layoutItems, itemId, allowInactiveInstances: true).Count == 0,
                    "Authored layouts cannot be published while this item has result custody.");
            }
        }

        private static IntPtr[] ReadAuthoredCraftingGroups<TGroup, TLayout>(
            Il2CppSystem.Collections.Generic.List<TGroup> groups, int itemId)
            where TGroup : LayoutItemGroup<TLayout> where TLayout : LayoutItem
        {
            RequireCraftTransfer(groups is not null && !groups.WasCollected && groups.Count <= MaximumCraftingMatEntries,
                "The native crafting group list is unavailable or exceeds the supported limit.");
            var result = new IntPtr[groups.Count];
            var identities = new HashSet<IntPtr>();
            for (int i = 0; i < result.Length; i++)
            {
                var existing = groups[i];
                RequireCraftTransfer(CraftExecutionLive(existing) && identities.Add(existing.Pointer)
                    && existing._itemId != itemId,
                    "The native group list is inconsistent or already contains this item. No replacement is allowed.");
                result[i] = existing.Pointer;
            }
            return result;
        }

        private static IntPtr[] ValidateAuthoredCraftingGroup<TGroup, TLayout>(ItemData item, TGroup group, Transform root)
            where TGroup : LayoutItemGroup<TLayout> where TLayout : LayoutItem
        {
            RequireCraftTransfer(CraftExecutionLive(root) && CraftExecutionLive(group) && group.isActiveAndEnabled
                && group.transform.parent?.Pointer == root.Pointer && group._itemId == item._id
                && group._isOneToOneCollection == item._eachInstanceIsUnique
                && (group._itemData is null || (CraftExecutionLive(group._itemData) && group._itemData.Pointer == item.Pointer)),
                "The enabled authored group must be a direct native-root child with the exact item definition and ownership mode.");
            RequireCraftTransfer(root.childCount <= MaximumCraftingMatEntries,
                "The native crafting root exceeds the supported group limit.");
            int matches = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                var childGroups = root.GetChild(i).GetComponents<TGroup>();
                if (childGroups.Length == 0) continue;
                RequireCraftTransfer(childGroups.Length == 1 && CraftExecutionLive(childGroups[0]),
                    "A direct native-root child cannot own competing layout groups.");
                var childGroup = childGroups[0];
                if (childGroup._itemId != item._id) continue;
                RequireCraftTransfer(childGroup.Pointer == group.Pointer,
                    "Another authored group under this native root already claims the item id.");
                matches++;
            }
            RequireCraftTransfer(matches == 1, "The authored group has ambiguous native-root ownership.");
            RequireCraftTransfer(group.GetComponentsInChildren<TGroup>(true).Length == 1,
                "An authored crafting group cannot contain another group with competing row ownership.");
            var rows = group._layoutItems;
            RequireCraftTransfer(rows is not null && !rows.WasCollected && rows.Count > 0
                && rows.Count <= MaximumCraftingMatEntries, "The authored group needs a bounded nonempty native row list.");
            var result = new IntPtr[rows.Count];
            var identities = new HashSet<IntPtr>();
            for (int i = 0; i < result.Length; i++)
            {
                var row = rows[i];
                RequireCraftTransfer(CraftExecutionLive(row) && row.enabled && identities.Add(row.Pointer)
                    && row.transform.Pointer != group.transform.Pointer && row.transform.IsChildOf(group.transform)
                    && !row.gameObject.activeSelf && row.ItemInstance is null && !row.IsHighlighted,
                    "Authored rows must be distinct, empty, inactive descendants with enabled native layout components.");
                foreach (var tag in row.GetComponentsInChildren<ItemLocatorTag>(true))
                    RequireCraftTransfer(CraftExecutionLive(tag) && tag._itemId == item._id,
                        "Authored row locator tags must match the published item id.");
                foreach (var renderable in row.GetComponentsInChildren<ItemRenderable>(true))
                    RequireCraftTransfer(CraftExecutionLive(renderable) && renderable._itemId == item._id,
                        "Authored row renderables must load the published item, not a donor item.");
                result[i] = row.Pointer;
            }
            var descendants = group.GetComponentsInChildren<TLayout>(true);
            RequireCraftTransfer(descendants.Length == result.Length,
                "All authored layout descendants must belong to this group's row list.");
            foreach (var row in descendants)
                RequireCraftTransfer(CraftExecutionLive(row) && identities.Contains(row.Pointer),
                    "An authored layout descendant has no exact group ownership.");
            return result;
        }

        private static bool SameAuthoredCraftingRows<T>(Il2CppSystem.Collections.Generic.List<T> rows, IntPtr[] expected)
            where T : LayoutItem
        {
            if (rows is null || rows.WasCollected || rows.Count != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (!CraftExecutionLive(rows[i]) || rows[i].Pointer != expected[i]
                    || rows[i].ItemInstance is not null || rows[i].gameObject.activeSelf) return false;
            return true;
        }

        private static bool SameAuthoredCraftingGroups<T>(Il2CppSystem.Collections.Generic.List<T> groups,
            IntPtr[] before, T appended) where T : Component
        {
            if (groups is null || groups.WasCollected || before is null
                || groups.Count != before.Length + (appended is null ? 0 : 1)) return false;
            for (int i = 0; i < before.Length; i++)
                if (!CraftExecutionLive(groups[i]) || groups[i].Pointer != before[i]) return false;
            return appended is null || (CraftExecutionLive(appended) && CraftExecutionLive(groups[before.Length])
                && groups[before.Length].Pointer == appended.Pointer);
        }

        private static bool RemoveAuthoredCraftingPublication<T>(Il2CppSystem.Collections.Generic.List<T> groups,
            IntPtr[] before, T group) where T : Component
        {
            try
            {
                if (SameAuthoredCraftingGroups(groups, before, null)) return true;
                if (!SameAuthoredCraftingGroups(groups, before, group)) return false;
                groups.RemoveAt(before.Length);
                return SameAuthoredCraftingGroups(groups, before, null);
            }
            catch { return false; }
        }
    }

    /// <summary>One local native lookup publication, not a grant, gameplay validation or network registration.</summary>
    public sealed class CraftingLayoutRegistrationResult
    {
        internal CraftingLayoutRegistrationResult(bool registered, bool attempted,
            bool uncertain, int itemId, string detail)
        {
            Registered = registered;
            MutationAttempted = attempted;
            NativeStateUncertain = uncertain;
            ItemId = itemId;
            Detail = detail;
        }

        public bool Registered { get; }
        public bool MutationAttempted { get; }
        public bool NativeStateUncertain { get; }
        public int ItemId { get; }
        public string Detail { get; }
    }
}
