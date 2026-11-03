// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Modstoke.Sons.Combat
{
    /// <summary>A best-effort sound observation for one recipient's exact combat binding and life.</summary>
    public readonly struct StrictCombatImpactAudio
    {
        public StrictCombatImpactAudio(StrictCombatBinding receiver, ulong receiverLifeGeneration,
            ulong sequence, ulong shooterActorNetworkId, ulong shooterLifeGeneration,
            ulong shotSequence, ulong impactSequence, Vector3 position, BallisticImpactSound sound)
        {
            ReceiverEpoch = receiver.Epoch;
            ReceiverGeneration = receiver.Generation;
            ReceiverActorNetworkId = receiver.ActorNetworkId;
            ReceiverLifeGeneration = receiverLifeGeneration;
            Sequence = sequence;
            ShooterActorNetworkId = shooterActorNetworkId;
            ShooterLifeGeneration = shooterLifeGeneration;
            ShotSequence = shotSequence;
            ImpactSequence = impactSequence;
            Position = position;
            Sound = sound;
        }

        public Guid ReceiverEpoch { get; }
        public ulong ReceiverGeneration { get; }
        public ulong ReceiverActorNetworkId { get; }
        public ulong ReceiverLifeGeneration { get; }
        public ulong Sequence { get; }
        public ulong ShooterActorNetworkId { get; }
        public ulong ShooterLifeGeneration { get; }
        public ulong ShotSequence { get; }
        public ulong ImpactSequence { get; }
        public Vector3 Position { get; }
        public BallisticImpactSound Sound { get; }

        public bool IsValid => ReceiverEpoch != Guid.Empty && ReceiverGeneration != 0
            && ReceiverActorNetworkId != 0 && ReceiverLifeGeneration != 0 && Sequence != 0
            && ShooterActorNetworkId != 0 && ShooterLifeGeneration != 0 && ShotSequence != 0 && ImpactSequence != 0
            && float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z)
            && Sound >= BallisticImpactSound.HardSurface && Sound <= BallisticImpactSound.Wood;
    }

    /// <summary>Cosmetic observations, separate from combat intents, receipts and damage authority.</summary>
    public static class StrictCombatImpactAudioProtocol
    {
        public const ulong Capability = 1UL << 6;
        public const byte Version = 1;
        public const int Length = 104;
        private const uint Magic = 0x4941534D;

        public static bool TryWrite(in StrictCombatImpactAudio value, Span<byte> destination, out int written)
        {
            written = 0;
            if (!value.IsValid || destination.Length < Length) return false;
            var record = destination.Slice(0, Length);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            record[5] = 1;
            record[6] = (byte)value.Sound;
            value.ReceiverEpoch.TryWriteBytes(record.Slice(8, 16));
            Write(record, 24, value.ReceiverGeneration);
            Write(record, 32, value.ReceiverActorNetworkId);
            Write(record, 40, value.ReceiverLifeGeneration);
            Write(record, 48, value.Sequence);
            Write(record, 56, value.ShooterActorNetworkId);
            Write(record, 64, value.ShooterLifeGeneration);
            Write(record, 72, value.ShotSequence);
            Write(record, 80, value.ImpactSequence);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(88, 4), value.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(92, 4), value.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(96, 4), value.Position.Z);
            written = Length;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> source, out StrictCombatImpactAudio value)
        {
            value = default;
            if (source.Length != Length || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != Version || source[5] != 1 || source[7] != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(100, 4)) != 0) return false;
            var receiver = new StrictCombatBinding(new Guid(source.Slice(8, 16)), Read(source, 24), Read(source, 32), 0);
            var candidate = new StrictCombatImpactAudio(receiver, Read(source, 40), Read(source, 48), Read(source, 56),
                Read(source, 64), Read(source, 72), Read(source, 80),
                new Vector3(BinaryPrimitives.ReadSingleLittleEndian(source.Slice(88, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(source.Slice(92, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(source.Slice(96, 4))),
                (BallisticImpactSound)source[6]);
            if (!candidate.IsValid) return false;
            value = candidate;
            return true;
        }

        private static void Write(Span<byte> record, int offset, ulong value)
            => BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(offset, 8), value);
        private static ulong Read(ReadOnlySpan<byte> record, int offset)
            => BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(offset, 8));
    }
}
