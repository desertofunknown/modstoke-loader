// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Animation;
using global::Sons.FieldOfView;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Reference binding is separate from renderer completion and activation. In particular,
    // native module Refresh would restore cached ammo and run visual/attachment callbacks.
    internal sealed class StrictCombatEquipmentBinding
    {
        private readonly StrictCombatPreparedEquipment _reservation;
        private readonly Action _requireOperation;
        private readonly ItemInstance _item;
        private readonly ItemData _data;
        private readonly RangedWeaponItemInstanceModule _module;
        private readonly RangedWeapon.Ammo _ammo;
        private readonly InventoryProps _props;
        private readonly Animator _animator;
        private readonly global::PlayerAnimatorControl _animation;
        private readonly AnimationEventReceiver _receiver;
        private readonly Transform _root, _baseTransform;
        private readonly GameObject _base;
        private readonly FovManager _fov;
        private readonly ItemRenderable _renderer;
        private readonly WeaponMods _mods;
        private readonly Il2CppSystem.Collections.Generic.List<ItemInstanceModule> _modules;
        private readonly ItemInstanceModule[] _moduleEntries;
        private readonly Il2CppSystem.Collections.Generic.List<WeaponMod> _attachments;
        private readonly Il2CppSystem.Collections.Generic.List<WeaponMods.ModLink> _locations;
        private readonly WeaponMods.ModLink[] _links;
        private readonly WeaponMod[] _attached;
        private readonly int _cachedAmmoType, _cachedAmmoCount;
        private readonly ArrowUpgradeItemData.Type _cachedUpgrade;
        private readonly bool _initialLocal;
        private readonly (Func<Il2CppObjectBase> Read, Action Write, Il2CppObjectBase Before, Il2CppObjectBase After)[] _references;
        private int _written, _frame;
        private ulong _revision;
        private bool _attempted, _mutationAttempted, _localWritten, _bound, _uncertain;

        private StrictCombatEquipmentBinding(StrictCombatPreparedEquipment reservation, Action requireOperation)
        {
            _reservation = reservation;
            _requireOperation = requireOperation;
            var source = reservation.Source;
            _item = source.Item;
            _data = _item._data;
            _module = source.Module;
            _ammo = source.Ammo;
            _props = source.Props;
            _animator = _props._playerAnimator;
            _animation = _props._playerAnimatorControl;
            _receiver = _props._playerAnimEventReceiver;
            _root = _props._playerTransform;
            _base = _props._playerBase;
            _baseTransform = _base.transform;
            var fovs = _animator.GetComponentsInChildren<FovManager>(true);
            if (fovs is null || fovs.Length != 1 || !Live(fovs[0]))
                throw new InvalidOperationException("The captured player has no unique field-of-view manager.");
            _fov = fovs[0];
            var controller = reservation.Controller;
            var weapon = reservation.Weapon;
            _renderer = controller.GetComponent<ItemRenderable>();
            if (!Live(_renderer)) throw new InvalidOperationException("The prepared controller has no renderer.");
            var mods = weapon.GetComponentsInChildren<WeaponMods>(true);
            if (mods is null || mods.Length > 1)
                throw new InvalidOperationException("The prepared weapon has an unsupported attachment layout.");
            _mods = mods.Length == 0 ? null : mods[0];
            if (Live(weapon._weaponMods) && !Same(weapon._weaponMods, _mods))
                throw new InvalidOperationException("The weapon's cached attachment component changed.");
            _attachments = _module._weaponMods;
            _modules = _item._modules;
            if (_modules is null || _modules.WasCollected || _modules.Count == 0 || _modules.Count > 32)
                throw new InvalidOperationException("The captured item modules are unavailable.");
            _moduleEntries = new ItemInstanceModule[_modules.Count];
            for (int i = 0; i < _moduleEntries.Length; i++) _moduleEntries[i] = _modules[i];
            if (_mods is not null)
            {
                if (!Live(_mods)) throw new InvalidOperationException("The attachment component is unavailable.");
                _locations = _mods._modLocations;
                if (_locations is null || _locations.WasCollected || _locations.Count > 32)
                    throw new InvalidOperationException("The attachment locations are unavailable.");
                _links = new WeaponMods.ModLink[_locations.Count];
                _attached = new WeaponMod[_locations.Count];
                for (int i = 0; i < _links.Length; i++)
                {
                    _links[i] = _locations[i];
                    _attached[i] = _links[i].AttachedMod;
                }
            }
            _cachedAmmoType = _module._ammoType;
            _cachedAmmoCount = _module._ammoCount;
            _cachedUpgrade = _module._ammoUpgradeType;
            _initialLocal = weapon._isLocalPlayer;
            _references = new[]
            {
                Reference(() => _item._ItemObject_k__BackingField, () => _item._ItemObject_k__BackingField = reservation.ItemObject, reservation.ItemObject),
                Reference(() => _item._itemRenderable, () => _item._itemRenderable = _renderer, _renderer),
                Reference(() => controller._itemInstance, () => controller._itemInstance = _item, _item),
                Reference(() => controller._playerAnimator, () => controller._playerAnimator = _animator, _animator),
                Reference(() => controller._playerAnimatorControl, () => controller._playerAnimatorControl = _animation, _animation),
                Reference(() => controller._receiver, () => controller._receiver = _receiver, _receiver),
                Reference(() => controller._playerRootTransform, () => controller._playerRootTransform = _root, _root),
                Reference(() => controller._playerBaseTransform, () => controller._playerBaseTransform = _baseTransform, _baseTransform),
                Reference(() => controller._cameraFovManager, () => controller._cameraFovManager = _fov, _fov),
                Reference(() => controller._rangedWeapon, () => controller._rangedWeapon = weapon, weapon),
                Reference(() => weapon._ammo, () => weapon._ammo = _ammo, _ammo),
                Reference(() => weapon._itemData, () => weapon._itemData = _data, _data),
                Reference(() => weapon._weaponMods, () => weapon._weaponMods = _mods, _mods),
                Reference(() => _module._rangedWeapon, () => _module._rangedWeapon = weapon, weapon),
                Reference(() => _module._weaponModsComponent, () => _module._weaponModsComponent = _mods, _mods)
            };
        }

        internal bool IsBound => _bound && !_uncertain;
        internal bool IsUncertain => _uncertain;

        internal static bool TryCapture(StrictCombatPreparedEquipment reservation, Action requireOperation,
            out StrictCombatEquipmentBinding binding)
        {
            binding = null;
            try
            {
                if (reservation == null || requireOperation == null || !reservation.IsCurrentPreparation) return false;
                requireOperation();
                if (!reservation.IsUnchangedBeforeBinding()) return false;
                var candidate = new StrictCombatEquipmentBinding(reservation, requireOperation);
                candidate.BeginFrame();
                candidate.RequireCurrent(requireInactive: true);
                binding = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool TryBind()
        {
            if (_attempted || _uncertain
                || !_reservation.Source.Owner.IsCurrentNativeHealthOwner(_reservation.Source.Life)) return false;
            _attempted = true;
            try
            {
                BeginFrame();
                for (int i = 0; i < _references.Length; i++)
                {
                    RequireCurrent(requireInactive: true);
                    _mutationAttempted = true;
                    _references[i].Write();
                    _written++;
                    RequireCurrent(requireInactive: true);
                }
                RequireCurrent(requireInactive: true);
                _mutationAttempted = true;
                _reservation.Weapon._isLocalPlayer = true;
                _localWritten = true;
                RequireCurrent(requireInactive: true);
                _bound = true;
                return true;
            }
            catch (Exception)
            {
                if (_mutationAttempted)
                {
                    _uncertain = true;
                    _reservation.Retire();
                    _reservation.Source.Life.Retire();
                }
                return false;
            }
        }

        // The later activation owner validates slots, object registration and phase through
        // requireOperation. These reference checks remain usable after the clone is active.
        internal bool ReferencesCurrent()
        {
            if (!IsBound) return false;
            try { BeginFrame(); RequireCurrent(requireInactive: false); return true; }
            catch (Exception) { return false; }
        }

        private void BeginFrame()
        {
            if (!_reservation.Source.Owner.IsCurrentNativeHealthOwner(_reservation.Source.Life))
                throw new InvalidOperationException("The equipment life is not current on this thread.");
            _requireOperation();
            if (!_reservation.Source.Owner.TryRead(out _, out var accepted))
                throw new InvalidOperationException("Accepted equipment state is unavailable.");
            _revision = accepted.Revision;
            _frame = Time.frameCount;
        }

        private void RequireCurrent(bool requireInactive)
        {
            if (!_reservation.Source.Owner.IsCurrentNativeHealthOwner(_reservation.Source.Life))
                throw new InvalidOperationException("The equipment life is not current on this thread.");
            _requireOperation();
            var source = _reservation.Source;
            if (_uncertain || !_reservation.IsCurrentOwnership || Time.frameCount != _frame
                || !source.Owner.TryRead(out _, out var accepted) || accepted.Revision != _revision
                || !Live(_reservation.Controller) || !Live(_reservation.Weapon)
                || !Same(_item, source.Item) || !Same(_data, _item._data) || _data._id != 355
                || !Same(_props, source.Inventory._InventoryProps_k__BackingField)
                || !Same(source.Inventory, _props._playerInventory)
                || !Live(_animator) || !Same(_animator, _props._playerAnimator) || !Same(_animator, source.Life.Stats._animator)
                || !Live(_animation) || !Same(_animation, _props._playerAnimatorControl)
                || !Live(_receiver) || !Same(_receiver, _props._playerAnimEventReceiver)
                || !Live(_root) || !Same(_root, _props._playerTransform)
                || !Live(_base) || !Same(_base, _props._playerBase) || !Same(_baseTransform, _base.transform)
                || !Live(_fov) || !Live(_renderer)
                || !Same(_renderer, _reservation.Controller.GetComponent<ItemRenderable>())
                || !Same(_modules, _item._modules) || _modules.Count != _moduleEntries.Length
                || !Same(_attachments, _module._weaponMods) || _attachments.Count != 0
                || _module._ammoType != _cachedAmmoType || _module._ammoCount != _cachedAmmoCount
                || _module._ammoUpgradeType != _cachedUpgrade || !Same(_ammo, _module._ammo)
                || _ammo._type != 362 || !_ammo._usesInternalAmmoStorage || _ammo._maxCount != 12
                || _reservation.Weapon._isLocalPlayer != (_localWritten || _initialLocal)
                || !Live(_reservation.Stage.Root) || !Live(_reservation.ItemObject)
                || !Same(_reservation.ItemObject.transform.parent, _reservation.Stage.Root.transform))
                throw new InvalidOperationException("The captured equipment references changed.");
            if (requireInactive && (!_reservation.Stage.IsCurrent() || !_reservation.Transfer.IsCurrentTaken()
                || _reservation.Controller._isInitialised))
                throw new InvalidOperationException("Equipment preparation is no longer inactive.");
            int rangedModules = 0;
            for (int i = 0; i < _moduleEntries.Length; i++)
            {
                if (!Same(_moduleEntries[i], _modules[i]))
                    throw new InvalidOperationException("The captured item module list changed.");
                if (Same(_moduleEntries[i], _module)) rangedModules++;
            }
            if (rangedModules != 1) throw new InvalidOperationException("The ranged module is not uniquely registered.");
            if (_mods is not null)
            {
                if (!Live(_mods) || !Same(_locations, _mods._modLocations) || _locations.Count != _links.Length)
                    throw new InvalidOperationException("The captured attachment locations changed.");
                for (int i = 0; i < _links.Length; i++)
                    if (!Same(_links[i], _locations[i]) || !SameOptional(_attached[i], _links[i].AttachedMod)
                        || Live(_links[i].AttachedMod))
                        throw new InvalidOperationException("The prepared weapon has an active or changed attachment.");
            }
            _requireOperation();
            for (int i = 0; i < _references.Length; i++)
                if (!SameOptional(_references[i].Read(), i < _written ? _references[i].After : _references[i].Before))
                    throw new InvalidOperationException("An equipment reference changed during binding.");
            if (Time.frameCount != _frame || !_reservation.IsCurrentOwnership
                || !source.Owner.TryRead(out _, out accepted) || accepted.Revision != _revision)
                throw new InvalidOperationException("Accepted equipment state changed during binding.");
        }

        private static (Func<Il2CppObjectBase>, Action, Il2CppObjectBase, Il2CppObjectBase) Reference(
            Func<Il2CppObjectBase> read, Action write, Il2CppObjectBase after) => (read, write, read(), after);
        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
        private static bool SameOptional(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is null ? second is null : Same(first, second);
    }
}
