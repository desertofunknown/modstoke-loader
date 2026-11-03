// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Modstoke.Sons.Combat
{
    /// <summary>An impact sound observation with its authoritative surface normal.</summary>
    public readonly struct StrictCombatImpactPresentation
    {
        public StrictCombatImpactPresentation(StrictCombatImpactAudio audio, Vector3 normal)
        {
            Audio = audio;
            Normal = normal;
        }

        public StrictCombatImpactAudio Audio { get; }
        public Vector3 Normal { get; }

        public bool IsValid
        {
            get
            {
                if (!Audio.IsValid || !float.IsFinite(Normal.X) || !float.IsFinite(Normal.Y)
                    || !float.IsFinite(Normal.Z)) return false;
                double lengthSquared = (double)Normal.X * Normal.X + (double)Normal.Y * Normal.Y
                    + (double)Normal.Z * Normal.Z;
                return lengthSquared >= 0.99 && lengthSquared <= 1.01;
            }
        }
    }

    /// <summary>Cosmetic impact observations, separate from damage authority.</summary>
    public static class StrictCombatImpactPresentationProtocol
    {
        public const ulong Capability = 1UL << 7;
        public const byte Version = 1;
        public const int Length = 124;
        public const uint Magic = 0x5049534D;

        public static bool TryWrite(in StrictCombatImpactPresentation value, Span<byte> destination, out int written)
        {
            written = 0;
            if (!value.IsValid || destination.Length < Length) return false;
            var record = destination.Slice(0, Length);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            var audio = value.Audio;
            if (!StrictCombatImpactAudioProtocol.TryWrite(in audio,
                record.Slice(8, StrictCombatImpactAudioProtocol.Length), out _)) return false;
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(112, 4), value.Normal.X);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(116, 4), value.Normal.Y);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(120, 4), value.Normal.Z);
            written = Length;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> source, out StrictCombatImpactPresentation value)
        {
            value = default;
            if (source.Length != Length || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != Version || source[5] != 0 || source[6] != 0 || source[7] != 0
                || !StrictCombatImpactAudioProtocol.TryRead(source.Slice(8, StrictCombatImpactAudioProtocol.Length),
                    out var audio)) return false;
            var candidate = new StrictCombatImpactPresentation(audio,
                new Vector3(BinaryPrimitives.ReadSingleLittleEndian(source.Slice(112, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(source.Slice(116, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(source.Slice(120, 4))));
            if (!candidate.IsValid) return false;
            value = candidate;
            return true;
        }
    }
}
