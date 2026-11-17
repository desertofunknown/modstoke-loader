// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Modstoke.Loader;
using Sons.Gameplay;
using Sons.Items.Core;

namespace Modstoke.Sons
{
    /// <summary>
    /// Adds mod items to the game's container loot tables: the weighted lists the weapon cases, storage crates,
    /// suitcases and graves roll from when they are opened. A container rolls from a seed every peer shares, so every
    /// peer needs the same tables: register at mod start in every process, clients and servers alike, as the item ids
    /// themselves already are. Containers a save has already opened keep what they rolled. In multiplayer the entries
    /// stay in only while <see cref="ContentAgreement.CanUseNetworkedContent"/>.
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

        private const int MaximumEntries = 256;
        private static readonly List<Entry> Entries = new();
        private static Entry[] _snapshot = Array.Empty<Entry>();
        private static bool _loggedFailure, _hooked;

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
                Volatile.Write(ref _snapshot, Entries.ToArray());
            }
            if (!_hooked) Hook();
            return true;
        }

        // Every roll, from a container, a hanging body, a dismantled structure or a crafted bundle, goes through the
        // table's GetRandomizedContentsInternal, so a table is brought up to date just before it rolls: no search of
        // the loaded objects, and a table that streams in late is covered all the same. A table the game has already
        // rolled from is unaffected: opened containers are saved as such.
        private static void Hook()
        {
            _hooked = true;
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-loot-tables");
                harmony.Patch(HarmonyLib.AccessTools.Method(typeof(ContainerItemSpawnerData),
                        nameof(ContainerItemSpawnerData.GetRandomizedContentsInternal), new[] { typeof(int) }),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(LootTables), nameof(BeforeRoll)));
            }
            catch (Exception error) { Failed(error); }
        }

        private static void BeforeRoll(ContainerItemSpawnerData __instance)
        {
            try { Prepare(__instance); }
            catch (Exception error) { Failed(error); }
        }

        /// <summary>Puts the mod entries for this table in, or takes them out while mod content isn't shared.</summary>
        internal static void Prepare(ContainerItemSpawnerData table)
        {
            var entries = Volatile.Read(ref _snapshot);
            if (entries.Length == 0 || table == null || table._items == null) return;
            var name = table.name;
            // A container rolls the same seed on every peer, so mod loot is in the tables only while every player
            // is known to have the mods; a session that can't promise that takes it out again.
            bool share = ContentAgreement.CanUseNetworkedContent;
            int changed = 0;
            foreach (var entry in entries)
                if (entry.Table == name && (share ? Append(table, entry) : Remove(table, entry))) changed++;
            if (changed > 0)
                RuntimeEntryPoint.LogSource?.LogInfo(changed + " mod loot entr" + (changed == 1 ? "y" : "ies")
                    + (share ? " added to the game's " + name + " table."
                        : " taken out of the game's " + name + " table (mod content: " + ContentAgreement.Sharing + ")."));
        }

        private static void Failed(Exception error)
        {
            if (_loggedFailure) return;
            _loggedFailure = true;
            RuntimeEntryPoint.LogSource?.LogWarning("Mod loot could not be added to the container tables: " + error.Message);
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

        private static bool Remove(ContainerItemSpawnerData table, Entry entry)
        {
            var items = table._items;
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i] != null && items[i]._id == entry.ItemId)
                {
                    items.RemoveAt(i);
                    return true;
                }
            return false;
        }
    }
}
