// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Crafting;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        private static CraftingExecution _activeCraftExecution;
        private static bool _craftExecutionCallInProgress;

        /// <summary>
        /// Invokes CraftingSystem.TryCombineIngredients once for the local player's existing mat,
        /// just as the native crafting cog does.
        /// </summary>
        /// <remarks>
        /// The game selects the recipe and retains its actual ingredient instances,
        /// reusable/module semantics, animation, outputs
        /// and consumption. Does not stage inventory items, execute a metadata plan, force
        /// completion, collect results, or create a server-authorized crafting transaction.
        /// Requires the initialized Unity thread, an open native inventory, an idle player
        /// and native local-player ownership (offline or the locally owned Bolt player).
        /// Migration-held inventories are refused; use their specialized authority route.
        /// True means the native call accepted, not that crafting finished. Always inspect
        /// execution.State, including after false: an attempted exception is Unconfirmed
        /// and must not be automatically retried. Poll execution.ReadState on the game thread.
        /// </remarks>
        public static bool TryBeginCurrentCraft(out CraftingExecution execution, out string status)
        {
            execution = new CraftingExecution();
            status = "";
            if (!OnCraftExecutionThread)
                return execution.Refuse("Craft execution requires the initialized Unity lifecycle thread.", out status);
            if (_craftExecutionCallInProgress)
                return execution.Refuse("A native crafting call is already in progress.", out status);

            RefreshActiveCraftExecution();
            if (_activeCraftExecution != null)
                return execution.Refuse("A previous craft is still pending. Observe that execution instead of resubmitting.", out status);

            try
            {
                if (!TryGetCraftExecutionContext(out var player, out var inventory, out var system, out status))
                    return execution.Refuse(status, out status);
                if (inventory._currentView != PlayerInventory.PlayerViews.Inventory || !system.isActiveAndEnabled)
                    return execution.Refuse("Open the native local inventory before crafting.", out status);
                if (LocalPlayer.IsInMidAction)
                    return execution.Refuse("The local player is in an action; no craft was attempted.", out status);
                var proxy = system._craftingAnimationEventsProxy;
                if (CraftExecutionLive(proxy) && proxy.CurrentRecipe != null)
                    return execution.Refuse("A native crafting animation is already pending.", out status);

                execution.Bind(player, inventory, system);
                _activeCraftExecution = execution;
                _craftExecutionCallInProgress = true;
                try
                {
                    execution.Invoke();
                }
                finally
                {
                    _craftExecutionCallInProgress = false;
                }
                execution.RefreshNativeState();
                ReleaseFinishedCraftExecution();
                status = execution.State.Detail;
                return execution.State.Accepted;
            }
            catch (Exception error)
            {
                execution.Fail("Craft execution could not be confirmed: " + error.Message);
                ReleaseFinishedCraftExecution();
                status = execution.State.Detail;
                return execution.State.Accepted;
            }
        }

        internal static bool OnCraftExecutionThread => _ownerThreadId != 0
            && Environment.CurrentManagedThreadId == _ownerThreadId;

        internal static bool CraftExecutionLive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;

        private static bool TryGetCraftExecutionContext(out LocalPlayer player, out PlayerInventory inventory,
            out CraftingSystem system, out string detail)
        {
            player = null;
            inventory = null;
            system = null;
            detail = "A live local player and native crafting system are required.";
            if (!GameRuntime.IsGameScene || GameRuntime.Environment.IsDedicatedServer)
                return false;
            player = LocalPlayer._instance;
            inventory = LocalPlayer.Inventory;
            system = CraftingSystem._Instance_k__BackingField;
            if (!CraftExecutionLive(player) || !CraftExecutionLive(inventory)
                || !CraftExecutionLive(system) || !system._isInitialized)
                return false;
            var playerInventory = player._inventory;
            if (!CraftExecutionLive(playerInventory) || playerInventory.Pointer != inventory.Pointer)
                return false;
            if (PlayerInventoryEnforcement.IsEquipmentAuthorityHeld(inventory))
            {
                detail = "The inventory is held by the specialized player authority route; native SDK crafting cannot bypass it.";
                return false;
            }
            if (BoltNetwork.isRunning)
            {
                var entity = LocalPlayer.Entity;
                if (!CraftExecutionLive(entity) || !entity.isAttached || !entity.isOwner)
                {
                    detail = "Crafting requires the locally owned native player, not a remote or migrated player.";
                    return false;
                }
            }
            detail = "";
            return true;
        }

        internal static bool CraftExecutionContextCurrent(LocalPlayer player, PlayerInventory inventory,
            CraftingSystem system, int loadCount, bool networked, IntPtr entityPointer)
        {
            if (!OnCraftExecutionThread || SaveLifecycle.State.BeforeLoadCount != loadCount
                || !CraftExecutionLive(player) || !CraftExecutionLive(inventory) || !CraftExecutionLive(system)
                || !TryGetCraftExecutionContext(out var currentPlayer, out var currentInventory,
                    out var currentSystem, out _)
                || currentPlayer.Pointer != player.Pointer || currentInventory.Pointer != inventory.Pointer
                || currentSystem.Pointer != system.Pointer || BoltNetwork.isRunning != networked)
                return false;
            return !networked || LocalPlayer.Entity.Pointer == entityPointer;
        }

        private static void RefreshActiveCraftExecution()
        {
            if (!OnCraftExecutionThread || _craftExecutionCallInProgress)
                return;
            _activeCraftExecution?.RefreshNativeState();
            ReleaseFinishedCraftExecution();
        }

        private static void ReleaseFinishedCraftExecution()
        {
            if (_activeCraftExecution != null && !_activeCraftExecution.State.Pending)
                _activeCraftExecution = null;
        }
    }

    public enum CraftingExecutionOutcome
    {
        Refused,
        Pending,
        Completed,
        Unconfirmed
    }

    /// <summary>
    /// Immutable observation, not an inventory transaction receipt. Completed means the
    /// native craft completion path was observed through its post-consumption boundary;
    /// outputs may still be on the mat. It does not acknowledge collection or remote delivery.
    /// </summary>
    public sealed class CraftingExecutionSnapshot
    {
        internal CraftingExecutionSnapshot(bool attempted, bool accepted,
            CraftingExecutionOutcome outcome, bool completionEventObserved,
            string recipeName, string detail)
        {
            Attempted = attempted;
            Accepted = accepted;
            Outcome = outcome;
            CompletionEventObserved = completionEventObserved;
            RecipeName = recipeName;
            Detail = detail;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public bool Attempted { get; }
        public bool Accepted { get; }
        public CraftingExecutionOutcome Outcome { get; }
        public bool Pending => Outcome == CraftingExecutionOutcome.Pending;
        public bool Completed => Outcome == CraftingExecutionOutcome.Completed;
        /// <summary>The game's event precedes output creation and consumption, so is not completion proof alone.</summary>
        public bool CompletionEventObserved { get; }
        /// <summary>Native recipe name observed at craft begin, not a caller-selected recipe or durable identity.</summary>
        public string RecipeName { get; }
        public string Detail { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>
    /// One native mat attempt. ReadState and Dispose require the initialized Unity thread.
    /// Dispose stops observation only; it never cancels, refunds or completes native crafting.
    /// Missing callbacks retain Pending; lifetime loss or observation failure is Unconfirmed.
    /// </summary>
    public sealed class CraftingExecution : IDisposable
    {
        private LocalPlayer _player;
        private PlayerInventory _inventory;
        private CraftingSystem _system;
        private CraftingRecipe _recipe;
        private CraftingAnimationEventsProxy _proxy;
        private UnityEvent<CraftingRecipe> _beginEvent, _completeEvent;
        private UnityAction<CraftingRecipe> _beginListener, _completeListener;
        private int _loadCount, _completionFrame;
        private bool _networked, _attempted, _accepted, _returned, _animated, _completionObserved;
        private IntPtr _entityPointer;
        private string _recipeName = "", _observationError = "";
        private volatile CraftingExecutionSnapshot _state = new(false, false,
            CraftingExecutionOutcome.Refused, false, "", "No native crafting call was made.");

        internal CraftingExecution() { }

        /// <summary>Last copied observation; safe to read off-thread. Use ReadState to refresh.</summary>
        public CraftingExecutionSnapshot State => _state;

        public CraftingExecutionSnapshot ReadState()
        {
            RequireThread();
            RefreshNativeState();
            return _state;
        }

        public void Dispose()
        {
            RequireThread();
            RefreshNativeState();
            if (_state.Pending)
                Fail("Observation ended while the native craft was pending. The craft was not cancelled or refunded.");
        }

        internal bool Refuse(string detail, out string status)
        {
            Publish(CraftingExecutionOutcome.Refused, detail);
            status = detail;
            return false;
        }

        internal void Bind(LocalPlayer player, PlayerInventory inventory, CraftingSystem system)
        {
            _player = player;
            _inventory = inventory;
            _system = system;
            _loadCount = SaveLifecycle.State.BeforeLoadCount;
            _networked = BoltNetwork.isRunning;
            _entityPointer = _networked ? LocalPlayer.Entity.Pointer : IntPtr.Zero;
            _beginEvent = system._OnCraftBegin_k__BackingField;
            _completeEvent = system._OnCraftComplete_k__BackingField;
            if (_beginEvent is null || _beginEvent.WasCollected || _completeEvent is null || _completeEvent.WasCollected)
                throw new InvalidOperationException("Native crafting events are unavailable.");
            _beginListener = (UnityAction<CraftingRecipe>)new Action<CraftingRecipe>(ObserveBegin);
            _completeListener = (UnityAction<CraftingRecipe>)new Action<CraftingRecipe>(ObserveComplete);
            _beginEvent.AddListener(_beginListener);
            _completeEvent.AddListener(_completeListener);
        }

        internal void Invoke()
        {
            if (!Current())
                throw new InvalidOperationException("The native crafting context changed before invocation.");
            _attempted = true;
            Publish(CraftingExecutionOutcome.Pending, "Native crafting call is in progress.");
            _accepted = _system.TryCombineIngredients();
            _returned = true;
            if (!_accepted)
            {
                if (_recipe != null || _observationError.Length != 0)
                    Fail("The native call declined after crafting activity was observed; do not retry automatically.");
                else
                    Finish(CraftingExecutionOutcome.Refused,
                        "The native crafting system declined the mat or cooldown. No success is claimed.");
                return;
            }
            Publish(CraftingExecutionOutcome.Pending, "The native craft was accepted; completion is not yet confirmed.");
        }

        private void ObserveBegin(CraftingRecipe recipe)
        {
            try
            {
                if (!_attempted || _returned || _recipe != null || !Current()
                    || !Crafting.CraftExecutionLive(recipe))
                    throw new InvalidOperationException("An unexpected craft begin or changed player lifetime was observed.");
                _recipe = recipe;
                _recipeName = recipe.name ?? "";
                _animated = !string.IsNullOrEmpty(recipe._animationStateName);
                _proxy = _system._craftingAnimationEventsProxy;
                if (_animated && (!Crafting.CraftExecutionLive(_proxy)
                    || _proxy.CurrentRecipe?.Pointer != recipe.Pointer))
                    throw new InvalidOperationException("The native animation proxy does not own the selected recipe.");
            }
            catch (Exception error)
            {
                _observationError = error.Message;
            }
        }

        private void ObserveComplete(CraftingRecipe recipe)
        {
            try
            {
                if (_completionObserved || !Current() || !Crafting.CraftExecutionLive(_recipe)
                    || !Crafting.CraftExecutionLive(recipe) || recipe.Pointer != _recipe.Pointer)
                    throw new InvalidOperationException("An unmatched native completion event was observed.");
                _completionObserved = true;
                _completionFrame = Time.frameCount;
            }
            catch (Exception error)
            {
                _observationError = error.Message;
            }
        }

        internal void RefreshNativeState()
        {
            if (!_state.Pending || !_returned)
                return;
            try
            {
                if (_observationError.Length != 0)
                {
                    Fail(_observationError);
                    return;
                }
                if (!Current())
                {
                    Fail("The native player, inventory, authority or crafting lifetime changed; completion is unknown.");
                    return;
                }
                if (!Crafting.CraftExecutionLive(_recipe))
                {
                    Fail("The accepted craft's native recipe was not observed or is no longer live.");
                    return;
                }
                if (_system._lastCraftedRecipe?.Pointer != _recipe.Pointer)
                {
                    Fail("Another native recipe replaced this craft before completion was confirmed.");
                    return;
                }
                if (!_completionObserved)
                    return;

                // OnCraftComplete is dispatched BEFORE CraftResultingItems and ConsumeIngredientsForRecipe.
                // Nonanimated completion returns within TryCombineIngredients. Animated completion clears
                // the same proxy's CurrentRecipe only at the END of OnCraftCompleteInternal.
                if (_animated)
                {
                    if (!Crafting.CraftExecutionLive(_proxy)
                        || _system._craftingAnimationEventsProxy?.Pointer != _proxy.Pointer)
                    {
                        Fail("The craft animation proxy changed before its completion boundary was observed.");
                        return;
                    }
                    if (Time.frameCount == _completionFrame || _proxy.CurrentRecipe != null)
                    {
                        Publish(CraftingExecutionOutcome.Pending,
                            "The native completion event was observed; the post-consumption boundary is not yet confirmed.");
                        return;
                    }
                }
                Finish(CraftingExecutionOutcome.Completed,
                    "The native craft completion boundary was observed. Outputs follow native mat/module behavior; collection and remote delivery are not acknowledged.");
            }
            catch (Exception error)
            {
                Fail("Craft completion observation failed: " + error.Message);
            }
        }

        private bool Current() => Crafting.CraftExecutionContextCurrent(
            _player, _inventory, _system, _loadCount, _networked, _entityPointer);

        internal void Fail(string detail) => Finish(_attempted
            ? CraftingExecutionOutcome.Unconfirmed : CraftingExecutionOutcome.Refused, detail);

        private void Finish(CraftingExecutionOutcome outcome, string detail)
        {
            Detach(_beginEvent, _beginListener);
            Detach(_completeEvent, _completeListener);
            _beginEvent = null;
            _completeEvent = null;
            _beginListener = null;
            _completeListener = null;
            _player = null;
            _inventory = null;
            _system = null;
            _recipe = null;
            _proxy = null;
            Publish(outcome, detail);
        }

        private static void Detach(UnityEvent<CraftingRecipe> source, UnityAction<CraftingRecipe> listener)
        {
            if (source is null || source.WasCollected || listener is null)
                return;
            try
            {
                source.RemoveListener(listener);
            }
            catch (Exception error)
            {
                global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                    "Craft execution listener cleanup failed: " + error.Message);
            }
        }

        private void Publish(CraftingExecutionOutcome outcome, string detail)
            => _state = new(_attempted, _accepted, outcome, _completionObserved, _recipeName, detail);

        private static void RequireThread()
        {
            if (!Crafting.OnCraftExecutionThread)
                throw new InvalidOperationException("Craft execution observation requires the initialized Unity lifecycle thread.");
        }
    }
}
