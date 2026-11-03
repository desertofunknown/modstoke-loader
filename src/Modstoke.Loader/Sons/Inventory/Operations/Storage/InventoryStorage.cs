// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// One reserved server inventory action's native world custody. The integrator owns the
    /// inventory ledger and authenticated receipt dispatch; this helper never mints a ledger
    /// grant or debits a player. Suppress the replaced native event before using it.
    /// Only single, module-free nonunique items are supported. No general storage coverage.
    /// </summary>
    internal sealed partial class InventoryStorage
    {
        internal enum TransferKind { Withdrawal, Deposit, Pickup, Drop }
        internal enum TransferState
        {
            Reserved, RemovalRequested, TakingSource, SourceRetiring, SourceTaken, SendAttempted,
            ApplyingDestination, WorldApplied, Committed, Cancelled, Unconfirmed
        }

        private const int MaximumPending = 64;
        private static readonly HashSet<InventoryStorage> Pending = new();
        private static readonly ConditionalWeakTable<PlayerInventoryAuthority, Cursor> Cursors = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerBridge _bridge;
        private readonly PlayerInventoryAuthority _authority;
        private readonly BoltEntity _actor;
        private readonly int _actorInstanceId;
        private readonly Cursor _cursor;
        private readonly RequestRouter _router;
        private bool _busy;
        private bool _grantSendAttempted;
        private BoltEntity _world;
        private GameObject _worldObject;
        private ulong _worldId;
        private int _worldInstanceId;

        internal TransferKind Kind { get; }
        internal TransferState State { get; private set; } = TransferState.Reserved;
        internal PlayerInventoryFrame Action { get; }
        internal ItemInstance Item { get; }
        internal string Modules { get; }
        internal string Detail { get; private set; }
        internal ulong WorldNetworkId => _worldId;
        internal bool GrantSendAttempted => _grantSendAttempted;

        private InventoryStorage(PlayerBridge bridge,
            PlayerInventoryAuthority authority, in PlayerInventoryFrame action,
            TransferKind kind, BoltEntity actor, ItemInstance item, string modules, Cursor cursor)
        {
            _bridge = bridge;
            _authority = authority;
            _actor = actor;
            _actorInstanceId = actor.GetInstanceID();
            _cursor = cursor;
            _router = cursor.Router;
            Action = action;
            Kind = kind;
            Item = item;
            Modules = modules;
            Detail = "Reserved action; no native custody mutation has been attempted.";
        }

        // Call only after reserving this exact action in the inventory authority, before sending
        // its prepare/removal command. A failed reservation must not fall through to vanilla.
        private static bool TryPrepare(PlayerBridge bridge,
            PlayerInventoryAuthority authority, in PlayerInventoryFrame action,
            TransferKind kind, out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "A reserved, armed server action for one plain item and a current remote actor is required.";
            if (bridge == null || authority == null || !bridge.IsServer || bridge.IsDisposed
                || authority.IsDisposed || authority.IsFaulted || !authority.HasAcceptedBaseline
                || authority.MutationEnforcement == null || !authority.MutationEnforcement.IsArmed
                || authority.MutationEnforcement.IsDisposed || authority.MutationEnforcement.IsFaulted
                || authority.MutationEnforcement.Baseline.Plan != bridge.Plan
                || authority.LastTransaction != action.Transaction || action.Transaction == 0 || action.Key == 0
                || action.MigrationId != bridge.Plan.Id || action.Generation != bridge.Plan.Generation
                || action.NetworkId != bridge.NetworkId || action.Fingerprint != authority.AcceptedBaselineFingerprint
                || action.Quantity != 1 || PendingFull()
                || !bridge.TryReadCurrentTarget(out var actor) || !Live(actor)
                || !BoltNetwork.isRunning || !BoltNetwork.isServer
                || ControlTransport.Resolve(bridge.Peer) == null) return false;
            bool grant = kind is TransferKind.Withdrawal or TransferKind.Pickup;
            if (grant ? action.Kind != PlayerInventoryMessage.GrantPrepare || action.Key != action.Transaction
                    || action.BeforeQuantity < 0
                : action.Kind != PlayerInventoryMessage.Remove || action.BeforeQuantity < 1) return false;
            var cursor = Cursors.GetValue(authority, _ => new Cursor());
            if (cursor.Active != null || action.Transaction <= cursor.LastTransaction
                || cursor.Router == null || !cursor.Router.Current()) return false;
            if (!TryPlainItem(action.ItemId, out var item, out var modules)) return false;
            if (grant && action.ModuleFingerprint != PlayerInventoryWire.HashModules(modules)) return false;
            transfer = new(bridge, authority, in action, kind, actor, item, modules, cursor);
            return true;
        }

        private bool Register()
        {
            if (!Current() || _cursor.Active != null || Action.Transaction <= _cursor.LastTransaction
                || PendingFull()) return false;
            if (HasCompetingCustody()) return false;
            if (_manager != null && !ManagerHolds.TryAdd(_manager.Pointer, this)
                && (!ManagerHolds.TryGetValue(_manager.Pointer, out var previous) || previous._load == _load
                    || !ManagerHolds.TryUpdate(_manager.Pointer, this, previous))) return false;
            _cursor.LastTransaction = Action.Transaction;
            _cursor.Active = this;
            Pending.Add(this);
            return true;
        }

        // Holds from an earlier load no longer fence any world object, so they must not fill the bound.
        private static bool PendingFull()
        {
            Pending.RemoveWhere(pending => pending._load != SaveLifecycle.State.BeforeLoadCount);
            return Pending.Count >= MaximumPending;
        }

        /// <summary>
        /// The upstream router must fence competing actors, including vanilla actors, while a
        /// selected world object/slot is held. -1 selects a pickup, or all slots of a structure.
        /// This does not enroll or intercept unrelated vanilla actors.
        /// </summary>
        internal static bool IsWorldHeld(BoltEntity world, int slotIndex = -1)
        {
            if (!HasWorldCustody || !Live(world) || !world.isAttached) return false;
            return IsWorldHeld(world.networkId.PackedValue, slotIndex);
        }

        internal static bool HasWorldCustody
        {
            get
            {
                if (Pending.Count == 0) return false;
                foreach (var pending in Pending)
                    if (pending.HasCurrentWorldCustody) return true;
                return false;
            }
        }

        private bool HasCurrentWorldCustody
            => _worldId != 0 && _load == SaveLifecycle.State.BeforeLoadCount;

        internal static bool IsWorldHeld(ulong networkId, int slotIndex = -1)
        {
            if (networkId == 0) return false;
            foreach (var pending in Pending)
                if (pending.HasCurrentWorldCustody
                    && pending._worldId == networkId
                    && (slotIndex < 0 || pending._slotIndex < 0 || pending._slotIndex == slotIndex)) return true;
            return false;
        }

        private bool HasCompetingCustody()
        {
            foreach (var pending in Pending)
                if (!ReferenceEquals(pending, this) && pending._load == _load
                    && (_worldId != 0 && pending._worldId == _worldId
                        && (_slotIndex < 0 || pending._slotIndex < 0 || _slotIndex == pending._slotIndex)
                        || Same(_manager, pending._manager))) return true;
            return false;
        }

        /// <summary>Accept only the authenticated exact GrantReady frame, never a raw client request.</summary>
        internal bool TryTakeSource(in PlayerInventoryFrame ready, out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.Reserved
                || !GrantReceipt(ready, PlayerInventoryMessage.GrantReady))
                return false;
            _busy = true;
            try
            {
                if (!Current() || !ItemCurrent()) return false;
                if (Kind == TransferKind.Withdrawal) return TakeStorage(out detail);
                if (Kind == TransferKind.Pickup) return TakePickup(out detail);
                return false;
            }
            catch (Exception error) { return Uncertain("Source transfer failed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        /// <summary>
        /// Only send through this method, not Authority.DeliverEnforcedGrant as well. Its native
        /// event is matched by the already-prepared source grant guard. Send is not an applied receipt.
        /// </summary>
        internal bool TrySendGrant(out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.SourceTaken
                || Kind is not (TransferKind.Withdrawal or TransferKind.Pickup))
                return false;
            _busy = true;
            PlayerAddItem request = null;
            bool attempted = false;
            try
            {
                if (!Current() || !ItemCurrent()) return Uncertain("Grant destination or escrow identity changed.", out detail);
                var connection = ControlTransport.Resolve(_bridge.Peer);
                if (connection == null) return Uncertain("Grant connection ended after source removal.", out detail);
                var token = Item.GetBoltDataToken();
                if (token == null || token.WasCollected || !Plain(token.ItemInstance, Action.ItemId)
                    || !PlayerInventoryBaseline.JsonEqual(Modules,
                        PlayerInventoryBaseline.CaptureModules(token.ItemInstance)))
                    return Uncertain("Exact escrow token is unavailable.", out detail);
                request = PlayerAddItem.Create(connection, ReliabilityModes.ReliableOrdered);
                request.Player = _actor;
                request.ItemId = Action.ItemId;
                request.Amount = 1;
                request.ItemInstance = token.Cast<IProtocolToken>();
                if (!Current() || !ItemCurrent()) return Uncertain("Grant identity changed before send.", out detail);
                State = TransferState.SendAttempted;
                _grantSendAttempted = true;
                attempted = true;
                request.Send();
                if (!Current()) return Uncertain("Actor retired during grant send.", out detail);
                Detail = detail = "Source removed and exact native grant send attempted once; applied receipt is pending.";
                return true;
            }
            catch (Exception error) { return Uncertain("Grant delivery is unconfirmed: " + error.Message, out detail); }
            finally
            {
                if (request != null && !attempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception) { Uncertain("Unsent grant event storage could not be released.", out detail); }
                }
                _busy = false;
            }
        }

        /// <summary>
        /// Call after the inventory integrator credits the same authenticated frame's reserved
        /// record. This checks the resulting ledger and never calls TryGrantItem.
        /// </summary>
        internal bool TryCompleteGrant(in PlayerInventoryFrame applied, out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.SendAttempted
                || !GrantReceipt(applied, PlayerInventoryMessage.GrantApplied))
                return false;
            _busy = true;
            try
            {
                if (!Current() || !ItemCurrent()) return Uncertain("Grant receipt arrived for a changed actor or escrow.", out detail);
                if (!_authority.TryReadItem(Action.Key, out var item) || item.ItemId != Action.ItemId
                    || item.Quantity != 1 || item.Consumed)
                    return Uncertain("Grant receipt has no matching committed inventory record.", out detail);
                Close(TransferState.Committed);
                Detail = detail = "Exact applied grant receipt settled world custody; no second grant was issued.";
                return true;
            }
            catch (Exception error) { return Uncertain("Grant settlement failed: " + error.Message, out detail); }
            finally { _busy = false; }
        }

        /// <summary>Only before any source/removal command is allowed to execute.</summary>
        internal bool TryCancelBeforeMutation(out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.Reserved) return false;
            Close(TransferState.Cancelled);
            Detail = detail = "Unused world reservation cancelled; transaction remains consumed.";
            return true;
        }

        internal void HoldUnconfirmed(string reason)
        {
            if (Environment.CurrentManagedThreadId != _thread || State is TransferState.Committed or TransferState.Cancelled) return;
            // No source or removal command has run while Reserved, so release the world hold
            // instead of keeping it for the process lifetime. The authority still faults.
            if (State == TransferState.Reserved && !_busy
                && (IsWorldVariant ? TryCancelWorldVariantBeforeMutation(out _) : TryCancelBeforeMutation(out _)))
            {
                _authority.FaultEnforcement(reason + " The unused world reservation was released.");
                return;
            }
            Uncertain(reason, out _);
        }

        /// <summary>Call immediately before sending the reserved removal command, including on send failure.</summary>
        internal bool TryBeginRemoval(out string detail)
        {
            detail = Detail;
            if (Environment.CurrentManagedThreadId != _thread || _busy || State != TransferState.Reserved
                || Kind is not (TransferKind.Deposit or TransferKind.Drop)) return false;
            _busy = true;
            try
            {
                if (!Current() || !ItemCurrent() || !AccessCurrent()
                    || Kind == TransferKind.Deposit && !StorageCurrent(_beforeCount)) return false;
                State = TransferState.RemovalRequested;
                Detail = detail = "Player removal dispatch reserved; cancellation and duplicate native removal are forbidden.";
                return true;
            }
            catch (Exception error) { detail = "Removal dispatch preparation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        private bool GrantReceipt(in PlayerInventoryFrame receipt, PlayerInventoryMessage kind)
            => Kind is TransferKind.Withdrawal or TransferKind.Pickup
                && receipt.Kind == kind && receipt == (Action with { Kind = kind });

        private bool RemovalReceipt(in PlayerInventoryFrame receipt)
            => Kind is TransferKind.Deposit or TransferKind.Drop
                && receipt.Kind == PlayerInventoryMessage.Applied
                && receipt == (Action with { Kind = PlayerInventoryMessage.Applied });

        private bool Current()
            => Environment.CurrentManagedThreadId == _thread && !_bridge.IsDisposed && _bridge.IsServer
                && _bridge.IsGameplayActive && _router != null && _router.Current()
                && _load == SaveLifecycle.State.BeforeLoadCount
                && !_authority.IsDisposed && !_authority.IsFaulted
                && _authority.MutationEnforcement != null && !_authority.MutationEnforcement.IsDisposed
                && !_authority.MutationEnforcement.IsFaulted && _authority.MutationEnforcement.IsArmed
                && _authority.MutationEnforcement.Baseline.Plan == _bridge.Plan
                && _authority.LastTransaction == Action.Transaction
                && _authority.AcceptedBaselineFingerprint == Action.Fingerprint
                && _bridge.TryReadCurrentTarget(out var actor) && Same(actor, _actor) && Live(actor)
                && actor.GetInstanceID() == _actorInstanceId && actor.isAttached && actor.isOwner
                && BoltNetwork.isRunning && BoltNetwork.isServer
                && ControlTransport.Resolve(_bridge.Peer) != null;

        private bool ItemCurrent() => Plain(Item, Action.ItemId)
            && PlayerInventoryBaseline.JsonEqual(Modules, PlayerInventoryBaseline.CaptureModules(Item));

        private bool Uncertain(string reason, out string detail)
        {
            State = TransferState.Unconfirmed;
            Detail = detail = reason + " Custody is held; do not retry, refund, or resume the original event.";
            _authority.FaultEnforcement(Detail);
            return false;
        }

        private void Close(TransferState state)
        {
            State = state;
            Pending.Remove(this);
            if (_manager != null && ManagerHolds.TryGetValue(_manager.Pointer, out var hold) && ReferenceEquals(hold, this))
                ManagerHolds.TryRemove(_manager.Pointer, out _);
            if (ReferenceEquals(_cursor.Active, this)) _cursor.Active = null;
        }

        private bool CaptureWorld(BoltEntity entity)
        {
            if (!Live(entity) || !entity.isAttached || !entity.isOwner || entity.networkId.PackedValue == 0) return false;
            _world = entity;
            _worldObject = entity.gameObject;
            _worldId = entity.networkId.PackedValue;
            _worldInstanceId = entity.GetInstanceID();
            return WorldCurrent();
        }

        private bool WorldCurrent()
            => Live(_world) && Live(_worldObject) && _world.GetInstanceID() == _worldInstanceId
                && !HasCompetingCustody()
                && Same(_world.gameObject, _worldObject) && _world.isAttached && _world.isOwner
                && _world.networkId.PackedValue == _worldId
                && Same(BoltNetwork.FindEntity(new NetworkId(_worldId)), _world);

        private bool SourceMatches(Bolt.Event request)
            => request != null && !request.WasCollected
                && Same(request.RaisedBy, ControlTransport.Resolve(_bridge.Peer));

        private static bool TryPlainItem(int id, out ItemInstance item, out string modules)
        {
            item = null;
            modules = null;
            if (id <= 0 || id > ServerInventory.MaxItemId) return false;
            var data = ItemDatabaseManager.ItemById(id);
            if (!Live(data) || data.Id != id || data._eachInstanceIsUnique || data._canNotBeStored || data._maxAmount < 1)
                return false;
            item = new ItemInstance(id);
            if (!Plain(item, id)) return false;
            modules = PlayerInventoryBaseline.CaptureModules(item);
            return modules != null;
        }

        private static bool Plain(ItemInstance item, int id)
            => item != null && !item.WasCollected && item._itemID == id && Live(item.Data)
                && item.Data.Id == id && !item.Data._eachInstanceIsUnique && !item.Data._canNotBeStored
                && item._modules != null && !item._modules.WasCollected && item._modules.Count == 0;

        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase a, Il2CppObjectBase b)
            => a != null && b != null && !a.WasCollected && !b.WasCollected && a.Pointer == b.Pointer;

        private sealed class Cursor
        {
            internal ulong LastTransaction;
            internal InventoryStorage Active;
            internal RequestRouter Router;
        }
    }
}
