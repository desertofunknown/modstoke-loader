// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class EquipmentTransfer
    {
        private bool _equipEntered, _removeEntered, _managerRemoved, _removeObserved, _zeroRemoved, _debitConfirmed;
        private bool _unequipEntered, _addEntered, _managerAdded, _dropEntered;
        private bool _managerAddObserved, _creditConfirmed, _unequipConfirmed;
        private PlayerInventory._EquipSequence_d__190 _sequence;
        private bool _sequenceExecuting, _sequenceFinished;
        private GameObject _initializedObject;

        // Prefixes pass the existing enforcement writer depth AFTER entering their writer.
        // Finalizers retain this invocation's permit result as admitted. For an admitted call
        // they must run even when a later prefix suppresses the original or it throws.
        // A true permit is not permission to override __runOriginal=false from another participant.
        // hasSlotOverride is the decoded Nullable value, not a boxed wrapper's address.
        internal bool TryPermitEquip(PlayerInventory receiver, ItemInstance instance, bool pickedUpFromWorld,
            bool memorizePrevious, bool hasSlotOverride, bool skipCreate, bool forceFirstLook, int depth)
        {
            try
            {
                if (!Applying(Operation.EquipStored) || _equipEntered || depth != 1
                    || !Same(receiver, _inventory) || !Same(instance, _item) || pickedUpFromWorld || memorizePrevious
                    || hasSlotOverride || skipCreate || forceFirstLook || !PropCurrent()
                    || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1)) return false;
                _equipEntered = true;
                return true;
            }
            catch (Exception error) { return Fail("Equip permit failed: " + error.Message, out _); }
        }

        internal bool TryPermitRemoveItem(PlayerInventory receiver, int id, int amount, bool overflow,
            bool equipPrevious, bool removeQuickSelect, ItemInstance instance, bool instantDestroy, int depth)
        {
            try
            {
                bool equip = Kind == Operation.EquipStored;
                if (!Applying(Kind) || Kind == Operation.StowHeld || _removeEntered
                    || depth != (equip ? 2 : 1) || (equip && !_equipEntered)
                    || !Same(receiver, _inventory) || id != ItemId || amount != 1 || overflow || equipPrevious
                    || removeQuickSelect || instantDestroy || !Same(instance, _item)
                    || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1)) return false;
                _removeEntered = true;
                return true;
            }
            catch (Exception error) { return Fail("Exact inventory removal permit failed: " + error.Message, out _); }
        }

        internal bool TryPermitRemoveInstance(ItemInstanceManager receiver, ItemInstance instance, int depth)
        {
            try
            {
                if (!Applying(Kind) || !_removeEntered || _managerRemoved || _removeObserved
                    || depth != (Kind == Operation.EquipStored ? 3 : 2)
                    || !Same(receiver, _manager) || !Same(instance, _item)
                    || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1)) return false;
                _managerRemoved = true;
                return true;
            }
            catch (Exception error) { return Fail("Exact manager removal permit failed: " + error.Message, out _); }
        }

        internal void ObserveRemoveInstance(bool admitted, bool ranOriginal, bool result, Exception error)
        {
            if (!admitted || !OnThread || !_applying || !_managerRemoved || _removeObserved || State == Stage.Unconfirmed) return;
            try
            {
                if (!ranOriginal || !result || error != null || !ContextCurrent(true) || !ConfirmsCustody(-1, -1))
                { Fail("Exact native manager debit or its callbacks are unconfirmed.", out _); return; }
                _removeObserved = true;
            }
            catch (Exception failure) { Fail("Exact manager debit readback failed: " + failure.Message, out _); }
        }

        internal bool TryPermitRemoveRemainder(ItemInstanceManager receiver, int id, int amount, bool overflow, int depth)
        {
            try
            {
                // RemoveItem falls back to RemoveItems(id,1) on failed exact removal. Never
                // allow that branch. Its successful path has only this zero-count notification.
                if (!Applying(Kind) || !_removeObserved || _zeroRemoved || _debitConfirmed
                    || depth != (Kind == Operation.EquipStored ? 3 : 2)
                    || !Same(receiver, _manager) || id != ItemId || amount != 0 || overflow) return false;
                _zeroRemoved = true;
                return true;
            }
            catch (Exception error) { return Fail("Zero-count removal permit failed: " + error.Message, out _); }
        }

        internal void ObserveRemoveItem(bool admitted, bool ranOriginal, bool result, Exception error)
        {
            if (!admitted || !OnThread || !_applying || !_removeEntered || _debitConfirmed || State == Stage.Unconfirmed) return;
            try
            {
                if (!ranOriginal || !result || error != null || !_removeObserved || !_zeroRemoved
                    || !ContextCurrent(true) || !ConfirmsCustody(-1, -1))
                { Fail("Inventory debit notifications or exact source readback are unconfirmed.", out _); return; }
                _debitConfirmed = true;
            }
            catch (Exception failure) { Fail("Inventory debit readback failed: " + failure.Message, out _); }
        }

        internal bool TryPermitUnequip(PlayerInventory receiver, EquipmentSlot slot, bool drop, bool stash,
            bool equipPrevious, bool instantDestroy, bool skipDestroy, bool ignoreMultiplayerCallback, int depth)
        {
            try
            {
                if (!Applying(Operation.StowHeld) || _unequipEntered || depth != 1 || !Same(receiver, _inventory)
                    || slot != _slot || drop || !stash || equipPrevious || !instantDestroy
                    || skipDestroy || !ignoreMultiplayerCallback || !_before.Matches(Custody.Capture(_inventory), _item, 0, -1)) return false;
                _unequipEntered = true;
                return true;
            }
            catch (Exception error) { return Fail("Unequip permit failed: " + error.Message, out _); }
        }

        internal bool TryPermitAddItem(PlayerInventory receiver, int id, int amount, bool preventAutoEquip,
            bool wasCrafted, ItemInstance instance, int depth)
        {
            try
            {
                if (!Applying(Operation.StowHeld) || !_unequipEntered || _addEntered || depth != 2
                    || !Same(receiver, _inventory) || id != ItemId || amount != 1 || !preventAutoEquip || !wasCrafted
                    || !Same(instance, _item) || _inventory._equipmentSlots[(int)_slot] != null) return false;
                _addEntered = true;
                return true;
            }
            catch (Exception error) { return Fail("Exact stow addition permit failed: " + error.Message, out _); }
        }

        internal bool TryPermitAddInstance(ItemInstanceManager receiver, ItemInstance instance, bool suppressCallback, int depth)
        {
            try
            {
                if (!Applying(Operation.StowHeld) || !_addEntered || _managerAdded || depth != 3
                    || !Same(receiver, _manager) || !Same(instance, _item) || suppressCallback) return false;
                _managerAdded = true;
                return true;
            }
            catch (Exception error) { return Fail("Exact stow manager permit failed: " + error.Message, out _); }
        }

        internal bool TryPermitDropInternal(int id, ItemInstance instance, bool fakeDrop, int count, int depth)
        {
            try
            {
                if (!Applying(Operation.DropStored) || !_debitConfirmed || _dropEntered || depth != 1
                    || id != ItemId || !Same(instance, _item) || fakeDrop || count != 1 || !ConfirmsCustody(-1, -1)) return false;
                _dropEntered = true;
                return true;
            }
            catch (Exception error) { return Fail("Exact native drop permit failed: " + error.Message, out _); }
        }

        internal void ObserveAddInstance(bool admitted, bool ranOriginal, bool result, Exception error)
        {
            if (!admitted || !OnThread || !_applying || !_managerAdded || _managerAddObserved || State == Stage.Unconfirmed) return;
            try
            {
                if (!ranOriginal || !result || error != null || !ContextCurrent(true) || !ConfirmsCustody(1, -1))
                { Fail("Exact native manager credit or its callbacks are unconfirmed.", out _); return; }
                _managerAddObserved = true;
            }
            catch (Exception failure) { Fail("Exact manager credit readback failed: " + failure.Message, out _); }
        }

        internal void ObserveAddItem(bool admitted, bool ranOriginal, bool result, Exception error)
        {
            if (!admitted || !OnThread || !_applying || !_addEntered || _creditConfirmed || State == Stage.Unconfirmed) return;
            try
            {
                if (!ranOriginal || !result || error != null || !_managerAddObserved
                    || !ContextCurrent(true) || !ConfirmsCustody(1, -1))
                { Fail("Inventory credit notifications or exact source readback are unconfirmed.", out _); return; }
                _creditConfirmed = true;
            }
            catch (Exception failure) { Fail("Inventory credit readback failed: " + failure.Message, out _); }
        }

        internal void ObserveUnequip(bool admitted, bool ranOriginal, Exception error)
        {
            if (!admitted || !OnThread || !_applying || !_unequipEntered || _unequipConfirmed || State == Stage.Unconfirmed) return;
            try
            {
                if (!ranOriginal || error != null || !_creditConfirmed || !_unequipNotified
                    || !ContextCurrent(true) || !ConfirmsCustody(1, -1))
                { Fail("Native stow returned without confirmed callbacks and exact custody.", out _); return; }
                _unequipConfirmed = true;
            }
            catch (Exception failure) { Fail("Native stow completion failed: " + failure.Message, out _); }
        }

        // Qualify and hook the actual MoveNext body, not IEnumerator.Dispose (a shared no-op).
        // surroundingWriterDepth is the current inventory depth, WITHOUT adding a coroutine
        // writer: first MoveNext is inside TryEquip (1), later native scheduler calls are at 0.
        internal bool TryEnterEquipSequence(PlayerInventory._EquipSequence_d__190 sequence, int surroundingWriterDepth)
        {
            try
            {
                if (!OnThread || Kind != Operation.EquipStored || State is not (Stage.Applying or Stage.AwaitingNative)
                    || _sequenceExecuting || _sequenceFinished || !Alive(sequence) || !ContextCurrent(true)
                    || !ModulesCurrent() || !_debitConfirmed || !Same(sequence.__4__this, _inventory)
                    || !Same(sequence.itemInstance, _item) || sequence.slot != _slot
                    || sequence.memorizePrevious || sequence.forceFirstLook || sequence.skipCreateItemObject
                    || !Same(sequence.propDefinition, _prop) || !PropCurrent() || !ConfirmsEquipInFlight()) return false;
                if (_sequence == null)
                {
                    if (!_applying || !_equipEntered || surroundingWriterDepth != 1 || sequence.__1__state != 0) return false;
                    _sequence = sequence;
                }
                else if (!Same(_sequence, sequence) || surroundingWriterDepth != (_applying ? 1 : 0)) return false;
                _sequenceExecuting = true;
                return true;
            }
            catch (Exception error) { return Fail("Equip coroutine admission failed: " + error.Message, out _); }
        }

        internal void ExitEquipSequence(PlayerInventory._EquipSequence_d__190 sequence, bool ranOriginal, bool moveNext, Exception error)
        {
            if (!OnThread || !_sequenceExecuting || !Same(sequence, _sequence)) return;
            _sequenceExecuting = false;
            try
            {
                if (!ranOriginal || error != null || !ContextCurrent(true) || !ModulesCurrent() || !ConfirmsEquipInFlight())
                { Fail("Reserved equip coroutine stopped without confirmed native completion.", out _); return; }
                _sequenceFinished = !moveNext;
                if (_sequenceFinished && (!_equipNotified || !ConfirmsCustody(-1, (int)_slot)))
                    Fail("Native equip ended without its exact equipped callback and held custody.", out _);
            }
            catch (Exception failure) { Fail("Equip coroutine completion failed: " + failure.Message, out _); }
        }

        // Route the existing retained InventoryProps.OnHeldItemInitialized callback here.
        // TryCreateHeldInstance can return a temporary object before asynchronous initialization;
        // coroutine completion and an active GameObject alone are not settled equipment.
        internal bool ObserveHeldInitialized(InventoryProps receiver, GameObject held,
            InventoryProps.PropDefinition definition, ItemInstance instance)
        {
            if (!OnThread || Kind != Operation.EquipStored || State is not (Stage.Applying or Stage.AwaitingNative)
                || !Same(receiver, _props) || !Same(instance, _item) || !Same(definition, _prop)) return false;
            try
            {
                if (!ContextCurrent(true) || !Live(held) || !ModulesCurrent()
                    || (_initializedObject != null && !Same(_initializedObject, held)))
                    return Fail("Native held initialization identity is unconfirmed.", out _);
                _initializedObject = held;
                return true;
            }
            catch (Exception error) { return Fail("Native held initialization failed: " + error.Message, out _); }
        }

        private bool Applying(Operation kind) => OnThread && _applying && State == Stage.Applying
            && Kind == kind && !_sequenceExecuting && ContextCurrent(true) && ModulesCurrent();
    }
}
