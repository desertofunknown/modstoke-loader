// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Animation;
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
    // The operation owner controls loading, initialization and activation. This part
    // owns the exact held-object registration, pose and inventory handoff. In particular,
    // a held-initialized notification does not mean the inventory handoff is complete.
    internal sealed partial class StrictCombatEquipmentHandoff
    {
        private readonly StrictCombatPreparedEquipment _reservation;
        private readonly Action _requireOperation;
        private readonly PlayerInventory _inventory;
        private readonly InventoryProps _props;
        private readonly ItemInstance _item;
        private readonly ItemData _data;
        private readonly GameObject _object;
        private readonly Animator _animator;
        private readonly PlayerStats _stats;
        private readonly ulong _life;
        private readonly InventoryProps.PropDefinition _definition;
        private readonly Transform _locator, _parent;
        private readonly Il2CppSystem.Collections.Generic.List<InventoryProps.PropDefinition> _definitions;
        private readonly Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> _objects;
        private readonly Il2CppReferenceArray<ItemInstance> _slots, _next;
        private readonly ItemInstanceManager _manager;
        private readonly Il2CppSystem.Collections.Generic.Dictionary<int, ItemInstanceManager.Items> _items;
        private readonly ItemInstanceManager.Items _entry;
        private readonly Il2CppSystem.Collections.Generic.List<ItemInstance> _stored;
        private readonly Il2CppReferenceArray<StatEffect> _effects;
        private readonly StatEffect[] _copiedEffects;
        private readonly Il2CppSystem.Action<GameObject, InventoryProps.PropDefinition, ItemInstance> _initialized;
        private readonly UnityEngine.Events.UnityEvent<ItemInstance, int> _equipped;
        private readonly Il2CppSystem.Action<int, bool> _removed;
        private readonly UnityEngine.Events.UnityEvent<ItemInstance, int> _unequipped;
        private readonly float _poseTarget, _poseSpeed;
        private int _frame, _lastPoseFrame = -1;
        private ulong _revision;
        private bool _registerAttempted, _registered, _poseAttempted, _poseApplied;
        private bool _notifyAttempted, _notified, _commitAttempted, _slotWritten, _committed;
        private bool _mutating, _uncertain;

        private StrictCombatEquipmentHandoff(StrictCombatPreparedEquipment reservation, Action requireOperation)
        {
            _reservation = reservation;
            _requireOperation = requireOperation;
            _inventory = reservation.Source.Inventory;
            _props = reservation.Source.Props;
            _item = reservation.Source.Item;
            _data = _item._data;
            _object = reservation.ItemObject;
            _animator = _props._playerAnimator;
            _stats = reservation.Source.Life.Stats;
            _life = reservation.Source.Life.LifeGeneration;
            _definitions = _props._propDefinitions;
            _definition = FindDefinition();
            if (_definition is null) throw new InvalidOperationException("The pistol has no held definition.");
            _locator = _definition._heldLocator;
            _parent = _locator.parent;
            _objects = _props._gameObjectInstances;
            _slots = _inventory._equipmentSlots;
            _next = _inventory._equipmentSlotsNext;
            _manager = _inventory._itemInstanceManager;
            _items = _manager._itemsMap;
            if (!_items.TryGetValue(355, out _entry))
                throw new InvalidOperationException("The transferred pistol storage entry is unavailable.");
            _stored = _entry.UniqueInstances;
            _effects = _data._equippedStatEffect;
            if (_effects is null || _effects.WasCollected || _effects.Length > 32)
                throw new InvalidOperationException("The pistol's equipped effects are unavailable.");
            _copiedEffects = new StatEffect[_effects.Length];
            for (int i = 0; i < _copiedEffects.Length; i++) _copiedEffects[i] = _effects[i];
            _initialized = _props.OnHeldItemInitialized;
            _equipped = _inventory.OnItemEquippedEvent;
            _removed = _props.OnHeldItemRemoved;
            _unequipped = _inventory.OnItemUnequippedEvent;
            _poseTarget = _data._leftHandWidePoseBlend;
            _poseSpeed = _props._leftHandWidePoseBlendSpeedMultiplier;
        }

        internal bool IsRegistered => _registered && !_uncertain && !_cancelAttempted;
        internal bool IsPoseApplied => _poseApplied && !_uncertain && !_cancelAttempted;
        internal bool IsInitializedNotificationComplete => _notified && !_uncertain && !_cancelAttempted;
        internal bool IsCommitted => _committed && !_uncertain && !_cancelAttempted;
        internal bool IsUncertain => _uncertain;

        internal bool IsCommittedFor(StrictCombatPreparedEquipment reservation)
            => ReferenceEquals(_reservation, reservation) && IsCommitted;

        internal static bool TryCapture(StrictCombatPreparedEquipment reservation, Action requireOperation,
            out StrictCombatEquipmentHandoff handoff)
        {
            handoff = null;
            try
            {
                if (reservation == null || requireOperation == null || !reservation.IsCurrentPreparation)
                    return false;
                requireOperation();
                if (!reservation.Transfer.IsCurrentTaken()) return false;
                var candidate = new StrictCombatEquipmentHandoff(reservation, requireOperation);
                candidate.BeginFrame();
                candidate.RequireCurrent();
                candidate.CaptureCancellationPose();
                if (!candidate._poseReversible) return false;
                candidate.RequireCurrent();
                handoff = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool TryRegisterObject()
        {
            if (_registerAttempted || _cancelAttempted || _mutating || !OnCurrentOwner()) return false;
            return Execute(() =>
            {
                _registerAttempted = true;
                Change(() => { _objects.Add(355, _object); _registered = true; });
            });
        }

        internal bool TryApplyPose()
        {
            if (!IsRegistered || _poseAttempted || _mutating || !OnCurrentOwner()) return false;
            return Execute(() =>
            {
                CaptureCancellationPose();
                if (!_poseReversible) throw new InvalidOperationException("The equipment pose cannot be safely cancelled.");
                _poseAttempted = true;
                Change(() => ItemAnimatorHashHelper.ApplyAnimVars(_data, true, _animator, null));
                _poseApplied = true;
            });
        }

        // One guarded damping step per caller frame, with the native target, damping and
        // multiplier. No native coroutine is created and no exact-target snap is invented.
        internal bool TryAdvancePose(out bool settled)
        {
            settled = false;
            if (!IsPoseApplied || _mutating || !OnCurrentOwner()) return false;
            bool result = false;
            bool success = Execute(() =>
            {
                if (_lastPoseFrame != _frame)
                {
                    float delta = Time.deltaTime;
                    float scaled = delta * _poseSpeed;
                    if (!float.IsFinite(delta) || delta < 0 || !float.IsFinite(scaled) || scaled < 0)
                        throw new InvalidOperationException("The equipment pose clock is unavailable.");
                    Change(() =>
                    {
                        _animator.SetFloat(AnimationHashes.LeftHandWidePoseBlendHash, _poseTarget, .5f, scaled);
                        _ownedWidePose = _animator.GetFloat(AnimationHashes.LeftHandWidePoseBlendHash);
                    });
                    _lastPoseFrame = _frame;
                }
                float value = _animator.GetFloat(AnimationHashes.LeftHandWidePoseBlendHash);
                RequireCurrent();
                if (!float.IsFinite(value)) throw new InvalidOperationException("The equipment pose is unavailable.");
                result = Math.Abs(value - _poseTarget) < .01f;
            });
            settled = success && result;
            return success;
        }

        internal bool TryNotifyInitialized()
        {
            if (!IsPoseApplied || _notifyAttempted || _mutating || !OnCurrentOwner()) return false;
            return Execute(() =>
            {
                if (!_reservation.Controller._isInitialised)
                    throw new InvalidOperationException("The equipment controller is not initialized.");
                _notifyAttempted = true;
                if (_initialized is not null)
                    Change(() => _initialized.Invoke(_object, _definition, _item));
                _notified = true;
            });
        }

        // The stage must already be active with its controlled controller disabled or
        // otherwise gated by the owner. Activation alone is not a successful handoff.
        internal bool TryCommit()
        {
            if (!IsInitializedNotificationComplete || _commitAttempted || _mutating || !OnCurrentOwner())
                return false;
            return Execute(() =>
            {
                if (!_object.activeInHierarchy || !_reservation.Controller._isInitialised)
                    throw new InvalidOperationException("The prepared equipment is not active and initialized.");
                _commitAttempted = true;
                Change(() => { _slots[(int)EquipmentSlot.RightHand] = _item; _slotWritten = true; });
                foreach (var effect in _copiedEffects)
                    Change(() => ItemUtils.ApplyEffectsToPlayerStats(effect, true));
                if (_equipped is not null) Change(() => _equipped.Invoke(_item, 355));
                _committed = true;
            });
        }

        internal bool ReferencesCurrent()
        {
            if (_uncertain || _cancelAttempted || _mutating || !OnCurrentOwner()) return false;
            try { BeginFrame(); RequireCurrent(); return true; }
            catch (Exception) { return false; }
        }

        private bool Execute(Action action)
        {
            _mutating = true;
            try
            {
                BeginFrame();
                RequireCurrent();
                action();
                RequireCurrent();
                return true;
            }
            catch (Exception)
            {
                // Registration, animator callbacks and inventory notifications can fail
                // after side effects. Never retry, remove a substitute item or synthesize
                // compensating events when the original result cannot be established.
                if (_registerAttempted || _poseAttempted || _notifyAttempted || _commitAttempted)
                {
                    _uncertain = true;
                    _reservation.Retire();
                    _reservation.Source.Life.Retire();
                }
                return false;
            }
            finally { _mutating = false; }
        }

        private void Change(Action action)
        {
            RequireCurrent();
            action();
            RequireCurrent();
        }

        private bool OnCurrentOwner()
            => _reservation.Source.Owner.IsCurrentNativeHealthOwner(_reservation.Source.Life);

        private void BeginFrame()
        {
            if (!OnCurrentOwner()) throw new InvalidOperationException("The equipment life is not current.");
            _requireOperation();
            if (!_reservation.Source.Owner.TryRead(out _, out var state))
                throw new InvalidOperationException("Accepted equipment state is unavailable.");
            _revision = state.Revision;
            _frame = Time.frameCount;
        }

        private void RequireCurrent()
        {
            if (!OnCurrentOwner()) throw new InvalidOperationException("The equipment life is not current.");
            _requireOperation();
            if (_uncertain || _cancelAttempted || !_reservation.IsCurrentOwnership || Time.frameCount != _frame
                || !_reservation.Source.Owner.TryRead(out _, out var state) || state.Revision != _revision)
                throw new InvalidOperationException("Accepted equipment state changed during handoff.");
            RequireCapturedReferences();
            _requireOperation();
            if (!_reservation.IsCurrentOwnership || Time.frameCount != _frame
                || !_reservation.Source.Owner.TryRead(out _, out state) || state.Revision != _revision)
                throw new InvalidOperationException("Accepted equipment state changed during handoff.");
        }

        private void RequireCapturedReferences()
        {
            if (!Live(_inventory) || !Same(_inventory, LocalPlayer._Inventory_k__BackingField)
                || !Live(_stats) || !Same(_stats, _reservation.Source.Life.Stats)
                || !Same(_stats, LocalPlayer._Stats_k__BackingField)
                || !Same(_inventory, _reservation.Source.Life.Stats._inventory)
                || !Live(_props) || !Same(_props, _inventory._InventoryProps_k__BackingField)
                || !Same(_inventory, _props._playerInventory)
                || !Live(_animator) || !Same(_animator, _props._playerAnimator)
                || !Same(_animator, _reservation.Source.Life.Stats._animator)
                || !Same(_animator, LocalPlayer._Animator_k__BackingField)
                || !_inventory.isActiveAndEnabled || _inventory._currentView != PlayerInventory.PlayerViews.World
                || _inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                || _inventory._equipSequenceRight is not null || _inventory._IsThrowing_k__BackingField
                || _inventory._IsInQuickSelect_k__BackingField || _props._leftHandWideBlendCoroutine is not null
                || !Same(_item, _reservation.Source.Item) || !Same(_data, _item._data)
                || _item._itemID != 355 || _data._id != 355 || _data._equipmentSlot != EquipmentSlot.RightHand
                || !Live(_object) || !Same(_object, _reservation.ItemObject)
                || !Same(_object, _item._ItemObject_k__BackingField)
                || !Live(_reservation.Stage.Root) || !Same(_object.transform.parent, _reservation.Stage.Root.transform)
                || !Live(_locator) || !Same(_locator, _definition._heldLocator) || !Live(_parent)
                || !Same(_parent, _locator.parent) || !Same(_parent, _reservation.Stage.Root.transform.parent)
                || !Same(_definitions, _props._propDefinitions) || !Same(_definition, FindDefinition())
                || !Same(_slots, _inventory._equipmentSlots) || !Same(_next, _inventory._equipmentSlotsNext)
                || _slots.Length <= (int)EquipmentSlot.LeftHand || _next.Length <= (int)EquipmentSlot.LeftHand
                || _slots[(int)EquipmentSlot.LeftHand] is not null
                || (_slotWritten ? !Same(_item, _slots[(int)EquipmentSlot.RightHand])
                    : _slots[(int)EquipmentSlot.RightHand] is not null)
                || !Same(_objects, _props._gameObjectInstances)
                || !Same(_manager, _inventory._itemInstanceManager) || !Same(_items, _manager._itemsMap)
                || !_items.TryGetValue(355, out var entry) || !Same(_entry, entry)
                || !Same(_stored, _entry.UniqueInstances) || _entry.Count != 0 || _stored.Count != 0
                || !Same(_effects, _data._equippedStatEffect) || _effects.Length != _copiedEffects.Length
                || !SameOptional(_initialized, _props.OnHeldItemInitialized)
                || !SameOptional(_equipped, _inventory.OnItemEquippedEvent)
                || !SameOptional(_removed, _props.OnHeldItemRemoved)
                || !SameOptional(_unequipped, _inventory.OnItemUnequippedEvent)
                || !float.IsFinite(_poseTarget) || !float.IsFinite(_poseSpeed) || _poseSpeed < 0
                || _data._leftHandWidePoseBlend != _poseTarget
                || _props._leftHandWidePoseBlendSpeedMultiplier != _poseSpeed)
                throw new InvalidOperationException("The captured equipment handoff changed.");
            bool found = _objects.TryGetValue(355, out var current);
            if (_registered ? !found || !Same(_object, current) : found)
                throw new InvalidOperationException("The held-object registration changed.");
            for (int i = 0; i < _next.Length; i++)
                if (_next[i] is not null) throw new InvalidOperationException("Another equip operation started.");
            for (int i = 0; i < _copiedEffects.Length; i++)
                if (!Same(_copiedEffects[i], _effects[i]))
                    throw new InvalidOperationException("The equipped effects changed.");
        }

        private InventoryProps.PropDefinition FindDefinition()
        {
            if (_definitions is null || _definitions.WasCollected || _definitions.Count > 1024)
                throw new InvalidOperationException("Held definitions are unavailable.");
            // Ordinary equip passes no forced slot. Slot overrides have different native
            // lookup semantics and must not be substituted for the ordinary definition.
            for (int i = 0; i < _definitions.Count; i++)
            {
                var definition = _definitions[i];
                if (definition is null || definition.WasCollected)
                    throw new InvalidOperationException("A held definition is unavailable.");
                if (definition._itemId == 355) return definition;
            }
            return null;
        }

        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                && first.Pointer == second.Pointer;
        private static bool SameOptional(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is null ? second is null : Same(first, second);
    }
}
