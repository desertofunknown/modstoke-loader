// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Numerics;

namespace Modstoke.Sons
{
    public enum BallisticReadStatus
    {
        Unavailable,
        Success,
        WrongThread,
        NotServer,
        WorldUnavailable,
        InvalidRequest,
        LimitReached,
        Busy,
        SettingsUnavailable,
        ItemUnavailable,
        PrefabUnavailable,
        InvalidProfile,
        ContextChanged,
        Failed
    }

    /// <summary>
    /// Copied current-world ballistic data. This does not establish player inventory,
    /// authorized aim or final health loss. Use IsCurrentBallisticSnapshot to validate the current frame.
    /// </summary>
    public sealed class BallisticSnapshot
    {
        internal BallisticSnapshot(BallisticFrame environment,
            int weaponItemId, int ammoItemId,
            global::ProjectileInfo projectile, global::Sons.Items.Core.ItemData weaponData,
            global::Sons.Weapon.RangedWeapon weapon)
        {
            Environment = environment;
            WeaponItemId = weaponItemId;
            AmmoItemId = ammoItemId;
            HitMask = projectile.hitMask.value;
            MuzzleDamage = projectile.muzzleDamage;
            AmmoDamageMultiplier = weaponData.AmmoDamageMult;
            RawDamage = MuzzleDamage * AmmoDamageMultiplier;
            MaximumSpeed = projectile.maxSpeed;
            Weight = projectile.weight;
            Diameter = projectile.diameter;
            DragCoefficient = projectile.dragCoefficient;
            PrecalculatedDrag = projectile.precalculatedDrag;
            RicochetProbabilityMultiplier = projectile.ricochetProbabilityMultiplier;
            SkipImpactObject = projectile.skipImpactObject;
            FloatsInLiquid = projectile.floatsInLiquid;
            IgnoreInstigator = projectile.ignoreInstigator;
            LifetimeSeconds = weapon.lifetime;
            InitialRayCheckDistance = weapon._initialRayCheckDistance;
        }

        internal WorldRecordContext World => Environment.World;
        public BallisticFrame Environment { get; }
        public int Frame => Environment.Frame;
        public int WeaponItemId { get; }
        public int AmmoItemId { get; }
        public float FrameDeltaTime => Environment.DeltaTime;
        public float MaximumDeltaTime => Environment.MaximumDeltaTime;
        public bool BulletDrop => Environment.BulletDrop;
        public bool BulletDrag => Environment.BulletDrag;
        public Vector3 WindVelocity => Environment.WindVelocity;
        public Vector3 Gravity => Environment.Gravity;
        public float AirDensity => Environment.AirDensity;
        public CollisionTriggers Triggers => Environment.Triggers;
        public int HitMask { get; }
        public float MuzzleDamage { get; }
        public float AmmoDamageMultiplier { get; }
        public float RawDamage { get; }
        public float MaximumSpeed { get; }
        public float Weight { get; }
        public float Diameter { get; }
        public float DragCoefficient { get; }
        public float PrecalculatedDrag { get; }
        public float RicochetProbabilityMultiplier { get; }
        public bool SkipImpactObject { get; }
        public bool FloatsInLiquid { get; }
        public bool IgnoreInstigator { get; }
        public float LifetimeSeconds { get; }
        public float InitialRayCheckDistance { get; }
    }

    /// <summary>
    /// Fire and magazine settings copied from a loaded held-prefab template, not a player's live gun.
    /// Attachments, runtime ammunition switching and other modifiers are not reflected, and the weapon ID
    /// usually comes from client-replicated state. No reload duration is offered: the game has no such field,
    /// since reloads are driven by animation and state. Evidence for clamps and scoring, never a verdict.
    /// </summary>
    public sealed class WeaponTimingSnapshot
    {
        internal WeaponTimingSnapshot(int frame, int weaponItemId, string controllerTypeName,
            global::Sons.Weapon.RangedWeaponController controller, global::Sons.Weapon.RangedWeapon.Ammo ammo)
        {
            Frame = frame;
            WeaponItemId = weaponItemId;
            ControllerTypeName = controllerTypeName;
            FireDelaySeconds = controller._fireDelay;
            IsChargedWeapon = controller._isChargedWeapon;
            MustAimToFire = controller._mustAimToFire;
            RequiresTwoHandsToFire = controller._requiresTwoHandsToFire;
            ReloadsSingleRound = controller._reloadSingleRound;
            CanInterruptReload = controller._canInterruptReload;
            AmmoItemId = ammo._type;
            UsesInternalAmmoStorage = ammo._usesInternalAmmoStorage;
            MagazineCapacity = UsesInternalAmmoStorage ? ammo._maxCount : null;
        }

        public int WeaponItemId { get; }
        /// <summary>Native type of the matched controller; subclasses can override firing.</summary>
        public string ControllerTypeName { get; }
        /// <summary>
        /// Template minimum for the native _nextFireDelay accumulator, checked once per frame. A lower bound,
        /// not the cadence: the attack animation cycle and subclass overrides also gate firing. Seconds is an
        /// inferred unit. For charged weapons the charge time governs and this may mean nothing.
        /// </summary>
        public float FireDelaySeconds { get; }
        public bool IsChargedWeapon { get; }
        public bool MustAimToFire { get; }
        public bool RequiresTwoHandsToFire { get; }
        /// <summary>Raw template ammunition item ID, not checked against the item database.</summary>
        public int AmmoItemId { get; }
        public bool UsesInternalAmmoStorage { get; }
        /// <summary>Raw template _maxCount, or null when the weapon does not use internal ammunition storage.</summary>
        public int? MagazineCapacity { get; }
        /// <summary>Template flag for reloading one round per cycle. Says nothing about how long a reload takes.</summary>
        public bool ReloadsSingleRound { get; }
        public bool CanInterruptReload { get; }
        /// <summary>Unity frame of the read.</summary>
        public int Frame { get; }
    }
}
