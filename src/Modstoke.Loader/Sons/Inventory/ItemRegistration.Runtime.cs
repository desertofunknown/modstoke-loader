// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using global::Sons.Gameplay;
using global::Sons.Items.Core;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using NativeItems = Il2CppSystem.Collections.Generic.List<global::Sons.Items.Core.ItemData>;
using NativeItemCache = Il2CppSystem.Collections.Generic.Dictionary<int, global::Sons.Items.Core.ItemData>;
using Object = UnityEngine.Object;
using Types = global::Sons.Items.Core.Types;

namespace Modstoke.Sons
{
    public static partial class ItemRegistration
    {
        private static readonly Dictionary<string, RuntimeRegistration> RuntimeRegistrations =
            new Dictionary<string, RuntimeRegistration>(StringComparer.OrdinalIgnoreCase);
        private static int _runtimeOwnerThreadId;
        private static bool _runtimeBusy;

        /// <summary>
        /// Registers a supported simple clone from BaseItemId. CustomItem declarations may
        /// also specify a base. No default donor, generated id, or asset-path resolution is used.
        /// Clones retain the donor's referenced assets. The loader owns the cloned ItemData,
        /// UI data and inactive pickup template for the process lifetime. Display translations
        /// are added only to the current Items locale; additional locales are mod-authored.
        /// </summary>
        public static ItemRegistrationResult ExecuteItemRegistration(CustomItemDefinition definition)
            => ExecuteRegistration(definition, null, true);

        /// <summary>
        /// Registers caller-authored ItemData in this process's native list and id cache.
        /// The caller supplies matching item/UI/pickup ids and retains its asset bundles.
        /// This does not grant items, author player layouts, register Bolt prefabs, replicate
        /// definitions, or authorize network use. Native registrations cannot be hot-unregistered.
        /// </summary>
        public static ItemRegistrationResult ExecuteItemRegistration(
            CustomItemDefinition definition, ItemData nativeItem)
            => ExecuteRegistration(definition, nativeItem, false);

        /// <summary>Fresh readiness on the initialized Unity thread, not cached planning readiness.</summary>
        public static bool RuntimeRegistrationReady => !_runtimeBusy && TryGetRuntimeDatabase(out _, out _, out _, out _);

        internal static bool IsPublishedRuntimeItem(ItemData item)
        {
            if (_runtimeOwnerThreadId == 0 || System.Environment.CurrentManagedThreadId != _runtimeOwnerThreadId
                || _runtimeBusy || !Live(item)) return false;
            lock (SyncRoot)
            {
                foreach (var registration in RuntimeRegistrations.Values)
                    if (registration.Published && Same(registration.Item, item)
                        && IsCurrent(registration, item._id)) return true;
            }
            return false;
        }

        private static ItemRegistrationResult ExecuteRegistration(
            CustomItemDefinition definition, ItemData nativeItem, bool cloneBase, int nativeModuleMask = 0)
        {
            if (_runtimeOwnerThreadId == 0 || Environment.CurrentManagedThreadId != _runtimeOwnerThreadId)
                return Result(false, false, false, "wrong-thread", "Registration requires the initialized Unity lifecycle thread.");
            if (!TryNormalizeDefinition(definition, out var normalized, out var reason, out var detail))
                return Result(false, false, false, reason, detail);

            lock (SyncRoot)
            {
                if (_runtimeBusy)
                    return Result(false, false, false, "registration-busy", "A native registration is already executing.");
                _runtimeBusy = true;
                try
                {
                    return ExecuteRegistrationCore(normalized, nativeItem, cloneBase, nativeModuleMask);
                }
                finally
                {
                    _runtimeBusy = false;
                }
            }
        }

        private static ItemRegistrationResult ExecuteRegistrationCore(
            NormalizedDefinition definition, ItemData nativeItem, bool cloneBase, int nativeModuleMask)
        {
            RuntimeRegistration registration = null;
            var attempted = false;
            var key = DefinitionKey(definition.ModId, definition.ItemKey);
            try
            {
                if (!TryGetRuntimeDatabase(out var database, out var items, out var cache, out var detail))
                    return Result(false, false, false, "database-not-ready", detail);
                if (RuntimeRegistrations.ContainsKey(key))
                    return Result(false, false, false, "runtime-registration-exists", "This identity has already executed. Native registrations are not replaced or hot-unregistered.");
                if (RegisteredDefinitions.TryGetValue(key, out var prior) && prior.ItemId != definition.ItemId)
                    return Result(false, false, false, "item-key-collision", "This declaration key already belongs to another item id.");
                if (RegisteredDefinitions.Values.Any(value => value.ItemId == definition.ItemId
                    && DefinitionKey(value.ModId, value.ItemKey) != key))
                    return Result(false, false, false, "item-id-collision", "This item id is reserved by another declaration.");
                if (RegisteredDefinitions.Count >= MaxRegisteredItemDefinitionCount && prior == null)
                    return Result(false, false, false, "definition-limit-reached", "The item declaration limit has been reached.");
                if (cache.ContainsKey(definition.ItemId) || FindItem(items, definition.ItemId) != null)
                    return Result(false, false, false, "native-item-id-collision", "The requested id already exists in the native cache or database list.");

                ItemData donor = null;
                if (definition.BaseItemId > 0 && (!cache.TryGetValue(definition.BaseItemId, out donor) || !Live(donor)))
                    return Result(false, false, false, "unknown-base-item", "The base id is not present in the live database.");
                if (cloneBase && !CanClone(definition, donor, out var reason, out detail))
                    return Result(false, false, false, reason, detail);

                if (nativeModuleMask != 0 && (cloneBase || !Live(nativeItem) || !nativeItem._eachInstanceIsUnique))
                    return Result(false, false, false, "unique-authored-item-required",
                        "Module contracts require caller-authored ItemData already configured for native unique-instance storage. No existing definition is converted.");
                // Last check before anything is allocated, so a refused registration never claims an id.
                if (!ItemIds.TryClaimForRegistration(definition.ModId, definition.ItemId, out reason, out detail))
                    return Result(false, false, false, reason, detail);

                registration = new RuntimeRegistration
                {
                    Database = database, Items = items, Cache = cache, Owned = cloneBase,
                    NativeModuleMask = nativeModuleMask, NativeModuleItemId = definition.ItemId
                };
                if (cloneBase)
                    CreateBaseClone(definition, donor, registration);
                else
                    registration.Item = nativeItem;

                if (!ValidateNativeItem(definition, registration.Item, out detail))
                {
                    ReleaseUnregistered(registration);
                    return Result(false, false, false, "invalid-native-item", detail);
                }

                // Recheck after allocation/localization; only exact owned entries are ever rolled back.
                if (!Same(database, ItemDatabaseManager._instance) || ItemDatabaseManager._itemsCache == null
                    || cache.Pointer != ItemDatabaseManager._itemsCache.Pointer
                    || database._itemDataList == null || items.Pointer != database._itemDataList.Pointer)
                    throw new InvalidOperationException("The native database lifetime changed before publication.");
                if (cache.ContainsKey(definition.ItemId) || FindItem(items, definition.ItemId) != null)
                    throw new InvalidOperationException("The requested id became occupied before publication.");

                RuntimeRegistrations.Add(key, registration);
                if (nativeModuleMask != 0) NativeModuleContracts.Add(definition.ItemId, registration);
                attempted = true;
                registration.NativeModuleItemIndex = items.Count;
                items.Add(registration.Item);
                cache.Add(definition.ItemId, registration.Item);
                if (!IsCurrent(registration, definition.ItemId))
                    throw new InvalidOperationException("Native list/cache readback did not retain the registered item.");

                var snapshot = DefinitionSnapshot(definition, donor, true);
                RegisteredDefinitions[key] = snapshot;
                registration.Published = true;
                // Publication is already complete. A managed catalog refresh must not roll it back.
                var refreshDetail = "";
                try
                {
                    var catalog = ItemCatalog.Refresh();
                    Refresh();
                    if (!catalog.Ready) refreshDetail = " Catalog refresh is pending: " + catalog.Detail;
                }
                catch (Exception ex)
                {
                    refreshDetail = " Snapshot refresh failed after publication: " + ex.GetType().Name + ": " + ex.Message;
                }
                return new ItemRegistrationResult(true, true, false, "registered",
                    "Registered in the local native item database. Inventory/held layouts and multiplayer content are separate."
                    + refreshDetail, snapshot);
            }
            catch (Exception ex)
            {
                var rollbackComplete = registration == null || RollBack(registration, definition.ItemId, attempted);
                if (rollbackComplete)
                {
                    RuntimeRegistrations.Remove(key);
                    if (NativeModuleContracts.TryGetValue(definition.ItemId, out var contract)
                        && ReferenceEquals(contract, registration)) NativeModuleContracts.Remove(definition.ItemId);
                    return Result(false, attempted, false, "registration-failed", ex.GetType().Name + ": " + ex.Message);
                }
                // Retain strong references and identity reservations if native cleanup is uncertain.
                RuntimeRegistrations[key] = registration;
                RegisteredDefinitions[key] = DefinitionSnapshot(definition, null, false);
                return Result(false, attempted, true, "rollback-incomplete",
                    ex.GetType().Name + ": " + ex.Message + " Native cleanup is unconfirmed; this identity remains reserved until restart.");
            }
        }

        private static bool TryGetRuntimeDatabase(out ItemDatabase database, out NativeItems items,
            out NativeItemCache cache, out string detail)
        {
            database = null;
            items = null;
            cache = null;
            detail = "Registration requires the initialized Unity lifecycle thread.";
            if (_runtimeOwnerThreadId == 0 || Environment.CurrentManagedThreadId != _runtimeOwnerThreadId)
                return false;
            try
            {
                database = ItemDatabaseManager._instance;
                cache = ItemDatabaseManager._itemsCache;
                items = Live(database) ? database._itemDataList : null;
                detail = "The native item database, list and id cache must already be initialized.";
                return Live(database) && items != null && cache != null && items.Count > 0;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool CanExecuteBaseClone(NormalizedDefinition definition, out string reason, out string detail)
        {
            reason = "native-item-data-required";
            detail = "Execute with caller-authored native ItemData, or specify a supported simple base item with no asset-reference override.";
            if (definition.BaseItemId <= 0)
                return false;
            if (!TryGetRuntimeDatabase(out _, out _, out var cache, out detail))
            {
                reason = "database-not-ready";
                return false;
            }
            try
            {
                cache.TryGetValue(definition.BaseItemId, out var donor);
                return CanClone(definition, donor, out reason, out detail);
            }
            catch (Exception ex)
            {
                reason = "base-item-unavailable";
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool CanClone(NormalizedDefinition definition, ItemData donor, out string reason, out string detail)
        {
            reason = "unsupported-base-clone";
            detail = "Automatic clones support simple non-equipped, non-bundled items only. Supply authored ItemData for other item families.";
            if (!Live(donor))
            {
                reason = "native-item-data-required";
                detail = "A live explicit base item or caller-authored native ItemData is required.";
                return false;
            }
            if (definition.AssetReferenceKind != "none" || definition.AssetReference.Length != 0)
            {
                reason = "asset-resolution-required";
                detail = "Asset references are declarations. Load and prepare those assets before using the native ItemData overload.";
                return false;
            }
            const Types simpleTypes = Types.CraftingMaterial | Types.Craftable | Types.Edible | Types.Droppable;
            if ((donor._type & ~simpleTypes) != 0
                || Live(donor._heldPrefab) || Live(donor._propPrefab) || Live(donor._pickupBundlePrefab)
                || donor._pickupCanBeBundled || (donor._type & Types.Equippable) != 0
                || donor._eachInstanceIsUnique || donor._isStructureElement || donor._isPerishable
                || donor._isVolumeContainer || donor._isGPSLocator || donor._isDismemberedLimb
                || donor._canBePlated || donor._isUpgradeableArrow || definition.CanBeHotKeyed)
                return false;
            if (definition.ItemType.Length != 0 && !string.Equals(definition.ItemType, donor._type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                reason = "base-type-mismatch";
                detail = "Automatic cloning preserves the donor's native item type.";
                return false;
            }
            if (donor._uiData == null || (definition.CanBeSpawned && !Live(donor._pickupPrefab)))
                return false;
            if (!IsPrefabSource(donor._pickupPrefab))
                return false;
            if (Live(donor._pickupPrefab))
            {
                if (donor._pickupPrefab.GetComponentInChildren<BoltEntity>(true))
                {
                    reason = "network-prefab-authoring-required";
                    detail = "Automatic cloning does not duplicate Bolt prefab identities. Supply an authored custom item and network prefab registration separately.";
                    return false;
                }
                var pickups = donor._pickupPrefab.GetComponentsInChildren<PickUp>(true);
                if (pickups.Length != 1 || !Same(pickups[0].transform, donor._pickupPrefab)
                    || pickups[0]._itemId != donor._id || pickups[0].ItemInstance != null)
                    return false;
                foreach (var tag in donor._pickupPrefab.GetComponentsInChildren<ItemLocatorTag>(true))
                    if (tag._itemId != donor._id) return false;
            }
            var table = LocalizationSettings.StringDatabase.GetTable("Items");
            if (!Live(table))
            {
                reason = "localization-not-ready";
                detail = "The active locale's Items table must be loaded before cloning a display name.";
                return false;
            }
            var translationKey = TranslationKey(definition.ItemId);
            if (table.GetEntry(translationKey) != null || table.GetEntry(translationKey + "_PLURAL") != null
                || table.GetEntry(translationKey + "_DESC") != null)
            {
                reason = "translation-key-collision";
                detail = "The clone's localization keys already exist; no existing translations are replaced.";
                return false;
            }
            reason = "ready";
            detail = "Explicit execution can clone this simple base item into the local native database; player layouts and network registration remain separate.";
            return true;
        }

        private static bool ValidateNativeItem(NormalizedDefinition definition, ItemData item, out string detail)
        {
            detail = "Native ItemData must be alive and match the declaration id, UI id, display title, type and capability flags.";
            if (!Live(item) || item._id != definition.ItemId || item._uiData == null || item._uiData._itemId != definition.ItemId
                || item._uiData._title != definition.DisplayName || item._canBeSpawned != definition.CanBeSpawned
                || item._canBeHotKeyed != definition.CanBeHotKeyed
                || (definition.ItemType.Length != 0 && !string.Equals(item._type.ToString(), definition.ItemType, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (definition.CanBeSpawned && !Live(item._pickupPrefab))
            {
                detail = "Spawnable items require a live pickup prefab.";
                return false;
            }
            if (!IsPrefabSource(item._pickupPrefab) || !IsPrefabSource(item._heldPrefab)
                || !IsPrefabSource(item._propPrefab) || !IsPrefabSource(item._pickupBundlePrefab))
            {
                detail = "Prefab references must be assets or inactive DontDestroyOnLoad templates, not scene-owned instances.";
                return false;
            }
            if (Live(item._pickupPrefab))
            {
                var pickup = item._pickupPrefab.GetComponent<PickUp>();
                if (!Live(pickup) || pickup._itemId != definition.ItemId
                    || (Live(pickup._itemDataCached) && !Same(pickup._itemDataCached, item))
                    || pickup.ItemInstance != null || item._pickupPrefab.GetComponentsInChildren<PickUp>(true).Length != 1)
                {
                    detail = "The pickup root must have a matching PickUp id, no foreign cached ItemData, and no live ItemInstance.";
                    return false;
                }
                foreach (var tag in item._pickupPrefab.GetComponentsInChildren<ItemLocatorTag>(true))
                    if (tag._itemId != definition.ItemId)
                    {
                        detail = "Pickup locator tags must refer to the new item id.";
                        return false;
                    }
            }
            detail = "Native item is ready for local database publication.";
            return true;
        }

        private static bool IsPrefabSource(Transform prefab)
        {
            if (prefab is null) return true;
            if (!Live(prefab)) return false;
            var gameObject = prefab.gameObject;
            var scene = gameObject.scene;
            return !scene.IsValid() || (scene.name == "DontDestroyOnLoad" && !gameObject.activeInHierarchy);
        }

        private static void CreateBaseClone(NormalizedDefinition definition, ItemData donor, RuntimeRegistration registration)
        {
            var item = registration.Item = Object.Instantiate(donor);
            item.name = definition.ModId + ":" + definition.ItemKey;
            item._id = definition.ItemId;
            item._name = item._editorName = definition.DisplayName;
            item._canBeSpawned = definition.CanBeSpawned;
            item._canBeHotKeyed = false;
            item._tags = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var tag in definition.Tags) item._tags.Add(tag);
            item._aliases = new Il2CppSystem.Collections.Generic.List<string>();
            item._uiData = CloneUiData(donor._uiData, definition);
            item.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            if (Live(donor._pickupPrefab))
            {
                // Instantiate below an inactive root so pickup Awake/OnEnable cannot run
                // with the donor id before the new identity is bound.
                var root = registration.PrefabRoot = new GameObject(item.name + " prefabs");
                root.SetActive(false);
                Object.DontDestroyOnLoad(root);
                var prefab = Object.Instantiate(donor._pickupPrefab.gameObject, root.transform, false);
                prefab.SetActive(true);
                var pickup = prefab.GetComponent<PickUp>();
                pickup._itemId = definition.ItemId;
                pickup._itemDataCached = item;
                pickup.ItemInstance = null;
                foreach (var tag in prefab.GetComponentsInChildren<ItemLocatorTag>(true))
                {
                    tag._itemId = definition.ItemId;
                    tag._name = definition.DisplayName;
                }
                item._pickupPrefab = prefab.transform;
            }
            registration.Table = LocalizationSettings.StringDatabase.GetTable("Items");
            var key = TranslationKey(definition.ItemId);
            AddTranslation(registration, key, definition.DisplayName);
            AddTranslation(registration, key + "_PLURAL", definition.DisplayName);
            AddTranslation(registration, key + "_DESC", item._uiData._description ?? "");
        }

        private static void AddTranslation(RuntimeRegistration registration, string key, string value)
        {
            if (!Live(registration.Table) || registration.Table.GetEntry(key) != null)
                throw new InvalidOperationException("The localization table changed or the requested key is occupied.");
            var entry = registration.Table.AddEntry(key, value);
            if (entry == null) throw new InvalidOperationException("The localization entry was not created.");
            registration.Translations.Add(key, entry.Pointer);
        }

        private static string TranslationKey(int id) => "MODSTOKE_CUSTOM_ITEM_" + id;

        private static ItemData FindItem(NativeItems items, int id)
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (Live(item) && item._id == id) return item;
            }
            return null;
        }

        private static bool IsCurrent(RuntimeRegistration registration, int id)
        {
            if (!Live(registration.Item) || !Same(registration.Database, ItemDatabaseManager._instance)) return false;
            var cache = ItemDatabaseManager._itemsCache;
            var items = registration.Database._itemDataList;
            if (cache == null || items == null || cache.Pointer != registration.Cache.Pointer || items.Pointer != registration.Items.Pointer
                || !cache.TryGetValue(id, out var cached) || !Same(cached, registration.Item)) return false;
            var matches = 0;
            for (var index = 0; index < items.Count; index++)
                if (Live(items[index]) && items[index]._id == id)
                {
                    if (!Same(items[index], registration.Item)) return false;
                    matches++;
                }
            return matches == 1;
        }

        private static bool RollBack(RuntimeRegistration registration, int id, bool attempted)
        {
            try
            {
                if (attempted)
                {
                    if (registration.Cache.TryGetValue(id, out var cached) && Same(cached, registration.Item))
                        registration.Cache.Remove(id);
                    for (var index = registration.Items.Count - 1; index >= 0; index--)
                        if (Same(registration.Items[index], registration.Item)) registration.Items.RemoveAt(index);
                    if (registration.Cache.TryGetValue(id, out cached) && Same(cached, registration.Item)) return false;
                    for (var index = 0; index < registration.Items.Count; index++)
                        if (Same(registration.Items[index], registration.Item)) return false;
                }
                ReleaseUnregistered(registration);
                return true;
            }
            catch { return false; }
        }

        private static void ReleaseUnregistered(RuntimeRegistration registration)
        {
            if (!registration.Owned) return;
            if (Live(registration.Table))
                foreach (var pair in registration.Translations)
                {
                    var entry = registration.Table.GetEntry(pair.Key);
                    if (entry != null && entry.Pointer == pair.Value)
                    {
                        registration.Table.RemoveEntry(pair.Key);
                        entry = registration.Table.GetEntry(pair.Key);
                        if (entry != null && entry.Pointer == pair.Value)
                            throw new InvalidOperationException("An owned localization entry could not be removed.");
                    }
                }
            if (Live(registration.PrefabRoot)) Object.Destroy(registration.PrefabRoot);
            if (Live(registration.Item)) Object.Destroy(registration.Item);
        }

        private static void RefreshRuntimeRegistrations()
        {
            if (_runtimeOwnerThreadId == 0 || Environment.CurrentManagedThreadId != _runtimeOwnerThreadId || _runtimeBusy) return;
            lock (SyncRoot)
            {
                foreach (var pair in RuntimeRegistrations)
                {
                    if (!RegisteredDefinitions.TryGetValue(pair.Key, out var definition)) continue;
                    var current = false;
                    try { current = pair.Value.Published && IsCurrent(pair.Value, definition.ItemId); }
                    catch { /* A missing native lifetime must never remain reported as registered. */ }
                    if (current != definition.RuntimeRegistered)
                        RegisteredDefinitions[pair.Key] = WithRuntimeState(definition, current);
                }
            }
        }

        private static CustomItemDefinitionSnapshot DefinitionSnapshot(NormalizedDefinition definition, ItemData donor, bool registered)
            => new CustomItemDefinitionSnapshot(definition.ModId, definition.ItemId, definition.ItemKey,
                definition.Kind, DefinitionKindName(definition.Kind), definition.DisplayName, definition.ItemType,
                definition.BaseItemId, Live(donor) ? donor._name : "", definition.CanBeSpawned, definition.CanBeHotKeyed,
                definition.AssetReferenceKind, definition.AssetReference, definition.Tags, registered, DateTimeOffset.UtcNow.ToString("O"));

        private static CustomItemDefinitionSnapshot WithRuntimeState(CustomItemDefinitionSnapshot value, bool registered)
            => new CustomItemDefinitionSnapshot(value.ModId, value.ItemId, value.ItemKey, value.DefinitionKind,
                value.DefinitionKindName, value.DisplayName, value.ItemType, value.BaseItemId, value.BaseItemDisplayName,
                value.CanBeSpawned, value.CanBeHotKeyed, value.AssetReferenceKind, value.AssetReference, value.Tags, registered, value.RegisteredUtc);

        private static bool Live(Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Object left, Object right) => Live(left) && Live(right) && left.Pointer == right.Pointer;
        private static ItemRegistrationResult Result(bool registered, bool attempted, bool uncertain, string reason, string detail)
            => new ItemRegistrationResult(registered, attempted, uncertain, reason, detail, null);

        private sealed class RuntimeRegistration
        {
            internal ItemDatabase Database;
            internal NativeItems Items;
            internal NativeItemCache Cache;
            internal ItemData Item;
            internal bool Owned;
            internal bool Published;
            internal int NativeModuleMask;
            internal int NativeModuleItemId;
            internal int NativeModuleItemIndex;
            internal GameObject PrefabRoot;
            internal StringTable Table;
            internal readonly Dictionary<string, IntPtr> Translations = new Dictionary<string, IntPtr>();
        }
    }

    /// <summary>Local native database publication only; not inventory, equip, save or multiplayer acceptance.</summary>
    public sealed class ItemRegistrationResult
    {
        internal ItemRegistrationResult(bool registered, bool attempted, bool uncertain,
            string reason, string detail, CustomItemDefinitionSnapshot definition)
        {
            Registered = registered;
            MutationAttempted = attempted;
            NativeStateUncertain = uncertain;
            ReasonCode = reason;
            Detail = detail;
            Definition = definition;
        }

        public bool Registered { get; }
        /// <summary>Whether native list/cache publication was attempted, excluding clone allocation and localization.</summary>
        public bool MutationAttempted { get; }
        public bool NativeStateUncertain { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public CustomItemDefinitionSnapshot Definition { get; }
    }
}
