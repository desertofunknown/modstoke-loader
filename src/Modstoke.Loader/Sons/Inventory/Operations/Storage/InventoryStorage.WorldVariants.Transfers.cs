// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        private bool CaptureVariantStorage()
        {
            if (!WorldGroupCurrent(_variant.Group, Kind == TransferKind.Deposit)
                || _variant.Group._itemId != Action.ItemId || !Same(_variant.Group._storage, _slot)
                || !TryReadWorldBucket(_manager, Action.ItemId, out var bucket) || !Same(bucket, _entry)) return false;
            _variant.Placement = _slot.ValidateItemPlacementCallback;
            _variant.Added = _manager.ItemsAddedEvent;
            _variant.Removed = _manager.ItemsRemovedEvent;
            _variant.ArrowMatching = _variant.Group._validateArrowType;
            var unique = _entry.UniqueInstances;
            _variant.UniqueList = unique;
            _variant.UniqueBefore = new ItemInstance[unique?.Count ?? 0];
            var seen = new System.Collections.Generic.HashSet<nint>();
            bool found = false;
            for (int i = 0; i < _variant.UniqueBefore.Length; i++)
            {
                var item = unique[i];
                if (!StorableWorldItem(item) || item._itemID != Action.ItemId || !seen.Add(item.Pointer)) return false;
                _variant.UniqueBefore[i] = item;
                if (Same(item, _variant.Donor.Native)) found = true;
            }
            return !Item.Data._eachInstanceIsUnique || (Kind == TransferKind.Withdrawal ? found : !found);
        }

        private static bool SameOptional(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase a,
            Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b) => a == null ? b == null : Same(a, b);

        private bool VariantStorageCurrent(int count)
        {
            if (!_variant.Escrow.Current() || !WorldGroupCurrent(_variant.Group, Kind == TransferKind.Deposit)
                || _variant.Group._itemId != Action.ItemId || !Same(_variant.Group._storage, _slot)
                || _variant.Group._validateArrowType != _variant.ArrowMatching
                || !SameOptional(_slot.ValidateItemPlacementCallback, _variant.Placement)
                || !SameOptional(_manager.ItemsAddedEvent, _variant.Added)
                || !SameOptional(_manager.ItemsRemovedEvent, _variant.Removed)) return false;
            if (_variant.Destination != null && (!_variant.Destination.Current()
                || !_variant.Escrow.Matches(_variant.Destination.Native))) return false;
            if (Kind == TransferKind.Deposit && _variant.ArrowMatching && _beforeCount > 0
                && (_entry.UniqueInstances == null || _entry.UniqueInstances.Count == 0
                    || !TryReadArrowType(_entry.UniqueInstances[0], out var storedType)
                    || !TryReadArrowType(Item, out var donorType) || storedType != donorType)) return false;
            if (!Item.Data._eachInstanceIsUnique) return NoUniqueItems(_entry);
            var unique = _entry.UniqueInstances;
            if (unique == null || unique.WasCollected || !Same(unique, _variant.UniqueList)
                || unique.Count != count || _variant.UniqueBefore == null) return false;
            int difference = count - _beforeCount;
            if (difference != 0 && difference != (Kind == TransferKind.Withdrawal ? -1 : 1)) return false;
            int index = 0;
            foreach (var item in _variant.UniqueBefore)
            {
                if (difference == -1 && Same(item, _variant.Donor.Native)) continue;
                if (index >= unique.Count || !Same(unique[index++], item)) return false;
            }
            if (difference == 1 && (_variant.Destination == null || !_variant.Destination.Current()
                || !_variant.Escrow.Matches(_variant.Destination.Native)
                || index >= unique.Count || !Same(unique[index++], _variant.Destination.Native))) return false;
            return index == unique.Count && (Kind != TransferKind.Withdrawal || _variant.Donor.Current());
        }

        internal bool TryValidateWorldVariantPlacement(out string detail)
        {
            detail = Detail;
            if (!IsWorldVariant || Kind != TransferKind.Deposit || Environment.CurrentManagedThreadId != _thread
                || _busy || State != TransferState.Reserved || _variant.PlacementAttempted) return false;
            _busy = true;
            try
            {
                if (!Current() || !WorldItemCurrent() || !AccessCurrent() || !StorageCurrent(_beforeCount)) return false;
                // The native callback is an action, not a reusable approval token. Invoke it
                // once under the exact destination hold, before dispatching the player debit.
                _variant.PlacementAttempted = true;
                if (Item.Data._eachInstanceIsUnique)
                {
                    _variant.Escrow.CreateDetached(out _variant.DestinationItem);
                    _variant.Destination = new(_variant.DestinationItem);
                }
                bool accepted = _slot.ValidateItemPlacement(_variant.Destination?.Native ?? Item);
                if (!Current() || !WorldItemCurrent() || !StorageCurrent(_beforeCount))
                    return Uncertain("Native placement callback changed reserved custody.", out detail);
                _variant.PlacementAccepted = accepted;
                Detail = detail = accepted ? "Native placement callback accepted the exact retained modules."
                    : "Native placement callback declined before any player debit.";
                return accepted;
            }
            catch (Exception error) { return Uncertain("Native placement callback is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        // BeforeRemoveInstance calls this only in its held-manager
        // branch, after EnterWriter. No second detour and no general manager exemption.
        internal static bool PermitWorldVariantRemoval(ItemInstanceManager manager, ItemInstance item)
        {
            if (manager == null || manager.WasCollected || !ManagerHolds.TryGetValue(manager.Pointer, out var transfer)
                || transfer._load != SaveLifecycle.State.BeforeLoadCount || !transfer.IsWorldVariant
                || !transfer._busy || Environment.CurrentManagedThreadId != transfer._thread
                || !transfer._variant.RemovePermit || transfer.State != TransferState.TakingSource
                || PlayerInventoryEnforcement.WorldWriterDepth != transfer._variant.RemoveDepth + 1
                || !Same(item, transfer._variant.Donor.Native)) return false;
            transfer._variant.RemovePermit = false;
            return true;
        }

        private int RemoveVariantStorageItem()
        {
            _variant.RemovePermit = true;
            _variant.RemoveDepth = PlayerInventoryEnforcement.WorldWriterDepth;
            try
            {
                bool removed = _manager.TryRemoveItem(_variant.Donor.Native);
                return removed && !_variant.RemovePermit ? 1 : 0;
            }
            finally { _variant.RemovePermit = false; }
        }

        internal static bool PermitWorldVariantAddition(ItemInstanceManager manager, ItemInstance item, bool suppressCallback)
        {
            if (manager == null || manager.WasCollected || !ManagerHolds.TryGetValue(manager.Pointer, out var transfer)
                || transfer._load != SaveLifecycle.State.BeforeLoadCount || !transfer.IsWorldVariant
                || !transfer._busy || Environment.CurrentManagedThreadId != transfer._thread
                || suppressCallback || !transfer._variant.AddPermit || transfer.State != TransferState.ApplyingDestination
                || PlayerInventoryEnforcement.WorldWriterDepth != transfer._variant.AddDepth + 1
                || transfer._variant.Destination == null || !Same(item, transfer._variant.Destination.Native)) return false;
            transfer._variant.AddPermit = false;
            return true;
        }

        private bool AddVariantStorageItem()
        {
            // World modules must keep their own native updater registration. Never put the
            // paused ledger copy into storage, where releasing the ledger would unregister it.
            // Use the SAME instance the native placement callback inspected before debit.
            if (_variant.Destination == null || !_variant.Destination.Current()
                || !_variant.Escrow.Matches(_variant.Destination.Native)) return false;
            var destination = _variant.Destination.Native;
            _variant.AddPermit = true;
            _variant.AddDepth = PlayerInventoryEnforcement.WorldWriterDepth;
            try
            {
                bool added = _manager.TryAddItem(destination, false);
                return added && !_variant.AddPermit;
            }
            finally { _variant.AddPermit = false; }
        }

        internal bool TryCancelWorldVariantBeforeMutation(out string detail)
        {
            detail = Detail;
            if (!IsWorldVariant || Environment.CurrentManagedThreadId != _thread || _busy
                || State != TransferState.Reserved) return false;
            _busy = true;
            try
            {
                if (_variant.DestinationItem != null)
                {
                    _authority.MutationEnforcement.RetainPausedModules(_variant.DestinationItem);
                    _authority.MutationEnforcement.ReleaseDetachedModules(_variant.DestinationItem);
                }
                Close(TransferState.Cancelled);
                Detail = detail = "Unused world reservation cancelled and its unattached destination modules released.";
                return true;
            }
            catch (Exception error) { return Uncertain("Unused world destination cleanup failed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        // These dispatchers preserve the old plain path. The authority owns when each
        // authenticated frame is delivered, and must use the dispatchers for both paths.
        internal bool TryTakeWorldSource(in PlayerInventoryFrame ready, out string detail)
        {
            if (!IsWorldVariant) return TryTakeSource(ready, out detail);
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.Reserved
                || !GrantReceipt(ready, PlayerInventoryMessage.GrantReady)) return false;
            _busy = true;
            try
            {
                if (!Current() || !WorldItemCurrent() || !_variant.Donor.Current()) return false;
                return Kind == TransferKind.Pickup ? TakePickup(out detail) : TakeStorage(out detail);
            }
            catch (Exception error) { return Uncertain("Exact world donor retirement failed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        internal bool TryBeginWorldRemoval(out string detail)
        {
            if (!IsWorldVariant) return TryBeginRemoval(out detail);
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.Reserved
                || Kind != TransferKind.Deposit) return false;
            _busy = true;
            try
            {
                if (!Current() || !WorldItemCurrent() || !_variant.Donor.Current() || !AccessCurrent()
                    || !_variant.PlacementAccepted || !StorageCurrent(_beforeCount)) return false;
                State = TransferState.RemovalRequested;
                Detail = detail = "Exact module-bearing removal dispatch reserved; no second source debit is permitted.";
                return true;
            }
            catch (Exception error) { detail = "World removal preparation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal bool TrySendWorldGrant(out string detail)
        {
            if (!IsWorldVariant) return TrySendGrant(out detail);
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.SourceTaken
                || Kind is not (TransferKind.Withdrawal or TransferKind.Pickup)) return false;
            _busy = true;
            PlayerAddItem request = null;
            bool attempted = false;
            try
            {
                if (!Current() || !WorldItemCurrent()) return Uncertain("World escrow changed before grant.", out detail);
                // SourceTaken proves removal/destruction. Only now may the retired world's
                // own updater roots be released; the distinct escrow remains ledger-owned.
                _authority.MutationEnforcement.RetainPausedModules(_variant.Donor.Native);
                _authority.MutationEnforcement.ReleaseDetachedModules(_variant.Donor.Native);
                var connection = ControlTransport.Resolve(_bridge.Peer);
                if (connection == null) return Uncertain("World grant connection ended.", out detail);
                var token = Item.GetBoltDataToken();
                if (token == null || token.WasCollected || !_variant.Escrow.Matches(token.ItemInstance))
                    return Uncertain("Native token lost the reserved donor modules.", out detail);
                request = PlayerAddItem.Create(connection, ReliabilityModes.ReliableOrdered);
                request.Player = _actor;
                request.ItemId = Action.ItemId;
                request.Amount = 1;
                request.ItemInstance = token.Cast<IProtocolToken>();
                if (!Current() || !WorldItemCurrent()) return Uncertain("World grant changed before send.", out detail);
                State = TransferState.SendAttempted;
                _grantSendAttempted = attempted = true;
                request.Send();
                if (!Current()) return Uncertain("Actor retired during world grant send.", out detail);
                Detail = detail = "Exact donor token sent once after native source retirement; receipt pending.";
                return true;
            }
            catch (Exception error) { return Uncertain("World grant is unconfirmed: " + error.Message, out detail); }
            finally
            {
                if (request != null && !attempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception) { Uncertain("Unsent world grant storage could not be freed.", out detail); }
                }
                _busy = false;
            }
        }

        internal bool TryCompleteWorldGrant(in PlayerInventoryFrame applied, out string detail)
        {
            if (!IsWorldVariant) return TryCompleteGrant(applied, out detail);
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.SendAttempted
                || !GrantReceipt(applied, PlayerInventoryMessage.GrantApplied)) return false;
            _busy = true;
            try
            {
                if (!Current() || !WorldItemCurrent() || !_authority.WorldVariantGrantMatches(Action, Item))
                    return Uncertain("World grant has no matching exact ledger instance receipt.", out detail);
                Close(TransferState.Committed);
                Detail = detail = "Exact world donor and module-bearing inventory grant settled once.";
                return true;
            }
            catch (Exception error) { return Uncertain("World grant settlement failed: " + error.Message, out detail); }
            finally { _busy = false; }
        }
    }
}
