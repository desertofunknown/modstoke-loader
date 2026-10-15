// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons item registration planning surface. This lets mods
// declare custom item intent and validates it against the OpenACAI item catalog
// without mutating the Sons item database until the runtime path is verified.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsItemRegistration
    {
        public const string ServiceKey = "openacai.sons.itemRegistration";
        public const int MaxRegisteredItemDefinitionCount = 512;
        public const int MaxItemKeyLength = 96;
        public const int MaxDisplayNameLength = 128;
        public const int MaxItemTypeLength = 96;
        public const int MaxTagsPerItem = 16;
        public const int MaxTagLength = 48;
        public const int MaxAssetReferenceLength = 192;

        private static readonly bool RuntimeItemRegistrationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAISonsCustomItemDefinitionSnapshot> RegisteredDefinitions =
            new Dictionary<string, OpenACAISonsCustomItemDefinitionSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] SupportedDefinitionKindNames =
        {
            "custom-item",
            "base-item-clone"
        };

        private static readonly string[] SupportedAssetReferenceKindNames =
        {
            "none",
            "asset-bundle",
            "prefab-path",
            "addressable-key"
        };

        private static OpenACAISonsItemRegistrationSnapshot _state = OpenACAISonsItemRegistrationSnapshot.CreateUnknown();
        private static bool _initialized;
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

        public static OpenACAISonsItemRegistrationSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => true;
        public static bool RuntimeItemRegistrationEnabled => RuntimeItemRegistrationEnabledValue;
        public static IReadOnlyList<string> SupportedDefinitionKinds => SupportedDefinitionKindNames;
        public static IReadOnlyList<string> SupportedAssetReferenceKinds => SupportedAssetReferenceKindNames;

        public static IReadOnlyList<OpenACAISonsCustomItemDefinitionSnapshot> GetRegisteredItemDefinitions()
        {
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
            out OpenACAISonsCustomItemDefinitionSnapshot definition)
        {
            return TryRegisterItemDefinition(
                new OpenACAISonsCustomItemDefinition(modId, itemId, itemKey, displayName),
                out definition);
        }

        public static bool TryRegisterItemDefinition(
            string modId,
            int itemId,
            string itemKey,
            string displayName,
            string itemType,
            int baseItemId,
            out OpenACAISonsCustomItemDefinitionSnapshot definition)
        {
            return TryRegisterItemDefinition(
                new OpenACAISonsCustomItemDefinition(
                    baseItemId > 0
                        ? OpenACAISonsItemRegistrationKind.BaseItemClone
                        : OpenACAISonsItemRegistrationKind.CustomItem,
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
            OpenACAISonsCustomItemDefinition definition,
            out OpenACAISonsCustomItemDefinitionSnapshot snapshot)
        {
            snapshot = null;
            var plan = PlanItemRegistration(definition);
            if (plan == null || !plan.Accepted)
            {
                return false;
            }

            var created = new OpenACAISonsCustomItemDefinitionSnapshot(
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
            OpenACAISonsCustomItemDefinitionSnapshot definition;
            if (!TryFindRegisteredDefinition(modId, itemId, out definition))
            {
                return false;
            }

            return TryUnregisterItemDefinition(definition.ModId, definition.ItemKey);
        }

        public static bool TryUnregisterItemDefinition(string modId, string itemKey)
        {
            var normalizedModId = OpenACAISonsSaveData.NormalizeModId(modId);
            var normalizedItemKey = NormalizeKey(itemKey);
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(normalizedItemKey))
            {
                return false;
            }

            bool removed;
            lock (SyncRoot)
            {
                removed = RegisteredDefinitions.Remove(DefinitionKey(normalizedModId, normalizedItemKey));
            }

            if (removed)
            {
                Refresh();
            }

            return removed;
        }

        public static OpenACAISonsItemRegistrationPlan PlanCustomItemRegistration(
            string modId,
            int itemId,
            string itemKey,
            string displayName)
        {
            return PlanItemRegistration(new OpenACAISonsCustomItemDefinition(modId, itemId, itemKey, displayName));
        }

        public static OpenACAISonsItemRegistrationPlan PlanBaseItemClone(
            string modId,
            int itemId,
            string itemKey,
            string displayName,
            int baseItemId)
        {
            return PlanItemRegistration(new OpenACAISonsCustomItemDefinition(
                OpenACAISonsItemRegistrationKind.BaseItemClone,
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

        public static OpenACAISonsItemRegistrationPlan PlanItemRegistration(OpenACAISonsCustomItemDefinition definition)
        {
            NormalizedDefinition normalized;
            string reason;
            string detail;
            var accepted = false;
            var canRegisterNow = false;
            var status = "CHECK";
            var baseItemDisplayName = "";
            var itemCatalog = OpenACAISonsItems.Catalog;

            if (!TryNormalizeDefinition(definition, out normalized, out reason, out detail))
            {
                status = "CHECK";
            }
            else if (itemCatalog == null || !itemCatalog.Ready)
            {
                reason = "item-catalog-not-ready";
                detail = itemCatalog == null
                    ? "OpenACAI item catalog has not been published yet."
                    : itemCatalog.Detail;
                status = itemCatalog == null ? "PENDING" : itemCatalog.Status;
            }
            else if (OpenACAISonsItems.TryFindItem(normalized.ItemId, out _))
            {
                reason = "vanilla-item-id-collision";
                detail = "Sons item id " + normalized.ItemId + " already exists in the item database.";
                status = "CHECK";
            }
            else if (normalized.BaseItemId > 0 && !OpenACAISonsItems.TryFindItem(normalized.BaseItemId, out var baseItem))
            {
                reason = "unknown-base-item";
                detail = "Base item id " + normalized.BaseItemId + " was not found in the OpenACAI item catalog.";
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
                detail = "OpenACAI item id " + normalized.ItemId + " is already registered by " + existingByItemId.ModId + ".";
                status = "CHECK";
            }
            else
            {
                OpenACAISonsItemSnapshot resolvedBaseItem = null;
                if (normalized.BaseItemId > 0)
                {
                    OpenACAISonsItems.TryFindItem(normalized.BaseItemId, out resolvedBaseItem);
                    baseItemDisplayName = resolvedBaseItem == null ? "" : resolvedBaseItem.DisplayName;
                }

                accepted = true;
                if (!RuntimeItemRegistrationEnabled)
                {
                    reason = "runtime-registration-disabled";
                    detail = "OpenACAI accepted the item definition; runtime item database mutation remains disabled until verified.";
                    status = "DEFERRED";
                }
                else
                {
                    canRegisterNow = true;
                    reason = "ready";
                    detail = "OpenACAI item registration gate accepted the definition for runtime registration.";
                    status = "READY";
                }
            }

            var plan = new OpenACAISonsItemRegistrationPlan(
                normalized == null ? OpenACAISonsItemRegistrationKind.CustomItem : normalized.Kind,
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

        public static OpenACAISonsItemRegistrationSnapshot Refresh()
        {
            var snapshot = CreateSnapshot();
            Publish(snapshot);
            return snapshot;
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            OpenACAISons.SceneChanged += _ => Refresh();
            OpenACAISons.WorldChanged += _ => Refresh();
            OpenACAISons.Lifecycle += _ => Refresh();
            Publish(OpenACAISonsItemRegistrationSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK item registration provider could not be registered.");
            }
        }

        private static void Publish(OpenACAISonsItemRegistrationSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAISonsItemRegistrationSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(OpenACAISonsItemRegistrationPlan plan)
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

        private static OpenACAISonsItemRegistrationSnapshot CreateSnapshot()
        {
            var sonsState = OpenACAISons.State;
            var itemCatalog = OpenACAISonsItems.Catalog;
            IReadOnlyList<OpenACAISonsCustomItemDefinitionSnapshot> definitions;
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
            return new OpenACAISonsItemRegistrationSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                itemCatalogReady,
                itemCatalog == null ? "PENDING" : itemCatalogReady ? "READY" : itemCatalog.Status,
                itemCatalog == null
                    ? "Sons item catalog has not refreshed yet."
                    : itemCatalogReady
                        ? "OpenACAI item definition validation is available; runtime registration is gated until verified."
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

        private static int CountCatalogCollisions(IReadOnlyList<OpenACAISonsCustomItemDefinitionSnapshot> definitions)
        {
            if (definitions == null || definitions.Count == 0 || OpenACAISonsItems.Catalog == null || !OpenACAISonsItems.Catalog.Ready)
            {
                return 0;
            }

            return definitions.Count(definition => OpenACAISonsItems.TryFindItem(definition.ItemId, out _));
        }

        private static bool TryNormalizeDefinition(
            OpenACAISonsCustomItemDefinition definition,
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
                detail = "An OpenACAI item definition is required.";
                return false;
            }

            var modId = OpenACAISonsSaveData.NormalizeModId(definition.ModId);
            if (string.IsNullOrWhiteSpace(definition.ModId))
            {
                reason = "missing-mod-id";
                detail = "A mod id is required for OpenACAI item ownership.";
                return false;
            }

            if (!IsSupportedDefinitionKind(definition.Kind))
            {
                reason = "unsupported-definition-kind";
                detail = "Unsupported OpenACAI item definition kind.";
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
            if (definition.Kind == OpenACAISonsItemRegistrationKind.BaseItemClone && baseItemId <= 0)
            {
                reason = "missing-base-item";
                detail = "Base item clone definitions require a positive base item id.";
                return false;
            }

            var assetReferenceKind = NormalizeAssetReferenceKind(definition.AssetReferenceKind);
            if (!SupportedAssetReferenceKindNames.Any(value => string.Equals(value, assetReferenceKind, StringComparison.OrdinalIgnoreCase)))
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

        private static bool TryFindRegisteredDefinition(string modId, int itemId, out OpenACAISonsCustomItemDefinitionSnapshot definition)
        {
            definition = null;
            var normalizedModId = OpenACAISonsSaveData.NormalizeModId(modId);
            lock (SyncRoot)
            {
                definition = RegisteredDefinitions.Values.FirstOrDefault(candidate =>
                    candidate.ItemId == itemId
                    && string.Equals(candidate.ModId, normalizedModId, StringComparison.OrdinalIgnoreCase));
            }

            return definition != null;
        }

        private static bool TryFindRegisteredDefinition(string modId, string itemKey, out OpenACAISonsCustomItemDefinitionSnapshot definition)
        {
            definition = null;
            var normalizedModId = OpenACAISonsSaveData.NormalizeModId(modId);
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

        private static bool TryFindRegisteredDefinition(int itemId, out OpenACAISonsCustomItemDefinitionSnapshot definition)
        {
            lock (SyncRoot)
            {
                definition = RegisteredDefinitions.Values.FirstOrDefault(candidate => candidate.ItemId == itemId);
            }

            return definition != null;
        }

        private static string DefinitionKey(string modId, string itemKey)
        {
            return OpenACAISonsSaveData.NormalizeModId(modId).ToLowerInvariant() + ":" + NormalizeKey(itemKey);
        }

        private static bool IsSupportedDefinitionKind(OpenACAISonsItemRegistrationKind kind)
        {
            return kind == OpenACAISonsItemRegistrationKind.CustomItem
                || kind == OpenACAISonsItemRegistrationKind.BaseItemClone;
        }

        private static string DefinitionKindName(OpenACAISonsItemRegistrationKind kind)
        {
            switch (kind)
            {
                case OpenACAISonsItemRegistrationKind.BaseItemClone:
                    return "base-item-clone";
                case OpenACAISonsItemRegistrationKind.CustomItem:
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

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Sons Item Registration",
                "OpenACAI-owned custom item definition planning and runtime-registration gate.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateDefinitionsSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAISonsItemRegistrationSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Registration Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned item registration planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can validate custom item definitions without RedLoader/SonsSdk item globals."),
                    Row("Runtime Registration", snapshot.RuntimeItemRegistrationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeItemRegistrationEnabled ? "READY" : "INFO", "Item database mutation remains gated until the Sons runtime registration path is verified."),
                    Row("Item Catalog", snapshot.ItemCatalogPublishedCount + " items", snapshot.ItemCatalogReady ? "READY" : snapshot.ItemCatalogStatus, "Custom item ids and base item ids are validated against Modstoke SDK Items."),
                    Row("Supported", string.Join(", ", snapshot.SupportedDefinitionKinds), "INFO", "Current OpenACAI-owned item definition kinds."),
                    Row("Asset References", string.Join(", ", snapshot.SupportedAssetReferenceKinds), "INFO", "Asset references are declarations only until item prefab wiring is verified.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateDefinitionsSection(OpenACAISonsItemRegistrationSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Registered", snapshot.RegisteredDefinitionCount + "/" + snapshot.MaxRegisteredItemDefinitionCount, "INFO", "OpenACAI-owned custom item definitions tracked by the loader."),
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

            return new OpenACAIMenuSectionSnapshot("Definitions", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAISonsItemRegistrationSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
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

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsItemRegistration;
            public string DisplayName => "Sons Item Registration";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 723;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned custom item definition planning and runtime-registration gate.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsItemRegistration.CreateContent();
            }
        }

        private sealed class NormalizedDefinition
        {
            public NormalizedDefinition(
                OpenACAISonsItemRegistrationKind kind,
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

            public OpenACAISonsItemRegistrationKind Kind { get; }
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

    public enum OpenACAISonsItemRegistrationKind
    {
        CustomItem = 0,
        BaseItemClone = 1
    }

    public sealed class OpenACAISonsCustomItemDefinition
    {
        public OpenACAISonsCustomItemDefinition(string modId, int itemId, string itemKey, string displayName)
            : this(
                OpenACAISonsItemRegistrationKind.CustomItem,
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

        public OpenACAISonsCustomItemDefinition(
            OpenACAISonsItemRegistrationKind kind,
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
            Tags = (tags ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        }

        public OpenACAISonsItemRegistrationKind Kind { get; }
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

    public sealed class OpenACAISonsItemRegistrationPlan
    {
        internal OpenACAISonsItemRegistrationPlan(
            OpenACAISonsItemRegistrationKind definitionKind,
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
            Tags = tags ?? Array.Empty<string>();
            Accepted = accepted;
            CanRegisterNow = canRegisterNow;
            RuntimeItemRegistrationEnabled = runtimeItemRegistrationEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public OpenACAISonsItemRegistrationKind DefinitionKind { get; }
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

    public sealed class OpenACAISonsCustomItemDefinitionSnapshot
    {
        internal OpenACAISonsCustomItemDefinitionSnapshot(
            string modId,
            int itemId,
            string itemKey,
            OpenACAISonsItemRegistrationKind definitionKind,
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
            Tags = tags ?? Array.Empty<string>();
            RuntimeRegistered = runtimeRegistered;
            RegisteredUtc = registeredUtc ?? "";
        }

        public int Schema => 1;
        public string ModId { get; }
        public int ItemId { get; }
        public string ItemKey { get; }
        public OpenACAISonsItemRegistrationKind DefinitionKind { get; }
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
        public bool CatalogCollision => OpenACAISonsItems.TryFindItem(ItemId, out _);
    }

    public sealed class OpenACAISonsItemRegistrationSnapshot
    {
        internal OpenACAISonsItemRegistrationSnapshot(
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
            IReadOnlyList<OpenACAISonsCustomItemDefinitionSnapshot> registeredDefinitions,
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
            ServiceKey = serviceKey ?? OpenACAISonsItemRegistration.ServiceKey;
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
            RegisteredDefinitions = registeredDefinitions ?? Array.Empty<OpenACAISonsCustomItemDefinitionSnapshot>();
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
        public IReadOnlyList<OpenACAISonsCustomItemDefinitionSnapshot> RegisteredDefinitions { get; }
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

        internal static OpenACAISonsItemRegistrationSnapshot CreateUnknown()
        {
            return new OpenACAISonsItemRegistrationSnapshot(
                OpenACAISonsItemRegistration.ServiceKey,
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
                OpenACAISonsItemRegistration.RuntimeItemRegistrationEnabled,
                Array.Empty<OpenACAISonsCustomItemDefinitionSnapshot>(),
                0,
                OpenACAISonsItemRegistration.SupportedDefinitionKinds,
                OpenACAISonsItemRegistration.SupportedAssetReferenceKinds,
                OpenACAISonsItemRegistration.MaxRegisteredItemDefinitionCount,
                OpenACAISonsItemRegistration.MaxItemKeyLength,
                OpenACAISonsItemRegistration.MaxDisplayNameLength,
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
