// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using PathologicalGames;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using TheForest.Items.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        internal sealed partial class RequestRouter
        {
            // Only the authenticated datagram endpoint calls this. Native cleanup is never intent.
            internal bool RouteIntent(ControlPeer peer, in PlayerWorldFrame request,
                out InventoryStorage transfer, out string detail)
            {
                transfer = null;
                detail = "World intent does not identify the current authenticated peer and actor.";
                if (!CanRouteWorldIntent(peer, request)) return false;
                _routing = true;
                try { return _authority.TryQueueNativeWorldIntent(request, out transfer, out detail); }
                finally { _routing = false; }
            }

            private bool CanRouteWorldIntent(ControlPeer peer, in PlayerWorldFrame request)
                => Current() && !_routing && ReferenceEquals(peer, _bridge.Peer)
                    && request.Kind == PlayerWorldMessage.Request && request.Transaction == 0
                    && request.MigrationId == _bridge.Plan.Id && request.Generation == _bridge.Plan.Generation
                    && request.NetworkId == _bridge.NetworkId
                    && request.Fingerprint == _authority.AcceptedBaselineFingerprint;

            internal bool TryGetDropPose(Transform prefab, out Vector3 position, out Quaternion rotation)
            {
                position = default;
                rotation = Quaternion.identity;
                if (!Current() || !TryDropRadius(prefab, out float radius)) return false;
                var forward = _actor.transform.forward;
                var bounds = _body.bounds;
                if (!Finite(forward) || !Finite(bounds.center)) return false;
                // A quiet drop from the approved server body, never from a client camera or pose.
                float distance = System.Math.Max(bounds.extents.x, bounds.extents.z) + radius + _clearanceRadius;
                position = bounds.center + forward * distance;
                rotation = _actor.transform.rotation;
                return Finite(rotation) && CheckDrop(prefab, position);
            }
        }

        internal static bool TryReserveIntent(PlayerBridge bridge,
            PlayerInventoryAuthority authority, in PlayerInventoryFrame action,
            in PlayerWorldIntent intent, out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "Only one exact plain world transfer is supported.";
            if (!TryPrepare(bridge, authority, action, intent.Action, out var candidate, out detail)
                || intent.ItemId != action.ItemId
                || PlayerInventoryWire.HashModules(candidate.Modules) != intent.ModuleFingerprint) return false;
            bool captured;
            switch (intent.Action)
            {
                case TransferKind.Pickup:
                    captured = intent.Key == 0 && intent.Slot == -1
                        && candidate.CaptureWorld(BoltNetwork.FindEntity(new NetworkId(intent.World)))
                        && candidate.CapturePickup();
                    break;
                case TransferKind.Withdrawal:
                case TransferKind.Deposit:
                    captured = candidate.CaptureStorage(intent.World, intent.Slot)
                        && (intent.Action == TransferKind.Withdrawal
                            ? intent.Key == 0 && candidate._beforeCount > 0
                            : intent.Key == action.Key && candidate._beforeCount < candidate._maximumCount
                                && candidate._slot.ValidateItemPlacementCallback == null);
                    break;
                case TransferKind.Drop:
                    var prefab = candidate.Item.Data.PickupPrefab;
                    captured = intent.World == 0 && intent.Slot == -1 && intent.Key == action.Key
                        && Live(prefab) && ItemUtils.IsValidForCurrentGameMode(candidate.Item.Data)
                        && PoolManager.Pools != null && !PoolManager.Pools.WasCollected
                        && PoolManager.Pools.TryGetValue("PickUps", out var pool) && Live(pool)
                        && Live(prefab.GetComponent<BoltEntity>()) && Live(prefab.GetComponent<CoopDynamicPickUp>())
                        && PlainPickupShape(prefab.GetComponentInChildren<PickUp>(), intent.ItemId);
                    if (!captured) return false;
                    candidate._dropPrefab = prefab;
                    captured = candidate._router.TryGetDropPose(prefab, out candidate._dropPosition, out candidate._dropRotation);
                    break;
                default: return false;
            }
            if (!captured || !candidate.AccessCurrent() || !candidate.Register()) return false;
            transfer = candidate;
            detail = "Exact typed world intent reserved; native source/destination receipts remain required.";
            return true;
        }

        internal static bool TryDescribePlain(int id, out string fingerprint)
        {
            fingerprint = null;
            if (!TryPlainItem(id, out _, out var modules)) return false;
            fingerprint = PlayerInventoryWire.HashModules(modules);
            return true;
        }

        internal static bool TryDescribeSourcePickup(PickUp pickup, out ulong world, out string fingerprint)
        {
            world = 0;
            fingerprint = null;
            if (!Live(pickup) || !PlainPickupShape(pickup, pickup._itemId)
                || !pickup.gameObject.activeInHierarchy || pickup._forceDisabled || pickup._isCollected
                || pickup._isBeingDestroyed || pickup._pendingDestroyThroughBolt || pickup._Used_k__BackingField
                || pickup._remainingItemCountNotCollected != 0 || !IsUnpooledPickup(pickup)) return false;
            var entity = pickup.GetComponent<BoltEntity>();
            var dynamicPickup = pickup.GetComponent<CoopDynamicPickUp>();
            var item = pickup._ItemInstance_k__BackingField;
            if (!Live(entity) || !entity.isAttached || entity.isOwner || entity.networkId.PackedValue == 0
                || !Same(entity.gameObject, pickup.gameObject) || !Same(pickup._destroyTarget, pickup.gameObject)
                || !Same(BoltNetwork.FindEntity(entity.networkId), entity) || !Live(dynamicPickup)
                || !Same(dynamicPickup.entity, entity) || dynamicPickup._bundleCount is < 0 or > 1
                || !Plain(item, pickup._itemId)) return false;
            world = entity.networkId.PackedValue;
            fingerprint = PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(item));
            return true;
        }
    }

    // Kept with the typed world helper: no synthetic Bolt.Event or invented RaisedBy is needed.
    public sealed partial class PlayerInventoryAuthority
    {
        internal bool TryQueueNativeWorldIntent(in PlayerWorldFrame request,
            out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "World intent requires idle exact server inventory custody.";
            var intent = request.Intent;
            if (!Ready(out _) || _worldTransfer != null || _lastTransaction == ulong.MaxValue
                || MutationEnforcement == null || !MutationEnforcement.CanQueue
                || !PlainStorageDefinition(intent.ItemId)) return false;
            bool grant = intent.IsGrant;
            if (grant)
            {
                long stored = 0;
                foreach (var item in _items.Values)
                    if (!item.Consumed && item.Id == intent.ItemId && item.HeldSlot < 0
                        && !item.Clothing && item.ArmourSlot < 0) stored += item.Quantity;
                if (intent.Key != 0 || stored != intent.BeforeQuantity) return false;
            }
            else if (!_items.TryGetValue(intent.Key, out var selected) || selected.Id != intent.ItemId
                || selected.Item != null || selected.Quantity != intent.BeforeQuantity || selected.Consumed
                || selected.HeldSlot >= 0 || selected.Clothing || selected.ArmourSlot >= 0) return false;
            ulong transaction = _lastTransaction + 1;
            if (!(grant ? TryQueueEnforcedGrant(transaction, intent.ItemId, out detail)
                : TryQueueEnforcedRemoval(transaction, intent.Key, 1, out detail))) return false;
            var action = grant ? _grant.Frame : _removal.Frame;
            try
            {
                if (InventoryStorage.TryReserveIntent(_bridge, this, action, intent, out transfer, out detail))
                {
                    _worldTransfer = transfer;
                    if (grant)
                    {
                        var previous = _grant;
                        _grant = new PendingGrant(previous.Frame, previous.Record, transfer.Item, transfer.Modules, previous.Equipment);
                        MutationEnforcement.ReleaseDetachedModules(previous.Native);
                    }
                    Detail = detail;
                    return true;
                }
            }
            catch (Exception error) { detail = "World intent reservation failed: " + error.Message; }
            if (transfer != null || !MutationEnforcement.CancelUnsentMutation(action))
            {
                transfer?.HoldUnconfirmed("World intent reservation failed after custody was retained.");
                Fault("World reservation is uncertain; never replay the source action.");
                detail = Detail;
                return false;
            }
            if (grant) { MutationEnforcement.ReleaseDetachedModules(_grant.Native); _grant = null; }
            else _removal = null;
            return false;
        }
    }
}
