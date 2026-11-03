// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    /// <summary>
    /// Server custody for one completed player bridge. Existing items require explicit acceptance
    /// of a complete migration baseline; client observations never authorize themselves. This is
    /// not a complete native inventory interceptor: crafting, storage, death bags and independent
    /// client module changes still require server decisions. Enforced perishable timing uses the
    /// server's native clock with exact retained-instance projection. Volume consumers and the
    /// remaining ordinary transactions are not a playable full-vanilla migration mode.
    /// Retirement never removes client items.
    /// All commands must originate in trusted server gameplay/operator policy, never be forwarded
    /// from client requests without independent authorization. Call only on the game's thread.
    /// </summary>
    public sealed partial class PlayerInventoryAuthority : IDisposable
    {
        public const int MaximumIssuedItems = PlayerInventoryBaseline.MaximumEntries;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly Dictionary<ulong, ItemRecord> _items = new();
        private ulong _lastTransaction;
        private bool _busy, _faulted;
        private PendingEquipment _pending;

        public PlayerEquipment Equipment { get; private set; }
        /// <summary>Full ongoing native custody interception is not implemented.</summary>
        public bool CoversVanillaInventory => false;
        public bool HasAcceptedBaseline { get; private set; }
        public string AcceptedBaselineFingerprint { get; private set; }
        public string AcceptanceReason { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool IsFaulted => _faulted || Equipment?.IsFaulted == true || MutationEnforcement?.IsFaulted == true;
        public string Detail { get; private set; } = "Empty server-issued scope; vanilla inventory is not authorized.";
        public ulong LastTransaction => _lastTransaction;
        internal bool CanQueueNativeMutation => !HasPendingArmourCustody && !HasPendingNativePowerProjection
            && (!HasPendingNativePowerSupply || _lastTransaction < _powerSupply.Transaction);

        private PlayerInventoryAuthority(PlayerBridge bridge) { _bridge = bridge; }

        /// <summary>
        /// Owns the server equipment endpoint and retires with the exact bridge. Does not alter
        /// existing equipment, enroll clients, enable capabilities, or establish a vanilla baseline.
        /// </summary>
        public static bool TryCreateServer(PlayerBridge bridge,
            out PlayerInventoryAuthority authority, out string detail)
        {
            authority = null;
            detail = "A current exact server player bridge is required.";
            if (!PlayerRoles.IsAvailable || bridge == null || !bridge.IsServer
                || !bridge.TryReadCurrentTarget(out _)) return false;
            var candidate = new PlayerInventoryAuthority(bridge);
            if (!PlayerEquipment.TryCreateServer(bridge, candidate.Authorize,
                out var equipment, out detail)) return false;
            candidate.Equipment = equipment;
            equipment.BindInventoryAuthority(candidate);
            bridge.Retired += candidate.Dispose;
            SaveLifecycle.Lifecycle += candidate.OnSaveLifecycle;
            authority = candidate;
            detail = candidate.Detail;
            return true;
        }

        /// <summary>
        /// Mints one new server-owned instance and queues its exact token to this player. The
        /// transaction ID becomes its instance key. Repeated IDs receive different keys.
        /// Queued delivery is NOT client acceptance.
        /// With MutationEnforcement bound, capacity preparation precedes the single native send,
        /// and LastReceipt.Applied must confirm completion before the ledger gains custody.
        /// No existing client item is adopted and no previous grant is retried. A send uncertainty
        /// quarantines the account. Held publication has a narrower module contract than custody.
        /// </summary>
        public bool TryGrantItem(ulong transaction, int itemId, out string detail)
        {
            if (MutationEnforcement != null)
                return TryQueueEnforcedGrant(transaction, itemId, out detail);
            detail = "A fresh server grant/key and idle current account are required.";
            if (!Ready(out var target) || !Fresh(transaction) || _pending != null
                || itemId is <= 0 or > ServerInventory.MaxItemId
                || _items.ContainsKey(transaction) || _items.Count >= MaximumIssuedItems) return false;
            _busy = true;
            PlayerAddItem request = null;
            bool sendAttempted = false;
            try
            {
                var state = target.GetState<IPlayerState>();
                if (state == null || state.WasCollected
                    || !TryReadIds(state.HeldItems, out var held)
                    || !TryReadIds(state.PlayerClothingIds, out var clothing)
                    || !TryReadIds(state.PlayerArmourIds, out var armour)
                    || (!HasAcceptedBaseline && (Array.IndexOf(held, itemId) >= 0 || Array.IndexOf(clothing, itemId) >= 0
                        || Array.IndexOf(armour, itemId) >= 0)) || !OwnedEquipmentMatches(EquipmentState.Capture(state)))
                {
                    detail = "Cannot issue an ID already represented by unowned equipment or unreadable state.";
                    return false;
                }
                var data = ItemDatabaseManager.ItemById(itemId);
                if (data == null || data.WasCollected || !data || data.Id != itemId) return false;
                _lastTransaction = transaction;
                var record = new ItemRecord(transaction, new ItemInstance(itemId));
                if (!record.CaptureIdentity())
                {
                    detail = "The new instance has unsupported or unavailable module identity; nothing was granted.";
                    return false;
                }
                var token = record.Item.GetBoltDataToken();
                if (token == null || token.WasCollected || !record.IdentityCurrent()) return false;
                var connection = ControlTransport.Resolve(_bridge.Peer);
                if (connection == null || !Current(out var current) || current.Pointer != target.Pointer) return false;
                request = PlayerAddItem.Create(connection, ReliabilityModes.ReliableOrdered);
                request.Player = target;
                request.ItemId = itemId;
                request.Amount = 1;
                request.ItemInstance = token.Cast<IProtocolToken>();
                if (!Current(out current) || current.Pointer != target.Pointer || !record.IdentityCurrent()) return false;

                // Establish server ownership BEFORE delivery can trigger callbacks. This is a
                // server grant record, not a conclusion drawn from Send or a client notification.
                _items.Add(transaction, record);
                sendAttempted = true;
                request.Send();
                if (!Current(out _))
                {
                    Fault("Player lifetime ended during grant delivery; do not retry this grant.");
                    detail = Detail;
                    return false;
                }
                Detail = detail = "Server instance granted; exact-token delivery queued, client acceptance unknown.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Server grant failed (" + error.GetType().Name + ").";
                if (sendAttempted) { Fault(detail + " Delivery is uncertain; do not retry."); detail = Detail; }
                return false;
            }
            finally
            {
                if (request != null && !sendAttempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception) { Fault("Unsent grant storage could not be released."); }
                }
                _busy = false;
            }
        }

        /// <summary>
        /// Reserves one explicitly server-authorized equipment transition for the next matching
        /// notification. For HeldInitialized, heldSlot must be the independently authorized slot.
        /// For race use instanceKey=0; all other changes require a live server-owned record.
        /// ArmourBroken must originate from server damage, not the client's break notification.
        /// Native state and the ledger commit together only after the publisher's applied receipt.
        /// No cancellation/replacement of a reservation: retire the endpoint to recover instead.
        /// Enforcement accepts qualified HeldRemoved stow and vacant right-hand HeldInitialized
        /// equip through exact native transactions. Slot transfers, clothing and armour remain closed.
        /// </summary>
        public bool TryPrepareEquipment(ulong transaction, ulong instanceKey,
            in PlayerEquipmentChange change, out string detail, int heldSlot = -1)
        {
            if (MutationEnforcement != null)
                return TryQueueEnforcedEquipment(transaction, instanceKey, in change, heldSlot, out detail);
            detail = "A fresh server decision and idle current account are required.";
            if (!Ready(out var target) || !Fresh(transaction) || _pending != null || !change.IsValid) return false;
            _busy = true;
            try
            {
                _items.TryGetValue(instanceKey, out var item);
                if (change.Kind == PlayerEquipmentKind.RaceChanged)
                {
                    if (instanceKey != 0 || heldSlot != -1) return false;
                }
                else if (item == null || item.Consumed || !item.IdentityCurrent()
                    || (change.Kind != PlayerEquipmentKind.RightHandLit && change.Value != item.Id))
                {
                    detail = "No live server-owned instance matches this transition.";
                    return false;
                }
                var state = target.GetState<IPlayerState>();
                var before = EquipmentState.Capture(state);
                if (before == null || !ValidateTransition(item, in change, heldSlot, before, out detail)) return false;
                if (!PlayerEquipment.Preflight(state, in change, item?.Item, out detail)) return false;
                if (!Current(out var current) || current.Pointer != target.Pointer) return false;
                _lastTransaction = transaction;
                _pending = new PendingEquipment(item, change, heldSlot, before);
                Detail = detail = "Server equipment transition reserved; awaiting its matching callback and native receipt.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Equipment reservation failed (" + error.GetType().Name + ").";
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Debits an unequipped server-owned instance once after trusted server consumption.
        /// Does not remove client presentation or infer consumption from a queued vanilla removal.
        /// For worn armour, use a server-authorized ArmourBroken transition instead.
        /// </summary>
        public bool TryConsumeItem(ulong transaction, ulong instanceKey, out string detail)
            => TryConsumeItems(transaction, instanceKey, 1, out detail);

        /// <summary>
        /// With MutationEnforcement bound, reserves an exact native client removal and commits only
        /// after its receipt. Otherwise this remains a trusted ledger-only debit. The client remove
        /// callback is never authorization. Neither path applies food, crafting or other stat effects.
        /// </summary>
        public bool TryConsumeItems(ulong transaction, ulong instanceKey, int quantity, out string detail)
        {
            if (MutationEnforcement != null) return TryQueueEnforcedRemoval(transaction, instanceKey, quantity, out detail);
            detail = "Consumption needs a fresh server transaction and an unequipped owned instance.";
            if (!Ready(out var target) || !Fresh(transaction) || _pending != null
                || !_items.TryGetValue(instanceKey, out var item) || item.Consumed
                || quantity <= 0 || quantity > item.Quantity
                || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0) return false;
            _busy = true;
            try
            {
                var state = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (state == null || !item.IdentityCurrent() || !OwnedEquipmentMatches(state)
                    || !Current(out var current) || current.Pointer != target.Pointer) return false;
                _lastTransaction = transaction;
                item.Quantity -= quantity;
                item.Consumed = item.Quantity == 0;
                Detail = detail = "Server inventory debit committed; no client removal or consumption was inferred.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Server consumption could not be prepared (" + error.GetType().Name + ").";
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>Reads committed ledger state, not mutation readiness or completion of pending native custody.</summary>
        public bool TryReadItem(ulong instanceKey, out PlayerInventoryItem item)
        {
            item = default;
            if (Environment.CurrentManagedThreadId != _thread || _suspended || !Current(out _)
                || !_items.TryGetValue(instanceKey, out var record)) return false;
            item = new(record.Key, record.Id, record.Consumed, record.HeldSlot, record.Clothing, record.ArmourSlot)
            {
                Quantity = record.Quantity, ClothingSlots = record.ClothingSlots,
                ArmourPoints = record.ArmourPoints, HasNativeInstance = record.Item != null,
                FromAcceptedBaseline = record.FromAcceptedBaseline
            };
            return true;
        }

        /// <summary>
        /// Readiness for approved-baseline guarded quantity grants/removals and qualified stow.
        /// Does not approve the snapshot, enable native action ingress or imply full inventory coverage.
        /// </summary>
        public bool TryReadGuardedInventoryReadiness(out string detail)
        {
            detail = "Guarded gameplay inventory needs an active bridge, approved baseline and armed idle peer custody.";
            if (!HasAcceptedBaseline || !Ready(out _) || _pending != null || _stow != null || _equip != null
                || HasPendingNativePowerSupply || MutationEnforcement == null || !MutationEnforcement.CanQueue
                || MutationEnforcement.HasPendingCustody) return false;
            detail = "Approved-baseline guarded grant/remove and qualified stow are ready; unsupported native actions remain closed.";
            return true;
        }

        private bool Fresh(ulong transaction) => _bridge.IsGameplayActive && transaction != 0 && transaction > _lastTransaction
            && !HasPendingArmourCustody && !HasPendingNativePowerProjection
            && (!HasPendingNativePowerSupply || transaction == _powerSupply.Transaction);
        private bool Ready(out BoltEntity target)
        {
            target = null;
            return _bridge.IsGameplayActive && PreparedReady(out target);
        }
        private bool PreparedReady(out BoltEntity target)
        {
            target = null;
            return Environment.CurrentManagedThreadId == _thread && !_busy && !_suspended
                && !HasNativeCraftReservation && !HasNativeConsumptionReservation && !HasNativeDeathTransfer
                && !HasPendingArmourCustody && !HasPendingNativePowerProjection && _worldTransfer == null
                && (_reservingCraftRequest || MutationEnforcement?.HasPendingNativeCraftRequest != true)
                && MutationEnforcement?.BlocksNativeWorldMutation != true
                && _removal == null && _grant == null && _passive == null && Current(out target);
        }
        private bool Current(out BoltEntity target)
        {
            target = null;
            return !IsDisposed && !IsFaulted && Equipment != null && !Equipment.IsDisposed
                && !Equipment.IsFaulted && _bridge.IsServer && SameWorld && _bridge.TryReadCurrentTarget(out target)
                && !IsDisposed;
        }
        private void Fault(string detail) { _faulted = true; Detail = detail; }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose inventory authority on its game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            RetireDeathInventory();
            RetireCrafting();
            _consumptionOwner?.Dispose();
            ClearPowerState();
            _worldTransfer?.HoldUnconfirmed("Inventory authority retired with unresolved native world custody.");
            _bridge.Retired -= Dispose;
            SaveLifecycle.Lifecycle -= OnSaveLifecycle;
            try { MutationEnforcement?.Dispose(); Equipment?.Dispose(); }
            finally
            {
                _pending = null; _removal = null; _grant = null; _stow = null; _equip = null; _equipNotification = null; _passive = null;
                _decay.Clear(); _decayInspected.Clear(); _items.Clear();
            }
        }

        private sealed class ItemRecord
        {
            internal readonly ulong Key;
            internal readonly ItemInstance Item;
            internal readonly int Id;
            internal int HeldSlot = -1, ArmourSlot = -1;
            internal bool Clothing, Consumed;
            internal int Quantity = 1, ClothingSlots;
            internal float ArmourPoints;
            internal bool FromAcceptedBaseline, DefinitionOnly;
            private ItemInstanceModule[] _modules;
            private IntPtr _moduleList;
            private IntPtr _data;

            internal ItemRecord(ulong key, ItemInstance item) { Key = key; Item = item; Id = item._itemID; }
            internal ItemRecord(ulong key, int id, int quantity) { Key = key; Id = id; Quantity = quantity; }
            internal bool CaptureIdentity()
            {
                var data = Item == null ? ItemDatabaseManager.ItemById(Id) : Item.Data;
                if (Id <= 0 || data == null || data.WasCollected || !data || data.Id != Id) return false;
                _data = data.Pointer;
                if (Item == null) return true;
                var modules = Item._modules;
                if (modules == null || modules.WasCollected || modules.Count is < 0 or > 32) return false;
                _moduleList = modules.Pointer;
                _modules = new ItemInstanceModule[modules.Count];
                var identities = new HashSet<IntPtr>();
                for (int i = 0; i < modules.Count; i++)
                {
                    var module = modules[i];
                    if (module == null || module.WasCollected) return false;
                    if (!identities.Add(module.Pointer)) return false;
                    _modules[i] = module;
                }
                return IdentityCurrent();
            }
            internal bool IdentityCurrent()
            {
                if (Item != null && (Item.WasCollected || Item._itemID != Id || _modules == null)) return false;
                var data = Item == null ? ItemDatabaseManager.ItemById(Id) : Item.Data;
                if (data == null || data.WasCollected || !data || data.Pointer != _data || data.Id != Id) return false;
                if (Item == null) return true;
                var modules = Item._modules;
                if (modules == null || modules.WasCollected || modules.Pointer != _moduleList
                    || modules.Count != _modules.Length) return false;
                for (int i = 0; i < _modules.Length; i++)
                    if (_modules[i].WasCollected || modules[i] == null || modules[i].WasCollected
                        || modules[i].Pointer != _modules[i].Pointer) return false;
                return true;
            }
        }
    }

    /// <summary>Server custody state, with explicit provenance for operator-accepted existing items.</summary>
    public readonly record struct PlayerInventoryItem(ulong InstanceKey, int ItemId,
        bool Consumed, int HeldSlot, bool ClothingEquipped, int ArmourSlot)
    {
        public int Quantity { get; init; }
        public int ClothingSlots { get; init; }
        public float ArmourPoints { get; init; }
        public bool HasNativeInstance { get; init; }
        public bool FromAcceptedBaseline { get; init; }
    }
}
