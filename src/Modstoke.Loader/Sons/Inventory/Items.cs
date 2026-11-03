// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Read-only Sons item metadata catalog captured from the native item database,
// with bounded queries and the Items menu page.

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;
using global::Sons.Items.Core;
using Il2CppCollections = Il2CppSystem.Collections.Generic;

namespace Modstoke.Sons
{
    public static partial class ItemCatalog
    {
        public const string ServiceKey = "openacai.sons.items";
        public const int MaxPublishedItems = 512;
        public const int MaxPublishedTagsPerItem = 12;
        public const int DefaultQueryMaxResults = 32;
        public const int MaxQueryResults = 128;

        private static readonly object SyncRoot = new object();
        private static readonly IReadOnlyList<string> QueryFieldNames = Array.AsReadOnly(new[]
        {
            "id",
            "name",
            "editorName",
            "uiTitle",
            "uiTitlePlural",
            "translationKey",
            "type",
            "tag",
            "spawnable",
            "hotkeyable",
            "quickSelect",
            "perishable",
            "prefab"
        });

        private static ItemCatalogSnapshot _catalog = ItemCatalogSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _ownerThreadId;
        private static long _refreshedAt;

        public static ItemCatalogSnapshot Catalog
        {
            get
            {
                lock (SyncRoot)
                {
                    return _catalog;
                }
            }
        }

        public static IReadOnlyList<ItemSnapshot> Items => Catalog.Items;
        public static bool Ready => Catalog.Ready;
        public static IReadOnlyList<string> QueryFields => QueryFieldNames;

        /// <summary>
        /// Captures item metadata on the initialized Unity lifecycle thread.
        /// Calls from other threads return an unavailable result without replacing
        /// the published catalog or accessing the game database.
        /// </summary>
        public static ItemCatalogSnapshot Refresh()
        {
            if (!CanReadGameDatabase())
            {
                return ItemCatalogSnapshot.CreateUnavailable(
                    Catalog.DatabaseAvailable,
                    "CHECK",
                    "Item catalog refresh requires the initialized Unity lifecycle thread.");
            }

            var snapshot = CreateCatalog();
            Publish(snapshot);
            _refreshedAt = Environment.TickCount64;
            return snapshot;
        }

        /// <summary>
        /// Searches the published catalog, then the live game database if called
        /// on the initialized Unity lifecycle thread. A cache miss on any other
        /// thread returns false without a native lookup.
        /// </summary>
        public static bool TryFindItem(int id, out ItemSnapshot item)
        {
            if (Catalog.TryFindItem(id, out item))
            {
                return true;
            }

            if (!CanReadGameDatabase())
                return false;

            try
            {
                item = CreateItemSnapshot(ItemDatabaseManager.ItemById(id));
                return item != null;
            }
            catch
            {
                item = null;
                return false;
            }
        }

        public static bool TryFindItem(string nameOrTitle, out ItemSnapshot item)
        {
            item = null;
            if (string.IsNullOrWhiteSpace(nameOrTitle))
            {
                return false;
            }

            var matches = SearchItems(nameOrTitle, MaxQueryResults);
            item = matches.FirstOrDefault(candidate => IsExactItemName(candidate, nameOrTitle))
                ?? matches.FirstOrDefault();
            return item != null;
        }

        public static IReadOnlyList<ItemSnapshot> SearchItems(string text, int maxResults = DefaultQueryMaxResults)
        {
            return FindItems(new ItemQuery
            {
                Text = text,
                MaxResults = maxResults
            });
        }

        public static IReadOnlyList<ItemSnapshot> FindItems(ItemQuery query)
        {
            query = query ?? new ItemQuery();
            var items = Catalog.Items;
            if (items.Count == 0)
            {
                return Array.Empty<ItemSnapshot>();
            }

            var maxResults = NormalizeMaxResults(query.MaxResults);
            var text = NormalizeQueryText(query.Text);
            var type = NormalizeQueryText(query.Type);
            var tag = NormalizeQueryText(query.Tag);
            var translationKey = NormalizeQueryText(query.TranslationKey);

            IEnumerable<ItemSnapshot> filtered = items;
            if (query.Id.HasValue)
            {
                filtered = filtered.Where(item => item.Id == query.Id.Value);
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                filtered = filtered.Where(item => ContainsDelimitedValue(item.Type, type));
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                filtered = filtered.Where(item => item.HasTag(tag));
            }

            if (!string.IsNullOrWhiteSpace(translationKey))
            {
                filtered = filtered.Where(item => string.Equals(item.TranslationKey, translationKey, StringComparison.OrdinalIgnoreCase));
            }

            filtered = ApplyOptionalFlag(filtered, query.CanBeSpawned, item => item.CanBeSpawned);
            filtered = ApplyOptionalFlag(filtered, query.CanBeHotKeyed, item => item.CanBeHotKeyed);
            filtered = ApplyOptionalFlag(filtered, query.AllowQuickSelect, item => item.AllowQuickSelect);
            filtered = ApplyOptionalFlag(filtered, query.IsPerishable, item => item.IsPerishable);
            filtered = ApplyOptionalFlag(filtered, query.HasHeldPrefab, item => item.HasHeldPrefab);
            filtered = ApplyOptionalFlag(filtered, query.HasPickupPrefab, item => item.HasPickupPrefab);
            filtered = ApplyOptionalFlag(filtered, query.HasPropPrefab, item => item.HasPropPrefab);

            if (!string.IsNullOrWhiteSpace(text))
            {
                return filtered
                    .Select(item => new ItemSearchResult(item, ScoreTextMatch(item, text)))
                    .Where(result => result.Score > 0)
                    .OrderByDescending(result => result.Score)
                    .ThenBy(result => result.Item.Id)
                    .ThenBy(result => result.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(maxResults)
                    .Select(result => result.Item)
                    .ToArray();
            }

            return filtered
                .OrderBy(item => item.Id)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToArray();
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            _initialized = true;
            GameRuntime.SceneChanged += _ => Refresh();
            Publish(ItemCatalogSnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK item catalog provider could not be registered.");
            }
        }

        private static void Publish(ItemCatalogSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _catalog = snapshot ?? ItemCatalogSnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _catalog);
            }
        }

        private static bool CanReadGameDatabase()
        {
            return _ownerThreadId != 0 && Environment.CurrentManagedThreadId == _ownerThreadId;
        }

        private static ItemCatalogSnapshot CreateCatalog()
        {
            try
            {
                var rawItems = ReadManagerItems(out var totalCount);
                if (rawItems == null)
                {
                    ItemDatabaseManager.Initialize();
                    rawItems = ReadManagerItems(out totalCount);
                }

                if (rawItems == null)
                {
                    return ItemCatalogSnapshot.CreateUnavailable(
                        false,
                        "CHECK",
                        "Sons item database exists, but the item list has not initialized yet.");
                }

                if (totalCount <= 0)
                {
                    return ItemCatalogSnapshot.CreateUnavailable(
                        true,
                        "CHECK",
                        "Sons item database is available, but no item records were exposed.");
                }

                var items = CaptureItems(rawItems, MaxPublishedItems)
                    .Select(CreateItemSnapshot)
                    .Where(item => item != null)
                    .OrderBy(item => item.Id)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return ItemCatalogSnapshot.CreateReady(
                    totalCount,
                    items,
                    totalCount > items.Length);
            }
            catch (Exception ex)
            {
                return ItemCatalogSnapshot.CreateUnavailable(
                    false,
                    "CHECK",
                    "Sons item catalog read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ItemSnapshot CreateItemSnapshot(ItemData item)
        {
            if (item is null || item.WasCollected || !item)
                return null;

            var uiData = item.UiData;
            var tags = ReadTags(item)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ItemSnapshot(
                item.Id,
                item.Name,
                item._editorName,
                item.Type.ToString(),
                uiData?._title,
                uiData?._titlePlural,
                uiData?._description,
                uiData?._translationKey,
                uiData?.HasDescription ?? false,
                item.MaxAmount,
                item.MaxVirtualPickups,
                item.MaxWorldPickups,
                item.EachInstanceIsUnique,
                item.AllowPickupWhenInventoryIsFull,
                item.CanNotBeStored,
                item.HasFirstLook,
                item.DropsOnDeath,
                item.AlwaysDropOnUnequip,
                item.BlocksWaterInteractions,
                item.RequiresTwoHandsToEat,
                item.IsPerishable,
                item.IsVolumeContainer,
                item.IsDismemberedLimb,
                item.IsGPSLocator,
                item.CanBePlated,
                item.IsUpgradeableArrow,
                item.IsStructureElement,
                item.AllowQuickSelect,
                item.CanBeHotKeyed,
                item.HotkeyCanToggleItem,
                item.ExcludeFromSpawnAll,
                item.CanBeSpawned,
                item.IsLegacyItem,
                item.AlwaysLoadStashedFromSaves,
                HasUnityObject(item.HeldPrefab),
                HasUnityObject(item.PickupPrefab),
                HasUnityObject(item.PropPrefab),
                tags,
                CreateEquipmentSnapshot(item));
        }

        private static ItemEquipmentSnapshot CreateEquipmentSnapshot(ItemData item)
        {
            try
            {
                if (!HasUnityObject(item))
                    return ItemEquipmentSnapshot.CreateUnavailable("Item definition is no longer live.");

                // Read serialized fields only; equipment metadata does not need
                // prefab loading, equipment routines, or a local player.
                var rawSlot = item._equipmentSlot;
                ItemEquipmentSlot? slot = rawSlot switch
                {
                    EquipmentSlot.RightHand => ItemEquipmentSlot.RightHand,
                    EquipmentSlot.LeftHand => ItemEquipmentSlot.LeftHand,
                    EquipmentSlot.Chest => ItemEquipmentSlot.Chest,
                    EquipmentSlot.Feet => ItemEquipmentSlot.Feet,
                    EquipmentSlot.FullBody => ItemEquipmentSlot.FullBody,
                    EquipmentSlot.Eyes => ItemEquipmentSlot.Eyes,
                    _ => null
                };
                var rawQuickSelectSlot = item._quickSelectSlot;
                ItemQuickSelectSlot? quickSelectSlot = rawQuickSelectSlot switch
                {
                    ItemData.QuickSelectSlotTypes.Small => ItemQuickSelectSlot.Small,
                    ItemData.QuickSelectSlotTypes.Medium => ItemQuickSelectSlot.Medium,
                    ItemData.QuickSelectSlotTypes.Large => ItemQuickSelectSlot.Large,
                    ItemData.QuickSelectSlotTypes.GuideBook => ItemQuickSelectSlot.GuideBook,
                    _ => null
                };
                var poseBlend = item._leftHandWidePoseBlend;
                var finitePoseBlend = float.IsFinite(poseBlend);
                var detail = "";
                if (!slot.HasValue)
                    detail += "Unrecognized equipment slot " + (int)rawSlot + ". ";
                if (!quickSelectSlot.HasValue)
                    detail += "Unrecognized quick-select slot " + (int)rawQuickSelectSlot + ". ";
                if (!finitePoseBlend)
                    detail += "Left-hand wide pose blend is not finite. ";

                return new ItemEquipmentSnapshot(
                    detail.Length == 0 ? "READY" : "UNCERTAIN",
                    detail.Length == 0 ? "Stored item equipment metadata is available." : detail.TrimEnd(),
                    slot,
                    quickSelectSlot,
                    item._requiresBothHands,
                    item._preventEquipFromInventoryView,
                    item._firstLookRequiresBothHands,
                    finitePoseBlend ? poseBlend : null);
            }
            catch (Exception ex)
            {
                return ItemEquipmentSnapshot.CreateUnavailable(
                    "Item equipment metadata read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Il2CppCollections.IEnumerable<ItemData> ReadManagerItems(out int count)
        {
            var items = ItemDatabaseManager.Items;
            if (items != null)
            {
                count = items.Count;
                return items.Cast<Il2CppCollections.IEnumerable<ItemData>>();
            }

            var cache = ItemDatabaseManager._itemsCache;
            if (cache != null)
            {
                count = cache.Count;
                return cache.Values.Cast<Il2CppCollections.IEnumerable<ItemData>>();
            }

            var database = ItemDatabaseManager._instance;
            items = database is null || database.WasCollected || !database
                ? null
                : database._itemDataList;
            count = items?.Count ?? 0;
            return items?.Cast<Il2CppCollections.IEnumerable<ItemData>>();
        }

        private static T[] CaptureItems<T>(Il2CppCollections.IEnumerable<T> source, int limit)
        {
            // Item getters can load game data. Copy the bounded native list before
            // reading metadata, without keeping its versioned enumerator alive.
            var list = source.TryCast<Il2CppCollections.List<T>>();
            if (list != null)
            {
                var items = new T[Math.Min(list.Count, limit)];
                for (var i = 0; i < items.Length; i++)
                    items[i] = list[i];
                return items;
            }

            var captured = new List<T>();
            var enumerator = source.GetEnumerator();
            try
            {
                // IL2CPP generic enumerators expose iteration and disposal through
                // separate native interfaces, not managed IEnumerable/IDisposable.
                var iterator = enumerator.Cast<Il2CppSystem.Collections.IEnumerator>();
                for (var i = 0; i < limit && iterator.MoveNext(); i++)
                    captured.Add(enumerator.Current);
            }
            finally
            {
                enumerator.Cast<Il2CppSystem.IDisposable>().Dispose();
            }
            return captured.ToArray();
        }

        private static IEnumerable<string> ReadTags(ItemData item)
        {
            var tags = item.GetTags()
                ?? item._tags?.Cast<Il2CppCollections.IEnumerable<string>>();
            return tags == null
                ? Array.Empty<string>()
                : CaptureItems(tags, MaxPublishedTagsPerItem);
        }

        private static bool HasUnityObject(UnityEngine.Object value)
        {
            return value is not null && !value.WasCollected && value;
        }

        private static MenuContentSnapshot CreateContent()
        {
            // Open menu pages poll this, so a catalog from the last two seconds is reused.
            var snapshot = Environment.TickCount64 - _refreshedAt < 2000 ? Catalog : Refresh();
            var sections = new List<MenuSectionSnapshot>
            {
                CreateCatalogSection(snapshot),
                CreateQuerySection(snapshot),
                CreateFeatureSection(snapshot),
                CreatePrefabSection(snapshot),
                CreateSampleSection(snapshot)
            };

            return new MenuContentSnapshot(
                "Sons Items",
                "Read-only Modstoke item metadata catalog for Sons Of The Forest.",
                sections);
        }

        private static MenuSectionSnapshot CreateCatalogSection(ItemCatalogSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Catalog",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Ready ? "READY" : snapshot.Status, "Modstoke item metadata snapshot."),
                    Row("Database", snapshot.DatabaseAvailable ? "Available" : "Unavailable", snapshot.DatabaseAvailable ? "READY" : "CHECK", snapshot.Detail),
                    Row("Items", snapshot.ItemCount.ToString(), snapshot.Ready ? "READY" : "CHECK", "Total item records reported by Sons item database."),
                    Row("Published", snapshot.PublishedItemCount + "/" + snapshot.ItemCount, snapshot.Truncated ? "CHECK" : snapshot.Ready ? "READY" : "CHECK", snapshot.Truncated ? "State snapshot is bounded to the first " + MaxPublishedItems + " items." : "All discovered item records are published."),
                    Row("Mode", "Read-only", "READY", "This service does not register, mutate, or spawn items.")
                });
        }

        private static MenuSectionSnapshot CreateQuerySection(ItemCatalogSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Query API",
                new[]
                {
                    Row("Lookup", "id/name/title/translation", snapshot.Ready ? "READY" : "CHECK", "TryFindItem, SearchItems, and FindItems are Modstoke helpers."),
                    Row("Filters", "type/tag/flags/prefabs", snapshot.Ready ? "READY" : "CHECK", "Item queries can filter metadata without mutating the Sons item database."),
                    Row("Max Results", MaxQueryResults.ToString(), "INFO", "Public query results are bounded for stable menu/state usage."),
                    Row("Fields", string.Join(",", QueryFieldNames.Take(6)), "INFO", "Additional query fields: " + string.Join(",", QueryFieldNames.Skip(6)))
                });
        }

        private static MenuSectionSnapshot CreateFeatureSection(ItemCatalogSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Item Flags",
                new[]
                {
                    Row("Spawnable", snapshot.SpawnableCount.ToString(), "INFO", "Items marked spawnable by Sons metadata."),
                    Row("Hotkeys", snapshot.HotkeyableCount.ToString(), "INFO", "Items that allow direct hotkey binding."),
                    Row("Quick Select", snapshot.QuickSelectCount.ToString(), "INFO", "Items that can appear in quick-select flows."),
                    Row("Perishable", snapshot.PerishableCount.ToString(), "INFO", "Items flagged as perishable."),
                    Row("Volume Containers", snapshot.VolumeContainerCount.ToString(), "INFO", "Items flagged as volume containers."),
                    Row("GPS Locators", snapshot.GpsLocatorCount.ToString(), "INFO", "Items flagged as GPS locator items.")
                });
        }

        private static MenuSectionSnapshot CreatePrefabSection(ItemCatalogSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Prefabs",
                new[]
                {
                    Row("Held Prefabs", snapshot.HeldPrefabCount.ToString(), "INFO", "Items with a held prefab reference."),
                    Row("Pickup Prefabs", snapshot.PickupPrefabCount.ToString(), "INFO", "Items with a pickup prefab reference."),
                    Row("Prop Prefabs", snapshot.PropPrefabCount.ToString(), "INFO", "Items with a prop prefab reference.")
                });
        }

        private static MenuSectionSnapshot CreateSampleSection(ItemCatalogSnapshot snapshot)
        {
            var rows = snapshot.Items
                .Take(8)
                .Select(item => Row(
                    item.Id + " " + DisplayName(item),
                    string.IsNullOrWhiteSpace(item.Type) ? "Unknown" : item.Type,
                    item.CanBeSpawned ? "SPAWN" : "INFO",
                    "Max " + item.MaxAmount + "; title " + (string.IsNullOrWhiteSpace(item.UiTitle) ? "none" : item.UiTitle) + "; tags " + string.Join(",", item.Tags.Take(4))))
                .ToArray();

            if (rows.Length == 0)
            {
                rows = new[] { Row("Sample", "Unavailable", "CHECK", snapshot.Detail) };
            }

            return new MenuSectionSnapshot("Sample Items", rows);
        }

        private static string DisplayName(ItemSnapshot item)
        {
            if (item == null)
            {
                return "Unknown";
            }

            return item.DisplayName;
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private static IEnumerable<ItemSnapshot> ApplyOptionalFlag(
            IEnumerable<ItemSnapshot> items,
            bool? expected,
            Func<ItemSnapshot, bool> selector)
        {
            return expected.HasValue
                ? items.Where(item => selector(item) == expected.Value)
                : items;
        }

        private static int NormalizeMaxResults(int maxResults)
        {
            if (maxResults <= 0)
            {
                return DefaultQueryMaxResults;
            }

            return Math.Min(maxResults, MaxQueryResults);
        }

        private static string NormalizeQueryText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static bool IsExactItemName(ItemSnapshot item, string value)
        {
            if (item == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            return item.Id.ToString() == text
                || string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.EditorName, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.UiTitle, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.UiTitlePlural, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TranslationKey, text, StringComparison.OrdinalIgnoreCase);
        }

        private static int ScoreTextMatch(ItemSnapshot item, string text)
        {
            if (item == null || string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            if (item.Id.ToString() == text)
            {
                return 1000;
            }

            var score = 0;
            score = Math.Max(score, ScoreStringMatch(item.Name, text, 900, 700, 500));
            score = Math.Max(score, ScoreStringMatch(item.EditorName, text, 850, 650, 450));
            score = Math.Max(score, ScoreStringMatch(item.UiTitle, text, 825, 625, 425));
            score = Math.Max(score, ScoreStringMatch(item.UiTitlePlural, text, 800, 600, 400));
            score = Math.Max(score, ScoreStringMatch(item.TranslationKey, text, 775, 575, 375));
            score = Math.Max(score, ScoreStringMatch(item.Type, text, 250, 250, 250));
            if (item.Tags.Any(tag => string.Equals(tag, text, StringComparison.OrdinalIgnoreCase)))
            {
                score = Math.Max(score, 350);
            }
            else if (item.Tags.Any(tag => tag.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                score = Math.Max(score, 225);
            }

            return score;
        }

        private static int ScoreStringMatch(string value, string text, int exact, int startsWith, int contains)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            if (string.Equals(value, text, StringComparison.OrdinalIgnoreCase))
            {
                return exact;
            }

            if (value.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return startsWith;
            }

            return value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ? contains : 0;
        }

        private static bool ContainsDelimitedValue(string value, string expected)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

            return value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private readonly struct ItemSearchResult
        {
            public ItemSearchResult(ItemSnapshot item, int score)
            {
                Item = item;
                Score = score;
            }

            public ItemSnapshot Item { get; }
            public int Score { get; }
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsItems;
            public string DisplayName => "Sons Items";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 720;
            public string Source => "Modstoke Loader";
            public string Description => "Show read-only Sons item metadata exposed by Modstoke.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return ItemCatalog.CreateContent();
            }
        }
    }

    public sealed class ItemCatalogSnapshot
    {
        private readonly IReadOnlyList<ItemSnapshot> _items;
        private readonly Dictionary<int, ItemSnapshot> _itemsById = new();

        private ItemCatalogSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool databaseAvailable,
            int itemCount,
            bool truncated,
            IEnumerable<ItemSnapshot> items)
        {
            ServiceKey = serviceKey ?? ItemCatalog.ServiceKey;
            GeneratedUtc = generatedUtc;
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            DatabaseAvailable = databaseAvailable;
            ItemCount = Math.Max(0, itemCount);
            _items = Array.AsReadOnly((items ?? Array.Empty<ItemSnapshot>())
                .Where(item => item != null)
                .ToArray());
            foreach (var item in _items)
                _itemsById.TryAdd(item.Id, item);
            PublishedItemCount = _items.Count;
            Truncated = truncated;
            SpawnableCount = _items.Count(item => item.CanBeSpawned);
            HotkeyableCount = _items.Count(item => item.CanBeHotKeyed);
            QuickSelectCount = _items.Count(item => item.AllowQuickSelect);
            PerishableCount = _items.Count(item => item.IsPerishable);
            VolumeContainerCount = _items.Count(item => item.IsVolumeContainer);
            GpsLocatorCount = _items.Count(item => item.IsGPSLocator);
            HeldPrefabCount = _items.Count(item => item.HasHeldPrefab);
            PickupPrefabCount = _items.Count(item => item.HasPickupPrefab);
            PropPrefabCount = _items.Count(item => item.HasPropPrefab);
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool DatabaseAvailable { get; }
        public int ItemCount { get; }
        public int PublishedItemCount { get; }
        public bool Truncated { get; }
        public int MaxPublishedItems => ItemCatalog.MaxPublishedItems;
        public int DefaultQueryMaxResults => ItemCatalog.DefaultQueryMaxResults;
        public int MaxQueryResults => ItemCatalog.MaxQueryResults;
        public IReadOnlyList<string> QueryFields => ItemCatalog.QueryFields;
        public int SpawnableCount { get; }
        public int HotkeyableCount { get; }
        public int QuickSelectCount { get; }
        public int PerishableCount { get; }
        public int VolumeContainerCount { get; }
        public int GpsLocatorCount { get; }
        public int HeldPrefabCount { get; }
        public int PickupPrefabCount { get; }
        public int PropPrefabCount { get; }
        public IReadOnlyList<ItemSnapshot> Items => _items;

        internal bool TryFindItem(int id, out ItemSnapshot item)
        {
            return _itemsById.TryGetValue(id, out item);
        }

        internal static ItemCatalogSnapshot CreateReady(
            int totalItemCount,
            IEnumerable<ItemSnapshot> items,
            bool truncated)
        {
            var published = (items ?? Array.Empty<ItemSnapshot>()).ToArray();
            return new ItemCatalogSnapshot(
                ItemCatalog.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                published.Length > 0,
                published.Length > 0 ? "READY" : "CHECK",
                published.Length > 0
                    ? "Sons item database metadata is available through Modstoke."
                    : "Sons item database was available, but no item snapshots were published.",
                true,
                totalItemCount,
                truncated,
                published);
        }

        internal static ItemCatalogSnapshot CreateUnavailable(bool databaseAvailable, string status, string detail)
        {
            return new ItemCatalogSnapshot(
                ItemCatalog.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                status,
                detail,
                databaseAvailable,
                0,
                false,
                Array.Empty<ItemSnapshot>());
        }

        internal static ItemCatalogSnapshot CreateUnknown()
        {
            return CreateUnavailable(false, "PENDING", "Sons item catalog has not refreshed yet.");
        }
    }

    public sealed class ItemSnapshot
    {
        private readonly IReadOnlyList<string> _tags;

        internal ItemSnapshot(
            int id,
            string name,
            string editorName,
            string type,
            string uiTitle,
            string uiTitlePlural,
            string uiDescription,
            string translationKey,
            bool hasDescription,
            int maxAmount,
            int maxVirtualPickups,
            int maxWorldPickups,
            bool eachInstanceIsUnique,
            bool allowPickupWhenInventoryIsFull,
            bool canNotBeStored,
            bool hasFirstLook,
            bool dropsOnDeath,
            bool alwaysDropOnUnequip,
            bool blocksWaterInteractions,
            bool requiresTwoHandsToEat,
            bool isPerishable,
            bool isVolumeContainer,
            bool isDismemberedLimb,
            bool isGPSLocator,
            bool canBePlated,
            bool isUpgradeableArrow,
            bool isStructureElement,
            bool allowQuickSelect,
            bool canBeHotKeyed,
            bool hotkeyCanToggleItem,
            bool excludeFromSpawnAll,
            bool canBeSpawned,
            bool isLegacyItem,
            bool alwaysLoadStashedFromSaves,
            bool hasHeldPrefab,
            bool hasPickupPrefab,
            bool hasPropPrefab,
            IEnumerable<string> tags,
            ItemEquipmentSnapshot equipment)
        {
            Id = id;
            Name = name ?? "";
            EditorName = editorName ?? "";
            Type = type ?? "";
            UiTitle = uiTitle ?? "";
            UiTitlePlural = uiTitlePlural ?? "";
            UiDescription = uiDescription ?? "";
            TranslationKey = translationKey ?? "";
            HasDescription = hasDescription;
            MaxAmount = maxAmount;
            MaxVirtualPickups = maxVirtualPickups;
            MaxWorldPickups = maxWorldPickups;
            EachInstanceIsUnique = eachInstanceIsUnique;
            AllowPickupWhenInventoryIsFull = allowPickupWhenInventoryIsFull;
            CanNotBeStored = canNotBeStored;
            HasFirstLook = hasFirstLook;
            DropsOnDeath = dropsOnDeath;
            AlwaysDropOnUnequip = alwaysDropOnUnequip;
            BlocksWaterInteractions = blocksWaterInteractions;
            RequiresTwoHandsToEat = requiresTwoHandsToEat;
            IsPerishable = isPerishable;
            IsVolumeContainer = isVolumeContainer;
            IsDismemberedLimb = isDismemberedLimb;
            IsGPSLocator = isGPSLocator;
            CanBePlated = canBePlated;
            IsUpgradeableArrow = isUpgradeableArrow;
            IsStructureElement = isStructureElement;
            AllowQuickSelect = allowQuickSelect;
            CanBeHotKeyed = canBeHotKeyed;
            HotkeyCanToggleItem = hotkeyCanToggleItem;
            ExcludeFromSpawnAll = excludeFromSpawnAll;
            CanBeSpawned = canBeSpawned;
            IsLegacyItem = isLegacyItem;
            AlwaysLoadStashedFromSaves = alwaysLoadStashedFromSaves;
            HasHeldPrefab = hasHeldPrefab;
            HasPickupPrefab = hasPickupPrefab;
            HasPropPrefab = hasPropPrefab;
            Equipment = equipment;
            _tags = Array.AsReadOnly((tags ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray());
        }

        public int Id { get; }
        public string Name { get; }
        public string EditorName { get; }
        public string Type { get; }
        public string UiTitle { get; }
        public string UiTitlePlural { get; }
        public string UiDescription { get; }
        public string TranslationKey { get; }
        public bool HasDescription { get; }
        public int MaxAmount { get; }
        public int MaxVirtualPickups { get; }
        public int MaxWorldPickups { get; }
        public bool EachInstanceIsUnique { get; }
        public bool AllowPickupWhenInventoryIsFull { get; }
        public bool CanNotBeStored { get; }
        public bool HasFirstLook { get; }
        public bool DropsOnDeath { get; }
        public bool AlwaysDropOnUnequip { get; }
        public bool BlocksWaterInteractions { get; }
        public bool RequiresTwoHandsToEat { get; }
        public bool IsPerishable { get; }
        public bool IsVolumeContainer { get; }
        public bool IsDismemberedLimb { get; }
        public bool IsGPSLocator { get; }
        public bool CanBePlated { get; }
        public bool IsUpgradeableArrow { get; }
        public bool IsStructureElement { get; }
        public bool AllowQuickSelect { get; }
        public bool CanBeHotKeyed { get; }
        public bool HotkeyCanToggleItem { get; }
        public bool ExcludeFromSpawnAll { get; }
        public bool CanBeSpawned { get; }
        public bool IsLegacyItem { get; }
        public bool AlwaysLoadStashedFromSaves { get; }
        public bool HasHeldPrefab { get; }
        public bool HasPickupPrefab { get; }
        public bool HasPropPrefab { get; }

        /// <summary>
        /// Detached equipment-definition data captured with this item. This is
        /// not a live equipment check or permission to equip the item.
        /// </summary>
        public ItemEquipmentSnapshot Equipment { get; }

        public IReadOnlyList<string> Tags => _tags;
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    return Name;
                }

                if (!string.IsNullOrWhiteSpace(UiTitle))
                {
                    return UiTitle;
                }

                if (!string.IsNullOrWhiteSpace(EditorName))
                {
                    return EditorName;
                }

                return "Item " + Id;
            }
        }

        public bool HasTag(string tag)
        {
            return !string.IsNullOrWhiteSpace(tag)
                && _tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        public bool HasType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            return Type.IndexOf(type.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public enum ItemEquipmentSlot
    {
        RightHand = 0,
        LeftHand = 1,
        Chest = 2,
        Feet = 3,
        FullBody = 4,
        Eyes = 5
    }

    public enum ItemQuickSelectSlot
    {
        Small = 0,
        Medium = 1,
        Large = 2,
        GuideBook = 3
    }

    /// <summary>
    /// Stored item equipment settings, not current player state. Nullable values
    /// are unknown when unavailable; an unrecognized slot or non-finite blend
    /// remains null with UNCERTAIN status while other captured values are retained.
    /// </summary>
    public sealed class ItemEquipmentSnapshot
    {
        internal ItemEquipmentSnapshot(
            string status,
            string detail,
            ItemEquipmentSlot? slot,
            ItemQuickSelectSlot? quickSelectSlot,
            bool? requiresBothHands,
            bool? preventEquipFromInventoryView,
            bool? firstLookRequiresBothHands,
            float? leftHandWidePoseBlend)
        {
            Status = status;
            Detail = detail;
            Slot = slot;
            QuickSelectSlot = quickSelectSlot;
            RequiresBothHands = requiresBothHands;
            PreventEquipFromInventoryView = preventEquipFromInventoryView;
            FirstLookRequiresBothHands = firstLookRequiresBothHands;
            LeftHandWidePoseBlend = leftHandWidePoseBlend;
        }

        public bool Ready => Status == "READY";
        public string Status { get; }
        public string Detail { get; }

        /// <summary>
        /// Nominal definition slot. A value does not make a non-equippable item equippable.
        /// </summary>
        public ItemEquipmentSlot? Slot { get; }

        /// <summary>
        /// Stored quick-select category; use the parent item's AllowQuickSelect flag as well.
        /// </summary>
        public ItemQuickSelectSlot? QuickSelectSlot { get; }

        public bool? RequiresBothHands { get; }
        public bool? PreventEquipFromInventoryView { get; }
        public bool? FirstLookRequiresBothHands { get; }
        public float? LeftHandWidePoseBlend { get; }

        internal static ItemEquipmentSnapshot CreateUnavailable(string detail)
        {
            return new ItemEquipmentSnapshot(
                "UNAVAILABLE", detail, null, null, null, null, null, null);
        }
    }

    public sealed class ItemQuery
    {
        public int? Id { get; set; }
        public string Text { get; set; } = "";
        public string Type { get; set; } = "";
        public string Tag { get; set; } = "";
        public string TranslationKey { get; set; } = "";
        public bool? CanBeSpawned { get; set; }
        public bool? CanBeHotKeyed { get; set; }
        public bool? AllowQuickSelect { get; set; }
        public bool? IsPerishable { get; set; }
        public bool? HasHeldPrefab { get; set; }
        public bool? HasPickupPrefab { get; set; }
        public bool? HasPropPrefab { get; set; }
        public int MaxResults { get; set; } = ItemCatalog.DefaultQueryMaxResults;
    }
}
