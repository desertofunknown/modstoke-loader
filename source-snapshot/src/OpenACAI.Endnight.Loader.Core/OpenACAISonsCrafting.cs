// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons crafting metadata and planning surface. Plans validate
// item ids and recipe intent; recipe reads capture current game definitions.
// Neither path executes crafting or depends on RedLoader/SonsSdk globals.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsCrafting
    {
        public const string ServiceKey = "openacai.sons.crafting";

        private static readonly bool RuntimeCraftingExecutionEnabledValue = false;

        private static readonly object SyncRoot = new object();
        private static readonly string[] SupportedPlanKindNames =
        {
            "craft-result",
            "recipe",
            "ingredient-check"
        };

        private static OpenACAISonsCraftingSnapshot _state = OpenACAISonsCraftingSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _ownerThreadId;
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

        public static OpenACAISonsCraftingSnapshot State
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
        public static bool RuntimeExecutionEnabled => RuntimeCraftingExecutionEnabledValue;
        public static IReadOnlyList<string> SupportedPlanKinds => SupportedPlanKindNames;

        public static OpenACAISonsCraftingPlan PlanCraftResult(int resultItemId, int quantity = 1)
        {
            return Plan(OpenACAISonsCraftingPlanKind.CraftResult, "", resultItemId, quantity, Array.Empty<OpenACAISonsCraftingIngredient>());
        }

        public static OpenACAISonsCraftingPlan PlanRecipe(
            string recipeId,
            int resultItemId,
            int quantity,
            params OpenACAISonsCraftingIngredient[] ingredients)
        {
            return PlanRecipe(recipeId, resultItemId, quantity, (IEnumerable<OpenACAISonsCraftingIngredient>)ingredients);
        }

        public static OpenACAISonsCraftingPlan PlanRecipe(
            string recipeId,
            int resultItemId,
            int quantity,
            IEnumerable<OpenACAISonsCraftingIngredient> ingredients)
        {
            return Plan(OpenACAISonsCraftingPlanKind.Recipe, recipeId, resultItemId, quantity, ingredients);
        }

        public static OpenACAISonsCraftingPlan PlanIngredientCheck(params OpenACAISonsCraftingIngredient[] ingredients)
        {
            return PlanIngredientCheck((IEnumerable<OpenACAISonsCraftingIngredient>)ingredients);
        }

        public static OpenACAISonsCraftingPlan PlanIngredientCheck(IEnumerable<OpenACAISonsCraftingIngredient> ingredients)
        {
            return Plan(OpenACAISonsCraftingPlanKind.IngredientCheck, "", 0, 0, ingredients);
        }

        public static OpenACAISonsCraftingPlan Plan(
            OpenACAISonsCraftingPlanKind kind,
            string recipeId,
            int resultItemId,
            int quantity,
            IEnumerable<OpenACAISonsCraftingIngredient> ingredients)
        {
            var snapshot = State;
            var planKindName = PlanKindName(kind);
            var normalizedQuantity = Math.Max(0, quantity);
            var normalizedIngredients = NormalizeIngredients(ingredients);
            var normalizedRecipeId = NormalizeRecipeId(recipeId, kind, resultItemId);
            var accepted = false;
            var canExecuteNow = false;
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Crafting plan is invalid.";
            OpenACAISonsItemSnapshot resultItem = null;
            IReadOnlyList<OpenACAISonsCraftingIngredientSnapshot> ingredientSnapshots = Array.Empty<OpenACAISonsCraftingIngredientSnapshot>();

            if (!IsSupportedPlanKind(kind))
            {
                detail = "Unsupported OpenACAI crafting plan kind.";
                reason = "unsupported-plan-kind";
            }
            else if (RequiresResultItem(kind) && resultItemId <= 0)
            {
                detail = "A positive Sons result item id is required.";
                reason = "invalid-result-item-id";
            }
            else if (RequiresResultItem(kind) && !OpenACAISonsItems.TryFindItem(resultItemId, out resultItem))
            {
                detail = "Sons result item id " + resultItemId + " was not found in the OpenACAI item catalog.";
                reason = "unknown-result-item";
            }
            else if (RequiresResultQuantity(kind) && normalizedQuantity <= 0)
            {
                detail = "Crafting plan '" + planKindName + "' requires a positive result quantity.";
                reason = "invalid-result-quantity";
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
                if (kind == OpenACAISonsCraftingPlanKind.IngredientCheck)
                {
                    canExecuteNow = true;
                    status = "READY";
                    reason = "validated";
                    detail = "OpenACAI validated ingredient item ids and quantities against the item catalog.";
                }
                else if (!snapshot.InventoryReady)
                {
                    status = "DEFERRED";
                    reason = snapshot.IsInWorld ? "inventory-not-ready" : "not-in-world";
                    detail = "OpenACAI accepted the crafting plan, but crafting execution is deferred until an in-world inventory is available.";
                }
                else if (!RuntimeExecutionEnabled)
                {
                    status = "DEFERRED";
                    reason = "execution-disabled";
                    detail = "OpenACAI accepted the crafting plan; runtime crafting execution remains disabled until the in-world path is verified.";
                }
                else
                {
                    canExecuteNow = true;
                    status = "READY";
                    reason = "ready";
                    detail = "OpenACAI crafting gate accepted the plan for execution.";
                }
            }

            var plan = new OpenACAISonsCraftingPlan(
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

        public static OpenACAISonsCraftingSnapshot Refresh()
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
            Publish(OpenACAISonsCraftingSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK crafting provider could not be registered.");
            }
        }

        private static void Publish(OpenACAISonsCraftingSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAISonsCraftingSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(OpenACAISonsCraftingPlan plan)
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

        private static OpenACAISonsCraftingSnapshot CreateSnapshot()
        {
            var sonsState = OpenACAISons.State;
            var itemCatalog = OpenACAISonsItems.Catalog;
            var inventory = OpenACAISonsInventory.State;

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
            return new OpenACAISonsCraftingSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                itemCatalogReady,
                itemCatalog == null ? "PENDING" : itemCatalogReady ? "READY" : itemCatalog.Status,
                itemCatalog == null
                    ? "Sons item catalog has not refreshed yet."
                    : itemCatalogReady
                        ? "Crafting plan validation is available through the OpenACAI item catalog."
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

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Sons Crafting",
                "OpenACAI-owned crafting plan validation and execution gate.",
                new[]
                {
                    CreatePlanningSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreatePlanningSection(OpenACAISonsCraftingSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Crafting Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned crafting planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can validate crafting intent without RedLoader/SonsSdk crafting globals."),
                    Row("Runtime Execution", snapshot.RuntimeExecutionEnabled ? "Enabled" : "Disabled", snapshot.RuntimeExecutionEnabled ? "READY" : "INFO", "Execution is gated until the in-world craft path is verified."),
                    Row("Supported", string.Join(", ", snapshot.SupportedPlanKinds), "INFO", "Current OpenACAI-owned crafting plan kinds."),
                    Row("Item Catalog", snapshot.ItemCatalogPublishedCount + " items", snapshot.ItemCatalogReady ? "READY" : "CHECK", "Result and ingredient item ids are validated against Modstoke SDK Items."),
                    Row("Inventory Gate", snapshot.InventoryReady ? "Ready" : snapshot.InventoryStatus, snapshot.InventoryReady ? "READY" : snapshot.InventoryStatus, "Craft execution waits for Modstoke SDK Inventory readiness.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAISonsCraftingSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
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

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private static IReadOnlyList<OpenACAISonsCraftingIngredient> NormalizeIngredients(IEnumerable<OpenACAISonsCraftingIngredient> ingredients)
        {
            return (ingredients ?? Array.Empty<OpenACAISonsCraftingIngredient>())
                .Where(ingredient => ingredient != null)
                .GroupBy(ingredient => ingredient.ItemId)
                .Select(group => new OpenACAISonsCraftingIngredient(
                    group.Key,
                    group.Sum(ingredient => Math.Max(0, ingredient.Quantity))))
                .ToArray();
        }

        private static bool TryCreateIngredientSnapshots(
            IReadOnlyList<OpenACAISonsCraftingIngredient> ingredients,
            out IReadOnlyList<OpenACAISonsCraftingIngredientSnapshot> snapshots,
            out string reason,
            out string detail)
        {
            var rows = new List<OpenACAISonsCraftingIngredientSnapshot>();
            foreach (var ingredient in ingredients ?? Array.Empty<OpenACAISonsCraftingIngredient>())
            {
                if (ingredient.ItemId <= 0)
                {
                    snapshots = Array.Empty<OpenACAISonsCraftingIngredientSnapshot>();
                    reason = "invalid-ingredient-item-id";
                    detail = "Ingredient item ids must be positive Sons item ids.";
                    return false;
                }

                if (ingredient.Quantity <= 0)
                {
                    snapshots = Array.Empty<OpenACAISonsCraftingIngredientSnapshot>();
                    reason = "invalid-ingredient-quantity";
                    detail = "Ingredient item id " + ingredient.ItemId + " requires a positive quantity.";
                    return false;
                }

                OpenACAISonsItemSnapshot item;
                if (!OpenACAISonsItems.TryFindItem(ingredient.ItemId, out item))
                {
                    snapshots = Array.Empty<OpenACAISonsCraftingIngredientSnapshot>();
                    reason = "unknown-ingredient-item";
                    detail = "Ingredient item id " + ingredient.ItemId + " was not found in the OpenACAI item catalog.";
                    return false;
                }

                rows.Add(new OpenACAISonsCraftingIngredientSnapshot(
                    ingredient.ItemId,
                    ingredient.Quantity,
                    item.DisplayName,
                    item.Type));
            }

            snapshots = rows.ToArray();
            reason = "validated";
            detail = "Ingredients validated.";
            return true;
        }

        private static bool IsSupportedPlanKind(OpenACAISonsCraftingPlanKind kind)
        {
            return kind == OpenACAISonsCraftingPlanKind.CraftResult
                || kind == OpenACAISonsCraftingPlanKind.Recipe
                || kind == OpenACAISonsCraftingPlanKind.IngredientCheck;
        }

        private static bool RequiresResultItem(OpenACAISonsCraftingPlanKind kind)
        {
            return kind == OpenACAISonsCraftingPlanKind.CraftResult
                || kind == OpenACAISonsCraftingPlanKind.Recipe;
        }

        private static bool RequiresResultQuantity(OpenACAISonsCraftingPlanKind kind)
        {
            return kind == OpenACAISonsCraftingPlanKind.CraftResult
                || kind == OpenACAISonsCraftingPlanKind.Recipe;
        }

        private static bool RequiresIngredients(OpenACAISonsCraftingPlanKind kind)
        {
            return kind == OpenACAISonsCraftingPlanKind.Recipe
                || kind == OpenACAISonsCraftingPlanKind.IngredientCheck;
        }

        private static string NormalizeRecipeId(string recipeId, OpenACAISonsCraftingPlanKind kind, int resultItemId)
        {
            if (!string.IsNullOrWhiteSpace(recipeId))
            {
                return recipeId.Trim();
            }

            if (kind == OpenACAISonsCraftingPlanKind.Recipe)
            {
                return "recipe:" + resultItemId;
            }

            if (kind == OpenACAISonsCraftingPlanKind.CraftResult)
            {
                return "result:" + resultItemId;
            }

            return "ingredient-check";
        }

        private static string PlanKindName(OpenACAISonsCraftingPlanKind kind)
        {
            switch (kind)
            {
                case OpenACAISonsCraftingPlanKind.CraftResult:
                    return "craft-result";
                case OpenACAISonsCraftingPlanKind.Recipe:
                    return "recipe";
                case OpenACAISonsCraftingPlanKind.IngredientCheck:
                    return "ingredient-check";
                default:
                    return "unknown";
            }
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsCrafting;
            public string DisplayName => "Sons Crafting";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 722;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons crafting planning and execution gate state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsCrafting.CreateContent();
            }
        }
    }

    public enum OpenACAISonsCraftingPlanKind
    {
        CraftResult = 0,
        Recipe = 1,
        IngredientCheck = 2
    }

    public sealed class OpenACAISonsCraftingIngredient
    {
        public OpenACAISonsCraftingIngredient(int itemId, int quantity = 1)
        {
            ItemId = itemId;
            Quantity = Math.Max(0, quantity);
        }

        public int ItemId { get; }
        public int Quantity { get; }
    }

    public sealed class OpenACAISonsCraftingPlan
    {
        private readonly IReadOnlyList<OpenACAISonsCraftingIngredientSnapshot> _ingredients;

        internal OpenACAISonsCraftingPlan(
            OpenACAISonsCraftingPlanKind planKind,
            string planKindName,
            string recipeId,
            int resultItemId,
            int resultQuantity,
            string resultItemDisplayName,
            string resultItemType,
            IReadOnlyList<OpenACAISonsCraftingIngredientSnapshot> ingredients,
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
            _ingredients = ingredients ?? Array.Empty<OpenACAISonsCraftingIngredientSnapshot>();
            Accepted = accepted;
            CanExecuteNow = canExecuteNow;
            RuntimeExecutionEnabled = runtimeExecutionEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public OpenACAISonsCraftingPlanKind PlanKind { get; }
        public string PlanKindName { get; }
        public string RecipeId { get; }
        public int ResultItemId { get; }
        public int ResultQuantity { get; }
        public string ResultItemDisplayName { get; }
        public string ResultItemType { get; }
        public IReadOnlyList<OpenACAISonsCraftingIngredientSnapshot> Ingredients => _ingredients;
        public int IngredientCount => _ingredients.Count;
        public bool Accepted { get; }
        public bool CanExecuteNow { get; }
        public bool RuntimeExecutionEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }

    public sealed class OpenACAISonsCraftingIngredientSnapshot
    {
        internal OpenACAISonsCraftingIngredientSnapshot(
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

    public sealed class OpenACAISonsCraftingSnapshot
    {
        internal OpenACAISonsCraftingSnapshot(
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
            ServiceKey = serviceKey ?? OpenACAISonsCrafting.ServiceKey;
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

        internal static OpenACAISonsCraftingSnapshot CreateUnknown()
        {
            return new OpenACAISonsCraftingSnapshot(
                OpenACAISonsCrafting.ServiceKey,
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
                OpenACAISonsCrafting.RuntimeExecutionEnabled,
                OpenACAISonsCrafting.SupportedPlanKinds,
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
