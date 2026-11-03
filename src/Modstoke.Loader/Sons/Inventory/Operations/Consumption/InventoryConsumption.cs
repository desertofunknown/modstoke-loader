// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Environment;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.StatSystem;
using UnityEngine;
using Environment = System.Environment;

namespace Modstoke.Sons
{
    public readonly record struct ConsumptionReceipt(Guid MigrationId, ulong Generation,
        ulong NetworkId, string BaselineFingerprint, ulong Transaction, ulong InstanceKey, int ItemId,
        int BeforeQuantity, int AfterQuantity, bool UsesVolume, float BeforeVolume, float AfterVolume,
        int BeforeVolumeRecipeId, int AfterVolumeRecipeId, string BeforeModules, string AfterModules,
        ConsumptionVitals BeforeVitals, ConsumptionVitals AfterVitals);

    /// <summary>
    /// Actor-specific server effects for reserved inventory intake. The inventory endpoint routes
    /// supported effects through native debit, numeric projection and authenticated ack.
    /// The aggregate must bind the explicitly approved, initialized actor after activation.
    /// Native PlayerStats/Vitals writers must already be disabled by the aggregate owner, and
    /// inventory Ready must exclude HasNativeConsumptionReservation before this helper is used.
    /// This is bounded consumption support, not full vanilla inventory coverage.
    /// </summary>
    public sealed partial class InventoryConsumption : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerInventoryAuthority _authority;
        private readonly PlayerStats _stats;
        private readonly Vitals _vitals;
        private readonly Stat _health, _sickness, _stamina, _vitality, _hydration, _fullness, _rested;
        private StatRecipe _cookingRecipe;
        private Effect[] _cookingEffects = Array.Empty<Effect>();
        private bool _busy;
        private int _cookingFrame = -1;
        private PlayerSurvivalRuntime _survivalCooking;
        private ConsumptionPlan _preparedIntake;
        private int _preparedIntakeId;
        private ulong _preparedTransaction, _preparedKey;
        private ConsumptionDebit _preparedDebit;

        public bool IsDisposed { get; private set; }
        public bool IsFaulted { get; private set; }
        public bool HasPendingAcknowledgement { get; private set; }
        public ConsumptionReceipt LastCommittedReceipt { get; private set; }
        public string Detail { get; private set; } = "Actor consumption bound; no intake is authorized by binding.";

        private InventoryConsumption(PlayerBridge bridge,
            PlayerInventoryAuthority authority, PlayerStats stats, Vitals vitals)
        {
            _bridge = bridge; _authority = authority; _stats = stats; _vitals = vitals;
            _health = vitals._health; _sickness = vitals._sickness; _stamina = vitals._stamina;
            _vitality = vitals._vitality; _hydration = vitals._hydration;
            _fullness = vitals._fullness; _rested = vitals._rested;
            _cookingRecipe = vitals._activeCookingBuffRecipe;
        }

        /// <summary>
        /// Does not create stats or disable native writers. Pass only the independently restored
        /// server actor's initialized components, with no competing StrictCombat health owner.
        /// Existing cooking state must be part of the aggregate's explicitly accepted baseline.
        /// </summary>
        public static bool TryCreateServer(PlayerBridge bridge,
            PlayerInventoryAuthority authority, PlayerStats stats, Vitals vitals,
            out InventoryConsumption consumption, out string detail)
            => TryCreateServer(bridge, authority, stats, vitals, false, out consumption, out detail);

        internal static bool TryPrepareServer(PlayerBridge bridge,
            PlayerInventoryAuthority authority, PlayerStats stats, Vitals vitals,
            out InventoryConsumption consumption, out string detail)
            => TryCreateServer(bridge, authority, stats, vitals, true, out consumption, out detail);

        private static bool TryCreateServer(PlayerBridge bridge,
            PlayerInventoryAuthority authority, PlayerStats stats, Vitals vitals, bool prepared,
            out InventoryConsumption consumption, out string detail)
        {
            consumption = null;
            detail = "A current server bridge, enforced account and initialized actor-local disabled stat writers are required.";
            if (bridge == null || !bridge.IsServer || authority == null || stats == null || vitals == null
                || stats.WasCollected || vitals.WasCollected || !stats || !vitals) return false;
            try
            {
                var candidate = new InventoryConsumption(bridge, authority, stats, vitals);
                if (!candidate.ActorCurrent(prepared)) return false;
                if (candidate._cookingRecipe is not null)
                {
                    candidate._cookingEffects = ReadEffects(candidate._cookingRecipe._statEffects);
                    ValidateExpiry(candidate._cookingEffects);
                }
                candidate.ReadVitals(prepared);
                if (!authority.BindConsumption(candidate, bridge, prepared)) return false;
                bridge.Retired += candidate.Dispose;
                consumption = candidate;
                detail = candidate.Detail;
                return true;
            }
            catch (Exception error) { detail = "Consumption binding refused: " + error.Message; return false; }
        }

        /// <summary>
        /// Direct effects cannot prove retained native custody and remain refused. Supported
        /// plain intake enters through the authenticated inventory endpoint instead.
        /// </summary>
        public bool TryCommit(ulong transaction, ulong instanceKey, float volumeToConsume,
            out ConsumptionReceipt receipt, out string detail)
        {
            receipt = default;
            // Direct server commit cannot prove retained-client custody. The connected native
            // inventory request path below reserves first and waits for the exact removal receipt.
            detail = "Use the authenticated native inventory consumption route; direct effects without a custody receipt are refused.";
            return false;
        }

        internal bool TryPrepareIntake(ulong transaction, ulong key, int id, int quantity, int heldSlot,
            ItemInstance item, float approvedPortion, PerishableItemInstanceModule authoritativePerishable,
            out ConsumptionDebit debit, out ConsumptionNumbers before,
            out ConsumptionNumbers after, out string detail)
        {
            debit = default;
            before = after = default;
            detail = "Consumption requires exact server custody, approved effects and a current bound actor.";
            if (!OnThread || _busy || IsDisposed || IsFaulted || HasPendingAcknowledgement || _preparedIntake != null
                || _survivalCooking?.BlocksPlayerLifeActions == true || _survivalCooking?.MustDeferStatWriters == true
                || transaction == 0 || key == 0 || quantity <= 0 || (item != null && quantity != 1)) return false;
            _busy = true;
            try
            {
                var data = ItemDatabaseManager.ItemById(id);
                if (!ActorCurrent() || (item == null && !SupportsPlainIntake(data)) || !TryReadIntake(_stats, out before)) return false;
                var plan = Plan(data, item, approvedPortion, authoritativePerishable);
                after = ConsumptionNumbers.From(plan.After);
                if (before != ConsumptionNumbers.From(plan.Before) || !CanProjectIntake(_stats, before, after)) return false;
                debit = new(quantity, plan.Volume == null ? quantity - 1 : quantity, heldSlot,
                    plan.Volume != null, approvedPortion, plan.VolumeMaximum, plan.VolumeBefore, plan.VolumeAfter,
                    plan.VolumeRecipeBefore == null ? -1 : plan.VolumeRecipeBefore.Id,
                    plan.VolumeRecipeAfter == null ? -1 : plan.VolumeRecipeAfter.Id, plan.PerishableState,
                    plan.ModulesBefore == null ? null : PlayerInventoryWire.HashModules(plan.ModulesBefore));
                if (!debit.IsValid) return false;
                _preparedIntakeId = id;
                _preparedTransaction = transaction;
                _preparedKey = key;
                _preparedDebit = debit;
                _preparedIntake = plan;
                detail = "Exact server definition/module effects and native debit are reserved; nothing has been applied.";
                return true;
            }
            catch (Exception error) { detail = "Consumption refused: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal void CancelPreparedIntake()
        { if (OnThread && !_busy && !HasPendingAcknowledgement && !IsFaulted) _preparedIntake = null; }

        internal bool CommitPreparedIntake(ulong transaction, ulong key, int id, int quantity, out string detail)
        {
            detail = "The exact reserved intake no longer matches its native debit receipt.";
            if (!OnThread || _busy || IsDisposed || IsFaulted || HasPendingAcknowledgement || _preparedIntake == null
                || id != _preparedIntakeId || transaction != _preparedTransaction || key != _preparedKey
                || quantity != _preparedDebit.BeforeQuantity) return false;
            _busy = true;
            bool writing = false;
            try
            {
                if (!ActorCurrent() || !TryReadIntake(_stats, out var before)) return false;
                var currentPlan = _preparedIntake;
                if (!SourceCurrent(currentPlan)) return false;
                RebaseEffects(currentPlan);
                var after = ConsumptionNumbers.From(currentPlan.After);
                if (before != ConsumptionNumbers.From(currentPlan.Before)
                    || !CanProjectIntake(_stats, before, after)) return false;
                writing = true;
                // Retire the prepared attempt before calling any native writer. Partial writes
                // keep custody held and fault the owner; an uncertain effect is never replayed.
                _preparedIntake = null;
                WriteVitals(currentPlan.After, currentPlan.CookingRecipe, currentPlan.CookingEffects);
                if (currentPlan.Volume != null)
                {
                    currentPlan.Volume._CurrentVolume_k__BackingField = currentPlan.VolumeAfter;
                    currentPlan.Volume._statRecipe = currentPlan.VolumeRecipeAfter;
                    Require(currentPlan.Volume._CurrentVolume_k__BackingField == currentPlan.VolumeAfter
                        && SameRecipe(currentPlan.VolumeRecipeAfter, currentPlan.Volume._statRecipe), "Server volume debit readback failed.");
                }
                Require(ReadVitals() == currentPlan.After, "Server numeric effects readback failed.");
                string afterModules = currentPlan.Item == null ? null : PlayerInventoryBaseline.CaptureModules(currentPlan.Item);
                LastCommittedReceipt = new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    _authority.AcceptedBaselineFingerprint, transaction, key, id, quantity, _preparedDebit.AfterQuantity,
                    _preparedDebit.UsesVolume, _preparedDebit.BeforeVolume, _preparedDebit.AfterVolume,
                    _preparedDebit.BeforeRecipeId, _preparedDebit.AfterRecipeId, currentPlan.ModulesBefore,
                    afterModules, currentPlan.Before, currentPlan.After);
                HasPendingAcknowledgement = true;
                Detail = detail = "Native source debit confirmed; reserved server effects applied once, client projection pending.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Reserved intake is unconfirmed: " + error.Message;
                if (writing) Fail(detail);
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Call only from authenticated delivery for this exact peer after native client custody
        /// and stat projection succeeded. Ack comparison does not authorize or apply effects.
        /// A duplicate ack is harmless; different, stale and cross-player receipts are rejected.
        /// </summary>
        public bool TryAcknowledge(ControlPeer authenticatedPeer,
            in ConsumptionReceipt receipt, out string detail)
        {
            detail = "No exact committed consumption matches this authenticated acknowledgement.";
            if (!OnThread || _busy || IsDisposed || IsFaulted || !ReferenceEquals(authenticatedPeer, _bridge.Peer)
                || receipt.Transaction == 0 || receipt != LastCommittedReceipt) return false;
            _busy = true;
            try
            {
                // The receipt proves this commit, not that subsequent authorized stamina or
                // survival writes have stopped while the source finishes its animation.
                if (!ActorCurrent() || !TryReadIntake(_stats, out _)
                    || !_authority.AcknowledgeConsumption(this)) return false;
                HasPendingAcknowledgement = false;
                _preparedIntake = null;
                Detail = detail = "Server consumption acknowledged; no effects were reapplied.";
                return true;
            }
            catch (Exception error) { Fail("Consumption acknowledgement became uncertain: " + error.Message); detail = Detail; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Call once per rendered game frame from the aggregate's authoritative survival update,
        /// never in addition to native Vitals.Update/UpdateCookingBuffs. Uses native game-time
        /// delta and actor-specific linked-effect expiry. Project returned state and expiry UI
        /// to this player's client; no host HUD or LocalPlayer dispatcher is invoked.
        /// </summary>
        public bool TryAdvanceCooking(out ConsumptionVitals state,
            out int expiredRecipeId, out string detail)
            => AdvanceCooking(null, null, out state, out expiredRecipeId, out detail);

        internal bool TryBindSurvivalCooking(PlayerSurvivalRuntime runtime, out string detail)
        {
            detail = "Cooking requires one exact prepared survival clock on the same actor and inventory authority.";
            if (!OnThread || _busy || runtime == null || _survivalCooking != null
                || _cookingFrame != -1 || !ActorCurrent() || !_authority.ConsumptionCurrent(this)
                || !runtime.OwnsCookingActor(_bridge, _authority, _stats)) return false;
            _survivalCooking = runtime;
            return true;
        }

        internal bool TryAdvanceSurvivalCooking(PlayerSurvivalRuntime runtime, float elapsed,
            out ConsumptionVitals state, out int expiredRecipeId, out string detail)
            => AdvanceCooking(runtime, elapsed, out state, out expiredRecipeId, out detail);

        private bool AdvanceCooking(PlayerSurvivalRuntime runtime, float? retainedElapsed,
            out ConsumptionVitals state, out int expiredRecipeId, out string detail)
        {
            state = default; expiredRecipeId = -1;
            detail = "Cooking requires a current actor and one authoritative update per frame.";
            if (!OnThread || _busy || IsDisposed || IsFaulted || _authority.HasNativeConsumptionReservation
                || _preparedIntake != null || HasPendingAcknowledgement) return false;
            bool retained = retainedElapsed.HasValue;
            if (retained ? runtime == null || !ReferenceEquals(runtime, _survivalCooking) || !runtime.IsActive
                    || !runtime.OwnsCookingActor(_bridge, _authority, _stats)
                : _survivalCooking != null || _cookingFrame == Time.frameCount) return false;
            _busy = true;
            bool writing = false;
            try
            {
                if (!ActorCurrent(cookingRuntime: runtime) || !_authority.ConsumptionCurrent(this)) return false;
                float elapsed = retainedElapsed ?? (float)TimeOfDayHolder.GetTimeOfDayDelta();
                Require(float.IsFinite(elapsed) && elapsed >= 0, "The game-time delta is unavailable.");
                state = ReadVitals(cookingRuntime: runtime);
                if (_cookingRecipe is null) { _cookingFrame = Time.frameCount; detail = "No active cooking recipe."; return true; }
                var recipe = _cookingRecipe;
                var effects = _cookingEffects;
                state = state with { CookingSeconds = state.CookingSeconds - elapsed };
                if (state.CookingSeconds <= 0)
                {
                    state = Apply(state, effects, false, true) with { CookingRecipeId = -1 };
                    expiredRecipeId = recipe.Id;
                    recipe = null;
                    effects = Array.Empty<Effect>();
                }
                Require(ActorCurrent(cookingRuntime: runtime) && Finite(state), "Actor identity or cooking state changed before expiry commit.");
                writing = true;
                _cookingFrame = Time.frameCount;
                WriteVitals(state, recipe, effects);
                Require(ReadVitals(cookingRuntime: runtime) == state, "Actor cooking readback did not match its commit.");
                detail = expiredRecipeId == -1 ? "Actor cooking time advanced." : "Actor cooking effects expired; client presentation is pending.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Cooking progression failed: " + error.Message;
                // An unserviceable active recipe cannot fall back to native global dispatch.
                if (writing || _cookingRecipe != null) Fail(detail);
                state = default; expiredRecipeId = -1;
                return false;
            }
            finally { _busy = false; }
        }

        private bool OnThread => Environment.CurrentManagedThreadId == _thread;

        private bool ActorCurrent(bool allowPrepared = false, PlayerSurvivalRuntime cookingRuntime = null)
        {
            if (!OnThread || IsDisposed || IsFaulted || _authority.IsDisposed || _authority.IsFaulted
                || !_bridge.IsServer || (!allowPrepared && !_bridge.IsGameplayActive) || !_bridge.TryReadCurrentTarget(out var actor)
                || _stats == null || _stats.WasCollected || !_stats || _stats.enabled
                || _vitals == null || _vitals.WasCollected || !_vitals || _vitals.enabled
                || _stats._vitals == null || _stats._vitals.Pointer != _vitals.Pointer
                || !_stats.transform.IsChildOf(actor.transform) || !_vitals.transform.IsChildOf(actor.transform)
                || Combat.StrictCombatLifeHooks.HasConsumptionHealthOwner(_vitals)) return false;
            return SameStat(_health, _vitals._health) && SameStat(_sickness, _vitals._sickness)
                && SameStat(_stamina, _vitals._stamina) && SameStat(_vitality, _vitals._vitality)
                && SameStat(_hydration, _vitals._hydration) && SameStat(_fullness, _vitals._fullness)
                && SameStat(_rested, _vitals._rested)
                && (cookingRuntime == null ? !_vitals._hasDied && _health._currentValue > 0
                    : _busy && ReferenceEquals(cookingRuntime, _survivalCooking) && cookingRuntime.OwnsCookingNumericStep(this))
                && SameRecipe(_cookingRecipe, _vitals._activeCookingBuffRecipe);
        }

        private static bool SameStat(Stat expected, Stat actual)
            => expected != null && !expected.WasCollected && actual != null && !actual.WasCollected
                && expected.Pointer == actual.Pointer && float.IsFinite(expected._currentValue)
                && float.IsFinite(expected._min) && float.IsFinite(expected._max)
                && expected._max > 0 && expected._max > expected._min;

        private static bool SameRecipe(StatRecipe expected, StatRecipe actual)
            => expected is null ? actual is null : !expected.WasCollected && expected && actual is not null
                && !actual.WasCollected && actual && expected.Pointer == actual.Pointer;

        internal void Fail(string detail)
        {
            IsFaulted = true;
            Detail = detail;
            _authority.FaultConsumption(this, detail);
        }

        public void Dispose()
        {
            if (!OnThread) throw new InvalidOperationException("Dispose consumption on the game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            _survivalCooking = null;
            _bridge.Retired -= Dispose;
            // No speculative refund or guard release after a commit, even on disconnect.
            _authority.FaultConsumption(this, "Consumption owner retired; aggregate recovery is required.");
        }

        internal bool CommitAccount(ulong transaction, ulong key, int id, int quantity,
            global::Sons.Inventory.ItemInstance item, ItemData data, float portion,
            global::Sons.Inventory.PerishableItemInstanceModule authoritativePerishable,
            Action<bool> commitDebit, out ConsumptionReceipt receipt, out string detail)
        {
            receipt = default;
            bool writing = false;
            try
            {
                var plan = Plan(data, item, portion, authoritativePerishable);
                string modules = item == null ? null : PlayerInventoryBaseline.CaptureModules(item);
                Require(ActorCurrent() && ReadVitals() == plan.Before, "Actor changed during consumption preflight.");
                writing = true;
                // The transaction high-water mark and debit precede direct actor writes. Any
                // exception after this point faults custody, never permitting automatic replay.
                commitDebit(plan.Volume == null);
                WriteVitals(plan.After, plan.CookingRecipe, plan.CookingEffects);
                if (plan.Volume != null)
                {
                    plan.Volume._CurrentVolume_k__BackingField = plan.VolumeAfter;
                    plan.Volume._statRecipe = plan.VolumeRecipeAfter;
                    Require(plan.Volume._CurrentVolume_k__BackingField == plan.VolumeAfter
                        && SameRecipe(plan.VolumeRecipeAfter, plan.Volume._statRecipe), "Volume debit readback failed.");
                }
                Require(ReadVitals() == plan.After, "Actor effects readback failed.");
                string afterModules = item == null ? null : PlayerInventoryBaseline.CaptureModules(item);
                receipt = new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    _authority.AcceptedBaselineFingerprint, transaction, key, id, quantity,
                    plan.Volume == null ? quantity - 1 : quantity, plan.Volume != null, plan.VolumeBefore, plan.VolumeAfter,
                    plan.VolumeRecipeBefore == null ? -1 : plan.VolumeRecipeBefore.Id,
                    plan.VolumeRecipeAfter == null ? -1 : plan.VolumeRecipeAfter.Id,
                    modules, afterModules, plan.Before, plan.After);
                LastCommittedReceipt = receipt;
                HasPendingAcknowledgement = true;
                Detail = detail = "Server custody and actor effects committed once; authenticated client projection is pending.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Consumption " + (writing ? "commit is uncertain; do not retry: " : "preflight refused: ") + error.Message;
                if (writing) Fail(detail);
                return false;
            }
        }
    }

    public sealed partial class PlayerInventoryAuthority
    {
        private InventoryConsumption _consumptionOwner;
        private bool _consumptionHoldsAccount;
        private PendingPlainConsumption _plainConsumption;

        private sealed class PendingPlainConsumption(PlayerConsumptionFrame frame, ConsumptionDebit debit,
            ItemRecord item, EquipmentState equipment)
        {
            internal readonly PlayerConsumptionFrame Frame = frame;
            internal readonly ConsumptionDebit Debit = debit;
            internal readonly ItemRecord Item = item;
            internal readonly EquipmentState Equipment = equipment;
            internal bool Committed;
            internal PlayerConsumptionFrame Projection;
        }

        internal bool TryReservePlainConsumption(in PlayerConsumptionFrame request,
            out PlayerConsumptionFrame prepared, out string detail)
        {
            prepared = default;
            detail = "Plain intake requires an exact stored nonunique inventory entry.";
            if (Environment.CurrentManagedThreadId != _thread || !_items.TryGetValue(request.Action.Key, out var item)
                || item.Item != null || item.HeldSlot >= 0) return false;
            return TryReserveConsumption(in request, 0, out prepared, out _, out detail);
        }

        // The number of uses comes from the server-approved InventoryEdibleItem definition,
        // never client action data. Non-volume intake passes zero. Every reply carries the debit.
        internal bool TryReserveConsumption(in PlayerConsumptionFrame request, int approvedNumberOfUses,
            out PlayerConsumptionFrame prepared, out ConsumptionDebit debit, out string detail)
        {
            prepared = default;
            debit = default;
            detail = "No active, initialized consumption owner or exact supported inventory entry is available.";
            if (_consumptionOwner == null || !Ready(out var target) || _pending != null || _stow != null || _equip != null
                || MutationEnforcement?.CanQueue != true || _lastTransaction == ulong.MaxValue
                || request.Action.Kind != PlayerInventoryMessage.ConsumeRequest || request.Request == 0
                || request.Action.MigrationId != _bridge.Plan.Id || request.Action.Generation != _bridge.Plan.Generation
                || request.Action.NetworkId != _bridge.NetworkId || request.Action.Fingerprint != AcceptedBaselineFingerprint
                || request.Action.Quantity != 1
                || !_items.TryGetValue(request.Action.Key, out var item) || item.Consumed || item.DefinitionOnly
                || item.Clothing || item.ArmourSlot >= 0 || (item.Item != null && item.Quantity != 1)
                || item.Id != request.Action.ItemId || item.Quantity != request.Action.BeforeQuantity
                || item.Quantity < 1 || !item.IdentityCurrent()) return false;
            _busy = true;
            try
            {
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (equipment == null || !OwnedEquipmentMatches(equipment)) return false;
                var data = item.Item == null ? ItemDatabaseManager.ItemById(item.Id) : item.Item.Data;
                float approvedVolumePortion = 0;
                if (data != null && data._isVolumeContainer)
                {
                    if (!InventoryConsumption.TrySelectVolumePortion(item.Item, approvedNumberOfUses,
                        out approvedVolumePortion))
                    { detail = "Volume intake requires the server-approved native definition's number of uses."; return false; }
                }
                else if (approvedNumberOfUses != 0) return false;
                // Drinking from a held volume container retains the same exact hand. Eating a
                // held item removes equipment and needs the equipment owner's receipt path.
                if (item.HeldSlot >= 0 && (item.Item == null || data == null || !data._isVolumeContainer
                    || !HandSlot(item.HeldSlot) || Count(equipment.Held, item.Id) != 1))
                { detail = "Held-item removal needs the equipment publisher's consumption receipt."; return false; }
                PerishableItemInstanceModule perishable = null;
                if (item.Item != null && PlayerInventoryEnforcement.FindPerishable(item.Item) != null)
                {
                    if (!_decay.TryGetValue(item.Key, out var clock) || !clock.Item.IdentityCurrent()
                        || clock.Item.Id != item.Id || clock.Item.Key != item.Key)
                    { detail = "Consumption needs the exact advanced server perishable clock."; return false; }
                    perishable = clock.Module;
                }
                ulong transaction = _lastTransaction + 1;
                if (!_consumptionOwner.TryPrepareIntake(transaction, item.Key, item.Id, item.Quantity, item.HeldSlot,
                    item.Item, approvedVolumePortion, perishable, out debit, out _, out var after, out detail)) return false;
                if (!Current(out var current) || current.Pointer != target.Pointer)
                { _consumptionOwner.CancelPreparedIntake(); detail = "Consumption lost its exact server actor."; return false; }
                prepared = new(new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    AcceptedBaselineFingerprint, transaction, item.Key, item.Id, 1, item.Quantity,
                    PlayerInventoryMessage.ConsumePrepare), request.Request, request.Before, after, debit);
                _plainConsumption = new(prepared, debit, item, equipment);
                _consumptionHoldsAccount = true;
                _lastTransaction = transaction;
                Detail = detail = "Native intake reserved; server effects await the exact approved client debit.";
                return true;
            }
            catch (Exception error)
            {
                if (_plainConsumption == null) _consumptionOwner.CancelPreparedIntake();
                else Fault("Consumption reservation became uncertain.");
                detail = "Consumption reservation refused: " + error.Message;
                return false;
            }
            finally { _busy = false; }
        }

        internal bool CommitPlainConsumption(in PlayerConsumptionFrame removed, out string detail)
        {
            detail = "A module or held intake needs its explicit native debit receipt.";
            var pending = _plainConsumption;
            if (pending == null || pending.Item.Item != null || pending.Debit.HeldSlot >= 0) return false;
            return CommitConsumptionDebit(in removed, pending.Debit, out _, out detail);
        }

        internal bool CommitConsumptionDebit(in PlayerConsumptionFrame removed, in ConsumptionDebit debit,
            out PlayerConsumptionFrame projection, out string detail)
        {
            projection = default;
            detail = "No exact consumption reservation matches the native debit receipt.";
            var pending = _plainConsumption;
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !_bridge.IsGameplayActive
                || pending == null || pending.Committed || !Current(out var target)
                || debit != pending.Debit
                || removed != pending.Frame.WithKind(PlayerInventoryMessage.ConsumeRemoved)) return false;
            _busy = true;
            try
            {
                var item = pending.Item;
                if (!item.IdentityCurrent() || item.Consumed || item.Quantity != removed.Action.BeforeQuantity || item.HeldSlot != debit.HeldSlot
                    || !pending.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
                // Reserve the effect attempt before calling anything native. A failure after the
                // source debit is held uncertain, never a second effect attempt or a refund.
                pending.Committed = true;
                if (!_consumptionOwner.CommitPreparedIntake(removed.Action.Transaction, item.Key, item.Id, item.Quantity, out detail))
                { _consumptionOwner.Fail(detail); return false; }
                item.Quantity = debit.AfterQuantity;
                item.Consumed = item.Quantity == 0;
                ReleaseConsumedModules(item);
                pending.Projection = pending.Frame.WithKind(PlayerInventoryMessage.ConsumeProject) with
                { After = ConsumptionNumbers.From(_consumptionOwner.LastCommittedReceipt.AfterVitals) };
                projection = pending.Projection;
                return true;
            }
            catch (Exception error)
            {
                detail = "Consumption effect/custody commit is uncertain: " + error.Message;
                if (pending.Committed) _consumptionOwner.Fail(detail);
                return false;
            }
            finally { _busy = false; }
        }

        internal bool AcknowledgePlainConsumption(in PlayerConsumptionFrame applied, out string detail)
        {
            detail = "A module or held intake needs its explicit native projection receipt.";
            var pending = _plainConsumption;
            if (pending == null || pending.Item.Item != null || pending.Debit.HeldSlot >= 0) return false;
            return AcknowledgeConsumptionProjection(in applied, pending.Debit, out detail);
        }

        internal bool AcknowledgeConsumptionProjection(in PlayerConsumptionFrame applied, in ConsumptionDebit debit, out string detail)
        {
            detail = "No committed consumption matches the authenticated numeric readback.";
            var pending = _plainConsumption;
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !_bridge.IsGameplayActive
                || pending?.Committed != true || !Current(out var target)
                || debit != pending.Debit
                || applied != pending.Projection.WithKind(PlayerInventoryMessage.ConsumeApplied)
                || pending.Item.Quantity != debit.AfterQuantity || pending.Item.HeldSlot != debit.HeldSlot
                || !pending.Item.IdentityCurrent()
                || !pending.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
            var receipt = _consumptionOwner.LastCommittedReceipt;
            if (pending.Item.Item != null && (receipt.AfterModules == null
                || !PlayerInventoryBaseline.JsonEqual(receipt.AfterModules,
                    PlayerInventoryBaseline.CaptureModules(pending.Item.Item)))) return false;
            if (debit.UsesVolume && !InventoryConsumption.ConfirmsVolumeDebit(pending.Item.Item, in debit)) return false;
            if (!_consumptionOwner.TryAcknowledge(_bridge.Peer, receipt, out detail)) return false;
            _plainConsumption = null;
            return true;
        }

        internal bool RejectPlainConsumption(in PlayerConsumptionFrame rejected)
        {
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !_bridge.IsGameplayActive || !Current(out _)
                || _plainConsumption == null || _plainConsumption.Committed
                || rejected != _plainConsumption.Frame.WithKind(PlayerInventoryMessage.ConsumeRejected)) return false;
            _consumptionOwner.CancelPreparedIntake();
            _plainConsumption = null;
            _consumptionHoldsAccount = false;
            return true;
        }

        // Native decay clocks may advance while projection waits, but custody and passive
        // publication stay reserved until the exact client acknowledgement.
        internal bool HasNativeConsumptionReservation => _consumptionHoldsAccount;

        internal bool BindConsumption(InventoryConsumption owner, PlayerBridge bridge, bool prepared = false)
        {
            if (owner == null || _consumptionOwner != null || !ReferenceEquals(_bridge, bridge)
                || !(prepared ? PreparedReady(out _) : Ready(out _))
                || _pending != null || !HasAcceptedBaseline || MutationEnforcement == null
                || MutationEnforcement.IsDisposed || MutationEnforcement.IsFaulted || !MutationEnforcement.IsArmed
                || MutationEnforcement.HasPendingCustody || (!prepared && !MutationEnforcement.CanQueue)) return false;
            _consumptionOwner = owner;
            return true;
        }

        internal bool ConsumptionCurrent(InventoryConsumption owner)
            => Environment.CurrentManagedThreadId == _thread && ReferenceEquals(_consumptionOwner, owner)
                && !_busy && !_suspended && Current(out _);

        internal bool CommitConsumption(InventoryConsumption owner, ulong transaction, ulong key,
            float portion, out ConsumptionReceipt receipt, out string detail)
        {
            receipt = default;
            detail = "Consumption requires a fresh exact stowed record and idle enforced account.";
            if (!ReferenceEquals(_consumptionOwner, owner) || !Ready(out var target) || _pending != null
                || !Fresh(transaction) || !MutationEnforcement.CanQueue || !_items.TryGetValue(key, out var item)
                || item.Consumed || item.DefinitionOnly || item.Quantity <= 0 || item.HeldSlot >= 0
                || item.Clothing || item.ArmourSlot >= 0 || (item.Item != null && item.Quantity != 1)) return false;
            _busy = true;
            try
            {
                if (!item.IdentityCurrent() || !OwnedEquipmentMatches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
                var data = item.Item == null ? ItemDatabaseManager.ItemById(item.Id) : item.Item.Data;
                var perishable = PlayerInventoryEnforcement.FindPerishable(item.Item);
                if (perishable != null)
                {
                    if (!_decay.TryGetValue(key, out var clock) || !clock.Item.IdentityCurrent()
                        || clock.Item.Id != item.Id || clock.Item.Key != key)
                    {
                        detail = "Advance the exact server perishable clock before authorizing intake.";
                        return false;
                    }
                    perishable = clock.Module;
                }
                if (!Current(out var current) || current.Pointer != target.Pointer) return false;
                bool committed = owner.CommitAccount(transaction, key, item.Id, item.Quantity, item.Item, data, portion,
                    perishable,
                    consumeItem =>
                    {
                        if (!item.IdentityCurrent() || !Current(out var exact) || exact.Pointer != target.Pointer)
                            throw new InvalidOperationException("Account identity changed at the consumption boundary.");
                        _lastTransaction = transaction;
                        if (!consumeItem) return;
                        item.Quantity--;
                        item.Consumed = item.Quantity == 0;
                    }, out receipt, out detail);
                if (!committed) return false;
                ReleaseConsumedModules(item);
                _consumptionHoldsAccount = true;
                Detail = detail;
                return true;
            }
            catch (Exception error)
            {
                owner.Fail("Consumption custody cleanup is uncertain; do not retry: " + error.Message);
                detail = Detail;
                return false;
            }
            finally
            {
                _busy = false;
            }
        }

        internal bool AcknowledgeConsumption(InventoryConsumption owner)
        {
            if (!ConsumptionCurrent(owner)) return false;
            if (!_consumptionHoldsAccount) return !owner.HasPendingAcknowledgement;
            _consumptionHoldsAccount = false;
            return true;
        }

        internal void FaultConsumption(InventoryConsumption owner, string detail)
        {
            if (!ReferenceEquals(_consumptionOwner, owner)) return;
            Fault(detail);
            // Keep a pending account hold: failure is not an acknowledgement or rollback.
        }
    }
}
