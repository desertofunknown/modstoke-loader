// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Inventory;
using global::Sons.Player;
using global::Sons.Weapon;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Captures an existing idle pistol without equipping, refreshing its modules
    // or changing ammunition. Presentation requires a separate accepted-shot operation.
    internal sealed partial class StrictCombatLocalWeapon
    {
        private readonly StrictCombatBinding _binding;
        private readonly LocalPlayer _local;
        private readonly ItemInstance _item;
        private readonly GameObject _itemObject;
        private readonly RangedWeaponItemInstanceModule _module;
        private readonly RangedWeapon.Ammo _ammo;
        private readonly Transform _camera;
        private readonly Il2CppReferenceArray<ItemInstance> _slots, _nextSlots;
        private readonly (nint Local, nint Inventory, nint Item, nint ItemObject,
            nint Controller, nint Weapon, nint Module, nint Ammo, nint Camera,
            nint Slots, nint NextSlots, nint Props) _identity;

        private StrictCombatLocalWeapon(StrictCombatClient owner, StrictCombatLifeBarrier life,
            in StrictCombatBinding binding, PlayerInventory inventory,
            ItemInstance item, GameObject itemObject, RangedWeaponController controller,
            RangedWeapon weapon, RangedWeaponItemInstanceModule module, RangedWeapon.Ammo ammo)
        {
            Owner = owner;
            Life = life;
            Inventory = inventory;
            Props = inventory._InventoryProps_k__BackingField;
            Controller = controller;
            Weapon = weapon;
            _binding = binding;
            _local = LocalPlayer._instance;
            _item = item;
            _itemObject = itemObject;
            _module = module;
            _ammo = ammo;
            _camera = LocalPlayer.MainCamTr;
            _slots = inventory._equipmentSlots;
            _nextSlots = inventory._equipmentSlotsNext;
            _identity = (_local.Pointer, inventory.Pointer, item.Pointer, itemObject.Pointer,
                controller.Pointer, weapon.Pointer, module.Pointer, ammo.Pointer, _camera.Pointer,
                _slots.Pointer, _nextSlots.Pointer, Props.Pointer);
        }

        internal ItemInstance Item => _item;
        internal GameObject ItemObject => _itemObject;
        internal InventoryProps Props { get; }
        internal RangedWeaponItemInstanceModule Module => _module;
        internal RangedWeapon.Ammo Ammo => _ammo;
        internal PlayerInventory Inventory { get; }
        internal RangedWeaponController Controller { get; }
        internal RangedWeapon Weapon { get; }
        internal StrictCombatLifeBarrier Life { get; }
        internal StrictCombatClient Owner { get; }

        internal static bool TryCapture(StrictCombatClient owner, StrictCombatLifeBarrier life,
            in StrictCombatBinding binding, in StrictCombatSnapshot state, out StrictCombatLocalWeapon capture)
        {
            capture = null;
            try
            {
                if (owner == null || life == null || !owner.IsCurrentNativeHealthOwner(life)) return false;
                int frame = Time.frameCount;
                var inventory = LocalPlayer._Inventory_k__BackingField;
                if (!Live(inventory)) return false;
                var item = inventory.RightHandItem;
                if (!Live(item) || item._itemID != 355) return false;
                var itemObject = item._ItemObject_k__BackingField;
                if (!Live(itemObject) || !TryFindController(itemObject, out var controller)
                    || !item.TryGetModule<RangedWeaponItemInstanceModule>(out var module)
                    || !Live(module)) return false;
                var weapon = controller._rangedWeapon;
                if (!Live(weapon) || !Live(weapon._ammo)) return false;
                var candidate = new StrictCombatLocalWeapon(owner, life, in binding,
                    inventory, item, itemObject, controller, weapon, module, weapon._ammo);
                if (!candidate.IsHeldCurrent(in binding, in state) || frame != Time.frameCount) return false;
                capture = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool IsHeldCurrent(in StrictCombatBinding binding, in StrictCombatSnapshot state, bool requireIdle = true)
            => HeldMatches(in binding, in state, requireIdle, forStash: false);

        internal bool IsHeldForStash(in StrictCombatBinding binding, in StrictCombatSnapshot state)
            => HeldMatches(in binding, in state, requireIdle: true, forStash: true);

        private bool HeldMatches(in StrictCombatBinding binding, in StrictCombatSnapshot state,
            bool requireIdle, bool forStash)
        {
            try
            {
                // This also rejects the wrong thread before any Unity reads.
                if (!CurrentOwner(in binding, in state, forStash)) return false;
                int frame = Time.frameCount;
                if (!Same(_local, LocalPlayer._instance)
                    || !Same(Inventory, LocalPlayer._Inventory_k__BackingField)
                    || !Same(Inventory, Life.Stats._inventory)
                    || !Same(Props, Inventory._InventoryProps_k__BackingField)
                    || !Same(Inventory, Props._playerInventory)
                    || !Same(Props._playerAnimator, Life.Stats._animator)
                    || Props._gameObjectInstances == null
                    || !Props._gameObjectInstances.TryGetValue(355, out var heldObject)
                    || !Same(_itemObject, heldObject)
                    || !Same(_slots, Inventory._equipmentSlots)
                    || !Same(_nextSlots, Inventory._equipmentSlotsNext)
                    || !Same(_item, Inventory.RightHandItem) || _item._itemID != 355
                    || !Same(_itemObject, _item._ItemObject_k__BackingField)
                    || !_itemObject.activeInHierarchy
                    || !TryFindController(_itemObject, out var controller) || !Same(Controller, controller)
                    || !Same(_item, Controller._itemInstance)
                    || !Same(Weapon, Controller._rangedWeapon)
                    || !_item.TryGetModule<RangedWeaponItemInstanceModule>(out var module)
                    || !Same(_module, module) || !Same(Weapon, _module._rangedWeapon)
                    || !Same(_ammo, _module._ammo) || !Same(_ammo, Weapon._ammo)
                    || _ammo._type != 362 || !_ammo._usesInternalAmmoStorage || _ammo._maxCount != 12
                    || Weapon._weaponItemId != 355
                    || !Weapon._isLocalPlayer || Weapon._controlledByAI
                    || !Same(_camera, LocalPlayer.MainCamTr)
                    || !NativeContextCurrent(requireIdle)) return false;
                return CurrentOwner(in binding, in state, forStash) && frame == Time.frameCount;
            }
            catch (Exception) { return false; }
        }

        internal bool CanReadInput(StrictCombatAction action,
            in StrictCombatBinding binding, in StrictCombatSnapshot state)
        {
            try
            {
                if (action != StrictCombatAction.Fire && action != StrictCombatAction.Reload) return false;
                if (!IsHeldCurrent(in binding, in state)) return false;
                int frame = Time.frameCount;
                if (action == StrictCombatAction.Fire)
                {
                    if (Controller._blockFiring || Inventory._blockRangedAttack_k__BackingField
                        || LocalPlayer.ConstructionBlockingPrimaryInteraction
                        || (Controller._mustAimToFire && !Controller.IsAiming)) return false;
                }
                else if (LocalPlayer.IsInMidAction) return false;
                // Native counts and next-fire time can diverge from accepted server state.
                // The server decides magazine, reserve, reload and cooldown eligibility.
                return IsHeldCurrent(in binding, in state) && frame == Time.frameCount;
            }
            catch (Exception) { return false; }
        }

        internal bool TryReadFireDirection(in StrictCombatBinding binding,
            in StrictCombatSnapshot state, out Vector3 direction)
        {
            direction = default;
            try
            {
                if (!CanReadInput(StrictCombatAction.Fire, in binding, in state)) return false;
                int frame = Time.frameCount;
                // This is a copied view direction, not native reticle/ray parity or aim authority.
                Vector3 forward = _camera.forward;
                double lengthSquared = (double)forward.x * forward.x
                    + (double)forward.y * forward.y + (double)forward.z * forward.z;
                if (!double.IsFinite(lengthSquared) || lengthSquared <= 0) return false;
                double length = Math.Sqrt(lengthSquared);
                var copied = new Vector3((float)(forward.x / length),
                    (float)(forward.y / length), (float)(forward.z / length));
                if (!CanReadInput(StrictCombatAction.Fire, in binding, in state)
                    || frame != Time.frameCount) return false;
                direction = copied;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool MatchesNativeObjects(StrictCombatLocalWeapon other)
            => other != null && ReferenceEquals(Owner, other.Owner) && ReferenceEquals(Life, other.Life)
                && _identity == other._identity;

        internal bool MatchesEquipmentBinding(in StrictCombatBinding binding)
            => _binding.SameBinding(in binding);

        private bool CurrentOwner(in StrictCombatBinding binding, in StrictCombatSnapshot state, bool forStash)
            => Owner.IsCurrentNativeHealthOwner(Life)
                && state.LifeGeneration != 0 && state.LifeGeneration == Life.LifeGeneration
                && state.OwnsPistol
                && (forStash ? state.LifeStage is StrictCombatLifeStage.Downed or StrictCombatLifeStage.Dead
                    : state.LifeStage == StrictCombatLifeStage.Alive && state.Equipped)
                && _binding.SameBinding(in binding)
                && Owner.TryRead(out var currentBinding, out var currentState)
                && binding.SameBinding(in currentBinding)
                && currentState.Revision == state.Revision
                && currentState.LifeGeneration == state.LifeGeneration
                && currentState.LifeStage == state.LifeStage
                && currentState.OwnsPistol && (forStash || currentState.Equipped);

        private bool NativeContextCurrent(bool requireAttackIdle)
        {
            if (!Inventory.isActiveAndEnabled || Inventory._currentView != PlayerInventory.PlayerViews.World
                || Inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                || Inventory._equipSequenceRight != null || Inventory._IsThrowing_k__BackingField
                || Inventory._IsInQuickSelect_k__BackingField
                || !Controller.isActiveAndEnabled || !Weapon.isActiveAndEnabled
                || !Controller._isInitialised || Controller._isUnequipping || Controller._isStashing
                || Controller._isDropping || Controller._isEquippingPrevious || Controller._firstLookIsRunning
                || Controller._reloadDelay != null || Controller._reloadQueued || Controller._isReloading
                || (requireAttackIdle && (int)Controller._attackState != 0)
                || Controller._isCharging || Controller._itemWasThrown
                || Controller._equipLeftHandWhenReloadIsDone || Controller._stashedLeftHandToFire
                || Life.Stats._dyingState != PlayerStats.DyingState.Alive || Life.Stats._playerIsDowned
                || Life.Stats._respawning || Life.Stats._healing || Life.Stats._isHealing
                || Life.Vitals._hasDied) return false;
            // EquipSequence can be running without a retained coroutine handle.
            // These fields reject known pending work; they do not prove every asynchronous path idle.
            for (int i = 0; i < _nextSlots.Length; i++)
                if (_nextSlots[i] != null) return false;
            return Controller.IsLocalPlayer() && (!requireAttackIdle || Controller.IsItemIdle());
        }

        private static bool TryFindController(GameObject itemObject, out RangedWeaponController controller)
        {
            controller = null;
            var controllers = itemObject.GetComponentsInChildren<RangedWeaponController>(true);
            if (!Live(controllers) || controllers.Length != 1) return false;
            var found = controllers[0];
            if (!Live(found) || found.TryCast<CompactPistolWeaponController>() == null) return false;
            controller = found;
            return true;
        }

        private static bool Live(Il2CppObjectBase value) => value != null && !value.WasCollected;
        private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
        private static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
    }
}
