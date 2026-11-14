// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using Sons.Gameplay;
using Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Adds mod items to the game's container loot tables: the weighted lists the weapon cases, storage crates,
    /// suitcases and graves roll from when they are opened. A container rolls from a seed every peer shares, so every
    /// peer needs the same tables: register at mod start in every process, clients and servers alike, as the item ids
    /// themselves already are. Containers a save has already opened keep what they rolled.
    /// </summary>
    /// <remarks>The content catalog (<c>content-catalog-next-launch.flag</c>) lists every table with its items and
    /// the containers that use it. Entries go in once the item has a world pickup, which modeled items registered
    /// with <c>canBeSpawned</c> get.</remarks>
    public static class LootTables
    {
        /// <summary>The colored weapon cases in helicopter crash sites and caves.</summary>
        public const string AmmoCase = "AmmoCase", GrenadeCase = "GrenadeCase", FlaresCase = "FlaresCase", MedsCase = "Meds";
        /// <summary>Storage crates A and B, the game's tech loot (circuit boards, batteries, printer resin).</summary>
        public const string StorageCrate = "BasicStorageCrateItems";
        public const string Suitcase = "SuitcaseItems", WoodenCrate = "WoodenCrateItems", Grave = "GraveItems";

        private const int MaximumEntries = 256, Searches = 3;
        private const float SearchSpacing = 30f;
        private static readonly List<Entry> Entries = new();
        private static readonly List<ContainerItemSpawnerData> Known = new();
        private static float _nextCheck, _nextSearch;
        private static int _searches;
        private static bool _loggedFailure;

        private sealed record Entry(string ModId, string Table, int ItemId, int Weight, int MaxCount);

        /// <summary>Adds <paramref name="itemId"/> to the named native table with a roll weight (the game's own
        /// entries weigh 1 to 300). <paramref name="maxCount"/> caps how many one container may roll; -1 leaves it to
        /// the table.</summary>
        public static bool TryAdd(string modId, string table, int itemId, int weight, out string problem, int maxCount = -1)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(table)) problem = "A mod id and a table name are required.";
            else if (!ItemIds.TryGetOwner(itemId, out var owner) || !string.Equals(owner.ModId, modId.Trim(), StringComparison.Ordinal))
                problem = "Item " + itemId + " is not reserved by " + modId + ".";
            else if (weight is < 1 or > 10000) problem = "The weight must be between 1 and 10000.";
            else if (maxCount is 0 or < -1) problem = "The max count must be -1 or at least 1.";
            if (problem != null) return false;
            lock (Entries)
            {
                foreach (var existing in Entries)
                    if (existing.Table == table && existing.ItemId == itemId)
                    {
                        problem = "Item " + itemId + " is already in " + table + ".";
                        return false;
                    }
                if (Entries.Count >= MaximumEntries) { problem = "The loot entry limit is reached."; return false; }
                Entries.Add(new Entry(modId.Trim(), table.Trim(), itemId, weight, maxCount));
                // Every peer appends in the same order, whichever mod registered first.
                Entries.Sort((a, b) =>
                {
                    int order = string.CompareOrdinal(a.Table, b.Table);
                    if (order == 0) order = string.CompareOrdinal(a.ModId, b.ModId);
                    return order != 0 ? order : a.ItemId.CompareTo(b.ItemId);
                });
            }
            return true;
        }

        // Finding the tables walks every loaded object, so it runs when a world loads and twice more over the next
        // minute for late loads; after that only a table the game unloaded brings it back. In between, the tables
        // already found are topped up, which costs a few list reads. A table the game has already rolled from is
        // unaffected: opened containers are saved as such.
        internal static void Tick()
        {
            if (Entries.Count == 0) return;
            if (!GameRuntime.IsGameScene)
            {
                if (_searches != 0) { Known.Clear(); _searches = 0; }
                return;
            }
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 2f;
            try
            {
                Entry[] entries;
                lock (Entries) entries = Entries.ToArray();
                bool lost = Known.RemoveAll(table => table == null || table.WasCollected) > 0;
                if (lost || (_searches < Searches && Time.unscaledTime >= _nextSearch))
                {
                    _searches = Math.Min(_searches + 1, Searches);
                    _nextSearch = Time.unscaledTime + SearchSpacing;
                    foreach (var table in Resources.FindObjectsOfTypeAll<ContainerItemSpawnerData>())
                        if (table != null && table._items != null && !Known.Exists(known => known.Pointer == table.Pointer)
                            && Array.Exists(entries, entry => entry.Table == table.name)) Known.Add(table);
                }
                int added = 0;
                foreach (var table in Known)
                    foreach (var entry in entries)
                        if (entry.Table == table.name && Append(table, entry)) added++;
                if (added > 0)
                    RuntimeEntryPoint.LogSource?.LogInfo(added + " mod loot entr" + (added == 1 ? "y" : "ies") + " added to the game's container tables.");
            }
            catch (Exception error)
            {
                if (_loggedFailure) return;
                _loggedFailure = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Mod loot could not be added to the container tables: " + error.Message);
            }
        }

        private static bool Append(ContainerItemSpawnerData table, Entry entry)
        {
            var items = table._items;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && items[i]._id == entry.ItemId) return false;
            if (!ItemDatabaseManager.TryFindItemById(entry.ItemId, out var item) || item == null || item._pickupPrefab == null) return false;
            var template = items.Count > 0 ? items[0] : null;
            items.Add(new ContainerItemSpawnerData.PickupItemAndSpawnWeight
            {
                _id = entry.ItemId,
                _item = item._pickupPrefab.gameObject,
                _weight = entry.Weight,
                _maxCount = entry.MaxCount,
                _maxWeight = template != null ? template._maxWeight : table._maxWeight
            });
            return true;
        }
    }
}
