// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        public const int MaximumCraftingMatEntries = 256;

        /// <summary>
        /// Copies a bounded prefix of the live local crafting mat's ingredient rows.
        /// Reads stored fields without refreshing recipes, invoking crafting callbacks or
        /// consuming items. Duplicate rows and raw counts are preserved. True can return
        /// a truncated snapshot; omitted rows are not absent ingredients. This sequential
        /// observation does not establish craftability, output capacity or a reservation.
        /// </summary>
        public static bool TryReadCraftingMat(out CraftingMatSnapshot snapshot,
            out string status, int maxEntries = MaximumCraftingMatEntries)
        {
            snapshot = null;
            status = "";
            if (maxEntries < 1 || maxEntries > MaximumCraftingMatEntries)
            {
                status = "Crafting mat reads accept 1 through " + MaximumCraftingMatEntries + " entries.";
                return false;
            }
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = "Crafting mat reads require the initialized Unity lifecycle thread.";
                return false;
            }

            try
            {
                var player = LocalPlayer._instance;
                if (player is null || player.WasCollected || !player)
                {
                    status = "A live local player is required to read the crafting mat.";
                    return false;
                }
                var inventory = LocalPlayer.Inventory;
                if (inventory is null || inventory.WasCollected || !inventory)
                {
                    status = "The local player's inventory is not available.";
                    return false;
                }
                var system = CraftingSystem._Instance_k__BackingField;
                if (system is null || system.WasCollected || !system || !system._isInitialized)
                {
                    status = "The local crafting system is not initialized.";
                    return false;
                }
                var source = system._CurrentIngredients_k__BackingField;
                if (source is null || source.WasCollected)
                {
                    status = "The crafting mat's ingredient list is not available.";
                    return false;
                }

                int total = source.Count;
                if (total < 0)
                {
                    status = "The crafting mat returned an invalid ingredient count.";
                    return false;
                }
                var rows = new RecipeIngredientSnapshot[Math.Min(total, maxEntries)];
                for (int i = 0; i < rows.Length; i++)
                {
                    var row = source[i];
                    if (row is null || row.WasCollected)
                    {
                        status = "A crafting mat ingredient row is unavailable. No snapshot was returned.";
                        return false;
                    }
                    rows[i] = new RecipeIngredientSnapshot(row.ItemId, row.Count, row.IsReusable);
                }

                var currentPlayer = LocalPlayer._instance;
                var currentInventory = LocalPlayer.Inventory;
                var currentSystem = CraftingSystem._Instance_k__BackingField;
                if (player.WasCollected || !player || inventory.WasCollected || !inventory
                    || system.WasCollected || !system || !system._isInitialized || source.WasCollected
                    || currentPlayer is null || currentPlayer.WasCollected || !currentPlayer
                    || currentInventory is null || currentInventory.WasCollected || !currentInventory
                    || currentSystem is null || currentSystem.WasCollected || !currentSystem
                    || currentPlayer.Pointer != player.Pointer || currentInventory.Pointer != inventory.Pointer
                    || currentSystem.Pointer != system.Pointer)
                {
                    status = "The local crafting context changed during capture. No snapshot was returned.";
                    return false;
                }
                var currentSource = currentSystem._CurrentIngredients_k__BackingField;
                if (currentSource is null || currentSource.WasCollected || currentSource.Pointer != source.Pointer
                    || currentSource.Count != total)
                {
                    status = "The crafting mat's ingredient list changed during capture. No snapshot was returned.";
                    return false;
                }

                snapshot = new CraftingMatSnapshot(total, rows);
                status = snapshot.Truncated ? "Only the requested prefix of ingredient rows was captured." : "";
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read the local crafting mat: " + error.Message;
                return false;
            }
        }
    }

    /// <summary>Copied ingredient rows; no native item instances or recipe state are retained.</summary>
    public sealed class CraftingMatSnapshot
    {
        internal CraftingMatSnapshot(int totalRows, RecipeIngredientSnapshot[] ingredients)
        {
            TotalRows = totalRows;
            Ingredients = Array.AsReadOnly(ingredients);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public int TotalRows { get; }
        public IReadOnlyList<RecipeIngredientSnapshot> Ingredients { get; }
        public bool Truncated => Ingredients.Count < TotalRows;
        public DateTimeOffset ObservedAt { get; }
    }
}
