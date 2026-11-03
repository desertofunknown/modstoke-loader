// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Item declarations remain separate from explicit native database registration.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class ItemRegistration
    {
        public const string ServiceKey = "openacai.sons.itemRegistration";
        public const int MaxRegisteredItemDefinitionCount = 512;
        public const int MaxItemKeyLength = 96;
        public const int MaxDisplayNameLength = 128;
        public const int MaxItemTypeLength = 96;
        public const int MaxTagsPerItem = 16;
        public const int MaxTagLength = 48;
        public const int MaxAssetReferenceLength = 192;

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, CustomItemDefinitionSnapshot> RegisteredDefinitions =
            new Dictionary<string, CustomItemDefinitionSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlyList<string> SupportedDefinitionKindNames = Array.AsReadOnly(new[]
        {
            "custom-item",
            "base-item-clone"
        });

        private static readonly IReadOnlyList<string> SupportedAssetReferenceKindNames = Array.AsReadOnly(new[]
        {
            "none",
            "asset-bundle",
            "prefab-path",
            "addressable-key"
        });

        private static ItemRegistrationSnapshot _state = ItemRegistrationSnapshot.CreateUnknown();
        private static bool _initialized;
        private static long _refreshedAt;
        private static int _planRequestCount;
        private static int _acceptedPlanCount;
        private static int _rejectedPlanCount;
        private static string _lastPlanKind = "none";
        private static string _lastPlanStatus = "INFO";
        private static string _lastPlanDetail = "No item registration plan has been validated.";
        private static string _lastPlanReason = "none";
        private static string _lastPlanModId = "";
        private static string _lastPlanItemKey = "";
        private static int _lastPlanItemId;
        private static int _lastPlanBaseItemId;

        public static ItemRegistrationSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        /// <summary>Declaration/catalog readiness. Use RuntimeRegistrationReady for native execution.</summary>
        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => true;
        public static bool RuntimeItemRegistrationEnabled => true;
        public static IReadOnlyList<string> SupportedDefinitionKinds => SupportedDefinitionKindNames;
        public static IReadOnlyList<string> SupportedAssetReferenceKinds => SupportedAssetReferenceKindNames;

        public static IReadOnlyList<CustomItemDefinitionSnapshot> GetRegisteredItemDefinitions()
        {
            RefreshRuntimeRegistrations();
            lock (SyncRoot)
            {
                return RegisteredDefinitions.Values
                    .OrderBy(definition => definition.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(definition => definition.ItemId)
                    .ThenBy(definition => definition.ItemKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        public static bool TryRegisterItemDefinition(
            string modId,
            int itemId,
            string itemKey,
            string displayName,
            out CustomItemDefinitionSnapshot definition)
        {
            return TryRegisterItemDefinition(
                new CustomItemDefinition(modId, itemId, itemKey, displayName),
                out definition);
        }

        public static bool TryRegisterItemDefinition(
            string modId,
            int itemId,
            string itemKey,
            string displayName,
            string itemType,
            int baseItemId,
            out CustomItemDefinitionSnapshot definition)
        {
            return TryRegisterItemDefinition(
                new CustomItemDefinition(
                    baseItemId > 0
                        ? ItemRegistrationKind.BaseItemClone
                        : ItemRegistrationKind.CustomItem,
                    modId,
                    itemId,
                    itemKey,
                    displayName,
                    itemType,
                    baseItemId,
                    false,
                    false,
                    "",
                    "",
                    Array.Empty<string>()),
                out definition);
        }

        public static bool TryRegisterItemDefinition(
            CustomItemDefinition definition,
            out CustomItemDefinitionSnapshot snapshot)
        {
            snapshot = null;
            var plan = PlanItemRegistration(definition);
            if (plan == null || !plan.Accepted)
            {
                return false;
            }

            var created = new CustomItemDefinitionSnapshot(
                plan.ModId,
                plan.ItemId,
                plan.ItemKey,
                plan.DefinitionKind,
                plan.DefinitionKindName,
                plan.DisplayName,
                plan.ItemType,
                plan.BaseItemId,
                plan.BaseItemDisplayName,
                plan.CanBeSpawned,
                plan.CanBeHotKeyed,
                plan.AssetReferenceKind,
                plan.AssetReference,
                plan.Tags,
                false,
                DateTimeOffset.UtcNow.ToString("O"));

            lock (SyncRoot)
            {
                var key = DefinitionKey(created.ModId, created.ItemKey);
                // A declaration update must not replace the identity of a native registration.
                if (RuntimeRegistrations.ContainsKey(key))
                    return false;
                // A plan is advisory: another registration can commit before this one.
                if (RegisteredDefinitions.TryGetValue(key, out var existingByKey)
                    && existingByKey.ItemId != created.ItemId)
                {
                    return false;
                }

                if (RegisteredDefinitions.Values.Any(existing => existing.ItemId == created.ItemId
                    && (!string.Equals(existing.ModId, created.ModId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(existing.ItemKey, created.ItemKey, StringComparison.OrdinalIgnoreCase))))
                {
                    return false;
                }

                if (RegisteredDefinitions.Count >= MaxRegisteredItemDefinitionCount
                    && !RegisteredDefinitions.ContainsKey(key))
                {
                    return false;
                }

                RegisteredDefinitions[key] = created;
            }

            snapshot = created;
            Refresh();
            return true;
        }

        public static bool TryUnregisterItemDefinition(string modId, int itemId)
        {
            if (string.IsNullOrWhiteSpace(modId) || itemId <= 0)
            {
                return false;
            }

            bool removed;
            lock (SyncRoot)
            {
                if (!TryFindRegisteredDefinition(modId, itemId, out var definition))
                    return false;

                var key = DefinitionKey(definition.ModId, definition.ItemKey);
                if (RuntimeRegistrations.ContainsKey(key))
                    return false;

                removed = RegisteredDefinitions.Remove(key);
            }

            if (removed)
                Refresh();
            return removed;
        }

        public static bool TryUnregisterItemDefinition(string modId, string itemKey)
        {
            var normalizedModId = SaveData.NormalizeModId(modId);
            var normalizedItemKey = NormalizeKey(itemKey);
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(normalizedItemKey))
            {
                return false;
            }

            bool removed;
            lock (SyncRoot)
            {
                var key = DefinitionKey(normalizedModId, normalizedItemKey);
                if (RuntimeRegistrations.ContainsKey(key))
                    return false;
                removed = RegisteredDefinitions.Remove(key);
            }

            if (removed)
            {
                Refresh();
            }

            return removed;
        }

        public static ItemRegistrationPlan PlanCustomItemRegistration(
            string modId,
            int itemId,
            string itemKey,
            string displayName)
        {
            return PlanItemRegistration(new CustomItemDefinition(modId, itemId, itemKey, displayName));
        }

        public static ItemRegistrationPlan PlanBaseItemClone(
            string modId,
            int itemId,
            string itemKey,
            string displayName,
            int baseItemId)
        {
            return PlanItemRegistration(new CustomItemDefinition(
                ItemRegistrationKind.BaseItemClone,
                modId,
                itemId,
                itemKey,
                displayName,
                "",
                baseItemId,
                false,
                false,
                "",
                "",
                Array.Empty<string>()));
        }

        public static ItemRegistrationPlan PlanItemRegistration(CustomItemDefinition definition)
        {
            NormalizedDefinition normalized;
            string reason;
            string detail;
            var accepted = false;
            var canRegisterNow = false;
            var status = "CHECK";
            var baseItemDisplayName = "";
            var itemCatalog = ItemCatalog.Catalog;

            if (!TryNormalizeDefinition(definition, out normalized, out reason, out detail))
            {
                status = "CHECK";
            }
            else if (itemCatalog == null || !itemCatalog.Ready)
            {
                reason = "item-catalog-not-ready";
                detail = itemCatalog == null
                    ? "Modstoke item catalog has not been published yet."
                    : itemCatalog.Detail;
                status = itemCatalog == null ? "PENDING" : itemCatalog.Status;
            }
            else if (ItemCatalog.TryFindItem(normalized.ItemId, out _))
            {
                reason = "vanilla-item-id-collision";
                detail = "Sons item id " + normalized.ItemId + " already exists in the item database.";
                status = "CHECK";
            }
            else if (normalized.BaseItemId > 0 && !ItemCatalog.TryFindItem(normalized.BaseItemId, out var baseItem))
            {
                reason = "unknown-base-item";
                detail = "Base item id " + normalized.BaseItemId + " was not found in the Modstoke item catalog.";
                status = "CHECK";
            }
            else if (TryFindRegisteredDefinition(normalized.ModId, normalized.ItemKey, out var existingByKey)
                && existingByKey.ItemId != normalized.ItemId)
            {
                reason = "item-key-collision";
                detail = "The mod item key is already registered with item id " + existingByKey.ItemId + ".";
                status = "CHECK";
            }
            else if (TryFindRegisteredDefinition(normalized.ItemId, out var existingByItemId)
                && (!string.Equals(existingByItemId.ModId, normalized.ModId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existingByItemId.ItemKey, normalized.ItemKey, StringComparison.OrdinalIgnoreCase)))
            {
                reason = "item-id-collision";
                detail = "Modstoke item id " + normalized.ItemId + " is already registered by " + existingByItemId.ModId + ".";
                status = "CHECK";
            }
            else
            {
                ItemSnapshot resolvedBaseItem = null;
                if (normalized.BaseItemId > 0)
                {
                    ItemCatalog.TryFindItem(normalized.BaseItemId, out resolvedBaseItem);
                    baseItemDisplayName = resolvedBaseItem == null ? "" : resolvedBaseItem.DisplayName;
                }

                lock (SyncRoot)
                {
                    accepted = RegisteredDefinitions.Count < MaxRegisteredItemDefinitionCount
                        || RegisteredDefinitions.ContainsKey(DefinitionKey(normalized.ModId, normalized.ItemKey));
                }

                if (!accepted)
                {
                    reason = "definition-limit-reached";
                    detail = "Modstoke already tracks the maximum of " + MaxRegisteredItemDefinitionCount + " item definitions.";
                    status = "CHECK";
                }
                else
                {
                    canRegisterNow = CanExecuteBaseClone(normalized, out reason, out detail);
                    status = canRegisterNow ? "READY" : "DEFERRED";
                }
            }

            var plan = new ItemRegistrationPlan(
                normalized == null ? ItemRegistrationKind.CustomItem : normalized.Kind,
                normalized == null ? "custom-item" : DefinitionKindName(normalized.Kind),
                normalized == null ? "" : normalized.ModId,
                normalized == null ? 0 : normalized.ItemId,
                normalized == null ? "" : normalized.ItemKey,
                normalized == null ? "" : normalized.DisplayName,
                normalized == null ? "" : normalized.ItemType,
                normalized == null ? 0 : normalized.BaseItemId,
                baseItemDisplayName,
                normalized != null && normalized.CanBeSpawned,
                normalized != null && normalized.CanBeHotKeyed,
                normalized == null ? "none" : normalized.AssetReferenceKind,
                normalized == null ? "" : normalized.AssetReference,
                normalized == null ? Array.Empty<string>() : normalized.Tags,
                accepted,
                canRegisterNow,
                RuntimeItemRegistrationEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
            RecordPlan(plan);
            Refresh();
            return plan;
        }

        public static ItemRegistrationSnapshot Refresh()
        {
            RefreshRuntimeRegistrations();
            var snapshot = CreateSnapshot();
            Publish(snapshot);
            _refreshedAt = Environment.TickCount64;
            return snapshot;
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _runtimeOwnerThreadId = Environment.CurrentManagedThreadId;
            GameRuntime.SceneChanged += _ => Refresh();
            GameRuntime.WorldChanged += _ => Refresh();
            GameRuntime.Lifecycle += _ => Refresh();
            Publish(ItemRegistrationSnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK item registration provider could not be registered.");
            }
        }

        private static void Publish(ItemRegistrationSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? ItemRegistrationSnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(ItemRegistrationPlan plan)
        {
            lock (SyncRoot)
            {
                _planRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedPlanCount++;
                }
                else
                {
                    _rejectedPlanCount++;
                }

                _lastPlanKind = plan == null ? "unknown" : plan.DefinitionKindName;
                _lastPlanStatus = plan == null ? "CHECK" : plan.Status;
                _lastPlanDetail = plan == null ? "Item registration plan was null." : plan.Detail;
                _lastPlanReason = plan == null ? "null-plan" : plan.ReasonCode;
                _lastPlanModId = plan == null ? "" : plan.ModId;
                _lastPlanItemKey = plan == null ? "" : plan.ItemKey;
                _lastPlanItemId = plan == null ? 0 : plan.ItemId;
                _lastPlanBaseItemId = plan == null ? 0 : plan.BaseItemId;
            }
        }

        private static ItemRegistrationSnapshot CreateSnapshot()
        {
            var sonsState = GameRuntime.State;
            var itemCatalog = ItemCatalog.Catalog;
            IReadOnlyList<CustomItemDefinitionSnapshot> definitions;
            int planRequestCount;
            int acceptedPlanCount;
            int rejectedPlanCount;
            string lastPlanKind;
            string lastPlanStatus;
            string lastPlanDetail;
            string lastPlanReason;
            string lastPlanModId;
            string lastPlanItemKey;
            int lastPlanItemId;
            int lastPlanBaseItemId;
            lock (SyncRoot)
            {
                definitions = RegisteredDefinitions.Values
                    .OrderBy(definition => definition.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(definition => definition.ItemId)
                    .ThenBy(definition => definition.ItemKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                planRequestCount = _planRequestCount;
                acceptedPlanCount = _acceptedPlanCount;
                rejectedPlanCount = _rejectedPlanCount;
                lastPlanKind = _lastPlanKind ?? "";
                lastPlanStatus = _lastPlanStatus ?? "";
                lastPlanDetail = _lastPlanDetail ?? "";
                lastPlanReason = _lastPlanReason ?? "";
                lastPlanModId = _lastPlanModId ?? "";
                lastPlanItemKey = _lastPlanItemKey ?? "";
                lastPlanItemId = _lastPlanItemId;
                lastPlanBaseItemId = _lastPlanBaseItemId;
            }

            var itemCatalogReady = itemCatalog != null && itemCatalog.Ready;
            var collisionCount = CountCatalogCollisions(definitions);
            return new ItemRegistrationSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                itemCatalogReady,
                itemCatalog == null ? "PENDING" : itemCatalogReady ? "READY" : itemCatalog.Status,
                itemCatalog == null
                    ? "Sons item catalog has not refreshed yet."
                    : itemCatalogReady
                        ? "Item declarations are available. Explicit execution registers authored native items or supported simple base clones; it does not create inventory layouts or network content."
                        : itemCatalog.Detail,
                sonsState == null ? "" : sonsState.SceneName,
                sonsState == null ? "" : sonsState.SceneKind,
                sonsState != null && sonsState.IsInWorld,
                itemCatalogReady,
                itemCatalog == null ? "PENDING" : itemCatalog.Status,
                itemCatalog == null ? 0 : itemCatalog.PublishedItemCount,
                PlanningEnabled,
                RuntimeItemRegistrationEnabled,
                definitions,
                collisionCount,
                SupportedDefinitionKindNames,
                SupportedAssetReferenceKindNames,
                MaxRegisteredItemDefinitionCount,
                MaxItemKeyLength,
                MaxDisplayNameLength,
                planRequestCount,
                acceptedPlanCount,
                rejectedPlanCount,
                lastPlanKind,
                lastPlanStatus,
                lastPlanDetail,
                lastPlanReason,
                lastPlanModId,
                lastPlanItemKey,
                lastPlanItemId,
                lastPlanBaseItemId);
        }

        private static int CountCatalogCollisions(IReadOnlyList<CustomItemDefinitionSnapshot> definitions)
        {
            if (definitions == null || definitions.Count == 0 || ItemCatalog.Catalog == null || !ItemCatalog.Catalog.Ready)
            {
                return 0;
            }

            return definitions.Count(definition => definition.CatalogCollision);
        }

        private static bool TryNormalizeDefinition(
            CustomItemDefinition definition,
            out NormalizedDefinition normalized,
            out string reason,
            out string detail)
        {
            normalized = null;
            reason = "invalid";
            detail = "Item definition is invalid.";
            if (definition == null)
            {
                reason = "missing-definition";
                detail = "A Modstoke item definition is required.";
                return false;
            }

            var modId = SaveData.NormalizeModId(definition.ModId);
            if (string.IsNullOrWhiteSpace(definition.ModId))
            {
                reason = "missing-mod-id";
                detail = "A mod id is required for Modstoke item ownership.";
                return false;
            }

            if (!IsSupportedDefinitionKind(definition.Kind))
            {
                reason = "unsupported-definition-kind";
                detail = "Unsupported Modstoke item definition kind.";
                return false;
            }

            if (definition.ItemId <= 0)
            {
                reason = "invalid-item-id";
                detail = "A positive custom Sons item id is required.";
                return false;
            }

            var itemKey = NormalizeKey(definition.ItemKey);
            if (string.IsNullOrWhiteSpace(itemKey))
            {
                itemKey = "item-" + definition.ItemId;
            }

            if (itemKey.Length > MaxItemKeyLength)
            {
                reason = "item-key-too-long";
                detail = "Item keys are bounded to " + MaxItemKeyLength + " characters.";
                return false;
            }

            var displayName = NormalizeText(definition.DisplayName);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                reason = "missing-display-name";
                detail = "A display name is required for custom item registration.";
                return false;
            }

            if (displayName.Length > MaxDisplayNameLength)
            {
                reason = "display-name-too-long";
                detail = "Display names are bounded to " + MaxDisplayNameLength + " characters.";
                return false;
            }

            var itemType = NormalizeText(definition.ItemType);
            if (itemType.Length > MaxItemTypeLength)
            {
                reason = "item-type-too-long";
                detail = "Item type names are bounded to " + MaxItemTypeLength + " characters.";
                return false;
            }

            var baseItemId = Math.Max(0, definition.BaseItemId);
            if (definition.Kind == ItemRegistrationKind.BaseItemClone && baseItemId <= 0)
            {
                reason = "missing-base-item";
                detail = "Base item clone definitions require a positive base item id.";
                return false;
            }

            var assetReferenceKind = NormalizeAssetReferenceKind(definition.AssetReferenceKind);
            if (!SupportedAssetReferenceKindNames.Contains(assetReferenceKind, StringComparer.OrdinalIgnoreCase))
            {
                reason = "unsupported-asset-reference";
                detail = "Unsupported item asset reference kind.";
                return false;
            }

            var assetReference = NormalizeText(definition.AssetReference);
            if (assetReference.Length > MaxAssetReferenceLength)
            {
                reason = "asset-reference-too-long";
                detail = "Asset references are bounded to " + MaxAssetReferenceLength + " characters.";
                return false;
            }

            var tags = NormalizeTags(definition.Tags);
            normalized = new NormalizedDefinition(
                definition.Kind,
                modId,
                definition.ItemId,
                itemKey,
                displayName,
                itemType,
                baseItemId,
                definition.CanBeSpawned,
                definition.CanBeHotKeyed,
                assetReferenceKind,
                assetReference,
                tags);
            reason = "validated";
            detail = "Item definition shape is valid.";
            return true;
        }

        private static bool TryFindRegisteredDefinition(string modId, int itemId, out CustomItemDefinitionSnapshot definition)
        {
            definition = null;
            var normalizedModId = SaveData.NormalizeModId(modId);
            lock (SyncRoot)
            {
                definition = RegisteredDefinitions.Values.FirstOrDefault(candidate =>
                    candidate.ItemId == itemId
                    && string.Equals(candidate.ModId, normalizedModId, StringComparison.OrdinalIgnoreCase));
            }

            return definition != null;
        }

        private static bool TryFindRegisteredDefinition(string modId, string itemKey, out CustomItemDefinitionSnapshot definition)
        {
            definition = null;
            var normalizedModId = SaveData.NormalizeModId(modId);
            var normalizedItemKey = NormalizeKey(itemKey);
            if (string.IsNullOrWhiteSpace(normalizedItemKey))
            {
                return false;
            }

            lock (SyncRoot)
            {
                return RegisteredDefinitions.TryGetValue(DefinitionKey(normalizedModId, normalizedItemKey), out definition);
            }
        }

        private static bool TryFindRegisteredDefinition(int itemId, out CustomItemDefinitionSnapshot definition)
        {
            lock (SyncRoot)
            {
                definition = RegisteredDefinitions.Values.FirstOrDefault(candidate => candidate.ItemId == itemId);
            }

            return definition != null;
        }

        private static string DefinitionKey(string modId, string itemKey)
        {
            return SaveData.NormalizeModId(modId).ToLowerInvariant() + ":" + NormalizeKey(itemKey);
        }

        private static bool IsSupportedDefinitionKind(ItemRegistrationKind kind)
        {
            return kind == ItemRegistrationKind.CustomItem
                || kind == ItemRegistrationKind.BaseItemClone;
        }

        private static string DefinitionKindName(ItemRegistrationKind kind)
        {
            switch (kind)
            {
                case ItemRegistrationKind.BaseItemClone:
                    return "base-item-clone";
                case ItemRegistrationKind.CustomItem:
                    return "custom-item";
                default:
                    return "unknown";
            }
        }

        private static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            var builder = new StringBuilder(trimmed.Length);
            var lastWasSeparator = false;
            for (var index = 0; index < trimmed.Length; index++)
            {
                var current = trimmed[index];
                if (IsSafeIdCharacter(current))
                {
                    builder.Append(char.ToLowerInvariant(current));
                    lastWasSeparator = false;
                    continue;
                }

                if (!lastWasSeparator)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }

            return builder.ToString().Trim('.', '_', '-');
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static string NormalizeAssetReferenceKind(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "none";
            }

            return NormalizeKey(value);
        }

        private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
        {
            return (tags ?? Array.Empty<string>())
                .Select(NormalizeKey)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Length <= MaxTagLength ? value : value.Substring(0, MaxTagLength).Trim('.', '_', '-'))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTagsPerItem)
                .ToArray();
        }

        private static bool IsSafeIdCharacter(char value)
        {
            return (value >= 'a' && value <= 'z')
                || (value >= 'A' && value <= 'Z')
                || (value >= '0' && value <= '9')
                || value == '.'
                || value == '_'
                || value == '-';
        }

        private static MenuContentSnapshot CreateContent()
        {
            // Open menu pages poll this, so a snapshot from the last two seconds is reused.
            var snapshot = Environment.TickCount64 - _refreshedAt < 2000 ? State : Refresh();
            return new MenuContentSnapshot(
                "Sons Item Registration",
                "Modstoke custom item definition planning and runtime-registration gate.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateDefinitionsSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateGateSection(ItemRegistrationSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Registration Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "Modstoke item registration planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can validate custom item definitions without RedLoader/SonsSdk item globals."),
                    Row("Runtime Registration", snapshot.RuntimeItemRegistrationEnabled ? "Enabled" : "Disabled", RuntimeRegistrationReady ? "READY" : "PENDING", "Explicit native registration requires the Unity thread, a live database, unused ids, and authored assets or a supported simple base clone."),
                    Row("Item Catalog", snapshot.ItemCatalogPublishedCount + " items", snapshot.ItemCatalogReady ? "READY" : snapshot.ItemCatalogStatus, "Custom item ids and base item ids are validated against Modstoke SDK Items."),
                    Row("Supported", string.Join(", ", snapshot.SupportedDefinitionKinds), "INFO", "Current Modstoke item definition kinds."),
                    Row("Asset References", string.Join(", ", snapshot.SupportedAssetReferenceKinds), "INFO", "Asset references are declarations only until item prefab wiring is verified.")
                });
        }

        private static MenuSectionSnapshot CreateDefinitionsSection(ItemRegistrationSnapshot snapshot)
        {
            var rows = new List<MenuRowSnapshot>
            {
                Row("Registered", snapshot.RegisteredDefinitionCount + "/" + snapshot.MaxRegisteredItemDefinitionCount, "INFO", "Modstoke custom item definitions tracked by the loader."),
                Row("Collisions", snapshot.CatalogCollisionCount.ToString(), snapshot.CatalogCollisionCount == 0 ? "READY" : "CHECK", "Registered custom ids that currently collide with the Sons item catalog."),
                Row("Key Limit", snapshot.MaxItemKeyLength.ToString(), "INFO", "Maximum normalized item key length."),
                Row("Name Limit", snapshot.MaxDisplayNameLength.ToString(), "INFO", "Maximum display name length.")
            };

            foreach (var definition in snapshot.RegisteredDefinitions.Take(8))
            {
                rows.Add(Row(
                    definition.ModId + ":" + definition.ItemKey,
                    definition.ItemId.ToString(),
                    definition.CatalogCollision ? "CHECK" : "READY",
                    definition.DisplayName + "; kind " + definition.DefinitionKindName + "; base " + (definition.BaseItemId <= 0 ? "none" : definition.BaseItemId.ToString())));
            }

            return new MenuSectionSnapshot("Definitions", rows);
        }

        private static MenuSectionSnapshot CreateActivitySection(ItemRegistrationSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Plan Activity",
                new[]
                {
                    Row("Plans", snapshot.PlanRequestCount + " requested", "INFO", snapshot.AcceptedPlanCount + " accepted, " + snapshot.RejectedPlanCount + " rejected."),
                    Row("Last Plan", snapshot.LastPlanKind + " / " + snapshot.LastPlanStatus, snapshot.LastPlanStatus, snapshot.LastPlanDetail),
                    Row("Last Mod", string.IsNullOrWhiteSpace(snapshot.LastPlanModId) ? "none" : snapshot.LastPlanModId, "INFO", "Last planned mod owner."),
                    Row("Last Item", snapshot.LastPlanItemId <= 0 ? "none" : snapshot.LastPlanItemId.ToString(), "INFO", "Last planned custom item id."),
                    Row("Last Key", string.IsNullOrWhiteSpace(snapshot.LastPlanItemKey) ? "none" : snapshot.LastPlanItemKey, "INFO", "Last planned item key."),
                    Row("Last Base", snapshot.LastPlanBaseItemId <= 0 ? "none" : snapshot.LastPlanBaseItemId.ToString(), "INFO", "Last planned base item id."),
                    Row("Last Reason", snapshot.LastPlanReason, snapshot.LastPlanStatus, "Last item registration reason code.")
                });
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsItemRegistration;
            public string DisplayName => "Sons Item Registration";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 723;
            public string Source => "Modstoke Loader";
            public string Description => "Show Modstoke custom item definition planning and runtime-registration gate.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return ItemRegistration.CreateContent();
            }
        }

        private sealed class NormalizedDefinition
        {
            public NormalizedDefinition(
                ItemRegistrationKind kind,
                string modId,
                int itemId,
                string itemKey,
                string displayName,
                string itemType,
                int baseItemId,
                bool canBeSpawned,
                bool canBeHotKeyed,
                string assetReferenceKind,
                string assetReference,
                IReadOnlyList<string> tags)
            {
                Kind = kind;
                ModId = modId ?? "";
                ItemId = Math.Max(0, itemId);
                ItemKey = itemKey ?? "";
                DisplayName = displayName ?? "";
                ItemType = itemType ?? "";
                BaseItemId = Math.Max(0, baseItemId);
                CanBeSpawned = canBeSpawned;
                CanBeHotKeyed = canBeHotKeyed;
                AssetReferenceKind = assetReferenceKind ?? "none";
                AssetReference = assetReference ?? "";
                Tags = tags ?? Array.Empty<string>();
            }

            public ItemRegistrationKind Kind { get; }
            public string ModId { get; }
            public int ItemId { get; }
            public string ItemKey { get; }
            public string DisplayName { get; }
            public string ItemType { get; }
            public int BaseItemId { get; }
            public bool CanBeSpawned { get; }
            public bool CanBeHotKeyed { get; }
            public string AssetReferenceKind { get; }
            public string AssetReference { get; }
            public IReadOnlyList<string> Tags { get; }
        }
    }

    public enum ItemRegistrationKind
    {
        CustomItem = 0,
        BaseItemClone = 1
    }

    public sealed class CustomItemDefinition
    {
        public CustomItemDefinition(string modId, int itemId, string itemKey, string displayName)
            : this(
                ItemRegistrationKind.CustomItem,
                modId,
                itemId,
                itemKey,
                displayName,
                "",
                0,
                false,
                false,
                "",
                "",
                Array.Empty<string>())
        {
        }

        public CustomItemDefinition(
            ItemRegistrationKind kind,
            string modId,
            int itemId,
            string itemKey,
            string displayName,
            string itemType,
            int baseItemId,
            bool canBeSpawned,
            bool canBeHotKeyed,
            string assetReferenceKind,
            string assetReference,
            IEnumerable<string> tags)
        {
            Kind = kind;
            ModId = modId ?? "";
            ItemId = Math.Max(0, itemId);
            ItemKey = itemKey ?? "";
            DisplayName = displayName ?? "";
            ItemType = itemType ?? "";
            BaseItemId = Math.Max(0, baseItemId);
            CanBeSpawned = canBeSpawned;
            CanBeHotKeyed = canBeHotKeyed;
            AssetReferenceKind = assetReferenceKind ?? "";
            AssetReference = assetReference ?? "";
            Tags = Array.AsReadOnly((tags ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        public ItemRegistrationKind Kind { get; }
        public string ModId { get; }
        public int ItemId { get; }
        public string ItemKey { get; }
        public string DisplayName { get; }
        public string ItemType { get; }
        public int BaseItemId { get; }
        public bool CanBeSpawned { get; }
        public bool CanBeHotKeyed { get; }
        public string AssetReferenceKind { get; }
        public string AssetReference { get; }
        public IReadOnlyList<string> Tags { get; }
    }

    public sealed class ItemRegistrationPlan
    {
        internal ItemRegistrationPlan(
            ItemRegistrationKind definitionKind,
            string definitionKindName,
            string modId,
            int itemId,
            string itemKey,
            string displayName,
            string itemType,
            int baseItemId,
            string baseItemDisplayName,
            bool canBeSpawned,
            bool canBeHotKeyed,
            string assetReferenceKind,
            string assetReference,
            IReadOnlyList<string> tags,
            bool accepted,
            bool canRegisterNow,
            bool runtimeItemRegistrationEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            DefinitionKind = definitionKind;
            DefinitionKindName = definitionKindName ?? "";
            ModId = modId ?? "";
            ItemId = Math.Max(0, itemId);
            ItemKey = itemKey ?? "";
            DisplayName = displayName ?? "";
            ItemType = itemType ?? "";
            BaseItemId = Math.Max(0, baseItemId);
            BaseItemDisplayName = baseItemDisplayName ?? "";
            CanBeSpawned = canBeSpawned;
            CanBeHotKeyed = canBeHotKeyed;
            AssetReferenceKind = assetReferenceKind ?? "none";
            AssetReference = assetReference ?? "";
            Tags = Array.AsReadOnly((tags ?? Array.Empty<string>()).ToArray());
            Accepted = accepted;
            CanRegisterNow = canRegisterNow;
            RuntimeItemRegistrationEnabled = runtimeItemRegistrationEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public ItemRegistrationKind DefinitionKind { get; }
        public string DefinitionKindName { get; }
        public string ModId { get; }
        public int ItemId { get; }
        public string ItemKey { get; }
        public string DisplayName { get; }
        public string ItemType { get; }
        public int BaseItemId { get; }
        public string BaseItemDisplayName { get; }
        public bool CanBeSpawned { get; }
        public bool CanBeHotKeyed { get; }
        public string AssetReferenceKind { get; }
        public string AssetReference { get; }
        public IReadOnlyList<string> Tags { get; }
        public bool Accepted { get; }
        public bool CanRegisterNow { get; }
        public bool RuntimeItemRegistrationEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }

    public sealed class CustomItemDefinitionSnapshot
    {
        internal CustomItemDefinitionSnapshot(
            string modId,
            int itemId,
            string itemKey,
            ItemRegistrationKind definitionKind,
            string definitionKindName,
            string displayName,
            string itemType,
            int baseItemId,
            string baseItemDisplayName,
            bool canBeSpawned,
            bool canBeHotKeyed,
            string assetReferenceKind,
            string assetReference,
            IReadOnlyList<string> tags,
            bool runtimeRegistered,
            string registeredUtc)
        {
            ModId = modId ?? "";
            ItemId = Math.Max(0, itemId);
            ItemKey = itemKey ?? "";
            DefinitionKind = definitionKind;
            DefinitionKindName = definitionKindName ?? "";
            DisplayName = displayName ?? "";
            ItemType = itemType ?? "";
            BaseItemId = Math.Max(0, baseItemId);
            BaseItemDisplayName = baseItemDisplayName ?? "";
            CanBeSpawned = canBeSpawned;
            CanBeHotKeyed = canBeHotKeyed;
            AssetReferenceKind = assetReferenceKind ?? "none";
            AssetReference = assetReference ?? "";
            Tags = Array.AsReadOnly((tags ?? Array.Empty<string>()).ToArray());
            RuntimeRegistered = runtimeRegistered;
            RegisteredUtc = registeredUtc ?? "";
        }

        public int Schema => 1;
        public string ModId { get; }
        public int ItemId { get; }
        public string ItemKey { get; }
        public ItemRegistrationKind DefinitionKind { get; }
        public string DefinitionKindName { get; }
        public string DisplayName { get; }
        public string ItemType { get; }
        public int BaseItemId { get; }
        public string BaseItemDisplayName { get; }
        public bool CanBeSpawned { get; }
        public bool CanBeHotKeyed { get; }
        public string AssetReferenceKind { get; }
        public string AssetReference { get; }
        public IReadOnlyList<string> Tags { get; }
        public bool RuntimeRegistered { get; }
        public string RegisteredUtc { get; }
        public bool CatalogCollision => !RuntimeRegistered && ItemCatalog.TryFindItem(ItemId, out _);
    }

    public sealed class ItemRegistrationSnapshot
    {
        internal ItemRegistrationSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            string sceneName,
            string sceneKind,
            bool isInWorld,
            bool itemCatalogReady,
            string itemCatalogStatus,
            int itemCatalogPublishedCount,
            bool planningEnabled,
            bool runtimeItemRegistrationEnabled,
            IReadOnlyList<CustomItemDefinitionSnapshot> registeredDefinitions,
            int catalogCollisionCount,
            IReadOnlyList<string> supportedDefinitionKinds,
            IReadOnlyList<string> supportedAssetReferenceKinds,
            int maxRegisteredItemDefinitionCount,
            int maxItemKeyLength,
            int maxDisplayNameLength,
            int planRequestCount,
            int acceptedPlanCount,
            int rejectedPlanCount,
            string lastPlanKind,
            string lastPlanStatus,
            string lastPlanDetail,
            string lastPlanReason,
            string lastPlanModId,
            string lastPlanItemKey,
            int lastPlanItemId,
            int lastPlanBaseItemId)
        {
            ServiceKey = serviceKey ?? ItemRegistration.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            SceneName = sceneName ?? "";
            SceneKind = sceneKind ?? "";
            IsInWorld = isInWorld;
            ItemCatalogReady = itemCatalogReady;
            ItemCatalogStatus = itemCatalogStatus ?? "";
            ItemCatalogPublishedCount = Math.Max(0, itemCatalogPublishedCount);
            PlanningEnabled = planningEnabled;
            RuntimeItemRegistrationEnabled = runtimeItemRegistrationEnabled;
            RegisteredDefinitions = Array.AsReadOnly((registeredDefinitions ?? Array.Empty<CustomItemDefinitionSnapshot>()).ToArray());
            RegisteredDefinitionCount = RegisteredDefinitions.Count;
            CatalogCollisionCount = Math.Max(0, catalogCollisionCount);
            SupportedDefinitionKinds = supportedDefinitionKinds ?? Array.Empty<string>();
            SupportedAssetReferenceKinds = supportedAssetReferenceKinds ?? Array.Empty<string>();
            MaxRegisteredItemDefinitionCount = Math.Max(0, maxRegisteredItemDefinitionCount);
            MaxItemKeyLength = Math.Max(0, maxItemKeyLength);
            MaxDisplayNameLength = Math.Max(0, maxDisplayNameLength);
            PlanRequestCount = Math.Max(0, planRequestCount);
            AcceptedPlanCount = Math.Max(0, acceptedPlanCount);
            RejectedPlanCount = Math.Max(0, rejectedPlanCount);
            LastPlanKind = lastPlanKind ?? "";
            LastPlanStatus = lastPlanStatus ?? "";
            LastPlanDetail = lastPlanDetail ?? "";
            LastPlanReason = lastPlanReason ?? "";
            LastPlanModId = lastPlanModId ?? "";
            LastPlanItemKey = lastPlanItemKey ?? "";
            LastPlanItemId = Math.Max(0, lastPlanItemId);
            LastPlanBaseItemId = Math.Max(0, lastPlanBaseItemId);
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public string SceneName { get; }
        public string SceneKind { get; }
        public bool IsInWorld { get; }
        public bool ItemCatalogReady { get; }
        public string ItemCatalogStatus { get; }
        public int ItemCatalogPublishedCount { get; }
        public bool PlanningEnabled { get; }
        public bool RuntimeItemRegistrationEnabled { get; }
        public IReadOnlyList<CustomItemDefinitionSnapshot> RegisteredDefinitions { get; }
        public int RegisteredDefinitionCount { get; }
        public int CatalogCollisionCount { get; }
        public IReadOnlyList<string> SupportedDefinitionKinds { get; }
        public IReadOnlyList<string> SupportedAssetReferenceKinds { get; }
        public int MaxRegisteredItemDefinitionCount { get; }
        public int MaxItemKeyLength { get; }
        public int MaxDisplayNameLength { get; }
        public int PlanRequestCount { get; }
        public int AcceptedPlanCount { get; }
        public int RejectedPlanCount { get; }
        public string LastPlanKind { get; }
        public string LastPlanStatus { get; }
        public string LastPlanDetail { get; }
        public string LastPlanReason { get; }
        public string LastPlanModId { get; }
        public string LastPlanItemKey { get; }
        public int LastPlanItemId { get; }
        public int LastPlanBaseItemId { get; }

        internal static ItemRegistrationSnapshot CreateUnknown()
        {
            return new ItemRegistrationSnapshot(
                ItemRegistration.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Sons item registration planning has not refreshed yet.",
                "",
                "",
                false,
                false,
                "PENDING",
                0,
                true,
                ItemRegistration.RuntimeItemRegistrationEnabled,
                Array.Empty<CustomItemDefinitionSnapshot>(),
                0,
                ItemRegistration.SupportedDefinitionKinds,
                ItemRegistration.SupportedAssetReferenceKinds,
                ItemRegistration.MaxRegisteredItemDefinitionCount,
                ItemRegistration.MaxItemKeyLength,
                ItemRegistration.MaxDisplayNameLength,
                0,
                0,
                0,
                "none",
                "INFO",
                "No item registration plan has been validated.",
                "none",
                "",
                "",
                0,
                0);
        }
    }
}
