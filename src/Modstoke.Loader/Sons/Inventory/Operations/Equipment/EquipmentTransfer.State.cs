// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Sons
{
    internal sealed partial class EquipmentTransfer
    {
        private readonly NativeState _nativeState;
        private UnityAction<ItemInstance, int> _equippedListener, _unequippedListener;
        private bool _equippedListenerAttempted, _unequippedListenerAttempted;
        private bool _equipNotified, _unequipNotified;
        private PlayerInventoryFrame? _command;

        // The endpoint must authenticate the sender before calling this overload. The baseline
        // fingerprint is the endpoint's retained value, never another field copied from the packet.
        internal bool TryReserve(in PlayerInventoryFrame command, string baselineFingerprint,
            Func<bool> reservationCurrent, out string detail)
        {
            detail = "The authoritative command must identify this exact hand, item, baseline and migration.";
            var expected = Kind == Operation.EquipStored ? PlayerInventoryMessage.EquipHeld : PlayerInventoryMessage.StowHeld;
            if (Kind == Operation.DropStored || command.Kind != expected || command.MigrationId != _bridge.Plan.Id
                || command.Generation != _bridge.Plan.Generation || command.NetworkId != _networkId
                || string.IsNullOrEmpty(baselineFingerprint) || command.Fingerprint != baselineFingerprint
                || command.Key != Key || command.ItemId != ItemId || command.Quantity != 1
                || command.BeforeQuantity != (int)_slot || command.EquipmentOptions != 0
                || command.ModuleFingerprint != PlayerInventoryWire.HashModules(_modules)) return false;
            if (!TryReserve(command.Transaction, reservationCurrent, out detail)) return false;
            _command = command;
            return true;
        }

        // Applying a committed result releases custody; it never repeats TryEquip/Unequip/AddItem.
        // Reliable duplicate receipts may be acknowledged again without native side effects.
        internal bool TryApplyAuthoritativeResult(in PlayerInventoryFrame result, out string detail)
        {
            detail = "A matching authenticated commit and replicated held state are required.";
            if (!OnThread || _bridge.IsDisposed || !_bridge.IsGameplayActive
                || _load != SaveLifecycle.State.BeforeLoadCount || _command is not { } command) return false;
            var expected = Kind == Operation.EquipStored ? PlayerInventoryMessage.EquipCommitted : PlayerInventoryMessage.StowCommitted;
            if (result.Kind != expected || result != (command with { Kind = expected })) return false;
            if (State == Stage.Published) { detail = Detail; return true; }
            if (Readback(out detail) != Stage.ReadBack) return false;
            try
            {
                if (!PublicationMatches())
                { detail = "The commit arrived before matching held-item replication; retain custody and poll again."; return false; }
                return TryReleaseAfterPublication(result.Transaction, out detail);
            }
            catch (Exception error) { return Fail("Authoritative equipment readback failed: " + error.Message, out detail); }
        }

        private bool PublicationMatches()
        {
            if (!_bridge.TryReadCurrentTarget(out var entity) || !Same(entity, LocalPlayer.Entity)) return false;
            var state = entity.GetState<IPlayerState>();
            if (!Alive(state) || !Alive(state.HeldItems) || state.HeldItems.Length is < 1 or > 32) return false;
            // HeldItems is a packed ID set, not an EquipmentSlot-indexed array.
            int occupied = 0;
            var slots = _inventory._equipmentSlotsIds;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] <= 0) continue;
                for (int j = 0; j < i; j++) if (slots[j] == slots[i]) return false;
                int count = 0;
                for (int j = 0; j < state.HeldItems.Length; j++) if (state.HeldItems[j] == slots[i]) count++;
                if (count != 1) return false;
                occupied++;
            }
            int published = 0;
            for (int i = 0; i < state.HeldItems.Length; i++)
            {
                if (state.HeldItems[i] < 0) return false;
                if (state.HeldItems[i] > 0) published++;
            }
            return published == occupied;
        }

        // Endpoint teardown may detach observations, but cannot refund, replay, release an
        // uncertain reservation or take ownership of the native scheduler's continuation.
        internal bool TryDetachCallbacks(out string detail)
        {
            detail = "Equipment callbacks cannot detach during native execution or a live reservation.";
            if (!OnThread || _applying || _sequenceExecuting) return false;
            if (State is not (Stage.Unconfirmed or Stage.Cancelled or Stage.Published))
            {
                if (!_bridge.IsDisposed && _load == SaveLifecycle.State.BeforeLoadCount) return false;
                Fail("Equipment owner retired before authoritative settlement; custody remains unconfirmed.", out _);
            }
            return ReleaseCallbacks(out detail);
        }

        private void RetainCallbacks()
        {
            Require(_nativeState.Current(_inventory, _props), "Native equipment callback sources changed.");
            if (Kind == Operation.DropStored) return;
            if (!_equippedListenerAttempted)
            {
                _equippedListener = (UnityAction<ItemInstance, int>)new Action<ItemInstance, int>(ObserveEquipped);
                _equippedListenerAttempted = true;
                _nativeState.Equipped.AddListener(_equippedListener);
            }
            if (!_unequippedListenerAttempted)
            {
                _unequippedListener = (UnityAction<ItemInstance, int>)new Action<ItemInstance, int>(ObserveUnequipped);
                _unequippedListenerAttempted = true;
                _nativeState.Unequipped.AddListener(_unequippedListener);
            }
        }

        private bool ReleaseCallbacks(out string detail)
        {
            detail = Detail;
            try
            {
                // Remove only our retained delegates. Never replace native event objects, clear
                // their listeners, replay their notifications, or stop their coroutines.
                if (_equippedListenerAttempted)
                {
                    Require(Alive(_nativeState.Equipped), "Equipped event expired before listener release.");
                    _nativeState.Equipped.RemoveListener(_equippedListener);
                    _equippedListenerAttempted = false;
                }
                if (_unequippedListenerAttempted)
                {
                    Require(Alive(_nativeState.Unequipped), "Unequipped event expired before listener release.");
                    _nativeState.Unequipped.RemoveListener(_unequippedListener);
                    _unequippedListenerAttempted = false;
                }
                return true;
            }
            catch (Exception error) { return Fail("Native equipment listener release is unconfirmed: " + error.Message, out detail); }
        }

        private void ObserveEquipped(ItemInstance item, int id)
        {
            if (!OnThread || State is not (Stage.Applying or Stage.AwaitingNative or Stage.ReadBack)) return;
            try
            {
                if (Kind != Operation.EquipStored || !_sequenceExecuting || _equipNotified || !_debitConfirmed
                    || id != ItemId || !Same(item, _item) || !Same(_inventory._equipmentSlots[(int)_slot], _item)
                    || !ContextCurrent(true) || !ModulesCurrent())
                { Fail("Native equipped notification did not match the reserved coroutine and exact hand instance.", out _); return; }
                _equipNotified = true;
            }
            catch (Exception error) { Fail("Native equipped notification failed: " + error.Message, out _); }
        }

        private void ObserveUnequipped(ItemInstance item, int id)
        {
            if (!OnThread || State is not (Stage.Applying or Stage.AwaitingNative or Stage.ReadBack)) return;
            try
            {
                if (Kind != Operation.StowHeld || !_applying || !_unequipEntered || _unequipNotified
                    || id != ItemId || !Same(item, _item) || _inventory._equipmentSlots[(int)_slot] != null
                    || !ContextCurrent(true) || !ModulesCurrent())
                { Fail("Native unequipped notification did not match the reserved hand instance.", out _); return; }
                _unequipNotified = true;
            }
            catch (Exception error) { Fail("Native unequipped notification failed: " + error.Message, out _); }
        }

        private sealed class NativeState
        {
            internal readonly UnityEvent<ItemInstance, int> Equipped, Unequipped;
            private readonly UnityEvent<ItemInstance, int> _added, _removed;
            private readonly Animator _animator;
            private readonly PlayerAnimatorControl _control;
            private readonly Transform _leftParent, _rightParent, _playerTransform;
            private readonly GameObject _playerBase;
            private readonly HeldState[] _untouched = new HeldState[6];
            private readonly ItemInstance[] _previous = new ItemInstance[6];

            internal NativeState(PlayerInventory inventory, InventoryProps props, ItemInstance selected)
            {
                Equipped = inventory.OnItemEquippedEvent;
                Unequipped = inventory.OnItemUnequippedEvent;
                _added = inventory.OnItemAddedEvent;
                _removed = inventory.OnItemRemovedEvent;
                _animator = props._playerAnimator;
                _control = props._playerAnimatorControl;
                _leftParent = props._leftHeldParent;
                _rightParent = props._rightHeldParent;
                _playerTransform = props._playerTransform;
                _playerBase = props._playerBase;
                for (int i = 0; i < _untouched.Length; i++)
                {
                    var item = inventory._equipmentSlots[i];
                    if (item != null && !Same(item, selected)) _untouched[i] = new HeldState(item);
                    _previous[i] = inventory._equipmentSlotsPrevious[i];
                }
            }

            internal bool Current(PlayerInventory inventory, InventoryProps props)
            {
                if (!Same(Equipped, inventory.OnItemEquippedEvent) || !Same(Unequipped, inventory.OnItemUnequippedEvent)
                    || !Same(_added, inventory.OnItemAddedEvent) || !Same(_removed, inventory.OnItemRemovedEvent)
                    || !Live(_animator) || !Same(_animator, props._playerAnimator) || !Same(_animator, LocalPlayer.Animator)
                    || !Live(_control) || !Same(_control, props._playerAnimatorControl)
                    || !Live(_leftParent) || !Same(_leftParent, props._leftHeldParent)
                    || !Live(_rightParent) || !Same(_rightParent, props._rightHeldParent)
                    || !Live(_playerTransform) || !Same(_playerTransform, props._playerTransform)
                    || !Live(_playerBase) || !Same(_playerBase, props._playerBase)) return false;
                for (int i = 0; i < _untouched.Length; i++)
                    if ((_untouched[i] != null && !_untouched[i].Current(inventory._equipmentSlots[i]))
                        || Pointer(_previous[i]) != Pointer(inventory._equipmentSlotsPrevious[i])) return false;
                return true;
            }
        }

        private sealed class HeldState
        {
            private readonly ItemInstance _item;
            private readonly ItemData _data;
            private readonly GameObject _object;
            private readonly nint _moduleList;
            private readonly nint[] _modules;
            private readonly string _serialized;

            internal HeldState(ItemInstance item)
            {
                _item = item;
                _data = item._data;
                _object = item.ItemObject;
                Require(Live(_data) && Live(_object) && Alive(item._modules) && item._modules.Count <= 32,
                    "Untouched held state is unavailable.");
                _moduleList = item._modules.Pointer;
                _modules = new nint[item._modules.Count];
                for (int i = 0; i < _modules.Length; i++)
                {
                    Require(Alive(item._modules[i]), "Untouched held module expired.");
                    _modules[i] = item._modules[i].Pointer;
                }
                _serialized = PlayerInventoryBaseline.CaptureModules(item);
            }

            internal bool Current(ItemInstance item)
            {
                if (!Same(_item, item) || !Live(_data) || !Same(_data, item._data) || item._itemID != _data._id
                    || !Live(_object) || !Same(_object, item.ItemObject) || !Alive(item._modules)
                    || item._modules.Pointer != _moduleList || item._modules.Count != _modules.Length) return false;
                for (int i = 0; i < _modules.Length; i++)
                    if (!Alive(item._modules[i]) || item._modules[i].Pointer != _modules[i]) return false;
                return PlayerInventoryBaseline.JsonEqual(_serialized,
                    PlayerInventoryBaseline.CaptureModules(item));
            }
        }
    }
}
