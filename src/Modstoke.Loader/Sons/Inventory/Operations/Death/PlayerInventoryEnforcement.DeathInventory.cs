// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;
using TheForest.Player.Actions;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private InventoryLifecycleHold _inventoryLifecycleHold;

        public bool HasInventoryLifecycleHold => _inventoryLifecycleHold != null && !_inventoryLifecycleHold.IsCompleted;

        internal void FailDeathMutation(string detail)
        {
            if (!OnThread || _authority == null)
                throw new InvalidOperationException("Stop death custody only on its owning server inventory thread.");
            Fail(detail);
        }

        // These are the existing authenticated removal/grant receipts, not death notifications,
        // animation callbacks or marker replies. A rejected prepare is distinct from a debit.
        internal bool TryReadDeathMutationReceipt(in PlayerInventoryFrame command,
            out bool completed, out bool applied, out string detail)
        {
            completed = applied = false;
            detail = "No exact live native inventory receiver matches the death command.";
            if (!OnThread || _busy || IsDisposed || IsFaulted || !IsArmed || _authority == null || !Current()
                || command.Kind is not (PlayerInventoryMessage.Remove or PlayerInventoryMessage.GrantPrepare)
                || command.MigrationId != _baseline.Plan.Id || command.Generation != _baseline.Plan.Generation
                || command.NetworkId != _bridge.NetworkId || command.Fingerprint != _baseline.Fingerprint) return false;
            if (LastCompletedTransaction < command.Transaction)
            {
                if (!_hasPending || command != (_pending with { Kind = command.Kind })
                    || (command.Kind == PlayerInventoryMessage.Remove ? _pending.Kind != PlayerInventoryMessage.Remove
                        : _pending.Kind is not (PlayerInventoryMessage.GrantPrepare or PlayerInventoryMessage.GrantPoll))) return false;
                detail = "Exact death mutation receipt pending; no second native write is authorized.";
                return true;
            }
            if (_hasPending || LastCompletedTransaction != command.Transaction || LastReceipt is not { } receipt) return false;
            var expected = new PlayerInventoryReceipt(command.Transaction, command.Key,
                command.ItemId, command.Quantity, command.Kind == PlayerInventoryMessage.GrantPrepare, receipt.Applied);
            if (receipt != expected || (command.Kind == PlayerInventoryMessage.Remove && !receipt.Applied)) return false;
            completed = true;
            applied = receipt.Applied;
            detail = applied ? "Exact native death custody mutation acknowledged." : "Native restore capacity refused before delivery.";
            return true;
        }

        /// <summary>
        /// Router predicate for exact inventory, death action, bag manager and pickup writers.
        /// It does not block the original client OnSerialize. Route RemoveAllItemsOnDeath
        /// independently of bag spawn/retrieval and do not use a false result as authorization.
        /// </summary>
        internal static bool IsNativeInventoryLifecycleHeld(Il2CppObjectBase instance)
        {
            var source = _guard;
            var hold = source?._inventoryLifecycleHold;
            return instance != null && !instance.WasCollected && hold != null && !hold.IsCompleted
                && source._load == SaveLifecycle.State.BeforeLoadCount
                && (InventoryDeath.Same(instance, source._inventory)
                    || InventoryDeath.Same(instance, source._manager)
                    || InventoryDeath.Same(instance, hold._action)
                    || (hold._bag != null && (InventoryDeath.Same(instance, hold._bag.Manager)
                        || InventoryDeath.Same(instance, hold._bag.Pickup))));
        }

        private bool LifecycleQueueIdle => !HasPendingCustody && !_grantReplyQueued
            && !_sourceStowAwaitingCommit && !_nativeStowReserved && !_receivedStowRequest
            && !_permit && !SourceStowPending && !SourceEquipPending && !_perishableUpdating
            && !HasPendingNativeCraftRoute
            && (!_requestingStow || _stowRequestSent == 0);

        // A long-lived busy latch prevents Pump/Receive/CanQueue from issuing a new mutation.
        // It is acquired only outside their call stacks; no in-flight command is discarded.
        internal bool TryHoldLifecycleQueue()
        {
            if (!OnThread || _writerDepth != 0 || IsDisposed || IsFaulted || !IsArmed || !LifecycleQueueIdle || !Current()) return false;
            _busy = true;
            if (_requestingStow) { _requestingStow = false; _nativeStowRequest = default; }
            return true;
        }

        internal void ReleaseLifecycleQueue() { _busy = false; }

        /// <summary>
        /// Call after the server hold is acknowledged and upstream lifecycle/action ingress is
        /// held, before any native death/drop/
        /// retrieval/deserialization write. Failure with a nonnull hold retains the freeze.
        /// Only unsent local stow intent is cancelled; a sent command must settle first.
        /// PlayerSave freezes inventory transactions but leaves its original serializer registered.
        /// </summary>
        public bool TryFreezeInventoryLifecycle(InventoryDeathBoundary boundary,
            PlayerRetrieveDroppedInventoryAction bagAction, out InventoryLifecycleHold hold, out string detail)
        {
            hold = null;
            detail = "A current armed original client, idle transactions and held upstream lifecycle ingress are required.";
            if (!OnThread || !InventoryDeath.Valid(boundary) || !GameBuild.IsRecognized
                || _authority != null || _writerDepth != 0 || !ReferenceEquals(_guard, this)
                || HasInventoryLifecycleHold || !SourceLifetime() || !TryHoldLifecycleQueue()) return false;
            hold = _inventoryLifecycleHold = new InventoryLifecycleHold(this, bagAction, boundary);
            try
            {
                hold.Capture();
                detail = Detail = "Client inventory transactions frozen; original inventory, serializer and separate bag data retained.";
                return true;
            }
            catch (Exception error)
            {
                // Capture can encounter partial native state. Do not release the existing native
                // guard or discard its identity simply because preservation could not complete.
                detail = Detail = "Inventory lifecycle capture is incomplete; freeze retained: " + error.Message;
                return false;
            }
        }

        public sealed class InventoryLifecycleHold
        {
            private readonly PlayerInventoryEnforcement _source;
            private readonly HashSet<IntPtr> _extraModules = new();
            internal readonly PlayerRetrieveDroppedInventoryAction _action;
            internal InventoryDeath.BagSnapshot _bag;
            private PlayerInventoryBaseline _inventory;
            private string _playerSave;
            private bool _captured, _working;
            public InventoryDeathBoundary Boundary { get; }
            public bool IsCompleted { get; private set; }
            public bool HasCapturedOriginalData => _captured;

            internal InventoryLifecycleHold(PlayerInventoryEnforcement source,
                PlayerRetrieveDroppedInventoryAction action, InventoryDeathBoundary boundary)
            { _source = source; _action = action; Boundary = boundary; }

            internal void Capture()
            {
                if (!_source.Unchanged()) throw new InvalidOperationException("Native inventory differs from its last exact receipt.");
                _inventory = _source._currentSnapshot;
                _bag = new InventoryDeath.BagSnapshot(_action, _source._inventory);
                var previous = new HashSet<IntPtr>(_source._pausedModules.Keys);
                try
                {
                    foreach (var item in _bag.Instances)
                    {
                        foreach (var retained in _source._sourceItems)
                            if (retained.Quantity > 0 && InventoryDeath.Same(item, retained.Instance))
                                throw new InvalidOperationException("A unique item is still present in both bag and player custody.");
                        _source.RetainPausedModules(item);
                    }
                }
                finally
                {
                    foreach (var key in _source._pausedModules.Keys)
                        if (!previous.Contains(key)) _extraModules.Add(key);
                }
                _playerSave = InventoryDeath.CapturePlayerSave(_source._inventory);
                _captured = true;
                if (!Unchanged()) throw new InvalidOperationException("Inventory or bag changed during preservation.");
            }

            private bool Held => _source.OnThread && !IsCompleted && !_working && _source._busy
                && ReferenceEquals(_source._inventoryLifecycleHold, this) && ReferenceEquals(_guard, _source)
                && _source.SourceLifetime();

            private bool Unchanged() => _captured && _source.SourceLifetime()
                && _inventory.LocalMatches(PlayerInventoryBaseline.CaptureRetained(_inventory.Plan))
                && _bag.Matches(_action)
                && PlayerInventoryBaseline.JsonEqual(_playerSave,
                    InventoryDeath.CapturePlayerSave(_source._inventory));

            /// <summary>Cancel only an intact original endpoint. No write, replay, refund or item restoration.</summary>
            public bool TryCancel(out string detail)
            {
                detail = "Only unchanged original client data and its still-current bridge may resume.";
                if (!Held || _source.IsDisposed || _source.IsFaulted || !_source.Current()) return false;
                _working = true;
                try
                {
                    if (!Unchanged() || !_source.Current()) return false;
                    foreach (var key in _extraModules) _source._pausedModules.Remove(key);
                    _extraModules.Clear();
                    _source._inventoryLifecycleHold = null;
                    _source.ReleaseLifecycleQueue();
                    IsCompleted = true;
                    detail = _source.Detail = "Unchanged client inventory resumed; cancelled unsent intent was not replayed.";
                    return true;
                }
                catch (Exception error) { detail = "Inventory cancellation refused; freeze retained: " + error.Message; return false; }
                finally { _working = false; }
            }

            /// <summary>
            /// Observe retained data for the exact completed replacement bridge. This is not
            /// server approval. Load or actual native death/drop mutations require new admission,
            /// never reconstruction from this pre-event observation.
            /// </summary>
            public bool TryObserveReconstruction(PlayerBridge replacement,
                out PlayerInventoryBaseline observation, out string detail)
            {
                observation = null;
                detail = "Reconstruction requires the retired old bridge, unchanged original data and an empty death bag.";
                if (!Held || !_source.IsDisposed || _source.IsFaulted
                    || !InventoryDeath.NextBridge(_source._bridge, replacement, false)) return false;
                _working = true;
                try
                {
                    if (!replacement.TryReadCurrentTarget(out var target) || target.isOwner
                        || LocalPlayer.Entity?.Pointer != target.Pointer || !Unchanged() || _bag.HasContents) return false;
                    var next = PlayerInventoryBaseline.CaptureRetained(replacement.Plan);
                    if (!Unchanged() || !replacement.TryReadCurrentTarget(out var current) || current.Pointer != target.Pointer) return false;
                    observation = next;
                    detail = "Exact retained client data observed for replacement; explicit server custody matching remains required.";
                    return true;
                }
                catch (Exception error) { detail = "Inventory reconstruction observation refused: " + error.Message; return false; }
                finally { _working = false; }
            }

            /// <summary>
            /// Rebuild only the endpoint around the same native objects after explicit approval.
            /// No native deserialization occurs. The new handshake must complete before mutations.
            /// </summary>
            public bool TryReconstruct(PlayerBridge replacement, string approvedFingerprint,
                out PlayerInventoryEnforcement endpoint, out string detail)
            {
                endpoint = null;
                if (!TryObserveReconstruction(replacement, out var baseline, out detail)
                    || baseline.Fingerprint != approvedFingerprint) return false;
                _working = true;
                PlayerInventoryEnforcement candidate = null;
                try
                {
                    candidate = new PlayerInventoryEnforcement(baseline)
                    {
                        _inventory = _source._inventory, _manager = _source._manager,
                        _root = _source._root, _originalEntity = _source._originalEntity
                    };
                    var offsets = new Dictionary<int, int>();
                    foreach (var entry in baseline.Entries)
                    {
                        ItemInstance instance = null;
                        if (Stowed(entry) && entry.Modules != null)
                        {
                            offsets.TryGetValue(entry.ItemId, out int offset);
                            instance = candidate._manager._itemsMap[entry.ItemId].UniqueInstances[offset];
                            offsets[entry.ItemId] = offset + 1;
                        }
                        else if (entry.EquipmentSlot >= 0) instance = candidate._inventory._equipmentSlots[entry.EquipmentSlot];
                        else if (entry.ClothingSlots != 0 && entry.Modules != null)
                            foreach (var hookup in LocalPlayer.ClothingSystem._clothingHookupData)
                                if ((int)hookup.Slot == entry.ClothingSlots && hookup.ItemInstance?._itemID == entry.ItemId)
                                    instance = hookup.ItemInstance;
                        if (entry.Modules != null && instance == null)
                            throw new InvalidOperationException("Exact reconstruction instance is unavailable.");
                        candidate.RetainPausedModules(instance);
                        candidate._sourceItems.Add(new SourceItem(entry, instance));
                    }
                    if (!Unchanged() || !candidate.TryBindSource(replacement, out detail) || !Unchanged())
                    { candidate.Dispose(); return false; }
                    Active.Add(candidate);
                    _guard = candidate;
                    IsCompleted = true;
                    endpoint = candidate;
                    detail = "Client custody endpoint reconstructed without changing native data; exact replacement handshake pending.";
                    return true;
                }
                catch (Exception error)
                {
                    candidate?.Dispose();
                    detail = "Client inventory reconstruction failed; original freeze retained: " + error.Message;
                    return false;
                }
                finally { _working = false; }
            }
        }
    }
}
