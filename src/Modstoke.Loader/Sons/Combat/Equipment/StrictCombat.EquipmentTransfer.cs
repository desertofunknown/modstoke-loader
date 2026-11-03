// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Items.Utils;
using TheForest.Utils;
using TheForest.Utils.Items;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal enum StrictCombatEquipmentTransferResult
    {
        Taken, Returned, ContextChanged, Busy, MutationUncertain
    }

    // The equip controller owns object creation and activation separately. This phase
    // transfers one existing instance; it never creates an item or substitutes another one.
    internal sealed class StrictCombatEquipmentTransfer
    {
        private readonly StrictCombatLocalWeapon _source;
        private readonly Action _requireOperation;
        private readonly StrictCombatBinding _binding;
        private readonly ulong _life;
        private readonly PlayerInventory _inventory;
        private readonly InventoryProps _props;
        private readonly ItemInstance _item;
        private readonly ItemData _data;
        private readonly ItemInstanceManager _manager;
        private readonly Il2CppSystem.Collections.Generic.Dictionary<int, ItemInstanceManager.Items> _items;
        private readonly Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> _objects;
        private readonly ItemInstanceManager.Items _entry;
        private readonly Il2CppSystem.Collections.Generic.List<ItemInstance> _instances;
        private readonly Il2CppReferenceArray<ItemInstance> _slots, _next;
        private readonly Il2CppSystem.Action<int, int> _removed;
        private readonly Il2CppReferenceArray<StatEffect> _ownedEffects;
        private readonly StatEffect[] _effects;
        private readonly nint _inventoryRemovedEvent;
        private StrictCombatSnapshot _accepted;
        private int _frame;
        private bool _executing, _takeAttempted, _listRemoved, _countRemoved, _taken;
        private bool _returnAttempted, _returned, _uncertain;
        private bool _requireEquipped, _returnForCancellation;

        private StrictCombatEquipmentTransfer(StrictCombatLocalWeapon source, Action requireOperation,
            in StrictCombatBinding binding, in StrictCombatSnapshot accepted)
        {
            _source = source;
            _requireOperation = requireOperation;
            _binding = binding;
            _life = accepted.LifeGeneration;
            _inventory = source.Inventory;
            _props = source.Props;
            _objects = _props._gameObjectInstances;
            _item = source.Item;
            _data = _item._data;
            _manager = _inventory._itemInstanceManager;
            _items = _manager._itemsMap;
            if (!_items.TryGetValue(355, out _entry))
                throw new InvalidOperationException("The captured pistol is not stored.");
            _instances = _entry.UniqueInstances;
            _slots = _inventory._equipmentSlots;
            _next = _inventory._equipmentSlotsNext;
            _removed = _manager.ItemsRemovedEvent;
            _inventoryRemovedEvent = Pointer(_inventory.OnItemRemovedEvent);
            _ownedEffects = _data._ownedStatEffect;
            if (_ownedEffects is null || _ownedEffects.WasCollected)
                throw new InvalidOperationException("The pistol's ownership effects are unavailable.");
            _effects = new StatEffect[_ownedEffects.Length];
            for (int i = 0; i < _effects.Length; i++) _effects[i] = _ownedEffects[i];
            BeginFrame();
        }

        internal ItemInstance Item => _item;
        internal bool IsTaken => _taken && !_returnAttempted && !_uncertain;
        internal bool IsUncertain => _uncertain;

        // Same-life downed/death presentation waits until this exact instance is stored.
        // Native inventory must still be alive, active and idle; a replacement life or
        // retired owner never authorizes a compensating write.
        internal StrictCombatEquipmentTransferResult ReturnForCancellation()
        {
            _returnForCancellation = true;
            return Return();
        }

        internal static bool TryCapture(StrictCombatLocalWeapon source, Action requireOperation,
            in StrictCombatBinding binding, in StrictCombatSnapshot accepted,
            out StrictCombatEquipmentTransfer transfer)
        {
            transfer = null;
            try
            {
                if (source == null || requireOperation == null || !source.MatchesEquipmentBinding(in binding)
                    || accepted.LifeGeneration == 0 || accepted.LifeGeneration != source.Life.LifeGeneration
                    || !source.Owner.IsCurrentNativeHealthOwner(source.Life)) return false;
                requireOperation();
                var candidate = new StrictCombatEquipmentTransfer(source, requireOperation, in binding, in accepted);
                if (candidate._accepted.Revision != accepted.Revision) return false;
                candidate.RequireCurrent();
                transfer = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal StrictCombatEquipmentTransferResult Take()
        {
            if (_uncertain) return StrictCombatEquipmentTransferResult.MutationUncertain;
            if (_executing) return StrictCombatEquipmentTransferResult.Busy;
            if (_returnAttempted) return _returned ? StrictCombatEquipmentTransferResult.Returned
                : StrictCombatEquipmentTransferResult.MutationUncertain;
            if (_takeAttempted) return _taken ? StrictCombatEquipmentTransferResult.Taken
                : StrictCombatEquipmentTransferResult.MutationUncertain;
            _executing = true;
            try
            {
                BeginFrame();
                _takeAttempted = true;
                // Retain the list and entry rather than using native RemoveItem, whose
                // exact-instance failure can fall back to removing another item by ID.
                Change(() => { _instances.RemoveAt(0); _listRemoved = true; });
                Change(() => { _entry.Count = 0; _countRemoved = true; });
                if (_removed != null) Change(() => _removed.Invoke(355, 1));
                var inventoryEvent = _inventory.OnItemRemovedEvent;
                if (inventoryEvent != null) Change(() => inventoryEvent.Invoke(_item, 355));
                // The native successful exact-removal path also emits this zero-count
                // notification. Reproduce it without invoking type-based removal.
                if (_removed != null) Change(() => _removed.Invoke(355, 0));
                foreach (var effect in _effects)
                    Change(() => ItemUtils.ApplyEffectsToPlayerStats(effect, false));
                RequireCurrent();
                _taken = true;
                return StrictCombatEquipmentTransferResult.Taken;
            }
            catch (Exception)
            {
                return _takeAttempted ? Fail() : StrictCombatEquipmentTransferResult.ContextChanged;
            }
            finally { _executing = false; }
        }

        // Only an intact, fully taken item can be returned with empty hands and no
        // registered held object. Context loss and partial notifications never roll back.
        internal StrictCombatEquipmentTransferResult Return()
        {
            if (_uncertain) return StrictCombatEquipmentTransferResult.MutationUncertain;
            if (_executing) return StrictCombatEquipmentTransferResult.Busy;
            if (_returnAttempted) return _returned ? StrictCombatEquipmentTransferResult.Returned
                : StrictCombatEquipmentTransferResult.MutationUncertain;
            if (!_taken) return StrictCombatEquipmentTransferResult.ContextChanged;
            _executing = true;
            try
            {
                BeginFrame(requireEquipped: false);
                _returnAttempted = true;
                // Mark the attempt before native callbacks. AddItem keeps this exact
                // instance and suppresses auto-equip; direct storage readback decides success.
                if (!_inventory.AddItem(355, 1, true, true, _item)) return Fail();
                _returned = true;
                RequireCurrent();
                return StrictCombatEquipmentTransferResult.Returned;
            }
            catch (Exception)
            {
                return _returnAttempted ? Fail() : StrictCombatEquipmentTransferResult.ContextChanged;
            }
            finally { _executing = false; }
        }

        internal bool IsCurrentTaken()
        {
            if (!IsTaken || _executing) return false;
            try { BeginFrame(); return true; }
            catch (Exception) { return false; }
        }

        private void BeginFrame(bool requireEquipped = true)
        {
            if (!_source.Owner.IsCurrentNativeHealthOwner(_source.Life))
                throw new InvalidOperationException("The captured life is not current on this thread.");
            _requireOperation();
            if (!_source.Owner.TryRead(out var binding, out var accepted)
                || !binding.SameBinding(in _binding) || accepted.LifeGeneration != _life
                || !AllowsLifeStage(accepted.LifeStage, requireEquipped) || !accepted.OwnsPistol
                || (requireEquipped && !accepted.Equipped))
                throw new InvalidOperationException("The server no longer authorizes this equipment operation.");
            _accepted = accepted;
            _requireEquipped = requireEquipped;
            _frame = Time.frameCount;
            RequireCurrent();
        }

        private void Change(Action action)
        {
            RequireCurrent();
            action();
            RequireCurrent();
        }

        private void RequireCurrent()
        {
            if (!_source.Owner.IsCurrentNativeHealthOwner(_source.Life))
                throw new InvalidOperationException("The captured life is not current on this thread.");
            _requireOperation();
            if (_uncertain || !_source.Owner.IsCurrentNativeHealthOwner(_source.Life)
                || !_source.Owner.TryRead(out var binding, out var accepted)
                || !binding.SameBinding(in _binding) || accepted.Revision != _accepted.Revision
                || accepted.LifeGeneration != _life || !AllowsLifeStage(accepted.LifeStage, _requireEquipped)
                || !accepted.OwnsPistol || (_requireEquipped && !accepted.Equipped) || Time.frameCount != _frame
                || !Same(_inventory, LocalPlayer._Inventory_k__BackingField)
                || !Same(_inventory, _source.Life.Stats._inventory)
                || !Same(_props, _inventory._InventoryProps_k__BackingField)
                || !Same(_inventory, _props._playerInventory)
                || !_inventory.isActiveAndEnabled || _inventory._currentView != PlayerInventory.PlayerViews.World
                || _inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                || _inventory._equipSequenceRight != null || _inventory._IsThrowing_k__BackingField
                || _inventory._IsInQuickSelect_k__BackingField || _source.Life.Stats._playerIsDowned
                || _source.Life.Stats._dyingState != PlayerStats.DyingState.Alive
                || _source.Life.Stats._healing || _source.Life.Stats._isHealing || _source.Life.Stats._respawning
                || _source.Life.Vitals._hasDied
                || !Same(_slots, _inventory._equipmentSlots) || !Same(_next, _inventory._equipmentSlotsNext)
                || _slots.Length <= (int)EquipmentSlot.LeftHand || _next.Length <= (int)EquipmentSlot.LeftHand
                || _slots[(int)EquipmentSlot.RightHand] != null || _slots[(int)EquipmentSlot.LeftHand] != null
                || !Same(_data, _item._data) || _item._itemID != 355 || _data._id != 355
                || _data._equipmentSlot != EquipmentSlot.RightHand || !_data._eachInstanceIsUnique
                || _data._alwaysDropOnUnequip || _data._canNotBeStored || _data._maxAmount < 1
                || !_inventory.Owns(402, null)
                || !Same(_manager, _inventory._itemInstanceManager) || !Same(_items, _manager._itemsMap)
                || !_items.TryGetValue(355, out var entry) || !Same(_entry, entry)
                || !Same(_instances, _entry.UniqueInstances)
                || Pointer(_removed) != Pointer(_manager.ItemsRemovedEvent)
                || _inventoryRemovedEvent != Pointer(_inventory.OnItemRemovedEvent)
                || !Same(_ownedEffects, _data._ownedStatEffect) || _ownedEffects.Length != _effects.Length)
                throw new InvalidOperationException("The captured pistol transfer context changed.");
            for (int i = 0; i < _next.Length; i++)
                if (_next[i] != null) throw new InvalidOperationException("Another equip operation started.");
            bool storedList = !_listRemoved || _returned;
            bool storedCount = !_countRemoved || _returned;
            if (_entry.Count != (storedCount ? 1 : 0) || _instances.Count != (storedList ? 1 : 0)
                || (storedList && !Same(_item, _instances[0])))
                throw new InvalidOperationException("The exact pistol's storage state changed.");
            if (!Same(_objects, _props._gameObjectInstances) || _objects.ContainsKey(355))
                throw new InvalidOperationException("A held pistol is already registered.");
            for (int i = 0; i < _effects.Length; i++)
                if (Pointer(_effects[i]) != Pointer(_ownedEffects[i]))
                    throw new InvalidOperationException("Pistol ownership effects changed.");
            _requireOperation();
            if (Time.frameCount != _frame || !_source.Owner.TryRead(out binding, out accepted)
                || !binding.SameBinding(in _binding) || accepted.Revision != _accepted.Revision)
                throw new InvalidOperationException("The accepted equipment state changed.");
        }

        private StrictCombatEquipmentTransferResult Fail()
        {
            _uncertain = true;
            _source.Life.Retire();
            return StrictCombatEquipmentTransferResult.MutationUncertain;
        }

        private bool AllowsLifeStage(StrictCombatLifeStage stage, bool requireEquipped)
            => stage == StrictCombatLifeStage.Alive || (_returnForCancellation && !requireEquipped
                && (stage == StrictCombatLifeStage.Downed || stage == StrictCombatLifeStage.Dead));

        private static nint Pointer(Il2CppObjectBase value) => value is null ? 0 : value.Pointer;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                && first.Pointer == second.Pointer;
    }
}
