// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons item metadata catalog. This deliberately starts as a
// read-only snapshot so OpenACAI can expose useful SDK metadata without
// copying RedLoader/SonsSdk item registration and mutation behavior.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Items.Core;
using Il2CppCollections = Il2CppSystem.Collections.Generic;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsItems
    {
        public const string ServiceKey = "openacai.sons.items";
        public const int MaxPublishedItems = 512;
        public const int MaxPublishedTagsPerItem = 12;
        public const int DefaultQueryMaxResults = 32;
        public const int MaxQueryResults = 128;

        private static readonly object SyncRoot = new object();
        private static readonly string[] QueryFieldNames =
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
        };

        private static OpenACAISonsItemCatalogSnapshot _catalog = OpenACAISonsItemCatalogSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _ownerThreadId;

        public static OpenACAISonsItemCatalogSnapshot Catalog
        {
            get
            {
                lock (SyncRoot)
                {
                    return _catalog;
                }
            }
        }

        public static IReadOnlyList<OpenACAISonsItemSnapshot> Items => Catalog.Items;
        public static bool Ready => Catalog.Ready;
        public static IReadOnlyList<string> QueryFields => QueryFieldNames;

        /// <summary>
        /// Captures item metadata on the initialized Unity lifecycle thread.
        /// Calls from other threads return an unavailable result without replacing
        /// the published catalog or accessing the game database.
        /// </summary>
        public static OpenACAISonsItemCatalogSnapshot Refresh()
        {
            if (!CanReadGameDatabase())
            {
                return OpenACAISonsItemCatalogSnapshot.CreateUnavailable(
                    Catalog.DatabaseAvailable,
                    "CHECK",
                    "Item catalog refresh requires the initialized Unity lifecycle thread.");
            }

            var snapshot = CreateCatalog();
            Publish(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Searches the published catalog, then the live game database if called
        /// on the initialized Unity lifecycle thread. A cache miss on any other
        /// thread returns false without a native lookup.
        /// </summary>
        public static bool TryFindItem(int id, out OpenACAISonsItemSnapshot item)
        {
            item = Catalog.Items.FirstOrDefault(candidate => candidate.Id == id);
            if (item != null)
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

        public static bool TryFindItem(string nameOrTitle, out OpenACAISonsItemSnapshot item)
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

        public static IReadOnlyList<OpenACAISonsItemSnapshot> SearchItems(string text, int maxResults = DefaultQueryMaxResults)
        {
            return FindItems(new OpenACAISonsItemQuery
            {
                Text = text,
                MaxResults = maxResults
            });
        }

        public static IReadOnlyList<OpenACAISonsItemSnapshot> FindItems(OpenACAISonsItemQuery query)
        {
            query = query ?? new OpenACAISonsItemQuery();
            var items = Catalog.Items;
            if (items.Count == 0)
            {
                return Array.Empty<OpenACAISonsItemSnapshot>();
            }

            var maxResults = NormalizeMaxResults(query.MaxResults);
            var text = NormalizeQueryText(query.Text);
            var type = NormalizeQueryText(query.Type);
            var tag = NormalizeQueryText(query.Tag);
            var translationKey = NormalizeQueryText(query.TranslationKey);

            IEnumerable<OpenACAISonsItemSnapshot> filtered = items;
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
                    .Select(item => new OpenACAISonsItemSearchResult(item, ScoreTextMatch(item, text)))
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
            OpenACAISons.SceneChanged += _ => Refresh();
            Publish(OpenACAISonsItemCatalogSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK item catalog provider could not be registered.");
            }
        }

        private static void Publish(OpenACAISonsItemCatalogSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _catalog = snapshot ?? OpenACAISonsItemCatalogSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _catalog);
            }
        }

        private static bool CanReadGameDatabase()
        {
            return _ownerThreadId != 0 && Environment.CurrentManagedThreadId == _ownerThreadId;
        }

        private static OpenACAISonsItemCatalogSnapshot CreateCatalog()
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
                    return OpenACAISonsItemCatalogSnapshot.CreateUnavailable(
                        false,
                        "CHECK",
                        "Sons item database exists, but the item list has not initialized yet.");
                }

                if (totalCount <= 0)
                {
                    return OpenACAISonsItemCatalogSnapshot.CreateUnavailable(
                        true,
                        "CHECK",
                        "Sons item database is available, but no item records were exposed.");
                }

                var items = EnumerateItems(rawItems, MaxPublishedItems)
                    .Select(CreateItemSnapshot)
                    .Where(item => item != null)
                    .OrderBy(item => item.Id)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return OpenACAISonsItemCatalogSnapshot.CreateReady(
                    totalCount,
                    items,
                    totalCount > items.Length);
            }
            catch (Exception ex)
            {
                return OpenACAISonsItemCatalogSnapshot.CreateUnavailable(
                    false,
                    "CHECK",
                    "Sons item catalog read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static OpenACAISonsItemSnapshot CreateItemSnapshot(ItemData item)
        {
            if (item is null || item.WasCollected || !item)
                return null;

            var uiData = item.UiData;
            var tags = ReadTags(item)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new OpenACAISonsItemSnapshot(
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
                tags);
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

        private static IEnumerable<T> EnumerateItems<T>(Il2CppCollections.IEnumerable<T> source, int limit)
        {
            var enumerator = source.GetEnumerator();
            try
            {
                // IL2CPP generic enumerators expose iteration and disposal through
                // separate native interfaces, not managed IEnumerable/IDisposable.
                var iterator = enumerator.Cast<Il2CppSystem.Collections.IEnumerator>();
                for (var i = 0; i < limit && iterator.MoveNext(); i++)
                    yield return enumerator.Current;
            }
            finally
            {
                enumerator.Cast<Il2CppSystem.IDisposable>().Dispose();
            }
        }

        private static IEnumerable<string> ReadTags(ItemData item)
        {
            var tags = item.GetTags()
                ?? item._tags?.Cast<Il2CppCollections.IEnumerable<string>>();
            return tags == null
                ? Array.Empty<string>()
                : EnumerateItems(tags, MaxPublishedTagsPerItem);
        }

        private static bool HasUnityObject(UnityEngine.Object value)
        {
            return value is not null && !value.WasCollected && value;
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            var sections = new List<OpenACAIMenuSectionSnapshot>
            {
                CreateCatalogSection(snapshot),
                CreateQuerySection(snapshot),
                CreateFeatureSection(snapshot),
                CreatePrefabSection(snapshot),
                CreateSampleSection(snapshot)
            };

            return new OpenACAIMenuContentSnapshot(
                "Sons Items",
                "Read-only OpenACAI item metadata catalog for Sons Of The Forest.",
                sections);
        }

        private static OpenACAIMenuSectionSnapshot CreateCatalogSection(OpenACAISonsItemCatalogSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Catalog",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Ready ? "READY" : snapshot.Status, "OpenACAI-owned item metadata snapshot."),
                    Row("Database", snapshot.DatabaseAvailable ? "Available" : "Unavailable", snapshot.DatabaseAvailable ? "READY" : "CHECK", snapshot.Detail),
                    Row("Items", snapshot.ItemCount.ToString(), snapshot.Ready ? "READY" : "CHECK", "Total item records reported by Sons item database."),
                    Row("Published", snapshot.PublishedItemCount + "/" + snapshot.ItemCount, snapshot.Truncated ? "CHECK" : snapshot.Ready ? "READY" : "CHECK", snapshot.Truncated ? "State snapshot is bounded to the first " + MaxPublishedItems + " items." : "All discovered item records are published."),
                    Row("Mode", "Read-only", "READY", "This service does not register, mutate, or spawn items.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateQuerySection(OpenACAISonsItemCatalogSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Query API",
                new[]
                {
                    Row("Lookup", "id/name/title/translation", snapshot.Ready ? "READY" : "CHECK", "TryFindItem, SearchItems, and FindItems are OpenACAI-owned helpers."),
                    Row("Filters", "type/tag/flags/prefabs", snapshot.Ready ? "READY" : "CHECK", "Item queries can filter metadata without mutating the Sons item database."),
                    Row("Max Results", MaxQueryResults.ToString(), "INFO", "Public query results are bounded for stable menu/state usage."),
                    Row("Fields", string.Join(",", QueryFieldNames.Take(6)), "INFO", "Additional query fields: " + string.Join(",", QueryFieldNames.Skip(6)))
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateFeatureSection(OpenACAISonsItemCatalogSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
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

        private static OpenACAIMenuSectionSnapshot CreatePrefabSection(OpenACAISonsItemCatalogSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Prefabs",
                new[]
                {
                    Row("Held Prefabs", snapshot.HeldPrefabCount.ToString(), "INFO", "Items with a held prefab reference."),
                    Row("Pickup Prefabs", snapshot.PickupPrefabCount.ToString(), "INFO", "Items with a pickup prefab reference."),
                    Row("Prop Prefabs", snapshot.PropPrefabCount.ToString(), "INFO", "Items with a prop prefab reference.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateSampleSection(OpenACAISonsItemCatalogSnapshot snapshot)
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

            return new OpenACAIMenuSectionSnapshot("Sample Items", rows);
        }

        private static string DisplayName(OpenACAISonsItemSnapshot item)
        {
            if (item == null)
            {
                return "Unknown";
            }

            return item.DisplayName;
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private static IEnumerable<OpenACAISonsItemSnapshot> ApplyOptionalFlag(
            IEnumerable<OpenACAISonsItemSnapshot> items,
            bool? expected,
            Func<OpenACAISonsItemSnapshot, bool> selector)
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

        private static bool IsExactItemName(OpenACAISonsItemSnapshot item, string value)
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

        private static int ScoreTextMatch(OpenACAISonsItemSnapshot item, string text)
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
            score = Math.Max(score, ScoreStringMatch(item.Type, text, 0, 0, 250));
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

            return value
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => string.Equals(part.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                || value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class OpenACAISonsItemSearchResult
        {
            public OpenACAISonsItemSearchResult(OpenACAISonsItemSnapshot item, int score)
            {
                Item = item;
                Score = score;
            }

            public OpenACAISonsItemSnapshot Item { get; }
            public int Score { get; }
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsItems;
            public string DisplayName => "Sons Items";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 720;
            public string Source => "Modstoke Loader";
            public string Description => "Show read-only Sons item metadata exposed by OpenACAI.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsItems.CreateContent();
            }
        }
    }

    public sealed class OpenACAISonsItemCatalogSnapshot
    {
        private readonly IReadOnlyList<OpenACAISonsItemSnapshot> _items;

        private OpenACAISonsItemCatalogSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            bool databaseAvailable,
            int itemCount,
            bool truncated,
            IEnumerable<OpenACAISonsItemSnapshot> items)
        {
            ServiceKey = serviceKey ?? OpenACAISonsItems.ServiceKey;
            GeneratedUtc = generatedUtc;
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            DatabaseAvailable = databaseAvailable;
            ItemCount = Math.Max(0, itemCount);
            _items = (items ?? Array.Empty<OpenACAISonsItemSnapshot>())
                .Where(item => item != null)
                .ToArray();
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
        public int MaxPublishedItems => OpenACAISonsItems.MaxPublishedItems;
        public int DefaultQueryMaxResults => OpenACAISonsItems.DefaultQueryMaxResults;
        public int MaxQueryResults => OpenACAISonsItems.MaxQueryResults;
        public IReadOnlyList<string> QueryFields => OpenACAISonsItems.QueryFields;
        public int SpawnableCount { get; }
        public int HotkeyableCount { get; }
        public int QuickSelectCount { get; }
        public int PerishableCount { get; }
        public int VolumeContainerCount { get; }
        public int GpsLocatorCount { get; }
        public int HeldPrefabCount { get; }
        public int PickupPrefabCount { get; }
        public int PropPrefabCount { get; }
        public IReadOnlyList<OpenACAISonsItemSnapshot> Items => _items;

        internal static OpenACAISonsItemCatalogSnapshot CreateReady(
            int totalItemCount,
            IEnumerable<OpenACAISonsItemSnapshot> items,
            bool truncated)
        {
            var published = (items ?? Array.Empty<OpenACAISonsItemSnapshot>()).ToArray();
            return new OpenACAISonsItemCatalogSnapshot(
                OpenACAISonsItems.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                published.Length > 0,
                published.Length > 0 ? "READY" : "CHECK",
                published.Length > 0
                    ? "Sons item database metadata is available through OpenACAI."
                    : "Sons item database was available, but no item snapshots were published.",
                true,
                totalItemCount,
                truncated,
                published);
        }

        internal static OpenACAISonsItemCatalogSnapshot CreateUnavailable(bool databaseAvailable, string status, string detail)
        {
            return new OpenACAISonsItemCatalogSnapshot(
                OpenACAISonsItems.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                status,
                detail,
                databaseAvailable,
                0,
                false,
                Array.Empty<OpenACAISonsItemSnapshot>());
        }

        internal static OpenACAISonsItemCatalogSnapshot CreateUnknown()
        {
            return CreateUnavailable(false, "PENDING", "Sons item catalog has not refreshed yet.");
        }
    }

    public sealed class OpenACAISonsItemSnapshot
    {
        private readonly IReadOnlyList<string> _tags;

        internal OpenACAISonsItemSnapshot(
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
            IEnumerable<string> tags)
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
            _tags = (tags ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
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
                && _tags.Any(value => string.Equals(value, tag.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public bool HasType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            return Type
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => string.Equals(part.Trim(), type.Trim(), StringComparison.OrdinalIgnoreCase))
                || Type.IndexOf(type.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class OpenACAISonsItemQuery
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
        public int MaxResults { get; set; } = OpenACAISonsItems.DefaultQueryMaxResults;
    }
}
