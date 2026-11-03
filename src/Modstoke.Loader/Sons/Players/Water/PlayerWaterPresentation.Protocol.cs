// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using V3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    internal readonly record struct PlayerWaterPresentationFrame(Guid MigrationId, ulong Generation,
        ulong NetworkId, ulong Sequence, ulong ContactGeneration, PlayerWaterContactSample Sample)
    {
        internal const int Length = 256, ContactOffset = 96, ContactLength = Length - ContactOffset;
        private const uint Magic = 0x5057504D; // MPWP v1, absolute sensor presentation, not commands.

        internal static bool Matches(ReadOnlySpan<byte> bytes)
            => bytes.Length == Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

        internal void Write(Span<byte> bytes, ReadOnlySpan<byte> contact)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 1;
            bytes[5] = (byte)((Sample.InWater ? 1 : 0) | (Sample.HasWaterSurface ? 2 : 0)
                | (Sample.InOcean ? 4 : 0) | (Sample.InCave ? 8 : 0));
            MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], NetworkId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], ContactGeneration);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], Sample.Frame);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[60..], Sample.Height);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[64..], Sample.Depth);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[68..], Sample.VolumeLevel);
            WriteVector(bytes[72..], Sample.Normal);
            WriteVector(bytes[84..], Sample.SurfaceVelocity);
            contact.CopyTo(bytes[ContactOffset..]);
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerWaterPresentationFrame frame)
        {
            frame = default;
            if (!Matches(bytes) || bytes[4] != 1 || (bytes[5] & ~15) != 0 || bytes[6] != 0 || bytes[7] != 0)
                return false;
            ulong actor = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
            var sample = new PlayerWaterContactSample(BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]),
                actor, (bytes[5] & 1) != 0, (bytes[5] & 2) != 0, (bytes[5] & 4) != 0, (bytes[5] & 8) != 0,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[60..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[64..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[68..]), ReadVector(bytes[72..]), ReadVector(bytes[84..]));
            frame = new(new Guid(bytes[8..24]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), actor,
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]), sample);
            return frame.MigrationId != Guid.Empty && frame.Generation != 0 && actor != 0 && frame.Sequence != 0
                && frame.ContactGeneration != 0 && sample.Frame >= 0 && float.IsFinite(sample.Height)
                && float.IsFinite(sample.Depth) && float.IsFinite(sample.VolumeLevel)
                && Finite(sample.Normal) && Finite(sample.SurfaceVelocity)
                && sample.InWater == (bytes[ContactOffset] != 0)
                && PlayerWaterContactAuthority.ValidPresentationContact(bytes[ContactOffset..]);
        }

        private static bool Finite(V3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        private static void WriteVector(Span<byte> bytes, V3 value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.Z);
        }
        private static V3 ReadVector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
    }
}
