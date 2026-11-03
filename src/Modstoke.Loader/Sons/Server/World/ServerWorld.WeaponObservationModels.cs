// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Numerics;

namespace Modstoke.Sons
{
    public enum WeaponObservationStatus
    {
        Unavailable,
        Success,
        WrongThread,
        NotServer,
        WorldUnavailable,
        InvalidRequest,
        BindingChanged,
        EquipmentUnavailable,
        AmbiguousEquipment,
        TransformUnavailable,
        LimitReached,
        Busy,
        ContextChanged,
        InvalidTransform,
        Failed,
        InvalidLaunchOffset
    }

    /// <summary>Selects an existing RangedWeapon field, without invoking native firing or aim methods.</summary>
    public enum WeaponTransformSource
    {
        Projectile,
        ProjectileVisual,
        /// <summary>The ranged weapon's cached _projectilePhysicalSpawnPoint.</summary>
        PhysicalSpawn,
        Aim,
        /// <summary>The inherited physicalSpawnPoint read by native ballistic launch.</summary>
        BallisticSpawn,
        /// <summary>The inherited visualSpawnPoint, used only for projectile presentation.</summary>
        VisualSpawn
    }

    /// <summary>
    /// Copied server observations of one existing equipped pistol transform. The handle retains
    /// its native objects for identity validation; discard it after the captured frame.
    /// Neither successful capture nor validation establishes trusted aim or a firing origin.
    /// </summary>
    public sealed class WeaponTransformSnapshot
    {
        internal WeaponTransformSnapshot(ServerWorld.WeaponTransformObservation observation)
        {
            Observation = observation;
        }

        internal ServerWorld.WeaponTransformObservation Observation { get; }
        public PlayerHandle Binding => Observation.Binding;
        public int Frame => Observation.Frame;
        public int WeaponItemId => 355;
        public WeaponTransformSource Source => Observation.Source;
        public Vector3 Position => Observation.Position;
        public Quaternion Rotation => Observation.Rotation;
        public bool ItemActiveInHierarchy => Observation.ItemActive;
        public bool WeaponActiveInHierarchy => Observation.WeaponActive;
        public bool TransformActiveInHierarchy => Observation.TransformActive;
        public bool RenderableIsLoaded => Observation.RenderableIsLoaded;
        public bool UseVisualTransformForSpawn => Observation.UseVisualTransformForSpawn;
        /// <summary>Signed native offset along the firing direction; copied without clamping.</summary>
        public float TraceForwardOffset => Observation.TraceForwardOffset;
        /// <summary>Both existing physical spawn fields are live and reference the same native transform.</summary>
        public bool PhysicalSpawnMatchesProjectilePoint => Observation.PhysicalSpawnMatchesProjectilePoint;
    }
}
