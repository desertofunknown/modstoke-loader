// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Items.Core;
using ItemList = Il2CppSystem.Collections.Generic.List<global::Sons.Items.Core.ItemData>;

namespace Modstoke.Sons
{
    public static partial class ItemCatalog
    {
        public const int MaximumLoadedItemPageEntries = 256;

        /// <summary>
        /// Copies a page from the loaded Items list without initializing the database or using its cache.
        /// Requires the initialized Unity lifecycle thread. Null previousPage starts at the first slot;
        /// otherwise the previous page must have more entries and still name the same database/list,
        /// count and version. A changed source fails with page null. maximumItems is 1..256.
        /// Entries preserve native order and unreadable slots; inspect their nullable fields.
        /// Pages do not freeze item fields, grant inventory authority or establish spawnability.
        /// </summary>
        public static bool TryReadLoadedItems(out ItemCatalogPage page, out string status,
            int maximumItems = 128, ItemCatalogPage previousPage = null)
        {
            page = null;
            status = "";
            if (maximumItems < 1 || maximumItems > MaximumLoadedItemPageEntries)
            {
                status = "An item page limit between 1 and " + MaximumLoadedItemPageEntries + " is required.";
                return false;
            }
            if (!CanReadGameDatabase())
            {
                status = "Item catalog reads require the initialized Unity lifecycle thread.";
                return false;
            }
            if (previousPage != null && !previousPage.HasMoreItems)
            {
                status = "The previous item page already reached the end of its list.";
                return false;
            }

            try
            {
                var database = ItemDatabaseManager._instance;
                if (database is null || database.WasCollected || !database)
                {
                    status = "The item database is not loaded.";
                    return false;
                }
                var databaseId = database.GetInstanceID();
                var items = ItemDatabaseManager.Items;
                if (items is null || items.WasCollected)
                {
                    status = "The loaded item list is unavailable.";
                    return false;
                }
                var total = items.Count;
                var version = items._version;
                if (total < 0)
                {
                    status = "The loaded item list returned an invalid count.";
                    return false;
                }
                if (previousPage != null
                    && (previousPage.DatabasePointer != database.Pointer
                        || previousPage.DatabaseInstanceId != databaseId
                        || previousPage.ItemsPointer != items.Pointer
                        || previousPage.TotalItemCount != total || previousPage.ListVersion != version))
                {
                    status = "The loaded item list changed between pages. Start a new read.";
                    return false;
                }

                var start = previousPage?.NextItemIndex ?? 0;
                var count = Math.Min(maximumItems, total - start);
                var entries = new ItemCatalogEntry[count];
                for (var index = 0; index < entries.Length; index++)
                {
                    if (items.WasCollected)
                    {
                        status = "The loaded item list became unavailable during the read.";
                        return false;
                    }
                    entries[index] = ReadLoadedItem(items, start + index);
                }
                if (!LoadedItemsCurrent(database, databaseId, items, total, version))
                {
                    status = "The loaded item list changed during the read. No page was returned.";
                    return false;
                }

                page = new ItemCatalogPage(database.Pointer, databaseId, items.Pointer,
                    version, start, start + count, total, entries);
                return true;
            }
            catch (Exception error)
            {
                status = "Could not read the loaded item catalog: " + error.Message;
                return false;
            }
        }

        private static bool LoadedItemsCurrent(ItemDatabase database, int databaseId,
            ItemList items, int count, int version)
        {
            var currentDatabase = ItemDatabaseManager._instance;
            if (database.WasCollected || !database || currentDatabase is null
                || currentDatabase.WasCollected || !currentDatabase
                || currentDatabase.Pointer != database.Pointer || currentDatabase.GetInstanceID() != databaseId)
                return false;
            var currentItems = ItemDatabaseManager.Items;
            return !items.WasCollected && currentItems != null && !currentItems.WasCollected
                && currentItems.Pointer == items.Pointer && items.Count == count && items._version == version;
        }

        private static ItemCatalogEntry ReadLoadedItem(ItemList items, int index)
        {
            ItemData item;
            try
            {
                item = items[index];
                if (item is null || item.WasCollected || !item)
                    return new ItemCatalogEntry(index, null, false, null, null);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Modstoke SDK item catalog entry " + index + " could not be read: " + error);
                return new ItemCatalogEntry(index, null, false, null, null);
            }

            string name;
            bool nameRead;
            try { name = item.Name; nameRead = true; }
            catch (Exception) { name = null; nameRead = false; }
            int? id;
            try { id = item.Id; }
            catch (Exception) { id = null; }
            int? typeMask;
            try { typeMask = (int)item.Type; }
            catch (Exception) { typeMask = null; }
            return new ItemCatalogEntry(index, name, nameRead, id, typeMask);
        }
    }

    /// <summary>
    /// Copied slots from one loaded-list read. Continuation retains only source identity values;
    /// it does not keep native objects alive or guarantee unchanged item fields between pages.
    /// </summary>
    public sealed class ItemCatalogPage
    {
        internal ItemCatalogPage(IntPtr databasePointer, int databaseInstanceId, IntPtr itemsPointer,
            int listVersion, int startItemIndex, int nextItemIndex, int totalItemCount, ItemCatalogEntry[] entries)
        {
            DatabasePointer = databasePointer;
            DatabaseInstanceId = databaseInstanceId;
            ItemsPointer = itemsPointer;
            ListVersion = listVersion;
            StartItemIndex = startItemIndex;
            NextItemIndex = nextItemIndex;
            TotalItemCount = totalItemCount;
            Entries = Array.AsReadOnly(entries);
        }

        internal IntPtr DatabasePointer { get; }
        internal int DatabaseInstanceId { get; }
        internal IntPtr ItemsPointer { get; }
        internal int ListVersion { get; }
        public int StartItemIndex { get; }
        public int NextItemIndex { get; }
        public int TotalItemCount { get; }
        public bool HasMoreItems => NextItemIndex < TotalItemCount;
        public IReadOnlyList<ItemCatalogEntry> Entries { get; }
    }

    /// <summary>
    /// Raw copied fields from one native list slot, without filtering or deduplication.
    /// Unreadable IDs and signed type masks are null; unreadable slots contain no fields.
    /// NameReadSucceeded distinguishes a native-null name from an unreadable name.
    /// </summary>
    public sealed class ItemCatalogEntry
    {
        internal ItemCatalogEntry(int index, string name, bool nameReadSucceeded, int? id, int? typeMask)
        {
            Index = index;
            Name = name;
            NameReadSucceeded = nameReadSucceeded;
            Id = id;
            TypeMask = typeMask;
        }

        public int Index { get; }
        public string Name { get; }
        public bool NameReadSucceeded { get; }
        public int? Id { get; }
        public int? TypeMask { get; }
    }
}
