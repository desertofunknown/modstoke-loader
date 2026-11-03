// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Native suppression starts before initialization. Input authority is acquired only
    // through the ordinary completed-weapon capture after the equip transaction finishes.
    internal sealed class StrictCombatPreparedEquipment
    {
        private readonly StrictCombatEquipmentPrefab _stage;
        private readonly StrictCombatEquipmentTransfer _transfer;
        private readonly StrictCombatBinding _binding;
        private readonly ulong _life;
        private int _retired;
        private StrictCombatWeaponBarrier _adopted;

        internal StrictCombatPreparedEquipment(StrictCombatLocalWeapon source,
            StrictCombatEquipmentPrefab stage, StrictCombatEquipmentTransfer transfer,
            RangedWeaponController controller, RangedWeapon weapon, RangedWeapon.Ammo initialAmmo,
            in StrictCombatBinding binding, in StrictCombatSnapshot accepted)
        {
            Source = source;
            _stage = stage;
            _transfer = transfer;
            Controller = controller;
            Weapon = weapon;
            InitialAmmo = initialAmmo;
            ItemObject = stage.Clone;
            _binding = binding;
            _life = accepted.LifeGeneration;
        }

        internal StrictCombatLocalWeapon Source { get; }
        internal StrictCombatEquipmentPrefab Stage => _stage;
        internal StrictCombatEquipmentTransfer Transfer => _transfer;
        internal RangedWeaponController Controller { get; }
        internal RangedWeapon Weapon { get; }
        internal RangedWeapon.Ammo InitialAmmo { get; }
        internal GameObject ItemObject { get; }
        internal bool IsRetired => Volatile.Read(ref _retired) != 0;
        internal bool IsAdopted => Volatile.Read(ref _adopted) != null;
        internal bool IsCurrentPreparation => !IsAdopted && CurrentLife();
        internal bool IsCurrentOwnership => CurrentLife() && Volatile.Read(ref _adopted)?.IsRetired != true;

        internal void Retire()
        {
            Interlocked.Exchange(ref _retired, 1);
            Volatile.Read(ref _adopted)?.Retire();
        }

        internal bool IsUnchangedBeforeBinding()
        {
            if (!CurrentLife() || IsAdopted) return false;
            int frame = Time.frameCount;
            if (!_stage.IsCurrent() || !_transfer.IsCurrentTaken()
                || !Same(_transfer.Item, Source.Item) || !Same(_stage.Clone, ItemObject)
                || !Live(ItemObject) || ItemObject.activeInHierarchy
                || !Live(Controller) || !Live(Weapon)
                || !Same(InitialAmmo, Weapon._ammo)
                || InitialAmmo._type != 362 || !InitialAmmo._usesInternalAmmoStorage || InitialAmmo._maxCount != 12
                || Weapon._weaponItemId != 355 || Weapon._controlledByAI
                || Controller._isInitialised
                || (Controller._itemInstance != null && !Same(Controller._itemInstance, Source.Item))
                || !Source.Item.TryGetModule<RangedWeaponItemInstanceModule>(out var module)
                || !Same(module, Source.Module) || !Same(module._ammo, Source.Ammo)
                || !ContainsOnlyExpectedComponents()) return false;
            return CurrentLife() && frame == Time.frameCount;
        }

        internal bool CanAdopt(StrictCombatLocalWeapon capture)
        {
            return !IsAdopted && CanRepresent(capture);
        }

        internal bool CanRepresent(StrictCombatLocalWeapon capture)
        {
            if (!CurrentLife() || capture == null
                || !ReferenceEquals(capture.Owner, Source.Owner) || !ReferenceEquals(capture.Life, Source.Life)
                || !capture.MatchesEquipmentBinding(in _binding)
                || !Same(capture.Inventory, Source.Inventory) || !Same(capture.Props, Source.Props)
                || !Same(capture.Item, Source.Item) || !Same(capture.Module, Source.Module)
                || !Same(capture.Ammo, Source.Ammo) || !Same(capture.ItemObject, ItemObject)
                || !Same(capture.Controller, Controller) || !Same(capture.Weapon, Weapon)) return false;
            // The stage is active by this point. Its inactive-state predicate is no longer
            // appropriate, but the prepared object's identity and parent must still match.
            return Live(_stage.Root) && Live(ItemObject)
                && Same(ItemObject.transform.parent, _stage.Root.transform) && CurrentLife();
        }

        internal void Adopt(StrictCombatWeaponBarrier barrier)
        {
            Volatile.Write(ref _adopted, barrier);
            if (IsRetired) barrier.Retire();
        }

        private bool CurrentLife()
            => !IsRetired && Source.Owner.IsCurrentNativeHealthOwner(Source.Life)
                && Source.Life.LifeGeneration == _life
                && Source.Owner.TryRead(out var binding, out var accepted)
                && Source.MatchesEquipmentBinding(in binding)
                && binding.SameBinding(in _binding)
                && accepted.LifeGeneration == _life && accepted.LifeStage == StrictCombatLifeStage.Alive
                && accepted.OwnsPistol && accepted.Equipped;

        private bool ContainsOnlyExpectedComponents()
        {
            var controllers = ItemObject.GetComponentsInChildren<HeldControllerBase>(true);
            var weapons = ItemObject.GetComponentsInChildren<RangedWeapon>(true);
            return controllers != null && controllers.Length == 1 && Same(controllers[0], Controller)
                && weapons != null && weapons.Length == 1 && Same(weapons[0], Weapon);
        }

        private static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                && first.Pointer == second.Pointer;
    }

    internal static partial class StrictCombatWeaponHooks
    {
        private static StrictCombatPreparedEquipment[] _preparedEquipment = Array.Empty<StrictCombatPreparedEquipment>();
        private static bool _reservingEquipment;

        internal static bool TryReserveEquipment(StrictCombatLocalWeapon source,
            StrictCombatEquipmentPrefab stage, StrictCombatEquipmentTransfer transfer,
            out StrictCombatPreparedEquipment reservation)
        {
            reservation = null;
            if (!OnOwnerThread || !_prepared || !_hooksInstalled || _historyIncomplete || _reservingEquipment
                || source == null || stage == null || transfer == null)
                return false;
            _reservingEquipment = true;
            try
            {
                var sourceOwner = FindController(source.Controller);
                if (sourceOwner == null || !ReferenceEquals(sourceOwner.Capture, source)
                    || !source.Owner.IsCurrentNativeHealthOwner(source.Life)
                    || !source.Owner.TryRead(out var binding, out var accepted)
                    || !source.MatchesEquipmentBinding(in binding)
                    || !stage.IsCurrent() || !transfer.IsCurrentTaken()
                    || !Same(transfer.Item, source.Item)) return false;
                int frame = Time.frameCount;
                var controllers = stage.Clone.GetComponentsInChildren<HeldControllerBase>(true);
                var weapons = stage.Clone.GetComponentsInChildren<RangedWeapon>(true);
                if (controllers == null || controllers.Length != 1 || weapons == null || weapons.Length != 1)
                    return false;
                var controller = controllers[0]?.TryCast<CompactPistolWeaponController>();
                var weapon = weapons[0];
                if (controller is null || controller.WasCollected || !controller
                    || weapon is null || weapon.WasCollected || !weapon) return false;
                var ammo = weapon._ammo;
                // The native constructor does not allocate ammo. Missing prefab state is
                // unsupported here; reservation must never manufacture replacement ammo.
                if (ammo is null || ammo.WasCollected || ammo.Pointer == 0) return false;
                var retained = Volatile.Read(ref _preparedEquipment);
                int pending = 0;
                foreach (var existing in retained)
                {
                    if (!existing.IsAdopted) pending++;
                    if (Same(existing.Controller, controller) || Same(existing.Weapon, weapon)
                        || Same(existing.ItemObject, stage.Clone)) return false;
                }
                var owners = Volatile.Read(ref _owners);
                if (retained.Length >= MaximumOwners || owners.Length + pending >= MaximumOwners) return false;
                foreach (var owner in owners)
                    if (Same(owner.Capture.Controller, controller) || Same(owner.Capture.Weapon, weapon)
                        || Same(owner.Capture.ItemObject, stage.Clone)) return false;
                var candidate = new StrictCombatPreparedEquipment(source, stage, transfer,
                    controller, weapon, ammo, in binding, in accepted);
                if (!candidate.IsUnchangedBeforeBinding() || frame != Time.frameCount
                    || !ReferenceEquals(retained, Volatile.Read(ref _preparedEquipment))
                    || !ReferenceEquals(owners, Volatile.Read(ref _owners)) || _historyIncomplete) return false;
                var next = new StrictCombatPreparedEquipment[retained.Length + 1];
                Array.Copy(retained, next, retained.Length);
                next[^1] = candidate;
                // Publish once. Failed or retired operations keep their native suppression.
                Volatile.Write(ref _preparedEquipment, next);
                reservation = candidate;
                return true;
            }
            catch (Exception) { return false; }
            finally { _reservingEquipment = false; }
        }

        private static StrictCombatPreparedEquipment FindPreparedController(Il2CppObjectBase controller)
        {
            foreach (var prepared in Volatile.Read(ref _preparedEquipment))
                if (Same(prepared.Controller, controller)) return prepared;
            return null;
        }

        private static bool IsPreparedObject(GameObject itemObject)
        {
            foreach (var prepared in Volatile.Read(ref _preparedEquipment))
                if (Same(prepared.ItemObject, itemObject)) return true;
            return false;
        }

        private static bool IsPreparedWeapon(nint pointer)
        {
            foreach (var prepared in Volatile.Read(ref _preparedEquipment))
                if (prepared.Weapon.Pointer == pointer) return true;
            return false;
        }

        private static bool IsPreparedAmmo(nint pointer)
        {
            foreach (var prepared in Volatile.Read(ref _preparedEquipment))
                if (prepared.InitialAmmo.Pointer == pointer) return true;
            return false;
        }

        private static int PendingEquipmentReservations()
        {
            int count = 0;
            foreach (var prepared in Volatile.Read(ref _preparedEquipment))
                if (!prepared.IsAdopted) count++;
            return count;
        }
    }
}
