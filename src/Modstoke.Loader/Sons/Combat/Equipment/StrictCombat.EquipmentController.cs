// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using global::Sons.Settings;
using global::Sons.Weapon;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using SavedPlayerState = global::Sons.Player.PlayerState;

namespace Modstoke.Sons.Combat
{
    // The already-seen local pistol path. Native Initialize and first-look helpers
    // resolve global receivers and are deliberately not called by this owner.
    internal sealed partial class StrictCombatEquipmentController
    {
        private readonly StrictCombatPreparedEquipment _reservation;
        private readonly Action _requireOperation;
        private readonly int _thread;
        private readonly ItemInstance _item;
        private readonly ItemData _data;
        private readonly string _itemName;
        private readonly RangedWeaponController _controller;
        private readonly Animator _animator, _weaponAnimator, _initialWeaponAnimator;
        private readonly PlayerAnimatorControl _control;
        private readonly Transform _root, _camera, _controllerTransform;
        private readonly PlayerStats _stats;
        private readonly PlayerInventory _inventory;
        private readonly FirstPersonCharacter _movement;
        private readonly BoltEntity _entity;
        private readonly Bolt.Entity _attachment;
        private readonly IPlayerState _networkState;
        private readonly Il2CppSystem.Collections.Generic.List<IHitReactReceiver> _hitReceivers;
        private readonly IHitReactReceiver _hitReceiver;
        private readonly int _initialRegistrations;
        private readonly Coroutine _cameraReset;
        private readonly bool _hasFirstLook, _everyFirstLook, _forceFirstLook, _suppressRight, _suppressLeft;
        private readonly bool _requiresHistory;
        private readonly bool _hasVariant, _canBePlated, _isPlated;
        private readonly int _variantNumber;
        private readonly Il2CppSystem.Collections.Generic.List<ItemInstanceModule> _modules;
        private readonly ItemInstanceModule[] _moduleEntries;
        private readonly ItemPlatingItemInstanceModule _plating;
        private readonly VisualVariantItemInstanceModule _variant;
        private readonly SavedPlayerState _savedState;
        private readonly PlayerStateData _savedData;
        private readonly Il2CppSystem.Collections.Generic.List<PersistentValue> _savedEntries;
        private readonly PersistentValue _seenEntry;
        private readonly string _seenKey, _seenName;
        private readonly int _seenType;
        private readonly bool _seenValue;
        private bool _attempted, _mutationAttempted, _initializedWritten, _weaponAnimatorWritten;
        private bool _registrationAttempted, _registered, _initialized, _uncertain, _retired, _detachAttempted;
        private int _frame;
        private ulong _revision;

        private StrictCombatEquipmentController(StrictCombatPreparedEquipment reservation, Action requireOperation)
        {
            _reservation = reservation;
            _requireOperation = requireOperation;
            _thread = Environment.CurrentManagedThreadId;
            _item = reservation.Source.Item;
            _data = _item._data;
            _controller = reservation.Controller;
            Require(Live(_controller) && Live(_data));
            _itemName = _data._name;
            _controllerTransform = _controller.transform;
            _animator = _controller._playerAnimator;
            _control = _controller._playerAnimatorControl;
            _root = _controller._playerRootTransform;
            _stats = reservation.Source.Life.Stats;
            _inventory = reservation.Source.Inventory;
            _camera = LocalPlayer._MainCamTr_k__BackingField;
            _movement = LocalPlayer._FpCharacter_k__BackingField;
            _entity = LocalPlayer._Entity_k__BackingField;
            Require(Live(_entity) && _entity.isAttached);
            _attachment = _entity._entity;
            _networkState = _entity.GetState<IPlayerState>();
            Require(Live(_control) && Live(_root) && Live(_stats));
            _cameraReset = _control._smoothResetCameraYPositionRoutine;
            _initialWeaponAnimator = _controller._weaponAnimator;
            _weaponAnimator = _initialWeaponAnimator is null
                ? _controllerTransform.GetComponent<Animator>() : _initialWeaponAnimator;
            _hitReceivers = _stats._hitReactReceivers;
            _hitReceiver = _controller.Cast<IHitReactReceiver>();
            _initialRegistrations = CountRegistrations();
            Require(_initialRegistrations <= 1);
            _hasFirstLook = _data._hasFirstLook;
            _everyFirstLook = _data._playFirstLookAtBeforeEveryEquip;
            _forceFirstLook = HeldControllerBase._forceFirstLook;
            _suppressRight = _inventory._suppressRightHandFirstLookAts;
            _suppressLeft = _controller._shouldSuppressLeftHandFirstLook;
            _hasVariant = _data._hasVisualVariant;
            _canBePlated = _data._canBePlated;
            _modules = _item._modules;
            Require(Valid(_modules) && _modules.Count <= 32);
            _moduleEntries = new ItemInstanceModule[_modules.Count];
            for (int i = 0; i < _moduleEntries.Length; i++)
            {
                var module = _modules[i];
                Require(Valid(module));
                _moduleEntries[i] = module;
                var plating = module.TryCast<ItemPlatingItemInstanceModule>();
                var variant = module.TryCast<VisualVariantItemInstanceModule>();
                if (plating is not null) { Require(_plating is null); _plating = plating; }
                if (variant is not null) { Require(_variant is null); _variant = variant; }
            }
            Require((!_canBePlated || Valid(_plating)) && (!_hasVariant || Valid(_variant)));
            _isPlated = _plating is not null && _plating._isPlated;
            _variantNumber = _variant is null ? 0 : _variant._variantNum;
            _seenKey = "seenFirstTimeEquip_00000355" + (_canBePlated && _isPlated ? "_Solafite" : "");
            _requiresHistory = _hasFirstLook && !_suppressRight;
            if (_requiresHistory)
            {
                Require(!_forceFirstLook && !_everyFirstLook);
                var states = _root.GetComponentsInChildren<SavedPlayerState>(false);
                Require(Valid(states) && states.Length == 1);
                _savedState = states[0];
                Require(Live(_savedState));
                _savedData = _savedState._data;
                Require(Valid(_savedData));
                _savedEntries = _savedData._entries;
                _seenEntry = FindSeenEntry();
                if (_seenEntry is not null)
                {
                    _seenName = _seenEntry.Name;
                    _seenType = (int)_seenEntry.SettingType;
                    _seenValue = _seenEntry.BoolValue;
                }
            }
            RequireFirstLookSkipped();
        }

        internal bool IsInitialized => _initialized && !_retired && !_uncertain && !_detachAttempted;
        internal bool IsUncertain => _uncertain;

        internal static bool TryCapture(StrictCombatPreparedEquipment reservation, Action requireOperation,
            out StrictCombatEquipmentController controller)
        {
            controller = null;
            try
            {
                if (reservation is null || requireOperation is null || !reservation.IsCurrentOwnership) return false;
                int frame = Time.frameCount;
                if (!reservation.Source.Owner.TryRead(out _, out var accepted)) return false;
                ulong revision = accepted.Revision;
                requireOperation();
                Require(reservation.IsCurrentOwnership);
                var candidate = new StrictCombatEquipmentController(reservation, requireOperation);
                candidate._frame = frame;
                candidate._revision = revision;
                candidate.RequireCurrent();
                controller = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool TryInitialize()
        {
            if (Environment.CurrentManagedThreadId != _thread || _attempted || _retired || _detachAttempted) return false;
            _attempted = true;
            try
            {
                BeginFrame();
                RequireCurrent();
                Change(() => { _controller._isInitialised = true; _initializedWritten = true; });
                if (_canBePlated)
                    Change(() =>
                    {
                        _networkState.RightHandItemPlatingState = _isPlated ? 1 : 0;
                        RequireCurrent();
                        Require(_networkState.RightHandItemPlatingState == (_isPlated ? 1 : 0));
                    });
                if (_hasVariant)
                    Change(() =>
                    {
                        _networkState.RightHandVisualVariantNum = _variantNumber;
                        RequireCurrent();
                        Require(_networkState.RightHandVisualVariantNum == _variantNumber);
                    });
                // Native Register only adds a missing receiver. An existing registration
                // is retained, but does not become this helper's cleanup responsibility.
                if (_initialRegistrations == 0)
                    Change(() =>
                    {
                        _registrationAttempted = true;
                        _hitReceivers.Add(_hitReceiver);
                        _registered = true;
                        RequireCurrent();
                        Require(CountRegistrations() == 1);
                    });
                if (_cameraReset is not null) Change(() => _control.StopCoroutine(_cameraReset));
                Change(() =>
                {
                    var position = _camera.localPosition;
                    Require(float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z));
                    position.y = 0;
                    _camera.localPosition = position;
                    RequireCurrent();
                    Require(_camera.localPosition == position);
                });
                if (_suppressLeft)
                    Change(() =>
                    {
                        _inventory._suppressLeftHandFirstLookAts = true;
                        RequireCurrent();
                        Require(_inventory._suppressLeftHandFirstLookAts);
                    });
                Change(() =>
                {
                    _networkState.ShowLoadedAmmo = false;
                    RequireCurrent();
                    Require(!_networkState.ShowLoadedAmmo);
                });
                if (_initialWeaponAnimator is null)
                    Change(() => { _controller._weaponAnimator = _weaponAnimator; _weaponAnimatorWritten = true; });
                RequireCurrent();
                _initialized = true;
                return true;
            }
            catch (Exception)
            {
                if (_mutationAttempted) Fail();
                else _retired = true;
                return false;
            }
        }

        internal bool ReferencesCurrent()
        {
            if (!IsInitialized || Environment.CurrentManagedThreadId != _thread) return false;
            try { BeginFrame(); RequireCurrent(); return true; }
            catch (Exception) { Fail(); return false; }
        }

        // Retirement forbids normal mutations, but this exact retained subscription
        // still needs removal. No current-player lookup or camera teardown is involved.
        internal bool TryDetach()
        {
            if (Environment.CurrentManagedThreadId != _thread || _detachAttempted) return false;
            _detachAttempted = true;
            _retired = true;
            _initialized = false;
            if (!_registrationAttempted) return true;
            try
            {
                Require(Live(_stats) && Live(_controller) && Same(_controller, _hitReceiver)
                    && Same(_stats._hitReactReceivers, _hitReceivers));
                int count = CountRegistrations();
                Require(count <= 1);
                if (count == 1) Require(_hitReceivers.Remove(_hitReceiver));
                Require(Live(_stats) && Live(_controller) && Same(_controller, _hitReceiver)
                    && Same(_stats._hitReactReceivers, _hitReceivers) && CountRegistrations() == 0);
                _registered = false;
                return true;
            }
            catch (Exception) { Fail(); return false; }
        }

        private void Change(Action write)
        {
            RequireCurrent();
            _mutationAttempted = true;
            write();
            RequireCurrent();
        }

        private void BeginFrame()
        {
            RequireLife();
            Require(_reservation.Source.Owner.TryRead(out _, out var accepted));
            _revision = accepted.Revision;
            _frame = Time.frameCount;
            _requireOperation();
            RequireFrame();
        }

        private void RequireLife()
            => Require(Environment.CurrentManagedThreadId == _thread && !_retired && !_uncertain && !_detachAttempted
                && _reservation.IsCurrentOwnership);

        private void RequireFrame()
        {
            RequireLife();
            Require(Time.frameCount == _frame && _reservation.Source.Owner.TryRead(out _, out var accepted)
                && accepted.Revision == _revision);
        }

        private void RequireCurrent()
        {
            // Current-life validation precedes all native receiver access, including
            // local static identities; the operation callback only checks its own state.
            RequireFrame();
            _requireOperation();
            RequireFrame();
            var source = _reservation.Source;
            Require(Live(_stats) && Live(_controller) && Same(_controller, _reservation.Controller)
                && Same(_controller._itemInstance, _item) && Same(source.Item, _item)
                && Same(_item._data, _data) && Live(_data) && _data._id == 355
                && Same(_item._ItemObject_k__BackingField, _reservation.ItemObject)
                && Live(_animator) && Same(_controller._playerAnimator, _animator)
                && Same(source.Props._playerAnimator, _animator) && Same(_stats._animator, _animator)
                && Same(LocalPlayer._Animator_k__BackingField, _animator)
                && Live(_control) && Same(_controller._playerAnimatorControl, _control)
                && Same(source.Props._playerAnimatorControl, _control)
                && Same(LocalPlayer._AnimControl_k__BackingField, _control)
                && Live(_root) && Same(_controller._playerRootTransform, _root)
                && Same(source.Props._playerTransform, _root) && Same(LocalPlayer._Transform_k__BackingField, _root)
                && Live(_stats) && Same(_stats, source.Life.Stats) && Same(LocalPlayer._Stats_k__BackingField, _stats)
                && Live(_inventory) && Same(_inventory, source.Inventory) && Same(_stats._inventory, _inventory)
                && Same(LocalPlayer._Inventory_k__BackingField, _inventory)
                && Same(source.Props, _inventory._InventoryProps_k__BackingField)
                && Live(_camera) && Same(LocalPlayer._MainCamTr_k__BackingField, _camera)
                && Live(_movement) && Same(LocalPlayer._FpCharacter_k__BackingField, _movement)
                && Same(_movement.Stats, _stats)
                && Live(_entity) && Same(LocalPlayer._Entity_k__BackingField, _entity) && _entity.isAttached
                && Same(_attachment, _entity._entity) && Same(_networkState, _entity.GetState<IPlayerState>())
                && Same(_stats._hitReactReceivers, _hitReceivers) && Same(_hitReceiver, _controller)
                && CountRegistrations() == (_registered ? 1 : _initialRegistrations)
                && _controller._isInitialised == _initializedWritten
                && Live(_controllerTransform) && Same(_controller.transform, _controllerTransform)
                && SameOptional(_controller._weaponAnimator, _weaponAnimatorWritten ? _weaponAnimator : _initialWeaponAnimator)
                && (_weaponAnimator is null || Live(_weaponAnimator))
                && (_initialWeaponAnimator is not null || SameOptional(_controllerTransform.GetComponent<Animator>(), _weaponAnimator))
                && _data._hasFirstLook == _hasFirstLook && _data._playFirstLookAtBeforeEveryEquip == _everyFirstLook
                && _data._hasVisualVariant == _hasVariant && _data._canBePlated == _canBePlated
                && _controller._shouldSuppressLeftHandFirstLook == _suppressLeft
                && Same(_modules, _item._modules) && _modules.Count == _moduleEntries.Length);
            for (int i = 0; i < _moduleEntries.Length; i++) Require(Same(_moduleEntries[i], _modules[i]));
            if (!_initialized)
            {
                RequireSavedState();
                Require(!_controller.enabled && !_controller._firstLookIsRunning
                    && HeldControllerBase._forceFirstLook == _forceFirstLook
                    && _inventory._suppressRightHandFirstLookAts == _suppressRight
                    && SameOptional(_control._smoothResetCameraYPositionRoutine, _cameraReset)
                    && (_cameraReset is null || Valid(_cameraReset))
                    && (_plating is null || _plating._isPlated == _isPlated)
                    && (_variant is null || _variant._variantNum == _variantNumber));
                RequireFirstLookSkipped();
            }
            _requireOperation();
            RequireFrame();
        }

        private void RequireSavedState()
        {
            if (!_requiresHistory) return;
            var states = _root.GetComponentsInChildren<SavedPlayerState>(false);
            Require(Valid(states) && states.Length == (_savedState is null ? 0 : 1));
            if (_savedState is null) return;
            Require(Live(_savedState) && Same(states[0], _savedState) && _savedState.gameObject.activeInHierarchy
                && (Same(_savedState.transform, _root) || _savedState.transform.IsChildOf(_root))
                && Same(_savedData, _savedState._data) && Same(_savedEntries, _savedData._entries)
                && SameOptional(_seenEntry, FindSeenEntry()));
            if (_seenEntry is not null)
                Require(_seenEntry.Name == _seenName && (int)_seenEntry.SettingType == _seenType
                    && _seenEntry.BoolValue == _seenValue);
        }

        private PersistentValue FindSeenEntry()
        {
            Require(Valid(_savedEntries));
            PersistentValue first = null;
            int count = _savedEntries.Count;
            Require(count <= 8192);
            for (int i = 0; i < count; i++)
            {
                var entry = _savedEntries[i];
                Require(Valid(entry));
                if (string.Equals(entry.Name, _seenKey, StringComparison.OrdinalIgnoreCase))
                { first = entry; break; }
            }
            Require(_savedEntries.Count == count && SameOptional(first, _savedData.GetEntry(_seenKey)));
            return first;
        }

        private void RequireFirstLookSkipped()
            => Require(!_hasFirstLook || _suppressRight
                || (!_forceFirstLook && !_everyFirstLook && Valid(_seenEntry) && _seenType == 0 && _seenValue));

        private int CountRegistrations()
        {
            Require(Valid(_hitReceivers) && Valid(_hitReceiver));
            int count = 0, length = _hitReceivers.Count;
            Require(length <= 512);
            for (int i = 0; i < length; i++) if (Same(_hitReceiver, _hitReceivers[i])) count++;
            Require(_hitReceivers.Count == length);
            return count;
        }

        private void Fail()
        {
            _uncertain = true;
            _retired = true;
            _reservation.Retire();
            _reservation.Source.Life.Retire();
        }

        private static void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("The retained equipment controller is unavailable or changed.");
        }
        private static bool Valid(Il2CppObjectBase value) => value is not null && !value.WasCollected && value.Pointer != 0;
        private static bool Live(UnityEngine.Object value) => Valid(value) && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => Valid(first) && Valid(second) && first.Pointer == second.Pointer;
        private static bool SameOptional(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is null ? second is null : Same(first, second);
    }
}
