// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;
using global::Sons.Multiplayer.Client;
using global::Sons.Weapon;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        // Retains only existing equipment. The caller brackets this lookup with the
        // exact session/player binding and frame checks before publishing observations.
        private sealed class EquippedPistolCapture
        {
            private readonly PlayerHandle _binding;
            private readonly RetainedObject[] _chain;
            private readonly RetainedObject[] _setups, _items, _modules, _weapons;

            private EquippedPistolCapture(PlayerHandle binding,
                RangedWeapon weapon, GameObject itemObject, RetainedObject[] chain,
                RetainedObject[] setups, RetainedObject[] items,
                RetainedObject[] modules, RetainedObject[] weapons)
            {
                _binding = binding;
                Weapon = weapon;
                ItemObject = itemObject;
                _chain = chain;
                _setups = setups;
                _items = items;
                _modules = modules;
                _weapons = weapons;
            }

            internal RangedWeapon Weapon { get; }
            internal GameObject ItemObject { get; }

            internal static bool TryCapture(PlayerHandle binding,
                out EquippedPistolCapture capture, out WeaponObservationStatus status)
            {
                capture = null;
                try
                {
                    if (!TryLookup(binding, out var candidate, out status)) return false;
                    if (!candidate.IsCurrent())
                    {
                        status = WeaponObservationStatus.ContextChanged;
                        return false;
                    }
                    capture = candidate;
                    status = WeaponObservationStatus.Success;
                    return true;
                }
                catch (Exception)
                {
                    status = WeaponObservationStatus.Failed;
                    return false;
                }
            }

            internal bool IsCurrent()
            {
                try
                {
                    // A fresh lookup repeats uniqueness, role and attachment checks.
                    // Compare against, and continue retaining, the original wrappers.
                    return TryLookup(_binding, out var current, out _)
                        && SameMembers(_chain, current._chain)
                        && SameMembers(_setups, current._setups)
                        && SameMembers(_items, current._items)
                        && SameMembers(_modules, current._modules)
                        && SameMembers(_weapons, current._weapons);
                }
                catch (Exception) { return false; }
            }

            private static bool TryLookup(PlayerHandle binding,
                out EquippedPistolCapture capture, out WeaponObservationStatus status)
            {
                capture = null;
                status = WeaponObservationStatus.InvalidRequest;
                if (binding == null) return false;
                status = WeaponObservationStatus.BindingChanged;
                if (!MatchesPlayerAttachment(binding, binding.Player)) return false;
                var player = binding.Player;
                var attachment = binding.Attachment;

                status = WeaponObservationStatus.EquipmentUnavailable;
                LocalPlayer local = null;
                PlayerInventory inventory = null;
                CoopPlayerRemoteSetup setup = null;
                Il2CppSystem.Collections.Generic.List<ItemInstance> equippedItems = null;
                RetainedObject[] setups = Array.Empty<RetainedObject>();
                RetainedObject[] items = Array.Empty<RetainedObject>();
                ItemInstance item = null;
                if (binding.IsLocalPlayer)
                {
                    local = LocalPlayer._instance;
                    inventory = LocalPlayer._Inventory_k__BackingField;
                    if (!Live(local) || !Live(inventory)) return false;
                    if (!MatchesPlayerAttachment(binding, LocalPlayer.Entity))
                    {
                        status = WeaponObservationStatus.BindingChanged;
                        return false;
                    }
                    item = inventory.RightHandItem;
                    if (!Live(item) || item._itemID != 355) return false;
                }
                else
                {
                    var foundSetups = player.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                    if (!Live(foundSetups)) return false;
                    int setupCount = foundSetups.Length;
                    if (!WithinLimit(setupCount, 32, out status)) return false;
                    if (!TryRetainMembers(setupCount, i => foundSetups[i], out setups)) return false;
                    foreach (var member in setups)
                    {
                        var candidate = member.Value as CoopPlayerRemoteSetup;
                        // entity's getter may lazily bind a parent; read only the cache.
                        if (candidate == null || !MatchesPlayerAttachment(binding, candidate._entity)) continue;
                        if (setup != null)
                        {
                            status = WeaponObservationStatus.AmbiguousEquipment;
                            return false;
                        }
                        setup = candidate;
                    }
                    if (setup == null) return false;
                    equippedItems = setup._equippedItemInstances;
                    if (!Live(equippedItems)) return false;
                    int itemCount = equippedItems.Count;
                    if (!WithinLimit(itemCount, 32, out status)) return false;
                    if (!TryRetainMembers(itemCount, i => equippedItems[i], out items)) return false;
                    foreach (var member in items)
                    {
                        var candidate = member.Value as ItemInstance;
                        if (candidate == null || candidate._itemID != 355) continue;
                        if (item != null)
                        {
                            status = WeaponObservationStatus.AmbiguousEquipment;
                            return false;
                        }
                        item = candidate;
                    }
                    if (item == null) return false;
                }

                var itemObject = item._ItemObject_k__BackingField;
                var moduleList = item._modules;
                if (!Live(itemObject) || !Live(moduleList)) return false;
                int moduleCount = moduleList.Count;
                if (!WithinLimit(moduleCount, 32, out status)) return false;
                if (!TryRetainMembers(moduleCount, i => moduleList[i], out var modules)) return false;
                RangedWeaponItemInstanceModule module = null;
                foreach (var member in modules)
                {
                    var candidate = member.Value?.TryCast<RangedWeaponItemInstanceModule>();
                    if (candidate == null) continue;
                    if (!Live(candidate)) return false;
                    if (module != null)
                    {
                        status = WeaponObservationStatus.AmbiguousEquipment;
                        return false;
                    }
                    module = candidate;
                }
                if (module == null) return false;
                var weapon = module._rangedWeapon;
                if (!Live(weapon) || weapon._weaponItemId != 355 || weapon._controlledByAI
                    || weapon._isLocalPlayer != binding.IsLocalPlayer) return false;

                var foundWeapons = itemObject.GetComponentsInChildren<RangedWeapon>(true);
                if (!Live(foundWeapons)) return false;
                int weaponCount = foundWeapons.Length;
                if (!WithinLimit(weaponCount, 8, out status)) return false;
                if (!TryRetainMembers(weaponCount, i => foundWeapons[i], out var weapons)) return false;
                RangedWeapon hierarchyWeapon = null;
                foreach (var member in weapons)
                {
                    var candidate = member.Value as RangedWeapon;
                    if (candidate == null || candidate._weaponItemId != 355) continue;
                    if (hierarchyWeapon != null)
                    {
                        status = WeaponObservationStatus.AmbiguousEquipment;
                        return false;
                    }
                    hierarchyWeapon = candidate;
                }
                if (!SameUnityObject(weapon, hierarchyWeapon)) return false;

                // List wrappers and every ordered member are retained. Query-result arrays
                // are newly allocated by Unity, so their members, not array addresses, matter.
                Il2CppObjectBase[] chain = { player, attachment, local, inventory, setup,
                    equippedItems, item, itemObject, moduleList, module, weapon };
                if (!TryRetainMembers(chain.Length, i => chain[i], out var retainedChain)) return false;
                capture = new EquippedPistolCapture(binding, weapon, itemObject,
                    retainedChain, setups, items, modules, weapons);
                status = WeaponObservationStatus.Success;
                return true;
            }

            private static bool WithinLimit(int count, int maximum, out WeaponObservationStatus status)
            {
                bool withinLimit = count >= 0 && count <= maximum;
                status = withinLimit
                    ? WeaponObservationStatus.EquipmentUnavailable
                    : WeaponObservationStatus.LimitReached;
                return withinLimit;
            }

            private static bool TryRetainMembers(int count, Func<int, Il2CppObjectBase> read,
                out RetainedObject[] members)
            {
                members = count == 0 ? Array.Empty<RetainedObject>() : new RetainedObject[count];
                for (int i = 0; i < count; i++)
                {
                    var value = read(i);
                    if (value != null && !Live(value)) return false;
                    if (value is UnityEngine.Object unity && !Live(unity)) return false;
                    members[i] = new RetainedObject(value);
                }
                return true;
            }

            private static bool SameMembers(RetainedObject[] before, RetainedObject[] after)
            {
                if (before.Length != after.Length) return false;
                for (int i = 0; i < before.Length; i++)
                    if (!before[i].Matches(after[i])) return false;
                return true;
            }

            private static bool Live(Il2CppObjectBase value)
                => value != null && !value.WasCollected && value.Pointer != IntPtr.Zero;

            private static bool Live(UnityEngine.Object value)
                => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value;

            private static bool SameUnityObject(UnityEngine.Object before, UnityEngine.Object after)
                => Live(before) && Live(after) && before.Pointer == after.Pointer
                    && before.GetInstanceID() == after.GetInstanceID();

            private readonly struct RetainedObject
            {
                internal RetainedObject(Il2CppObjectBase value)
                {
                    Value = value;
                    Pointer = value?.Pointer ?? IntPtr.Zero;
                    UnityObject = value as UnityEngine.Object;
                    InstanceId = UnityObject is null ? 0 : UnityObject.GetInstanceID();
                }

                internal Il2CppObjectBase Value { get; }
                private IntPtr Pointer { get; }
                private UnityEngine.Object UnityObject { get; }
                private int InstanceId { get; }

                internal bool Matches(RetainedObject other)
                {
                    if (Value == null || other.Value == null) return Value == null && other.Value == null;
                    if (!Live(Value) || !Live(other.Value) || Value.Pointer != Pointer
                        || other.Value.Pointer != other.Pointer || Pointer != other.Pointer) return false;
                    if (UnityObject is null || other.UnityObject is null)
                        return UnityObject is null && other.UnityObject is null;
                    return Live(UnityObject) && Live(other.UnityObject)
                        && InstanceId == other.InstanceId && UnityObject.GetInstanceID() == InstanceId
                        && other.UnityObject.GetInstanceID() == other.InstanceId;
                }
            }
        }
    }
}
