// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using global::Sons.StatSystem;

namespace Modstoke.Sons
{
    internal readonly record struct PlayerStatBounds(float Minimum, float Maximum)
    {
        internal bool IsValid => float.IsFinite(Minimum) && float.IsFinite(Maximum) && Maximum > Minimum;
        internal bool Contains(float value) => float.IsFinite(value) && value >= Minimum && value <= Maximum;
        internal static PlayerStatBounds Read(Stat stat) => new(stat._min, stat._max);
        internal void Apply(Stat stat) { stat._min = Minimum; stat._max = Maximum; }
    }

    // The stream preserves the original Stat objects. Bounds travel with values because
    // authoritative progression can change them; source-side clamps must not use old ranges.
    internal readonly record struct PlayerStatValues(
        ConsumptionNumbers Intake,
        float Health, float Vitality, float Strength, float Temperature,
        PlayerStatBounds HealthBounds, PlayerStatBounds SicknessBounds, PlayerStatBounds StaminaBounds,
        PlayerStatBounds VitalityBounds, PlayerStatBounds HydrationBounds, PlayerStatBounds FullnessBounds,
        PlayerStatBounds RestBounds, PlayerStatBounds StrengthBounds, PlayerStatBounds TemperatureBounds,
        float VitalityBase, float TemperatureBase, float BodyTemperature, float Wetness, float ColdFactor,
        float ShiverBlend, uint Flags)
    {
        internal const int WireLength = 200;
        internal const uint Cold = 1, Freezing = 2, Burning = 4, InteriorWarmth = 8,
            HasDied = 16, Downed = 32, Respawning = 64, FinalDeath = 128;
        internal const uint LifeMask = HasDied | Downed | Respawning | FinalDeath | 0x00FF0000;
        private const uint AllowedFlags = 0x00FF00FF;
        internal uint Life => Flags & LifeMask;

        internal bool IsValid => Intake.IsFinite && HealthBounds.IsValid && SicknessBounds.IsValid
            && StaminaBounds.IsValid && VitalityBounds.IsValid && HydrationBounds.IsValid && FullnessBounds.IsValid
            && RestBounds.IsValid && StrengthBounds.IsValid && TemperatureBounds.IsValid
            && HealthBounds.Contains(Health) && SicknessBounds.Contains(Intake.Sickness)
            && StaminaBounds.Contains(Intake.Stamina) && VitalityBounds.Contains(Vitality)
            && HydrationBounds.Contains(Intake.Hydration) && FullnessBounds.Contains(Intake.Fullness)
            && RestBounds.Contains(Intake.Rest) && StrengthBounds.Contains(Strength) && TemperatureBounds.Contains(Temperature)
            && float.IsFinite(VitalityBase) && float.IsFinite(TemperatureBase) && float.IsFinite(BodyTemperature)
            && float.IsFinite(Wetness) && Wetness is >= 0 and <= 1
            && float.IsFinite(ColdFactor) && ColdFactor is >= 0 and <= 1
            && float.IsFinite(ShiverBlend) && ShiverBlend is >= 0 and <= 1
            && Intake.TargetHealth >= 0 && Intake.TargetHealth <= HealthBounds.Maximum
            && (Flags & ~AllowedFlags) == 0 && Enum.IsDefined((PlayerStats.DyingState)((Flags >> 16) & 255));

        internal static PlayerStatValues Capture(PlayerStats stats)
        {
            var v = stats._vitals;
            uint flags = (v._isCold ? Cold : 0) | (stats._IsFreezing_k__BackingField ? Freezing : 0)
                | (stats._isOnFire ? Burning : 0) | (v._temperature._interiorSpaceWarmth ? InteriorWarmth : 0)
                | (v._hasDied ? HasDied : 0) | (stats._playerIsDowned ? Downed : 0)
                | (stats._respawning ? Respawning : 0) | (stats._doFinalDeath ? FinalDeath : 0)
                | ((uint)stats._dyingState << 16);
            return new(InventoryConsumption.ReadProjectionNumbers(stats),
                v._health._currentValue, v._vitality._currentValue, v._strength._currentValue, v._temperature._currentValue,
                PlayerStatBounds.Read(v._health), PlayerStatBounds.Read(v._sickness), PlayerStatBounds.Read(v._stamina),
                PlayerStatBounds.Read(v._vitality), PlayerStatBounds.Read(v._hydration), PlayerStatBounds.Read(v._fullness),
                PlayerStatBounds.Read(v._rested), PlayerStatBounds.Read(v._strength), PlayerStatBounds.Read(v._temperature),
                v._vitality._baseValue, v._temperature._baseValue, stats._bodyTemperature, stats._currentWetness,
                v._coldFactor, stats._shiverAnimBlendDelta, flags);
        }

        internal bool LifeMatches(PlayerStats stats)
        {
            var current = Capture(stats);
            // A numeric packet is never permission to perform a down/death/respawn callback.
            return Life == current.Life && (Health > 0 || (Flags & (HasDied | Downed | FinalDeath)) != 0);
        }

        internal bool TryApply(PlayerStats stats)
        {
            return LifeMatches(stats) && TryApplyNumbers(stats, false);
        }

        internal bool TryApplyLifeNumbers(PlayerStats stats) => TryApplyNumbers(stats, true);

        private bool TryApplyNumbers(PlayerStats stats, bool retainLife)
        {
            if (!IsValid
                || !InventoryConsumption.CanResolveProjectionRecipe(stats, Intake.CookingRecipeId)) return false;
            uint previousLife = Capture(stats).Life;
            var v = stats._vitals;
            HealthBounds.Apply(v._health); SicknessBounds.Apply(v._sickness); StaminaBounds.Apply(v._stamina);
            VitalityBounds.Apply(v._vitality); HydrationBounds.Apply(v._hydration); FullnessBounds.Apply(v._fullness);
            RestBounds.Apply(v._rested); StrengthBounds.Apply(v._strength); TemperatureBounds.Apply(v._temperature);
            if (!InventoryConsumption.TryWriteProjectionNumbers(stats, Intake)) return false;
            v._health._currentValue = Health; v._vitality._currentValue = Vitality;
            v._strength._currentValue = Strength; v._temperature._currentValue = Temperature;
            v._vitality._baseValue = VitalityBase; v._temperature._baseValue = TemperatureBase;
            stats._bodyTemperature = BodyTemperature; stats._currentWetness = Wetness;
            v._coldFactor = ColdFactor; stats._shiverAnimBlendDelta = ShiverBlend;
            v._isCold = (Flags & Cold) != 0; stats._IsFreezing_k__BackingField = (Flags & Freezing) != 0;
            stats._isOnFire = (Flags & Burning) != 0;
            v._temperature._interiorSpaceWarmth = (Flags & InteriorWarmth) != 0;
            var current = Capture(stats);
            return (retainLife ? current with { Flags = (current.Flags & ~LifeMask) | Life } : current) == this
                && (!retainLife || current.Life == previousLife);
        }

        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != WireLength) throw new ArgumentException("Exact stat payload required.", nameof(bytes));
            Intake.Write(bytes[..84]);
            ReadOnlySpan<float> values = stackalloc float[] {
                Health, Vitality, Strength, Temperature,
                HealthBounds.Minimum, HealthBounds.Maximum, SicknessBounds.Minimum, SicknessBounds.Maximum,
                StaminaBounds.Minimum, StaminaBounds.Maximum, VitalityBounds.Minimum, VitalityBounds.Maximum,
                HydrationBounds.Minimum, HydrationBounds.Maximum, FullnessBounds.Minimum, FullnessBounds.Maximum,
                RestBounds.Minimum, RestBounds.Maximum, StrengthBounds.Minimum, StrengthBounds.Maximum,
                TemperatureBounds.Minimum, TemperatureBounds.Maximum,
                VitalityBase, TemperatureBase, BodyTemperature, Wetness, ColdFactor, ShiverBlend };
            for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes[(84 + i * 4)..], values[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[196..], Flags);
        }

        internal static PlayerStatValues Read(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != WireLength) throw new ArgumentException("Exact stat payload required.", nameof(bytes));
            Span<float> v = stackalloc float[28];
            for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(84 + i * 4)..]);
            return new(ConsumptionNumbers.Read(bytes[..84]), v[0], v[1], v[2], v[3],
                new(v[4], v[5]), new(v[6], v[7]), new(v[8], v[9]), new(v[10], v[11]), new(v[12], v[13]),
                new(v[14], v[15]), new(v[16], v[17]), new(v[18], v[19]), new(v[20], v[21]),
                v[22], v[23], v[24], v[25], v[26], v[27], BinaryPrimitives.ReadUInt32LittleEndian(bytes[196..]));
        }
    }

    internal readonly record struct PlayerStatFrame(Guid MigrationId, ulong Generation, ulong NetworkId,
        uint Sequence, uint LifeSequence, ulong IntakeTransaction, PlayerStatValues Values)
    {
        internal const int Length = 256;
        private const uint Magic = 0x5350504D; // MPPS
        internal static bool Matches(ReadOnlySpan<byte> bytes)
            => bytes.Length == Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != Length) throw new ArgumentException("Exact stat frame required.", nameof(bytes));
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 2;
            MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], NetworkId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], LifeSequence);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], IntakeTransaction);
            Values.Write(bytes[56..]);
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerStatFrame frame)
        {
            frame = default;
            if (!Matches(bytes) || bytes[4] != 2 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0) return false;
            frame = new(new Guid(bytes[8..24]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]), PlayerStatValues.Read(bytes[56..]));
            return frame.MigrationId != Guid.Empty && frame.Generation != 0 && frame.NetworkId != 0
                && frame.Sequence != 0 && frame.Values.IsValid;
        }
    }
}
