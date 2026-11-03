// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Captures an existing idle rebreather without equipping, refreshing modules
    // or reading its air into accepted server state.
    internal sealed partial class StrictCombatLocalRebreather
    {
        private readonly StrictCombatBinding _binding;
        private readonly LocalPlayer _local;
        private readonly Il2CppReferenceArray<ItemInstance> _slots, _nextSlots;
        private readonly (nint Local, nint Inventory, nint Props, nint Slots,
            nint NextSlots, nint Item, nint ItemObject, nint Controller, nint Module, nint Breathing) _identity;

        private StrictCombatLocalRebreather(StrictCombatClient owner, StrictCombatLifeBarrier life,
            in StrictCombatBinding binding, PlayerInventory inventory, ItemInstance item,
            GameObject itemObject, RebreatherController controller, VolumeContainerItemInstanceModule module)
        {
            Owner = owner;
            Life = life;
            LifeGeneration = life.LifeGeneration;
            _binding = binding;
            _local = LocalPlayer._instance;
            Inventory = inventory;
            Props = inventory._InventoryProps_k__BackingField;
            _slots = inventory._equipmentSlots;
            _nextSlots = inventory._equipmentSlotsNext;
            Item = item;
            ItemObject = itemObject;
            Controller = controller;
            Module = module;
            Breathing = life.Vitals.LungBreathing;
            _identity = (_local.Pointer, inventory.Pointer, Props.Pointer, _slots.Pointer,
                _nextSlots.Pointer, item.Pointer, itemObject.Pointer, controller.Pointer, module.Pointer, Breathing.Pointer);
        }

        internal PlayerInventory Inventory { get; }
        internal InventoryProps Props { get; }
        internal ItemInstance Item { get; }
        internal GameObject ItemObject { get; }
        internal RebreatherController Controller { get; }
        internal VolumeContainerItemInstanceModule Module { get; }
        internal BreathingData Breathing { get; }
        internal StrictCombatClient Owner { get; }
        internal StrictCombatLifeBarrier Life { get; }
        internal ulong LifeGeneration { get; }

        internal static bool TryCapture(StrictCombatClient owner, StrictCombatLifeBarrier life,
            in StrictCombatBinding binding, in StrictCombatSnapshot state, out StrictCombatLocalRebreather capture)
        {
            capture = null;
            try
            {
                if (owner == null || life == null || !owner.IsCurrentNativeHealthOwner(life)) return false;
                int frame = Time.frameCount;
                var inventory = LocalPlayer._Inventory_k__BackingField;
                if (!Live(inventory) || !inventory.TryGetEquipped(444, out var item)
                    || !Live(item) || item._itemID != 444) return false;
                var itemObject = item._ItemObject_k__BackingField;
                if (!Live(itemObject) || !TryFindController(itemObject, out var controller)
                    || !item.TryGetModule<VolumeContainerItemInstanceModule>(out var module)
                    || !Live(module)) return false;
                var candidate = new StrictCombatLocalRebreather(owner, life, in binding,
                    inventory, item, itemObject, controller, module);
                if (!candidate.IsCurrent(in binding, in state) || frame != Time.frameCount) return false;
                capture = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool IsCurrent(in StrictCombatBinding binding, in StrictCombatSnapshot state)
        {
            try
            {
                // Reject the wrong owner or thread before any Unity reads.
                if (!CurrentOwner(in binding, in state)) return false;
                int frame = Time.frameCount;
                if (!Same(_local, LocalPlayer._instance)
                    || !Same(Inventory, LocalPlayer._Inventory_k__BackingField)
                    || !Same(Inventory, Life.Stats._inventory)
                    || !Same(Props, Inventory._InventoryProps_k__BackingField)
                    || !Same(Inventory, Props._playerInventory)
                    || !Same(Props._playerAnimator, Life.Stats._animator)
                    || Props._gameObjectInstances == null
                    || !Props._gameObjectInstances.TryGetValue(444, out var equippedObject)
                    || !Same(ItemObject, equippedObject)
                    || !Same(_slots, Inventory._equipmentSlots)
                    || !Same(_nextSlots, Inventory._equipmentSlotsNext)
                    || !Inventory.TryGetEquipped(444, out var item) || !Same(Item, item)
                    || Item._itemID != 444 || !Same(ItemObject, Item._ItemObject_k__BackingField)
                    || !ItemObject.activeInHierarchy
                    || !TryFindController(ItemObject, out var controller) || !Same(Controller, controller)
                    || !Same(Item, Controller._itemInstance)
                    || !Item.TryGetModule<VolumeContainerItemInstanceModule>(out var module)
                    || !Same(Module, module) || !Same(Module, Controller._airCapacity)
                    || !Same(Life.Vitals, Controller._vitals)
                    || !Same(Breathing, Life.Vitals.LungBreathing)
                    || !Same(Life.Stats._animator, Controller._playerAnimator)
                    || !NativeContextCurrent()) return false;
                return CurrentOwner(in binding, in state) && frame == Time.frameCount;
            }
            catch (Exception) { return false; }
        }

        internal bool MatchesNativeObjects(StrictCombatLocalRebreather other)
            => other != null && ReferenceEquals(Owner, other.Owner) && ReferenceEquals(Life, other.Life)
                && LifeGeneration == other.LifeGeneration
                && _binding.SameBinding(in other._binding)
                && _identity == other._identity;

        internal bool CanRetainNativeForNextLife(StrictCombatLocalRebreather other)
            => other != null && LifeGeneration != 0 && LifeGeneration != ulong.MaxValue
                && other.LifeGeneration == LifeGeneration + 1
                && ReferenceEquals(Owner, other.Owner) && ReferenceEquals(Life, other.Life)
                && _binding.SameBinding(in other._binding) && _identity == other._identity;

        private bool CurrentOwner(in StrictCombatBinding binding, in StrictCombatSnapshot state)
            => Owner.IsCurrentNativeHealthOwner(Life)
                && LifeGeneration != 0 && state.LifeGeneration == LifeGeneration
                && LifeGeneration == Life.LifeGeneration
                && state.LifeStage == StrictCombatLifeStage.Alive
                && _binding.SameBinding(in binding)
                && Owner.TryRead(out var currentBinding, out var currentState)
                && binding.SameBinding(in currentBinding)
                && currentState.Revision == state.Revision
                && currentState.LifeGeneration == state.LifeGeneration
                && currentState.LifeStage == StrictCombatLifeStage.Alive;

        private bool NativeContextCurrent()
        {
            if (!Inventory.isActiveAndEnabled || Inventory._currentView != PlayerInventory.PlayerViews.World
                || Inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                || Inventory._equipSequenceRight != null || Inventory._IsThrowing_k__BackingField
                || Inventory._IsInQuickSelect_k__BackingField
                || !Controller.isActiveAndEnabled || !Controller._isInitialised
                || Controller._isUnequipping || Controller._isStashing || Controller._isDropping
                || Controller._isEquippingPrevious || Controller._firstLookIsRunning || Controller._isRefilling
                || Life.Stats._dyingState != PlayerStats.DyingState.Alive || Life.Stats._playerIsDowned
                || Life.Stats._respawning || Life.Stats._healing || Life.Stats._isHealing
                || Life.Vitals._hasDied) return false;
            int matches = 0;
            for (int i = 0; i < _slots.Length; i++)
                if (Same(Item, _slots[i])) matches++;
            if (matches != 1) return false;
            // Known pending equipment work must be absent, regardless of slot.
            for (int i = 0; i < _nextSlots.Length; i++)
                if (_nextSlots[i] != null) return false;
            return Controller.IsLocalPlayer() && Controller.IsItemIdle();
        }

        private static bool TryFindController(GameObject itemObject, out RebreatherController controller)
        {
            controller = null;
            var controllers = itemObject.GetComponentsInChildren<RebreatherController>(true);
            if (!Live(controllers) || controllers.Length != 1 || !Live(controllers[0])) return false;
            controller = controllers[0];
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
