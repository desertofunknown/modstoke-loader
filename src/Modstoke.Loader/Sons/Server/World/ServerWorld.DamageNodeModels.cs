// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    /// <summary>Copied damage-node multipliers captured for a melee impact request.</summary>
    public sealed class MeleeNodeSettings
    {
        internal MeleeNodeSettings(float bluntMultiplier, float sharpMultiplier, float blockingMultiplier)
        {
            BluntMultiplier = bluntMultiplier;
            SharpMultiplier = sharpMultiplier;
            BlockingMultiplier = blockingMultiplier;
        }

        public float BluntMultiplier { get; }
        public float SharpMultiplier { get; }
        public float BlockingMultiplier { get; }
    }

    /// <summary>
    /// Copied damage-node observations only. These values do not establish native impact admission,
    /// armour resolution, final health damage or trusted resource ownership.
    /// </summary>
    public sealed class PlayerDamageNodeSnapshot
    {
        internal PlayerDamageNodeSnapshot(ServerWorld.DamageNodeObservation observation,
            ServerWorld.PlayerImpactRoute route)
        {
            Observation = observation;
            Route = route;
        }

        internal ServerWorld.DamageNodeObservation Observation { get; }
        internal ServerWorld.PlayerImpactRoute Route { get; }
        public PlayerHandle Binding => Observation.Binding;
        public int Frame => Observation.Frame;
        /// <summary>An ephemeral Unity identifier; never a durable ownership identifier.</summary>
        public int NodeInstanceId => Observation.NodeInstanceId;
        public float ProjectileMultiplier => Observation.ProjectileMultiplier;
        /// <summary>Melee multipliers when captured for a melee impact request; otherwise null.</summary>
        public MeleeNodeSettings MeleeSettings => Observation.MeleeSettings;
        public float AbsorptionRemaining => Observation.AbsorptionRemaining;
        public bool UseBreak => Observation.UseBreak;
        public float BreakAmount => Observation.BreakAmount;
        public float AppliedNodeDamage => Observation.AppliedNodeDamage;
        public bool TakeDamageAfterBreak => Observation.TakeDamageAfterBreak;
        public bool UpdateRemainingDamage => Observation.UpdateRemainingDamage;
        public bool IsBroken => Observation.IsBroken;
        public bool IsEnabled => Observation.IsEnabled;
        public bool ActiveInHierarchy => Observation.ActiveInHierarchy;
        public bool IsDodging => Observation.IsDodging;
        public bool IsParrying => Observation.IsParrying;
        public bool IsArmour => Observation.IsArmour;
        public bool CanBlock => Observation.CanBlock;
        public bool IsBlocking => Observation.IsBlocking;
        public bool VerifyBlockDirection => Observation.VerifyBlockDirection;
        public bool UsesTeamDamage => Observation.UsesTeamDamage;
        public float TeamDamageMultiplier => Observation.TeamDamageMultiplier;
        public bool HasParent => Observation.HasParent;
        public bool HasChild => Observation.HasChild;
        public bool HasValidationOverride => Observation.HasValidationOverride;
        public bool HasBreakCallback => Observation.HasBreakCallback;
        public bool HasBlockCallback => Observation.HasBlockCallback;
        public bool HasDismemberCallback => Observation.HasDismemberCallback;
        public bool HasTeamStimuliType => Observation.HasTeamStimuliType;
        public int LastImpactId => Observation.LastImpactId;
        public float LastImpactTime => Observation.LastImpactTime;
        public bool IsBaseDamageNode => Observation.IsBaseDamageNode;
    }
}

