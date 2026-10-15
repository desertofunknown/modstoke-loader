// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned inventory readiness and action planning. Live read helpers
// share this service's lifecycle thread; runtime mutation remains gated.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsInventory
    {
        public const string ServiceKey = "openacai.sons.inventory";

        private static readonly bool RuntimeInventoryMutationEnabledValue = false;

        private static readonly object SyncRoot = new object();
        private static readonly string[] SupportedActionKindNames =
        {
            "add-item",
            "remove-item",
            "equip-item",
            "spawn-item",
            "craft-result",
            "register-item",
            "replace-inventory-model"
        };

        private static OpenACAISonsInventorySnapshot _state = OpenACAISonsInventorySnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _ownerThreadId;
        private static int _planRequestCount;
        private static int _acceptedPlanCount;
        private static int _rejectedPlanCount;
        private static string _lastActionKind = "none";
        private static string _lastActionStatus = "INFO";
        private static string _lastActionDetail = "No inventory action has been planned.";
        private static string _lastActionReason = "none";

        public static OpenACAISonsInventorySnapshot State
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
        public static bool ActionPlanningEnabled => true;
        public static bool RuntimeMutationEnabled => RuntimeInventoryMutationEnabledValue;
        public static IReadOnlyList<string> SupportedActionKinds => SupportedActionKindNames;

        public static OpenACAISonsInventoryActionPlan PlanAddItem(int itemId, int quantity = 1)
        {
            return PlanAction(OpenACAISonsInventoryActionKind.AddItem, itemId, quantity);
        }

        public static OpenACAISonsInventoryActionPlan PlanRemoveItem(int itemId, int quantity = 1)
        {
            return PlanAction(OpenACAISonsInventoryActionKind.RemoveItem, itemId, quantity);
        }

        public static OpenACAISonsInventoryActionPlan PlanEquipItem(int itemId)
        {
            return PlanAction(OpenACAISonsInventoryActionKind.EquipItem, itemId, 1);
        }

        public static OpenACAISonsInventoryActionPlan PlanSpawnItem(int itemId, int quantity = 1)
        {
            return PlanAction(OpenACAISonsInventoryActionKind.SpawnItem, itemId, quantity);
        }

        public static OpenACAISonsInventoryActionPlan PlanCraftResult(int itemId, int quantity = 1)
        {
            return PlanAction(OpenACAISonsInventoryActionKind.CraftResult, itemId, quantity);
        }

        public static OpenACAISonsInventoryActionPlan PlanRegisterItem(int itemId)
        {
            return PlanAction(OpenACAISonsInventoryActionKind.RegisterItem, itemId, 1);
        }

        public static OpenACAISonsInventoryActionPlan PlanReplaceInventoryModel(int itemId)
        {
            return PlanAction(OpenACAISonsInventoryActionKind.ReplaceInventoryModel, itemId, 1);
        }

        public static OpenACAISonsInventoryActionPlan PlanAction(OpenACAISonsInventoryActionKind kind, int itemId, int quantity = 1)
        {
            var snapshot = State;
            var actionKind = ActionKindName(kind);
            var normalizedQuantity = Math.Max(0, quantity);
            var accepted = false;
            var canExecuteNow = false;
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Inventory action plan is invalid.";
            OpenACAISonsItemSnapshot item = null;

            if (!IsSupportedActionKind(kind))
            {
                detail = "Unsupported OpenACAI inventory action kind.";
                reason = "unsupported-action";
            }
            else if (itemId <= 0)
            {
                detail = "A positive Sons item id is required.";
                reason = "invalid-item-id";
            }
            else if (kind != OpenACAISonsInventoryActionKind.RegisterItem
                && !OpenACAISonsItems.TryFindItem(itemId, out item))
            {
                detail = "Sons item id " + itemId + " was not found in the OpenACAI item catalog.";
                reason = "unknown-item";
            }
            else if (kind == OpenACAISonsInventoryActionKind.RegisterItem
                && OpenACAISonsItems.TryFindItem(itemId, out item))
            {
                detail = "Sons item id " + itemId + " is already registered in the item database.";
                reason = "item-already-registered";
            }
            else if (RequiresPositiveQuantity(kind) && normalizedQuantity <= 0)
            {
                detail = "Inventory action '" + actionKind + "' requires a positive quantity.";
                reason = "invalid-quantity";
            }
            else if (kind == OpenACAISonsInventoryActionKind.SpawnItem && item != null && !item.CanBeSpawned)
            {
                detail = "Item " + itemId + " is not marked spawnable by Sons metadata.";
                reason = "item-not-spawnable";
            }
            else
            {
                accepted = true;
                if (!snapshot.Ready)
                {
                    status = "DEFERRED";
                    reason = snapshot.IsInWorld ? "inventory-not-ready" : "not-in-world";
                    detail = "OpenACAI accepted the action plan, but inventory mutation is deferred until an in-world inventory is available.";
                }
                else if (!RuntimeMutationEnabled)
                {
                    status = "DEFERRED";
                    reason = "mutation-disabled";
                    detail = "OpenACAI accepted the action plan; runtime inventory mutation remains disabled until the in-world mutation path is verified.";
                }
                else
                {
                    canExecuteNow = true;
                    status = "READY";
                    reason = "ready";
                    detail = "OpenACAI inventory action gate accepted the action for execution.";
                }
            }

            var plan = new OpenACAISonsInventoryActionPlan(
                kind,
                actionKind,
                itemId,
                normalizedQuantity,
                item == null ? "" : item.DisplayName,
                item == null ? "" : item.Type,
                accepted,
                canExecuteNow,
                RuntimeMutationEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
            RecordPlan(plan);
            return plan;
        }

        public static OpenACAISonsInventorySnapshot Refresh()
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

            _ownerThreadId = Environment.CurrentManagedThreadId;
            _initialized = true;
            OpenACAISons.SceneChanged += _ => Refresh();
            OpenACAISons.WorldChanged += _ => Refresh();
            Publish(OpenACAISonsInventorySnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK inventory provider could not be registered.");
            }
        }

        internal static void GetActionMetrics(
            out int planRequestCount,
            out int acceptedPlanCount,
            out int rejectedPlanCount,
            out string lastActionKind,
            out string lastActionStatus,
            out string lastActionDetail,
            out string lastActionReason)
        {
            lock (SyncRoot)
            {
                planRequestCount = _planRequestCount;
                acceptedPlanCount = _acceptedPlanCount;
                rejectedPlanCount = _rejectedPlanCount;
                lastActionKind = _lastActionKind ?? "";
                lastActionStatus = _lastActionStatus ?? "";
                lastActionDetail = _lastActionDetail ?? "";
                lastActionReason = _lastActionReason ?? "";
            }
        }

        private static void Publish(OpenACAISonsInventorySnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAISonsInventorySnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(OpenACAISonsInventoryActionPlan plan)
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

                _lastActionKind = plan == null ? "unknown" : plan.ActionKindName;
                _lastActionStatus = plan == null ? "CHECK" : plan.Status;
                _lastActionDetail = plan == null ? "Inventory action plan was null." : plan.Detail;
                _lastActionReason = plan == null ? "null-plan" : plan.ReasonCode;
            }
        }

        private static bool IsSupportedActionKind(OpenACAISonsInventoryActionKind kind)
        {
            switch (kind)
            {
                case OpenACAISonsInventoryActionKind.AddItem:
                case OpenACAISonsInventoryActionKind.RemoveItem:
                case OpenACAISonsInventoryActionKind.EquipItem:
                case OpenACAISonsInventoryActionKind.SpawnItem:
                case OpenACAISonsInventoryActionKind.CraftResult:
                case OpenACAISonsInventoryActionKind.RegisterItem:
                case OpenACAISonsInventoryActionKind.ReplaceInventoryModel:
                    return true;
                default:
                    return false;
            }
        }

        private static bool RequiresPositiveQuantity(OpenACAISonsInventoryActionKind kind)
        {
            return kind == OpenACAISonsInventoryActionKind.AddItem
                || kind == OpenACAISonsInventoryActionKind.RemoveItem
                || kind == OpenACAISonsInventoryActionKind.SpawnItem
                || kind == OpenACAISonsInventoryActionKind.CraftResult;
        }

        private static string ActionKindName(OpenACAISonsInventoryActionKind kind)
        {
            switch (kind)
            {
                case OpenACAISonsInventoryActionKind.AddItem:
                    return "add-item";
                case OpenACAISonsInventoryActionKind.RemoveItem:
                    return "remove-item";
                case OpenACAISonsInventoryActionKind.EquipItem:
                    return "equip-item";
                case OpenACAISonsInventoryActionKind.SpawnItem:
                    return "spawn-item";
                case OpenACAISonsInventoryActionKind.CraftResult:
                    return "craft-result";
                case OpenACAISonsInventoryActionKind.RegisterItem:
                    return "register-item";
                case OpenACAISonsInventoryActionKind.ReplaceInventoryModel:
                    return "replace-inventory-model";
                default:
                    return "unknown";
            }
        }

        private static OpenACAISonsInventorySnapshot CreateSnapshot()
        {
            try
            {
                var sonsState = OpenACAISons.State;
                var itemCatalog = OpenACAISonsItems.Catalog;
                var localPlayerType = FindType("TheForest.Utils.LocalPlayer", "TheForest.Utils");
                var inventoryLayoutItemGroupType = FindType("Sons.Inventory.InventoryLayoutItemGroup", "Sons");
                var inventoryLayoutItemType = FindType("Sons.Inventory.InventoryLayoutItem", "Sons");

                if (localPlayerType == null)
                {
                    return CreateSnapshotWithActionGate(
                        ServiceKey,
                        DateTimeOffset.UtcNow.ToString("O"),
                        false,
                        "CHECK",
                        "TheForest.Utils.LocalPlayer has not loaded yet.",
                        sonsState.SceneName,
                        sonsState.SceneKind,
                        sonsState.IsInWorld,
                        false,
                        false,
                        false,
                        "",
                        false,
                        false,
                        inventoryLayoutItemGroupType != null,
                        inventoryLayoutItemType != null,
                        itemCatalog.Ready,
                        itemCatalog.PublishedItemCount);
                }

                var localPlayer = ReadStaticValue(localPlayerType, "_instance", "Instance");
                var localPlayerAvailable = HasUnityObject(localPlayer);
                var localPlayerInWorld = ReadBool(localPlayerType, "IsInWorld") ?? sonsState.IsInWorld;
                var inventory = ReadStaticValue(localPlayerType, "Inventory")
                    ?? ReadValue(localPlayer, "Inventory", "_inventory");
                var inventoryAvailable = HasUnityObject(inventory);
                var inventoryProps = ReadValue(inventory, "InventoryProps", "_inventoryProps");
                var inventoryPropsAvailable = HasUnityObject(inventoryProps);
                var ready = localPlayerInWorld && inventoryAvailable;
                var detail = ready
                    ? "LocalPlayer inventory is available for read-only OpenACAI inventory-aware features."
                    : localPlayerInWorld
                        ? "LocalPlayer is in world, but inventory has not been observed yet."
                        : "Waiting for an in-world LocalPlayer inventory.";

                return CreateSnapshotWithActionGate(
                    ServiceKey,
                    DateTimeOffset.UtcNow.ToString("O"),
                    ready,
                    ready ? "READY" : "PENDING",
                    detail,
                    sonsState.SceneName,
                    sonsState.SceneKind,
                    localPlayerInWorld,
                    true,
                    localPlayerAvailable,
                    inventoryAvailable,
                    inventory == null ? "" : inventory.GetType().FullName,
                    inventoryPropsAvailable,
                    false,
                    inventoryLayoutItemGroupType != null,
                    inventoryLayoutItemType != null,
                    itemCatalog.Ready,
                    itemCatalog.PublishedItemCount);
            }
            catch (Exception ex)
            {
                return CreateSnapshotWithActionGate(
                    ServiceKey,
                    DateTimeOffset.UtcNow.ToString("O"),
                    false,
                    "CHECK",
                    "Sons inventory readiness probe failed: " + ex.GetType().Name + ": " + ex.Message,
                    OpenACAISons.State.SceneName,
                    OpenACAISons.State.SceneKind,
                    OpenACAISons.State.IsInWorld,
                    false,
                    false,
                    false,
                    "",
                    false,
                    false,
                    false,
                    false,
                    OpenACAISonsItems.Catalog.Ready,
                    OpenACAISonsItems.Catalog.PublishedItemCount);
            }
        }

        private static OpenACAISonsInventorySnapshot CreateSnapshotWithActionGate(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            string sceneName,
            string sceneKind,
            bool isInWorld,
            bool localPlayerTypeAvailable,
            bool localPlayerAvailable,
            bool inventoryAvailable,
            string inventoryTypeName,
            bool inventoryPropsAvailable,
            bool craftingSystemAvailable,
            bool layoutItemGroupTypeAvailable,
            bool inventoryLayoutItemTypeAvailable,
            bool itemCatalogReady,
            int itemCatalogPublishedCount)
        {
            int planRequestCount;
            int acceptedPlanCount;
            int rejectedPlanCount;
            string lastActionKind;
            string lastActionStatus;
            string lastActionDetail;
            string lastActionReason;
            GetActionMetrics(
                out planRequestCount,
                out acceptedPlanCount,
                out rejectedPlanCount,
                out lastActionKind,
                out lastActionStatus,
                out lastActionDetail,
                out lastActionReason);

            return new OpenACAISonsInventorySnapshot(
                serviceKey,
                generatedUtc,
                ready,
                status,
                detail,
                sceneName,
                sceneKind,
                isInWorld,
                localPlayerTypeAvailable,
                localPlayerAvailable,
                inventoryAvailable,
                inventoryTypeName,
                inventoryPropsAvailable,
                craftingSystemAvailable,
                layoutItemGroupTypeAvailable,
                inventoryLayoutItemTypeAvailable,
                itemCatalogReady,
                itemCatalogPublishedCount,
                ActionPlanningEnabled,
                RuntimeMutationEnabled,
                SupportedActionKindNames,
                planRequestCount,
                acceptedPlanCount,
                rejectedPlanCount,
                lastActionKind,
                lastActionStatus,
                lastActionDetail,
                lastActionReason);
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Sons Inventory",
                "Read-only OpenACAI inventory readiness state for Sons Of The Forest.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateMetadataSection(snapshot),
                    CreateActionSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAISonsInventorySnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Inventory Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned read-only inventory readiness snapshot."),
                    Row("Scene", string.IsNullOrWhiteSpace(snapshot.SceneName) ? "Unknown" : snapshot.SceneName, "INFO", "Current Sons scene observed by OpenACAI."),
                    Row("World", snapshot.IsInWorld ? "In world" : "Not in world", snapshot.IsInWorld ? "READY" : "PENDING", "Inventory helpers should wait for in-world state."),
                    Row("LocalPlayer", snapshot.LocalPlayerAvailable ? "Available" : "Unavailable", snapshot.LocalPlayerAvailable ? "READY" : "PENDING", "LocalPlayer instance readiness."),
                    Row("Inventory", snapshot.InventoryAvailable ? "Available" : "Unavailable", snapshot.InventoryAvailable ? "READY" : "PENDING", snapshot.Detail),
                    Row("Mode", "Read-only", "READY", "This service does not add, remove, spawn, register, or mutate items.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateMetadataSection(OpenACAISonsInventorySnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Inventory Metadata",
                new[]
                {
                    Row("Inventory Type", string.IsNullOrWhiteSpace(snapshot.InventoryTypeName) ? "Unknown" : snapshot.InventoryTypeName, snapshot.InventoryAvailable ? "INFO" : "PENDING", "Observed runtime inventory object type."),
                    Row("Inventory Props", snapshot.InventoryPropsAvailable ? "Available" : "Unavailable", snapshot.InventoryPropsAvailable ? "INFO" : "PENDING", "Inventory prop metadata surface."),
                    Row("Layout Groups", snapshot.LayoutItemGroupTypeAvailable ? "Type available" : "Type unavailable", snapshot.LayoutItemGroupTypeAvailable ? "INFO" : "CHECK", "Sons inventory layout item-group type availability."),
                    Row("Layout Item Type", snapshot.InventoryLayoutItemTypeAvailable ? "Type available" : "Type unavailable", snapshot.InventoryLayoutItemTypeAvailable ? "INFO" : "CHECK", "Sons inventory layout item type availability."),
                    Row("Item Catalog", snapshot.ItemCatalogReady ? snapshot.ItemCatalogPublishedCount.ToString() : "Unavailable", snapshot.ItemCatalogReady ? "READY" : "CHECK", "Backed by Modstoke SDK item metadata.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateActionSection(OpenACAISonsInventorySnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Inventory Actions",
                new[]
                {
                    Row("Action Planning", snapshot.ActionPlanningEnabled ? "Enabled" : "Disabled", snapshot.ActionPlanningEnabled ? "READY" : "CHECK", "OpenACAI validates inventory/item action intent before any runtime mutation path is used."),
                    Row("Runtime Mutation", snapshot.RuntimeMutationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeMutationEnabled ? "READY" : "INFO", "Inventory mutation remains disabled until the in-world mutation path is verified."),
                    Row("Supported", snapshot.SupportedActionKinds.Count.ToString(), "INFO", string.Join(", ", snapshot.SupportedActionKinds.Take(6))),
                    Row("Plans", snapshot.PlanRequestCount + " total", "INFO", snapshot.AcceptedPlanCount + " accepted, " + snapshot.RejectedPlanCount + " rejected."),
                    Row("Last Action", string.IsNullOrWhiteSpace(snapshot.LastActionKind) ? "none" : snapshot.LastActionKind, snapshot.LastActionStatus, snapshot.LastActionDetail),
                    Row("Last Reason", string.IsNullOrWhiteSpace(snapshot.LastActionReason) ? "none" : snapshot.LastActionReason, "INFO", "Reason code from the most recent OpenACAI inventory action plan.")
                });
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private static Type FindType(string fullName, string assemblyName)
        {
            var type = Type.GetType(fullName + ", " + assemblyName, false);
            if (type != null)
            {
                return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static object ReadStaticValue(Type type, params string[] names)
        {
            return ReadValue(type, null, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy, names);
        }

        private static object ReadValue(object target, params string[] names)
        {
            return target == null
                ? null
                : ReadValue(target.GetType(), target, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, names);
        }

        private static object ReadValue(Type type, object target, BindingFlags flags, params string[] names)
        {
            if (type == null)
            {
                return null;
            }

            foreach (var name in names.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                try
                {
                    var property = type.GetProperty(name, flags);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        return property.GetValue(target, null);
                    }
                }
                catch
                {
                }

                try
                {
                    var field = type.GetField(name, flags);
                    if (field != null)
                    {
                        return field.GetValue(target);
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool? ReadBool(Type type, params string[] names)
        {
            var value = ReadStaticValue(type, names);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasUnityObject(object value)
        {
            if (value == null)
            {
                return false;
            }

            var wasCollected = ReadValue(value, "WasCollected");
            if (wasCollected != null)
            {
                try
                {
                    if (Convert.ToBoolean(wasCollected))
                    {
                        return false;
                    }
                }
                catch
                {
                }
            }

            var pointer = ReadValue(value, "Pointer", "NativePointer");
            if (pointer is IntPtr && (IntPtr)pointer == IntPtr.Zero)
            {
                return false;
            }

            return true;
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsInventory;
            public string DisplayName => "Sons Inventory";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 721;
            public string Source => "Modstoke Loader";
            public string Description => "Show read-only Sons inventory readiness exposed by OpenACAI.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsInventory.CreateContent();
            }
        }
    }

    public sealed class OpenACAISonsInventorySnapshot
    {
        internal OpenACAISonsInventorySnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            string sceneName,
            string sceneKind,
            bool isInWorld,
            bool localPlayerTypeAvailable,
            bool localPlayerAvailable,
            bool inventoryAvailable,
            string inventoryTypeName,
            bool inventoryPropsAvailable,
            bool craftingSystemAvailable,
            bool layoutItemGroupTypeAvailable,
            bool inventoryLayoutItemTypeAvailable,
            bool itemCatalogReady,
            int itemCatalogPublishedCount,
            bool actionPlanningEnabled,
            bool runtimeMutationEnabled,
            IReadOnlyList<string> supportedActionKinds,
            int planRequestCount,
            int acceptedPlanCount,
            int rejectedPlanCount,
            string lastActionKind,
            string lastActionStatus,
            string lastActionDetail,
            string lastActionReason)
        {
            ServiceKey = serviceKey ?? OpenACAISonsInventory.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            SceneName = sceneName ?? "";
            SceneKind = sceneKind ?? "";
            IsInWorld = isInWorld;
            LocalPlayerTypeAvailable = localPlayerTypeAvailable;
            LocalPlayerAvailable = localPlayerAvailable;
            InventoryAvailable = inventoryAvailable;
            InventoryTypeName = inventoryTypeName ?? "";
            InventoryPropsAvailable = inventoryPropsAvailable;
            CraftingSystemAvailable = craftingSystemAvailable;
            LayoutItemGroupTypeAvailable = layoutItemGroupTypeAvailable;
            InventoryLayoutItemTypeAvailable = inventoryLayoutItemTypeAvailable;
            ItemCatalogReady = itemCatalogReady;
            ItemCatalogPublishedCount = Math.Max(0, itemCatalogPublishedCount);
            ActionPlanningEnabled = actionPlanningEnabled;
            RuntimeMutationEnabled = runtimeMutationEnabled;
            SupportedActionKinds = supportedActionKinds ?? Array.Empty<string>();
            PlanRequestCount = Math.Max(0, planRequestCount);
            AcceptedPlanCount = Math.Max(0, acceptedPlanCount);
            RejectedPlanCount = Math.Max(0, rejectedPlanCount);
            LastActionKind = lastActionKind ?? "";
            LastActionStatus = lastActionStatus ?? "";
            LastActionDetail = lastActionDetail ?? "";
            LastActionReason = lastActionReason ?? "";
        }

        public int Schema => 2;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public string SceneName { get; }
        public string SceneKind { get; }
        public bool IsInWorld { get; }
        public bool LocalPlayerTypeAvailable { get; }
        public bool LocalPlayerAvailable { get; }
        public bool InventoryAvailable { get; }
        public string InventoryTypeName { get; }
        public bool InventoryPropsAvailable { get; }
        public bool CraftingSystemAvailable { get; }
        public bool LayoutItemGroupTypeAvailable { get; }
        public bool InventoryLayoutItemTypeAvailable { get; }
        public bool ItemCatalogReady { get; }
        public int ItemCatalogPublishedCount { get; }
        public bool ActionPlanningEnabled { get; }
        public bool RuntimeMutationEnabled { get; }
        public IReadOnlyList<string> SupportedActionKinds { get; }
        public int PlanRequestCount { get; }
        public int AcceptedPlanCount { get; }
        public int RejectedPlanCount { get; }
        public string LastActionKind { get; }
        public string LastActionStatus { get; }
        public string LastActionDetail { get; }
        public string LastActionReason { get; }

        internal static OpenACAISonsInventorySnapshot CreateUnknown()
        {
            return new OpenACAISonsInventorySnapshot(
                OpenACAISonsInventory.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Sons inventory readiness has not refreshed yet.",
                "",
                "",
                false,
                false,
                false,
                false,
                "",
                false,
                false,
                false,
                false,
                false,
                0,
                true,
                OpenACAISonsInventory.RuntimeMutationEnabled,
                OpenACAISonsInventory.SupportedActionKinds,
                0,
                0,
                0,
                "none",
                "INFO",
                "No inventory action has been planned.",
                "none");
        }
    }

    public enum OpenACAISonsInventoryActionKind
    {
        AddItem = 0,
        RemoveItem = 1,
        EquipItem = 2,
        SpawnItem = 3,
        CraftResult = 4,
        RegisterItem = 5,
        ReplaceInventoryModel = 6
    }

    public sealed class OpenACAISonsInventoryActionPlan
    {
        internal OpenACAISonsInventoryActionPlan(
            OpenACAISonsInventoryActionKind actionKind,
            string actionKindName,
            int itemId,
            int quantity,
            string itemDisplayName,
            string itemType,
            bool accepted,
            bool canExecuteNow,
            bool runtimeMutationEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            ActionKind = actionKind;
            ActionKindName = actionKindName ?? "";
            ItemId = itemId;
            Quantity = Math.Max(0, quantity);
            ItemDisplayName = itemDisplayName ?? "";
            ItemType = itemType ?? "";
            Accepted = accepted;
            CanExecuteNow = canExecuteNow;
            RuntimeMutationEnabled = runtimeMutationEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public OpenACAISonsInventoryActionKind ActionKind { get; }
        public string ActionKindName { get; }
        public int ItemId { get; }
        public int Quantity { get; }
        public string ItemDisplayName { get; }
        public string ItemType { get; }
        public bool Accepted { get; }
        public bool CanExecuteNow { get; }
        public bool RuntimeMutationEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }
}
