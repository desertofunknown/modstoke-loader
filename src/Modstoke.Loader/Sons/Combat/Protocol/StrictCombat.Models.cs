// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatAction : byte
    {
        None, EquipPistol, Unequip, Reload, Fire, ReadState
    }

    public enum StrictCombatResult : byte
    {
        Accepted, NoWeapon, NotEquipped, EmptyMagazine, AlreadyReloading,
        MagazineFull, NoReserve, Cooldown, InvalidIntent, OutOfOrder,
        Conflict, StaleSequence, Busy, ShotPending, EffectFailed, Retired, Incapacitated, StaleLife, NotReady
    }

    public enum StrictCombatLifeStage : byte
    {
        Uninitialized, Alive, Downed, Dead
    }

    public readonly struct StrictCombatIntent
    {
        /// <summary>Creates a request with life generation 0, so only ReadState is valid with this overload.
        /// Mutating requests require the overload with a nonzero life generation.</summary>
        public StrictCombatIntent(Guid epoch, ulong sequence, StrictCombatAction action,
            float directionX = 0, float directionY = 0, float directionZ = 0)
            : this(epoch, sequence, 0, action, directionX, directionY, directionZ)
        {
        }

        public StrictCombatIntent(Guid epoch, ulong sequence, ulong lifeGeneration, StrictCombatAction action,
            float directionX = 0, float directionY = 0, float directionZ = 0)
        {
            Epoch = epoch;
            Sequence = sequence;
            LifeGeneration = lifeGeneration;
            Action = action;
            DirectionX = directionX;
            DirectionY = directionY;
            DirectionZ = directionZ;
        }

        public Guid Epoch { get; }
        public ulong Sequence { get; }
        public ulong LifeGeneration { get; }
        public StrictCombatAction Action { get; }
        public float DirectionX { get; }
        public float DirectionY { get; }
        public float DirectionZ { get; }

        public bool IsValid
        {
            get
            {
                if (Epoch == Guid.Empty || Sequence == 0
                    || Action < StrictCombatAction.EquipPistol || Action > StrictCombatAction.ReadState)
                    return false;
                if (Action == StrictCombatAction.ReadState ? LifeGeneration != 0 : LifeGeneration == 0)
                    return false;
                if (Action != StrictCombatAction.Fire)
                    return BitConverter.SingleToInt32Bits(DirectionX) == 0
                        && BitConverter.SingleToInt32Bits(DirectionY) == 0
                        && BitConverter.SingleToInt32Bits(DirectionZ) == 0;
                double lengthSquared = (double)DirectionX * DirectionX
                    + (double)DirectionY * DirectionY + (double)DirectionZ * DirectionZ;
                return double.IsFinite(lengthSquared) && lengthSquared > 0;
            }
        }

        public bool SameRequest(in StrictCombatIntent other)
        {
            return Epoch == other.Epoch && Sequence == other.Sequence && LifeGeneration == other.LifeGeneration
                && Action == other.Action
                && BitConverter.SingleToInt32Bits(DirectionX) == BitConverter.SingleToInt32Bits(other.DirectionX)
                && BitConverter.SingleToInt32Bits(DirectionY) == BitConverter.SingleToInt32Bits(other.DirectionY)
                && BitConverter.SingleToInt32Bits(DirectionZ) == BitConverter.SingleToInt32Bits(other.DirectionZ);
        }
    }

    public readonly struct StrictCombatSnapshot
    {
        public StrictCombatSnapshot(ulong revision, bool ownsPistol, bool equipped,
            int magazine, int reserve, int reloadRounds, uint reloadRemainingMs, uint fireRemainingMs,
            StrictCombatLifeStage lifeStage, ulong lifeGeneration, float health, float maximumHealth,
            uint downedRemainingMs)
        {
            Revision = revision;
            OwnsPistol = ownsPistol;
            Equipped = equipped;
            Magazine = magazine;
            Reserve = reserve;
            ReloadRounds = reloadRounds;
            ReloadRemainingMs = reloadRemainingMs;
            FireRemainingMs = fireRemainingMs;
            LifeStage = lifeStage;
            LifeGeneration = lifeGeneration;
            Health = health;
            MaximumHealth = maximumHealth;
            DownedRemainingMs = downedRemainingMs;
        }

        public ulong Revision { get; }
        public bool OwnsPistol { get; }
        public bool Equipped { get; }
        public int Magazine { get; }
        public int Reserve { get; }
        public int ReloadRounds { get; }
        public uint ReloadRemainingMs { get; }
        public uint FireRemainingMs { get; }
        public StrictCombatLifeStage LifeStage { get; }
        public ulong LifeGeneration { get; }
        public float Health { get; }
        public float MaximumHealth { get; }
        public uint DownedRemainingMs { get; }
    }

    public readonly struct StrictCombatReceipt
    {
        /// <summary>Infers the request life generation: 0 for ReadState, otherwise the snapshot's
        /// life generation. StaleLife outcomes require the explicit overload.</summary>
        public StrictCombatReceipt(Guid epoch, ulong sequence, StrictCombatAction action,
            StrictCombatResult result, StrictCombatSnapshot state)
            : this(epoch, sequence, action, result,
                action == StrictCombatAction.ReadState ? 0 : state.LifeGeneration, state)
        {
        }

        public StrictCombatReceipt(Guid epoch, ulong sequence, StrictCombatAction action,
            StrictCombatResult result, ulong requestLifeGeneration, StrictCombatSnapshot state)
        {
            Epoch = epoch;
            Sequence = sequence;
            Action = action;
            Result = result;
            RequestLifeGeneration = requestLifeGeneration;
            State = state;
        }

        public Guid Epoch { get; }
        public ulong Sequence { get; }
        public StrictCombatAction Action { get; }
        public StrictCombatResult Result { get; }
        public ulong RequestLifeGeneration { get; }
        public StrictCombatSnapshot State { get; }
    }

    public readonly struct StrictCombatBinding
    {
        public StrictCombatBinding(Guid epoch, ulong generation, ulong actorNetworkId, uint reloadDurationMs)
        {
            Epoch = epoch;
            Generation = generation;
            ActorNetworkId = actorNetworkId;
            ReloadDurationMs = reloadDurationMs;
        }

        public Guid Epoch { get; }
        public ulong Generation { get; }
        public ulong ActorNetworkId { get; }
        public uint ReloadDurationMs { get; }

        public bool SameBinding(in StrictCombatBinding other)
            => Epoch == other.Epoch && Generation == other.Generation
                && ActorNetworkId == other.ActorNetworkId && ReloadDurationMs == other.ReloadDurationMs;
    }
}
