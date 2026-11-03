// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private PlayerInventoryBaseline _acceptedObservation;
        private PendingRemoval _removal;
        private InventoryStorage _worldTransfer;
        public PlayerInventoryEnforcement MutationEnforcement { get; private set; }

        /// <summary>
        /// Queues one independently server-authorized plain storage withdrawal. The upstream
        /// router must suppress the original event and fence competing access to held world slots.
        /// Sender identity is rechecked by the native helper; this API does not authorize reach.
        /// </summary>
        public bool TryQueueStorageWithdrawal(ulong transaction, ItemStorageRemoveItemEvent request, out string detail)
        {
            detail = "A current authenticated single-item storage request and exclusive native routing are required.";
            if (!Ready(out _) || _worldTransfer != null || request == null || request.WasCollected || request.Count != 1
                || !PlainStorageDefinition(request.ItemId) || MutationEnforcement == null) return false;
            if (!TryQueueEnforcedGrant(transaction, request.ItemId, out detail)) return false;
            var pending = _grant;
            if (InventoryStorage.TryReserveWithdrawal(_bridge, this, pending.Frame, request,
                out var transfer, out detail))
            {
                _worldTransfer = transfer;
                _grant = new PendingGrant(pending.Frame, pending.Record, transfer.Item, transfer.Modules, pending.Equipment);
                MutationEnforcement.ReleaseDetachedModules(pending.Native);
                Detail = detail = "Storage source reserved; exact retained-client capacity must be ready before native withdrawal.";
                return true;
            }
            if (!MutationEnforcement.CancelUnsentMutation(pending.Frame))
            { Fault("Storage reservation failed after its prepare may have escaped; no replay or refund."); detail = Detail; return false; }
            MutationEnforcement.ReleaseDetachedModules(pending.Native);
            _grant = null;
            return false;
        }

        /// <summary>
        /// Always refused. DestroyPickUp is downstream cleanup, not pre-mutation intent, so it can
        /// never authorize a grant. Plain pickups are reserved by the loader's typed world intents.
        /// </summary>
        [Obsolete("DestroyPickUp cannot reserve a pickup. Plain world pickups are reserved by the loader's typed world intents; this method always returns false.")]
        public bool TryQueueWorldPickup(ulong transaction, DestroyPickUp request, out string detail)
        {
            detail = "DestroyPickUp is downstream cleanup, not pre-mutation intent. Plain pickups use the typed world intent route.";
            return false;
        }

        /// <summary>
        /// Reserve one plain stored item for an independently authorized quiet drop. Caller
        /// suppresses DropItem and validates placement; this never permits a second native debit.
        /// </summary>
        public bool TryQueueWorldDrop(ulong transaction, ulong instanceKey, DropItem request, out string detail)
        {
            detail = "An authenticated quiet drop and exact server-selected plain source key are required.";
            if (!Ready(out _) || request == null || request.WasCollected || request.ItemCount != 1
                || MutationEnforcement == null || !_items.TryGetValue(instanceKey, out var item)
                || item.Item != null || !PlainStorageDefinition(item.Id)) return false;
            if (!TryQueueEnforcedRemoval(transaction, instanceKey, 1, out detail)) return false;
            var pending = _removal;
            if (InventoryStorage.TryReserveDrop(_bridge, this, pending.Frame, request,
                out var transfer, out detail))
            {
                _worldTransfer = transfer;
                Detail = detail = "Quiet drop reserved; one exact client removal must precede the native pickup factory.";
                return true;
            }
            if (!MutationEnforcement.CancelUnsentMutation(pending.Frame))
            { Fault("Drop reservation failed after its removal may have escaped; no replay or refund."); detail = Detail; return false; }
            _removal = null;
            return false;
        }

        internal bool PrepareWorldRemovalDispatch(in PlayerInventoryFrame command, out string detail)
        {
            detail = "World transfer removal dispatch does not match its reservation.";
            if (_worldTransfer == null) return true;
            if (_removal == null || command != _removal.Frame) return false;
            if (_removal.DispatchPrepared) return true;
            if (!_worldTransfer.TryBeginWorldRemoval(out detail)) return false;
            _removal.DispatchPrepared = true;
            return true;
        }

        /// <summary>
        /// Queues one plain deposit from a server-selected stored instance key. Caller must
        /// authorize reach/access and suppress/fence the original world event. Native storage
        /// addition occurs only after the exact source removal receipt, before ledger debit.
        /// </summary>
        public bool TryQueueStorageDeposit(ulong transaction, ulong instanceKey, ItemStorageAddItemEvent request, out string detail)
        {
            detail = "A current authenticated single-item deposit and exact server-selected source key are required.";
            if (!Ready(out _) || _worldTransfer != null || request == null || request.WasCollected || request.Count != 1
                || !PlainStorageDefinition(request.ItemId) || MutationEnforcement == null
                || !_items.TryGetValue(instanceKey, out var item) || item.Id != request.ItemId || item.Item != null) return false;
            if (!TryQueueEnforcedRemoval(transaction, instanceKey, 1, out detail)) return false;
            var pending = _removal;
            if (InventoryStorage.TryReserveDeposit(_bridge, this, pending.Frame, request,
                out var transfer, out detail))
            {
                _worldTransfer = transfer;
                Detail = detail = "Storage destination reserved; exact native source removal receipt is pending.";
                return true;
            }
            if (!MutationEnforcement.CancelUnsentMutation(pending.Frame))
            { Fault("Storage reservation failed after its removal may have escaped; no replay or refund."); detail = Detail; return false; }
            _removal = null;
            return false;
        }

        private static bool PlainStorageDefinition(int id)
        {
            var data = ItemDatabaseManager.ItemById(id);
            return data != null && !data.WasCollected && data && data.Id == id
                && !data._eachInstanceIsUnique && !data._canNotBeStored && data._maxAmount > 0;
        }

        /// <summary>
        /// Bind immediately after explicit baseline acceptance, before any ledger/equipment edits.
        /// The matching client must have captured that baseline under its retained-source guard.
        /// This does not infer approval, enable capabilities or grant full vanilla coverage.
        /// </summary>
        public bool TryEnableMutationEnforcement(out string detail)
        {
            detail = "An unchanged explicitly accepted baseline and idle completed server bridge are required.";
            if (!PreparedReady(out _) || MutationEnforcement != null || _pending != null || !HasAcceptedBaseline
                || _acceptedObservation == null || _items.Count != _acceptedObservation.EntryCount) return false;
            _busy = true;
            try
            {
                ulong floor = 0;
                foreach (var entry in _acceptedObservation.Entries)
                {
                    floor = System.Math.Max(floor, entry.Key);
                    if (!_items.TryGetValue(entry.Key, out var record) || record.Consumed || !record.FromAcceptedBaseline
                        || record.Id != entry.ItemId || record.Quantity != entry.Quantity
                        || record.HeldSlot != entry.EquipmentSlot || record.ClothingSlots != entry.ClothingSlots
                        || record.ArmourSlot != entry.ArmourSlot || record.ArmourPoints != entry.ArmourPoints
                        || !record.IdentityCurrent() || (entry.Modules != null
                            && !PlayerInventoryBaseline.MatchesInstance(entry, record.Item))) return false;
                }
                if (_lastTransaction != floor || !Current(out _)) return false;
                if (!PlayerInventoryEnforcement.TryCreateServer(_bridge, this, _acceptedObservation,
                    out var endpoint, out detail)) return false;
                MutationEnforcement = endpoint;
                Detail = detail;
                return true;
            }
            catch (Exception error) { detail = "Inventory enforcement binding failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        private bool TryQueueEnforcedRemoval(ulong transaction, ulong key, int quantity, out string detail)
        {
            detail = "A fresh trusted server decision, armed exact source, and stowed owned item are required.";
            if (!Ready(out var target) || !MutationEnforcement.CanQueue || !Fresh(transaction) || _pending != null
                || !_items.TryGetValue(key, out var item) || item.Consumed || item.DefinitionOnly
                || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0
                || quantity <= 0 || quantity > item.Quantity || (item.Item != null && quantity != 1)) return false;
            _busy = true;
            try
            {
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (equipment == null || !OwnedEquipmentMatches(equipment) || !item.IdentityCurrent()) return false;
                string modules = item.Item == null ? null : PlayerInventoryBaseline.CaptureModules(item.Item);
                if (!Current(out var current) || current.Pointer != target.Pointer) return false;
                var frame = new PlayerInventoryFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    AcceptedBaselineFingerprint, transaction, key, item.Id, quantity, item.Quantity, PlayerInventoryMessage.Remove);
                _removal = new PendingRemoval(frame, item, modules, equipment);
                _lastTransaction = transaction;
                MutationEnforcement.QueueMutation(frame);
                Detail = detail = "Server removal reserved; native source receipt is pending. No consumption effect or delivery is implied.";
                return true;
            }
            catch (Exception error) { detail = "Server removal reservation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal bool CompleteEnforcedRemoval(in PlayerInventoryFrame receipt, out string detail)
        {
            detail = "No exact pending server removal matches the native receipt.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out var target)
                || _removal == null || receipt.Kind != PlayerInventoryMessage.Applied
                || receipt != (_removal.Frame with { Kind = PlayerInventoryMessage.Applied })) return false;
            _busy = true;
            try
            {
                var pending = _removal;
                var item = pending.Item;
                if (item.Consumed || item.Quantity != pending.Frame.BeforeQuantity || !item.IdentityCurrent()
                    || !pending.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))
                    || (item.Item != null && !PlayerInventoryBaseline.JsonEqual(pending.Modules,
                        PlayerInventoryBaseline.CaptureModules(item.Item)))
                    || (item.Item != null && pending.ModuleState != PlayerInventoryBaseline.CaptureModuleState(item.Item))
                    || !Current(out var current) || current.Pointer != target.Pointer) return false;
                // The client acknowledges only the removal already authorized by this server. It
                // cannot select the item, increase quantities, issue a grant or approve a new baseline.
                if (_worldTransfer != null)
                {
                    bool worldApplied = _worldTransfer.Kind == InventoryStorage.TransferKind.Drop
                        ? _worldTransfer.TryApplyDrop(receipt, out detail)
                        : _worldTransfer.TryApplyDeposit(receipt, out detail);
                    if (!worldApplied) return false;
                    if (!Current(out var afterDeposit) || afterDeposit.Pointer != target.Pointer
                        || !item.IdentityCurrent() || item.Quantity != pending.Frame.BeforeQuantity
                        || !pending.Equipment.Matches(EquipmentState.Capture(afterDeposit.GetState<IPlayerState>())))
                    {
                        _worldTransfer.HoldUnconfirmed("Player custody changed during storage destination publication.");
                        detail = _worldTransfer.Detail;
                        return false;
                    }
                }
                item.Quantity -= pending.Frame.Quantity;
                item.Consumed = item.Quantity == 0;
                ReleaseConsumedModules(item);
                if (_worldTransfer != null)
                {
                    if (!_worldTransfer.TryCompleteRemoval(receipt, out detail)) return false;
                    _worldTransfer = null;
                }
                _removal = null;
                Detail = detail = "Server-authorized native inventory removal acknowledged and ledger debit committed.";
                return true;
            }
            catch (Exception error) { detail = "Removal reconciliation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal void FaultEnforcement(string detail) => Fault(detail);

        private sealed class PendingRemoval(PlayerInventoryFrame frame, ItemRecord item, string modules, EquipmentState equipment)
        {
            internal readonly PlayerInventoryFrame Frame = frame;
            internal readonly ItemRecord Item = item;
            internal readonly string Modules = modules;
            internal readonly PlayerInventoryModuleState ModuleState = item.Item == null ? null
                : PlayerInventoryBaseline.CaptureModuleState(item.Item);
            internal readonly EquipmentState Equipment = equipment;
            internal bool DispatchPrepared;
        }
    }
}
