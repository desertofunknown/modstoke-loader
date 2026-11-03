// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons
{
    internal readonly record struct PlayerWaterTransitionFrame(PlayerWaterTransitionMessage Kind,
        PlayerWaterTransitionKey Key)
    {
        internal const int Length = 64;
        private const uint Magic = 0x4557504D; // MPWE v1; not an absolute sensor sample.

        internal static bool Matches(ReadOnlySpan<byte> bytes) => bytes.Length == Length
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

        internal void Write(Span<byte> bytes)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 1; bytes[5] = (byte)Kind;
            Key.Migration.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], Key.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], Key.Actor);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], Key.LifeGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], Key.Ordinal);
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerWaterTransitionFrame frame)
        {
            frame = default;
            if (!Matches(bytes) || bytes[4] != 1 || bytes[6] != 0 || bytes[7] != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]) != 0
                || bytes[5] is not (1 or 2)) return false;
            frame = new((PlayerWaterTransitionMessage)bytes[5], new(new Guid(bytes[8..24]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..])));
            return frame.Key.IsValid;
        }
    }
}
