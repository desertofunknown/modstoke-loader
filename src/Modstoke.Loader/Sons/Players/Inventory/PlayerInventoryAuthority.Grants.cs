// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private PendingGrant _grant;

        internal bool TryReadEnforcedGrantModuleState(in PlayerInventoryFrame frame, out PlayerInventoryModuleState state)
        {
            state = null;
            if (_grant == null || frame != (_grant.Frame with { Kind = frame.Kind })
                || _grant.ModuleState != PlayerInventoryBaseline.CaptureModuleState(_grant.Native)) return false;
            state = _grant.ModuleState;
            return state != null;
        }

        private bool TryQueueEnforcedGrant(ulong transaction, int id, out string detail)
        {
            detail = "Grant requires a fresh trusted server decision, an armed source, and bounded native storage capacity.";
            if (!Ready(out var target) || !MutationEnforcement.CanQueue || !Fresh(transaction) || _pending != null
                || _items.ContainsKey(transaction) || _items.Count >= MaximumIssuedItems
                || id <= 0 || id > ServerInventory.MaxItemId) return false;
            _busy = true;
            try
            {
                var data = ItemDatabaseManager.ItemById(id);
                if (data == null || data.WasCollected || !data || data.Id != id || data._canNotBeStored || data._maxAmount < 1)
                    return false;
                long total = 0, stored = 0;
                foreach (var existing in _items.Values)
                {
                    if (existing.Consumed || existing.Id != id) continue;
                    total += existing.Quantity;
                    if (existing.HeldSlot < 0 && !existing.Clothing && existing.ArmourSlot < 0) stored += existing.Quantity;
                }
                if (total >= data._maxAmount || stored >= int.MaxValue) return false;
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (equipment == null || !OwnedEquipmentMatches(equipment)) return false;
                var native = new ItemInstance(id);
                PlayerInventoryEnforcement.InitializeGrantModules(native);
                string modules = PlayerInventoryBaseline.CaptureModules(native);
                string hash = PlayerInventoryWire.HashModules(modules);
                // Native nonunique storage has no module custody; never discard modules to fit it.
                if (!data._eachInstanceIsUnique && native._modules.Count != 0) return false;
                var record = data._eachInstanceIsUnique ? new ItemRecord(transaction, native) : new ItemRecord(transaction, id, 1);
                if (!record.CaptureIdentity() || !Current(out var current) || current.Pointer != target.Pointer) return false;
                MutationEnforcement.RetainPausedModules(native);
                var frame = new PlayerInventoryFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    AcceptedBaselineFingerprint, transaction, transaction, id, 1, (int)stored,
                    PlayerInventoryMessage.GrantPrepare, hash);
                _grant = new PendingGrant(frame, record, native, modules, equipment);
                _lastTransaction = transaction;
                MutationEnforcement.QueueMutation(frame);
                Detail = detail = "Server grant reserved; waiting for source capacity before one exact native-token delivery.";
                return true;
            }
            catch (Exception error) { detail = "Grant reservation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal bool DeliverEnforcedGrant(out string detail)
        {
            detail = "No current prepared server grant is available for delivery.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || _grant == null
                || _grant.SendAttempted || !Current(out var target)) return false;
            _busy = true;
            PlayerAddItem request = null;
            bool sendAttempted = false;
            try
            {
                var pending = _grant;
                if (!GrantCurrent(pending, target)) return false;
                if (_worldTransfer != null)
                {
                    if (!_worldTransfer.TryTakeWorldSource(pending.Frame with { Kind = PlayerInventoryMessage.GrantReady }, out detail))
                        return _worldTransfer.State == InventoryStorage.TransferState.SourceRetiring;
                    bool sent = _worldTransfer.TrySendWorldGrant(out detail);
                    pending.SendAttempted = _worldTransfer.GrantSendAttempted;
                    return sent;
                }
                var connection = ControlTransport.Resolve(_bridge.Peer);
                if (connection == null) return false;
                var token = pending.Native.GetBoltDataToken();
                if (token == null || token.WasCollected || token.ItemInstance == null || token.ItemInstance.WasCollected
                    || token.ItemInstance._itemID != pending.Frame.ItemId
                    || !PlayerInventoryBaseline.JsonEqual(pending.Modules,
                        PlayerInventoryBaseline.CaptureModules(token.ItemInstance))) return false;
                request = PlayerAddItem.Create(connection, ReliabilityModes.ReliableOrdered);
                request.Player = target;
                if (!ItemIdCarrier.TrySetItemId(request, pending.Frame.ItemId)) return false;
                request.Amount = 1;
                request.ItemInstance = token.Cast<IProtocolToken>();
                if (!Current(out var current) || current.Pointer != target.Pointer || !GrantCurrent(pending, target)) return false;
                pending.SendAttempted = true;
                sendAttempted = true;
                request.Send();
                if (!Current(out _)) return false;
                detail = "Exact native grant queued once; only its matching source receipt can commit custody.";
                return true;
            }
            catch (Exception error) { detail = "Native grant delivery is uncertain: " + error.Message; return false; }
            finally
            {
                if (request != null && !sendAttempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception) { Fault("Unsent native grant storage could not be released."); }
                }
                _busy = false;
            }
        }

        internal bool PumpWorldGrant(out string detail)
        {
            detail = Detail;
            if (_worldTransfer?.State != InventoryStorage.TransferState.SourceRetiring) return true;
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || _grant == null
                || _grant.SendAttempted || !Current(out var target)) return false;
            _busy = true;
            try
            {
                if (!GrantCurrent(_grant, target)) return false;
                if (!_worldTransfer.TryObservePickupRetirement(out detail))
                    return _worldTransfer.State == InventoryStorage.TransferState.SourceRetiring;
                bool sent = _worldTransfer.TrySendWorldGrant(out detail);
                _grant.SendAttempted = _worldTransfer.GrantSendAttempted;
                return sent;
            }
            catch (Exception error) { detail = "Pickup retirement/delivery is uncertain: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal bool CompleteEnforcedGrant(in PlayerInventoryFrame receipt, out string detail)
        {
            detail = "No exact pending server grant matches this receipt.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || _grant == null
                || !Current(out var target) || receipt != (_grant.Frame with { Kind = receipt.Kind })
                || receipt.Kind is not (PlayerInventoryMessage.GrantApplied or PlayerInventoryMessage.GrantRejected)) return false;
            _busy = true;
            try
            {
                var pending = _grant;
                if (!GrantCurrent(pending, target) || _items.ContainsKey(pending.Record.Key)) return false;
                if (receipt.Kind == PlayerInventoryMessage.GrantApplied)
                {
                    if (!pending.SendAttempted) return false;
                    _items.Add(pending.Record.Key, pending.Record);
                    if (_worldTransfer != null && !_worldTransfer.TryCompleteWorldGrant(receipt, out detail)) return false;
                    Detail = detail = "Exact native grant acknowledged; server instance key committed to custody.";
                }
                else
                {
                    if (_worldTransfer != null && (pending.SendAttempted || !_worldTransfer.TryCancelBeforeMutation(out detail)))
                    {
                        _worldTransfer.HoldUnconfirmed("Native storage grant was rejected after source mutation may have occurred.");
                        detail = _worldTransfer.Detail;
                        return false;
                    }
                    MutationEnforcement.ReleaseDetachedModules(pending.Native);
                    Detail = detail = "Grant refused before native addition; transaction consumed, no item credited or delivery retried.";
                }
                _grant = null;
                _worldTransfer = null;
                return true;
            }
            catch (Exception error) { detail = "Grant receipt could not be reconciled: " + error.Message; return false; }
            finally { _busy = false; }
        }

        private bool GrantCurrent(PendingGrant pending, BoltEntity target)
            => pending.Record.IdentityCurrent() && pending.Native != null && !pending.Native.WasCollected
                && pending.Native._itemID == pending.Frame.ItemId
                && PlayerInventoryBaseline.JsonEqual(pending.Modules,
                    PlayerInventoryBaseline.CaptureModules(pending.Native))
                && pending.ModuleState == PlayerInventoryBaseline.CaptureModuleState(pending.Native)
                && pending.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()));

        private sealed class PendingGrant(PlayerInventoryFrame frame, ItemRecord record, ItemInstance native,
            string modules, EquipmentState equipment)
        {
            internal readonly PlayerInventoryFrame Frame = frame;
            internal readonly ItemRecord Record = record;
            internal readonly ItemInstance Native = native;
            internal readonly string Modules = modules;
            internal readonly PlayerInventoryModuleState ModuleState = PlayerInventoryBaseline.CaptureModuleState(native);
            internal readonly EquipmentState Equipment = equipment;
            internal bool SendAttempted;
        }
    }
}
