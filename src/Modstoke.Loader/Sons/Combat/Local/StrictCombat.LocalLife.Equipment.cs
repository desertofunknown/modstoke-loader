// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Animation;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using global::Sons.Weapon;
using TheForest.Items.Inventory;
using TheForest.Items.Utils;
using TheForest.Utils;
using TheForest.Utils.Items;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalLife
    {
        private EquipmentStash _equipmentStash;

        internal bool IsReadyForEquipment()
        {
            try
            {
                if (!ControlsCurrent() || _phase != Phase.Alive || !AliveMatches()) return false;
                int frame = Time.frameCount;
                return _barrier.Owner.TryRead(out _, out var state)
                    && state.LifeStage == StrictCombatLifeStage.Alive
                    && state.LifeGeneration == _barrier.LifeGeneration && state.OwnsPistol
                    && frame == Time.frameCount && ControlsCurrent() && AliveMatches();
            }
            catch (Exception) { return false; }
        }

        internal bool TryReadStashedEquipment(out StrictCombatLocalWeapon source)
        {
            source = null;
            if (!IsReadyForEquipment()) return false;
            source = _equipmentStash?.ReturnedSource;
            return source is not null && ReferenceEquals(source.Life, _barrier);
        }

        private bool TryPrepareEquipmentStash()
        {
            _equipmentStash = null;
            try
            {
                RequireCurrent();
                var capture = new EquipmentStash(this);
                capture.RequireCurrent();
                if (!StrictCombatLifeHooks.TryOwnEquipmentPose(
                    _barrier, _inventory, capture.Props, _animator)) return false;
                capture.RequireCurrent();
                _equipmentStash = capture;
                return true;
            }
            catch (Exception) { return false; }
        }

        // This transfer handles the managed pistol profile and empty hands. Native bulk
        // stashing can reread replacement items after callbacks and start unowned workers.
        private sealed class EquipmentStash
        {
            private readonly StrictCombatLocalLife _life;
            private readonly ulong _generation;
            private readonly PlayerInventory _inventory;
            private readonly Il2CppReferenceArray<ItemInstance> _slots, _next, _previous;
            private readonly ItemInstanceManager _manager;
            private readonly Il2CppSystem.Collections.Generic.Dictionary<int, ItemInstanceManager.Items> _items;
            private readonly Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> _objects;
            private readonly ItemInstance _item;
            private readonly ItemData _data;
            private readonly GameObject _object;
            private readonly StrictCombatLocalWeapon _weapon;
            private readonly Il2CppReferenceArray<StatEffect> _effects;
            private readonly StatEffect[] _copiedEffects;
            private readonly EquipmentCleanup _cleanup;
            private bool _reserved, _attempted, _destroyed, _removed, _cleared, _returned;
            private bool _unequipping, _stashing, _instantDestroy;

            internal InventoryProps Props { get; }
            internal StrictCombatLocalWeapon ReturnedSource => _returned ? _weapon : null;

            internal EquipmentStash(StrictCombatLocalLife life)
            {
                _life = life;
                _generation = life._barrier.LifeGeneration;
                _inventory = life._inventory;
                Props = _inventory._InventoryProps_k__BackingField;
                _slots = _inventory._equipmentSlots;
                _next = _inventory._equipmentSlotsNext;
                _previous = _inventory._equipmentSlotsPrevious;
                _manager = _inventory._itemInstanceManager;
                _items = _manager._itemsMap;
                _objects = Props._gameObjectInstances;
                _item = _slots[(int)EquipmentSlot.RightHand];
                if (_item is null) return;
                _data = _item._data;
                _object = _item._ItemObject_k__BackingField;
                if (!life._barrier.Owner.TryReadWeaponForStash(life._barrier, _inventory, _item, out var weapon))
                    throw new InvalidOperationException("The held pistol has no current weapon owner.");
                _weapon = weapon;
                _instantDestroy = _weapon.Controller._isInstantDestroy;
                // Legacy weaponInfo has separate collider callbacks. It is not part of
                // this compact-pistol profile and must be qualified before supporting it.
                if (_object.GetComponentInChildren<weaponInfo>() != null)
                    throw new InvalidOperationException("The held pistol has legacy collider behavior.");
                _effects = _data._equippedStatEffect;
                if (_effects is null || _effects.WasCollected)
                    throw new InvalidOperationException("Equipped stat effects are unavailable.");
                _copiedEffects = new StatEffect[_effects.Length];
                for (int i = 0; i < _effects.Length; i++) _copiedEffects[i] = _effects[i];
                _cleanup = new EquipmentCleanup(life, _weapon);
            }

            internal void ReserveCleanup()
            {
                RequireCurrent();
                if (_reserved || (_item != null && !StrictCombatWeaponHooks.TrySuppressCleanup(_weapon)))
                    throw new InvalidOperationException("The pistol cleanup could not be owned.");
                _reserved = true;
            }

            internal void Execute()
            {
                if (!_reserved || _attempted) throw new InvalidOperationException("Equipment stashing is not available.");
                RequireCurrent();
                _attempted = true;
                var stashEvent = _inventory.OnStashHeldItems;
                if (stashEvent != null) Change(() => stashEvent.Invoke());
                if (_item is null) return;

                Change(() => _previous[(int)EquipmentSlot.RightHand] = _item);
                var litEvent = _inventory.OnRightHandItemLit;
                if (litEvent != null) Change(() => litEvent.Invoke(false));
                foreach (var effect in _copiedEffects)
                    Change(() => ItemUtils.ApplyEffectsToPlayerStats(effect, false));

                var controller = _weapon.Controller;
                Change(() => { controller._isUnequipping = true; _unequipping = true; });
                Change(() => { controller._isStashing = true; _stashing = true; });
                Change(() => controller._isDropping = false);
                Change(() => controller._isEquippingPrevious = false);
                Change(() => { controller._isInstantDestroy = true; _instantDestroy = true; });
                Change(() => controller.UnequipStartedHook());
                Change(() => ItemAnimatorHashHelper.ApplyAnimVars(
                    _data, false, _life._animator, null));
                // The owned iterator gate prevents an older blend from restoring its pose.
                // Use the final removal pose without scheduling another native continuation.
                Change(() => _life._animator.SetFloat(AnimationHashes.LeftHandWidePoseBlendHash, 0f));
                _cleanup.Execute(Change);
                Change(() => { _destroyed = true; UnityEngine.Object.Destroy(_object); });
                Change(() =>
                {
                    if (!_objects.Remove(355)) throw new InvalidOperationException("The held object disappeared.");
                    _removed = true;
                });
                var removedEvent = Props.OnHeldItemRemoved;
                if (removedEvent != null) Change(() => removedEvent.Invoke(355, false));
                Change(() => { _slots[(int)EquipmentSlot.RightHand] = null; _cleared = true; });
                var unequippedEvent = _inventory.OnItemUnequippedEvent;
                if (unequippedEvent != null) Change(() => unequippedEvent.Invoke(_item, 355));
                Change(() =>
                {
                    // Consume the transfer attempt before entering the native helper. It
                    // invokes inventory callbacks; neither exceptions nor failed readback retry it.
                    _returned = true;
                    if (!_inventory.AddItem(355, 1, true, true, _item))
                        throw new InvalidOperationException("The pistol was not returned to inventory.");
                });
                RequireCurrent();
            }

            private void Change(Action action)
            {
                RequireCurrent();
                _life.Change(action);
                RequireCurrent();
            }

            internal void RequireCurrent()
            {
                _life.RequireCurrent();
                if (_life._barrier.LifeGeneration != _generation
                    || !Same(Props, _inventory._InventoryProps_k__BackingField)
                    || !Same(_inventory, Props._playerInventory)
                    || !Same(_life._animator, Props._playerAnimator)
                    || Props._leftHandWideBlendCoroutine != null
                    || !Same(_slots, _inventory._equipmentSlots)
                    || !Same(_next, _inventory._equipmentSlotsNext)
                    || !Same(_previous, _inventory._equipmentSlotsPrevious)
                    || _slots.Length <= (int)EquipmentSlot.LeftHand
                    || _previous.Length <= (int)EquipmentSlot.RightHand
                    || _slots[(int)EquipmentSlot.LeftHand] != null
                    || _inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                    || _inventory._equipSequenceRight != null || _inventory._IsThrowing_k__BackingField
                    || _inventory._IsInQuickSelect_k__BackingField
                    || !Same(_manager, _inventory._itemInstanceManager)
                    || !Same(_items, _manager._itemsMap) || !Same(_objects, Props._gameObjectInstances))
                    throw new InvalidOperationException("Equipment ownership changed during stashing.");
                for (int i = 0; i < _next.Length; i++)
                    if (_next[i] != null) throw new InvalidOperationException("Another equipment operation started.");
                var right = _slots[(int)EquipmentSlot.RightHand];
                if (_item is null)
                {
                    if (right != null) throw new InvalidOperationException("The empty hand changed.");
                    _life.RequireCurrent();
                    return;
                }
                if ((_cleared ? right != null : !Same(_item, right))
                    || _item._itemID != 355 || !Same(_data, _item._data)
                    || _data._id != 355 || _data._equipmentSlot != EquipmentSlot.RightHand
                    || !_data._eachInstanceIsUnique || _data._alwaysDropOnUnequip || _data._canNotBeStored
                    || _data._maxAmount < 1 || !_inventory.Owns(402, null)
                    || !Same(_effects, _data._equippedStatEffect) || _effects.Length != _copiedEffects.Length)
                    throw new InvalidOperationException("The pistol cannot be stored under its captured rules.");
                for (int i = 0; i < _copiedEffects.Length; i++)
                    if (!Same(_effects[i], _copiedEffects[i]))
                        throw new InvalidOperationException("Equipped stat effects changed.");
                if (!_destroyed)
                {
                    if (!Same(_object, _item._ItemObject_k__BackingField)
                        || !Same(_weapon.Controller, _object.GetComponentInChildren<CompactPistolWeaponController>())
                        || !Same(_item, _weapon.Controller._itemInstance)
                        || !Same(_weapon.Weapon, _weapon.Controller._rangedWeapon)
                        || !Same(_weapon.Weapon, _weapon.Module._rangedWeapon)
                        || !Same(_weapon.Ammo, _weapon.Module._ammo)
                        || !Same(_weapon.Ammo, _weapon.Weapon._ammo)
                        || !_weapon.Controller._isInitialised || _weapon.Controller._firstLookIsRunning
                        || _weapon.Controller._isUnequipping != _unequipping
                        || _weapon.Controller._isStashing != _stashing
                        || _weapon.Controller._isDropping || _weapon.Controller._isEquippingPrevious
                        || _weapon.Controller._isInstantDestroy != _instantDestroy
                        || _weapon.Controller._reloadDelay != null || _weapon.Controller._reloadQueued
                        || _weapon.Controller._isReloading || _weapon.Controller._isCharging
                        || _weapon.Controller._itemWasThrown)
                        throw new InvalidOperationException("The held pistol changed.");
                    _cleanup.RequireCurrent();
                }
                bool hasObject = _objects.TryGetValue(355, out var currentObject);
                if (_removed ? hasObject : !hasObject || !PointerSame(_object, currentObject))
                    throw new InvalidOperationException("The held-object registration changed.");
                bool hasItems = _items.TryGetValue(355, out var items);
                if (_returned)
                {
                    if (!hasItems || items is null || items.WasCollected || items.Count != 1
                        || items.UniqueInstances is null || items.UniqueInstances.WasCollected
                        || items.UniqueInstances.Count != 1 || !Same(_item, items.UniqueInstances[0]))
                        throw new InvalidOperationException("The exact pistol transfer could not be confirmed.");
                }
                else if (hasItems && (items is null || items.WasCollected || items.Count != 0
                    || items.UniqueInstances is null || items.UniqueInstances.WasCollected
                    || items.UniqueInstances.Count != 0))
                    throw new InvalidOperationException("Inventory changed before the pistol transfer.");
                _life.RequireCurrent();
            }

            private static bool PointerSame(Il2CppObjectBase first, Il2CppObjectBase second)
                => first is not null && second is not null && first.Pointer == second.Pointer;
        }
    }
}
