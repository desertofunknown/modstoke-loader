// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private ItemInstance _unreleasedWorldEscrow;

        internal bool TryQueueNativeWorldVariantIntent(in PlayerWorldFrame request,
            out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "World variant requires idle authoritative custody and an exact native donor.";
            var intent = request.Intent;
            if (!Ready(out var target) || _worldTransfer != null || _lastTransaction == ulong.MaxValue
                || MutationEnforcement == null || !MutationEnforcement.CanQueue || _pending != null
                || HasPendingNativePowerSupply || _unreleasedWorldEscrow != null
                || intent.Action == InventoryStorage.TransferKind.Drop) return false;
            ItemRecord source = null;
            ItemInstance playerDonor = null;
            if (!intent.IsGrant)
            {
                if (!_items.TryGetValue(intent.Key, out source) || source.Consumed || source.DefinitionOnly
                    || source.Id != intent.ItemId || source.Quantity != intent.BeforeQuantity || source.Quantity < 1
                    || source.HeldSlot >= 0 || source.Clothing || source.ArmourSlot >= 0 || !source.IdentityCurrent()) return false;
                playerDonor = source.Item ?? new ItemInstance(source.Id);
            }
            if (!InventoryStorage.TryResolveWorldVariant(intent, playerDonor, out var donor, out var group))
                return false;
            long total = 0, stored = 0;
            foreach (var item in _items.Values)
            {
                if (item.Consumed || item.Id != intent.ItemId) continue;
                total += item.Quantity;
                if (item.HeldSlot < 0 && !item.Clothing && item.ArmourSlot < 0) stored += item.Quantity;
            }
            if (intent.IsGrant && (intent.Key != 0 || stored != intent.BeforeQuantity || stored >= int.MaxValue
                || total >= donor.Native.Data._maxAmount || _items.Count >= MaximumIssuedItems)) return false;
            ulong transaction = _lastTransaction + 1;
            if (!Fresh(transaction) || _items.ContainsKey(transaction)) return false;
            _busy = true;
            ItemInstance escrow = null;
            bool queued = false;
            try
            {
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (equipment == null || !OwnedEquipmentMatches(equipment) || !donor.Current()) return false;
                donor.CreateDetached(out escrow);
                if (!donor.Matches(escrow)) return false;
                MutationEnforcement.RetainPausedModules(escrow);
                var record = intent.IsGrant
                    ? escrow.Data._eachInstanceIsUnique ? new ItemRecord(transaction, escrow) : new ItemRecord(transaction, intent.ItemId, 1)
                    : source;
                if (intent.IsGrant ? !record.CaptureIdentity() : !record.IdentityCurrent()) return false;
                var action = new PlayerInventoryFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    AcceptedBaselineFingerprint, transaction, intent.IsGrant ? transaction : intent.Key,
                    intent.ItemId, 1, intent.BeforeQuantity,
                    intent.IsGrant ? PlayerInventoryMessage.GrantPrepare : PlayerInventoryMessage.Remove,
                    intent.IsGrant ? intent.ModuleFingerprint : null);
                if (!Current(out var current) || current.Pointer != target.Pointer || !donor.Current()) return false;
                _lastTransaction = transaction;
                if (!InventoryStorage.TryReserveWorldVariant(_bridge, this, action, intent,
                    donor, escrow, group, out transfer, out detail)) return false;
                _worldTransfer = transfer;
                if (!intent.IsGrant && !transfer.TryValidateWorldVariantPlacement(out detail))
                {
                    if (transfer.State == InventoryStorage.TransferState.Reserved
                        && transfer.TryCancelWorldVariantBeforeMutation(out _))
                    { _worldTransfer = null; transfer = null; }
                    return false;
                }
                if (intent.IsGrant) _grant = new PendingGrant(action, record, escrow, donor.Modules, equipment);
                else _removal = new PendingRemoval(action, source,
                    source.Item == null ? null : donor.Modules, equipment);
                MutationEnforcement.QueueMutation(action);
                queued = true;
                Detail = detail = "Exact native world variant reserved with donor modules and one inventory transaction.";
                return true;
            }
            catch (Exception error)
            {
                detail = "World variant reservation failed: " + error.Message;
                if (transfer != null) transfer.HoldUnconfirmed(detail);
                return false;
            }
            finally
            {
                // An uncertain transfer retains its escrow and hold. Never release its modules
                // or allow a second action just because the managed call returned false.
                try
                {
                    if (!queued && transfer == null && escrow != null)
                    {
                        MutationEnforcement.RetainPausedModules(escrow);
                        MutationEnforcement.ReleaseDetachedModules(escrow);
                    }
                }
                catch (Exception error)
                {
                    _unreleasedWorldEscrow = escrow;
                    Fault("Unused world escrow cleanup failed: " + error.Message);
                }
                finally { _busy = false; }
            }
        }

        internal bool WorldVariantGrantMatches(in PlayerInventoryFrame action, ItemInstance escrow)
            => _items.TryGetValue(action.Key, out var item) && item.Id == action.ItemId && item.Quantity == 1
                && !item.Consumed && item.IdentityCurrent()
                && (escrow.Data._eachInstanceIsUnique ? item.Item != null && item.Item.Pointer == escrow.Pointer
                    : item.Item == null);
    }
}
