// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Ballistics;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        public const int MaximumBallisticReadsPerFrame = 32;
        private const int MaximumWeaponComponents = 16;
        private static WorldRecordContext _ballisticWorld;
        private static int _ballisticFrame;
        private static int _ballisticReads;
        private static bool _ballisticReading;

        /// <summary>
        /// Copies settings and a ranged weapon's existing prefab/ammunition profile on the server.
        /// Does not load missing assets, initialize the item database, or create a weapon.
        /// </summary>
        public static bool TryReadBallisticWeapon(int weaponItemId, out BallisticSnapshot snapshot,
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
                if (!CaptureBallisticFrame(world, frame, out var environment, out var handler,
                    out var settings, out status)) return false;

                // The public item lookup can initialize the database. Observe only its loaded cache.
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
                    // Ambiguous prefabs need a more specific integration, not an arbitrary first match.
                    if (weapon != null) { status = BallisticReadStatus.InvalidProfile; return false; }
                    weapon = candidate;
                }
                if (weapon == null) return false;

                status = BallisticReadStatus.ItemUnavailable;
                var ammo = weapon._ammo;
                if (ammo is null || ammo.WasCollected || ammo._isMissingAmmoProperties) return false;
                int ammoItemId = ammo._type;
                if (ammoItemId <= 0 || !items.TryGetValue(ammoItemId, out var ammoData)
                    || !LiveBallisticObject(ammoData) || ammoData._id != ammoItemId)
                    return false;
                var properties = ammo._properties;
                var databaseProperties = ammoData._ammoProperties;
                if (!LiveBallisticObject(properties) || !SameBallisticObject(properties, databaseProperties)) return false;
                var projectile = properties.ProjectileInfo;
                if (projectile is null || projectile.WasCollected) return false;

                var captured = new BallisticSnapshot(environment, weaponItemId, ammoItemId,
                    projectile, weaponData, weapon);
                if (!ValidBallisticSnapshot(captured))
                {
                    status = BallisticReadStatus.InvalidProfile;
                    return false;
                }

                // Reject replacement of any source while its fields were being copied.
                status = BallisticReadStatus.ContextChanged;
                if (!IsCurrentBallisticSnapshot(captured)
                    || !SameBallisticObject(database, ItemDatabaseManager._instance)
                    || ItemDatabaseManager._itemsCache is not { } currentItems || currentItems.WasCollected
                    || currentItems.Pointer != items.Pointer
                    || !currentItems.TryGetValue(weaponItemId, out var currentWeaponData)
                    || !SameBallisticObject(weaponData, currentWeaponData)
                    || !currentItems.TryGetValue(ammoItemId, out var currentAmmoData)
                    || !SameBallisticObject(ammoData, currentAmmoData)
                    || !SameBallisticObject(handler, BulletHandler._instance)
                    || !SameBallisticObject(settings, handler.ballisticSettings)
                    || !SameBallisticObject(prefab, weaponData._heldPrefab)
                    || !LiveBallisticObject(weapon) || weapon._weaponItemId != weaponItemId)
                    return false;
                var currentAmmo = weapon._ammo;
                var currentProjectile = properties.ProjectileInfo;
                if (currentAmmo is null || currentAmmo.WasCollected || currentAmmo.Pointer != ammo.Pointer
                    || currentAmmo._type != ammoItemId || currentAmmo._isMissingAmmoProperties
                    || !SameBallisticObject(properties, currentAmmo._properties)
                    || !SameBallisticObject(properties, ammoData._ammoProperties)
                    || currentProjectile is null || currentProjectile.WasCollected
                    || currentProjectile.Pointer != projectile.Pointer || !IsCurrentBallisticSnapshot(captured))
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

        /// <summary>
        /// Checks only the snapshot's world and frame lifetime on the server game thread.
        /// Copied values are observations; this does not freeze assets or prove they are unchanged.
        /// </summary>
        public static bool IsCurrentBallisticSnapshot(BallisticSnapshot snapshot)
        {
            return snapshot != null && IsCurrentBallisticFrame(snapshot.Environment);
        }

        /// <summary>Reads one current simulation frame without looking up or loading any weapon assets.</summary>
        public static bool TryReadBallisticFrame(out BallisticFrame snapshot,
            out BallisticReadStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                if (!TryBallisticContext(out var world, out int frame, out status)
                    || !ReserveBallisticRead(world, frame, out status)) return false;
                _ballisticReading = entered = true;
                if (!CaptureBallisticFrame(world, frame, out var captured, out _, out _, out status)) return false;
                snapshot = captured;
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

        /// <summary>Checks that a simulation frame still belongs to the running server world and current Unity frame.</summary>
        public static bool IsCurrentBallisticFrame(BallisticFrame snapshot)
        {
            if (snapshot == null) return false;
            try
            {
                return TryBallisticContext(out var world, out int frame, out _)
                    && ReferenceEquals(world, snapshot.World) && frame == snapshot.Frame;
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        /// <summary>Also requires the launch profile to belong to this world; its captured frame may be older.</summary>
        public static bool IsCurrentBallisticFrame(BallisticFrame snapshot, BallisticSnapshot launch)
        {
            return snapshot != null && launch != null && ReferenceEquals(snapshot.World, launch.World)
                && IsCurrentBallisticFrame(snapshot);
        }

        private static bool CaptureBallisticFrame(WorldRecordContext world, int frame,
            out BallisticFrame snapshot, out BulletHandler handler, out BallisticSettings settings,
            out BallisticReadStatus status)
        {
            snapshot = null;
            settings = null;
            status = BallisticReadStatus.SettingsUnavailable;
            handler = BulletHandler._instance;
            if (!LiveBallisticObject(handler)) return false;
            settings = handler.ballisticSettings;
            if (!LiveBallisticObject(settings)) return false;
            if (!TryBallisticTriggers(settings.TriggerInteraction, out var triggers))
            { status = BallisticReadStatus.InvalidProfile; return false; }
            var captured = new BallisticFrame(world, frame, settings, triggers);
            if (!ValidBallisticFrame(captured))
            { status = BallisticReadStatus.InvalidProfile; return false; }
            if (!SameBallisticObject(handler, BulletHandler._instance)
                || !SameBallisticObject(settings, handler.ballisticSettings) || !IsCurrentBallisticFrame(captured))
            { status = BallisticReadStatus.ContextChanged; return false; }
            snapshot = captured;
            status = BallisticReadStatus.Success;
            return true;
        }

        private static bool ReserveBallisticRead(WorldRecordContext world, int frame,
            out BallisticReadStatus status)
        {
            status = BallisticReadStatus.Busy;
            if (_ballisticReading) return false;
            if (!ReferenceEquals(world, _ballisticWorld) || frame != _ballisticFrame)
            {
                _ballisticWorld = world;
                _ballisticFrame = frame;
                _ballisticReads = 0;
            }
            status = BallisticReadStatus.LimitReached;
            if (_ballisticReads >= MaximumBallisticReadsPerFrame) return false;
            _ballisticReads++;
            return true;
        }

        private static bool TryBallisticContext(out WorldRecordContext world, out int frame,
            out BallisticReadStatus status)
        {
            world = null;
            frame = 0;
            var context = CheckContext();
            status = context == WorldReadStatus.WrongThread ? BallisticReadStatus.WrongThread
                : context == WorldReadStatus.NotServer ? BallisticReadStatus.NotServer
                : BallisticReadStatus.Unavailable;
            if (context != WorldReadStatus.Success) return false;
            if (!WorldRecords.TryGetCurrent(out world, out _))
            {
                status = BallisticReadStatus.WorldUnavailable;
                return false;
            }
            frame = Time.frameCount;
            return true;
        }

        private static bool TryBallisticTriggers(QueryTriggerInteraction value, out CollisionTriggers triggers)
        {
            triggers = CollisionTriggers.Ignore;
            switch (value)
            {
                case QueryTriggerInteraction.UseGlobal:
                    triggers = Physics.queriesHitTriggers ? CollisionTriggers.Collide : CollisionTriggers.Ignore;
                    return true;
                case QueryTriggerInteraction.Ignore: return true;
                case QueryTriggerInteraction.Collide: triggers = CollisionTriggers.Collide; return true;
                default: return false;
            }
        }

        private static bool ValidBallisticSnapshot(BallisticSnapshot value)
        {
            return ValidBallisticFrame(value.Environment) && value.HitMask != 0 && NonnegativeBallisticValue(value.MuzzleDamage)
                && NonnegativeBallisticValue(value.AmmoDamageMultiplier) && NonnegativeBallisticValue(value.RawDamage)
                && PositiveBallisticValue(value.MaximumSpeed) && PositiveBallisticValue(value.Weight)
                && PositiveBallisticValue(value.Diameter) && NonnegativeBallisticValue(value.DragCoefficient)
                && NonnegativeBallisticValue(value.PrecalculatedDrag) && NonnegativeBallisticValue(value.RicochetProbabilityMultiplier)
                && PositiveBallisticValue(value.LifetimeSeconds) && NonnegativeBallisticValue(value.InitialRayCheckDistance);
        }

        private static bool ValidBallisticFrame(BallisticFrame value)
        {
            return NonnegativeBallisticValue(value.DeltaTime) && PositiveBallisticValue(value.MaximumDeltaTime)
                && Finite(value.WindVelocity) && Finite(value.Gravity) && NonnegativeBallisticValue(value.AirDensity);
        }

        private static bool LiveBallisticObject(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool SameBallisticObject(UnityEngine.Object left, UnityEngine.Object right)
            => LiveBallisticObject(left) && LiveBallisticObject(right) && left.Pointer == right.Pointer;
        private static bool NonnegativeBallisticValue(float value) => float.IsFinite(value) && value >= 0;
        private static bool PositiveBallisticValue(float value) => float.IsFinite(value) && value > 0;
    }
}
