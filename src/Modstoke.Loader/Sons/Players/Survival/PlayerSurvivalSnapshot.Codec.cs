// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalSnapshot
    {
        private const uint Magic = 0x34565350; // PSV4
        public const uint SchemaVersion = 4;
        private const int HeaderLength = 32;
        private const int RuntimeLength = 9 * 28 + 16 * 4 + 8;
        public const int EncodedLength = HeaderLength + RuntimeLength
            + (int)PlayerSurvivalScalar.Count * 4 + 40
            + PlayerWaterConfiguration.EncodedLength
            + PlayerSurvivalSettings.EncodedLength;
        public const int MaximumBytes = EncodedLength;

        public byte[] Export()
        {
            var bytes = new byte[EncodedLength];
            if (!TryEncode(bytes, out _)) throw new InvalidOperationException("Invalid survival snapshot.");
            return bytes;
        }

        public bool TryEncode(Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < EncodedLength || !Valid()) return false;
            var w = new Writer(destination.Slice(0, EncodedLength));
            w.U(Magic); w.U(SchemaVersion); w.UL(SourceNetworkId); w.I(PrefabId); w.I(StateTypeId);
            w.I((int)PlayerSurvivalScalar.Count); w.U(0);
            WriteRuntime(ref w, Runtime);
            foreach (float value in _scalars) w.F(value);
            w.U(_flags); w.U(_actionFlags);
            w.I(StrengthLevel); w.I(MaximumStrengthLevel); w.I(LowHealthThreshold);
            w.I(DeathCount); w.I(MaximumDeathCount); w.I(CookingRecipeId);
            w.UL(CookingRecipeFingerprint);
            Water.Encode(destination.Slice(w.Offset, PlayerWaterConfiguration.EncodedLength));
            w.Offset += PlayerWaterConfiguration.EncodedLength;
            Settings.Encode(destination.Slice(w.Offset, PlayerSurvivalSettings.EncodedLength));
            w.Offset += PlayerSurvivalSettings.EncodedLength;
            written = w.Offset;
            return written == EncodedLength;
        }

        public static bool TryDecode(ReadOnlySpan<byte> bytes,
            out PlayerSurvivalSnapshot snapshot, out string detail)
        {
            snapshot = null;
            detail = "The survival payload must use the complete canonical V4 encoding, including native water and awake/asleep settings.";
            if (bytes.Length != EncodedLength) return false;
            var r = new Reader(bytes);
            if (r.U() != Magic || r.U() != SchemaVersion) return false;
            ulong source = r.UL();
            int prefab = r.I(), state = r.I();
            if (r.I() != (int)PlayerSurvivalScalar.Count || r.U() != 0) return false;
            var runtime = ReadRuntime(ref r, out bool valid);
            if (!valid) return false;
            var values = new float[(int)PlayerSurvivalScalar.Count];
            for (int i = 0; i < values.Length; i++) values[i] = r.F();
            uint flags = r.U(), actions = r.U();
            int strength = r.I(), maximumStrength = r.I(), lowHealth = r.I();
            int deaths = r.I(), maximumDeaths = r.I(), recipe = r.I();
            ulong recipeFingerprint = r.UL();
            if (!PlayerWaterConfiguration.TryDecode(
                bytes.Slice(r.Offset, PlayerWaterConfiguration.EncodedLength), out var water)) return false;
            r.Offset += PlayerWaterConfiguration.EncodedLength;
            if (!PlayerSurvivalSettings.TryDecode(bytes.Slice(r.Offset), out var settings)) return false;
            r.Offset += PlayerSurvivalSettings.EncodedLength;
            var candidate = new PlayerSurvivalSnapshot(source, prefab, state, runtime, values,
                flags, actions, strength, maximumStrength, lowHealth, deaths, maximumDeaths, recipe, recipeFingerprint, water, settings);
            if (r.Offset != EncodedLength || !candidate.Valid()) return false;
            snapshot = candidate;
            detail = "Canonical survival observations decoded; no authority or policy approval granted.";
            return true;
        }

        private static void WriteRuntime(ref Writer w, PlayerAuthorityRuntimeState s)
        {
            WriteStat(ref w, s.Health);
            WriteStat(ref w, s.Sickness);
            WriteStat(ref w, s.Stamina);
            WriteStat(ref w, s.Vitality);
            WriteStat(ref w, s.Hydration);
            WriteStat(ref w, s.Fullness);
            WriteStat(ref w, s.Rested);
            WriteStat(ref w, s.Strength);
            WriteStat(ref w, s.Temperature);
            w.F(s.JumpStaminaCost);
            w.F(s.StaminaRecoveryRate);
            w.F(s.StaminaActionAge);
            w.F(s.ColdRecoveryNormal);
            w.F(s.ColdRecoveryHard);
            w.F(s.StatRecoveryNormal);
            w.F(s.StatRecoveryHard);
            w.F(s.TargetHealth);
            w.F(s.MaximumVitalityLostWhenFreezing);
            w.F(s.ColdFactor);
            w.U((s.Cold ? 1u : 0) | (s.Freezing ? 2u : 0) | (s.HasDied ? 4u : 0)
                | (s.Downed ? 8u : 0) | (s.Respawning ? 16u : 0) | (s.FinalDeath ? 32u : 0));
            w.I(s.DyingState);
            w.F(s.HydrationBuff);
            w.F(s.FullnessBuff);
            w.F(s.RestedBuff);
            w.F(s.LastHydrationFactor);
            w.F(s.LastFullnessFactor);
            w.F(s.LastRestedFactor);
        }

        private static PlayerAuthorityRuntimeState ReadRuntime(ref Reader r, out bool valid)
        {
            var health = ReadStat(ref r);
            var sickness = ReadStat(ref r);
            var stamina = ReadStat(ref r);
            var vitality = ReadStat(ref r);
            var hydration = ReadStat(ref r);
            var fullness = ReadStat(ref r);
            var rested = ReadStat(ref r);
            var strength = ReadStat(ref r);
            var temperature = ReadStat(ref r);
            float jumpStaminaCost = r.F();
            float staminaRecoveryRate = r.F();
            float staminaActionAge = r.F();
            float coldRecoveryNormal = r.F();
            float coldRecoveryHard = r.F();
            float statRecoveryNormal = r.F();
            float statRecoveryHard = r.F();
            float targetHealth = r.F();
            float maximumVitalityLostWhenFreezing = r.F();
            float coldFactor = r.F();
            uint flags = r.U();
            int dying = r.I();
            valid = (flags & ~63u) == 0;
            return new(health, sickness, stamina, vitality, hydration, fullness, rested, strength, temperature,
                jumpStaminaCost, staminaRecoveryRate, staminaActionAge, coldRecoveryNormal, coldRecoveryHard, statRecoveryNormal, statRecoveryHard, targetHealth, maximumVitalityLostWhenFreezing, coldFactor,
                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0,
                dying, (flags & 16) != 0, (flags & 32) != 0,
                r.F(), r.F(), r.F(), r.F(), r.F(), r.F());
        }

        private static void WriteStat(ref Writer w, PlayerAuthorityStatState s)
        {
            w.F(s.Current); w.F(s.Base); w.F(s.Minimum); w.F(s.Maximum);
            w.F(s.Default); w.F(s.FadeRate); w.F(s.RegenRate);
        }

        private static PlayerAuthorityStatState ReadStat(ref Reader r)
            => new(r.F(), r.F(), r.F(), r.F(), r.F(), r.F(), r.F());

        private ref struct Writer
        {
            private readonly Span<byte> _bytes;
            internal int Offset;
            internal Writer(Span<byte> bytes) { _bytes = bytes; Offset = 0; }
            internal void U(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(_bytes.Slice(Offset), value); Offset += 4; }
            internal void I(int value) { BinaryPrimitives.WriteInt32LittleEndian(_bytes.Slice(Offset), value); Offset += 4; }
            internal void F(float value) { BinaryPrimitives.WriteSingleLittleEndian(_bytes.Slice(Offset), value); Offset += 4; }
            internal void UL(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(_bytes.Slice(Offset), value); Offset += 8; }
        }

        private ref struct Reader
        {
            private readonly ReadOnlySpan<byte> _bytes;
            internal int Offset;
            internal Reader(ReadOnlySpan<byte> bytes) { _bytes = bytes; Offset = 0; }
            internal uint U() { uint value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.Slice(Offset)); Offset += 4; return value; }
            internal int I() { int value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.Slice(Offset)); Offset += 4; return value; }
            internal float F() { float value = BinaryPrimitives.ReadSingleLittleEndian(_bytes.Slice(Offset)); Offset += 4; return value; }
            internal ulong UL() { ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.Slice(Offset)); Offset += 8; return value; }
        }
    }
}

