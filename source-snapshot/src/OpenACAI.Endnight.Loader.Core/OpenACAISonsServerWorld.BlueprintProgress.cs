// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Crafting.Structures;
using IngredientLinks = Il2CppSystem.Collections.Generic.List<Sons.Crafting.Structures.StructureCraftingNode.CraftingIngredientLink>;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerWorld
    {
        /// <summary>
        /// Reads a current attached blueprint's ingredient links on the server game thread.
        /// Counts describe the node, not a player's inventory or proof that a built structure exists.
        /// </summary>
        public static bool TryReadBlueprintProgress(ulong networkId, out OpenACAISonsBlueprintProgressSnapshot snapshot,
            out OpenACAISonsWorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;

                status = CaptureBlueprintProgress(networkId, out var links, out var before);
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                var entries = new BlueprintIngredientRead[before.LinkCount];
                for (var i = 0; i < entries.Length; i++)
                {
                    if (!TryReadBlueprintIngredient(links[i], out entries[i]))
                    {
                        status = OpenACAISonsWorldReadStatus.BlueprintUnavailable;
                        return false;
                    }
                }

                status = CaptureBlueprintProgress(networkId, out var currentLinks, out var after);
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = OpenACAISonsWorldReadStatus.BindingChanged;
                    return false;
                }
                for (var i = 0; i < entries.Length; i++)
                {
                    if (!TryReadBlueprintIngredient(currentLinks[i], out var current) || entries[i] != current)
                    {
                        status = OpenACAISonsWorldReadStatus.BindingChanged;
                        return false;
                    }
                }

                var ingredients = new OpenACAISonsBlueprintIngredientSnapshot[entries.Length];
                for (var i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    ingredients[i] = new OpenACAISonsBlueprintIngredientSnapshot(entry.ItemId, entry.DeclaredCount,
                        entry.DisabledCount, entry.AppliedCount, entry.TotalCount, entry.RemainingCount);
                }
                snapshot = new OpenACAISonsBlueprintProgressSnapshot(networkId, before.RecipeId, before.IsActive,
                    before.IsPendingDestroy, before.HasRuntimeIngredients, ingredients);
                status = OpenACAISonsWorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsWorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static OpenACAISonsWorldReadStatus CaptureBlueprintProgress(ulong networkId,
            out IngredientLinks links, out BlueprintProgressBinding binding)
        {
            links = null;
            binding = default;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (networkId == 0 || !IsLiveRangeEntity(entity) || entity.networkId.PackedValue != networkId)
                return OpenACAISonsWorldReadStatus.EntityUnavailable;
            if (!entity.TryGetComponent<StructureCraftingNode>(out var node)
                || node is null || node.WasCollected || !node)
                return OpenACAISonsWorldReadStatus.BlueprintUnavailable;
            var nodeEntity = node.entity;
            if (!IsLiveRangeEntity(nodeEntity) || nodeEntity.Pointer != entity.Pointer
                || nodeEntity.networkId.PackedValue != networkId)
                return OpenACAISonsWorldReadStatus.BlueprintUnavailable;
            var recipe = node.Recipe;
            links = node._craftingIngredientLinks;
            if (recipe is null || recipe.WasCollected || !recipe || links is null || links.WasCollected)
                return OpenACAISonsWorldReadStatus.BlueprintUnavailable;

            binding = new BlueprintProgressBinding(entity.Pointer, node.Pointer, recipe.Pointer, links.Pointer,
                links._version, links.Count, recipe.Id, node._isActive, node._pendingDestroy, node._hasRuntimeIngredients);
            return OpenACAISonsWorldReadStatus.Success;
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
    }

    /// <summary>Managed current-node progress. This is not a completion, inventory or resource-consumption receipt.</summary>
    public sealed class OpenACAISonsBlueprintProgressSnapshot
    {
        internal OpenACAISonsBlueprintProgressSnapshot(ulong networkId, int recipeId, bool isActive,
            bool isPendingDestroy, bool hasRuntimeIngredients, OpenACAISonsBlueprintIngredientSnapshot[] ingredients)
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
        public IReadOnlyList<OpenACAISonsBlueprintIngredientSnapshot> Ingredients { get; }
        /// <summary>Local read-completion time, not an atomic simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Raw counts from one native ingredient link, without clamping, aggregation or inventory inference.</summary>
    public sealed class OpenACAISonsBlueprintIngredientSnapshot
    {
        internal OpenACAISonsBlueprintIngredientSnapshot(int itemId, int declaredCount, int disabledCount, int appliedCount,
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
}
