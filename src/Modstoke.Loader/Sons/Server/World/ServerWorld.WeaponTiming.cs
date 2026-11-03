// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Items.Core;
using global::Sons.Weapon;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Copies fire and magazine settings from a loaded held-prefab template on the server game thread.
        /// Shares the ballistic read budget and reentrancy guard. Does not load assets, initialize the item
        /// database or need the bullet handler. Expect PrefabUnavailable on a dedicated server until held
        /// prefabs there are qualified in-game.
        /// </summary>
        public static bool TryReadWeaponTiming(int weaponItemId, out WeaponTimingSnapshot snapshot,
            out BallisticReadStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                if (!TryBallisticContext(out var world, out int frame, out status)) return false;
                if (weaponItemId <= 0)
                {
                    status = BallisticReadStatus.InvalidRequest;
                    return false;
                }
                if (!ReserveBallisticRead(world, frame, out status)) return false;
                _ballisticReading = entered = true;

                status = BallisticReadStatus.ItemUnavailable;
                var database = ItemDatabaseManager._instance;
                var items = ItemDatabaseManager._itemsCache;
                if (!LiveBallisticObject(database) || items is null || items.WasCollected
                    || !items.TryGetValue(weaponItemId, out var weaponData)
                    || !LiveBallisticObject(weaponData) || weaponData._id != weaponItemId)
                    return false;

                status = BallisticReadStatus.PrefabUnavailable;
                var prefab = weaponData._heldPrefab;
                if (!LiveBallisticObject(prefab)) return false;
                var weapons = prefab.GetComponentsInChildren<RangedWeapon>(true);
                if (weapons is null || weapons.WasCollected || weapons.Length == 0
                    || weapons.Length > MaximumWeaponComponents)
                    return false;
                RangedWeapon weapon = null;
                for (int i = 0; i < weapons.Length; i++)
                {
                    var candidate = weapons[i];
                    if (!LiveBallisticObject(candidate) || candidate._weaponItemId != weaponItemId) continue;
                    if (weapon != null) { status = BallisticReadStatus.InvalidProfile; return false; }
                    weapon = candidate;
                }
                if (weapon == null) return false;

                var controllers = prefab.GetComponentsInChildren<RangedWeaponController>(true);
                if (controllers is null || controllers.WasCollected || controllers.Length == 0
                    || controllers.Length > MaximumWeaponComponents)
                    return false;
                RangedWeaponController controller = null;
                RangedWeaponController unlinked = null;
                for (int i = 0; i < controllers.Length; i++)
                {
                    var candidate = controllers[i];
                    if (!LiveBallisticObject(candidate)) continue;
                    var linked = candidate._rangedWeapon;
                    if (!LiveBallisticObject(linked)) { unlinked = candidate; continue; }
                    if (linked.Pointer != weapon.Pointer) continue;
                    if (controller != null) { status = BallisticReadStatus.InvalidProfile; return false; }
                    controller = candidate;
                }
                // Templates can leave the link to Awake; accept that only when nothing else could match.
                if (controller == null && controllers.Length == 1 && unlinked != null)
                    controller = unlinked;
                if (controller == null) { status = BallisticReadStatus.InvalidProfile; return false; }
                bool wasLinked = LiveBallisticObject(controller._rangedWeapon);

                var ammo = weapon._ammo;
                if (ammo is null || ammo.WasCollected) { status = BallisticReadStatus.InvalidProfile; return false; }
                var typeName = controller.GetIl2CppType().FullName;
                var captured = new WeaponTimingSnapshot(frame, weaponItemId, typeName, controller, ammo);
                if (!NonnegativeBallisticValue(captured.FireDelaySeconds) || captured.MagazineCapacity < 0)
                {
                    status = BallisticReadStatus.InvalidProfile;
                    return false;
                }

                status = BallisticReadStatus.ContextChanged;
                if (!TryBallisticContext(out var currentWorld, out int currentFrame, out _)
                    || !ReferenceEquals(world, currentWorld) || frame != currentFrame
                    || !SameBallisticObject(database, ItemDatabaseManager._instance)
                    || ItemDatabaseManager._itemsCache is not { } currentItems || currentItems.WasCollected
                    || currentItems.Pointer != items.Pointer
                    || !currentItems.TryGetValue(weaponItemId, out var currentWeaponData)
                    || !SameBallisticObject(weaponData, currentWeaponData)
                    || !SameBallisticObject(prefab, weaponData._heldPrefab)
                    || !LiveBallisticObject(weapon) || weapon._weaponItemId != weaponItemId
                    || !LiveBallisticObject(controller))
                    return false;
                var currentLink = controller._rangedWeapon;
                var currentAmmo = weapon._ammo;
                if ((wasLinked ? !SameBallisticObject(currentLink, weapon) : LiveBallisticObject(currentLink))
                    || currentAmmo is null || currentAmmo.WasCollected || currentAmmo.Pointer != ammo.Pointer
                    || currentAmmo._type != captured.AmmoItemId)
                    return false;

                snapshot = captured;
                status = BallisticReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = BallisticReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _ballisticReading = false; }
        }
    }
}
