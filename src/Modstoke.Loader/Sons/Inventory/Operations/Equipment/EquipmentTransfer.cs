// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using System.Collections.Generic;
using Bolt;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    // Source-client custody only. The inventory endpoint owns authentication, native hooks,
    // module-writer suspension, ledger publication and acknowledgement. This is not coverage
    // admission for a player adapter, and must never run a remote player's methods on a host.
    internal sealed partial class EquipmentTransfer
    {
        internal enum Operation { EquipStored, StowHeld, DropStored }
        internal enum Stage { Captured, Admitted, Reserved, Applying, AwaitingNative, AwaitingWorld, ReadBack, Published, Cancelled, Unconfirmed }

        private static readonly Dictionary<nint, EquipmentTransfer> Reservations = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerBridge _bridge;
        private readonly PlayerInventory _inventory;
        private readonly ItemInstanceManager _manager;
        private readonly InventoryProps _props;
        private readonly ItemInstance _item;
        private readonly ItemData _data;
        private readonly int _itemId;
        private readonly EquipmentSlot _slot;
        private readonly nint _heldPrefab, _pickupPrefab;
        private readonly nint _root, _entity;
        private readonly string _modules;
        private readonly nint _moduleList;
        private readonly nint[] _moduleIdentities;
        private readonly Custody _before;
        private readonly ulong _networkId;
        private readonly Il2CppSystem.Nullable<EquipmentSlot> _defaultSlot = new();
        private InventoryProps.PropDefinition _prop;
        private Transform _locator;
        private Func<bool> _reservationCurrent;
        private bool _checking, _applying;
        private GameObject _dropOutput;
        private ulong _worldNetworkId;

        internal ulong Key { get; }
        internal ulong Transaction { get; private set; }
        internal int ItemId => _itemId;
        internal EquipmentSlot Slot => _slot;
        internal Operation Kind { get; private set; }
        internal Stage State { get; private set; } = Stage.Captured;
        internal string Detail { get; private set; } = "Exact native custody captured; not authorized.";
        internal GameObject DropOutput => _dropOutput;

        private EquipmentTransfer(PlayerBridge bridge,
            PlayerInventory inventory, ItemInstance item, ulong key, string modules)
        {
            _bridge = bridge;
            _inventory = inventory;
            _manager = inventory._itemInstanceManager;
            _props = inventory._InventoryProps_k__BackingField;
            _item = item;
            _data = item._data;
            _itemId = item._itemID;
            _slot = _data._equipmentSlot;
            _heldPrefab = Pointer(_data._heldPrefab);
            _pickupPrefab = Pointer(_data._pickupPrefab);
            _root = LocalPlayer.GameObject.Pointer;
            _entity = LocalPlayer.Entity.Pointer;
            _networkId = bridge.NetworkId;
            Key = key;
            _modules = modules;
            _moduleList = item._modules.Pointer;
            _moduleIdentities = new nint[item._modules.Count];
            for (int i = 0; i < _moduleIdentities.Length; i++) _moduleIdentities[i] = item._modules[i].Pointer;
            _before = Custody.Capture(inventory);
            _nativeState = new NativeState(inventory, _props, _item);
        }

        internal static bool TryCapture(PlayerBridge bridge, PlayerInventory inventory,
            ItemInstance exactItem, ulong key, string expectedModules,
            out EquipmentTransfer operation, out string detail)
        {
            operation = null;
            detail = "An exact retained source-client inventory and approved instance observation are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || bridge == null || bridge.IsServer || bridge.IsDisposed || key == 0 || expectedModules == null) return false;
            try
            {
                if (!BoltNetwork.isClient || BoltNetwork.isServer || !bridge.TryReadCurrentTarget(out var target)
                    || !Live(inventory) || !Same(inventory, LocalPlayer.Inventory) || !Live(LocalPlayer.GameObject)
                    || !Same(target, LocalPlayer.Entity) || !Alive(exactItem) || !Live(exactItem._data)
                    || exactItem._itemID <= 0 || exactItem._data._id != exactItem._itemID
                    || !Live(inventory._itemInstanceManager) || !Live(inventory._InventoryProps_k__BackingField)
                    || !Alive(exactItem._modules) || exactItem._modules.Count > 32) return false;
                foreach (var module in exactItem._modules) if (!Alive(module)) return false;
                var candidate = new EquipmentTransfer(bridge, inventory, exactItem, key, expectedModules);
                if (!candidate.ContextCurrent(false) || !candidate.ModulesCurrent() || !candidate.Idle()
                    || !candidate._before.Matches(Custody.Capture(inventory), exactItem, 0, -1)) return false;
                operation = candidate;
                detail = candidate.Detail;
                return true;
            }
            catch (Exception error) { detail = "Native equipment capture rejected: " + error.Message; return false; }
        }

        internal bool TryAdmit(Operation kind, out string detail)
        {
            detail = "An idle unique hand item without ownership/equipped effects or an unreserved hand replacement is required.";
            if (State != Stage.Captured || !OnThread) return false;
            try
            {
                if (!Enum.IsDefined(kind) || !ContextCurrent(false) || !ModulesCurrent() || !Idle()
                    || !ProfileCurrent() || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1)) return false;
                int slot = (int)_slot;
                if (kind != Operation.DropStored
                    && _before.Slots[slot] != (kind == Operation.StowHeld ? _item.Pointer : 0)) return false;
                if (kind == Operation.EquipStored && !CanPreserveOtherHand()) return false;
                int stored = _before.Occurrences(_item.Pointer);
                if (kind == Operation.StowHeld)
                {
                    if (stored != 0 || !_before.HasStorageSpace(ItemId, _data._maxAmount)
                        || (ItemId != 380 && !_before.HasItem(402)) || !Live(_item.ItemObject)
                        || !_before.Objects.TryGetValue(ItemId, out nint held) || held != _item.ItemObject.Pointer) return false;
                }
                else
                {
                    if (stored != 1 || !_before.IsUniqueStorage(ItemId) || Live(_item.ItemObject)
                        || _before.Objects.ContainsKey(ItemId)) return false;
                    if (kind == Operation.EquipStored)
                    {
                        // A populated Nullable searches alternate definitions, not the default prop.
                        _prop = _inventory.GetPropDefinitionForItem(ItemId, _defaultSlot);
                        if (!Alive(_prop) || _prop._itemId != ItemId || !Live(_prop._heldLocator)
                            || !Live(_data._heldPrefab)) return false;
                        _locator = _prop._heldLocator;
                    }
                    else if (!Live(_data._pickupPrefab)) return false;
                }
                Kind = kind;
                State = Stage.Admitted;
                Detail = detail = "Native custody shape admitted; server reservation and installed participants still required.";
                return true;
            }
            catch (Exception error) { detail = "Native equipment admission rejected: " + error.Message; return false; }
        }

        // The callback must revalidate the authenticated transaction, migration/life, exclusive
        // source guard and all required native participants. Never substitute a capability flag.
        // Drop also requires an already reserved server world-pickup participant before debit.
        internal bool TryReserve(ulong transaction, Func<bool> reservationCurrent, out string detail)
        {
            detail = "An authenticated, exclusive inventory/world reservation is required before native mutation.";
            if (!OnThread || State != Stage.Admitted || transaction == 0 || reservationCurrent == null) return false;
            try
            {
                if (Reservations.TryGetValue(_inventory.Pointer, out var existing) && existing._load == _load) return false;
                if (!ContextCurrent(false) || !ModulesCurrent() || !Idle() || !ProfileCurrent()
                    || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1)) return false;
                _reservationCurrent = reservationCurrent;
                Transaction = transaction;
                if (!ContextCurrent(true)
                    || (Reservations.TryGetValue(_inventory.Pointer, out existing) && existing._load == _load)
                    || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1))
                { _reservationCurrent = null; Transaction = 0; return false; }
                Reservations[_inventory.Pointer] = this;
                State = Stage.Reserved;
                Detail = detail = "Exact item and all native hand/storage custody reserved; no write attempted.";
                return true;
            }
            catch (Exception error)
            {
                _reservationCurrent = null;
                Transaction = 0;
                detail = "Native equipment reservation rejected: " + error.Message;
                return false;
            }
        }

        // Existing ingress hooks must consume the exact permits in Writers.cs. This method does
        // not install hooks, replace input, bypass another prefix, or authorize server publication.
        internal bool TryApply(out string detail)
        {
            detail = Detail;
            if (!OnThread || State != Stage.Reserved || _applying) return false;
            try
            {
                if (!ContextCurrent(true) || !ModulesCurrent() || !Idle() || !ProfileCurrent()
                    || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1)
                    || (Kind == Operation.EquipStored && (!PropCurrent() || !CanPreserveOtherHand())))
                { detail = "Reservation changed before native dispatch; no write attempted."; return false; }
                RetainCallbacks();
                if (!ContextCurrent(true))
                { detail = "Equipment callback ownership changed before native dispatch."; return false; }
                if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out detail))
                    return Fail("Accepted equipment work could not yield the source body hold: " + detail, out detail);
                State = Stage.Applying;
                _applying = true;
                if (Kind == Operation.EquipStored)
                {
                    bool dispatched = _inventory.TryEquip(_item, false, false, _defaultSlot, false, false);
                    if (State == Stage.Unconfirmed || !dispatched || !_equipEntered || !_debitConfirmed || _sequence == null)
                        return Fail("Equip dispatch did not confirm its exact debit and coroutine identity.", out detail);
                    State = Stage.AwaitingNative;
                    Detail = detail = "Exact item debited; native equip coroutine remains responsible for completion.";
                }
                else if (Kind == Operation.StowHeld)
                {
                    _inventory.UnequipItemAtSlot(_slot, false, true, false, true, false, true);
                    if (State == Stage.Unconfirmed || !_unequipConfirmed || !_creditConfirmed || !_unequipNotified)
                        return Fail("Stow did not traverse every reserved native custody boundary.", out detail);
                    State = Stage.AwaitingNative;
                    Detail = detail = "Native stow returned; exact storage and held-object readback is still required.";
                }
                else
                {
                    // DropItemFromInventory spawns but does not debit. Remove exactly once first;
                    // its ID-fallback RemoveItems(1) is never permitted by this operation.
                    if (!_inventory.RemoveItem(ItemId, 1, false, false, false, _item, false) || !_debitConfirmed
                        || !ContextCurrent(true) || !ConfirmsCustody(-1, -1))
                        return Fail("Stored-drop source debit is unconfirmed; no world spawn was attempted.", out detail);
                    PlayerInventory.DropItemInternal(ItemId, _item, false, out _dropOutput, 1);
                    if (!_dropEntered || !Live(_dropOutput) || !ContextCurrent(true) || !ConfirmsCustody(-1, -1))
                        return Fail("Drop spawn or source custody is uncertain; do not retry or refund.", out detail);
                    State = Stage.AwaitingWorld;
                    Detail = detail = "Source debit and exact native output observed; authenticated world receipt is still required.";
                }
                return State != Stage.Unconfirmed;
            }
            catch (Exception error)
            {
                if (!_applying) { detail = "Native equipment preflight rejected: " + error.Message; return false; }
                return Fail("Native equipment attempt is unconfirmed: " + error.Message, out detail);
            }
            finally { _applying = false; }
        }

        internal Stage Readback(out string detail)
        {
            detail = Detail;
            if (!OnThread || _applying || _sequenceExecuting || State is not (Stage.AwaitingNative or Stage.AwaitingWorld or Stage.ReadBack)) return State;
            try
            {
                if (!ContextCurrent(true) || !ModulesCurrent())
                { Fail("Native equipment context or modules changed after dispatch.", out detail); return State; }
                if (Kind == Operation.EquipStored && (!_sequenceFinished || !Live(_initializedObject) || !_equipNotified))
                {
                    if (State == Stage.ReadBack) Fail("Settled held initialization was lost before publication acknowledgement.", out detail);
                    return State;
                }
                int delta = Kind == Operation.StowHeld ? 1 : -1;
                int heldSlot = Kind == Operation.EquipStored ? (int)_slot : -1;
                // Native pose blending may outlive the synchronous stow and equip body.
                // Wait for its owner; never stop or replace a scheduler-owned continuation.
                if (!Idle())
                {
                    if (State == Stage.ReadBack) Fail("Settled equipment became busy before publication acknowledgement.", out detail);
                    return State;
                }
                if (!ConfirmsCustody(delta, heldSlot))
                { Fail("Native equipment terminal custody disagrees with the exact reserved transition.", out detail); return State; }
                if (Kind == Operation.EquipStored && !Same(_initializedObject, _item.ItemObject))
                { Fail("Native held initialization and settled item object disagree.", out detail); return State; }
                if (Kind == Operation.DropStored)
                {
                    if (_worldNetworkId == 0) return State;
                    if (!WorldOutputMatches(_worldNetworkId))
                    { Fail("Confirmed world pickup changed before publication acknowledgement.", out detail); return State; }
                }
                State = Stage.ReadBack;
                Detail = detail = "Exact native custody read back; server ledger/equipment publication is not yet acknowledged.";
            }
            catch (Exception error) { Fail("Native equipment readback is unconfirmed: " + error.Message, out detail); }
            return State;
        }

        // Call only for an authenticated server receipt correlated to this transaction/key, not
        // a locally observed entity ID. A spawned GameObject alone never establishes world custody.
        internal bool ConfirmWorldReceipt(ulong transaction, GameObject exactOutput, ulong networkId, out string detail)
        {
            detail = "A correlated authoritative pickup receipt and exact native output are required.";
            if (!OnThread || State != Stage.AwaitingWorld || _applying || transaction != Transaction
                || networkId == 0 || !Same(_dropOutput, exactOutput)) return false;
            try
            {
                if (!ContextCurrent(true) || !WorldOutputMatches(networkId) || !ConfirmsCustody(-1, -1)) return false;
                _worldNetworkId = networkId;
                return Readback(out detail) == Stage.ReadBack;
            }
            catch (Exception error) { return Fail("Drop receipt readback is unconfirmed: " + error.Message, out detail); }
        }

        internal bool TryReleaseAfterPublication(ulong transaction, out string detail)
        {
            detail = "Publication acknowledgement must match the still-current native readback.";
            if (!OnThread || _applying || _sequenceExecuting || transaction != Transaction
                || State != Stage.ReadBack || Readback(out detail) != Stage.ReadBack) return false;
            if (!Reservations.TryGetValue(_inventory.Pointer, out var reserved) || !ReferenceEquals(reserved, this)) return false;
            try
            {
                if (!PublicationMatches())
                { detail = "Server held-item replication has not caught up with native custody."; return false; }
            }
            catch (Exception error) { return Fail("Equipment publication readback failed: " + error.Message, out detail); }
            if (!ReleaseCallbacks(out detail)) return false;
            if (Readback(out detail) != Stage.ReadBack) return false;
            Reservations.Remove(_inventory.Pointer);
            _reservationCurrent = null;
            State = Stage.Published;
            Detail = detail = "Native custody and caller-confirmed server publication completed.";
            return true;
        }

        internal bool TryCancelBeforeApply()
        {
            if (!OnThread || State is not (Stage.Captured or Stage.Admitted or Stage.Reserved)) return false;
            if (!ReleaseCallbacks(out _)) return false;
            if (Reservations.TryGetValue(_inventory.Pointer, out var reserved) && ReferenceEquals(reserved, this))
                Reservations.Remove(_inventory.Pointer);
            _reservationCurrent = null;
            State = Stage.Cancelled;
            Detail = "Cancelled before any native write; no rollback was attempted.";
            return true;
        }

        private bool WorldOutputMatches(ulong networkId)
        {
            if (!Live(_dropOutput)) return false;
            var pickup = _dropOutput.GetComponent<PickUp>();
            var entity = _dropOutput.GetComponent<BoltEntity>();
            return Live(pickup) && pickup._itemId == ItemId && pickup._amountToAdd == 1
                && Same(pickup._ItemInstance_k__BackingField, _item)
                && Live(entity) && entity.isAttached && entity.networkId.PackedValue == networkId;
        }

        private bool OnThread => Environment.CurrentManagedThreadId == _thread;
        private bool Fail(string reason, out string detail)
        {
            State = Stage.Unconfirmed;
            Detail = detail = reason;
            return false;
        }
    }
}
