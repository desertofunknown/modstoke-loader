// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Sons crafting metadata, planning and native mat execution.
// Plans validate metadata only; execution uses the local game's crafting lifecycle.

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        public const string ServiceKey = "openacai.sons.crafting";

        private static readonly object SyncRoot = new object();
        private static readonly IReadOnlyList<string> SupportedPlanKindNames = Array.AsReadOnly(new[]
        {
            "craft-result",
            "recipe",
            "ingredient-check"
        });

        private static CraftingSnapshot _state = CraftingSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _ownerThreadId;
        private static long _refreshedAt;
        private static int _planRequestCount;
        private static int _acceptedPlanCount;
        private static int _rejectedPlanCount;
        private static string _lastPlanKind = "none";
        private static string _lastPlanStatus = "INFO";
        private static string _lastPlanDetail = "No crafting plan has been validated.";
        private static string _lastPlanReason = "none";
        private static string _lastRecipeId = "";
        private static int _lastResultItemId;
        private static int _lastIngredientCount;

        public static CraftingSnapshot State
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
        /// <summary>
        /// Native local-mat execution is implemented. This is not a readiness or authority
        /// check, and does not make arbitrary metadata plans executable.
        /// </summary>
        public static bool RuntimeExecutionEnabled => true;
        public static IReadOnlyList<string> SupportedPlanKinds => SupportedPlanKindNames;

        public static CraftingPlan PlanCraftResult(int resultItemId, int quantity = 1)
        {
            return Plan(CraftingPlanKind.CraftResult, "", resultItemId, quantity, Array.Empty<CraftingIngredient>());
        }

        public static CraftingPlan PlanRecipe(
            string recipeId,
            int resultItemId,
            int quantity,
            params CraftingIngredient[] ingredients)
        {
            return PlanRecipe(recipeId, resultItemId, quantity, (IEnumerable<CraftingIngredient>)ingredients);
        }

        public static CraftingPlan PlanRecipe(
            string recipeId,
            int resultItemId,
            int quantity,
            IEnumerable<CraftingIngredient> ingredients)
        {
            return Plan(CraftingPlanKind.Recipe, recipeId, resultItemId, quantity, ingredients);
        }

        public static CraftingPlan PlanIngredientCheck(params CraftingIngredient[] ingredients)
        {
            return PlanIngredientCheck((IEnumerable<CraftingIngredient>)ingredients);
        }

        public static CraftingPlan PlanIngredientCheck(IEnumerable<CraftingIngredient> ingredients)
        {
            return Plan(CraftingPlanKind.IngredientCheck, "", 0, 0, ingredients);
        }

        public static CraftingPlan Plan(
            CraftingPlanKind kind,
            string recipeId,
            int resultItemId,
            int quantity,
            IEnumerable<CraftingIngredient> ingredients)
        {
            var snapshot = State;
            var planKindName = PlanKindName(kind);
            var normalizedQuantity = Math.Max(0, quantity);
            var ingredientQuantitiesValid = TryNormalizeIngredients(ingredients, out var normalizedIngredients);
            var normalizedRecipeId = NormalizeRecipeId(recipeId, kind, resultItemId);
            var accepted = false;
            var canExecuteNow = false;
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Crafting plan is invalid.";
            ItemSnapshot resultItem = null;
            IReadOnlyList<CraftingIngredientSnapshot> ingredientSnapshots = Array.Empty<CraftingIngredientSnapshot>();

            if (!IsSupportedPlanKind(kind))
            {
                detail = "Unsupported Modstoke crafting plan kind.";
                reason = "unsupported-plan-kind";
            }
            else if (RequiresResultItem(kind) && resultItemId <= 0)
            {
                detail = "A positive Sons result item id is required.";
                reason = "invalid-result-item-id";
            }
            else if (RequiresResultItem(kind) && !ItemCatalog.TryFindItem(resultItemId, out resultItem))
            {
                detail = "Sons result item id " + resultItemId + " was not found in the Modstoke item catalog.";
                reason = "unknown-result-item";
            }
            else if (RequiresResultQuantity(kind) && normalizedQuantity <= 0)
            {
                detail = "Crafting plan '" + planKindName + "' requires a positive result quantity.";
                reason = "invalid-result-quantity";
            }
            else if (!ingredientQuantitiesValid)
            {
                detail = "Ingredient quantities must be positive and their combined quantity must fit the supported integer range.";
                reason = "invalid-ingredient-quantity";
            }
            else if (RequiresIngredients(kind) && normalizedIngredients.Count == 0)
            {
                detail = "Crafting plan '" + planKindName + "' requires at least one ingredient.";
                reason = "missing-ingredients";
            }
            else if (!TryCreateIngredientSnapshots(normalizedIngredients, out ingredientSnapshots, out reason, out detail))
            {
                status = "CHECK";
            }
            else
            {
                accepted = true;
                if (kind == CraftingPlanKind.IngredientCheck)
                {
                    canExecuteNow = true;
                    status = "READY";
                    reason = "validated";
                    detail = "Modstoke validated ingredient item ids and quantities against the item catalog.";
                }
                else if (!snapshot.InventoryReady || !snapshot.IsInWorld)
                {
                    status = "DEFERRED";
                    reason = snapshot.IsInWorld ? "inventory-not-ready" : "not-in-world";
                    detail = "Modstoke accepted the crafting plan, but crafting execution is deferred until an in-world inventory is available.";
                }
                else
                {
                    status = "DEFERRED";
                    reason = "native-mat-required";
                    detail = "Metadata validated only. TryBeginCurrentCraft uses the native recipe selected by actual staged mat instances; this plan is not an executable recipe or reservation.";
                }
            }

            var plan = new CraftingPlan(
                kind,
                planKindName,
                normalizedRecipeId,
                resultItemId,
                normalizedQuantity,
                resultItem == null ? "" : resultItem.DisplayName,
                resultItem == null ? "" : resultItem.Type,
                ingredientSnapshots,
                accepted,
                canExecuteNow,
                RuntimeExecutionEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
            RecordPlan(plan);
            Refresh();
            return plan;
        }

        public static CraftingSnapshot Refresh()
        {
            RefreshActiveCraftExecution();
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
            Publish(CraftingSnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK crafting provider could not be registered.");
            }
        }

        private static void Publish(CraftingSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? CraftingSnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(CraftingPlan plan)
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

                _lastPlanKind = plan == null ? "unknown" : plan.PlanKindName;
                _lastPlanStatus = plan == null ? "CHECK" : plan.Status;
                _lastPlanDetail = plan == null ? "Crafting plan was null." : plan.Detail;
                _lastPlanReason = plan == null ? "null-plan" : plan.ReasonCode;
                _lastRecipeId = plan == null ? "" : plan.RecipeId;
                _lastResultItemId = plan == null ? 0 : plan.ResultItemId;
                _lastIngredientCount = plan == null ? 0 : plan.IngredientCount;
            }
        }

        private static CraftingSnapshot CreateSnapshot()
        {
            var sonsState = GameRuntime.State;
            var itemCatalog = ItemCatalog.Catalog;
            var inventory = Inventory.State;

            int planRequestCount;
            int acceptedPlanCount;
            int rejectedPlanCount;
            string lastPlanKind;
            string lastPlanStatus;
            string lastPlanDetail;
            string lastPlanReason;
            string lastRecipeId;
            int lastResultItemId;
            int lastIngredientCount;
            GetMetrics(
                out planRequestCount,
                out acceptedPlanCount,
                out rejectedPlanCount,
                out lastPlanKind,
                out lastPlanStatus,
                out lastPlanDetail,
                out lastPlanReason,
                out lastRecipeId,
                out lastResultItemId,
                out lastIngredientCount);

            var itemCatalogReady = itemCatalog != null && itemCatalog.Ready;
            var inventoryReady = inventory != null && inventory.Ready;
            return new CraftingSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                itemCatalogReady,
                itemCatalog == null ? "PENDING" : itemCatalogReady ? "READY" : itemCatalog.Status,
                itemCatalog == null
                    ? "Sons item catalog has not refreshed yet."
                    : itemCatalogReady
                        ? "Crafting plan validation is available through the Modstoke item catalog."
                        : itemCatalog.Detail,
                sonsState == null ? "" : sonsState.SceneName,
                sonsState == null ? "" : sonsState.SceneKind,
                sonsState != null && sonsState.IsInWorld,
                itemCatalogReady,
                itemCatalog == null ? 0 : itemCatalog.PublishedItemCount,
                inventoryReady,
                inventory == null ? "PENDING" : inventory.Status,
                PlanningEnabled,
                RuntimeExecutionEnabled,
                SupportedPlanKindNames,
                planRequestCount,
                acceptedPlanCount,
                rejectedPlanCount,
                lastPlanKind,
                lastPlanStatus,
                lastPlanDetail,
                lastPlanReason,
                lastRecipeId,
                lastResultItemId,
                lastIngredientCount);
        }

        private static void GetMetrics(
            out int planRequestCount,
            out int acceptedPlanCount,
            out int rejectedPlanCount,
            out string lastPlanKind,
            out string lastPlanStatus,
            out string lastPlanDetail,
            out string lastPlanReason,
            out string lastRecipeId,
            out int lastResultItemId,
            out int lastIngredientCount)
        {
            lock (SyncRoot)
            {
                planRequestCount = _planRequestCount;
                acceptedPlanCount = _acceptedPlanCount;
                rejectedPlanCount = _rejectedPlanCount;
                lastPlanKind = _lastPlanKind ?? "";
                lastPlanStatus = _lastPlanStatus ?? "";
                lastPlanDetail = _lastPlanDetail ?? "";
                lastPlanReason = _lastPlanReason ?? "";
                lastRecipeId = _lastRecipeId ?? "";
                lastResultItemId = _lastResultItemId;
                lastIngredientCount = _lastIngredientCount;
            }
        }

        private static MenuContentSnapshot CreateContent()
        {
            // Open menu pages poll this, so a snapshot from the last two seconds is reused.
            var snapshot = Environment.TickCount64 - _refreshedAt < 2000 ? State : Refresh();
            return new MenuContentSnapshot(
                "Sons Crafting",
                "Modstoke crafting plan validation and execution gate.",
                new[]
                {
                    CreatePlanningSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreatePlanningSection(CraftingSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Crafting Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "Modstoke crafting planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can validate crafting intent without RedLoader/SonsSdk crafting globals."),
                    Row("Runtime Execution", snapshot.RuntimeExecutionEnabled ? "Native Mat" : "Disabled", "INFO", "Local-player native mat execution is separate from metadata planning and requires live thread, inventory and authority checks."),
                    Row("Supported", string.Join(", ", snapshot.SupportedPlanKinds), "INFO", "Current Modstoke crafting plan kinds."),
                    Row("Item Catalog", snapshot.ItemCatalogPublishedCount + " items", snapshot.ItemCatalogReady ? "READY" : "CHECK", "Result and ingredient item ids are validated against Modstoke SDK Items."),
                    Row("Inventory Gate", snapshot.InventoryReady ? "Ready" : snapshot.InventoryStatus, snapshot.InventoryReady ? "READY" : snapshot.InventoryStatus, "Craft execution waits for Modstoke SDK Inventory readiness.")
                });
        }

        private static MenuSectionSnapshot CreateActivitySection(CraftingSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Plan Activity",
                new[]
                {
                    Row("Plans", snapshot.PlanRequestCount + " requested", "INFO", snapshot.AcceptedPlanCount + " accepted, " + snapshot.RejectedPlanCount + " rejected."),
                    Row("Last Plan", snapshot.LastPlanKind + " / " + snapshot.LastPlanStatus, snapshot.LastPlanStatus, snapshot.LastPlanDetail),
                    Row("Last Recipe", string.IsNullOrWhiteSpace(snapshot.LastRecipeId) ? "none" : snapshot.LastRecipeId, "INFO", "Last validated recipe identifier."),
                    Row("Last Result", snapshot.LastResultItemId <= 0 ? "none" : snapshot.LastResultItemId.ToString(), "INFO", "Last validated result item id."),
                    Row("Last Ingredients", snapshot.LastIngredientCount.ToString(), "INFO", "Last validated ingredient count."),
                    Row("Last Reason", snapshot.LastPlanReason, snapshot.LastPlanStatus, "Last crafting plan reason code.")
                });
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private static bool TryNormalizeIngredients(IEnumerable<CraftingIngredient> ingredients,
            out IReadOnlyList<CraftingIngredient> normalized)
        {
            var rows = new List<CraftingIngredient>();
            foreach (var group in (ingredients ?? Array.Empty<CraftingIngredient>())
                .Where(ingredient => ingredient != null)
                .GroupBy(ingredient => ingredient.ItemId))
            {
                var quantity = 0;
                foreach (var ingredient in group)
                {
                    if (ingredient.Quantity <= 0 || ingredient.Quantity > int.MaxValue - quantity)
                    {
                        normalized = Array.Empty<CraftingIngredient>();
                        return false;
                    }
                    quantity += ingredient.Quantity;
                }
                rows.Add(new CraftingIngredient(group.Key, quantity));
            }
            normalized = rows;
            return true;
        }

        private static bool TryCreateIngredientSnapshots(
            IReadOnlyList<CraftingIngredient> ingredients,
            out IReadOnlyList<CraftingIngredientSnapshot> snapshots,
            out string reason,
            out string detail)
        {
            var rows = new List<CraftingIngredientSnapshot>();
            foreach (var ingredient in ingredients ?? Array.Empty<CraftingIngredient>())
            {
                if (ingredient.ItemId <= 0)
                {
                    snapshots = Array.Empty<CraftingIngredientSnapshot>();
                    reason = "invalid-ingredient-item-id";
                    detail = "Ingredient item ids must be positive Sons item ids.";
                    return false;
                }

                if (ingredient.Quantity <= 0)
                {
                    snapshots = Array.Empty<CraftingIngredientSnapshot>();
                    reason = "invalid-ingredient-quantity";
                    detail = "Ingredient item id " + ingredient.ItemId + " requires a positive quantity.";
                    return false;
                }

                ItemSnapshot item;
                if (!ItemCatalog.TryFindItem(ingredient.ItemId, out item))
                {
                    snapshots = Array.Empty<CraftingIngredientSnapshot>();
                    reason = "unknown-ingredient-item";
                    detail = "Ingredient item id " + ingredient.ItemId + " was not found in the Modstoke item catalog.";
                    return false;
                }

                rows.Add(new CraftingIngredientSnapshot(
                    ingredient.ItemId,
                    ingredient.Quantity,
                    item.DisplayName,
                    item.Type));
            }

            // The plan constructor takes the defensive copy.
            snapshots = rows;
            reason = "validated";
            detail = "Ingredients validated.";
            return true;
        }

        private static bool IsSupportedPlanKind(CraftingPlanKind kind)
        {
            return kind == CraftingPlanKind.CraftResult
                || kind == CraftingPlanKind.Recipe
                || kind == CraftingPlanKind.IngredientCheck;
        }

        private static bool RequiresResultItem(CraftingPlanKind kind)
        {
            return kind == CraftingPlanKind.CraftResult
                || kind == CraftingPlanKind.Recipe;
        }

        private static bool RequiresResultQuantity(CraftingPlanKind kind)
        {
            return kind == CraftingPlanKind.CraftResult
                || kind == CraftingPlanKind.Recipe;
        }

        private static bool RequiresIngredients(CraftingPlanKind kind)
        {
            return kind == CraftingPlanKind.Recipe
                || kind == CraftingPlanKind.IngredientCheck;
        }

        private static string NormalizeRecipeId(string recipeId, CraftingPlanKind kind, int resultItemId)
        {
            if (!string.IsNullOrWhiteSpace(recipeId))
            {
                return recipeId.Trim();
            }

            if (kind == CraftingPlanKind.Recipe)
            {
                return "recipe:" + resultItemId;
            }

            if (kind == CraftingPlanKind.CraftResult)
            {
                return "result:" + resultItemId;
            }

            return "ingredient-check";
        }

        private static string PlanKindName(CraftingPlanKind kind)
        {
            switch (kind)
            {
                case CraftingPlanKind.CraftResult:
                    return "craft-result";
                case CraftingPlanKind.Recipe:
                    return "recipe";
                case CraftingPlanKind.IngredientCheck:
                    return "ingredient-check";
                default:
                    return "unknown";
            }
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsCrafting;
            public string DisplayName => "Sons Crafting";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 722;
            public string Source => "Modstoke Loader";
            public string Description => "Show Modstoke Sons crafting planning and execution gate state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return Crafting.CreateContent();
            }
        }
    }

    public enum CraftingPlanKind
    {
        CraftResult = 0,
        Recipe = 1,
        IngredientCheck = 2
    }

    public sealed class CraftingIngredient
    {
        public CraftingIngredient(int itemId, int quantity = 1)
        {
            ItemId = itemId;
            Quantity = Math.Max(0, quantity);
        }

        public int ItemId { get; }
        public int Quantity { get; }
    }

    public sealed class CraftingPlan
    {
        private readonly IReadOnlyList<CraftingIngredientSnapshot> _ingredients;

        internal CraftingPlan(
            CraftingPlanKind planKind,
            string planKindName,
            string recipeId,
            int resultItemId,
            int resultQuantity,
            string resultItemDisplayName,
            string resultItemType,
            IReadOnlyList<CraftingIngredientSnapshot> ingredients,
            bool accepted,
            bool canExecuteNow,
            bool runtimeExecutionEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            PlanKind = planKind;
            PlanKindName = planKindName ?? "";
            RecipeId = recipeId ?? "";
            ResultItemId = resultItemId;
            ResultQuantity = Math.Max(0, resultQuantity);
            ResultItemDisplayName = resultItemDisplayName ?? "";
            ResultItemType = resultItemType ?? "";
            _ingredients = Array.AsReadOnly((ingredients ?? Array.Empty<CraftingIngredientSnapshot>()).ToArray());
            Accepted = accepted;
            CanExecuteNow = canExecuteNow;
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public CraftingPlanKind PlanKind { get; }
        public string PlanKindName { get; }
        public string RecipeId { get; }
        public int ResultItemId { get; }
        public int ResultQuantity { get; }
        public string ResultItemDisplayName { get; }
        public string ResultItemType { get; }
        public IReadOnlyList<CraftingIngredientSnapshot> Ingredients => _ingredients;
        public int IngredientCount => _ingredients.Count;
        public bool Accepted { get; }
        public bool CanExecuteNow { get; }
        public bool RuntimeExecutionEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }

    public sealed class CraftingIngredientSnapshot
    {
        internal CraftingIngredientSnapshot(
            int itemId,
            int quantity,
            string itemDisplayName,
            string itemType)
        {
            ItemId = itemId;
            Quantity = Math.Max(0, quantity);
            ItemDisplayName = itemDisplayName ?? "";
            ItemType = itemType ?? "";
        }

        public int ItemId { get; }
        public int Quantity { get; }
        public string ItemDisplayName { get; }
        public string ItemType { get; }
    }

    public sealed class CraftingSnapshot
    {
        internal CraftingSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            string sceneName,
            string sceneKind,
            bool isInWorld,
            bool itemCatalogReady,
            int itemCatalogPublishedCount,
            bool inventoryReady,
            string inventoryStatus,
            bool planningEnabled,
            bool runtimeExecutionEnabled,
            IReadOnlyList<string> supportedPlanKinds,
            int planRequestCount,
            int acceptedPlanCount,
            int rejectedPlanCount,
            string lastPlanKind,
            string lastPlanStatus,
            string lastPlanDetail,
            string lastPlanReason,
            string lastRecipeId,
            int lastResultItemId,
            int lastIngredientCount)
        {
            ServiceKey = serviceKey ?? Crafting.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            SceneName = sceneName ?? "";
            SceneKind = sceneKind ?? "";
            IsInWorld = isInWorld;
            ItemCatalogReady = itemCatalogReady;
            ItemCatalogPublishedCount = Math.Max(0, itemCatalogPublishedCount);
            InventoryReady = inventoryReady;
            InventoryStatus = inventoryStatus ?? "";
            PlanningEnabled = planningEnabled;
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
            SupportedPlanKinds = supportedPlanKinds ?? Array.Empty<string>();
            PlanRequestCount = Math.Max(0, planRequestCount);
            AcceptedPlanCount = Math.Max(0, acceptedPlanCount);
            RejectedPlanCount = Math.Max(0, rejectedPlanCount);
            LastPlanKind = lastPlanKind ?? "";
            LastPlanStatus = lastPlanStatus ?? "";
            LastPlanDetail = lastPlanDetail ?? "";
            LastPlanReason = lastPlanReason ?? "";
            LastRecipeId = lastRecipeId ?? "";
            LastResultItemId = Math.Max(0, lastResultItemId);
            LastIngredientCount = Math.Max(0, lastIngredientCount);
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
        public int ItemCatalogPublishedCount { get; }
        public bool InventoryReady { get; }
        public string InventoryStatus { get; }
        public bool PlanningEnabled { get; }
        public bool RuntimeExecutionEnabled { get; }
        public IReadOnlyList<string> SupportedPlanKinds { get; }
        public int PlanRequestCount { get; }
        public int AcceptedPlanCount { get; }
        public int RejectedPlanCount { get; }
        public string LastPlanKind { get; }
        public string LastPlanStatus { get; }
        public string LastPlanDetail { get; }
        public string LastPlanReason { get; }
        public string LastRecipeId { get; }
        public int LastResultItemId { get; }
        public int LastIngredientCount { get; }

        internal static CraftingSnapshot CreateUnknown()
        {
            return new CraftingSnapshot(
                Crafting.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Sons crafting planning has not refreshed yet.",
                "",
                "",
                false,
                false,
                0,
                false,
                "PENDING",
                true,
                Crafting.RuntimeExecutionEnabled,
                Crafting.SupportedPlanKinds,
                0,
                0,
                0,
                "none",
                "INFO",
                "No crafting plan has been validated.",
                "none",
                "",
                0,
                0);
        }
    }
}
