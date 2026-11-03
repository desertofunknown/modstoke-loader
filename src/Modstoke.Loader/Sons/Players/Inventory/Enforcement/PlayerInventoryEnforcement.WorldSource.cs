// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using UnityEngine;
using WorldAction = Modstoke.Sons.InventoryStorage.TransferKind;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        internal static void ValidateWorldModule(ItemInstanceModule module) => RequireKnownModule(module);

        /// <summary>Request a quiet server-placed drop while the exact source key is still stowed.</summary>
        public bool TryRequestNativeWorldDrop(ulong instanceKey, out ulong request, out string detail)
        {
            request = 0;
            detail = "Quiet drop requires an idle guarded source and one exact plain stored key.";
            if (!WorldSourceReady(0)) return false;
            try
            {
                foreach (var item in _sourceItems)
                    if (item.Entry.Key == instanceKey && WorldPlainStored(item)
                        && InventoryStorage.TryDescribePlain(item.Entry.ItemId, out var fingerprint))
                        return StartWorldIntent(new(WorldAction.Drop, 0, instanceKey, item.Entry.ItemId,
                            item.Quantity, -1, fingerprint), out request, out detail);
                return false;
            }
            catch (Exception error) { detail = "Quiet drop intent declined: " + error.Message; return false; }
        }

        private bool WorldSourceReady(int depth) => OnThread && _writerDepth == depth && !_busy
            && !IsDisposed && !IsFaulted && !_worldRetired && IsArmed && _authority == null
            && !SourceActionInputHeld
            && _bridge?.IsGameplayActive == true && !HasPendingCustody && !HasPendingNativeWorldIntent
            && !HasPendingNativeCraftPresentation && _worldNextRequest < ulong.MaxValue;

        private static bool WorldPlainStored(SourceItem item) => item.Quantity > 0 && Stowed(item.Entry)
            && item.Instance == null && item.Entry.Modules == null;

        private SourceItem WorldStoredById(int id)
        {
            foreach (var item in _sourceItems)
                if (item.Entry.ItemId == id && WorldPlainStored(item)) return item;
            return null;
        }

        private int WorldStoredCount(int id)
        {
            long count = 0;
            foreach (var item in _sourceItems)
                if (item.Entry.ItemId == id && Stowed(item.Entry)) count += item.Quantity;
            return count <= int.MaxValue ? (int)count : -1;
        }

        private bool StartWorldIntent(in PlayerWorldIntent intent, out ulong request, out string detail)
        {
            request = 0;
            detail = "Source custody changed or its lifecycle is fenced.";
            if (PlayerLifecycle.IsInventoryIngressFenced(_inventory, out _)
                || !Current() || !Unchanged()) return false;
            request = ++_worldNextRequest;
            _worldSourceRequest = new(_baseline.Plan.Id, _baseline.Plan.Generation, _bridge.NetworkId,
                _baseline.Fingerprint, request, intent, PlayerWorldMessage.Request);
            _worldSourceReply = default;
            _worldSourcePending = true;
            _worldStarted = Stopwatch.GetTimestamp();
            _worldProgress = LastCompletedTransaction;
            _worldSent = 0;
            detail = Detail = "World action captured before mutation; exact server reservation is pending.";
            _worldIntentReceipt = new(request, NativeWorldIntentState.Pending, 0, intent.World, detail);
            return true;
        }

        private void RequestWorldPickup(PickUp pickup)
        {
            if (!WorldSourceReady(1)) return;
            try
            {
                if (!InventoryStorage.TryDescribeVariantPickup(pickup, false, out ulong world, out var donor))
                { Detail = "Pickup shape, modules or native effects are not qualified for world authority."; return; }
                int before = WorldStoredCount(pickup._itemId);
                if (before < 0) return;
                StartWorldIntent(new(WorldAction.Pickup, world, 0, pickup._itemId, before, -1,
                    PlayerInventoryWire.HashModules(donor.Modules)), out _, out _);
            }
            catch (Exception error) { Detail = "Pickup intent declined before native mutation: " + error.Message; }
        }

        private void RequestWorldStoredDrop(int id)
        {
            if (!WorldSourceReady(1)) return;
            try
            {
                var item = WorldStoredById(id);
                if (item == null || !InventoryStorage.TryDescribePlain(id, out var fingerprint)) return;
                StartWorldIntent(new(WorldAction.Drop, 0, item.Entry.Key, id, item.Quantity, -1, fingerprint), out _, out _);
            }
            catch (Exception error) { Detail = "Stored drop intent declined before spawning: " + error.Message; }
        }

        private void RequestWorldStorage(InWorldLayoutItemGroup group, bool deposit)
        {
            if (!WorldSourceReady(1)) return;
            try
            {
                if (!InventoryStorage.WorldGroupCurrent(group, deposit)) return;
                var storage = group._storage;
                if (storage == null || storage.WasCollected || !storage || storage.StorageSlotIndex is < 0 or > 11) return;
                var entity = storage._boltEntity;
                if (entity == null || entity.WasCollected || !entity || !entity.isAttached || entity.isOwner
                    || entity.networkId.PackedValue == 0 || BoltNetwork.FindEntity(entity.networkId)?.Pointer != entity.Pointer) return;
                int id = group._itemId;
                if (!InventoryStorage.TrySelectWorldDonor(group, _manager, deposit, out var donor)) return;
                SourceItem item = null;
                if (deposit)
                {
                    foreach (var candidate in _sourceItems)
                    {
                        if (candidate.Entry.ItemId != id || candidate.Quantity < 1 || !Stowed(candidate.Entry)) continue;
                        if (donor.Data._eachInstanceIsUnique
                            ? candidate.Instance == null || candidate.Instance.WasCollected || candidate.Instance.Pointer != donor.Pointer
                            : !WorldPlainStored(candidate)) continue;
                        item = candidate;
                        break;
                    }
                    if (item == null) return;
                }
                string fingerprint = PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(donor));
                int before = deposit ? item.Quantity : WorldStoredCount(id);
                if (before < 0) return;
                StartWorldIntent(new(deposit ? WorldAction.Deposit : WorldAction.Withdrawal, entity.networkId.PackedValue,
                    item?.Entry.Key ?? 0, id, before, storage.StorageSlotIndex, fingerprint), out _, out _);
            }
            catch (Exception error) { Detail = "Storage intent declined before native mutation: " + error.Message; }
        }
    }
}
