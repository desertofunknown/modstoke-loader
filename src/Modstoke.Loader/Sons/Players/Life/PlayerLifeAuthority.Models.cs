// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public enum PlayerLifePhase { Alive, Downed, Reviving, Dead }
    public enum PlayerLifeSourceAction { None, StartDeath, ResetFromHeal, Respawn }
    public enum PlayerStatDamageResult { Applied, HitDetectionImmune }
    internal enum PlayerLifeBoundaryResult { Pending, Settled, Faulted }

    /// <summary>A retained commit, not a source presentation or respawn-completion receipt.</summary>
    public sealed class PlayerLifeTransition
    {
        internal PlayerLifeTransition(PlayerLifeAuthority owner,
            ulong sequence, ulong survivalRevision, PlayerLifePhase phase,
            PlayerLifeSourceAction action, float health, float targetHealth,
            PlayerReviveRequest revive, InventoryDeathReceipt? death)
        {
            Owner = owner; Migration = owner.Migration; ActorNetworkId = owner.ActorNetworkId;
            LifeGeneration = owner.LifeGeneration; Sequence = sequence; SurvivalRevision = survivalRevision;
            IntakeTransaction = owner.IntakeTransaction;
            NumericValues = owner.ReadCommittedSourceValues();
            BurnTime = owner.ReadCommittedBurnTime();
            Phase = phase; SourceAction = action; Health = health; TargetHealth = targetHealth;
            ReviveRequest = revive; DeathCustody = death;
        }
        internal PlayerLifeAuthority Owner { get; }
        internal PlayerStatValues NumericValues { get; }
        internal float BurnTime { get; }
        public Guid Migration { get; }
        public ulong ActorNetworkId { get; }
        public ulong LifeGeneration { get; }
        public ulong Sequence { get; }
        public ulong SurvivalRevision { get; }
        public ulong IntakeTransaction { get; }
        public PlayerLifePhase Phase { get; }
        public PlayerLifeSourceAction SourceAction { get; }
        /// <summary>This owner handles the ordinary CheckDeath(Unknown) survival path only.</summary>
        public global::Sons.Player.Stats.DeathType DeathTypeSource => global::Sons.Player.Stats.DeathType.Unknown;
        public float Health { get; }
        public float TargetHealth { get; }
        public PlayerReviveRequest ReviveRequest { get; }
        public InventoryDeathReceipt? DeathCustody { get; }
        // No public completion setter: MPPS numeric equality is not source lifecycle evidence.
        public bool SourcePresentationPending => Sequence > Owner.SourceCompletedSequence;
    }

    public readonly record struct PlayerStatDamageReceipt(ulong LifeGeneration,
        ulong SurvivalRevision, float Damage, float HealthBefore, float HealthAfter,
        PlayerStatDamageResult Result);
}
