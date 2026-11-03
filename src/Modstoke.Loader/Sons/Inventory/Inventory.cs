// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Inventory plans, live observations and explicit native actions share the
// initialized Unity lifecycle thread. Plans never execute an action.

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        public const string ServiceKey = "openacai.sons.inventory";

        private static readonly bool RuntimeInventoryMutationEnabledValue = true;

        private static readonly object SyncRoot = new object();
        private static readonly IReadOnlyList<string> SupportedActionKindNames = Array.AsReadOnly(new[]
        {
            "add-item",
            "remove-item",
            "equip-item",
            "spawn-item",
            "craft-result",
            "register-item",
            "replace-inventory-model"
        });

        private static InventorySnapshot _state = InventorySnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _ownerThreadId;
        private static long _refreshedAt;
        private static int _planRequestCount;
        private static int _acceptedPlanCount;
        private static int _rejectedPlanCount;
        private static string _lastActionKind = "none";
        private static string _lastActionStatus = "INFO";
        private static string _lastActionDetail = "No inventory action has been planned.";
        private static string _lastActionReason = "none";

        public static InventorySnapshot State
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

        public static InventoryActionPlan PlanAddItem(int itemId, int quantity = 1)
        {
            return PlanAction(InventoryActionKind.AddItem, itemId, quantity);
        }

        public static InventoryActionPlan PlanRemoveItem(int itemId, int quantity = 1)
        {
            return PlanAction(InventoryActionKind.RemoveItem, itemId, quantity);
        }

        public static InventoryActionPlan PlanEquipItem(int itemId)
        {
            return PlanAction(InventoryActionKind.EquipItem, itemId, 1);
        }

        public static InventoryActionPlan PlanSpawnItem(int itemId, int quantity = 1)
        {
            return PlanAction(InventoryActionKind.SpawnItem, itemId, quantity);
        }

        public static InventoryActionPlan PlanCraftResult(int itemId, int quantity = 1)
        {
            return PlanAction(InventoryActionKind.CraftResult, itemId, quantity);
        }

        public static InventoryActionPlan PlanRegisterItem(int itemId)
        {
            return PlanAction(InventoryActionKind.RegisterItem, itemId, 1);
        }

        public static InventoryActionPlan PlanReplaceInventoryModel(int itemId)
        {
            return PlanAction(InventoryActionKind.ReplaceInventoryModel, itemId, 1);
        }

        public static InventoryActionPlan PlanAction(InventoryActionKind kind, int itemId, int quantity = 1)
        {
            var snapshot = State;
            var actionKind = ActionKindName(kind);
            var normalizedQuantity = Math.Max(0, quantity);
            var accepted = false;
            var canExecuteNow = false;
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Inventory action plan is invalid.";
            ItemSnapshot item = null;

            if (!IsSupportedActionKind(kind))
            {
                detail = "Unsupported Modstoke inventory action kind.";
                reason = "unsupported-action";
            }
            else if (itemId <= 0)
            {
                detail = "A positive Sons item id is required.";
                reason = "invalid-item-id";
            }
            else if (kind != InventoryActionKind.RegisterItem
                && !ItemCatalog.TryFindItem(itemId, out item))
            {
                detail = "Sons item id " + itemId + " was not found in the Modstoke item catalog.";
                reason = "unknown-item";
            }
            else if (kind == InventoryActionKind.RegisterItem
                && ItemCatalog.TryFindItem(itemId, out item))
            {
                detail = "Sons item id " + itemId + " is already registered in the item database.";
                reason = "item-already-registered";
            }
            else if (RequiresPositiveQuantity(kind) && normalizedQuantity <= 0)
            {
                detail = "Inventory action '" + actionKind + "' requires a positive quantity.";
                reason = "invalid-quantity";
            }
            else if (kind == InventoryActionKind.SpawnItem && item != null && !item.CanBeSpawned)
            {
                detail = "Item " + itemId + " is not marked spawnable by Sons metadata.";
                reason = "item-not-spawnable";
            }
            else
            {
                accepted = true;
                if (!snapshot.Ready || !snapshot.IsInWorld)
                {
                    status = "DEFERRED";
                    reason = snapshot.IsInWorld ? "inventory-not-ready" : "not-in-world";
                    detail = "Modstoke accepted the action plan, but inventory mutation is deferred until an in-world inventory is available.";
                }
                else if (!CanExecuteItemIdAction(kind))
                {
                    status = "DEFERRED";
                    reason = "additional-execution-input-required";
                    detail = "This action needs its dedicated execution API and recipe, definition, prefab or world placement inputs; an item-id plan alone cannot execute it.";
                }
                else
                {
                    canExecuteNow = true;
                    status = "READY";
                    reason = "ready";
                    detail = "An explicit native action API is available. Execution rechecks the live inventory; this plan does not authorize or perform a mutation.";
                }
            }

            var plan = new InventoryActionPlan(
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

        /// <summary>
        /// Captures inventory readiness on the initialized Unity lifecycle thread.
        /// Other callers receive an unavailable result without native reads or
        /// replacement of the published state.
        /// </summary>
        public static InventorySnapshot Refresh()
        {
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                var current = State;
                return CreateSnapshotWithActionGate(
                    ServiceKey,
                    DateTimeOffset.UtcNow.ToString("O"),
                    false,
                    "CHECK",
                    "Inventory refresh requires the initialized Unity lifecycle thread.",
                    current.SceneName,
                    current.SceneKind,
                    current.IsInWorld,
                    current.LocalPlayerTypeAvailable,
                    false,
                    false,
                    "",
                    false,
                    false,
                    current.LayoutItemGroupTypeAvailable,
                    current.InventoryLayoutItemTypeAvailable,
                    current.ItemCatalogReady,
                    current.ItemCatalogPublishedCount);
            }

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

            _ownerThreadId = Environment.CurrentManagedThreadId;
            _initialized = true;
            GameRuntime.SceneChanged += _ => Refresh();
            GameRuntime.WorldChanged += _ => Refresh();
            Publish(InventorySnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK inventory provider could not be registered.");
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

        private static void Publish(InventorySnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? InventorySnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(InventoryActionPlan plan)
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

        private static bool IsSupportedActionKind(InventoryActionKind kind)
        {
            switch (kind)
            {
                case InventoryActionKind.AddItem:
                case InventoryActionKind.RemoveItem:
                case InventoryActionKind.EquipItem:
                case InventoryActionKind.SpawnItem:
                case InventoryActionKind.CraftResult:
                case InventoryActionKind.RegisterItem:
                case InventoryActionKind.ReplaceInventoryModel:
                    return true;
                default:
                    return false;
            }
        }

        private static bool RequiresPositiveQuantity(InventoryActionKind kind)
        {
            return kind == InventoryActionKind.AddItem
                || kind == InventoryActionKind.RemoveItem
                || kind == InventoryActionKind.SpawnItem
                || kind == InventoryActionKind.CraftResult;
        }

        private static string ActionKindName(InventoryActionKind kind)
        {
            switch (kind)
            {
                case InventoryActionKind.AddItem:
                    return "add-item";
                case InventoryActionKind.RemoveItem:
                    return "remove-item";
                case InventoryActionKind.EquipItem:
                    return "equip-item";
                case InventoryActionKind.SpawnItem:
                    return "spawn-item";
                case InventoryActionKind.CraftResult:
                    return "craft-result";
                case InventoryActionKind.RegisterItem:
                    return "register-item";
                case InventoryActionKind.ReplaceInventoryModel:
                    return "replace-inventory-model";
                default:
                    return "unknown";
            }
        }

        private static InventorySnapshot CreateSnapshot()
        {
            try
            {
                var sonsState = GameRuntime.State;
                var itemCatalog = ItemCatalog.Catalog;
                var localPlayerAvailable = InventoryLayout.Live(LocalPlayer._instance);
                var localPlayerInWorld = LocalPlayer.IsInWorld;
                var inventory = LocalPlayer.Inventory;
                var inventoryAvailable = InventoryLayout.Live(inventory);
                var inventoryPropsAvailable = inventoryAvailable && InventoryLayout.Live(inventory._InventoryProps_k__BackingField);
                // IsInWorld describes the current view, not the inventory's lifetime.
                var ready = localPlayerAvailable && inventoryAvailable;
                var detail = ready
                    ? "LocalPlayer inventory is available for read-only Modstoke inventory-aware features."
                    : localPlayerAvailable
                        ? "LocalPlayer is available, but inventory has not been observed yet."
                        : "Waiting for a live LocalPlayer inventory.";

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
                    true,
                    true,
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
                    GameRuntime.State.SceneName,
                    GameRuntime.State.SceneKind,
                    GameRuntime.State.IsInWorld,
                    false,
                    false,
                    false,
                    "",
                    false,
                    false,
                    false,
                    false,
                    ItemCatalog.Catalog.Ready,
                    ItemCatalog.Catalog.PublishedItemCount);
            }
        }

        private static InventorySnapshot CreateSnapshotWithActionGate(
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

            return new InventorySnapshot(
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

        private static MenuContentSnapshot CreateContent()
        {
            // Open menu pages poll this, so a snapshot from the last two seconds is reused.
            var snapshot = Environment.TickCount64 - _refreshedAt < 2000 ? State : Refresh();
            return new MenuContentSnapshot(
                "Sons Inventory",
                "Read-only Modstoke inventory readiness state for Sons Of The Forest.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateMetadataSection(snapshot),
                    CreateActionSection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateGateSection(InventorySnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Inventory Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "Modstoke read-only inventory readiness snapshot."),
                    Row("Scene", string.IsNullOrWhiteSpace(snapshot.SceneName) ? "Unknown" : snapshot.SceneName, "INFO", "Current Sons scene observed by Modstoke."),
                    Row("World", snapshot.IsInWorld ? "In world" : "Not in world", snapshot.IsInWorld ? "READY" : "PENDING", "Inventory helpers should wait for in-world state."),
                    Row("LocalPlayer", snapshot.LocalPlayerAvailable ? "Available" : "Unavailable", snapshot.LocalPlayerAvailable ? "READY" : "PENDING", "LocalPlayer instance readiness."),
                    Row("Inventory", snapshot.InventoryAvailable ? "Available" : "Unavailable", snapshot.InventoryAvailable ? "READY" : "PENDING", snapshot.Detail),
                    Row("Mode", "Read-only", "READY", "This service does not add, remove, spawn, register, or mutate items.")
                });
        }

        private static MenuSectionSnapshot CreateMetadataSection(InventorySnapshot snapshot)
        {
            return new MenuSectionSnapshot(
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

        private static MenuSectionSnapshot CreateActionSection(InventorySnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Inventory Actions",
                new[]
                {
                    Row("Action Planning", snapshot.ActionPlanningEnabled ? "Enabled" : "Disabled", snapshot.ActionPlanningEnabled ? "READY" : "CHECK", "Modstoke validates inventory/item action intent before any runtime mutation path is used."),
                    Row("Runtime Mutation", snapshot.RuntimeMutationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeMutationEnabled ? "READY" : "INFO", "Explicit native actions are available. Plans do not execute; each call rechecks its required world, thread and inventory context."),
                    Row("Supported", snapshot.SupportedActionKinds.Count.ToString(), "INFO", string.Join(", ", snapshot.SupportedActionKinds.Take(6))),
                    Row("Plans", snapshot.PlanRequestCount + " total", "INFO", snapshot.AcceptedPlanCount + " accepted, " + snapshot.RejectedPlanCount + " rejected."),
                    Row("Last Action", string.IsNullOrWhiteSpace(snapshot.LastActionKind) ? "none" : snapshot.LastActionKind, snapshot.LastActionStatus, snapshot.LastActionDetail),
                    Row("Last Reason", string.IsNullOrWhiteSpace(snapshot.LastActionReason) ? "none" : snapshot.LastActionReason, "INFO", "Reason code from the most recent Modstoke inventory action plan.")
                });
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsInventory;
            public string DisplayName => "Sons Inventory";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 721;
            public string Source => "Modstoke Loader";
            public string Description => "Show read-only Sons inventory readiness exposed by Modstoke.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return Inventory.CreateContent();
            }
        }
    }

    public sealed class InventorySnapshot
    {
        internal InventorySnapshot(
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
            ServiceKey = serviceKey ?? Inventory.ServiceKey;
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

        internal static InventorySnapshot CreateUnknown()
        {
            return new InventorySnapshot(
                Inventory.ServiceKey,
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
                Inventory.RuntimeMutationEnabled,
                Inventory.SupportedActionKinds,
                0,
                0,
                0,
                "none",
                "INFO",
                "No inventory action has been planned.",
                "none");
        }
    }

    public enum InventoryActionKind
    {
        AddItem = 0,
        RemoveItem = 1,
        EquipItem = 2,
        SpawnItem = 3,
        CraftResult = 4,
        RegisterItem = 5,
        ReplaceInventoryModel = 6
    }

    public sealed class InventoryActionPlan
    {
        internal InventoryActionPlan(
            InventoryActionKind actionKind,
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

        public InventoryActionKind ActionKind { get; }
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
