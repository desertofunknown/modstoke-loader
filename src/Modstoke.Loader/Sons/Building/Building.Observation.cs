// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting.Structures;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        /// <summary>
        /// Retains observations for one exact currently registered native construction node.
        /// Subscribe before submitting ingredients. No target selection, ingredient or authority
        /// mutation occurs. Dispose on the Unity thread. Replacement worlds detach automatically.
        /// The native completion event runs before built-object creation and even before its client
        /// rejection check; it is never a successful construction receipt. On a server, existing
        /// completion hooks may additionally provide correlated factory/database observations.
        /// This does not correlate a placement request with a future node by recipe/position guesses.
        /// A failed attachment can return a handle when cleanup also fails; retain it for disposal.
        /// </summary>
        public static BuildingActionResult ObserveConstruction(
            StructureCraftingNode node, out ConstructionObservation observation)
        {
            observation = null;
            if (!TryEnterNative(out var rejected)) return rejected;
            bool attempted = false;
            try
            {
                if (!node || node._pendingDestroy || !node._recipe || !TryGetBuildingSystem(out var system)
                    || system._activeStructureNodes == null || !system._activeStructureNodes.Contains(node)
                    || node._structureCraftingSystem != system)
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false,
                        "Pass the exact live node registered with the current native construction system.");
                ulong? networkId = null;
                if (BoltNetwork.isRunning)
                {
                    var entity = node.entity;
                    if (!entity || !entity.isAttached || entity.networkId.PackedValue == 0
                        || BoltNetwork.FindEntity(entity.networkId) != entity)
                        return BuildingResult(BuildingActionStatus.Unavailable, node._recipe._id, false,
                            "The node does not have a current attached network identity.");
                    networkId = entity.networkId.PackedValue;
                }
                var candidate = new ConstructionObservation(node, system, _nativeThread, networkId);
                observation = candidate;
                attempted = true;
                try { candidate.Attach(); }
                catch
                {
                    candidate.Dispose();
                    observation = null;
                    throw;
                }
                return BuildingResult(BuildingActionStatus.Ready, candidate.RecipeId, true,
                    "Exact-node observation attached. Completion events and native results are retained separately, without declaring build or replication success.");
            }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, observation?.RecipeId ?? 0, attempted,
                    exception.Message + (observation != null ? " Retain the handle and retry disposal on the Unity thread." : ""));
            }
            finally { _nativeBusy = false; }
        }
    }

    /// <summary>
    /// Unity-thread-owned subscription with retained managed evidence. No polling/scanning is installed.
    /// Call TryReadProgress for a fresh bounded node read. Retained completion evidence survives disposal;
    /// it neither proves ingredient custody nor save, replication or eventual conversion success.
    /// </summary>
    public sealed partial class ConstructionObservation : IDisposable
    {
        private StructureCraftingNode _node;
        private StructureCraftingSystem _system;
        private readonly StructureRecipe _recipe;
        private readonly int _ownerThread;
        private Il2CppSystem.Action _completed;

        internal ConstructionObservation(StructureCraftingNode node, StructureCraftingSystem system,
            int ownerThread, ulong? networkId)
        {
            _node = node;
            _system = system;
            _recipe = node._recipe;
            _ownerThread = ownerThread;
            RecipeId = _recipe._id;
            BlueprintNetworkId = networkId;
        }

        public int RecipeId { get; }
        public ulong? BlueprintNetworkId { get; }
        public bool IsDisposed { get; private set; }
        public int CompletionEventCount { get; private set; }
        public DateTimeOffset? LastCompletionEventAt { get; private set; }
        /// <summary>Null when no matching installed server hook observation arrived. Never inferred from the native completion event.</summary>
        public BlueprintCompletionObservation LastServerObservation { get; private set; }

        internal void Attach()
        {
            _completed = (Il2CppSystem.Action)new Action(OnNativeCompletion);
            var current = _node.OnStructureComplete;
            _node.OnStructureComplete = current == null ? _completed
                : Il2CppSystem.Delegate.Combine(current, _completed).Cast<Il2CppSystem.Action>();
            ServerConstruction.CompletionObserved += OnServerCompletion;
            Building.CompletionIdentities += CaptureCompletionIdentities;
            GameRuntime.WorldChanged += OnWorldChanged;
        }

        private void OnNativeCompletion()
        {
            // Do not invoke user callbacks or inspect mutable native objects inside this native event.
            if (IsDisposed || System.Environment.CurrentManagedThreadId != _ownerThread) return;
            if (CompletionEventCount < int.MaxValue) CompletionEventCount++;
            LastCompletionEventAt = DateTimeOffset.UtcNow;
        }

        private void OnServerCompletion(BlueprintCompletionObservation observation)
        {
            if (IsDisposed || System.Environment.CurrentManagedThreadId != _ownerThread || !BlueprintNetworkId.HasValue
                || observation.BlueprintNetworkId != BlueprintNetworkId || observation.RecipeId != RecipeId) return;
            try
            {
                if (SameSystem())
                {
                    if (!ReferenceEquals(observation, _identitySource)) ClearCompletionIdentities();
                    LastServerObservation = observation;
                }
            }
            catch { /* An unreadable world cannot establish correlation. */ }
        }

        private bool SameSystem()
            => _system && StructureCraftingSystem.TryGetInstance(out var current) && current == _system;

        private void OnWorldChanged(WorldEvent world)
        {
            if (IsDisposed || System.Environment.CurrentManagedThreadId != _ownerThread) return;
            try { if (!world.IsInWorld || !SameSystem() || !_node) Dispose(); }
            catch { /* The caller can retry disposal on the Unity thread after teardown. */ }
        }

        /// <summary>Reads native ingredient counts without modifying them. Missing/destroyed nodes are not treated as completed.</summary>
        public bool TryReadProgress(out ConstructionProgress progress)
        {
            progress = null;
            if (IsDisposed || System.Environment.CurrentManagedThreadId != _ownerThread) return false;
            try
            {
                if (!SameSystem() || !_node || _node._recipe != _recipe
                    || _system._activeStructureNodes == null || !_system._activeStructureNodes.Contains(_node)
                    || _node._craftingIngredientLinks == null) return false;
                var ingredients = new List<ConstructionIngredientProgress>();
                foreach (var link in _node._craftingIngredientLinks)
                {
                    if (link == null || link.Ingredient == null) return false;
                    ingredients.Add(new(link.Ingredient.ItemId, link.TotalCount, link.AppliedCount));
                }
                progress = new(RecipeId, _node._pendingDestroy, _node.IsStructureComplete(), ingredients.ToArray());
                return true;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            if (System.Environment.CurrentManagedThreadId != _ownerThread)
                throw new InvalidOperationException("Construction observations must be disposed on their Unity owner thread.");
            // Remove only our delegate. Keep the handle retryable if native teardown prevents removal.
            if (_node && _completed != null)
            {
                var current = _node.OnStructureComplete;
                _node.OnStructureComplete = current == null ? null
                    : Il2CppSystem.Delegate.Remove(current, _completed)?.Cast<Il2CppSystem.Action>();
            }
            ServerConstruction.CompletionObserved -= OnServerCompletion;
            Building.CompletionIdentities -= CaptureCompletionIdentities;
            GameRuntime.WorldChanged -= OnWorldChanged;
            _completed = null;
            _node = null;
            _system = null;
            ClearCompletionIdentities();
            IsDisposed = true;
        }
    }

    public sealed class ConstructionProgress
    {
        internal ConstructionProgress(int recipeId, bool pendingDestroy, bool ingredientsComplete,
            ConstructionIngredientProgress[] ingredients)
        { RecipeId = recipeId; PendingDestroy = pendingDestroy; NativeIngredientsComplete = ingredientsComplete; Ingredients = Array.AsReadOnly(ingredients); ObservedAt = DateTimeOffset.UtcNow; }
        public int RecipeId { get; }
        public bool PendingDestroy { get; }
        public bool NativeIngredientsComplete { get; }
        public IReadOnlyList<ConstructionIngredientProgress> Ingredients { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    public readonly struct ConstructionIngredientProgress
    {
        internal ConstructionIngredientProgress(int itemId, int totalCount, int appliedCount)
        { ItemId = itemId; TotalCount = totalCount; AppliedCount = appliedCount; }
        public int ItemId { get; }
        public int TotalCount { get; }
        public int AppliedCount { get; }
    }
}
