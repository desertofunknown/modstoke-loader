// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private readonly int _loadCount = SaveLifecycle.State.BeforeLoadCount;
        private bool _suspended;
        private bool SameWorld => _loadCount == SaveLifecycle.State.BeforeLoadCount && _bridge.Peer.IsCurrent;

        /// <summary>
        /// TRUSTED SERVER/OPERATOR COMMAND. Grandfathers the exact observed inventory, including
        /// modules, without claiming its prior acquisition was legitimate. approvedFingerprint
        /// must come from the server's approval decision, not an automatically echoed client field.
        /// Call before any grants or equipment decisions. Failure accepts NONE of the baseline;
        /// it never clears, adds or replays client items. Use the normal migration barrier to keep
        /// the captured client inventory suspended through this decision and bridge binding.
        /// </summary>
        public bool TryAcceptUnverifiedExistingInventory(PlayerInventoryBaseline baseline,
            string approvedFingerprint, string operatorReason, out string detail)
        {
            detail = "Explicit approval of this exact complete baseline on an empty current server account is required.";
            if (!PreparedReady(out var target) || baseline == null || baseline.Plan != _bridge.Plan
                || baseline.Fingerprint != approvedFingerprint || string.IsNullOrWhiteSpace(operatorReason)
                || operatorReason.Length > 512 || HasAcceptedBaseline || _items.Count != 0
                || _lastTransaction != 0 || _pending != null) return false;
            _busy = true;
            try
            {
                var before = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (!MatchesBaseline(before, baseline))
                {
                    detail = "Replacement equipment does not match the captured baseline; no inventory was accepted.";
                    return false;
                }
                var staged = new Dictionary<ulong, ItemRecord>();
                ulong highestKey = 0;
                foreach (var entry in baseline.Entries)
                {
                    var data = ItemDatabaseManager.ItemById(entry.ItemId);
                    PlayerInventoryBaseline.Require(data != null && !data.WasCollected && data
                        && data.Id == entry.ItemId, "An accepted item definition is unavailable on the server.");
                    ItemRecord record;
                    if (entry.Modules != null)
                        record = new ItemRecord(entry.Key, PlayerInventoryBaseline.RestoreInstance(entry));
                    else
                    {
                        PlayerInventoryBaseline.Require(entry.DefinitionOnly || !data._eachInstanceIsUnique,
                            "A unique stored item is missing its native module save data.");
                        record = new ItemRecord(entry.Key, entry.ItemId, entry.Quantity);
                    }
                    record.Quantity = entry.Quantity;
                    record.HeldSlot = entry.EquipmentSlot;
                    record.ClothingSlots = entry.ClothingSlots;
                    record.Clothing = entry.ClothingSlots != 0;
                    record.ArmourSlot = entry.ArmourSlot;
                    record.ArmourPoints = entry.ArmourPoints;
                    record.FromAcceptedBaseline = true;
                    record.DefinitionOnly = entry.DefinitionOnly;
                    PlayerInventoryBaseline.Require(record.CaptureIdentity(), "Native accepted instance identity is unavailable.");
                    PlayerInventoryBaseline.Require(entry.Modules == null
                        || PlayerInventoryBaseline.MatchesInstance(entry, record.Item),
                        "Native accepted instance lost supplemental module state.");
                    staged.Add(record.Key, record);
                    highestKey = System.Math.Max(highestKey, record.Key);
                }
                if (!Current(out var current) || current.Pointer != target.Pointer
                    || !before.Matches(EquipmentState.Capture(current.GetState<IPlayerState>())))
                {
                    detail = "Player lifetime/equipment changed during baseline preparation; nothing was accepted.";
                    return false;
                }
                foreach (var entry in baseline.Entries)
                {
                    var record = staged[entry.Key];
                    PlayerInventoryBaseline.Require(record.IdentityCurrent()
                        && (entry.Modules == null || PlayerInventoryBaseline.MatchesInstance(entry, record.Item)),
                        "Prepared instance changed before acceptance.");
                }
                PlayerInventoryBaseline.Require(Current(out current) && current.Pointer == target.Pointer,
                    "Player lifetime changed before baseline commit.");
                foreach (var entry in staged) _items.Add(entry.Key, entry.Value);
                _lastTransaction = highestKey;
                HasAcceptedBaseline = true;
                AcceptedBaselineFingerprint = baseline.Fingerprint;
                AcceptanceReason = operatorReason;
                _acceptedObservation = baseline;
                Detail = detail = "Operator-approved existing inventory admitted in full; prior acquisition remains unverified. "
                    + "Next transaction must exceed " + highestKey + ". Client inventory was not altered.";
                return true;
            }
            catch (Exception error)
            {
                // Commit only contains managed dictionary additions; clear any partial staging,
                // never touch the player's native inventory to recover an admission failure.
                _items.Clear();
                detail = "Existing inventory was not accepted: " + error.Message;
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>Enumerate stable account keys, including repeated IDs and native stack records.</summary>
        public bool TryReadItems(out PlayerInventoryItem[] items)
        {
            items = Array.Empty<PlayerInventoryItem>();
            if (!Ready(out _)) return false;
            var result = new List<PlayerInventoryItem>(_items.Count);
            foreach (ulong key in _items.Keys)
            {
                if (!TryReadItem(key, out var item)) return false;
                result.Add(item);
            }
            items = result.ToArray();
            return true;
        }

        public bool TryReadModuleSaveData(ulong instanceKey, out string moduleSaveData)
        {
            moduleSaveData = null;
            if (!Ready(out _) || !_items.TryGetValue(instanceKey, out var item) || item.Consumed || item.Item == null) return false;
            _busy = true;
            try
            {
                if (!item.IdentityCurrent()) return false;
                moduleSaveData = PlayerInventoryBaseline.CaptureModules(item.Item);
                if (Current(out _)) return true;
                moduleSaveData = null;
                return false;
            }
            catch (Exception) { moduleSaveData = null; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Reserves one unit from an accepted native nonunique stack for exact-key equipment use.
        /// This is a ledger split, not a grant or client mutation. Newly created native modules
        /// would invent state, so only a moduleless nonunique definition may be materialized.
        /// The new account key is transaction. Failure leaves the stack unchanged.
        /// </summary>
        public bool TryMaterializeStackItem(ulong transaction, ulong stackKey, out string detail)
        {
            detail = "A fresh transaction and a stowed nonunique stack are required.";
            if (MutationEnforcement != null)
            {
                detail = "A native source stack split must be authorized before changing enforced instance keys.";
                return false;
            }
            if (!Ready(out _) || !Fresh(transaction) || _pending != null || _items.ContainsKey(transaction)
                || _items.Count >= MaximumIssuedItems || !_items.TryGetValue(stackKey, out var stack)
                || stack.Item != null || stack.DefinitionOnly || stack.Consumed
                || stack.HeldSlot >= 0 || stack.Clothing || stack.ArmourSlot >= 0)
                return false;
            _busy = true;
            try
            {
                var data = ItemDatabaseManager.ItemById(stack.Id);
                if (data == null || data.WasCollected || !data || data._eachInstanceIsUnique || !stack.IdentityCurrent()) return false;
                var item = new ItemInstance(stack.Id);
                if (item._modules == null || item._modules.Count != 0 || !Current(out _))
                {
                    detail = "Native stack materialization would invent module state; stack left unchanged.";
                    return false;
                }
                var record = new ItemRecord(transaction, item) { FromAcceptedBaseline = stack.FromAcceptedBaseline };
                if (!record.CaptureIdentity() || !Current(out _)) return false;
                _items.Add(transaction, record);
                stack.Quantity--;
                stack.Consumed = stack.Quantity == 0;
                _lastTransaction = transaction;
                Detail = detail = "One existing stack unit reserved under key " + transaction + "; no item was granted.";
                return true;
            }
            catch (Exception error) { detail = "Stack materialization failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        private static bool MatchesBaseline(EquipmentState state, PlayerInventoryBaseline baseline)
            => state != null && state.Held.AsSpan().SequenceEqual(baseline.Held)
                && state.Clothing.AsSpan().SequenceEqual(baseline.Clothing)
                && state.Armour.AsSpan().SequenceEqual(baseline.Armour);

        private bool OwnedEquipmentMatches(EquipmentState state)
        {
            if (state == null) return false;
            var held = new List<int>();
            var clothing = new List<int>();
            var armour = new int[state.Armour.Length];
            foreach (var item in _items.Values)
            {
                if (item.Consumed) continue;
                if (item.HeldSlot >= 0) held.Add(item.Id);
                if (item.Clothing) clothing.Add(item.Id);
                if (item.ArmourSlot >= 0)
                {
                    if (item.ArmourSlot >= armour.Length || armour[item.ArmourSlot] != 0) return false;
                    armour[item.ArmourSlot] = item.Id;
                }
            }
            return MatchesOwnedIds(held, state.Held, HasAcceptedBaseline)
                && MatchesOwnedIds(clothing, state.Clothing, HasAcceptedBaseline)
                && (!HasAcceptedBaseline || armour.AsSpan().SequenceEqual(state.Armour));
        }

        private static bool MatchesOwnedIds(List<int> owned, int[] state, bool complete)
        {
            var counts = new Dictionary<int, int>();
            foreach (int id in owned)
            {
                counts.TryGetValue(id, out int count);
                counts[id] = count + 1;
            }
            foreach (int id in state)
            {
                if (id == 0) continue;
                if (!counts.TryGetValue(id, out int count))
                {
                    if (complete) return false;
                    continue;
                }
                if (count == 0) return false;
                counts[id] = count - 1;
            }
            foreach (int count in counts.Values) if (count != 0) return false;
            return true;
        }

        private void OnSaveLifecycle(SaveLifecycleEvent change)
        {
            if (!SameWorld) Dispose();
        }

        /// <summary>
        /// Suspend this account before a subsequent migration. The single-use handoff keeps the
        /// accepted records and transaction floor, not a new client report. Caller must Dispose
        /// it on abort; disconnect/load invalidates it. No save/death/respawn reconstruction.
        /// </summary>
        public bool TryPrepareMigration(BoltMigrationPlan nextPlan, out MigrationHandoff handoff, out string detail)
        {
            handoff = null;
            detail = "An idle current account and exact next player migration are required.";
            if (MutationEnforcement != null)
            {
                detail = "The enforced native source needs a custody handoff before another player migration; ledger-only handoff is refused.";
                return false;
            }
            if (!Ready(out var target) || _pending != null || !nextPlan.IsValid || nextPlan.Id == _bridge.Plan.Id
                || nextPlan.OldNetworkId != _bridge.NetworkId || nextPlan.Generation != _bridge.Plan.Generation
                || nextPlan.PrefabId != _bridge.Plan.PrefabId || nextPlan.StateTypeId != _bridge.Plan.StateTypeId) return false;
            _busy = true;
            try
            {
                if (!OwnedEquipmentMatches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
                foreach (var item in _items.Values) if (!item.Consumed && !item.IdentityCurrent()) return false;
                handoff = new MigrationHandoff(this, nextPlan);
                if (!Current(out _)) { handoff.Dispose(); handoff = null; return false; }
                _suspended = true;
                Detail = detail = "Inventory account suspended for an exact single-use migration handoff.";
                return true;
            }
            catch (Exception error) { detail = "Inventory handoff rejected: " + error.Message; return false; }
            finally { _busy = false; }
        }

        public sealed class MigrationHandoff : IDisposable
        {
            private readonly PlayerInventoryAuthority _source;
            private readonly BoltMigrationPlan _plan;
            private readonly Dictionary<ulong, ItemRecord> _records;
            private readonly Dictionary<ulong, string> _modules = new();
            private readonly Dictionary<ulong, PlayerInventoryModuleState> _moduleStates = new();
            private readonly EquipmentState _equipment;
            private bool _busy;
            public bool IsDisposed { get; private set; }

            internal MigrationHandoff(PlayerInventoryAuthority source, BoltMigrationPlan plan)
            {
                _source = source; _plan = plan;
                _records = new(source._items);
                foreach (var item in _records.Values)
                    if (!item.Consumed && item.Item != null)
                    {
                        _modules.Add(item.Key, PlayerInventoryBaseline.CaptureModules(item.Item));
                        _moduleStates.Add(item.Key, PlayerInventoryBaseline.CaptureModuleState(item.Item));
                    }
                PlayerInventoryBaseline.Require(source._bridge.TryReadCurrentTarget(out var target),
                    "The inventory source bridge retired during handoff preparation.");
                _equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                PlayerInventoryBaseline.Require(_equipment != null, "Equipment became unavailable while preparing handoff.");
                ControlTransport.Closed += OnClosed;
                SaveLifecycle.Lifecycle += OnLoad;
            }

            /// <summary>Rebind only after the old bridge retired and the next migration completed.</summary>
            public bool TryResumeServer(PlayerBridge bridge,
                out PlayerInventoryAuthority authority, out string detail)
            {
                authority = null;
                detail = "A live single-use handoff and completed replacement bridge for the same peer are required.";
                if (Environment.CurrentManagedThreadId != _source._thread || _busy || IsDisposed || !_source.SameWorld
                    || !_source._bridge.IsDisposed || _source.IsFaulted || bridge == null || !bridge.IsServer
                    || bridge.Plan != _plan || !ReferenceEquals(bridge.Peer, _source._bridge.Peer)
                    || !bridge.TryReadCurrentTarget(out var target)) return false;
                PlayerInventoryAuthority replacement = null;
                _busy = true;
                try
                {
                    var state = EquipmentState.Capture(target.GetState<IPlayerState>());
                    if (!_equipment.SameValues(state) || !RecordsCurrent() || IsDisposed || !_source.SameWorld) return false;
                    if (!TryCreateServer(bridge, out replacement, out detail)) return false;
                    foreach (var entry in _records) replacement._items.Add(entry.Key, entry.Value);
                    replacement._lastTransaction = _source._lastTransaction;
                    replacement.HasAcceptedBaseline = _source.HasAcceptedBaseline;
                    replacement.AcceptedBaselineFingerprint = _source.AcceptedBaselineFingerprint;
                    replacement.AcceptanceReason = _source.AcceptanceReason;
                    authority = replacement;
                    replacement.Detail = detail = "Accepted inventory and transaction history continued on the exact replacement bridge.";
                    Dispose();
                    return true;
                }
                catch (Exception error)
                {
                    replacement?.Dispose();
                    if (replacement != null) Dispose();
                    authority = null;
                    detail = "Inventory handoff failed: " + error.Message;
                    return false;
                }
                finally { _busy = false; }
            }

            /// <summary>Cancel only while the original bridge and all captured values remain intact.</summary>
            public bool TryCancel(out string detail)
            {
                detail = "Cancellation requires the original current bridge and unchanged inventory.";
                if (Environment.CurrentManagedThreadId != _source._thread || _busy || IsDisposed || !_source.Current(out var target)) return false;
                _busy = true;
                try
                {
                    if (!_equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>())) || !RecordsCurrent()
                        || IsDisposed || !_source.Current(out _)) return false;
                    _source._suspended = false;
                    Dispose();
                    _source.Detail = detail = "Original inventory authority resumed; no client item was changed.";
                    return true;
                }
                catch (Exception error) { detail = "Inventory cancellation failed: " + error.Message; return false; }
                finally { _busy = false; }
            }

            private bool RecordsCurrent()
            {
                foreach (var item in _records.Values)
                {
                    if (item.Consumed) continue;
                    if (!item.IdentityCurrent() || (item.Item != null
                        && (_moduleStates[item.Key] != PlayerInventoryBaseline.CaptureModuleState(item.Item)
                            || !PlayerInventoryBaseline.JsonEqual(_modules[item.Key],
                                PlayerInventoryBaseline.CaptureModules(item.Item))))) return false;
                }
                return true;
            }

            private void OnClosed(ControlPeer peer) { if (ReferenceEquals(peer, _source._bridge.Peer)) Dispose(); }
            private void OnLoad(SaveLifecycleEvent change) { if (!_source.SameWorld) Dispose(); }
            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId != _source._thread)
                    throw new InvalidOperationException("Dispose inventory handoff on its game thread.");
                if (IsDisposed) return;
                IsDisposed = true;
                ControlTransport.Closed -= OnClosed;
                SaveLifecycle.Lifecycle -= OnLoad;
                _records.Clear();
                _modules.Clear();
            }
        }
    }
}
