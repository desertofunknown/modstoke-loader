// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Math = System.Math;
using System.Collections.Generic;
using Bolt;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private InventoryLifecycleHold _inventoryLifecycleHold;
        public bool HasInventoryLifecycleHold => _inventoryLifecycleHold != null && !_inventoryLifecycleHold.IsCompleted;

        /// <summary>
        /// Freeze the exact accepted server ledger before client death, bag, retrieval or save
        /// ingress. Outstanding delivery is not cancellation: settle it before acquiring this
        /// hold. This never writes or claims custody of a client inventory save/death bag.
        /// </summary>
        public bool TryFreezeInventoryLifecycle(InventoryDeathBoundary boundary,
            out InventoryLifecycleHold hold, out string detail)
        {
            hold = null;
            detail = "An idle accepted account and settled native mutation endpoint are required.";
            if (!InventoryDeath.Valid(boundary) || HasInventoryLifecycleHold
                || HasNativeDeathTransfer || HasNativeDeathBag
                || !Ready(out var target) || !HasAcceptedBaseline || MutationEnforcement == null
                || _pending != null || _stow != null || !MutationEnforcement.CanQueue) return false;
            _busy = true;
            bool queueHeld = false;
            try
            {
                var candidate = new InventoryLifecycleHold(this, boundary);
                if (!candidate.Capture(target) || !Current(out var current) || current.Pointer != target.Pointer
                    || !MutationEnforcement.TryHoldLifecycleQueue()) return false;
                queueHeld = true;
                _suspended = true;
                hold = _inventoryLifecycleHold = candidate;
                detail = Detail = "Server inventory custody frozen; no pending command was discarded and no save ownership changed.";
                return true;
            }
            catch (Exception error)
            {
                if (queueHeld) { _suspended = true; Fault("Inventory lifecycle freeze became uncertain: " + error.Message); }
                detail = "Inventory lifecycle freeze rejected: " + error.Message;
                return false;
            }
            finally { _busy = false; }
        }

        public sealed class InventoryLifecycleHold
        {
            private readonly PlayerInventoryAuthority _source;
            private readonly List<FrozenRecord> _records = new();
            private EquipmentState _equipment;
            private ulong _floor;
            private bool _working;
            public InventoryDeathBoundary Boundary { get; }
            public bool IsCompleted { get; private set; }
            public ulong TransactionFloor => _floor;

            internal InventoryLifecycleHold(PlayerInventoryAuthority source,
                InventoryDeathBoundary boundary)
            { _source = source; Boundary = boundary; }

            internal bool Capture(BoltEntity target)
            {
                _equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (_equipment == null || !_source.OwnedEquipmentMatches(_equipment)) return false;
                _floor = _source._lastTransaction;
                foreach (var record in _source._items.Values)
                    if (!record.Consumed) _records.Add(new FrozenRecord(record));
                return RecordsCurrent() && _equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()));
            }

            private bool Held => Environment.CurrentManagedThreadId == _source._thread && !_working
                && !IsCompleted && ReferenceEquals(_source._inventoryLifecycleHold, this)
                && _source._suspended && _source.SameWorld && !_source.IsFaulted;

            private bool RecordsCurrent()
            {
                if (_source._lastTransaction != _floor) return false;
                foreach (var record in _records) if (!record.MatchesOriginal()) return false;
                return true;
            }

            public bool TryCancel(out string detail)
            {
                detail = "Cancellation requires the unchanged original server account and bridge.";
                if (!Held || _source.IsDisposed || !_source.Current(out var target)
                    || _source.MutationEnforcement.IsDisposed || _source.MutationEnforcement.IsFaulted) return false;
                _working = true;
                try
                {
                    if (!RecordsCurrent() || !_equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
                    _source.MutationEnforcement.ReleaseLifecycleQueue();
                    _source._suspended = false;
                    _source._inventoryLifecycleHold = null;
                    IsCompleted = true;
                    detail = _source.Detail = "Original server custody resumed without mutation, replay or refund.";
                    return true;
                }
                catch (Exception error) { detail = "Server inventory cancellation refused: " + error.Message; return false; }
                finally { _working = false; }
            }

            /// <summary>
            /// Reconstruct a new account only for the same authenticated peer's exact replacement
            /// bridge. The complete observation must independently match frozen server custody;
            /// approval alone cannot authorize a changed inventory. Item keys are scoped to the
            /// new baseline; the old transaction floor is retained before returning the account.
            /// Client serializer/death-bag data is not imported into server world persistence.
            /// </summary>
            public bool TryReconstruct(PlayerBridge replacement,
                PlayerInventoryBaseline observation, string approvedFingerprint, string reason,
                out PlayerInventoryAuthority authority, out string detail)
            {
                authority = null;
                detail = "Reconstruction requires explicit approval and an exact match to frozen server custody.";
                if (!Held || !_source.IsDisposed || !_source.MutationEnforcement.IsDisposed
                    || !InventoryDeath.NextBridge(_source._bridge, replacement, true)
                    || observation == null || observation.Plan != replacement.Plan
                    || observation.Fingerprint != approvedFingerprint || string.IsNullOrWhiteSpace(reason)
                    || reason.Length > 512) return false;
                _working = true;
                PlayerInventoryAuthority candidate = null;
                try
                {
                    if (!RecordsCurrent() || !TryMatchObservation(observation, out var records)
                        || !replacement.TryReadCurrentTarget(out var target)
                        || !_equipment.SameValues(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
                    if (!TryCreateServer(replacement, out candidate, out detail)) return false;
                    for (int i = 0; i < records.Length; i++)
                    {
                        ulong key = observation.Entries[i].Key;
                        candidate._items.Add(key, records[i].Reconstruct(key));
                        candidate._lastTransaction = Math.Max(candidate._lastTransaction, key);
                    }
                    candidate.HasAcceptedBaseline = true;
                    candidate.AcceptedBaselineFingerprint = approvedFingerprint;
                    candidate.AcceptanceReason = reason;
                    candidate._acceptedObservation = observation;
                    if (!candidate.TryEnableMutationEnforcement(out detail))
                    { candidate.Dispose(); return false; }
                    candidate._lastTransaction = Math.Max(candidate._lastTransaction, _floor);
                    if (!RecordsCurrent() || !candidate.Current(out _))
                    { candidate.Dispose(); return false; }
                    IsCompleted = true;
                    authority = candidate;
                    detail = candidate.Detail = "Frozen server custody reconstructed; exact replacement client handshake pending. Client save ownership unchanged.";
                    return true;
                }
                catch (Exception error)
                {
                    candidate?.Dispose();
                    detail = "Server inventory reconstruction failed; frozen original records retained: " + error.Message;
                    return false;
                }
                finally { _working = false; }
            }

            private bool TryMatchObservation(PlayerInventoryBaseline observation, out FrozenRecord[] records)
            {
                records = null;
                if (observation.EntryCount != _records.Count) return false;
                var ordered = new FrozenRecord[_records.Count];
                var matched = new bool[_records.Count];
                int index = 0;
                foreach (var entry in observation.Entries)
                {
                    int found = -1;
                    for (int i = 0; i < _records.Count; i++)
                        if (!matched[i] && _records[i].Matches(entry)) { found = i; break; }
                    if (found < 0) return false;
                    matched[found] = true;
                    ordered[index++] = _records[found];
                }
                records = ordered;
                return true;
            }

            private sealed class FrozenRecord
            {
                private readonly ItemRecord _original;
                private readonly int _id, _quantity, _held, _clothing, _armour;
                private readonly float _points;
                private readonly bool _definitionOnly;
                private readonly string _modules;

                internal FrozenRecord(ItemRecord item)
                {
                    if (!item.IdentityCurrent()) throw new InvalidOperationException("Frozen server instance identity is unavailable.");
                    _original = item; _id = item.Id; _quantity = item.Quantity; _held = item.HeldSlot;
                    _clothing = item.ClothingSlots; _armour = item.ArmourSlot; _points = item.ArmourPoints;
                    _definitionOnly = item.DefinitionOnly;
                    _modules = item.Item == null ? null : PlayerInventoryBaseline.CaptureModules(item.Item);
                }

                internal bool MatchesOriginal() => !_original.Consumed && _original.IdentityCurrent()
                    && _original.Id == _id && _original.Quantity == _quantity && _original.HeldSlot == _held
                    && _original.ClothingSlots == _clothing && _original.ArmourSlot == _armour
                    && _original.ArmourPoints == _points && _original.DefinitionOnly == _definitionOnly
                    && (_modules == null ? _original.Item == null : _original.Item != null
                        && PlayerInventoryBaseline.JsonEqual(_modules,
                            PlayerInventoryBaseline.CaptureModules(_original.Item)));

                internal bool Matches(PlayerInventoryBaseline.Entry entry)
                    => entry.ItemId == _id && entry.Quantity == _quantity && entry.EquipmentSlot == _held
                        && entry.ClothingSlots == _clothing && entry.ArmourSlot == _armour
                        && entry.ArmourPoints == _points && entry.DefinitionOnly == _definitionOnly
                        && (_modules == null ? entry.Modules == null : entry.Modules != null
                            && PlayerInventoryBaseline.JsonEqual(_modules, entry.Modules));

                internal ItemRecord Reconstruct(ulong key)
                {
                    if (!MatchesOriginal()) throw new InvalidOperationException("Frozen native custody changed before reconstruction.");
                    // Rebind the retained native instance, not a freshly created item/module set.
                    var record = _original.Item == null ? new ItemRecord(key, _id, _quantity)
                        : new ItemRecord(key, _original.Item);
                    record.Quantity = _quantity;
                    record.HeldSlot = _held;
                    record.ClothingSlots = _clothing;
                    record.Clothing = _clothing != 0;
                    record.ArmourSlot = _armour;
                    record.ArmourPoints = _points;
                    record.DefinitionOnly = _definitionOnly;
                    record.FromAcceptedBaseline = true;
                    if (!record.CaptureIdentity()) throw new InvalidOperationException("Retained instance cannot bind to replacement custody.");
                    return record;
                }
            }
        }
    }
}
