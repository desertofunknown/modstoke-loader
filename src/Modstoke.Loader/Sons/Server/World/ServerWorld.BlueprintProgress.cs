// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting.Structures;
using IngredientLinks = Il2CppSystem.Collections.Generic.List<global::Sons.Crafting.Structures.StructureCraftingNode.CraftingIngredientLink>;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Maximum entries read from each of the recipe and node lists by TryReadBlueprintRequirements.</summary>
        public const int MaximumBlueprintRequirementEntries = 512;

        /// <summary>
        /// Reads a current attached blueprint's ingredient links on the server game thread.
        /// Counts describe the node, not a player's inventory or proof that a built structure exists.
        /// </summary>
        public static bool TryReadBlueprintProgress(ulong networkId, out BlueprintProgressSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                status = CaptureBlueprintProgress(networkId, out var links, out var before);
                if (status != WorldReadStatus.Success)
                    return false;
                var entries = new BlueprintIngredientRead[before.LinkCount];
                for (var i = 0; i < entries.Length; i++)
                {
                    if (!TryReadBlueprintIngredient(links[i], out entries[i]))
                    {
                        status = WorldReadStatus.BlueprintUnavailable;
                        return false;
                    }
                }

                status = CaptureBlueprintProgress(networkId, out var currentLinks, out var after);
                if (status != WorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                for (var i = 0; i < entries.Length; i++)
                {
                    if (!TryReadBlueprintIngredient(currentLinks[i], out var current) || entries[i] != current)
                    {
                        status = WorldReadStatus.BindingChanged;
                        return false;
                    }
                }

                var ingredients = new BlueprintIngredientSnapshot[entries.Length];
                for (var i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    ingredients[i] = new BlueprintIngredientSnapshot(entry.ItemId, entry.DeclaredCount,
                        entry.DisabledCount, entry.AppliedCount, entry.TotalCount, entry.RemainingCount);
                }
                snapshot = new BlueprintProgressSnapshot(networkId, before.RecipeId, before.IsActive,
                    before.IsPendingDestroy, before.HasRuntimeIngredients, ingredients);
                status = WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Groups the attached blueprint's recipe costs and current node progress by item ID on the server game thread.
        /// Reads at most MaximumBlueprintRequirementEntries from each list. Success may be truncated; inspect
        /// snapshot.IsTruncated before treating a missing item or a total as complete. No ingredients are applied.
        /// </summary>
        public static bool TryReadBlueprintRequirements(ulong networkId,
            out BlueprintRequirementsSnapshot snapshot, out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureBlueprintProgress(networkId, out var links, out var before, out var recipe);
                if (status != WorldReadStatus.Success)
                    return false;
                var ingredients = recipe._ingredients;
                if (ingredients is null || ingredients.WasCollected || before.LinkCount < 0 || ingredients.Count < 0)
                {
                    status = WorldReadStatus.BlueprintUnavailable;
                    return false;
                }
                var recipeCount = ingredients.Count;
                var recipeVersion = ingredients._version;
                var recipeEntries = new BlueprintRecipeIngredientRead[Math.Min(recipeCount, MaximumBlueprintRequirementEntries)];
                var nodeEntries = new BlueprintIngredientRead[Math.Min(before.LinkCount, MaximumBlueprintRequirementEntries)];
                for (var i = 0; i < recipeEntries.Length; i++)
                {
                    if (!TryReadBlueprintRecipeIngredient(ingredients[i], out recipeEntries[i]))
                    {
                        status = WorldReadStatus.BlueprintUnavailable;
                        return false;
                    }
                }
                for (var i = 0; i < nodeEntries.Length; i++)
                {
                    if (!TryReadBlueprintIngredient(links[i], out nodeEntries[i]))
                    {
                        status = WorldReadStatus.BlueprintUnavailable;
                        return false;
                    }
                }

                status = CaptureBlueprintProgress(networkId, out var currentLinks, out var after, out var currentRecipe);
                if (status != WorldReadStatus.Success)
                    return false;
                var currentIngredients = currentRecipe._ingredients;
                if (before != after || currentIngredients is null || currentIngredients.WasCollected
                    || currentIngredients.Pointer != ingredients.Pointer || currentIngredients._version != recipeVersion
                    || currentIngredients.Count != recipeCount)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                for (var i = 0; i < recipeEntries.Length; i++)
                {
                    if (!TryReadBlueprintRecipeIngredient(currentIngredients[i], out var current) || recipeEntries[i] != current)
                    {
                        status = WorldReadStatus.BindingChanged;
                        return false;
                    }
                }
                for (var i = 0; i < nodeEntries.Length; i++)
                {
                    if (!TryReadBlueprintIngredient(currentLinks[i], out var current) || nodeEntries[i] != current)
                    {
                        status = WorldReadStatus.BindingChanged;
                        return false;
                    }
                }
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                snapshot = new BlueprintRequirementsSnapshot(networkId, before.RecipeId, before.IsActive,
                    before.IsPendingDestroy, before.HasRuntimeIngredients, recipeCount, recipeEntries.Length,
                    before.LinkCount, nodeEntries.Length, SummarizeBlueprintRequirements(recipeEntries, nodeEntries));
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static bool TryReadBlueprintRecipeIngredient(StructureCraftingRecipeIngredient ingredient,
            out BlueprintRecipeIngredientRead result)
        {
            result = default;
            if (ingredient is null || ingredient.WasCollected)
                return false;
            result = new BlueprintRecipeIngredientRead(ingredient.Pointer, ingredient.ItemId, ingredient.Count,
                ingredient._DisabledCount_k__BackingField);
            return true;
        }

        private static BlueprintItemRequirementSnapshot[] SummarizeBlueprintRequirements(
            BlueprintRecipeIngredientRead[] recipeEntries, BlueprintIngredientRead[] nodeEntries)
        {
            var totals = new Dictionary<int, BlueprintRequirementTotals>();
            foreach (var entry in recipeEntries)
            {
                totals.TryGetValue(entry.ItemId, out var total);
                totals[entry.ItemId] = total with
                {
                    RecipeEntryCount = total.RecipeEntryCount + 1,
                    RecipeDeclaredCount = total.RecipeDeclaredCount + entry.DeclaredCount,
                    RecipeDisabledCount = total.RecipeDisabledCount + entry.DisabledCount,
                    HasNegativeCounts = total.HasNegativeCounts || entry.DeclaredCount < 0 || entry.DisabledCount < 0
                };
            }
            foreach (var entry in nodeEntries)
            {
                totals.TryGetValue(entry.ItemId, out var total);
                totals[entry.ItemId] = total with
                {
                    NodeLinkCount = total.NodeLinkCount + 1,
                    NodeDeclaredCount = total.NodeDeclaredCount + entry.DeclaredCount,
                    NodeDisabledCount = total.NodeDisabledCount + entry.DisabledCount,
                    NodeAppliedCount = total.NodeAppliedCount + entry.AppliedCount,
                    NodeTotalCount = total.NodeTotalCount + entry.TotalCount,
                    NodeRemainingCount = total.NodeRemainingCount + entry.RemainingCount,
                    HasNegativeCounts = total.HasNegativeCounts || entry.DeclaredCount < 0 || entry.DisabledCount < 0
                        || entry.AppliedCount < 0 || entry.TotalCount < 0 || entry.RemainingCount < 0
                };
            }
            var result = new BlueprintItemRequirementSnapshot[totals.Count];
            var index = 0;
            foreach (var entry in totals)
            {
                var total = entry.Value;
                result[index++] = new BlueprintItemRequirementSnapshot(entry.Key, total.RecipeEntryCount,
                    total.RecipeDeclaredCount, total.RecipeDisabledCount, total.NodeLinkCount, total.NodeDeclaredCount,
                    total.NodeDisabledCount, total.NodeAppliedCount, total.NodeTotalCount, total.NodeRemainingCount,
                    total.HasNegativeCounts);
            }
            Array.Sort(result, static (left, right) => left.ItemId.CompareTo(right.ItemId));
            return result;
        }

        private static WorldReadStatus CaptureBlueprintProgress(ulong networkId,
            out IngredientLinks links, out BlueprintProgressBinding binding)
            => CaptureBlueprintProgress(networkId, out links, out binding, out _);

        private static WorldReadStatus CaptureBlueprintProgress(ulong networkId,
            out IngredientLinks links, out BlueprintProgressBinding binding, out StructureRecipe recipe)
        {
            links = null;
            binding = default;
            recipe = null;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (networkId == 0 || !IsLiveRangeEntity(entity) || entity.networkId.PackedValue != networkId)
                return WorldReadStatus.EntityUnavailable;
            if (!entity.TryGetComponent<StructureCraftingNode>(out var node)
                || node is null || node.WasCollected || !node)
                return WorldReadStatus.BlueprintUnavailable;
            var nodeEntity = node.entity;
            if (!IsLiveRangeEntity(nodeEntity) || nodeEntity.Pointer != entity.Pointer
                || nodeEntity.networkId.PackedValue != networkId)
                return WorldReadStatus.BlueprintUnavailable;
            recipe = node.Recipe;
            links = node._craftingIngredientLinks;
            if (recipe is null || recipe.WasCollected || !recipe || links is null || links.WasCollected)
                return WorldReadStatus.BlueprintUnavailable;

            binding = new BlueprintProgressBinding(entity.Pointer, node.Pointer, recipe.Pointer, links.Pointer,
                links._version, links.Count, recipe.Id, node._isActive, node._pendingDestroy, node._hasRuntimeIngredients);
            return WorldReadStatus.Success;
        }

        private static bool TryReadBlueprintIngredient(StructureCraftingNode.CraftingIngredientLink link,
            out BlueprintIngredientRead result)
        {
            result = default;
            if (link is null || link.WasCollected)
                return false;
            var ingredient = link.Ingredient;
            if (ingredient is null || ingredient.WasCollected)
                return false;
            result = new BlueprintIngredientRead(link.Pointer, ingredient.Pointer, ingredient.ItemId, ingredient.Count,
                ingredient.DisabledCount, link.AppliedCount, link.TotalCount, link.RemainingCount);
            return true;
        }

        private readonly record struct BlueprintProgressBinding(IntPtr EntityPointer, IntPtr NodePointer,
            IntPtr RecipePointer, IntPtr LinksPointer, int ListVersion, int LinkCount, int RecipeId,
            bool IsActive, bool IsPendingDestroy, bool HasRuntimeIngredients);

        private readonly record struct BlueprintIngredientRead(IntPtr LinkPointer, IntPtr IngredientPointer,
            int ItemId, int DeclaredCount, int DisabledCount, int AppliedCount, int TotalCount, int RemainingCount);

        private readonly record struct BlueprintRecipeIngredientRead(IntPtr IngredientPointer,
            int ItemId, int DeclaredCount, int DisabledCount);

        private readonly record struct BlueprintRequirementTotals(int RecipeEntryCount, long RecipeDeclaredCount,
            long RecipeDisabledCount, int NodeLinkCount, long NodeDeclaredCount, long NodeDisabledCount,
            long NodeAppliedCount, long NodeTotalCount, long NodeRemainingCount, bool HasNegativeCounts);
    }

    /// <summary>Managed current-node progress. This is not a completion, inventory or resource-consumption receipt.</summary>
    public sealed class BlueprintProgressSnapshot
    {
        internal BlueprintProgressSnapshot(ulong networkId, int recipeId, bool isActive,
            bool isPendingDestroy, bool hasRuntimeIngredients, BlueprintIngredientSnapshot[] ingredients)
        {
            NetworkId = networkId;
            RecipeId = recipeId;
            IsActive = isActive;
            IsPendingDestroy = isPendingDestroy;
            HasRuntimeIngredients = hasRuntimeIngredients;
            Ingredients = Array.AsReadOnly(ingredients);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong NetworkId { get; }
        /// <summary>Current structure recipe ID, not a Bolt prefab ID or an inventory item ID.</summary>
        public int RecipeId { get; }
        public bool IsActive { get; }
        public bool IsPendingDestroy { get; }
        public bool HasRuntimeIngredients { get; }
        /// <summary>Current link order, including duplicate item IDs and an empty list when present.</summary>
        public IReadOnlyList<BlueprintIngredientSnapshot> Ingredients { get; }
        /// <summary>Local read-completion time, not an atomic simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Raw counts from one native ingredient link, without clamping, aggregation or inventory inference.</summary>
    public sealed class BlueprintIngredientSnapshot
    {
        internal BlueprintIngredientSnapshot(int itemId, int declaredCount, int disabledCount, int appliedCount,
            int totalCount, int remainingCount)
        {
            ItemId = itemId;
            DeclaredCount = declaredCount;
            DisabledCount = disabledCount;
            AppliedCount = appliedCount;
            TotalCount = totalCount;
            RemainingCount = remainingCount;
        }

        public int ItemId { get; }
        public int DeclaredCount { get; }
        public int DisabledCount { get; }
        public int AppliedCount { get; }
        /// <summary>Native active total, excluding disabled ingredients.</summary>
        public int TotalCount { get; }
        public int RemainingCount { get; }
    }

    /// <summary>
    /// Managed recipe and node totals for one attached blueprint. Recipe costs can differ from runtime node costs.
    /// Counts cover only the captured prefixes and do not prove inventory possession, permission or completion.
    /// </summary>
    public sealed class BlueprintRequirementsSnapshot
    {
        internal BlueprintRequirementsSnapshot(ulong networkId, int recipeId, bool isActive,
            bool isPendingDestroy, bool hasRuntimeIngredients, int recipeIngredientCount, int capturedRecipeIngredientCount,
            int nodeIngredientLinkCount, int capturedNodeIngredientLinkCount, BlueprintItemRequirementSnapshot[] items)
        {
            NetworkId = networkId;
            RecipeId = recipeId;
            IsActive = isActive;
            IsPendingDestroy = isPendingDestroy;
            HasRuntimeIngredients = hasRuntimeIngredients;
            RecipeIngredientCount = recipeIngredientCount;
            CapturedRecipeIngredientCount = capturedRecipeIngredientCount;
            NodeIngredientLinkCount = nodeIngredientLinkCount;
            CapturedNodeIngredientLinkCount = capturedNodeIngredientLinkCount;
            Items = Array.AsReadOnly(items);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Current-world Bolt entity ID, not a persistent or cross-session identity.</summary>
        public ulong NetworkId { get; }
        public int RecipeId { get; }
        public bool IsActive { get; }
        public bool IsPendingDestroy { get; }
        public bool HasRuntimeIngredients { get; }
        public int RecipeIngredientCount { get; }
        public int CapturedRecipeIngredientCount { get; }
        public int NodeIngredientLinkCount { get; }
        public int CapturedNodeIngredientLinkCount { get; }
        public bool RecipeIngredientsTruncated => CapturedRecipeIngredientCount < RecipeIngredientCount;
        public bool NodeIngredientsTruncated => CapturedNodeIngredientLinkCount < NodeIngredientLinkCount;
        public bool IsTruncated => RecipeIngredientsTruncated || NodeIngredientsTruncated;
        /// <summary>One entry per observed item ID, ascending by ID; duplicate source entries are summed, not discarded.</summary>
        public IReadOnlyList<BlueprintItemRequirementSnapshot> Items { get; }
        /// <summary>Local read-completion time, not an atomic simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>
    /// Signed 64-bit sums of captured native counts for one item. Recipe and node values remain separate;
    /// neither is clamped, and a zero sum is not proof that this ingredient is satisfied.
    /// </summary>
    public sealed class BlueprintItemRequirementSnapshot
    {
        internal BlueprintItemRequirementSnapshot(int itemId, int recipeEntryCount, long recipeDeclaredCount,
            long recipeDisabledCount, int nodeLinkCount, long nodeDeclaredCount, long nodeDisabledCount,
            long nodeAppliedCount, long nodeTotalCount, long nodeRemainingCount, bool hasNegativeCounts)
        {
            ItemId = itemId;
            RecipeEntryCount = recipeEntryCount;
            RecipeDeclaredCount = recipeDeclaredCount;
            RecipeDisabledCount = recipeDisabledCount;
            NodeLinkCount = nodeLinkCount;
            NodeDeclaredCount = nodeDeclaredCount;
            NodeDisabledCount = nodeDisabledCount;
            NodeAppliedCount = nodeAppliedCount;
            NodeTotalCount = nodeTotalCount;
            NodeRemainingCount = nodeRemainingCount;
            HasNegativeCounts = hasNegativeCounts;
        }

        /// <summary>Observed item ID, not validated against the item database.</summary>
        public int ItemId { get; }
        public int RecipeEntryCount { get; }
        public long RecipeDeclaredCount { get; }
        public long RecipeDisabledCount { get; }
        public int NodeLinkCount { get; }
        public long NodeDeclaredCount { get; }
        public long NodeDisabledCount { get; }
        public long NodeAppliedCount { get; }
        /// <summary>Sum of native node TotalCount values, which exclude disabled ingredients.</summary>
        public long NodeTotalCount { get; }
        /// <summary>Sum of native node RemainingCount values, not an amount safe to add or remove from inventory.</summary>
        public long NodeRemainingCount { get; }
        /// <summary>At least one captured source count was negative, even if its sum is nonnegative.</summary>
        public bool HasNegativeCounts { get; }
    }
}
